using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Common;
using PadForge.Resources.Strings;

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

        // Full-width forge canvas (#175 competitor item 4): the editor
        // stretches to the parent width at a fixed ChartHeight instead of
        // the square ChartSize. Set once in XAML before load. Runtime
        // flips are not supported.
        public static readonly DependencyProperty IsFullWidthProperty =
            DependencyProperty.Register(nameof(IsFullWidth), typeof(bool), typeof(CurveEditor),
                new PropertyMetadata(false));

        public static readonly DependencyProperty ChartHeightProperty =
            DependencyProperty.Register(nameof(ChartHeight), typeof(double), typeof(CurveEditor),
                new PropertyMetadata(180.0, OnDisplayParamChanged));

        public string CurveString { get => (string)GetValue(CurveStringProperty); set => SetValue(CurveStringProperty, value); }
        public double DeadZone { get => (double)GetValue(DeadZoneProperty); set => SetValue(DeadZoneProperty, value); }
        public double MaxRange { get => (double)GetValue(MaxRangeProperty); set => SetValue(MaxRangeProperty, value); }
        public double MaxRangeNeg { get => (double)GetValue(MaxRangeNegProperty); set => SetValue(MaxRangeNegProperty, value); }
        public double LiveInput { get => (double)GetValue(LiveInputProperty); set => SetValue(LiveInputProperty, value); }
        public bool IsSigned { get => (bool)GetValue(IsSignedProperty); set => SetValue(IsSignedProperty, value); }
        public double ChartSize { get => (double)GetValue(ChartSizeProperty); set => SetValue(ChartSizeProperty, value); }
        public bool IsFullWidth { get => (bool)GetValue(IsFullWidthProperty); set => SetValue(IsFullWidthProperty, value); }
        public double ChartHeight { get => (double)GetValue(ChartHeightProperty); set => SetValue(ChartHeightProperty, value); }

        /// <summary>Total size including border padding.</summary>
        public double TotalSize => ChartSize + 8;

        // Plot dimensions: square ChartSize by default. In full-width mode
        // the width tracks the canvas and the height is ChartHeight. All
        // mapping/drawing below goes through these so both modes share one
        // geometry path (w == h == ChartSize reduces to the original math).
        private double PlotWidth => IsFullWidth
            ? Math.Max(ChartCanvas?.ActualWidth ?? 0.0, 1.0)
            : ChartSize;
        private double PlotHeight => IsFullWidth ? ChartHeight : ChartSize;

        // ── Internal state ──

        private List<(double X, double Y)> _controlPoints = new() { (0, 0), (1, 1) };
        private readonly List<Ellipse> _pointEllipses = new();
        private int _dragIndex = -1;
        private bool _isDragging;
        // The point the keys act on while the editor holds keyboard focus.
        private int _keyIndex;

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
        // Mono coordinate readout shown beside the dragged node in
        // full-width mode only (#175 competitor item 4), e.g. "62, 48".
        private readonly TextBlock _dragReadout = new();

        // Ember palette (steel curve/handles, ember-hot live dot + dragged handle).
        // Curve/handle brushes resolve from the themed text ramp in InitVisuals so
        // light mode gets its own values; these frozen statics are fallbacks only.
        private static readonly SolidColorBrush CurveStrokeFallbackBrush = CreateFrozen(0x94, 0xA3, 0xBD);
        private static readonly SolidColorBrush HandleFallbackBrush = CreateFrozen(0x5D, 0x6B, 0x85);
        private static readonly SolidColorBrush EmberHotBrush = CreateFrozen(0xFF, 0xA2, 0x4D);
        // Steel fallbacks for full-width mode (SteelLineBrush / SteelCardBrush values).
        private static readonly SolidColorBrush SteelLineFallbackBrush = CreateFrozen(0x25, 0x30, 0x49);
        private static readonly SolidColorBrush ReadoutBackBrush = CreateFrozenChip();

        private static SolidColorBrush CreateFrozenChip()
        {
            var brush = new SolidColorBrush(Color.FromArgb(0xD9, 0x11, 0x16, 0x23));
            brush.Freeze();
            return brush;
        }

        private Brush _curveStrokeBrush = CurveStrokeFallbackBrush;
        private Brush _handleBrush = HandleFallbackBrush;

        private static SolidColorBrush CreateFrozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // Hover glow (#175): handles flip ember-hot under the cursor (the
        // same hue they hold while dragged), so the bloom is ember. Small
        // glyph, so BlurRadius 8. Shared + frozen, set directly on the
        // element, never animated.
        private static readonly System.Windows.Media.Effects.DropShadowEffect HandleHoverGlow = CreateFrozenHandleGlow();

        private static System.Windows.Media.Effects.DropShadowEffect CreateFrozenHandleGlow()
        {
            var fx = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0x6B, 0x2C),
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.45
            };
            fx.Freeze();
            return fx;
        }

        private const double PointRadius = 5;
        private const double HitRadius = 8;

        public CurveEditor()
        {
            InitializeComponent();
            ChartCanvas.LostMouseCapture += Canvas_LostMouseCapture;
            Loaded += (_, _) => InitVisuals();
            // Full-width mode: the plot width follows the parent, so any
            // resize redraws by value change (no animation loop involved).
            ChartCanvas.SizeChanged += (_, _) => { if (IsFullWidth && IsLoaded) DrawAll(); };
        }

        private void InitVisuals()
        {
            var canvas = ChartCanvas;
            if (IsFullWidth)
            {
                // Stretch horizontally: drop the square Width binding so the
                // border fills the parent, and pin height to the chart + padding.
                RootBorder.ClearValue(WidthProperty);
                RootBorder.Height = ChartHeight + 8;
                canvas.Height = ChartHeight;
            }
            else
            {
                double sz = ChartSize;
                canvas.Width = sz;
                canvas.Height = sz;
            }

            canvas.Children.Clear();
            _pointEllipses.Clear();

            var gridBrush = TryFindResource("ControlStrokeColorSecondaryBrush") as Brush
                ?? Application.Current?.TryFindResource("ControlStrokeColorSecondaryBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x5C));

            _curveStrokeBrush = TryFindResource("TextFillColorSecondaryBrush") as Brush
                ?? Application.Current?.TryFindResource("TextFillColorSecondaryBrush") as Brush
                ?? CurveStrokeFallbackBrush;
            _handleBrush = TryFindResource("TextFillColorTertiaryBrush") as Brush
                ?? Application.Current?.TryFindResource("TextFillColorTertiaryBrush") as Brush
                ?? HandleFallbackBrush;

            // Grid lines at 25%/75%
            SetupLine(_gridV25, gridBrush, 0.5, true); canvas.Children.Add(_gridV25);
            SetupLine(_gridV75, gridBrush, 0.5, true); canvas.Children.Add(_gridV75);
            SetupLine(_gridH25, gridBrush, 0.5, true); canvas.Children.Add(_gridH25);
            SetupLine(_gridH75, gridBrush, 0.5, true); canvas.Children.Add(_gridH75);

            // Crosshairs at center
            SetupLine(_crossH, gridBrush, 0.5); canvas.Children.Add(_crossH);
            SetupLine(_crossV, gridBrush, 0.5); canvas.Children.Add(_crossV);

            // Linear reference diagonal. The full-width forge canvas dashes
            // it in steel (#175 competitor item 4). Fixed squares keep the
            // original grid tone so the radar-sidebar mini-plots are
            // pixel-identical.
            var steelBrush = TryFindResource("SteelLineBrush") as Brush
                ?? Application.Current?.TryFindResource("SteelLineBrush") as Brush
                ?? (Brush)SteelLineFallbackBrush;
            _refDiag.Stroke = IsFullWidth ? steelBrush : gridBrush;
            _refDiag.StrokeThickness = IsFullWidth ? 1.0 : 0.5;
            _refDiag.StrokeDashArray = new DoubleCollection { 4, 2 };
            canvas.Children.Add(_refDiag);

            // Curve line
            _curveLine.Stroke = _curveStrokeBrush;
            _curveLine.StrokeThickness = 1.5;
            _curveLine.Fill = Brushes.Transparent;
            canvas.Children.Add(_curveLine);

            // Live dot (ember-hot with a small ember glow; effect set directly on
            // the element, never animated from a style trigger)
            _liveDot.Width = 7;
            _liveDot.Height = 7;
            _liveDot.Fill = EmberHotBrush;
            _liveDot.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0x82, 0x28),
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.9
            };
            canvas.Children.Add(_liveDot);
            // Keep the live dot above the handle ellipses DrawControlPoints appends later.
            Panel.SetZIndex(_liveDot, 10);

            // Drag coordinate readout (full-width mode only): mono chip that
            // rides beside the grabbed node while dragging. Value-driven,
            // no animation, hidden except during a drag.
            if (IsFullWidth)
            {
                _dragReadout.FontFamily = TryFindResource("TelemetryFontFamily") as FontFamily
                    ?? Application.Current?.TryFindResource("TelemetryFontFamily") as FontFamily
                    ?? new FontFamily("Consolas");
                _dragReadout.FontSize = 10;
                _dragReadout.Foreground = EmberHotBrush;
                _dragReadout.Background = ReadoutBackBrush;
                _dragReadout.Padding = new Thickness(4, 1, 4, 1);
                _dragReadout.IsHitTestVisible = false;
                _dragReadout.Visibility = Visibility.Collapsed;
                canvas.Children.Add(_dragReadout);
                Panel.SetZIndex(_dragReadout, 20);
            }

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
            double w = PlotWidth, h = PlotHeight;
            if (IsSigned)
            {
                // Control points in 0..1 map to the positive quadrant (right half, top half)
                return (w / 2.0 + cx * (w / 2.0), h / 2.0 - cy * (h / 2.0));
            }
            else
            {
                return (cx * w, (1.0 - cy) * h);
            }
        }

        private (double cx, double cy) PixelToCurve(double px, double py)
        {
            double w = PlotWidth, h = PlotHeight;
            if (IsSigned)
            {
                return ((px - w / 2.0) / (w / 2.0), (h / 2.0 - py) / (h / 2.0));
            }
            else
            {
                return (px / w, 1.0 - py / h);
            }
        }

        /// <summary>Map a signed input value (-1..+1 or 0..1) to full pipeline output pixel position.</summary>
        private (double px, double py) InputToPixel(double input)
        {
            double w = PlotWidth, h = PlotHeight;
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
                return ((input + 1.0) * (w / 2.0), (1.0 - output) * (h / 2.0));
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
                return (input * w, (1.0 - output) * h);
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
            if (IsFullWidth)
            {
                // Width follows the parent. Only the height is pinned.
                ChartCanvas.Height = ChartHeight;
            }
            else
            {
                ChartCanvas.Width = ChartSize;
                ChartCanvas.Height = ChartSize;
            }

            // Grid lines (same layout for signed and unsigned charts)
            double w = PlotWidth, h = PlotHeight;
            _crossH.X1 = 0; _crossH.Y1 = h / 2; _crossH.X2 = w; _crossH.Y2 = h / 2;
            _crossV.X1 = w / 2; _crossV.Y1 = 0; _crossV.X2 = w / 2; _crossV.Y2 = h;
            _gridV25.X1 = w * 0.25; _gridV25.Y1 = 0; _gridV25.X2 = w * 0.25; _gridV25.Y2 = h;
            _gridV75.X1 = w * 0.75; _gridV75.Y1 = 0; _gridV75.X2 = w * 0.75; _gridV75.Y2 = h;
            _gridH25.X1 = 0; _gridH25.Y1 = h * 0.25; _gridH25.X2 = w; _gridH25.Y2 = h * 0.25;
            _gridH75.X1 = 0; _gridH75.Y1 = h * 0.75; _gridH75.X2 = w; _gridH75.Y2 = h * 0.75;
            _refDiag.X1 = 0; _refDiag.Y1 = h; _refDiag.X2 = w; _refDiag.Y2 = 0;

            DrawCurveLine();
            DrawControlPoints();
            UpdateLiveDot();
        }

        private void DrawCurveLine()
        {
            int samples = Math.Max((int)PlotWidth, 2);
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

            _keyIndex = Math.Clamp(_keyIndex, 0, Math.Max(0, _controlPoints.Count - 1));
            int marked = MarkedIndex;
            for (int i = 0; i < _controlPoints.Count; i++)
            {
                var (cx, cy) = _controlPoints[i];
                var (px, py) = CurveToPixel(cx, cy);

                // The grabbed handle, or the point the keys act on.
                bool isDragged = i == marked;
                var ellipse = new Ellipse
                {
                    Width = PointRadius * 2,
                    Height = PointRadius * 2,
                    Fill = isDragged ? EmberHotBrush : _handleBrush,
                    Stroke = isDragged ? EmberHotBrush : _curveStrokeBrush,
                    StrokeThickness = 1.5,
                    Cursor = Cursors.Hand,
                    ToolTip = $"({cx:F2}, {cy:F2})"
                };

                // Grabbed handle keeps its ember bloom across the per-move
                // rebuilds (mouse capture suppresses MouseEnter here).
                if (isDragged)
                    ellipse.Effect = HandleHoverGlow;
                ellipse.MouseEnter += Handle_MouseEnter;
                ellipse.MouseLeave += Handle_MouseLeave;

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

        /// <summary>The point drawn ember-hot: the grabbed handle during a
        /// drag, else the point the keys act on while the editor holds
        /// keyboard focus, else none. A mouse click clears keyboard focus
        /// (MainWindow), so the keyboard's mark never shows to the mouse.</summary>
        private int MarkedIndex => _isDragging ? _dragIndex : IsKeyboardFocused ? _keyIndex : -1;

        // Mono coordinate readout beside the dragged node ("62, 48" in curve
        // percent), full-width mode only. Purely value-driven: shown on grab,
        // repositioned per move, hidden on release. The point the keys act on
        // shows it the same way while the editor holds keyboard focus.
        private void UpdateDragReadout()
        {
            int marked = MarkedIndex;
            if (!IsFullWidth || marked < 0 || marked >= _controlPoints.Count)
                return;

            var (cx, cy) = _controlPoints[marked];
            _dragReadout.Text = $"{Math.Round(cx * 100):0}, {Math.Round(cy * 100):0}";

            var (px, py) = CurveToPixel(cx, cy);
            _dragReadout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double rw = _dragReadout.DesiredSize.Width;
            double rh = _dragReadout.DesiredSize.Height;
            Canvas.SetLeft(_dragReadout, Math.Clamp(px + 10, 0, Math.Max(0, PlotWidth - rw)));
            Canvas.SetTop(_dragReadout, Math.Clamp(py - rh - 8, 0, Math.Max(0, PlotHeight - rh)));
            _dragReadout.Visibility = Visibility.Visible;
        }

        private void HideDragReadout()
        {
            _dragReadout.Visibility = Visibility.Collapsed;
        }

        // ── Mouse interaction ──

        // Hover glow (#175): a hovered handle previews the grab state,
        // ember-hot fill plus the ember bloom. Restored to steel on leave
        // unless it is the handle being dragged.
        private void Handle_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isDragging) return;
            if (sender is not Ellipse ellipse) return;
            ellipse.Fill = EmberHotBrush;
            ellipse.Stroke = EmberHotBrush;
            ellipse.Effect = HandleHoverGlow;
        }

        private void Handle_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is not Ellipse ellipse) return;
            int i = _pointEllipses.IndexOf(ellipse);
            if (i == MarkedIndex) return;
            ellipse.Fill = _handleBrush;
            ellipse.Stroke = _curveStrokeBrush;
            ellipse.Effect = null;
        }

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
                DrawControlPoints(); // recolor the grabbed handle ember-hot
                UpdateDragReadout();
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

        /// <summary>Capture can be taken away without a MouseLeftButtonUp
        /// (an Alt-Tab, a modal opening, another control grabbing it). Without
        /// this the drag flags stayed set and button-free mouse moves kept
        /// editing the control point.</summary>
        private void Canvas_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            _dragIndex = -1;
            HideDragReadout();
            CommitPoints();
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                _dragIndex = -1;
                ChartCanvas.ReleaseMouseCapture();
                HideDragReadout();
                CommitPoints();
                e.Handled = true;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // Bounds on BOTH sides, like the UpdateLiveDot sibling above.
            // A lower-bound-only guard let a stale index index past the end.
            if (!_isDragging || _dragIndex < 0 || _dragIndex >= _controlPoints.Count) return;

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
            UpdateDragReadout();
            e.Handled = true;
        }

        private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(ChartCanvas);
            int hit = HitTestPoint(pos);

            // Can only remove interior points (not endpoints)
            if (hit > 0 && hit < _controlPoints.Count - 1)
            {
                // End any in-flight LEFT drag first. Removing a point shifts
                // every later index down, so a drag on an index at or after
                // `hit` was left pointing one past the end and the next
                // MouseMove threw ArgumentOutOfRangeException on the UI
                // thread. Canceling is simpler than renumbering and matches
                // what the user means by deleting the thing under the cursor.
                if (_isDragging)
                {
                    _isDragging = false;
                    _dragIndex = -1;
                    ChartCanvas.ReleaseMouseCapture();
                    HideDragReadout();
                }
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

        // ── Keyboard editing ──

        /// <summary>What a key did: nothing the editor handles, a point
        /// selected (or a move the point could not make), or the points
        /// changed.</summary>
        internal enum KeyResult { Ignored, Selected, Changed }

        private const double KeyStep = 0.01;
        private const double PageStep = 0.10;
        // The gap a point keeps from its neighbors, as a drag keeps it.
        private const double MinGap = 0.01;

        /// <summary>Applies one key to the points and the index of the point
        /// the keys act on. Left and Right select the point before and after,
        /// Home and End the first and last. Up and Down move the point's
        /// output one percent, Page Up and Page Down ten. Shift+Left and
        /// Shift+Right move an interior point's input one percent, short of
        /// its neighbors as a drag stops. Insert adds a point on the curve
        /// halfway to the next point (from the one before, on the last
        /// point), Delete removes an interior point. The endpoints keep their
        /// inputs and stay, as they do under the mouse.</summary>
        internal static KeyResult ApplyKey(List<(double X, double Y)> points, ref int index,
            ModifierKeys modifiers, Key key, Func<double, double> curveAt)
        {
            if (points.Count < 2)
                return KeyResult.Ignored;
            int last = points.Count - 1;
            index = Math.Clamp(index, 0, last);
            bool interior = index > 0 && index < last;
            var (x, y) = points[index];

            if (modifiers == ModifierKeys.Shift && key is Key.Left or Key.Right)
            {
                if (!interior)
                    return KeyResult.Selected;
                double moved = Math.Round(x + (key == Key.Left ? -KeyStep : KeyStep), 2);
                moved = Math.Clamp(moved, points[index - 1].X + MinGap, points[index + 1].X - MinGap);
                return Move(points, index, moved, y);
            }
            if (modifiers != ModifierKeys.None)
                return KeyResult.Ignored;

            switch (key)
            {
                case Key.Left:
                    index = Math.Max(0, index - 1);
                    return KeyResult.Selected;
                case Key.Right:
                    index = Math.Min(last, index + 1);
                    return KeyResult.Selected;
                case Key.Home:
                    index = 0;
                    return KeyResult.Selected;
                case Key.End:
                    index = last;
                    return KeyResult.Selected;
                case Key.Up:
                case Key.Down:
                case Key.PageUp:
                case Key.PageDown:
                    double step = key switch
                    {
                        Key.Up => KeyStep,
                        Key.Down => -KeyStep,
                        Key.PageUp => PageStep,
                        _ => -PageStep,
                    };
                    return Move(points, index, x, Math.Clamp(Math.Round(y + step, 2), 0, 1));
                case Key.Insert:
                    int before = index == last ? last - 1 : index;
                    double mid = Math.Round((points[before].X + points[before + 1].X) / 2, 3);
                    if (mid - points[before].X < MinGap || points[before + 1].X - mid < MinGap)
                        return KeyResult.Selected;
                    points.Insert(before + 1, (mid, Math.Clamp(curveAt(mid), 0, 1)));
                    index = before + 1;
                    return KeyResult.Changed;
                case Key.Delete:
                    if (!interior)
                        return KeyResult.Selected;
                    points.RemoveAt(index);
                    index--;
                    return KeyResult.Changed;
            }
            return KeyResult.Ignored;
        }

        private static KeyResult Move(List<(double X, double Y)> points, int index, double x, double y)
        {
            if (points[index].X == x && points[index].Y == y)
                return KeyResult.Selected;
            points[index] = (x, y);
            return KeyResult.Changed;
        }

        /// <summary>The point the keys act on as a screen reader reads it:
        /// "Point 2 of 4, input 50%, output 38%". The numbers are curve
        /// percent, as the drag readout shows them.</summary>
        internal string PointText
        {
            get
            {
                if (_controlPoints.Count == 0)
                    return string.Empty;
                int i = Math.Clamp(_keyIndex, 0, _controlPoints.Count - 1);
                var (x, y) = _controlPoints[i];
                return string.Format(CultureInfo.CurrentCulture, Strings.Instance.CurveEditor_PointFormat,
                    i + 1, _controlPoints.Count, Math.Round(x * 100), Math.Round(y * 100));
            }
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            if (e.NewFocus == this && IsLoaded && !_isDragging)
            {
                DrawControlPoints();
                UpdateDragReadout();
            }
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            if (e.OldFocus == this && IsLoaded && !_isDragging)
            {
                HideDragReadout();
                DrawControlPoints();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || _isDragging || e.OriginalSource != this || !IsLoaded)
                return;
            string before = PointText;
            double[] lut = CurveLut.GetOrBuild(CurveString);
            KeyResult result = ApplyKey(_controlPoints, ref _keyIndex, HeldModifiers.Now, e.Key,
                x => lut != null ? CurveLut.Lookup(lut, x) : x);
            if (result == KeyResult.Ignored)
                return;
            e.Handled = true;
            if (result == KeyResult.Changed)
                CommitPoints();
            else
                DrawControlPoints();
            UpdateDragReadout();
            string after = PointText;
            if (after != before && UIElementAutomationPeer.FromElement(this) is CurveEditorPeer peer)
                peer.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, before, after);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new CurveEditorPeer(this);

        /// <summary>The editor's value for a screen reader: the point the
        /// keys act on, read again after each key that changes it.</summary>
        private sealed class CurveEditorPeer : UserControlAutomationPeer, IValueProvider
        {
            public CurveEditorPeer(CurveEditor owner) : base(owner) { }

            public override object GetPattern(PatternInterface patternInterface)
                => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

            /// <summary>A UserControl's kind reads as the word custom, which
            /// tells a listener nothing. The editor says what it is, in the
            /// language.</summary>
            protected override string GetLocalizedControlTypeCore() => Strings.Instance.Common_CurveEditor;

            string IValueProvider.Value => ((CurveEditor)Owner).PointText;

            // The value describes the point. The keys change it, and text
            // set through UI Automation does not.
            bool IValueProvider.IsReadOnly => true;

            void IValueProvider.SetValue(string value) => throw new InvalidOperationException();
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
