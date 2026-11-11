using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PadForge.Views.Controls
{
    /// <summary>
    /// The profile switcher in the title bar (#574). One instance exists.
    /// It shows the authoritative MainViewModel.Settings.ActiveProfileInfo
    /// and marks the moment an auto-switch changes the profile.
    /// </summary>
    public partial class ProfilePill : UserControl
    {
        /// <summary>Raised on a click. MainWindow opens the switcher.</summary>
        public event EventHandler Clicked;

        public ProfilePill()
        {
            InitializeComponent();

            // Block DataContext inheritance until the Loaded rebind below:
            // the pill realizes under its host's context (MainViewModel in
            // the main window), where ActiveProfileInfo doesn't exist, so
            // every launch logged a binding path error before Loaded fired.
            // A null context keeps the bindings dormant instead.
            PillBorder.DataContext = null;

            // The host's DataContext is not the pill's. Rebind to the
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

        private void Pill_Click(object sender, RoutedEventArgs e)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Marks an auto-switch that just landed: the box's outline turns
        /// ember and cools back to its own color in under a second. With
        /// Windows animations off (SystemParameters.ClientAreaAnimation)
        /// nothing moves, and the name changing is the cue.
        /// </summary>
        public void Flare()
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            if (TryFindResource("SteelPillLineBrush") is not SolidColorBrush rest) return;

            // The style's brush is frozen and shared, so the pulse runs on
            // a brush of its own, and the style's value comes back when it
            // ends. A second flare that starts before then owns its own
            // brush, and the first one's end leaves it alone.
            var edge = new SolidColorBrush(Common.EmberTheme.Accent);
            PillBorder.BorderBrush = edge;
            var cool = new ColorAnimation(Common.EmberTheme.Accent, rest.Color, new Duration(TimeSpan.FromMilliseconds(900)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            };
            cool.Completed += (s, e) =>
            {
                if (ReferenceEquals(PillBorder.BorderBrush, edge))
                    PillBorder.ClearValue(Border.BorderBrushProperty);
            };
            edge.BeginAnimation(SolidColorBrush.ColorProperty, cool);
        }
    }
}
