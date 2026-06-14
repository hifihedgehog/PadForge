using System;
using System.IO;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Throwaway diagnostic log for the Remote Link reverse feedback channel
    /// (issue #138 M2). Appends to %TEMP%\padforge-remotelink.log so a 2-PC test
    /// can show exactly which hop a feedback frame stops at (capture -> route ->
    /// send -> receive -> apply), with the known-working input counters as a
    /// same-window positive control. Cheap and lock-serialized; remove once the
    /// reverse path is confirmed on hardware.
    /// </summary>
    internal static class RemoteLinkDiag
    {
        private static readonly object _lock = new();
        private static string _path;

        public static bool Enabled = true;

        public static void Log(string msg)
        {
            if (!Enabled) return;
            try
            {
                _path ??= Path.Combine(Path.GetTempPath(), "padforge-remotelink.log");
                lock (_lock)
                    File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
            catch { /* logging must never throw into the pipeline */ }
        }
    }
}
