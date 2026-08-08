using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// <summary>
    /// Live preview for a VR slot (#49), drawn in the SAME schematic
    /// grammar as ControllerSchematicView: a canvas of sticks, bars, and
    /// button circles, not cards of text. Every constant, brush key, hover
    /// affordance, and hit rule below is that view's, so the two read as
    /// one family.
    ///
    /// <para>One VR slot drives BOTH SteamVR hands, so the canvas carries
    /// two groups side by side rather than one controller body.</para>
    ///
    /// <para>Interaction matches the branded and Extended previews: hover
    /// warms an element's ring, hovering a stick shows a DIRECTION ARROW
    /// for the quadrant under the pointer so the user can see which way
    /// they are about to bind, clicking records that direction's target,
    /// and the element the grid is recording flashes.</para>
    /// </summary>
    public partial class VRPreviewView : UserControl
    {
        /// <summary>Raised when the user clicks an element to map it.</summary>
        public event EventHandler<string> ControllerElementRecordRequested;

        // Same brush keys as ControllerSchematicView, so a theme switch
        // re-resolves both identically.
        private const string BgKey = "ControlFillColorDefaultBrush";
        private const string DimKey = "ControlStrokeColorDefaultBrush";
        private const string LabelKey = "TextFillColorSecondaryBrush";
        private const string AccentKey = "AccentFillColorDefaultBrush";

        private static readonly Brush FlashBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
        private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA2, 0x4D));

        // Schematic metrics, borrowed wholesale.
        private const double StickSize = 100;
        private const double TriggerWidth = 24;
        private const double TriggerHeight = 80;
        private const double ButtonSize = 22;
        private const double LabelHeight = 18;
        private const double GroupGap = 90;
        private const double Pad = 16;

        private PadViewModel _vm;
        private bool _built;
        private VrRawState _painted;
        private bool _paintedValid;

        private string _flashTarget;
        private bool _flashOn;
        private System.Windows.Threading.DispatcherTimer _flashTimer;

        private sealed class Hand
        {
            public Ellipse StickRing;
            public Ellipse StickDot;
            public Polygon StickArrow;
            public Canvas StickArrowHost;
            public Rectangle TriggerBg, TriggerFill;
            public Rectangle GripBg, GripFill;
            public Ellipse[] Buttons = new Ellipse[8];
        }

        private readonly Hand _left = new();
        private readonly Hand _right = new();

        // Every hit element by its target, so the flash pass can find them
        // without walking the tree.
        private readonly Dictionary<string, Shape> _byTarget = new(StringComparer.Ordinal);

        public VRPreviewView()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                Build();
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
            };
            Unloaded += (s, e) => CompositionTarget.Rendering -= OnRendering;
        }

        /// <summary>Binds the pad ViewModel. Same entry point the sibling
        /// previews expose so PadPage wires them all alike.</summary>
        public void Bind(PadViewModel vm)
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = vm;
            if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
            _paintedValid = false;
            UpdateFlashTarget(_vm?.CurrentRecordingTarget);
        }

        /// <summary>Drops the ViewModel subscription (sibling contract).</summary>
        public void Unbind()
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
            _paintedValid = false;
            UpdateFlashTarget(null);
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.CurrentRecordingTarget))
                Dispatcher.Invoke(() => UpdateFlashTarget(_vm?.CurrentRecordingTarget));
        }

        // ─────────────────────────────────────────────
        //  Layout
        // ─────────────────────────────────────────────

        private void Build()
        {
            if (_built) return;
            var st = Strings.Instance;

            double groupWidth = StickSize + 40 + TriggerWidth * 2 + 24;
            BuildHand(_left, Pad, "L", st.Vr_LeftHand,
                st.Btn_LeftTrigger, st.Btn_VrLeftGrip,
                st.Btn_LeftStickX, st.Btn_LeftStickY);
            BuildHand(_right, Pad + groupWidth + GroupGap, "R", st.Vr_RightHand,
                st.Btn_RightTrigger, st.Btn_VrRightGrip,
                st.Btn_RightStickX, st.Btn_RightStickY);

            VrCanvas.Width = Pad * 2 + groupWidth * 2 + GroupGap;
            VrCanvas.Height = Pad * 2 + LabelHeight + StickSize + 26 + ButtonSize * 2 + 12 + LabelHeight;
            _built = true;
        }

        private void BuildHand(Hand h, double ox, string side, string title,
            string triggerName, string gripName, string stickXName, string stickYName)
        {
            double y = Pad + LabelHeight;

            VrCanvas.Children.Add(CreateLabel(title, ox, Pad, 12));

            // Stick, with quadrant hover + direction arrow.
            BuildStick(h, ox, y, side, stickXName, stickYName);

            // Trigger and grip bars, right of the stick.
            double bx = ox + StickSize + 28;
            BuildBar(h, bx, y, $"Vr{side}Trigger", triggerName, isTrigger: true);
            BuildBar(h, bx + TriggerWidth + 26, y, $"Vr{side}Grip", gripName, isTrigger: false);

            // Buttons under the row, in HMVRButton bit order.
            var keys = side == "L" ? VrLayout.LeftButtonKeys : VrLayout.RightButtonKeys;
            var st = Strings.Instance;
            string[] names = side == "L"
                ? new[] { st.Btn_VrLeftSystem, st.Btn_VrLeftA, st.Btn_VrLeftATouch, st.Btn_VrLeftB,
                          st.Btn_VrLeftBTouch, st.Btn_VrLeftTriggerClick, st.Btn_VrLeftGripClick, st.Btn_LeftStickButton }
                : new[] { st.Btn_VrRightSystem, st.Btn_VrRightA, st.Btn_VrRightATouch, st.Btn_VrRightB,
                          st.Btn_VrRightBTouch, st.Btn_VrRightTriggerClick, st.Btn_VrRightGripClick, st.Btn_RightStickButton };
            // Short glyph captions, so a 22 px circle stays legible. The
            // full localized name lives on the tooltip.
            string[] caps = { "S", "A", "a", "B", "b", "T", "G", "●" };

            double by = y + StickSize + 26;
            for (int i = 0; i < 8; i++)
            {
                double cx = ox + (i % 4) * (ButtonSize + 10);
                double cy = by + (i / 4) * (ButtonSize + 10);
                h.Buttons[i] = BuildButton(cx, cy, caps[i], keys[i], names[i]);
            }
        }

        private void BuildStick(Hand h, double x, double y, string side,
            string stickXName, string stickYName)
        {
            var outer = new Ellipse
            {
                Width = StickSize,
                Height = StickSize,
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand,
            };
            outer.SetResourceReference(Shape.StrokeProperty, DimKey);
            outer.SetResourceReference(Shape.FillProperty, BgKey);
            Canvas.SetLeft(outer, x);
            Canvas.SetTop(outer, y);
            VrCanvas.Children.Add(outer);

            var hLine = new Line
            {
                X1 = x + 4, Y1 = y + StickSize / 2,
                X2 = x + StickSize - 4, Y2 = y + StickSize / 2,
                StrokeThickness = 0.5, Opacity = 0.5, IsHitTestVisible = false,
            };
            hLine.SetResourceReference(Shape.StrokeProperty, DimKey);
            var vLine = new Line
            {
                X1 = x + StickSize / 2, Y1 = y + 4,
                X2 = x + StickSize / 2, Y2 = y + StickSize - 4,
                StrokeThickness = 0.5, Opacity = 0.5, IsHitTestVisible = false,
            };
            vLine.SetResourceReference(Shape.StrokeProperty, DimKey);
            VrCanvas.Children.Add(hLine);
            VrCanvas.Children.Add(vLine);

            var dot = new Ellipse { Width = 10, Height = 10, IsHitTestVisible = false };
            dot.SetResourceReference(Shape.FillProperty, AccentKey);
            Canvas.SetLeft(dot, x + StickSize / 2 - 5);
            Canvas.SetTop(dot, y + StickSize / 2 - 5);
            VrCanvas.Children.Add(dot);

            // Direction arrow: hidden until hover or flash, rotated to the
            // quadrant so "which way am I binding" is visible before the
            // click, exactly as the schematic does it.
            double arrowLen = StickSize * 0.35, arrowBase = 6;
            var arrow = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(StickSize / 2, StickSize / 2 - arrowLen),
                    new Point(StickSize / 2 - arrowBase, StickSize / 2 - arrowLen * 0.5),
                    new Point(StickSize / 2 + arrowBase, StickSize / 2 - arrowLen * 0.5),
                },
                Fill = FlashBrush,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            var arrowHost = new Canvas { Width = StickSize, Height = StickSize, IsHitTestVisible = false };
            arrowHost.Children.Add(arrow);
            Canvas.SetLeft(arrowHost, x);
            Canvas.SetTop(arrowHost, y);
            VrCanvas.Children.Add(arrowHost);

            outer.MouseMove += (s, e) =>
            {
                if (_flashTarget != null) return;
                var p = e.GetPosition(outer);
                double hx = p.X - StickSize / 2, hy = p.Y - StickSize / 2;
                double angle = Math.Abs(hx) > Math.Abs(hy) ? (hx > 0 ? 90 : 270) : (hy > 0 ? 180 : 0);
                arrow.Visibility = Visibility.Visible;
                arrow.Fill = HoverBrush;
                arrowHost.RenderTransform = new RotateTransform(angle, StickSize / 2, StickSize / 2);
                outer.Stroke = HoverBrush;
                outer.StrokeThickness = 2.5;
                // Name the exact direction under the pointer.
                outer.ToolTip = Math.Abs(hx) > Math.Abs(hy) ? stickXName : stickYName;
            };
            outer.MouseLeave += (s, e) =>
            {
                if (_flashTarget != null) return;
                arrow.Visibility = Visibility.Collapsed;
                outer.SetResourceReference(Shape.StrokeProperty, DimKey);
                outer.StrokeThickness = 1.5;
            };
            outer.MouseLeftButtonDown += (s, e) =>
            {
                var p = e.GetPosition(outer);
                double cx = p.X - StickSize / 2, cy = p.Y - StickSize / 2;
                string target = Math.Abs(cx) > Math.Abs(cy)
                    ? (cx > 0 ? $"Vr{side}StickX" : $"Vr{side}StickXNeg")
                    : (cy > 0 ? $"Vr{side}StickY" : $"Vr{side}StickYNeg");
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };

            h.StickRing = outer;
            h.StickDot = dot;
            h.StickArrow = arrow;
            h.StickArrowHost = arrowHost;
            _byTarget[$"Vr{side}StickX"] = outer;
            _byTarget[$"Vr{side}StickXNeg"] = outer;
            _byTarget[$"Vr{side}StickY"] = outer;
            _byTarget[$"Vr{side}StickYNeg"] = outer;
        }

        private void BuildBar(Hand h, double x, double y, string target, string displayName, bool isTrigger)
        {
            var bg = new Rectangle
            {
                Width = TriggerWidth,
                Height = TriggerHeight,
                StrokeThickness = 1,
                RadiusX = 3, RadiusY = 3,
                Cursor = Cursors.Hand,
                ToolTip = displayName,
            };
            bg.SetResourceReference(Shape.FillProperty, BgKey);
            bg.SetResourceReference(Shape.StrokeProperty, DimKey);
            Canvas.SetLeft(bg, x);
            Canvas.SetTop(bg, y);
            VrCanvas.Children.Add(bg);

            var fill = new Rectangle
            {
                Width = TriggerWidth - 4,
                Height = 0,
                RadiusX = 2, RadiusY = 2,
                IsHitTestVisible = false,
            };
            fill.SetResourceReference(Shape.FillProperty, AccentKey);
            Canvas.SetLeft(fill, x + 2);
            Canvas.SetTop(fill, y + TriggerHeight - 2);
            VrCanvas.Children.Add(fill);

            VrCanvas.Children.Add(CreateLabel(displayName, x - 6, y + TriggerHeight + 4, 9));

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
            bg.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };

            if (isTrigger) { h.TriggerBg = bg; h.TriggerFill = fill; }
            else { h.GripBg = bg; h.GripFill = fill; }
            _byTarget[target] = bg;
        }

        private Ellipse BuildButton(double x, double y, string caption, string target, string displayName)
        {
            var circle = new Ellipse
            {
                Width = ButtonSize,
                Height = ButtonSize,
                StrokeThickness = 1.5,
                Cursor = Cursors.Hand,
                ToolTip = displayName,
            };
            circle.SetResourceReference(Shape.StrokeProperty, DimKey);
            circle.SetResourceReference(Shape.FillProperty, BgKey);
            Canvas.SetLeft(circle, x);
            Canvas.SetTop(circle, y);
            VrCanvas.Children.Add(circle);

            var text = new TextBlock
            {
                Text = caption,
                FontFamily = (FontFamily)FindResource("TelemetryFontFamily"),
                FontSize = 9,
                IsHitTestVisible = false,
                TextAlignment = TextAlignment.Center,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, LabelKey);
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(text, x + (ButtonSize - text.DesiredSize.Width) / 2);
            Canvas.SetTop(text, y + (ButtonSize - text.DesiredSize.Height) / 2);
            VrCanvas.Children.Add(text);

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
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };

            _byTarget[target] = circle;
            return circle;
        }

        private TextBlock CreateLabel(string text, double x, double y, double size)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = size,
                IsHitTestVisible = false,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, LabelKey);
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            return tb;
        }

        // ─────────────────────────────────────────────
        //  Recording flash
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            if (_flashTimer == null)
            {
                _flashTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(450) };
                _flashTimer.Tick += (s, e) => { _flashOn = !_flashOn; ApplyFlash(); };
            }

            // Restore the element the previous target owned.
            if (_flashTarget != null && _byTarget.TryGetValue(_flashTarget, out var prev))
            {
                prev.SetResourceReference(Shape.StrokeProperty, DimKey);
                prev.StrokeThickness = prev is Ellipse ? 1.5 : 1;
            }
            if (_left.StickArrow != null) _left.StickArrow.Visibility = Visibility.Collapsed;
            if (_right.StickArrow != null) _right.StickArrow.Visibility = Visibility.Collapsed;

            _flashTarget = target;
            _flashOn = false;
            if (string.IsNullOrEmpty(_flashTarget)) _flashTimer.Stop();
            else _flashTimer.Start();
            ApplyFlash();
        }

        private void ApplyFlash()
        {
            if (!_built || string.IsNullOrEmpty(_flashTarget)) return;
            if (!_byTarget.TryGetValue(_flashTarget, out var shape)) return;

            if (_flashOn) { shape.Stroke = FlashBrush; shape.StrokeThickness = 2.5; }
            else
            {
                shape.SetResourceReference(Shape.StrokeProperty, DimKey);
                shape.StrokeThickness = shape is Ellipse ? 1.5 : 1;
            }

            // A stick target also points its arrow at the direction being
            // recorded, which is the whole reason the arrow exists.
            var hand = _flashTarget.StartsWith("VrL", StringComparison.Ordinal) ? _left : _right;
            if (hand.StickArrow == null) return;
            if (_flashTarget.Contains("Stick") && !_flashTarget.EndsWith("Click", StringComparison.Ordinal))
            {
                double angle = _flashTarget.Contains("StickX")
                    ? (_flashTarget.EndsWith("Neg", StringComparison.Ordinal) ? 270 : 90)
                    : (_flashTarget.EndsWith("Neg", StringComparison.Ordinal) ? 0 : 180);
                hand.StickArrowHost.RenderTransform = new RotateTransform(angle, StickSize / 2, StickSize / 2);
                hand.StickArrow.Fill = FlashBrush;
                hand.StickArrow.Visibility = _flashOn ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ─────────────────────────────────────────────
        //  Rendering
        // ─────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
            // Retained-page guard, matching the sibling previews.
            if (!IsVisible || Common.AmbientMotionProbe.Instance.IsWindowMinimized) return;
            if (_vm == null || !_built) return;

            var vr = _vm.VrOutputSnapshot;
            if (_paintedValid && Same(in vr, in _painted)) return;
            _painted = vr;
            _paintedValid = true;

            PaintHand(in vr.Left, _left, Pad);
            PaintHand(in vr.Right, _right, Pad + (StickSize + 40 + TriggerWidth * 2 + 24) + GroupGap);
        }

        private void PaintHand(in VrHandRaw hand, Hand h, double ox)
        {
            for (int i = 0; i < h.Buttons.Length; i++)
            {
                if (h.Buttons[i] == null) continue;
                bool lit = (hand.Buttons & (1 << i)) != 0;
                if (lit) h.Buttons[i].Fill = AccentFill;
                else h.Buttons[i].SetResourceReference(Shape.FillProperty, BgKey);
            }

            // The snapshot is the pipeline's SDL-native frame (Y positive =
            // down), which is also screen-Y, so neither axis flips here.
            // The single Y flip for the wire lives in
            // HMaestroVRController.PackHand.
            double y = Pad + LabelHeight;
            double nx = hand.StickX >= 0 ? hand.StickX / 32767.0 : hand.StickX / 32768.0;
            double ny = hand.StickY >= 0 ? hand.StickY / 32767.0 : hand.StickY / 32768.0;
            double travel = StickSize / 2 - 8;
            Canvas.SetLeft(h.StickDot, ox + StickSize / 2 - 5 + nx * travel);
            Canvas.SetTop(h.StickDot, y + StickSize / 2 - 5 + ny * travel);

            SetBar(h.TriggerFill, hand.Trigger, y);
            SetBar(h.GripFill, hand.Grip, y);
        }

        private static void SetBar(Rectangle fill, short value, double y)
        {
            if (fill == null) return;
            double frac = Math.Clamp(value / 32767.0, 0, 1);
            double hgt = (TriggerHeight - 4) * frac;
            fill.Height = hgt;
            Canvas.SetTop(fill, y + TriggerHeight - 2 - hgt);
        }

        private static readonly Brush AccentFill = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x2C));

        private static bool Same(in VrRawState a, in VrRawState b)
            => Same(in a.Left, in b.Left) && Same(in a.Right, in b.Right);

        private static bool Same(in VrHandRaw a, in VrHandRaw b)
            => a.Buttons == b.Buttons && a.Trigger == b.Trigger && a.Grip == b.Grip
            && a.StickX == b.StickX && a.StickY == b.StickY;
    }
}
