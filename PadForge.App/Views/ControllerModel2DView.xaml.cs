using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        // 2D colorway state: the resolved id for the loaded model (null when
        // the folder ships a single colorway), the appearance-store family
        // key, and the folder's set, kept for the picker's handler. The
        // store is PadSetting.Model3DAppearances, shared with the 3D picker.
        private string _loadedColorway;
        private string _colorwayFamilyKey;
        private Colorway2D[] _colorwaySet;
        private bool _pickerUpdating;

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

        // Touchpad preview (PlayStation slots only — built once at canvas time, updated each frame)
        private Image _touchpadClickHighlight; // full-zone blue overlay when click is held
        private Ellipse _touchpadFinger0Dot;
        private Ellipse _touchpadFinger1Dot;
        private OverlayElement _touchpadOverlay;   // layout entry for positioning the dots

        // Layout data
        private double _stickMaxTravel;

        public ControllerModel2DView()
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
            // Annotation overlay (#175): anchors move only when the Viewbox
            // rescales, so a size change is the one geometry trigger the 2D
            // layer needs (no camera, no timer-driven re-projection).
            SizeChanged += (s, e) => LayoutAnnotations();
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
            TeardownAnnotations();
            _vm = null;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.OutputType)
                || e.PropertyName == nameof(PadViewModel.ProfileId)
                || e.PropertyName == nameof(PadViewModel.Model3DAppearances))
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

            // Per-profile asset selection (DualSense, Xbox One S, Xbox Series,
            // ...). Falls back to DS4 / Xbox 360 for unrecognized profiles.
            var (needed, _) = PadForge.Common.Input.HMaestroProfileCatalog.ResolveAssetFolders(
                _vm.ProfileId, _vm.OutputType);

            // Colorway rides the same guard: a change in the pad's stored
            // appearance rebuilds the canvas on the recolored assets.
            var (famKey, set) = Controller2DColorways.For(needed);
            string colorway = null;
            if (set != null)
            {
                colorway = set[0].Id;
                string chosen = _vm.GetModelAppearance(famKey);
                foreach (var c in set)
                    if (c.Id == chosen) { colorway = c.Id; break; }
            }

            if (_loadedModel == needed && _loadedColorway == colorway) return;
            _loadedModel = needed;
            _loadedColorway = colorway;

            BuildCanvas(needed);
            _dirty = true;
        }

        /// <summary>The 2D colorway picker, twin of the 3D view's: hidden
        /// unless the folder ships more than one colorway, listing only what
        /// this view can render.</summary>
        private void UpdateAppearancePicker(Colorway2D[] set, Colorway2D chosen)
        {
            _pickerUpdating = true;
            try
            {
                if (set == null || set.Length < 2)
                {
                    AppearancePicker.ItemsSource = null;
                    AppearancePicker.Visibility = Visibility.Collapsed;
                    return;
                }
                var names = new string[set.Length];
                int sel = 0;
                for (int i = 0; i < set.Length; i++)
                {
                    names[i] = set[i].Name;
                    if (chosen != null && set[i].Id == chosen.Id) sel = i;
                }
                AppearancePicker.ItemsSource = names;
                AppearancePicker.SelectedIndex = sel;
                AppearancePicker.Visibility = Visibility.Visible;
            }
            finally
            {
                _pickerUpdating = false;
            }
        }

        private void AppearancePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_pickerUpdating || _vm == null || _colorwaySet == null) return;
            int i = AppearancePicker.SelectedIndex;
            if (i < 0 || i >= _colorwaySet.Length) return;

            // Writes the pad's shared appearance store; its PropertyChanged
            // re-enters EnsureModel, which rebuilds on the new colorway.
            _vm.SetModelAppearance(_colorwayFamilyKey, _colorwaySet[i].Id);
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

            int baseW, baseH;
            string basePath;
            OverlayElement[] overlays;

            switch (modelName)
            {
                case "DS4":
                    baseW = DS4Layout.BaseWidth; baseH = DS4Layout.BaseHeight;
                    basePath = DS4Layout.BasePath; overlays = DS4Layout.Overlays;
                    _stickMaxTravel = DS4Layout.StickMaxTravel;
                    break;
                case "DualSense":
                    baseW = DualSenseLayout.BaseWidth; baseH = DualSenseLayout.BaseHeight;
                    basePath = DualSenseLayout.BasePath; overlays = DualSenseLayout.Overlays;
                    _stickMaxTravel = DualSenseLayout.StickMaxTravel;
                    break;
                case "DUALSENSEEDGE":
                    baseW = DualSenseEdgeLayout.BaseWidth; baseH = DualSenseEdgeLayout.BaseHeight;
                    basePath = DualSenseEdgeLayout.BasePath; overlays = DualSenseEdgeLayout.Overlays;
                    _stickMaxTravel = DualSenseEdgeLayout.StickMaxTravel;
                    break;
                case "XBOXONE":
                    baseW = XboxOneSLayout.BaseWidth; baseH = XboxOneSLayout.BaseHeight;
                    basePath = XboxOneSLayout.BasePath; overlays = XboxOneSLayout.Overlays;
                    _stickMaxTravel = XboxOneSLayout.StickMaxTravel;
                    break;
                case "XBOXSERIES":
                    baseW = XboxSeriesXLayout.BaseWidth; baseH = XboxSeriesXLayout.BaseHeight;
                    basePath = XboxSeriesXLayout.BasePath; overlays = XboxSeriesXLayout.Overlays;
                    _stickMaxTravel = XboxSeriesXLayout.StickMaxTravel;
                    break;
                case "SWITCHPRO":
                    baseW = SwitchProLayout.BaseWidth; baseH = SwitchProLayout.BaseHeight;
                    basePath = SwitchProLayout.BasePath; overlays = SwitchProLayout.Overlays;
                    _stickMaxTravel = SwitchProLayout.StickMaxTravel;
                    break;
                case "SWITCH2PRO":
                    baseW = Switch2ProLayout.BaseWidth; baseH = Switch2ProLayout.BaseHeight;
                    basePath = Switch2ProLayout.BasePath; overlays = Switch2ProLayout.Overlays;
                    _stickMaxTravel = Switch2ProLayout.StickMaxTravel;
                    break;
                default:
                    baseW = Xbox360Layout.BaseWidth; baseH = Xbox360Layout.BaseHeight;
                    basePath = Xbox360Layout.BasePath; overlays = Xbox360Layout.Overlays;
                    _stickMaxTravel = Xbox360Layout.StickMaxTravel;
                    break;
            }
            string folder = modelName;

            // Colorway resolution: swap the base render and any rest-art
            // sprite the chosen colorway recolors (trigger silhouettes,
            // stick rings); press-highlight art is shared across colorways.
            var (famKey, set) = Controller2DColorways.For(modelName);
            _colorwayFamilyKey = famKey;
            _colorwaySet = set;
            Colorway2D chosen = null;
            if (set != null)
            {
                chosen = set[0];
                foreach (var c in set)
                    if (c.Id == _loadedColorway) { chosen = c; break; }
                basePath = $"2DModels/{folder}/{chosen.BaseFile}";
            }
            UpdateAppearancePicker(set, chosen);
            string Resolve(string file)
                => chosen != null && chosen.Overrides.TryGetValue(file, out var v) ? v : file;

            // Annotation overlay (#175): the layout table is also the anchor
            // position source, so the chips point at exactly what the model
            // draws.
            SetAnnotationAnchors(overlays);

            ModelCanvas.Width = baseW;
            ModelCanvas.Height = baseH;

            // Base image (Z=1) — sits ABOVE TriggerBase/Trigger (Z=0,0)
            // so the controller body silhouette covers the lower
            // portion of the trigger PNG (matches asset pack canonicals
            // where the body is rendered in front of the triggers).
            _baseImage = CreateImage(basePath, 0, 0, baseW, baseH);
            Panel.SetZIndex(_baseImage, 1);
            ModelCanvas.Children.Add(_baseImage);

            // Overlay images (Z=1) + hit-test rectangles (Z=10)
            foreach (var ov in overlays)
            {
                string imgPath = $"2DModels/{folder}/{Resolve(ov.ImageFile)}";
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
                else if (ov.ElementType == OverlayElementType.TriggerBase)
                {
                    // Rest-state trigger silhouette under the active-press
                    // overlay; always visible, no clip, no hit-test (the
                    // Trigger element above owns the hit area).
                    img.Visibility = Visibility.Visible;
                    img.Opacity = 1.0;
                }
                else
                {
                    // Buttons, StickClicks: hidden until pressed
                    img.Visibility = Visibility.Collapsed;
                }

                // TriggerBase z-index 0 (under Trigger), all others z-index 1
                // TriggerBase (rest-state silhouette) renders BEHIND the
                // base PNG (Z=0 < base Z=1) so the controller body
                // covers its lower portion. The active blue Trigger
                // overlay stays in FRONT (Z=2) so the press fill is
                // fully visible. All other overlays at Z=2.
                int z = ov.ElementType == OverlayElementType.TriggerBase ? 0 : 2;
                Panel.SetZIndex(img, z);
                _overlayImages[ov.TargetName] = img;
                ModelCanvas.Children.Add(img);

                // StickClick + TriggerBase: no hit-test rect
                if (ov.ElementType == OverlayElementType.StickClick ||
                    ov.ElementType == OverlayElementType.TriggerBase)
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
                // Per-pixel hit zone: the generator traces each overlay's
                // opaque region into polygons, and UIElement.Clip bounds
                // hit-testing as well as rendering, so hover/click only
                // fire where the art actually shows (a trigger's thin arc,
                // not the empty box around it).
                if (!string.IsNullOrEmpty(ov.HitPath))
                    hitRect.Clip = BuildHitGeometry(ov.HitPath, ov.Width, ov.Height);
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

            // Touchpad preview: a full-zone blue highlight (shown when
            // TouchpadClick is held) plus two finger dots positioned by the
            // VM's TouchpadFingerN(X,Y,Down) properties. Mirrors the DS4 web
            // controller's preview shape. Built for every layout that
            // declares a Touchpad element and ships the click art: the gate
            // used to name the two folders that had it when it was written,
            // which silently excluded the Edge the day it got its own folder.
            _touchpadClickHighlight = null;
            _touchpadFinger0Dot = null;
            _touchpadFinger1Dot = null;
            _touchpadOverlay = default;
            if (TouchpadClickSprite(modelName) != null)
            {
                OverlayElement touchpad = default, click = default;
                foreach (var ov in overlays)
                {
                    if (ov.ElementType == OverlayElementType.Touchpad) touchpad = ov;
                    if (ov.TargetName == "TouchpadClick") click = ov;
                }
                if (touchpad != null)
                {
                    _touchpadOverlay = touchpad;
                    // Click highlight visual uses TouchpadClick bounds (sized to
                    // the asset pack's click PNG); finger dots use Touchpad
                    // bounds (the smaller actual touchpad surface).
                    BuildTouchpadPreview(click ?? touchpad, modelName);
                }
            }

            // Create stick quadrant highlights using the stick click overlay image
            foreach (var ov in overlays)
            {
                if (ov.ElementType != OverlayElementType.StickClick) continue;

                // Map stick click target to its ring target
                string ringTarget = ov.TargetName == "LeftThumbButton" ? "LeftThumbRing" : "RightThumbRing";

                string clickImgPath = $"2DModels/{folder}/{Resolve(ov.ImageFile)}";
                var highlight = CreateImage(clickImgPath, ov.X, ov.Y, ov.Width, ov.Height);
                highlight.IsHitTestVisible = false;
                highlight.Opacity = 0.4;
                highlight.Visibility = Visibility.Collapsed;
                Panel.SetZIndex(highlight, 5);
                _stickHighlights[ringTarget] = highlight;
                ModelCanvas.Children.Add(highlight);
            }

            // Chips re-anchor against the new layout table. Queued (not
            // inline) so the Viewbox has re-measured the resized canvas
            // before anchors translate.
            QueueAnnotationRebuild();
        }

        /// <summary>The touchpad-click sprite each 2D asset folder ships, or
        /// null for a folder with no single-touchpad preview art. Keyed on the
        /// folder because the art family and the folder name diverge (the
        /// DualSense Edge folder carries the DualSense sprites).</summary>
        internal static string TouchpadClickSprite(string folder) => folder switch
        {
            "DS4" => "DS4_Touchpad_Click.png",
            "DualSense" or "DUALSENSEEDGE" => "DualSense_Touchpad_Click.png",
            _ => null,
        };

        private void BuildTouchpadPreview(OverlayElement ov, string modelName)
        {
            // Full-zone touchpad-click highlight, hidden by default. Shown when
            // the TouchpadClick button is held, on hover (lower opacity), and
            // during the Map All flash. Uses the asset pack's touchpad-click
            // PNG at the layout-defined Touchpad rectangle so it lines up with
            // the visible touchpad surface on the rendered controller body.
            //
            // The sprite's stem is the ART family, not the folder: the Edge
            // has its own folder but ships the DualSense sprites, so
            // interpolating the folder name asked for a file that does not
            // exist and the preview never built.
            string stem = TouchpadClickSprite(modelName);
            if (stem == null) return;
            string clickPng = $"2DModels/{modelName}/{stem}";
            _touchpadClickHighlight = CreateImage(clickPng, ov.X, ov.Y, ov.Width, ov.Height);
            _touchpadClickHighlight.IsHitTestVisible = false;
            _touchpadClickHighlight.Visibility = Visibility.Collapsed;
            Panel.SetZIndex(_touchpadClickHighlight, 6);
            ModelCanvas.Children.Add(_touchpadClickHighlight);

            const double dotDiameter = 22;
            _touchpadFinger0Dot = new Ellipse
            {
                Width = dotDiameter,
                Height = dotDiameter,
                Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0x66, 0x00)), // orange
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _touchpadFinger1Dot = new Ellipse
            {
                Width = dotDiameter,
                Height = dotDiameter,
                Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x66, 0xFF)), // blue
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Panel.SetZIndex(_touchpadFinger0Dot, 7);
            Panel.SetZIndex(_touchpadFinger1Dot, 7);
            ModelCanvas.Children.Add(_touchpadFinger0Dot);
            ModelCanvas.Children.Add(_touchpadFinger1Dot);
        }

        private void UpdateTouchpadPreview()
        {
            if (_touchpadClickHighlight == null || _touchpadFinger0Dot == null || _touchpadFinger1Dot == null)
                return;

            // Don't overwrite hover state — HitArea_MouseEnter sets the
            // highlight visible at 0.4 opacity for the click-mapping
            // affordance and HitArea_MouseLeave restores via _dirty=true.
            // Same goes for the Map All flash animation: FlashTick toggles
            // visibility on the same rectangle, and writing here every render
            // frame would race with it.
            bool flashClaimsTouchpad = _flashTarget == "TouchpadClick";
            if (_hoverTarget != "Touchpad" && _hoverTarget != "TouchpadClick"
                && !flashClaimsTouchpad)
            {
                _touchpadClickHighlight.Visibility = _vm.TouchpadClickPressed
                    ? Visibility.Visible : Visibility.Collapsed;
                _touchpadClickHighlight.Opacity = 1.0;
            }

            UpdateFingerDot(_touchpadFinger0Dot, _vm.TouchpadFinger0Down,
                _vm.TouchpadFinger0X, _vm.TouchpadFinger0Y);
            UpdateFingerDot(_touchpadFinger1Dot, _vm.TouchpadFinger1Down,
                _vm.TouchpadFinger1X, _vm.TouchpadFinger1Y);
        }

        private void UpdateFingerDot(Ellipse dot, bool down, double normX, double normY)
        {
            if (!down)
            {
                dot.Visibility = Visibility.Collapsed;
                return;
            }
            dot.Visibility = Visibility.Visible;
            // Center the dot on the normalized touchpad coordinate.
            double cx = _touchpadOverlay.X + normX * _touchpadOverlay.Width;
            double cy = _touchpadOverlay.Y + normY * _touchpadOverlay.Height;
            Canvas.SetLeft(dot, cx - dot.Width / 2);
            Canvas.SetTop(dot, cy - dot.Height / 2);
        }

        /// <summary>Builds the hit-zone clip from the generator's normalized
        /// polygon groups ("x,y x,y ...;x,y ..."), scaled to the rendered
        /// entry size. Culture-invariant parse; frozen for sharing.</summary>
        private static Geometry BuildHitGeometry(string hitPath, double w, double h)
        {
            var sg = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var ctx = sg.Open())
            {
                foreach (var poly in hitPath.Split(';'))
                {
                    var pts = poly.Split(' ');
                    Point Parse(string t)
                    {
                        int c = t.IndexOf(',');
                        return new Point(
                            double.Parse(t.Substring(0, c), System.Globalization.CultureInfo.InvariantCulture) * w,
                            double.Parse(t.Substring(c + 1), System.Globalization.CultureInfo.InvariantCulture) * h);
                    }
                    if (pts.Length < 3) continue;
                    ctx.BeginFigure(Parse(pts[0]), isFilled: true, isClosed: true);
                    for (int i = 1; i < pts.Length; i++)
                        ctx.LineTo(Parse(pts[i]), isStroked: false, isSmoothJoin: false);
                }
            }
            sg.Freeze();
            return sg;
        }

        private static Image CreateImage(string resourcePath, double x, double y, double w, double h)
        {
            // Bitmap loading lives in EmbeddedBitmaps (shared with the PadPage
            // lightbar preview, #175): assembly .g.resources stream, never
            // pack:// URIs, which crash on .NET 10 single-file publish.
            var bitmap = EmbeddedBitmaps.Load(resourcePath);
            if (bitmap == null)
            {
                // Resource not found. Return an empty placeholder to avoid a crash.
                return new Image { Width = w, Height = h };
            }
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
            // Retained-page guard (see ControllerModelView.OnRendering): skip
            // the overlay repaint while hidden; _dirty catches up on the first
            // visible frame.
            // Iconic gate: IsVisible stays TRUE while the window is
            // minimized, so without this the overlay repainted per display
            // frame while nothing could render (the marquee mechanism).
            if (!IsVisible || PadForge.Common.AmbientMotionProbe.Instance.IsWindowMinimized) return;
            if (!_dirty || _vm == null || _loadedModel == null)
                return;
            _dirty = false;

            UpdateButtons();
            UpdateTriggers();
            UpdateSticks();
            UpdateTouchpadPreview();
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
            SetOverlayVisible("ButtonShare", _vm.ButtonShare);
            SetOverlayVisible("ButtonMute", _vm.ButtonMute);
            SetOverlayVisible("LeftPaddle", _vm.LeftPaddle);
            SetOverlayVisible("RightPaddle", _vm.RightPaddle);
            SetOverlayVisible("LeftFunction", _vm.LeftFunction);
            SetOverlayVisible("RightFunction", _vm.RightFunction);
            SetOverlayVisible("ButtonC", _vm.ButtonC);
            SetOverlayVisible("LeftPaddle", _vm.LeftPaddle);
            SetOverlayVisible("RightPaddle", _vm.RightPaddle);
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
                else if (elemType == OverlayElementType.Touchpad)
                {
                    // Touchpad surface routes to the click button — this is the
                    // affordance for binding the touchpad-press button via the
                    // big visible region rather than the narrow strip above.
                    ControllerElementRecordRequested?.Invoke(this, "TouchpadClick");
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
            if (sender is Rectangle rect && rect.Tag is string target)
            {
                _elementTypes.TryGetValue(target, out var elemType);

                // Sticks are always visible — skip hover ghost
                if (elemType == OverlayElementType.StickRing)
                    return;

                if (_flashTarget == target) return;

                // Touchpad family has no per-element overlay image (the zone
                // is rendered by the click highlight rectangle from
                // BuildTouchpadPreview), and the click strip's hit rect
                // extends above the pad surface, so both targets show that
                // rectangle at low opacity for the hover affordance. This
                // must run before the overlay-image lookup: the strip has no
                // image, and bailing there left its exclusive band with a
                // hand cursor and no highlight.
                if ((elemType == OverlayElementType.Touchpad || target == "TouchpadClick")
                    && _touchpadClickHighlight != null)
                {
                    _hoverTarget = target;
                    _touchpadClickHighlight.Visibility = Visibility.Visible;
                    _touchpadClickHighlight.Opacity = 0.4;
                    return;
                }

                if (!_overlayImages.TryGetValue(target, out var img))
                    return;

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

            // A Nintendo slot's CurrentRecordingTarget is a raw grid name
            // (RawBtn1, RawAxis0Neg); the flash machinery below speaks the
            // preview element grammar. Translate back before resolving.
            if (target.StartsWith("Raw", StringComparison.Ordinal))
            {
                target = NintendoPreviewMap.ToPreview(target, _vm?.ProfileId);
                if (string.IsNullOrEmpty(target)) return;
            }

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

            // Touchpad-click target uses the rectangle highlight built in
            // BuildTouchpadPreview (the layout has no per-element PNG asset
            // for the touch surface, so the standard _overlayImages flow
            // doesn't render anything visible).
            if (_flashTarget == "TouchpadClick" && _touchpadClickHighlight != null)
            {
                _touchpadClickHighlight.Visibility = _flashOn ? Visibility.Visible : Visibility.Collapsed;
                _touchpadClickHighlight.Opacity = 1.0;
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
                else if (_triggerClips.TryGetValue(_flashTarget, out var clip))
                {
                    // A trigger flashes through its CLIP, full to empty, the
                    // same channel its live level uses. Toggling Visibility
                    // here instead left the image Collapsed whenever the
                    // flash happened to stop on an off phase, because the
                    // restore path's trigger branch only resets the clip and
                    // never puts Visibility back.
                    img.Visibility = Visibility.Visible;
                    img.Opacity = 1.0;
                    clip.Rect = _flashOn
                        ? new Rect(0, 0, img.Width, img.Height)
                        : new Rect(0, img.Height, img.Width, 0);
                }
                else
                {
                    img.Visibility = _flashOn ? Visibility.Visible : Visibility.Collapsed;
                    if (_flashOn) img.Opacity = 1.0;
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

            // Touchpad rectangle: hide so UpdateTouchpadPreview's next frame
            // can set the press-state-driven visibility cleanly.
            if (_flashTarget == "TouchpadClick" && _touchpadClickHighlight != null)
            {
                _touchpadClickHighlight.Visibility = Visibility.Collapsed;
                _touchpadClickHighlight.Opacity = 1.0;
            }

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
