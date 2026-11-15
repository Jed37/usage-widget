$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$temporary = Join-Path $env:LOCALAPPDATA 'Temp\opencode'
if (-not (Test-Path -LiteralPath $temporary)) { throw 'Test output directory is missing' }
$mock = Join-Path $temporary 'widget-mock-codex.exe'
& (Join-Path $framework 'csc.exe') /nologo /warn:4 /warnaserror+ /target:exe "/reference:$(Join-Path $framework 'System.Web.Extensions.dll')" "/out:$mock" (Join-Path $PSScriptRoot '..\test\MockCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'Could not compile protocol fixture' }
$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Drawing.dll',
  'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'WPF\WindowsBase.dll',
  'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll') | ForEach-Object { Join-Path $framework $_ }
Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

public static class WidgetFeatureVerify {
  const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
  static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
  static object Field(object target, string name) { return target.GetType().GetField(name, Private).GetValue(target); }
  static void Pump() {
    var frame = new DispatcherFrame();
    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
    Dispatcher.PushFrame(frame);
  }
  static void Wait(Task task) {
    var deadline = DateTime.UtcNow.AddSeconds(25);
    while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
    Check(task.IsCompleted, "Operation never completed");
    task.GetAwaiter().GetResult();
  }
  static Task Call(object target, string name, params object[] args) {
    return (Task)target.GetType().GetMethod(name, Private | BindingFlags.Public).Invoke(target, args);
  }
  static void Scenario(string value) { Environment.SetEnvironmentVariable("WIDGET_MOCK_SCENARIO", value); }
  static void SelectFirst(Window window) {
    var selection = (ComboBox)Field(window, "resetSelection");
    selection.SelectedIndex = 0;
    Check(((Button)Field(window, "redeem")).IsEnabled, "A fresh available fixture credit could not be selected");
  }
  public static string Run(string exe, string mock, string directory) {
    string log = Path.Combine(directory, "widget-mock-requests.jsonl");
    string state = Path.Combine(directory, "widget-mock-state.txt");
    File.WriteAllText(log, ""); File.WriteAllText(state, "");
    Environment.SetEnvironmentVariable("WIDGET_MOCK_LOG", log);
    Environment.SetEnvironmentVariable("WIDGET_MOCK_STATE", state);
    var assembly = Assembly.LoadFrom(exe);
    var window = (Window)Activator.CreateInstance(assembly.GetType("UsageWidget.WidgetWindow"), true);
    object reader = Field(window, "reader");
    reader.GetType().GetField("executable", Private).SetValue(reader, mock);
    try {
      Scenario("reset"); window.Show(); Pump(); Wait((Task)Field(window, "refreshTask"));
      Check(!window.ShowInTaskbar, "Main widget still has a taskbar entry");
      Check(((Forms.NotifyIcon)Field(window, "tray")).Visible, "Tray icon was not registered");
      Wait(Call(window, "ShowUsage"));
      var details = (TextBox)Field(window, "usageDetails");
      var popup = (Window)Field(window, "usageWindow");
      Check(!popup.ShowInTaskbar && popup.IsVisible, "Usage popup must be visible without a taskbar button");
      Check(details.Text.Contains("2 usage limit resets available.") && details.Text.Contains("36% remaining")
        && details.Text.Contains("Expires"), "Usage/count/expiry did not come from the fixture API response");
      Check(!((Button)Field(window, "redeem")).IsEnabled, "A reset must be explicitly selected");
      SelectFirst(window);
      Scenario("unexpectedOutcome");
      Task first = Call(window, "RedeemSelected");
      Wait(Call(window, "RedeemSelected")); Wait(first);
      Check(((TextBlock)Field(window, "redeemStatus")).Text.Contains("Could not confirm"), "Unknown outcome was shown as success");
      Scenario("alreadyRedeemed"); Wait(Call(window, "RedeemSelected"));
      Check(((TextBlock)Field(window, "redeemStatus")).Text.Contains("already redeemed"), "Already-redeemed outcome was not handled");
      Check(details.Text.Contains("1 usage limit resets available."), "Redemption did not refresh the account");
      Check(!((Button)Field(window, "redeem")).IsEnabled, "Next credit must not be auto-selected after redemption");
      var json = new JavaScriptSerializer();
      var consumes = new List<Dictionary<string, object>>();
      foreach (string line in File.ReadAllLines(log)) {
        var message = json.Deserialize<Dictionary<string, object>>(line);
        if (Convert.ToString(message["method"]) == "account/rateLimitResetCredit/consume")
          consumes.Add((Dictionary<string, object>)message["params"]);
      }
      Check(consumes.Count == 2, "Double-click sent a duplicate redemption");
      Check(Convert.ToString(consumes[0]["creditId"]) == "fixture-A"
        && Convert.ToString(consumes[0]["idempotencyKey"]) == Convert.ToString(consumes[1]["idempotencyKey"]),
        "Retry changed the selected credit or idempotency key");
      File.WriteAllText(state, ""); Scenario("nothingToReset"); Wait(Call(window, "ShowUsage")); SelectFirst(window);
      Wait(Call(window, "RedeemSelected"));
      Check(((TextBlock)Field(window, "redeemStatus")).Text.Contains("No current usage-limit"), "Nothing-to-reset outcome was not handled");
      Scenario("reset"); SelectFirst(window); Wait(Call(window, "RedeemSelected"));
      Check(((TextBlock)Field(window, "redeemStatus")).Text.StartsWith("Reset redeemed"), "Successful reset was not handled");
      Scenario("noCredit"); SelectFirst(window); Wait(Call(window, "RedeemSelected"));
      Check(details.Text.Contains("0 usage limit resets available."), "No-credit outcome did not refresh availability");
      Check(!((Button)Field(window, "redeem")).IsEnabled, "Zero availability still allowed redemption");
      var parseCredits = reader.GetType().GetMethod("ParseResetCredits", BindingFlags.Static | BindingFlags.NonPublic);
      var partial = json.Deserialize<Dictionary<string, object>>(@"{""availableCount"":7,""credits"":[{""id"":""partial"",""status"":""available"",""resetType"":""codexRateLimits"",""title"":""Server title"",""expiresAt"":null},{""id"":""used"",""status"":""redeemed""}]}");
      object parsed = parseCredits.Invoke(null, new object[] { partial });
      Check(Convert.ToInt64(parsed.GetType().GetField("AvailableCount").GetValue(parsed)) == 7,
        "Count was inferred from the partial credit list instead of the API");
      var rows = (System.Collections.IList)parsed.GetType().GetField("Credits").GetValue(parsed);
      Check(rows.Count == 1 && Convert.ToString(rows[0].GetType().GetField("Title").GetValue(rows[0])) == "Server title",
        "Server title/status filtering was not preserved");
      parsed = parseCredits.Invoke(null, new object[] { null });
      Check(parsed.GetType().GetField("AvailableCount").GetValue(parsed) == null,
        "Missing availability was incorrectly shown as zero");
      File.WriteAllText(state, ""); Scenario("reset"); Wait(Call(window, "ShowUsage")); SelectFirst(window);
      Scenario("rpcError"); Wait(Call(window, "ShowUsage"));
      Check(details.Text.Contains("Fixture API failure") && !((Button)Field(window, "redeem")).IsEnabled,
        "Failed availability check left stale redemption enabled");
      Scenario("lostReply");
      bool timedOut = false;
      try { Wait(Call(reader, "Redeem", "fixture-A", Guid.NewGuid().ToString())); }
      catch (TimeoutException) { timedOut = true; }
      Check(timedOut && Field(reader, "child") == null, "Timeout did not close the fixture app-server");
      window.GetType().GetMethod("HideWidget", Private).Invoke(window, null); Pump();
      Check(!window.IsVisible, "Hide-to-tray did not hide the window");
      Scenario("reset"); Wait(Call(window, "ShowUsage"));
      Check(!window.IsVisible && ((Window)Field(window, "usageWindow")).IsVisible, "Usage cannot be checked while widget is hidden");
      ((Forms.ToolStripMenuItem)Field(window, "visibilityMenu")).PerformClick(); Pump();
      Check(window.IsVisible, "Tray show action did not restore the widget");
      ((DispatcherTimer)Field(window, "timer")).Stop();
      return "PASS tray-only UI, API usage/expiry, all reset outcomes, selected credit, retry idempotency, double-click guard, timeout cleanup, stale-data guard";
    } finally {
      window.Close(); Pump();
      Environment.SetEnvironmentVariable("WIDGET_MOCK_LOG", null);
      Environment.SetEnvironmentVariable("WIDGET_MOCK_STATE", null);
      Environment.SetEnvironmentVariable("WIDGET_MOCK_SCENARIO", null);
    }
  }
}
'@
[WidgetFeatureVerify]::Run((Join-Path $PSScriptRoot 'bin\UsageWidget.exe'), $mock, $temporary)
