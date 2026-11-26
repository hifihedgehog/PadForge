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
        // When each line of the ring was written, as a Stopwatch timestamp,
        // so a taker can place the lines it inherits on its own clock.
        private static readonly Queue<long> _ringTicks = new Queue<long>(MaxLines);

        /// <summary>How long the stamp at the head of a ring line is:
        /// "HH:mm:ss.fff" and a space.</summary>
        private const int StampLength = 13;

        /// <summary>The taker of every line while a problem recording, or
        /// its memory of the last minute, runs (#600), or null. It gets each
        /// line without its stamp, with the line's own time, and has to hand
        /// it to a queue and return: it is called under the ring's lock on
        /// whichever thread wrote the line, the poll thread among them.</summary>
        private static volatile Action<long, string> _taker;
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
        public static bool IsMirroring => _mirrorPath != null || _taker != null;

        /// <summary>Hands every line from now on to <paramref name="taker"/>
        /// and returns the ring's lines up to now, oldest first, each with
        /// the time it was written and without its stamp. Taking counts as
        /// mirroring, so the trace sites that speak only then speak, and
        /// their lines go to the taker's queue and through no file append of
        /// this class. One taker at a time: a second replaces the first.
        ///
        /// <para>The taker is then called once with a null line, which marks
        /// in its queue where the history ends. All of it is one step under
        /// the ring's lock, so no line is in both the history and the queue
        /// after the mark, and none is in neither. A taker already attached
        /// calls this again for a fresh history and a fresh mark.</para></summary>
        public static List<(long Ticks, string Line)> Attach(Action<long, string> taker)
        {
            var history = new List<(long, string)>();
            lock (_sync)
            {
                using var ticks = _ringTicks.GetEnumerator();
                foreach (string stamped in _ring)
                {
                    ticks.MoveNext();
                    history.Add((ticks.Current, stamped.Length > StampLength ? stamped.Substring(StampLength) : stamped));
                }
                _taker = taker;
                taker?.Invoke(System.Diagnostics.Stopwatch.GetTimestamp(), null);
            }
            return history;
        }

        /// <summary>Ends what <see cref="Attach"/> began, when
        /// <paramref name="taker"/> is still the one attached.</summary>
        public static void Detach(Action<long, string> taker)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_taker, taker)) _taker = null;
            }
        }

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
                if (string.IsNullOrWhiteSpace(p)) return null;
                return Environment.IsPrivilegedProcess ? MirrorPathWithRights(p, Environment.ProcessPath) : p;
            }
            catch { return null; }
        }

        /// <summary>The one file PADFORGE_DIAG may name in a process with
        /// administrator rights, beside PadForge.exe.</summary>
        internal const string BenchLogName = "diag.log";

        /// <summary>
        /// PADFORGE_DIAG in a process with administrator rights (#600): taken
        /// only when it names <see cref="BenchLogName"/> directly in the exe's
        /// own folder. Every program of the user can set the variable
        /// (HKCU\Environment), and a log written with rights to a path it
        /// chose would let it write into folders only administrators may
        /// change, or append to, rotate and move away a file PadForge keeps
        /// there, PadForge.xml and crash.log among them. Null otherwise.
        /// </summary>
        internal static string MirrorPathWithRights(string path, string exe)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(exe)) return null;
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(exe));
                string parent = System.IO.Path.GetDirectoryName(full);
                if (folder == null || parent == null) return null;
                if (!string.Equals(System.IO.Path.GetFileName(full), BenchLogName, StringComparison.OrdinalIgnoreCase)) return null;
                return string.Equals(parent.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch
            {
                return null;
            }
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
        /// oldest when full, hands it to the taker when one is attached, and
        /// appends it to the mirror file while one is armed, rotating that
        /// file past <see cref="RotateAtBytes"/>. Never throws.</summary>
        public static void WriteLine(string line)
        {
            try
            {
                string stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
                long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_sync)
                {
                    if (_ring.Count >= MaxLines)
                    {
                        _ring.Dequeue();
                        _ringTicks.Dequeue();
                    }
                    _ring.Enqueue(stamped);
                    _ringTicks.Enqueue(ticks);
                    _taker?.Invoke(ticks, line);
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
