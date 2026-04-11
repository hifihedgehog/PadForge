using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    public partial class ProfileSwitchOverlay : Window
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

        // Win11 confirmator icons
        private const string ProfileIcon = "\uE8F1";
        private const string InitializingIcon = "\uE895";
        private const string ActiveIcon = "\uE73E";

        private readonly DispatcherTimer _dismissTimer;
        private readonly DispatcherTimer _initMonitorTimer;
        private bool _showingInitializing;
        private bool _isDark = true;

        public Func<(bool anyInitializing, bool allReady)> CheckInitState { get; set; }

        public ProfileSwitchOverlay()
        {
            InitializeComponent();
            Loaded += OnLoaded;

            _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _initMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _initMonitorTimer.Tick += OnInitMonitorTick;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);

            ApplyTheme();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
            return IntPtr.Zero;
        }

        private void ApplyTheme()
        {
            _isDark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                == Wpf.Ui.Appearance.ApplicationTheme.Dark;

            if (_isDark)
            {
                // Dark: very dark semi-transparent background, faint black border
                FlyoutBorder.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2C, 0x2C, 0x2C));
                FlyoutStroke.Color = Color.FromArgb(0x33, 0x00, 0x00, 0x00);
                StatusIcon.Foreground = Brushes.White;
                StatusText.Foreground = Brushes.White;
            }
            else
            {
                // Light: near-white semi-transparent background, very faint border
                FlyoutBorder.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xF9, 0xF9, 0xF9));
                FlyoutStroke.Color = Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
                StatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            }
        }

        public void ShowProfileName(string profileName)
        {
            _dismissTimer.Stop();
            _initMonitorTimer.Stop();
            _showingInitializing = false;

            ApplyTheme();

            StatusIcon.BeginAnimation(OpacityProperty, null);
            StatusIcon.Opacity = 1;
            StatusIcon.Text = ProfileIcon;
            StatusText.Text = profileName;

            ShowFlyout();

            _dismissTimer.Tick -= OnDismissThenClose;
            _dismissTimer.Tick -= OnDismissThenMonitorInit;
            _dismissTimer.Tick += OnDismissThenMonitorInit;
            _dismissTimer.Interval = TimeSpan.FromSeconds(2);
            _dismissTimer.Start();
        }

        private void OnDismissThenMonitorInit(object sender, EventArgs e)
        {
            _dismissTimer.Stop();
            _dismissTimer.Tick -= OnDismissThenMonitorInit;
            _initMonitorTimer.Start();
        }

        private void OnInitMonitorTick(object sender, EventArgs e)
        {
            if (CheckInitState == null)
            {
                _initMonitorTimer.Stop();
                ShowActive();
                return;
            }

            var (anyInit, allReady) = CheckInitState();

            if (anyInit && !_showingInitializing)
                ShowInitializing();
            else if (allReady)
            {
                _initMonitorTimer.Stop();
                ShowActive();
            }
        }

        private void ShowInitializing()
        {
            _showingInitializing = true;
            StatusIcon.Text = InitializingIcon;
            StatusText.Text = Strings.Instance.Main_Initializing;

            var flash = new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(600))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            };
            StatusIcon.BeginAnimation(OpacityProperty, flash);
            ShowFlyout();
        }

        private void ShowActive()
        {
            _showingInitializing = false;
            StatusIcon.BeginAnimation(OpacityProperty, null);
            StatusIcon.Opacity = 1;
            StatusIcon.Text = ActiveIcon;
            StatusText.Text = Strings.Instance.Main_Active;
            StatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));

            ShowFlyout();

            _dismissTimer.Tick -= OnDismissThenMonitorInit;
            _dismissTimer.Tick -= OnDismissThenClose;
            _dismissTimer.Tick += OnDismissThenClose;
            _dismissTimer.Interval = TimeSpan.FromSeconds(2);
            _dismissTimer.Start();
        }

        private void OnDismissThenClose(object sender, EventArgs e)
        {
            _dismissTimer.Stop();
            _dismissTimer.Tick -= OnDismissThenClose;
            BeginFadeOut();
        }

        private void ShowFlyout()
        {
            // Position: center-bottom, 80px above work area bottom (Win11 default)
            var screen = SystemParameters.WorkArea;
            Left = screen.Left + (screen.Width - 360) / 2;
            Top = screen.Bottom - ActualHeight - 80;

            // Win11 open animation: 40px slide-up, 367ms decelerate, 166ms opacity with 83ms delay
            var transform = new TranslateTransform(0, 40);
            FlyoutBorder.RenderTransform = transform;
            Opacity = 0;
            Show();

            // Recalculate position after Show() measures the window
            Top = screen.Bottom - ActualHeight - 80;

            var slideUp = new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(367));
            slideUp.SetValue(Timeline.DesiredFrameRateProperty, 60);
            var keySpline = new KeySplineAnimation();
            // Approximate cubic-bezier(0.1, 0.9, 0.2, 1.0) with QuadraticEase EaseOut
            slideUp.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };

            // Opacity: 83ms hold at 0, then 83ms linear fade to 1
            var fadeIn = new DoubleAnimationUsingKeyFrames();
            fadeIn.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(83))));
            fadeIn.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(166))));

            transform.BeginAnimation(TranslateTransform.YProperty, slideUp);
            BeginAnimation(OpacityProperty, fadeIn);
        }

        private void BeginFadeOut()
        {
            // Win11 close: 367ms slide-down, opacity 1->0 in 166ms, hidden at 250ms
            var transform = FlyoutBorder.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 0);
            FlyoutBorder.RenderTransform = transform;

            var slideDown = new DoubleAnimation(0, 40, TimeSpan.FromMilliseconds(367));
            slideDown.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };

            var fadeOut = new DoubleAnimationUsingKeyFrames();
            fadeOut.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(83))));
            fadeOut.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
            fadeOut.Completed += (_, _) =>
            {
                Hide();
                ApplyTheme(); // Reset green icon color
            };

            transform.BeginAnimation(TranslateTransform.YProperty, slideDown);
            BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    // Stub to avoid compile error from removed reference
    internal class KeySplineAnimation { }
}
