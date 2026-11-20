using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Common.Crosshairs;
using PadForge.Controls;
using PadForge.Services;

namespace PadForge.Views
{
    /// <summary>
    /// A window drawn in the display's own pixels that the mouse goes
    /// through (#585). It is topmost, never takes the focus, and carries
    /// WS_EX_TRANSPARENT over WPF's layered window: the styles
    /// Crosshair-Overlay gives the window it draws its crosshair in
    /// (Window.hpp, uUninteractableExtendedStyle: WS_EX_TRANSPARENT |
    /// WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_NOACTIVATE). It is a tool window,
    /// so it has no taskbar button. Each crosshair is one, and so are the
    /// magnifier's border and the spotlight's hole.
    ///
    /// <para>It is placed with SetWindowPos in pixels rather than through
    /// Left and Top in DIPs, so the window and what it draws land on whole
    /// pixels on a display of any scale.</para>
    /// </summary>
    internal class PixelOverlay : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int MA_NOACTIVATE = 3;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        public PixelOverlay(FrameworkElement content, string title)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Title = title;
            Content = content;
            // What it draws is placed in pixels, which a right-to-left
            // language would mirror.
            PadForge.Common.UiLanguage.KeepLeftToRight(this);
        }

        /// <summary>The displays changed: one was plugged in or out, or its
        /// resolution or place changed (WM_DISPLAYCHANGE, which Windows sends
        /// after the change).</summary>
        public event Action DisplaysChanged;

        private IntPtr _hwnd;

        /// <summary>The window's handle, made without showing the window
        /// when it has none yet. Kept, since the magnifier moves its windows
        /// once a frame.</summary>
        internal IntPtr Handle => _hwnd != IntPtr.Zero ? _hwnd : (_hwnd = new WindowInteropHelper(this).EnsureHandle());

        /// <summary>The styles go on before the window first shows, so one a
        /// macro shows during a game takes no focus and no click.</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return (IntPtr)MA_NOACTIVATE;
            }
            if (msg == WM_DISPLAYCHANGE && DisplaysChanged != null)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => DisplaysChanged?.Invoke()));
            return IntPtr.Zero;
        }

        /// <summary>Puts the window on <paramref name="rect"/>, in pixels, on
        /// top, and shows it when it is hidden.</summary>
        internal void PlaceAt(Int32Rect rect)
        {
            var hwnd = Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOACTIVATE);
            // A move onto a display of another scale has WPF size the window
            // to WM_DPICHANGED's suggested rectangle, which keeps its size in
            // DIPs. Placed again on that display, it stays.
            if (GetWindowRect(hwnd, out var got)
                && (got.Left != rect.X || got.Top != rect.Y || got.Right - got.Left != rect.Width || got.Bottom - got.Top != rect.Height))
                SetWindowPos(hwnd, HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOACTIVATE);
            if (!IsVisible) Show();
        }

        /// <summary>Moves the window to (x, y) in pixels, keeping its size
        /// and its place in the stack: the magnifier's once-a-frame
        /// move.</summary>
        internal void MoveTo(int x, int y)
            => SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        /// <summary>To the top of the topmost windows.</summary>
        internal void Raise()
            => SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        /// <summary>Back above another program's window that went on top of
        /// it, the Screen Overlay's once-a-second check.</summary>
        internal void KeepOnTop()
        {
            if (IsVisible) ScreenOverlay.KeepAboveOthers(Handle, ScreenOverlay.IsOurWindow);
        }
    }

    /// <summary>
    /// One crosshair's window (#585). It covers only what the crosshair
    /// draws, cut to its display, and puts the crosshair's center on the
    /// point <see cref="CrosshairPlace.Center"/> gives: the display's center
    /// plus the offset.
    /// </summary>
    internal sealed class CrosshairOverlay : PixelOverlay
    {
        private readonly CrosshairView _view;
        private CrosshairData _look;

        public CrosshairOverlay(string title) : this(new CrosshairView(), title) { }

        private CrosshairOverlay(CrosshairView view, string title) : base(view, title)
        {
            _view = view;
            DisplaysChanged += PlaceAgain;
            DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PlaceAgain));
        }

        /// <summary>A display change hid the window or showed it again. The
        /// crosshair's card says whether it is running, so it learns of it
        /// here.</summary>
        internal event Action ShownOrHiddenByDisplays;

        private bool _closed;

        /// <summary>A display change queued before the window closed runs
        /// after it, and a closed window cannot show again.</summary>
        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            base.OnClosed(e);
        }

        private void PlaceAgain()
        {
            if (_closed) return;
            bool was = IsVisible;
            Place();
            if (IsVisible != was) ShownOrHiddenByDisplays?.Invoke();
        }

        /// <summary>Shows the crosshair <paramref name="look"/> draws. The
        /// window hides while none of it is on its display, and while an
        /// Image crosshair's picture cannot be read.</summary>
        internal void ShowLook(CrosshairData look)
        {
            _look = look;
            // The same look raises no change, and its picture is read again
            // at every show.
            if (ReferenceEquals(_view.Look, look)) _view.Reload();
            else _view.Look = look;
            Place();
        }

        private void Place()
        {
            var shape = _view.Shape;
            Int32Rect rect = default;
            Point origin = default;
            if (_look != null && shape != null && (!shape.IsImage || _view.Picture != null))
            {
                var display = DisplayBounds.Of(_look.Display);
                rect = CrosshairPlace.Window(display, CrosshairPlace.Center(display, _look.OffsetX, _look.OffsetY), shape.Bounds, out origin);
            }
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                if (IsVisible) Hide();
                return;
            }
            _view.Origin = origin;
            PlaceAt(rect);
        }
    }
}
