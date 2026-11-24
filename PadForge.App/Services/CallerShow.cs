using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// How PadForge shows its window for a caller (#597): a second start of
    /// the exe, the <c>show</c> command and the posted show message.
    ///
    /// <para>A show that comes within <see cref="GapMs"/> of the last one is
    /// ignored, so a program that posts the message in a loop cannot keep
    /// throwing the window back over everything else. The window comes
    /// forward with the right to the foreground the caller lent
    /// (RunningCopy.LendForeground, AllowSetForegroundWindow), and where
    /// none was lent Windows keeps the window where it is and its taskbar
    /// button flashes until the user turns to it. The window is never made
    /// topmost for a caller, which is how a program without that right would
    /// otherwise take the front.</para>
    /// </summary>
    internal sealed class CallerShow
    {
        /// <summary>How long after one show the next is ignored.</summary>
        internal const int GapMs = 2000;

        private readonly Func<long> _nowMs;
        private long _lastMs = long.MinValue / 2;

        public CallerShow(Func<long> nowMs = null) => _nowMs = nowMs ?? (() => Environment.TickCount64);

        /// <summary>Whether this show goes ahead. True for the first and for
        /// one that comes <see cref="GapMs"/> or more after the last one
        /// that went ahead.</summary>
        internal bool Take()
        {
            long now = _nowMs();
            long last = Interlocked.Read(ref _lastMs);
            if (now - last < GapMs) return false;
            return Interlocked.CompareExchange(ref _lastMs, now, last) == last;
        }

        /// <summary>Test seams: the calls to Windows. Null in the
        /// app.</summary>
        internal static Func<IntPtr> ForegroundSeam;
        internal static Func<IntPtr, bool> SetForegroundSeam;
        internal static Action<IntPtr> FlashSeam;

        /// <summary>Brings the window to the front with the right the caller
        /// lent. False, and the taskbar button flashes, when Windows refused
        /// it.</summary>
        internal static bool ComeForward(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var foreground = ForegroundSeam ?? GetForegroundWindow;
            if (foreground() == hwnd) return true;
            var set = SetForegroundSeam ?? SetForegroundWindow;
            if (set(hwnd) && foreground() == hwnd) return true;
            (FlashSeam ?? Flash)(hwnd);
            return false;
        }

        private static void Flash(IntPtr hwnd)
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = 0,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }

        private const uint FLASHW_TRAY = 0x00000002;
        private const uint FLASHW_TIMERNOFG = 0x0000000C;

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO info);
    }
}
