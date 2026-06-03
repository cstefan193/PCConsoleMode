using System;
using System.IO;
using System.Text;

namespace PCConsoleMode
{
    internal static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logDir = "logs";
        private static string _logFile = string.Empty;

        public static void Init()
        {
            try
            {
                if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);
                _logFile = Path.Combine(_logDir, "app-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                Log("Logger initialized");
            }
            catch { }
        }

        public static void Log(string message)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}" + Environment.NewLine;
                lock (_lock)
                {
                    File.AppendAllText(_logFile, line, Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void LogException(Exception ex, string? context = null)
        {
            try
            {
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
                    File.AppendAllText(_logFile, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
