param([switch]$Capture)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll',
  'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll') |
  ForEach-Object { Join-Path $framework $_ }
Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public static class WidgetLayoutVerify {
  const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
  static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
  static void Pump() {
    var frame = new DispatcherFrame();
    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
      new Action(delegate { frame.Continue = false; }));
    Dispatcher.PushFrame(frame);
  }
  static object Field(Window window, string name) {
    return window.GetType().GetField(name, Instance).GetValue(window);
  }
  static Rect CheckText(TextBlock text, FrameworkElement host) {
    var formatted = new FormattedText(text.Text, CultureInfo.InvariantCulture, text.FlowDirection,
      new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
      text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
    Rect ink = formatted.BuildGeometry(new Point()).Bounds;
    Rect bounds = text.TransformToAncestor(host).TransformBounds(ink);
    Check(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= host.ActualWidth + 0.5
      && bounds.Bottom <= host.ActualHeight + 0.5, "Text clipped: " + text.Text);
    return bounds;
  }
  static void Save(RenderTargetBitmap bitmap, string path) {
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using (var file = File.Create(path)) encoder.Save(file);
  }
  public static string Run(string exe, string captureDirectory) {
    Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Run this script with -Sta");
    var assembly = Assembly.LoadFrom(exe);
    var window = (Window)Activator.CreateInstance(assembly.GetType("UsageWidget.WidgetWindow"), true);
    try {
      window.Show();
      var status = (TextBlock)Field(window, "status");
      var deadline = DateTime.UtcNow.AddSeconds(20);
      while (status.Text == "Updating..." && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(50); }
      ((DispatcherTimer)Field(window, "timer")).Stop();
      var host = (FrameworkElement)window.Content;
      var usageButton = (Button)Field(window, "usageCheck");
      var refreshButton = (Button)Field(window, "refresh");
      var value = (TextBlock)Field(window, "compactValue");
      var reset = (TextBlock)Field(window, "compactReset");
      var switchMode = window.GetType().GetMethod("SetCompact", Instance);
      int layouts = 0;
      foreach (bool small in new[] { false, true }) {
        switchMode.Invoke(window, new object[] { small });
        double normalizedWidth = 0;
        foreach (double scale in new[] { 0.65, 1.0, 2.0, 3.0, 4.0 }) {
          window.Width = (small ? 74 : 300) * scale;
          window.Height = (small ? 74 : 130) * scale;
          Pump(); window.UpdateLayout();
          foreach (string sample in new[] { "--", "0%", "100%", "99.9%" }) {
            foreach (string time in new[] { "Reset --", "Reset 15:32", "Reset Tomorrow 23:59" }) {
              value.Text = sample; reset.Text = time;
              foreach (string name in new[] { "primary", "weekly" }) ((TextBlock)Field(window, name)).Text = sample;
              ((TextBlock)Field(window, "primaryReset")).Text = time;
              ((TextBlock)Field(window, "weeklyReset")).Text = "Reset Wed 12/31 23:59";
              window.UpdateLayout();
              if (small) {
                Rect ink = CheckText(value, host); CheckText(reset, host);
                if (sample == "100%" && time == "Reset 15:32") {
                  double relativeWidth = ink.Width / host.ActualWidth;
                  Check(relativeWidth >= 0.5 && relativeWidth <= 0.85, "Compact percentage is too small or edge-to-edge");
                  if (normalizedWidth == 0) normalizedWidth = relativeWidth;
                  else Check(Math.Abs(relativeWidth - normalizedWidth) < 0.02, "Font did not scale with the frame");
                }
              }
              else foreach (string name in new[] { "primary", "weekly", "primaryReset", "weeklyReset", "status" })
                CheckText((TextBlock)Field(window, name), host);
              if (!small) {
                Rect usageBounds = usageButton.TransformToAncestor(host).TransformBounds(new Rect(usageButton.RenderSize));
                Rect refreshBounds = refreshButton.TransformToAncestor(host).TransformBounds(new Rect(refreshButton.RenderSize));
                Check(usageBounds.Right <= refreshBounds.Left && refreshBounds.Right <= host.ActualWidth,
                  "Usage button must fit to the left of Refresh");
              }
              layouts++;
            }
          }
          if (captureDirectory != null && scale == 2) {
            value.Text = "100%"; reset.Text = "Reset 15:32";
            window.UpdateLayout();
          }
          // Render the production visual tree, including its real clipping geometry.
          var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth),
            (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
          bitmap.Render(host);
          int stride = bitmap.PixelWidth * 4;
          var pixels = new byte[stride * bitmap.PixelHeight];
          bitmap.CopyPixels(pixels, stride, 0);
          Check(pixels[3] == 0, "Corner is not transparent");
          int radius = Math.Max(2, (int)Math.Round(14 * scale));
          int previous = 0;
          for (int i = 0; i < radius; i++) {
            int alpha = pixels[i * stride + i * 4 + 3];
            Check(alpha + 2 >= previous, "Corner has a second transparency edge");
            previous = alpha;
          }
          Check(previous >= 220 && previous <= 232, "Corner tint did not reach the single surface");
          if (captureDirectory != null && scale == 2) Save(bitmap,
            Path.Combine(captureDirectory, small ? "verified-compact.png" : "verified-full.png"));
        }
      }
      // A quota reset must survive invalid or out-of-range server data.
      var parse = assembly.GetType("UsageWidget.CodexReader").GetMethod("ParseQuota", BindingFlags.NonPublic | BindingFlags.Static);
      foreach (double timestamp in new[] { double.NaN, double.PositiveInfinity, -1.0, 1e30 }) {
        var map = new Dictionary<string, object> { { "usedPercent", double.NaN }, { "resetsAt", timestamp } };
        object quota = parse.Invoke(null, new object[] { map, false });
        Check(quota.GetType().GetField("Used").GetValue(quota) == null, "Invalid usage was accepted");
        Check((string)quota.GetType().GetField("Reset").GetValue(quota) == "--", "Invalid reset was accepted");
      }
      foreach (bool milliseconds in new[] { false, true }) {
        double seconds = (DateTime.Today.AddHours(12).ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        object quota = parse.Invoke(null, new object[] {
          new Dictionary<string, object> { { "usedPercent", 10.5 }, { "resetsAt", milliseconds ? seconds * 1000 : seconds } }, false });
        Check((string)quota.GetType().GetField("Reset").GetValue(quota) == "12:00", "Seconds/milliseconds reset conversion failed");
      }
      var quotaType = assembly.GetType("UsageWidget.Quota");
      var render = window.GetType().GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Static);
      var percent = (TextBlock)Field(window, "primary");
      var bar = (ProgressBar)Field(window, "primaryBar");
      var resetText = (TextBlock)Field(window, "primaryReset");
      foreach (double used in new[] { -10.0, 0.0, 100.0, 110.0 }) {
        var quota = Activator.CreateInstance(quotaType, true);
        quotaType.GetField("Used").SetValue(quota, used);
        render.Invoke(null, new object[] { percent, bar, resetText, quota });
        double expected = Math.Max(0, Math.Min(100, 100 - used));
        Check(bar.Value == expected && percent.Text == expected.ToString("0.#", CultureInfo.InvariantCulture) + "%", "Remaining percentage was not clamped correctly");
      }
      render.Invoke(null, new object[] { percent, bar, resetText, Activator.CreateInstance(quotaType, true) });
      Check(percent.Text == "--", "Missing quota was shown as exhausted");
      return "PASS " + layouts + " layouts: percentage/reset text fits, font scaling, single transparent corner, quota edge cases";
    } finally { window.Close(); Pump(); }
  }
}
'@
$captureDirectory = $null
if ($Capture) {
  $captureDirectory = Join-Path $env:LOCALAPPDATA 'Temp\opencode'
  if (-not (Test-Path -LiteralPath $captureDirectory)) { throw 'Capture directory is missing' }
}
[WidgetLayoutVerify]::Run((Join-Path $PSScriptRoot 'bin\UsageWidget.exe'), $captureDirectory)
