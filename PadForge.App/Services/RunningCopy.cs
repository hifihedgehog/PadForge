using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// The running PadForge, as another copy of the exe finds it (#600).
    ///
    /// <para>PadForge runs as one copy. A second start hands its switches to
    /// that copy and leaves. The running copy has administrator rights and the
    /// second one has none, so the two meet only through what Windows lets
    /// cross that line: a window looked up by its name, the signal pipe of the
    /// same name, which takes show, exit and a logon start's switches from a
    /// client its token checks admit, and the pipe of
    /// <see cref="ExternalControlService"/>. A posted message carries no
    /// sender a window can check, so the running copy's window takes the two
    /// registered messages only from a process of its own integrity or above,
    /// as Windows has it by default, and a copy with rights posts
    /// them.</para>
    ///
    /// <para>This class is the second copy's side and uses no window toolkit:
    /// it runs before WPF loads. The running copy's side is
    /// <see cref="InstanceSignal"/>.</para>
    ///
    /// <para>Any program of the user's can make a message-only window of the
    /// same name, so a window counts as the running copy's only when its
    /// process runs at high integrity or above, which no program without
    /// administrator rights can. Show and exit go to that copy alone, over
    /// its pipe, whose owner is checked (PipeOwner), and the right to the
    /// foreground is lent to its process alone. The single-instance lock
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

        /// <summary>The registered message that asks the running copy to
        /// close, as its tray icon's Exit does (#600 item 44).</summary>
        internal const string ExitMessageName = "PadForge.Exit";

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

        /// <summary>How an ask to close ended.</summary>
        internal enum Closing
        {
            /// <summary>The copy's process ended.</summary>
            Closed,

            /// <summary>The copy took the ask, or Windows said it did, and
            /// still runs at the end of the wait.</summary>
            StillRunning,

            /// <summary>No copy runs.</summary>
            NotRunning,
        }

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint INFINITE = 0xFFFFFFFF;
        private const uint WAIT_OBJECT_0 = 0;

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

        /// <summary>Test seam: the process a window belongs to. Null in the
        /// app.</summary>
        internal static Func<IntPtr, uint> WindowOwnerSeam;

        private static uint OwnerOf(IntPtr hwnd)
        {
            var seam = WindowOwnerSeam;
            if (seam != null) return seam(hwnd);
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid;
        }

        /// <summary>The first message-only window of the name whose process
        /// runs at high integrity or above, or zero. A window of that name a
        /// program without rights made is passed over. The walk goes on to the
        /// last window of the name, so windows another program made cannot
        /// hide the running copy's, and each process's answer is read once, so
        /// however many windows one program makes they cost one look at its
        /// rights. The fence keeps the test host from the app's own names, and
        /// a test looks up names of its own.</summary>
        internal static IntPtr FindChecked(string windowName)
        {
            if (OffLimitsForTest && IsTheAppsOwn(windowName)) return IntPtr.Zero;
            IntPtr after = IntPtr.Zero;
            Dictionary<uint, bool> withRights = null;
            while ((after = FindWindowExW(HWND_MESSAGE, after, null, windowName)) != IntPtr.Zero)
            {
                uint pid = OwnerOf(after);
                withRights ??= new Dictionary<uint, bool>();
                if (!withRights.TryGetValue(pid, out bool rights)) withRights[pid] = rights = RunsWithRights(pid);
                if (rights) return after;
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
        /// given <paramref name="startingMs"/>, by default the time a close is
        /// given (<see cref="CloseWaitMs"/>), since a closing copy shows the
        /// same windows as a starting one and its close may run that long.
        /// A copy still starting ends the wait as soon as it is ready. One
        /// that holds the lock and
        /// shows no window is a copy in the first moment of its start, and is
        /// given <paramref name="lockOnlyMs"/>. Only a copy with rights can
        /// see the lock.
        /// </summary>
        internal static Settled WaitUntilSettled(int startingMs = CloseWaitMs, int lockOnlyMs = 2000)
        {
            if (OffLimitsForTest) return Settled.Gone;
            return WaitUntilSettled(SignalWindowName, ReadyWindowName, startingMs, lockOnlyMs);
        }

        /// <summary>The same wait, for the signal and ready windows of the
        /// names given. A test gives names of its own, and the fence in
        /// <see cref="FindChecked"/> keeps the app's from the test
        /// host.</summary>
        internal static Settled WaitUntilSettled(string signalName, string readyName, int startingMs, int lockOnlyMs)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (FindChecked(readyName) != IntPtr.Zero) return Settled.Ready;
                bool signal = FindChecked(signalName) != IntPtr.Zero;
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

        /// <summary>The running copy's signal window. For a caller with rights
        /// a copy that is still starting, and so holds the lock, is waited for,
        /// up to <paramref name="waitMs"/>. A caller without rights cannot see
        /// the lock (<see cref="LockIsHeld"/>), so it waits for nothing and
        /// finds only a window that stands. Zero when none came.</summary>
        private static IntPtr WaitForSignalWindow(int waitMs, string signalName = SignalWindowName)
        {
            var clock = Stopwatch.StartNew();
            IntPtr hwnd;
            while ((hwnd = FindChecked(signalName)) == IntPtr.Zero)
            {
                if (clock.ElapsedMilliseconds >= waitMs || !LockIsHeld()) return IntPtr.Zero;
                Thread.Sleep(100);
            }
            return hwnd;
        }

        /// <summary>Asks the running copy to show its window and hands it this
        /// process's right to come to the front. For a caller with rights a
        /// copy that is still starting is waited for, up to
        /// <paramref name="waitMs"/>. A caller without rights finds only a
        /// signal window that stands (<see cref="WaitForSignalWindow"/>).
        /// False when no copy took the ask.</summary>
        internal static bool Show(int waitMs = 10000)
        {
            if (OffLimitsForTest) return false;
            return Show(SignalWindowName, ShowMessageName, waitMs);
        }

        /// <summary>The same ask, to the signal window, pipe and message of
        /// the names given. A test gives names of its own, and the fences in
        /// <see cref="FindChecked"/> and <see cref="Ask"/> keep the app's from
        /// the test host.</summary>
        internal static bool Show(string signalName, string showMessageName, int waitMs)
        {
            IntPtr hwnd = WaitForSignalWindow(waitMs, signalName);
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0) AllowSetForegroundWindow(pid);
            return Ask(hwnd, signalName, showMessageName, ShowLine, AskMs);
        }

        // ── The asks a second copy makes (#600 item 44) ──

        /// <summary>The signal pipe's line that asks for the main
        /// window.</summary>
        internal const string ShowLine = "show";

        /// <summary>The signal pipe's line that asks the copy to close.</summary>
        internal const string ExitLine = "exit";

        /// <summary>The start of the signal pipe's line that hands a logon
        /// start's switches: the words follow as a JSON array of
        /// strings.</summary>
        internal const string SwitchesLine = "switches";

        /// <summary>How long the running copy has to take an ask.</summary>
        internal const int AskMs = 5000;

        /// <summary>The running copy's signal pipe, of the signal window's
        /// name. Window names and pipe names are kept apart by Windows.</summary>
        internal static string SignalPipeName(string signalName) => signalName;

        /// <summary>
        /// Asks the running copy that stands at <paramref name="hwnd"/>: a
        /// caller with rights posts the registered message, which Windows
        /// lets through to a window of its own integrity, and any other caller
        /// sends the line over the signal pipe, whose server reads its token
        /// (InstanceSignal) and whose owner this side checks (PipeOwner). True
        /// when the copy took it.
        /// </summary>
        private static bool Ask(IntPtr hwnd, string signalName, string messageName, string line, int answerMs)
        {
            if (OffLimitsForTest && IsTheAppsOwn(signalName)) return false;
            if (Environment.IsPrivilegedProcess)
            {
                uint msg = RegisterWindowMessageW(messageName);
                return msg != 0 && PostMessageW(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
            }
            return Signal(signalName, line, answerMs);
        }

        /// <summary>One line to the signal pipe of that name. True when the
        /// copy answered ok.</summary>
        private static bool Signal(string signalName, string line, int answerMs)
        {
            if (OffLimitsForTest && IsTheAppsOwn(signalName)) return false;
            string reply = ControlPipeClient.Exchange(SignalPipeName(signalName), line, 2000, answerMs);
            return reply != null && reply.StartsWith("ok", StringComparison.Ordinal);
        }

        /// <summary>How long a close is given: <see cref="Exit()"/>'s and
        /// <see cref="HoldForClose"/>'s wait for the copy to end, and
        /// <see cref="WaitUntilSettled(int, int)"/>'s for a copy that shows
        /// its signal window and no ready window. Closing stops the engine,
        /// whose stop waits up to its lifecycle wait for a virtual controller's
        /// create (InputManager.LifecycleWaitMs) and its other bounded waits on
        /// top (InputManager.StopJoinsMs), and the MIDI controllers' shutdown
        /// waits up to its own (MidiVirtualController.DisconnectTimeoutMs). The
        /// close is given their sum and <see cref="CloseSlackMs"/> for the rest
        /// of the close.</summary>
        internal const int CloseWaitMs = PadForge.Common.Input.InputManager.LifecycleWaitMs + PadForge.Common.Input.InputManager.StopJoinsMs
            + PadForge.Common.Input.MidiVirtualController.DisconnectTimeoutMs + CloseSlackMs;

        /// <summary>The time a close is given past its bounded waits: the
        /// services' own stops and the window's teardown.</summary>
        internal const int CloseSlackMs = 15_000;

        /// <summary>
        /// Asks the running copy to close, as its tray icon's Exit does, and
        /// waits up to <see cref="CloseWaitMs"/> for its process to end
        /// (#600 item 44). The ask goes as the show does
        /// (<see cref="Ask"/>), so it needs no Automation. For a caller with
        /// rights a copy that is still starting is waited for, up to
        /// <paramref name="waitMs"/>, and closes once its window is up. A
        /// caller without rights finds only a signal window that stands.
        /// </summary>
        internal static Closing Exit(int waitMs = 10000)
            => OffLimitsForTest ? Closing.NotRunning : Exit(SignalWindowName, ExitMessageName, waitMs, CloseWaitMs);

        /// <summary>A hold on the running copy's process, taken before
        /// <c>exit</c> goes down the control pipe, whose wait of up to
        /// <see cref="CloseWaitMs"/> for its end is true when it ended (#600
        /// item 44). Null when no copy can be held.</summary>
        internal static Hold<bool> HoldForClose()
        {
            if (OffLimitsForTest) return null;
            IntPtr hwnd = Find(SignalWindowName);
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            IntPtr process = pid == 0 ? IntPtr.Zero : OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return null;
            return new Hold<bool>(() => WaitForSingleObject(process, (uint)CloseWaitMs) == WAIT_OBJECT_0, () => CloseHandle(process));
        }

        /// <summary>A hold on the running copy's process: the wait for it,
        /// and the handle, which is let go once, when the wait ends or at
        /// <see cref="Dispose"/>, whichever comes first. A hold that is never
        /// waited for is disposed, so no handle outlives the run that took
        /// it.</summary>
        internal sealed class Hold<T> : IDisposable
        {
            private readonly Func<T> _wait;
            private Action _release;

            internal Hold(Func<T> wait, Action release)
            {
                _wait = wait;
                _release = release ?? (() => { });
            }

            /// <summary>Waits, then lets go. A hold already let go waits for
            /// nothing.</summary>
            internal T Wait()
            {
                if (Volatile.Read(ref _release) == null) return default;
                try { return _wait(); }
                finally { Dispose(); }
            }

            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }

        /// <summary>The same ask, to the signal window, pipe and message of
        /// the names given. A test gives names of its own, and the fences in
        /// <see cref="FindChecked"/> and <see cref="Ask"/> keep the app's from
        /// the test host.</summary>
        internal static Closing Exit(string signalName, string exitMessageName, int waitMs, int closeMs)
        {
            IntPtr hwnd = WaitForSignalWindow(waitMs, signalName);
            if (hwnd == IntPtr.Zero) return Closing.NotRunning;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return Closing.NotRunning;
            // The hold is taken before the message goes, so the process
            // waited for is the one asked, whatever starts after it.
            IntPtr process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            try
            {
                if (!Ask(hwnd, signalName, exitMessageName, ExitLine, AskMs)) return Closing.StillRunning;
                if (process != IntPtr.Zero)
                    return WaitForSingleObject(process, (uint)Math.Max(0, closeMs)) == WAIT_OBJECT_0 ? Closing.Closed : Closing.StillRunning;
                // Without a hold the end is the signal window's, which stands
                // until the process ends (InstanceSignal.MarkClosing).
                var clock = Stopwatch.StartNew();
                while (FindChecked(signalName) == hwnd)
                {
                    if (clock.ElapsedMilliseconds >= closeMs) return Closing.StillRunning;
                    Thread.Sleep(100);
                }
                return Closing.Closed;
            }
            finally
            {
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }

        // ── The start switches of a start through the logon task (#600 item 44) ──

        /// <summary>The most a start's switches line may take, in bytes of
        /// UTF-8: the line a pipe of <see cref="ExternalControlService"/>
        /// keeps.</summary>
        internal const int MostStartSwitchBytes = ExternalControlService.LongestRequest;

        /// <summary>The name of the event a start holds while the copy it
        /// started through the logon task is to wait for its switches. It
        /// lives in the session's namespace, beside the signal window of the
        /// same name.</summary>
        internal static string StartPendingName(string signalName) => @"Local\" + signalName + ".Switches";

        /// <summary>Says to the copy about to start that its switches are
        /// coming, until disposed. Null in the test host for the app's own
        /// name, or when the event cannot be made.</summary>
        internal static IDisposable MarkStartPending(string signalName = SignalWindowName)
        {
            if (OffLimitsForTest && IsTheAppsOwn(signalName)) return null;
            try { return new EventWaitHandle(false, EventResetMode.ManualReset, StartPendingName(signalName)); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or System.IO.IOException)
            {
                return null;
            }
        }

        /// <summary>Whether a start holds the event of
        /// <see cref="MarkStartPending"/> for this signal window. The name is
        /// in the session's namespace, where any program of the user can make
        /// it first, so one this copy cannot open is no pending start, and the
        /// question never throws.</summary>
        internal static bool StartIsPending(string signalName)
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(StartPendingName(signalName), out var pending)) return false;
                pending.Dispose();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Waits up to <paramref name="waitMs"/> for the checked
        /// signal window of a copy that is starting, which a copy without
        /// rights cannot see by the lock. False when none came.</summary>
        internal static bool WaitForStartedCopy(int waitMs, string signalName = SignalWindowName)
        {
            var clock = Stopwatch.StartNew();
            while (FindChecked(signalName) == IntPtr.Zero)
            {
                if (clock.ElapsedMilliseconds >= waitMs) return false;
                Thread.Sleep(100);
            }
            return true;
        }

        /// <summary>
        /// Hands the start's switches to the copy the logon task started,
        /// with this process's right to come to the front, over the signal
        /// pipe of its checked signal window: one line, <see cref="SwitchesLine"/>
        /// and the words as a JSON array. True when the copy took them.
        /// </summary>
        internal static bool HandStartSwitches(IReadOnlyList<string> words, int answerMs, string signalName = SignalWindowName)
        {
            IntPtr hwnd = FindChecked(signalName);
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0) AllowSetForegroundWindow(pid);
            string line = SwitchesLine + " " + System.Text.Json.JsonSerializer.Serialize(words ?? Array.Empty<string>());
            if (System.Text.Encoding.UTF8.GetByteCount(line) > MostStartSwitchBytes) return false;
            return Signal(signalName, line, answerMs);
        }

        /// <summary>The words of a switches line's JSON array, or null for
        /// one that is not an array of strings.</summary>
        internal static string[] ReadStartSwitches(string json)
        {
            try
            {
                string[] words = System.Text.Json.JsonSerializer.Deserialize<string[]>(json ?? string.Empty);
                if (words == null) return null;
                foreach (string word in words)
                    if (word == null) return null;
                return words;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Takes hold of the running copy now, and answers with the hold,
        /// whose wait for its exit gives its exit code. The hold is taken
        /// before any line is sent, so a copy told to exit is still the one
        /// waited for. Null when there is no copy to wait for or Windows does
        /// not let this process watch it.
        /// </summary>
        internal static Hold<int?> Watch(int waitMs = 10000)
        {
            if (OffLimitsForTest) return null;
            IntPtr hwnd = WaitForSignalWindow(waitMs);
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            IntPtr process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return null;
            return new Hold<int?>(() =>
            {
                WaitForSingleObject(process, INFINITE);
                return GetExitCodeProcess(process, out uint code) ? unchecked((int)code) : (int?)null;
            }, () => CloseHandle(process));
        }
    }
}
