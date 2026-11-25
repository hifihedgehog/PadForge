using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PadForge.Views
{
    /// <summary>
    /// Where the flyout family stands (#600 item 42): the profile flyout, the
    /// controllers on or off note, the device notes and the shift-layer
    /// flyout. Each shows at the bottom center of the work area of the
    /// monitor that holds the window in front, so a game on a second monitor
    /// gets its note there, not on the primary one.
    ///
    /// <para>The window is put in that monitor's own pixels with
    /// SetWindowPos, the way the question card is
    /// (<see cref="AutomationRequestCard"/>'s Place): a WPF Left and Top are
    /// DIPs at the scale of the monitor the window was on, which lands wrong
    /// on a monitor of another scale. A window that crosses to a monitor of
    /// another scale is sized again by WPF, and its owner places it again
    /// from its SizeChanged.</para>
    /// </summary>
    internal static class FlyoutPlacement
    {
        /// <summary>A monitor's work area in its own pixels, and its
        /// scale.</summary>
        internal readonly record struct WorkArea(int Left, int Top, int Right, int Bottom, double Scale)
        {
            public int Width => Right - Left;
        }

        /// <summary>Test seam: the window in front, in place of
        /// GetForegroundWindow. Null in the app.</summary>
        internal static Func<IntPtr> ForegroundSeam;

        /// <summary>Room the flyout's panel, border, padding and glyph take
        /// beside its line, and 16 more to keep it off the monitor's
        /// sides, in DIPs.</summary>
        internal const double Beside = 96;

        /// <summary>The least width the line is given, in DIPs.</summary>
        internal const double LeastLine = 160;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        private const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        /// <summary>The work area of the monitor that holds the window in
        /// front, or of the primary monitor when no window is in front (the
        /// desktop, a locked session) or the monitor cannot be read.</summary>
        internal static WorkArea Front()
        {
            IntPtr front = ForegroundSeam != null ? ForegroundSeam() : GetForegroundWindow();
            IntPtr monitor = front != IntPtr.Zero
                ? MonitorFromWindow(front, MONITOR_DEFAULTTONEAREST)
                : MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                // What WPF knows of the primary monitor, at the scale of 1.
                var area = SystemParameters.WorkArea;
                return new WorkArea((int)area.Left, (int)area.Top, (int)area.Right, (int)area.Bottom, 1.0);
            }
            double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
            return new WorkArea(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom, scale);
        }

        /// <summary>The widest the flyout's one line may be on a monitor, in
        /// DIPs: a longer line, a burst that names many devices or a long
        /// profile name, ends in an ellipsis.</summary>
        internal static double LineWidth(WorkArea area) => Math.Max(LeastLine, area.Width / area.Scale - Beside);

        /// <summary>Where a window of a size in DIPs goes, in the monitor's
        /// pixels: centered across the work area, its bottom on the work
        /// area's bottom. The flyout's own bottom margin keeps it off the
        /// taskbar.</summary>
        internal static (int X, int Y) BottomCenter(WorkArea area, double width, double height)
        {
            int w = (int)Math.Ceiling(width * area.Scale), h = (int)Math.Ceiling(height * area.Scale);
            return (area.Left + (area.Width - w) / 2, area.Bottom - h);
        }

        /// <summary>Puts a shown window at the bottom center of a work
        /// area. A window with no handle yet stays where it is.</summary>
        internal static void Place(Window window, WorkArea area)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var (x, y) = BottomCenter(area, window.ActualWidth, window.ActualHeight);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }
}
