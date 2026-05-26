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
        private readonly string _settingsFile = "settings.json";
        private Settings _settings = new Settings();

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
                IntervalText.Text = _settings.CheckIntervalSeconds.ToString();
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
            var dlg = new OpenFileDialog();
            dlg.Filter = "Executables|*.exe|All files|*.*";
            if (dlg.ShowDialog() == true)
            {
                SteamPathText.Text = dlg.FileName;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            Log("Settings saved");
            StatusText.Text = "Saved";
            Task.Delay(1000).ContinueWith(_ => Dispatcher.Invoke(() => StatusText.Text = _cts != null ? "Running" : "Stopped"));
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
            if (_cts == null)
            {
                _cts = new CancellationTokenSource();
                Task.Run(() => MonitorLoop(_cts.Token));
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
            if (int.TryParse(IntervalText.Text.Trim(), out var iv)) _settings.CheckIntervalSeconds = iv;
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

        private async void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
                StartStopButton.Content = "Start Monitoring";
                StatusText.Text = "Stopped";
                SaveSettings();
                Log("Monitoring stopped by user");
                return;
            }

            SaveSettings();
            _cts = new CancellationTokenSource();
            StartStopButton.Content = "Stop Monitoring";
            StatusText.Text = "Running";
            Log("Monitoring started");
            try
            {
                await Task.Run(() => MonitorLoop(_cts.Token));
            }
            catch (OperationCanceledException) { }
            finally
            {
                _cts = null;
                Dispatcher.Invoke(() => { StartStopButton.Content = "Start Monitoring"; StatusText.Text = "Stopped"; });
            }
        }

        private void MonitorLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                bool bt = GetBtStatus(_settings.ControllerFriendlyName);
                if (bt != _lastBtStatus)
                {
                    Log("Detected change in controller presence");
                    _lastBtStatus = bt;
                    try { SwitchMode(bt); }
                    catch (Exception ex) { Log($"SwitchMode error: {ex.Message}"); }
                }
                else
                {
                    Log("No change..");
                }
                Thread.Sleep(TimeSpan.FromSeconds(_settings.CheckIntervalSeconds));
            }
        }

        private bool GetBtStatus(string friendlyName)
        {
            // Use PowerShell Get-PnpDevice to check
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"try {{ (Get-PnpDevice -Class Bluetooth -FriendlyName '{friendlyName}') -ne $null }} catch {{ $false }}\"")
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
                var outt = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                return outt.Contains("True");
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
                    RunProcessHidden(_settings.SteamPath, "steam://open/bigpicture");
                }
            }
            else
            {
                Log("Launching desktop mode...");
                var desktopId = !string.IsNullOrEmpty(_settings.DesktopAudioDeviceId) ? _settings.DesktopAudioDeviceId : GetAudioDeviceID("Headphones");
                if (desktopId != null) SetAudioDeviceById(desktopId);
                RunProcessHidden("DisplaySwitch.exe", "/internal");
                StopProcessByName("steam");
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
            public int CheckIntervalSeconds { get; set; } = 5;
        }
    }
}