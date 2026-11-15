using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PathShape = System.Windows.Shapes.Path;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]
[assembly: AssemblyTitle("UsageWidget")]
[assembly: AssemblyProduct("UsageWidget")]
[assembly: AssemblyVersion("0.4.1.0")]
[assembly: AssemblyFileVersion("0.4.1.0")]

namespace UsageWidget
{
    static class Program
    {
        [STAThread]
        public static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\UsageWidget.Native", out first))
            {
                if (!first)
                {
                    IntPtr window = Native.FindWindow(null, "UsageWidget");
                    if (window != IntPtr.Zero)
                    {
                        Native.PostMessage(window, Native.ShowWidgetMessage, IntPtr.Zero, IntPtr.Zero);
                    }
                    return;
                }
                Native.SetCurrentProcessExplicitAppUserModelID("com.jed.usagewidget");
                new Application().Run(new WidgetWindow());
            }
        }
    }

    sealed class WidgetWindow : Window
    {
        readonly Grid content;
        readonly CodexReader reader = new CodexReader();
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        readonly TextBlock primary, weekly, primaryReset, weeklyReset, status;
        readonly TextBlock compactValue, compactReset;
        readonly Grid fullView, compactView;
        readonly Run plan;
        readonly ProgressBar primaryBar, weeklyBar;
        readonly Button refresh;
        readonly Button usageCheck;
        readonly Forms.NotifyIcon tray;
        readonly Forms.ContextMenuStrip trayMenu;
        readonly Forms.ToolStripMenuItem visibilityMenu, refreshMenu, usageMenu;
        readonly Drawing.Icon trayIcon;
        Window usageWindow;
        TextBox usageDetails;
        ComboBox resetSelection;
        Button redeem;
        TextBlock redeemStatus;
        bool redeeming, resetDetailsFresh;
        readonly Dictionary<string, string> redemptionKeys = new Dictionary<string, string>();
        Usage latestUsage;
        Task refreshTask = Task.FromResult(0);
        bool readingResetDetails;
        bool busy, closing, compact, compactPressing;
        Point compactPressScreen;
        DateTime? updated;
        Point moveAnchorScreen;
        bool moving, applying;
        Size fullSize = new Size(FullW, FullH);
        Size? compactSize;
        readonly Border designRoot;
        IntPtr handle;
        const double FullW = 300, FullH = 130, Small = 74, MinScale = 0.65, MaxScale = 4;

        public WidgetWindow()
        {
            Title = "UsageWidget";
            Width = FullW; Height = FullH;
            SetLimits();
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false; Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            WindowChrome.SetWindowChrome(this, new WindowChrome {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Widget.xaml"))
                designRoot = (Border)XamlReader.Load(stream);
            SetDesignSize();
            content = (Grid)designRoot.Child;
            Content = new Viewbox { Stretch = Stretch.Uniform, Child = designRoot };
            // Screen-pixel offsets stay correct while dragging across monitors with different DPI.
            content.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                if (e.ChangedButton != MouseButton.Left) return;
                if (InsideButton(e.OriginalSource as DependencyObject)) return; // Buttons keep their clicks; resize edges never reach here.
                moveAnchorScreen = PointToScreen(e.GetPosition(this));
                compactPressing = false;
                if (compact)
                {
                    compactPressScreen = moveAnchorScreen;
                    compactPressing = true;
                }
                moving = content.CaptureMouse();
                if (!moving) compactPressing = false;
            };
            content.PreviewMouseMove += delegate(object sender, MouseEventArgs e) {
                if (!moving || e.LeftButton != MouseButtonState.Pressed) return;
                Point now = PointToScreen(e.GetPosition(this));
                var frame = ReadFrame();
                frame.X += (int)Math.Round(now.X - moveAnchorScreen.X);
                frame.Y += (int)Math.Round(now.Y - moveAnchorScreen.Y);
                frame = ClampFrame(frame);
                Native.Move(handle, frame.X, frame.Y);
                moveAnchorScreen = now;
            };
            content.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) {
                bool expand = false;
                if (compactPressing)
                {
                    Point release = PointToScreen(e.GetPosition(this));
                    double dx = release.X - compactPressScreen.X, dy = release.Y - compactPressScreen.Y;
                    expand = dx * dx + dy * dy < 64;
                }
                moving = false; compactPressing = false;
                content.ReleaseMouseCapture();
                if (expand) SetCompact(false);
            };
            content.LostMouseCapture += delegate { moving = false; compactPressing = false; };
            primary = Element<TextBlock>("PrimaryValue"); weekly = Element<TextBlock>("WeeklyValue");
            primaryReset = Element<TextBlock>("PrimaryReset"); weeklyReset = Element<TextBlock>("WeeklyReset");
            primaryBar = Element<ProgressBar>("PrimaryBar"); weeklyBar = Element<ProgressBar>("WeeklyBar");
            plan = Element<Run>("Plan"); status = Element<TextBlock>("Status");
            refresh = Element<Button>("Refresh");
            usageCheck = Element<Button>("UsageCheck");
            fullView = Element<Grid>("FullView"); compactView = Element<Grid>("CompactView");
            compactValue = Element<TextBlock>("CompactValue"); compactReset = Element<TextBlock>("CompactReset");
            var pin = Element<Button>("Pin");
            pin.Click += delegate {
                Topmost = !Topmost;
                var shape = Element<PathShape>("PinShape");
                shape.Stroke = new SolidColorBrush(Topmost ? Color.FromRgb(218, 226, 241) : Color.FromRgb(132, 143, 161));
                shape.Data = Geometry.Parse(Topmost
                    ? "M16,9 L16,4 17,3 7,3 8,4 8,9 5,12 5,14 19,14 19,12 16,9 M12,14 L12,21"
                    : "M2,2 L22,22 M16,9 L16,4 17,3 7,3 M9,9 L5,12 5,14 14,14 M12,14 L12,21");
                PinPolicy();
            };
            Element<Button>("Minimize").Click += delegate { HideWidget(); };
            Element<Button>("Close").Click += delegate { HideWidget(); };
            Element<Button>("Collapse").Click += delegate { SetCompact(true); };
            refresh.Click += async delegate { await Refresh(); };
            usageCheck.Click += async delegate { await ShowUsage(); };
            trayIcon = CreateTrayIcon();
            Icon = Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            Icon.Freeze();
            visibilityMenu = new Forms.ToolStripMenuItem("Hide widget", null, delegate { ToggleWidget(); });
            refreshMenu = new Forms.ToolStripMenuItem("Refresh usage", null, async delegate { await Refresh(); });
            usageMenu = new Forms.ToolStripMenuItem("Check usage / reset availability", null, async delegate { await ShowUsage(); });
            trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.AddRange(new Forms.ToolStripItem[] {
                visibilityMenu, refreshMenu, usageMenu, new Forms.ToolStripSeparator(),
                new Forms.ToolStripMenuItem("Exit", null, delegate { Close(); })
            });
            trayMenu.Opening += delegate { visibilityMenu.Text = IsVisible ? "Hide widget" : "Show widget"; };
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "UsageWidget", ContextMenuStrip = trayMenu, Visible = true };
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) {
                if (e.Button == Forms.MouseButtons.Left) ToggleWidget();
            };
            timer.Tick += async delegate { await Refresh(); };
            Loaded += async delegate { PinPolicy(); await Refresh(); };
            StateChanged += delegate { if (WindowState != WindowState.Minimized) PinPolicy(); };
            Closed += delegate {
                closing = true; timer.Stop(); reader.Dispose();
                if (usageWindow != null) usageWindow.Close();
                tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); trayIcon.Dispose();
            };
            SourceInitialized += delegate {
                handle = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(handle).AddHook(WindowMessage);
                StripBorder();
                Native.ConfigureFrame(handle);
            };
        }

        static Drawing.Icon CreateTrayIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsageWidget.ico"))
                return new Drawing.Icon(stream, 32, 32);
        }

        void ShowWidget()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        void HideWidget()
        {
            if (usageWindow != null) usageWindow.Hide();
            Hide();
        }

        void ToggleWidget() { if (IsVisible) HideWidget(); else ShowWidget(); }

        async Task ShowUsage()
        {
            if (usageWindow == null)
            {
                usageDetails = new TextBox {
                    IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(16),
                    FontFamily = FontFamily, FontSize = 14, BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Color.FromRgb(20, 22, 29)), Foreground = Brushes.WhiteSmoke,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                resetSelection = new ComboBox { Margin = new Thickness(12, 8, 12, 0) };
                redeem = new Button { Content = "Redeem selected reset", Margin = new Thickness(12, 8, 12, 0), Padding = new Thickness(8), IsEnabled = false };
                redeemStatus = new TextBlock { Margin = new Thickness(12, 8, 12, 12), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray };
                resetSelection.SelectionChanged += delegate { UpdateRedeemState(); };
                redeem.Click += async delegate { await RedeemSelected(); };
                var layout = new Grid { Background = usageDetails.Background };
                layout.RowDefinitions.Add(new RowDefinition());
                for (int row = 1; row < 4; row++) layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(resetSelection, 1); Grid.SetRow(redeem, 2); Grid.SetRow(redeemStatus, 3);
                layout.Children.Add(usageDetails); layout.Children.Add(resetSelection);
                layout.Children.Add(redeem); layout.Children.Add(redeemStatus);
                usageWindow = new Window {
                    Title = "UsageWidget — Usage & resets", Owner = this, ShowInTaskbar = false,
                    Icon = Icon,
                    Width = 520, Height = 520, MinWidth = 340, MinHeight = 320,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = layout
                };
                usageWindow.Closed += delegate {
                    usageWindow = null; usageDetails = null; resetSelection = null; redeem = null; redeemStatus = null;
                };
            }
            usageDetails.Text = "Checking usage and reset availability…";
            usageWindow.Show(); usageWindow.Activate();
            await Refresh(true);
        }

        void SetBusyState(bool value)
        {
            refresh.IsEnabled = !value; usageCheck.IsEnabled = !value;
            refreshMenu.Enabled = !value; usageMenu.Enabled = !value;
            if (resetSelection != null) resetSelection.IsEnabled = !value;
            UpdateRedeemState();
        }

        void UpdateRedeemState()
        {
            if (redeem == null) return;
            var selected = resetSelection.SelectedItem as ResetCredit;
            redeem.IsEnabled = !busy && !redeeming && resetDetailsFresh && selected != null && !string.IsNullOrEmpty(selected.Id)
                && (!selected.ExpiresAt.HasValue || selected.ExpiresAt.Value > DateTime.Now);
            redeem.Content = selected != null && redemptionKeys.ContainsKey(selected.Id ?? "")
                ? "Retry selected reset" : "Redeem selected reset";
        }

        void UpdateResetSelection(ResetCredits credits)
        {
            if (resetSelection == null) return;
            var previous = resetSelection.SelectedItem as ResetCredit;
            resetSelection.ItemsSource = credits.Credits;
            ResetCredit selected = null;
            if (previous != null) selected = credits.Credits.Find(delegate(ResetCredit credit) { return credit.Id == previous.Id; });
            resetSelection.SelectedItem = selected;
            UpdateRedeemState();
        }

        async Task RedeemSelected()
        {
            UpdateRedeemState();
            var selected = resetSelection == null ? null : resetSelection.SelectedItem as ResetCredit;
            if (closing || busy || redeeming || selected == null || !redeem.IsEnabled) return;
            string key;
            if (!redemptionKeys.TryGetValue(selected.Id, out key)) {
                key = Guid.NewGuid().ToString();
                redemptionKeys.Add(selected.Id, key);
            }
            redeeming = true; timer.Stop(); SetBusyState(true);
            redeemStatus.Text = "Redeeming selected reset…";
            bool refreshAfter = false;
            try {
                var outcome = await reader.Redeem(selected.Id, key);
                if (closing) return;
                redemptionKeys.Remove(selected.Id);
                if (redeemStatus != null) redeemStatus.Text = RedemptionMessage(outcome);
                refreshAfter = true;
            }
            catch (Exception error) {
                if (!closing && redeemStatus != null) redeemStatus.Text = "Could not confirm redemption: " + error.Message
                    + "\nRetry keeps the same request ID to avoid a duplicate reset.";
            }
            finally {
                redeeming = false;
                if (!closing) { SetBusyState(busy); timer.Start(); }
            }
            if (refreshAfter && !closing) await Refresh(true);
        }

        static string RedemptionMessage(RedemptionOutcome outcome)
        {
            switch (outcome) {
                case RedemptionOutcome.Reset: return "Reset redeemed. Refreshing usage…";
                case RedemptionOutcome.AlreadyRedeemed: return "This request was already redeemed. Refreshing usage…";
                case RedemptionOutcome.NothingToReset: return "No current usage-limit window is eligible for a reset.";
                case RedemptionOutcome.NoCredit: return "No reset credit is available for this account.";
                default: throw new InvalidOperationException("Unknown redemption outcome");
            }
        }

        T Element<T>(string name) where T : DependencyObject { return (T)content.FindName(name); }

        struct Frame { public int X, Y, W, H; }

        double DesignW { get { return compact ? Small : FullW; } }
        double DesignH { get { return compact ? Small : FullH; } }

        double DeviceScale { get { return handle == IntPtr.Zero ? 1 : Native.GetDpiForWindow(handle) / 96.0; } }

        void SetDesignSize()
        {
            designRoot.Width = DesignW; designRoot.Height = DesignH;
            double radius = designRoot.CornerRadius.TopLeft;
            designRoot.Clip = new RectangleGeometry(new Rect(0, 0, DesignW, DesignH), radius, radius);
        }

        void SetLimits()
        {
            MinWidth = DesignW * MinScale; MinHeight = DesignH * MinScale;
            MaxWidth = DesignW * MaxScale; MaxHeight = DesignH * MaxScale;
        }

        Frame ReadFrame()
        {
            Native.RECT rect;
            if (!Native.GetWindowRect(handle, out rect)) throw new System.ComponentModel.Win32Exception();
            return new Frame { X = rect.Left, Y = rect.Top, W = rect.Right - rect.Left, H = rect.Bottom - rect.Top };
        }

        void Place(Frame frame)
        {
            frame = ClampFrame(frame);
            applying = true;
            try
            {
                Width = frame.W / DeviceScale; Height = frame.H / DeviceScale;
                UpdateLayout();
                Native.Place(handle, frame.X, frame.Y, frame.W, frame.H);
            }
            finally { applying = false; }
        }

        Frame ClampFrame(Frame frame)
        {
            var area = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
                frame.X, frame.Y, Math.Max(1, frame.W), Math.Max(1, frame.H))).WorkingArea;
            if (frame.W < area.Width) frame.X = Math.Max(area.Left, Math.Min(frame.X, area.Right - frame.W));
            if (frame.H < area.Height) frame.Y = Math.Max(area.Top, Math.Min(frame.Y, area.Bottom - frame.H));
            return frame;
        }

        void SetCompact(bool on)
        {
            if (on == compact) return;
            var from = ReadFrame();
            double dpi = DeviceScale;
            var currentSize = new Size(from.W / dpi, from.H / dpi);
            if (compact) compactSize = currentSize;
            else fullSize = currentSize;
            if (!compactSize.HasValue)
            {
                double scale = currentSize.Width / FullW;
                compactSize = new Size(Small * scale, Small * scale);
            }
            compact = on;
            moving = false; compactPressing = false;
            content.ReleaseMouseCapture();
            fullView.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            compactView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            SetDesignSize();
            SetLimits();
            Size size = on ? compactSize.Value : fullSize;
            var sized = new Frame { W = (int)Math.Round(size.Width * dpi), H = (int)Math.Round(size.Height * dpi) };
            // Keep the current center; moving either mode moves the next mode too.
            sized.X = from.X + (from.W - sized.W) / 2;
            sized.Y = from.Y + (from.H - sized.H) / 2;
            Place(sized);
        }

        void LockAspect(ref Native.RECT rect, int edge)
        {
            double aspect = DesignW / DesignH;
            int minW = Math.Max(1, (int)Math.Round(DesignW * MinScale * DeviceScale));
            int maxW = Math.Max(minW, (int)Math.Round(DesignW * MaxScale * DeviceScale));
            bool left = edge == 1 || edge == 4 || edge == 7;
            bool right = edge == 2 || edge == 5 || edge == 8;
            bool top = edge == 3 || edge == 4 || edge == 5;
            bool bottom = edge == 6 || edge == 7 || edge == 8;
            int width = Math.Max(1, rect.Right - rect.Left);
            int height = Math.Max(1, rect.Bottom - rect.Top);
            if ((top || bottom) && !(left || right)) width = (int)Math.Round(height * aspect);
            if (width < minW) width = minW;
            if (width > maxW) width = maxW;
            height = Math.Max(1, (int)Math.Round(width / aspect));
            int x = left && !right ? rect.Right - width : rect.Left;
            int y = top && !bottom ? rect.Bottom - height : rect.Top;
            if (!left && !right) x = (rect.Left + rect.Right - width) / 2;
            if (!top && !bottom) y = (rect.Top + rect.Bottom - height) / 2;
            rect.Left = x; rect.Top = y; rect.Right = x + width; rect.Bottom = y + height;
        }

        static bool InsideButton(DependencyObject source)
        {
            while (source != null)
            {
                if (source is Button) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        void PinPolicy() { if (handle != IntPtr.Zero) Native.SetMinimizable(handle, !Topmost); }

        void StripBorder()
        {
            if (handle == IntPtr.Zero) return;
            int style = Native.GetStyle(handle);
            int next = style & ~0x00C00000; // WS_BORDER | WS_DLGFRAME, keep thick frame for resize.
            if (next != style) Native.SetStyle(handle, next);
        }

        IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == Native.ShowWidgetMessage)
            {
                Dispatcher.BeginInvoke(new Action(ShowWidget));
                handled = true;
                return IntPtr.Zero;
            }
            if (message == 0x0214 && !applying)
            {
                var rect = (Native.RECT)Marshal.PtrToStructure(lParam, typeof(Native.RECT));
                LockAspect(ref rect, wParam.ToInt32());
                Marshal.StructureToPtr(rect, lParam, false);
                handled = true;
                return (IntPtr)1;
            }
            if (message == 0x0046 && !applying && WindowState == WindowState.Normal)
            {
                var position = (Native.WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(Native.WINDOWPOS));
                if ((position.Flags & 1) == 0 && position.Width > 0 && position.Height > 0)
                {
                    var rect = new Native.RECT { Right = position.Width, Bottom = position.Height };
                    LockAspect(ref rect, 2);
                    position.Width = rect.Right - rect.Left;
                    position.Height = rect.Bottom - rect.Top;
                    Marshal.StructureToPtr(position, lParam, false);
                }
            }
            long command = wParam.ToInt64() & 0xfff0;
            if (message == 0x112 && (command == 0xf030 || (command == 0xf020 && Topmost)))
                handled = true; // No maximize or automatic pinned minimize; explicit minimize uses WindowState.
            return IntPtr.Zero;
        }

        Task Refresh(bool includeResetDetails = false)
        {
            if (closing || redeeming) return Task.FromResult(0);
            includeResetDetails = includeResetDetails || (usageWindow != null && usageWindow.IsVisible);
            if (!refreshTask.IsCompleted)
                return includeResetDetails && !readingResetDetails ? RefreshDetailsAfter(refreshTask) : refreshTask;
            readingResetDetails = includeResetDetails;
            refreshTask = UpdateUsage(includeResetDetails);
            return refreshTask;
        }

        async Task RefreshDetailsAfter(Task pending)
        {
            await pending;
            await Refresh(true);
        }

        async Task UpdateUsage(bool includeResetDetails)
        {
            if (busy || closing) return;
            busy = true; timer.Stop(); SetBusyState(true);
            resetDetailsFresh = false;
            try
            {
                var data = await reader.Read(includeResetDetails);
                if (closing) return;
                latestUsage = data;
                resetDetailsFresh = includeResetDetails && data.ResetCredits.AvailableCount.GetValueOrDefault() > 0;
                plan.Text = data.Plan == "plus" ? "Plus" : data.Plan;
                Render(primary, primaryBar, primaryReset, data.Primary);
                Render(weekly, weeklyBar, weeklyReset, data.Weekly);
                compactValue.Text = primary.Text;
                compactReset.Text = primaryReset.Text;
                updated = DateTime.Now;
                status.Text = "Updated " + updated.Value.ToString("HH:mm");
                status.ToolTip = null;
                status.Foreground = new SolidColorBrush(Color.FromRgb(156, 167, 184));
                string resets = data.ResetCredits.AvailableCount.HasValue ? data.ResetCredits.AvailableCount.Value.ToString(CultureInfo.InvariantCulture) : "?";
                string tooltip = "UsageWidget | 5h " + primary.Text + " | week " + weekly.Text + " | resets " + resets;
                tray.Text = tooltip.Length <= 63 ? tooltip : tooltip.Substring(0, 63);
                if (usageDetails != null) {
                    usageDetails.Text = DescribeUsage(data);
                    UpdateResetSelection(data.ResetCredits);
                }
            }
            catch (Exception error)
            {
                if (!closing)
                {
                    status.Text = updated.HasValue ? "Update failed · " + updated.Value.ToString("HH:mm") : "Update failed";
                    status.ToolTip = error.Message;
                    status.Foreground = new SolidColorBrush(Color.FromRgb(226, 166, 166));
                    tray.Text = "UsageWidget — update failed";
                    if (usageDetails != null) usageDetails.Text = "Usage check failed: " + error.Message
                        + (latestUsage == null ? "" : "\n\nLast successful result (not current):\n" + DescribeUsage(latestUsage));
                }
            }
            finally
            {
                busy = false;
                if (!closing) {
                    SetBusyState(redeeming); timer.Start();
                }
            }
        }

        static string DescribeUsage(Usage data)
        {
            var text = new StringBuilder();
            text.AppendLine("Codex " + data.Plan);
            text.AppendLine("5-hour quota: " + data.Primary.RemainingText + " remaining · Reset " + data.Primary.Reset);
            text.AppendLine("Weekly quota: " + data.Weekly.RemainingText + " remaining · Reset " + data.Weekly.Reset);
            text.AppendLine();
            var resets = data.ResetCredits;
            text.AppendLine("Usage limit resets");
            if (!resets.AvailableCount.HasValue)
                text.AppendLine("Reset availability was not provided by this Codex version/account.");
            else {
                text.AppendLine(resets.AvailableCount.Value.ToString(CultureInfo.InvariantCulture) + " usage limit resets available.");
                foreach (var credit in resets.Credits) {
                    text.AppendLine();
                    text.AppendLine("• " + credit.Title);
                    text.AppendLine("  " + (credit.ExpiresAt.HasValue
                        ? "Expires " + credit.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                        : credit.NoExpiration ? "No expiration" : "Expiry not provided"));
                    if (!string.IsNullOrEmpty(credit.Description)) text.AppendLine("  " + credit.Description);
                }
                if (resets.AvailableCount.Value > resets.Credits.Count)
                    text.AppendLine("\nSome expiry details were not provided by the server.");
            }
            return text.ToString();
        }

        static void Render(TextBlock value, ProgressBar bar, TextBlock reset, Quota quota)
        {
            double remaining = quota.Remaining ?? 0;
            value.Text = quota.RemainingText;
            bar.Value = remaining;
            bar.Foreground = new SolidColorBrush(!quota.Used.HasValue ? Color.FromRgb(148, 167, 202)
                : remaining <= 10 ? Color.FromRgb(217, 83, 79)
                : remaining <= 30 ? Color.FromRgb(216, 162, 61) : Color.FromRgb(148, 167, 202));
            reset.Text = "Reset " + quota.Reset;
        }
    }

    sealed class Quota
    {
        public double? Used;
        public string Reset = "--";
        public double? Remaining { get { return Used.HasValue ? Math.Round(Math.Max(0, Math.Min(100, 100 - Used.Value)), 1) : (double?)null; } }
        public string RemainingText { get { return Remaining.HasValue ? Remaining.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "--"; } }
    }

    sealed class Usage
    {
        public string Plan = "";
        public Quota Primary = new Quota(), Weekly = new Quota();
        public ResetCredits ResetCredits = new ResetCredits();
    }

    sealed class ResetCredits
    {
        public long? AvailableCount;
        public List<ResetCredit> Credits = new List<ResetCredit>();
    }

    sealed class ResetCredit
    {
        public string Id, Title, Description;
        public DateTime? ExpiresAt;
        public bool NoExpiration;
        public override string ToString() {
            return Title + " — " + (ExpiresAt.HasValue ? ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : NoExpiration ? "No expiration" : "Expiry not provided");
        }
    }

    enum RedemptionOutcome { Reset, NothingToReset, NoCredit, AlreadyRedeemed }

    sealed class CodexReader : IDisposable
    {
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        Process child;
        string executable;

        public async Task<Usage> Read(bool includeResetDetails = false)
        {
            var result = await Request("account/rateLimits/read", new { excludeResetCreditDetails = !includeResetDetails });
            var limits = Map(result, "rateLimits");
            if (limits == null) throw new InvalidOperationException("Codex usage unavailable");
            return new Usage {
                Plan = limits.ContainsKey("planType") ? Convert.ToString(limits["planType"]) : "",
                Primary = ParseQuota(Map(limits, "primary"), false),
                Weekly = ParseQuota(Map(limits, "secondary"), true),
                ResetCredits = ParseResetCredits(Map(result, "rateLimitResetCredits"))
            };
        }

        public async Task<RedemptionOutcome> Redeem(string creditId, string idempotencyKey)
        {
            if (string.IsNullOrWhiteSpace(creditId) || string.IsNullOrWhiteSpace(idempotencyKey))
                throw new ArgumentException("A selected credit and request ID are required");
            var result = await Request("account/rateLimitResetCredit/consume", new { creditId, idempotencyKey });
            object value;
            string outcome = result.TryGetValue("outcome", out value) ? Convert.ToString(value) : "";
            switch (outcome) {
                case "reset": return RedemptionOutcome.Reset;
                case "nothingToReset": return RedemptionOutcome.NothingToReset;
                case "noCredit": return RedemptionOutcome.NoCredit;
                case "alreadyRedeemed": return RedemptionOutcome.AlreadyRedeemed;
                default: throw new InvalidOperationException("Codex returned an unknown redemption outcome");
            }
        }

        async Task<Dictionary<string, object>> Request(string method, object parameters)
        {
            executable = executable ?? FindExecutable();
            var process = new Process { StartInfo = new ProcessStartInfo(executable, "app-server --stdio") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            } };
            child = process;
            try
            {
                process.Start();
                process.ErrorDataReceived += delegate { };
                process.BeginErrorReadLine();
                var request = Exchange(process, method, parameters);
                if (await Task.WhenAny(request, Task.Delay(15000)) != request)
                {
                    Stop(process);
                    try { await request; } catch { }
                    throw new TimeoutException("Codex request timed out");
                }
                return await request;
            }
            finally { Stop(process); process.Dispose(); if (child == process) child = null; }
        }

        async Task<Dictionary<string, object>> Exchange(Process process, string method, object parameters)
        {
            Send(process, new { id = 1, method = "initialize", @params = new {
                clientInfo = new { name = "usage-widget", version = "0.4.1" }, capabilities = new { experimentalApi = true }
            } });
            while (true)
            {
                string line = await process.StandardOutput.ReadLineAsync();
                if (line == null) throw new IOException("Codex closed the connection");
                Dictionary<string, object> message;
                try { message = json.Deserialize<Dictionary<string, object>>(line); } catch (ArgumentException) { continue; }
                object responseId;
                int id;
                if (message == null || !message.TryGetValue("id", out responseId)
                    || !int.TryParse(Convert.ToString(responseId, CultureInfo.InvariantCulture), out id)
                    || (id != 1 && id != 2)) continue;
                if (message.ContainsKey("error")) {
                    var error = Map(message, "error");
                    object detail;
                    throw new InvalidOperationException(error != null && error.TryGetValue("message", out detail)
                        ? Convert.ToString(detail) : "Codex request failed");
                }
                if (id == 1)
                {
                    Send(process, new { method = "initialized" });
                    Send(process, new { id = 2, method, @params = parameters });
                }
                else if (id == 2)
                {
                    var result = Map(message, "result");
                    if (result == null) throw new InvalidOperationException("Codex returned no result");
                    return result;
                }
            }
        }

        static ResetCredits ParseResetCredits(Dictionary<string, object> summary)
        {
            var result = new ResetCredits();
            if (summary == null) return result;
            object value;
            long count;
            if (summary.TryGetValue("availableCount", out value) && value != null
                && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out count) && count >= 0)
                result.AvailableCount = count;
            if (!summary.TryGetValue("credits", out value)) return result;
            var credits = value as System.Collections.IEnumerable;
            if (credits == null) return result;
            foreach (var item in credits)
            {
                var credit = item as Dictionary<string, object>;
                if (credit == null || !credit.TryGetValue("status", out value) || Convert.ToString(value) != "available") continue;
                string type = credit.TryGetValue("resetType", out value) ? Convert.ToString(value) : "unknown";
                string title = credit.TryGetValue("title", out value) ? Convert.ToString(value) : null;
                object expiration;
                bool hasExpiration = credit.TryGetValue("expiresAt", out expiration);
                result.Credits.Add(new ResetCredit {
                    Id = credit.TryGetValue("id", out value) ? Convert.ToString(value) : null,
                    Title = !string.IsNullOrWhiteSpace(title) ? title : type == "codexRateLimits" ? "Full reset (Weekly + 5 hr)" : "Usage limit reset",
                    Description = credit.TryGetValue("description", out value) ? Convert.ToString(value) : null,
                    ExpiresAt = hasExpiration ? ParseTimestamp(expiration) : null,
                    NoExpiration = hasExpiration && expiration == null
                });
            }
            result.Credits.Sort(delegate(ResetCredit a, ResetCredit b) {
                return DateTime.Compare(a.ExpiresAt ?? DateTime.MaxValue, b.ExpiresAt ?? DateTime.MaxValue);
            });
            return result;
        }

        static DateTime? ParseTimestamp(object value)
        {
            double timestamp;
            if (value == null || !double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out timestamp)
                || double.IsNaN(timestamp) || double.IsInfinity(timestamp) || timestamp <= 0) return null;
            double milliseconds = timestamp < 1e12 ? timestamp * 1000 : timestamp;
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (milliseconds >= (DateTime.MaxValue - epoch).TotalMilliseconds) return null;
            return epoch.AddMilliseconds(milliseconds).ToLocalTime();
        }

        void Send(Process process, object message)
        {
            process.StandardInput.WriteLine(json.Serialize(message));
            process.StandardInput.Flush();
        }

        static Dictionary<string, object> Map(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        static Quota ParseQuota(Dictionary<string, object> map, bool weekly)
        {
            var quota = new Quota();
            if (map == null) return quota;
            object value;
            double used;
            if (map.TryGetValue("usedPercent", out value) && value != null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out used) && !double.IsNaN(used) && !double.IsInfinity(used))
                quota.Used = used;
            DateTime? timestamp = map.TryGetValue("resetsAt", out value) ? ParseTimestamp(value) : null;
            if (!timestamp.HasValue) return quota;
            DateTime reset = timestamp.Value;
            quota.Reset = weekly ? reset.ToString("ddd M/d HH:mm", CultureInfo.InvariantCulture)
                : reset.Date == DateTime.Today ? reset.ToString("HH:mm")
                : reset.Date == DateTime.Today.AddDays(1) ? "Tomorrow " + reset.ToString("HH:mm")
                : reset.ToString("M/d HH:mm");
            return quota;
        }

        static string FindExecutable()
        {
            var prefixes = new List<string> { System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") };
            prefixes.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            foreach (string entry in prefixes)
            {
                string prefix = entry.Trim().Trim('"');
                if (prefix.Length == 0) continue;
                string direct = System.IO.Path.Combine(prefix, "codex.exe");
                if (File.Exists(direct)) return direct;
                string root = System.IO.Path.Combine(prefix, "node_modules", "@openai", "codex");
                foreach (string vendor in new[] {
                    System.IO.Path.Combine(root, "node_modules", "@openai", "codex-win32-x64", "vendor"),
                    System.IO.Path.Combine(prefix, "node_modules", "@openai", "codex-win32-x64", "vendor"),
                    System.IO.Path.Combine(root, "vendor")
                })
                {
                    string binary = System.IO.Path.Combine(vendor, "x86_64-pc-windows-msvc", "bin", "codex.exe");
                    if (File.Exists(binary)) return binary;
                }
            }
            throw new FileNotFoundException("Codex CLI not found");
        }

        static void Stop(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Dispose() { if (child != null) Stop(child); }
    }

    static class Native
    {
        public const int ShowWidgetMessage = 0x8001;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr window, int index, int style);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
        public static int GetStyle(IntPtr window) { return GetWindowLong(window, -16); }
        public static void SetStyle(IntPtr window, int style)
        {
            SetWindowLong(window, -16, style);
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x37);
        }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct WINDOWPOS {
            public IntPtr Window, After;
            public int X, Y, Width, Height;
            public uint Flags;
        }
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        public static void Place(IntPtr window, int x, int y, int width, int height)
        {
            if (!SetWindowPos(window, IntPtr.Zero, x, y, width, height, 0x0014))
                throw new System.ComponentModel.Win32Exception();
        }
        public static void Move(IntPtr window, int x, int y)
        {
            if (!SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, 0x0015))
                throw new System.ComponentModel.Win32Exception();
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string id);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void ConfigureFrame(IntPtr window)
        {
            int enabled = 1, noRound = 1, noBorder = -2, noBackdrop = 1;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 20, ref enabled, 4));
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 33, ref noRound, 4));
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 34, ref noBorder, 4));
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 38, ref noBackdrop, 4));
        }

        public static void SetMinimizable(IntPtr window, bool enabled)
        {
            if (window == IntPtr.Zero) return;
            int style = GetWindowLong(window, -16);
            int next = (enabled ? style | 0x20000 : style & ~0x20000) & ~0x10000;
            if (next != style)
            {
                SetStyle(window, next);
                ConfigureFrame(window);
            }
        }
    }
}
