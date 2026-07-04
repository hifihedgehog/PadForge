using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace PadForge.Common
{
    // Ember identity (#175): the app accent is the forge orange from the
    // PadForge logo, never the Windows accent. Re-apply after every theme
    // apply, because ApplicationThemeManager.Apply and ApplySystemTheme
    // re-derive the accent from the system color by default.
    internal static class EmberTheme
    {
        public static readonly Color Accent = Color.FromRgb(0xFF, 0x6B, 0x2C);

        // Dark-ground text colors.
        private static readonly Color ColdDark = Color.FromRgb(0x58, 0xB6, 0xE4);
        private static readonly Color ColdDeepDark = Color.FromRgb(0x2E, 0x6A, 0x8F);
        private static readonly Color EmberHotDark = Color.FromRgb(0xFF, 0xA2, 0x4D);

        // Light-ground variants (#175 Light sweep): the dark-ground cold and
        // hot-ember tones fail contrast on white, so text brushes deepen.
        private static readonly Color ColdLight = Color.FromRgb(0x1E, 0x6E, 0x9F);
        private static readonly Color ColdDeepLight = Color.FromRgb(0x17, 0x54, 0x7A);
        private static readonly Color EmberHotLight = Color.FromRgb(0xC2, 0x4A, 0x12);

        public static void ApplyAccent()
        {
            var theme = ApplicationThemeManager.GetAppTheme();
            ApplicationAccentColorManager.Apply(Accent, theme);

            // Swap the identity text brushes for the active ground. Consumers
            // bind with DynamicResource, so an app-level override wins over
            // the App.xaml defaults and retargets live.
            bool dark = theme == ApplicationTheme.Dark;
            SetBrush("ColdBrush", dark ? ColdDark : ColdLight);
            SetBrush("ColdDeepBrush", dark ? ColdDeepDark : ColdDeepLight);
            SetBrush("EmberHotBrush", dark ? EmberHotDark : EmberHotLight);

            // Steel ground (#175 pitch): on dark, the WPF-UI translucent-gray
            // surface tokens swap to the steel palette so every page sits on
            // #0B0E14 with #111623 cards and #253049 strokes. On light the
            // overrides are removed so lookup falls back to the stock theme.
            if (dark)
            {
                SetBrush("ApplicationBackgroundBrush", Color.FromRgb(0x0B, 0x0E, 0x14));
                SetBrush("NavigationViewContentBackground", Color.FromRgb(0x0B, 0x0E, 0x14));
                SetBrush("NavigationViewContentGridBorderBrush", Color.FromRgb(0x25, 0x30, 0x49));
                SetBrush("CardBackgroundFillColorDefaultBrush", Color.FromRgb(0x11, 0x16, 0x23));
                SetBrush("CardBackgroundFillColorSecondaryBrush", Color.FromRgb(0x1B, 0x23, 0x33));
                SetBrush("CardStrokeColorDefaultBrush", Color.FromRgb(0x25, 0x30, 0x49));
                SetBrush("ControlFillColorDefaultBrush", Color.FromRgb(0x1B, 0x23, 0x33));
                SetBrush("ControlStrokeColorDefaultBrush", Color.FromRgb(0x25, 0x30, 0x49));
            }
            else
            {
                foreach (var key in SteelKeys)
                    Application.Current.Resources.Remove(key);
            }

            // Crucible card ground (#175 pitch): on the dark ground the slot
            // cards carry a subtle vertical steel gradient instead of the
            // flat card fill. On light ground they fall back to the theme's
            // own card fill so the gradient never fights a white page.
            if (dark)
            {
                var grad = new LinearGradientBrush(
                    Color.FromRgb(0x11, 0x16, 0x23),
                    Color.FromRgb(0x0B, 0x0E, 0x14),
                    new Point(0, 0), new Point(0, 1));
                grad.Freeze();
                Application.Current.Resources["CrucibleCardBrush"] = grad;
            }
            else if (Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] is Brush cardFill)
            {
                Application.Current.Resources["CrucibleCardBrush"] = cardFill;
            }
        }

        private static readonly string[] SteelKeys =
        {
            "ApplicationBackgroundBrush",
            "NavigationViewContentBackground",
            "NavigationViewContentGridBorderBrush",
            "CardBackgroundFillColorDefaultBrush",
            "CardBackgroundFillColorSecondaryBrush",
            "CardStrokeColorDefaultBrush",
            "ControlFillColorDefaultBrush",
            "ControlStrokeColorDefaultBrush",
        };

        private static void SetBrush(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }
}
