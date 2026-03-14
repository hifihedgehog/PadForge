using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.ViewModels;

namespace PadForge.Views
{
    /// <summary>
    /// Read-only mouse graphic for the Devices page detail pane.
    /// Shows LMB, RMB, MMB, scroll wheel with intensity arrows,
    /// movement circle with deflection dot, and side buttons.
    /// </summary>
    public partial class MousePreviewControl : UserControl
    {
        private Path _lmbPath, _rmbPath;
        private Rectangle _scrollWheelPill;
        private Polygon _scrollUpArrow, _scrollDownArrow;
        private Ellipse _movementDot, _moveCircle;

        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        private static readonly Brush MouseBodyBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        private static readonly Brush MouseButtonBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
        private static readonly Brush MmbBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        private static readonly Brush ScrollWheelBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38));
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        private static readonly Brush DotBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        private const double MC = 80;
        private const double MoveSize = 55;
        private const double BtnBottom = 58;
        private const double MoveTop = 86;

        private Rectangle _x1Rect, _x2Rect;
        private bool _built;

        public MousePreviewControl()
        {
            InitializeComponent();
            CompositionTarget.Rendering += OnRendering;
            Loaded += (s, e) => BuildMouse();
        }

        private void BuildMouse()
        {
            if (_built) return;
            _built = true;
            MouseCanvas.Children.Clear();

            const double mW = 100;
            double mL = MC - mW / 2;
            double mR = MC + mW / 2;
            const double mH = 185;

            const double swW = 14, swH = 36;
            double swL = MC - swW / 2;
            double swR = MC + swW / 2;
            const double swTop = 13;
            const double swBot = swTop + swH;

            double gapL = swL - 1;
            double gapR = swR + 1;

            // Mouse body
            MouseCanvas.Children.Add(new Path
            {
                Data = Geometry.Parse(
                    $"M {mL},18 C {mL},6 {mL + 14},0 {MC},0 C {mR - 14},0 {mR},6 {mR},18" +
                    $" L {mR},{mH - 18} C {mR},{mH - 4} {mR - 14},{mH} {MC},{mH}" +
                    $" C {mL + 14},{mH} {mL},{mH - 4} {mL},{mH - 18} Z"),
                Fill = MouseBodyBrush, Stroke = DimBrush, StrokeThickness = 2
            });

            // LMB
            _lmbPath = new Path
            {
                Data = Geometry.Parse(
                    $"M {MC - 2},2 Q {gapL},{swTop - 4} {gapL},{swTop + 4} " +
                    $"L {gapL},{BtnBottom} L {mL + 2},{BtnBottom} L {mL + 2},18 " +
                    $"C {mL + 2},8 {mL + 14},2 {MC - 2},2 Z"),
                Fill = MouseButtonBrush, Stroke = DimBrush, StrokeThickness = 1
            };
            MouseCanvas.Children.Add(_lmbPath);

            // RMB
            _rmbPath = new Path
            {
                Data = Geometry.Parse(
                    $"M {MC + 2},2 Q {gapR},{swTop - 4} {gapR},{swTop + 4} " +
                    $"L {gapR},{BtnBottom} L {mR - 2},{BtnBottom} L {mR - 2},18 " +
                    $"C {mR - 2},8 {mR - 14},2 {MC + 2},2 Z"),
                Fill = MouseButtonBrush, Stroke = DimBrush, StrokeThickness = 1
            };
            MouseCanvas.Children.Add(_rmbPath);

            // MMB channel
            var mmbChannel = new Rectangle
            {
                Width = gapR - gapL, Height = BtnBottom - 2,
                Fill = MmbBrush, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false
            };
            Canvas.SetLeft(mmbChannel, gapL);
            Canvas.SetTop(mmbChannel, 2);
            MouseCanvas.Children.Add(mmbChannel);

            // Scroll wheel pill
            _scrollWheelPill = new Rectangle
            {
                Width = swW, Height = swH,
                RadiusX = swW / 2, RadiusY = swW / 2,
                Fill = ScrollWheelBrush, Stroke = DimBrush, StrokeThickness = 1
            };
            Canvas.SetLeft(_scrollWheelPill, swL);
            Canvas.SetTop(_scrollWheelPill, swTop);
            MouseCanvas.Children.Add(_scrollWheelPill);

            // Scroll arrows
            _scrollUpArrow = new Polygon
            {
                Points = new PointCollection { new Point(MC, swTop + 4), new Point(MC - 4, swTop + 10), new Point(MC + 4, swTop + 10) },
                Fill = DimBrush
            };
            MouseCanvas.Children.Add(_scrollUpArrow);

            _scrollDownArrow = new Polygon
            {
                Points = new PointCollection { new Point(MC, swBot - 4), new Point(MC - 4, swBot - 10), new Point(MC + 4, swBot - 10) },
                Fill = DimBrush
            };
            MouseCanvas.Children.Add(_scrollDownArrow);

            // Separator
            MouseCanvas.Children.Add(new Line
            {
                X1 = mL + 8, Y1 = BtnBottom + 6, X2 = mR - 8, Y2 = BtnBottom + 6,
                Stroke = DimBrush, StrokeThickness = 0.5
            });

            // Movement circle
            double moveX = MC - MoveSize / 2;
            _moveCircle = new Ellipse
            {
                Width = MoveSize, Height = MoveSize,
                Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x88, 0x88, 0x88)),
                Stroke = DimBrush, StrokeThickness = 1.5
            };
            Canvas.SetLeft(_moveCircle, moveX);
            Canvas.SetTop(_moveCircle, MoveTop);
            MouseCanvas.Children.Add(_moveCircle);

            _movementDot = new Ellipse { Width = 10, Height = 10, Fill = DotBrush, IsHitTestVisible = false };
            Canvas.SetLeft(_movementDot, moveX + MoveSize / 2 - 5);
            Canvas.SetTop(_movementDot, MoveTop + MoveSize / 2 - 5);
            MouseCanvas.Children.Add(_movementDot);

            // Side buttons
            _x1Rect = new Rectangle
            {
                Width = 8, Height = 14, RadiusX = 2, RadiusY = 2,
                Fill = MouseButtonBrush, Stroke = DimBrush, StrokeThickness = 1
            };
            Canvas.SetLeft(_x1Rect, mL - 4); Canvas.SetTop(_x1Rect, 70);
            MouseCanvas.Children.Add(_x1Rect);

            _x2Rect = new Rectangle
            {
                Width = 8, Height = 14, RadiusX = 2, RadiusY = 2,
                Fill = MouseButtonBrush, Stroke = DimBrush, StrokeThickness = 1
            };
            Canvas.SetLeft(_x2Rect, mL - 4); Canvas.SetTop(_x2Rect, 88);
            MouseCanvas.Children.Add(_x2Rect);

            MouseCanvas.Height = mH + 6;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_built) return;
            var vm = DataContext as DevicesViewModel;
            if (vm == null || !vm.IsMouseDevice) return;

            // Buttons: LMB=0, MMB=1, RMB=2, X1=3, X2=4
            bool lmb = vm.RawButtons.Count > 0 && vm.RawButtons[0].IsPressed;
            bool mmb = vm.RawButtons.Count > 1 && vm.RawButtons[1].IsPressed;
            bool rmb = vm.RawButtons.Count > 2 && vm.RawButtons[2].IsPressed;
            bool x1 = vm.RawButtons.Count > 3 && vm.RawButtons[3].IsPressed;
            bool x2 = vm.RawButtons.Count > 4 && vm.RawButtons[4].IsPressed;

            _lmbPath.Fill = lmb ? AccentBrush : MouseButtonBrush;
            _rmbPath.Fill = rmb ? AccentBrush : MouseButtonBrush;
            _scrollWheelPill.Fill = mmb ? AccentBrush : ScrollWheelBrush;
            _x1Rect.Fill = x1 ? AccentBrush : MouseButtonBrush;
            _x2Rect.Fill = x2 ? AccentBrush : MouseButtonBrush;

            // Movement dot
            double moveX = MC - MoveSize / 2;
            double centerX = moveX + MoveSize / 2 - 5;
            double centerY = MoveTop + MoveSize / 2 - 5;
            double maxDeflect = MoveSize / 2 - 8;

            double mx = vm.MouseMotionX;
            double my = vm.MouseMotionY;
            Canvas.SetLeft(_movementDot, centerX + mx * maxDeflect);
            Canvas.SetTop(_movementDot, centerY - my * maxDeflect);
            _movementDot.Fill = (Math.Abs(mx) > 0.01 || Math.Abs(my) > 0.01) ? AccentBrush : DotBrush;

            // Scroll arrows — intensity varies with scroll magnitude
            double scroll = vm.MouseScrollIntensity;
            double absScroll = Math.Min(Math.Abs(scroll), 1.0);
            if (scroll > 0.01)
            {
                _scrollUpArrow.Fill = AccentBrush;
                _scrollUpArrow.Opacity = 0.3 + 0.7 * absScroll;
                _scrollUpArrow.RenderTransform = new ScaleTransform(1.0 + 0.4 * absScroll, 1.0 + 0.4 * absScroll, MC, 7);
                _scrollDownArrow.Fill = DimBrush;
                _scrollDownArrow.Opacity = 1.0;
                _scrollDownArrow.RenderTransform = null;
            }
            else if (scroll < -0.01)
            {
                _scrollDownArrow.Fill = AccentBrush;
                _scrollDownArrow.Opacity = 0.3 + 0.7 * absScroll;
                _scrollDownArrow.RenderTransform = new ScaleTransform(1.0 + 0.4 * absScroll, 1.0 + 0.4 * absScroll, MC, swBotConst - 7);
                _scrollUpArrow.Fill = DimBrush;
                _scrollUpArrow.Opacity = 1.0;
                _scrollUpArrow.RenderTransform = null;
            }
            else
            {
                _scrollUpArrow.Fill = DimBrush;
                _scrollUpArrow.Opacity = 1.0;
                _scrollUpArrow.RenderTransform = null;
                _scrollDownArrow.Fill = DimBrush;
                _scrollDownArrow.Opacity = 1.0;
                _scrollDownArrow.RenderTransform = null;
            }
        }

        private const double swBotConst = 13 + 36; // swTop + swH
    }
}
