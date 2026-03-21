using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Common;

namespace PadForge.Controls
{
    public partial class CurveEditor : UserControl
    {
        // ── Dependency Properties ──

        public static readonly DependencyProperty CurveStringProperty =
            DependencyProperty.Register(nameof(CurveString), typeof(string), typeof(CurveEditor),
                new FrameworkPropertyMetadata("0,0;1,1",
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnCurveStringChanged));

        public static readonly DependencyProperty DeadZoneProperty =
            DependencyProperty.Register(nameof(DeadZone), typeof(double), typeof(CurveEditor),
                new PropertyMetadata(0.0, OnDisplayParamChanged));

        public static readonly DependencyProperty MaxRangeProperty =
            DependencyProperty.Register(nameof(MaxRange), typeof(double), typeof(CurveEditor),
                new PropertyMetadata(100.0, OnDisplayParamChanged));

        public static readonly DependencyProperty MaxRangeNegProperty =
            DependencyProperty.Register(nameof(MaxRangeNeg), typeof(double), typeof(CurveEditor),
                new PropertyMetadata(100.0, OnDisplayParamChanged));

        public static readonly DependencyProperty LiveInputProperty =
            DependencyProperty.Register(nameof(LiveInput), typeof(double), typeof(CurveEditor),
                new PropertyMetadata(0.0, OnLiveInputChanged));

        public static readonly DependencyProperty IsSignedProperty =
            DependencyProperty.Register(nameof(IsSigned), typeof(bool), typeof(CurveEditor),
                new PropertyMetadata(true, OnDisplayParamChanged));

        public static readonly DependencyProperty ChartSizeProperty =
            DependencyProperty.Register(nameof(ChartSize), typeof(double), typeof(CurveEditor),
                new PropertyMetadata(140.0, OnDisplayParamChanged));

        public string CurveString { get => (string)GetValue(CurveStringProperty); set => SetValue(CurveStringProperty, value); }
        public double DeadZone { get => (double)GetValue(DeadZoneProperty); set => SetValue(DeadZoneProperty, value); }
        public double MaxRange { get => (double)GetValue(MaxRangeProperty); set => SetValue(MaxRangeProperty, value); }
        public double MaxRangeNeg { get => (double)GetValue(MaxRangeNegProperty); set => SetValue(MaxRangeNegProperty, value); }
        public double LiveInput { get => (double)GetValue(LiveInputProperty); set => SetValue(LiveInputProperty, value); }
        public bool IsSigned { get => (bool)GetValue(IsSignedProperty); set => SetValue(IsSignedProperty, value); }
        public double ChartSize { get => (double)GetValue(ChartSizeProperty); set => SetValue(ChartSizeProperty, value); }

        /// <summary>Total size including border padding.</summary>
        public double TotalSize => ChartSize + 8;

        // ── Internal state ──

        private List<(double X, double Y)> _controlPoints = new() { (0, 0), (1, 1) };
        private readonly List<Ellipse> _pointEllipses = new();
        private int _dragIndex = -1;
        private bool _isDragging;

        // Visual elements
        private readonly Polyline _curveLine = new();
        private readonly Line _refDiag = new();
        private readonly Line _crossH = new();
        private readonly Line _crossV = new();
        private readonly Line _gridH25 = new();
        private readonly Line _gridH75 = new();
        private readonly Line _gridV25 = new();
        private readonly Line _gridV75 = new();
        private readonly Ellipse _liveDot = new();
        private Brush _gridBrush;
        private Brush _accentBrush;

        private const double PointRadius = 5;
        private const double HitRadius = 8;

        public CurveEditor()
        {
            InitializeComponent();
            Loaded += (_, _) => InitVisuals();
        }

