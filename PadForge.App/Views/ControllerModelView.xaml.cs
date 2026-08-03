// 3D controller model view adapted from Handheld Companion
// https://github.com/Valkirie/HandheldCompanion
// Copyright (c) CasperH2O, Lesueur Benjamin, trippyone
// Licensed under CC BY-NC-SA 4.0
//
// Modifications for PadForge: PadSetting-based button mapping,
// ViewModel-driven updates via CompositionTarget.Rendering,
// click-to-record hit testing, Map All flash animation.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HelixToolkit.Wpf;
using PadForge.Engine;
using PadForge.Models3D;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class ControllerModelView : UserControl
    {
        // ─────────────────────────────────────────────
        //  Events
        // ─────────────────────────────────────────────

        /// <summary>Raised when the user clicks a 3D model part to start recording a mapping.</summary>
        public event EventHandler<string> ControllerElementRecordRequested;

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private PadViewModel _vm;
        private ControllerModelBase _currentModel;
        // Tracks whether the current Xbox One mesh has Share wired in.
        // Profile switches within the same asset folder (Xbox One ↔
        // Xbox Series) need to force a rebuild when this flag would
        // change so the Share mesh transitions between inert and live.
        private bool _currentModelShareEnabled;
        private bool _dirty;

        // Trigger animation state (from HC OverlayModel)
        private float _triggerAngleLeft;
        private float _triggerAngleRight;

        // Map All flash state
        private DispatcherTimer _flashTimer;
        private string _flashTarget;
        private bool _flashOn;

        // Axis arrow overlay (visible until mapping finishes)
        private ModelVisual3D _arrowVisual;

        // Quadrant ring overlay (subset of ring triangles for the target quadrant)
        private ModelVisual3D _quadrantRingVisual;
        private DiffuseMaterial _quadrantRingMaterial;

        // Hover highlight state
        private Model3DGroup _hoverGroup;            // Currently highlighted group (button/trigger)
        private string _hoverQuadrant;                // Current quadrant axis string (e.g., "LeftThumbAxisXNeg")
        private ModelVisual3D _hoverQuadrantVisual;    // Quadrant wedge overlay for hover

        // Touchpad preview state (PlayStation slots only — applied each render frame from
        // PadViewModel.TouchpadFingerN(X,Y,Down) and TouchpadClickPressed)
        private DiffuseMaterial _touchpadHighlightMaterial; // accent-color blue used while click is held
        private bool _touchpadCurrentlyHighlighted;          // tracks current swap so we don't churn materials
        private ModelVisual3D _touchpadFinger0Visual;
        private ModelVisual3D _touchpadFinger1Visual;
        private TranslateTransform3D _touchpadFinger0Transform;
        private TranslateTransform3D _touchpadFinger1Transform;

        // Model rotation via left/right-drag or single-touch drag (turntable-style)
        private bool _isRightDragging;
        private bool _isLeftDragging;
        private bool _leftMouseActive;   // true only when our handler captured the mouse (not button clicks)
        private Point _leftDragStart;          // initial left-down position (for click vs drag threshold)
        private const double DragThreshold = 5; // pixels before left-click becomes a drag
        private Point _rightDragLast;
        private double _modelYaw;    // degrees around Z axis (horizontal drag)
        private double _modelPitch;  // degrees around X axis (vertical drag)
        private int? _touchDragId;   // active touch device ID for rotation
        private int? _touchSecondId; // second touch device ID for pinch-to-zoom
        private Point _touchSecondLast;
        private double _pinchStartDist;
        private Point _pinchMidpoint;  // midpoint of two fingers for panning
        private readonly Transform3DGroup _modelRotation = new();
        private readonly AxisAngleRotation3D _yawRotation = new(new Vector3D(0, 0, 1), 0);
        private readonly AxisAngleRotation3D _pitchRotation = new(new Vector3D(1, 0, 0), 0);
        // Per-model uniform scale, composed into the same Transform3DGroup
        // so the rotation and scale share one assignment to ModelVisual3D's
        // Transform property. Setting Transform = scale-only would clobber
        // the rotation children and break left-drag camera rotation.
        private readonly ScaleTransform3D _modelScaleTransform = new(1, 1, 1);

        public ControllerModelView()
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

            // Order matters: scale first so it applies in the model's local
            // frame, then yaw/pitch rotate the scaled model around its
            // (post-scale) center. With rotation first the rotated controller
            // would scale around its rotated bounding-box center, which
            // shifts when yaw isn't zero.
            _modelRotation.Children.Add(_modelScaleTransform);
            _modelRotation.Children.Add(new RotateTransform3D(_yawRotation));
            _modelRotation.Children.Add(new RotateTransform3D(_pitchRotation));
            ModelVisual3D.Transform = _modelRotation;

            // Subscribe Preview events on the viewport itself (not the UserControl) so
            // overlay controls like the Reset View button are never intercepted.
            ModelViewPort.PreviewMouseLeftButtonDown += Viewport_PreviewMouseLeftButtonDown;
            ModelViewPort.PreviewMouseLeftButtonUp += Viewport_PreviewMouseLeftButtonUp;
            ModelViewPort.PreviewMouseRightButtonDown += Viewport_MouseRightButtonDown;
            ModelViewPort.PreviewMouseRightButtonUp += Viewport_MouseRightButtonUp;
            ModelViewPort.PreviewMouseMove += Viewport_PreviewMouseMove;
            ModelViewPort.PreviewMouseWheel += Viewport_PreviewMouseWheel;
            ModelViewPort.MouseLeave += Viewport_MouseLeave;

            PreviewTouchDown += Viewport_PreviewTouchDown;
            PreviewTouchMove += Viewport_PreviewTouchMove;
            PreviewTouchUp += Viewport_PreviewTouchUp;

            // Block WPF stylus system gestures (press-and-hold → right-click, flicks)
            // that delay or swallow touch events.
            PreviewStylusSystemGesture += (s, e) => e.Handled = true;

            // Belt-and-suspenders: cancel any WPF manipulation that tries to start
            // (HelixToolkit may re-enable IsManipulationEnabled internally).
            ModelViewPort.ManipulationStarting += (s, e) => { e.Cancel(); e.Handled = true; };

            // Annotation overlay (#175 roadmap 1): belt-and-suspenders
            // re-projection triggers. The 150 ms tick is the primary
            // mechanism; CameraChanged may never fire for this view's
            // direct cam.Position writes.
            ModelViewPort.CameraChanged += (s, e) => ReprojectAnnotations();
            ModelViewPort.SizeChanged += (s, e) => ReprojectAnnotations();
        }

        // ─────────────────────────────────────────────
        //  ViewModel binding
        // ─────────────────────────────────────────────

        public void Bind(PadViewModel vm)
        {
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;

            _vm = vm;

            if (_vm != null)
            {
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
                _vm.PropertyChanged += OnVmPropertyChanged;
                EnsureModel();
                RebuildAnnotations();
            }
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Model type change
            if (e.PropertyName == nameof(PadViewModel.OutputType)
                || e.PropertyName == nameof(PadViewModel.ProfileId))
            {
                Dispatcher.Invoke(EnsureModel);
                return;
            }

            // Map All flash target change / recording finished
            if (e.PropertyName == nameof(PadViewModel.CurrentRecordingTarget))
            {
                Dispatcher.Invoke(() =>
                {
                    string target = _vm.CurrentRecordingTarget;
                    UpdateFlashTarget(target);
                    ShowArrowForTarget(target);
                });
                return;
            }

            // High-churn readout properties the 3D render never consumes
            // (gyro noise re-armed the full refresh every tick while the
            // preview was visible with a motion pad at rest).
            switch (e.PropertyName)
            {
                case nameof(PadViewModel.GyroLiveRatePitch):
                case nameof(PadViewModel.GyroLiveRateYaw):
                case nameof(PadViewModel.GyroLiveRateRoll):
                case nameof(PadViewModel.AccelLiveX):
                case nameof(PadViewModel.AccelLiveY):
                case nameof(PadViewModel.AccelLiveZ):
                    return;
            }

            // Any other controller state property marks dirty for the
            // next render frame.
            _dirty = true;
        }

        // ─────────────────────────────────────────────
        //  Model lifecycle
        // ─────────────────────────────────────────────

        private void EnsureModel()
        {
            if (_vm == null) return;

            // Per-profile asset selection — DualSense profiles get the
            // DualSense mesh, Xbox One/Elite/Series/Adaptive profiles get
            // the Xbox One mesh (HC has no Series-specific 3D), DualShock
            // and Xbox 360 fall through unchanged.
            var (_, needed) = PadForge.Common.Input.HMaestroProfileCatalog.ResolveAssetFolders(
                _vm.ProfileId, _vm.OutputType);

            bool wantShare =
                needed == "XBOXONE" &&
                _vm.ProfileId != null &&
                _vm.ProfileId.StartsWith("xbox-series-", System.StringComparison.OrdinalIgnoreCase);

            if (_currentModel?.ModelName == needed && _currentModelShareEnabled == wantShare)
                return;

            // Model rebuild: drop retained per-thumb transform entries.
            // They key on the OUTGOING model's Model3DGroups, so without
            // this every profile/output switch leaked the old model's
            // transform graphs for the view's lifetime.
            _stickTransforms3D.Clear();
            // The retained trigger angles key on the OUTGOING model the same
            // way, and they are passed by ref as the running state of the
            // smoothing. Left set, a switch carried the old model's pull angles
            // into the new one and the triggers rendered part-pressed at rest
            // until the next real input moved them.
            _triggerAngleLeft = 0f;
            _triggerAngleRight = 0f;

            // Clear arrow from old model before switching
            RemoveArrow();

            _currentModel?.Dispose();
            _currentModel = null;

            try
            {
                // Xbox One mesh is shared with Xbox Series profiles, but
                // only Series profiles actually expose Share. Pass the
                // flag so non-Series profiles get an inert Share mesh
                // (no hover / click / highlight) while Series profiles
                // wire it into the click-to-record + highlight maps.
                _currentModel = needed switch
                {
                    "DS4" => new ControllerModelDS4(),
                    "DualSense" => new ControllerModelDualSense(),
                    "XBOXONE" => new ControllerModelXboxOne(enableShare: wantShare),
                    "Switch2Pro" => new ControllerModelSwitch2Pro(),
                    _ => new ControllerModelXbox360()
                };
                _currentModelShareEnabled = wantShare;

                ModelVisual3D.Content = _currentModel.model3DGroup;
                // Update the per-model uniform scale on the existing
                // Transform3DGroup that ModelVisual3D.Transform points at.
                // Don't replace ModelVisual3D.Transform — that's the
                // rotation group used by left-drag turntable rotation; a
                // fresh ScaleTransform3D would un-wire the yaw/pitch
                // children and break rotation.
                double s = _currentModel.ModelScale;
                _modelScaleTransform.ScaleX = s;
                _modelScaleTransform.ScaleY = s;
                _modelScaleTransform.ScaleZ = s;
                BuildTouchpadFingerVisuals();
                _dirty = true;
                RebuildAnnotations();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ControllerModelView] Failed to load 3D model: {ex}");
            }
        }

        // ─────────────────────────────────────────────
        //  Touchpad preview (PlayStation slots only)
        // ─────────────────────────────────────────────

        private void BuildTouchpadFingerVisuals()
        {
            // Tear down any visuals from a previous model.
            if (_touchpadFinger0Visual != null)
                ModelVisual3D.Children.Remove(_touchpadFinger0Visual);
            if (_touchpadFinger1Visual != null)
                ModelVisual3D.Children.Remove(_touchpadFinger1Visual);
            _touchpadFinger0Visual = _touchpadFinger1Visual = null;
            _touchpadFinger0Transform = _touchpadFinger1Transform = null;
            _touchpadHighlightMaterial = null;
            _touchpadCurrentlyHighlighted = false;

            if (_currentModel?.Touchpad == null) return;

            var accent = ResolveAccentColor();
            _touchpadHighlightMaterial = new DiffuseMaterial(
                new SolidColorBrush(Color.FromArgb(0xC0, accent.R, accent.G, accent.B)));

            (_touchpadFinger0Visual, _touchpadFinger0Transform) = CreateFingerSphere(
                Color.FromArgb(0xE6, 0xFF, 0x66, 0x00));   // orange — matches the 2D dot
            (_touchpadFinger1Visual, _touchpadFinger1Transform) = CreateFingerSphere(
                Color.FromArgb(0xE6, 0x00, 0x66, 0xFF));   // blue — matches the 2D dot

            ModelVisual3D.Children.Add(_touchpadFinger0Visual);
            ModelVisual3D.Children.Add(_touchpadFinger1Visual);
        }

        private static (ModelVisual3D visual, TranslateTransform3D transform) CreateFingerSphere(Color color)
        {
            var mb = new MeshBuilder();
            mb.AddSphere(new Point3D(0, 0, 0), 2.5, 12, 8);
            var material = new DiffuseMaterial(new SolidColorBrush(color));
            var geo = new GeometryModel3D(mb.ToMesh(), material) { BackMaterial = material };
            var transform = new TranslateTransform3D();
            var visual = new ModelVisual3D
            {
                Content = geo,
                Transform = transform,
            };
            // Hide initially — UpdateTouchpadPreview3D positions and shows the
            // visual only while the corresponding finger is down. We "hide" by
            // translating off-screen since ModelVisual3D has no Visibility.
            transform.OffsetY = -10000;
            return (visual, transform);
        }

        private static Color ResolveAccentColor()
        {
            try
            {
                var brush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                if (brush is SolidColorBrush scb) return scb.Color;
            }
            catch { }
            return Color.FromRgb(0x21, 0x96, 0xF3);
        }

        private void UpdateTouchpadPreview3D()
        {
            if (_currentModel?.Touchpad == null || _vm == null) return;

            // ── Click highlight: swap touchpad surface material ─────────
            bool clickPressed = _vm.TouchpadClickPressed;
            if (clickPressed != _touchpadCurrentlyHighlighted
                && _currentModel.Touchpad.Children.Count > 0
                && _currentModel.Touchpad.Children[0] is GeometryModel3D geo)
            {
                if (clickPressed && _touchpadHighlightMaterial != null)
                {
                    geo.Material = _touchpadHighlightMaterial;
                    geo.BackMaterial = _touchpadHighlightMaterial;
                    _touchpadCurrentlyHighlighted = true;
                }
                else if (_currentModel.DefaultMaterials.TryGetValue(_currentModel.Touchpad, out var defMat))
                {
                    geo.Material = defMat;
                    geo.BackMaterial = defMat;
                    _touchpadCurrentlyHighlighted = false;
                }
            }

            // ── Finger spheres: position above the touchpad surface ─────
            var bounds = _currentModel.Touchpad.Bounds;
            if (bounds.IsEmpty) return;

            PositionFingerSphere(_touchpadFinger0Transform,
                _vm.TouchpadFinger0Down, _vm.TouchpadFinger0X, _vm.TouchpadFinger0Y, bounds, _currentModel);
            PositionFingerSphere(_touchpadFinger1Transform,
                _vm.TouchpadFinger1Down, _vm.TouchpadFinger1X, _vm.TouchpadFinger1Y, bounds, _currentModel);
        }

        private static void PositionFingerSphere(
            TranslateTransform3D t, bool down, float normX, float normY, Rect3D bounds,
            PadForge.Models3D.ControllerModelBase model)
        {
            if (t == null) return;
            if (!down)
            {
                // Park well off-screen so the sphere isn't visible / hit-testable.
                t.OffsetY = -10000;
                return;
            }

            // Model coords: X = left/right (matches normX 0..1 left→right),
            // Z = top/bottom of body (touch normY 0=top → high Z, 1=bottom →
            // low Z), Y is the surface depth — float the sphere just in
            // front of the touchpad face (Y at bounds.Min.Y + small offset
            // toward the camera, which is -Y in HC's body model).
            //
            // Each model's Touchpad mesh extends past the actual touch surface
            // by a different amount (DS4 Screen.obj has a small bezel; the
            // DualSense Touchpad mesh is the entire central front-face area
            // and is much larger than the real touchpad). The model owns the
            // inset fractions so this code stays controller-agnostic.
            double xInsetFrac      = model?.TouchpadXInsetFrac      ?? 0.03;
            double zTopInsetFrac   = model?.TouchpadZTopInsetFrac   ?? 0.12;
            double zBottomInsetFrac = model?.TouchpadZBottomInsetFrac ?? 0.12;

            double touchX0 = bounds.X + bounds.SizeX * xInsetFrac;
            double touchXSize = bounds.SizeX * (1.0 - 2 * xInsetFrac);
            double touchZ0 = bounds.Z + bounds.SizeZ * zBottomInsetFrac;
            double touchZSize = bounds.SizeZ * (1.0 - zTopInsetFrac - zBottomInsetFrac);

            t.OffsetX = touchX0 + (double)normX * touchXSize;
            t.OffsetZ = touchZ0 + (1.0 - (double)normY) * touchZSize;
            t.OffsetY = bounds.Y - 1.5;
        }

        // ─────────────────────────────────────────────
        //  Render-frame batched update
        // ─────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
            // Retained-page guard: pages are eagerly instantiated and
            // visibility-toggled, so Loaded fires at startup even for hidden
            // pages and Unloaded never fires. Without this, a connected
            // device's 30Hz VM updates kept _dirty set and this handler
            // rebuilt the WHOLE WPF3D transform/material graph every frame
            // while completely invisible, burning the render thread on every
            // page of the app. _dirty stays set; the first visible frame
            // catches up.
            // Iconic gate: IsVisible stays TRUE while minimized.
            if (!IsVisible || PadForge.Common.AmbientMotionProbe.Instance.IsWindowMinimized) return;
            if (!_dirty || _vm == null || _currentModel == null)
                return;
            _dirty = false;

            HighlightButtons();
            UpdateJoystick(
                _vm.RawThumbLX, _vm.RawThumbLY,
                _currentModel.LeftThumbRing, _currentModel.LeftThumb,
                _currentModel.JoystickRotationPointCenterLeftMillimeter,
                _currentModel.JoystickMaxAngleDeg);
            UpdateJoystick(
                _vm.RawThumbRX, _vm.RawThumbRY,
                _currentModel.RightThumbRing, _currentModel.RightThumb,
                _currentModel.JoystickRotationPointCenterRightMillimeter,
                _currentModel.JoystickMaxAngleDeg);
            UpdateTrigger(
                _vm.LeftTrigger,
                _currentModel.LeftShoulderTrigger,
                _currentModel.ShoulderTriggerRotationPointCenterLeftMillimeter,
                _currentModel.TriggerMaxAngleDeg,
                ref _triggerAngleLeft);
            UpdateTrigger(
                _vm.RightTrigger,
                _currentModel.RightShoulderTrigger,
                _currentModel.ShoulderTriggerRotationPointCenterRightMillimeter,
                _currentModel.TriggerMaxAngleDeg,
                ref _triggerAngleRight);
            UpdateTouchpadPreview3D();
            UpdateAnnotationLevelBars();
        }

        // ─────────────────────────────────────────────
        //  Button highlighting (adapted from HC HighLightButtons)
        // ─────────────────────────────────────────────

        /// <summary>PadSetting property name → getter that reads the current bool from the VM.</summary>
        private static readonly string[] ButtonProperties =
        {
            "ButtonA", "ButtonB", "ButtonX", "ButtonY",
            "LeftShoulder", "RightShoulder",
            "ButtonBack", "ButtonStart", "ButtonGuide",
            "ButtonShare",
            "DPadUp", "DPadDown", "DPadLeft", "DPadRight",
            "LeftThumbButton", "RightThumbButton"
        };

        private void HighlightButtons()
        {
            foreach (var prop in ButtonProperties)
            {
                if (!_currentModel.ButtonMap.TryGetValue(prop, out var groups))
                    continue;

                bool pressed = GetButtonState(prop);

                foreach (var group in groups)
                {
                    if (group.Children.Count == 0 || group.Children[0] is not GeometryModel3D geo)
                        continue;
                    if (geo.Material is not DiffuseMaterial)
                        continue;
                    // The hovered group is owned by the hover highlight while
                    // the cursor sits on it; without this skip the per-frame
                    // reset stomps the hover material after a single frame.
                    if (group == _hoverGroup)
                        continue;

                    if (pressed && _currentModel.HighlightMaterials.TryGetValue(group, out var hlMat))
                    {
                        geo.Material = hlMat;
                        geo.BackMaterial = hlMat;
                    }
                    else if (_currentModel.DefaultMaterials.TryGetValue(group, out var defMat))
                    {
                        geo.Material = defMat;
                        geo.BackMaterial = defMat;
                    }
                }
            }
        }

        private bool GetButtonState(string prop)
        {
            if (_vm == null) return false;
            return prop switch
            {
                "ButtonA" => _vm.ButtonA,
                "ButtonB" => _vm.ButtonB,
                "ButtonX" => _vm.ButtonX,
                "ButtonY" => _vm.ButtonY,
                "LeftShoulder" => _vm.LeftShoulder,
                "RightShoulder" => _vm.RightShoulder,
                "ButtonBack" => _vm.ButtonBack,
                "ButtonStart" => _vm.ButtonStart,
                "ButtonGuide" => _vm.ButtonGuide,
                "ButtonShare" => _vm.ButtonShare,
                "DPadUp" => _vm.DPadUp,
                "DPadDown" => _vm.DPadDown,
                "DPadLeft" => _vm.DPadLeft,
                "DPadRight" => _vm.DPadRight,
                "LeftThumbButton" => _vm.LeftThumbButton,
                "RightThumbButton" => _vm.RightThumbButton,
                _ => false
            };
        }

        // ─────────────────────────────────────────────
        //  Joystick tilt (adapted from HC UpdateJoystick)
        // ─────────────────────────────────────────────

        private void UpdateJoystick(
            short rawX, short rawY,
            Model3DGroup thumbRing, Model3D thumb,
            Vector3D rotationPoint, float maxAngleDeg)
        {
            if (thumbRing == null) return;

            float normX = rawX / (float)short.MaxValue;
            float normY = rawY / (float)short.MaxValue;

            // Gradient highlight on stick ring
            if (thumbRing.Children.Count > 0 && thumbRing.Children[0] is GeometryModel3D geo)
            {
                bool moved = normX != 0f || normY != 0f;
                if (moved && _currentModel.DefaultMaterials.TryGetValue(thumbRing, out var defMat)
                          && _currentModel.HighlightMaterials.TryGetValue(thumbRing, out var hlMat))
                {
                    float factor = Math.Max(Math.Abs(normX), Math.Abs(normY));
                    geo.Material = GradientHighlight(geo, defMat, hlMat, factor);
                }
                else if (_currentModel.DefaultMaterials.TryGetValue(thumbRing, out var def))
                {
                    geo.Material = def;
                }
            }

            // Rotation
            float angleX = maxAngleDeg * normX;
            float angleY = -maxAngleDeg * normY;

            // Retained per-stick transform: allocating the 5-object
            // transform graph per dirty frame was pure churn. Mutate the
            // two angles instead; skip when unchanged.
            if (!_stickTransforms3D.TryGetValue(thumbRing, out var st))
            {
                var ax = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
                var ay = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
                var group = new Transform3DGroup();
                group.Children.Add(new RotateTransform3D(ax,
                    new Point3D(rotationPoint.X, rotationPoint.Y, rotationPoint.Z)));
                group.Children.Add(new RotateTransform3D(ay,
                    new Point3D(rotationPoint.X, rotationPoint.Y, rotationPoint.Z)));
                st = (group, ax, ay, float.NaN, float.NaN);
                _stickTransforms3D[thumbRing] = st;
                thumbRing.Transform = group;
                if (thumb != null) thumb.Transform = group;
            }
            if (st.lastX != angleX || st.lastY != angleY)
            {
                st.ax.Angle = angleX;
                st.ay.Angle = angleY;
                _stickTransforms3D[thumbRing] = (st.group, st.ax, st.ay, angleX, angleY);
                // Reassert in case the model was rebuilt around the cache.
                if (!ReferenceEquals(thumbRing.Transform, st.group))
                {
                    thumbRing.Transform = st.group;
                    if (thumb != null) thumb.Transform = st.group;
                }
            }
        }

        private readonly System.Collections.Generic.Dictionary<Model3DGroup,
            (Transform3DGroup group, AxisAngleRotation3D ax, AxisAngleRotation3D ay, float lastX, float lastY)>
            _stickTransforms3D = new();

        // ─────────────────────────────────────────────
        //  Trigger rotation + gradient (adapted from HC UpdateShoulderButtons)
        // ─────────────────────────────────────────────

        private void UpdateTrigger(
            double triggerNorm,
            Model3DGroup triggerModel,
            Vector3D rotationPoint,
            float maxAngleDeg,
            ref float prevAngle)
        {
            if (triggerModel == null) return;
            // The hovered trigger is owned by the hover highlight (#175
            // hover-hold fix, trigger sibling of the button-loop skip).
            if (triggerModel == _hoverGroup) return;

            float value = (float)triggerNorm;

            // Gradient color
            if (triggerModel.Children.Count > 0 && triggerModel.Children[0] is GeometryModel3D geo)
            {
                if (value > 0 && _currentModel.DefaultMaterials.TryGetValue(triggerModel, out var defMat)
                              && _currentModel.HighlightMaterials.TryGetValue(triggerModel, out var hlMat))
                {
                    geo.Material = GradientHighlight(geo, defMat, hlMat, value);
                }
                else if (_currentModel.DefaultMaterials.TryGetValue(triggerModel, out var def))
                {
                    geo.Material = def;
                }
            }

            // Rotation
            float angle = -maxAngleDeg * value;
            if (Math.Abs(angle - prevAngle) < 0.01f) return;
            prevAngle = angle;

            var rot = new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(1, 0, 0), angle),
                new Point3D(rotationPoint.X, rotationPoint.Y, rotationPoint.Z));
            triggerModel.Transform = rot;
        }

        // ─────────────────────────────────────────────
        //  Gradient color interpolation (from HC)
        // ─────────────────────────────────────────────

        // Per-element retained highlight material (keyed weakly on the
        // GeometryModel3D so rebuilt models collect): the per-frame
        // DiffuseMaterial + SolidColorBrush pair was allocated for every
        // deflected stick / pulled trigger every dirty frame. The brush we
        // own is never frozen, so mutating its Color is the supported
        // dependent-animation shape.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GeometryModel3D, DiffuseMaterial>
            s_highlightMaterials = new();

        private static DiffuseMaterial GradientHighlight(GeometryModel3D owner,
            Material defaultMaterial, Material highlightMaterial, float factor)
        {
            factor = Math.Clamp(factor, 0f, 1f);
            // Cast-proof (#175 regression fix): a themed material may carry a
            // gradient brush; lerp from its first stop instead of crashing
            // the render loop with an invalid cast.
            var startColor = BrushColor((defaultMaterial as DiffuseMaterial)?.Brush);
            var endColor = BrushColor((highlightMaterial as DiffuseMaterial)?.Brush);

            byte a = (byte)(startColor.A * (1 - factor) + endColor.A * factor);
            byte r = (byte)(startColor.R * (1 - factor) + endColor.R * factor);
            byte g = (byte)(startColor.G * (1 - factor) + endColor.G * factor);
            byte b = (byte)(startColor.B * (1 - factor) + endColor.B * factor);

            var mat = s_highlightMaterials.GetValue(owner,
                _ => new DiffuseMaterial(new SolidColorBrush()));
            ((SolidColorBrush)mat.Brush).Color = Color.FromArgb(a, r, g, b);
            return mat;
        }

        private static Color BrushColor(Brush brush) => brush switch
        {
            SolidColorBrush s => s.Color,
            GradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color,
            _ => Color.FromRgb(0xFF, 0x6B, 0x2C),
        };

        // ─────────────────────────────────────────────
        //  Click-to-record hit testing
        // ─────────────────────────────────────────────

        private void Viewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _leftMouseActive = true;
            _leftDragStart = e.GetPosition(ModelViewPort);
            _isLeftDragging = false;
            _rightDragLast = _leftDragStart;
            Mouse.Capture(ModelViewPort, CaptureMode.SubTree);
            e.Handled = true;
        }

        private void Viewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool wasDragging = _isLeftDragging;
            _isLeftDragging = false;
            _leftMouseActive = false;
            Mouse.Capture(null);
            SetAnnotationsDragHidden(false);

            if (wasDragging)
            {
                // Was a drag — just end rotation, no hit-test.
                e.Handled = true;
                return;
            }
            if (_currentModel == null) return;

            var pos = e.GetPosition(ModelViewPort);
            var hits = Viewport3DHelper.FindHits(ModelViewPort.Viewport, pos);

            foreach (var hit in hits)
            {
                if (hit.Model is not GeometryModel3D hitGeo)
                    continue;

                if (IsStickRingHit(hitGeo, hit.Position, out string stickAxis))
                {
                    ControllerElementRecordRequested?.Invoke(this, stickAxis);
                    e.Handled = true;
                    return;
                }

                foreach (var kv in _currentModel.ClickMap)
                {
                    if (kv.Key.Children.Contains(hitGeo))
                    {
                        ControllerElementRecordRequested?.Invoke(this, kv.Value);
                        e.Handled = true;
                        return;
                    }
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Model rotation (right-drag turntable)
        // ─────────────────────────────────────────────

        private void Viewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isRightDragging = true;
            SetAnnotationsDragHidden(true);
            _rightDragLast = e.GetPosition(ModelViewPort);
            Mouse.Capture(ModelViewPort, CaptureMode.SubTree);
            e.Handled = true;
        }

        private void Viewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isRightDragging)
            {
                _isRightDragging = false;
                Mouse.Capture(null);
                SetAnnotationsDragHidden(false);
                e.Handled = true;
            }
        }

        /// <summary>Preview handler for left-drag rotation, right-drag pan + hover highlighting.</summary>
        private void Viewport_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            // Promote left-button hold to drag once past threshold
            if (_leftMouseActive && !_isLeftDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(ModelViewPort);
                double ddx = pos.X - _leftDragStart.X;
                double ddy = pos.Y - _leftDragStart.Y;
                if (ddx * ddx + ddy * ddy > DragThreshold * DragThreshold)
                {
                    _isLeftDragging = true;
                    SetAnnotationsDragHidden(true);
                }
            }

            if (_isLeftDragging)
            {
                var p = e.GetPosition(ModelViewPort);
                double dx = p.X - _rightDragLast.X;
                double dy = p.Y - _rightDragLast.Y;
                _rightDragLast = p;

                _modelYaw += dx * 0.5;
                _modelPitch = Math.Clamp(_modelPitch + dy * 0.5, -60, 60);

                _yawRotation.Angle = _modelYaw;
                _pitchRotation.Angle = _modelPitch;
                e.Handled = true;
                return;
            }

            if (_isRightDragging)
            {
                var p = e.GetPosition(ModelViewPort);
                double dx = p.X - _rightDragLast.X;
                double dy = p.Y - _rightDragLast.Y;
                _rightDragLast = p;

                if (ModelViewPort.Camera is PerspectiveCamera cam && (Math.Abs(dx) > 0.1 || Math.Abs(dy) > 0.1))
                {
                    var look = cam.LookDirection;
                    look.Normalize();
                    var up = cam.UpDirection;
                    up.Normalize();
                    var right = Vector3D.CrossProduct(look, up);
                    right.Normalize();
                    var trueUp = Vector3D.CrossProduct(right, look);
                    trueUp.Normalize();

                    double panScale = 0.5;
                    cam.Position = new Point3D(
                        cam.Position.X - right.X * dx * panScale + trueUp.X * dy * panScale,
                        cam.Position.Y - right.Y * dx * panScale + trueUp.Y * dy * panScale,
                        cam.Position.Z - right.Z * dx * panScale + trueUp.Z * dy * panScale);
                }
                e.Handled = true;
                return;
            }

            // Hover highlighting (no button held)
            UpdateHoverHighlight(e);
        }

        // ─────────────────────────────────────────────
        //  Mouse wheel zoom (replaces HelixToolkit zoom)
        // ─────────────────────────────────────────────

        private void Viewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ModelViewPort.Camera is PerspectiveCamera cam)
            {
                var look = cam.LookDirection;
                look.Normalize();
                double zoomDelta = e.Delta * 0.05;
                cam.Position = new Point3D(
                    cam.Position.X + look.X * zoomDelta,
                    cam.Position.Y + look.Y * zoomDelta,
                    cam.Position.Z + look.Z * zoomDelta);
                e.Handled = true;
                ReprojectAnnotations();
            }
        }

        // ─────────────────────────────────────────────
        //  Touch rotation (single-finger drag)
        // ─────────────────────────────────────────────

        private void Viewport_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            if (_touchDragId == null)
            {
                // First finger — start rotation tracking.
                _touchDragId = e.TouchDevice.Id;
                _rightDragLast = e.GetTouchPoint(this).Position;
                e.TouchDevice.Capture(this);
                SetAnnotationsDragHidden(true);
                e.Handled = true;
            }
            else if (_touchSecondId == null && e.TouchDevice.Id != _touchDragId)
            {
                // Second finger — start pinch-to-zoom + pan.
                _touchSecondId = e.TouchDevice.Id;
                _touchSecondLast = e.GetTouchPoint(this).Position;
                _pinchStartDist = Distance(_rightDragLast, _touchSecondLast);
                _pinchMidpoint = Midpoint(_rightDragLast, _touchSecondLast);
                e.TouchDevice.Capture(this);
                e.Handled = true;
            }
        }

        private void Viewport_PreviewTouchMove(object sender, TouchEventArgs e)
        {
            var pos = e.GetTouchPoint(this).Position;

            if (_touchSecondId != null)
            {
                // Two fingers down — pinch-to-zoom mode.
                if (e.TouchDevice.Id == _touchDragId.Value)
                    _rightDragLast = pos;
                else if (e.TouchDevice.Id == _touchSecondId.Value)
                    _touchSecondLast = pos;
                else
                    return;

                double dist = Distance(_rightDragLast, _touchSecondLast);
                var mid = Midpoint(_rightDragLast, _touchSecondLast);
                if (ModelViewPort.Camera is PerspectiveCamera cam)
                {
                    // Zoom by moving camera along look direction.
                    if (_pinchStartDist > 1)
                    {
                        double scale = dist / _pinchStartDist;
                        double zoomDelta = (scale - 1.0) * 50;
                        var look = cam.LookDirection;
                        look.Normalize();
                        cam.Position = new System.Windows.Media.Media3D.Point3D(
                            cam.Position.X + look.X * zoomDelta,
                            cam.Position.Y + look.Y * zoomDelta,
                            cam.Position.Z + look.Z * zoomDelta);
                    }

                    // Pan by moving camera perpendicular to look direction.
                    double panDx = mid.X - _pinchMidpoint.X;
                    double panDy = mid.Y - _pinchMidpoint.Y;
                    if (Math.Abs(panDx) > 0.5 || Math.Abs(panDy) > 0.5)
                    {
                        var look2 = cam.LookDirection;
                        look2.Normalize();
                        var up = cam.UpDirection;
                        up.Normalize();
                        var right = System.Windows.Media.Media3D.Vector3D.CrossProduct(look2, up);
                        right.Normalize();
                        var trueUp = System.Windows.Media.Media3D.Vector3D.CrossProduct(right, look2);
                        trueUp.Normalize();

                        double panScale = 0.5;
                        cam.Position = new System.Windows.Media.Media3D.Point3D(
                            cam.Position.X - right.X * panDx * panScale + trueUp.X * panDy * panScale,
                            cam.Position.Y - right.Y * panDx * panScale + trueUp.Y * panDy * panScale,
                            cam.Position.Z - right.Z * panDx * panScale + trueUp.Z * panDy * panScale);
                    }
                }
                _pinchStartDist = dist;
                _pinchMidpoint = mid;
                e.Handled = true;
            }
            else if (e.TouchDevice.Id == _touchDragId)
            {
                // Single finger — rotation.
                double dx = pos.X - _rightDragLast.X;
                double dy = pos.Y - _rightDragLast.Y;
                _rightDragLast = pos;

                _modelYaw += dx * 0.5;
                _modelPitch = Math.Clamp(_modelPitch + dy * 0.5, -60, 60);

                _yawRotation.Angle = _modelYaw;
                _pitchRotation.Angle = _modelPitch;
                e.Handled = true;
            }
        }

        private void Viewport_PreviewTouchUp(object sender, TouchEventArgs e)
        {
            if (_touchSecondId != null && e.TouchDevice.Id == _touchSecondId.Value)
            {
                // Second finger lifted — return to single-finger rotation.
                _touchSecondId = null;
                e.TouchDevice.Capture(null);
                e.Handled = true;
            }
            else if (_touchDragId != null && e.TouchDevice.Id == _touchDragId.Value)
            {
                if (_touchSecondId != null)
                {
                    // First finger lifted while second still down — promote second to primary.
                    _touchDragId = _touchSecondId;
                    _rightDragLast = _touchSecondLast;
                    _touchSecondId = null;
                }
                else
                {
                    _touchDragId = null;
                    SetAnnotationsDragHidden(false);
                }
                e.TouchDevice.Capture(null);
                e.Handled = true;
            }
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Point Midpoint(Point a, Point b)
            => new((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

        // ─────────────────────────────────────────────
        //  Hover highlighting
        // ─────────────────────────────────────────────

        private void UpdateHoverHighlight(MouseEventArgs e)
        {
            if (_currentModel == null) return;

            var hoverPos = e.GetPosition(ModelViewPort);
            var hits = Viewport3DHelper.FindHits(ModelViewPort.Viewport, hoverPos);

            foreach (var hit in hits)
            {
                if (hit.Model is not GeometryModel3D hitGeo)
                    continue;

                // Check stick ring quadrant
                if (IsStickRingHit(hitGeo, hit.Position, out string quadrant))
                {
                    // Same quadrant as before — nothing to do
                    if (quadrant == _hoverQuadrant) return;
                    ClearHover();
                    _hoverQuadrant = quadrant;

                    // Show a hover quadrant wedge
                    ShowHoverQuadrant(quadrant);
                    ModelViewPort.Cursor = Cursors.Hand;
                    return;
                }

                // Check ClickMap (buttons, triggers)
                foreach (var kv in _currentModel.ClickMap)
                {
                    if (kv.Key.Children.Contains(hitGeo))
                    {
                        if (_hoverGroup == kv.Key) return; // Same group
                        ClearHover();
                        _hoverGroup = kv.Key;
                        ApplyHoverHighlight(kv.Key);
                        ModelViewPort.Cursor = Cursors.Hand;
                        return;
                    }
                }
            }

            // Mouse is over the model but not on a mappable element
            ClearHover();
        }

        private void Viewport_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isRightDragging || _isLeftDragging)
            {
                _isRightDragging = false;
                _isLeftDragging = false;
                Mouse.Capture(null);
                SetAnnotationsDragHidden(false);
            }
            // End the left gesture outright. Leaving this set meant that
            // re-entering with the button still held re-promoted to dragging
            // against the ORIGINAL press point, which is always past the
            // threshold, and then rotated by p - _rightDragLast, a stale
            // position from the moment of exit. The model snapped by the
            // whole exit-to-re-entry cursor distance. Capture is already
            // released above, so the gesture really is over.
            _leftMouseActive = false;
            ClearHover();
        }

        private void ApplyHoverHighlight(Model3DGroup group)
        {
            if (_currentModel == null) return;
            if (group.Children.Count == 0 || group.Children[0] is not GeometryModel3D geo)
                return;

            if (_currentModel.HighlightMaterials.TryGetValue(group, out var hlMat))
            {
                geo.Material = hlMat;
                geo.BackMaterial = hlMat;
            }
        }

        private void RestoreHoverGroup(Model3DGroup group)
        {
            // _currentModel goes null when the slot's OutputType swaps to
            // a non-3D preview (KBM / MIDI) but _hoverGroup can linger from
            // the previous viewport's last hover. The next mouse-leave fires
            // ClearHover → RestoreHoverGroup with that stale group reference,
            // and the material dictionary lookup NRE'd at line 979 before
            // this guard. Treat "no current model" as "nothing to restore."
            if (_currentModel == null) return;
            if (group == null) return;
            if (group.Children.Count == 0 || group.Children[0] is not GeometryModel3D geo)
                return;

            // Don't restore if this group is currently being flash-animated
            if (_flashTarget != null)
            {
                var flashGroups = ResolveFlashGroups(_flashTarget);
                if (flashGroups != null && flashGroups.Contains(group))
                    return;
            }

            if (_currentModel.DefaultMaterials.TryGetValue(group, out var defMat))
            {
                geo.Material = defMat;
                geo.BackMaterial = defMat;
            }
        }

        private void ShowHoverQuadrant(string target)
        {
            RemoveHoverQuadrant();

            if (_currentModel == null) return;

            bool isNeg = target.EndsWith("Neg", StringComparison.Ordinal);
            string baseTarget = isNeg ? target.Substring(0, target.Length - 3) : target;
            bool isX = baseTarget.Contains("AxisX");
            bool isLeft = baseTarget.StartsWith("Left", StringComparison.Ordinal);

            var ring = isLeft ? _currentModel.LeftThumbRing : _currentModel.RightThumbRing;
            if (ring == null) return;

            // Anchor the quadrant wedge on the visible ring mesh's centroid,
            // not the deflection rotation pivot. Same reason as the click-
            // detection fix: those two points can be several mm apart on
            // models like DualSense (left ring centroid X=-33.25, rotation
            // pivot X=-30.34), and the wedge geometry winds up askew if
            // built from the pivot.
            var center = MeshCentroid(ring);

            var quadrantMesh = BuildClippedQuadrantMesh(ring, center, isX, isNeg);
            if (quadrantMesh.Positions.Count == 0) return;

            var accentColor = Color.FromRgb(0x21, 0x96, 0xF3);
            try
            {
                var accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                if (accentBrush is SolidColorBrush scb) accentColor = scb.Color;
            }
            catch { }

            var color = Color.FromArgb(120, accentColor.R, accentColor.G, accentColor.B);
            var material = new DiffuseMaterial(new SolidColorBrush(color));
            var quadrantGeo = new GeometryModel3D(quadrantMesh, material) { BackMaterial = material };
            _hoverQuadrantVisual = new ModelVisual3D { Content = quadrantGeo };
            ModelVisual3D.Children.Add(_hoverQuadrantVisual);
        }

        private void RemoveHoverQuadrant()
        {
            if (_hoverQuadrantVisual != null)
            {
                ModelVisual3D.Children.Remove(_hoverQuadrantVisual);
                _hoverQuadrantVisual = null;
            }
        }

        private void ClearHover()
        {
            if (_hoverGroup != null)
            {
                RestoreHoverGroup(_hoverGroup);
                _hoverGroup = null;
            }
            if (_hoverQuadrant != null)
            {
                RemoveHoverQuadrant();
                _hoverQuadrant = null;
            }
            ModelViewPort.Cursor = Cursors.Arrow;
        }

        /// <summary>
        /// Checks if the hit geometry belongs to a stick ring, and determines
        /// X or Y axis based on the click quadrant relative to the joystick center.
        /// Left/right quadrants → X axis, top/bottom quadrants → Y axis.
        /// </summary>
        private bool IsStickRingHit(GeometryModel3D hitGeo, Point3D hitPos, out string axis)
        {
            axis = null;

            // Transform hit position from world space back to model-local space
            // so quadrant detection works correctly when the model is rotated.
            var localHitPos = TransformToLocal(hitPos);

            // Check left stick ring — use the visible mesh's centroid for
            // quadrant math, not JoystickRotationPointCenter*. The rotation
            // pivot is the deflection axis (where the stick tilts from), not
            // the geometric center of the ring; on DualSense the left stick
            // mesh sits at X=-33.25 but the rotation pivot is at X=-30.34,
            // a 2.9 mm offset that skews quadrant detection ~10° toward
            // NNE/SSE on the user-perceived "up" / "down" hits.
            if (_currentModel.LeftThumbRing?.Children.Contains(hitGeo) == true)
            {
                var center = MeshCentroid(_currentModel.LeftThumbRing);
                axis = DetermineAxisFromQuadrant(localHitPos, center, "LeftThumbAxisX", "LeftThumbAxisY");
                return true;
            }

            // Check right stick ring
            if (_currentModel.RightThumbRing?.Children.Contains(hitGeo) == true)
            {
                var center = MeshCentroid(_currentModel.RightThumbRing);
                axis = DetermineAxisFromQuadrant(localHitPos, center, "RightThumbAxisX", "RightThumbAxisY");
                return true;
            }

            return false;
        }

        /// <summary>Bounding-box centroid of a Model3DGroup in its own local
        /// coordinate system. Used to anchor click-quadrant detection on the
        /// visible mesh rather than a rotation pivot that may sit off-center.</summary>
        private static Vector3D MeshCentroid(Model3DGroup group)
        {
            var b = group.Bounds;
            return new Vector3D(b.X + b.SizeX / 2.0, b.Y + b.SizeY / 2.0, b.Z + b.SizeZ / 2.0);
        }

        /// <summary>
        /// Transforms a world-space point back to model-local space by applying
        /// the inverse of the model rotation transform.
        /// </summary>
        private Point3D TransformToLocal(Point3D worldPoint)
        {
            var transform = ModelVisual3D.Transform;
            if (transform == null || transform == Transform3D.Identity)
                return worldPoint;

            var inverse = transform.Value;
            if (!inverse.HasInverse)
                return worldPoint;

            inverse.Invert();
            return inverse.Transform(worldPoint);
        }

        /// <summary>
        /// Determines X or Y axis and positive or negative direction based on hit position
        /// relative to joystick center. Returns the PadSetting target name including "Neg" suffix
        /// for negative-direction quadrants.
        /// Model coords: X = left/right, Z = up/down.
        /// Right (+X) → pos X, Left (-X) → neg X.
        /// Y axis is inverted by NegateAxis in Step 3, so:
        /// Down (-Z) → pos Y (becomes negative output = stick down),
        /// Up (+Z) → neg Y (becomes positive output = stick up).
        /// </summary>
        private static string DetermineAxisFromQuadrant(
            Point3D hitPos, Vector3D center, string xAxis, string yAxis)
        {
            double deltaX = hitPos.X - center.X;
            double deltaZ = hitPos.Z - center.Z;
            if (Math.Abs(deltaX) > Math.Abs(deltaZ))
                return deltaX >= 0 ? xAxis : xAxis + "Neg";
            else
                // Y is inverted: up in model = neg descriptor (becomes + after NegateAxis = up in game)
                return deltaZ >= 0 ? yAxis + "Neg" : yAxis;
        }

        /// <summary>
        /// Creates a flat rectangular arrow (box shaft + triangular prism head).
        /// Arrow points along +X/-X (isX) or +Z/-Z (!isX).
        /// For X axis: neg=false → +X (right), neg=true → -X (left).
        /// For Y axis: neg=false → -Z (down), neg=true → +Z (up).
        /// Y is flipped because NegateAxis in Step 3 inverts the output.
        /// </summary>
        private static GeometryModel3D CreateFlatArrow(Point3D center, bool isX, bool negative, Color color)
        {
            double sign;
            if (isX)
                sign = negative ? -1 : 1;
            else
                // Y visual: pos descriptor → down (-Z), neg descriptor → up (+Z)
                sign = negative ? 1 : -1;
            var dir = isX ? new Vector3D(sign, 0, 0) : new Vector3D(0, 0, sign);
            var perp = isX ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0);
            var depthDir = new Vector3D(0, 1, 0);

            double shaftHalfLen = 6.0;
            double headLen = 6.0;
            double shaftW = 2.0;
            double headHalfW = 3.0;
            double depth = 2.0;
            double halfDepth = depth / 2;

            var mb = new MeshBuilder();

            // Shaft: axis-aligned box
            if (isX)
                mb.AddBox(center, shaftHalfLen * 2, depth, shaftW);
            else
                mb.AddBox(center, shaftW, depth, shaftHalfLen * 2);

            // Head: triangular prism extending from shaft end to tip
            var headBase = center + dir * shaftHalfLen;
            var tip = center + dir * (shaftHalfLen + headLen);

            // Front face vertices (Y = -halfDepth, toward camera)
            var h0f = headBase - perp * headHalfW - depthDir * halfDepth;
            var h1f = tip - depthDir * halfDepth;
            var h2f = headBase + perp * headHalfW - depthDir * halfDepth;

            // Back face vertices (Y = +halfDepth)
            var h0b = headBase - perp * headHalfW + depthDir * halfDepth;
            var h1b = tip + depthDir * halfDepth;
            var h2b = headBase + perp * headHalfW + depthDir * halfDepth;

            // Front and back triangles
            mb.AddTriangle(h0f, h1f, h2f);
            mb.AddTriangle(h2b, h1b, h0b);

            // Side quads
            mb.AddQuad(h0f, h0b, h1b, h1f);
            mb.AddQuad(h1f, h1b, h2b, h2f);
            mb.AddQuad(h2f, h2b, h0b, h0f);

            var mesh = mb.ToMesh();
            var material = new DiffuseMaterial(new SolidColorBrush(color));
            return new GeometryModel3D(mesh, material) { BackMaterial = material };
        }

        /// <summary>
        /// Shows a guidance arrow for axis recording targets (Map All, auto-prompt, or click).
        /// Non-axis targets just remove any existing arrow.
        /// </summary>
        private void ShowArrowForTarget(string target)
        {
            RemoveArrow();

            if (_currentModel == null || string.IsNullOrEmpty(target))
                return;

            // Check if this is a stick axis target (with or without Neg suffix)
            bool isNeg = target.EndsWith("Neg", StringComparison.Ordinal);
            string baseTarget = isNeg ? target.Substring(0, target.Length - 3) : target;

            bool isLeftStick = baseTarget is "LeftThumbAxisX" or "LeftThumbAxisY";
            bool isRightStick = baseTarget is "RightThumbAxisX" or "RightThumbAxisY";
            if (!isLeftStick && !isRightStick)
                return;

            bool isX = baseTarget.Contains("AxisX");

            // Anchor on the visible ring mesh's centroid so the direction
            // arrow sits over the stick the user sees, not the deflection
            // pivot which can be offset (DualSense pivot is 2.9 mm right
            // of the visible ring center).
            var ring = isLeftStick ? _currentModel.LeftThumbRing : _currentModel.RightThumbRing;
            Vector3D center = ring != null
                ? MeshCentroid(ring)
                : (isLeftStick
                    ? _currentModel.JoystickRotationPointCenterLeftMillimeter
                    : _currentModel.JoystickRotationPointCenterRightMillimeter);

            // Place arrow at stick center, floating well in front of the model surface.
            // Rotation center Y is inside the body (~-6); use a large offset to ensure
            // the arrow is clearly visible in front of the controller model.
            var arrowCenter = new Point3D(center.X, center.Y - 25, center.Z);

            var accentColor = Color.FromRgb(0x21, 0x96, 0xF3);
            try
            {
                var accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                if (accentBrush is SolidColorBrush scb) accentColor = scb.Color;
            }
            catch { }

            var arrowGeo = CreateFlatArrow(arrowCenter, isX, isNeg, accentColor);
            _arrowVisual = new ModelVisual3D { Content = arrowGeo };
            ModelVisual3D.Children.Add(_arrowVisual);
        }

        private void RemoveArrow()
        {
            if (_arrowVisual != null)
            {
                ModelVisual3D.Children.Remove(_arrowVisual);
                _arrowVisual = null;
            }
        }

        // ─────────────────────────────────────────────
        //  Quadrant highlight (flashing wedge on stick ring)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Builds a quadrant overlay from the stick ring's actual mesh triangles.
        /// Uses Sutherland-Hodgman clipping for clean diagonal edges and
        /// geometric torus-outward offset for reliable z-fighting prevention.
        /// </summary>
        private void ShowQuadrantRingOverlay(string target)
        {
            RemoveQuadrantRing();

            if (_currentModel == null || string.IsNullOrEmpty(target))
                return;

            bool isNeg = target.EndsWith("Neg", StringComparison.Ordinal);
            string baseTarget = isNeg ? target.Substring(0, target.Length - 3) : target;

            bool isLeftStick = baseTarget is "LeftThumbAxisX" or "LeftThumbAxisY";
            bool isRightStick = baseTarget is "RightThumbAxisX" or "RightThumbAxisY";
            if (!isLeftStick && !isRightStick)
                return;

            bool isX = baseTarget.Contains("AxisX");
            var ring = isLeftStick ? _currentModel.LeftThumbRing : _currentModel.RightThumbRing;
            if (ring == null) return;

            // Visible-mesh centroid, same reason as ShowHoverQuadrant.
            var center = MeshCentroid(ring);

            var quadrantMesh = BuildClippedQuadrantMesh(ring, center, isX, isNeg);
            if (quadrantMesh.Positions.Count == 0) return;

            var accentColor = Color.FromRgb(0x21, 0x96, 0xF3);
            try
            {
                var accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                if (accentBrush is SolidColorBrush scb) accentColor = scb.Color;
            }
            catch { }

            var color = Color.FromArgb(200, accentColor.R, accentColor.G, accentColor.B);
            _quadrantRingMaterial = new DiffuseMaterial(new SolidColorBrush(color));
            var quadrantGeo = new GeometryModel3D(quadrantMesh, _quadrantRingMaterial)
            {
                BackMaterial = _quadrantRingMaterial
            };
            _quadrantRingVisual = new ModelVisual3D { Content = quadrantGeo };
            ModelVisual3D.Children.Add(_quadrantRingVisual);
        }

        /// <summary>Toggles the quadrant ring overlay visibility for flashing.</summary>
        private void FlashQuadrantRing(bool on)
        {
            if (_quadrantRingVisual == null || _quadrantRingMaterial == null) return;

            var brush = (SolidColorBrush)_quadrantRingMaterial.Brush;
            var c = brush.Color;
            _quadrantRingMaterial.Brush = new SolidColorBrush(
                Color.FromArgb(on ? (byte)200 : (byte)0, c.R, c.G, c.B));
        }

        private void RemoveQuadrantRing()
        {
            if (_quadrantRingVisual != null)
            {
                ModelVisual3D.Children.Remove(_quadrantRingVisual);
                _quadrantRingVisual = null;
                _quadrantRingMaterial = null;
            }
        }

        // ─────────────────────────────────────────────
        //  Quadrant mesh building helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Builds a clipped quadrant mesh from the stick ring geometry.
        /// Uses Sutherland-Hodgman clipping for clean diagonal edges and
        /// geometric torus-outward offset to prevent z-fighting.
        /// </summary>
        private MeshGeometry3D BuildClippedQuadrantMesh(
            Model3DGroup ring, Vector3D center, bool isX, bool isNeg)
        {
            // Quadrant boundary half-planes: a*cx + b*cz >= 0
            // Each quadrant is the intersection of two half-planes at ±45°
            double a1, b1, a2, b2;
            if (isX && !isNeg)       { a1 =  1; b1 = -1; a2 =  1; b2 =  1; } // +X
            else if (isX && isNeg)   { a1 = -1; b1 = -1; a2 = -1; b2 =  1; } // -X
            else if (!isX && isNeg)  { a1 = -1; b1 =  1; a2 =  1; b2 =  1; } // +Z
            else /* !isX && !isNeg */{ a1 =  1; b1 = -1; a2 = -1; b2 = -1; } // -Z

            // Compute torus major radius (average XZ distance from center to vertices)
            double totalDist = 0;
            int vertCount = 0;
            foreach (var child in ring.Children)
            {
                if (child is not GeometryModel3D geo || geo.Geometry is not MeshGeometry3D m)
                    continue;
                foreach (Point3D p in m.Positions)
                {
                    double dx = p.X - center.X, dz = p.Z - center.Z;
                    totalDist += Math.Sqrt(dx * dx + dz * dz);
                    vertCount++;
                }
            }
            double majorR = vertCount > 0 ? totalDist / vertCount : 10.0;

            var quadrantMesh = new MeshGeometry3D();
            foreach (var child in ring.Children)
            {
                if (child is not GeometryModel3D geo || geo.Geometry is not MeshGeometry3D srcMesh)
                    continue;

                var positions = srcMesh.Positions;
                var indices = srcMesh.TriangleIndices;
                for (int t = 0; t + 2 < indices.Count; t += 3)
                {
                    var p0 = positions[indices[t]];
                    var p1 = positions[indices[t + 1]];
                    var p2 = positions[indices[t + 2]];

                    // Clip triangle against both quadrant boundary half-planes
                    var poly = new List<Point3D> { p0, p1, p2 };
                    poly = ClipPolygonByHalfPlane(poly, center, a1, b1);
                    if (poly.Count < 3) continue;
                    poly = ClipPolygonByHalfPlane(poly, center, a2, b2);
                    if (poly.Count < 3) continue;

                    // Triangulate clipped polygon as a fan and offset outward
                    for (int i = 1; i < poly.Count - 1; i++)
                    {
                        int baseIdx = quadrantMesh.Positions.Count;
                        quadrantMesh.Positions.Add(OffsetTorusOutward(poly[0], center, majorR));
                        quadrantMesh.Positions.Add(OffsetTorusOutward(poly[i], center, majorR));
                        quadrantMesh.Positions.Add(OffsetTorusOutward(poly[i + 1], center, majorR));
                        quadrantMesh.TriangleIndices.Add(baseIdx);
                        quadrantMesh.TriangleIndices.Add(baseIdx + 1);
                        quadrantMesh.TriangleIndices.Add(baseIdx + 2);
                    }
                }
            }
            return quadrantMesh;
        }

        /// <summary>
        /// Offsets a point outward from the torus surface by pushing it away
        /// from the nearest point on the torus center circle (skeleton).
        /// Works correctly for all surface orientations (top, bottom, inner, outer).
        /// </summary>
        private static Point3D OffsetTorusOutward(Point3D p, Vector3D center, double majorR)
        {
            const double offset = 0.8;
            double dx = p.X - center.X, dz = p.Z - center.Z;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist < 0.001) return p;

            // Nearest point on the center circle (in the XZ plane at center.Y)
            double sx = center.X + majorR * dx / dist;
            double sy = center.Y;
            double sz = center.Z + majorR * dz / dist;

            // Direction from skeleton point to surface point = tube outward normal
            double ox = p.X - sx, oy = p.Y - sy, oz = p.Z - sz;
            double odist = Math.Sqrt(ox * ox + oy * oy + oz * oz);
            if (odist < 0.001) return p;

            return new Point3D(
                p.X + ox / odist * offset,
                p.Y + oy / odist * offset,
                p.Z + oz / odist * offset);
        }

        /// <summary>
        /// Sutherland-Hodgman polygon clipping against a half-plane
        /// defined by a*cx + b*cz >= 0, where cx = p.X - center.X, cz = p.Z - center.Z.
        /// </summary>
        private static List<Point3D> ClipPolygonByHalfPlane(
            List<Point3D> poly, Vector3D center, double a, double b)
        {
            var result = new List<Point3D>(poly.Count + 1);
            for (int i = 0; i < poly.Count; i++)
            {
                var curr = poly[i];
                var next = poly[(i + 1) % poly.Count];
                double dCurr = a * (curr.X - center.X) + b * (curr.Z - center.Z);
                double dNext = a * (next.X - center.X) + b * (next.Z - center.Z);

                if (dCurr >= 0) // curr inside
                {
                    result.Add(curr);
                    if (dNext < 0) // next outside → intersection
                    {
                        double t = dCurr / (dCurr - dNext);
                        result.Add(LerpPoint(curr, next, t));
                    }
                }
                else if (dNext >= 0) // curr outside, next inside → intersection
                {
                    double t = dCurr / (dCurr - dNext);
                    result.Add(LerpPoint(curr, next, t));
                }
            }
            return result;
        }

        private static Point3D LerpPoint(Point3D a, Point3D b, double t)
        {
            return new Point3D(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t);
        }

        // ─────────────────────────────────────────────
        //  Map All flash animation
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            StopFlash();

            if (string.IsNullOrEmpty(target))
                return;

            // A Nintendo slot's CurrentRecordingTarget is a raw grid name
            // (RawBtn1, RawAxis0Neg); the flash machinery below speaks the
            // preview element grammar. Translate back before resolving,
            // mirroring ControllerModel2DView.UpdateFlashTarget.
            if (target.StartsWith("Raw", StringComparison.Ordinal))
            {
                target = Models2D.NintendoPreviewMap.ToPreview(target);
                if (string.IsNullOrEmpty(target)) return;
            }

            _flashTarget = target;
            _flashOn = false;

            // For stick axis targets, build a quadrant ring overlay from the actual ring mesh
            ShowQuadrantRingOverlay(target);

            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _flashTimer.Tick += FlashTick;
            _flashTimer.Start();
            FlashTick(null, EventArgs.Empty); // immediate first tick
        }

        /// <summary>
        /// Resolves a flash/recording target to the model groups that should flash.
        /// Stick axis targets (LeftThumbAxisX/Y, RightThumbAxisX/Y) all flash the stick ring.
        /// </summary>
        private List<Model3DGroup> ResolveFlashGroups(string target)
        {
            if (_currentModel == null || target == null)
                return null;

            // Stick axis targets (including *Neg variants) → flash the stick ring
            string baseTarget = target.EndsWith("Neg", StringComparison.Ordinal)
                ? target.Substring(0, target.Length - 3)
                : target;

            if (baseTarget is "LeftThumbAxisX" or "LeftThumbAxisY" && _currentModel.LeftThumbRing != null)
                return new List<Model3DGroup> { _currentModel.LeftThumbRing };
            if (baseTarget is "RightThumbAxisX" or "RightThumbAxisY" && _currentModel.RightThumbRing != null)
                return new List<Model3DGroup> { _currentModel.RightThumbRing };

            // Button targets
            if (_currentModel.ButtonMap.TryGetValue(target, out var btnGroups))
                return btnGroups;

            // ClickMap targets (triggers, etc.)
            foreach (var kv in _currentModel.ClickMap)
            {
                if (kv.Value == target)
                    return new List<Model3DGroup> { kv.Key };
            }

            return null;
        }

        private void FlashTick(object sender, EventArgs e)
        {
            if (_currentModel == null || _flashTarget == null) return;

            _flashOn = !_flashOn;

            // For stick axis targets, flash the quadrant ring overlay instead of the full ring
            if (_quadrantRingVisual != null)
            {
                FlashQuadrantRing(_flashOn);
                return;
            }

            var groups = ResolveFlashGroups(_flashTarget);
            if (groups == null) return;

            foreach (var group in groups)
            {
                if (group.Children.Count == 0 || group.Children[0] is not GeometryModel3D geo)
                    continue;

                if (_flashOn && _currentModel.HighlightMaterials.TryGetValue(group, out var hlMat))
                {
                    geo.Material = hlMat;
                    geo.BackMaterial = hlMat;
                }
                else if (_currentModel.DefaultMaterials.TryGetValue(group, out var defMat))
                {
                    geo.Material = defMat;
                    geo.BackMaterial = defMat;
                }
            }
        }

        private void StopFlash()
        {
            if (_flashTimer != null)
            {
                _flashTimer.Stop();
                _flashTimer.Tick -= FlashTick;
                _flashTimer = null;
            }

            // Restore default materials
            if (_currentModel != null && _flashTarget != null)
            {
                var groups = ResolveFlashGroups(_flashTarget);
                if (groups != null)
                {
                    foreach (var group in groups)
                    {
                        if (group.Children.Count == 0 || group.Children[0] is not GeometryModel3D geo)
                            continue;
                        if (_currentModel.DefaultMaterials.TryGetValue(group, out var defMat))
                        {
                            geo.Material = defMat;
                            geo.BackMaterial = defMat;
                        }
                    }
                }
            }

            _flashTarget = null;

            // Remove quadrant ring overlay
            RemoveQuadrantRing();
            RemoveArrow();
        }

        // ─────────────────────────────────────────────
        //  Reset View
        // ─────────────────────────────────────────────

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            if (ModelViewPort.Camera is PerspectiveCamera cam)
            {
                cam.Position = new Point3D(0, -172, 132);
                cam.LookDirection = new Vector3D(0, 0.793, -0.609);
                cam.UpDirection = new Vector3D(0, 0, 1);
                cam.FieldOfView = 55;
            }

            // Reset model rotation
            _modelYaw = 0;
            _modelPitch = 0;
            _yawRotation.Angle = 0;
            _pitchRotation.Angle = 0;
            ReprojectAnnotations();
        }

        // ─────────────────────────────────────────────
        //  Cleanup
        // ─────────────────────────────────────────────

        public void Unbind()
        {
            StopFlash();
            RemoveArrow();
            CompositionTarget.Rendering -= OnRendering;
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            TeardownAnnotations();
            _currentModel?.Dispose();
            _currentModel = null;
            _stickTransforms3D.Clear();
            _vm = null;
        }
    }
}
