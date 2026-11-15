# UsageWidget

A small, native Windows 11 Codex quota monitor. One executable; no browser engine
or npm dependencies are included. It uses Windows' .NET Framework 4.8 / WPF.

## Run

Open `native/bin/UsageWidget.exe`. Codex CLI must already be installed and signed
in. The widget reads quotas through its local app-server, without submitting
model prompts or storing credentials.

- Two-column quota view and compact percentage/reset view.
- Aspect-locked resizing scales text and icons with the frame. Each view remembers
  its size during this run; switching keeps the current center, including after a move.
- A single WPF rounded surface with a dark translucent fill; no overlapping system
  border, Acrylic backdrop, or native clipping region.
- Notification-area icon (normally in tray overflow); no taskbar buttons. Left-click
  toggles the widget; right-click offers Show/Hide, Refresh, Check usage, and Exit.
- One cyan/blue quota-ring icon for the tray and executable, with 16–256 px variants.
- Minimize and Close hide to the tray. Exit in the tray menu ends the application.
- The Usage button to the left of Refresh reads live usage and reset-credit
  availability from the Codex app-server, including backend titles and local expiry times.
- Select a reset credit in the usage window and click **Redeem selected reset** to
  redeem that specific credit. The widget uses an idempotency key for retries and
  refreshes availability after a completed response; checking usage alone never redeems.
- Single-instance behavior restores the existing widget even if it is hidden.
- Pin keeps the widget above ordinary windows and visible during Win+D.
- Automatic refresh every 60 seconds; manual vector refresh button.

## Build

No SDK download is required on this machine. The build uses Windows' C# compiler
and embeds the XAML and manifest into the executable:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File native/build.ps1
```

Output: `native/bin/UsageWidget.exe`. `npm run build` is an optional shortcut.

## Verify

Close any running copy, then:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File native/verify-all.ps1
```

Verification covers live quota reads, Windows UI Automation, real mouse resizing
on four edges and two left corners, size memory, moved-position switching, Win+D,
tray restoration, absence of taskbar buttons, and DWM attributes.
`native/verify-layout.ps1` checks text fitting, rendered transparency, scaling,
and quota parser edge cases. `native/verify-features.ps1` uses a local mock
app-server to verify redemption outcomes, retry idempotency, double-click guarding,
timeouts and stale-data handling without consuming any real reset credits.
Test scripts are not embedded in the executable.
