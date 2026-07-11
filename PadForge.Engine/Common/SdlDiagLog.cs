using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Engine
{
    /// <summary>Ground-truth diagnostics for the ~10 s device-freeze
    /// investigation (issue #210 follow-up): captures the SDL driver's own
    /// SDL_LogDebug lines (the Wii Motion Plus state machine narrates its
    /// CONNECTED/DISCONNECTED/status decisions there, otherwise invisible in
    /// a WPF app) and accepts stall-watchdog lines from the poll loop. One
    /// timestamped file, size-capped, always on: the write path costs nothing
    /// unless a line is actually emitted, and steady-state SDL debug traffic
    /// is event-driven, not per-frame.</summary>
    public static class SdlDiagLog
    {
        private const long MaxBytes = 8 * 1024 * 1024;
        private static readonly object _sync = new object();
        private static string _path;
        // Rooted so the GC never collects the delegate SDL holds.
        private static SDL3.SDL.SDL_LogOutputFunction _sdlCallback;

        /// <summary>Installs the file sink and routes SDL's log output into
        /// it at DEBUG priority. Call once, before SDL_Init, so init-time
        /// messages are captured too. Never throws: diagnostics must not be
        /// able to take the input stack down.</summary>
        public static void Install(string path)
        {
            try
            {
                lock (_sync)
                {
                    _path = path;
                    File.AppendAllText(_path,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} DIAG start pid={Environment.ProcessId}{Environment.NewLine}",
                        Encoding.UTF8);
                }
                _sdlCallback = OnSdlLog;
                SDL3.SDL.SDL_SetLogOutputFunction(_sdlCallback, IntPtr.Zero);
                SDL3.SDL.SDL_SetLogPriorities(SDL3.SDL.SDL_LOG_PRIORITY_DEBUG);
            }
            catch
            {
                _path = null;
            }
        }

        private static void OnSdlLog(IntPtr userdata, int category, int priority, IntPtr message)
        {
            string text;
            try { text = Marshal.PtrToStringUTF8(message); }
            catch { return; }
            WriteLine($"SDL [{category}/{priority}] {text}");
        }

        /// <summary>Appends one timestamped line. Used by the SDL callback
        /// and by the poll-loop stall watchdog. Silently drops on any I/O
        /// error and truncates the file when it outgrows the cap.</summary>
        public static void WriteLine(string line)
        {
            var path = _path;
            if (path == null)
                return;
            try
            {
                lock (_sync)
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                        File.WriteAllText(path, string.Empty);
                    File.AppendAllText(path,
                        $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics never throw into the input stack.
            }
        }
    }
}
