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

        // Quadrant highlight overlay (flashing wedge on stick ring)
        private ModelVisual3D _quadrantVisual;
        private DiffuseMaterial _quadrantMaterial;

        // Hover highlight state
        private Model3DGroup _hoverGroup;            // Currently highlighted group (button/trigger)
        private Model3DGroup _hoverStickRing;         // Currently highlighted stick ring (for quadrant)
        private string _hoverQuadrant;                // Current quadrant axis string (e.g., "LeftThumbAxisXNeg")
        private ModelVisual3D _hoverQuadrantVisual;    // Quadrant wedge overlay for hover

        public ControllerModelView()
        {
            InitializeComponent();
            CompositionTarget.Rendering += OnRendering;
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
            }
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Model type change
            if (e.PropertyName == nameof(PadViewModel.OutputType))
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
                    // Always show the arrow and quadrant highlight for the current target.
                    ShowArrowForTarget(target);
                    ShowQuadrantForTarget(target);
                });
                return;
            }

            // Any controller state property — mark dirty for next render frame
            _dirty = true;
        }

        // ─────────────────────────────────────────────
        //  Model lifecycle
        // ─────────────────────────────────────────────

        private void EnsureModel()
        {
            if (_vm == null) return;

            var type = _vm.OutputType;
            string needed = type switch
            {
                VirtualControllerType.DualShock4 => "DS4",
                _ => "XBOX360"     // Xbox360 and VJoy both use Xbox model
            };

            if (_currentModel?.ModelName == needed)
                return;

            // Clear arrow from old model before switching
            RemoveArrow();

            _currentModel?.Dispose();
            _currentModel = null;

            try
            {
                _currentModel = needed switch
                {
                    "DS4" => new ControllerModelDS4(),
                    _ => new ControllerModelXbox360()
                };

                ModelVisual3D.Content = _currentModel.model3DGroup;
                ModelViewPort.ZoomExtents();
                _dirty = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ControllerModelView] Failed to load 3D model: {ex}");
            }
        }

        // ─────────────────────────────────────────────
        //  Render-frame batched update
        // ─────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
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
                    geo.Material = GradientHighlight(defMat, hlMat, factor);
                }
                else if (_currentModel.DefaultMaterials.TryGetValue(thumbRing, out var def))
                {
                    geo.Material = def;
                }
            }

            // Rotation
            float angleX = maxAngleDeg * normX;
            float angleY = -maxAngleDeg * normY;

            var rotX = new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(0, 0, 1), angleX),
                new Point3D(rotationPoint.X, rotationPoint.Y, rotationPoint.Z));
            var rotY = new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(1, 0, 0), angleY),
                new Point3D(rotationPoint.X, rotationPoint.Y, rotationPoint.Z));

            var group = new Transform3DGroup();
            group.Children.Add(rotX);
            group.Children.Add(rotY);

            thumbRing.Transform = group;
            if (thumb != null) thumb.Transform = group;
        }

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

            float value = (float)triggerNorm;

            // Gradient color
            if (triggerModel.Children.Count > 0 && triggerModel.Children[0] is GeometryModel3D geo)
            {
                if (value > 0 && _currentModel.DefaultMaterials.TryGetValue(triggerModel, out var defMat)
                              && _currentModel.HighlightMaterials.TryGetValue(triggerModel, out var hlMat))
                {
                    geo.Material = GradientHighlight(defMat, hlMat, value);
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

        private static DiffuseMaterial GradientHighlight(Material defaultMaterial, Material highlightMaterial, float factor)
        {
            factor = Math.Clamp(factor, 0f, 1f);
            var startColor = ((SolidColorBrush)((DiffuseMaterial)defaultMaterial).Brush).Color;
            var endColor = ((SolidColorBrush)((DiffuseMaterial)highlightMaterial).Brush).Color;

            byte a = (byte)(startColor.A * (1 - factor) + endColor.A * factor);
            byte r = (byte)(startColor.R * (1 - factor) + endColor.R * factor);
            byte g = (byte)(startColor.G * (1 - factor) + endColor.G * factor);
            byte b = (byte)(startColor.B * (1 - factor) + endColor.B * factor);

            return new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(a, r, g, b)));
        }

        // ─────────────────────────────────────────────
        //  Click-to-record hit testing
        // ─────────────────────────────────────────────

        private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentModel == null) return;

            var pos = e.GetPosition(ModelViewPort);
            var hits = Viewport3DHelper.FindHits(ModelViewPort.Viewport, pos);

            foreach (var hit in hits)
            {
                if (hit.Model is not GeometryModel3D hitGeo)
                    continue;

                // Check if hit is on a stick ring — use quadrant detection for X vs Y
                if (IsStickRingHit(hitGeo, hit.Position, out string stickAxis))
                {
                    ControllerElementRecordRequested?.Invoke(this, stickAxis);
                    ShowAxisArrow(hit.Position, stickAxis);
                    e.Handled = true;
                    return;
                }

                // Walk ClickMap to find which Model3DGroup contains this geometry
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
        //  Hover highlighting
        // ─────────────────────────────────────────────

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (_currentModel == null) return;

            var pos = e.GetPosition(ModelViewPort);
            var hits = Viewport3DHelper.FindHits(ModelViewPort.Viewport, pos);

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

                    // Determine which stick ring this belongs to
                    bool isLeft = quadrant.StartsWith("Left", StringComparison.Ordinal);
                    _hoverStickRing = isLeft ? _currentModel.LeftThumbRing : _currentModel.RightThumbRing;

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
            ClearHover();
        }

        private void ApplyHoverHighlight(Model3DGroup group)
        {
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

            bool isNeg = target.EndsWith("Neg", StringComparison.Ordinal);
            string baseTarget = isNeg ? target.Substring(0, target.Length - 3) : target;
            bool isX = baseTarget.Contains("AxisX");
            bool isLeft = baseTarget.StartsWith("Left", StringComparison.Ordinal);

            Vector3D center = isLeft
                ? _currentModel.JoystickRotationPointCenterLeftMillimeter
                : _currentModel.JoystickRotationPointCenterRightMillimeter;

            double angleDeg;
            if (isX)
                angleDeg = isNeg ? 180.0 : 0.0;
            else
                angleDeg = isNeg ? 90.0 : 270.0;

            var accentColor = Color.FromRgb(0x21, 0x96, 0xF3);
            try
            {
                var accentBrush = (Brush)Application.Current.Resources["AccentButtonBackground"];
                if (accentBrush is SolidColorBrush scb) accentColor = scb.Color;
            }
            catch { }

            var color = Color.FromArgb(100, accentColor.R, accentColor.G, accentColor.B);
            var material = new DiffuseMaterial(new SolidColorBrush(color));
            var wedgeCenter = new Point3D(center.X, center.Y - 25, center.Z);
            var mesh = CreateWedgeMesh(wedgeCenter, radius: 10.0, angleDeg - 45, angleDeg + 45, segments: 12);
            var geo = new GeometryModel3D(mesh, material) { BackMaterial = material };
            _hoverQuadrantVisual = new ModelVisual3D { Content = geo };
            ModelViewPort.Children.Add(_hoverQuadrantVisual);
        }

        private void RemoveHoverQuadrant()
        {
            if (_hoverQuadrantVisual != null)
            {
                ModelViewPort.Children.Remove(_hoverQuadrantVisual);
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
                _hoverStickRing = null;
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

            // Check left stick ring
            if (_currentModel.LeftThumbRing?.Children.Contains(hitGeo) == true)
            {
                var center = _currentModel.JoystickRotationPointCenterLeftMillimeter;
                axis = DetermineAxisFromQuadrant(hitPos, center, "LeftThumbAxisX", "LeftThumbAxisY");
                return true;
            }

            // Check right stick ring
            if (_currentModel.RightThumbRing?.Children.Contains(hitGeo) == true)
            {
                var center = _currentModel.JoystickRotationPointCenterRightMillimeter;
                axis = DetermineAxisFromQuadrant(hitPos, center, "RightThumbAxisX", "RightThumbAxisY");
                return true;
            }

            return false;
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
        /// Shows a flat 3D arrow at the stick indicating the axis direction.
        /// Called when the user clicks on a stick ring — uses the actual hit Y position.
        /// </summary>
        private void ShowAxisArrow(Point3D hitPos, string axis)
        {
            ShowArrowForTarget(axis);
            ShowQuadrantForTarget(axis);
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

            Vector3D center = isLeftStick
                ? _currentModel.JoystickRotationPointCenterLeftMillimeter
                : _currentModel.JoystickRotationPointCenterRightMillimeter;

            // Place arrow at stick center, floating well in front of the model surface.
            // Rotation center Y is inside the body (~-6); use a large offset to ensure
            // the arrow is clearly visible in front of the controller model.
            var arrowCenter = new Point3D(center.X, center.Y - 25, center.Z);

            var accentColor = Color.FromRgb(0x21, 0x96, 0xF3);
            try
            {
                var accentBrush = (Brush)Application.Current.Resources["AccentButtonBackground"];
                if (accentBrush is SolidColorBrush scb) accentColor = scb.Color;
            }
            catch { }

            var arrowGeo = CreateFlatArrow(arrowCenter, isX, isNeg, accentColor);
            _arrowVisual = new ModelVisual3D { Content = arrowGeo };
            ModelViewPort.Children.Add(_arrowVisual);
        }

        private void RemoveArrow()
        {
            if (_arrowVisual != null)
            {
                ModelViewPort.Children.Remove(_arrowVisual);
                _arrowVisual = null;
            }
        }

        // ─────────────────────────────────────────────
        //  Quadrant highlight (flashing wedge on stick ring)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Shows or updates a semi-transparent wedge over one quadrant of a stick ring.
        /// The wedge indicates which direction the user should push for recording.
        /// </summary>
        private void ShowQuadrantForTarget(string target)
        {
            RemoveQuadrant();

            if (_currentModel == null || string.IsNullOrEmpty(target))
                return;

            bool isNeg = target.EndsWith("Neg", StringComparison.Ordinal);
            string baseTarget = isNeg ? target.Substring(0, target.Length - 3) : target;

            bool isLeftStick = baseTarget is "LeftThumbAxisX" or "LeftThumbAxisY";
            bool isRightStick = baseTarget is "RightThumbAxisX" or "RightThumbAxisY";
            if (!isLeftStick && !isRightStick)
                return;

            bool isX = baseTarget.Contains("AxisX");

            Vector3D center = isLeftStick
                ? _currentModel.JoystickRotationPointCenterLeftMillimeter
                : _currentModel.JoystickRotationPointCenterRightMillimeter;

            // Determine the angular center of the quadrant (degrees from +X, CCW).
            // X axis: pos(right)=0°, neg(left)=180°.
            // Y axis (NegateAxis): neg(up)=90°, pos(down)=270°.
            double angleDeg;
            if (isX)
                angleDeg = isNeg ? 180.0 : 0.0;
            else
                angleDeg = isNeg ? 90.0 : 270.0;

            var accentColor = Color.FromRgb(0x21, 0x96, 0xF3);
            try
            {
                var accentBrush = (Brush)Application.Current.Resources["AccentButtonBackground"];
                if (accentBrush is SolidColorBrush scb) accentColor = scb.Color;
            }
            catch { }

            // Semi-transparent accent blue
            var color = Color.FromArgb(160, accentColor.R, accentColor.G, accentColor.B);
            _quadrantMaterial = new DiffuseMaterial(new SolidColorBrush(color));

            // Build a pie-wedge disc: 90° arc from (angleDeg-45) to (angleDeg+45)
            var wedgeCenter = new Point3D(center.X, center.Y - 25, center.Z);
            var mesh = CreateWedgeMesh(wedgeCenter, radius: 10.0, angleDeg - 45, angleDeg + 45, segments: 12);
            var geo = new GeometryModel3D(mesh, _quadrantMaterial) { BackMaterial = _quadrantMaterial };
            _quadrantVisual = new ModelVisual3D { Content = geo };
            ModelViewPort.Children.Add(_quadrantVisual);
        }

        /// <summary>
        /// Creates a flat pie-wedge mesh in the XZ plane at the given Y height.
        /// Angles are in degrees, measured from +X axis (right), counter-clockwise.
        /// +X = right, +Z = up on screen (model coordinates).
        /// </summary>
        private static MeshGeometry3D CreateWedgeMesh(Point3D center, double radius,
            double startDeg, double endDeg, int segments)
        {
            var mesh = new MeshGeometry3D();
            double startRad = startDeg * Math.PI / 180.0;
            double step = (endDeg - startDeg) * Math.PI / 180.0 / segments;

            // Center vertex
            mesh.Positions.Add(center);

            // Arc vertices
            for (int i = 0; i <= segments; i++)
            {
                double angle = startRad + step * i;
                mesh.Positions.Add(new Point3D(
                    center.X + radius * Math.Cos(angle),
                    center.Y,
                    center.Z + radius * Math.Sin(angle)));
            }

            // Triangle fan from center to arc
            for (int i = 0; i < segments; i++)
            {
                mesh.TriangleIndices.Add(0);
                mesh.TriangleIndices.Add(i + 1);
                mesh.TriangleIndices.Add(i + 2);
                // Back face
                mesh.TriangleIndices.Add(0);
                mesh.TriangleIndices.Add(i + 2);
                mesh.TriangleIndices.Add(i + 1);
            }

            return mesh;
        }

        /// <summary>Toggles the quadrant wedge visibility for flashing.</summary>
        private void FlashQuadrant(bool on)
        {
            if (_quadrantVisual == null || _quadrantMaterial == null) return;

            var brush = (SolidColorBrush)_quadrantMaterial.Brush;
            var c = brush.Color;
            // Toggle between visible (alpha 160) and hidden (alpha 0).
            _quadrantMaterial.Brush = new SolidColorBrush(
                Color.FromArgb(on ? (byte)160 : (byte)0, c.R, c.G, c.B));
        }

        private void RemoveQuadrant()
        {
            if (_quadrantVisual != null)
            {
                ModelViewPort.Children.Remove(_quadrantVisual);
                _quadrantVisual = null;
                _quadrantMaterial = null;
            }
        }

        // ─────────────────────────────────────────────
        //  Map All flash animation
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            StopFlash();

            if (string.IsNullOrEmpty(target))
                return;

            _flashTarget = target;
            _flashOn = false;
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

            // Flash the quadrant wedge in sync
            FlashQuadrant(_flashOn);
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

            // Remove quadrant overlay
            RemoveQuadrant();
            RemoveArrow();
        }

        // ─────────────────────────────────────────────
        //  Reset View
        // ─────────────────────────────────────────────

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            if (ModelViewPort.Camera is PerspectiveCamera cam)
            {
                cam.Position = new Point3D(0, -173, 100);
                cam.LookDirection = new Vector3D(0, 0.866, -0.5);
                cam.UpDirection = new Vector3D(0, 0, 1);
                cam.FieldOfView = 35;
            }
            ModelViewPort.ZoomExtents();
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
            _currentModel?.Dispose();
            _currentModel = null;
            _vm = null;
        }
    }
}
