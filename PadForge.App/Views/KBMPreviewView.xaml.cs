using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Engine;
using PadForge.ViewModels;

namespace PadForge.Views
{
    public partial class KBMPreviewView : UserControl
    {
        public event EventHandler<string> ControllerElementRecordRequested;

        private PadViewModel _vm;
        private bool _dirty;
        private bool _layoutBuilt;

        private readonly List<KbmKeyWidget> _keyWidgets = new();

        // Mouse elements
        private Path _lmbPath;
        private Path _rmbPath;
        private Rectangle _scrollWheelPill;
        private Polygon _scrollUpArrow;
        private Polygon _scrollDownArrow;
        private Ellipse _movementDot;
        private Ellipse _moveCircle;
        private Polygon _moveArrow;
        private Canvas _moveArrowCanvas;

        // Colors
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush MouseBodyBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        private static readonly Brush MouseButtonBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
        private static readonly Brush MmbBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        private static readonly Brush ScrollWheelBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38));
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        private static readonly Brush DotBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly Brush KeyNormalBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x88, 0x88, 0x88));
        private static readonly Brush KeyPressedBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromRgb(0x40, 0xA0, 0xE0));
        private static readonly Brush FlashBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));

        // Layout constants
        private const double MC = 80;       // mouse center X
        private const double MoveSize = 55; // movement circle diameter

        // Button area (used by both build and render)
        private const double BtnBottom = 58;
        private const double MoveTop = 86;  // top of movement circle

        private System.Windows.Threading.DispatcherTimer _flashTimer;
        private string _flashTarget;
        private bool _flashOn;

        public KBMPreviewView()
        {
            InitializeComponent();
            CompositionTarget.Rendering += OnRendering;
        }

        public void Bind(PadViewModel vm)
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = vm;
            if (_vm != null)
            {
                CompositionTarget.Rendering -= OnRendering;
                CompositionTarget.Rendering += OnRendering;
                _vm.PropertyChanged += OnVmPropertyChanged;
                RebuildLayout();
            }
        }

        public void Unbind()
        {
            CompositionTarget.Rendering -= OnRendering;
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
            _layoutBuilt = false;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PadViewModel.OutputType)) { Dispatcher.Invoke(RebuildLayout); return; }
            if (e.PropertyName == nameof(PadViewModel.CurrentRecordingTarget)) { Dispatcher.Invoke(() => UpdateFlashTarget(_vm?.CurrentRecordingTarget)); return; }
            _dirty = true;
        }

        private void RebuildLayout()
        {
            _layoutBuilt = false;
            _keyWidgets.Clear();
            if (_vm == null || _vm.OutputType != VirtualControllerType.KeyboardMouse) return;
            BuildKeyboardCanvas();
            BuildMouseCanvas();
            _layoutBuilt = true;
            _dirty = true;
        }

        // ─────────────────────────────────────────────
        //  Keyboard
        // ─────────────────────────────────────────────

        private void BuildKeyboardCanvas()
        {
            KeyboardCanvas.Children.Clear();
            var keys = KeyboardKeyItem.BuildLayout();
            foreach (var key in keys)
            {
                string targetName = $"KbmKey{key.VKeyIndex:X2}";
                string tooltipLabel = _vm?.Mappings?.FirstOrDefault(m => m.TargetSettingName == targetName)?.TargetLabel ?? key.Label;
                var border = new Border
                {
                    Width = key.KeyWidth, Height = key.KeyHeight,
                    CornerRadius = new CornerRadius(3),
                    Background = KeyNormalBrush, Cursor = Cursors.Hand,
                    ToolTip = tooltipLabel
                };
                border.Child = new TextBlock
                {
                    Text = key.Label, FontSize = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.8, IsHitTestVisible = false
                };
                Canvas.SetLeft(border, key.X);
                Canvas.SetTop(border, key.Y);
                KeyboardCanvas.Children.Add(border);

                border.MouseEnter += (s, e) => { if (_flashTarget == null) { border.BorderBrush = HoverBrush; border.BorderThickness = new Thickness(1.5); } };
                border.MouseLeave += (s, e) => { if (_flashTarget == null) { border.BorderBrush = null; border.BorderThickness = new Thickness(0); } };
                border.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, targetName); e.Handled = true; };
                _keyWidgets.Add(new KbmKeyWidget { VKeyIndex = key.VKeyIndex, Border = border, TargetName = targetName });
            }
        }

        // ─────────────────────────────────────────────
        //  Mouse — buttons contour around scroll wheel
        // ─────────────────────────────────────────────

        private void BuildMouseCanvas()
        {
            MouseCanvas.Children.Clear();

            const double mW = 100;              // mouse body width
            double mL = MC - mW / 2;            // 30
            double mR = MC + mW / 2;            // 130
            const double mH = 185;              // mouse body height (taller)

            // Scroll wheel dimensions (longer + slightly wider per request)
            const double swW = 14, swH = 36;
            double swL = MC - swW / 2;          // 73
            double swR = MC + swW / 2;          // 87
            const double swTop = 13;
            const double swBot = swTop + swH;   // 49

            // Button gap edges (1px margin outside scroll wheel)
            double gapL = swL - 1;              // 72
            double gapR = swR + 1;              // 88

            // ── Mouse body outline ──
            var mouseBody = new Path
            {
                Data = Geometry.Parse(
                    $"M {mL},18 C {mL},6 {mL + 14},0 {MC},0 C {mR - 14},0 {mR},6 {mR},18" +
                    $" L {mR},{mH - 18} C {mR},{mH - 4} {mR - 14},{mH} {MC},{mH}" +
                    $" C {mL + 14},{mH} {mL},{mH - 4} {mL},{mH - 18} Z"),
                Fill = MouseBodyBrush, Stroke = DimBrush, StrokeThickness = 2
            };
            MouseCanvas.Children.Add(mouseBody);

            // ── LMB — contours around scroll wheel ──
            // Path: top near center → curves outward to gap edge → down → across to body edge → up along body → curves back
            _lmbPath = new Path
            {
                Data = Geometry.Parse(
                    $"M {MC - 2},2 " +
                    $"Q {gapL},{swTop - 4} {gapL},{swTop + 4} " +
                    $"L {gapL},{BtnBottom} " +
                    $"L {mL + 2},{BtnBottom} " +
                    $"L {mL + 2},18 " +
                    $"C {mL + 2},8 {mL + 14},2 {MC - 2},2 Z"),
                Fill = MouseButtonBrush, Stroke = DimBrush, StrokeThickness = 1, Cursor = Cursors.Hand
            };
            MouseCanvas.Children.Add(_lmbPath);
            _lmbPath.ToolTip = MappingLabel("KbmMBtn0");
            AddButtonHandlers(_lmbPath, "KbmMBtn0");

            // ── RMB — mirror of LMB ──
            _rmbPath = new Path
            {
                Data = Geometry.Parse(
                    $"M {MC + 2},2 " +
                    $"Q {gapR},{swTop - 4} {gapR},{swTop + 4} " +
                    $"L {gapR},{BtnBottom} " +
                    $"L {mR - 2},{BtnBottom} " +
                    $"L {mR - 2},18 " +
                    $"C {mR - 2},8 {mR - 14},2 {MC + 2},2 Z"),
                Fill = MouseButtonBrush, Stroke = DimBrush, StrokeThickness = 1, Cursor = Cursors.Hand
            };
            MouseCanvas.Children.Add(_rmbPath);
            _rmbPath.ToolTip = MappingLabel("KbmMBtn1");
            AddButtonHandlers(_rmbPath, "KbmMBtn1");

            // ── MMB channel background (between buttons) ──
            MouseCanvas.Children.Add(new Rectangle
            {
                Width = gapR - gapL, Height = BtnBottom - 2,
                Fill = MmbBrush, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false
            });
            Canvas.SetLeft(MouseCanvas.Children[^1], gapL);
            Canvas.SetTop(MouseCanvas.Children[^1], 2);

            // ── Scroll wheel pill (MMB click target) ──
            _scrollWheelPill = new Rectangle
            {
                Width = swW, Height = swH,
                RadiusX = swW / 2, RadiusY = swW / 2,
                Fill = ScrollWheelBrush, Stroke = DimBrush, StrokeThickness = 1,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(_scrollWheelPill, swL);
            Canvas.SetTop(_scrollWheelPill, swTop);
            MouseCanvas.Children.Add(_scrollWheelPill);
            _scrollWheelPill.ToolTip = MappingLabel("KbmMBtn2");
            _scrollWheelPill.MouseEnter += (s, e) => { if (_flashTarget == null) { _scrollWheelPill.Stroke = HoverBrush; _scrollWheelPill.StrokeThickness = 2; } };
            _scrollWheelPill.MouseLeave += (s, e) => { if (_flashTarget == null) { _scrollWheelPill.Stroke = DimBrush; _scrollWheelPill.StrokeThickness = 1; } };
            _scrollWheelPill.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, "KbmMBtn2"); e.Handled = true; };

            // ── Scroll direction arrows (on the scroll wheel) ──
            _scrollUpArrow = new Polygon
            {
                Points = new PointCollection { new Point(MC, swTop + 4), new Point(MC - 4, swTop + 10), new Point(MC + 4, swTop + 10) },
                Fill = DimBrush, Cursor = Cursors.Hand
            };
            MouseCanvas.Children.Add(_scrollUpArrow);
            _scrollUpArrow.ToolTip = MappingLabel("KbmScroll") + " Up";
            _scrollUpArrow.MouseEnter += (s, e) => { if (_flashTarget == null) _scrollUpArrow.Fill = HoverBrush; };
            _scrollUpArrow.MouseLeave += (s, e) => { if (_flashTarget == null) _scrollUpArrow.Fill = DimBrush; };
            _scrollUpArrow.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, "KbmScroll"); e.Handled = true; };

            _scrollDownArrow = new Polygon
            {
                Points = new PointCollection { new Point(MC, swBot - 4), new Point(MC - 4, swBot - 10), new Point(MC + 4, swBot - 10) },
                Fill = DimBrush, Cursor = Cursors.Hand
            };
            MouseCanvas.Children.Add(_scrollDownArrow);
            _scrollDownArrow.ToolTip = MappingLabel("KbmScroll") + " Down";
            _scrollDownArrow.MouseEnter += (s, e) => { if (_flashTarget == null) _scrollDownArrow.Fill = HoverBrush; };
            _scrollDownArrow.MouseLeave += (s, e) => { if (_flashTarget == null) _scrollDownArrow.Fill = DimBrush; };
            _scrollDownArrow.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, "KbmScrollNeg"); e.Handled = true; };

            // ── Separator between buttons and movement area ──
            MouseCanvas.Children.Add(new Line
            {
                X1 = mL + 8, Y1 = BtnBottom + 6, X2 = mR - 8, Y2 = BtnBottom + 6,
                Stroke = DimBrush, StrokeThickness = 0.5
            });

            // ── Movement circle — embedded in the body ──
            double moveX = MC - MoveSize / 2;

            _moveCircle = new Ellipse
            {
                Width = MoveSize, Height = MoveSize,
                Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x88, 0x88, 0x88)),
                Stroke = DimBrush, StrokeThickness = 1.5, Cursor = Cursors.Hand,
                ToolTip = "Mouse Movement"
            };
            Canvas.SetLeft(_moveCircle, moveX);
            Canvas.SetTop(_moveCircle, MoveTop);
            MouseCanvas.Children.Add(_moveCircle);

            _movementDot = new Ellipse { Width = 10, Height = 10, Fill = DotBrush, IsHitTestVisible = false };
            Canvas.SetLeft(_movementDot, moveX + MoveSize / 2 - 5);
            Canvas.SetTop(_movementDot, MoveTop + MoveSize / 2 - 5);
            MouseCanvas.Children.Add(_movementDot);

            // Direction arrow (hidden until hover/flash)
            double arrowLen = MoveSize * 0.35, arrowBase = 6;
            _moveArrow = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(MoveSize / 2, MoveSize / 2 - arrowLen),
                    new Point(MoveSize / 2 - arrowBase, MoveSize / 2 - arrowLen * 0.5),
                    new Point(MoveSize / 2 + arrowBase, MoveSize / 2 - arrowLen * 0.5)
                },
                Fill = HoverBrush, IsHitTestVisible = false, Visibility = Visibility.Collapsed
            };
            _moveArrowCanvas = new Canvas { Width = MoveSize, Height = MoveSize, IsHitTestVisible = false };
            _moveArrowCanvas.Children.Add(_moveArrow);
            Canvas.SetLeft(_moveArrowCanvas, moveX);
            Canvas.SetTop(_moveArrowCanvas, MoveTop);
            MouseCanvas.Children.Add(_moveArrowCanvas);

            // Hover: directional arrow in quadrant
            _moveCircle.MouseMove += (s, e) =>
            {
                if (_flashTarget != null) return;
                var pos = e.GetPosition(_moveCircle);
                double hx = pos.X - MoveSize / 2, hy = pos.Y - MoveSize / 2;
                double angle = Math.Abs(hx) >= Math.Abs(hy) ? (hx > 0 ? 90 : 270) : (hy > 0 ? 180 : 0);
                _moveArrow.Visibility = Visibility.Visible;
                _moveArrow.Fill = HoverBrush;
                _moveArrowCanvas.RenderTransform = new RotateTransform(angle, MoveSize / 2, MoveSize / 2);
                _moveCircle.Stroke = HoverBrush; _moveCircle.StrokeThickness = 2.5;
            };
            _moveCircle.MouseLeave += (s, e) =>
            {
                if (_flashTarget != null) return;
                _moveArrow.Visibility = Visibility.Collapsed;
                _moveCircle.Stroke = DimBrush; _moveCircle.StrokeThickness = 1.5;
            };
            _moveCircle.MouseLeftButtonDown += (s, e) =>
            {
                var pos = e.GetPosition(_moveCircle);
                double cx = pos.X - MoveSize / 2, cy = pos.Y - MoveSize / 2;
                string target = Math.Abs(cx) >= Math.Abs(cy)
                    ? (cx >= 0 ? "KbmMouseX" : "KbmMouseXNeg")
                    : (cy >= 0 ? "KbmMouseYNeg" : "KbmMouseY");
                ControllerElementRecordRequested?.Invoke(this, target);
                e.Handled = true;
            };

            // Side buttons (X1, X2) — small areas on the left side of the body
            var x1Rect = new Rectangle
            {
                Width = 8, Height = 14, RadiusX = 2, RadiusY = 2,
                Fill = MouseButtonBrush, Stroke = DimBrush, StrokeThickness = 1, Cursor = Cursors.Hand
            };
            Canvas.SetLeft(x1Rect, mL - 4); Canvas.SetTop(x1Rect, 70);
            MouseCanvas.Children.Add(x1Rect);
            x1Rect.ToolTip = MappingLabel("KbmMBtn3");
            x1Rect.MouseEnter += (s, e) => { if (_flashTarget == null) { x1Rect.Stroke = HoverBrush; x1Rect.StrokeThickness = 2; } };
            x1Rect.MouseLeave += (s, e) => { if (_flashTarget == null) { x1Rect.Stroke = DimBrush; x1Rect.StrokeThickness = 1; } };
            x1Rect.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, "KbmMBtn3"); e.Handled = true; };

            var x2Rect = new Rectangle
            {
                Width = 8, Height = 14, RadiusX = 2, RadiusY = 2,
                Fill = MouseButtonBrush, Stroke = DimBrush, StrokeThickness = 1, Cursor = Cursors.Hand
            };
            Canvas.SetLeft(x2Rect, mL - 4); Canvas.SetTop(x2Rect, 88);
            MouseCanvas.Children.Add(x2Rect);
            x2Rect.ToolTip = MappingLabel("KbmMBtn4");
            x2Rect.MouseEnter += (s, e) => { if (_flashTarget == null) { x2Rect.Stroke = HoverBrush; x2Rect.StrokeThickness = 2; } };
            x2Rect.MouseLeave += (s, e) => { if (_flashTarget == null) { x2Rect.Stroke = DimBrush; x2Rect.StrokeThickness = 1; } };
            x2Rect.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, "KbmMBtn4"); e.Handled = true; };

            MouseCanvas.Height = mH + 6;
        }

        // ─────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────

        private void AddButtonHandlers(Path path, string target)
        {
            path.MouseEnter += (s, e) => { if (_flashTarget == null) { path.Stroke = HoverBrush; path.StrokeThickness = 2; } };
            path.MouseLeave += (s, e) => { if (_flashTarget == null) { path.Stroke = DimBrush; path.StrokeThickness = 1; } };
            path.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, target); e.Handled = true; };
        }

        private string MappingLabel(string targetSettingName)
            => _vm?.Mappings?.FirstOrDefault(m => m.TargetSettingName == targetSettingName)?.TargetLabel ?? targetSettingName;

        private void Lbl(string text, double x, double y, double fs = 9)
        {
            var tb = new TextBlock { Text = text, FontSize = fs, Foreground = Brushes.Gray, IsHitTestVisible = false };
            Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
            MouseCanvas.Children.Add(tb);
        }

        // ─────────────────────────────────────────────
        //  Flash animation
        // ─────────────────────────────────────────────

        private void UpdateFlashTarget(string target)
        {
            if (_flashTimer != null) { _flashTimer.Stop(); _flashTimer = null; }
            ApplyFlashState(false);
            _flashTarget = target;
            if (string.IsNullOrEmpty(target)) return;
            _flashOn = true;
            _flashTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _flashTimer.Tick += (s, e) => { _flashOn = !_flashOn; ApplyFlashState(_flashOn); };
            _flashTimer.Start();
            ApplyFlashState(true);
        }

        private void ApplyFlashState(bool highlight)
        {
            if (string.IsNullOrEmpty(_flashTarget)) return;

            foreach (var w in _keyWidgets)
                if (_flashTarget == w.TargetName) { w.Border.Background = highlight ? FlashBrush : KeyNormalBrush; return; }

            if (_flashTarget == "KbmMBtn0") { _lmbPath.Fill = highlight ? FlashBrush : MouseButtonBrush; return; }
            if (_flashTarget == "KbmMBtn1") { _rmbPath.Fill = highlight ? FlashBrush : MouseButtonBrush; return; }
            if (_flashTarget == "KbmMBtn2") { _scrollWheelPill.Fill = highlight ? FlashBrush : ScrollWheelBrush; return; }

            if (_flashTarget.StartsWith("KbmMouse"))
            {
                _moveCircle.Stroke = highlight ? FlashBrush : DimBrush;
                _moveCircle.StrokeThickness = highlight ? 2.5 : 1.5;
                _moveArrow.Visibility = highlight ? Visibility.Visible : Visibility.Collapsed;
                _moveArrow.Fill = FlashBrush;
                double angle = _flashTarget switch
                {
                    "KbmMouseX" => 90, "KbmMouseXNeg" => 270,
                    "KbmMouseY" => 0, "KbmMouseYNeg" => 180, _ => 0
                };
                _moveArrowCanvas.RenderTransform = new RotateTransform(angle, MoveSize / 2, MoveSize / 2);
                return;
            }

            if (_flashTarget == "KbmScroll")
            {
                _scrollUpArrow.Fill = highlight ? FlashBrush : DimBrush;
                _scrollWheelPill.Stroke = highlight ? FlashBrush : DimBrush;
                _scrollWheelPill.StrokeThickness = highlight ? 2 : 1;
                return;
            }
            if (_flashTarget == "KbmScrollNeg")
            {
                _scrollDownArrow.Fill = highlight ? FlashBrush : DimBrush;
                _scrollWheelPill.Stroke = highlight ? FlashBrush : DimBrush;
                _scrollWheelPill.StrokeThickness = highlight ? 2 : 1;
                return;
            }
        }

        // ─────────────────────────────────────────────
        //  Rendering
        // ─────────────────────────────────────────────

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_dirty || _vm == null || !_layoutBuilt) return;
            _dirty = false;
            var kbm = _vm.KbmOutputSnapshot;

            // Keyboard keys
            foreach (var w in _keyWidgets)
            {
                if (_flashTarget == w.TargetName && _flashOn) continue;
                bool pressed = w.VKeyIndex >= 0 && w.VKeyIndex <= 255 && kbm.GetKey((byte)w.VKeyIndex);
                w.Border.Background = pressed ? KeyPressedBrush : KeyNormalBrush;
            }

            // Mouse buttons
            if (_flashTarget != "KbmMBtn0" || !_flashOn)
                _lmbPath.Fill = kbm.GetMouseButton(0) ? AccentBrush : MouseButtonBrush;
            if (_flashTarget != "KbmMBtn1" || !_flashOn)
                _rmbPath.Fill = kbm.GetMouseButton(1) ? AccentBrush : MouseButtonBrush;
            if (_flashTarget != "KbmMBtn2" || !_flashOn)
                _scrollWheelPill.Fill = kbm.GetMouseButton(2) ? AccentBrush : ScrollWheelBrush;

            // Movement dot — map output values directly (deadzone already applied in Step 3)
            if (_flashTarget == null || !_flashTarget.StartsWith("KbmMouse"))
            {
                double moveX = MC - MoveSize / 2;
                double centerX = moveX + MoveSize / 2 - 5;
                double centerY = MoveTop + MoveSize / 2 - 5;
                double maxDeflect = MoveSize / 2 - 8;
                short mx = kbm.MouseDeltaX, my = kbm.MouseDeltaY;

                double dotX = centerX + mx / 32767.0 * maxDeflect;
                double dotY = centerY - my / 32767.0 * maxDeflect;

                Canvas.SetLeft(_movementDot, dotX);
                Canvas.SetTop(_movementDot, dotY);
                _movementDot.Fill = (mx != 0 || my != 0) ? AccentBrush : DotBrush;
                if (_flashTarget == null) _moveArrow.Visibility = Visibility.Collapsed;
            }

            // Scroll direction visual feedback
            if (_flashTarget == null || !_flashTarget.StartsWith("KbmScroll"))
            {
                short scroll = kbm.ScrollDelta;
                _scrollUpArrow.Fill = scroll > 0 ? AccentBrush : DimBrush;
                _scrollDownArrow.Fill = scroll < 0 ? AccentBrush : DimBrush;
            }
        }

        private struct KbmKeyWidget
        {
            public int VKeyIndex;
            public Border Border;
            public string TargetName;
        }
    }
}
