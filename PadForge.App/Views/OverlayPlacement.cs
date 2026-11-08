using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PadForge.Views
{
    /// <summary>
    /// Where an overlay window sits, for the Touchpad Overlay, the Screen
    /// Overlay and the On-Screen Keyboard. Window.Left/Top/Width/Height are DIPs scaled to the target
    /// monitor's DPI. Screen.Bounds and WorkingArea are physical pixels. Mixing
    /// them silently breaks on non-100% displays (e.g. on a 250% 4K monitor a
    /// centered-by-px window lands ~1.5x past the edge).
    /// </summary>
    internal static class OverlayPlacement
    {
        /// <summary>A coordinate that means no saved place. A drag keeps the
        /// pointer on a monitor and Reset Position centers on one, so no window
        /// gets this far off, and every nearer number is a place: a monitor left
        /// of or above the primary has negative ones. It is a number because the
        /// view models refuse NaN.</summary>
        public const double NoPlace = -100000;

        public static bool HasPlace(double left, double top) => left != NoPlace && top != NoPlace;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;

        private static double GetMonitorScaleAtPoint(int physicalX, int physicalY)
        {
            var pt = new POINT { X = physicalX, Y = physicalY };
            var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (hMon == IntPtr.Zero) return 1.0;
            if (GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) != 0) return 1.0;
            return dpiX / 96.0;
        }

        /// <summary>Centers the window on the specified monitor's working
        /// area.</summary>
        public static void MoveToMonitor(Window window, int monitorIndex)
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (monitorIndex < 0 || monitorIndex >= screens.Length)
                monitorIndex = 0;

            var bounds = screens[monitorIndex].WorkingArea;

            // Convert physical-px bounds to DIPs at the target monitor's
            // effective DPI before assigning to Window.Left/Top.
            int cxPx = bounds.Left + bounds.Width / 2;
            int cyPx = bounds.Top + bounds.Height / 2;
            double scale = GetMonitorScaleAtPoint(cxPx, cyPx);

            double leftDip = bounds.Left / scale;
            double topDip = bounds.Top / scale;
            double widthDip = bounds.Width / scale;
            double heightDip = bounds.Height / scale;

            window.Left = leftDip + (widthDip - window.Width) / 2;
            window.Top = topDip + (heightDip - window.Height) / 2;
        }

        /// <summary>
        /// If the window's physical-px rect doesn't intersect any monitor's
        /// working area (e.g. saved position from a now-detached display, or
        /// stale coords written by an earlier broken centering routine), re-
        /// center on the requested monitor. Cheap to call after every Show().
        /// </summary>
        public static void EnsureOnScreen(Window window, int preferredMonitor)
        {
            if (!IsOnScreen(window)) MoveToMonitor(window, preferredMonitor);
        }

        /// <summary>True when the window's physical-px rect overlaps any
        /// monitor's working area, or when it has no window rect yet.</summary>
        public static bool IsOnScreen(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r))
                return true;

            var screens = System.Windows.Forms.Screen.AllScreens;
            foreach (var screen in screens)
            {
                var b = screen.WorkingArea;
                if (r.Right > b.Left && r.Left < b.Right &&
                    r.Bottom > b.Top && r.Top < b.Bottom)
                    return true; // any overlap is enough
            }
            return false;
        }

        /// <summary>Returns the monitor index the window's center point is on.</summary>
        public static int GetCurrentMonitor(Window window)
        {
            // Ask Win32 for the window rect directly in physical px. That sidesteps
            // any DIP/virtual-screen-space ambiguity that would come from
            // converting Window.Left/Top by hand.
            var hwnd = new WindowInteropHelper(window).Handle;
            int cxPx, cyPx;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT r))
            {
                cxPx = (r.Left + r.Right) / 2;
                cyPx = (r.Top + r.Bottom) / 2;
            }
            else
            {
                // Window has no HWND yet (pre-Show). Fall back to DIP×scale at
                // the nearest monitor, a seed until the first show.
                double cxDip = window.Left + window.Width / 2;
                double cyDip = window.Top + window.Height / 2;
                double scale = GetMonitorScaleAtPoint((int)cxDip, (int)cyDip);
                cxPx = (int)(cxDip * scale);
                cyPx = (int)(cyDip * scale);
            }

            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                if (cxPx >= b.Left && cxPx < b.Right && cyPx >= b.Top && cyPx < b.Bottom)
                    return i;
            }
            return 0;
        }
    }
}
