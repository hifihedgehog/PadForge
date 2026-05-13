using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PadForge.Views
{
    /// <summary>
    /// Win11-flyout-styled window that surfaces the currently-engaged
    /// shift layer's name + color. Visually matches ProfileSwitchOverlay:
    /// bottom-center of the work area, slides up from below the taskbar
    /// edge, themed background/border. Stays visible while a layer is
    /// engaged; slides out when the slot returns to Base. Owned by
    /// InputService which polls the engine's shift-engagement state at
    /// ~30Hz and calls <see cref="ShowLayer"/> / <see cref="HideFlyout"/>.
    /// </summary>
    public partial class ShiftLayerFlyout : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // Slide travel distance — enough to fully hide the flyout below the clip boundary.
        private const double SlideTravel = 80;

        // Auto-dismiss delay. Matches the profile flyout's 2-second window so
        // toggle / custom / cycle modes don't leave the flyout on screen
        // forever after a transition. Hold mode users keep seeing it for the
        // duration of their hold because the engine-driven mask-change calls
        // re-arm the timer on every fresh engage.
        private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(2);

        private readonly TranslateTransform _slideTransform;
        private readonly DispatcherTimer _autoDismissTimer;
        private bool _isSlidingOut;

        public ShiftLayerFlyout()
        {
            InitializeComponent();

            _slideTransform = new TranslateTransform(0, SlideTravel);
            FlyoutPanel.RenderTransform = _slideTransform;

            _autoDismissTimer = new DispatcherTimer { Interval = AutoDismissDelay };
            _autoDismissTimer.Tick += OnAutoDismissTick;

            Loaded += OnLoaded;
        }

        private void OnAutoDismissTick(object sender, EventArgs e)
        {
            _autoDismissTimer.Stop();
            HideFlyout();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            // Click-through + no activation + no taskbar/alt-tab presence.
            SetWindowLong(hwnd, GWL_EXSTYLE,
                exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);

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
                var bg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2E, 0x2E));
                ShadowBorder.Background = bg;
                ContentBorder.Background = bg;
                ContentBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x16));
                StatusIcon.Foreground = Brushes.White;
                LayerNameText.Foreground = Brushes.White;
            }
            else
            {
                var bg = new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xEF));
                ShadowBorder.Background = bg;
                ContentBorder.Background = bg;
                ContentBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
                StatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                LayerNameText.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            }
        }

        // ── Slide animations ──────────────────────────────────

        private void SlideIn()
        {
            _slideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _slideTransform.Y = SlideTravel;

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

        /// <summary>Updates the displayed icon, layer name and dot color
        /// and shows the flyout, arming a 2-second auto-dismiss timer.
        /// Re-calling while visible refreshes the content and re-arms the
        /// timer (so transitioning from one shift layer to another keeps
        /// the flyout on screen for another 2 seconds). An empty
        /// <paramref name="icon"/> falls back to the universal Shift
        /// glyph <c>⇧</c>.</summary>
        public void ShowLayer(string layerName, string colorHex, string icon)
        {
            ApplyTheme();
            StatusIcon.Text = string.IsNullOrEmpty(icon) ? "⇧" : icon;
            LayerNameText.Text = string.IsNullOrEmpty(layerName) ? "" : layerName;
            ColorDot.Fill = ParseColor(colorHex) ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

            // Mid-slide-out (e.g. user re-engaged a layer right as the
            // dismiss timer fired) — cancel the outbound animation and
            // slide back in cleanly instead of leaving the flyout half-
            // off-screen.
            if (_isSlidingOut)
            {
                _isSlidingOut = false;
                _slideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                _slideTransform.Y = 0;
            }

            if (Visibility != Visibility.Visible)
                ShowFlyout();

            // Always (re-)arm the dismiss timer — even on a content-only
            // refresh between layer transitions.
            _autoDismissTimer.Stop();
            _autoDismissTimer.Start();
        }

        /// <summary>Slides the flyout out and hides it. Safe to call when
        /// already hidden. Cancels any pending auto-dismiss.</summary>
        public void HideFlyout()
        {
            _autoDismissTimer.Stop();
            if (Visibility != Visibility.Visible || _isSlidingOut)
                return;
            _isSlidingOut = true;
            SlideOut(() =>
            {
                _isSlidingOut = false;
                Hide();
            });
        }

        private void ShowFlyout()
        {
            var screen = SystemParameters.WorkArea;

            UpdateLayout();
            Show();
            UpdateLayout();

            // Center horizontally; bottom margin (14px in XAML) provides gap above taskbar.
            Left = screen.Left + (screen.Width - ActualWidth) / 2;
            Top = screen.Bottom - ActualHeight;

            SlideIn();
        }

        private static SolidColorBrush ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color c)
                    return new SolidColorBrush(c);
            }
            catch { }
            return null;
        }
    }
}
