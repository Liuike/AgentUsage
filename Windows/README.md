# AgentUsage for Windows

The Windows edition is a native notification-area app that preserves the Codex portion of AgentUsage. It uses the authenticated `codex app-server` already installed on the machine and does not send analytics or telemetry.

## Requirements

- Windows 10 or Windows 11
- .NET Framework 4.7.2 or newer (included with supported Windows versions)
- Codex CLI installed, available on `PATH`, and signed in

## Build and run

From PowerShell at the repository root:

```powershell
.\Scripts\build-windows.ps1
.\Windows\bin\AgentUsage.exe --show
```

No NuGet packages, SDK downloads, or JavaScript runtime are required. The build uses the .NET Framework compiler included with Windows.

To install under `%LOCALAPPDATA%\Programs\AgentUsage` and create a Start Menu shortcut:

```powershell
.\Scripts\install-windows.ps1
```

In a packaged release, extract the ZIP and run:

```powershell
.\Install-AgentUsage.ps1
```

Left-click the tray icon to show or hide the panel. Right-click it for refresh, startup, and quit commands.

## Windows behavior

- Settings are stored in `%APPDATA%\AgentUsage\settings.ini`.
- **Open on Startup** uses the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key and does not require administrator access.
- The tray icon reflects the chosen metric and display mode. Hovering it shows both current Codex limits.
- Day, Week, and Cumulative views use the same Codex activity data and visual language as the macOS app.
- Claude Code support is intentionally not part of the Windows edition.
