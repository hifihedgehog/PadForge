using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// The running PadForge, as another copy of the exe finds it (#597).
    ///
    /// <para>PadForge runs as one copy. A second start hands its switches to
    /// that copy and leaves. The running copy has administrator rights and the
    /// second one has none, so the two meet only through what Windows lets
    /// cross that line: a window looked up by its name, one posted message the
    /// running copy lets through, and the pipe of
    /// <see cref="ExternalControlService"/>.</para>
    ///
    /// <para>This class is the second copy's side and uses no window toolkit:
    /// it runs before WPF loads. The running copy's side is
    /// <see cref="InstanceSignal"/>.</para>
    ///
    /// <para>Any program of the user's can make a message-only window of the
    /// same name, so a window counts as the running copy's only when its
    /// process runs at high integrity or above, which no program without
    /// administrator rights can. Show is posted, and the right to the
    /// foreground lent, to that window alone. The single-instance lock
    /// stands in a namespace only a copy with rights can open
    /// (<see cref="InstanceLock"/>), so a copy without them finds the running
    /// copy by that checked window, and its pipes by their owner
    /// (PipeOwner).</para>
    /// </summary>
    internal static class RunningCopy
    {
        /// <summary>The name of the running copy's message-only window. It
        /// exists from the moment the copy holds the single-instance lock until
        /// it exits.</summary>
        internal const string SignalWindowName = "PadForge.Instance";

        /// <summary>The name of a second message-only window, which stands
        /// while the running copy is past its start and not yet closing. A
        /// copy that has it and serves no pipe has Automation off. One
        /// without it is still starting, or closing.</summary>
        internal const string ReadyWindowName = "PadForge.Ready";

        /// <summary>The registered message that asks the running copy to show
        /// its window, as a click on its tray icon does.</summary>
        internal const string ShowMessageName = "PadForge.Show";

        /// <summary>The single-instance lock's name in the session's plain
        /// namespace, which a copy without rights takes in a developer's
        /// run. The app's own lock is <see cref="InstanceLock"/>'s.</summary>
        internal const string InstanceMutexName = "PadForge_SingleInstance";

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests RunningCopyOffLimits), never by the app. A PadForge
        /// may be running on the PC the tests run on. While this is set no
        /// window of it is looked up, nothing is posted to it and its lock is
        /// not opened, so a test meets the running copy only through a seam of
        /// its own. And no window is made under the names a second copy looks
        /// for (<see cref="InstanceSignal"/>).</summary>
        internal static bool OffLimitsForTest;

        /// <summary>How a wait for the running copy ended.</summary>
        internal enum Settled
        {
            /// <summary>The copy is past its start.</summary>
            Ready,

            /// <summary>No copy runs any more.</summary>
            Gone,

            /// <summary>The copy never said it was ready in the time
            /// given.</summary>
            Starting,
        }

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint INFINITE = 0xFFFFFFFF;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint RegisterWindowMessageW(string name);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>The running copy's message-only window of that name, or
        /// zero.</summary>
        private static IntPtr Find(string windowName)
            => OffLimitsForTest ? IntPtr.Zero : FindChecked(windowName);

        /// <summary>Test seam: a process's integrity level. Null in the
        /// app.</summary>
        internal static Func<uint, int?> IntegritySeam;

        /// <summary>The first message-only window of the name whose process
        /// runs at high integrity or above, or zero. A window of that name a
        /// program without rights made is passed over. The fence keeps the
        /// test host from the app's own names, and a test looks up names of
        /// its own.</summary>
        internal static IntPtr FindChecked(string windowName)
        {
            if (OffLimitsForTest && IsTheAppsOwn(windowName)) return IntPtr.Zero;
            IntPtr after = IntPtr.Zero;
            // A bounded walk: a program cannot make the search run on by
            // making windows of the name without end.
            for (int i = 0; i < 64; i++)
            {
                after = FindWindowExW(HWND_MESSAGE, after, null, windowName);
                if (after == IntPtr.Zero) return IntPtr.Zero;
                GetWindowThreadProcessId(after, out uint pid);
                if (RunsWithRights(pid)) return after;
            }
            return IntPtr.Zero;
        }

        /// <summary>Whether a process runs at high integrity or above.</summary>
        internal static bool RunsWithRights(uint pid)
        {
            if (pid == 0) return false;
            var seam = IntegritySeam;
            int? integrity = seam != null ? seam(pid) : PadForge.Common.ProcessIntegrity.Of(pid);
            return integrity is { } rid && rid >= PadForge.Common.ProcessIntegrity.High;
        }

        private static bool IsTheAppsOwn(string windowName)
            => string.Equals(windowName, SignalWindowName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(windowName, ReadyWindowName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a PadForge runs in this session: its checked
        /// signal window stands, or, for a copy with rights, a copy that is
        /// still starting holds the lock.</summary>
        internal static bool Exists()
        {
            if (OffLimitsForTest) return false;
            return Find(SignalWindowName) != IntPtr.Zero || LockIsHeld();
        }

        /// <summary>Whether the running copy is past its start and not yet
        /// closing.</summary>
        internal static bool IsReady() => Find(ReadyWindowName) != IntPtr.Zero;

        /// <summary>Whether a copy holds the lock. Only a copy with rights
        /// can open the lock's namespace to know, and one without is told
        /// no: it waits on nothing a program without rights could make.</summary>
        private static bool LockIsHeld() => InstanceLock.IsHeld();

        /// <summary>The running copy's process ID, or 0 while it has no signal
        /// window yet.</summary>
        internal static uint ProcessId()
        {
            IntPtr hwnd = Find(SignalWindowName);
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid;
        }

        /// <summary>
        /// Waits while the running copy is neither ready nor gone: it is still
        /// starting, or it is closing. A copy that shows its signal window is
        /// given <paramref name="startingMs"/>. One that holds the lock and
        /// shows no window is a copy in the first moment of its start, and is
        /// given <paramref name="lockOnlyMs"/>. Only a copy with rights can
        /// see the lock.
        /// </summary>
        internal static Settled WaitUntilSettled(int startingMs = 15000, int lockOnlyMs = 2000)
        {
            if (OffLimitsForTest) return Settled.Gone;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (IsReady()) return Settled.Ready;
                bool signal = Find(SignalWindowName) != IntPtr.Zero;
                if (!signal && !LockIsHeld()) return Settled.Gone;
                if (clock.ElapsedMilliseconds >= (signal ? startingMs : lockOnlyMs)) return Settled.Starting;
                Thread.Sleep(50);
            }
        }

        /// <summary>Hands the running copy this process's right to come to the
        /// front, so the window it is about to show is not left flashing in
        /// the taskbar.</summary>
        internal static void LendForeground()
        {
            uint pid = ProcessId();
            if (pid != 0) AllowSetForegroundWindow(pid);
        }

        /// <summary>The running copy's signal window. A copy that is still
        /// starting is waited for, up to <paramref name="waitMs"/>. Zero when
        /// none came.</summary>
        private static IntPtr WaitForSignalWindow(int waitMs)
        {
            var clock = Stopwatch.StartNew();
            IntPtr hwnd;
            while ((hwnd = Find(SignalWindowName)) == IntPtr.Zero)
            {
                if (clock.ElapsedMilliseconds >= waitMs || !LockIsHeld()) return IntPtr.Zero;
                Thread.Sleep(100);
            }
            return hwnd;
        }

        /// <summary>Asks the running copy to show its window and hands it this
        /// process's right to come to the front. A copy that is still starting
        /// is waited for, up to <paramref name="waitMs"/>. False when no copy
        /// took the message.</summary>
        internal static bool Show(int waitMs = 10000)
        {
            if (OffLimitsForTest) return false;
            IntPtr hwnd = WaitForSignalWindow(waitMs);
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0) AllowSetForegroundWindow(pid);
            uint msg = RegisterWindowMessageW(ShowMessageName);
            return msg != 0 && PostMessageW(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Takes hold of the running copy now, and answers with the wait for
        /// its exit, which gives its exit code. The hold is taken before any
        /// line is sent, so a copy told to exit is still the one waited for.
        /// Null when there is no copy to wait for or Windows does not let this
        /// process watch it.
        /// </summary>
        internal static Func<int?> Watch(int waitMs = 10000)
        {
            if (OffLimitsForTest) return null;
            IntPtr hwnd = WaitForSignalWindow(waitMs);
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            IntPtr process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return null;
            return () =>
            {
                try
                {
                    WaitForSingleObject(process, INFINITE);
                    return GetExitCodeProcess(process, out uint code) ? unchecked((int)code) : (int?)null;
                }
                finally { CloseHandle(process); }
            };
        }
    }
}
