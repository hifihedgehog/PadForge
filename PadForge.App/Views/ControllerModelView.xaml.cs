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

            // Map All flash target change
            if (e.PropertyName == nameof(PadViewModel.CurrentRecordingTarget))
            {
                Dispatcher.Invoke(() => UpdateFlashTarget(_vm.CurrentRecordingTarget));
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
        //  Map All flash animation
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            StopFlash();

            if (string.IsNullOrEmpty(target))
                return;

            // Axis Y targets resolve to the stick ring (same visual part)
            _flashTarget = target switch
            {
                "LeftThumbAxisY" => "LeftThumbAxisX",
                "RightThumbAxisY" => "RightThumbAxisX",
                _ => target
            };

            _flashOn = false;
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _flashTimer.Tick += FlashTick;
            _flashTimer.Start();
            FlashTick(null, EventArgs.Empty); // immediate first tick
        }

        private void FlashTick(object sender, EventArgs e)
        {
            if (_currentModel == null || _flashTarget == null) return;

            _flashOn = !_flashOn;

            // Find model groups for this target
            List<Model3DGroup> groups = null;

            if (_currentModel.ButtonMap.TryGetValue(_flashTarget, out var btnGroups))
                groups = btnGroups;
            else
            {
                // Check ClickMap for axis/trigger targets
                foreach (var kv in _currentModel.ClickMap)
                {
                    if (kv.Value == _flashTarget)
                    {
                        groups = new List<Model3DGroup> { kv.Key };
                        break;
                    }
                }
            }

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
                List<Model3DGroup> groups = null;
                if (_currentModel.ButtonMap.TryGetValue(_flashTarget, out var btnGroups))
                    groups = btnGroups;
                else
                {
                    foreach (var kv in _currentModel.ClickMap)
                    {
                        if (kv.Value == _flashTarget)
                        {
                            groups = new List<Model3DGroup> { kv.Key };
                            break;
                        }
                    }
                }

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
            CompositionTarget.Rendering -= OnRendering;
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            _currentModel?.Dispose();
            _currentModel = null;
            _vm = null;
        }
    }
}
