param(
  [long]$Handle,
  [ValidateSet('Inspect', 'Drag', 'Move', 'Click', 'Desktop', 'Taskbar', 'Tray', 'TrayToggle')][string]$Action = 'Inspect',
  [string]$Edge = 'e',
  [int]$DeltaX = 60,
  [int]$DeltaY = 40
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

# Exercise the real Windows HWND/input path, not a JS setSize simulation.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WidgetNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBOARD { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
  [StructLayout(LayoutKind.Explicit, Size=40)] public struct INPUT { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KEYBOARD Keyboard; }
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out UIntPtr result);
  [DllImport("user32.dll", EntryPoint="GetWindowLong")] public static extern int GetWindowLong(IntPtr hWnd, int index);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
  [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
  public static void ToggleDesktop() {
    var inputs = new INPUT[4];
    for (int i = 0; i < 4; i++) { inputs[i].Type = 1; inputs[i].Keyboard.Key = (ushort)(i == 0 || i == 3 ? 0x5b : 0x44); inputs[i].Keyboard.Flags = i >= 2 ? 2u : 0u; }
    if (SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT))) != 4) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  }
  public static long BeginDrag(int x, int y, out POINT point) {
    SetCursorPos(x, y);
    GetCursorPos(out point);
    long target = GetAncestor(WindowFromPoint(point), 2).ToInt64();
    mouse_event(2, 0, 0, 0, UIntPtr.Zero);
    return target;
  }
}
'@

