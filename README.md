# PCConsoleMode

PCConsoleMode is a small WPF utility that replicates the behavior of the original old-scripts/xbox.ps1: it watches for an Xbox Wireless Controller connecting via Bluetooth and switches the PC between a "game" setup (external display, selected audio device, launch a program such as Steam Big Picture) and a "desktop" setup (internal display, desktop audio device, stop the program).

Key features
- Watch for controller connect / disconnect using WMI and the same PnP property used by the original script.
- Configure controller, audio devices (game/desktop), program to launch (Steam or custom) and program args.
- Debounce window to ignore noisy or rapid state flips.
- Tray (background) mode with optional start-on-login registration.
- Settings saved to settings.json (local, not committed to Git by default).

Requirements
- Windows 10/11 with .NET 10 (net10.0-windows).
- PowerShell available (the app invokes a few PowerShell commands).
- Optional: AudioDeviceCmdlets PowerShell module for audio device enumeration/control (used when available).

Build & run
1. Open the solution in Visual Studio (or use dotnet build).
2. Run the app. Use the UI to configure controller, audio devices and program/args.
3. Click Save to persist configuration to settings.json. Click Start Monitoring to begin watching for controller events, or Run in Background to minimize to tray and enable start-on-login.

Notes
- The app invokes PowerShell commands (Get-PnpDevice, Get-PnpDeviceProperty, AudioDeviceCmdlets). Ensure the executing user has the necessary permissions. The HKCU Run registry entry is used for start-on-login so admin rights are not required for that.
- settings.json is ignored by .gitignore so your local configuration will not be committed.
- If you need more robust process tracking (to only stop the exact launched process instance), that can be implemented.

Troubleshooting
- If audio devices do not appear, install the AudioDeviceCmdlets PowerShell module or refresh the audio list in the UI.
- If the controller state does not change, try running the PowerShell commands shown in the original script to confirm the property values on your machine.

License
This repository contains your project code. Replace this section with the appropriate license for your project.
