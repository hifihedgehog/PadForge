using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PadForge.Services;

namespace PadForge.Views
{
    /// <summary>
    /// What a keyboard screen shows, in a borderless window on the PC's own
    /// display (#560), with no keyboard screen needed. Built from
    /// <see cref="TouchpadOverlay"/>: WS_EX_NOACTIVATE and MA_NOACTIVATE so it
    /// never takes focus from a game, topmost, the same right-button drag and
    /// corner grip.
    ///
    /// <para>The Touchpad Overlay exists to take input, and this one only
    /// displays. While another program is in front it carries WS_EX_TRANSPARENT,
    /// so clicks reach the program beneath it. While PadForge is in front it
    /// takes input and shows its outline and grip, so it can be found, moved and
    /// sized even with nothing to show. g-helper's HardwareOverlay and
    /// Forza-Telemetry-Splitter's OverlayForm run the same two states.</para>
    ///
    /// <para>Each overlay is its own window with its own place, size, opacity and
    /// content, as each keyboard's screen is its own panel.</para>
    /// </summary>
    public partial class ScreenOverlay : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;
        private const uint GW_HWNDPREV = 3;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        /// <summary>Alpha 1, the least Windows routes input to.</summary>
        private static readonly Brush TakesInput = CreateTakesInput();

        private static Brush CreateTakesInput()
        {
            var brush = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
            brush.Freeze();
            return brush;
        }

        private bool _arranging;
        private bool _isMouseDragging;
        private int? _dragTouchId;
        private Point _dragStartScreen;
        private double _dragStartLeft, _dragStartTop;
        private bool _isResizing;
        private Point _resizeStart;
        private double _resizeStartW, _resizeStartH;
        private bool _isPanning;
        private Point _panStart;

        /// <summary>Fired when the user finishes dragging or resizing (position/size changed).</summary>
        public event Action PositionChanged;

        /// <summary>The left button dragging across the window: how far the pointer
        /// is from where the drag began, in the window's units, and true when it
        /// begins. A zoomed picture follows it.</summary>
        public event Action<Vector, bool> PanDragged;

        public ScreenOverlay()
        {
            InitializeComponent();
            // The grip stays in the bottom-right corner in every language.
            PadForge.Common.UiLanguage.KeepLeftToRight(this);
            EmptyHint.Text = PadForge.Resources.Strings.Strings.Instance.ScreenOverlay_Empty;
            ApplyArranging();
            IsVisibleChanged += OnIsVisibleChanged;
        }

