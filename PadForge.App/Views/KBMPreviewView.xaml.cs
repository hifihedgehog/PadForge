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
        private Path _mmbPath;
        private Ellipse _movementDot;
        private Ellipse _moveCircle;
        private Polygon _moveArrow;
        private Canvas _moveArrowCanvas;
        private Ellipse _scrollCircle;

        // Colors
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush MouseBodyBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        private static readonly Brush MouseButtonBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
        private static readonly Brush MmbBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        private static readonly Brush DotBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly Brush KeyNormalBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x88, 0x88, 0x88));
        private static readonly Brush KeyPressedBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromRgb(0x40, 0xA0, 0xE0));
        private static readonly Brush FlashBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));

        // Movement circle size
        private const double MoveSize = 55;
        private const double ScrollSize = 28;
        // Mouse body layout constants (used in build + render)
        private const double MC = 80; // center X
        private const double MBodyH = 190; // total mouse height (buttons + body with embedded movement)
        private const double MBtnBottom = 60; // button area bottom Y

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
                var border = new Border
                {
                    Width = key.KeyWidth, Height = key.KeyHeight,
                    CornerRadius = new CornerRadius(3),
                    Background = KeyNormalBrush, Cursor = Cursors.Hand,
                    ToolTip = key.Label
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

                string targetName = $"KbmKey{key.VKeyIndex:X2}";
                border.MouseEnter += (s, e) => { if (_flashTarget == null) { border.BorderBrush = HoverBrush; border.BorderThickness = new Thickness(1.5); } };
                border.MouseLeave += (s, e) => { if (_flashTarget == null) { border.BorderBrush = null; border.BorderThickness = new Thickness(0); } };
                border.MouseLeftButtonDown += (s, e) => { ControllerElementRecordRequested?.Invoke(this, targetName); e.Handled = true; };
                _keyWidgets.Add(new KbmKeyWidget { VKeyIndex = key.VKeyIndex, Border = border, TargetName = targetName });
            }
        }

        // ─────────────────────────────────────────────
        //  Mouse — movement circle INSIDE the body
        // ─────────────────────────────────────────────

        private void BuildMouseCanvas()
        {
            MouseCanvas.Children.Clear();

            // Mouse body dimensions — moderate width, tall to contain movement area
            const double mW = 90;
            double mL = MC - mW / 2, mR = MC + mW / 2;

            // ── Body outline (tall, rounded, contains everything) ──
            var mouseBody = new Path
            {
                Data = Geometry.Parse(
                    $"M {mL},22 C {mL},8 {mL + 12},0 {MC},0 C {mR - 12},0 {mR},8 {mR},22" +
                    $" L {mR},{MBodyH - 22} C {mR},{MBodyH - 6} {mR - 12},{MBodyH} {MC},{MBodyH}" +
                    $" C {mL + 12},{MBodyH} {mL},{MBodyH - 6} {mL},{MBodyH - 22} Z"),
                Fill = MouseBodyBrush, Stroke = DimBrush, StrokeThickness = 2
            };
            MouseCanvas.Children.Add(mouseBody);

            // ── Button area: LMB | MMB (scroll) | RMB ──
            double btnDiv1 = MC - 8, btnDiv2 = MC + 8;

            _lmbPath = new Path
            {
                Data = Geometry.Parse($"M {mL + 2},22 C {mL + 2},10 {mL + 12},2 {btnDiv1},2 L {btnDiv1},{MBtnBottom} L {mL + 2},{MBtnBottom} Z"),
                Fill = MouseButtonBrush, Stroke = DimBrush, StrokeThickness = 1, Cursor = Cursors.Hand
            };
            MouseCanvas.Children.Add(_lmbPath);
            AddButtonHandlers(_lmbPath, "KbmMBtn0");

            _mmbPath = new Path
            {
                Data = Geometry.Parse($"M {btnDiv1},2 L {btnDiv2},2 L {btnDiv2},{MBtnBottom} L {btnDiv1},{MBtnBottom} Z"),
                Fill = MmbBrush, Stroke = Brushes.Transparent, StrokeThickness = 0, Cursor = Cursors.Hand
            };
            MouseCanvas.Children.Add(_mmbPath);
            AddButtonHandlers(_mmbPath, "KbmMBtn2");

            // Scroll wheel pill on MMB
            MouseCanvas.Children.Add(MakeRect(10, 16, MC - 5, 18,
                new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)), DimBrush, 5, 5));

            _rmbPath = new Path
            {
                Data = Geometry.Parse($"M {btnDiv2},2 C {mR - 12},2 {mR - 2},10 {mR - 2},22 L {mR - 2},{MBtnBottom} L {btnDiv2},{MBtnBottom} Z"),
                Fill = MouseButtonBrush, Stroke = DimBrush, StrokeThickness = 1, Cursor = Cursors.Hand
            };
            MouseCanvas.Children.Add(_rmbPath);
            AddButtonHandlers(_rmbPath, "KbmMBtn1");

            // Button labels
            Lbl("LMB", (mL + btnDiv1) / 2 - 9, MBtnBottom - 15);
            Lbl("MMB", MC - 9, MBtnBottom - 15, 6);
            Lbl("RMB", (btnDiv2 + mR) / 2 - 9, MBtnBottom - 15);

            // Thin horizontal separator between buttons and movement area
            MouseCanvas.Children.Add(new Line
            {
                X1 = mL + 8, Y1 = MBtnBottom + 4, X2 = mR - 8, Y2 = MBtnBottom + 4,
                Stroke = DimBrush, StrokeThickness = 0.5
            });

            // ── Movement circle — embedded in the body center ──
            double moveY = MBtnBottom + 12;
            double moveX = MC - MoveSize / 2;

            _moveCircle = new Ellipse
            {
                Width = MoveSize, Height = MoveSize,
                Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x88, 0x88, 0x88)),
                Stroke = DimBrush, StrokeThickness = 1.5, Cursor = Cursors.Hand
            };
            Canvas.SetLeft(_moveCircle, moveX);
            Canvas.SetTop(_moveCircle, moveY);
            MouseCanvas.Children.Add(_moveCircle);

            _movementDot = new Ellipse { Width = 10, Height = 10, Fill = DotBrush, IsHitTestVisible = false };
            Canvas.SetLeft(_movementDot, moveX + MoveSize / 2 - 5);
            Canvas.SetTop(_movementDot, moveY + MoveSize / 2 - 5);
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
            Canvas.SetTop(_moveArrowCanvas, moveY);
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

            // ── Scroll circle — below the mouse body ──
            double scrollY = MBodyH + 10;
            double scrollX = MC - ScrollSize / 2;

            _scrollCircle = new Ellipse
            {
                Width = ScrollSize, Height = ScrollSize,
                Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x88, 0x88, 0x88)),
                Stroke = DimBrush, StrokeThickness = 1.5, Cursor = Cursors.Hand
            };
            Canvas.SetLeft(_scrollCircle, scrollX);
            Canvas.SetTop(_scrollCircle, scrollY);
            MouseCanvas.Children.Add(_scrollCircle);

            var upA = MakeArrow(ScrollSize / 2, 5, ScrollSize / 2 - 4, 11, ScrollSize / 2 + 4, 11);
            var dnA = MakeArrow(ScrollSize / 2, ScrollSize - 5, ScrollSize / 2 - 4, ScrollSize - 11, ScrollSize / 2 + 4, ScrollSize - 11);
            var sac = new Canvas { Width = ScrollSize, Height = ScrollSize, IsHitTestVisible = false };
            sac.Children.Add(upA); sac.Children.Add(dnA);
            Canvas.SetLeft(sac, scrollX); Canvas.SetTop(sac, scrollY);
            MouseCanvas.Children.Add(sac);

            Lbl("Scroll", scrollX + ScrollSize / 2 - 14, scrollY + ScrollSize + 1);

            _scrollCircle.MouseEnter += (s, e) => { if (_flashTarget != null) return; _scrollCircle.Stroke = HoverBrush; _scrollCircle.StrokeThickness = 2.5; upA.Fill = HoverBrush; dnA.Fill = HoverBrush; };
            _scrollCircle.MouseLeave += (s, e) => { if (_flashTarget != null) return; _scrollCircle.Stroke = DimBrush; _scrollCircle.StrokeThickness = 1.5; upA.Fill = DimBrush; dnA.Fill = DimBrush; };
            _scrollCircle.MouseLeftButtonDown += (s, e) =>
            {
                var pos = e.GetPosition(_scrollCircle);
                ControllerElementRecordRequested?.Invoke(this, pos.Y < ScrollSize / 2 ? "KbmScroll" : "KbmScrollNeg");
                e.Handled = true;
            };

            MouseCanvas.Height = scrollY + ScrollSize + 16;
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

        private void Lbl(string text, double x, double y, double fs = 9)
        {
            var tb = new TextBlock { Text = text, FontSize = fs, Foreground = Brushes.Gray, IsHitTestVisible = false };
            Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
            MouseCanvas.Children.Add(tb);
        }

        private static Rectangle MakeRect(double w, double h, double x, double y, Brush fill, Brush stroke, double rx = 0, double ry = 0)
        {
            var r = new Rectangle { Width = w, Height = h, RadiusX = rx, RadiusY = ry, Fill = fill, Stroke = stroke, StrokeThickness = 1, IsHitTestVisible = false };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            return r;
        }

        private static Polygon MakeArrow(double tipX, double tipY, double lx, double ly, double rx, double ry)
        {
            return new Polygon
            {
                Points = new PointCollection { new Point(tipX, tipY), new Point(lx, ly), new Point(rx, ry) },
                Fill = DimBrush, IsHitTestVisible = false
            };
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
            if (_flashTarget == "KbmMBtn2") { _mmbPath.Fill = highlight ? FlashBrush : MmbBrush; return; }

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

            if (_flashTarget.StartsWith("KbmScroll"))
            {
                _scrollCircle.Stroke = highlight ? FlashBrush : DimBrush;
                _scrollCircle.StrokeThickness = highlight ? 2.5 : 1.5;
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

            foreach (var w in _keyWidgets)
            {
                if (_flashTarget == w.TargetName && _flashOn) continue;
                bool pressed = w.VKeyIndex >= 0 && w.VKeyIndex <= 255 && kbm.GetKey((byte)w.VKeyIndex);
                w.Border.Background = pressed ? KeyPressedBrush : KeyNormalBrush;
            }

            if (_flashTarget != "KbmMBtn0" || !_flashOn)
                _lmbPath.Fill = kbm.GetMouseButton(0) ? AccentBrush : MouseButtonBrush;
            if (_flashTarget != "KbmMBtn1" || !_flashOn)
                _rmbPath.Fill = kbm.GetMouseButton(1) ? AccentBrush : MouseButtonBrush;
            if (_flashTarget != "KbmMBtn2" || !_flashOn)
                _mmbPath.Fill = kbm.GetMouseButton(2) ? AccentBrush : MmbBrush;

            if (_flashTarget == null || !_flashTarget.StartsWith("KbmMouse"))
            {
                double moveY = MBtnBottom + 12;
                double moveX = MC - MoveSize / 2;
                double centerX = moveX + MoveSize / 2 - 5;
                double centerY = moveY + MoveSize / 2 - 5;
                double dotX = centerX, dotY = centerY;
                const short deadZone = 7849;
                double maxDeflect = MoveSize / 2 - 8;
                short mx = kbm.MouseDeltaX, my = kbm.MouseDeltaY;
                if (Math.Abs(mx) > deadZone)
                    dotX += (mx - Math.Sign(mx) * deadZone) / (double)(32767 - deadZone) * maxDeflect;
                if (Math.Abs(my) > deadZone)
                    dotY -= (my - Math.Sign(my) * deadZone) / (double)(32767 - deadZone) * maxDeflect;
                Canvas.SetLeft(_movementDot, dotX);
                Canvas.SetTop(_movementDot, dotY);
                _movementDot.Fill = (Math.Abs(mx) > deadZone || Math.Abs(my) > deadZone) ? AccentBrush : DotBrush;
                if (_flashTarget == null) _moveArrow.Visibility = Visibility.Collapsed;
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
