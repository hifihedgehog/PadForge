using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace PadForge.Common.Crosshairs
{
    /// <summary>
    /// The Magnification API and the magnifier's two plain windows (#585):
    /// the lens's host, which holds a magnifier control, and the spotlight's
    /// shade. Both are made as Microsoft's windowed sample makes its host
    /// (Windows-classic-samples, Samples/Magnification/cpp/Windowed/
    /// MagnifierSample.cpp, SetupMagnifier): WS_EX_TOPMOST | WS_EX_LAYERED,
    /// with SetLayeredWindowAttributes, and WS_EX_TRANSPARENT so clicks pass
    /// through as in its full-screen mode (GoFullScreen). They add
    /// WS_EX_NOACTIVATE and WS_EX_TOOLWINDOW, as every PadForge overlay does.
    /// Values from the Windows SDK's magnification.h (10.0.26100.0).
    /// </summary>
    internal static class MagnifierNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X, Y; }

        /// <summary>magnification.h MAGTRANSFORM: float v[3][3], row by row.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MAGTRANSFORM
        {
            public float V00, V01, V02, V10, V11, V12, V20, V21, V22;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        // magnification.h
        private const string WC_MAGNIFIER = "Magnifier";
        private const int MS_SHOWMAGNIFIEDCURSOR = 0x0001;
        private const uint MW_FILTERMODE_EXCLUDE = 0;

        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint LWA_ALPHA = 0x2;
        private const int SW_SHOWNOACTIVATE = 4;
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const uint WM_NCHITTEST = 0x0084;
        private const int MA_NOACTIVATE = 3;
        private const int HTTRANSPARENT = -1;
        private const int BLACK_BRUSH = 4;
        private const int RGN_DIFF = 4;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MagInitialize();

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MagUninitialize();

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MagSetWindowSource(IntPtr hwnd, RECT rect);

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MagSetWindowTransform(IntPtr hwnd, ref MAGTRANSFORM transform);

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MagSetWindowFilterList(IntPtr hwnd, uint mode, int count, IntPtr[] list);

        [DllImport("Magnification.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MagSetFullscreenTransform(float magLevel, int xOffset, int yOffset);

        /// <summary>Set by the test host (TestHostFences): Windows'
        /// full-screen zoom is left as it is, since it changes the whole
        /// desktop. A test hands its own recorder
        /// (<see cref="FullScreenSeam"/>).</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: what the full-screen zoom is set to while the
        /// fence is up. Null refuses every call.</summary>
        internal static Func<float, int, int, bool> FullScreenSeam;

        /// <summary>Windows' full-screen zoom: <paramref name="zoom"/> with the
        /// view's corner at the offsets, in unmagnified pixels from the primary
        /// display's corner (MagSetFullscreenTransform). A zoom of 1 at 0, 0 is
        /// none.</summary>
        internal static bool SetFullScreen(float zoom, int x, int y)
        {
            if (OffLimitsForTest) return FullScreenSeam?.Invoke(zoom, x, y) ?? false;
            return MagSetFullscreenTransform(zoom, x, y);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT point);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int index);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        private const string HostClass = "PadForgeMagnifierHost";

        // Kept for the life of the process: Windows calls it for every
        // window of the class.
        private static readonly WndProcDelegate s_wndProc = HostWndProc;
        private static bool s_registered;

        /// <summary>The windows take neither the focus nor a click.</summary>
        private static IntPtr HostWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_MOUSEACTIVATE) return (IntPtr)MA_NOACTIVATE;
            if (msg == WM_NCHITTEST) return (IntPtr)HTTRANSPARENT;
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private static bool EnsureClass()
        {
            if (s_registered) return true;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandle(null),
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszClassName = HostClass,
            };
            s_registered = RegisterClassEx(ref wc) != 0;
            return s_registered;
        }

        private static IntPtr CreateHost(Int32Rect rect, byte alpha)
        {
            if (!EnsureClass()) return IntPtr.Zero;
            var hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                HostClass, "PadForge Magnifier", WS_POPUP | WS_CLIPCHILDREN,
                rect.X, rect.Y, rect.Width, rect.Height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (hwnd != IntPtr.Zero) SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
            return hwnd;
        }

        /// <summary>Makes the lens: a host window on <paramref name="rect"/>,
        /// cut to a circle or an ellipse when <paramref name="round"/>, and a
        /// magnifier control filling it that shows the pointer magnified,
        /// zoomed by <paramref name="zoom"/> through a scale matrix, as
        /// SetupMagnifier does. The control is the second handle. Both are
        /// zero when Windows refused, with nothing left behind.</summary>
        internal static (IntPtr Host, IntPtr View) CreateLens(Int32Rect rect, bool round, double zoom)
        {
            var host = CreateHost(rect, 255);
            if (host == IntPtr.Zero) return default;
            var view = CreateWindowEx(0, WC_MAGNIFIER, "PadForgeMagnifier", WS_CHILD | MS_SHOWMAGNIFIEDCURSOR | WS_VISIBLE,
                0, 0, rect.Width, rect.Height, host, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (view == IntPtr.Zero || !SetZoom(view, zoom))
            {
                DestroyWindow(host);
                return default;
            }
            if (round) SetWindowRgn(host, CreateEllipticRgn(0, 0, rect.Width, rect.Height), false);
            return (host, view);
        }

        /// <summary>A new size for the lens: the host on
        /// <paramref name="rect"/>, the control filling it, and the circle or
        /// ellipse cut again.</summary>
        internal static void ResizeLens(IntPtr host, IntPtr view, Int32Rect rect, bool round)
        {
            Place(host, rect);
            SetWindowPos(view, IntPtr.Zero, 0, 0, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            SetWindowRgn(host, round ? CreateEllipticRgn(0, 0, rect.Width, rect.Height) : IntPtr.Zero, true);
        }

        /// <summary>How see-through the shade is.</summary>
        internal static void SetAlpha(IntPtr hwnd, byte alpha) => SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);

        /// <summary>The control's zoom, MagSetWindowTransform with
        /// v[0][0] = v[1][1] = the zoom and v[2][2] = 1.</summary>
        internal static bool SetZoom(IntPtr view, double zoom)
        {
            var matrix = new MAGTRANSFORM { V00 = (float)zoom, V11 = (float)zoom, V22 = 1f };
            return MagSetWindowTransform(view, ref matrix);
        }

        /// <summary>Leaves these windows out of what the lens shows. The
        /// control leaves itself out (MagSetWindowFilterList remarks).</summary>
        internal static void Exclude(IntPtr view, IntPtr[] windows)
        {
            if (view != IntPtr.Zero && windows != null && windows.Length > 0)
                MagSetWindowFilterList(view, MW_FILTERMODE_EXCLUDE, windows.Length, windows);
        }

        /// <summary>One frame of the lens, as the sample's timer does it
        /// (UpdateMagWindow): the area to magnify, then a repaint, which takes
        /// a new picture of it.</summary>
        internal static void ShowSource(IntPtr view, Int32Rect source)
        {
            MagSetWindowSource(view, new RECT { Left = source.X, Top = source.Y, Right = source.X + source.Width, Bottom = source.Y + source.Height });
            InvalidateRect(view, IntPtr.Zero, true);
        }

        /// <summary>Makes the spotlight's shade: a black window on the
        /// display, see-through to <paramref name="alpha"/>.</summary>
        internal static IntPtr CreateShade(Int32Rect display, byte alpha) => CreateHost(display, alpha);

        /// <summary>Cuts <paramref name="hole"/>, in screen pixels, out of a
        /// window that sits on <paramref name="rect"/>.</summary>
        internal static void CutHole(IntPtr hwnd, Int32Rect rect, Int32Rect hole)
        {
            var region = CreateRectRgn(0, 0, rect.Width, rect.Height);
            var cut = CreateRectRgn(hole.X - rect.X, hole.Y - rect.Y, hole.X - rect.X + hole.Width, hole.Y - rect.Y + hole.Height);
            CombineRgn(region, region, cut, RGN_DIFF);
            DeleteObject(cut);
            // Windows owns the region from here.
            SetWindowRgn(hwnd, region, true);
        }

        internal static void Show(IntPtr hwnd)
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        internal static void Place(IntPtr hwnd, Int32Rect rect)
            => SetWindowPos(hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);

        internal static void MoveTo(IntPtr hwnd, int x, int y)
            => SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        internal static void Destroy(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        }
    }
}
