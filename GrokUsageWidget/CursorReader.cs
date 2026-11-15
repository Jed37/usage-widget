using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GrokUsageWidget
{
    internal sealed class CursorUsage
    {
        public double Percent;
        public DateTimeOffset? PeriodStart;
        public DateTimeOffset? PeriodEnd;
        public string Plan;
        public bool HasIncludedLimit;
        public string Error;
    }

    internal sealed class CursorReader : IDisposable
    {
        private static readonly Uri UsageUri =
            new Uri("https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus");

        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly HttpClient http;

        public CursorReader()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GrokUsageWidget/0.1");
        }

        public async Task<CursorUsage> Fetch()
        {
            CursorUsage usage = new CursorUsage();
            try
            {
                string token = ReadToken();
                if (string.IsNullOrEmpty(token))
                {
                    throw new InvalidOperationException("Grok Bot is not signed in on this PC.");
                }

                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, UsageUri);
                request.Content = new ByteArrayContent(new byte[0]);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/proto");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                request.Headers.TryAddWithoutValidation("x-cursor-client-type", "grok-bot");
                request.Headers.TryAddWithoutValidation("x-cursor-client-version", "0.0.0");

                using (HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false))
                {
                    byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                        {
                            throw new InvalidOperationException("Grok Bot sign-in expired. Open Grok Bot to refresh.");
                        }
                        throw new InvalidOperationException("Grok Bot usage returned HTTP " + (int)response.StatusCode + ".");
                    }
                    Parse(body, usage);
                }
            }
            catch (Exception ex)
            {
                usage.Error = ex.Message;
            }
            return usage;
        }

        private string ReadToken()
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Grok Bot");
            string localStatePath = Path.Combine(appData, "Local State");
            string secretsPath = Path.Combine(appData, "sand-secrets.json");
            if (!File.Exists(localStatePath) || !File.Exists(secretsPath))
            {
                return null;
            }

            string localStateText = ReadShared(localStatePath);
            Dictionary<string, object> localState = json.Deserialize<Dictionary<string, object>>(localStateText);
            Dictionary<string, object> osCrypt = GetObject(localState, "os_crypt");
            string encryptedKey = GetString(osCrypt, "encrypted_key");
            if (string.IsNullOrEmpty(encryptedKey))
            {
                return null;
            }

            byte[] keyBlob = Convert.FromBase64String(encryptedKey);
            if (keyBlob.Length <= 5)
            {
                return null;
            }
            byte[] keyPayload = new byte[keyBlob.Length - 5];
            Buffer.BlockCopy(keyBlob, 5, keyPayload, 0, keyPayload.Length);
            byte[] key = ProtectedData.Unprotect(
                keyPayload,
                null,
                DataProtectionScope.CurrentUser);

            string secretsText = ReadShared(secretsPath);
            Dictionary<string, object> secrets = json.Deserialize<Dictionary<string, object>>(secretsText);
            string accountsText = GetString(secrets, "cursor-accounts");
            if (string.IsNullOrEmpty(accountsText))
            {
                return null;
            }

            Dictionary<string, object> accountsRoot = json.Deserialize<Dictionary<string, object>>(accountsText);
            string active = GetString(accountsRoot, "active");
            Dictionary<string, object> accounts = GetObject(accountsRoot, "accounts");
            Dictionary<string, object> account = GetObject(accounts, active);
            string tokenValue = GetString(account, "cursor-access-token");
            if (string.IsNullOrEmpty(tokenValue))
            {
                return null;
            }

            byte[] tokenBlob = Convert.FromBase64String(tokenValue);
            if (tokenBlob.Length < 3 || tokenBlob[0] != (byte)'v' || tokenBlob[1] != (byte)'1' || tokenBlob[2] != (byte)'0')
            {
                return Encoding.UTF8.GetString(tokenBlob);
            }
            return DecryptGcm(key, tokenBlob);
        }

        private static string ReadShared(string path)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static string DecryptGcm(byte[] key, byte[] blob)
        {
            int offset = 3;
            int nonceLength = 12;
            int tagLength = 16;
            int cipherLength = blob.Length - offset - nonceLength - tagLength;
            if (cipherLength < 0)
            {
                throw new InvalidOperationException("Grok Bot token is malformed.");
            }

            byte[] nonce = new byte[nonceLength];
            byte[] cipher = new byte[cipherLength];
            byte[] tag = new byte[tagLength];
            byte[] plain = new byte[cipherLength];
            Buffer.BlockCopy(blob, offset, nonce, 0, nonceLength);
            Buffer.BlockCopy(blob, offset + nonceLength, cipher, 0, cipherLength);
            Buffer.BlockCopy(blob, blob.Length - tagLength, tag, 0, tagLength);

            IntPtr algorithm = IntPtr.Zero;
            IntPtr keyHandle = IntPtr.Zero;
            IntPtr noncePointer = IntPtr.Zero;
            IntPtr tagPointer = IntPtr.Zero;
            IntPtr cipherPointer = IntPtr.Zero;
            IntPtr plainPointer = IntPtr.Zero;
            try
            {
                int status = BCryptOpenAlgorithmProvider(out algorithm, "AES", null, 0);
                if (status != 0)
                {
                    throw CryptError(status);
                }

                byte[] chainingMode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                status = BCryptSetProperty(algorithm, "ChainingMode", chainingMode, chainingMode.Length, 0);
                if (status != 0)
                {
                    throw CryptError(status);
                }

                status = BCryptGenerateSymmetricKey(
                    algorithm,
                    out keyHandle,
                    IntPtr.Zero,
                    0,
                    key,
                    key.Length,
                    0);
                if (status != 0)
                {
                    throw CryptError(status);
                }

                noncePointer = Marshal.AllocHGlobal(nonce.Length);
                tagPointer = Marshal.AllocHGlobal(tag.Length);
                cipherPointer = Marshal.AllocHGlobal(cipher.Length);
                plainPointer = Marshal.AllocHGlobal(plain.Length);
                Marshal.Copy(nonce, 0, noncePointer, nonce.Length);
                Marshal.Copy(tag, 0, tagPointer, tag.Length);
                Marshal.Copy(cipher, 0, cipherPointer, cipher.Length);

                BcryptAuthenticatedCipherModeInfo info = new BcryptAuthenticatedCipherModeInfo();
                info.cbSize = Marshal.SizeOf(typeof(BcryptAuthenticatedCipherModeInfo));
                info.dwInfoVersion = 1;
                info.pbNonce = noncePointer;
                info.cbNonce = nonce.Length;
                info.pbAuthData = IntPtr.Zero;
                info.cbAuthData = 0;
                info.pbTag = tagPointer;
                info.cbTag = tag.Length;
                info.pbMacContext = IntPtr.Zero;
                info.cbMacContext = 0;
                info.cbAAD = 0;
                info.cbData = 0;
                info.dwFlags = 0;

                int written;
                status = BCryptDecrypt(
                    keyHandle,
                    cipherPointer,
                    cipher.Length,
                    ref info,
                    IntPtr.Zero,
                    0,
                    plainPointer,
                    plain.Length,
                    out written,
                    0);
                if (status != 0)
                {
                    throw CryptError(status);
                }

                Marshal.Copy(plainPointer, plain, 0, written);
                return Encoding.UTF8.GetString(plain, 0, written);
            }
            finally
            {
                if (plainPointer != IntPtr.Zero) Marshal.FreeHGlobal(plainPointer);
                if (cipherPointer != IntPtr.Zero) Marshal.FreeHGlobal(cipherPointer);
                if (tagPointer != IntPtr.Zero) Marshal.FreeHGlobal(tagPointer);
                if (noncePointer != IntPtr.Zero) Marshal.FreeHGlobal(noncePointer);
                if (keyHandle != IntPtr.Zero) BCryptDestroyKey(keyHandle);
                if (algorithm != IntPtr.Zero) BCryptCloseAlgorithmProvider(algorithm, 0);
            }
        }

        private static Exception CryptError(int status)
        {
            return new InvalidOperationException(
                "Grok Bot token decryption failed (0x" + status.ToString("X8", CultureInfo.InvariantCulture) + ").");
        }

        private void Parse(byte[] data, CursorUsage usage)
        {
            int index = 0;
            while (index < data.Length)
            {
                ulong tag = ReadVarint(data, ref index);
                int field = (int)(tag >> 3);
                int wire = (int)(tag & 7);
                if (wire == 0)
                {
                    ulong value = ReadVarint(data, ref index);
                    if (field == 8)
                    {
                        usage.HasIncludedLimit = value != 0;
                    }
                }
                else if (wire == 1)
                {
                    if (index + 8 > data.Length)
                    {
                        break;
                    }
                    if (field == 3)
                    {
                        usage.Percent = Clamp(BitConverter.ToDouble(data, index));
                    }
                    index += 8;
                }
                else if (wire == 2)
                {
                    int length = (int)ReadVarint(data, ref index);
                    if (length < 0 || index + length > data.Length)
                    {
                        break;
                    }
                    if (field == 1)
                    {
                        usage.PeriodStart = ReadTimestamp(data, index, length);
                    }
                    else if (field == 2)
                    {
                        usage.PeriodEnd = ReadTimestamp(data, index, length);
                    }
                    else if (field == 15)
                    {
                        usage.Plan = Encoding.UTF8.GetString(data, index, length);
                    }
                    index += length;
                }
                else if (wire == 5)
                {
                    index += 4;
                }
                else
                {
                    break;
                }
            }

            if (!usage.PeriodEnd.HasValue && !usage.PeriodStart.HasValue && usage.Percent <= 0.0)
            {
                throw new InvalidOperationException("Grok Bot usage response was empty.");
            }
        }

        private static DateTimeOffset? ReadTimestamp(byte[] data, int offset, int length)
        {
            int index = offset;
            int end = offset + length;
            long seconds = 0;
            long nanos = 0;
            while (index < end)
            {
                ulong tag = ReadVarint(data, ref index);
                int field = (int)(tag >> 3);
                int wire = (int)(tag & 7);
                if (wire != 0)
                {
                    if (wire == 1) index += 8;
                    else if (wire == 2)
                    {
                        int skip = (int)ReadVarint(data, ref index);
                        index += skip;
                    }
                    else if (wire == 5) index += 4;
                    else break;
                    continue;
                }
                ulong value = ReadVarint(data, ref index);
                if (field == 1)
                {
                    seconds = (long)value;
                }
                else if (field == 2)
                {
                    nanos = (long)value;
                }
            }
            if (seconds <= 0)
            {
                return null;
            }
            return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100L);
        }

        private static ulong ReadVarint(byte[] data, ref int index)
        {
            ulong value = 0;
            int shift = 0;
            while (index < data.Length)
            {
                byte b = data[index++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return value;
                }
                shift += 7;
                if (shift >= 64)
                {
                    break;
                }
            }
            throw new InvalidOperationException("Grok Bot usage response was malformed.");
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || string.IsNullOrEmpty(key) || !map.TryGetValue(key, out value))
            {
                return null;
            }
            return value as Dictionary<string, object>;
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || string.IsNullOrEmpty(key) || !map.TryGetValue(key, out value) || value == null)
            {
                return null;
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.0;
            }
            return Math.Round(Math.Max(0.0, Math.Min(100.0, value)), 1);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BcryptAuthenticatedCipherModeInfo
        {
            public int cbSize;
            public int dwInfoVersion;
            public IntPtr pbNonce;
            public int cbNonce;
            public IntPtr pbAuthData;
            public int cbAuthData;
            public IntPtr pbTag;
            public int cbTag;
            public IntPtr pbMacContext;
            public int cbMacContext;
            public int cbAAD;
            public long cbData;
            public int dwFlags;
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptOpenAlgorithmProvider(
            out IntPtr phAlgorithm,
            string pszAlgId,
            string pszImplementation,
            int dwFlags);

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptSetProperty(
            IntPtr hObject,
            string pszProperty,
            byte[] pbInput,
            int cbInput,
            int dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptGenerateSymmetricKey(
            IntPtr hAlgorithm,
            out IntPtr phKey,
            IntPtr pbKeyObject,
            int cbKeyObject,
            byte[] pbSecret,
            int cbSecret,
            int dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDecrypt(
            IntPtr hKey,
            IntPtr pbInput,
            int cbInput,
            ref BcryptAuthenticatedCipherModeInfo pPaddingInfo,
            IntPtr pbIV,
            int cbIV,
            IntPtr pbOutput,
            int cbOutput,
            out int pcbResult,
            int dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyKey(IntPtr hKey);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, int dwFlags);

        public void Dispose()
        {
            http.Dispose();
        }
    }
}
