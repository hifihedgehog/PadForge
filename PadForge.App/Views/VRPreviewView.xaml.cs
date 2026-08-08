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
    /// Live preview for a VR slot (#49), drawn from the 2D art in
    /// <c>2DModels/VRCONTROLLER/</c> the same way ControllerModel2DView
    /// draws the branded pads: one base bitmap, then a per-element tint
    /// layer composited on top when that element is lit, hovered, or under
    /// record.
    ///
    /// <para>One VR slot drives BOTH SteamVR hands, so the art is the pair
    /// side by side rather than a single controller body.</para>
    ///
    /// <para>Elements are tinted with the Rectangle + ImageBrush
    /// OpacityMask idiom (see [[feedback_controller_art_from_2dmodels_pipeline]]):
    /// the cutout supplies the shape, one brush supplies the colour, so
    /// lit / hover / flash all drive the same layer instead of needing a
    /// second "-Active" bitmap per element.</para>
    ///
    /// <para>Interaction matches the branded and Extended previews: hover
    /// warms an element, clicking records it, the element under record
    /// flashes, and hovering a stick shows a DIRECTION ARROW for the
    /// quadrant under the pointer so the axis direction being bound is
    /// visible before the click.</para>
    /// </summary>
    public partial class VRPreviewView : UserControl
    {
        /// <summary>Raised when the user clicks an element to map it.</summary>
        public event EventHandler<string> ControllerElementRecordRequested;

        // Base art size; every element position below is in these pixels.
        private const double ArtW = 975, ArtH = 726;

        private static readonly Brush LitBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x2C));
        private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA2, 0x4D));
        private static readonly Brush FlashBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));

        /// <summary>One element: its art, where it sits on the base, and
        /// the mapping target it records. Sticks carry the four
        /// directional targets instead of one.</summary>
        private sealed record Elem(string File, double X, double Y, double W, double H, string Target);

        private static readonly Elem[] Elements =
        {
            new("VRController_L_Stick",   231, 107, 64,  64,  "VrLStick"),
            new("VRController_L_A",       119, 181, 61,  61,  "VrLA"),
            new("VRController_L_B",        99,  77, 62,  62,  "VrLB"),
            new("VRController_L_System",  200, 246, 43,  44,  "VrLSystem"),
            new("VRController_L_Trigger", 297, 342, 79, 106,  "VrLTrigger"),
            new("VRController_L_Grip",    416, 396, 50, 231,  "VrLGrip"),
            new("VRController_R_Stick",   680, 107, 64,  64,  "VrRStick"),
            new("VRController_R_A",       796, 181, 61,  61,  "VrRA"),
            new("VRController_R_B",       814,  77, 62,  62,  "VrRB"),
            new("VRController_R_System",  733, 246, 42,  44,  "VrRSystem"),
            new("VRController_R_Trigger", 603, 342, 75, 106,  "VrRTrigger"),
            new("VRController_R_Grip",    510, 396, 50, 230,  "VrRGrip"),
        };

        private PadViewModel _vm;
        private bool _built;
        private VrRawState _painted;
        private bool _paintedValid;

        private string _flashTarget;
        private bool _flashOn;
        private System.Windows.Threading.DispatcherTimer _flashTimer;
        private string _hoverTarget;

        // target -> the tint layer that colours it.
        private readonly Dictionary<string, Image> _overlays = new(StringComparer.Ordinal);
        private Polygon _lArrow, _rArrow;
        private Canvas _lArrowHost, _rArrowHost;

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

        /// <summary>Binds the pad ViewModel (sibling-preview contract).</summary>
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
            VrCanvas.Width = ArtW;
            VrCanvas.Height = ArtH;

            var baseBmp = EmbeddedBitmaps.Load("2DModels/VRCONTROLLER/VRController_base.png");
            if (baseBmp != null)
            {
                var img = new Image
                {
                    Source = baseBmp, Width = ArtW, Height = ArtH,
                    Stretch = Stretch.Fill, IsHitTestVisible = false,
                };
                Canvas.SetLeft(img, 0); Canvas.SetTop(img, 0);
                VrCanvas.Children.Add(img);
            }

            var st = Strings.Instance;
            foreach (var el in Elements)
                AddElement(el, DisplayName(el.Target, st));

            _lArrowHost = AddStickArrow(231, 107, 64, out _lArrow);
            _rArrowHost = AddStickArrow(680, 107, 64, out _rArrow);

            _built = true;
        }

        private void AddElement(Elem el, string displayName)
        {
            // The overlay is the pack's highlight ART (cyan fill at half
            // alpha inside a solid cyan stroke), shown by OPACITY exactly
            // as ControllerModel2DView shows its element overlays: 0.4 for
            // hover, full when the element is active.
            var art = EmbeddedBitmaps.Load($"2DModels/VRCONTROLLER/{el.File}.png");
            var overlay = new Image
            {
                Width = el.W, Height = el.H,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                Opacity = 0,
            };
            if (art != null) overlay.Source = art;
            Canvas.SetLeft(overlay, el.X); Canvas.SetTop(overlay, el.Y);
            VrCanvas.Children.Add(overlay);
            _overlays[el.Target] = overlay;

            var hit = new Rectangle
            {
                Width = el.W, Height = el.H,
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = displayName,
            };
            Canvas.SetLeft(hit, el.X); Canvas.SetTop(hit, el.Y);
            VrCanvas.Children.Add(hit);

            bool isStick = el.Target.EndsWith("Stick", StringComparison.Ordinal);

            hit.MouseMove += (s, e) =>
            {
                _hoverTarget = el.Target;
                _paintedValid = false;
                if (!isStick) return;
                var p = e.GetPosition(hit);
                double hx = p.X - el.W / 2, hy = p.Y - el.H / 2;
                bool horiz = Math.Abs(hx) > Math.Abs(hy);
                double angle = horiz ? (hx > 0 ? 90 : 270) : (hy > 0 ? 180 : 0);
                bool left = el.Target.StartsWith("VrL", StringComparison.Ordinal);
                var host = left ? _lArrowHost : _rArrowHost;
                var arrow = left ? _lArrow : _rArrow;
                if (_flashTarget == null && host != null)
                {
                    host.RenderTransform = new RotateTransform(angle, 32, 32);
                    arrow.Fill = HoverBrush;
                    arrow.Visibility = Visibility.Visible;
                }
                var stn = Strings.Instance;
                hit.ToolTip = horiz ? (left ? stn.Btn_LeftStickX : stn.Btn_RightStickX)
                                    : (left ? stn.Btn_LeftStickY : stn.Btn_RightStickY);
            };
            hit.MouseLeave += (s, e) =>
            {
                if (_hoverTarget == el.Target) _hoverTarget = null;
                if (isStick && _flashTarget == null)
                {
                    if (el.Target.StartsWith("VrL", StringComparison.Ordinal)) _lArrow.Visibility = Visibility.Collapsed;
                    else _rArrow.Visibility = Visibility.Collapsed;
                }
                _paintedValid = false;
            };
            hit.MouseLeftButtonDown += (s, e) =>
            {
                string target = el.Target;
                if (isStick)
                {
                    var p = e.GetPosition(hit);
                    double cx = p.X - el.W / 2, cy = p.Y - el.H / 2;
                    string axis = el.Target.StartsWith("VrL", StringComparison.Ordinal) ? "VrL" : "VrR";
                    target = Math.Abs(cx) > Math.Abs(cy)
                        ? (cx > 0 ? axis + "StickX" : axis + "StickXNeg")
                        : (cy > 0 ? axis + "StickY" : axis + "StickYNeg");
                }
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };
        }

        private Canvas AddStickArrow(double x, double y, double size, out Polygon arrow)
        {
            double len = size * 0.9, baseW = 7;
            arrow = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(size / 2, size / 2 - len),
                    new Point(size / 2 - baseW, size / 2 - len * 0.62),
                    new Point(size / 2 + baseW, size / 2 - len * 0.62),
                },
                Fill = HoverBrush,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            var host = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
            host.Children.Add(arrow);
            Canvas.SetLeft(host, x); Canvas.SetTop(host, y);
            VrCanvas.Children.Add(host);
            return host;
        }

        private static string DisplayName(string target, Strings st) => target switch
        {
            "VrLStick" => st.Vr_LeftHand + " " + st.Vr_Stick,
            "VrRStick" => st.Vr_RightHand + " " + st.Vr_Stick,
            "VrLA" => st.Btn_VrLeftA,
            "VrLB" => st.Btn_VrLeftB,
            "VrLSystem" => st.Btn_VrLeftSystem,
            "VrLTrigger" => st.Btn_LeftTrigger,
            "VrLGrip" => st.Btn_VrLeftGrip,
            "VrRA" => st.Btn_VrRightA,
            "VrRB" => st.Btn_VrRightB,
            "VrRSystem" => st.Btn_VrRightSystem,
            "VrRTrigger" => st.Btn_RightTrigger,
            "VrRGrip" => st.Btn_VrRightGrip,
            _ => target,
        };

        // ─────────────────────────────────────────────
        //  Recording flash
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            if (_flashTimer == null)
            {
                _flashTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(450) };
                _flashTimer.Tick += (s, e) => { _flashOn = !_flashOn; _paintedValid = false; };
            }
            _flashTarget = target;
            _flashOn = false;
            if (string.IsNullOrEmpty(_flashTarget))
            {
                _flashTimer.Stop();
                if (_lArrow != null) _lArrow.Visibility = Visibility.Collapsed;
                if (_rArrow != null) _rArrow.Visibility = Visibility.Collapsed;
            }
            else _flashTimer.Start();
            _paintedValid = false;
        }

        /// <summary>Maps a recording target back to the element that owns
        /// it: the four stick directions all belong to that hand's stick.</summary>
        private static string ElementKeyFor(string target)
        {
            if (target == null) return null;
            if (target.StartsWith("VrLStick", StringComparison.Ordinal) && !target.EndsWith("Click", StringComparison.Ordinal))
                return "VrLStick";
            if (target.StartsWith("VrRStick", StringComparison.Ordinal) && !target.EndsWith("Click", StringComparison.Ordinal))
                return "VrRStick";
            // A stick CLICK lights the stick too, since that is the part
            // the user presses.
            if (target == "VrLStickClick") return "VrLStick";
            if (target == "VrRStickClick") return "VrRStick";
            // Trigger/grip clicks light their analog element.
            if (target == "VrLTriggerClick") return "VrLTrigger";
            if (target == "VrRTriggerClick") return "VrRTrigger";
            if (target == "VrLGripClick") return "VrLGrip";
            if (target == "VrRGripClick") return "VrRGrip";
            // Touch targets light their button.
            if (target == "VrLATouch") return "VrLA";
            if (target == "VrRATouch") return "VrRA";
            if (target == "VrLBTouch") return "VrLB";
            if (target == "VrRBTouch") return "VrRB";
            return target;
        }

        // ─────────────────────────────────────────────
        //  Rendering
        // ─────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
            // Retained-page guard, matching the sibling previews.
            if (!IsVisible || Common.AmbientMotionProbe.Instance.IsWindowMinimized) return;
            if (!_built) return;

            var vr = _vm?.VrOutputSnapshot ?? default;
            if (_paintedValid && Same(in vr, in _painted)) return;
            _painted = vr;
            _paintedValid = true;

            string flashElem = ElementKeyFor(_flashTarget);

            PaintHand(in vr.Left, "VrL", flashElem);
            PaintHand(in vr.Right, "VrR", flashElem);

            // Stick direction arrow follows the target under record.
            PaintStickArrow(_flashTarget, "VrL", _lArrowHost, _lArrow);
            PaintStickArrow(_flashTarget, "VrR", _rArrowHost, _rArrow);
        }

        private void PaintHand(in VrHandRaw hand, string side, string flashElem)
        {
            // Bit order is HMVRButton's: System, A, ATouch, B, BTouch,
            // TriggerClick, GripClick, StickClick.
            bool sys = (hand.Buttons & 0x01) != 0;
            bool a = (hand.Buttons & 0x02) != 0 || (hand.Buttons & 0x04) != 0;
            bool b = (hand.Buttons & 0x08) != 0 || (hand.Buttons & 0x10) != 0;
            bool trg = (hand.Buttons & 0x20) != 0 || hand.Trigger > 0;
            bool grp = (hand.Buttons & 0x40) != 0 || hand.Grip > 0;
            bool stk = (hand.Buttons & 0x80) != 0
                     || Math.Abs((int)hand.StickX) > 3000 || Math.Abs((int)hand.StickY) > 3000;

            SetOverlay(side + "System", sys, flashElem);
            SetOverlay(side + "A", a, flashElem);
            SetOverlay(side + "B", b, flashElem);
            SetOverlay(side + "Stick", stk, flashElem);
            // Analog elements fade with their pull, so a half-squeeze reads
            // as half-lit rather than binary.
            SetOverlay(side + "Trigger", trg, flashElem, hand.Trigger / 32767.0);
            SetOverlay(side + "Grip", grp, flashElem, hand.Grip / 32767.0);
        }

        private void SetOverlay(string key, bool lit, string flashElem, double analog = -1)
        {
            if (!_overlays.TryGetValue(key, out var img)) return;

            // Recording beats everything, then the live state, then hover.
            if (flashElem == key) { img.Opacity = _flashOn ? 1.0 : 0.0; return; }
            if (lit)
            {
                // Analog elements track their pull, so a half squeeze reads
                // as half lit instead of binary.
                img.Opacity = analog >= 0 ? 0.4 + 0.6 * Math.Clamp(analog, 0, 1) : 1.0;
                return;
            }
            // 0.4 is the pack's hover convention (ControllerModel2DView).
            img.Opacity = _hoverTarget == key ? 0.4 : 0.0;
        }

        private void PaintStickArrow(string target, string side, Canvas host, Polygon arrow)
        {
            if (host == null || arrow == null) return;
            if (target == null || !target.StartsWith(side + "Stick", StringComparison.Ordinal)
                || target.EndsWith("Click", StringComparison.Ordinal))
                return;
            double angle = target.Contains("StickX")
                ? (target.EndsWith("Neg", StringComparison.Ordinal) ? 270 : 90)
                : (target.EndsWith("Neg", StringComparison.Ordinal) ? 0 : 180);
            host.RenderTransform = new RotateTransform(angle, 32, 32);
            arrow.Fill = FlashBrush;
            arrow.Visibility = _flashOn ? Visibility.Visible : Visibility.Collapsed;
        }

        private static bool Same(in VrRawState a, in VrRawState b)
            => Same(in a.Left, in b.Left) && Same(in a.Right, in b.Right);

        private static bool Same(in VrHandRaw a, in VrHandRaw b)
            => a.Buttons == b.Buttons && a.Trigger == b.Trigger && a.Grip == b.Grip
            && a.StickX == b.StickX && a.StickY == b.StickY;
    }
}
