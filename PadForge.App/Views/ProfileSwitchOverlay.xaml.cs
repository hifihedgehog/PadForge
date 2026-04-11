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

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWCP_ROUND = 2;          // 8px rounded corners
        private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic (transient surface)

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

            // DWM: 8px rounded corners (same as Win11 volume OSD).
            int cornerPref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

            // DWM: Acrylic backdrop (transient surface, same material as volume OSD).
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            ApplyTheme();
        }

        private const int WM_NCCALCSIZE = 0x0083;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
            // Remove non-client area (title bar/border) while keeping DWM rounded corners.
            if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero) { handled = true; return IntPtr.Zero; }
            return IntPtr.Zero;
        }

        private void ApplyTheme()
        {
            _isDark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                == Wpf.Ui.Appearance.ApplicationTheme.Dark;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int darkMode = _isDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }

            if (_isDark)
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2D, 0x2D));
                StatusIcon.Foreground = Brushes.White;
                StatusText.Foreground = Brushes.White;
            }
            else
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
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
            StatusIcon.SetResourceReference(ForegroundProperty, "SystemAccentColorPrimaryBrush");

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

        private double _restingTop;

        private void ShowFlyout()
        {
            var screen = SystemParameters.WorkArea;

            // Clear any active animation so we can set Top directly.
            BeginAnimation(TopProperty, null);

            // Position off-screen (behind taskbar) BEFORE showing.
            Left = screen.Left + (screen.Width - 200) / 2; // Estimate width for initial centering.
            Top = screen.Bottom;

            Show();
            UpdateLayout();

            // Re-center with actual measured width.
            Left = screen.Left + (screen.Width - ActualWidth) / 2;
            _restingTop = screen.Bottom - ActualHeight - 13;

            // Slide up from behind the taskbar.
            var slideUp = new DoubleAnimation(screen.Bottom, _restingTop, TimeSpan.FromMilliseconds(300));
            slideUp.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(TopProperty, slideUp);
        }

        private void BeginFadeOut()
        {
            // Slide down behind the taskbar.
            var screen = SystemParameters.WorkArea;
            var slideDown = new DoubleAnimation(_restingTop, screen.Bottom, TimeSpan.FromMilliseconds(300));
            slideDown.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn };
            slideDown.Completed += (_, _) =>
            {
                BeginAnimation(TopProperty, null); // Clear animation so Top can be set directly next time.
                Hide();
                ApplyTheme();
            };

            BeginAnimation(TopProperty, slideDown);
        }
    }

}
