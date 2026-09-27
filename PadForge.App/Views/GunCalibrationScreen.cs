using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// The GunCon 2 calibration screen (hifihedgehog/SDL#33 Part 9). The gun
    /// reads only a 15 kHz CRT and sees nothing on a dark screen, so every
    /// monitor turns white and shows the same target, and whichever screen
    /// is the CRT is the one the gun reads. Four targets, set in from the
    /// corners the way beardypig's and psakhis's calibrate.py set theirs,
    /// one pull each. The shot is the raw count the poll thread latched with
    /// the pull (SdlDeviceWrapper.TryGetGunCon2Pull), and the window is the
    /// line through the shots read at the picture's edges
    /// (GunCon2Calibration.TryFit). Esc, or the gun's A or B, cancels, as A
    /// and B end psakhis's calibration.
    /// </summary>
    internal sealed class GunCalibrationScreen
    {
        /// <summary>The targets as fractions of the picture's width and
        /// height, top left first and clockwise, a tenth in from each edge.</summary>
        internal static readonly (double U, double V)[] Targets =
        {
            (0.1, 0.1), (0.9, 0.1), (0.9, 0.9), (0.1, 0.9),
        };

        private readonly UserDevice _device;
        private readonly SdlDeviceWrapper _gun;
        private readonly List<Surface> _surfaces = new();
        private readonly (int X, int Y)[] _shots = new (int X, int Y)[Targets.Length];
        private readonly TaskCompletionSource<GunCon2Calibration> _result = new();
        private readonly DispatcherTimer _timer;
        private int _target;
        private int _lastPull;
        private bool _cancelHeld = true;
        private bool _finished;

        private GunCalibrationScreen(UserDevice device, SdlDeviceWrapper gun)
        {
            _device = device;
            _gun = gun;
            _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
            _timer.Tick += (_, _) => Poll();
        }

        /// <summary>Runs the screen and returns the fitted window, or null
        /// when it was canceled or the gun left.</summary>
        public static Task<GunCon2Calibration> RunAsync(UserDevice device, SdlDeviceWrapper gun, Window owner)
        {
            var screen = new GunCalibrationScreen(device, gun);
            screen.Start(owner);
            return screen._result.Task;
        }

        private void Start(Window owner)
        {
            // A pull from before the screen opened is not a shot.
            _gun.TryGetGunCon2Pull(out _, out _, out _lastPull);

            foreach (var monitor in System.Windows.Forms.Screen.AllScreens)
            {
                var surface = new Surface(monitor.Bounds, owner);
                surface.Window.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Escape)
                        Finish(null);
                };
                surface.Window.Closed += (_, _) => Finish(null);
                _surfaces.Add(surface);
            }
            foreach (var surface in _surfaces)
                surface.Show();
            ShowTarget(message: null);
            _timer.Start();
        }

        private void Poll()
        {
            if (_finished) return;
            if (!_device.IsOnline)
            {
                Finish(null);
                return;
            }

            // The gun's A and B are buttons 1 and 2 (docs/README-guncon.md in
            // the fork). Held when the screen opens, they count only after a
            // release, so the press that opened nothing cannot close it.
            var state = _device.InputState;
            bool cancel = state?.Buttons != null && state.Buttons.Length > 2
                && (state.Buttons[1] || state.Buttons[2]);
            if (cancel && !_cancelHeld)
            {
                Finish(null);
                return;
            }
            _cancelHeld = cancel;

            if (!_gun.TryGetGunCon2Pull(out short x, out short y, out int pull) || pull == _lastPull)
                return;
            _lastPull = pull;

            if (!SdlDeviceWrapper.GunCon2OnScreen(x, y))
            {
                ShowTarget(Strings.Instance.GunCalibration_OffScreen);
                return;
            }

            _shots[_target] = (x, y);
            _target++;
            if (_target < Targets.Length)
            {
                ShowTarget(message: null);
                return;
            }

            if (GunCon2Calibration.TryFit(Targets, _shots, out var calibration))
            {
                Finish(calibration);
                return;
            }
            _target = 0;
            ShowTarget(Strings.Instance.GunCalibration_TooClose);
        }

        private void ShowTarget(string message)
        {
            var (u, v) = Targets[_target];
            string progress = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Strings.Instance.GunCalibration_Progress, _target + 1, Targets.Length);
            foreach (var surface in _surfaces)
                surface.Update(u, v, progress, message);
        }

        private void Finish(GunCon2Calibration calibration)
        {
            if (_finished) return;
            _finished = true;
            _timer.Stop();
            foreach (var surface in _surfaces)
                surface.Close();
            _result.TrySetResult(calibration);
        }

        /// <summary>One monitor's white screen, target and text.</summary>
        private sealed class Surface
        {
            private const double TargetRadius = 24;
            private const double ArmLength = 36;
            private const double CenterGap = 8;

            public readonly Window Window;
            private readonly System.Drawing.Rectangle _bounds;
            private readonly Canvas _canvas = new();
            private readonly TextBlock _progress;
            private readonly TextBlock _message;
            private double _u, _v;
            private bool _closing;

            public Surface(System.Drawing.Rectangle bounds, Window owner)
            {
                _bounds = bounds;
                var ink = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
                ink.Freeze();

                var instruction = new TextBlock
                {
                    Text = Strings.Instance.GunCalibration_Instruction,
                    FontSize = 28,
                    Foreground = ink,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                };
                _progress = new TextBlock
                {
                    FontSize = 20,
                    Foreground = ink,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 12, 0, 0),
                };
                _message = new TextBlock
                {
                    FontSize = 20,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA4, 0x26, 0x0E)),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                };
                var cancel = new TextBlock
                {
                    Text = Strings.Instance.GunCalibration_Cancel,
                    FontSize = 16,
                    Foreground = ink,
                    Opacity = 0.7,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 24, 0, 0),
                };
                var text = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 720,
                    Margin = new Thickness(24),
                };
                text.Children.Add(instruction);
                text.Children.Add(_progress);
                text.Children.Add(_message);
                text.Children.Add(cancel);

                var root = new Grid { Background = Brushes.White };
                root.Children.Add(text);
                root.Children.Add(_canvas);
                root.SizeChanged += (_, _) => Draw();

                Window = new Window
                {
                    Title = Strings.Instance.GunCalibration_Title,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Background = Brushes.White,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Width = 640,
                    Height = 480,
                    Content = root,
                    Cursor = Cursors.None,
                };
                if (owner != null && owner.IsLoaded)
                    Window.Owner = owner;
                Window.SourceInitialized += (_, _) => MoveToMonitor();
            }

            public void Show()
            {
                Window.Show();
                // Maximized with no border and no resize covers the whole
                // monitor it sits on, taskbar included.
                Window.WindowState = WindowState.Maximized;
            }

            public void Close()
            {
                if (_closing) return;
                _closing = true;
                Window.Close();
            }

            public void Update(double u, double v, string progress, string message)
            {
                _u = u;
                _v = v;
                _progress.Text = progress;
                _message.Text = message ?? string.Empty;
                _message.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
                Draw();
            }

            /// <summary>Places the window on its monitor in physical pixels
            /// before it maximizes there, so no DPI conversion can land it on
            /// a neighboring monitor.</summary>
            private void MoveToMonitor()
            {
                var hwnd = new WindowInteropHelper(Window).Handle;
                if (hwnd == IntPtr.Zero) return;
                SetWindowPos(hwnd, IntPtr.Zero,
                    _bounds.Left + _bounds.Width / 4, _bounds.Top + _bounds.Height / 4,
                    Math.Max(1, _bounds.Width / 2), Math.Max(1, _bounds.Height / 2),
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }

            /// <summary>A ring and a cross with an open center: the gun reads
            /// light, so the point it aims at stays white.</summary>
            private void Draw()
            {
                _canvas.Children.Clear();
                double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
                if (w <= 0 || h <= 0) return;
                double cx = _u * w, cy = _v * h;
                var ink = Brushes.Black;

                var ring = new Ellipse
                {
                    Width = TargetRadius * 2,
                    Height = TargetRadius * 2,
                    Stroke = ink,
                    StrokeThickness = 3,
                };
                Canvas.SetLeft(ring, cx - TargetRadius);
                Canvas.SetTop(ring, cy - TargetRadius);
                _canvas.Children.Add(ring);

                AddArm(cx - ArmLength, cy, cx - CenterGap, cy);
                AddArm(cx + CenterGap, cy, cx + ArmLength, cy);
                AddArm(cx, cy - ArmLength, cx, cy - CenterGap);
                AddArm(cx, cy + CenterGap, cx, cy + ArmLength);

                void AddArm(double x1, double y1, double x2, double y2) =>
                    _canvas.Children.Add(new Line
                    {
                        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                        Stroke = ink,
                        StrokeThickness = 3,
                    });
            }

            private const uint SWP_NOZORDER = 0x0004;
            private const uint SWP_NOACTIVATE = 0x0010;

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                int x, int y, int cx, int cy, uint flags);
        }
    }
}
