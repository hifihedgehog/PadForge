using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace PadForge.Views.Controls
{
    /// <summary>
    /// Active-profile pill (#175 item 8). Two instances exist (pad-page
    /// tier-1 bar, status-bar instrument cluster); both show the same
    /// authoritative MainViewModel.Settings.ActiveProfileInfo and both
    /// flare when an auto-switch actually changes the profile.
    /// </summary>
    public partial class ProfilePill : UserControl
    {
        /// <summary>Raised on pill click; MainWindow opens the switcher flyout.</summary>
        public event EventHandler Clicked;

        public ProfilePill()
        {
            InitializeComponent();

            // Block DataContext inheritance until the Loaded rebind below:
            // the pill realizes under its host's context (MainViewModel in
            // the status bar), where ActiveProfileInfo doesn't exist, so
            // every launch logged a binding path error before Loaded fired.
            // A null context keeps the bindings dormant instead.
            PillBorder.DataContext = null;

            // The hosts inherit different DataContexts. Rebind to the
            // single notifying source instead: MainViewModel.Settings.
            // Resolved on Loaded because MainWindow assigns its DataContext
            // after InitializeComponent (same lookup as PadPage's
            // Application.Current.MainWindow pattern).
            Loaded += (_, _) =>
            {
                if (Application.Current.MainWindow?.DataContext is ViewModels.MainViewModel vm)
                    PillBorder.DataContext = vm.Settings;
            };
        }

        private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Short code-driven glow pulse when an auto-switch lands: the
        /// mini-card heat ring shape (BeginAnimation on the effect's
        /// opacity, never from style triggers). Reduced motion (#175
        /// item 98, SystemParameters.ClientAreaAnimation): no pulse; the
        /// pill text updating is the remaining cue.
        /// </summary>
        public void Flare()
        {
            if (!SystemParameters.ClientAreaAnimation) return;

            var glow = new DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0x6B, 0x2C),
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.7,
            };
            PillBorder.Effect = glow;
            var pulse = new DoubleAnimation(0.7, 0.0, new Duration(TimeSpan.FromMilliseconds(900)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            };
            // Drop the effect when done so the pill renders effect-free at
            // rest (a live 0-opacity DropShadowEffect still costs a render
            // surface). Guarded in case a second flare replaced it.
            pulse.Completed += (s, e) =>
            {
                if (ReferenceEquals(PillBorder.Effect, glow))
                    PillBorder.Effect = null;
            };
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
        }
    }
}
