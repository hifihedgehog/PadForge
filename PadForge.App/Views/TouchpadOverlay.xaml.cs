using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PadForge.Engine;

namespace PadForge.Views
{
    /// <summary>
    /// Transparent overlay window that captures touch input for DS4 touchpad emulation.
    /// Uses WS_EX_NOACTIVATE to prevent stealing focus from games.
    /// First touch = finger 0, second touch = finger 1 (no zones needed).
    /// Double-tap triggers touchpad click.
    /// Draggable via mouse on surface, resizable via corner grip.
    /// </summary>
    public partial class TouchpadOverlay : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // Touch tracking: first touch = finger 0, second = finger 1
        private readonly object _stateLock = new();
        private int? _finger0TouchId;
        private int? _finger1TouchId;
        private float _x0, _y0, _x1, _y1;
        private bool _down0, _down1;
        private bool _click;
        private DateTime _lastTapTime = DateTime.MinValue;
        private const double DoubleTapMs = 300;

        // Resize tracking
        private bool _isResizing;
        private Point _resizeStart;
        private double _resizeStartW, _resizeStartH;

        /// <summary>Fired when the user finishes dragging or resizing (position/size changed).</summary>
        public event Action PositionChanged;

        public TouchpadOverlay()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);

            UpdateSurfaceSize();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSurfaceSize();
        }

        private void UpdateSurfaceSize()
        {
            Surface.Width = ActualWidth;
            Surface.Height = ActualHeight;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return (IntPtr)MA_NOACTIVATE;
            }
            return IntPtr.Zero;
        }

        // ─────────────────────────────────────────────
        //  Drag (right-click mouse or three-finger touch)
        // ─────────────────────────────────────────────

        private int _activeTouchCount;
        private bool _isDragging;
        private Point _dragStartScreen;
        private double _dragStartLeft, _dragStartTop;

        private bool _isMouseDragging;

        private void Surface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isMouseDragging = true;
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            _dragStartLeft = Left;
            _dragStartTop = Top;
            Surface.CaptureMouse();
            e.Handled = true;
        }

        private void Surface_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isMouseDragging) return;
            var current = PointToScreen(e.GetPosition(this));
            // PointToScreen returns physical screen px; Window.Left/Top are
            // DIPs at the window's current monitor DPI. Divide the delta by
            // the DPI scale before applying — otherwise on a 250% monitor
            // the window moves 2.5× the mouse, etc.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            Left = _dragStartLeft + (current.X - _dragStartScreen.X) / dpi.DpiScaleX;
            Top = _dragStartTop + (current.Y - _dragStartScreen.Y) / dpi.DpiScaleY;
        }

        private void Surface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isMouseDragging) return;
            _isMouseDragging = false;
            Surface.ReleaseMouseCapture();
            PositionChanged?.Invoke();
        }

        // ─────────────────────────────────────────────
        //  Resize (grip in bottom-right corner)
        // ─────────────────────────────────────────────

        private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isResizing = true;
            _resizeStart = PointToScreen(e.GetPosition(this));
            _resizeStartW = Width;
            _resizeStartH = Height;
            ResizeGrip.CaptureMouse();
            e.Handled = true;
        }

        private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isResizing) return;
            var current = PointToScreen(e.GetPosition(this));
            // Same px → DIP conversion as the drag handler. Width/Height are
            // DIPs at the window's monitor DPI.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double newW = _resizeStartW + (current.X - _resizeStart.X) / dpi.DpiScaleX;
            double newH = _resizeStartH + (current.Y - _resizeStart.Y) / dpi.DpiScaleY;
            Width = Math.Max(MinWidth, newW);
            Height = Math.Max(MinHeight, newH);
        }

        private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isResizing) return;
            _isResizing = false;
            ResizeGrip.ReleaseMouseCapture();
            PositionChanged?.Invoke();
        }

        // ─────────────────────────────────────────────
        //  Monitor
        // ─────────────────────────────────────────────

        // ── Per-monitor DPI helpers ─────────────────────────────────────
        // Window.Left/Top/Width/Height are DIPs scaled to the *target*
        // monitor's DPI. Screen.Bounds/WorkingArea are physical pixels.
        // Mixing them silently breaks on non-100% displays (e.g. on a 250%
        // 4K monitor a centered-by-px window lands ~1.5x past the edge).

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

        /// <summary>Moves the overlay to the specified monitor index.</summary>
        public void MoveToMonitor(int monitorIndex)
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

            Left = leftDip + (widthDip - Width) / 2;
            Top = topDip + (heightDip - Height) / 2;
        }

        /// <summary>
        /// If the window's physical-px rect doesn't intersect any monitor's
        /// working area (e.g. saved position from a now-detached display, or
        /// stale coords written by an earlier broken centering routine), re-
        /// center on the requested monitor. Cheap to call after every Show().
        /// </summary>
        public void EnsureOnScreen(int preferredMonitor)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r))
                return;

            var screens = System.Windows.Forms.Screen.AllScreens;
            foreach (var screen in screens)
            {
                var b = screen.WorkingArea;
                if (r.Right > b.Left && r.Left < b.Right &&
                    r.Bottom > b.Top && r.Top < b.Bottom)
                    return; // any overlap is enough
            }

            MoveToMonitor(preferredMonitor);
        }

        /// <summary>Returns the monitor index the overlay's center point is on.</summary>
        public int GetCurrentMonitor()
        {
            // Ask Win32 for the window rect directly in physical px — sidesteps
            // any DIP/virtual-screen-space ambiguity that would come from
            // converting Window.Left/Top by hand.
            var hwnd = new WindowInteropHelper(this).Handle;
            int cxPx, cyPx;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT r))
            {
                cxPx = (r.Left + r.Right) / 2;
                cyPx = (r.Top + r.Bottom) / 2;
            }
            else
            {
                // Window has no HWND yet (pre-Show). Fall back to DIP×scale at
                // the nearest monitor — good enough as a seed before first show.
                double cxDip = Left + Width / 2;
                double cyDip = Top + Height / 2;
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

        // ─────────────────────────────────────────────
        //  Surface opacity
        // ─────────────────────────────────────────────

        /// <summary>Sets the touchpad surface opacity (0.0 = invisible, 1.0 = opaque).</summary>
        public void SetSurfaceOpacity(double opacity)
        {
            Surface.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(
                    (byte)(Math.Clamp(opacity, 0.0, 1.0) * 255), 255, 255, 255));
        }

        // ─────────────────────────────────────────────
        //  Touch input
        // ─────────────────────────────────────────────

        protected override void OnTouchDown(TouchEventArgs e)
        {
            e.Handled = true;
            CaptureTouch(e.TouchDevice);
            _activeTouchCount++;

            // Three or more fingers: enter drag mode.
            if (_activeTouchCount >= 3 && !_isDragging)
            {
                _isDragging = true;
                var screenPos = PointToScreen(e.GetTouchPoint(this).Position);
                _dragStartScreen = screenPos;
                _dragStartLeft = Left;
                _dragStartTop = Top;
                return;
            }

            if (_isDragging) return;

            var pos = e.GetTouchPoint(this).Position;
            float nx = (float)(pos.X / ActualWidth);
            float ny = (float)(pos.Y / ActualHeight);

            lock (_stateLock)
            {
                if (_finger0TouchId == null)
                {
                    _finger0TouchId = e.TouchDevice.Id;
                    _x0 = nx; _y0 = ny; _down0 = true;
                }
                else if (_finger1TouchId == null)
                {
                    _finger1TouchId = e.TouchDevice.Id;
                    _x1 = nx; _y1 = ny; _down1 = true;
                }
            }
            UpdateFingerDots();
        }

        protected override void OnTouchMove(TouchEventArgs e)
        {
            e.Handled = true;

            if (_isDragging)
            {
                var screenPos = PointToScreen(e.GetTouchPoint(this).Position);
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                Left = _dragStartLeft + (screenPos.X - _dragStartScreen.X) / dpi.DpiScaleX;
                Top = _dragStartTop + (screenPos.Y - _dragStartScreen.Y) / dpi.DpiScaleY;
                return;
            }

            var pos = e.GetTouchPoint(this).Position;
            float nx = (float)(pos.X / ActualWidth);
            float ny = (float)(pos.Y / ActualHeight);

            lock (_stateLock)
            {
                if (_finger0TouchId == e.TouchDevice.Id)
                { _x0 = nx; _y0 = ny; }
                else if (_finger1TouchId == e.TouchDevice.Id)
                { _x1 = nx; _y1 = ny; }
            }
            UpdateFingerDots();
        }

        protected override void OnTouchUp(TouchEventArgs e)
        {
            e.Handled = true;
            ReleaseTouchCapture(e.TouchDevice);
            _activeTouchCount = Math.Max(0, _activeTouchCount - 1);

            if (_isDragging)
            {
                if (_activeTouchCount < 3)
                {
                    _isDragging = false;
                    PositionChanged?.Invoke();
                }
                return;
            }

            lock (_stateLock)
            {
                if (_finger0TouchId == e.TouchDevice.Id)
                {
                    _finger0TouchId = null;
                    _down0 = false;

                    var now = DateTime.UtcNow;
                    if ((now - _lastTapTime).TotalMilliseconds < DoubleTapMs)
                    {
                        _click = true;
                        _lastTapTime = DateTime.MinValue;
                    }
                    else
                    {
                        _lastTapTime = now;
                        _click = false;
                    }
                }
                else if (_finger1TouchId == e.TouchDevice.Id)
                {
                    _finger1TouchId = null;
                    _down1 = false;
                }
            }
            UpdateFingerDots();
        }

        /// <summary>Reads current overlay touchpad state. Called from polling thread.</summary>
        public TouchpadState GetTouchpadState()
        {
            lock (_stateLock)
            {
                var tp = new TouchpadState
                {
                    X0 = Math.Clamp(_x0, 0f, 1f),
                    Y0 = Math.Clamp(_y0, 0f, 1f),
                    X1 = Math.Clamp(_x1, 0f, 1f),
                    Y1 = Math.Clamp(_y1, 0f, 1f),
                    Down0 = _down0,
                    Down1 = _down1,
                    Click = _click
                };
                _click = false;
                return tp;
            }
        }

        private void UpdateFingerDots()
        {
            Dispatcher.BeginInvoke(() =>
            {
                lock (_stateLock)
                {
                    if (_down0)
                    {
                        Finger0Dot.Visibility = Visibility.Visible;
                        Canvas.SetLeft(Finger0Dot, _x0 * ActualWidth - 10);
                        Canvas.SetTop(Finger0Dot, _y0 * ActualHeight - 10);
                    }
                    else
                    {
                        Finger0Dot.Visibility = Visibility.Collapsed;
                    }

                    if (_down1)
                    {
                        Finger1Dot.Visibility = Visibility.Visible;
                        Canvas.SetLeft(Finger1Dot, _x1 * ActualWidth - 10);
                        Canvas.SetTop(Finger1Dot, _y1 * ActualHeight - 10);
                    }
                    else
                    {
                        Finger1Dot.Visibility = Visibility.Collapsed;
                    }
                }
            });
        }
    }
}