        private void InitVisuals()
        {
            var canvas = ChartCanvas;
            double sz = ChartSize;
            canvas.Width = sz;
            canvas.Height = sz;

            canvas.Children.Clear();
            _pointEllipses.Clear();

            var gridBrush = TryFindResource("ControlStrokeColorSecondaryBrush") as Brush
                ?? Application.Current.TryFindResource("ControlStrokeColorSecondaryBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x5C));
            var accentBrush = TryFindResource("SystemAccentColorSecondaryBrush") as Brush
                ?? Application.Current.TryFindResource("SystemAccentColorSecondaryBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7));
            _gridBrush = gridBrush;
            _accentBrush = accentBrush;

            // Grid lines at 25%/75%
            SetupLine(_gridV25, gridBrush, 0.5, true); canvas.Children.Add(_gridV25);
            SetupLine(_gridV75, gridBrush, 0.5, true); canvas.Children.Add(_gridV75);
            SetupLine(_gridH25, gridBrush, 0.5, true); canvas.Children.Add(_gridH25);
            SetupLine(_gridH75, gridBrush, 0.5, true); canvas.Children.Add(_gridH75);

            // Crosshairs at center
            SetupLine(_crossH, gridBrush, 0.5); canvas.Children.Add(_crossH);
            SetupLine(_crossV, gridBrush, 0.5); canvas.Children.Add(_crossV);

            // Linear reference diagonal
            _refDiag.Stroke = gridBrush;
            _refDiag.StrokeThickness = 0.5;
            _refDiag.StrokeDashArray = new DoubleCollection { 4, 2 };
            canvas.Children.Add(_refDiag);

            // Curve line
            _curveLine.Stroke = accentBrush;
            _curveLine.StrokeThickness = 1.5;
            _curveLine.Fill = Brushes.Transparent;
            canvas.Children.Add(_curveLine);

            // Live dot
            _liveDot.Width = 7;
            _liveDot.Height = 7;
            _liveDot.Fill = accentBrush;
            canvas.Children.Add(_liveDot);

            ParseAndDraw();
        }

        private void SetupLine(Line line, Brush stroke, double thickness, bool isDashed = false)
        {
            line.Stroke = stroke;
            line.StrokeThickness = thickness;
            if (isDashed) line.StrokeDashArray = new DoubleCollection { 2, 4 };
        }

        // ── Coordinate mapping ──

        // For signed charts: input -1..+1 maps to 0..sz, output -1..+1 maps to sz..0
        // For unsigned charts: input 0..1 maps to 0..sz, output 0..1 maps to sz..0
        // Control points are always in unsigned 0..1 curve space.

        private (double px, double py) CurveToPixel(double cx, double cy)
        {
            double sz = ChartSize;
            if (IsSigned)
            {
                // Control points in 0..1 map to the positive quadrant (right half, top half)
                double half = sz / 2.0;
                return (half + cx * half, half - cy * half);
            }
            else
            {
                return (cx * sz, (1.0 - cy) * sz);
            }
        }

        private (double cx, double cy) PixelToCurve(double px, double py)
        {
            double sz = ChartSize;
            if (IsSigned)
            {
                double half = sz / 2.0;
                return ((px - half) / half, (half - py) / half);
            }
            else
            {
                return (px / sz, 1.0 - py / sz);
            }
        }

        /// <summary>Map a signed input value (-1..+1 or 0..1) to full pipeline output pixel position.</summary>
        private (double px, double py) InputToPixel(double input)
        {
            double sz = ChartSize;
            double dzN = DeadZone / 100.0;

            if (IsSigned)
            {
                // Pick max range based on direction of input.
                double mrN = (input >= 0 ? MaxRange : MaxRangeNeg) / 100.0;
                if (mrN <= dzN) mrN = dzN + 0.01;

                double sign = Math.Sign(input);
                double mag = Math.Abs(input);
                double output;
                if (mag < dzN)
                    output = 0;
                else
                {
                    double rem = Math.Min((mag - dzN) / (mrN - dzN), 1.0);
                    var lut = CurveLut.GetOrBuild(CurveString);
                    output = sign * (lut != null ? CurveLut.Lookup(lut, rem) : rem);
                }
                double half = sz / 2.0;
                return ((input + 1.0) * half, (1.0 - output) * half);
            }
            else
            {
                double mrN = MaxRange / 100.0;
                if (mrN <= dzN) mrN = dzN + 0.01;

                double output;
                if (input < dzN)
                    output = 0;
                else
                {
                    double rem = Math.Min((input - dzN) / (mrN - dzN), 1.0);
                    var lut = CurveLut.GetOrBuild(CurveString);
                    output = lut != null ? CurveLut.Lookup(lut, rem) : rem;
                }
                return (input * sz, (1.0 - output) * sz);
            }
        }

        // ── Drawing ──

        private void ParseAndDraw()
        {
            if (ChartCanvas == null || !IsLoaded) return;

            _controlPoints = CurveLut.Parse(CurveString ?? "0,0;1,1");
            DrawAll();
        }

        private void DrawAll()
        {
            if (ChartCanvas == null || !IsLoaded) return;
            double sz = ChartSize;
            ChartCanvas.Width = sz;
            ChartCanvas.Height = sz;

            // Grid lines
            if (IsSigned)
            {
                double half = sz / 2.0;
                _crossH.X1 = 0; _crossH.Y1 = half; _crossH.X2 = sz; _crossH.Y2 = half;
                _crossV.X1 = half; _crossV.Y1 = 0; _crossV.X2 = half; _crossV.Y2 = sz;
                _gridV25.X1 = sz * 0.25; _gridV25.Y1 = 0; _gridV25.X2 = sz * 0.25; _gridV25.Y2 = sz;
                _gridV75.X1 = sz * 0.75; _gridV75.Y1 = 0; _gridV75.X2 = sz * 0.75; _gridV75.Y2 = sz;
                _gridH25.X1 = 0; _gridH25.Y1 = sz * 0.25; _gridH25.X2 = sz; _gridH25.Y2 = sz * 0.25;
                _gridH75.X1 = 0; _gridH75.Y1 = sz * 0.75; _gridH75.X2 = sz; _gridH75.Y2 = sz * 0.75;
                _refDiag.X1 = 0; _refDiag.Y1 = sz; _refDiag.X2 = sz; _refDiag.Y2 = 0;
            }
            else
            {
                _crossH.X1 = 0; _crossH.Y1 = sz / 2; _crossH.X2 = sz; _crossH.Y2 = sz / 2;
                _crossV.X1 = sz / 2; _crossV.Y1 = 0; _crossV.X2 = sz / 2; _crossV.Y2 = sz;
                _gridV25.X1 = sz * 0.25; _gridV25.Y1 = 0; _gridV25.X2 = sz * 0.25; _gridV25.Y2 = sz;
                _gridV75.X1 = sz * 0.75; _gridV75.Y1 = 0; _gridV75.X2 = sz * 0.75; _gridV75.Y2 = sz;
                _gridH25.X1 = 0; _gridH25.Y1 = sz * 0.25; _gridH25.X2 = sz; _gridH25.Y2 = sz * 0.25;
                _gridH75.X1 = 0; _gridH75.Y1 = sz * 0.75; _gridH75.X2 = sz; _gridH75.Y2 = sz * 0.75;
                _refDiag.X1 = 0; _refDiag.Y1 = sz; _refDiag.X2 = sz; _refDiag.Y2 = 0;
            }

            DrawCurveLine();
            DrawControlPoints();
            UpdateLiveDot();
        }

        private void DrawCurveLine()
        {
            double sz = ChartSize;
            int samples = (int)sz;
            var pts = new PointCollection(samples + 1);

            if (IsSigned)
            {
                for (int i = 0; i <= samples; i++)
                {
                    double input = (double)i / samples * 2.0 - 1.0; // -1..+1
                    var (px, py) = InputToPixel(input);
                    pts.Add(new Point(px, py));
                }
            }
            else
            {
                for (int i = 0; i <= samples; i++)
                {
                    double input = (double)i / samples; // 0..1
                    var (px, py) = InputToPixel(input);
                    pts.Add(new Point(px, py));
                }
            }

            _curveLine.Points = pts;
        }

        private void DrawControlPoints()
        {
            // Remove old point ellipses
            foreach (var e in _pointEllipses)
                ChartCanvas.Children.Remove(e);
            _pointEllipses.Clear();

            var accentBrush = _accentBrush ?? Brushes.DodgerBlue;

            for (int i = 0; i < _controlPoints.Count; i++)
            {
                var (cx, cy) = _controlPoints[i];
                var (px, py) = CurveToPixel(cx, cy);

                bool isEndpoint = (i == 0 || i == _controlPoints.Count - 1);
                var ellipse = new Ellipse
                {
                    Width = PointRadius * 2,
                    Height = PointRadius * 2,
                    Fill = isEndpoint ? Brushes.White : accentBrush,
                    Stroke = accentBrush,
                    StrokeThickness = 1.5,
                    Cursor = Cursors.Hand,
                    ToolTip = $"({cx:F2}, {cy:F2})"
                };

                Canvas.SetLeft(ellipse, px - PointRadius);
                Canvas.SetTop(ellipse, py - PointRadius);
                ChartCanvas.Children.Add(ellipse);
                _pointEllipses.Add(ellipse);
            }
        }

        private void UpdateLiveDot()
        {
            double input = LiveInput;
            var (px, py) = InputToPixel(input);
            Canvas.SetLeft(_liveDot, px - 3.5);
            Canvas.SetTop(_liveDot, py - 3.5);
        }

        // ── Mouse interaction ──

        private int HitTestPoint(Point mousePos)
        {
            for (int i = _controlPoints.Count - 1; i >= 0; i--)
            {
                var (px, py) = CurveToPixel(_controlPoints[i].X, _controlPoints[i].Y);
                double dx = mousePos.X - px, dy = mousePos.Y - py;
                if (dx * dx + dy * dy <= HitRadius * HitRadius)
                    return i;
            }
            return -1;
        }

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(ChartCanvas);
            int hit = HitTestPoint(pos);

            if (hit >= 0)
            {
                // Start dragging
                _dragIndex = hit;
                _isDragging = true;
                ChartCanvas.CaptureMouse();
                e.Handled = true;
            }
            else if (e.ClickCount == 2)
            {
                // Double-click: add a new point
                var (cx, cy) = PixelToCurve(pos.X, pos.Y);
                cx = Math.Clamp(cx, 0.01, 0.99);
                cy = Math.Clamp(cy, 0, 1);

                // Find insertion index (sorted by X)
                int insertAt = 0;
                for (int i = 0; i < _controlPoints.Count; i++)
                {
                    if (_controlPoints[i].X < cx) insertAt = i + 1;
                }

                _controlPoints.Insert(insertAt, (cx, cy));
                CommitPoints();
                e.Handled = true;
            }
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                _dragIndex = -1;
                ChartCanvas.ReleaseMouseCapture();
                CommitPoints();
                e.Handled = true;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _dragIndex < 0) return;

            var pos = e.GetPosition(ChartCanvas);
            var (cx, cy) = PixelToCurve(pos.X, pos.Y);
            cy = Math.Clamp(cy, 0, 1);

            bool isFirst = _dragIndex == 0;
            bool isLast = _dragIndex == _controlPoints.Count - 1;

            if (isFirst || isLast)
            {
                // Endpoints: X locked, Y draggable
                _controlPoints[_dragIndex] = (_controlPoints[_dragIndex].X, cy);
            }
            else
            {
                // Interior points: X constrained between neighbors
                double minX = _controlPoints[_dragIndex - 1].X + 0.01;
                double maxX = _controlPoints[_dragIndex + 1].X - 0.01;
                cx = Math.Clamp(cx, minX, maxX);
                _controlPoints[_dragIndex] = (cx, cy);
            }

            // Update visuals immediately during drag
            DrawCurveLine();
            DrawControlPoints();
            UpdateLiveDot();
            e.Handled = true;
        }

        private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(ChartCanvas);
            int hit = HitTestPoint(pos);

            // Can only remove interior points (not endpoints)
            if (hit > 0 && hit < _controlPoints.Count - 1)
            {
                _controlPoints.RemoveAt(hit);
                CommitPoints();
                e.Handled = true;
            }
        }

        private void CommitPoints()
        {
            // Invalidate LUT cache for old string
            CurveString = CurveLut.Serialize(_controlPoints);
            DrawCurveLine();
            DrawControlPoints();
            UpdateLiveDot();
        }

        // ── Property change handlers ──

        private static void OnCurveStringChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CurveEditor editor && editor.IsLoaded)
                editor.ParseAndDraw();
        }

        private static void OnDisplayParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CurveEditor editor && editor.IsLoaded)
                editor.DrawAll();
        }

        private static void OnLiveInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CurveEditor editor && editor.IsLoaded)
                editor.UpdateLiveDot();
        }
    }
}
