using System;
using System.Threading;
using System.Windows;

namespace GrokUsageWidget
{
    internal static class Program
    {
        [STAThread]
        public static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\GrokUsageWidget", out createdNew))
            {
                if (!createdNew)
                {
                    IntPtr existing = Native.FindWindow(null, "GrokUsageWidget");
                    if (existing != IntPtr.Zero)
                    {
                        Native.PostMessage(existing, Native.ShowWidgetMessage, IntPtr.Zero, IntPtr.Zero);
                    }
                    return;
                }

                Native.SetCurrentProcessExplicitAppUserModelID("com.jed.grokusagewidget");
                Application application = new Application();
                application.Run(new WidgetWindow());
            }
        }
    }
}