        /// <summary>The styles go on before the window first shows, so a macro
        /// that shows it during a game takes no focus and no click.</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, InputStyle(GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
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

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // A drag cut short by a hide never gets its release.
            _isMouseDragging = false;
            _isResizing = false;
            _isPanning = false;
            _dragTouchId = null;
        }

        // ─────────────────────────────────────────────
        //  What it shows
        // ─────────────────────────────────────────────

        internal void ShowContent(ScreenOverlayContent content)
        {
            ContentView.Show(content);
            var s = PadForge.Resources.Strings.Strings.Instance;
            EmptyHint.Text = ContentView.Shown.Blank switch
            {
                KeyboardScreenService.Blank.NoPicture => s.ScreenOverlay_Blank_NoPicture,
                KeyboardScreenService.Blank.NoLightbar => s.ScreenOverlay_Blank_NoLightbar,
                _ => s.ScreenOverlay_Empty,
            };
            UpdateEmptyHint();
        }

        /// <summary>Sets how opaque the content is (0.0 = invisible, 1.0 = opaque).
        /// The outline and the grip keep theirs.</summary>
        internal void SetContentOpacity(double opacity) => ContentView.Opacity = Math.Clamp(opacity, 0.0, 1.0);

        // ─────────────────────────────────────────────
        //  Arranging
        // ─────────────────────────────────────────────

        internal bool Arranging => _arranging;

        /// <summary>A drag or a resize is under way, and the window keeps its place
        /// and its state until it ends.</summary>
        internal bool InGesture => _isMouseDragging || _isResizing || _isPanning || _dragTouchId != null;

        /// <summary>True while PadForge is in front: the window takes the mouse
        /// and shows its outline and grip. False: clicks pass through it and only
        /// its content shows. A drag in progress finishes in the state it began
        /// in, as g-helper's overlay holds its drag mode.</summary>
        internal void SetArranging(bool arranging)
        {
            if (arranging == _arranging || InGesture) return;
            _arranging = arranging;
            ApplyArranging();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) SetWindowLong(hwnd, GWL_EXSTYLE, InputStyle(GetWindowLong(hwnd, GWL_EXSTYLE)));
        }

        private int InputStyle(int exStyle) => _arranging ? exStyle & ~WS_EX_TRANSPARENT : exStyle | WS_EX_TRANSPARENT;

        private void ApplyArranging()
        {
            Surface.Background = _arranging ? TakesInput : null;
            if (_arranging) Surface.SetResourceReference(Border.BorderBrushProperty, "EmberBrush");
            else Surface.BorderBrush = null;
            ResizeGrip.Visibility = _arranging ? Visibility.Visible : Visibility.Collapsed;
            UpdateEmptyHint();
        }

        private void UpdateEmptyHint()
            => EmptyHint.Visibility = _arranging && ContentView.Shown.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

        internal void KeepOnTop() => KeepOnTop(IsOurWindow);

        internal static bool IsOurWindow(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint process);
            return process == (uint)Environment.ProcessId;
        }

        /// <summary>Back on top of a game that made itself topmost after this
        /// window did, the check and the call g-helper's overlay makes once a
        /// second. PadForge's radial menu and flyouts are topmost too and stay
        /// over this window, so only another program's visible window above it
        /// counts.</summary>
        internal void KeepOnTop(Func<IntPtr, bool> isOurs)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (_arranging || hwnd == IntPtr.Zero) return;
            for (var above = GetWindow(hwnd, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
            {
                if (isOurs(above) || !IsWindowVisible(above)) continue;
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                return;
            }
        }

        // ─────────────────────────────────────────────
        //  Drag (right mouse button, or one finger)
        // ─────────────────────────────────────────────

        private void BeginDrag(Point inWindow)
        {
            _dragStartScreen = PointToScreen(inWindow);
            _dragStartLeft = Left;
            _dragStartTop = Top;
        }

        private void DragTo(Point inWindow)
        {
            var current = PointToScreen(inWindow);
            // PointToScreen returns physical screen px. Window.Left/Top are
            // DIPs at the window's current monitor DPI.
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = _dragStartLeft + (current.X - _dragStartScreen.X) / dpi.DpiScaleX;
            Top = _dragStartTop + (current.Y - _dragStartScreen.Y) / dpi.DpiScaleY;
        }

        private void Surface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isMouseDragging = true;
            BeginDrag(e.GetPosition(this));
            Surface.CaptureMouse();
            e.Handled = true;
        }

        private void Surface_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isMouseDragging) DragTo(e.GetPosition(this));
            else if (_isPanning) PanDragged?.Invoke(MovedSincePanStart(e.GetPosition(this)), false);
        }

        private Vector MovedSincePanStart(Point inWindow)
        {
            var current = PointToScreen(inWindow);
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Vector((current.X - _panStart.X) / dpi.DpiScaleX, (current.Y - _panStart.Y) / dpi.DpiScaleY);
        }

        private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isPanning = true;
            _panStart = PointToScreen(e.GetPosition(this));
            Surface.CaptureMouse();
            PanDragged?.Invoke(default, true);
            e.Handled = true;
        }

        private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            _isPanning = false;
            Surface.ReleaseMouseCapture();
        }

        private void Surface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isMouseDragging) return;
            EndMouseDrag();
            Surface.ReleaseMouseCapture();
        }

        /// <summary>A capture taken away mid-drag never delivers the button's
        /// release, and the window would follow the pointer until the next
        /// click.</summary>
        private void Surface_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _isPanning = false;
            EndMouseDrag();
        }

        private void EndMouseDrag()
        {
            if (!_isMouseDragging) return;
            _isMouseDragging = false;
            PositionChanged?.Invoke();
        }

        protected override void OnTouchDown(TouchEventArgs e)
        {
            // A finger on the grip goes on to the mouse events WPF makes of it.
            if (ReferenceEquals(e.OriginalSource, ResizeGrip)) return;
            e.Handled = true;
            if (_dragTouchId != null) return;
            _dragTouchId = e.TouchDevice.Id;
            CaptureTouch(e.TouchDevice);
            BeginDrag(e.GetTouchPoint(this).Position);
        }

        protected override void OnTouchMove(TouchEventArgs e)
        {
            if (_dragTouchId != e.TouchDevice.Id) return;
            e.Handled = true;
            DragTo(e.GetTouchPoint(this).Position);
        }

        protected override void OnTouchUp(TouchEventArgs e)
        {
            if (_dragTouchId != e.TouchDevice.Id) return;
            e.Handled = true;
            ReleaseTouchCapture(e.TouchDevice);
            EndTouchDrag(e.TouchDevice.Id);
        }

        protected override void OnLostTouchCapture(TouchEventArgs e)
        {
            base.OnLostTouchCapture(e);
            EndTouchDrag(e.TouchDevice.Id);
        }

        private void EndTouchDrag(int touch)
        {
            if (_dragTouchId != touch) return;
            _dragTouchId = null;
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
            // Same px → DIP conversion as the drag handler.
            var dpi = VisualTreeHelper.GetDpi(this);
            Width = Math.Max(MinWidth, _resizeStartW + (current.X - _resizeStart.X) / dpi.DpiScaleX);
            Height = Math.Max(MinHeight, _resizeStartH + (current.Y - _resizeStart.Y) / dpi.DpiScaleY);
        }

        private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isResizing) return;
            EndResize();
            ResizeGrip.ReleaseMouseCapture();
        }

        private void ResizeGrip_LostMouseCapture(object sender, MouseEventArgs e) => EndResize();

        private void EndResize()
        {
            if (!_isResizing) return;
            _isResizing = false;
            PositionChanged?.Invoke();
        }
    }
}
