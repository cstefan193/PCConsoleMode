using System;
using System.IO;
using System.Text;

namespace PCConsoleMode
{
    internal static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logDir = string.Empty;
        private static string _logFile = string.Empty;

        public static void Init()
        {
            try
            {
                // Use application base directory for logs so paths are consistent when started from Run registry
                _logDir = Path.Combine(AppContext.BaseDirectory ?? string.Empty, "logs");
                if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);

                // FIX #5: Use a daily log file instead of a per-session timestamped file.
                // Per-session files accumulate rapidly for a tray app restarted at every login.
                _logFile = Path.Combine(_logDir, "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

                // FIX #5: Delete log files older than 7 days to prevent unbounded growth.
                PruneOldLogs(_logDir, maxAgeDays: 7);

                Log("Logger initialized");
            }
            catch { }
        }

        private static void PruneOldLogs(string dir, int maxAgeDays)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-maxAgeDays);
                foreach (var file in Directory.GetFiles(dir, "app-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                            File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // FIX #4: Shared helper that ensures _logFile is populated before any write attempt.
        // Both Log() and LogException() call this so neither silently drops output when
        // Init() was never called or failed.
        private static void EnsureLogFileSet()
        {
            if (!string.IsNullOrEmpty(_logFile)) return;
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory ?? string.Empty, "logs");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _logFile = Path.Combine(dir, "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            }
            catch
            {
                _logFile = Path.Combine(Path.GetTempPath(), "PCConsoleMode.log");
            }
        }

        public static void Log(string message)
        {
            try
            {
                EnsureLogFileSet();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}" + Environment.NewLine;
                lock (_lock)
                {
                    if (!string.IsNullOrEmpty(_logFile)) File.AppendAllText(_logFile, line, Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void LogException(Exception ex, string? context = null)
        {
            try
            {
                // FIX #4: Ensure _logFile is set before writing, just as Log() does.
                EnsureLogFileSet();

                var sb = new StringBuilder();
                sb.AppendLine("----- UNHANDLED EXCEPTION -----");
                if (!string.IsNullOrEmpty(context)) sb.AppendLine("Context: " + context);
                var e = ex;
                int depth = 0;
                while (e != null && depth < 10)
                {
                    sb.AppendLine($"Exception Type: {e.GetType().FullName}");
                    sb.AppendLine($"Message: {e.Message}");
                    sb.AppendLine("StackTrace:");
                    sb.AppendLine(e.StackTrace ?? "(no stack)");
                    sb.AppendLine("---");
                    e = e.InnerException;
                    depth++;
                }
                sb.AppendLine("-------------------------------");
                lock (_lock)
                {
                    if (!string.IsNullOrEmpty(_logFile))
                        File.AppendAllText(_logFile, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