[void][WidgetNative]::SetProcessDPIAware()
[void][WidgetNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
$hwnd = [IntPtr]$Handle
function Wait-ForIdleCursor {
  # The human may be using the machine while tests drive the real cursor.
  # Wait until it sits still before pressing, otherwise we click their window.
  for ($w = 0; $w -lt 60; $w++) {
    $before = New-Object WidgetNative+POINT
    [void][WidgetNative]::GetCursorPos([ref]$before)
    Start-Sleep -Milliseconds 100
    $after = New-Object WidgetNative+POINT
    [void][WidgetNative]::GetCursorPos([ref]$after)
    if ($before.X -eq $after.X -and $before.Y -eq $after.Y) { return }
  }
  throw 'Cursor never idle; user is moving the mouse'
}
function Click-Rectangle([double]$x, [double]$y, [double]$width, [double]$height) {
  $saved = New-Object WidgetNative+POINT
  [void][WidgetNative]::GetCursorPos([ref]$saved)
  Wait-ForIdleCursor
  try {
    [void][WidgetNative]::SetCursorPos([int]($x + $width / 2), [int]($y + $height / 2))
    Start-Sleep -Milliseconds 100
    [WidgetNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [WidgetNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1000
  } finally {
    [void][WidgetNative]::SetCursorPos($saved.X, $saved.Y)
  }
}
if ($Action -eq 'Tray' -or $Action -eq 'TrayToggle') {
  Add-Type -AssemblyName UIAutomationClient
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $taskbarCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')
  $taskbar = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $taskbarCondition)
  $buttons = $taskbar.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $entry = @($buttons | Where-Object { $_.Current.Name -like 'UsageWidget*' -and $_.Current.ClassName -eq 'SystemTray.NormalButton' })[0]
  $overflow = $false
  $arrow = $null
  if ($null -eq $entry) {
    $hiddenIcons = 'hidden|overflow|' + [char]0x96b1 + [char]0x85cf
    $arrow = @($buttons | Where-Object { $_.Current.ClassName -eq 'SystemTray.NormalButton' -and $_.Current.Name -match $hiddenIcons })[0]
    if ($null -eq $arrow) { throw 'Tray overflow button not found' }
    $arrow.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'TopLevelWindowForOverflowXamlIsland')
    $popup = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    if ($null -eq $popup) { throw 'Tray overflow popup not found' }
    $entry = @($popup.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
      Where-Object { $_.Current.Name -like 'UsageWidget*' })[0]
    $overflow = $true
  }
  $name = if ($null -eq $entry) { '' } else { $entry.Current.Name }
  if ($Action -eq 'TrayToggle' -and $null -ne $entry) {
    $bounds = $entry.Current.BoundingRectangle
    Click-Rectangle $bounds.Left $bounds.Top $bounds.Width $bounds.Height
  } elseif ($null -ne $arrow) {
    $arrow.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  }
  @{ found = $null -ne $entry; name = $name; overflow = $overflow } | ConvertTo-Json -Compress
  exit
}
if ($Action -eq 'Taskbar') {
  Add-Type -AssemblyName UIAutomationClient
  Add-Type -AssemblyName UIAutomationTypes
  $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')
  $taskbar = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
  if ($null -eq $taskbar) { throw 'Windows taskbar not found' }
  $buttons = $taskbar.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $entry = $null
  $names = @()
  foreach ($button in $buttons) {
    if ($button.Current.ClassName -ne 'Taskbar.TaskListButtonAutomationPeer') { continue }
    $names += $button.Current.Name
    if ($button.Current.Name -match 'UsageWidget') { $entry = $button; break }
  }
  $name = if ($null -ne $entry) { $entry.Current.Name } else { '' }
  @{ found = $null -ne $entry; name = $name; buttons = $names; minimized = [WidgetNative]::IsIconic($hwnd) } | ConvertTo-Json -Compress
  exit
}
$rect = New-Object WidgetNative+RECT
[void][WidgetNative]::GetWindowRect($hwnd, [ref]$rect)
$cx = [int](($rect.Left + $rect.Right) / 2)
$cy = [int](($rect.Top + $rect.Bottom) / 2)
if ($Action -eq 'Desktop') {
  # A normal control window proves this is Show Desktop, not the opposite toggle.
  Add-Type -AssemblyName System.Windows.Forms
  $control = New-Object System.Windows.Forms.Form
  $control.Text = 'Win+D verification'
  $control.StartPosition = 'Manual'
  $control.SetBounds(20, 20, 200, 80)
  $control.Show()
  $control.Activate()
  [System.Windows.Forms.Application]::DoEvents()
  function Toggle-Desktop {
    [WidgetNative]::ToggleDesktop()
  }
  Toggle-Desktop
  try {
    for ($wait = 0; $wait -lt 10; $wait++) {
      Start-Sleep -Milliseconds 100
      [System.Windows.Forms.Application]::DoEvents()
    }
    $point = New-Object WidgetNative+POINT
    $point.X = $cx; $point.Y = $cy
    $root = [WidgetNative]::GetAncestor([WidgetNative]::WindowFromPoint($point), 2)
    $controlBounds = New-Object WidgetNative+RECT
    [void][WidgetNative]::GetWindowRect($control.Handle, [ref]$controlBounds)
    $point.X = [int](($controlBounds.Left + $controlBounds.Right) / 2)
    $point.Y = [int](($controlBounds.Top + $controlBounds.Bottom) / 2)
    $controlRoot = [WidgetNative]::GetAncestor([WidgetNative]::WindowFromPoint($point), 2)
    @{
      visible = [WidgetNative]::IsWindowVisible($hwnd)
      minimized = [WidgetNative]::IsIconic($hwnd)
      uncovered = $root -eq $hwnd
      root = $root.ToInt64()
      controlMinimized = [WidgetNative]::IsIconic($control.Handle)
      controlUncovered = $controlRoot -eq $control.Handle
    } | ConvertTo-Json -Compress
  } finally {
    Toggle-Desktop
    for ($wait = 0; $wait -lt 5; $wait++) {
      Start-Sleep -Milliseconds 100
      [System.Windows.Forms.Application]::DoEvents()
    }
    $control.Dispose()
  }
  exit
}
$points = @{
  n = @($cx, ($rect.Top + 1))
  s = @($cx, ($rect.Bottom - 2))
  w = @(($rect.Left + 1), $cy)
  e = @(($rect.Right - 2), $cy)
  # Corner grabs sit 5px in: the layered window's 14px rounded cutouts make the
  # extreme corner tip click-through, so grabbing there hits the window below.
  nw = @(($rect.Left + 5), ($rect.Top + 5))
  ne = @(($rect.Right - 4), ($rect.Top + 3))
  sw = @(($rect.Left + 5), ($rect.Bottom - 5))
  se = @(($rect.Right - 5), ($rect.Bottom - 5))
}

if ($Action -eq 'Inspect') {
  $hits = @{}
  foreach ($key in $points.Keys) {
    $point = $points[$key]
    $packed = (($point[1] -band 0xffff) -shl 16) -bor ($point[0] -band 0xffff)
    $result = [UIntPtr]::Zero
    [void][WidgetNative]::SendMessageTimeout($hwnd, 0x84, [IntPtr]::Zero, [IntPtr]$packed, 2, 1500, [ref]$result)
    $hits[$key] = [int]$result.ToUInt64()
  }
  @{ hits = $hits; thickFrame = ([WidgetNative]::GetWindowLong($hwnd, -16) -band 0x40000) -ne 0; minimizeBox = ([WidgetNative]::GetWindowLong($hwnd, -16) -band 0x20000) -ne 0 } | ConvertTo-Json -Compress
  exit
}

$pointer = New-Object WidgetNative+POINT
[void][WidgetNative]::GetCursorPos([ref]$pointer)
if ($Action -eq 'Move') {
  # Press the window body (center, clear of caption buttons) and drag it like a widget.
  $before = New-Object WidgetNative+RECT
  [void][WidgetNative]::GetWindowRect($hwnd, [ref]$before)
  $after = $before
  $target = 0
  Wait-ForIdleCursor
  try {
    $actualPointer = New-Object WidgetNative+POINT
    $target = [WidgetNative]::BeginDrag($cx, $cy, [ref]$actualPointer)
    Start-Sleep -Milliseconds 60
    for ($i = 1; $i -le 10; $i++) {
      [void][WidgetNative]::SetCursorPos($cx + [int]($DeltaX * $i / 10), $cy + [int]($DeltaY * $i / 10))
      Start-Sleep -Milliseconds 16
      $after = New-Object WidgetNative+RECT
      [void][WidgetNative]::GetWindowRect($hwnd, [ref]$after)
    }
  } finally {
    [WidgetNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    [void][WidgetNative]::SetCursorPos($pointer.X, $pointer.Y)
  }
  @{ startX = $before.Left; startY = $before.Top; startWidth = $before.Right - $before.Left; startHeight = $before.Bottom - $before.Top; endX = $after.Left; endY = $after.Top; endWidth = $after.Right - $after.Left; endHeight = $after.Bottom - $after.Top; target = $target } | ConvertTo-Json -Compress
  exit
}
if ($Action -eq 'Click') {
  # Plain left click at (window.Left + DeltaX, window.Top + DeltaY): used to tap
  # the small box body to expand it.
  Wait-ForIdleCursor
  try {
    [void][WidgetNative]::SetCursorPos($rect.Left + $DeltaX, $rect.Top + $DeltaY)
    Start-Sleep -Milliseconds 120
    [WidgetNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [WidgetNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
  } finally {
    [void][WidgetNative]::SetCursorPos($pointer.X, $pointer.Y)
  }
  @{ x = $rect.Left; y = $rect.Top } | ConvertTo-Json -Compress
  exit
}
$start = $points[$Edge]
if ($Edge -in @('nw', 'sw', 'se')) {
  # Walk the diagonal inward until the pixel belongs to our window: any DPI, any radius.
  $stepX = if ($Edge -eq 'se') { -1 } else { 1 }
  $stepY = if ($Edge -eq 'nw') { 1 } else { -1 }
  for ($k = 0; $k -lt 24; $k++) {
    $probe = New-Object WidgetNative+POINT
    $probe.X = $start[0] + $stepX * $k
    $probe.Y = $start[1] + $stepY * $k
    if ([WidgetNative]::GetAncestor([WidgetNative]::WindowFromPoint($probe), 2) -ne $hwnd) { continue }
    # Opaque is not enough: caption buttons also belong to us. Only a real resize hit counts.
    $packed = (($probe.Y -band 0xffff) -shl 16) -bor ($probe.X -band 0xffff)
    $hit = [UIntPtr]::Zero
    [void][WidgetNative]::SendMessageTimeout($hwnd, 0x84, [IntPtr]::Zero, [IntPtr]$packed, 2, 1500, [ref]$hit)
    $code = [int]$hit.ToUInt64()
    if ($code -ge 10 -and $code -le 17) {
      $start = @($probe.X, $probe.Y)
      break
    }
  }
}
$frames = @()
Wait-ForIdleCursor
try {
  $actualPointer = New-Object WidgetNative+POINT
  $target = [WidgetNative]::BeginDrag($start[0], $start[1], [ref]$actualPointer)
  Start-Sleep -Milliseconds 40
  for ($i = 1; $i -le 20; $i++) {
    [void][WidgetNative]::SetCursorPos($start[0] + [int]($DeltaX * $i / 20), $start[1] + [int]($DeltaY * $i / 20))
    Start-Sleep -Milliseconds 16
    $current = New-Object WidgetNative+RECT
    [void][WidgetNative]::GetWindowRect($hwnd, [ref]$current)
    $frames += @{ x = $current.Left; y = $current.Top; width = $current.Right - $current.Left; height = $current.Bottom - $current.Top; target = $target; pointerX = $actualPointer.X; pointerY = $actualPointer.Y; down = ([WidgetNative]::GetAsyncKeyState(1) -band 0x8000) -ne 0 }
  }
} finally {
  [WidgetNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
  [void][WidgetNative]::SetCursorPos($pointer.X, $pointer.Y)
}
$frames | ConvertTo-Json -Compress
