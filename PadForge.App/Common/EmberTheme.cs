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
        private static readonly Color EmberDeepDark = Color.FromRgb(0xC4, 0x3D, 0x0C);

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

                // Chrome unification (#175 control spec §2): pin the accent
                // fill ramp itself. Wpf.Ui's Apply sets AccentFillColorDefault
                // to the derived light shade on dark, which turned every
                // Primary button washed salmon instead of ember. These are
                // Color pins, so Apply rewrites them on every theme change
                // and light needs no removal.
                Application.Current.Resources["SystemAccentColorPrimary"] = Accent;
                Application.Current.Resources["AccentFillColorDefault"] = Accent;
                Application.Current.Resources["AccentFillColorSecondary"] = EmberHotDark;
                Application.Current.Resources["AccentFillColorTertiary"] = EmberDeepDark;
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
            // Steel chrome tokens go theme-swapped (#175 control spec §3):
            // EmberIconButton references them, and the App.xaml constants are
            // dark values, so without a light re-pin the icon buttons would
            // show dark steel chrome on white.
            SetBrush("SteelRaisedBrush", dark ? Color.FromRgb(0x1B, 0x23, 0x33) : Color.FromRgb(0xE4, 0xE7, 0xEE));
            SetBrush("SteelLineSoftBrush", dark ? Color.FromRgb(0x1C, 0x25, 0x36) : Color.FromRgb(0xD6, 0xD8, 0xDE));
            // Text ramp (#175): both grounds get a deliberate steel-tinted
            // hierarchy; stock light gray reads too faint.
            SetBrush("TextFillColorPrimaryBrush", dark ? Color.FromRgb(0xE9, 0xED, 0xF4) : Color.FromRgb(0x1A, 0x24, 0x33));
            SetBrush("TextFillColorSecondaryBrush", dark ? Color.FromRgb(0x94, 0xA3, 0xBD) : Color.FromRgb(0x44, 0x53, 0x6B));
            SetBrush("TextFillColorTertiaryBrush", dark ? Color.FromRgb(0x5D, 0x6B, 0x85) : Color.FromRgb(0x6B, 0x7A, 0x94));
            SetBrush("TextFillColorDisabledBrush", dark ? Color.FromRgb(0x3D, 0x4A, 0x63) : Color.FromRgb(0x9A, 0xA5, 0xB8));
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

                ApplyDarkChrome();
            }
            else
            {
                foreach (var key in SteelKeys)
                    Application.Current.Resources.Remove(key);
                foreach (var key in EmberChromeKeys)
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

        // Chrome unification (#175 control spec §§1-8, route a): every stock
        // control key that StaticResource-captures a translucent ControlFill
        // color at theme load gets a steel-palette brush pinned over it.
        // Buttons are raises (#1B2333 with a top-light elevation border),
        // inputs are recessed wells (#0B0E14), flyout surfaces sit on
        // steel-850 #151C2C. Every key pinned here is listed in
        // EmberChromeKeys so light theme falls back to stock Fluent.
        private static void ApplyDarkChrome()
        {
            var res = Application.Current.Resources;

            // §1 Button: steel raise.
            SetBrush("ButtonBackground", Color.FromRgb(0x1B, 0x23, 0x33));
            SetBrush("ButtonBackgroundPointerOver", Color.FromRgb(0x23, 0x2D, 0x42));
            SetBrush("ButtonBackgroundPressed", Color.FromRgb(0x16, 0x1D, 0x2B));
            SetBrush("ButtonBackgroundDisabled", Color.FromRgb(0x13, 0x19, 0x26));
            SetBrush("ButtonBorderBrushPressed", Color.FromRgb(0x25, 0x30, 0x49));
            SetBrush("ButtonBorderBrushDisabled", Color.FromRgb(0x1C, 0x25, 0x36));
            SetBrush("ButtonForegroundPressed", Color.FromRgb(0x94, 0xA3, 0xBD));

            // §1 raised-steel 1px top-light: same shape as the stock dark
            // ControlElevationBorderBrush (MappingMode=Absolute, 3px run),
            // recolored to steel. Frozen, because gradient brushes created
            // in code must be frozen (WPF crash trap).
            var elevation = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 3),
            };
            elevation.GradientStops.Add(new GradientStop(Color.FromRgb(0x2E, 0x3A, 0x52), 0.33));
            elevation.GradientStops.Add(new GradientStop(Color.FromRgb(0x25, 0x30, 0x49), 1.0));
            elevation.Freeze();
            res["ControlElevationBorderBrush"] = elevation;

            // §2 Primary button: rest fill is the seg-control ember gradient
            // (App.xaml EmberSegGradient canon, 140deg ember to deep). The
            // theme original binds AccentFillColorDefault, so this override
            // must be a brush of its own. Hover/pressed keep the stock
            // brushes, which flow live from the AccentFillColor* pins above.
            var segClone = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0.64, 0.77),
            };
            segClone.GradientStops.Add(new GradientStop(Accent, 0.0));
            segClone.GradientStops.Add(new GradientStop(EmberDeepDark, 1.0));
            segClone.Freeze();
            res["AccentButtonBackground"] = segClone;

            // §2 Primary text: the theme originals StaticResource-capture
            // black TextOnAccentFillColorPrimary at load, so Color pins
            // cannot reach them.
            SetBrush("AccentButtonForeground", Color.FromRgb(0xFF, 0xF3, 0xE8));
            SetBrush("AccentButtonForegroundPointerOver", Color.FromRgb(0xFF, 0xF3, 0xE8));
            SetBrush("AccentButtonForegroundPressed", Color.FromRgb(0xF5, 0xE3, 0xD4));

            // §4 ComboBox closed control.
            SetBrush("ComboBoxBackground", Color.FromRgb(0x1B, 0x23, 0x33));
            SetBrush("ComboBoxBackgroundPointerOver", Color.FromRgb(0x23, 0x2D, 0x42));
            SetBrush("ComboBoxBackgroundDisabled", Color.FromRgb(0x13, 0x19, 0x26));
            SetBrush("ComboBoxBorderBrushDisabled", Color.FromRgb(0x1C, 0x25, 0x36));
            SetBrush("ComboBoxDropDownGlyphForeground", Color.FromRgb(0x94, 0xA3, 0xBD));

            // §4 dropdown popup: steel-850 flyout ground with steel stroke,
            // replacing the stock acrylic. Item hover/highlight goes raised
            // steel (the stock key is 6%-white, invisible on #151C2C), item
            // text joins the steel text ramp. The ember selection pill needs
            // no key here: ComboBoxItemPillFillBrush binds
            // SystemAccentColorPrimary via DynamicResource, so the §2 pin
            // retargets it live.
            SetBrush("ComboBoxDropDownBackground", Color.FromRgb(0x15, 0x1C, 0x2C));
            SetBrush("ComboBoxDropDownBorderBrush", Color.FromRgb(0x25, 0x30, 0x49));
            SetBrush("ComboBoxItemBackgroundSelected", Color.FromRgb(0x1B, 0x23, 0x33));
            SetBrush("ComboBoxForeground", Color.FromRgb(0xE9, 0xED, 0xF4));
            SetBrush("ComboBoxItemForegroundSelected", Color.FromRgb(0xE9, 0xED, 0xF4));
            SetBrush("ComboBoxForegroundDisabled", Color.FromRgb(0x3D, 0x4A, 0x63));

            // §4 sibling flyout surfaces, same ground (iteration-41 raised
            // steel). AcrylicBackgroundFillColorDefault itself stays stock:
            // it also feeds Snackbar/ContentDialog surfaces not audited.
            SetBrush("ContextMenuBackground", Color.FromRgb(0x15, 0x1C, 0x2C));
            SetBrush("FlyoutBackground", Color.FromRgb(0x15, 0x1C, 0x2C));

            // §5 TextBox/NumberBox: recessed wells, uniform inset hairline
            // (a well gets no top-light, that is for raises).
            SetBrush("TextControlBackground", Color.FromRgb(0x0B, 0x0E, 0x14));
            SetBrush("TextControlBackgroundPointerOver", Color.FromRgb(0x0E, 0x13, 0x20));
            SetBrush("TextControlBackgroundFocused", Color.FromRgb(0x0B, 0x0E, 0x14));
            SetBrush("TextControlBackgroundDisabled", Color.FromRgb(0x10, 0x15, 0x1F));
            SetBrush("TextControlBorderBrushDisabled", Color.FromRgb(0x1C, 0x25, 0x36));
            SetBrush("TextControlPlaceholderForeground", Color.FromRgb(0x5D, 0x6B, 0x85));
            SetBrush("TextControlButtonForeground", Color.FromRgb(0x94, 0xA3, 0xBD));
            SetBrush("TextControlElevationBorderBrush", Color.FromRgb(0x1C, 0x25, 0x36));

            // §6 CheckBox rest: well fill + visible steel stroke.
            SetBrush("CheckBoxBackground", Color.FromRgb(0x0B, 0x0E, 0x14));
            SetBrush("CheckBoxBorderBrush", Color.FromRgb(0x5D, 0x6B, 0x85));

            // §7 ScrollBar: solid steel thumb, steel track.
            SetBrush("ScrollBarThumbFill", Color.FromRgb(0x3A, 0x47, 0x60));
            SetBrush("ScrollBarTrackFillPointerOver", Color.FromRgb(0x11, 0x16, 0x23));
            SetBrush("ScrollBarButtonArrowForeground", Color.FromRgb(0x94, 0xA3, 0xBD));

            // §8 ToolTip: flyout ground.
            // Dropdown stragglers (#175): some popup surfaces resolve
            // through the acrylic/focused keys instead of the plain ones.
            SetBrush("AcrylicBackgroundFillColorDefaultBrush", Color.FromRgb(0x15, 0x1C, 0x2C));
            SetBrush("SystemFillColorSolidNeutralBackgroundBrush", Color.FromRgb(0x15, 0x1C, 0x2C));
            SetBrush("ComboBoxBackgroundFocused", Color.FromRgb(0x1B, 0x23, 0x33));
            SetBrush("ToolTipBackground", Color.FromRgb(0x15, 0x1C, 0x2C));
            SetBrush("ToolTipBorderBrush", Color.FromRgb(0x25, 0x30, 0x49));
            SetBrush("ToolTipForeground", Color.FromRgb(0xE9, 0xED, 0xF4));
        }

        private static readonly string[] EmberChromeKeys =
        {
            "AcrylicBackgroundFillColorDefaultBrush",
            "SystemFillColorSolidNeutralBackgroundBrush",
            "ComboBoxBackgroundFocused",
            // §1 Button
            "ButtonBackground",
            "ButtonBackgroundPointerOver",
            "ButtonBackgroundPressed",
            "ButtonBackgroundDisabled",
            "ButtonBorderBrushPressed",
            "ButtonBorderBrushDisabled",
            "ButtonForegroundPressed",
            "ControlElevationBorderBrush",
            // §2 Primary (Color pins self-heal, brushes must be removed)
            "AccentButtonBackground",
            "AccentButtonForeground",
            "AccentButtonForegroundPointerOver",
            "AccentButtonForegroundPressed",
            // §4 ComboBox + flyout surfaces
            "ComboBoxBackground",
            "ComboBoxBackgroundPointerOver",
            "ComboBoxBackgroundDisabled",
            "ComboBoxBorderBrushDisabled",
            "ComboBoxDropDownGlyphForeground",
            "ComboBoxDropDownBackground",
            "ComboBoxDropDownBorderBrush",
            "ComboBoxItemBackgroundSelected",
            "ComboBoxForeground",
            "ComboBoxItemForegroundSelected",
            "ComboBoxForegroundDisabled",
            "ContextMenuBackground",
            "FlyoutBackground",
            // §5 TextControl
            "TextControlBackground",
            "TextControlBackgroundPointerOver",
            "TextControlBackgroundFocused",
            "TextControlBackgroundDisabled",
            "TextControlBorderBrushDisabled",
            "TextControlPlaceholderForeground",
            "TextControlButtonForeground",
            "TextControlElevationBorderBrush",
            // §6 CheckBox
            "CheckBoxBackground",
            "CheckBoxBorderBrush",
            // §7 ScrollBar
            "ScrollBarThumbFill",
            "ScrollBarTrackFillPointerOver",
            "ScrollBarButtonArrowForeground",
            // §8 ToolTip
            "ToolTipBackground",
            "ToolTipBorderBrush",
            "ToolTipForeground",
        };

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
