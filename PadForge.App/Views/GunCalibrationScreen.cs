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
    /// <summary>What the calibration screen reads from one kind of light gun:
    /// its latest latched shot, whether its cancel buttons are down, and the
    /// words the screen shows for it.</summary>
    internal sealed class CalibrationGun
    {
        /// <summary>The latest shot: whether one was latched yet, the counts,
        /// whether they landed on the screen, and a count that changes with
        /// each shot.</summary>
        public Func<(bool Any, short X, short Y, bool OnScreen, int Shot)> LatestShot { get; init; }

        public Func<bool> CancelHeld { get; init; }

        /// <summary>The wrapper the shot read closes over. The screen ends
        /// when the device stops using it.</summary>
        public SdlDeviceWrapper Wrapper { get; init; }

        public string Instruction { get; init; }
        public string OffScreen { get; init; }
        public string Cancel { get; init; }

        /// <summary>A GunCon 2: the raw beam counts latched with the trigger
        /// pull (SdlDeviceWrapper.TryGetGunCon2Pull), on the screen past
        /// GunconUSB's off-screen counts, and the gun's A and B (buttons 1 and
        /// 2) cancel, as they end psakhis's calibration.</summary>
        public static CalibrationGun ForGunCon2(UserDevice device, SdlDeviceWrapper gun) => new()
        {
            Wrapper = gun,
            LatestShot = () =>
            {
                bool any = gun.TryGetGunCon2Pull(out short x, out short y, out int pull);
                return (any, x, y, SdlDeviceWrapper.GunCon2OnScreen(x, y), pull);
            },
            CancelHeld = () =>
            {
                var state = device.InputState;
                return state?.Buttons != null && state.Buttons.Length > 2
                    && (state.Buttons[1] || state.Buttons[2]);
            },
            Instruction = Strings.Instance.GunCalibration_Instruction,
            OffScreen = Strings.Instance.GunCalibration_OffScreen,
            Cancel = Strings.Instance.GunCalibration_Cancel,
        };

        /// <summary>A Wii Remote (#485): the twist-compensated aim in pointer
        /// counts latched with a press of B, the trigger
        /// (SdlDeviceWrapper.TryGetWiiPointerShot), on the screen while the
        /// remote saw the sensor bar, and Home cancels, since B shoots.</summary>
        public static CalibrationGun ForWiiRemote(SdlDeviceWrapper remote) => new()
        {
            Wrapper = remote,
            LatestShot = () =>
            {
                bool any = remote.TryGetWiiPointerShot(out short x, out short y, out bool onScreen, out int shot);
                return (any, x, y, onScreen, shot);
            },
            CancelHeld = () => remote.WiiHomeHeld,
            Instruction = Strings.Instance.GunCalibration_WiiInstruction,
            OffScreen = Strings.Instance.GunCalibration_WiiOffScreen,
            Cancel = Strings.Instance.GunCalibration_WiiCancel,
        };
    }

    /// <summary>
    /// The light-gun calibration screen, first for the GunCon 2
    /// (hifihedgehog/SDL#33 Part 9) and then for a Wii Remote (#485). The
    /// GunCon reads only a 15 kHz CRT and sees nothing on a dark screen, so
    /// every monitor turns white and shows the same target, and whichever
    /// screen is the CRT is the one the gun reads. A Wii Remote aims at the
    /// sensor bar and reads any screen. Four targets, set in from the corners
    /// the way beardypig's and psakhis's calibrate.py set theirs, one shot
    /// each. The shot is what the poll thread latched with the trigger
    /// (<see cref="CalibrationGun.LatestShot"/>), and the window is the line
    /// through the shots read at the picture's edges
    /// (GunCon2Calibration.TryFit), which is also how Touchmote's light-gun
    /// fork calibrates two-LED aim, one line per axis. Esc, or the gun's
    /// cancel buttons, cancels.
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
        private readonly CalibrationGun _gun;
        private readonly List<Surface> _surfaces = new();
        private readonly (int X, int Y)[] _shots = new (int X, int Y)[Targets.Length];
        private readonly TaskCompletionSource<GunCon2Calibration> _result = new();
        private readonly DispatcherTimer _timer;
        private int _target;
        private int _lastPull;
        private bool _cancelHeld = true;
        private bool _finished;

        private GunCalibrationScreen(UserDevice device, CalibrationGun gun)
        {
            _device = device;
            _gun = gun;
            _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
            _timer.Tick += (_, _) => Poll();
        }

        /// <summary>Runs the screen and returns the fitted window, or null
        /// when it was canceled or the gun left.</summary>
        public static Task<GunCon2Calibration> RunAsync(UserDevice device, CalibrationGun gun, Window owner)
        {
            var screen = new GunCalibrationScreen(device, gun);
            screen.Start(owner);
            return screen._result.Task;
        }

        private void Start(Window owner)
        {
            // A shot from before the screen opened is not a shot.
            _lastPull = _gun.LatestShot().Shot;

            foreach (var monitor in System.Windows.Forms.Screen.AllScreens)
            {
                var surface = new Surface(monitor.Bounds, owner, _gun.Instruction, _gun.Cancel);
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
            if (ShotSourceGone(_device, _gun))
            {
                Finish(null);
                return;
            }

            // The gun's cancel buttons (the GunCon's A and B, buttons 1 and 2
            // in docs/README-guncon.md in the fork, or a Wii Remote's Home).
            // Held when the screen opens, they count only after a release, so
            // the press that opened nothing cannot close it.
            bool cancel = _gun.CancelHeld();
            if (cancel && !_cancelHeld)
            {
                Finish(null);
                return;
            }
            _cancelHeld = cancel;

            var (any, x, y, onScreen, pull) = _gun.LatestShot();
            if (!any || pull == _lastPull)
                return;
            _lastPull = pull;

            if (!onScreen)
            {
                ShowTarget(_gun.OffScreen);
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

        /// <summary>The gun left: its device went offline, or a reconnect gave
        /// the device a new wrapper while the screen still reads the old one.
        /// A Wii Remote reopens in place when an extension is plugged in, and
        /// a replug inside the disconnect debounce rebinds without going
        /// offline (UserDevice.LoadFromDevice), so the old wrapper's shot would
        /// never move again.</summary>
        internal static bool ShotSourceGone(UserDevice device, CalibrationGun gun)
            => !device.IsOnline || !ReferenceEquals(device.Device, gun.Wrapper);

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

            public Surface(System.Drawing.Rectangle bounds, Window owner, string instructionText, string cancelText)
            {
                _bounds = bounds;
                var ink = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
                ink.Freeze();

                var instruction = new TextBlock
                {
                    Text = instructionText,
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
                    Text = cancelText,
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
