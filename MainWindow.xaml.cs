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
using ComboBox = System.Windows.Controls.ComboBox;
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
            // If configured to run at startup (either saved or registry), schedule start-minimized behavior
            if ((_settings.RunAtStartup || RegistryRunKeyExists()) && _settingsLoaded)
            {
                // remember whether we should minimize on startup (either saved preference or --minimized flag)
                _startMinimized = _settings.MinimizeToTray || cmdMinimized;
                // If RunAtStartup is enabled, ensure monitoring is started on launch
                if (_settings.RunAtStartup || _settings.IsMonitoring)
                {
                    _settings.IsMonitoring = true;
                    StartMonitoringIfNeeded();
                    // persist that monitoring should be active
                    SaveSettings();
                }
            }

            // perform minimize-to-tray after the window has loaded to avoid being shown briefly
            this.Loaded += MainWindow_Loaded;
        }

        // FIX #7: Cache pwsh availability so we don't spawn 'where pwsh' on every PS call
        private static readonly string _powershellExe = DetectPowershellExe();
        private static string DetectPowershellExe()
        {
            try
            {
                var which = Process.Start(new ProcessStartInfo("where", "pwsh")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (which != null)
                {
                    var wout = which.StandardOutput.ReadToEnd();
                    which.WaitForExit(500);
                    if (!string.IsNullOrWhiteSpace(wout)) return "pwsh";
                }
            }
            catch { }
            return "powershell";
        }

        private string RunPowershellAndGetOutput(string command, int timeoutMs)
        {
            var args = $"-NoProfile -Command \"{command}\"";
            var psi = new ProcessStartInfo(_powershellExe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return string.Empty;

                // FIX #6: Read streams asynchronously to prevent pipe-buffer deadlock.
                // Reading stdout/stderr only after WaitForExit can deadlock if the child
                // writes more data than fits in the OS pipe buffer (~4 KB).
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                p.WaitForExit(timeoutMs);
                var outt = outTask.Result;
                var err = errTask.Result;
                if (!string.IsNullOrWhiteSpace(err)) Log(err.Trim());
                return outt ?? string.Empty;
            }
            catch (Exception ex)
            {
                Log($"RunPowershellAndGetOutput error: {ex.Message}");
                return string.Empty;
            }
        }

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (_startMinimized)
                {
                    MinimizeToTray();
                }
            }
            catch (Exception ex) { Log($"MainWindow_Loaded error: {ex.Message}"); }
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
            catch (Exception ex) { Log($"RunAtStartupCheck_Checked error: {ex.Message}"); }
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
            catch (Exception ex) { Log($"RunAtStartupCheck_Unchecked error: {ex.Message}"); }
        }

        // FIX #1/#13: _cts was declared but never used (leftover from old polling approach). Removed.
        private bool _lastBtStatus = false;
        private ManagementEventWatcher? _watcher;
        // Use an absolute path in the application's base directory so the
        // settings file is found even when the process current directory is
        // different (e.g. launched by the Run registry key at login).
        private readonly string _settingsFile = System.IO.Path.Combine(AppContext.BaseDirectory ?? string.Empty, "settings.json");
        private Settings _settings = new Settings();
        private bool _settingsLoaded = false;
        private bool _suppressUiEvents = false;
        private DateTime _lockUntil = DateTime.MinValue;
        private bool _advancedVisible = false;
        private bool _startMinimized = false;

        private void BindSettingsToUi()
        {
            Dispatcher.Invoke(() => {
                // select controller in combo if present; if the combo hasn't been populated yet
                // ensure the saved friendly name is visible so the user sees their saved preference.
                if (!string.IsNullOrEmpty(_settings.ControllerFriendlyName))
                {
                    if (ControllerCombo.ItemsSource is IEnumerable<string> items)
                    {
                        var match = items.FirstOrDefault(s => s == _settings.ControllerFriendlyName);
                        if (match != null)
                        {
                            ControllerCombo.SelectedItem = match;
                        }
                        else
                        {
                            // Items exist but don't contain the saved name; prepend it so it's visible.
                            var list = new System.Collections.Generic.List<string>(items);
                            list.Insert(0, _settings.ControllerFriendlyName);
                            ControllerCombo.ItemsSource = list;
                            ControllerCombo.IsEnabled = true;
                            ControllerCombo.SelectedItem = _settings.ControllerFriendlyName;
                        }
                    }
                    else
                    {
                        // ItemsSource not set yet (loading); show the saved name so UI isn't empty.
                        ControllerCombo.ItemsSource = new System.Collections.Generic.List<string> { _settings.ControllerFriendlyName };
                        ControllerCombo.IsEnabled = true;
                        ControllerCombo.SelectedItem = _settings.ControllerFriendlyName;
                    }
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

        // FIX #11: RegistryRunKeyExists no longer logs on every call to avoid flooding the log.
        // Verbose registry logging was happening during BindSettingsToUi and UpdateAutoStartIndicator.
        private bool RegistryRunKeyExists()
        {
            try
            {
                var runKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false);
                if (runKey == null) return false;
                return runKey.GetValue("PCConsoleMode") != null;
            }
            catch { return false; }
        }

        private void UpdateAutoStartIndicator()
        {
            Dispatcher.Invoke(() => {
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
                catch (Exception ex)
                {
                    // FIX #12: Log deserialization errors instead of swallowing them silently.
                    // The file may be malformed (e.g. manual edit). Reset to defaults and continue.
                    Logger.Log($"LoadSettings: failed to deserialize settings, resetting to defaults. Error: {ex.Message}");
                    _settings = new Settings();
                }
            }
        }

        private void PopulateBtDevices()
        {
            SetComboLoading(ControllerCombo, true);
            Task.Run(() => {
                try
                {
                    var script = "Get-PnpDevice -Class Bluetooth | Select-Object -ExpandProperty FriendlyName";
                    var outt = RunPowershellAndGetOutput(script, 2000);
                    var lines = outt.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim())
                                    .Where(s => !string.IsNullOrEmpty(s))
                                    .ToList();
                    Dispatcher.Invoke(() => {
                        // Preserve the user's saved controller choice even if the device isn't currently detected.
                        // If the saved friendly name is missing from the discovered list, insert it so it remains selected.
                        var items = new System.Collections.Generic.List<string>(lines);
                        if (!string.IsNullOrEmpty(_settings.ControllerFriendlyName) && !items.Contains(_settings.ControllerFriendlyName))
                        {
                            items.Insert(0, _settings.ControllerFriendlyName);
                        }
                        ControllerCombo.ItemsSource = items;
                        ControllerCombo.IsEnabled = true;
                        if (!string.IsNullOrEmpty(_settings.ControllerFriendlyName))
                            ControllerCombo.SelectedItem = _settings.ControllerFriendlyName;
                    });
                }
                catch (Exception ex)
                {
                    Log($"PopulateBtDevices error: {ex.Message}");
                    Logger.LogException(ex, "PopulateBtDevices");
                    Dispatcher.Invoke(() => ControllerCombo.IsEnabled = true);
                }
            });
        }

        private void RefreshBtButton_Click(object sender, RoutedEventArgs e)
        {
            PopulateBtDevices();
        }

        private void PopulateAudioDevices()
        {
            SetComboLoading(GameAudioCombo, true);
            SetComboLoading(DesktopAudioCombo, true);
            Task.Run(() => {
                try
                {
                    var script = "Import-Module AudioDeviceCmdlets -ErrorAction SilentlyContinue; Get-AudioDevice -List | ForEach-Object { $_.ID + '||' + $_.Name }";
                    var outt = RunPowershellAndGetOutput(script, 3000);
                    var lines = outt.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim())
                                    .Where(s => s.Contains("||"))
                                    .Select(s => {
                                        var parts = s.Split(new[] { "||" }, System.StringSplitOptions.None);
                                        return new AudioDevice
                                        {
                                            Id = parts.Length > 0 ? parts[0] : string.Empty,
                                            Display = parts.Length > 1 ? parts[1] : parts.FirstOrDefault() ?? string.Empty
                                        };
                                    }).ToList();
                    Dispatcher.Invoke(() => {
                        // Preserve user's saved audio device selections even if they aren't present during enumeration.
                        var audioItems = new System.Collections.Generic.List<AudioDevice>(lines);
                        // Always offer an explicit "None" selection that maps to an empty ID.
                        audioItems.Insert(0, new AudioDevice { Id = string.Empty, Display = "(None - use system default)" });
                        if (!string.IsNullOrEmpty(_settings.GameAudioDeviceId) && !audioItems.Any(a => a.Id == _settings.GameAudioDeviceId))
                        {
                            audioItems.Insert(0, new AudioDevice { Id = _settings.GameAudioDeviceId, Display = "(Saved - not present) " + _settings.GameAudioDeviceId });
                        }
                        if (!string.IsNullOrEmpty(_settings.DesktopAudioDeviceId) && !audioItems.Any(a => a.Id == _settings.DesktopAudioDeviceId))
                        {
                            audioItems.Insert(0, new AudioDevice { Id = _settings.DesktopAudioDeviceId, Display = "(Saved - not present) " + _settings.DesktopAudioDeviceId });
                        }
                        GameAudioCombo.ItemsSource = audioItems;
                        GameAudioCombo.IsEnabled = true;
                        DesktopAudioCombo.ItemsSource = audioItems;
                        DesktopAudioCombo.IsEnabled = true;
                    });
                }
                catch (Exception ex)
                {
                    Log($"PopulateAudioDevices error: {ex.Message}");
                    Logger.LogException(ex, "PopulateAudioDevices");
                    Dispatcher.Invoke(() => { GameAudioCombo.IsEnabled = true; DesktopAudioCombo.IsEnabled = true; });
                }
            });
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
            // FIX #8: Check the Steam registry key first, which works regardless of install location.
            try
            {
                var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", false);
                if (steamKey != null)
                {
                    var regPath = steamKey.GetValue("SteamExe") as string;
                    if (!string.IsNullOrWhiteSpace(regPath) && File.Exists(regPath))
                        return regPath;
                }
            }
            catch { }

            // Fallback to common install locations
            try
            {
                var candidates = new[]
                {
                    @"C:\Program Files (x86)\Steam\steam.exe",
                    @"C:\Program Files\Steam\steam.exe"
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
                SetStatus("Saved", System.Windows.Media.Brushes.DarkBlue);
                Task.Delay(1000).ContinueWith(_ => SetStatus(
                    _watcher is not null ? "Running" : "Stopped",
                    _watcher is not null ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Gray));
            }
            catch (Exception ex)
            {
                Log($"SaveButton_Click error: {ex.Message}");
                Logger.LogException(ex, "SaveButton_Click");
                // FIX #8 (CrashDumper): Don't write a crash dump for a settings-save failure —
                // that's too heavy-handed for a non-fatal error. Log it and show status instead.
                SetStatus("Error saving settings", System.Windows.Media.Brushes.Red);
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

        private void StartMonitoringIfNeeded()
        {
            if (_watcher is null)
            {
                StartWatcher();
                Dispatcher.Invoke(() => { StartStopButton.Content = "Stop Monitoring"; SetStatus("Running", System.Windows.Media.Brushes.Green); });
                Log("Monitoring started (background)");
            }
        }

        // FIX #1: MinimizeToTray now delegates icon setup to EnsureNotifyIconCreated,
        // eliminating the duplicate setup code that caused ghost event handler registrations
        // when MinimizeToTray was called more than once.
        private void MinimizeToTray()
        {
            EnsureNotifyIconCreated();
            _isBackground = true;
            this.Hide();
        }

        // FIX #1: Single authoritative method for NotifyIcon creation and setup.
        // The early return guard (if _notifyIcon != null) ensures handlers are only
        // registered once, no matter how many times this is called.
        private void EnsureNotifyIconCreated()
        {
            if (_notifyIcon != null) return;
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Text = "PCConsoleMode";
            try
            {
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
            var openItem = new System.Windows.Forms.ToolStripMenuItem("Open", null, (s, e) => Dispatcher.Invoke(RestoreFromTray));
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit", null, (s, e) => Dispatcher.Invoke(() =>
            {
                _notifyIcon.Visible = false;
                System.Windows.Application.Current.Shutdown();
            }));
            menu.Items.Add(openItem);
            menu.Items.Add(exitItem);
            _notifyIcon.ContextMenuStrip = menu;
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
            // dispose notify icon if present
            if (_notifyIcon != null)
            {
                try { _notifyIcon.Visible = false; _notifyIcon.Dispose(); } catch { }
                _notifyIcon = null;
            }
            base.OnClosing(e);
        }

        // FIX #13: SaveSettings reads UI controls and must only be called from the UI thread.
        // SaveSettings() is private; callers from background threads (StartWatcher/StopWatcher)
        // should continue to call it only while on the UI thread (which they currently do).
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
            if (int.TryParse(IntervalText.Text.Trim(), out var iv)) _settings.DebounceSeconds = Math.Max(0, iv);
            if (int.TryParse(RetryCountText.Text.Trim(), out var rc)) _settings.RetryCount = Math.Max(1, rc);
            if (int.TryParse(RetryDelayText.Text.Trim(), out var rd)) _settings.RetryDelaySeconds = Math.Max(1, rd);
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

        // FIX #3: Update StatusText color alongside its text so state is immediately obvious.
        private void SetStatus(string text, System.Windows.Media.Brush color)
        {
            Dispatcher.Invoke(() => {
                StatusText.Text = text;
                StatusText.Foreground = color;
            });
        }

        // FIX #5: Block non-digit keypresses in numeric TextBoxes.
        private void NumericTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        // FIX #7: Trim log TextBox to a maximum number of lines to prevent unbounded memory growth
        // when monitoring runs for extended periods.
        private const int MaxLogLines = 500;
        private void LogText_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var lines = LogText.LineCount;
                if (lines > MaxLogLines)
                {
                    // Remove the oldest lines to bring count back to the limit.
                    // GetCharacterIndexFromLineIndex gives us the char offset of the line
                    // we want to keep as the new first line.
                    int trimToIndex = LogText.GetCharacterIndexFromLineIndex(lines - MaxLogLines);
                    LogText.Text = LogText.Text.Substring(trimToIndex);
                    LogText.ScrollToEnd();
                }
            }
            catch { }
        }

        // FIX #9: Helpers to show/hide the "Loading…" state on the ComboBoxes.
        // Note: WPF forbids mixing ItemsSource with direct Items manipulation.
        // We therefore only disable the combo while loading and rely on the
        // placeholder items defined in XAML — we never touch Items from code.
        private void SetComboLoading(ComboBox combo, bool loading)
        {
            Dispatcher.Invoke(() => {
                if (loading)
                {
                    combo.ItemsSource = null;
                    combo.IsEnabled = false;
                }
                // Re-enabling happens in the caller once ItemsSource is set.
            });
        }

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_watcher != null)
            {
                StopWatcher();
                StartStopButton.Content = "Start Monitoring";
                SetStatus("Stopped", System.Windows.Media.Brushes.Gray);
                SaveSettings(true);
                Log("Monitoring stopped by user");
                return;
            }

            SaveSettings(true);
            StartWatcher();
            StartStopButton.Content = "Stop Monitoring";
            SetStatus("Running", System.Windows.Media.Brushes.Green);
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

            // FIX #5: Set debounce lock BEFORE calling SwitchMode.
            // SwitchMode can take several seconds (audio retries), during which another
            // WMI event could arrive and slip through the old lock check.
            _lockUntil = DateTime.UtcNow.AddSeconds(Math.Max(0, _settings.DebounceSeconds));

            try { SwitchMode(bt); }
            catch (Exception ex) { Log($"SwitchMode error: {ex.Message}"); }
        }

        private bool GetBtStatus(string friendlyName)
        {
            // Use PowerShell to read the PnP device connection property
            if (string.IsNullOrWhiteSpace(friendlyName)) friendlyName = "Xbox Wireless Controller";
            // escape single quotes
            var fnEsc = friendlyName.Replace("'", "''");
            var script = $"try {{ (Get-PnpDevice -Class Bluetooth -FriendlyName '{fnEsc}' | Get-PnpDeviceProperty -KeyName '{{83DA6326-97A6-4088-9453-A1923F573B29}} 15' | Select -ExpandProperty Data) }} catch {{ $false }}";
            try
            {
                var outt = RunPowershellAndGetOutput(script, 2000).Trim();
                if (string.IsNullOrEmpty(outt)) return false;
                if (bool.TryParse(outt, out var b)) return b;
                if (int.TryParse(outt, out var i)) return i != 0;
                return outt.Equals("OK", System.StringComparison.OrdinalIgnoreCase) ||
                       outt.Equals("True", System.StringComparison.OrdinalIgnoreCase);
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
                // If user explicitly chose None (empty string), skip audio switching for game mode
                if (_settings.GameAudioDeviceId != null && _settings.GameAudioDeviceId == string.Empty)
                {
                    Log("Game audio switching skipped (user selected None).");
                }
                else if (!string.IsNullOrEmpty(_settings.GameAudioDeviceId))
                {
                    // If user selected a specific audio device, prefer that
                    deviceId = _settings.GameAudioDeviceId;
                }
                else
                {
                    // FIX #10: Fallback audio search used personal device names (Beyond, SONY, HDMI).
                    // Now only searches for HDMI which is a generic term. Users should configure
                    // a specific audio device in settings to avoid this fallback entirely.
                    int retries = 1;
                    while (retries <= 10 && deviceId == null)
                    {
                        deviceId = GetAudioDeviceID("HDMI");
                        if (deviceId != null) break;
                        Log($"retrying audio device.. ({retries})");
                        Thread.Sleep(1000);
                        retries++;
                    }
                }
                if (deviceId == null)
                {
                    // If no audio device is found (or user skipped), log a warning but continue launching the game as requested.
                    Log("Warning: No suitable game audio device found or switching skipped. Continuing to launch game without switching audio.");
                }
                else
                {
                    if (!TrySetAudioDeviceWithRetries(deviceId))
                    {
                        // Do not throw — log and continue to launch the game even if switching audio failed.
                        Log("Warning: Failed to set game audio device after retries. Continuing to launch game.");
                    }
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
                // Skip desktop audio switching if user explicitly selected None (empty string)
                if (_settings.DesktopAudioDeviceId != null && _settings.DesktopAudioDeviceId == string.Empty)
                {
                    Log("Desktop audio switching skipped (user selected None).");
                }
                else
                {
                    var desktopId = !string.IsNullOrEmpty(_settings.DesktopAudioDeviceId)
                        ? _settings.DesktopAudioDeviceId
                        : GetAudioDeviceID("Headphones");
                    if (desktopId != null)
                    {
                        if (!TrySetAudioDeviceWithRetries(desktopId))
                        {
                            Log("Warning: failed to set desktop audio device after retries.");
                        }
                    }
                }
                RunProcessHidden("DisplaySwitch.exe", "/internal");
                // FIX #9: Only stop Steam/custom process if this app was the one that launched it.
                // Previously this would kill Steam even if it was already running before console mode.
                if (_settings.LaunchMode == "Custom")
                {
                    var procName = System.IO.Path.GetFileNameWithoutExtension(_settings.CustomPath ?? string.Empty);
                    if (!string.IsNullOrEmpty(procName)) StopProcessByName(procName);
                }
                else if (_steamLaunchedByUs)
                {
                    StopProcessByName("steam");
                }
            }
        }

        // FIX #9: Track whether this app launched Steam so we only kill it if we started it.
        private bool _steamLaunchedByUs = false;

        private void RunProcessHidden(string file, string args)
        {
            try
            {
                if (File.Exists(file))
                {
                    Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
                    // If this is the configured Steam/custom path, record that we launched it
                    if (file.Equals(_settings.SteamPath, StringComparison.OrdinalIgnoreCase) ||
                        file.Equals(_settings.CustomPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _steamLaunchedByUs = true;
                    }
                    return;
                }
                // Fallback: try starting via shell (useful for URL schemes like steam://)
                Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
            }
            catch (Exception ex) { Log($"RunProcessHidden error: {ex.Message}"); }
        }

        private string? GetAudioDeviceID(string keyword)
        {
            var script = $"Import-Module AudioDeviceCmdlets -ErrorAction SilentlyContinue; $results = Get-AudioDevice -List | Where-Object {{ $_.Name -like '*{keyword}*' }}; $results | ForEach-Object {{ $_.ID + '||' + $_.Name }}";
            try
            {
                var outt = RunPowershellAndGetOutput(script, 3000);
                if (string.IsNullOrWhiteSpace(outt)) return null;
                var lines = outt.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
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
            RunPowershellAndGetOutput(script, 3000);
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
                var outt = RunPowershellAndGetOutput(script, 2000);
                var lines = outt.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
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
            RunPowershellAndGetOutput(script, 2000);
        }

        // NOTE: RunPowershellScript was deprecated and unused — removed.

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
