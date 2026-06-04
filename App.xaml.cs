using System.Configuration;
using System.Data;
using System.Windows;
using System.Threading;
using System;
using System.Threading.Tasks;


namespace PCConsoleMode
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private Mutex? _instanceMutex;
        private EventWaitHandle? _activationEvent;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(System.IntPtr hWnd);

        protected override void OnStartup(StartupEventArgs e)
        {
            // initialize file logger and global exception handlers
            Logger.Init();
            AppDomain.CurrentDomain.UnhandledException += (s, ev) => {
                try { if (ev.ExceptionObject is Exception ex) Logger.LogException(ex, "AppDomain.UnhandledException"); }
                catch (Exception ex) { Logger.Log($"AppDomain.UnhandledException handler error: {ex.Message}"); }
            };
            TaskScheduler.UnobservedTaskException += (s, ev) => {
                try { Logger.LogException(ev.Exception, "TaskScheduler.UnobservedTaskException"); ev.SetObserved(); }
                catch (Exception ex) { Logger.Log($"TaskScheduler.UnobservedTaskException handler error: {ex.Message}"); }
            };
            // WPF UI-thread unhandled exceptions
            this.DispatcherUnhandledException += (s, ev) => {
                try { Logger.LogException(ev.Exception, "DispatcherUnhandledException"); }
                catch (Exception ex) { Logger.Log($"DispatcherUnhandledException handler error: {ex.Message}"); }
                // do not swallow by default; allow default behavior (app will still crash)
                try { CrashDumper.WriteDump(ev.Exception, "DispatcherUnhandledException"); } catch { }
                ev.Handled = false;
            };

            const string mutexName = "PCConsoleMode_SingleInstanceMutex_v1";
            const string activationEventName = "PCConsoleMode_ActivationEvent_v1";
            try
            {
                bool createdNew;
                // Create a named mutex to enforce single instance per user
                _instanceMutex = new Mutex(true, mutexName, out createdNew);
                if (!createdNew)
                {
                    // Another instance is already running; signal it to activate and exit
                    try
                    {
                        var existingEvent = EventWaitHandle.OpenExisting(activationEventName);
                        existingEvent.Set();
                    }
                    catch { }
                    Shutdown();
                    return;
                }

                // First instance: create the activation event and start listening
                try
                {
                    bool createdEvent;
                    _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activationEventName, out createdEvent);
                    // Listen for activation signals in background
                    Task.Run(() => {
                        try
                        {
                            while (_activationEvent != null)
                            {
                                _activationEvent.WaitOne();
                                Dispatcher.Invoke(() => {
                                    var mw = this.MainWindow ?? System.Windows.Application.Current?.MainWindow;
                                    if (mw != null)
                                    {
                                        try
                                        {
                                            if (mw is PCConsoleMode.MainWindow main)
                                            {
                                                // Use the MainWindow helper so internal state (_isBackground) is reset correctly
                                                main.ActivateFromExternal();
                                            }
                                            else
                                            {
                                                mw.Show();
                                                if (mw.WindowState == System.Windows.WindowState.Minimized) mw.WindowState = System.Windows.WindowState.Normal;
                                                mw.Activate();
                                                var hwnd = new System.Windows.Interop.WindowInteropHelper(mw).Handle;
                                                if (hwnd != System.IntPtr.Zero) SetForegroundWindow(hwnd);
                                            }
                                        }
                                        catch { }
                                    }
                                });
                            }
                        }
                        catch { }
                    });
                }
                catch { }
            }
            catch
            {
                // If creating/opening mutex fails for any reason, allow startup to continue
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_instanceMutex != null)
                {
                    try { _instanceMutex.ReleaseMutex(); } catch { }
                    _instanceMutex.Dispose();
                    _instanceMutex = null;
                }
                if (_activationEvent != null)
                {
                    try { _activationEvent.Set(); } catch { }
                    _activationEvent.Dispose();
                    _activationEvent = null;
                }
            }
            catch { }
            base.OnExit(e);
        }
    }

}
