using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Engine;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// Live preview for a VR slot (#49): both SteamVR hands, their button
    /// state, stick position, and trigger/grip pull.
    ///
    /// <para>One VR slot drives BOTH hands, so unlike the gamepad previews
    /// there is no single controller body to draw. Two panels are the
    /// honest shape.</para>
    ///
    /// <para>Every element is a mapping target: clicking one raises
    /// <see cref="ControllerElementRecordRequested"/> with its target name,
    /// the same contract KBMPreviewView / ControllerSchematicView use, and
    /// the element that is currently recording pulses so the user can see
    /// which one the grid is waiting on.</para>
    ///
    /// <para>Reads <see cref="PadViewModel.VrOutputSnapshot"/>, a plain
    /// auto-property written by the 30 Hz push, and repaints only when the
    /// state moved. Same CompositionTarget.Rendering + visibility gate as
    /// KBMPreviewView, including the minimized guard (IsVisible stays true
    /// when iconic).</para>
    /// </summary>
    public partial class VRPreviewView : UserControl
    {
        /// <summary>Raised when the user clicks an element to map it.</summary>
        public event EventHandler<string> ControllerElementRecordRequested;

        private PadViewModel _vm;
        private bool _built;
        private VrRawState _painted;
        private bool _paintedValid;
        private string _flashTarget;
        private bool _flashOn;
        private System.Windows.Threading.DispatcherTimer _flashTimer;

        // Indexed by HMVRButton BIT POSITION, so index i is bit 1 << i.
        // Same ordering contract VrLayout's key arrays carry.
        private readonly Border[] _leftPills = new Border[8];
        private readonly Border[] _rightPills = new Border[8];

        private Ellipse _leftDot, _rightDot;
        private readonly Dictionary<string, Rectangle> _bars = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Border> _barHosts = new(StringComparer.Ordinal);
        private Ellipse _leftStickFace, _rightStickFace;

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

        /// <summary>Binds the pad ViewModel, mirroring the other previews'
        /// Bind(vm) entry point so PadPage wires them all the same way.</summary>
        public void Bind(PadViewModel vm)
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = vm;
            if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
            _paintedValid = false;
            UpdateFlashTarget(_vm?.CurrentRecordingTarget);
        }

        /// <summary>Drops the ViewModel subscription, matching the other
        /// previews' Unbind() so PadPage's "unbind all first" pass covers
        /// this view too.</summary>
        public void Unbind()
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
            _paintedValid = false;
            _flashTimer?.Stop();
            _flashTarget = null;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.CurrentRecordingTarget))
                Dispatcher.Invoke(() => UpdateFlashTarget(_vm?.CurrentRecordingTarget));
        }

        // ─────────────────────────────────────────────
        //  Layout
        // ─────────────────────────────────────────────

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

        private void Build()
        {
            if (_built) return;
            var st = PadForge.Resources.Strings.Strings.Instance;
            var captions = PillCaptions;

            _leftStickFace = BuildStickFace(LeftStickCanvas, out _leftDot, "VrLStickX", "VrLStickY");
            _rightStickFace = BuildStickFace(RightStickCanvas, out _rightDot, "VrRStickX", "VrRStickY");

            AddAxisRow(LeftAxisPanel, st.Btn_LeftTrigger, "VrLTrigger");
            AddAxisRow(LeftAxisPanel, st.Btn_VrLeftGrip, "VrLGrip");
            AddAxisRow(RightAxisPanel, st.Btn_RightTrigger, "VrRTrigger");
            AddAxisRow(RightAxisPanel, st.Btn_VrRightGrip, "VrRGrip");

            for (int i = 0; i < 8; i++)
            {
                _leftPills[i] = MakePill(captions[i], VrLayout.LeftButtonKeys[i]);
                LeftButtonPanel.Children.Add(_leftPills[i]);
                _rightPills[i] = MakePill(captions[i], VrLayout.RightButtonKeys[i]);
                RightButtonPanel.Children.Add(_rightPills[i]);
            }
            _built = true;
        }

        /// <summary>Stick face: ring, crosshair, and the travel dot. The X
        /// and Y halves are separate mapping targets, so the face is split
        /// into two invisible hit zones (horizontal band picks X, vertical
        /// band picks Y) rather than guessing from one click point.</summary>
        private Ellipse BuildStickFace(Canvas canvas, out Ellipse dot, string xTarget, string yTarget)
        {
            var face = new Ellipse { Width = 120, Height = 120, StrokeThickness = 1 };
            face.SetResourceReference(Shape.StrokeProperty, "SegTrackStrokeBrush");
            face.SetResourceReference(Shape.FillProperty, "SegTrackBrush");
            canvas.Children.Add(face);

            for (int i = 0; i < 2; i++)
            {
                var line = i == 0
                    ? new Line { X1 = 12, Y1 = 60, X2 = 108, Y2 = 60 }
                    : new Line { X1 = 60, Y1 = 12, X2 = 60, Y2 = 108 };
                line.StrokeThickness = 1;
                line.Opacity = 0.35;
                line.SetResourceReference(Shape.StrokeProperty, "SegTrackStrokeBrush");
                canvas.Children.Add(line);
            }

            dot = new Ellipse { Width = 18, Height = 18 };
            dot.SetResourceReference(Shape.FillProperty, "EmberBrush");
            Canvas.SetLeft(dot, 51);
            Canvas.SetTop(dot, 51);
            canvas.Children.Add(dot);

            // Hit zones LAST so they sit above the art. Transparent (not
            // null) so they actually receive the click.
            AddStickHit(canvas, 0, 40, 120, 40, xTarget);
            AddStickHit(canvas, 40, 0, 40, 120, yTarget);
            return face;
        }

        private void AddStickHit(Canvas canvas, double x, double y, double w, double h, string target)
        {
            var hit = new Rectangle { Width = w, Height = h, Fill = Brushes.Transparent, Cursor = Cursors.Hand };
            hit.ToolTip = target;
            Canvas.SetLeft(hit, x);
            Canvas.SetTop(hit, y);
            hit.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };
            canvas.Children.Add(hit);
        }

        /// <summary>One analog row: caption sized to its content, then a
        /// bar that takes the rest. A fixed caption width clipped "Left
        /// Trigger" in English alone.</summary>
        private void AddAxisRow(StackPanel host, string caption, string target)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock { Text = caption };
            label.SetResourceReference(StyleProperty, "VrAxisLabel");
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var track = new Border { Height = 12, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), MinWidth = 120 };
            track.SetResourceReference(Border.BackgroundProperty, "SegTrackBrush");
            track.SetResourceReference(Border.BorderBrushProperty, "SegTrackStrokeBrush");
            var bar = new Rectangle { Width = 0, HorizontalAlignment = HorizontalAlignment.Left, RadiusX = 6, RadiusY = 6 };
            bar.SetResourceReference(Shape.FillProperty, "EmberBrush");
            track.Child = bar;
            Grid.SetColumn(track, 1);
            grid.Children.Add(track);

            // Transparent backdrop so the whole row is clickable, caption
            // included, not just the bar.
            grid.Background = Brushes.Transparent;
            grid.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };

            _bars[target] = bar;
            _barHosts[target] = track;
            host.Children.Add(grid);
        }

        private Border MakePill(string caption, string target)
        {
            var text = new TextBlock
            {
                Text = caption,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

            var b = new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 7, 7),
                Cursor = Cursors.Hand,
                ToolTip = target,
            };
            b.SetResourceReference(Border.BorderBrushProperty, "SegTrackStrokeBrush");
            b.SetResourceReference(Border.BackgroundProperty, "SegTrackBrush");
            b.MouseLeftButtonDown += (s, e) =>
            {
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };
            b.Tag = target;
            return b;
        }

        // ─────────────────────────────────────────────
        //  Recording highlight
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            _flashTarget = target;
            _flashOn = false;
            if (_flashTimer == null)
            {
                _flashTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(450) };
                _flashTimer.Tick += (s, e) => { _flashOn = !_flashOn; ApplyFlash(); };
            }
            if (string.IsNullOrEmpty(_flashTarget)) _flashTimer.Stop();
            else _flashTimer.Start();
            ApplyFlash();
            _paintedValid = false;   // let the next frame repaint the rest
        }

        private void ApplyFlash()
        {
            if (!_built) return;
            foreach (var pills in new[] { _leftPills, _rightPills })
                foreach (var p in pills)
                {
                    if (p?.Tag as string != _flashTarget) continue;
                    p.SetResourceReference(Border.BorderBrushProperty,
                        _flashOn ? "EmberBrush" : "SegTrackStrokeBrush");
                }
            foreach (var kv in _barHosts)
                kv.Value.SetResourceReference(Border.BorderBrushProperty,
                    kv.Key == _flashTarget && _flashOn ? "EmberBrush" : "SegTrackStrokeBrush");
        }

        // ─────────────────────────────────────────────
        //  Rendering
        // ─────────────────────────────────────────────

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

            PaintHand(in vr.Left, _leftPills, _leftDot, "VrLTrigger", "VrLGrip");
            PaintHand(in vr.Right, _rightPills, _rightDot, "VrRTrigger", "VrRGrip");
        }

        private void PaintHand(in VrHandRaw hand, Border[] pills, Ellipse dot,
            string triggerTarget, string gripTarget)
        {
            for (int i = 0; i < pills.Length; i++)
            {
                bool lit = (hand.Buttons & (1 << i)) != 0;
                pills[i].SetResourceReference(Border.BackgroundProperty,
                    lit ? "EmberSegGradient" : "SegTrackBrush");
                ((TextBlock)pills[i].Child).SetResourceReference(TextBlock.ForegroundProperty,
                    lit ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
            }

            // 120 px face, 18 px dot: 51 centres it and the travel is 51 px
            // each way. The snapshot is the pipeline's SDL-native frame
            // (Y positive = down), which is also screen-Y, so neither axis
            // is flipped here. The single Y flip for the wire lives in
            // HMaestroVRController.PackHand.
            const double centre = 51.0, travel = 45.0;
            double nx = hand.StickX >= 0 ? hand.StickX / 32767.0 : hand.StickX / 32768.0;
            double ny = hand.StickY >= 0 ? hand.StickY / 32767.0 : hand.StickY / 32768.0;
            Canvas.SetLeft(dot, centre + nx * travel);
            Canvas.SetTop(dot, centre + ny * travel);

            SetBar(triggerTarget, hand.Trigger);
            SetBar(gripTarget, hand.Grip);
        }

        private void SetBar(string target, short value)
        {
            if (!_bars.TryGetValue(target, out var bar) || !_barHosts.TryGetValue(target, out var host)) return;
            double w = host.ActualWidth - 2;   // inside the 1 px border
            if (w <= 0) return;
            bar.Width = w * Math.Clamp(value / 32767.0, 0, 1);
        }

        private static bool Same(in VrRawState a, in VrRawState b)
            => Same(in a.Left, in b.Left) && Same(in a.Right, in b.Right);

        private static bool Same(in VrHandRaw a, in VrHandRaw b)
            => a.Buttons == b.Buttons && a.Trigger == b.Trigger && a.Grip == b.Grip
            && a.StickX == b.StickX && a.StickY == b.StickY;
    }
}
