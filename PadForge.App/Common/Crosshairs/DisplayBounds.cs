using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace PadForge.Common.Crosshairs
{
    /// <summary>
    /// The displays' bounds in pixels for the crosshairs and the magnifier
    /// (#585), read from Windows on each call (EnumDisplayMonitors and
    /// GetMonitorInfo). WinForms' Screen keeps its list until its own notice
    /// of a change arrives, and a window answering WM_DISPLAYCHANGE can run
    /// before that notice. A display is named as Screen Color names one
    /// (ScreenDisplayChoice): by the MONITORINFOEX szDevice that
    /// Screen.DeviceName reads, such as \\.\DISPLAY2.
    /// </summary>
    internal static class DisplayBounds
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfoEx(IntPtr monitor, ref MONITORINFOEX info);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        private const uint MONITORINFOF_PRIMARY = 1;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>Test seam: a display's bounds by name. Null in
        /// production.</summary>
        internal static Func<string, Int32Rect> Seam;

        /// <summary>The bounds of the display Windows calls
        /// <paramref name="name"/>. Empty, or a display that is not
        /// connected, gives the primary display. With no display at all, a
        /// rectangle of no size.</summary>
        public static Int32Rect Of(string name)
        {
            if (Seam != null) return Seam(name);
            Int32Rect primary = default, named = default;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfoEx(monitor, ref info)) return true;
                var bounds = ToRect(info.rcMonitor);
                if ((info.dwFlags & MONITORINFOF_PRIMARY) != 0) primary = bounds;
                if (!string.IsNullOrEmpty(name) && string.Equals(info.szDevice, name, StringComparison.OrdinalIgnoreCase))
                    named = bounds;
                return true;
            }, IntPtr.Zero);
            return named.Width > 0 ? named : primary;
        }

        /// <summary>The bounds of the display a point is on, or the nearest
        /// one. Called once a frame while the magnifier follows the pointer,
        /// so it reads the plain MONITORINFO and makes no string.</summary>
        public static Int32Rect At(int x, int y)
        {
            if (Seam != null) return Seam(null);
            var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? ToRect(info.rcMonitor) : default;
        }

        private static Int32Rect ToRect(RECT r)
            => new(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));
    }
}
