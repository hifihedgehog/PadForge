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
    /// Win11-flyout-styled window that shows a shift layer's icon, name and
    /// color. Visually matches ProfileSwitchOverlay: bottom-center of the
    /// work area of the monitor that holds the window in front
    /// (<see cref="FlyoutPlacement"/>), slides up from below the taskbar
    /// edge, themed background/border, and clicks pass through it.
    ///
    /// <para>The window shows what it is told and lets it go after
    /// <see cref="AutoDismissDelay"/> unless it is kept alive. How long a
    /// layer's flyout stays is the layer's own Flyout choice (#581), which
    /// <c>LayerFlyoutDirector</c> reads on InputService's UI timer: it calls
    /// <see cref="ShowLayer"/> for a layer, <see cref="KeepAlive"/> on every
    /// tick for a layer set to stay, and <see cref="HideFlyout"/> when a
    /// layer ends with nothing to follow it.</para>
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

        // How long a flyout stays when nothing keeps it alive: the profile
        // flyout's two seconds. That is the whole life of a layer set to
        // For 2 Seconds and of Base's flyout. A layer set to While the Layer
        // Is On, the default in every mode, is kept alive on each tick of the
        // UI timer for as long as it is on, so this delay never runs out for
        // it (#581).
        private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(2);

        private readonly TranslateTransform _slideTransform;
        private readonly DispatcherTimer _autoDismissTimer;
        private bool _isSlidingOut;

        // The monitor the flyout was last put on, for a window WPF sizes
        // again on a monitor of another scale.
        private FlyoutPlacement.WorkArea? _placedOn;

        public ShiftLayerFlyout()
        {
            InitializeComponent();

            _slideTransform = new TranslateTransform(0, SlideTravel);
            FlyoutPanel.RenderTransform = _slideTransform;

            _autoDismissTimer = new DispatcherTimer { Interval = AutoDismissDelay };
            _autoDismissTimer.Tick += OnAutoDismissTick;

            Loaded += OnLoaded;
            SizeChanged += (_, _) =>
            {
                if (IsVisible && _placedOn is { } area) FlyoutPlacement.Place(this, area);
            };
            // Through a change of scale, at the bottom center of the monitor
            // it was put on.
            FlyoutPlacement.KeepPlace(this, (width, height, _) => _placedOn is { } area ? FlyoutPlacement.BottomCenterInPixels(area, width, height) : null);
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
            bool isDark = PadForge.Common.EmberTheme.DarkGround;

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
        /// timer (so a change from one layer to another gives the new one
        /// its own 2 seconds, or longer if the caller keeps it alive). An empty
        /// <paramref name="icon"/> falls back to the universal Shift
        /// glyph <c>⇧</c>. A picture icon (#471), a package entry or an
        /// image file, draws scaled into the glyph's spot, and one that no
        /// longer resolves falls back to ⇧ rather than showing its path.</summary>
        public void ShowLayer(string layerName, string colorHex, string icon)
        {
            ApplyTheme();
            var image = Common.MenuIconResolver.ResolveLayerIcon(icon, out string glyph);
            StatusImage.Source = image;
            StatusImage.Visibility = image != null ? Visibility.Visible : Visibility.Collapsed;
            StatusIcon.Visibility = image != null ? Visibility.Collapsed : Visibility.Visible;
            StatusIcon.Text = glyph;
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

        /// <summary>Re-arms the auto-dismiss without touching the content.
        /// The caller sends it on every tick while a layer set to stay is
        /// on. A steady layer brings no new <see cref="ShowLayer"/>, so
        /// without it the flyout went two seconds in with the layer still
        /// on.
        ///
        /// <para>Does nothing while hidden or sliding out, so it can never
        /// resurrect a flyout that HideFlyout is in the middle of retiring,
        /// and says so: false means the flyout is not up. A caller that still
        /// wants the layer shown calls <see cref="ShowLayer"/> again, which
        /// is how a flyout that the timer took during a stall of the UI
        /// thread comes back.</para></summary>
        public bool KeepAlive()
        {
            if (_isSlidingOut || Visibility != Visibility.Visible) return false;
            _autoDismissTimer.Stop();
            _autoDismissTimer.Start();
            return true;
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
            // The monitor of the window in front, as every flyout of the
            // family (#600 item 42).
            var area = FlyoutPlacement.Front();
            // The layer's color dot and its gap take 20 beside the line.
            LayerNameText.MaxWidth = Math.Max(FlyoutPlacement.LeastLine, FlyoutPlacement.LineWidth(area) - 20);

            UpdateLayout();
            Show();
            UpdateLayout();

            // Centered across the work area. The panel's bottom margin keeps
            // it above the taskbar.
            _placedOn = area;
            FlyoutPlacement.Place(this, area);

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
