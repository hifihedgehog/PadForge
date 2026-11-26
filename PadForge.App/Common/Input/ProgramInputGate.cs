using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Holds PadForge's keyboard and mouse output back from windows that run
    /// with rights when a program fed it (#597, #600).
    ///
    /// <para>PadForge runs with administrator rights, so the keys and clicks
    /// it sends reach windows that a program without rights cannot reach
    /// itself: Windows' UIPI lets input down the integrity levels only. A
    /// program at Send Inputs can drive that output through a hold on a
    /// control, a device of its own or a script value a macro reads. Every
    /// event PadForge sends carries its cause (OutputCause): the person's
    /// own devices alone, a program's session, or nothing proven. An event
    /// not proven the person's own is held when it is a key press, a button
    /// press, a wheel turn or a move and the window in front belongs to a
    /// process above medium integrity, and a button press or a wheel turn is
    /// held as well while the window under the pointer does, since a click
    /// lands where the pointer is. Releases always go out, so nothing stays
    /// held. With no window in front at all, as on the secure desktop of a
    /// Windows prompt or the lock screen, nothing of it but a release goes
    /// out. The person's own output goes out whatever window is in front,
    /// while a program drives PadForge or not.</para>
    ///
    /// <para>The window's process is read when the window changes and not on
    /// every send, the way ForegroundMonitorService reads the program in
    /// front: the window and its process ID are two cheap calls, and the
    /// token is opened only for a pair not seen last time. A process whose
    /// token cannot be read counts as above medium. A batch of the person's
    /// own output, or of releases alone, never asks for the window.</para>
    ///
    /// <para>Every way PadForge's output reaches Windows asks here: the
    /// output queue's send (InputManager.SendQueued, which carries the
    /// macros, the Keyboard + Mouse virtual controller and the On-Screen
    /// Keyboard), the virtual HID keyboard and mouse senders
    /// (KbmHidKeyboard, KbmHidMouse) and the cursor writes
    /// (KeyboardMouseVirtualController.SetCursorPos,
    /// CursorControlService.SetCursorPos). The handheld chord's replays are
    /// left alone: they replay keys the user pressed on the PC's own
    /// keyboard, which the low-level hook took, and the hook lets every
    /// injected event pass by without a replay.</para>
    /// </summary>
    internal static class ProgramInputGate
    {

        private static int _liveProgramDevices;

        /// <summary>Test seam: stands in for <see cref="InForce"/>. Null in
        /// the app.</summary>
        internal static Func<bool> InForceSeam;

        /// <summary>Test seam: the window in front. Null in the app.</summary>
        internal static Func<IntPtr> ForegroundSeam;

        /// <summary>Test seam: the top-level window under the pointer. Null
        /// in the app.</summary>
        internal static Func<IntPtr> PointerWindowSeam;

        /// <summary>Test seam: a window's process ID. Null in the app.</summary>
        internal static Func<IntPtr, uint> ProcessOfSeam;

        /// <summary>Test seam: a process's integrity level's RID, or null
        /// when its token cannot be read. Null in the app.</summary>
        internal static Func<uint, int?> IntegritySeam;

        /// <summary>Test seam: the extra information of the message the
        /// interface's thread handles now. Null in the app.</summary>
        internal static Func<IntPtr> ExtraInfoSeam;

        /// <summary>How many integrity reads were made, for the test that
        /// holds the read to a change of window.</summary>
        internal static int IntegrityReads;

        /// <summary>Whether a program drives PadForge now: it holds a
        /// control, has a device of its own online, or has a script value
        /// set.</summary>
        internal static bool InForce
        {
            get
            {
                var seam = InForceSeam;
                if (seam != null) return seam();
                return ControlHolds.Current != null
                    || ControlHolds.Pending
                    || Volatile.Read(ref _liveProgramDevices) > 0
                    || ScriptValues.AnySet;
            }
        }

        /// <summary>Whether some slot's output carries a program's mark on
        /// the last pass, which can outlast every hold, device and value
        /// (#600). Written by the poll thread.</summary>
        internal static volatile bool MarksKept;

        /// <summary>A registry of program devices changed its count of
        /// devices online by <paramref name="delta"/>.</summary>
        internal static void ProgramDevicesChanged(int delta)
        {
            if (delta != 0) Interlocked.Add(ref _liveProgramDevices, delta);
        }

        /// <summary>Whether a key press or a move not proven the person's
        /// own is held now: the window in front runs above medium
        /// integrity.</summary>
        internal static bool HoldsKeys() => Foreground.AboveMedium(ForegroundWindow());

        /// <summary>Whether a button press or a wheel turn not proven the
        /// person's own is held now: as <see cref="HoldsKeys()"/>, or the
        /// window under the pointer runs above medium integrity.</summary>
        internal static bool HoldsButtons() => HoldsKeys() || Pointer.AboveMedium(PointerWindow());

        /// <summary>Whether a key press or a move of
        /// <paramref name="cause"/> is held now.</summary>
        internal static bool HoldsKeys(OutputCause cause) => !cause.IsOwn && HoldsKeys();

        /// <summary>Whether a button press or a wheel turn of
        /// <paramref name="cause"/> is held now.</summary>
        internal static bool HoldsButtons(OutputCause cause) => !cause.IsOwn && HoldsButtons();

        private static int _heldCount;
        private static int _lastHeldProcess;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _sessionsHeld = new();

        /// <summary>Raised the first time output a session fed is held, with
        /// the session's number and the process ID of the window with rights
        /// it was held from, on the thread that held it. Must not
        /// block.</summary>
        internal static event Action<int, uint> FirstHeld;

        /// <summary>How many sends held something since PadForge
        /// started.</summary>
        internal static int HeldCount => Volatile.Read(ref _heldCount);

        /// <summary>The process ID of the window with rights output was held
        /// from last, 0 when none was.</summary>
        internal static uint LastHeldProcess => (uint)Volatile.Read(ref _lastHeldProcess);

        /// <summary>A sender held output of <paramref name="cause"/> from
        /// the window in front, or from the one under the pointer.</summary>
        internal static void NoteHeld(OutputCause cause)
        {
            Interlocked.Increment(ref _heldCount);
            uint process = Foreground.LastAboveProcess;
            if (process == 0) process = Pointer.LastAboveProcess;
            Volatile.Write(ref _lastHeldProcess, (int)process);
            int session = cause.Session;
            if (session > 0 && _sessionsHeld.TryAdd(session, 0)) FirstHeld?.Invoke(session, process);
        }

        /// <summary>The process of the window in front when it runs with
        /// rights and output was held from it last, else 0. Reads the window
        /// as the gate last read it, with no new probe.</summary>
        internal static uint HeldFromWindowInFront()
        {
            uint held = LastHeldProcess;
            return held != 0 && held == Foreground.LastAboveProcess ? held : 0;
        }

        /// <summary>Test seam: a program's name by process ID. Null in the
        /// app.</summary>
        internal static Func<uint, string> ProgramNameSeam;

        /// <summary>A process's program name as Windows describes its file
        /// ("Registry Editor"), else its image name. Null when the process
        /// is gone or 0.</summary>
        internal static string ProgramNameOf(uint process)
        {
            if (process == 0) return null;
            var seam = ProgramNameSeam;
            if (seam != null) return seam(process);
            string path = ProcessImage.PathOf(process);
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string description = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>Whether the message the interface's thread handles now
        /// is PadForge's own output while a program drives PadForge. The
        /// Allow and Don't Allow buttons ignore such a click or key.</summary>
        internal static bool RefusesOwnOutputNow()
        {
            if (!InForce) return false;
            var seam = ExtraInfoSeam;
            IntPtr extra = seam != null ? seam() : GetMessageExtraInfo();
            return IsOwnOutput(extra);
        }

        /// <summary>Whether an event's extra information is the stamp
        /// PadForge puts on its own output
        /// (InputHookManager.OutputTag).</summary>
        internal static bool IsOwnOutput(IntPtr extraInfo)
            => unchecked((uint)(long)extraInfo) == unchecked((uint)(long)PadForge.Engine.Common.InputHookManager.OutputTag);

        /// <summary>The last window read and what came of it, swapped whole
        /// so two senders never see half of one.</summary>
        private sealed class Seen
        {
            public Seen(IntPtr window, uint process, bool above)
            {
                Window = window;
                Process = process;
                Above = above;
            }

            public readonly IntPtr Window;
            public readonly uint Process;
            public readonly bool Above;
        }

        /// <summary>One window's answer, kept until the window changes.</summary>
        private sealed class WindowWatch
        {
            private Seen _seen;

            public bool AboveMedium(IntPtr window)
            {
                if (window == IntPtr.Zero) return true;
                uint process = ProcessOf(window);
                if (process == 0) return true;
                var seen = Volatile.Read(ref _seen);
                if (seen != null && seen.Window == window && seen.Process == process) return seen.Above;
                bool above = ProcessAboveMedium(process);
                Volatile.Write(ref _seen, new Seen(window, process, above));
                return above;
            }

            /// <summary>The process of the last window read when it ran
            /// above medium integrity, else 0.</summary>
            public uint LastAboveProcess
            {
                get
                {
                    var seen = Volatile.Read(ref _seen);
                    return seen != null && seen.Above ? seen.Process : 0;
                }
            }

            public void Forget() => Volatile.Write(ref _seen, null);
        }

        private static readonly WindowWatch Foreground = new();
        private static readonly WindowWatch Pointer = new();

        /// <summary>Test seam: forgets the windows read, so the next ask
        /// reads again.</summary>
        internal static void ForgetForTest()
        {
            Foreground.Forget();
            Pointer.Forget();
            IntegrityReads = 0;
            Volatile.Write(ref _heldCount, 0);
            Volatile.Write(ref _lastHeldProcess, 0);
            _sessionsHeld.Clear();
        }

        private static IntPtr ForegroundWindow()
        {
            var seam = ForegroundSeam;
            return seam != null ? seam() : GetForegroundWindow();
        }

        private static IntPtr PointerWindow()
        {
            var seam = PointerWindowSeam;
            if (seam != null) return seam();
            if (!GetCursorPos(out POINT p)) return IntPtr.Zero;
            IntPtr hwnd = WindowFromPoint(p);
            return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);
        }

        private static uint ProcessOf(IntPtr window)
        {
            var seam = ProcessOfSeam;
            if (seam != null) return seam(window);
            GetWindowThreadProcessId(window, out uint pid);
            return pid;
        }

        private static bool ProcessAboveMedium(uint pid)
        {
            Interlocked.Increment(ref IntegrityReads);
            var seam = IntegritySeam;
            int? integrity = seam != null ? seam(pid) : PadForge.Common.ProcessIntegrity.Of(pid);
            return integrity is not { } rid || rid > PadForge.Common.ProcessIntegrity.Medium;
        }

        private const uint GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetMessageExtraInfo();
    }
}
