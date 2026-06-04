# PCConsoleMode

PCConsoleMode is a small Windows desktop utility that detects when a Bluetooth controller (default: "Xbox Wireless Controller") connects and switches your system between a "game" configuration and a "desktop" configuration. Typical actions include changing display mode, switching the default audio device, and launching/stopping a program such as Steam Big Picture.

Key features
- WMI-based device change detection and PnP inspection for controller presence.
- Configure controller friendly name, game/desktop audio devices, program to launch (Steam or custom) and program args.
- Debounce window to avoid rapid flip-flopping when devices flake.
- Minimize to tray and optional start-on-login registration (HKCU Run entry).
- Settings persisted to a local settings.json file.

Requirements
- Windows 10 or Windows 11 (desktop app using WPF).
- .NET: none required to run if you publish a self-contained single-file executable (recommended). If you plan to run from source you need the .NET 10 SDK to build.
- PowerShell available on PATH (either Windows PowerShell or PowerShell Core/pwsh). The app runs a few short PowerShell commands under the current user context.
- Optional: the AudioDeviceCmdlets PowerShell module for better audio device enumeration and control. If the module is missing the app will still try heuristic matching of audio device names.

Packaging / Distribution
- Recommended: publish as a self-contained single-file executable so end users do not need to install .NET. Example (from repo root):

  dotnet publish PCConsoleMode.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false

  Notes:
  - Use the RID appropriate for your target (win-x64, win-x86).
  - Keep PublishTrimmed=false unless you validate trimming, because native P/Invoke (Dbghelp.dll) and reflection may be affected by aggressive trimming.

Running from source (developer)
- You will need the .NET 10 SDK installed. Build with:

  dotnet build

  Then run from Visual Studio or dotnet run.

Behavioral details and runtime notes
- The app invokes PowerShell commands (Get-PnpDevice, Get-PnpDeviceProperty, Get-AudioDevice etc.). These are executed as the current user and do not require elevation for the HKCU Run registry entry.
- The app attempts to write logs and crash dumps under the application's base directory (near the executable). When publishing as single-file, verify disk write permissions in the chosen install location; using a per-user location (LocalAppData) is recommended for installers.
- Crash dumps (minidumps) are only created on Windows and only when Dbghelp.dll is available; failures to create dumps are logged but do not crash the app.

Troubleshooting
- Audio devices not listed: install AudioDeviceCmdlets or use the Refresh button after plugging devices in.
- Controller changes not detected: try running the underlying PowerShell commands used by the app to confirm PnP property values on your machine.
- Publish errors: if publishing as self-contained single-file fails, check the publish output for missing native dependencies (Dbghelp) or permission issues on the publish target folder.

Privacy & storage
- settings.json is stored beside the executable (or in the publish folder). It is not committed to source control by default.

License
- Replace this section with your chosen license.
