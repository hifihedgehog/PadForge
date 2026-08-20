using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PadForge.Engine
{
    /// <summary>In-memory diagnostics ring. SDL's own log lines (DEBUG
    /// priority, e.g. driver state machines), the poll-loop stall
    /// watchdogs, and subsystem diagnostics accumulate in this bounded
    /// ring, and the crash handler appends the ring's tail to crash.log
    /// so a crash still carries its recent context. By default the ring
    /// stays in memory and a healthy session leaves nothing on disk
    /// beyond what the user asked for: the file mirror exists only when
    /// armed, by the PADFORGE_DIAG bench variable or by the Diagnostics
    /// setting (#303), and it writes diagnostics.log beside the exe like
    /// everything else PadForge persists.</summary>
    public static class SdlDiagLog
    {
        // 400 held about two minutes of a real session and the owner hit
        // exactly that: they pressed Save Snapshot right after a UI bug and
        // got nothing but microphone and mapping-filter heartbeat, because
        // the event had already rolled out. A snapshot that cannot reach
        // back past the thing you just saw is not a snapshot. 4000 lines is
        // a few hundred KB of strings held once per process, which is
        // nothing beside what the ring is for.
        private const int MaxLines = 4000;
        private static readonly object _sync = new object();
        private static readonly Queue<string> _ring = new Queue<string>(MaxLines);
        // Rooted so the GC never collects the delegate SDL holds.
        private static SDL3.SDL.SDL_LogOutputFunction _sdlCallback;

        /// <summary>Bench re-enable switch: launch with the PADFORGE_DIAG
        /// environment variable set to a file path and every ring line is
        /// also appended there. Unset (the default for every normal
        /// launch), the mirror follows the user's Diagnostics setting via
        /// <see cref="SetMirror"/> instead (#303: PadForge auto-starts
        /// with Windows for many users, so a launch-time flag was
        /// unusable for exactly the long-running sessions that need a
        /// trace). The acceptance bar for a re-enabled log is that it
        /// stays free of errors.</summary>
        private static readonly string _envMirrorPath = ReadMirrorPath();

        private static volatile string _mirrorPath = _envMirrorPath;

        /// <summary>Approximate bytes written to the current mirror file,
        /// maintained locally so rotation costs no syscall per line.
        /// Guarded by _sync.</summary>
        private static long _mirrorBytes;

        /// <summary>Rotation cap for the mirror file. A settings-armed
        /// mirror can run for days on an always-on machine, so the file
        /// rotates to "<c>{path}.old</c>" past this size instead of
        /// growing without bound. Internal-settable for the tests.</summary>
        internal static long RotateAtBytes = 8L * 1024 * 1024;

        /// <summary>True while a file mirror is armed (bench env var or
        /// the Diagnostics setting). Hot paths that would otherwise format
        /// a string on every change test this first, so a normal session
        /// pays nothing for a trace. The ring itself stays cheap; the
        /// string interpolation is not.</summary>
        public static bool IsMirroring => _mirrorPath != null;

        /// <summary>Arms (path) or disarms (null) the file mirror at
        /// runtime, for the Settings toggle (#303). A PADFORGE_DIAG bench
        /// launch keeps its env-supplied path for the whole session: the
        /// bench harness owns that file and the setting must not steal
        /// it. Never throws.</summary>
        public static void SetMirror(string path)
        {
            try
            {
                if (_envMirrorPath != null) return;
                bool arming;
                lock (_sync)
                {
                    _mirrorPath = string.IsNullOrWhiteSpace(path) ? null : path;
                    _mirrorBytes = 0;
                    arming = _mirrorPath != null;
                    if (arming)
                    {
                        try
                        {
                            var fi = new System.IO.FileInfo(_mirrorPath);
                            if (fi.Exists) _mirrorBytes = fi.Length;
                        }
                        catch { }
                    }
                }
                // Session marker, through the normal path so it also rides
                // the ring: log files from auto-started sessions need a
                // line that separates one launch from the next.
                if (arming) WriteLine("=== diagnostics logging enabled ===");
            }
            catch
            {
                // Diagnostics never throw into the input stack.
            }
        }

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
                    {
                        string data = stamped + Environment.NewLine;
                        System.IO.File.AppendAllText(_mirrorPath, data);
                        // Chars approximate bytes closely enough for a
                        // rotation cap (the lines are ASCII-dominant).
                        _mirrorBytes += data.Length;
                        if (_mirrorBytes > RotateAtBytes)
                            RotateLocked();
                    }
                }
            }
            catch
            {
                // Diagnostics never throw into the input stack.
            }
        }

        /// <summary>Rolls the mirror file to "{path}.old", replacing any
        /// previous rollover. Caller holds _sync.</summary>
        private static void RotateLocked()
        {
            try
            {
                string old = _mirrorPath + ".old";
                if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                System.IO.File.Move(_mirrorPath, old);
            }
            catch
            {
                // A locked or missing file must not take the ring down;
                // the counter reset below re-arms the next attempt.
            }
            _mirrorBytes = 0;
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
