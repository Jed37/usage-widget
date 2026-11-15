# GrokUsageWidget

A small native Windows widget for a SuperGrok subscription. Grok Build and Grok Bot are separate meters, so the window shows both.

## Run

Open `bin/GrokUsageWidget.exe`. It needs:

- Grok Build signed in on this PC (`grok login`). The widget reads `~/.grok/auth.json` and refreshes the access token before it expires.
- Grok Bot signed in on this PC. The widget reads that app's local session and does not store a copy of the token.

Refresh is automatic every 60 seconds. The tray icon toggles the window; Exit in the tray menu quits.

## Build

```powershell
dotnet build GrokUsageWidget.csproj -c Release
```

Output: `bin/Release/net48/GrokUsageWidget.exe`. Copy that file, and the `.exe.config` beside it, over `bin/GrokUsageWidget.exe` if you want the repo copy updated.
