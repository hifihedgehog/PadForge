using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class ControllerModel2DView : UserControl
    {
        // ─────────────────────────────────────────────
        //  Events
        // ─────────────────────────────────────────────

        public event EventHandler<string> ControllerElementRecordRequested;

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private PadViewModel _vm;
        private string _loadedModel; // "XBOX360" or "DS4"
        private bool _dirty;

        // Overlay elements
        private Image _baseImage;
        private readonly Dictionary<string, Image> _overlayImages = new();
        private readonly Dictionary<string, TranslateTransform> _stickTransforms = new();

        // Flash animation
        private DispatcherTimer _flashTimer;
        private string _flashTarget;
        private bool _flashOn;

        // Layout data
        private double _stickMaxTravel;

        public ControllerModel2DView()
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

        public void Unbind()
        {
            StopFlash();
            CompositionTarget.Rendering -= OnRendering;
            if (_vm != null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.OutputType))
            {
                Dispatcher.Invoke(EnsureModel);
                return;
            }

            if (e.PropertyName == nameof(PadViewModel.CurrentRecordingTarget))
            {
                Dispatcher.Invoke(() => UpdateFlashTarget(_vm.CurrentRecordingTarget));
                return;
            }

            _dirty = true;
        }

        // ─────────────────────────────────────────────
        //  Model lifecycle
        // ─────────────────────────────────────────────

        private void EnsureModel()
        {
            if (_vm == null) return;

            string needed = _vm.OutputType switch
            {
                VirtualControllerType.DualShock4 => "DS4",
                _ => "XBOX360"
            };

            if (_loadedModel == needed) return;
            _loadedModel = needed;

            BuildCanvas(needed);
            _dirty = true;
        }

        private void BuildCanvas(string modelName)
        {
            ModelCanvas.Children.Clear();
            _overlayImages.Clear();
            _stickTransforms.Clear();

            string folder = modelName == "DS4" ? "DS4" : "XBOX360";
            int baseW, baseH;
            string basePath;
            OverlayElement[] overlays;

            if (modelName == "DS4")
            {
                baseW = DS4Layout.BaseWidth;
                baseH = DS4Layout.BaseHeight;
                basePath = DS4Layout.BasePath;
                overlays = DS4Layout.Overlays;
                _stickMaxTravel = DS4Layout.StickMaxTravel;
            }
            else
            {
                baseW = Xbox360Layout.BaseWidth;
                baseH = Xbox360Layout.BaseHeight;
                basePath = Xbox360Layout.BasePath;
                overlays = Xbox360Layout.Overlays;
                _stickMaxTravel = Xbox360Layout.StickMaxTravel;
            }

            ModelCanvas.Width = baseW;
            ModelCanvas.Height = baseH;

            // Base image
            _baseImage = CreateImage(basePath, 0, 0, baseW, baseH);
            ModelCanvas.Children.Add(_baseImage);

            // Overlay images
            foreach (var ov in overlays)
            {
                string imgPath = $"2DModels/{folder}/{ov.ImageFile}";
                var img = CreateImage(imgPath, ov.X, ov.Y, ov.Width, ov.Height);
                img.Visibility = Visibility.Collapsed;
                img.Cursor = Cursors.Hand;
                img.Tag = ov.TargetName;

                // Stick rings get a translate transform for analog movement
                if (ov.ElementType == OverlayElementType.StickRing)
                {
                    var tt = new TranslateTransform();
                    img.RenderTransform = tt;
                    _stickTransforms[ov.TargetName] = tt;
                }

                // Click-to-record
                img.MouseLeftButtonDown += Overlay_MouseLeftButtonDown;

                // Hover highlight
                img.MouseEnter += Overlay_MouseEnter;
                img.MouseLeave += Overlay_MouseLeave;

                _overlayImages[ov.TargetName] = img;
                ModelCanvas.Children.Add(img);
            }
        }

        private static Image CreateImage(string resourcePath, double x, double y, double w, double h)
        {
            var uri = new Uri($"pack://application:,,,/{resourcePath}", UriKind.Absolute);
            var bitmap = new BitmapImage(uri);
            var img = new Image
            {
                Source = bitmap,
                Width = w,
                Height = h,
                Stretch = Stretch.Fill,
            };
            Canvas.SetLeft(img, x);
            Canvas.SetTop(img, y);
            return img;
        }

        // ─────────────────────────────────────────────
        //  Render-frame batched update
        // ─────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_dirty || _vm == null || _loadedModel == null)
                return;
            _dirty = false;

            UpdateButtons();
            UpdateTriggers();
            UpdateSticks();
        }

        private void UpdateButtons()
        {
            SetOverlayVisible("ButtonA", _vm.ButtonA);
            SetOverlayVisible("ButtonB", _vm.ButtonB);
            SetOverlayVisible("ButtonX", _vm.ButtonX);
            SetOverlayVisible("ButtonY", _vm.ButtonY);
            SetOverlayVisible("DPadUp", _vm.DPadUp);
            SetOverlayVisible("DPadDown", _vm.DPadDown);
            SetOverlayVisible("DPadLeft", _vm.DPadLeft);
            SetOverlayVisible("DPadRight", _vm.DPadRight);
            SetOverlayVisible("LeftShoulder", _vm.LeftShoulder);
            SetOverlayVisible("RightShoulder", _vm.RightShoulder);
            SetOverlayVisible("ButtonBack", _vm.ButtonBack);
            SetOverlayVisible("ButtonStart", _vm.ButtonStart);
            SetOverlayVisible("ButtonGuide", _vm.ButtonGuide);
            SetOverlayVisible("LeftThumbButton", _vm.LeftThumbButton);
            SetOverlayVisible("RightThumbButton", _vm.RightThumbButton);

            // DS4 FaceButtonGroup: visible when ANY face button is pressed
            if (_overlayImages.ContainsKey("FaceButtonGroup"))
            {
                bool anyFace = _vm.ButtonA || _vm.ButtonB || _vm.ButtonX || _vm.ButtonY;
                SetOverlayVisible("FaceButtonGroup", anyFace);
            }
        }

        private void UpdateTriggers()
        {
            SetOverlayOpacity("LeftTrigger", _vm.LeftTrigger);
            SetOverlayOpacity("RightTrigger", _vm.RightTrigger);
        }

        private void UpdateSticks()
        {
            // Normalize short (-32768..32767) to -1..1
            double lx = _vm.RawThumbLX / 32767.0;
            double ly = _vm.RawThumbLY / 32767.0;
            double rx = _vm.RawThumbRX / 32767.0;
            double ry = _vm.RawThumbRY / 32767.0;

            if (_stickTransforms.TryGetValue("LeftThumbRing", out var lt))
            {
                lt.X = lx * _stickMaxTravel;
                lt.Y = -ly * _stickMaxTravel; // Y is inverted (up = negative in screen coords)
            }
            if (_stickTransforms.TryGetValue("RightThumbRing", out var rt))
            {
                rt.X = rx * _stickMaxTravel;
                rt.Y = -ry * _stickMaxTravel;
            }

            // Show stick ring overlays always (they represent the current position)
            SetOverlayVisible("LeftThumbRing", true);
            SetOverlayVisible("RightThumbRing", true);
        }

        private void SetOverlayVisible(string target, bool visible)
        {
            if (_overlayImages.TryGetValue(target, out var img))
            {
                // Don't override flash animation visibility
                if (_flashTarget == target) return;
                img.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible) img.Opacity = 1.0;
            }
        }

        private void SetOverlayOpacity(string target, double value)
        {
            if (_overlayImages.TryGetValue(target, out var img))
            {
                if (_flashTarget == target) return;
                img.Visibility = value > 0.01 ? Visibility.Visible : Visibility.Collapsed;
                img.Opacity = Math.Clamp(value, 0.0, 1.0);
            }
        }

        // ─────────────────────────────────────────────
        //  Click-to-record
        // ─────────────────────────────────────────────

        private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Image img && img.Tag is string target)
            {
                // For stick rings, determine axis from click quadrant
                if (target == "LeftThumbRing" || target == "RightThumbRing")
                {
                    var pos = e.GetPosition(img);
                    string axis = DetermineAxisFromQuadrant(pos, img.Width, img.Height, target);
                    ControllerElementRecordRequested?.Invoke(this, axis);
                }
                // For face button group, determine which face button from click position
                else if (target == "FaceButtonGroup")
                {
                    var pos = e.GetPosition(img);
                    string button = DetermineFaceButtonFromPosition(pos, img.Width, img.Height);
                    ControllerElementRecordRequested?.Invoke(this, button);
                }
                else
                {
                    ControllerElementRecordRequested?.Invoke(this, target);
                }
                e.Handled = true;
            }
        }

        private static string DetermineAxisFromQuadrant(Point pos, double w, double h, string stickTarget)
        {
            double cx = w / 2, cy = h / 2;
            double dx = pos.X - cx, dy = pos.Y - cy;

            bool isLeft = stickTarget == "LeftThumbRing";
            string xAxis = isLeft ? "LeftThumbAxisX" : "RightThumbAxisX";
            string yAxis = isLeft ? "LeftThumbAxisY" : "RightThumbAxisY";

            if (Math.Abs(dx) >= Math.Abs(dy))
                return dx >= 0 ? xAxis : xAxis + "Neg";
            else
                return dy >= 0 ? yAxis : yAxis + "Neg"; // Down = positive Y (screen coords, inverted by Step 3)
        }

        private static string DetermineFaceButtonFromPosition(Point pos, double w, double h)
        {
            // DS4 face buttons: Triangle=top, Cross=bottom, Square=left, Circle=right
            double cx = w / 2, cy = h / 2;
            double dx = pos.X - cx, dy = pos.Y - cy;

            if (Math.Abs(dx) >= Math.Abs(dy))
                return dx >= 0 ? "ButtonB" : "ButtonX"; // Circle=right, Square=left
            else
                return dy >= 0 ? "ButtonA" : "ButtonY"; // Cross=bottom, Triangle=top
        }

        // ─────────────────────────────────────────────
        //  Hover highlight
        // ─────────────────────────────────────────────

        private void Overlay_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Image img)
                img.Opacity = 0.7;
        }

        private void Overlay_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Image img && img.Tag is string target)
            {
                // Restore opacity based on current state
                if (_flashTarget == target) return;

                if (target == "LeftThumbRing" || target == "RightThumbRing")
                    img.Opacity = 1.0;
                else if (target == "LeftTrigger")
                    img.Opacity = _vm?.LeftTrigger ?? 0;
                else if (target == "RightTrigger")
                    img.Opacity = _vm?.RightTrigger ?? 0;
                else
                    img.Opacity = 1.0;
            }
        }

        // ─────────────────────────────────────────────
        //  Flash animation (Map All)
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            StopFlash();
            if (string.IsNullOrEmpty(target)) return;

            _flashTarget = ResolveFlashTarget(target);
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _flashTimer.Tick += FlashTick;
            _flashTimer.Start();
            FlashTick(null, EventArgs.Empty);
        }

        private string ResolveFlashTarget(string target)
        {
            // Axis targets -> stick ring
            if (target.Contains("LeftThumbAxis")) return "LeftThumbRing";
            if (target.Contains("RightThumbAxis")) return "RightThumbRing";
            return target;
        }

        private void FlashTick(object sender, EventArgs e)
        {
            _flashOn = !_flashOn;
            if (_flashTarget != null && _overlayImages.TryGetValue(_flashTarget, out var img))
            {
                img.Visibility = _flashOn ? Visibility.Visible : Visibility.Collapsed;
                if (_flashOn) img.Opacity = 1.0;
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

            // Restore the flashed element to its default state
            if (_flashTarget != null && _overlayImages.TryGetValue(_flashTarget, out var img))
            {
                img.Visibility = Visibility.Collapsed;
                img.Opacity = 1.0;
            }
            _flashTarget = null;
            _flashOn = false;
        }
    }
}
