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
            LoadSettings();
            PopulateBtDevices();
            BindSettingsToUi();
        }

        private CancellationTokenSource? _cts;
        private bool _lastBtStatus = false;
        private ManagementEventWatcher? _watcher;
        private readonly string _settingsFile = "settings.json";
        private Settings _settings = new Settings();
        private DateTime _lockUntil = DateTime.MinValue;

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
                SteamPathText.Text = _settings.SteamPath;
                // Program choice
                ProgramChoice.SelectedIndex = (_settings.LaunchMode ?? "Steam") == "Custom" ? 1 : 0;
                ProgramArgsText.Text = _settings.ProgramArgs ?? string.Empty;
                IntervalText.Text = _settings.DebounceSeconds.ToString();
            });
        }

        private void LoadSettings()
        {
            if (File.Exists(_settingsFile))
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
            var choice = (ProgramChoice.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (choice == "Steam")
            {
                // default steam path and args
                if (string.IsNullOrWhiteSpace(SteamPathText.Text)) SteamPathText.Text = "C:\\Program Files (x86)\\Steam\\steam.exe";
                ProgramArgsText.Text = "steam://open/bigpicture";
            }
            else
            {
                // custom clears args by default
                if (ProgramArgsText.Text == "steam://open/bigpicture") ProgramArgsText.Text = string.Empty;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            Log("Settings saved");
            StatusText.Text = "Saved";
            Task.Delay(1000).ContinueWith(_ => Dispatcher.Invoke(() => StatusText.Text = _watcher is not null ? "Running" : "Stopped"));
        }

        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isBackground = false;

        private void BackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isBackground)
            {
                EnableStartup();
                StartMonitoringIfNeeded();
                MinimizeToTray();
            }
            else
            {
                DisableStartup();
                RestoreFromTray();
            }
        }

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
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += (s, e) => Dispatcher.Invoke(RestoreFromTray);

            var menu = new System.Windows.Forms.ContextMenuStrip();
            var openItem = new System.Windows.Forms.ToolStripMenuItem("Open", null, (s,e)=> Dispatcher.Invoke(RestoreFromTray));
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit", null, (s,e)=> Dispatcher.Invoke(() => { _notifyIcon.Visible = false; System.Windows.Application.Current.Shutdown(); }));
            menu.Items.Add(openItem);
            menu.Items.Add(exitItem);
            _notifyIcon.ContextMenuStrip = menu;

            _isBackground = true;
            BackgroundButton.Content = "Stop Background";
            this.Hide();
        }

        private void RestoreFromTray()
        {
            _isBackground = false;
            BackgroundButton.Content = "Run in Background";
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            if (_notifyIcon != null) _notifyIcon.Visible = true;
        }

        private void EnableStartup()
        {
            try
            {
                var runKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                var exePath = Assembly.GetEntryAssembly()?.Location ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null) runKey.SetValue("PCConsoleMode", '"' + exePath + '"');
            }
            catch (Exception ex) { Log($"EnableStartup error: {ex.Message}"); }
        }

        private void DisableStartup()
        {
            try
            {
                var runKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                runKey.DeleteValue("PCConsoleMode", false);
            }
            catch (Exception ex) { Log($"DisableStartup error: {ex.Message}"); }
        }

        private void SaveSettings()
        {
            _settings.ControllerFriendlyName = (ControllerCombo.SelectedItem as string) ?? string.Empty;
            _settings.GameAudioDeviceId = (GameAudioCombo.SelectedValue as string) ?? string.Empty;
            _settings.DesktopAudioDeviceId = (DesktopAudioCombo.SelectedValue as string) ?? string.Empty;
            _settings.SteamPath = SteamPathText.Text.Trim();
            _settings.LaunchMode = (ProgramChoice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Steam";
            _settings.ProgramArgs = ProgramArgsText.Text.Trim();
            if (int.TryParse(IntervalText.Text.Trim(), out var iv)) _settings.DebounceSeconds = iv;
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFile, json);
        }

        private void Log(string msg)
        {
            Dispatcher.Invoke(() => {
                LogText.AppendText($"{DateTime.Now:HH:mm:ss} - {msg}\n");
                LogText.ScrollToEnd();
            });
        }

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_watcher != null)
            {
                StopWatcher();
                StartStopButton.Content = "Start Monitoring";
                StatusText.Text = "Stopped";
                SaveSettings();
                Log("Monitoring stopped by user");
                return;
            }

            SaveSettings();
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
                SetAudioDeviceById(deviceId);
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
                if (desktopId != null) SetAudioDeviceById(desktopId);
                RunProcessHidden("DisplaySwitch.exe", "/internal");
                // stop steam or custom process if configured
                if (_settings.LaunchMode == "Custom")
                {
                    var procName = System.IO.Path.GetFileNameWithoutExtension(_settings.SteamPath ?? string.Empty);
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
            public string SteamPath { get; set; } = "C:\\Program Files (x86)\\Steam\\steam.exe";
            public string LaunchMode { get; set; } = "Steam"; // or Custom
            public string? ProgramArgs { get; set; } = "steam://open/bigpicture";
            public int DebounceSeconds { get; set; } = 1;
        }
    }
}