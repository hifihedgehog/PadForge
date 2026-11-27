using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Common.Input
{
    /// <summary>
    /// A running process's image path from its id, through a limited-query
    /// handle and <c>QueryFullProcessImageNameW</c>, the access Windows still
    /// grants for a protected process. <c>Process.GetProcessById(id).ProcessName</c>
    /// walks every process when it cannot open its target (245 to 639 ms
    /// measured on a PC with about 480 processes), and
    /// <c>Process.MainModule</c> needs memory-read access, which a protected
    /// process refuses. This asks the one process and returns null when it
    /// cannot answer.
    /// </summary>
    internal static class ProcessImage
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint WAIT_TIMEOUT = 0x102;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] name, ref uint size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        /// <summary>The full path of the process's image, or null when the
        /// process is gone or answers no limited query.</summary>
        public static string PathOf(uint processId)
        {
            char[] buffer = null;
            return PathOf(processId, ref buffer);
        }

        /// <summary><see cref="PathOf(uint)"/> with a buffer the caller keeps
        /// across calls, grown here when a path does not fit, so a walk of
        /// every process allocates one buffer.</summary>
        internal static string PathOf(uint processId, ref char[] buffer)
        {
            if (processId == 0) return null;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero) return null;
            try { return PathOf(handle, ref buffer); }
            finally { CloseHandle(handle); }
        }

        private static string PathOf(IntPtr handle, ref char[] buffer)
        {
            if (buffer == null || buffer.Length < 512) buffer = new char[512];
            while (true)
            {
                uint size = (uint)buffer.Length;
                if (QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                    return new string(buffer, 0, (int)size);
                if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER || buffer.Length >= 32768) return null;
                buffer = new char[buffer.Length * 4];
            }
        }

        /// <summary>A handle that can say whether the process has exited, and
        /// the process's image path, or a zero handle when the process is gone
        /// or answers neither. An open handle keeps the process's id from going
        /// to another process, so the path stays the process's own while the
        /// handle is held.</summary>
        internal static IntPtr OpenWatched(uint processId, ref char[] buffer, out string path)
        {
            path = null;
            if (processId == 0) return IntPtr.Zero;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, processId);
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            path = PathOf(handle, ref buffer);
            if (!string.IsNullOrEmpty(path)) return handle;
            CloseHandle(handle);
            return IntPtr.Zero;
        }

        /// <summary>Whether a handle from <see cref="OpenWatched"/> names a
        /// process that still runs.</summary>
        internal static bool StillRuns(IntPtr handle) => WaitForSingleObject(handle, 0) == WAIT_TIMEOUT;

        internal static void Close(IntPtr handle) => CloseHandle(handle);

        /// <summary>The image name without its extension, as
        /// <c>Process.ProcessName</c> gives it, or null.</summary>
        public static string NameOf(uint processId)
        {
            string path = PathOf(processId);
            return string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>
    /// The running processes, each as its id and image path: the list of
    /// process ids (<c>K32EnumProcesses</c>), then one limited query per
    /// process (<see cref="ProcessImage"/>), the way Microsoft's
    /// "Enumerating All Processes" sample names them.
    ///
    /// <para><c>Process.GetProcesses</c> and a Toolhelp snapshot both copy
    /// the kernel's record of every process and every thread, and on a PC
    /// with about 400 processes and 9,000 threads one copy cost 240 to 360 ms
    /// of CPU, nearly all of it in the kernel. Taken every few seconds by the
    /// peripheral link, the Flydigi watch and the handheld watch, those
    /// copies held a core at 20 percent and more while the engine ran. On
    /// the same PC the id list costs 0.05 ms and a pass that names every
    /// process 7 ms, elevated.</para>
    ///
    /// <para>A process that answers no limited query is left out. Elevated,
    /// as PadForge runs, those are the kernel's own (System, Registry,
    /// Memory Compression). Unelevated, every process of another account is
    /// left out too.</para>
    /// </summary>
    internal static class RunningProcessList
    {
        /// <summary>One running process.</summary>
        internal readonly record struct Entry(uint Id, string Path)
        {
            /// <summary>The image's file name, "lghub_agent.exe".</summary>
            public string FileName => System.IO.Path.GetFileName(Path);

            /// <summary>The file name without a final ".exe", as
            /// <c>Process.ProcessName</c> gives it.</summary>
            public string Name => ShortName(FileName);
        }

        /// <summary><paramref name="fileName"/> without a final ".exe", in
        /// any case, the one extension <c>Process.ProcessName</c>
        /// drops.</summary>
        internal static string ShortName(string fileName)
            => fileName != null && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - 4)
                : fileName;

        /// <summary>Every running process's id, in the order Windows lists
        /// them, or null when the list could not be read. One call that
        /// fills an array: no handle is opened and no name is read.</summary>
        internal static uint[] Ids()
        {
            try
            {
                var buffer = new uint[1024];
                while (true)
                {
                    uint size = (uint)(buffer.Length * sizeof(uint));
                    if (!K32EnumProcesses(buffer, size, out uint filled)) return null;
                    if (filled < size)
                    {
                        var ids = new uint[filled / sizeof(uint)];
                        Array.Copy(buffer, ids, ids.Length);
                        return ids;
                    }
                    // A buffer filled to the end may have left ids out, so
                    // the list is read again with twice the room.
                    if (buffer.Length >= 1 << 20) return null;
                    buffer = new uint[buffer.Length * 2];
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Processes named by a read so far, for the test that a
        /// running one is named once.</summary>
        private static int s_named;
        internal static int NamedCount => Volatile.Read(ref s_named);

        /// <summary>The processes named by an earlier read, each with the
        /// handle that keeps its id its own. A read names only the ids it has
        /// not seen running, so a check every few seconds costs the id list
        /// and one wait per process, where naming every process cost 7 to
        /// 22 ms each time.</summary>
        private static Dictionary<uint, (IntPtr Handle, string Path)> s_known = new();
        private static readonly object s_knownLock = new();
        private static char[] s_buffer;

        /// <summary>A process that exits after a read stays a zombie object
        /// while its handle is held. A timer armed by a read that leaves
        /// handles held looks once a minute, and when no read came in the last
        /// minute it closes every held handle and stops, so the next read
        /// names every process again.</summary>
        internal const int IdleCloseMs = 60000;
        private static long s_readAt;
        private static System.Threading.Timer s_idleClose;

        /// <summary>The handles held, for the test of the idle close.</summary>
        internal static int HeldCount { get { lock (s_knownLock) return s_known.Count; } }

        /// <summary>Closes every held handle when no read came for
        /// <see cref="IdleCloseMs"/>, and stops the timer. True when it
        /// closed them.</summary>
        internal static bool CloseIfIdle(long nowMs)
        {
            lock (s_knownLock)
            {
                if (nowMs - s_readAt < IdleCloseMs) return false;
                foreach (var held in s_known.Values) ProcessImage.Close(held.Handle);
                s_known = new();
                s_idleClose?.Dispose();
                s_idleClose = null;
                return true;
            }
        }

        /// <summary>Every running process that answers a limited query, or
        /// null when the list of ids could not be read.</summary>
        internal static List<Entry> Read()
        {
            uint[] ids = Ids();
            if (ids == null) return null;
            var entries = new List<Entry>(ids.Length);
            lock (s_knownLock)
            {
                var known = s_known;
                var next = new Dictionary<uint, (IntPtr Handle, string Path)>(ids.Length);
                foreach (uint id in ids)
                {
                    if (known.Remove(id, out var seen))
                    {
                        if (ProcessImage.StillRuns(seen.Handle))
                        {
                            next[id] = seen;
                            entries.Add(new Entry(id, seen.Path));
                            continue;
                        }
                        ProcessImage.Close(seen.Handle);
                    }
                    IntPtr handle = ProcessImage.OpenWatched(id, ref s_buffer, out string path);
                    if (handle != IntPtr.Zero)
                    {
                        Interlocked.Increment(ref s_named);
                        next[id] = (handle, path);
                        entries.Add(new Entry(id, path));
                        continue;
                    }
                    // A process that grants no wait is named on every read.
                    path = ProcessImage.PathOf(id, ref s_buffer);
                    if (!string.IsNullOrEmpty(path))
                    {
                        Interlocked.Increment(ref s_named);
                        entries.Add(new Entry(id, path));
                    }
                }
                // What is left has exited.
                foreach (var gone in known.Values) ProcessImage.Close(gone.Handle);
                s_known = next;
                s_readAt = Environment.TickCount64;
                if (next.Count > 0 && s_idleClose == null)
                    s_idleClose = new System.Threading.Timer(_ => CloseIfIdle(Environment.TickCount64), null, IdleCloseMs, IdleCloseMs);
            }
            return entries;
        }

        // The size and the count are in bytes, and a count equal to the
        // size means the buffer may have been too small.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32EnumProcesses([Out] uint[] processIds, uint size, out uint bytesReturned);
    }

    /// <summary>
    /// Which of a few named programs run, from one read of
    /// <see cref="RunningProcessList"/>, the id list and one wait per
    /// process after its first read, where a walk of the
    /// kernel's process records cost 240 to 360 ms.
    /// <c>Process.GetProcessesByName</c> takes that walk once per name. Worker
    /// threads only. A thread that holds a rate asks a
    /// <see cref="PeriodicWorkerCheck"/>.
    /// </summary>
    internal static class ProcessPresence
    {
        /// <summary>The entries of <paramref name="names"/> (image names
        /// without .exe, compared without case) that a running process
        /// carries, spelled as the caller spelled them. Empty when none runs
        /// or the list cannot be read.</summary>
        public static HashSet<string> RunningAmong(IReadOnlyCollection<string> names)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var running = RunningProcessList.Read();
            if (running == null) return found;
            foreach (var process in running)
            {
                string n = process.Name;
                foreach (var name in names)
                    if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { found.Add(name); break; }
            }
            return found;
        }
    }

    /// <summary>
    /// A yes-or-no check that runs on a worker every interval, one at a time,
    /// so the thread that asks never waits on it: the handheld sweep's worker
    /// half and <c>InputManager.FlydigiWatchTick</c>, made safe for several
    /// asking threads, since every Razer analog keyboard's reader asks the
    /// one Synapse check.
    /// </summary>
    internal sealed class PeriodicWorkerCheck
    {
        /// <summary>One finished check. <see cref="Generation"/> grows by one
        /// per check, so a caller acts on each result once.</summary>
        internal sealed class Result
        {
            public bool Value { get; init; }
            public long Generation { get; init; }
        }

        private readonly Func<bool> _check;
        private readonly int _intervalMs;
        private int _inFlight;
        private long _startedMs = long.MinValue / 2;
        private long _generation;
        private volatile Result _latest;

        public PeriodicWorkerCheck(Func<bool> check, int intervalMs)
        {
            _check = check;
            _intervalMs = intervalMs;
        }

        /// <summary>The newest finished check, or null before the first one
        /// ends.</summary>
        public Result Latest => _latest;

        /// <summary>Starts the check on a worker when none is in flight and
        /// the interval has passed since the last one started, and returns
        /// <see cref="Latest"/>. Never waits on the check. Any thread.</summary>
        public Result Poll(long now)
        {
            if (Volatile.Read(ref _inFlight) == 0
                && now - Interlocked.Read(ref _startedMs) >= _intervalMs
                && Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0)
            {
                // Read again under the flag: a check another thread started
                // may have ended between the two reads.
                if (now - Interlocked.Read(ref _startedMs) < _intervalMs)
                {
                    Volatile.Write(ref _inFlight, 0);
                    return _latest;
                }
                Interlocked.Exchange(ref _startedMs, now);
                Task.Run(() =>
                {
                    try
                    {
                        bool value = _check();
                        _latest = new Result { Value = value, Generation = Interlocked.Increment(ref _generation) };
                    }
                    catch
                    {
                        // A check that throws publishes nothing. The next
                        // interval tries again.
                    }
                    finally { Volatile.Write(ref _inFlight, 0); }
                });
            }
            return _latest;
        }
    }
}
