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
        private static readonly Color ColdDark = Color.FromRgb(0x7C, 0xC7, 0xEA);
        private static readonly Color ColdDeepDark = Color.FromRgb(0x2E, 0x6A, 0x8F);
        private static readonly Color EmberHotDark = Color.FromRgb(0xFF, 0x8A, 0x57);
        private static readonly Color EmberDeepDark = Color.FromRgb(0xC4, 0x3D, 0x0C);

        // Light-ground variants (#175 Light sweep): the dark-ground cold and
        // hot-ember tones fail contrast on white, so text brushes deepen.
        private static readonly Color ColdLight = Color.FromRgb(0x1E, 0x6E, 0x9F);
        private static readonly Color ColdDeepLight = Color.FromRgb(0x17, 0x54, 0x7A);
        private static readonly Color EmberHotLight = Color.FromRgb(0xC2, 0x4A, 0x12);
        // Light accent ramp mid-stop (round-3 A2): sits between raw ember
        // and the deepened hot, for the Secondary accent brush on white.
        private static readonly Color EmberMidLight = Color.FromRgb(0xE6, 0x60, 0x28);

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
            // ours alone (Accent.xaml never updates them), so light pins its
            // own deepened ember ramp (round-3 A2): removing them let lookup
            // fall back to the stock brushes carrying the default Windows
            // blue (the Devices selection pipe sampled #003E92 on light).
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

                // Accent fill Brush keys: Apply materializes these three via
                // ToBrush() from the derived shade (on dark that is ember at
                // +17 brightness / -45 saturation = #FFBB9F), so the Color
                // pins above never reach them. Every Brush-key consumer
                // sampled that peach: the 3D stick wedges and mapping arrows,
                // the schematic rig accents, the FFB force vector. Apply
                // rewrites the Brush keys on every call too, so light needs
                // no removal here either.
                SetBrush("AccentFillColorDefaultBrush", Accent);
                SetBrush("AccentFillColorSecondaryBrush", EmberHotDark);
                SetBrush("AccentFillColorTertiaryBrush", EmberDeepDark);
            }
            else
            {
                SetBrush("SystemAccentColorSecondaryBrush", EmberMidLight);
                SetBrush("SystemAccentColorTertiaryBrush", EmberHotLight);
            }
            // Seg-control track (#175): recessed steel on dark; on light a
            // pale recessed tray so the branded glyphs stay visible.
            // Recessed tray + hairline tokens get light variants too, so
            // the raw-input cells and instrument tracks never stay pitch
            // black on a white page.
            SetBrush("SteelGroundBrush", dark ? Color.FromRgb(0x0A, 0x0E, 0x15) : Color.FromRgb(0xEC, 0xED, 0xF1));
            SetBrush("SteelLineBrush", dark ? Color.FromRgb(0x2A, 0x35, 0x46) : Color.FromRgb(0xC9, 0xCD, 0xD6));
            // Steel chrome tokens go theme-swapped (#175 control spec §3):
            // EmberIconButton references them, and the App.xaml constants are
            // dark values, so without a light re-pin the icon buttons would
            // show dark steel chrome on white.
            SetBrush("SteelRaisedBrush", dark ? Color.FromRgb(0x18, 0x20, 0x2E) : Color.FromRgb(0xE4, 0xE7, 0xEE));
            SetBrush("SteelLineSoftBrush", dark ? Color.FromRgb(0x1E, 0x27, 0x34) : Color.FromRgb(0xD6, 0xD8, 0xDE));
            // An icon button's fill while it is pressed: a step under the
            // raised fill in each theme. A fixed dark value drew a
            // near-black square under every reset pressed on the light one.
            SetBrush("SteelPressedBrush", dark ? Color.FromRgb(0x16, 0x1D, 0x2B) : Color.FromRgb(0xD6, 0xD8, 0xDE));
            // PadForge 6 surfaces (#574): the pane (the sidebar) with its
            // hairline, the field a value is typed in, a raised control
            // under the pointer, and the engine's block at the sidebar's
            // foot. Then the tinted grounds: the square behind a lit flame,
            // a running controller's edge, the selected controller, the
            // text on an ember fill, the band that offers a device, and a
            // dashed outline.
            SetBrush("SteelPaneBrush", dark ? Color.FromRgb(0x0A, 0x0E, 0x16) : Color.FromRgb(0xF3, 0xF4, 0xF7));
            SetBrush("SteelPaneLineBrush", dark ? Color.FromRgb(0x16, 0x1E, 0x2B) : Color.FromRgb(0xD6, 0xD8, 0xDE));
            // The window's own ground, under the title bar: the darkest
            // step, with the sidebar one step up, the page another and a
            // card another. The title bar is warm while the engine runs,
            // fainter on a light ground.
            SetBrush("SteelWindowBrush", dark ? Color.FromRgb(0x07, 0x0A, 0x10) : Color.FromRgb(0xE9, 0xEB, 0xF0));
            SetTitleWarmth(dark ? (byte)0x66 : (byte)0x38);
            // A forging controller's pill: a soft ember outline and wash.
            // A waiting one: a gold outline.
            SetBrush("EmberRingSoftBrush", dark ? Color.FromArgb(0x73, 0xFF, 0x6B, 0x2C) : Color.FromArgb(0x99, 0xC2, 0x4A, 0x12));
            SetBrush("EmberWashBrush", dark ? Color.FromArgb(0x0D, 0xFF, 0x6B, 0x2C) : Color.FromArgb(0x14, 0xFF, 0x6B, 0x2C));
            SetBrush("WaitRingBrush", dark ? Color.FromArgb(0x66, 0xF2, 0xB8, 0x4B) : Color.FromArgb(0x99, 0xB0, 0x7C, 0x10));
            // The edge of a forging card and of its state pill, the fainter
            // edge of an extra that is on, the gold wash of a waiting pill,
            // and the steel of a mark at rest.
            SetBrush("EmberRingBrush", dark ? Color.FromArgb(0x8C, 0xFF, 0x6B, 0x2C) : Color.FromArgb(0xB3, 0xC2, 0x4A, 0x12));
            SetBrush("EmberRingFaintBrush", dark ? Color.FromArgb(0x4D, 0xFF, 0x6B, 0x2C) : Color.FromArgb(0x66, 0xC2, 0x4A, 0x12));
            SetBrush("WaitTintBrush", dark ? Color.FromArgb(0x1A, 0xF2, 0xB8, 0x4B) : Color.FromArgb(0x24, 0xB0, 0x7C, 0x10));
            SetBrush("SteelMarkBrush", dark ? Color.FromRgb(0x3A, 0x47, 0x5A) : Color.FromRgb(0xAE, 0xB6, 0xC4));
            SetBrush("SteelFieldBrush", dark ? Color.FromRgb(0x0D, 0x13, 0x1D) : Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush("SteelLiftBrush", dark ? Color.FromRgb(0x21, 0x2C, 0x3D) : Color.FromRgb(0xD9, 0xDD, 0xE6));
            SetBrush("SteelFootBrush", dark ? Color.FromRgb(0x10, 0x17, 0x22) : Color.FromRgb(0xE4, 0xE7, 0xEE));
            // The page area behind a dialog drawn inside the window: one step
            // under the page ground in either theme.
            SetBrush("ShellDialogGroundBrush", dark ? Color.FromRgb(0x06, 0x09, 0x0E) : Color.FromRgb(0xE4, 0xE7, 0xEE));
            SetBrush("SteelPillLineBrush", dark ? Color.FromRgb(0x26, 0x31, 0x42) : Color.FromRgb(0xC9, 0xCD, 0xD6));
            // The chosen segment of a segmented switch, on the field's fill.
            SetBrush("SteelSegmentBrush", dark ? Color.FromRgb(0x26, 0x31, 0x42) : Color.FromRgb(0xD9, 0xDD, 0xE6));
            // A band across a card: the header of a group of rows.
            SetBrush("SteelBandBrush", dark ? Color.FromRgb(0x0F, 0x15, 0x20) : Color.FromRgb(0xF3, 0xF4, 0xF7));
            // A part card's tray, where its optional features stand as lines
            // under its More row, and the well of a live view, a step under
            // the card with a hairline of its own (the sections pass).
            SetBrush("SteelTrayBrush", dark ? Color.FromRgb(0x0D, 0x13, 0x1D) : Color.FromRgb(0xF3, 0xF4, 0xF7));
            SetBrush("SteelWellBrush", dark ? Color.FromRgb(0x0A, 0x0F, 0x17) : Color.FromRgb(0xEE, 0xF0, 0xF4));
            SetBrush("SteelWellLineBrush", dark ? Color.FromRgb(0x16, 0x1E, 0x2B) : Color.FromRgb(0xDD, 0xE1, 0xE8));
            // The sidebar's rows. The selected one takes the raised fill and
            // one under the pointer a fainter lift. A controller's pill and
            // the Add Controller entry clear the row's own fills (their
            // entry shadows the three row keys), so the lift they take
            // under the pointer has a key of its own, SteelPaneHoverBrush,
            // with the row's color.
            var paneHover = dark ? Color.FromRgb(0x12, 0x19, 0x25) : Color.FromRgb(0xEC, 0xEE, 0xF2);
            SetBrush("NavigationViewItemBackgroundSelected", dark ? Color.FromRgb(0x14, 0x1C, 0x2A) : Color.FromRgb(0xE4, 0xE7, 0xEE));
            SetBrush("NavigationViewItemBackgroundPointerOver", paneHover);
            SetBrush("NavigationViewItemBackgroundPressed", dark ? Color.FromRgb(0x14, 0x1B, 0x27) : Color.FromRgb(0xE8, 0xEA, 0xEF));
            SetBrush("SteelPaneHoverBrush", paneHover);
            // Inside a card: a full-width row under the pointer, a reset
            // button's glyph at rest, and the label of a setting that
            // belongs to the row above it.
            SetBrush("SteelCardHoverBrush", dark ? Color.FromRgb(0x16, 0x1E, 0x2B) : Color.FromRgb(0xF0, 0xF2, 0xF6));
            SetBrush("QuietGlyphBrush", dark ? Color.FromRgb(0x6B, 0x78, 0x90) : Color.FromRgb(0x8A, 0x96, 0xAB));
            SetBrush("SubRowTextBrush", dark ? Color.FromRgb(0xCF, 0xD7, 0xE3) : Color.FromRgb(0x2D, 0x39, 0x4B));
            // The picked item of a list that stands on a card.
            SetBrush("EmberCardPickBrush", dark ? Color.FromRgb(0x24, 0x16, 0x0D) : Color.FromRgb(0xFF, 0xEA, 0xDF));
            // The ring on a control of the picture under the pointer (#574):
            // the boards' ember at 60 percent, and on light the deeper ember
            // at 60 percent.
            // An input pill of a macro's trigger: its edge and its words.
            SetBrush("InputChipEdgeBrush", dark ? Color.FromRgb(0x33, 0x50, 0x6A) : Color.FromRgb(0x9D, 0xBB, 0xD2));
            SetBrush("InputChipTextBrush", dark ? Color.FromRgb(0xCF, 0xE6, 0xF5) : Color.FromRgb(0x1D, 0x4B, 0x6B));
            SetBrush("EmberWellBrush", dark ? Color.FromRgb(0x2A, 0x15, 0x09) : Color.FromRgb(0xFF, 0xE9, 0xDD));
            SetBrush("EmberEdgeBrush", dark ? Color.FromRgb(0x4A, 0x2C, 0x1A) : Color.FromRgb(0xF0, 0xB7, 0x9C));
            SetBrush("EmberPickBrush", dark ? Color.FromRgb(0x1D, 0x16, 0x11) : Color.FromRgb(0xFF, 0xF1, 0xE9));
            SetBrush("EmberInkBrush", Color.FromRgb(0x16, 0x0A, 0x03));
            SetBrush("EmberLiftBrush", Color.FromRgb(0xFF, 0x84, 0x50));
            SetBrush("EmberPressBrush", Color.FromRgb(0xE8, 0x5F, 0x25));
            SetBrush("DangerEdgeBrush", dark ? Color.FromRgb(0x7A, 0x3A, 0x1A) : Color.FromRgb(0xC9, 0x6A, 0x3D));
            SetBrush("DangerInkBrush", dark ? Color.FromRgb(0xFF, 0xB0, 0x8A) : Color.FromRgb(0x9A, 0x3A, 0x10));
            SetBrush("LinkHoverBrush", dark ? Color.FromRgb(0xFF, 0xB0, 0x8A) : Color.FromRgb(0x9A, 0x3A, 0x10));
            SetBrush("WaitWellBrush", dark ? Color.FromRgb(0x17, 0x15, 0x0F) : Color.FromRgb(0xFF, 0xF6, 0xDF));
            SetBrush("WaitEdgeBrush", dark ? Color.FromRgb(0x3D, 0x32, 0x20) : Color.FromRgb(0xE8, 0xCE, 0x8A));
            SetBrush("WaitTextBrush", dark ? Color.FromRgb(0xE8, 0xD9, 0xB5) : Color.FromRgb(0x5C, 0x4A, 0x16));
            SetBrush("WaitMutedBrush", dark ? Color.FromRgb(0xCD, 0xB9, 0x8D) : Color.FromRgb(0x7A, 0x65, 0x28));
            // Gold as text and marks (#574): Low beside a battery, Awaiting
            // devices in a state pill, the unsaved changes line. Raw gold
            // reads about 1.8 to 1 on white, so light deepens it to hold 4.5
            // to 1 on a card, on the wait well and on the wait tint.
            SetBrush("WaitBrush", dark ? Color.FromRgb(0xF2, 0xB8, 0x4B) : Color.FromRgb(0x8F, 0x62, 0x00));
            SetBrush("DashLineBrush", dark ? Color.FromRgb(0x34, 0x41, 0x54) : Color.FromRgb(0xAE, 0xB6, 0xC4));
            // Text ramp (#175): both grounds get a deliberate steel-tinted
            // hierarchy; stock light gray reads too faint.
            SetBrush("TextFillColorPrimaryBrush", dark ? Color.FromRgb(0xEE, 0xF2, 0xF7) : Color.FromRgb(0x1A, 0x24, 0x33));
            SetBrush("TextFillColorSecondaryBrush", dark ? Color.FromRgb(0xA9, 0xB4, 0xC4) : Color.FromRgb(0x44, 0x53, 0x6B));
            SetBrush("TextFillColorTertiaryBrush", dark ? Color.FromRgb(0x86, 0x93, 0xA6) : Color.FromRgb(0x6B, 0x7A, 0x94));
            SetBrush("TextFillColorDisabledBrush", dark ? Color.FromRgb(0x56, 0x62, 0x78) : Color.FromRgb(0x9A, 0xA5, 0xB8));
            // The words of the picked section and of a segmented switch's
            // chosen segment (#574): white on dark, as the boards draw them,
            // a step over the text color. Light has no step over its text
            // color, so it keeps that.
            SetBrush("SelectedTextBrush", dark ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1A, 0x24, 0x33));
            SetBrush("ColdBrush", dark ? ColdDark : ColdLight);
            SetBrush("ColdDeepBrush", dark ? ColdDeepDark : ColdDeepLight);
            SetBrush("EmberHotBrush", dark ? EmberHotDark : EmberHotLight);
            // Text-safe pairs (round-3 A4). EmberTextBrush: raw ember reads
            // ~2.6:1 on white, so light deepens to the hot ramp. ColdMuted:
            // the dark side bakes the 62% dim into the color's alpha so the
            // light side can carry deepened cold at high alpha and hold 4:1.
            SetBrush("EmberTextBrush", dark ? Accent : EmberHotLight);
            SetBrush("ColdMutedBrush", dark
                ? Color.FromArgb(0x9E, ColdDark.R, ColdDark.G, ColdDark.B)
                : Color.FromArgb(0xCC, ColdLight.R, ColdLight.G, ColdLight.B));

            // Steel ground: on dark, the WPF-UI translucent-gray surface
            // tokens swap to the steel palette, so every page sits on the
            // one ground with its card fill and card edge (the PadForge 6
            // values, #574). The page is a step over the sidebar, and a
            // card's edge is lit at its top. On light the overrides are
            // removed so lookup falls back to the stock theme.
            if (dark)
            {
                SetBrush("ApplicationBackgroundBrush", Color.FromRgb(0x0B, 0x10, 0x18));
                SetBrush("SolidBackgroundFillColorBaseBrush", Color.FromRgb(0x0B, 0x10, 0x18));
                SetBrush("NavigationViewContentBackground", Color.FromRgb(0x0B, 0x10, 0x18));
                SetBrush("NavigationViewContentGridBorderBrush", Color.FromRgb(0x16, 0x1E, 0x2B));
                SetBrush("CardBackgroundFillColorDefaultBrush", Color.FromRgb(0x12, 0x19, 0x25));
                SetBrush("CardBackgroundFillColorSecondaryBrush", Color.FromRgb(0x18, 0x20, 0x2E));
                SetCardEdge();
                SetBrush("ControlFillColorDefaultBrush", Color.FromRgb(0x18, 0x20, 0x2E));
                SetBrush("ControlStrokeColorDefaultBrush", Color.FromRgb(0x2A, 0x35, 0x46));
                // Slider recolor (#175 item 10): Wpf.Ui's Slider template
                // pulls its colors from these DynamicResource keys, not from
                // TemplateBindings, so a derived Style cannot recolor it.
                // Rail goes raised steel, thumb dot goes ember. There is no
                // decrease-side value-fill element in the Wpf.Ui 4.3.0
                // template (and no SliderTrackValueFill key), so the ember
                // value fill is intentionally not attempted here.
                SetBrush("SliderTrackFill", Color.FromRgb(0x2A, 0x35, 0x46));
                SetBrush("SliderTrackFillPointerOver", Color.FromRgb(0x2A, 0x35, 0x46));
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
        }

        // Chrome unification (#175 control spec §§1-8, route a): every stock
        // control key that StaticResource-captures a translucent ControlFill
        // color at theme load gets a steel-palette brush pinned over it.
        // Buttons are raised steel with one flat edge, inputs are darker
        // fields, and flyout surfaces sit a step above the page. Every key
        // pinned here is listed in EmberChromeKeys so light theme falls
        // back to stock Fluent.
        private static void ApplyDarkChrome()
        {
            // §1 Button: steel raise.
            SetBrush("ButtonBackground", Color.FromRgb(0x18, 0x20, 0x2E));
            SetBrush("ButtonBackgroundPointerOver", Color.FromRgb(0x21, 0x2C, 0x3D));
            SetBrush("ButtonBackgroundPressed", Color.FromRgb(0x14, 0x1B, 0x27));
            SetBrush("ButtonBackgroundDisabled", Color.FromRgb(0x11, 0x17, 0x22));
            SetBrush("ButtonBorderBrushPressed", Color.FromRgb(0x2A, 0x35, 0x46));
            SetBrush("ButtonBorderBrushDisabled", Color.FromRgb(0x1E, 0x27, 0x34));
            SetBrush("ButtonForegroundPressed", Color.FromRgb(0xA9, 0xB4, 0xC4));

            // §1 A control's edge: one flat line all the way around (#574).
            // The stock dark key is a gradient that lights the top edge.
            SetBrush("ControlElevationBorderBrush", Color.FromRgb(0x2A, 0x35, 0x46));

            // §2 Primary button: filled ember (#574). The theme original
            // binds AccentFillColorDefault, so this override must be a
            // brush of its own. Hover/pressed keep the stock brushes,
            // which flow live from the AccentFillColor* pins above.
            SetBrush("AccentButtonBackground", Accent);

            // §2 Primary text: the theme originals StaticResource-capture
            // black TextOnAccentFillColorPrimary at load, so Color pins
            // cannot reach them.
            SetBrush("AccentButtonForeground", Color.FromRgb(0x16, 0x0A, 0x03));
            SetBrush("AccentButtonForegroundPointerOver", Color.FromRgb(0x16, 0x0A, 0x03));
            SetBrush("AccentButtonForegroundPressed", Color.FromRgb(0x16, 0x0A, 0x03));

            // §4 ComboBox closed control.
            SetBrush("ComboBoxBackground", Color.FromRgb(0x18, 0x20, 0x2E));
            SetBrush("ComboBoxBackgroundPointerOver", Color.FromRgb(0x21, 0x2C, 0x3D));
            SetBrush("ComboBoxBackgroundDisabled", Color.FromRgb(0x11, 0x17, 0x22));
            SetBrush("ComboBoxBorderBrushDisabled", Color.FromRgb(0x1E, 0x27, 0x34));
            SetBrush("ComboBoxDropDownGlyphForeground", Color.FromRgb(0xA9, 0xB4, 0xC4));

            // §4 dropdown popup: steel-850 flyout ground with steel stroke,
            // replacing the stock acrylic. Item hover/highlight goes raised
            // steel (the stock key is 6%-white, invisible on this ground), item
            // text joins the steel text ramp. The ember selection pill needs
            // no key here: ComboBoxItemPillFillBrush binds
            // SystemAccentColorPrimary via DynamicResource, so the §2 pin
            // retargets it live.
            SetBrush("ComboBoxDropDownBackground", Color.FromRgb(0x14, 0x1B, 0x28));
            SetBrush("ComboBoxDropDownBorderBrush", Color.FromRgb(0x2A, 0x35, 0x46));
            SetBrush("ComboBoxItemBackgroundSelected", Color.FromRgb(0x21, 0x2C, 0x3D));
            SetBrush("ComboBoxForeground", Color.FromRgb(0xEE, 0xF2, 0xF7));
            SetBrush("ComboBoxItemForegroundSelected", Color.FromRgb(0xEE, 0xF2, 0xF7));
            SetBrush("ComboBoxForegroundDisabled", Color.FromRgb(0x56, 0x62, 0x78));

            // §4 sibling flyout surfaces, same ground (iteration-41 raised
            // steel). AcrylicBackgroundFillColorDefault itself stays stock:
            // it also feeds Snackbar/ContentDialog surfaces not audited.
            SetBrush("ContextMenuBackground", Color.FromRgb(0x14, 0x1B, 0x28));
            SetBrush("FlyoutBackground", Color.FromRgb(0x14, 0x1B, 0x28));

            // §5 TextBox/NumberBox: recessed wells, uniform inset hairline
            // (a well gets no top-light, that is for raises).
            SetBrush("TextControlBackground", Color.FromRgb(0x0D, 0x13, 0x1D));
            SetBrush("TextControlBackgroundPointerOver", Color.FromRgb(0x10, 0x17, 0x24));
            SetBrush("TextControlBackgroundFocused", Color.FromRgb(0x0D, 0x13, 0x1D));
            SetBrush("TextControlBackgroundDisabled", Color.FromRgb(0x10, 0x15, 0x1F));
            SetBrush("TextControlBorderBrushDisabled", Color.FromRgb(0x1E, 0x27, 0x34));
            SetBrush("TextControlPlaceholderForeground", Color.FromRgb(0x86, 0x93, 0xA6));
            SetBrush("TextControlButtonForeground", Color.FromRgb(0xA9, 0xB4, 0xC4));
            SetBrush("TextControlElevationBorderBrush", Color.FromRgb(0x2A, 0x35, 0x46));

            // §6 CheckBox rest: well fill + visible steel stroke.
            SetBrush("CheckBoxBackground", Color.FromRgb(0x0D, 0x13, 0x1D));
            SetBrush("CheckBoxBorderBrush", Color.FromRgb(0x6B, 0x78, 0x90));

            // §7 ScrollBar: solid steel thumb, steel track.
            SetBrush("ScrollBarThumbFill", Color.FromRgb(0x3A, 0x47, 0x60));
            SetBrush("ScrollBarTrackFillPointerOver", Color.FromRgb(0x12, 0x19, 0x25));
            SetBrush("ScrollBarButtonArrowForeground", Color.FromRgb(0xA9, 0xB4, 0xC4));

            // §8 ToolTip: flyout ground.
            // Dropdown stragglers (#175): some popup surfaces resolve
            // through the acrylic/focused keys instead of the plain ones.
            SetBrush("AcrylicBackgroundFillColorDefaultBrush", Color.FromRgb(0x14, 0x1B, 0x28));
            SetBrush("SystemFillColorSolidNeutralBackgroundBrush", Color.FromRgb(0x14, 0x1B, 0x28));
            SetBrush("ComboBoxBackgroundFocused", Color.FromRgb(0x18, 0x20, 0x2E));
            SetBrush("ToolTipBackground", Color.FromRgb(0x18, 0x20, 0x2E));
            SetBrush("ToolTipBorderBrush", Color.FromRgb(0x2A, 0x35, 0x46));
            SetBrush("ToolTipForeground", Color.FromRgb(0xEE, 0xF2, 0xF7));
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

        /// <summary>A card's edge on the dark ground (#574): one hairline
        /// with a lit top, as light from above catches an edge. The lit part
        /// is the top three pixels whatever the card's height, so the brush
        /// is mapped in pixels and holds its last color below them.</summary>
        internal static readonly Color CardEdgeTop = Color.FromRgb(0x2C, 0x37, 0x4A);
        internal static readonly Color CardEdge = Color.FromRgb(0x1E, 0x27, 0x34);

        private static void SetCardEdge()
        {
            var edge = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 3),
            };
            edge.GradientStops.Add(new GradientStop(CardEdgeTop, 0));
            edge.GradientStops.Add(new GradientStop(CardEdge, 1));
            edge.Freeze();
            Application.Current.Resources["CardStrokeColorDefaultBrush"] = edge;
        }

        /// <summary>The warm light in the title bar while the engine runs
        /// (#574): the lower rim of a wide ember glow whose middle is above
        /// the bar, toward its trailing end.</summary>
        private static void SetTitleWarmth(byte alpha)
        {
            var warmth = new RadialGradientBrush
            {
                Center = new Point(0.8, -1.5),
                GradientOrigin = new Point(0.8, -1.5),
                RadiusX = 0.6,
                RadiusY = 3.2,
            };
            warmth.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, Accent.R, Accent.G, Accent.B), 0));
            warmth.GradientStops.Add(new GradientStop(Color.FromArgb(0, Accent.R, Accent.G, Accent.B), 0.64));
            warmth.Freeze();
            Application.Current.Resources["TitleWarmthBrush"] = warmth;
        }
    }
}
