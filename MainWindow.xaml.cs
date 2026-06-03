using System.Text;
using System.Windows;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Management;
using Microsoft.Win32;
using System.Windows.Threading;
using System.Reflection;
using System.Runtime.InteropServices;
using Registry = Microsoft.Win32.Registry;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace PCConsoleMode
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // Check command-line for explicit minimized request (e.g. from registry Run value)
            var cmdArgs = System.Environment.GetCommandLineArgs();
            var cmdMinimized = cmdArgs.Any(a => string.Equals(a, "--minimized", System.StringComparison.OrdinalIgnoreCase));
            LoadSettings();
            PopulateBtDevices();
            BindSettingsToUi();
            // If configured to run at startup (either saved or registry), start minimized to tray
            if ((_settings.RunAtStartup || RegistryRunKeyExists()) && _settingsLoaded)
            {
                // minimize-to-tray if user had that enabled, or if the process was launched with --minimized
                if (_settings.MinimizeToTray || cmdMinimized)
                {
                    MinimizeToTray();
                }
                // If RunAtStartup is enabled, ensure monitoring is started on launch
                if (_settings.RunAtStartup || _settings.IsMonitoring)
                {
                    _settings.IsMonitoring = true;
                    StartMonitoringIfNeeded();
                    // persist that monitoring should be active
                    SaveSettings();
                }
            }

        }

        private void RunAtStartupCheck_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressUiEvents) return;
            try
            {
                // Include --minimized in the registry command if the user wants minimize-to-tray
                var includeMin = MinimizeToTrayCheck.IsChecked == true;
                EnableStartup(includeMin);
                _settings.RunAtStartup = true;
                // persist the preference so next launch reads it
                SaveSettings(true);
                // do not minimize the running app when toggling run-at-startup; only create registry entry
                UpdateAutoStartIndicator();
            }
            catch { }
        }

        private void RunAtStartupCheck_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressUiEvents) return;
            try
            {
                DisableStartup();
                _settings.RunAtStartup = false;
                SaveSettings(true);
                UpdateAutoStartIndicator();
            }
            catch { }
        }

        private CancellationTokenSource? _cts;
        private bool _lastBtStatus = false;
        private ManagementEventWatcher? _watcher;
        private readonly string _settingsFile = "settings.json";
        private Settings _settings = new Settings();
        private bool _settingsLoaded = false;
        private bool _suppressUiEvents = false;
        private DateTime _lockUntil = DateTime.MinValue;
        private bool _advancedVisible = false;

        private void BindSettingsToUi()
        {
            Dispatcher.Invoke(() => {
                // select controller in combo if present
                if (!string.IsNullOrEmpty(_settings.ControllerFriendlyName) && ControllerCombo.ItemsSource is IEnumerable<string> items)
                {
                    var match = items.FirstOrDefault(s => s == _settings.ControllerFriendlyName);
                    if (match != null) ControllerCombo.SelectedItem = match;
                }
                // populate audio combos and select stored choices
                PopulateAudioDevices();
                if (!string.IsNullOrEmpty(_settings.GameAudioDeviceId))
                    GameAudioCombo.SelectedValue = _settings.GameAudioDeviceId;
                if (!string.IsNullOrEmpty(_settings.DesktopAudioDeviceId))
                    DesktopAudioCombo.SelectedValue = _settings.DesktopAudioDeviceId;
                // Program choice
                ProgramChoice.SelectedIndex = (_settings.LaunchMode ?? "Steam") == "Custom" ? 1 : 0;
                // Populate program path textbox according to selected mode
                UpdateProgramPathDisplay();
                ProgramArgsText.Text = _settings.ProgramArgs ?? string.Empty;
                RetryCountText.Text = _settings.RetryCount.ToString();
                RetryDelayText.Text = _settings.RetryDelaySeconds.ToString();
                // show debounce seconds (interval) in UI; default is 1 in Settings
                IntervalText.Text = _settings.DebounceSeconds.ToString();
                // Minimize to tray and run at startup checkboxes
                _suppressUiEvents = true;
                MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
                // sync run-at-startup with registry actual value
                var regHas = RegistryRunKeyExists();
                RunAtStartupCheck.IsChecked = regHas;
                _suppressUiEvents = false;
                UpdateAutoStartIndicator();
            });
        }

        private bool RegistryRunKeyExists()
        {
            try
            {
                const string regPath = "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\PCConsoleMode";
                var runKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false);
                if (runKey == null)
                {
                    Log($"Registry Run key not found: {regPath}");
                    return false;
                }
                var val = runKey.GetValue("PCConsoleMode");
                if (val != null)
                {
                    Log($"Registry Run value found: {regPath} = {val}");
                    return true;
                }
                Log($"Registry Run value not present: {regPath}");
                return false;
            }
            catch { return false; }
        }

        private void UpdateAutoStartIndicator()
        {
            Dispatcher.Invoke(() => {
                // Show that monitoring will auto-start if the app is configured to run at startup
                // (either via saved preference or registry Run key). This aligns with the user's
                // expectation that checking Run at startup enables auto-start behavior.
                if (RegistryRunKeyExists() || _settings.RunAtStartup)
                {
                    AutoStartIndicator.Text = "Monitoring WILL auto-start on next launch";
                    AutoStartIndicator.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    AutoStartIndicator.Text = "Monitoring will NOT auto-start on next launch";
                    AutoStartIndicator.Foreground = System.Windows.Media.Brushes.Gray;
                }
            });
        }

        private void LoadSettings()
        {
            _settingsLoaded = File.Exists(_settingsFile);
            if (_settingsLoaded)
            {
                try
                {
                    var json = File.ReadAllText(_settingsFile);
                    _settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                }
                catch { _settings = new Settings(); }
            }
        }

        private void PopulateBtDevices()
        {
            try
            {
                var script = "Get-PnpDevice -Class Bluetooth | Select-Object -ExpandProperty FriendlyName";
                var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return;
                var outt = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                var lines = outt.Split(new[] { '\r','\n' }, System.StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                Dispatcher.Invoke(() => {
                    ControllerCombo.ItemsSource = lines;
                    if (!string.IsNullOrEmpty(_settings.ControllerFriendlyName) && lines.Contains(_settings.ControllerFriendlyName))
                        ControllerCombo.SelectedItem = _settings.ControllerFriendlyName;
                });
            }
            catch (Exception ex)
            {
                Log($"PopulateBtDevices error: {ex.Message}");
            }
        }

        private void RefreshBtButton_Click(object sender, RoutedEventArgs e)
        {
            PopulateBtDevices();
        }

        private void PopulateAudioDevices()
        {
            try
            {
                var script = "Import-Module AudioDeviceCmdlets -ErrorAction SilentlyContinue; Get-AudioDevice -List | ForEach-Object { $_.ID + '||' + $_.Name }";
                var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return;
                var outt = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var lines = outt.Split(new[] { '\r','\n' }, System.StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Contains("||")).Select(s => {
                    var parts = s.Split(new[] {"||"}, System.StringSplitOptions.None);
                    return new AudioDevice { Id = parts[0], Display = parts[1] };
                }).ToList();
                Dispatcher.Invoke(() => {
                    GameAudioCombo.ItemsSource = lines;
                    DesktopAudioCombo.ItemsSource = lines;
                });
            }
            catch (Exception ex)
            {
                Log($"PopulateAudioDevices error: {ex.Message}");
            }
        }

        private void RefreshAudioButton_Click(object sender, RoutedEventArgs e)
        {
            PopulateAudioDevices();
        }

        private void RefreshDesktopButton_Click(object sender, RoutedEventArgs e)
        {
            PopulateAudioDevices();
        }

        private void BrowseSteamButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Executables|*.exe|All files|*.*";
            if (dlg.ShowDialog() == true)
            {
                SteamPathText.Text = dlg.FileName;
            }
        }

        private void ProgramChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateProgramPathDisplay();
        }

        private void UpdateProgramPathDisplay()
        {
            var choice = (ProgramChoice.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (choice == "Steam")
            {
                // If we don't have a stored Steam path, try to discover a common installation location
                if (string.IsNullOrWhiteSpace(_settings.SteamPath))
                {
                    var found = FindSteamExe();
                    if (!string.IsNullOrWhiteSpace(found)) _settings.SteamPath = found;
                }
                SteamPathText.Text = _settings.SteamPath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(ProgramArgsText.Text)) ProgramArgsText.Text = "steam://open/bigpicture";
            }
            else
            {
                // Custom program mode: show the custom path (may be blank)
                SteamPathText.Text = _settings.CustomPath ?? string.Empty;
                if (ProgramArgsText.Text == "steam://open/bigpicture") ProgramArgsText.Text = string.Empty;
            }
        }

        private string FindSteamExe()
        {
            try
            {
                var candidates = new[] {
                    @"C:\\Program Files (x86)\\Steam\\steam.exe",
                    @"C:\\Program Files\\Steam\\steam.exe"
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
            return string.Empty;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveSettings(true);
                Log("Settings saved");
                StatusText.Text = "Saved";
                Task.Delay(1000).ContinueWith(_ => Dispatcher.Invoke(() => StatusText.Text = _watcher is not null ? "Running" : "Stopped"));
            }
            catch (Exception ex)
            {
                Log($"SaveButton_Click error: {ex.Message}");
                Logger.LogException(ex, "SaveButton_Click");
                try { CrashDumper.WriteDump(ex, "SaveButton_Click"); } catch { }
                throw;
            }
        }

        private void ToggleAdvancedButton_Click(object? sender, RoutedEventArgs e)
        {
            _advancedVisible = !_advancedVisible;
            AdvancedContent.Visibility = _advancedVisible ? Visibility.Visible : Visibility.Collapsed;
            if (AdvancedToggleIcon != null)
            {
                AdvancedToggleIcon.Content = _advancedVisible ? "▼" : "▶";
            }
        }

        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isBackground = false;

        // BackgroundButton removed; use Start Monitoring + close to minimize to tray

        private void StartMonitoringIfNeeded()
        {
            if (_watcher is null)
            {
                StartWatcher();
                Dispatcher.Invoke(() => { StartStopButton.Content = "Stop Monitoring"; StatusText.Text = "Running"; });
                Log("Monitoring started (background)");
            }
        }

        private void MinimizeToTray()
        {
            _notifyIcon ??= new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Text = "PCConsoleMode";
            try
            {
                // Load the icon from the application resources (pack URI)
                var uri = new System.Uri("pack://application:,,,/icons/1-05_icon-icons.com_69204.ico", System.UriKind.Absolute);
                var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
                if (stream != null)
                {
                    using var ms = new System.IO.MemoryStream();
                    stream.CopyTo(ms);
                    ms.Seek(0, System.IO.SeekOrigin.Begin);
                    _notifyIcon.Icon = new System.Drawing.Icon(ms);
                }
                else
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                }
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += (s, e) => Dispatcher.Invoke(RestoreFromTray);

            var menu = new System.Windows.Forms.ContextMenuStrip();
            var openItem = new System.Windows.Forms.ToolStripMenuItem("Open", null, (s,e)=> Dispatcher.Invoke(RestoreFromTray));
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit", null, (s,e)=> Dispatcher.Invoke(() => { _notifyIcon.Visible = false; System.Windows.Application.Current.Shutdown(); }));
            menu.Items.Add(openItem);
            menu.Items.Add(exitItem);
            _notifyIcon.ContextMenuStrip = menu;

            _isBackground = true;
            this.Hide();
        }

        private void RestoreFromTray()
        {
            _isBackground = false;
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            if (_notifyIcon != null) _notifyIcon.Visible = true;
        }

        // Called by the single-instance activation handler to restore the window and reset background state.
        public void ActivateFromExternal()
        {
            _isBackground = false;
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            if (_notifyIcon != null) _notifyIcon.Visible = true;
        }

        private void EnableStartup(bool includeMinimized)
        {
            try
            {
                var runKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (runKey == null)
                {
                    // try to create the key if it doesn't exist
                    var baseKey = Registry.CurrentUser.CreateSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run");
                    runKey = baseKey;
                }
                // Determine a command that will actually start the app on login.
                // Prefer the actual process executable (Environment.ProcessPath) for single-file published apps.
                // If the entry assembly is a .dll but a sibling .exe exists (common when publishing), prefer that .exe.
                string? processExe = System.Environment.ProcessPath;
                string? entryAssembly = Assembly.GetEntryAssembly()?.Location;
                string command = string.Empty;

                // If entry assembly is a DLL but there's an EXE with the same base name beside it, prefer the EXE.
                if (!string.IsNullOrEmpty(entryAssembly) && entryAssembly.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var dir = System.IO.Path.GetDirectoryName(entryAssembly) ?? string.Empty;
                        var candidateExe = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(entryAssembly) + ".exe");
                        if (System.IO.File.Exists(candidateExe))
                        {
                            command = "\"" + candidateExe + "\"";
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(command))
                {
                    // If running under dotnet host and entry assembly is a dll, run: "dotnet" "app.dll"
                    processExe ??= Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(processExe) && System.IO.Path.GetFileName(processExe).StartsWith("dotnet", System.StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entryAssembly))
                    {
                        command = "\"" + processExe + "\" \"" + entryAssembly + "\"";
                    }
                    else if (!string.IsNullOrEmpty(entryAssembly) && System.IO.Path.GetExtension(entryAssembly).Equals(".exe", System.StringComparison.OrdinalIgnoreCase))
                    {
                        // standalone exe
                        command = "\"" + entryAssembly + "\"";
                    }
                    else if (!string.IsNullOrEmpty(processExe))
                    {
                        // fallback to the process executable
                        command = "\"" + processExe + "\"";
                    }
                    else
                    {
                        // last resort: try current process file name
                        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                        command = exePath != null ? "\"" + exePath + "\"" : string.Empty;
                    }
                }

                const string regPath = "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\PCConsoleMode";
                if (!string.IsNullOrEmpty(command))
                {
                    // append --minimized if requested so the launched process knows to minimize
                    if (includeMinimized)
                    {
                        command = command + " --minimized";
                    }
                    runKey.SetValue("PCConsoleMode", command);
                    Log($"EnableStartup: wrote {regPath} = {command}");
                }
                else
                {
                    Log($"EnableStartup: no command determined, nothing written to {regPath}");
                }
            }
            catch (Exception ex) { Log($"EnableStartup error: {ex.Message}"); }
        }

        private void DisableStartup()
        {
            try
            {
                var runKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                const string regPath = "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\PCConsoleMode";
                if (runKey != null)
                {
                    var val = runKey.GetValue("PCConsoleMode");
                    if (val != null)
                    {
                        runKey.DeleteValue("PCConsoleMode", false);
                        Log($"DisableStartup: removed {regPath} (previous value: {val})");
                    }
                    else
                    {
                        Log($"DisableStartup: Run value not present: {regPath}");
                    }
                }
                else
                {
                    Log($"DisableStartup: Run registry key not found: {regPath}");
                }
            }
            catch (Exception ex) { Log($"DisableStartup error: {ex.Message}"); }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // if minimize-to-tray is enabled, cancel close and minimize to tray
            if (MinimizeToTrayCheck.IsChecked == true)
            {
                e.Cancel = true;
                if (!_isBackground)
                {
                    // just minimize to tray; do not start monitoring automatically
                    MinimizeToTray();
                }
                return;
            }
            base.OnClosing(e);
        }

        private void SaveSettings(bool forceCreate = false)
        {
            _settings.ControllerFriendlyName = (ControllerCombo.SelectedItem as string) ?? string.Empty;
            _settings.GameAudioDeviceId = (GameAudioCombo.SelectedValue as string) ?? string.Empty;
            _settings.DesktopAudioDeviceId = (DesktopAudioCombo.SelectedValue as string) ?? string.Empty;
            var programChoice = (ProgramChoice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Steam";
            _settings.LaunchMode = programChoice == "Custom" ? "Custom" : "Steam";
            // Persist path based on selected program choice
            if (programChoice == "Custom")
            {
                _settings.CustomPath = SteamPathText.Text.Trim();
            }
            else
            {
                _settings.SteamPath = SteamPathText.Text.Trim();
            }
            _settings.ProgramArgs = ProgramArgsText.Text.Trim();
            if (int.TryParse(IntervalText.Text.Trim(), out var iv)) _settings.DebounceSeconds = iv;
            if (int.TryParse(RetryCountText.Text.Trim(), out var rc)) _settings.RetryCount = rc;
            if (int.TryParse(RetryDelayText.Text.Trim(), out var rd)) _settings.RetryDelaySeconds = rd;
            _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
            _settings.RunAtStartup = RunAtStartupCheck.IsChecked == true;
            // Only create/write settings file if we previously loaded settings or forceCreate is true
            if (_settingsLoaded || forceCreate)
            {
                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFile, json);
                _settingsLoaded = true;
            }
            UpdateAutoStartIndicator();
        }



        private void Log(string msg)
        {
            try
            {
                Dispatcher.Invoke(() => {
                    LogText.AppendText($"{DateTime.Now:HH:mm:ss} - {msg}\n");
                    LogText.ScrollToEnd();
                });
            }
            catch { }
            Logger.Log(msg);
        }

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_watcher != null)
            {
                StopWatcher();
                StartStopButton.Content = "Start Monitoring";
                StatusText.Text = "Stopped";
                SaveSettings(true);
                Log("Monitoring stopped by user");
                return;
            }

            SaveSettings(true);
            StartWatcher();
            StartStopButton.Content = "Stop Monitoring";
            StatusText.Text = "Running";
            Log("Monitoring started");
        }

        // WMI watcher-based approach replaces polling
        private void StartWatcher()
        {
            try
            {
                // set baseline
                _lastBtStatus = GetBtStatus(_settings.ControllerFriendlyName);
                var query = new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent");
                _watcher = new ManagementEventWatcher(query);
                _watcher.EventArrived += (s, e) => {
                    Log("Device change detected (WMI event)");
                    try { CheckControllerStatus(); } catch (Exception ex) { Log($"CheckControllerStatus error: {ex.Message}"); }
                };
                _watcher.Start();
                _settings.IsMonitoring = true;
                SaveSettings();
                Log("WMI watcher started");
            }
            catch (Exception ex)
            {
                Log($"StartWatcher error: {ex.Message}");
                _watcher = null;
            }
        }

        private void StopWatcher()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.Stop();
                    _watcher.Dispose();
                    _watcher = null;
                    _settings.IsMonitoring = false;
                    SaveSettings();
                }
                Log("WMI watcher stopped");
            }
            catch (Exception ex) { Log($"StopWatcher error: {ex.Message}"); }
        }

        private void CheckControllerStatus()
        {
            bool bt = GetBtStatus(_settings.ControllerFriendlyName);
            if (bt == _lastBtStatus)
            {
                Log("No change..");
                return;
            }

            var now = DateTime.UtcNow;
            if (now < _lockUntil)
            {
                Log($"Change to {(bt ? "connected" : "disconnected")} ignored due to debounce until {_lockUntil:HH:mm:ss}");
                return;
            }

            Log("Detected change in controller presence");
            _lastBtStatus = bt;
            try { SwitchMode(bt); }
            catch (Exception ex) { Log($"SwitchMode error: {ex.Message}"); }

            // lock until opposite events are allowed
            _lockUntil = DateTime.UtcNow.AddSeconds(Math.Max(0, _settings.DebounceSeconds));
        }

        private bool GetBtStatus(string friendlyName)
        {
            // Use PowerShell similar to original script: read the PnP device property Data
            if (string.IsNullOrWhiteSpace(friendlyName)) friendlyName = "Xbox Wireless Controller";
            // escape single quotes
            var fnEsc = friendlyName.Replace("'", "''");
            var script = $"-NoProfile -Command \"try {{ (Get-PnpDevice -Class Bluetooth -FriendlyName '{fnEsc}' | Get-PnpDeviceProperty -KeyName '{{83DA6326-97A6-4088-9453-A1923F573B29}} 15' | Select -ExpandProperty Data) }} catch {{ $false }}\"";
            var psi = new ProcessStartInfo("powershell", script)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return false;
                var outt = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(2000);
                if (string.IsNullOrEmpty(outt)) return false;
                // PowerShell may return True/False or 1/0 or other; try parse
                if (bool.TryParse(outt, out var b)) return b;
                if (int.TryParse(outt, out var i)) return i != 0;
                // fallback: check textual values
                return outt.Equals("OK", System.StringComparison.OrdinalIgnoreCase) || outt.Equals("True", System.StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log($"GetBtStatus error: {ex.Message}");
                return false;
            }
        }

        private void SwitchMode(bool btStatus)
        {
            if (btStatus)
            {
                Log("Launching game mode...");
                RunProcessHidden("DisplaySwitch.exe", "/external");
                string? deviceId = null;
                // If user selected a specific audio device, prefer that
                if (!string.IsNullOrEmpty(_settings.GameAudioDeviceId))
                {
                    deviceId = _settings.GameAudioDeviceId;
                }
                else
                {
                    int retries = 1;
                    while (retries <= 10 && deviceId == null)
                    {
                        // try common names
                        deviceId = GetAudioDeviceID("Beyond");
                        if (deviceId == null) deviceId = GetAudioDeviceID("SONY");
                        if (deviceId == null) deviceId = GetAudioDeviceID("HDMI");
                        if (deviceId != null) break;
                        Log($"retrying audio device.. ({retries})");
                        Thread.Sleep(1000);
                        retries++;
                    }
                }
                if (deviceId == null) throw new Exception("No suitable audio device found.");
                // attempt to set and verify with retries
                if (!TrySetAudioDeviceWithRetries(deviceId))
                {
                    throw new Exception("Failed to set game audio device after retries.");
                }
                if (!string.IsNullOrWhiteSpace(_settings.SteamPath))
                {
                    var args = _settings.ProgramArgs ?? "steam://open/bigpicture";
                    RunProcessHidden(_settings.SteamPath, args);
                }
            }
            else
            {
                Log("Launching desktop mode...");
                var desktopId = !string.IsNullOrEmpty(_settings.DesktopAudioDeviceId) ? _settings.DesktopAudioDeviceId : GetAudioDeviceID("Headphones");
                if (desktopId != null)
                {
                    if (!TrySetAudioDeviceWithRetries(desktopId))
                    {
                        Log("Warning: failed to set desktop audio device after retries.");
                    }
                }
                RunProcessHidden("DisplaySwitch.exe", "/internal");
                // stop steam or custom process if configured
                if (_settings.LaunchMode == "Custom")
                {
                    var procName = System.IO.Path.GetFileNameWithoutExtension(_settings.CustomPath ?? string.Empty);
                    if (!string.IsNullOrEmpty(procName)) StopProcessByName(procName);
                }
                else
                {
                    StopProcessByName("steam");
                }
            }
        }

        private void RunProcessHidden(string file, string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
            }
            catch (Exception ex) { Log($"RunProcessHidden error: {ex.Message}"); }
        }

        private string? GetAudioDeviceID(string keyword)
        {
            // call: Get-AudioDevice -List | Where-Object { $_.Name -like "*keyword*" }
            var script = $"Import-Module AudioDeviceCmdlets -ErrorAction SilentlyContinue; $results = Get-AudioDevice -List | Where-Object {{ $_.Name -like '*{keyword}*' }}; $results | ForEach-Object {{ $_.ID + '||' + $_.Name }}";
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return null;
                var outt = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                if (string.IsNullOrWhiteSpace(outt)) return null;
                var lines = outt.Split(new[] { '\r','\n' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 1)
                {
                    var parts = lines[0].Split(new[] { "||" }, System.StringSplitOptions.None);
                    return parts.Length > 0 ? parts[0] : null;
                }
                Log($"Multiple audio devices match '{keyword}':\n{string.Join('\n', lines)}");
                return null;
            }
            catch (Exception ex) { Log($"GetAudioDeviceID error: {ex.Message}"); return null; }
        }

        private void SetAudioDeviceById(string id)
        {
            var script = $"Import-Module AudioDeviceCmdlets -ErrorAction SilentlyContinue; Set-AudioDevice -ID '{id}'";
            RunPowershellScript(script, 3000);
        }

        private bool TrySetAudioDeviceWithRetries(string id)
        {
            for (int attempt = 1; attempt <= Math.Max(1, _settings.RetryCount); attempt++)
            {
                SetAudioDeviceById(id);
                // small delay to allow OS to apply
                Thread.Sleep(Math.Max(200, _settings.RetryDelaySeconds * 1000));
                if (VerifyAudioDeviceIsDefault(id)) return true;
                Log($"Audio device not yet default, retry {attempt}/{_settings.RetryCount}");
            }
            return false;
        }

        private bool VerifyAudioDeviceIsDefault(string id)
        {
            try
            {
                var script = "Import-Module AudioDeviceCmdlets -ErrorAction SilentlyContinue; (Get-AudioDevice -List | Where-Object { $_.Default -eq $true }) | ForEach-Object { $_.ID }";
                var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                var outt = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                var lines = outt.Split(new[] { '\r','\n' }, System.StringSplitOptions.RemoveEmptyEntries);
                return lines.Any(l => l.Trim().Equals(id, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Log($"VerifyAudioDeviceIsDefault error: {ex.Message}");
                return false;
            }
        }

        private void StopProcessByName(string name)
        {
            var script = $"Get-Process | Where-Object {{ $_.Name -like '{name}' }} | ForEach-Object {{ Stop-Process -Id $_.Id -Force }}";
            RunPowershellScript(script, 2000);
        }

        private void RunPowershellScript(string script, int timeoutMs)
        {
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return;
                var outt = p.StandardOutput.ReadToEnd();
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(timeoutMs);
                if (!string.IsNullOrWhiteSpace(outt)) Log(outt.Trim());
                if (!string.IsNullOrWhiteSpace(err)) Log(err.Trim());
            }
            catch (Exception ex) { Log($"RunPowershellScript error: {ex.Message}"); }
        }
        private class AudioDevice
        {
            public string Id { get; set; } = string.Empty;
            public string Display { get; set; } = string.Empty;
            public override string ToString() => Display;
        }

        private class Settings
        {
            public string ControllerFriendlyName { get; set; } = "Xbox Wireless Controller";
            public string GameAudioDeviceId { get; set; } = string.Empty;
            public string DesktopAudioDeviceId { get; set; } = string.Empty;
            public string SteamPath { get; set; } = string.Empty;
            public string CustomPath { get; set; } = string.Empty;
            public string LaunchMode { get; set; } = "Steam"; // or Custom
            public bool MinimizeToTray { get; set; } = true;
            public bool RunAtStartup { get; set; } = false;
            public bool IsMonitoring { get; set; } = false;
            public string? ProgramArgs { get; set; } = "steam://open/bigpicture";
            public int DebounceSeconds { get; set; } = 1;
            public int RetryCount { get; set; } = 5;
            public int RetryDelaySeconds { get; set; } = 1;
        }
    }
}