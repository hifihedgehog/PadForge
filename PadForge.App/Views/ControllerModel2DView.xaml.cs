using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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

        // Visual overlay elements
        private Image _baseImage;
        private readonly Dictionary<string, Image> _overlayImages = new();
        private readonly Dictionary<string, TranslateTransform> _stickTransforms = new();
        private readonly Dictionary<string, RectangleGeometry> _triggerClips = new();
        private readonly Dictionary<string, OverlayElementType> _elementTypes = new();

        // Flash animation
        private DispatcherTimer _flashTimer;
        private string _flashTarget;
        private string _flashRawTarget; // Original target before resolution (e.g., "LeftThumbAxisXNeg")
        private Geometry _flashStickClip; // Stored clip for re-application on each tick
        private bool _flashOn;

        // Hover state
        private string _hoverTarget;

        // Stick quadrant highlight (uses stick click overlay image, clipped to quadrant)
        private readonly Dictionary<string, Image> _stickHighlights = new();

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
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
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
                VirtualControllerType.VJoy when _vm.VJoyConfig?.Preset == VJoyPreset.DualShock4 => "DS4",
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
            _triggerClips.Clear();
            _elementTypes.Clear();
            _stickHighlights.Clear();
            _hoverTarget = null;

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

            // Base image (Z=0)
            _baseImage = CreateImage(basePath, 0, 0, baseW, baseH);
            ModelCanvas.Children.Add(_baseImage);

            // Overlay images (Z=1) + hit-test rectangles (Z=10)
            foreach (var ov in overlays)
            {
                string imgPath = $"2DModels/{folder}/{ov.ImageFile}";
                var img = CreateImage(imgPath, ov.X, ov.Y, ov.Width, ov.Height);
                img.IsHitTestVisible = false; // Hit rect handles clicks
                _elementTypes[ov.TargetName] = ov.ElementType;

                if (ov.ElementType == OverlayElementType.StickRing)
                {
                    // Always visible, translates with stick input
                    img.Visibility = Visibility.Visible;
                    var tt = new TranslateTransform();
                    img.RenderTransform = tt;
                    _stickTransforms[ov.TargetName] = tt;
                }
                else if (ov.ElementType == OverlayElementType.Trigger)
                {
                    // Always visible, clip controls fill level (gas tank effect)
                    img.Visibility = Visibility.Visible;
                    img.Opacity = 1.0;
                    var clip = new RectangleGeometry(new Rect(0, ov.Height, ov.Width, 0));
                    img.Clip = clip;
                    _triggerClips[ov.TargetName] = clip;
                }
                else
                {
                    // Buttons, StickClicks: hidden until pressed
                    img.Visibility = Visibility.Collapsed;
                }

                Panel.SetZIndex(img, 1);
                _overlayImages[ov.TargetName] = img;
                ModelCanvas.Children.Add(img);

                // StickClick: no hit-test rect — handled by StickRing's center-click detection
                if (ov.ElementType == OverlayElementType.StickClick)
                    continue;

                // Hit-test rectangle (always visible, transparent, catches all clicks)
                var hitRect = new Rectangle
                {
                    Width = ov.Width,
                    Height = ov.Height,
                    Fill = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Tag = ov.TargetName,
                };
                Canvas.SetLeft(hitRect, ov.X);
                Canvas.SetTop(hitRect, ov.Y);
                Panel.SetZIndex(hitRect, 10);
                hitRect.MouseLeftButtonDown += HitArea_Click;
                hitRect.MouseEnter += HitArea_MouseEnter;
                hitRect.MouseLeave += HitArea_MouseLeave;
                ModelCanvas.Children.Add(hitRect);

                if (ov.ElementType == OverlayElementType.StickRing)
                    hitRect.MouseMove += StickHitArea_MouseMove;
            }

            // Create stick quadrant highlights using the stick click overlay image
            foreach (var ov in overlays)
            {
                if (ov.ElementType != OverlayElementType.StickClick) continue;

                // Map stick click target to its ring target
                string ringTarget = ov.TargetName == "LeftThumbButton" ? "LeftThumbRing" : "RightThumbRing";

                string clickImgPath = $"2DModels/{folder}/{ov.ImageFile}";
                var highlight = CreateImage(clickImgPath, ov.X, ov.Y, ov.Width, ov.Height);
                highlight.IsHitTestVisible = false;
                highlight.Opacity = 0.4;
                highlight.Visibility = Visibility.Collapsed;
                Panel.SetZIndex(highlight, 5);
                _stickHighlights[ringTarget] = highlight;
                ModelCanvas.Children.Add(highlight);
            }
        }

        private static Image CreateImage(string resourcePath, double x, double y, double w, double h)
        {
            // Load from embedded resource stream — avoids pack:// URI scheme issues
            // on .NET 10 single-file publish where PackUriHelper.ValidatePartUri fails.
            var sri = Application.GetResourceStream(new Uri(resourcePath, UriKind.Relative));
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = sri.Stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            sri.Stream.Dispose();
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
        }

        private void UpdateTriggers()
        {
            SetTriggerFill("LeftTrigger", _vm.LeftTrigger);
            SetTriggerFill("RightTrigger", _vm.RightTrigger);
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
        }

        private void SetOverlayVisible(string target, bool visible)
        {
            if (_overlayImages.TryGetValue(target, out var img))
            {
                if (_flashTarget == target || _hoverTarget == target) return;
                img.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible) img.Opacity = 1.0;
            }
        }

        private void SetTriggerFill(string target, double value)
        {
            if (_triggerClips.TryGetValue(target, out var clip) &&
                _overlayImages.TryGetValue(target, out var img))
            {
                if (_flashTarget == target || _hoverTarget == target) return;
                double h = img.Height;
                double w = img.Width;
                double v = Math.Clamp(value, 0.0, 1.0);
                double clipY = h * (1.0 - v);
                clip.Rect = new Rect(0, clipY, w, h - clipY);
                img.Opacity = 1.0;
            }
        }

        // ─────────────────────────────────────────────
        //  Click-to-record (via hit-test rectangles)
        // ─────────────────────────────────────────────

        private void HitArea_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Rectangle rect && rect.Tag is string target)
            {
                _elementTypes.TryGetValue(target, out var elemType);

                if (elemType == OverlayElementType.StickRing)
                {
                    var pos = e.GetPosition(rect);
                    string axis = DetermineAxisFromQuadrant(pos, rect.Width, rect.Height, target);
                    ControllerElementRecordRequested?.Invoke(this, axis);
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
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double radius = Math.Min(w, h) / 2;

            bool isLeft = stickTarget == "LeftThumbRing";

            // Center click → stick button (L3/R3)
            if (dist < radius * 0.3)
                return isLeft ? "LeftThumbButton" : "RightThumbButton";

            string xAxis = isLeft ? "LeftThumbAxisX" : "RightThumbAxisX";
            string yAxis = isLeft ? "LeftThumbAxisY" : "RightThumbAxisY";

            if (Math.Abs(dx) >= Math.Abs(dy))
                return dx >= 0 ? xAxis : xAxis + "Neg";
            else
                return dy >= 0 ? yAxis : yAxis + "Neg"; // Down = positive Y (screen coords, inverted by Step 3)
        }

        // ─────────────────────────────────────────────
        //  Hover highlight (via hit-test rectangles)
        // ─────────────────────────────────────────────

        private void HitArea_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Rectangle rect && rect.Tag is string target &&
                _overlayImages.TryGetValue(target, out var img))
            {
                _elementTypes.TryGetValue(target, out var elemType);

                // Sticks are always visible — skip hover ghost
                if (elemType == OverlayElementType.StickRing)
                    return;

                if (_flashTarget == target) return;

                _hoverTarget = target;
                img.Visibility = Visibility.Visible;
                img.Opacity = 0.4;

                // For triggers, show full image during hover (override clip)
                if (_triggerClips.TryGetValue(target, out var clip))
                    clip.Rect = new Rect(0, 0, img.Width, img.Height);
            }
        }

        private void HitArea_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Rectangle rect && rect.Tag is string target &&
                _stickHighlights.TryGetValue(target, out var highlight))
            {
                highlight.Visibility = Visibility.Collapsed;
                highlight.Clip = null;
            }
            _hoverTarget = null;
            _dirty = true; // Next render frame restores proper state
        }

        private void StickHitArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Rectangle rect || rect.Tag is not string target)
                return;
            if (!_stickHighlights.TryGetValue(target, out var highlight))
                return;

            // Hit rect is stick ring coords; highlight image is stick click coords (slightly larger)
            // Compute clip in the highlight image's local coordinate space
            double hw = highlight.Width, hh = highlight.Height;
            double hcx = hw / 2, hcy = hh / 2;
            double hrx = hw / 2, hry = hh / 2;

            // Mouse position relative to stick ring hit rect
            var pos = e.GetPosition(rect);
            double rw = rect.Width, rh = rect.Height;
            double rcx = rw / 2, rcy = rh / 2;
            double dx = pos.X - rcx, dy = pos.Y - rcy;
            double rdist = Math.Sqrt(dx * dx / (rcx * rcx) + dy * dy / (rcy * rcy));
            double centerR = 0.3;

            Geometry clip;
            if (rdist < centerR)
            {
                clip = new EllipseGeometry(new Point(hcx, hcy), hrx * centerR, hry * centerR);
            }
            else
            {
                var fullEllipse = new EllipseGeometry(new Point(hcx, hcy), hrx, hry);
                var centerEllipse = new EllipseGeometry(new Point(hcx, hcy), hrx * centerR, hry * centerR);

                Rect halfRect;
                if (Math.Abs(dx) >= Math.Abs(dy))
                    halfRect = dx >= 0
                        ? new Rect(hcx, 0, hw / 2, hh)
                        : new Rect(0, 0, hw / 2, hh);
                else
                    halfRect = dy >= 0
                        ? new Rect(0, hcy, hw, hh / 2)
                        : new Rect(0, 0, hw, hh / 2);

                var quadrant = new CombinedGeometry(GeometryCombineMode.Intersect,
                    fullEllipse, new RectangleGeometry(halfRect));
                clip = new CombinedGeometry(GeometryCombineMode.Exclude,
                    quadrant, centerEllipse);
            }

            highlight.Clip = clip;
            highlight.Visibility = Visibility.Visible;
        }

        // ─────────────────────────────────────────────
        //  Flash animation (Map All)
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            StopFlash();
            if (string.IsNullOrEmpty(target)) return;

            _flashRawTarget = target;
            _flashTarget = ResolveFlashTarget(target);

            // For stick axes, compute and store the quadrant clip
            _flashStickClip = null;
            if (IsStickAxisTarget(target) && _stickHighlights.TryGetValue(_flashTarget, out var highlight))
            {
                _flashStickClip = GetStickQuadrantClip(target, highlight.Width, highlight.Height);
                highlight.Clip = _flashStickClip;
                highlight.Opacity = 0.4;
            }

            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _flashTimer.Tick += FlashTick;
            _flashTimer.Start();
            FlashTick(null, EventArgs.Empty);
        }

        private string ResolveFlashTarget(string target)
        {
            if (target.Contains("LeftThumbAxis")) return "LeftThumbRing";
            if (target.Contains("RightThumbAxis")) return "RightThumbRing";
            if (target == "LeftThumbButton") return "LeftThumbRing";
            if (target == "RightThumbButton") return "RightThumbRing";
            return target;
        }

        private static bool IsStickAxisTarget(string target) =>
            target.Contains("ThumbAxis") || target == "LeftThumbButton" || target == "RightThumbButton";

        private static Geometry GetStickQuadrantClip(string target, double w, double h)
        {
            double cx = w / 2, cy = h / 2;
            double rx = w / 2, ry = h / 2;
            double centerR = 0.3;
            var fullEllipse = new EllipseGeometry(new Point(cx, cy), rx, ry);
            var centerEllipse = new EllipseGeometry(new Point(cx, cy), rx * centerR, ry * centerR);

            if (target == "LeftThumbButton" || target == "RightThumbButton")
                return centerEllipse;

            // Determine quadrant from axis name
            Rect halfRect;
            if (target.Contains("AxisX"))
            {
                bool neg = target.EndsWith("Neg");
                halfRect = neg
                    ? new Rect(0, 0, w / 2, h)        // Left
                    : new Rect(cx, 0, w / 2, h);      // Right
            }
            else // AxisY
            {
                bool neg = target.EndsWith("Neg");
                halfRect = neg
                    ? new Rect(0, 0, w, h / 2)        // Top (Neg = up in screen coords)
                    : new Rect(0, cy, w, h / 2);      // Bottom
            }

            var quadrant = new CombinedGeometry(GeometryCombineMode.Intersect,
                fullEllipse, new RectangleGeometry(halfRect));
            return new CombinedGeometry(GeometryCombineMode.Exclude,
                quadrant, centerEllipse);
        }

        private void FlashTick(object sender, EventArgs e)
        {
            _flashOn = !_flashOn;
            if (_flashTarget == null) return;

            // Stick axis/button targets: flash the quadrant highlight image
            if (IsStickAxisTarget(_flashRawTarget) && _stickHighlights.TryGetValue(_flashTarget, out var highlight))
            {
                // Re-apply clip on every tick to guard against it being cleared
                if (_flashStickClip != null)
                    highlight.Clip = _flashStickClip;
                highlight.Visibility = _flashOn ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            // All other targets: flash the overlay image
            if (_overlayImages.TryGetValue(_flashTarget, out var img))
            {
                _elementTypes.TryGetValue(_flashTarget, out var elemType);

                if (elemType == OverlayElementType.StickRing)
                {
                    img.Opacity = _flashOn ? 1.0 : 0.2;
                }
                else
                {
                    img.Visibility = _flashOn ? Visibility.Visible : Visibility.Collapsed;
                    if (_flashOn)
                    {
                        img.Opacity = 1.0;
                        if (_triggerClips.TryGetValue(_flashTarget, out var clip))
                            clip.Rect = new Rect(0, 0, img.Width, img.Height);
                    }
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

            // Hide stick quadrant highlight (don't clear clip — UpdateFlashTarget will set it fresh)
            if (_flashTarget != null && _stickHighlights.TryGetValue(_flashTarget, out var highlight))
            {
                highlight.Visibility = Visibility.Collapsed;
            }
            _flashStickClip = null;

            // Restore the flashed element to its default state
            if (_flashTarget != null && _overlayImages.TryGetValue(_flashTarget, out var img))
            {
                _elementTypes.TryGetValue(_flashTarget, out var elemType);

                if (_triggerClips.TryGetValue(_flashTarget, out var clip))
                {
                    clip.Rect = new Rect(0, img.Height, img.Width, 0);
                }
                else if (elemType == OverlayElementType.StickRing)
                {
                    img.Visibility = Visibility.Visible;
                }
                else
                {
                    img.Visibility = Visibility.Collapsed;
                }
                img.Opacity = 1.0;
            }
            _flashTarget = null;
            _flashRawTarget = null;
            _flashOn = false;
            _dirty = true;
        }
    }
}
