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
    /// gets its note there, not on the primary one. The menu overlay and the
    /// On-Screen Keyboard take the same monitor and stand at their menu's
    /// Screen Position on it (<see cref="At"/>).
    ///
    /// <para>The window is put in that monitor's own pixels with
    /// SetWindowPos, the way the question card is
    /// (<see cref="AutomationRequestCard"/>'s Place): a WPF Left and Top are
    /// DIPs at the scale of the monitor the window was on, which lands wrong
    /// on a monitor of another scale. A window that crosses to a monitor of
    /// another scale keeps its place through the change itself
    /// (<see cref="KeepPlace"/>), whether WPF then changes its size or
    /// not.</para>
    /// </summary>
    internal static class FlyoutPlacement
    {
        /// <summary>A monitor's work area in its own pixels, and its
        /// scale.</summary>
        internal readonly record struct WorkArea(int Left, int Top, int Right, int Bottom, double Scale)
        {
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        /// <summary>Test seam: the window in front, in place of
        /// GetForegroundWindow. Null in the app.</summary>
        internal static Func<IntPtr> ForegroundSeam;

        /// <summary>Test seam: the work area of the monitor that holds a
        /// window, in place of what Windows says, for monitors this PC does
        /// not have. Null in the app.</summary>
        internal static Func<IntPtr, WorkArea> MonitorSeam;

        /// <summary>Test seam: true reads every monitor as one Windows
        /// cannot describe, as GetMonitorInfo fails for a monitor that went
        /// away between the two calls. False in the app.</summary>
        internal static bool MonitorUnreadSeam;

        /// <summary>Test seam: the system's dots per inch, in place of
        /// GetDpiForSystem. Null in the app.</summary>
        internal static Func<uint> SystemDpiSeam;

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

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);

        private const uint SPI_GETWORKAREA = 0x0030;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        private const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const int WM_DPICHANGED = 0x02E0;

        /// <summary>Where a window belongs at a new scale, for its size in
        /// that scale's pixels: its top left, or null where it has no place
        /// of its own just now.</summary>
        internal delegate (int X, int Y)? PlaceAtScale(int width, int height, double scale);

        /// <summary>Keeps a window where it belongs through a change of
        /// scale. Windows tells a window that reaches a monitor of another
        /// scale, or whose monitor changes scale, with WM_DPICHANGED and a
        /// rectangle it suggests at the new scale, and WPF moves the window
        /// to that rectangle after the window raises DpiChanged
        /// (HwndTarget.OnDpiChanged). This hook reads the message before WPF
        /// does and moves the rectangle to where <paramref name="belongs"/>
        /// puts a window of its size, so WPF's own move puts the window there,
        /// whether or not WPF changes its size after. The hook goes on as the
        /// window gets its handle, before a first placement can move
        /// it.</summary>
        internal static void KeepPlace(Window window, PlaceAtScale belongs)
        {
            var helper = new WindowInteropHelper(window);
            void Hook() => HwndSource.FromHwnd(helper.Handle)?.AddHook(
                (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == WM_DPICHANGED && lParam != IntPtr.Zero) Suggest(wParam, lParam, belongs);
                    return IntPtr.Zero;
                });
            if (helper.Handle != IntPtr.Zero) Hook();
            else window.SourceInitialized += (_, _) => Hook();
        }

        /// <summary>Moves the rectangle a WM_DPICHANGED suggests to where
        /// <paramref name="belongs"/> puts a window of its size, keeping the
        /// size. The new scale is in the low word of wParam, in dots per
        /// inch.</summary>
        private static void Suggest(IntPtr wParam, IntPtr lParam, PlaceAtScale belongs)
        {
            var rect = Marshal.PtrToStructure<RECT>(lParam);
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            double scale = (wParam.ToInt64() & 0xFFFF) / 96.0;
            if (belongs(width, height, scale) is not { } at) return;
            Marshal.StructureToPtr(new RECT { Left = at.X, Top = at.Y, Right = at.X + width, Bottom = at.Y + height }, lParam, false);
        }

        /// <summary>The work area of the monitor that holds the window in
        /// front, or of the primary monitor when no window is in front (the
        /// desktop, a locked session) or the monitor cannot be read.</summary>
        internal static WorkArea Front() => Of(ForegroundSeam != null ? ForegroundSeam() : GetForegroundWindow());

        /// <summary>The work area of the monitor that holds
        /// <paramref name="window"/>, or the one nearest it, or of the primary
        /// monitor for no window or a monitor that cannot be read.</summary>
        internal static WorkArea Of(IntPtr window)
        {
            if (MonitorSeam != null) return MonitorSeam(window);
            IntPtr monitor = window != IntPtr.Zero
                ? MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST)
                : MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || MonitorUnreadSeam || !GetMonitorInfo(monitor, ref info))
            {
                // The primary monitor's work area in its pixels, which WPF
                // reads with SPI_GETWORKAREA and then turns into DIPs at the
                // system's scale (SystemParameters.WorkArea), and that scale.
                double system = (SystemDpiSeam != null ? SystemDpiSeam() : GetDpiForSystem()) / 96.0;
                if (system <= 0) system = 1.0;
                var work = new RECT();
                if (SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0))
                    return new WorkArea(work.Left, work.Top, work.Right, work.Bottom, system);
                var area = SystemParameters.WorkArea;
                return new WorkArea((int)Math.Round(area.Left * system), (int)Math.Round(area.Top * system),
                    (int)Math.Round(area.Right * system), (int)Math.Round(area.Bottom * system), system);
            }
            double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
            return new WorkArea(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom, scale);
        }

        /// <summary>The work area of the monitor that holds a WPF window
        /// (<see cref="Of(IntPtr)"/>).</summary>
        internal static WorkArea Of(Window window) => Of(new WindowInteropHelper(window).Handle);

        /// <summary>The work area of the monitor that holds
        /// <paramref name="window"/> (<see cref="Of(IntPtr)"/>), in the DIPs of a
        /// window on that monitor, which WPF counts at that monitor's
        /// scale.</summary>
        internal static Rect DipsOf(IntPtr window)
        {
            var area = Of(window);
            return new Rect(area.Left / area.Scale, area.Top / area.Scale, area.Width / area.Scale, area.Height / area.Scale);
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
            => BottomCenterInPixels(area, (int)Math.Ceiling(width * area.Scale), (int)Math.Ceiling(height * area.Scale));

        /// <summary><see cref="BottomCenter"/> for a window whose size is
        /// already in the monitor's pixels.</summary>
        internal static (int X, int Y) BottomCenterInPixels(WorkArea area, int width, int height)
            => (area.Left + (area.Width - width) / 2, area.Bottom - height);

        /// <summary>Puts a shown window at the bottom center of a work
        /// area. A window with no handle yet stays where it is.</summary>
        internal static void Place(Window window, WorkArea area)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var (x, y) = BottomCenter(area, window.ActualWidth, window.ActualHeight);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>Where a window of a size in DIPs goes for a menu's Screen
        /// Position, in the monitor's pixels: its center at the percents
        /// across and down the work area, moved in as far as it takes for the
        /// whole window and a margin in DIPs around it to stay on the work
        /// area. A window too large for that keeps its left and top on
        /// it.</summary>
        internal static (int X, int Y) At(WorkArea area, double width, double height, double xPercent, double yPercent, double margin = 0)
            => AtInPixels(area, (int)Math.Ceiling(width * area.Scale), (int)Math.Ceiling(height * area.Scale), xPercent, yPercent,
                (int)Math.Round(margin * area.Scale, MidpointRounding.AwayFromZero));

        /// <summary><see cref="At"/> for a window whose size and margin are
        /// already in the monitor's pixels.</summary>
        internal static (int X, int Y) AtInPixels(WorkArea area, int width, int height, double xPercent, double yPercent, int margin = 0)
        {
            double cx = area.Left + area.Width * Math.Clamp(xPercent, 0, 100) / 100.0;
            double cy = area.Top + area.Height * Math.Clamp(yPercent, 0, 100) / 100.0;
            int minX = area.Left + margin, minY = area.Top + margin;
            int x = Math.Clamp((int)Math.Round(cx - width / 2.0, MidpointRounding.AwayFromZero), minX, Math.Max(minX, area.Right - margin - width));
            int y = Math.Clamp((int)Math.Round(cy - height / 2.0, MidpointRounding.AwayFromZero), minY, Math.Max(minY, area.Bottom - margin - height));
            return (x, y);
        }

        /// <summary>A point in a monitor's pixels as a Screen Position: whole
        /// percents across and down the work area, from 0 to 100.</summary>
        internal static (int X, int Y) PercentOf(WorkArea area, int x, int y)
            => ((int)Math.Round(Math.Clamp((x - area.Left) * 100.0 / Math.Max(1, area.Width), 0, 100)),
                (int)Math.Round(Math.Clamp((y - area.Top) * 100.0 / Math.Max(1, area.Height), 0, 100)));

        /// <summary>Puts a window of a size in DIPs at a Screen Position on a
        /// work area (<see cref="At"/>). A window with no handle yet stays
        /// where it is.</summary>
        internal static void PlaceAt(Window window, WorkArea area, double width, double height, double xPercent, double yPercent, double margin = 0)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var (x, y) = At(area, width, height, xPercent, yPercent, margin);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>Centers a shown window on a work area, in that monitor's
        /// pixels, moved down and across by a step in DIPs, for the Touchpad
        /// Overlay and a Screen Overlay that the user never placed. A move to
        /// a monitor of another scale changes the window's size during the
        /// move and leaves it where Windows suggests (WPF's
        /// HwndTarget.OnDpiChanged), so once moved the window is centered
        /// again at the size it then has.</summary>
        internal static void CenterOn(Window window, WorkArea area, double step = 0)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            window.UpdateLayout();
            int s = (int)Math.Round(step * area.Scale, MidpointRounding.AwayFromZero);
            var (x, y) = At(area, window.ActualWidth, window.ActualHeight, 50, 50);
            SetWindowPos(hwnd, IntPtr.Zero, x + s, y + s, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            if (!GetWindowRect(hwnd, out RECT rect)) return;
            (x, y) = AtInPixels(area, rect.Right - rect.Left, rect.Bottom - rect.Top, 50, 50);
            if (rect.Left != x + s || rect.Top != y + s)
                SetWindowPos(hwnd, IntPtr.Zero, x + s, y + s, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        /// <summary>Where a shown window stands, as a Screen Position: the
        /// work area of the monitor that holds it, and its center in percent
        /// of that work area. Null for a window with no handle yet.</summary>
        internal static (WorkArea Area, int X, int Y)? PositionOf(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect)) return null;
            var area = Of(hwnd);
            var (x, y) = PercentOf(area, rect.Left + (rect.Right - rect.Left) / 2, rect.Top + (rect.Bottom - rect.Top) / 2);
            return (area, x, y);
        }
    }
}
