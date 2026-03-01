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
                    UpdateFlashTarget(_vm.CurrentRecordingTarget);
                    // Remove axis arrow when recording target changes or clears
                    RemoveArrow();
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
        /// Determines X or Y axis based on hit position relative to joystick center.
        /// Model coords: X = left/right, Z = up/down.
        /// |deltaX| > |deltaZ| → horizontal click → X axis.
        /// |deltaZ| >= |deltaX| → vertical click → Y axis.
        /// </summary>
        private static string DetermineAxisFromQuadrant(
            Point3D hitPos, Vector3D center, string xAxis, string yAxis)
        {
            double deltaX = hitPos.X - center.X;
            double deltaZ = hitPos.Z - center.Z;
            return Math.Abs(deltaX) > Math.Abs(deltaZ) ? xAxis : yAxis;
        }

        /// <summary>
        /// Shows a flat 3D arrow at the stick indicating the positive axis direction.
        /// X axis → arrow pointing right (+X), Y axis → arrow pointing up (+Z).
        /// Arrow stays visible until mapping finishes (CurrentRecordingTarget clears).
        /// </summary>
        private void ShowAxisArrow(Point3D hitPos, string axis)
        {
            RemoveArrow();

            bool isX = axis.Contains("AxisX");

            Vector3D center;
            if (axis.StartsWith("Left"))
                center = _currentModel.JoystickRotationPointCenterLeftMillimeter;
            else
                center = _currentModel.JoystickRotationPointCenterRightMillimeter;

            // Place arrow at stick center X/Z, raised to hit surface toward camera
            var arrowCenter = new Point3D(center.X, hitPos.Y - 3, center.Z);

            var accentColor = Color.FromRgb(0x21, 0x96, 0xF3);
            try
            {
                var accentBrush = (Brush)Application.Current.Resources["AccentButtonBackground"];
                if (accentBrush is SolidColorBrush scb) accentColor = scb.Color;
            }
            catch { }

            var arrowGeo = CreateFlatArrow(arrowCenter, isX, accentColor);
            _arrowVisual = new ModelVisual3D { Content = arrowGeo };
            ModelViewPort.Children.Add(_arrowVisual);
        }

        /// <summary>
        /// Creates a flat rectangular arrow shape (extruded 2D arrow profile with Y depth).
        /// Arrow points along +X (isX=true) or +Z (isX=false).
        /// </summary>
        private static GeometryModel3D CreateFlatArrow(Point3D center, bool isX, Color color)
        {
            // Direction the arrow points and perpendicular (width) axis
            var dir = isX ? new Vector3D(1, 0, 0) : new Vector3D(0, 0, 1);
            var perp = isX ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0);
            var depthVec = new Vector3D(0, 1, 0);

            double shaftHalfLen = 6.0;
            double headLen = 6.0;
            double shaftHalfW = 1.0;
            double headHalfW = 3.0;
            double halfDepth = 1.0;

            // 7 vertices of the arrow outline (shaft + triangular head)
            var outline = new Point3D[]
            {
                center - dir * shaftHalfLen - perp * shaftHalfW,  // 0: tail bottom
                center + dir * shaftHalfLen - perp * shaftHalfW,  // 1: shaft-head join bottom
                center + dir * shaftHalfLen - perp * headHalfW,   // 2: head base bottom
                center + dir * (shaftHalfLen + headLen),           // 3: tip
                center + dir * shaftHalfLen + perp * headHalfW,   // 4: head base top
                center + dir * shaftHalfLen + perp * shaftHalfW,  // 5: shaft-head join top
                center - dir * shaftHalfLen + perp * shaftHalfW,  // 6: tail top
            };

            // Extrude: front face at -Y (toward camera), back face at +Y
            var front = new Point3D[7];
            var back = new Point3D[7];
            for (int i = 0; i < 7; i++)
            {
                front[i] = outline[i] - depthVec * halfDepth;
                back[i] = outline[i] + depthVec * halfDepth;
            }

            var mb = new MeshBuilder();

            // Front face (facing camera at -Y)
            mb.AddPolygon(new List<Point3D> {
                front[0], front[1], front[2], front[3], front[4], front[5], front[6]
            });

            // Back face (reversed winding)
            mb.AddPolygon(new List<Point3D> {
                back[6], back[5], back[4], back[3], back[2], back[1], back[0]
            });

            // Side quads connecting front to back edges
            for (int i = 0; i < 7; i++)
            {
                int next = (i + 1) % 7;
                mb.AddQuad(front[next], front[i], back[i], back[next]);
            }

            var mesh = mb.ToMesh();
            var material = new DiffuseMaterial(new SolidColorBrush(color));
            return new GeometryModel3D(mesh, material) { BackMaterial = material };
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

            // Stick axis targets → flash the ring
            if (target is "LeftThumbAxisX" or "LeftThumbAxisY" && _currentModel.LeftThumbRing != null)
                return new List<Model3DGroup> { _currentModel.LeftThumbRing };
            if (target is "RightThumbAxisX" or "RightThumbAxisY" && _currentModel.RightThumbRing != null)
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
        }

        // ─────────────────────────────────────────────
        //  Reset View
        // ─────────────────────────────────────────────

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
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
