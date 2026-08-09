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
    /// element, clicking records it, and the element under record flashes.
    /// Elements that carry MORE THAN ONE mapping target use the drawn-view
    /// REGION convention (ControllerModel2DView's stick quadrants and
    /// touchpad click strip): the element's own highlight art clipped to
    /// the region under the pointer, both on hover and while recording.
    /// Sticks split center (click) from direction half-discs, A/B split an
    /// inner press disc from the outer touch ring, trigger and grip split
    /// the body (axis) from a tip band (click). No arrows; arrows are the
    /// schematic view's grammar, not the drawn packs'.</para>
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
            // Trigger rects are the whole HOUSING (outer bezel included),
            // not just the blade face: the face alone read as "just the
            // front edge" of the trigger. The housing's flood also fuses to
            // the body seam curving past it, so the cutout opens that thin
            // arc away before use.
            new("VRController_L_Trigger", 295, 360, 89, 99, "VrLTrigger"),
            new("VRController_L_Grip",    416, 396, 50, 231,  "VrLGrip"),
            new("VRController_R_Stick",   680, 107, 64,  64,  "VrRStick"),
            new("VRController_R_A",       796, 181, 61,  61,  "VrRA"),
            new("VRController_R_B",       814,  77, 62,  62,  "VrRB"),
            new("VRController_R_System",  733, 246, 42,  44,  "VrRSystem"),
            new("VRController_R_Trigger", 592, 360, 89, 99, "VrRTrigger"),
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
        // Multi-target element key -> the region highlight: a second copy
        // of the element's cyan overlay at the drawn packs' 0.4 hover
        // opacity, shown clipped to the region under the pointer or under
        // record (ControllerModel2DView._stickHighlights, opacity and
        // z-order measured there). Every element except System carries
        // more than one target, so every one of them gets a highlight.
        private readonly Dictionary<string, Image> _regionHighlights = new(StringComparer.Ordinal);

        // Stick caps translate with deflection like every branded preview's
        // stick ring (ControllerModel2DView: always-visible image + a
        // TranslateTransform driven from the raw axes). The cap art is the
        // stick's pixels cut from the base (the base keeps an inpainted
        // socket underneath), and the stick's cyan overlay + region
        // highlight share the same transform so they ride the cap.
        // 14 px ~= the branded 25-per-~100px-ring travel ratio at 64 px.
        private const double StickTravel = 14;
        private TranslateTransform _lStickT, _rStickT;

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

            _lStickT = AddStickCap("L", 231, 107, 64, 64);
            _rStickT = AddStickCap("R", 680, 107, 64, 64);

            var st = Strings.Instance;
            foreach (var el in Elements)
                AddElement(el, DisplayName(el.Target, st));

            _built = true;
        }

        /// <summary>The movable stick-cap layer: above the base (and its
        /// socket), below the element overlays added afterwards.</summary>
        private TranslateTransform AddStickCap(string side, double x, double y, double w, double h)
        {
            var t = new TranslateTransform();
            var art = EmbeddedBitmaps.Load($"2DModels/VRCONTROLLER/VRController_{side}_StickCap.png");
            if (art == null) return t;
            var img = new Image
            {
                Source = art, Width = w, Height = h,
                Stretch = Stretch.Fill, IsHitTestVisible = false,
                RenderTransform = t,
            };
            Canvas.SetLeft(img, x); Canvas.SetTop(img, y);
            VrCanvas.Children.Add(img);
            return t;
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

            bool isMulti = !el.Target.EndsWith("System", StringComparison.Ordinal);
            bool isStick = el.Target.EndsWith("Stick", StringComparison.Ordinal);
            if (isStick)
                overlay.RenderTransform =
                    el.Target.StartsWith("VrL", StringComparison.Ordinal) ? _lStickT : _rStickT;

            if (isMulti)
            {
                // Second copy of the same art: the region highlight. Added
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
                // The region highlight does NOT ride the stick cap, and the
                // reference's stick highlights carry no transform either. Its
                // clip is computed from a pointer position measured against
                // the STATIONARY hit rect, so translating the highlight put
                // the lit quadrant up to StickTravel px away from the pointer
                // that chose it whenever the stick was deflected.
                VrCanvas.Children.Add(highlight);
                _regionHighlights[el.Target] = highlight;
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
                if (!isMulti) { _hoverTarget = el.Target; return; }
                // Multi-target elements skip the whole-element hover warm:
                // the region highlight IS the hover affordance ("Sticks are
                // always visible - skip hover ghost" in the reference).
                string target = RegionTargetAt(el.Target, e.GetPosition(hit), el.W, el.H);
                hit.ToolTip = DisplayName(target, Strings.Instance);
                // While the flash owns this element's highlight, hover must
                // not fight it for the clip (the reference re-applies the
                // flash clip every tick for the same reason).
                if (FlashOwnsRegion(el.Target)) return;
                if (_regionHighlights.TryGetValue(el.Target, out var hl))
                {
                    hl.Clip = RegionClipFor(target, hl.Width, hl.Height);
                    hl.Visibility = Visibility.Visible;
                }
            };
            hit.MouseLeave += (s, e) =>
            {
                if (_hoverTarget == el.Target) _hoverTarget = null;
                if (isMulti && !FlashOwnsRegion(el.Target)
                    && _regionHighlights.TryGetValue(el.Target, out var hl))
                {
                    hl.Visibility = Visibility.Collapsed;
                    hl.Clip = null;
                }
                _paintedValid = false;
            };
            hit.MouseLeftButtonDown += (s, e) =>
            {
                string target = el.Target;
                if (isMulti)
                    target = RegionTargetAt(el.Target, e.GetPosition(hit), el.W, el.H);
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };
        }

        // Center-region fraction of the stick radius that means "the click,
        // not a direction" (measured from the reference's centerR).
        private const double CenterR = 0.3;
        // A/B buttons: inner fraction that records the PRESS; the ring
        // outside it records the TOUCH sensor.
        private const double PressR = 0.6;
        // Trigger/grip: bottom fraction of the art that records the CLICK
        // (the end-of-travel detent lives at the tip); the body above it
        // records the analog pull.
        private const double ClickBand = 0.3;

        /// <summary>The recording target for a pointer position on a
        /// multi-target element. Sticks: center = click (reference:
        /// DetermineAxisFromQuadrant's dist &lt; 0.3 branch), else the
        /// direction under the pointer, down = positive Y in screen
        /// coordinates inverted downstream exactly as the branded views'.
        /// A/B: inner disc = press, outer ring = touch. Trigger/grip:
        /// body = axis, bottom band = click.</summary>
        private static string RegionTargetAt(string elemKey, Point p, double w, double h)
        {
            if (elemKey.EndsWith("Stick", StringComparison.Ordinal))
            {
                double cx = w / 2, cy = h / 2;
                double dx = p.X - cx, dy = p.Y - cy;
                if (Math.Sqrt(dx * dx / (cx * cx) + dy * dy / (cy * cy)) < CenterR)
                    return elemKey + "Click";
                return Math.Abs(dx) >= Math.Abs(dy)
                    ? (dx >= 0 ? elemKey + "X" : elemKey + "XNeg")
                    : (dy >= 0 ? elemKey + "Y" : elemKey + "YNeg");
            }
            if (elemKey.EndsWith("A", StringComparison.Ordinal)
                || elemKey.EndsWith("B", StringComparison.Ordinal))
            {
                double cx = w / 2, cy = h / 2;
                double dx = p.X - cx, dy = p.Y - cy;
                return Math.Sqrt(dx * dx / (cx * cx) + dy * dy / (cy * cy)) < PressR
                    ? elemKey : elemKey + "Touch";
            }
            // Trigger / grip.
            return p.Y >= h * (1 - ClickBand) ? elemKey + "Click" : elemKey;
        }

        /// <summary>The region clip for any multi-target element's target,
        /// the reference's GetStickQuadrantClip generalized. Stick click =
        /// center ellipse, stick direction = half-disc minus that center,
        /// A/B press = inner disc, touch = the ring outside it, trigger/
        /// grip axis = the body, their click = the bottom band.</summary>
        private static Geometry RegionClipFor(string target, double w, double h)
        {
            double cx = w / 2, cy = h / 2;
            if (target.Contains("Stick"))
            {
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
            if (target.EndsWith("Touch", StringComparison.Ordinal))
                return new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(new Point(cx, cy), cx, cy),
                    new EllipseGeometry(new Point(cx, cy), cx * PressR, cy * PressR));
            if (target.EndsWith("A", StringComparison.Ordinal)
                || target.EndsWith("B", StringComparison.Ordinal))
                return new EllipseGeometry(new Point(cx, cy), cx * PressR, cy * PressR);
            if (target.EndsWith("Click", StringComparison.Ordinal))
                return new RectangleGeometry(new Rect(0, h * (1 - ClickBand), w, h * ClickBand));
            // Trigger / grip axis body.
            return new RectangleGeometry(new Rect(0, 0, w, h * (1 - ClickBand)));
        }

        /// <summary>True when the target under record belongs to this
        /// multi-target element, i.e. the flash is driving this element's
        /// region highlight rather than its whole-element overlay.</summary>
        private bool FlashOwnsRegion(string elemKey)
            => _flashTarget != null && ElementKeyFor(_flashTarget) == elemKey;

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
            // Region targets, named with the same strings as their
            // mapping-grid rows so the tooltip and the grid agree.
            "VrLStickClick" => st.Btn_LeftStickButton,
            "VrRStickClick" => st.Btn_RightStickButton,
            "VrLStickX" or "VrLStickXNeg" => st.Btn_LeftStickX,
            "VrLStickY" or "VrLStickYNeg" => st.Btn_LeftStickY,
            "VrRStickX" or "VrRStickXNeg" => st.Btn_RightStickX,
            "VrRStickY" or "VrRStickYNeg" => st.Btn_RightStickY,
            "VrLATouch" => st.Btn_VrLeftATouch,
            "VrLBTouch" => st.Btn_VrLeftBTouch,
            "VrRATouch" => st.Btn_VrRightATouch,
            "VrRBTouch" => st.Btn_VrRightBTouch,
            "VrLTriggerClick" => st.Btn_VrLeftTriggerClick,
            "VrRTriggerClick" => st.Btn_VrRightTriggerClick,
            "VrLGripClick" => st.Btn_VrLeftGripClick,
            "VrRGripClick" => st.Btn_VrRightGripClick,
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
            // Start LIT, not dark. The reference fires its first tick
            // synchronously (ControllerModel2DView drives FlashTick once
            // before the timer starts); seeding false here left the element
            // the user had just clicked showing nothing for a full timer
            // interval, with its hover highlight already collapsed below.
            _flashOn = true;
            // A target change always resets the region highlights: the old
            // flash may have left one visible with its clip, and hover
            // re-shows its own as soon as the pointer moves.
            foreach (var hl in _regionHighlights.Values)
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
            // A multi-target element's target under record flashes its
            // REGION highlight only, never the whole-element overlay (the
            // reference's FlashTick returns after its quadrant branch for
            // the same reason): the region is what distinguishes A from
            // ATouch and Trigger from TriggerClick.
            if (flashElem != null && _regionHighlights.ContainsKey(flashElem))
                flashElem = null;

            PaintHand(in vr.Left, "VrL", flashElem);
            PaintHand(in vr.Right, "VrR", flashElem);

            // Stick caps ride the raw axes. VrRawState keeps the SDL screen
            // frame (positive Y = down; the OpenVR Y-up flip happens at
            // PackHand), so unlike the XInput previews no negation here.
            if (_lStickT != null)
            {
                _lStickT.X = vr.Left.StickX / 32767.0 * StickTravel;
                _lStickT.Y = vr.Left.StickY / 32767.0 * StickTravel;
            }
            if (_rStickT != null)
            {
                _rStickT.X = vr.Right.StickX / 32767.0 * StickTravel;
                _rStickT.Y = vr.Right.StickY / 32767.0 * StickTravel;
            }

            foreach (var key in _regionHighlights.Keys)
                PaintRegionFlash(key);
        }

        /// <summary>Drives an element's region highlight while one of its
        /// targets is under record: clip from the target name, visibility
        /// from the flash phase.</summary>
        private void PaintRegionFlash(string elemKey)
        {
            if (!FlashOwnsRegion(elemKey)) return;
            if (!_regionHighlights.TryGetValue(elemKey, out var hl)) return;
            hl.Clip = RegionClipFor(_flashTarget, hl.Width, hl.Height);
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
            // as half-lit rather than binary. The CLICK bit pins the pull to
            // full: a source bound to VrLTriggerClick with VrLTrigger left
            // unmapped drives the click alone, and passing its zero analog
            // through rendered the press at 0.4, which is exactly the hover
            // opacity, so a real press was indistinguishable from the
            // pointer resting on the trigger.
            SetOverlay(side + "Trigger", trg, flashElem, PullFor(hand.Buttons, 0x20, hand.Trigger));
            SetOverlay(side + "Grip", grp, flashElem, PullFor(hand.Buttons, 0x40, hand.Grip));
        }

        /// <summary>How far an analog element reads as pulled, 0..1. The
        /// CLICK bit pins it to full: a source bound to VrLTriggerClick with
        /// VrLTrigger left unmapped drives the click alone, and passing its
        /// zero analog through rendered the press at the 0.4 that
        /// <see cref="SetOverlay"/> also uses for hover, so a real press was
        /// indistinguishable from the pointer resting on the trigger.
        /// Internal for the test seam (InternalsVisibleTo PadForge.Tests):
        /// the rest of this view has no callable surface.</summary>
        internal static double PullFor(byte buttons, byte clickBit, short analog)
            => (buttons & clickBit) != 0 ? 1.0 : analog / 32767.0;

        private void SetOverlay(string key, bool lit, string flashElem, double analog = -1)
        {
            if (!_overlays.TryGetValue(key, out var img)) return;

            // Recording beats everything, then the live state, then hover.
            if (flashElem == key) { img.Opacity = _flashOn ? 1.0 : 0.0; return; }
            // A multi-target element under record shows its clipped REGION
            // and nothing else. Without this the live-state branch below wins
            // (flashElem is deliberately null for these, so the guard above
            // cannot fire) and paints the WHOLE element at up to full
            // opacity, drowning the 0.4 region the flash is drawing: record
            // a trigger that is already mapped, pull it, and the record
            // indication vanishes under its own live state. The reference
            // blocks the same collision with an early return keyed on its
            // flash target (ControllerModel2DView.SetOverlayVisible).
            if (FlashOwnsRegion(key)) { img.Opacity = 0.0; return; }
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
