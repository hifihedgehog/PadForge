using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class KBMPreviewView : UserControl, IPictureWithPick
    {
        public event EventHandler<string> ControllerElementRecordRequested;

        /// <summary>Raised when the keyboard and the mouse were drawn again,
        /// or one of them came or went (#574).</summary>
        public event EventHandler PickBoundsChanged;

        private PadViewModel _vm;
        private bool _dirty;

        /// <summary>Last state actually painted, so the preview can repaint on
        /// its OWN data changing.
        ///
        /// <para>PadViewModel.KbmOutputSnapshot is a plain auto-property that
        /// InputService assigns every poll. Assigning it raises nothing, so
        /// _dirty never went true for it and this view only ever repainted
        /// when some UNRELATED view-model property happened to change. A
        /// mapped button engaging is precisely the case where nothing else
        /// changes, which is why the preview sat dead while the mapping
        /// worked.</para></summary>
        private KbmRawState _painted;
        private bool _paintedValid;
        private bool _layoutBuilt;

        private readonly List<KbmKeyWidget> _keyWidgets = new();

        // Mouse elements
        private Shape _lmbPath;
        private Shape _rmbPath;
        private Shape _scrollWheelPill;
        private Polygon _scrollUpArrow;
        private Polygon _scrollDownArrow;
        private Ellipse _movementDot;
        private Ellipse _moveCircle;
        private Polygon _moveArrow;
        private Shape _x1Rect;
        private Shape _x2Rect;
        private Canvas _moveArrowCanvas;
        // Tilt arrows, drawn while the mouse half can pan (null otherwise).
        private Polygon _scrollLeftArrow;
        private Polygon _scrollRightArrow;

        // The virtual keyboard's lock lights (Num, Caps, Scroll), drawn while
        // the picked keyboard has an LED report (null otherwise). Each light
        // is also its lock key's control: a click records that key's row.
        private Ellipse[] _ledDots;
        private string[] _ledTargets;
        private int _paintedLeds = int.MinValue;

        /// <summary>The key each lock light belongs to, in HMKeyboardLeds bit
        /// order: Num Lock, Caps Lock, Scroll Lock.</summary>
        internal static readonly int[] LockLightKeys = { 0x90, 0x14, 0x91 };

        // Colors — pre-cached dark/light variants (zero per-frame allocation)
        private static bool IsDarkTheme =>
            Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;

        private static SolidColorBrush F(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }
        private static SolidColorBrush FA(byte a, byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br; }

        private static readonly Brush _dimD = F(0x40,0x40,0x40), _dimL = F(0xB0,0xB0,0xB0);
        private static readonly Brush _btnD = F(0x60,0x60,0x60), _btnL = F(0xD0,0xD0,0xD0);
        private static readonly Brush _swD = F(0x38,0x38,0x38), _swL = F(0xA8,0xA8,0xA8);
        private static readonly Brush _dotD = F(0x88,0x88,0x88), _dotL = F(0x70,0x70,0x70);
        private static readonly Brush _knD = FA(0x28,0x88,0x88,0x88), _knL = FA(0x30,0x40,0x40,0x40);

        private static Brush DimBrush => IsDarkTheme ? _dimD : _dimL;
        private static Brush MouseButtonBrush => IsDarkTheme ? _btnD : _btnL;
        private static Brush ScrollWheelBrush => IsDarkTheme ? _swD : _swL;
        // Ember (#175): this preview shows what the virtual keyboard and
        // mouse emit, so pressed states light ember, not the old blue.
        private static readonly Brush AccentBrush = F(0xFF,0x6B,0x2C);
        private static Brush DotBrush => IsDarkTheme ? _dotD : _dotL;
        internal static Brush KeyNormalBrush => IsDarkTheme ? _knD : _knL;
        private static readonly Brush KeyPressedBrush = F(0xFF,0x6B,0x2C);
        private static readonly Brush HoverBrush = F(0xFF,0xA2,0x4D);
        private static readonly Brush FlashBrush = F(0xFF,0xA5,0x00);

        // Ember bloom (#175 glow sweep): pressed visuals carry a static
        // DropShadowEffect, attached alongside the brush swap and detached
        // when unlit. Frozen and shared, never animated. Small variant for
        // glyphs 14px and under (movement dot, scroll arrows, side buttons).
        private static readonly System.Windows.Media.Effects.DropShadowEffect EmberGlow = MakeEmberGlow(12);
        private static readonly System.Windows.Media.Effects.DropShadowEffect EmberGlowSmall = MakeEmberGlow(8);

        private static System.Windows.Media.Effects.DropShadowEffect MakeEmberGlow(double blur)
        {
            var fx = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0x6B, 0x2C),
                BlurRadius = blur,
                ShadowDepth = 0,
                Opacity = 0.5
            };
            fx.Freeze();
            return fx;
        }

        private static void SetGlow(UIElement element, System.Windows.Media.Effects.DropShadowEffect glow)
        {
            if (!ReferenceEquals(element.Effect, glow))
                element.Effect = glow;
        }

        // Layout constants
        private const double MC = MouseGlyph.CenterX;       // mouse center X
        private const double MoveSize = MouseGlyph.MoveSize;

        // Button area (used by both build and render)
        private const double MoveTop = MouseGlyph.MoveTop;

        private System.Windows.Threading.DispatcherTimer _flashTimer;
        private string _flashTarget;
        private MouseGlyph.Parts _parts;
        private bool _flashOn;
        private Wpf.Ui.Appearance.ApplicationTheme? _lastTheme;

        public KBMPreviewView()
        {
            InitializeComponent();
            // Rendering rides tree presence, matching MousePreviewControl. A
            // ctor-lifetime subscription to the STATIC CompositionTarget.Rendering
            // roots the view forever and keeps its per-frame callback
            // invalidating layout even when the hosting page is swapped out.
            // See the note in ControllerSchematicView for the measurement.
            Loaded += (s, e) =>
            {
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
                InputLanguageManager.Current.InputLanguageChanged -= OnInputLanguageChanged;
                InputLanguageManager.Current.InputLanguageChanged += OnInputLanguageChanged;
            };
            Unloaded += (s, e) =>
            {
                CompositionTarget.Rendering -= OnRendering;
                InputLanguageManager.Current.InputLanguageChanged -= OnInputLanguageChanged;
            };
            // The drawings carry their tooltips and the approximate caption as
            // text, so a language switch redraws them. Held weakly by Strings.
            Strings.CultureChanged += OnCultureChanged;
        }

        /// <summary>Redraws once every handler of the switch has run, so the
        /// mapping rows the tooltips name are already in the new
        /// language.</summary>
        private void OnCultureChanged()
            => Dispatcher.BeginInvoke(new Action(() => { if (_layoutBuilt) RebuildLayout(); }));

        /// <summary>A device keyboard's legends and targets follow the input
        /// layout, so a layout switch redraws it.</summary>
        private void OnInputLanguageChanged(object sender, InputLanguageEventArgs e)
        {
            if (_layoutBuilt && KeyboardFamilies.For(_vm?.KbmKeyboardDevice) != null) RebuildLayout();
        }

        public void Bind(PadViewModel vm)
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = vm;
            if (_vm != null)
            {
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
                _vm.PropertyChanged += OnVmPropertyChanged;
                RebuildLayout();
            }
        }

        public void Unbind()
        {
            CompositionTarget.Rendering -= OnRendering;
            // Stop the recording flash FIRST. UpdateFlashTarget(null) is the
            // only path that stops _flashTimer and clears _flashTarget, and
            // it is reached only through the subscription torn down below, so
            // unbinding mid-recording used to strand an armed timer that kept
            // a control blinking and suppressed hover across the preview.
            UpdateFlashTarget(null);
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
            _layoutBuilt = false;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.OutputType)) { Dispatcher.Invoke(RebuildLayout); return; }
            // A picked keyboard or mouse redraws as that device.
            if (e.PropertyName is nameof(PadViewModel.KbmKeyboardDevice) or nameof(PadViewModel.KbmMouseDevice))
            {
                Dispatcher.Invoke(RebuildLayout);
                return;
            }
            if (e.PropertyName == nameof(PadViewModel.KbmSurfaces))
            {
                // Marked dirty like both siblings: a half coming back into
                // view has widgets painted from a stale snapshot behind it.
                Dispatcher.Invoke(() => { ApplySurfaceVisibility(); _paintedValid = false; _dirty = true; });
                return;
            }
            if (e.PropertyName == nameof(PadViewModel.CurrentRecordingTarget)) { Dispatcher.Invoke(() => UpdateFlashTarget(_vm?.CurrentRecordingTarget)); return; }
            _dirty = true;
        }

        private void RebuildLayout()
        {
            _layoutBuilt = false;
            _keyWidgets.Clear();
            _ledDots = null;
            _ledTargets = null;
            _paintedLeds = int.MinValue;
            _scrollLeftArrow = _scrollRightArrow = null;
            _lastTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
            if (_vm == null || _vm.OutputType != VirtualControllerType.KeyboardMouse) return;
            BuildKeyboardCanvas();
            BuildMouseCanvas();
            ApplySurfaceVisibility();
            _layoutBuilt = true;
            _dirty = true;
            PickBoundsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Where a target's control is drawn (#574): the key, the
        /// mouse button, the wheel for its click and for scrolling, or the
        /// movement circle for the pointer's four directions. Only the
        /// movement circle is round. A control on a half the slot does not
        /// drive, or one the slot's mouse lacks, is not drawn.</summary>
        public bool TryGetPickBounds(string target, out Rect bounds, out bool round)
        {
            bounds = default;
            round = false;
            if (!_layoutBuilt || string.IsNullOrEmpty(target)) return false;
            FrameworkElement part = null;
            foreach (var w in _keyWidgets)
                if (w.TargetName == target) { part = w.Border; break; }
            if (part == null && _parts != null)
            {
                part = target switch
                {
                    "KbmMBtn0" => _parts.LmbHit,
                    "KbmMBtn1" => _parts.RmbHit,
                    "KbmMBtn2" => _parts.WheelHit,
                    "KbmMBtn3" => _parts.X1Hit,
                    "KbmMBtn4" => _parts.X2Hit,
                    "KbmScroll" or "KbmScrollNeg" or "KbmScrollH" or "KbmScrollHNeg" => _parts.WheelHit,
                    _ => target.StartsWith("KbmMouse", StringComparison.Ordinal) ? _moveCircle : null,
                };
                round = ReferenceEquals(part, _moveCircle) && part != null;
            }
            return part != null && PicturePick.Shows(part, this)
                && PicturePick.TryMap(part, this, PicturePick.OwnRect(part), out bounds);
        }

        /// <summary>Shows only the halves the slot actually drives (#408).
        /// A slot set to Mouse Only sends no keystrokes, so drawing a
        /// keyboard for it misrepresents what the pad does.</summary>
        private void ApplySurfaceVisibility()
            => ApplySurfaceVisibility(
                _vm?.KbmConfig?.KeyboardEnabled ?? true,
                _vm?.KbmConfig?.MouseEnabled ?? true);

        /// <summary>The element half, taking the two flags directly. Internal
        /// so a test can drive it without Bind, which builds both canvases and
        /// needs application resources a bare test host does not load.
        ///
        /// <para>The row height goes with the visibility. Collapsing the
        /// Viewbox alone leaves its star row holding the space, so Mouse Only
        /// would draw the mouse in the bottom two fifths under an empty
        /// gap.</para></summary>
        internal void ApplySurfaceVisibility(bool keyboard, bool mouse)
        {
            // A flash on a half about to be collapsed would blink a widget
            // nobody can see, and it would hold the hover gate shut over the
            // half that stays visible.
            var flashing = PadViewModel.KbmSurfaceOf(_flashTarget);
            if (flashing == KbmSurfaceKind.Keyboard && !keyboard) UpdateFlashTarget(null);
            else if (flashing == KbmSurfaceKind.Mouse && !mouse) UpdateFlashTarget(null);

            KeyboardHost.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
            MouseHost.Visibility = mouse ? Visibility.Visible : Visibility.Collapsed;

            KeyboardRow.Height = keyboard
                ? new GridLength(mouse ? 3 : 1, GridUnitType.Star)
                : new GridLength(0);
            MouseRow.Height = mouse
                ? new GridLength(keyboard ? 2 : 1, GridUnitType.Star)
                : new GridLength(0);
            PickBoundsChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────────────────────
        //  Keyboard
        // ─────────────────────────────────────────────

        /// <summary>The generic keyboard canvas size, which the XAML
        /// declares.</summary>
        private const double GenericKeyboardWidth = 556, GenericKeyboardHeight = 136;

        /// <summary>Pixels per key unit in a device drawing, the generic
        /// layout's key pitch.</summary>
        private const double KeyUnit = 24, KeyGap = 2;

        /// <summary>The keyboard half: the picked keyboard as its own board
        /// (HIDMaestro 1.12.0, KeyboardFamilies), or the generic keyboard for
        /// Windows input and for a keyboard profile no drawing covers.</summary>
        private void BuildKeyboardCanvas()
        {
            KeyboardCanvas.Children.Clear();
            string deviceId = _vm?.KbmKeyboardDevice;
            var caps = KbmDeviceCatalog.CapsOf(deviceId);
            var family = KeyboardFamilies.For(deviceId);
            if (family != null) BuildDeviceKeyboard(family, caps);
            else BuildGenericKeyboard(caps);
        }

        private void BuildGenericKeyboard(KbmDeviceCaps caps)
        {
            KeyboardCanvas.Width = GenericKeyboardWidth;
            KeyboardCanvas.Height = GenericKeyboardHeight;
            foreach (var key in KeyboardKeyItem.BuildLayout())
            {
                bool sendable = caps == null || DeviceSends(caps, key.VKeyIndex);
                AddKey(key.VKeyIndex, key.Label, null, key.X, key.Y, key.KeyWidth, key.KeyHeight, sendable);
            }

            // Four media keys over the number pad, where 108-key boards have
            // them. The grid carries the rest of the media and browser keys.
            const double u = 24, kh = 20;
            int[] media = { 0xAD, 0xAE, 0xAF, 0xB3 };
            for (int i = 0; i < media.Length; i++)
            {
                bool sendable = caps == null || DeviceSends(caps, media[i]);
                AddKey(media[i], MediaGlyph(KbmHidKeyMap.Nominal(media[i]).Usage), SymbolFont,
                    (19 + i) * u, 0, u - KeyGap, kh, sendable);
            }
        }

        /// <summary>Whether the picked keyboard sends the key at index
        /// <paramref name="vk"/>.</summary>
        private static bool DeviceSends(KbmDeviceCaps caps, int vk)
        {
            var key = KbmHidKeyMap.Nominal(vk);
            return key.IsKeyboard ? key.Usage <= 0xFF && caps.CanSendKey((byte)key.Usage)
                : key.IsConsumer && caps.CanSendConsumer(key.Usage);
        }

        /// <summary>A keyboard as its board: QMK's key positions, each key
        /// standing for the row the input layout gives it, with the legend
        /// that layout types, and its lock lights when it has them.</summary>
        private void BuildDeviceKeyboard(KeyboardFamilies.Family family, KbmDeviceCaps caps)
        {
            IntPtr layout = KbmHidKeyMap.UiLayout();
            bool leds = caps?.HasKeyboardLeds == true;
            double top = leds ? KeyUnit * 0.6 : 0;
            double width = 0, height = 0;
            foreach (var k in family.Keys)
            {
                width = Math.Max(width, k.X + k.W);
                height = Math.Max(height, k.Y + k.H);
            }
            KeyboardCanvas.Width = width * KeyUnit;
            KeyboardCanvas.Height = top + height * KeyUnit;

            foreach (var k in family.Keys)
            {
                var usage = new KbmHidKeyMap.HidKey(k.Page, k.Id);
                int vk = k.Usage == 0 ? -1 : KbmHidKeyMap.TargetOf(usage, layout);
                bool sendable = k.Usage != 0 && (caps == null
                    || (usage.IsKeyboard ? k.Id <= 0xFF && caps.CanSendKey((byte)k.Id)
                        : usage.IsConsumer && caps.CanSendConsumer(k.Id)));
                string legend = KeyboardFamilies.FixedLegend(k, family.Legends);
                FontFamily font = null;
                if (legend == null && usage.IsConsumer)
                {
                    legend = MediaGlyph(k.Id);
                    font = SymbolFont;
                }
                if (legend == null && usage.IsKeyboard && KbmHidKeyMap.IsCharacterUsage(k.Id))
                    legend = KbmHidKeyMap.CharacterLegend(vk, layout);
                legend ??= ShortLegend(vk);
                AddKey(vk, legend, font, k.X * KeyUnit, top + k.Y * KeyUnit,
                    k.W * KeyUnit - KeyGap, k.H * KeyUnit - KeyGap, sendable);
            }

            if (leds) BuildLedStrip(width * KeyUnit, caps);

            // A board drawn as the nearest standard set says so under it.
            if (family.Approximate)
            {
                var note = new TextBlock
                {
                    Text = Strings.Instance.Pad_Kbm_Preview_Approximate,
                    FontSize = 8, Opacity = 0.6, IsHitTestVisible = false,
                };
                Canvas.SetLeft(note, 0);
                Canvas.SetTop(note, KeyboardCanvas.Height + 2);
                KeyboardCanvas.Children.Add(note);
                KeyboardCanvas.Height += 14;
            }
        }

        /// <summary>One key. A key the device cannot send, or one no row
        /// reaches, is drawn faint and takes no clicks. Whether a row reaches
        /// it is the engine's key list, not the mapping rows, which a slot
        /// can still be building when the preview draws.</summary>
        private void AddKey(int vk, string legend, FontFamily font, double x, double y, double w, double h, bool sendable)
        {
            string targetName = vk >= 0 ? $"KbmKey{vk:X2}" : null;
            string rowLabel = targetName == null ? null
                : _vm?.Mappings?.FirstOrDefault(m => m.TargetSettingName == targetName)?.TargetLabel;
            bool live = sendable && PadForge.Common.Input.InputManager.IsKbmKeyTarget(vk);
            var border = new Border
            {
                Width = w, Height = h,
                CornerRadius = new CornerRadius(3),
                Background = KeyNormalBrush,
                Cursor = live ? Cursors.Hand : null,
                ToolTip = rowLabel ?? (string.IsNullOrEmpty(legend) ? null : legend),
                Opacity = live ? 1.0 : 0.35,
            };
            border.Child = new TextBlock
            {
                Text = legend ?? string.Empty, FontSize = 8,
                FontFamily = font ?? (FontFamily)FindResource("TelemetryFontFamily"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.8, IsHitTestVisible = false
            };
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            KeyboardCanvas.Children.Add(border);
            if (!live) return;

            border.MouseEnter += (s, e) => { if (_flashTarget == null) { border.BorderBrush = HoverBrush; border.BorderThickness = new Thickness(1.5); } };
            border.MouseLeave += (s, e) => { if (_flashTarget == null) { border.BorderBrush = null; border.BorderThickness = new Thickness(0); } };
            border.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, targetName); e.Handled = true; };
            _keyWidgets.Add(new KbmKeyWidget { VKeyIndex = vk, Border = border, TargetName = targetName });
        }

        /// <summary>The lock lights in the board's top right corner, lit from
        /// the LED report Windows last sent the virtual keyboard. Each light
        /// is a control as well as a display: hover names its key's row, a
        /// click records that row, and the light flashes with the key while
        /// the row records. A light whose key no row reaches, or that the
        /// keyboard cannot send, only shows.</summary>
        private void BuildLedStrip(double right, KbmDeviceCaps caps)
        {
            var s = Strings.Instance;
            string[] names = { s.Key_NumLock, s.Key_CapsLock, s.Key_ScrollLock };
            string[] marks = { "1", "A", "⇩" };
            _ledDots = new Ellipse[3];
            _ledTargets = new string[3];
            for (int i = 0; i < 3; i++)
            {
                double x = right - (3 - i) * 22;
                var dot = new Ellipse { Width = 6, Height = 6, Fill = DimBrush, ToolTip = names[i] };
                Canvas.SetLeft(dot, x);
                Canvas.SetTop(dot, 4);
                KeyboardCanvas.Children.Add(dot);
                var mark = new TextBlock
                {
                    Text = marks[i], FontSize = 7, Opacity = 0.7, IsHitTestVisible = false,
                    FontFamily = (FontFamily)FindResource("TelemetryFontFamily"),
                };
                Canvas.SetLeft(mark, x + 8);
                Canvas.SetTop(mark, 2);
                KeyboardCanvas.Children.Add(mark);
                _ledDots[i] = dot;

                int vk = LockLightKeys[i];
                string target = $"KbmKey{vk:X2}";
                _ledTargets[i] = target;
                bool live = (caps == null || DeviceSends(caps, vk))
                    && PadForge.Common.Input.InputManager.IsKbmKeyTarget(vk);
                if (!live) continue;

                // The dot is 6 px, too small to aim at, so the light and its
                // mark share one hit area, the way the mouse controls ride a
                // hit shape of their own.
                string rowLabel = _vm?.Mappings?.FirstOrDefault(m => m.TargetSettingName == target)?.TargetLabel;
                var hit = new Rectangle
                {
                    Width = 20, Height = 12, Fill = Brushes.Transparent,
                    Cursor = Cursors.Hand, ToolTip = rowLabel ?? names[i],
                };
                Canvas.SetLeft(hit, x - 3);
                Canvas.SetTop(hit, 1);
                hit.MouseEnter += (s2, e) =>
                {
                    if (_flashTarget != null) return;
                    dot.Stroke = HoverBrush;
                    dot.StrokeThickness = 1.5;
                };
                hit.MouseLeave += (s2, e) => dot.Stroke = null;
                hit.MouseLeftButtonDown += (s2, e) =>
                {
                    ControllerElementRecordRequested?.Invoke(this, target);
                    e.Handled = true;
                };
                KeyboardCanvas.Children.Add(hit);
            }
        }

        private static readonly FontFamily SymbolFont = new("Segoe MDL2 Assets");

        /// <summary>A Segoe MDL2 Assets glyph for a consumer usage the
        /// key map sends, or null.</summary>
        internal static string MediaGlyph(ushort usage) => usage switch
        {
            0xE2 => "",   // Mute
            0xEA => "",   // Volume Down
            0xE9 => "",   // Volume Up
            0xCD => "",   // Play/Pause
            0xB7 => "",   // Stop
            0xB6 => "",   // Previous Track
            0xB5 => "",   // Next Track
            0x192 => "",  // Calculator
            0x18A => "",  // Mail
            0x194 => "",  // File Browser
            0x183 => "",  // Media Player
            0x221 => "",  // Browser Search
            0x223 => "",  // Browser Home
            0x224 => "",  // Browser Back
            0x225 => "",  // Browser Forward
            0x226 => "",  // Browser Stop
            0x227 => "",  // Browser Refresh
            0x22A => "",  // Browser Favorites
            _ => null,
        };

        /// <summary>The generic keyboard's short legend for a key whose
        /// legend no layout writes.</summary>
        internal static string ShortLegend(int vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (vk >= 0x60 && vk <= 0x69) return (vk - 0x60).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return vk switch
            {
                0x1B => "Esc", 0x2C => "PrSc", 0x91 => "ScLk", 0x13 => "Pse",
                0x08 => "←", 0x09 => "Tab", 0x14 => "Caps", 0x0D => "↵", 0x88 => "↵",
                0xA0 or 0xA1 => "Shift", 0xA2 or 0xA3 => "Ctrl", 0xA4 or 0xA5 => "Alt",
                0x5B or 0x5C => "Win", 0x5D => "Menu", 0x20 => "",
                0x2D => "Ins", 0x24 => "Hm", 0x21 => "PU", 0x2E => "Del", 0x23 => "End", 0x22 => "PD",
                0x26 => "▲", 0x25 => "◄", 0x28 => "▼", 0x27 => "►",
                0x90 => "Num", 0x6F => "/", 0x6A => "*", 0x6D => "-", 0x6B => "+", 0x6E => ".",
                0xF3 => "半/全",
                _ => "",
            };
        }

        // ─────────────────────────────────────────────
        //  Mouse — buttons contour around scroll wheel
        // ─────────────────────────────────────────────

        private void BuildMouseCanvas()
        {
            MouseCanvas.Children.Clear();

            var parts = MouseGlyph.Build(MouseCanvas, IsDarkTheme, DimBrush,
                MouseButtonBrush, ScrollWheelBrush, DotBrush);
            _lmbPath = parts.Lmb; _rmbPath = parts.Rmb;
            _x1Rect = parts.X1;   _x2Rect = parts.X2;
            _scrollWheelPill = parts.Wheel;
            _scrollUpArrow = parts.ScrollUp; _scrollDownArrow = parts.ScrollDown;
            _moveCircle = parts.MoveCircle;  _movementDot = parts.MoveDot;
            _parts = parts;
            _moveCircle.Cursor = Cursors.Hand;
            _moveCircle.ToolTip = Strings.Instance.Pad_MouseMovement;

            // Direction arrow (hidden until hover/flash)
            double arrowLen = MoveSize * 0.35, arrowBase = 6;
            _moveArrow = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(MoveSize / 2, MoveSize / 2 - arrowLen),
                    new Point(MoveSize / 2 - arrowBase, MoveSize / 2 - arrowLen * 0.5),
                    new Point(MoveSize / 2 + arrowBase, MoveSize / 2 - arrowLen * 0.5)
                },
                Fill = HoverBrush, IsHitTestVisible = false, Visibility = Visibility.Collapsed
            };
            _moveArrowCanvas = new Canvas { Width = MoveSize, Height = MoveSize, IsHitTestVisible = false };
            _moveArrowCanvas.Children.Add(_moveArrow);
            Canvas.SetLeft(_moveArrowCanvas, parts.MoveX);
            Canvas.SetTop(_moveArrowCanvas, MoveTop);
            MouseCanvas.Children.Add(_moveArrowCanvas);

            MouseGlyph.AddOutline(MouseCanvas, DimBrush);

            // Only the scroll arrows carry their own handlers (below); the
            // five masked art layers are IsHitTestVisible=false with
            // dedicated hit Paths, so a Cursor on them is inert (round 33).
            _scrollUpArrow.Cursor = Cursors.Hand;
            _scrollDownArrow.Cursor = Cursors.Hand;

            // Engine descriptors, not visual order: LMB is Btn0, RMB is
            // Btn1, the wheel is Btn2.
            AddMouseControlHandlers(_parts.LmbHit,   _parts.LmbHover,   "KbmMBtn0");
            AddMouseControlHandlers(_parts.RmbHit,   _parts.RmbHover,   "KbmMBtn1");
            AddMouseControlHandlers(_parts.WheelHit, _parts.WheelHover, "KbmMBtn2");
            AddMouseControlHandlers(_parts.X1Hit,    _parts.X1Hover,    "KbmMBtn3");
            AddMouseControlHandlers(_parts.X2Hit,    _parts.X2Hover,    "KbmMBtn4");

            _scrollUpArrow.ToolTip = MappingLabel("KbmScroll") + " " + Strings.Instance.Pad_ScrollUp;
            _scrollUpArrow.MouseEnter += (s, e) => { if (_flashTarget == null) _scrollUpArrow.Fill = HoverBrush; };
            _scrollUpArrow.MouseLeave += (s, e) => { if (_flashTarget == null) _scrollUpArrow.Fill = DimBrush; };
            _scrollUpArrow.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, "KbmScroll"); e.Handled = true; };

            _scrollDownArrow.ToolTip = MappingLabel("KbmScroll") + " " + Strings.Instance.Pad_ScrollDown;
            _scrollDownArrow.MouseEnter += (s, e) => { if (_flashTarget == null) _scrollDownArrow.Fill = HoverBrush; };
            _scrollDownArrow.MouseLeave += (s, e) => { if (_flashTarget == null) _scrollDownArrow.Fill = DimBrush; };
            _scrollDownArrow.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, "KbmScrollNeg"); e.Handled = true; };

            _moveCircle.MouseMove += (s, e) =>
            {
                if (_flashTarget != null) return;
                var pos = e.GetPosition(_moveCircle);
                double hx = pos.X - MoveSize / 2, hy = pos.Y - MoveSize / 2;
                double angle = Math.Abs(hx) >= Math.Abs(hy) ? (hx > 0 ? 90 : 270) : (hy > 0 ? 180 : 0);
                _moveArrow.Visibility = Visibility.Visible;
                _moveArrow.Fill = HoverBrush;
                _moveArrowCanvas.RenderTransform = new RotateTransform(angle, MoveSize / 2, MoveSize / 2);
                _moveCircle.Stroke = HoverBrush; _moveCircle.StrokeThickness = 2.5;
            };
            _moveCircle.MouseLeave += (s, e) =>
            {
                if (_flashTarget != null) return;
                _moveArrow.Visibility = Visibility.Collapsed;
                _moveCircle.Stroke = DimBrush; _moveCircle.StrokeThickness = 1.5;
            };
            _moveCircle.MouseLeftButtonDown += (s, e) =>
            {
                var pos = e.GetPosition(_moveCircle);
                double cx = pos.X - MoveSize / 2, cy = pos.Y - MoveSize / 2;
                string target = Math.Abs(cx) >= Math.Abs(cy)
                    ? (cx >= 0 ? "KbmMouseX" : "KbmMouseXNeg")
                    : (cy >= 0 ? "KbmMouseYNeg" : "KbmMouseY");
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };

            ApplyMouseCaps(KbmDeviceCatalog.CapsOf(_vm?.KbmMouseDevice));

            MouseCanvas.Height = MouseGlyph.BodyH + 6;
        }

        /// <summary>Draws the controls the slot's mouse has (HIDMaestro
        /// 1.12.0). Windows input sends every control, so it keeps them all.
        /// A virtual mouse loses the side buttons past its button count, a
        /// button it lacks goes faint and takes no clicks, the scroll arrows
        /// go with a missing wheel, and a mouse that tilts gets left and right
        /// arrows beside the wheel.</summary>
        private void ApplyMouseCaps(KbmDeviceCaps caps)
        {
            bool Has(int button) => caps == null || caps.CanSendMouseButton(button);
            bool wheel = caps == null || caps.HasWheel;
            bool pan = caps == null || caps.HasPan;

            void Side(Shape visual, Shape hover, Shape hit, bool present)
            {
                var v = present ? Visibility.Visible : Visibility.Collapsed;
                if (visual != null) visual.Visibility = v;
                if (hit != null) hit.Visibility = v;
                if (!present && hover != null) hover.Visibility = Visibility.Collapsed;
            }
            void Faint(Shape visual, Shape hit, bool present)
            {
                if (visual != null) visual.Opacity = present ? 1.0 : 0.3;
                if (hit != null) hit.IsHitTestVisible = present;
            }

            Side(_parts.X1, _parts.X1Hover, _parts.X1Hit, Has(3));
            Side(_parts.X2, _parts.X2Hover, _parts.X2Hit, Has(4));
            Faint(_parts.Lmb, _parts.LmbHit, Has(0));
            Faint(_parts.Rmb, _parts.RmbHit, Has(1));
            // The wheel art is also the middle button: it stays while either
            // is there, and takes clicks only while the middle button is.
            Faint(_parts.Wheel, _parts.WheelHit, Has(2));
            if (wheel && !Has(2)) _parts.Wheel.Opacity = 1.0;
            _scrollUpArrow.Visibility = wheel ? Visibility.Visible : Visibility.Collapsed;
            _scrollDownArrow.Visibility = wheel ? Visibility.Visible : Visibility.Collapsed;

            if (!pan) return;
            // On the wheel, between its up and down arrows, where those two
            // already read against the wheel's own dark fill.
            double cy = (MouseGlyph.WheelTop + MouseGlyph.WheelBottom) / 2;
            double left = MouseArt.WheelRect.X + 3, right = MouseArt.WheelRect.X + MouseArt.WheelRect.Width - 3;
            _scrollLeftArrow = new Polygon
            {
                Points = new PointCollection { new(left, cy), new(left + 7, cy - 4.5), new(left + 7, cy + 4.5) },
                Fill = DimBrush, Cursor = Cursors.Hand,
                ToolTip = MappingLabel("KbmScrollH") + " " + Strings.Instance.Pad_ScrollLeft,
            };
            _scrollRightArrow = new Polygon
            {
                Points = new PointCollection { new(right, cy), new(right - 7, cy - 4.5), new(right - 7, cy + 4.5) },
                Fill = DimBrush, Cursor = Cursors.Hand,
                ToolTip = MappingLabel("KbmScrollH") + " " + Strings.Instance.Pad_ScrollRight,
            };
            foreach (var (arrow, target) in new[] { (_scrollLeftArrow, "KbmScrollHNeg"), (_scrollRightArrow, "KbmScrollH") })
            {
                var a = arrow;
                a.MouseEnter += (s, e) => { if (_flashTarget == null) a.Fill = HoverBrush; };
                a.MouseLeave += (s, e) => { if (_flashTarget == null) a.Fill = DimBrush; };
                a.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, target); e.Handled = true; };
                MouseCanvas.Children.Add(a);
            }
        }

        // ─────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────

        /// <summary>Wires one mouse control for recording.
        ///
        /// <para>Input rides a dedicated HIT shape, not the visual: the visual
        /// is a full-canvas rectangle carrying an alpha mask, and WPF hit-tests
        /// such a rectangle over its whole rect rather than its mask, so every
        /// control would otherwise answer for the entire pad. Hover shows a
        /// separate wash layer rather than stroking anything, because the
        /// visual's Fill is owned by the render loop and the flash
        /// animation.</para></summary>
        private void AddMouseControlHandlers(Shape hit, Shape hover, string target)
        {
            if (hit == null) return;
            hit.Cursor = Cursors.Hand;
            hit.ToolTip = MappingLabel(target);
            hit.MouseEnter += (s, e) =>
            {
                if (_flashTarget == null && hover != null) hover.Visibility = Visibility.Visible;
            };
            hit.MouseLeave += (s, e) =>
            {
                if (hover != null) hover.Visibility = Visibility.Collapsed;
            };
            hit.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };
        }

        /// <summary>Only the fields this preview actually draws. Comparing
        /// the whole struct would repaint on values nothing here shows.
        ///
        /// <para>Change gate for the preview repaint. It has to name EVERY
        /// lane that can move the cursor, or motion arriving only on an
        /// unlisted lane repaints nothing and the preview reports a still
        /// cursor while the real one moves. Gyro and touchpad stopped writing
        /// MouseDeltaX/Y when they moved to their own exact-counts lanes, so
        /// listing only that pair left the preview blind to both; flick stick
        /// had been invisible here since it got its lane.</para></summary>
        internal static bool SamePreviewState(in KbmRawState a, in KbmRawState b)
            => a.Keys0 == b.Keys0 && a.Keys1 == b.Keys1
            && a.Keys2 == b.Keys2 && a.Keys3 == b.Keys3
            && a.MouseButtons == b.MouseButtons
            && a.ScrollDelta == b.ScrollDelta
            && a.ScrollDeltaH == b.ScrollDeltaH
            && a.MouseDeltaX == b.MouseDeltaX
            && a.MouseDeltaY == b.MouseDeltaY
            && a.MouseGyroX == b.MouseGyroX
            && a.MouseGyroY == b.MouseGyroY
            && a.MouseTouchX == b.MouseTouchX
            && a.MouseTouchY == b.MouseTouchY
            && a.MouseFlickX == b.MouseFlickX;

        private string MappingLabel(string targetSettingName)
            => _vm?.Mappings?.FirstOrDefault(m => m.TargetSettingName == targetSettingName)?.TargetLabel ?? targetSettingName;

        // ─────────────────────────────────────────────
        //  Flash animation
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            if (_flashTimer != null) { _flashTimer.Stop(); _flashTimer = null; }
            ApplyFlashState(false);
            // ApplyFlashState(false) repaints the widget to its NEUTRAL brush,
            // which is not necessarily its live value. Drop the change latch
            // so the next frame repaints from the snapshot; without this, an
            // idle pad leaves the control stuck neutral because nothing in
            // the snapshot changed.
            _paintedValid = false;
            _dirty = true;
            _flashTarget = target;
            if (string.IsNullOrEmpty(target)) return;
            _flashOn = true;
            _flashTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _flashTimer.Tick += (s, e) => { _flashOn = !_flashOn; ApplyFlashState(_flashOn); };
            _flashTimer.Start();
            ApplyFlashState(true);
        }

        private void ApplyFlashState(bool highlight)
        {
            if (string.IsNullOrEmpty(_flashTarget)) return;

            // A lock light records its key's row, so it flashes with the key.
            if (_ledTargets != null)
                for (int i = 0; i < _ledTargets.Length; i++)
                    if (_flashTarget == _ledTargets[i]) _ledDots[i].Fill = highlight ? FlashBrush : DimBrush;

            foreach (var w in _keyWidgets)
                if (_flashTarget == w.TargetName) { w.Border.Background = highlight ? FlashBrush : KeyNormalBrush; return; }

            if (_flashTarget == "KbmMBtn0") { _lmbPath.Fill = highlight ? FlashBrush : MouseButtonBrush; return; }
            if (_flashTarget == "KbmMBtn1") { _rmbPath.Fill = highlight ? FlashBrush : MouseButtonBrush; return; }
            if (_flashTarget == "KbmMBtn2") { _scrollWheelPill.Fill = highlight ? FlashBrush : ScrollWheelBrush; return; }
            // Fill, not Stroke: these are masked full-canvas rectangles now,
            // so stroking one draws a border around the whole pad.
            if (_flashTarget == "KbmMBtn3") { _x1Rect.Fill = highlight ? FlashBrush : MouseButtonBrush; return; }
            if (_flashTarget == "KbmMBtn4") { _x2Rect.Fill = highlight ? FlashBrush : MouseButtonBrush; return; }

            if (_flashTarget.StartsWith("KbmMouse"))
            {
                _moveCircle.Stroke = highlight ? FlashBrush : DimBrush;
                _moveCircle.StrokeThickness = highlight ? 2.5 : 1.5;
                _moveArrow.Visibility = highlight ? Visibility.Visible : Visibility.Collapsed;
                _moveArrow.Fill = FlashBrush;
                double angle = _flashTarget switch
                {
                    "KbmMouseX" => 90, "KbmMouseXNeg" => 270,
                    "KbmMouseY" => 0, "KbmMouseYNeg" => 180, _ => 0
                };
                _moveArrowCanvas.RenderTransform = new RotateTransform(angle, MoveSize / 2, MoveSize / 2);
                return;
            }

            if (_flashTarget == "KbmScroll")
            {
                _scrollUpArrow.Fill = highlight ? FlashBrush : DimBrush;
                // Fill, not Stroke: the wheel is a masked full-canvas
                // rectangle, so stroking it borders the whole pad.
                _scrollWheelPill.Fill = highlight ? FlashBrush : ScrollWheelBrush;
                return;
            }
            if (_flashTarget == "KbmScrollNeg")
            {
                _scrollDownArrow.Fill = highlight ? FlashBrush : DimBrush;
                // Fill, not Stroke: the wheel is a masked full-canvas
                // rectangle, so stroking it borders the whole pad.
                _scrollWheelPill.Fill = highlight ? FlashBrush : ScrollWheelBrush;
                return;
            }
            // The horizontal pair lights the wheel and, on a mouse that tilts,
            // its arrow, the way the vertical pair does.
            if (_flashTarget == "KbmScrollH" || _flashTarget == "KbmScrollHNeg")
            {
                var arrow = _flashTarget == "KbmScrollH" ? _scrollRightArrow : _scrollLeftArrow;
                if (arrow != null) arrow.Fill = highlight ? FlashBrush : DimBrush;
                _scrollWheelPill.Fill = highlight ? FlashBrush : ScrollWheelBrush;
                return;
            }
        }

        // ─────────────────────────────────────────────
        //  Rendering
        // ─────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
            // Retained-page guard (see ControllerModelView.OnRendering): skip
            // all per-frame work while hidden, including the theme check.
            // Iconic gate: IsVisible stays TRUE while minimized.
            if (!IsVisible || PadForge.Common.AmbientMotionProbe.Instance.IsWindowMinimized) return;
            // Rebuild on theme change.
            var currentTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
            if (_layoutBuilt && _lastTheme != currentTheme) { _lastTheme = currentTheme; RebuildLayout(); }

            if (_vm == null || !_layoutBuilt) return;
            var kbm = _vm.KbmOutputSnapshot;
            int leds = _vm.KbmKeyboardLeds;
            // Repaint when OUR data moved, not only when the view model
            // happened to raise something. See _painted.
            bool moved = !_paintedValid || !SamePreviewState(kbm, _painted) || leds != _paintedLeds;
            if (!_dirty && !moved) return;
            _dirty = false;
            _painted = kbm;
            _paintedValid = true;
            _paintedLeds = leds;

            // Lock lights: Num, Caps, Scroll, bits 0 to 2 of the LED report
            // (HMKeyboardLeds). -1 is a keyboard Windows has not set yet.
            if (_ledDots != null)
            {
                for (int i = 0; i < _ledDots.Length; i++)
                {
                    // The light recording its key keeps the flash the timer
                    // paints, as the keys do.
                    if (_flashOn && _ledTargets != null && _flashTarget == _ledTargets[i]) continue;
                    bool on = leds >= 0 && (leds & (1 << i)) != 0;
                    _ledDots[i].Fill = on ? AccentBrush : DimBrush;
                    SetGlow(_ledDots[i], on ? EmberGlowSmall : null);
                }
            }

            // Keyboard keys
            foreach (var w in _keyWidgets)
            {
                if (_flashTarget == w.TargetName && _flashOn) continue;
                bool pressed = w.VKeyIndex >= 0 && w.VKeyIndex <= 255 && kbm.GetKey((byte)w.VKeyIndex);
                w.Border.Background = pressed ? KeyPressedBrush : KeyNormalBrush;
                SetGlow(w.Border, pressed ? EmberGlow : null);
            }

            // Mouse buttons. Ember bloom rides the pressed brush (#175).
            if (_flashTarget != "KbmMBtn0" || !_flashOn)
            {
                bool p = kbm.GetMouseButton(0);
                _lmbPath.Fill = p ? AccentBrush : MouseButtonBrush;
            }
            if (_flashTarget != "KbmMBtn1" || !_flashOn)
            {
                bool p = kbm.GetMouseButton(1);
                _rmbPath.Fill = p ? AccentBrush : MouseButtonBrush;
            }
            // The wheel Fill is ALSO the recording flash surface for all four
            // scroll targets (ApplyFlashState) as well as KbmMBtn2. A guard
            // that missed one let this repaint stomp that flash every frame
            // (round 33, C4, and later the horizontal pair).
            if ((_flashTarget != "KbmMBtn2"
                 && _flashTarget != "KbmScroll"
                 && _flashTarget != "KbmScrollNeg"
                 && _flashTarget != "KbmScrollH"
                 && _flashTarget != "KbmScrollHNeg") || !_flashOn)
            {
                bool p = kbm.GetMouseButton(2);
                _scrollWheelPill.Fill = p ? AccentBrush : ScrollWheelBrush;
            }
            if (_flashTarget != "KbmMBtn3" || !_flashOn)
            {
                bool p = kbm.GetMouseButton(3);
                _x1Rect.Fill = p ? AccentBrush : MouseButtonBrush;
            }
            if (_flashTarget != "KbmMBtn4" || !_flashOn)
            {
                bool p = kbm.GetMouseButton(4);
                _x2Rect.Fill = p ? AccentBrush : MouseButtonBrush;
            }

            // Movement dot — map output values directly (deadzone already applied in Step 3)
            if (_flashTarget == null || !_flashTarget.StartsWith("KbmMouse"))
            {
                double moveX = MC - MoveSize / 2;
                double centerX = moveX + MoveSize / 2 - 5;
                double centerY = MoveTop + MoveSize / 2 - 5;
                double maxDeflect = MoveSize / 2 - 8;
                // Every lane that moves the cursor, not just the deflection
                // one. The rate lanes (gyro, touchpad, flick) report mouse
                // COUNTS rather than a [-1..+1] deflection, so they are
                // normalized by the counts one full deflection is worth
                // (KeyboardMouseVirtualController's per-poll spend) before
                // being summed in. Sign matches what the VC sends: those
                // lanes negate into screen space there, and screen-Y is
                // positive-down while this dot's Y is positive-up.
                // Signs derived per lane from what the VC actually sends, not
                // assumed. This dot is positive-RIGHT and positive-UP, while
                // the screen is positive-right and positive-DOWN.
                //   deflection: dx = +DeltaX, dy = -DeltaY  -> +X, +Y here
                //   gyro:       dx = -GyroX,  dy = -GyroY   -> -X, +Y here
                //   touchpad:   dx = +TouchX, dy = +TouchY  -> +X, -Y here
                //   flick:      dx = +FlickX                -> +X
                const double RateCountsFullScale = 15.0;
                double mx = kbm.MouseDeltaX / 32767.0
                            + (-kbm.MouseGyroX + kbm.MouseTouchX + kbm.MouseFlickX) / RateCountsFullScale;
                double my = kbm.MouseDeltaY / 32767.0
                            + (kbm.MouseGyroY - kbm.MouseTouchY) / RateCountsFullScale;
                mx = Math.Clamp(mx, -1.0, 1.0);
                my = Math.Clamp(my, -1.0, 1.0);

                double dotX = centerX + mx * maxDeflect;
                double dotY = centerY - my * maxDeflect;

                Canvas.SetLeft(_movementDot, dotX);
                Canvas.SetTop(_movementDot, dotY);
                bool moving = mx != 0 || my != 0;
                _movementDot.Fill = moving ? AccentBrush : DotBrush;
                SetGlow(_movementDot, moving ? EmberGlowSmall : null);
                if (_flashTarget == null) _moveArrow.Visibility = Visibility.Collapsed;
            }

            // Scroll direction visual feedback
            if (_flashTarget == null || !_flashTarget.StartsWith("KbmScroll"))
            {
                short scroll = kbm.ScrollDelta;
                _scrollUpArrow.Fill = scroll > 0 ? AccentBrush : DimBrush;
                SetGlow(_scrollUpArrow, scroll > 0 ? EmberGlowSmall : null);
                _scrollDownArrow.Fill = scroll < 0 ? AccentBrush : DimBrush;
                SetGlow(_scrollDownArrow, scroll < 0 ? EmberGlowSmall : null);
                if (_scrollLeftArrow != null)
                {
                    short tilt = kbm.ScrollDeltaH;
                    _scrollLeftArrow.Fill = tilt < 0 ? AccentBrush : DimBrush;
                    SetGlow(_scrollLeftArrow, tilt < 0 ? EmberGlowSmall : null);
                    _scrollRightArrow.Fill = tilt > 0 ? AccentBrush : DimBrush;
                    SetGlow(_scrollRightArrow, tilt > 0 ? EmberGlowSmall : null);
                }
            }
        }

        private struct KbmKeyWidget
        {
            public int VKeyIndex;
            public Border Border;
            public string TargetName;
        }
    }
}
