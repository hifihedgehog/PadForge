using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using PadForge.Engine.Data;
using PadForge.Engine.Steering;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// The Release Test's window (#588). The test itself runs on the
    /// polling thread, inside the wheel's force feedback pass
    /// (<see cref="WheelCenteringRuntime"/>): this window starts it with the
    /// Centering card's settings, reads how far it has come every twentieth
    /// of a second, and draws the two releases as they are recorded.
    ///
    /// <para>Apply keeps the measurement in the wheel's row. Until then
    /// Vehicle mode goes on with the wheel's earlier measurement, or with
    /// none. Closing the window by any way stops a test that is running:
    /// the wheel is turning by itself while one runs, so nothing leaves it
    /// running unwatched.</para>
    /// </summary>
    public partial class ReleaseTestDialog : DialogWindow
    {
        /// <summary>How often the window reads the test.</summary>
        private const int PollMs = 50;

        private readonly UserDevice _device;
        private readonly PadViewModel _vm;
        private readonly DispatcherTimer _timer;
        private WheelCalibration _result;

        public ReleaseTestDialog(UserDevice device, PadViewModel vm)
        {
            InitializeComponent();
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));

            var culture = CultureInfo.CurrentCulture;
            double angle = ReleaseTest.ReleaseAngleFor(vm.CenteringSettingsNow());
            Subtitle = string.Format(culture, Strings.Instance.ReleaseTest_Subtitle_Format, angle.ToString("F0", culture));
            Chart.ReleaseAngle = angle;
            Chart.Seconds = ReleaseTest.ReleaseMs / 1000.0;
            AutomationProperties.SetName(Chart, string.Format(culture, Strings.Instance.ReleaseTest_Chart_Name_Format,
                Chart.Seconds.ToString("0.#", culture)));

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollMs) };
            _timer.Tick += (_, _) => Poll();
            Closed += (_, _) =>
            {
                _timer.Stop();
                _device.Centering.ClearTest();
            };
            // Opened by a key press, the next key press acts on Start: the
            // window's body holds no control for InitialFocus to find. Opened
            // by the mouse, nothing is focused, as in every dialog.
            Loaded += (_, _) =>
            {
                if (PadForge.Common.KeyboardPopup.FromKeyboard) StartBtn.Focus();
            };
            ShowReady(Strings.Instance.Common_Ready);
        }

        /// <summary>The measurement Apply kept, or null.</summary>
        public WheelCalibration Result { get; private set; }

        // ── The window's three states ──

        /// <summary>No test is running and none is done: Start, and a line
        /// that says the window is ready or why the last test ended.</summary>
        private void ShowReady(string line)
        {
            _result = null;
            StatusText.Text = line;
            Buttons(start: true, stop: false, done: false);
        }

        /// <summary>A test is under way: Stop, and an empty chart for its
        /// two releases. Internal for the page pictures, which draw this
        /// state from a simulated wheel.</summary>
        internal void ShowRunning()
        {
            _result = null;
            ResultText.Text = string.Empty;
            ShowFigures(null, 0);
            Chart.Before = null;
            Chart.After = null;
            Buttons(start: false, stop: true, done: false);
        }

        private void ShowDone(ReleaseTestStatus status)
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            _result = status.Result;
            StatusText.Text = s.ReleaseTest_Done;
            ShowFigures(status.Result, status.Damping);
            ResultText.Text = ResultSentence(status, culture);
            Buttons(start: false, stop: false, done: true);
        }

        private void Buttons(bool start, bool stop, bool done)
        {
            // A button that leaves while it has the focus hands it to the
            // button that takes its place, so a keyboard keeps its place.
            bool hadFocus = StartBtn.IsKeyboardFocusWithin || StopBtn.IsKeyboardFocusWithin
                || RunAgainBtn.IsKeyboardFocusWithin || ApplyBtn.IsKeyboardFocusWithin;
            StartBtn.Visibility = start ? Visibility.Visible : Visibility.Collapsed;
            StopBtn.Visibility = stop ? Visibility.Visible : Visibility.Collapsed;
            RunAgainBtn.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
            ApplyBtn.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
            // Enter starts the test, and keeps the measurement once there is one.
            StartBtn.IsDefault = start;
            ApplyBtn.IsDefault = done;
            if (hadFocus) (start ? StartBtn : stop ? StopBtn : ApplyBtn).Focus();
        }

        /// <summary>The three measured figures, shown once there is a
        /// measurement.</summary>
        private void ShowFigures(WheelCalibration result, double damping)
        {
            Figures.Visibility = result != null ? Visibility.Visible : Visibility.Hidden;
            if (result == null) return;
            var culture = CultureInfo.CurrentCulture;
            FrictionFigure.Text = result.Friction.ToString("F2", culture);
            InertiaFigure.Text = result.Inertia.ToString("F3", culture);
            DampingFigure.Text = damping.ToString("F2", culture);
        }

        /// <summary>What the measurement changed: how far the wheel swung
        /// past center as it centered before, then how far it swings now and
        /// where it comes to rest.</summary>
        internal static string ResultSentence(ReleaseTestStatus status, CultureInfo culture)
        {
            var s = Strings.Instance;
            string Degrees(double value) => value.ToString("F0", culture);
            string before = string.Format(culture, s.ReleaseTest_Result_Before_Format,
                Degrees(ReleaseTestStatus.PastCenter(status.Before)));
            double past = ReleaseTestStatus.PastCenter(status.After);
            string rests = Degrees(ReleaseTestStatus.RestsAt(status.After));
            // The test's own measure of a return with no overshoot: under it, the sentence's figure would say 0.
            string after = past < ReleaseTest.NoOvershootDegrees
                ? string.Format(culture, s.ReleaseTest_Result_AfterNone_Format, rests)
                : string.Format(culture, s.ReleaseTest_Result_After_Format, Degrees(past), rests);
            return before + " " + after;
        }

        /// <summary>The line for a test that ended without a measurement.</summary>
        internal static string OutcomeLine(ReleaseTestOutcome outcome)
        {
            var s = Strings.Instance;
            return outcome switch
            {
                ReleaseTestOutcome.Canceled => s.ReleaseTest_Stopped,
                ReleaseTestOutcome.Moving => s.ReleaseTest_Fail_Moving,
                ReleaseTestOutcome.NoMotion => s.ReleaseTest_Fail_NoMotion,
                ReleaseTestOutcome.NearLock => s.ReleaseTest_Fail_NearLock,
                _ => s.ReleaseTest_Fail_Unreadable,
            };
        }

        // ── The test ──

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            long now = WheelCenteringRuntime.NowMs();
            // A wheel no slot uses, or with the engine stopped, has no force
            // feedback pass to run the test in.
            if (!_device.Centering.IsPolled(now))
            {
                ShowReady(Strings.Instance.ReleaseTest_Fail_NotInUse);
                return;
            }
            if (!_device.Centering.StartTest(_vm.CenteringSettingsNow(), _device.WheelCalibration, now)) return;
            ShowRunning();
            _timer.Start();
            Poll();
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _device.Centering.CancelTest();
            Poll();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            _device.WheelCalibration = _result.Format();
            Result = _result;
            DialogResult = true;
        }

        /// <summary>Reads the test and shows how far it has come, or what it
        /// ended with.</summary>
        internal void Poll()
        {
            var status = _device.Centering.Test(WheelCenteringRuntime.NowMs());
            if (status == null) return;
            Chart.ReleaseAngle = status.ReleaseAngle;
            Chart.Before = status.Before;
            Chart.After = status.After;

            if (status.Outcome == ReleaseTestOutcome.Running)
            {
                // The wheel left, or the engine stopped, with the test under way.
                if (status.Stalled)
                {
                    _timer.Stop();
                    _device.Centering.CancelTest();
                    ShowReady(Strings.Instance.ReleaseTest_Fail_Stalled);
                    return;
                }
                var culture = CultureInfo.CurrentCulture;
                StatusText.Text = string.Format(culture, Strings.Instance.ReleaseTest_Running_Format,
                    ((int)Math.Round(status.Progress * 100.0)).ToString(culture));
                return;
            }

            _timer.Stop();
            if (status.Outcome == ReleaseTestOutcome.Done && status.Result != null) ShowDone(status);
            else ShowReady(OutcomeLine(status.Outcome));
        }
    }
}
