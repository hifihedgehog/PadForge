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
    /// Transparent overlay window that captures touch input for PlayStation touchpad emulation.
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
        // Overlay supports up to OverlayMaxFingers per-slot tracking. The
        // first two slots feed the legacy DS4-shape TouchpadState struct
        // (consumed by the virtual-output side); all slots feed the new
        // TouchpadInputState the gesture engine reads. Slots get
        // allocated dynamically by TouchDevice.Id on TouchDown; freed on
        // TouchUp. Mouse-drag drives slot 0 only.
        private const int OverlaySlotCount = Engine.TouchpadOverlayDevice.OverlayMaxFingers;
        private readonly int?[] _slotTouchIds = new int?[OverlaySlotCount];
        private readonly float[] _slotX = new float[OverlaySlotCount];
        private readonly float[] _slotY = new float[OverlaySlotCount];
        private readonly bool[] _slotDown = new bool[OverlaySlotCount];
        private readonly int[] _slotContactIds = new int[OverlaySlotCount]; // -1 when empty
        private int _slotContactIdNext = 1;

        // Legacy two-finger shortcuts — kept as ref-projections into the
        // first two slots so the existing 2-finger-shaped UI code
        // (finger dots, click bar pulse, GetTouchpadState DS4 struct)
        // keeps reading the same place. Indexers can't return ref to
        // value-typed array elements in property form, so wrappers use
        // helper methods where needed.
        private int? _finger0TouchId { get => _slotTouchIds[0]; set => _slotTouchIds[0] = value; }
        private int? _finger1TouchId { get => _slotTouchIds[1]; set => _slotTouchIds[1] = value; }
        private float _x0 { get => _slotX[0]; set => _slotX[0] = value; }
        private float _y0 { get => _slotY[0]; set => _slotY[0] = value; }
        private float _x1 { get => _slotX[1]; set => _slotX[1] = value; }
        private float _y1 { get => _slotY[1]; set => _slotY[1] = value; }
        private bool _down0 { get => _slotDown[0]; set => _slotDown[0] = value; }
        private bool _down1 { get => _slotDown[1]; set => _slotDown[1] = value; }
        // Click bar: held while the user presses the bottom strip (mouse
        // or touch) — reported as a sustained Buttons[16]=true so
        // click-and-hold patterns (click-drag, sustained context input)
        // work. _clickPulse is the legacy double-tap-on-surface pulse,
        // single-frame, kept as a quick momentary fallback. Both feed
        // Buttons[16] via OR in GetTouchpadState.
        private bool _clickBarHeld;
        private int? _clickBarTouchId;
        private bool _clickPulse;
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
            for (int i = 0; i < OverlaySlotCount; i++) _slotContactIds[i] = -1;
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Self-heal any stale touch tracking from a previous session.
            // OnTouchUp can be skipped if the overlay is hidden mid-touch
            // (macro toggle, engine stop, screen lock, touch device disconnect)
            // — leaving _fingerNTouchId / _activeTouchIds / _isDragging in
            // states that block subsequent finger detection. Resetting on
            // visibility transitions both directions covers Show/Hide cycles.
            ResetTouchTracking();
        }

        private void ResetTouchTracking()
        {
            lock (_stateLock)
            {
                for (int i = 0; i < OverlaySlotCount; i++)
                {
                    _slotTouchIds[i] = null;
                    _slotDown[i] = false;
                    _slotX[i] = 0f;
                    _slotY[i] = 0f;
                    _slotContactIds[i] = -1;
                }
                _clickPulse = false;
                _clickBarHeld = false;
                _clickBarTouchId = null;
                _activeTouchIds.Clear();
                _isDragging = false;
                _isMouseDragging = false;
                _isResizing = false;
            }
            UpdateFingerDots();
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
            // No-op now that the inner Grid handles row sizing — kept as a
            // wired method so OnLoaded / OnSizeChanged callers don't need
            // to know the layout strategy changed. The Surface and ClickBar
            // both stretch to fill their respective Grid rows.
        }

        // ─────────────────────────────────────────────
        //  Click bar (touchpad click)
        // ─────────────────────────────────────────────

        private void ClickBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            lock (_stateLock) _clickBarHeld = true;
            ClickBar.CaptureMouse();
            UpdateClickBarVisual();
            e.Handled = true;
        }

        private void ClickBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            lock (_stateLock) _clickBarHeld = false;
            ClickBar.ReleaseMouseCapture();
            UpdateClickBarVisual();
            e.Handled = true;
        }

        private void ClickBar_MouseLeave(object sender, MouseEventArgs e)
        {
            // Release if the mouse drags off the bar with the button still
            // down — keeps the click from latching past where the user
            // actually wanted to release it.
            if (!ClickBar.IsMouseCaptured) return;
            lock (_stateLock) _clickBarHeld = false;
            ClickBar.ReleaseMouseCapture();
            UpdateClickBarVisual();
        }

        private void ClickBar_TouchDown(object sender, TouchEventArgs e)
        {
            // Setting Handled here stops the touch from bubbling up to the
            // Window's OnTouchDown override (where it would otherwise be
            // claimed as a finger).
            e.Handled = true;
            lock (_stateLock)
            {
                if (_clickBarTouchId == null)
                {
                    _clickBarTouchId = e.TouchDevice.Id;
                    _clickBarHeld = true;
                }
            }
            UpdateClickBarVisual();
        }

        private void ClickBar_TouchUp(object sender, TouchEventArgs e)
        {
            e.Handled = true;
            lock (_stateLock)
            {
                if (_clickBarTouchId == e.TouchDevice.Id)
                {
                    _clickBarTouchId = null;
                    _clickBarHeld = false;
                }
            }
            UpdateClickBarVisual();
        }

        private void UpdateClickBarVisual()
        {
            Dispatcher.BeginInvoke(() =>
            {
                ClickBar.Background = _clickBarHeld
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            });
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

        // Active touch device IDs. A set instead of an int counter so the
        // value can't drift when a touch-up doesn't fire (e.g. window hidden
        // mid-touch, engine restart, touch device disconnect). The previous
        // counter approach would tip into drag mode at 2 real fingers if a
        // phantom +1 was stuck from an earlier session.
        private readonly System.Collections.Generic.HashSet<int> _activeTouchIds = new();
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
            // AllowsTransparency=True turns the window into a layered HWND
            // where Windows only routes input to pixels with alpha > 0.
            // Floor the alpha at 1 so an "invisible" surface still receives
            // touches/clicks instead of becoming click-through.
            double clamped = Math.Clamp(opacity, 0.0, 1.0);
            byte alpha = (byte)Math.Max(1, (int)(clamped * 255));
            Surface.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(alpha, 255, 255, 255));
        }

        // ─────────────────────────────────────────────
        //  Touch input
        // ─────────────────────────────────────────────

        protected override void OnTouchDown(TouchEventArgs e)
        {
            e.Handled = true;
            CaptureTouch(e.TouchDevice);
            _activeTouchIds.Add(e.TouchDevice.Id);

            // Three or more fingers: enter drag mode.
            if (_activeTouchIds.Count >= 3 && !_isDragging)
            {
                _isDragging = true;
                var screenPos = PointToScreen(e.GetTouchPoint(this).Position);
                _dragStartScreen = screenPos;
                _dragStartLeft = Left;
                _dragStartTop = Top;
                return;
            }

            if (_isDragging) return;

            // Click-bar touches are intercepted by ClickBar_TouchDown so
            // they never reach this override. Anything that does reach
            // here came from inside RootCanvas (the surface area), so we
            // normalize against RootCanvas's bounds — this is the visual
            // surface region the finger dots live in. Window-relative
            // coordinates would include the click bar strip and skew the
            // normalization.
            var pos = e.GetTouchPoint(RootCanvas).Position;
            double surfaceWidth = Math.Max(1.0, RootCanvas.ActualWidth);
            double surfaceHeight = Math.Max(1.0, RootCanvas.ActualHeight);
            float nx = (float)(pos.X / surfaceWidth);
            float ny = (float)(pos.Y / surfaceHeight);

            lock (_stateLock)
            {
                // Allocate the lowest free slot. The 3-finger-drag check
                // above only triggers on the 3rd touch _across the
                // window_, not via this slot allocation; this loop will
                // happily fill slots 2/3/4 if it ever runs for them
                // (currently it doesn't because of the early-return at
                // _activeTouchIds.Count >= 3, but the slot infra is
                // ready for a future relaxation of the drag heuristic).
                for (int i = 0; i < OverlaySlotCount; i++)
                {
                    if (_slotTouchIds[i] != null) continue;
                    _slotTouchIds[i] = e.TouchDevice.Id;
                    _slotX[i] = nx;
                    _slotY[i] = ny;
                    _slotDown[i] = true;
                    _slotContactIds[i] = _slotContactIdNext++;
                    break;
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

            // Click-bar touches are intercepted by ClickBar_TouchDown / Up;
            // their move events route to the captured ClickBar element and
            // never reach this override. Same normalization as OnTouchDown.
            var pos = e.GetTouchPoint(RootCanvas).Position;
            double surfaceWidth = Math.Max(1.0, RootCanvas.ActualWidth);
            double surfaceHeight = Math.Max(1.0, RootCanvas.ActualHeight);
            float nx = (float)(pos.X / surfaceWidth);
            float ny = (float)(pos.Y / surfaceHeight);

            lock (_stateLock)
            {
                for (int i = 0; i < OverlaySlotCount; i++)
                {
                    if (_slotTouchIds[i] == e.TouchDevice.Id)
                    {
                        _slotX[i] = nx;
                        _slotY[i] = ny;
                        break;
                    }
                }
            }
            UpdateFingerDots();
        }

        protected override void OnTouchUp(TouchEventArgs e)
        {
            e.Handled = true;
            ReleaseTouchCapture(e.TouchDevice);
            _activeTouchIds.Remove(e.TouchDevice.Id);

            if (_isDragging)
            {
                if (_activeTouchIds.Count < 3)
                {
                    _isDragging = false;
                    PositionChanged?.Invoke();
                }
                return;
            }

            lock (_stateLock)
            {
                for (int i = 0; i < OverlaySlotCount; i++)
                {
                    if (_slotTouchIds[i] != e.TouchDevice.Id) continue;
                    _slotTouchIds[i] = null;
                    _slotDown[i] = false;
                    _slotContactIds[i] = -1;
                    // Double-tap-to-click pulse fires only on slot 0
                    // releases — preserves the existing single-finger
                    // tap-to-click muscle memory. Multi-finger releases
                    // don't trigger the pulse; their click semantics
                    // come from the dedicated click bar or a future
                    // multi-finger-tap gesture.
                    if (i == 0)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - _lastTapTime).TotalMilliseconds < DoubleTapMs)
                        {
                            _clickPulse = true;
                            _lastTapTime = DateTime.MinValue;
                        }
                        else
                        {
                            _lastTapTime = now;
                            _clickPulse = false;
                        }
                    }
                    break;
                }
            }
            UpdateFingerDots();
        }

        /// <summary>Reads current overlay touchpad state in the legacy
        /// 2-finger <see cref="TouchpadState"/> struct shape. Click is
        /// true while the dedicated click bar is held OR during a single-
        /// frame pulse from the surface double-tap gesture. The held
        /// branch supports click-and-hold (drag, hold-to-context); the
        /// pulse branch preserves the double-tap-to-click muscle memory.
        /// Used by the DS4-output side; the gesture engine uses
        /// <see cref="GetMultiFingerState"/> instead.</summary>
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
                    Click = _clickBarHeld || _clickPulse
                };
                _clickPulse = false;
                return tp;
            }
        }

        /// <summary>Reads current overlay touchpad state as a full
        /// <see cref="Engine.TouchpadInputState"/> snapshot with all
        /// active slots populated. Used by the engine bridge that feeds
        /// <see cref="Engine.TouchpadOverlayDevice.UpdateStateMulti"/>
        /// so the gesture recognizer sees every active finger, not just
        /// the first two. Click bit returned separately (the snapshot's
        /// <c>Clicked</c> field is set by the bridge as well so both
        /// physical-input paths agree on the click state).</summary>
        public Engine.TouchpadInputState GetMultiFingerState(out bool click)
        {
            var snap = new Engine.TouchpadInputState(OverlaySlotCount);
            lock (_stateLock)
            {
                for (int i = 0; i < OverlaySlotCount; i++)
                {
                    snap.FingerX[i] = Math.Clamp(_slotX[i], 0f, 1f);
                    snap.FingerY[i] = Math.Clamp(_slotY[i], 0f, 1f);
                    snap.FingerPressure[i] = _slotDown[i] ? 1f : 0f;
                    snap.FingerDown[i] = _slotDown[i];
                    snap.FingerContactId[i] = _slotContactIds[i];
                }
                click = _clickBarHeld || _clickPulse;
                // Click pulse consumed by the legacy reader path or here,
                // whichever runs first. Single-shot pulse semantics.
                _clickPulse = false;
            }
            snap.Clicked = click;
            return snap;
        }

        private void UpdateFingerDots()
        {
            Dispatcher.BeginInvoke(() =>
            {
                // Finger dots are children of RootCanvas; normalize coords
                // against RootCanvas's dimensions so the dot lands under
                // the user's finger on the visible surface.
                double w = Math.Max(1.0, RootCanvas.ActualWidth);
                double h = Math.Max(1.0, RootCanvas.ActualHeight);
                lock (_stateLock)
                {
                    if (_down0)
                    {
                        Finger0Dot.Visibility = Visibility.Visible;
                        Canvas.SetLeft(Finger0Dot, _x0 * w - 10);
                        Canvas.SetTop(Finger0Dot, _y0 * h - 10);
                    }
                    else
                    {
                        Finger0Dot.Visibility = Visibility.Collapsed;
                    }

                    if (_down1)
                    {
                        Finger1Dot.Visibility = Visibility.Visible;
                        Canvas.SetLeft(Finger1Dot, _x1 * w - 10);
                        Canvas.SetTop(Finger1Dot, _y1 * h - 10);
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
