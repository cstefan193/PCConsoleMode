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
        // FIX #10: Track whether this instance owns the mutex so we only release it if we acquired it.
        private bool _ownsInstanceMutex = false;
        // FIX #1: CancellationTokenSource to cleanly stop the activation listener loop on exit.
        private CancellationTokenSource _activationCts = new CancellationTokenSource();

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
                try { CrashDumper.WriteDump(ev.Exception, "DispatcherUnhandledException"); } catch { }
                // do not swallow by default; allow default behavior (app will still crash)
                ev.Handled = false;
            };

            const string mutexName = "PCConsoleMode_SingleInstanceMutex_v1";
            const string activationEventName = "PCConsoleMode_ActivationEvent_v1";
            try
            {
                bool createdNew;
                _instanceMutex = new Mutex(true, mutexName, out createdNew);

                if (!createdNew)
                {
                    // FIX #10: This instance did NOT acquire the mutex, so don't try to release it.
                    // Dispose the handle but leave _ownsInstanceMutex = false.
                    _instanceMutex.Dispose();
                    _instanceMutex = null;

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

                // FIX #10: We created the mutex and own it.
                _ownsInstanceMutex = true;

                // First instance: create the activation event and start listening
                try
                {
                    _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activationEventName, out _);
                    var token = _activationCts.Token;

                    // FIX #1/#3: Use cancellation token to stop the loop cleanly on exit,
                    // instead of relying on _activationEvent being nulled out mid-wait.
                    Task.Run(() => {
                        try
                        {
                            while (!token.IsCancellationRequested)
                            {
                                // FIX #3: Use a timeout so the loop can check cancellation periodically
                                // rather than blocking forever on WaitOne().
                                bool signaled = _activationEvent?.WaitOne(500) ?? false;
                                if (token.IsCancellationRequested) break;
                                if (!signaled) continue;

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
                                                if (mw.WindowState == System.Windows.WindowState.Minimized)
                                                    mw.WindowState = System.Windows.WindowState.Normal;
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
                        catch (OperationCanceledException) { }
                        catch { }
                    }, token);
                }
                catch { }
            }
            catch
            {
                // If creating/opening mutex fails for any reason, allow startup to continue
            }

            base.OnStartup(e);

            // FIX #1 (App.xaml): StartupUri was removed from App.xaml so WPF no longer
            // auto-creates MainWindow before OnStartup runs. We create it manually here,
            // after the single-instance check, so the second-instance path (Shutdown above)
            // exits cleanly without ever constructing a window.
            var window = new MainWindow();
            this.MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // FIX #1: Cancel the activation listener loop cleanly before disposing the event.
                _activationCts.Cancel();

                if (_instanceMutex != null)
                {
                    // FIX #10: Only release the mutex if this instance actually acquired it.
                    // Calling ReleaseMutex() without ownership throws ApplicationException.
                    if (_ownsInstanceMutex)
                    {
                        try { _instanceMutex.ReleaseMutex(); } catch { }
                    }
                    _instanceMutex.Dispose();
                    _instanceMutex = null;
                }
                if (_activationEvent != null)
                {
                    // FIX #1: No longer Set() the event on exit — the cancellation token
                    // stops the loop. Setting it here was signaling the loop to "activate"
                    // the window during shutdown, which was unintentional.
                    _activationEvent.Dispose();
                    _activationEvent = null;
                }
                _activationCts.Dispose();
            }
            catch { }
            base.OnExit(e);
        }
    }
}
