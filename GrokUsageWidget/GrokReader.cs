using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace GrokUsageWidget
{
    internal sealed class GrokUsage
    {
        public string Plan = "SuperGrok";
        public double TotalPercent;
        public double BuildPercent;
        public double ChatPercent;
        public bool HasBuild;
        public bool HasChat;
        public DateTimeOffset? PeriodStart;
        public DateTimeOffset? PeriodEnd;
        public string PeriodType = "WEEKLY";
        public double OnDemandCap;
        public double OnDemandUsed;
        public double PrepaidBalance;
        public bool Unified;
        public double BotPercent;
        public bool HasBot;
        public DateTimeOffset? BotPeriodStart;
        public DateTimeOffset? BotPeriodEnd;
        public string BotPlan;
        public string BotError;
        public DateTime UpdatedLocal;
        public string Error;
        public string Warning;
    }

    internal sealed class AuthAccount
    {
        public string Token;
        public string UserId;
        public DateTimeOffset? ExpiresAt;
        public string ExpiresAtRaw;
        public string RefreshToken;
        public string Issuer;
        public string ClientId;
    }

    internal sealed class GrokReader : IDisposable
    {
        private static readonly Uri BillingUri =
            new Uri("https://cli-chat-proxy.grok.com/v1/billing?format=credits");

        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly HttpClient http;
        private readonly CursorReader cursor = new CursorReader();
        private readonly SemaphoreSlim authLock = new SemaphoreSlim(1, 1);
        private string tokenEndpoint;

        public GrokReader()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GrokUsageWidget/0.1");
        }

        public async Task<GrokUsage> Fetch()
        {
            GrokUsage usage = new GrokUsage();
            usage.UpdatedLocal = DateTime.Now;
            Task build = FetchBuild(usage);
            Task bot = FetchBot(usage);
            await Task.WhenAll(build, bot).ConfigureAwait(false);
            return usage;
        }

        private async Task FetchBuild(GrokUsage usage)
        {
            try
            {
                AuthAccount auth = await FreshAuth(false).ConfigureAwait(false);
                string text = await Billing(auth).ConfigureAwait(false);
                if (text == null)
                {
                    auth = await FreshAuth(true).ConfigureAwait(false);
                    text = await Billing(auth).ConfigureAwait(false);
                    if (text == null)
                    {
                        throw new InvalidOperationException("Grok sign-in expired. Run `grok login`.");
                    }
                }
                Parse(text, usage);
                usage.HasBuild = true;
            }
            catch (Exception ex)
            {
                usage.Error = ex.Message;
            }
        }

        private async Task<string> Billing(AuthAccount auth)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, BillingUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
            if (!string.IsNullOrEmpty(auth.UserId))
            {
                request.Headers.TryAddWithoutValidation("x-userid", auth.UserId);
            }
            request.Headers.TryAddWithoutValidation("x-grok-client-identifier", "grok-usage-widget");
            using (HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false))
            {
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return text;
                }
                if ((int)response.StatusCode == 401)
                {
                    return null;
                }
                throw new InvalidOperationException("Grok billing returned HTTP " + (int)response.StatusCode + ".");
            }
        }

        private async Task<AuthAccount> FreshAuth(bool force)
        {
            await authLock.WaitAsync().ConfigureAwait(false);
            try
            {
                AuthAccount auth = ReadAuth();
                if (auth == null)
                {
                    throw new InvalidOperationException("Grok login not found. Run `grok login`.");
                }
                if (!force && auth.ExpiresAt.HasValue && auth.ExpiresAt.Value > DateTimeOffset.UtcNow.AddMinutes(3))
                {
                    return auth;
                }
                if (string.IsNullOrEmpty(auth.RefreshToken) || string.IsNullOrEmpty(auth.ClientId))
                {
                    throw new InvalidOperationException("Grok sign-in expired. Run `grok login`.");
                }
                return await Refresh(auth).ConfigureAwait(false);
            }
            finally
            {
                authLock.Release();
            }
        }

        private async Task<AuthAccount> Refresh(AuthAccount auth)
        {
            string endpoint = await TokenEndpoint(auth.Issuer).ConfigureAwait(false);
            FormUrlEncodedContent form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", auth.RefreshToken),
                new KeyValuePair<string, string>("client_id", auth.ClientId)
            });
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = form;
            request.Headers.Accept.ParseAdd("application/json");
            using (HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false))
            {
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode == 400 || (int)response.StatusCode == 401)
                    {
                        throw new InvalidOperationException("Grok sign-in expired. Run `grok login`.");
                    }
                    throw new InvalidOperationException("Grok token refresh returned HTTP " + (int)response.StatusCode + ".");
                }

                Dictionary<string, object> body = json.Deserialize<Dictionary<string, object>>(text);
                string access = GetString(body, "access_token");
                if (string.IsNullOrEmpty(access))
                {
                    throw new InvalidOperationException("Grok token refresh returned no access token.");
                }

                string rotated = GetString(body, "refresh_token");
                DateTimeOffset expires = ExpiryFromToken(access, GetDouble(body, "expires_in"));
                AuthAccount updated = new AuthAccount
                {
                    Token = access,
                    UserId = auth.UserId,
                    ExpiresAt = expires,
                    ExpiresAtRaw = expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00Z",
                    RefreshToken = string.IsNullOrEmpty(rotated) ? auth.RefreshToken : rotated,
                    Issuer = auth.Issuer,
                    ClientId = auth.ClientId
                };
                SaveAuth(auth, updated);
                return updated;
            }
        }

        private async Task<string> TokenEndpoint(string issuer)
        {
            if (!string.IsNullOrEmpty(tokenEndpoint))
            {
                return tokenEndpoint;
            }

            string baseIssuer = string.IsNullOrEmpty(issuer) ? "https://auth.x.ai" : issuer.TrimEnd('/');
            using (HttpResponseMessage response = await http.GetAsync(baseIssuer + "/.well-known/openid-configuration").ConfigureAwait(false))
            {
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Grok login discovery returned HTTP " + (int)response.StatusCode + ".");
                }
                Dictionary<string, object> doc = json.Deserialize<Dictionary<string, object>>(text);
                string endpoint = GetString(doc, "token_endpoint");
                if (string.IsNullOrEmpty(endpoint))
                {
                    throw new InvalidOperationException("Grok login discovery has no token endpoint.");
                }
                tokenEndpoint = endpoint;
                return endpoint;
            }
        }

        private async Task FetchBot(GrokUsage usage)
        {
            CursorUsage bot = await cursor.Fetch().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(bot.Error))
            {
                usage.BotError = bot.Error;
                return;
            }
            usage.BotPercent = bot.Percent;
            usage.BotPeriodStart = bot.PeriodStart;
            usage.BotPeriodEnd = bot.PeriodEnd;
            usage.BotPlan = bot.Plan;
            usage.HasBot = true;
        }

        private AuthAccount ReadAuth()
        {
            string path = AuthPath();
            if (!File.Exists(path))
            {
                return null;
            }

            string text = ReadShared(path);

            Dictionary<string, object> root = json.Deserialize<Dictionary<string, object>>(text);
            if (root == null)
            {
                return null;
            }

            foreach (object value in root.Values)
            {
                Dictionary<string, object> account = value as Dictionary<string, object>;
                if (account == null)
                {
                    continue;
                }
                string token = GetString(account, "key");
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }
                string expiresRaw = GetString(account, "expires_at");
                return new AuthAccount
                {
                    Token = token,
                    UserId = GetString(account, "user_id"),
                    ExpiresAt = ParseDate(expiresRaw),
                    ExpiresAtRaw = expiresRaw,
                    RefreshToken = GetString(account, "refresh_token"),
                    Issuer = GetString(account, "oidc_issuer"),
                    ClientId = GetString(account, "oidc_client_id")
                };
            }
            return null;
        }

        private void SaveAuth(AuthAccount previous, AuthAccount updated)
        {
            string path = AuthPath();
            string text = ReadShared(path);
            string next = ReplaceQuoted(text, previous.Token, updated.Token);
            next = ReplaceQuoted(next, previous.RefreshToken, updated.RefreshToken);
            next = ReplaceQuoted(next, previous.ExpiresAtRaw, updated.ExpiresAtRaw);
            bool savedToken = next.IndexOf("\"" + updated.Token + "\"", StringComparison.Ordinal) >= 0;
            bool savedRefresh = next.IndexOf("\"" + updated.RefreshToken + "\"", StringComparison.Ordinal) >= 0;
            bool savedExpiry = next.IndexOf("\"" + updated.ExpiresAtRaw + "\"", StringComparison.Ordinal) >= 0;
            if (!savedToken || !savedRefresh || !savedExpiry)
            {
                Dictionary<string, object> root = json.Deserialize<Dictionary<string, object>>(text);
                if (root == null)
                {
                    throw new InvalidOperationException("Could not save refreshed Grok login.");
                }
                bool found = false;
                foreach (object value in root.Values)
                {
                    Dictionary<string, object> account = value as Dictionary<string, object>;
                    if (account == null)
                    {
                        continue;
                    }
                    string token = GetString(account, "key");
                    string refresh = GetString(account, "refresh_token");
                    if (token == previous.Token || refresh == previous.RefreshToken || token == updated.Token)
                    {
                        account["key"] = updated.Token;
                        account["refresh_token"] = updated.RefreshToken;
                        account["expires_at"] = updated.ExpiresAtRaw;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    throw new InvalidOperationException("Could not save refreshed Grok login.");
                }
                next = json.Serialize(root);
            }
            WriteAtomic(path, next);
        }

        private static string ReplaceQuoted(string text, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(oldValue) || oldValue == newValue)
            {
                return text;
            }
            string needle = "\"" + oldValue + "\"";
            int index = text.IndexOf(needle, StringComparison.Ordinal);
            if (index < 0)
            {
                return text;
            }
            return text.Substring(0, index) + "\"" + newValue + "\"" + text.Substring(index + needle.Length);
        }

        private static void WriteAtomic(string path, string text)
        {
            string temp = path + ".widget.tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            try
            {
                File.Replace(temp, path, null);
            }
            catch (IOException)
            {
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }

        private static DateTimeOffset ExpiryFromToken(string access, double expiresIn)
        {
            DateTimeOffset? exp = ReadJwtExpiry(access);
            if (exp.HasValue)
            {
                return exp.Value;
            }
            double seconds = expiresIn > 60.0 ? expiresIn : 21600.0;
            return DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        private static DateTimeOffset? ReadJwtExpiry(string jwt)
        {
            if (string.IsNullOrEmpty(jwt))
            {
                return null;
            }
            string[] parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }
            try
            {
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                int pad = payload.Length % 4;
                if (pad == 2)
                {
                    payload += "==";
                }
                else if (pad == 3)
                {
                    payload += "=";
                }
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                int marker = text.IndexOf("\"exp\"", StringComparison.Ordinal);
                if (marker < 0)
                {
                    return null;
                }
                int colon = text.IndexOf(':', marker);
                if (colon < 0)
                {
                    return null;
                }
                int start = colon + 1;
                while (start < text.Length && (text[start] == ' ' || text[start] == '\t'))
                {
                    start++;
                }
                int end = start;
                while (end < text.Length && text[end] >= '0' && text[end] <= '9')
                {
                    end++;
                }
                long seconds;
                if (end == start || !long.TryParse(text.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                {
                    return null;
                }
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static string AuthPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".grok",
                "auth.json");
        }

        private void Parse(string text, GrokUsage usage)
        {
            Dictionary<string, object> root = json.Deserialize<Dictionary<string, object>>(text);
            Dictionary<string, object> config = GetObject(root, "config");
            if (config == null)
            {
                throw new InvalidOperationException("Grok billing response has no config.");
            }

            usage.TotalPercent = Clamp(GetDouble(config, "creditUsagePercent"));
            usage.OnDemandCap = GetMoney(config, "onDemandCap");
            usage.OnDemandUsed = GetMoney(config, "onDemandUsed");
            usage.PrepaidBalance = GetMoney(config, "prepaidBalance");
            usage.Unified = GetBool(config, "isUnifiedBillingUser");

            Dictionary<string, object> period = GetObject(config, "currentPeriod");
            if (period != null)
            {
                usage.PeriodType = GetString(period, "type") ?? usage.PeriodType;
                usage.PeriodStart = ParseDate(GetString(period, "start"));
                usage.PeriodEnd = ParseDate(GetString(period, "end"));
            }
            if (!usage.PeriodStart.HasValue)
            {
                usage.PeriodStart = ParseDate(GetString(config, "billingPeriodStart"));
            }
            if (!usage.PeriodEnd.HasValue)
            {
                usage.PeriodEnd = ParseDate(GetString(config, "billingPeriodEnd"));
            }
            usage.PeriodType = usage.PeriodType.IndexOf("MONTHLY", StringComparison.OrdinalIgnoreCase) >= 0
                ? "MONTHLY"
                : "WEEKLY";

            bool sawBuild = false;
            bool sawChat = false;
            object productsValue;
            if (config.TryGetValue("productUsage", out productsValue) && productsValue != null)
            {
                IEnumerable products = productsValue as IEnumerable;
                if (products != null)
                {
                    foreach (object item in products)
                    {
                        Dictionary<string, object> product = item as Dictionary<string, object>;
                        if (product == null)
                        {
                            continue;
                        }
                        string name = GetString(product, "product") ?? "";
                        double percent = Clamp(GetDouble(product, "usagePercent"));
                        if (name.Equals("GrokBuild", StringComparison.OrdinalIgnoreCase))
                        {
                            usage.BuildPercent = percent;
                            sawBuild = true;
                        }
                        else if (name.Equals("GrokChat", StringComparison.OrdinalIgnoreCase))
                        {
                            usage.ChatPercent = percent;
                            sawChat = true;
                        }
                    }
                }
            }

            if (!sawBuild && !sawChat)
            {
                usage.BuildPercent = usage.TotalPercent;
                usage.ChatPercent = 0.0;
                usage.Warning = "Per-product usage was not provided; showing the shared pool for Grok Build.";
            }
            else if (!sawBuild)
            {
                usage.BuildPercent = Math.Max(0.0, usage.TotalPercent - usage.ChatPercent);
                usage.Warning = "Grok Build usage was not provided separately.";
            }
            else if (!sawChat)
            {
                usage.ChatPercent = 0.0;
            }

            if (usage.TotalPercent <= 0.0 && (usage.BuildPercent > 0.0 || usage.ChatPercent > 0.0))
            {
                usage.TotalPercent = Clamp(usage.BuildPercent + usage.ChatPercent);
            }
            usage.HasChat = sawChat;
        }

        private static Dictionary<string, object> GetObject(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value))
            {
                return null;
            }
            return value as Dictionary<string, object>;
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null)
            {
                return null;
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double GetDouble(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null)
            {
                return 0.0;
            }
            double result;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
                && !double.IsNaN(result) && !double.IsInfinity(result))
            {
                return result;
            }
            return 0.0;
        }

        private static double GetMoney(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null)
            {
                return 0.0;
            }
            Dictionary<string, object> cent = value as Dictionary<string, object>;
            if (cent != null)
            {
                return GetDouble(cent, "val") / 100.0;
            }
            double direct;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out direct))
            {
                return direct;
            }
            return 0.0;
        }

        private static bool GetBool(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null)
            {
                return false;
            }
            bool result;
            if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result))
            {
                return result;
            }
            return false;
        }

        private static DateTimeOffset? ParseDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            DateTimeOffset result;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result))
            {
                return result;
            }
            return null;
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.0;
            }
            return Math.Round(Math.Max(0.0, Math.Min(100.0, value)), 1);
        }

        private static string ReadShared(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        public void Dispose()
        {
            http.Dispose();
            cursor.Dispose();
            authLock.Dispose();
        }
    }
}
