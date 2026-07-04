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

            // Accent re-pin (#175 item 21): Wpf.Ui's Apply derives the
            // Secondary/Tertiary accent shades from the base color, which
            // turns ember into a washed #EF9770 salmon on checked CheckBox /
            // ToggleSwitch fills. On dark, pin both the Color and Brush keys
            // to the sanctioned ember ramp. Apply rewrites the Color keys on
            // every call, so light needs no color cleanup. The Brush keys are
            // ours alone (Accent.xaml never updates them), so on light they
            // are removed and lookup falls back to the stock theme.
            if (dark)
            {
                Application.Current.Resources["SystemAccentColorSecondary"] = Accent;
                Application.Current.Resources["SystemAccentColorTertiary"] = EmberHotDark;
                SetBrush("SystemAccentColorSecondaryBrush", Accent);
                SetBrush("SystemAccentColorTertiaryBrush", EmberHotDark);
            }
            else
            {
                Application.Current.Resources.Remove("SystemAccentColorSecondaryBrush");
                Application.Current.Resources.Remove("SystemAccentColorTertiaryBrush");
            }
            // Seg-control track (#175): recessed steel on dark; on light a
            // pale recessed tray so the branded glyphs stay visible.
            SetBrush("SegTrackBrush", dark ? Color.FromRgb(0x0B, 0x0E, 0x14) : Color.FromRgb(0xEC, 0xED, 0xF1));
            SetBrush("SegTrackStrokeBrush", dark ? Color.FromRgb(0x1C, 0x25, 0x36) : Color.FromRgb(0xD6, 0xD8, 0xDE));
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
                SetBrush("SolidBackgroundFillColorBaseBrush", Color.FromRgb(0x0B, 0x0E, 0x14));
                SetBrush("NavigationViewContentBackground", Color.FromRgb(0x0B, 0x0E, 0x14));
                SetBrush("NavigationViewContentGridBorderBrush", Color.FromRgb(0x25, 0x30, 0x49));
                SetBrush("CardBackgroundFillColorDefaultBrush", Color.FromRgb(0x11, 0x16, 0x23));
                SetBrush("CardBackgroundFillColorSecondaryBrush", Color.FromRgb(0x1B, 0x23, 0x33));
                SetBrush("CardStrokeColorDefaultBrush", Color.FromRgb(0x25, 0x30, 0x49));
                SetBrush("ControlFillColorDefaultBrush", Color.FromRgb(0x1B, 0x23, 0x33));
                SetBrush("ControlStrokeColorDefaultBrush", Color.FromRgb(0x25, 0x30, 0x49));
                // Artifact text ramp: primary #E9EDF4, secondary #94A3BD,
                // tertiary #5D6B85. This is what separates the pitch's body
                // from stock WPF-UI neutral gray.
                SetBrush("TextFillColorPrimaryBrush", Color.FromRgb(0xE9, 0xED, 0xF4));
                SetBrush("TextFillColorSecondaryBrush", Color.FromRgb(0x94, 0xA3, 0xBD));
                SetBrush("TextFillColorTertiaryBrush", Color.FromRgb(0x5D, 0x6B, 0x85));
                SetBrush("TextFillColorDisabledBrush", Color.FromRgb(0x3D, 0x4A, 0x63));
                // Slider recolor (#175 item 10): Wpf.Ui's Slider template
                // pulls its colors from these DynamicResource keys, not from
                // TemplateBindings, so a derived Style cannot recolor it.
                // Rail goes raised steel, thumb dot goes ember. There is no
                // decrease-side value-fill element in the Wpf.Ui 4.3.0
                // template (and no SliderTrackValueFill key), so the ember
                // value fill is intentionally not attempted here.
                SetBrush("SliderTrackFill", Color.FromRgb(0x1B, 0x23, 0x33));
                SetBrush("SliderTrackFillPointerOver", Color.FromRgb(0x1B, 0x23, 0x33));
                SetBrush("SliderThumbBackground", Accent);
                SetBrush("SliderThumbBackgroundPointerOver", EmberHotDark);
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
                // End stop stays on the card fill (#175 item 24, spec 417):
                // fading to the page ground made the card melt into it.
                var grad = new LinearGradientBrush(
                    Color.FromRgb(0x11, 0x16, 0x23),
                    Color.FromRgb(0x11, 0x16, 0x23),
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
            "SolidBackgroundFillColorBaseBrush",
            "NavigationViewContentBackground",
            "NavigationViewContentGridBorderBrush",
            "CardBackgroundFillColorDefaultBrush",
            "CardBackgroundFillColorSecondaryBrush",
            "CardStrokeColorDefaultBrush",
            "ControlFillColorDefaultBrush",
            "ControlStrokeColorDefaultBrush",
            "TextFillColorPrimaryBrush",
            "TextFillColorSecondaryBrush",
            "TextFillColorTertiaryBrush",
            "TextFillColorDisabledBrush",
            "SliderTrackFill",
            "SliderTrackFillPointerOver",
            "SliderThumbBackground",
            "SliderThumbBackgroundPointerOver",
        };

        private static void SetBrush(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }
}
