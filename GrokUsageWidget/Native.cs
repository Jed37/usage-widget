using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GrokUsageWidget
{
    internal static class Native
    {
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public struct WINDOWPOS
        {
            public IntPtr Window;
            public IntPtr After;
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public uint Flags;
        }

        public const int ShowWidgetMessage = 32769;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string title);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr window, int index, int style);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr window);

        public static int GetStyle(IntPtr window)
        {
            return GetWindowLong(window, -16);
        }

        public static void SetStyle(IntPtr window, int style)
        {
            SetWindowLong(window, -16, style);
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 55u);
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        public static void Place(IntPtr window, int x, int y, int width, int height)
        {
            if (!SetWindowPos(window, IntPtr.Zero, x, y, width, height, 20u))
            {
                throw new Win32Exception();
            }
        }

        public static void Move(IntPtr window, int x, int y)
        {
            if (!SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, 21u))
            {
                throw new Win32Exception();
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public static void ConfigureFrame(IntPtr window)
        {
            try
            {
                int value = 1;
                int value2 = 1;
                int value3 = -2;
                int value4 = 1;
                DwmSetWindowAttribute(window, 20, ref value, 4);
                DwmSetWindowAttribute(window, 33, ref value2, 4);
                DwmSetWindowAttribute(window, 34, ref value3, 4);
                DwmSetWindowAttribute(window, 38, ref value4, 4);
            }
            catch
            {
            }
        }

        public static void SetMinimizable(IntPtr window, bool enabled)
        {
            if (window == IntPtr.Zero)
            {
                return;
            }
            int style = GetWindowLong(window, -16);
            int updated = (enabled ? (style | 0x20000) : (style & -131073)) & -65537;
            if (updated != style)
            {
                SetStyle(window, updated);
                ConfigureFrame(window);
            }
        }

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);
    }
}
