using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace GrokUsageWidget
{
    internal sealed class WidgetWindow : Window
    {
        private struct Frame
        {
            public int X;
            public int Y;
            public int W;
            public int H;
        }

        private const double FullW = 300.0;
        private const double FullH = 136.0;
        private const double Small = 74.0;
        private const double MinScale = 0.65;
        private const double MaxScale = 4.0;

        private readonly Grid content;
        private readonly GrokReader reader = new GrokReader();
        private readonly DispatcherTimer timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60.0)
        };

        private readonly TextBlock primary;
        private readonly TextBlock weekly;
        private readonly TextBlock primaryReset;
        private readonly TextBlock weeklyReset;
        private readonly TextBlock primaryNote;
        private readonly TextBlock weeklyNote;
        private readonly TextBlock status;
        private readonly TextBlock compactValue;
        private readonly TextBlock compactBot;
        private readonly TextBlock compactReset;
        private readonly Grid fullView;
        private readonly Grid compactView;
        private readonly Run plan;
        private readonly ProgressBar primaryBar;
        private readonly ProgressBar weeklyBar;
        private readonly Button refresh;
        private readonly Button usageCheck;

        private readonly WinForms.NotifyIcon tray;
        private readonly WinForms.ContextMenuStrip trayMenu;
        private readonly WinForms.ToolStripMenuItem visibilityMenu;
        private readonly WinForms.ToolStripMenuItem refreshMenu;
        private readonly WinForms.ToolStripMenuItem usageMenu;
        private readonly Drawing.Icon trayIcon;

        private Window usageWindow;
        private TextBox usageDetails;
        private GrokUsage lastGood;

        private Task refreshTask = Task.FromResult(0);
        private bool busy;
        private bool closing;
        private bool compact;
        private bool compactPressing;
        private System.Windows.Point compactPressScreen;
        private DateTime? updated;
        private System.Windows.Point moveAnchorScreen;
        private bool moving;
        private bool applying;

        private System.Windows.Size fullSize = new System.Windows.Size(FullW, FullH);
        private System.Windows.Size? compactSize;

        private readonly Border designRoot;
        private IntPtr handle;

        private double DesignW
        {
            get { return compact ? Small : FullW; }
        }

        private double DesignH
        {
            get { return compact ? Small : FullH; }
        }

        private double DeviceScale
        {
            get
            {
                if (handle == IntPtr.Zero)
                {
                    return 1.0;
                }
                return (double)Native.GetDpiForWindow(handle) / 96.0;
            }
        }

        public WidgetWindow()
        {
            Title = "GrokUsageWidget";
            Width = FullW;
            Height = FullH;
            SetLimits();
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0.0,
                ResizeBorderThickness = new Thickness(6.0),
                GlassFrameThickness = new Thickness(0.0),
                UseAeroCaptionButtons = false
            });

            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Widget.xaml"))
            {
                designRoot = (Border)XamlReader.Load(stream);
            }
            SetDesignSize();
            content = (Grid)designRoot.Child;
            Content = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = designRoot
            };

            content.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ChangedButton == MouseButton.Left && !InsideButton(e.OriginalSource as DependencyObject))
                {
                    moveAnchorScreen = PointToScreen(e.GetPosition(this));
                    compactPressing = false;
                    if (compact)
                    {
                        compactPressScreen = moveAnchorScreen;
                        compactPressing = true;
                    }
                    moving = content.CaptureMouse();
                    if (!moving)
                    {
                        compactPressing = false;
                    }
                }
            };
            content.PreviewMouseMove += delegate(object sender, System.Windows.Input.MouseEventArgs e)
            {
                if (moving && e.LeftButton == MouseButtonState.Pressed)
                {
                    System.Windows.Point point = PointToScreen(e.GetPosition(this));
                    Frame frame = ReadFrame();
                    frame.X += (int)Math.Round(point.X - moveAnchorScreen.X);
                    frame.Y += (int)Math.Round(point.Y - moveAnchorScreen.Y);
                    frame = ClampFrame(frame);
                    Native.Move(handle, frame.X, frame.Y);
                    moveAnchorScreen = point;
                }
            };
            content.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                bool expand = false;
                if (compactPressing)
                {
                    System.Windows.Point point = PointToScreen(e.GetPosition(this));
                    double dx = point.X - compactPressScreen.X;
                    double dy = point.Y - compactPressScreen.Y;
                    expand = dx * dx + dy * dy < 64.0;
                }
                moving = false;
                compactPressing = false;
                content.ReleaseMouseCapture();
                if (expand)
                {
                    SetCompact(false);
                }
            };
            content.LostMouseCapture += delegate
            {
                moving = false;
                compactPressing = false;
            };

            primary = Element<TextBlock>("PrimaryValue");
            weekly = Element<TextBlock>("WeeklyValue");
            primaryReset = Element<TextBlock>("PrimaryReset");
            weeklyReset = Element<TextBlock>("WeeklyReset");
            primaryNote = Element<TextBlock>("PrimaryNote");
            weeklyNote = Element<TextBlock>("WeeklyNote");
            primaryBar = Element<ProgressBar>("PrimaryBar");
            weeklyBar = Element<ProgressBar>("WeeklyBar");
            plan = Element<Run>("Plan");
            status = Element<TextBlock>("Status");
            refresh = Element<Button>("Refresh");
            usageCheck = Element<Button>("UsageCheck");
            fullView = Element<Grid>("FullView");
            compactView = Element<Grid>("CompactView");
            compactValue = Element<TextBlock>("CompactValue");
            compactBot = Element<TextBlock>("CompactBot");
            compactReset = Element<TextBlock>("CompactReset");

            Button pin = Element<Button>("Pin");
            pin.Click += delegate
            {
                Topmost = !Topmost;
                System.Windows.Shapes.Path pinShape = Element<System.Windows.Shapes.Path>("PinShape");
                pinShape.Stroke = new SolidColorBrush(Topmost
                    ? System.Windows.Media.Color.FromRgb(218, 226, 241)
                    : System.Windows.Media.Color.FromRgb(132, 143, 161));
                pinShape.Data = Geometry.Parse(Topmost
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
            Icon = Imaging.CreateBitmapSourceFromHIcon(
                trayIcon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            Icon.Freeze();

            visibilityMenu = new WinForms.ToolStripMenuItem("Hide widget", null, delegate { ToggleWidget(); });
            refreshMenu = new WinForms.ToolStripMenuItem("Refresh usage", null, async delegate { await Refresh(); });
            usageMenu = new WinForms.ToolStripMenuItem("Show usage details", null, async delegate { await ShowUsage(); });
            trayMenu = new WinForms.ContextMenuStrip();
            trayMenu.Items.AddRange(new WinForms.ToolStripItem[]
            {
                visibilityMenu,
                refreshMenu,
                usageMenu,
                new WinForms.ToolStripSeparator(),
                new WinForms.ToolStripMenuItem("Exit", null, delegate { Close(); })
            });
            trayMenu.Opening += delegate
            {
                visibilityMenu.Text = IsVisible ? "Hide widget" : "Show widget";
            };
            tray = new WinForms.NotifyIcon
            {
                Icon = trayIcon,
                Text = "Grok Usage",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            tray.MouseClick += delegate(object sender, WinForms.MouseEventArgs e)
            {
                if (e.Button == WinForms.MouseButtons.Left)
                {
                    ToggleWidget();
                }
            };

            timer.Tick += async delegate { await Refresh(); };
            Loaded += async delegate
            {
                PinPolicy();
                await Refresh();
            };
            StateChanged += delegate
            {
                if (WindowState != WindowState.Minimized)
                {
                    PinPolicy();
                }
            };
            Closed += delegate
            {
                closing = true;
                timer.Stop();
                reader.Dispose();
                if (usageWindow != null)
                {
                    usageWindow.Close();
                }
                tray.Visible = false;
                tray.Dispose();
                trayMenu.Dispose();
                trayIcon.Dispose();
            };
            SourceInitialized += delegate
            {
                handle = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(handle).AddHook(WindowMessage);
                StripBorder();
                Native.ConfigureFrame(handle);
            };
        }

        private static Drawing.Icon CreateTrayIcon()
        {
            try
            {
                Drawing.Icon extracted = Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
                if (extracted != null)
                {
                    return (Drawing.Icon)extracted.Clone();
                }
            }
            catch
            {
            }

            using (Drawing.Bitmap bitmap = new Drawing.Bitmap(32, 32))
            {
                using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.Clear(Drawing.Color.Transparent);
                    using (Drawing.SolidBrush background = new Drawing.SolidBrush(Drawing.Color.FromArgb(20, 22, 29)))
                    {
                        graphics.FillEllipse(background, 0, 0, 31, 31);
                    }
                    using (Drawing.Font font = new Drawing.Font("Segoe UI", 18, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel))
                    using (Drawing.StringFormat format = new Drawing.StringFormat())
                    {
                        format.Alignment = Drawing.StringAlignment.Center;
                        format.LineAlignment = Drawing.StringAlignment.Center;
                        graphics.DrawString("G", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 0, 32, 32), format);
                    }
                }
                IntPtr iconHandle = bitmap.GetHicon();
                try
                {
                    using (Drawing.Icon temporary = Drawing.Icon.FromHandle(iconHandle))
                    {
                        return (Drawing.Icon)temporary.Clone();
                    }
                }
                finally
                {
                    Native.DestroyIcon(iconHandle);
                }
            }
        }

        private void ShowWidget()
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
        }

        private void HideWidget()
        {
            if (usageWindow != null)
            {
                usageWindow.Hide();
            }
            Hide();
        }

        private void ToggleWidget()
        {
            if (IsVisible)
            {
                HideWidget();
            }
            else
            {
                ShowWidget();
            }
        }

        private async Task ShowUsage()
        {
            if (usageWindow == null)
            {
                usageDetails = new TextBox
                {
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(16.0),
                    FontFamily = FontFamily,
                    FontSize = 13.0,
                    BorderThickness = new Thickness(0.0),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 22, 29)),
                    Foreground = System.Windows.Media.Brushes.WhiteSmoke,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                Grid grid = new Grid();
                grid.Background = usageDetails.Background;
                grid.Children.Add(usageDetails);
                usageWindow = new Window
                {
                    Title = "Grok Usage \u2014 SuperGrok",
                    Owner = this,
                    ShowInTaskbar = false,
                    Icon = Icon,
                    Width = 520.0,
                    Height = 470.0,
                    MinWidth = 360.0,
                    MinHeight = 300.0,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Content = grid
                };
                usageWindow.Closed += delegate
                {
                    usageWindow = null;
                    usageDetails = null;
                };
            }

            usageDetails.Text = "Checking Grok usage...";
            usageWindow.Show();
            usageWindow.Activate();
            await Refresh();
        }

        private void SetBusyState(bool value)
        {
            refresh.IsEnabled = !value;
            usageCheck.IsEnabled = !value;
            refreshMenu.Enabled = !value;
            usageMenu.Enabled = !value;
        }

        private T Element<T>(string name) where T : DependencyObject
        {
            return (T)content.FindName(name);
        }

        private void SetDesignSize()
        {
            designRoot.Width = DesignW;
            designRoot.Height = DesignH;
            double radius = designRoot.CornerRadius.TopLeft;
            designRoot.Clip = new RectangleGeometry(new Rect(0.0, 0.0, DesignW, DesignH), radius, radius);
        }

        private void SetLimits()
        {
            MinWidth = DesignW * MinScale;
            MinHeight = DesignH * MinScale;
            MaxWidth = DesignW * MaxScale;
            MaxHeight = DesignH * MaxScale;
        }

        private Frame ReadFrame()
        {
            Native.RECT rect;
            if (!Native.GetWindowRect(handle, out rect))
            {
                throw new System.ComponentModel.Win32Exception();
            }
            Frame result = default(Frame);
            result.X = rect.Left;
            result.Y = rect.Top;
            result.W = rect.Right - rect.Left;
            result.H = rect.Bottom - rect.Top;
            return result;
        }

        private void Place(Frame frame)
        {
            frame = ClampFrame(frame);
            applying = true;
            try
            {
                Width = (double)frame.W / DeviceScale;
                Height = (double)frame.H / DeviceScale;
                UpdateLayout();
                Native.Place(handle, frame.X, frame.Y, frame.W, frame.H);
            }
            finally
            {
                applying = false;
            }
        }

        private Frame ClampFrame(Frame frame)
        {
            System.Drawing.Rectangle workingArea = WinForms.Screen.FromRectangle(
                new System.Drawing.Rectangle(frame.X, frame.Y, Math.Max(1, frame.W), Math.Max(1, frame.H))).WorkingArea;
            if (frame.W < workingArea.Width)
            {
                frame.X = Math.Max(workingArea.Left, Math.Min(frame.X, workingArea.Right - frame.W));
            }
            if (frame.H < workingArea.Height)
            {
                frame.Y = Math.Max(workingArea.Top, Math.Min(frame.Y, workingArea.Bottom - frame.H));
            }
            return frame;
        }

        private void SetCompact(bool on)
        {
            if (on == compact)
            {
                return;
            }

            Frame frame = ReadFrame();
            double deviceScale = DeviceScale;
            System.Windows.Size current = new System.Windows.Size(
                (double)frame.W / deviceScale,
                (double)frame.H / deviceScale);
            if (compact)
            {
                compactSize = current;
            }
            else
            {
                fullSize = current;
            }
            if (!compactSize.HasValue)
            {
                double scale = current.Width / FullW;
                compactSize = new System.Windows.Size(Small * scale, Small * scale);
            }

            compact = on;
            moving = false;
            compactPressing = false;
            content.ReleaseMouseCapture();
            fullView.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            compactView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            SetDesignSize();
            SetLimits();

            System.Windows.Size size = on ? compactSize.Value : fullSize;
            Frame target = default(Frame);
            target.W = (int)Math.Round(size.Width * deviceScale);
            target.H = (int)Math.Round(size.Height * deviceScale);
            target.X = frame.X + (frame.W - target.W) / 2;
            target.Y = frame.Y + (frame.H - target.H) / 2;
            Place(target);
        }

        private void LockAspect(ref Native.RECT rect, int edge)
        {
            double aspect = DesignW / DesignH;
            int minWidth = Math.Max(1, (int)Math.Round(DesignW * MinScale * DeviceScale));
            int maxWidth = Math.Max(minWidth, (int)Math.Round(DesignW * MaxScale * DeviceScale));
            bool left = edge == 1 || edge == 4 || edge == 7;
            bool right = edge == 2 || edge == 5 || edge == 8;
            bool top = edge == 3 || edge == 4 || edge == 5;
            bool bottom = edge == 6 || edge == 7 || edge == 8;

            int width = Math.Max(1, rect.Right - rect.Left);
            int height = Math.Max(1, rect.Bottom - rect.Top);
            if ((top || bottom) && !left && !right)
            {
                width = (int)Math.Round((double)height * aspect);
            }
            if (width < minWidth)
            {
                width = minWidth;
            }
            if (width > maxWidth)
            {
                width = maxWidth;
            }
            height = Math.Max(1, (int)Math.Round((double)width / aspect));

            int x = (left && !right) ? (rect.Right - width) : rect.Left;
            int y = (top && !bottom) ? (rect.Bottom - height) : rect.Top;
            if (!left && !right)
            {
                x = (rect.Left + rect.Right - width) / 2;
            }
            if (!top && !bottom)
            {
                y = (rect.Top + rect.Bottom - height) / 2;
            }
            rect.Left = x;
            rect.Top = y;
            rect.Right = x + width;
            rect.Bottom = y + height;
        }

        private static bool InsideButton(DependencyObject source)
        {
            while (source != null)
            {
                if (source is Button)
                {
                    return true;
                }
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private void PinPolicy()
        {
            if (handle != IntPtr.Zero)
            {
                Native.SetMinimizable(handle, !Topmost);
            }
        }

        private void StripBorder()
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }
            int style = Native.GetStyle(handle);
            int updated = style & -12582913;
            if (updated != style)
            {
                Native.SetStyle(handle, updated);
            }
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == Native.ShowWidgetMessage)
            {
                Dispatcher.BeginInvoke(new Action(ShowWidget));
                handled = true;
                return IntPtr.Zero;
            }
            if (message == 532 && !applying)
            {
                Native.RECT rect = (Native.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(
                    lParam,
                    typeof(Native.RECT));
                LockAspect(ref rect, wParam.ToInt32());
                System.Runtime.InteropServices.Marshal.StructureToPtr(rect, lParam, false);
                handled = true;
                return (IntPtr)1;
            }
            if (message == 70 && !applying && WindowState == WindowState.Normal)
            {
                Native.WINDOWPOS position = (Native.WINDOWPOS)System.Runtime.InteropServices.Marshal.PtrToStructure(
                    lParam,
                    typeof(Native.WINDOWPOS));
                if ((position.Flags & 1) == 0 && position.Width > 0 && position.Height > 0)
                {
                    Native.RECT rect = default(Native.RECT);
                    rect.Right = position.Width;
                    rect.Bottom = position.Height;
                    LockAspect(ref rect, 2);
                    position.Width = rect.Right - rect.Left;
                    position.Height = rect.Bottom - rect.Top;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(position, lParam, false);
                }
            }
            long command = wParam.ToInt64() & 0xFFF0;
            if (message == 274 && (command == 61488 || (command == 61472 && Topmost)))
            {
                handled = true;
            }
            return IntPtr.Zero;
        }

        private Task Refresh()
        {
            if (closing)
            {
                return Task.FromResult(0);
            }
            if (!refreshTask.IsCompleted)
            {
                return refreshTask;
            }
            refreshTask = UpdateUsage();
            return refreshTask;
        }

        private async Task UpdateUsage()
        {
            if (busy || closing)
            {
                return;
            }
            busy = true;
            timer.Stop();
            SetBusyState(true);
            try
            {
                GrokUsage data = await reader.Fetch();
                if (closing)
                {
                    return;
                }
                if (!data.HasBuild && !data.HasBot)
                {
                    string error = CombineErrors(data);
                    status.Text = updated.HasValue
                        ? "Update failed \u00b7 " + updated.Value.ToString("HH:mm")
                        : "Update failed";
                    status.ToolTip = error;
                    status.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 166, 166));
                    tray.Text = "Grok Usage \u2014 update failed";
                    if (usageDetails != null)
                    {
                        usageDetails.Text = "Usage check failed: " + error
                            + (lastGood == null ? "" : "\n\nLast successful result (not current):\n" + DescribeUsage(lastGood));
                    }
                    return;
                }

                lastGood = data;
                Render(data);
            }
            catch (Exception ex)
            {
                if (!closing)
                {
                    status.Text = updated.HasValue
                        ? "Update failed \u00b7 " + updated.Value.ToString("HH:mm")
                        : "Update failed";
                    status.ToolTip = ex.Message;
                    status.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 166, 166));
                    tray.Text = "Grok Usage \u2014 update failed";
                    if (usageDetails != null)
                    {
                        usageDetails.Text = "Usage check failed: " + ex.Message
                            + (lastGood == null ? "" : "\n\nLast successful result (not current):\n" + DescribeUsage(lastGood));
                    }
                }
            }
            finally
            {
                busy = false;
                if (!closing)
                {
                    SetBusyState(false);
                    timer.Start();
                }
            }
        }

        private void Render(GrokUsage data)
        {
            plan.Text = string.IsNullOrEmpty(data.Plan) ? "SuperGrok" : data.Plan;

            if (data.HasBuild)
            {
                primary.Text = Percent(data.TotalPercent);
                primaryBar.Value = data.TotalPercent;
                primaryBar.Foreground = BrushFor(data.TotalPercent);
                primaryReset.Text = "Reset " + FormatReset(data.PeriodEnd);
                primaryNote.Text = data.HasChat
                    ? "pool \u00b7 Build " + Percent(data.BuildPercent) + " + Chat " + Percent(data.ChatPercent)
                    : "weekly pool";
            }
            else
            {
                primary.Text = "--";
                primaryBar.Value = 0.0;
                primaryBar.Foreground = BrushFor(0.0);
                primaryReset.Text = "unavailable";
                primaryNote.Text = ShortError(data.Error);
            }

            if (data.HasBot)
            {
                weekly.Text = Percent(data.BotPercent);
                weeklyBar.Value = data.BotPercent;
                weeklyBar.Foreground = BrushFor(data.BotPercent);
                weeklyReset.Text = "Reset " + FormatReset(data.BotPeriodEnd);
                weeklyNote.Text = string.IsNullOrEmpty(data.BotPlan)
                    ? "Cursor meter"
                    : data.BotPlan + " \u00b7 Cursor";
            }
            else
            {
                weekly.Text = "--";
                weeklyBar.Value = 0.0;
                weeklyBar.Foreground = BrushFor(0.0);
                weeklyReset.Text = "unavailable";
                weeklyNote.Text = ShortError(data.BotError);
            }

            compactValue.Text = data.HasBuild ? "Build " + Percent(data.TotalPercent) : "Build --";
            compactBot.Text = data.HasBot ? "Bot " + Percent(data.BotPercent) : "Bot --";
            compactReset.Text = (data.HasBuild ? FormatResetShort(data.PeriodEnd) : "--")
                + " \u00b7 " + (data.HasBot ? FormatResetShort(data.BotPeriodEnd) : "--");

            updated = DateTime.Now;
            string time = updated.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (data.HasBuild && data.HasBot)
            {
                status.Text = "Updated " + time;
                status.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 167, 184));
            }
            else if (data.HasBuild)
            {
                status.Text = "Bot unavailable \u00b7 " + time;
                status.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 166, 166));
            }
            else
            {
                status.Text = "Build unavailable \u00b7 " + time;
                status.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 166, 166));
            }

            StringBuilder tip = new StringBuilder();
            tip.Append("Grok Build pool ");
            tip.Append(data.HasBuild ? Percent(data.TotalPercent) : "unavailable");
            if (data.HasBuild)
            {
                tip.Append(" (Build " + Percent(data.BuildPercent));
                if (data.HasChat)
                {
                    tip.Append(" + Chat " + Percent(data.ChatPercent));
                }
                tip.Append(")");
            }
            tip.Append(" \u00b7 resets " + FormatReset(data.PeriodEnd));
            tip.Append(" | Grok Bot ");
            tip.Append(data.HasBot ? Percent(data.BotPercent) : "unavailable");
            tip.Append(" \u00b7 resets " + FormatReset(data.BotPeriodEnd));
            if (!data.HasBuild && !string.IsNullOrEmpty(data.Error))
            {
                tip.Append("\nBuild error: " + data.Error);
            }
            if (!data.HasBot && !string.IsNullOrEmpty(data.BotError))
            {
                tip.Append("\nBot error: " + data.BotError);
            }
            status.ToolTip = tip.ToString();

            string trayText = "Grok Usage | Build " + (data.HasBuild ? Percent(data.TotalPercent) : "--")
                + " | Bot " + (data.HasBot ? Percent(data.BotPercent) : "--");
            tray.Text = trayText.Length <= 63 ? trayText : trayText.Substring(0, 63);

            if (usageDetails != null)
            {
                usageDetails.Text = DescribeUsage(data);
            }
        }

        private static string DescribeUsage(GrokUsage data)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Grok \u2014 " + data.Plan);
            text.AppendLine("Two separate usage meters");
            text.AppendLine();

            text.AppendLine("GROK BUILD \u2014 shared weekly pool");
            if (data.HasBuild)
            {
                text.AppendLine("  Used:        " + Percent(data.TotalPercent));
                text.AppendLine("  Build share: " + Percent(data.BuildPercent));
                if (data.HasChat)
                {
                    text.AppendLine("  Chat share:  " + Percent(data.ChatPercent));
                }
                AppendPeriod(text, data.PeriodStart, data.PeriodEnd);
                text.AppendLine("  Resets:      " + FormatResetLong(data.PeriodEnd));
            }
            else
            {
                text.AppendLine("  Unavailable: " + (data.Error ?? "unknown error"));
            }
            text.AppendLine("  Source:      cli-chat-proxy.grok.com/v1/billing?format=credits");
            text.AppendLine();

            text.AppendLine("GROK BOT \u2014 separate Cursor weekly meter");
            if (data.HasBot)
            {
                text.AppendLine("  Used:        " + Percent(data.BotPercent));
                if (!string.IsNullOrEmpty(data.BotPlan))
                {
                    text.AppendLine("  Plan:        " + data.BotPlan);
                }
                AppendPeriod(text, data.BotPeriodStart, data.BotPeriodEnd);
                text.AppendLine("  Resets:      " + FormatResetLong(data.BotPeriodEnd));
            }
            else
            {
                text.AppendLine("  Unavailable: " + (data.BotError ?? "unknown error"));
            }
            text.AppendLine("  Source:      api2.cursor.sh (DashboardService/GetSandUsageStatus)");
            text.AppendLine();

            text.AppendLine("On-demand:    " + Money(data.OnDemandUsed) + " used of " + Money(data.OnDemandCap) + " cap");
            text.AppendLine("Prepaid:      " + Money(data.PrepaidBalance));
            text.AppendLine("Unified:      " + (data.Unified ? "yes" : "no"));
            if (!string.IsNullOrEmpty(data.Warning))
            {
                text.AppendLine();
                text.AppendLine("Note: " + data.Warning);
            }
            text.AppendLine();
            text.AppendLine("Updated: " + data.UpdatedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static void AppendPeriod(StringBuilder text, DateTimeOffset? start, DateTimeOffset? end)
        {
            if (!start.HasValue && !end.HasValue)
            {
                return;
            }
            string startText = start.HasValue
                ? start.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "--";
            string endText = end.HasValue
                ? end.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "--";
            text.AppendLine("  Period:      " + startText + "  \u2192  " + endText);
        }

        private static string ShortError(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return "unavailable";
            }
            if (error.IndexOf("not signed in", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "not signed in";
            }
            if (error.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "sign-in expired";
            }
            return "unavailable";
        }

        private static string CombineErrors(GrokUsage data)
        {
            StringBuilder text = new StringBuilder();
            if (!string.IsNullOrEmpty(data.Error))
            {
                text.AppendLine("Grok Build: " + data.Error);
            }
            if (!string.IsNullOrEmpty(data.BotError))
            {
                text.AppendLine("Grok Bot: " + data.BotError);
            }
            return text.Length == 0 ? "Unknown error." : text.ToString().TrimEnd();
        }

        private static string Percent(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        private static string Money(double value)
        {
            return "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string FormatReset(DateTimeOffset? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            DateTime local = value.Value.LocalDateTime;
            if (local.Date == DateTime.Today)
            {
                return local.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            if (local.Date == DateTime.Today.AddDays(1.0))
            {
                return "Tomorrow " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            return local.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        }

        private static string FormatResetShort(DateTimeOffset? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            DateTime local = value.Value.LocalDateTime;
            if (local.Date == DateTime.Today)
            {
                return local.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            return local.ToString("M/d", CultureInfo.InvariantCulture);
        }

        private static string FormatResetLong(DateTimeOffset? value)
        {
            if (!value.HasValue)
            {
                return "--";
            }
            return value.Value.LocalDateTime.ToString("dddd, MMMM d, HH:mm", CultureInfo.InvariantCulture);
        }

        private static Brush BrushFor(double usedPercent)
        {
            System.Windows.Media.Color color;
            if (usedPercent <= 70.0)
            {
                color = System.Windows.Media.Color.FromRgb(148, 167, 202);
            }
            else if (usedPercent <= 90.0)
            {
                color = System.Windows.Media.Color.FromRgb(216, 162, 61);
            }
            else
            {
                color = System.Windows.Media.Color.FromRgb(217, 83, 79);
            }
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
