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
        private bool _flashOn;

        // Hover state
        private string _hoverTarget;

        // Stick quadrant highlight
        private readonly Dictionary<string, Path> _stickHighlights = new();

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

                // Stick quadrant highlight path (hidden until hover)
                if (ov.ElementType == OverlayElementType.StickRing)
                {
                    var highlight = new Path
                    {
                        Fill = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                        IsHitTestVisible = false,
                        Visibility = Visibility.Collapsed,
                    };
                    Canvas.SetLeft(highlight, ov.X);
                    Canvas.SetTop(highlight, ov.Y);
                    Panel.SetZIndex(highlight, 5);
                    _stickHighlights[ov.TargetName] = highlight;
                    ModelCanvas.Children.Add(highlight);

                    hitRect.MouseMove += StickHitArea_MouseMove;
                }
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

            var pos = e.GetPosition(rect);
            double w = rect.Width, h = rect.Height;
            double cx = w / 2, cy = h / 2;
            double dx = pos.X - cx, dy = pos.Y - cy;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            double radius = Math.Min(w, h) / 2;

            Geometry geom;
            if (dist < radius * 0.3)
            {
                // Center — circle for stick click
                geom = new EllipseGeometry(new Point(cx, cy), radius * 0.3, radius * 0.3);
            }
            else if (Math.Abs(dx) >= Math.Abs(dy))
            {
                if (dx >= 0)
                    geom = CreateWedge(cx, cy, w, 0, w, h); // Right
                else
                    geom = CreateWedge(cx, cy, 0, 0, 0, h); // Left
            }
            else
            {
                if (dy >= 0)
                    geom = CreateWedge(cx, cy, 0, h, w, h); // Bottom
                else
                    geom = CreateWedge(cx, cy, 0, 0, w, 0); // Top
            }

            highlight.Data = geom;
            highlight.Visibility = Visibility.Visible;
        }

        private static Geometry CreateWedge(double cx, double cy, double x1, double y1, double x2, double y2)
        {
            var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
            fig.Segments.Add(new LineSegment(new Point(x1, y1), true));
            fig.Segments.Add(new LineSegment(new Point(x2, y2), true));
            var pathGeom = new PathGeometry();
            pathGeom.Figures.Add(fig);
            return pathGeom;
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
                if (_flashOn)
                {
                    img.Opacity = 1.0;
                    // For triggers, show full image during flash
                    if (_triggerClips.TryGetValue(_flashTarget, out var clip))
                        clip.Rect = new Rect(0, 0, img.Width, img.Height);
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

            // Restore the flashed element to its default state
            if (_flashTarget != null && _overlayImages.TryGetValue(_flashTarget, out var img))
            {
                if (_triggerClips.TryGetValue(_flashTarget, out var clip))
                {
                    // Reset trigger clip to empty — next dirty update will set correct fill
                    clip.Rect = new Rect(0, img.Height, img.Width, 0);
                }
                else
                {
                    img.Visibility = Visibility.Collapsed;
                }
                img.Opacity = 1.0;
            }
            _flashTarget = null;
            _flashOn = false;
            _dirty = true;
        }
    }
}
