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

        private const string ProfileIcon = "\uE8F1";
        private const string InitializingIcon = "\uE895";
        private const string ActiveIcon = "\uE73E";
        private const string OfflineIcon = "\uE7BA";

        // Slide travel distance — enough to fully hide the flyout below the clip boundary.
        private const double SlideTravel = 80;

        private readonly DispatcherTimer _dismissTimer;
        private readonly DispatcherTimer _initMonitorTimer;
        private readonly TranslateTransform _slideTransform;
        private bool _showingInitializing;

        public Func<(bool anyInitializing, bool allReady)> CheckInitState { get; set; }
        public Func<bool> CheckAnyOffline { get; set; }

        public ProfileSwitchOverlay()
        {
            InitializeComponent();

            // Slide animation transform on the inner FlyoutPanel; ClipPanel clips it.
            _slideTransform = new TranslateTransform(0, SlideTravel);
            FlyoutPanel.RenderTransform = _slideTransform;

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
            bool isDark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                == Wpf.Ui.Appearance.ApplicationTheme.Dark;

            if (isDark)
            {
                // Pixel-measured from native Win11 volume OSD.
                var bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2E, 0x2E));
                ShadowBorder.Background = bg;
                ContentBorder.Background = bg;
                ContentBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x16));
                StatusIcon.Foreground = Brushes.White;
                StatusText.Foreground = Brushes.White;
            }
            else
            {
                var bg = new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xEF));
                ShadowBorder.Background = bg;
                ContentBorder.Background = bg;
                ContentBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
                StatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            }
        }

        // ── Slide animations ──────────────────────────────────

        private void SlideIn()
        {
            // Snap to off-screen position so the first rendered frame is hidden.
            _slideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _slideTransform.Y = SlideTravel;

            // Defer animation start until after the first render pass so WPF
            // doesn't coalesce the start and end into a single frame.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var anim = new DoubleAnimation(SlideTravel, 0, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                _slideTransform.BeginAnimation(TranslateTransform.YProperty, anim);
            });
        }

        private void SlideOut(Action onCompleted)
        {
            _slideTransform.BeginAnimation(TranslateTransform.YProperty, null);

            var anim = new DoubleAnimation(0, SlideTravel, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (_, _) => onCompleted?.Invoke();
            _slideTransform.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        // ── Public API ────────────────────────────────────────

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
            else if (allReady || (!anyInit && !allReady))
            {
                // Either all VCs are ready, or nothing is initializing anymore
                // (remaining slots may have offline devices). Show Active either way.
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
            _dismissTimer.Tick -= OnDismissThenCheckOffline;
            _dismissTimer.Tick += OnDismissThenCheckOffline;
            _dismissTimer.Interval = TimeSpan.FromSeconds(2);
            _dismissTimer.Start();
        }

        private void OnDismissThenCheckOffline(object sender, EventArgs e)
        {
            _dismissTimer.Stop();
            _dismissTimer.Tick -= OnDismissThenCheckOffline;

            if (CheckAnyOffline?.Invoke() == true)
                ShowOffline();
            else
            {
                SlideOut(() =>
                {
                    Hide();
                    ApplyTheme();
                });
            }
        }

        private void ShowOffline()
        {
            StatusIcon.BeginAnimation(OpacityProperty, null);
            StatusIcon.Opacity = 1;
            StatusIcon.Text = OfflineIcon;
            StatusText.Text = Strings.Instance.Main_ControllersOffline;
            StatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00));

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

            SlideOut(() =>
            {
                Hide();
                ApplyTheme();
            });
        }

        private void ShowFlyout()
        {
            var screen = SystemParameters.WorkArea;

            UpdateLayout();
            Show();
            UpdateLayout();

            // Center horizontally. Bottom margin (15px in XAML) provides gap above taskbar.
            Left = screen.Left + (screen.Width - ActualWidth) / 2;
            Top = screen.Bottom - ActualHeight;

            SlideIn();
        }
    }
}
