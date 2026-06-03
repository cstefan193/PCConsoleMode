using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCConsoleMode
{
    internal static class CrashDumper
    {
        [Flags]
        private enum MiniDumpType : uint
        {
            MiniDumpNormal = 0x00000000,
            MiniDumpWithDataSegs = 0x00000001,
            MiniDumpWithFullMemory = 0x00000002,
            MiniDumpWithHandleData = 0x00000004,
            MiniDumpFilterMemory = 0x00000008,
            MiniDumpScanMemory = 0x00000010,
            MiniDumpWithUnloadedModules = 0x00000020,
            MiniDumpWithIndirectlyReferencedMemory = 0x00000040,
            MiniDumpFilterModulePaths = 0x00000080,
            MiniDumpWithProcessThreadData = 0x00000100,
            MiniDumpWithPrivateReadWriteMemory = 0x00000200,
            MiniDumpWithoutOptionalData = 0x00000400,
            MiniDumpWithFullMemoryInfo = 0x00000800,
            MiniDumpWithThreadInfo = 0x00001000,
            MiniDumpWithCodeSegs = 0x00002000,
            MiniDumpWithoutAuxiliaryState = 0x00004000,
            MiniDumpWithFullAuxiliaryState = 0x00008000,
            MiniDumpWithPrivateWriteCopyMemory = 0x00010000,
            MiniDumpIgnoreInaccessibleMemory = 0x00020000,
            MiniDumpWithTokenInformation = 0x00040000,
            MiniDumpWithModuleHeaders = 0x00080000,
            MiniDumpFilterTriage = 0x00100000
        }

        [DllImport("Dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, SafeFileHandle hFile, MiniDumpType dumpType,
            IntPtr expParam, IntPtr userStreamParam, IntPtr callbackParam);

        public static void WriteDump(Exception? ex = null, string? context = null)
        {
            try
            {
                var baseDir = AppContext.BaseDirectory ?? string.Empty;
                var dumpsDir = Path.Combine(baseDir, "logs", "dumps");
                Directory.CreateDirectory(dumpsDir);
                var file = Path.Combine(dumpsDir, $"dump-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
                using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
                var proc = Process.GetCurrentProcess();
                var hProc = proc.Handle;
                var pid = (uint)proc.Id;
                var type = MiniDumpType.MiniDumpWithDataSegs | MiniDumpType.MiniDumpWithHandleData | MiniDumpType.MiniDumpWithPrivateReadWriteMemory | MiniDumpType.MiniDumpWithThreadInfo;
                bool ok = MiniDumpWriteDump(hProc, pid, fs.SafeFileHandle, type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (ok)
                {
                    Logger.Log($"WriteDump: success -> {file}");
                }
                else
                {
                    var err = Marshal.GetLastWin32Error();
                    Logger.Log($"WriteDump: failed (err={err})");
                }
                if (ex != null) Logger.LogException(ex, context ?? "WriteDump");
            }
            catch (Exception e)
            {
                try { Logger.Log($"WriteDump exception: {e.Message}"); } catch { }
            }
        }
    }
}
