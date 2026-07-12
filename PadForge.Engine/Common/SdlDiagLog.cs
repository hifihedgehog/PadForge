using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PadForge.Engine
{
    /// <summary>In-memory diagnostics ring. PadForge writes no log file in
    /// normal operation; the single sanctioned on-disk artifact is
    /// crash.log. SDL's own log lines (DEBUG priority, e.g. driver state
    /// machines), the poll-loop stall watchdogs, and subsystem diagnostics
    /// accumulate in this bounded ring, and the crash handler appends the
    /// ring's tail to crash.log so a crash still carries its recent
    /// context. A healthy session leaves nothing on disk.</summary>
    public static class SdlDiagLog
    {
        private const int MaxLines = 400;
        private static readonly object _sync = new object();
        private static readonly Queue<string> _ring = new Queue<string>(MaxLines);
        // Rooted so the GC never collects the delegate SDL holds.
        private static SDL3.SDL.SDL_LogOutputFunction _sdlCallback;

        /// <summary>Bench re-enable switch: launch with the PADFORGE_DIAG
        /// environment variable set to a file path and every ring line is
        /// also appended there. Unset (the default for every normal
        /// launch), PadForge writes no log file. The acceptance bar for a
        /// re-enabled log is that it stays free of errors.</summary>
        private static readonly string _mirrorPath = ReadMirrorPath();

        private static string ReadMirrorPath()
        {
            try
            {
                string p = Environment.GetEnvironmentVariable("PADFORGE_DIAG");
                return string.IsNullOrWhiteSpace(p) ? null : p;
            }
            catch { return null; }
        }

        /// <summary>Routes SDL's log output into the ring at DEBUG
        /// priority. Call once, before SDL_Init, so init-time messages are
        /// captured too. Never throws: diagnostics must not be able to
        /// take the input stack down.</summary>
        public static void Install()
        {
            try
            {
                _sdlCallback = OnSdlLog;
                SDL3.SDL.SDL_SetLogOutputFunction(_sdlCallback, IntPtr.Zero);
                SDL3.SDL.SDL_SetLogPriorities(SDL3.SDL.SDL_LOG_PRIORITY_DEBUG);
            }
            catch
            {
                // Diagnostics never throw into the input stack.
            }
        }

        private static void OnSdlLog(IntPtr userdata, int category, int priority, IntPtr message)
        {
            string text;
            try { text = Marshal.PtrToStringUTF8(message); }
            catch { return; }
            WriteLine($"SDL [{category}/{priority}] {text}");
        }

        /// <summary>Appends one timestamped line to the ring, evicting the
        /// oldest when full. Never touches disk and never throws.</summary>
        public static void WriteLine(string line)
        {
            try
            {
                string stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
                lock (_sync)
                {
                    if (_ring.Count >= MaxLines) _ring.Dequeue();
                    _ring.Enqueue(stamped);
                    if (_mirrorPath != null)
                        System.IO.File.AppendAllText(_mirrorPath,
                            stamped + Environment.NewLine);
                }
            }
            catch
            {
                // Diagnostics never throw into the input stack.
            }
        }

        /// <summary>The ring's current contents, oldest first. For the
        /// crash handler's crash.log appendix.</summary>
        public static string Snapshot()
        {
            try
            {
                lock (_sync)
                    return string.Join(Environment.NewLine, _ring);
            }
            catch { return string.Empty; }
        }
    }
}
