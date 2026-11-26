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
        private const int WS_EX_TRANSPARENT = 0x00000020;
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
        private const string VCsEnabledIcon = "\uE73E";   // checkmark \u2014 same as ActiveIcon
        private const string VCsDisabledIcon = "\uE7E8";  // Cancel glyph

        // Slide travel distance — enough to fully hide the flyout below the clip boundary.
        private const double SlideTravel = 80;

        // The amber of the offline line's glyph, which a device that
        // disconnects is drawn in too, and the green of the check for the
        // controllers turned on, which a device that connects takes.
        // Green, gold and red are the app's colors for health.
        private static readonly Color OfflineAmber = Color.FromRgb(0xFF, 0xB9, 0x00);
        private static readonly Color EnabledGreen = Color.FromRgb(0x37, 0xC8, 0x52);

        /// <summary>How long a note stays (#600 item 42): a drop, the note
        /// that matters more, four seconds, and every other note two.</summary>
        internal static readonly TimeSpan NoteTime = TimeSpan.FromSeconds(2);
        internal static readonly TimeSpan DropNoteTime = TimeSpan.FromSeconds(4);

        /// <summary>How long a device note stays: a device that connected
        /// two seconds, a drop, a burst of drops or a device that keeps
        /// disconnecting four.</summary>
        internal static TimeSpan DeviceNoteTime(bool connected) => connected ? NoteTime : DropNoteTime;

        // The monitor the flyout was last put on, for a window WPF sizes
        // again on a monitor of another scale.
        private FlyoutPlacement.WorkArea? _placedOn;

        private readonly DispatcherTimer _dismissTimer;
        private readonly DispatcherTimer _initMonitorTimer;
        private readonly TranslateTransform _slideTransform;
        private bool _showingInitializing;

        /// <summary>What the flyout shows, from the call that shows it until
        /// the window has slid away and is hidden. A profile switch keeps
        /// its state through Initializing, Forging and the offline
        /// line.</summary>
        private enum Showing { Nothing, Profile, VirtualControllers, DeviceNote }

        private Showing _showing;

        // Counts every state shown. A slide away that began under an earlier
        // state leaves the window to the one shown since.
        private int _shows;

        // A slide away runs: the dismiss timer has fired and the window has
        // not yet been hidden.
        private bool _slidingAway;

        /// <summary>Whether the flyout shows anything.</summary>
        public bool IsUp => _showing != Showing.Nothing;

        /// <summary>Whether what it shows is a device note (#597).</summary>
        public bool IsShowingDeviceNote => _showing == Showing.DeviceNote;

        public Func<(bool anyInitializing, bool allReady)> CheckInitState { get; set; }
        public Func<bool> CheckAnyOffline { get; set; }

        public ProfileSwitchOverlay()
        {
            InitializeComponent();

            // Slide animation transform on the inner FlyoutPanel; ClipPanel clips it.
            _slideTransform = new TranslateTransform(0, SlideTravel);
            FlyoutPanel.RenderTransform = _slideTransform;

            Loaded += OnLoaded;
            SizeChanged += (_, _) =>
            {
                if (IsVisible && _placedOn is { } area) FlyoutPlacement.Place(this, area);
            };
            // Through a change of scale, at the bottom center of the monitor
            // it was put on.
            FlyoutPlacement.KeepPlace(this, (width, height, _) => _placedOn is { } area ? FlyoutPlacement.BottomCenterInPixels(area, width, height) : null);

            _dismissTimer = new DispatcherTimer { Interval = NoteTime };
            _initMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _initMonitorTimer.Tick += OnInitMonitorTick;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            // Clicks pass through to the game, as the shift-layer flyout's
            // do (#600 item 42).
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);

            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);

            // No theme here. Every state that shows the window applies the
            // theme and then colors its own glyph, and the first load arrives
            // inside that state's ShowFlyout. A theme applied here took the
            // color away again: the first time the controllers were turned
            // on or off, and the first device note, drew a plain glyph.
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

        public void StopTimers()
        {
            _dismissTimer.Stop();
            _initMonitorTimer.Stop();
        }

        /// <summary>Show the Win11-volume-OSD-style flyout for a bulk virtual-controller
        /// enable/disable toggle (#91). 2-second slide-in + dismiss, no init monitoring.
        /// Glyph reads at a glance even with sound off — green checkmark for
        /// enabled, red Cancel glyph for disabled — matching the universal
        /// traffic-light convention used in Windows system OSDs.</summary>
        public void ShowVCsToggle(bool enabled)
        {
            _dismissTimer.Stop();
            _initMonitorTimer.Stop();
            _showingInitializing = false;
            _showing = Showing.VirtualControllers;

            ApplyTheme();
            StatusIcon.BeginAnimation(OpacityProperty, null);
            StatusIcon.Opacity = 1;
            StatusIcon.Text = enabled ? VCsEnabledIcon : VCsDisabledIcon;
            StatusIcon.Foreground = enabled
                ? new SolidColorBrush(EnabledGreen)                     // Fluent success green
                : new SolidColorBrush(Color.FromRgb(0xE8, 0x1B, 0x1C)); // Fluent critical red
            StatusText.Text = enabled
                ? Strings.Instance.Main_VCsEnabled
                : Strings.Instance.Main_VCsDisabled;

            ShowFlyout();

            _dismissTimer.Tick -= OnDismissThenClose;
            _dismissTimer.Tick -= OnDismissThenMonitorInit;
            _dismissTimer.Tick -= OnDismissThenCheckOffline;
            _dismissTimer.Tick += OnDismissThenClose;
            _dismissTimer.Interval = TimeSpan.FromSeconds(2);
            _dismissTimer.Start();
        }

        /// <summary>Shows one line about a mapped device (#597): a device
        /// that disconnects under the offline line's glyph in its amber for
        /// four seconds, one that connects under the check in the green of
        /// Virtual Controllers Enabled for two (<see cref="DeviceNoteTime"/>).
        /// InputService words the line and shows one at a time, while the
        /// flyout shows nothing else.</summary>
        public void ShowDeviceNote(string text, bool connected)
        {
            _dismissTimer.Stop();
            _initMonitorTimer.Stop();
            _showingInitializing = false;
            _showing = Showing.DeviceNote;

            ApplyTheme();
            StatusIcon.BeginAnimation(OpacityProperty, null);
            StatusIcon.Opacity = 1;
            StatusIcon.Text = connected ? VCsEnabledIcon : OfflineIcon;
            StatusIcon.Foreground = new SolidColorBrush(connected ? EnabledGreen : OfflineAmber);
            StatusText.Text = text;

            ShowFlyout();

            _dismissTimer.Tick -= OnDismissThenClose;
            _dismissTimer.Tick -= OnDismissThenMonitorInit;
            _dismissTimer.Tick -= OnDismissThenCheckOffline;
            _dismissTimer.Tick += OnDismissThenClose;
            _dismissTimer.Interval = DeviceNoteTime(connected);
            _dismissTimer.Start();
        }

        /// <summary>Changes the device note on screen in place (#600 item
        /// 42): a device it names came back ("is back", the check in green),
        /// dropped again, or dropped once more on its count. The window does
        /// not slide in again. <paramref name="restartTime"/> gives the note
        /// its full time from now, as the new state's own note would have,
        /// and false keeps the time it has left, for a count that grew. A
        /// note that was sliding away stands again where it shows, and the
        /// end of that slide hides nothing. With no device note up, it is
        /// shown as a new one.</summary>
        public void UpdateDeviceNote(string text, bool connected, bool restartTime)
        {
            if (_showing != Showing.DeviceNote)
            {
                ShowDeviceNote(text, connected);
                return;
            }
            if (_slidingAway)
            {
                _shows++;
                _slidingAway = false;
                _slideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                _slideTransform.Y = 0;
                restartTime = true;
            }

            StatusIcon.Text = connected ? VCsEnabledIcon : OfflineIcon;
            StatusIcon.Foreground = new SolidColorBrush(connected ? EnabledGreen : OfflineAmber);
            StatusText.Text = text;
            if (!restartTime) return;

            _dismissTimer.Stop();
            _dismissTimer.Tick -= OnDismissThenClose;
            _dismissTimer.Tick -= OnDismissThenMonitorInit;
            _dismissTimer.Tick -= OnDismissThenCheckOffline;
            _dismissTimer.Tick += OnDismissThenClose;
            _dismissTimer.Interval = DeviceNoteTime(connected);
            _dismissTimer.Start();
        }

        public void ShowProfileName(string profileName)
        {
            _dismissTimer.Stop();
            _initMonitorTimer.Stop();
            _showingInitializing = false;
            _showing = Showing.Profile;

            ApplyTheme();
            StatusIcon.BeginAnimation(OpacityProperty, null);
            StatusIcon.Opacity = 1;
            StatusIcon.Text = ProfileIcon;
            StatusText.Text = profileName;

            ShowFlyout();

            _dismissTimer.Tick -= OnDismissThenClose;
            _dismissTimer.Tick -= OnDismissThenMonitorInit;
            // The third handler has to come off too. Its sibling sites detach
            // all three before attaching one; this one detached two, so a
            // pending CheckOffline stayed subscribed and fired alongside the
            // MonitorInit handler on the next tick.
            _dismissTimer.Tick -= OnDismissThenCheckOffline;
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
                SlideAway();
        }

        private void ShowOffline()
        {
            StatusIcon.BeginAnimation(OpacityProperty, null);
            StatusIcon.Opacity = 1;
            StatusIcon.Text = OfflineIcon;
            StatusText.Text = Strings.Instance.Main_ControllersOffline;
            StatusIcon.Foreground = new SolidColorBrush(OfflineAmber);

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

            SlideAway();
        }

        /// <summary>Slides the flyout out and hides the window, unless a
        /// state was shown while it slid. A slide that is taken off its
        /// property can still report its end: measured 2026-11-23, an
        /// animation replaced 60 ms in raised Completed at its own 250 ms.
        /// That end hid whatever had been shown in between.</summary>
        private void SlideAway()
        {
            int shows = _shows;
            _slidingAway = true;
            SlideOut(() =>
            {
                if (shows != _shows) return;
                _slidingAway = false;
                Hide();
                ApplyTheme();
                _showing = Showing.Nothing;
            });
        }

        private void ShowFlyout()
        {
            // The monitor of the window in front (#600 item 42).
            var area = FlyoutPlacement.Front();
            _shows++;
            _slidingAway = false;

            // One line that fits that monitor: a long name is cut with an
            // ellipsis where the flyout would run past the work area. The
            // panel's margins, the border, the padding and the glyph take 80,
            // and 16 more keep the flyout off the monitor's sides.
            StatusText.MaxWidth = FlyoutPlacement.LineWidth(area);

            UpdateLayout();
            Show();
            UpdateLayout();

            // Centered across the work area, the panel's bottom margin above
            // the taskbar.
            _placedOn = area;
            FlyoutPlacement.Place(this, area);

            SlideIn();
        }
    }
}
