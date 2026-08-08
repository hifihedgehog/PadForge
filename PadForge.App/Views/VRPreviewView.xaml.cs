using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Engine;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// Live preview for a VR slot (#49): both SteamVR hands, their button
    /// state, stick position, and trigger/grip pull.
    ///
    /// <para>The VR slot drives BOTH hands from one virtual controller, so
    /// unlike the gamepad previews there is no single controller body to
    /// draw. Two panels are the honest shape.</para>
    ///
    /// <para>Reads <see cref="PadViewModel.VrOutputSnapshot"/>, a plain
    /// auto-property written by the 30 Hz push, and repaints only when the
    /// state actually moved. Same contract and the same
    /// CompositionTarget.Rendering + visibility gate as KBMPreviewView,
    /// including the minimized guard (IsVisible stays true when
    /// iconic).</para>
    /// </summary>
    public partial class VRPreviewView : UserControl
    {
        private PadViewModel _vm;
        private bool _built;
        private VrRawState _painted;
        private bool _paintedValid;

        // Pills in HMVRButton BIT ORDER, so index i is bit 1 << i and the
        // paint loop is a shift. Same ordering contract VrLayout's key
        // arrays carry.
        private readonly Border[] _leftPills = new Border[8];
        private readonly Border[] _rightPills = new Border[8];

        public VRPreviewView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => { _vm = DataContext as PadViewModel; _paintedValid = false; };
            Loaded += (s, e) =>
            {
                _vm = DataContext as PadViewModel;
                BuildPills();
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
            };
            Unloaded += (s, e) => CompositionTarget.Rendering -= OnRendering;
        }

        /// <summary>Short pill captions. Index = HMVRButton bit position.
        /// Kept terse because eight pills share one column.</summary>
        private static string[] PillCaptions
        {
            get
            {
                // Fully qualified: inside a UserControl the bare name
                // "Resources" binds to FrameworkElement.Resources, not the
                // strings namespace.
                var s = PadForge.Resources.Strings.Strings.Instance;
                return new[] { s.Vr_Sys, "A", s.Vr_ATouch, "B", s.Vr_BTouch, s.Vr_Trg, s.Vr_Grip, s.Vr_Stick };
            }
        }

        private void BuildPills()
        {
            if (_built) return;
            var captions = PillCaptions;
            for (int i = 0; i < 8; i++)
            {
                _leftPills[i] = MakePill(captions[i]);
                LeftButtonPanel.Children.Add(_leftPills[i]);
                _rightPills[i] = MakePill(captions[i]);
                RightButtonPanel.Children.Add(_rightPills[i]);
            }
            _built = true;
        }

        private Border MakePill(string caption)
        {
            var text = new TextBlock { Text = caption };
            text.SetResourceReference(StyleProperty, "VrPillText");
            var b = new Border { Child = text };
            b.SetResourceReference(StyleProperty, "VrPill");
            return b;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            // Retained-page guard, matching KBMPreviewView: skip every
            // per-frame read while hidden or minimized.
            if (!IsVisible || Common.AmbientMotionProbe.Instance.IsWindowMinimized) return;
            if (_vm == null || !_built) return;

            var vr = _vm.VrOutputSnapshot;
            if (_paintedValid && Same(in vr, in _painted)) return;
            _painted = vr;
            _paintedValid = true;

            PaintHand(in vr.Left, _leftPills, LeftStickDot, LeftTriggerBar, LeftGripBar);
            PaintHand(in vr.Right, _rightPills, RightStickDot, RightTriggerBar, RightGripBar);
        }

        private void PaintHand(in VrHandRaw hand, Border[] pills,
            System.Windows.Shapes.Ellipse dot,
            System.Windows.Shapes.Rectangle triggerBar,
            System.Windows.Shapes.Rectangle gripBar)
        {
            for (int i = 0; i < pills.Length; i++)
            {
                bool lit = (hand.Buttons & (1 << i)) != 0;
                pills[i].SetResourceReference(Border.BackgroundProperty,
                    lit ? "EmberSegGradient" : "SegTrackBrush");
                ((TextBlock)pills[i].Child).SetResourceReference(TextBlock.ForegroundProperty,
                    lit ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
            }

            // Stick: the canvas is 96 px with a 16 px dot, so the dot's
            // travel is 40 px from centre in each direction. The snapshot
            // is the pipeline's SDL-native frame (Y positive = down),
            // which is also screen-Y, so neither axis is flipped here.
            const double half = 40.0;
            double nx = hand.StickX >= 0 ? hand.StickX / 32767.0 : hand.StickX / 32768.0;
            double ny = hand.StickY >= 0 ? hand.StickY / 32767.0 : hand.StickY / 32768.0;
            Canvas.SetLeft(dot, 40 + nx * half);
            Canvas.SetTop(dot, 40 + ny * half);

            triggerBar.Width = 160.0 * Math.Clamp(hand.Trigger / 32767.0, 0, 1);
            gripBar.Width = 160.0 * Math.Clamp(hand.Grip / 32767.0, 0, 1);
        }

        private static bool Same(in VrRawState a, in VrRawState b)
            => Same(in a.Left, in b.Left) && Same(in a.Right, in b.Right);

        private static bool Same(in VrHandRaw a, in VrHandRaw b)
            => a.Buttons == b.Buttons && a.Trigger == b.Trigger && a.Grip == b.Grip
            && a.StickX == b.StickX && a.StickY == b.StickY;
    }
}
