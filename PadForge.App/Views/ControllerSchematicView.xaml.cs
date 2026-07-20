using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// Programmatic schematic view for custom Extended controllers.
    /// Displays stick position circles, trigger bars, POV compasses, and button grids.
    /// </summary>
    public partial class ControllerSchematicView : UserControl
    {
        public event EventHandler<string> ControllerElementRecordRequested;

        private PadViewModel _vm;
        private bool _dirty;
        private bool _layoutBuilt;

        // Theme-aware brush keys — re-resolved by WPF on theme switch via
        // SetResourceReference. Dark-mode literals here used to leave the
        // schematic stuck on dark colors after a switch to light.
        private const string BgKey = "ControlFillColorDefaultBrush";
        private const string DimKey = "ControlStrokeColorDefaultBrush";
        private const string LabelKey = "TextFillColorSecondaryBrush";
        private const string AccentKey = "AccentFillColorDefaultBrush";

        // Semantic colors, intentionally fixed (recording flash + hover
        // affordance), not driven by theme. Hover warmed to the ember
        // family (#175): the rig is an output surface.
        private static readonly Brush FlashBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
        private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA2, 0x4D));

        // Ember bloom (#175 glow sweep): lit rig elements carry a static
        // DropShadowEffect. Frozen and shared, attached/detached where the
        // lit state is applied, never animated. Small variant for glyphs
        // 14px and under (the stick position dot).
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

        // Flash state
        private DispatcherTimer _flashTimer;
        private string _flashTarget;
        private bool _flashOn;

        // Widget tracking
        private readonly List<StickWidget> _stickWidgets = new();
        private readonly List<TriggerWidget> _triggerWidgets = new();
        private readonly List<PovWidget> _povWidgets = new();
        private readonly List<ButtonWidget> _buttonWidgets = new();

        // Layout constants
        private const double StickSize = 100;
        private const double TriggerWidth = 24;
        private const double TriggerHeight = 80;
        private const double PovSize = 60;
        private const double ButtonSize = 22;
        private const double SectionGap = 24;
        private const double LabelHeight = 18;
        private const double LayoutPadding = 12;
        private const int ButtonsPerRow = 8;

        public ControllerSchematicView()
        {
            InitializeComponent();
            // Rendering rides tree presence, matching MousePreviewControl.
            // CompositionTarget.Rendering is a STATIC event, so a ctor-lifetime
            // subscription is a permanent GC root: the view never dies, its
            // per-frame callback keeps invalidating layout for the life of the
            // process, and every fresh visit to the hosting page adds another
            // one. Measured 2026-07-15: one pad-page visit took the process
            // from 13% of a core to 125%, and it stayed there after navigating
            // away. The -= before += guards repeated Loaded without an
            // intervening Unloaded.
            Loaded += (s, e) =>
            {
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
            };
            Unloaded += (s, e) => CompositionTarget.Rendering -= OnRendering;
        }

        // ─────────────────────────────────────────────
        //  ViewModel binding (same interface as 2D/3D views)
        // ─────────────────────────────────────────────

        public void Bind(PadViewModel vm)
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.ExtendedConfig.PropertyChanged -= OnExtendedConfigPropertyChanged;
            }

            _vm = vm;

            if (_vm != null)
            {
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
                _vm.PropertyChanged += OnVmPropertyChanged;
                _vm.ExtendedConfig.PropertyChanged += OnExtendedConfigPropertyChanged;
                RebuildLayout();
            }
        }

        public void Unbind()
        {
            // Stop any in-flight recording-flash timer before tearing
            // down the rest of the binding state. Otherwise the
            // DispatcherTimer holds the control + widget objects alive
            // through its callback closure and keeps firing
            // ApplyFlashState every 400 ms after the page has been
            // unbound.
            UpdateFlashTarget(null);

            CompositionTarget.Rendering -= OnRendering;
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.ExtendedConfig.PropertyChanged -= OnExtendedConfigPropertyChanged;
            }
            _vm = null;
            _layoutBuilt = false;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.RawHidOutputSnapshot))
            {
                _dirty = true;
                return;
            }

            if (e.PropertyName == nameof(PadViewModel.OutputType)
                || e.PropertyName == nameof(PadViewModel.ProfileId))
            {
                // ProfileId changes need an explicit rebuild: when the incoming
                // profile's layout happens to match ExtendedConfig's current
                // values exactly (e.g. selecting "padforge-custom" while the
                // config already holds its 2/2/1/11 defaults), every setter
                // inside SyncExtendedConfigFromProfile is a no-op and no
                // ExtendedConfig.PropertyChanged fires, so the config-change
                // listener below would never trigger the rebuild.
                Dispatcher.Invoke(RebuildLayout);
                return;
            }

            if (e.PropertyName == nameof(PadViewModel.CurrentRecordingTarget))
            {
                Dispatcher.Invoke(() => UpdateFlashTarget(_vm?.CurrentRecordingTarget));
                return;
            }
        }

        private void OnExtendedConfigPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Rebuild layout when config counts change
            Dispatcher.Invoke(RebuildLayout);
        }

        // ─────────────────────────────────────────────
        //  Layout construction
        // ─────────────────────────────────────────────

        private void RebuildLayout()
        {
            SchematicCanvas.Children.Clear();
            _stickWidgets.Clear();
            _triggerWidgets.Clear();
            _povWidgets.Clear();
            _buttonWidgets.Clear();

            if (_vm == null) return;
            var cfg = _vm.ExtendedConfig;
            if (cfg == null) return;

            cfg.ComputeAxisLayout(out var stickAxisX, out var stickAxisY, out var triggerAxis);

            double x = LayoutPadding;
            double topY = LayoutPadding + LabelHeight;

            // ── Sticks ──
            for (int i = 0; i < cfg.ThumbstickCount; i++)
            {
                var w = CreateStickWidget(i, stickAxisX[i], stickAxisY[i], x, topY);
                _stickWidgets.Add(w);
                x += StickSize + SectionGap;
            }

            // ── Triggers ──
            for (int i = 0; i < cfg.TriggerCount; i++)
            {
                var w = CreateTriggerWidget(i, triggerAxis[i], x, topY);
                _triggerWidgets.Add(w);
                x += TriggerWidth + SectionGap;
            }

            // ── POVs ──
            for (int i = 0; i < cfg.PovCount; i++)
            {
                var w = CreatePovWidget(i, x, topY);
                _povWidgets.Add(w);
                x += PovSize + SectionGap;
            }

            // ── Buttons ── (wrap to rows)
            double btnStartX = LayoutPadding;
            double btnStartY = topY + Math.Max(StickSize, TriggerHeight) + SectionGap + LabelHeight;

            // Section label
            var btnSectionLabel = CreateLabel("Buttons", btnStartX, btnStartY - LabelHeight - 2);
            SchematicCanvas.Children.Add(btnSectionLabel);

            for (int i = 0; i < cfg.ButtonCount; i++)
            {
                int col = i % ButtonsPerRow;
                int row = i / ButtonsPerRow;
                double bx = btnStartX + col * (ButtonSize + 6);
                double by = btnStartY + row * (ButtonSize + 6);
                var w = CreateButtonWidget(i, bx, by);
                _buttonWidgets.Add(w);
            }

            // Set canvas size for Viewbox scaling
            double totalWidth = Math.Max(x, btnStartX + ButtonsPerRow * (ButtonSize + 6)) + LayoutPadding;
            int btnRows = cfg.ButtonCount > 0 ? ((cfg.ButtonCount - 1) / ButtonsPerRow + 1) : 0;
            double totalHeight = btnStartY + btnRows * (ButtonSize + 6) + LayoutPadding;

            SchematicCanvas.Width = totalWidth;
            SchematicCanvas.Height = totalHeight;
            _layoutBuilt = true;
            _dirty = true;
        }

        // ─────────────────────────────────────────────
        //  Stick widget
        // ─────────────────────────────────────────────

        private StickWidget CreateStickWidget(int index, int axisXIdx, int axisYIdx, double x, double y)
        {
            // Outer circle (deadzone ring)
            var outer = new Ellipse
            {
                Width = StickSize,
                Height = StickSize,
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand
            };
            // Idle ring is the same dim stroke MouseLeave restores (user
            // report 2026-07-06): the build-time ember ring read as a stuck
            // highlight until the pointer first passed over the element.
            // Ember is hover/flash feedback only.
            outer.SetResourceReference(Shape.StrokeProperty, DimKey);
            outer.SetResourceReference(Shape.FillProperty, BgKey);
            Canvas.SetLeft(outer, x);
            Canvas.SetTop(outer, y);
            SchematicCanvas.Children.Add(outer);

            // Crosshair lines
            var hLine = new Line
            {
                X1 = x + 4, Y1 = y + StickSize / 2,
                X2 = x + StickSize - 4, Y2 = y + StickSize / 2,
                StrokeThickness = 0.5, Opacity = 0.5
            };
            hLine.SetResourceReference(Shape.StrokeProperty, DimKey);
            var vLine = new Line
            {
                X1 = x + StickSize / 2, Y1 = y + 4,
                X2 = x + StickSize / 2, Y2 = y + StickSize - 4,
                StrokeThickness = 0.5, Opacity = 0.5
            };
            vLine.SetResourceReference(Shape.StrokeProperty, DimKey);
            SchematicCanvas.Children.Add(hLine);
            SchematicCanvas.Children.Add(vLine);

            // Position dot
            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                IsHitTestVisible = false
            };
            dot.SetResourceReference(Shape.FillProperty, AccentKey);
            Canvas.SetLeft(dot, x + StickSize / 2 - 5);
            Canvas.SetTop(dot, y + StickSize / 2 - 5);
            SchematicCanvas.Children.Add(dot);

            // Direction arrow (hidden until recording flash) — inside a Canvas for centered rotation
            double arrowLen = StickSize * 0.35;
            double arrowBase = 6;
            var dirArrow = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(StickSize / 2, StickSize / 2 - arrowLen),
                    new Point(StickSize / 2 - arrowBase, StickSize / 2 - arrowLen * 0.5),
                    new Point(StickSize / 2 + arrowBase, StickSize / 2 - arrowLen * 0.5)
                },
                Fill = FlashBrush,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            var stickArrowCanvas = new Canvas
            {
                Width = StickSize,
                Height = StickSize,
                IsHitTestVisible = false
            };
            stickArrowCanvas.Children.Add(dirArrow);
            Canvas.SetLeft(stickArrowCanvas, x);
            Canvas.SetTop(stickArrowCanvas, y);
            SchematicCanvas.Children.Add(stickArrowCanvas);

            // Label
            var label = CreateLabel(string.Format(Strings.Instance.Preview_Stick_Format, index + 1), x, y - LabelHeight);
            SchematicCanvas.Children.Add(label);

            // Hover: show direction arrow for hovered quadrant
            outer.MouseMove += (s, e) =>
            {
                if (_flashTarget != null) return; // don't override active flash
                var pos = e.GetPosition(outer);
                double hx = pos.X - StickSize / 2, hy = pos.Y - StickSize / 2;
                double angle;
                if (Math.Abs(hx) > Math.Abs(hy))
                    angle = hx > 0 ? 90 : 270; // right or left
                else
                    angle = hy > 0 ? 180 : 0; // down or up
                dirArrow.Visibility = Visibility.Visible;
                dirArrow.Fill = HoverBrush;
                stickArrowCanvas.RenderTransform = new RotateTransform(angle,
                    StickSize / 2, StickSize / 2);
                outer.Stroke = HoverBrush;
                outer.StrokeThickness = 2.5;
            };
            outer.MouseLeave += (s, e) =>
            {
                if (_flashTarget != null) return;
                dirArrow.Visibility = Visibility.Collapsed;
                outer.SetResourceReference(Shape.StrokeProperty, DimKey);
                outer.StrokeThickness = 1.5;
            };

            // Click-to-record: quadrant detection
            outer.MouseLeftButtonDown += (s, e) =>
            {
                var pos = e.GetPosition(outer);
                double cx = pos.X - StickSize / 2;
                double cy = pos.Y - StickSize / 2;
                string target;
                if (Math.Abs(cx) > Math.Abs(cy))
                    target = cx > 0 ? $"RawAxis{axisXIdx}" : $"RawAxis{axisXIdx}Neg";
                else
                    target = cy > 0 ? $"RawAxis{axisYIdx}" : $"RawAxis{axisYIdx}Neg";
                ControllerElementRecordRequested?.Invoke(this, target);
            };

            return new StickWidget
            {
                AxisXIndex = axisXIdx,
                AxisYIndex = axisYIdx,
                Dot = dot,
                DirectionArrow = dirArrow,
                ArrowCanvas = stickArrowCanvas,
                OuterCircle = outer,
                X = x,
                Y = y
            };
        }

        // ─────────────────────────────────────────────
        //  Trigger widget
        // ─────────────────────────────────────────────

        private TriggerWidget CreateTriggerWidget(int index, int axisIdx, double x, double y)
        {
            // Background bar
            var bg = new Rectangle
            {
                Width = TriggerWidth,
                Height = TriggerHeight,
                StrokeThickness = 1,
                RadiusX = 3, RadiusY = 3,
                Cursor = Cursors.Hand
            };
            bg.SetResourceReference(Shape.FillProperty, BgKey);
            bg.SetResourceReference(Shape.StrokeProperty, DimKey);
            Canvas.SetLeft(bg, x);
            Canvas.SetTop(bg, y);
            SchematicCanvas.Children.Add(bg);

            // Fill bar (grows from bottom)
            var fill = new Rectangle
            {
                Width = TriggerWidth - 4,
                Height = 0,
                RadiusX = 2, RadiusY = 2,
                IsHitTestVisible = false
            };
            fill.SetResourceReference(Shape.FillProperty, AccentKey);
            Canvas.SetLeft(fill, x + 2);
            Canvas.SetTop(fill, y + TriggerHeight - 2);
            SchematicCanvas.Children.Add(fill);

            // Label
            var label = CreateLabel(string.Format(Strings.Instance.Preview_Trigger_Format, index + 1), x, y - LabelHeight);
            SchematicCanvas.Children.Add(label);

            // Hover highlight
            bg.MouseEnter += (s, e) =>
            {
                if (_flashTarget != null) return;
                bg.Stroke = HoverBrush;
                bg.StrokeThickness = 2.5;
            };
            bg.MouseLeave += (s, e) =>
            {
                if (_flashTarget != null) return;
                bg.SetResourceReference(Shape.StrokeProperty, DimKey);
                bg.StrokeThickness = 1;
            };

            // Click-to-record
            bg.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, $"RawAxis{axisIdx}");
            };

            return new TriggerWidget
            {
                AxisIndex = axisIdx,
                Background = bg,
                Fill = fill,
                X = x,
                Y = y
            };
        }

        // ─────────────────────────────────────────────
        //  POV widget
        // ─────────────────────────────────────────────

        private PovWidget CreatePovWidget(int index, double x, double y)
        {
            // Outer circle
            var outer = new Ellipse
            {
                Width = PovSize,
                Height = PovSize,
                StrokeThickness = 1.5
            };
            // Idle = MouseLeave state (user report 2026-07-06): dim ring,
            // ember only on hover/flash.
            outer.SetResourceReference(Shape.StrokeProperty, DimKey);
            outer.SetResourceReference(Shape.FillProperty, BgKey);
            Canvas.SetLeft(outer, x);
            Canvas.SetTop(outer, y);
            SchematicCanvas.Children.Add(outer);

            // Direction arrow (initially hidden) — placed inside a small Canvas
            // so rotation always pivots around the POV center.
            double arrowTip = PovSize * 0.35;
            double arrowBase = PovSize * 0.15;
            var arrow = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(PovSize / 2, PovSize / 2 - arrowTip),
                    new Point(PovSize / 2 - 6, PovSize / 2 - arrowBase),
                    new Point(PovSize / 2 + 6, PovSize / 2 - arrowBase)
                },
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            arrow.SetResourceReference(Shape.FillProperty, AccentKey);
            // Use a fixed-size Canvas so RenderTransformOrigin (0.5,0.5) = POV center
            var arrowCanvas = new Canvas
            {
                Width = PovSize,
                Height = PovSize,
                IsHitTestVisible = false
            };
            arrowCanvas.Children.Add(arrow);
            Canvas.SetLeft(arrowCanvas, x);
            Canvas.SetTop(arrowCanvas, y);
            SchematicCanvas.Children.Add(arrowCanvas);

            // Label
            string povLabel = _vm.ExtendedConfig.PovCount == 1 ? Strings.Instance.Preview_DPad : string.Format(Strings.Instance.Preview_POV_Format, index + 1);
            var label = CreateLabel(povLabel, x, y - LabelHeight);
            SchematicCanvas.Children.Add(label);

            // Hover: show direction arrow for hovered quadrant
            outer.Cursor = Cursors.Hand;
            outer.MouseMove += (s, e) =>
            {
                if (_flashTarget != null) return;
                var pos = e.GetPosition(outer);
                double hx = pos.X - PovSize / 2, hy = pos.Y - PovSize / 2;
                double angle;
                if (Math.Abs(hx) > Math.Abs(hy))
                    angle = hx > 0 ? 90 : 270;
                else
                    angle = hy > 0 ? 180 : 0;
                arrow.Visibility = Visibility.Visible;
                arrow.Fill = HoverBrush;
                arrowCanvas.RenderTransform = new RotateTransform(angle,
                    PovSize / 2, PovSize / 2);
                outer.Stroke = HoverBrush;
                outer.StrokeThickness = 2.5;
            };
            outer.MouseLeave += (s, e) =>
            {
                if (_flashTarget != null) return;
                arrow.Visibility = Visibility.Collapsed;
                outer.SetResourceReference(Shape.StrokeProperty, DimKey);
                outer.StrokeThickness = 1.5;
            };

            // Click-to-record: detect direction by click position relative to center
            outer.MouseLeftButtonDown += (s, e) =>
            {
                var pos = e.GetPosition(outer);
                double cx = pos.X - PovSize / 2;
                double cy = pos.Y - PovSize / 2;
                string dir;
                if (Math.Abs(cx) > Math.Abs(cy))
                    dir = cx > 0 ? "Right" : "Left";
                else
                    dir = cy > 0 ? "Down" : "Up";
                ControllerElementRecordRequested?.Invoke(this, $"RawPov{index}{dir}");
                e.Handled = true;
            };

            var rotate = new RotateTransform(0, PovSize / 2, PovSize / 2);
            arrowCanvas.RenderTransform = rotate;
            return new PovWidget
            {
                PovIndex = index,
                Arrow = arrow,
                ArrowCanvas = arrowCanvas,
                Outer = outer,
                CenterX = x + PovSize / 2,
                CenterY = y + PovSize / 2,
                Rotate = rotate,
                FlashPrefix = $"RawPov{index}"
            };
        }

        // ─────────────────────────────────────────────
        //  Button widget
        // ─────────────────────────────────────────────

        private ButtonWidget CreateButtonWidget(int index, double x, double y)
        {
            var circle = new Ellipse
            {
                Width = ButtonSize,
                Height = ButtonSize,
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand
            };
            // Idle = MouseLeave state (user report 2026-07-06): dim ring,
            // ember only on hover/flash.
            circle.SetResourceReference(Shape.StrokeProperty, DimKey);
            circle.SetResourceReference(Shape.FillProperty, BgKey);
            Canvas.SetLeft(circle, x);
            Canvas.SetTop(circle, y);
            SchematicCanvas.Children.Add(circle);

            var text = new TextBlock
            {
                Text = (index + 1).ToString(),
                FontFamily = (FontFamily)FindResource("TelemetryFontFamily"),
                FontSize = 9,
                IsHitTestVisible = false,
                TextAlignment = TextAlignment.Center
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, LabelKey);
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(text, x + (ButtonSize - text.DesiredSize.Width) / 2);
            Canvas.SetTop(text, y + (ButtonSize - text.DesiredSize.Height) / 2);
            SchematicCanvas.Children.Add(text);

            circle.MouseEnter += (s, e) =>
            {
                if (_flashTarget != null) return;
                circle.Stroke = HoverBrush;
                circle.StrokeThickness = 2.5;
            };
            circle.MouseLeave += (s, e) =>
            {
                if (_flashTarget != null) return;
                circle.SetResourceReference(Shape.StrokeProperty, DimKey);
                circle.StrokeThickness = 1.5;
            };

            circle.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, $"RawBtn{index}");
            };

            return new ButtonWidget { ButtonIndex = index, Circle = circle };
        }

        // ─────────────────────────────────────────────
        //  Flash animation for recording target
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            // Stop existing flash
            if (_flashTimer != null)
            {
                _flashTimer.Stop();
                _flashTimer = null;
            }

            // Reset previous flash element
            ApplyFlashState(false);

            // Invalidate the transition gates: the flash stomped fills,
            // visibility, and angles, and with change-detected snapshots
            // an idle pad may never get another dirty frame to repaint.
            foreach (var w in _povWidgets) w.LastPov = int.MinValue;
            foreach (var w in _buttonWidgets) w.LastPressed = -1;

            _flashTarget = target;

            if (string.IsNullOrEmpty(target))
                return;

            _flashOn = true;
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _flashTimer.Tick += (s, e) =>
            {
                _flashOn = !_flashOn;
                ApplyFlashState(_flashOn);
            };
            _flashTimer.Start();
            ApplyFlashState(true);
        }

        private void ApplyFlashState(bool highlight)
        {
            if (string.IsNullOrEmpty(_flashTarget)) return;

            string t = _flashTarget;
            // Strip "Neg" suffix for matching
            string baseTarget = t.EndsWith("Neg", StringComparison.Ordinal) ? t[..^3] : t;

            // Check sticks (match RawAxisN where N is either X or Y index)
            foreach (var w in _stickWidgets)
            {
                if (baseTarget == $"RawAxis{w.AxisXIndex}" || baseTarget == $"RawAxis{w.AxisYIndex}")
                {
                    bool isNeg = t.EndsWith("Neg", StringComparison.Ordinal);
                    bool isX = baseTarget == $"RawAxis{w.AxisXIndex}";
                    // Determine arrow angle: right=90, left=270, up=0, down=180
                    // Y: Neg = up (top of stick in WPF), non-Neg = down (bottom)
                    double angle;
                    if (isX)
                        angle = isNeg ? 270 : 90; // left or right
                    else
                        angle = isNeg ? 0 : 180; // up or down

                    if (highlight) w.OuterCircle.Stroke = FlashBrush;
                    else w.OuterCircle.SetResourceReference(Shape.StrokeProperty, DimKey);
                    w.OuterCircle.StrokeThickness = highlight ? 2.5 : 1.5;
                    w.DirectionArrow.Visibility = highlight ? Visibility.Visible : Visibility.Collapsed;
                    w.DirectionArrow.Fill = FlashBrush;
                    w.ArrowCanvas.RenderTransform = new RotateTransform(angle,
                        StickSize / 2, StickSize / 2);
                    return;
                }
            }

            // Check triggers. Flash the BACKGROUND rect's stroke
            // (matches the button-widget pattern) instead of the inner
            // progress-fill rect's color. The fill starts at Height = 0
            // when the trigger is at rest, so recoloring an invisible
            // zero-height rectangle never shows up — that was the bug:
            // click-to-record on an extended trigger produced no visible
            // flash until the user pressed the source.
            foreach (var w in _triggerWidgets)
            {
                if (baseTarget == $"RawAxis{w.AxisIndex}")
                {
                    if (highlight) w.Background.Stroke = FlashBrush;
                    else w.Background.SetResourceReference(Shape.StrokeProperty, DimKey);
                    w.Background.StrokeThickness = highlight ? 2.5 : 1;
                    return;
                }
            }

            // Check buttons
            foreach (var w in _buttonWidgets)
            {
                if (t == $"RawBtn{w.ButtonIndex}")
                {
                    if (highlight) w.Circle.Stroke = FlashBrush;
                    else w.Circle.SetResourceReference(Shape.StrokeProperty, DimKey);
                    w.Circle.StrokeThickness = highlight ? 2.5 : 1.5;
                    return;
                }
            }

            // Check POVs (match RawPov{N}Up/Down/Left/Right)
            foreach (var w in _povWidgets)
            {
                if (t.StartsWith($"RawPov{w.PovIndex}", StringComparison.Ordinal))
                {
                    if (highlight) w.Arrow.Fill = FlashBrush;
                    else w.Arrow.SetResourceReference(Shape.FillProperty, AccentKey);
                    // Match the stick pattern: blink on/off rather than
                    // staying visible the whole time so the flash is
                    // actually perceptible against a centered POV.
                    w.Arrow.Visibility = highlight ? Visibility.Visible : Visibility.Collapsed;
                    // Show arrow pointing in the target direction
                    string dir = t.Substring($"RawPov{w.PovIndex}".Length);
                    double angle = dir switch
                    {
                        "Up" => 0,
                        "Right" => 90,
                        "Down" => 180,
                        "Left" => 270,
                        _ => 0
                    };
                    // Mutate the retained transform: replacing
                    // RenderTransform would orphan the repaint loop's
                    // w.Rotate binding.
                    w.Rotate.Angle = angle;
                    return;
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Rendering (30Hz update)
        // ─────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
            // Retained-page guard (same as MidiPreviewView's input path): the
            // page is visibility-toggled, not unloaded, so skip the repaint
            // while hidden. _dirty stays set, so the first visible frame
            // catches up.
            if (!IsVisible) return;
            if (!_dirty || _vm == null || !_layoutBuilt) return;
            _dirty = false;

            var raw = _vm.RawHidOutputSnapshot;

            // Update sticks
            foreach (var w in _stickWidgets)
            {
                double nx = 0.5, ny = 0.5;
                if (raw.Axes != null)
                {
                    if (w.AxisXIndex < raw.Axes.Length)
                        nx = (raw.Axes[w.AxisXIndex] - (double)short.MinValue) / 65535.0;
                    if (w.AxisYIndex < raw.Axes.Length)
                        ny = (raw.Axes[w.AxisYIndex] - (double)short.MinValue) / 65535.0;
                }
                double dotX = w.X + nx * (StickSize - 10);
                double dotY = w.Y + ny * (StickSize - 10);
                Canvas.SetLeft(w.Dot, dotX);
                Canvas.SetTop(w.Dot, dotY);

                // Deflected dot is lit: ember bloom (#175). Centered: none.
                bool deflected = Math.Abs(nx - 0.5) > 0.02 || Math.Abs(ny - 0.5) > 0.02;
                SetGlow(w.Dot, deflected ? EmberGlowSmall : null);
            }

            // Update triggers
            foreach (var w in _triggerWidgets)
            {
                double value = 0;
                if (raw.Axes != null && w.AxisIndex < raw.Axes.Length)
                    value = (raw.Axes[w.AxisIndex] - (double)short.MinValue) / 65535.0;
                double fillH = Math.Clamp(value, 0, 1) * (TriggerHeight - 4);
                w.Fill.Height = fillH;
                Canvas.SetTop(w.Fill, w.Y + TriggerHeight - 2 - fillH);

                // A visible fill is lit: ember bloom (#175). At rest: none.
                SetGlow(w.Fill, fillH > 0 ? EmberGlow : null);
            }

            // Update POVs (skip when hovered or flash-targeted to prevent flickering)
            foreach (var w in _povWidgets)
            {
                if (w.Outer.IsMouseOver) continue;
                if (_flashTarget != null && _flashTarget.StartsWith(w.FlashPrefix, StringComparison.Ordinal)) continue;

                int povValue = -1;
                if (raw.Povs != null && w.PovIndex < raw.Povs.Length)
                    povValue = raw.Povs[w.PovIndex];

                bool engaged = povValue >= 0 && povValue <= 36000;
                bool wasEngaged = w.LastPov >= 0 && w.LastPov <= 36000;
                if (povValue == w.LastPov) continue;
                w.LastPov = povValue;

                if (!engaged)
                {
                    w.Arrow.Visibility = Visibility.Collapsed;
                }
                else
                {
                    w.Arrow.Visibility = Visibility.Visible;
                    if (!wasEngaged)
                        w.Arrow.SetResourceReference(Shape.FillProperty, AccentKey);
                    w.Rotate.Angle = povValue / 100.0;
                }
            }

            // Update buttons (transition-only: SetResourceReference installs
            // and re-resolves a resource expression on every call).
            foreach (var w in _buttonWidgets)
            {
                bool pressed = raw.IsButtonPressed(w.ButtonIndex);
                int p = pressed ? 1 : 0;
                if (p == w.LastPressed) continue;
                w.LastPressed = p;
                w.Circle.SetResourceReference(Shape.FillProperty, pressed ? AccentKey : BgKey);
                // Pressed ring blooms ember (#175). Unpressed: none.
                SetGlow(w.Circle, pressed ? EmberGlow : null);
            }
        }

        // ─────────────────────────────────────────────
        //  Helper
        // ─────────────────────────────────────────────

        private static TextBlock CreateLabel(string text, double x, double y)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                IsHitTestVisible = false
            };
            // Mono telemetry face like the XAML canvases. Static helper, so
            // resolve at app scope; skip gracefully if the resource is absent.
            if (Application.Current.TryFindResource("TelemetryFontFamily") is FontFamily mono)
                tb.FontFamily = mono;
            tb.SetResourceReference(TextBlock.ForegroundProperty, LabelKey);
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            return tb;
        }

        // ─────────────────────────────────────────────
        //  Widget structs
        // ─────────────────────────────────────────────

        private struct StickWidget
        {
            public int AxisXIndex, AxisYIndex;
            public Ellipse Dot;
            public Polygon DirectionArrow;
            public Canvas ArrowCanvas;
            public Ellipse OuterCircle;
            public double X, Y;
        }

        private struct TriggerWidget
        {
            public int AxisIndex;
            public Rectangle Background;
            public Rectangle Fill;
            public double X, Y;
        }

        private sealed class PovWidget
        {
            public int PovIndex;
            public Polygon Arrow;
            public Canvas ArrowCanvas;
            public Ellipse Outer;
            public double CenterX, CenterY;
            /// <summary>Retained transform, mutated per change (a fresh
            /// RotateTransform per repaint frame was pure churn).</summary>
            public RotateTransform Rotate;
            /// <summary>Prebuilt "RawPovN" flash prefix (the interpolation
            /// allocated per POV per repaint while a flash was active).</summary>
            public string FlashPrefix;
            /// <summary>Last painted POV value; int.MinValue = unknown.
            /// SetResourceReference re-resolves the key on every call with
            /// no equality short-circuit, so paint only on transitions.</summary>
            public int LastPov = int.MinValue;
        }

        private sealed class ButtonWidget
        {
            public int ButtonIndex;
            public Ellipse Circle;
            /// <summary>-1 unknown, else 0/1: transition-only repaint.</summary>
            public int LastPressed = -1;
        }
    }
}
