param([switch]$Capture)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WidgetVerify {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT bounds);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window, int index);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string className, string title);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, int message, IntPtr wp, IntPtr lp);
  public static IntPtr FindWidget() { return FindWindow(null, "UsageWidget"); }
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int length);
  [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int length);
}
'@
[void][WidgetVerify]::SetThreadDpiAwarenessContext([IntPtr](-4))
$exe = Join-Path $PSScriptRoot 'bin\UsageWidget.exe'
$testHelper = Join-Path (Split-Path $PSScriptRoot) 'tools\native-window.ps1'
if (Get-Process UsageWidget -ErrorAction SilentlyContinue) { throw 'Close the running widget before verification' }
$process = Start-Process -FilePath $exe -PassThru
try {
  $deadline = [DateTime]::Now.AddSeconds(20)
  do {
    Start-Sleep -Milliseconds 200
    $process.Refresh()
    if ($process.HasExited) { throw 'Native widget exited during startup' }
    $handle = [WidgetVerify]::FindWidget()
  } until ($handle -ne 0 -or [DateTime]::Now -gt $deadline)
  if ($handle -eq 0) { throw 'Native widget did not create a window' }
  $ui = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
  function Find-Button([string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $ui.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
  }
  function Click-Button([string]$name) {
    $button = Find-Button $name
    if ($null -eq $button) { throw "Button missing: $name" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
  }
  function Native-Test([string]$action, [string]$edge = 'e', [int]$dx = 0, [int]$dy = 0) {
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $testHelper -Handle $handle.ToInt64() -Action $action -Edge $edge -DeltaX $dx -DeltaY $dy
    if ($LASTEXITCODE -ne 0) { throw "Native test failed: $action" }
    return ($output -join "`n") | ConvertFrom-Json
  }
  function Get-Frame {
    $rect = New-Object WidgetVerify+RECT
    [void][WidgetVerify]::GetWindowRect($handle, [ref]$rect)
    return [PSCustomObject]@{
      X = $rect.Left; Y = $rect.Top; W = $rect.Right - $rect.Left; H = $rect.Bottom - $rect.Top
      CenterX = ($rect.Left + $rect.Right) / 2.0; CenterY = ($rect.Top + $rect.Bottom) / 2.0
    }
  }
  function Assert-Size($actual, $expected) {
    if ([Math]::Abs($actual.W - $expected.W) -gt 2 -or [Math]::Abs($actual.H - $expected.H) -gt 2) {
      throw "Size was not remembered: $($actual.W)x$($actual.H), expected $($expected.W)x$($expected.H)"
    }
  }
  function Assert-Center($actual, $expected) {
    if ([Math]::Abs($actual.CenterX - $expected.CenterX) -gt 2 -or [Math]::Abs($actual.CenterY - $expected.CenterY) -gt 2) {
      throw 'Switching jumped away from the current position'
    }
  }
  do {
    $elements = $ui.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $texts = @($elements | ForEach-Object { $_.Current.Name })
    if ($texts -match '^Updated \d{2}:\d{2}$') { break }
    Start-Sleep -Milliseconds 250
  } until ([DateTime]::Now -gt $deadline)
  if (-not ($texts -match '^Updated \d{2}:\d{2}$')) { throw ('Live quota did not update: ' + ($texts -join ' | ')) }
  if (@($texts -match '^\d+(\.\d+)?%$').Count -ne 2) { throw 'Both remaining percentages must be visible' }
  'PASS live Codex quotas and update timestamp'
  $rounded = 0; $material = 0; $border = -2
  if ([WidgetVerify]::DwmGetWindowAttribute($handle, 33, [ref]$rounded, 4) -ne 0 -or $rounded -ne 1) { throw 'DWM must not add a second rounded frame' }
  # Border color is settable but GetWindowAttribute returns E_INVALIDARG for it on this Windows build.
  if ([WidgetVerify]::DwmSetWindowAttribute($handle, 34, [ref]$border, 4) -ne 0) { throw 'DWM rejected disabling the frame border' }
  if ([WidgetVerify]::DwmGetWindowAttribute($handle, 38, [ref]$material, 4) -ne 0 -or $material -ne 1) { throw 'System backdrop must be disabled' }
  'PASS disabled system rounding, border and backdrop'

  if ($Capture) {
    $bounds = New-Object WidgetVerify+RECT
    [void][WidgetVerify]::GetWindowRect($handle, [ref]$bounds)
    $bitmap = New-Object System.Drawing.Bitmap(($bounds.Right - $bounds.Left), ($bounds.Bottom - $bounds.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
      $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
      $bitmap.Save((Join-Path $env:LOCALAPPDATA 'Temp\opencode\widget-native.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
  }

  $taskbar = Native-Test 'Taskbar'
  if ($taskbar.found) { throw 'Tray-only widget still has a taskbar button' }
  $tray = Native-Test 'Tray'
  if (-not $tray.found) { throw 'Notification icon missing' }
  'PASS notification icon and no taskbar button'
  Click-Button 'Check usage'
  $usageTitle = 'UsageWidget ' + [char]0x2014 + ' Usage & resets'
  $usageCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $usageTitle)
  $usage = $ui.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $usageCondition)
  if ($null -eq $usage) { $usage = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $usageCondition) }
  if ($null -eq $usage) { throw 'Usage button did not open details' }
  $editCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
  $edit = $usage.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
  $detailsDeadline = [DateTime]::Now.AddSeconds(20)
  do {
    Start-Sleep -Milliseconds 200
    $usageText = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
  } until ($usageText -notmatch '^Checking' -or [DateTime]::Now -gt $detailsDeadline)
  if ($usageText -notmatch 'Usage limit resets' -or $usageText -match '^Usage check failed') { throw 'Usage/reset API read failed' }
  $redeemCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Redeem selected reset')
  $redeem = $usage.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $redeemCondition)
  if ($null -eq $redeem -or $redeem.Current.IsEnabled) { throw 'Redemption must require selecting a credit' }
  $usage.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  'PASS live usage/reset availability popup (no credits consumed)'

  # NOTE: 'ne' and 'se' corners are skipped by design. The top-right corner holds the
  # pin/min/close buttons and the bottom-right corner holds Refresh, so a press there
  # hits a button instead of the resize grip (same tradeoff as any standard Windows
  # window). E/W/S/N edges and the nw/sw corners resize normally.
  foreach ($drag in @(@('e',60,0), @('w',-60,0), @('s',0,60), @('n',0,-60), @('nw',-60,-60), @('sw',-60,60))) {
    [void][WidgetVerify]::ShowWindow($handle, 9)
    [void][WidgetVerify]::SetForegroundWindow($handle)
    [void][WidgetVerify]::SetWindowPos($handle, [IntPtr]::Zero, 300, 300, 720, 312, 0x14)
    $frames = Native-Test 'Drag' $drag[0] $drag[1] $drag[2]
    $distinct = @($frames | ForEach-Object { '{0},{1},{2},{3}' -f $_.x,$_.y,$_.width,$_.height } | Sort-Object -Unique)
    if ($distinct.Count -lt 10) { throw ("Resize did not update continuously: $($drag[0]): " + ((Native-Test 'Inspect') | ConvertTo-Json -Compress) + ' frame=' + ($frames[0] | ConvertTo-Json -Compress) + ' expected=' + $handle.ToInt64()) }
    foreach ($frame in $frames) {
      if ($frame.target -ne $handle.ToInt64()) { throw 'Resize grabbed a different window' }
      if ([Math]::Abs($frame.height - $frame.width * 130 / 300.0) -gt 2) { throw "Resize broke aspect ratio: $($drag[0])" }
    }
    if ($frames[-1].width -eq 720 -or $frames[-1].height -eq 312) { throw 'Resize must change both dimensions' }
    "PASS native resize $($drag[0]): $($distinct.Count) intermediate sizes"
  }

  $move = $null
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    [void][WidgetVerify]::ShowWindow($handle, 9)
    [void][WidgetVerify]::SetWindowPos($handle, [IntPtr]::Zero, 300, 300, 720, 312, 0x14)
    $move = Native-Test 'Move' -dx 60 -dy 40
    if ($move.target -eq $handle.ToInt64() -and [Math]::Abs($move.endX - ($move.startX + 60)) -le 10 -and [Math]::Abs($move.endY - ($move.startY + 40)) -le 10 -and $move.endWidth -eq $move.startWidth -and $move.endHeight -eq $move.startHeight) { break }
    $move | Add-Member -NotePropertyName attempt -NotePropertyValue $attempt -Force
    Start-Sleep -Milliseconds 500
  }
  if ($move.target -ne $handle.ToInt64()) { throw ('Move grabbed the wrong window: ' + ($move | ConvertTo-Json -Compress)) }
  if ([Math]::Abs($move.endX - ($move.startX + 60)) -gt 10 -or [Math]::Abs($move.endY - ($move.startY + 40)) -gt 10) { throw ('Drag-anywhere move failed: ' + ($move | ConvertTo-Json -Compress)) }
  if ($move.endWidth -ne $move.startWidth -or $move.endHeight -ne $move.startHeight) { throw ('Move resized the window: ' + ($move | ConvertTo-Json -Compress)) }
  'PASS drag anywhere to move'
  Add-Type -AssemblyName System.Windows.Forms
  Add-Type -AssemblyName System.Drawing
  [void][WidgetVerify]::ShowWindow($handle, 9)
  [void][WidgetVerify]::SetWindowPos($handle, [IntPtr]::Zero, 300, 300, 720, 312, 0x14)
  $fling = Native-Test 'Move' -dx 5000 -dy 5000
  if (($fling.endX - $fling.startX) -lt 200 -or ($fling.endY - $fling.startY) -lt 200) { throw ('Fling did not move the widget: ' + ($fling | ConvertTo-Json -Compress)) }
  $corner = New-Object System.Drawing.Point(($fling.endX + 1), ($fling.endY + 1))
  $area = [System.Windows.Forms.Screen]::FromPoint($corner).WorkingArea
  if ($fling.endX -lt $area.Left -or $fling.endY -lt $area.Top -or ($fling.endX + $fling.endWidth) -gt $area.Right -or ($fling.endY + $fling.endHeight) -gt $area.Bottom) { throw ('Widget escaped the screen: ' + ($fling | ConvertTo-Json -Compress)) }
  'PASS widget stays on screen'

  [void][WidgetVerify]::ShowWindow($handle, 9)
  [void][WidgetVerify]::SetWindowPos($handle, [IntPtr]::Zero, 300, 300, 720, 312, 0x14)
  $savedFull = Get-Frame
  Click-Button 'Collapse'
  Start-Sleep -Milliseconds 500
  $smallRect = New-Object WidgetVerify+RECT
  [void][WidgetVerify]::GetWindowRect($handle, [ref]$smallRect)
  $smallW = $smallRect.Right - $smallRect.Left
  $smallH = $smallRect.Bottom - $smallRect.Top
  if ($smallW -ge $savedFull.W -or $smallH -ge $savedFull.H -or [Math]::Abs($smallW - $smallH) -gt 1) { throw "Collapse did not produce a smaller square: ${smallW}x${smallH}" }
  $savedSmall = Get-Frame
  Assert-Center $savedSmall $savedFull
  $smallTexts = @($ui.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
  if ($smallTexts -match '5-hour quota') { throw 'Full view still visible after collapse' }
  if (-not ($smallTexts -match '^\d+(\.\d+)?%$' -and $smallTexts -match '^Reset ')) { throw ('Small box missing number/reset: ' + ($smallTexts -join ' | ')) }
  'PASS collapse to small box'

  # Tap the TOP of the small box: with the old 36px caption this was a dead OS
  # title-drag zone, so expanding from here proves the caption is really gone.
  $clicked = Native-Test 'Click' -dx 40 -dy 20
  Start-Sleep -Milliseconds 500
  $bigRect = New-Object WidgetVerify+RECT
  [void][WidgetVerify]::GetWindowRect($handle, [ref]$bigRect)
  $bigW = $bigRect.Right - $bigRect.Left
  $bigH = $bigRect.Bottom - $bigRect.Top
  if ($bigW -lt ($smallW * 1.5) -or $bigH -lt ($smallH + 20)) { throw "Tap did not expand the small box: ${bigW}x${bigH} from ${smallW}x${smallH}" }
  Assert-Size (Get-Frame) $savedFull
  Assert-Center (Get-Frame) $savedFull
  $bigTexts = @()
  $deadline2 = [DateTime]::Now.AddSeconds(10)
  do {
    Start-Sleep -Milliseconds 250
    $bigTexts = @($ui.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
  } until (($bigTexts -match '5-hour quota') -or [DateTime]::Now -gt $deadline2)
  if (-not ($bigTexts -match '5-hour quota')) { throw ('Full view missing after expand: ' + ($bigTexts -join ' | ')) }
  'PASS tap small box to expand'

  Click-Button 'Collapse'
  Start-Sleep -Milliseconds 500
  Assert-Size (Get-Frame) $savedSmall
  $smallResize = Native-Test 'Drag' 'e' 40 0
  $resizedSmall = Get-Frame
  if ($resizedSmall.W -le $savedSmall.W -or [Math]::Abs($resizedSmall.W - $resizedSmall.H) -gt 1) { throw 'Compact resize must enlarge both dimensions equally' }
  $dragSmall = Native-Test 'Move' -dx 60 -dy 40
  $dragRect = New-Object WidgetVerify+RECT
  [void][WidgetVerify]::GetWindowRect($handle, [ref]$dragRect)
  $dragW = $dragRect.Right - $dragRect.Left
  $dragH = $dragRect.Bottom - $dragRect.Top
  if ([Math]::Abs($dragSmall.endX - ($dragSmall.startX + 60)) -gt 10 -or [Math]::Abs($dragSmall.endY - ($dragSmall.startY + 40)) -gt 10) { throw ('Small box did not move: ' + ($dragSmall | ConvertTo-Json -Compress)) }
  Assert-Size (Get-Frame) $resizedSmall
  $movedSmall = Get-Frame
  'PASS drag small box stays small'

  # Back to full view: everything after this assumes the expanded buttons exist.
  $clicked2 = Native-Test 'Click' -dx 40 -dy 20
  $backTexts = @()
  $deadline3 = [DateTime]::Now.AddSeconds(10)
  do {
    Start-Sleep -Milliseconds 250
    $backTexts = @($ui.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
  } until (($backTexts -match '5-hour quota') -or [DateTime]::Now -gt $deadline3)
  if (-not ($backTexts -match '5-hour quota')) { throw 'Did not return to full view' }
  Assert-Size (Get-Frame) $savedFull
  Assert-Center (Get-Frame) $movedSmall
  Click-Button 'Collapse'
  Assert-Size (Get-Frame) $resizedSmall
  Assert-Center (Get-Frame) $movedSmall
  [void](Native-Test 'Click' -dx 40 -dy 20)
  'PASS separate size memory and switching follows the moved position'

  $desktop = Native-Test 'Desktop'
  if ($desktop.controlUncovered -or -not $desktop.uncovered -or $desktop.minimized) { throw ('Pinned widget failed actual Win+D: ' + ($desktop | ConvertTo-Json -Compress)) }
  'PASS pinned Win+D with no taskbar button'
  Click-Button 'Always on top'
  if (([WidgetVerify]::GetWindowLong($handle, -20) -band 8) -ne 0) { throw 'Pin toggle did not turn off topmost' }
  Click-Button 'Always on top'
  if (([WidgetVerify]::GetWindowLong($handle, -20) -band 8) -eq 0) { throw 'Pin toggle did not turn on topmost' }
  'PASS pin toggling'

  $beforeMinimize = Get-Frame
  Click-Button 'Minimize'
  if ([WidgetVerify]::IsWindowVisible($handle)) { throw 'Minimize must hide to tray' }
  $restored = Native-Test 'TrayToggle'
  if (-not $restored.found -or -not [WidgetVerify]::IsWindowVisible($handle)) { throw 'Tray restore failed' }
  Assert-Size (Get-Frame) $beforeMinimize
  Assert-Center (Get-Frame) $beforeMinimize
  Click-Button 'Refresh'
  Start-Sleep -Seconds 2
  'PASS hide-to-tray, tray restore and manual refresh'
  Click-Button 'Close'
  if ([WidgetVerify]::IsWindowVisible($handle) -or $process.HasExited) { throw 'Close button must hide, not exit' }
  $second = Start-Process -FilePath $exe -PassThru
  try {
    if (-not $second.WaitForExit(5000)) { throw 'Second instance did not exit' }
    if ($process.HasExited) { throw 'First instance was closed' }
    Start-Sleep -Milliseconds 500
    if (-not [WidgetVerify]::IsWindowVisible($handle)) { throw 'Second instance did not restore hidden widget' }
  } finally { $second.Dispose() }
  'PASS single-instance activation'
} finally {
  if (-not $process.HasExited) {
    if ($handle -ne 0) { [void][WidgetVerify]::PostMessage($handle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
    if (-not $process.WaitForExit(3000)) { $process.Kill() }
  }
  $process.Dispose()
}
