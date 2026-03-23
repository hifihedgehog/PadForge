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

        public TouchpadOverlay()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
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

        protected override void OnTouchDown(TouchEventArgs e)
        {
            e.Handled = true;
            CaptureTouch(e.TouchDevice);

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
