using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class KBMPreviewView : UserControl
    {
        public event EventHandler<string> ControllerElementRecordRequested;

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
        private static Brush KeyNormalBrush => IsDarkTheme ? _knD : _knL;
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
        private const double BtnBottom = 58;
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
            };
            Unloaded += (s, e) => CompositionTarget.Rendering -= OnRendering;
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
            if (e.PropertyName == nameof(PadViewModel.CurrentRecordingTarget)) { Dispatcher.Invoke(() => UpdateFlashTarget(_vm?.CurrentRecordingTarget)); return; }
            _dirty = true;
        }

        private void RebuildLayout()
        {
            _layoutBuilt = false;
            _keyWidgets.Clear();
            _lastTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
            if (_vm == null || _vm.OutputType != VirtualControllerType.KeyboardMouse) return;
            BuildKeyboardCanvas();
            BuildMouseCanvas();
            _layoutBuilt = true;
            _dirty = true;
        }

        // ─────────────────────────────────────────────
        //  Keyboard
        // ─────────────────────────────────────────────

        private void BuildKeyboardCanvas()
        {
            KeyboardCanvas.Children.Clear();
            var keys = KeyboardKeyItem.BuildLayout();
            foreach (var key in keys)
            {
                string targetName = $"KbmKey{key.VKeyIndex:X2}";
                string tooltipLabel = _vm?.Mappings?.FirstOrDefault(m => m.TargetSettingName == targetName)?.TargetLabel ?? key.Label;
                var border = new Border
                {
                    Width = key.KeyWidth, Height = key.KeyHeight,
                    CornerRadius = new CornerRadius(3),
                    Background = KeyNormalBrush, Cursor = Cursors.Hand,
                    ToolTip = tooltipLabel
                };
                border.Child = new TextBlock
                {
                    Text = key.Label, FontSize = 8,
                    FontFamily = (FontFamily)FindResource("TelemetryFontFamily"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.8, IsHitTestVisible = false
                };
                Canvas.SetLeft(border, key.X);
                Canvas.SetTop(border, key.Y);
                KeyboardCanvas.Children.Add(border);

                border.MouseEnter += (s, e) => { if (_flashTarget == null) { border.BorderBrush = HoverBrush; border.BorderThickness = new Thickness(1.5); } };
                border.MouseLeave += (s, e) => { if (_flashTarget == null) { border.BorderBrush = null; border.BorderThickness = new Thickness(0); } };
                border.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, targetName); e.Handled = true; };
                _keyWidgets.Add(new KbmKeyWidget { VKeyIndex = key.VKeyIndex, Border = border, TargetName = targetName });
            }
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

            // Interaction is KBM-only: these surfaces record a mapping.
            // Targets are the ORIGINAL ones -- LMB is Btn0, RMB is Btn1,
            // the wheel is Btn2. Do not renumber them to match the visual
            // left-to-right order; they are the engine's descriptors.
            foreach (var sh in new Shape[] { _lmbPath, _rmbPath, _scrollWheelPill, _x1Rect, _x2Rect,
                                             _scrollUpArrow, _scrollDownArrow })
                sh.Cursor = Cursors.Hand;

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

            MouseCanvas.Height = MouseGlyph.BodyH + 6;
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
        /// the whole struct would repaint on values nothing here shows.</summary>
        internal static bool SamePreviewState(in KbmRawState a, in KbmRawState b)
            => a.Keys0 == b.Keys0 && a.Keys1 == b.Keys1
            && a.Keys2 == b.Keys2 && a.Keys3 == b.Keys3
            && a.MouseButtons == b.MouseButtons
            && a.ScrollDelta == b.ScrollDelta
            && a.MouseDeltaX == b.MouseDeltaX
            && a.MouseDeltaY == b.MouseDeltaY;

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
            // Repaint when OUR data moved, not only when the view model
            // happened to raise something. See _painted.
            bool moved = !_paintedValid || !SamePreviewState(kbm, _painted);
            if (!_dirty && !moved) return;
            _dirty = false;
            _painted = kbm;
            _paintedValid = true;

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
            if (_flashTarget != "KbmMBtn2" || !_flashOn)
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
                short mx = kbm.MouseDeltaX, my = kbm.MouseDeltaY;

                double dotX = centerX + mx / 32767.0 * maxDeflect;
                double dotY = centerY - my / 32767.0 * maxDeflect;

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
