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
    /// <para>Interaction matches the branded 2D previews: hover warms an
    /// element, clicking records it, the element under record flashes, and
    /// sticks use the drawn-view QUADRANT convention
    /// (ControllerModel2DView.StickHitArea_MouseMove /
    /// GetStickQuadrantClip): the stick's own highlight art, clipped to the
    /// half-disc under the pointer for an axis direction or to the center
    /// ellipse for the stick click, both on hover and while recording.
    /// No arrows; arrows are the schematic view's grammar, not the drawn
    /// packs'.</para>
    /// </summary>
    public partial class VRPreviewView : UserControl
    {
        /// <summary>Raised when the user clicks an element to map it.</summary>
        public event EventHandler<string> ControllerElementRecordRequested;

        // Base art size; every element position below is in these pixels.
        private const double ArtW = 975, ArtH = 726;

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
        // Stick element key -> the quadrant highlight: a second copy of the
        // stick's cyan overlay at the drawn packs' 0.4 hover opacity, shown
        // clipped to a direction half-disc or the click center ellipse
        // (ControllerModel2DView._stickHighlights, opacity and z-order
        // measured there).
        private readonly Dictionary<string, Image> _stickHighlights = new(StringComparer.Ordinal);

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

            bool isStick = el.Target.EndsWith("Stick", StringComparison.Ordinal);

            if (isStick)
            {
                // Second copy of the same art: the quadrant highlight. Added
                // after the overlay so it composites above it, matching the
                // reference's ZIndex ordering.
                var highlight = new Image
                {
                    Width = el.W, Height = el.H,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                    Opacity = 0.4,
                    Visibility = Visibility.Collapsed,
                };
                if (art != null) highlight.Source = art;
                Canvas.SetLeft(highlight, el.X); Canvas.SetTop(highlight, el.Y);
                VrCanvas.Children.Add(highlight);
                _stickHighlights[el.Target] = highlight;
            }

            var hit = new Rectangle
            {
                Width = el.W, Height = el.H,
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = displayName,
            };
            Canvas.SetLeft(hit, el.X); Canvas.SetTop(hit, el.Y);
            VrCanvas.Children.Add(hit);

            hit.MouseMove += (s, e) =>
            {
                _paintedValid = false;
                if (!isStick) { _hoverTarget = el.Target; return; }
                // Sticks skip the whole-element hover warm: the quadrant
                // highlight IS the hover affordance ("Sticks are always
                // visible - skip hover ghost" in the reference).
                var p = e.GetPosition(hit);
                string target = StickTargetAt(el.Target, p, el.W, el.H);
                bool left = el.Target.StartsWith("VrL", StringComparison.Ordinal);
                var stn = Strings.Instance;
                hit.ToolTip = target.EndsWith("Click", StringComparison.Ordinal)
                    ? (left ? stn.Btn_LeftStickButton : stn.Btn_RightStickButton)
                    : target.Contains("StickX")
                        ? (left ? stn.Btn_LeftStickX : stn.Btn_RightStickX)
                        : (left ? stn.Btn_LeftStickY : stn.Btn_RightStickY);
                // While the flash owns this stick's highlight, hover must not
                // fight it for the clip (the reference re-applies the flash
                // clip every tick for the same reason).
                if (FlashOwnsStick(el.Target)) return;
                if (_stickHighlights.TryGetValue(el.Target, out var hl))
                {
                    hl.Clip = StickClipFor(target, hl.Width, hl.Height);
                    hl.Visibility = Visibility.Visible;
                }
            };
            hit.MouseLeave += (s, e) =>
            {
                if (_hoverTarget == el.Target) _hoverTarget = null;
                if (isStick && !FlashOwnsStick(el.Target)
                    && _stickHighlights.TryGetValue(el.Target, out var hl))
                {
                    hl.Visibility = Visibility.Collapsed;
                    hl.Clip = null;
                }
                _paintedValid = false;
            };
            hit.MouseLeftButtonDown += (s, e) =>
            {
                string target = el.Target;
                if (isStick)
                    target = StickTargetAt(el.Target, e.GetPosition(hit), el.W, el.H);
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };
        }

        /// <summary>The recording target for a pointer position on a stick:
        /// the center region is the stick CLICK (reference:
        /// DetermineAxisFromQuadrant's dist &lt; 0.3 branch), the rest is the
        /// direction under the pointer. Down is positive Y in screen
        /// coordinates, inverted downstream exactly as the branded views'.</summary>
        private static string StickTargetAt(string stickKey, Point p, double w, double h)
        {
            double cx = w / 2, cy = h / 2;
            double dx = p.X - cx, dy = p.Y - cy;
            if (Math.Sqrt(dx * dx / (cx * cx) + dy * dy / (cy * cy)) < CenterR)
                return stickKey + "Click";
            return Math.Abs(dx) >= Math.Abs(dy)
                ? (dx >= 0 ? stickKey + "X" : stickKey + "XNeg")
                : (dy >= 0 ? stickKey + "Y" : stickKey + "YNeg");
        }

        // Center-region fraction of the stick radius that means "the click,
        // not a direction" (measured from the reference's centerR).
        private const double CenterR = 0.3;

        /// <summary>The quadrant clip for a stick axis or click target, the
        /// reference's GetStickQuadrantClip verbatim: click = the center
        /// ellipse, a direction = the half-disc minus that center.</summary>
        private static Geometry StickClipFor(string target, double w, double h)
        {
            double cx = w / 2, cy = h / 2;
            var full = new EllipseGeometry(new Point(cx, cy), cx, cy);
            var center = new EllipseGeometry(new Point(cx, cy), cx * CenterR, cy * CenterR);
            if (target.EndsWith("Click", StringComparison.Ordinal)) return center;

            bool neg = target.EndsWith("Neg", StringComparison.Ordinal);
            Rect half = target.Contains("StickX")
                ? (neg ? new Rect(0, 0, w / 2, h) : new Rect(cx, 0, w / 2, h))
                : (neg ? new Rect(0, 0, w, h / 2) : new Rect(0, cy, w, h / 2));

            var quadrant = new CombinedGeometry(GeometryCombineMode.Intersect,
                full, new RectangleGeometry(half));
            return new CombinedGeometry(GeometryCombineMode.Exclude, quadrant, center);
        }

        /// <summary>True when the element under record is one of this
        /// stick's axis directions or its click, i.e. the flash is driving
        /// this stick's quadrant highlight.</summary>
        private bool FlashOwnsStick(string stickKey)
            => _flashTarget != null
            && _flashTarget.StartsWith(stickKey, StringComparison.Ordinal)
            && _flashTarget.Length > stickKey.Length;

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
            // A target change always resets the stick highlights: the old
            // flash may have left one visible with its clip, and hover
            // re-shows its own as soon as the pointer moves.
            foreach (var hl in _stickHighlights.Values)
            {
                hl.Visibility = Visibility.Collapsed;
                hl.Clip = null;
            }
            if (string.IsNullOrEmpty(_flashTarget)) _flashTimer.Stop();
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
            // A stick axis/click under record flashes the QUADRANT highlight
            // only, never the whole stick overlay (the reference's FlashTick
            // returns after its quadrant branch for the same reason). The
            // EndsWith guard keeps this to sticks: FlashOwnsStick's
            // prefix-and-longer test would also match VrLTriggerClick
            // against the VrLTrigger element and eat that flash.
            if (flashElem != null
                && flashElem.EndsWith("Stick", StringComparison.Ordinal)
                && FlashOwnsStick(flashElem))
                flashElem = null;

            PaintHand(in vr.Left, "VrL", flashElem);
            PaintHand(in vr.Right, "VrR", flashElem);

            PaintStickFlash("VrLStick");
            PaintStickFlash("VrRStick");
        }

        /// <summary>Drives a stick's quadrant highlight while one of its
        /// directions (or its click) is under record: clip from the target
        /// name, visibility from the flash phase.</summary>
        private void PaintStickFlash(string stickKey)
        {
            if (!FlashOwnsStick(stickKey)) return;
            if (!_stickHighlights.TryGetValue(stickKey, out var hl)) return;
            hl.Clip = StickClipFor(_flashTarget, hl.Width, hl.Height);
            hl.Visibility = _flashOn ? Visibility.Visible : Visibility.Collapsed;
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

        private static bool Same(in VrRawState a, in VrRawState b)
            => Same(in a.Left, in b.Left) && Same(in a.Right, in b.Right);

        private static bool Same(in VrHandRaw a, in VrHandRaw b)
            => a.Buttons == b.Buttons && a.Trigger == b.Trigger && a.Grip == b.Grip
            && a.StickX == b.StickX && a.StickY == b.StickY;
    }
}
