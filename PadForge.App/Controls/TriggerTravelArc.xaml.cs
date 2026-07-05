using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PadForge.Controls
{
    /// <summary>
    /// Quarter-arc trigger travel gauge (#175 competitor item 4).
    /// Dotted steel arc from rest (bottom-right of the arc) to full pull
    /// (top-left), steel tick stops at the range clamps, a thin cold
    /// needle at the raw pull position, and an ember sweep from rest to
    /// the post-curve output position. Value-driven only: every visual
    /// moves on a property change, never on a clock.
    /// </summary>
    public partial class TriggerTravelArc : UserControl
    {
        // ── Dependency Properties ──

        /// <summary>Raw pull, 0..1 (pre-pipeline).</summary>
        public static readonly DependencyProperty RawValueProperty =
            DependencyProperty.Register(nameof(RawValue), typeof(double), typeof(TriggerTravelArc),
                new PropertyMetadata(0.0, OnRawChanged));

        /// <summary>Post-curve output, 0..1.</summary>
        public static readonly DependencyProperty OutValueProperty =
            DependencyProperty.Register(nameof(OutValue), typeof(double), typeof(TriggerTravelArc),
                new PropertyMetadata(0.0, OnOutChanged));

        /// <summary>Range floor (deadzone) in percent, 0..100.</summary>
        public static readonly DependencyProperty RangeMinProperty =
            DependencyProperty.Register(nameof(RangeMin), typeof(double), typeof(TriggerTravelArc),
                new PropertyMetadata(0.0, OnRangeChanged));

        /// <summary>Range ceiling (max range) in percent, 0..100.</summary>
        public static readonly DependencyProperty RangeMaxProperty =
            DependencyProperty.Register(nameof(RangeMax), typeof(double), typeof(TriggerTravelArc),
                new PropertyMetadata(100.0, OnRangeChanged));

        /// <summary>Square gauge size in pixels.</summary>
        public static readonly DependencyProperty GaugeSizeProperty =
            DependencyProperty.Register(nameof(GaugeSize), typeof(double), typeof(TriggerTravelArc),
                new PropertyMetadata(90.0, OnLayoutChanged));

        public double RawValue { get => (double)GetValue(RawValueProperty); set => SetValue(RawValueProperty, value); }
        public double OutValue { get => (double)GetValue(OutValueProperty); set => SetValue(OutValueProperty, value); }
        public double RangeMin { get => (double)GetValue(RangeMinProperty); set => SetValue(RangeMinProperty, value); }
        public double RangeMax { get => (double)GetValue(RangeMaxProperty); set => SetValue(RangeMaxProperty, value); }
        public double GaugeSize { get => (double)GetValue(GaugeSizeProperty); set => SetValue(GaugeSizeProperty, value); }

        // ── Visual elements (code-drawn, CurveEditor idiom) ──

        private readonly Path _baseArc = new();
        private readonly Path _sweepArc = new();
        private readonly Line _tickMin = new();
        private readonly Line _tickMax = new();
        private readonly Line _needle = new();

        // Ember palette fallbacks (frozen). The live values resolve from the
        // themed resources in InitVisuals.
        private static readonly SolidColorBrush SteelFallbackBrush = CreateFrozen(0x25, 0x30, 0x49);
        private static readonly SolidColorBrush TickFallbackBrush = CreateFrozen(0x5D, 0x6B, 0x85);
        private static readonly SolidColorBrush ColdFallbackBrush = CreateFrozen(0x58, 0xB6, 0xE4);
        private static readonly SolidColorBrush EmberFallbackBrush = CreateFrozen(0xFF, 0x6B, 0x2C);

        private static SolidColorBrush CreateFrozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // Static ember glow on the sweep, set once on the element and never
        // animated (reduced-motion rule shared with CurveEditor's live dot).
        private static readonly System.Windows.Media.Effects.DropShadowEffect SweepGlow = CreateFrozenSweepGlow();

        private static System.Windows.Media.Effects.DropShadowEffect CreateFrozenSweepGlow()
        {
            var fx = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0x6B, 0x2C),
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.5
            };
            fx.Freeze();
            return fx;
        }

        // ── Geometry ──

        // Arc center sits at the bottom-left corner (inset by Pad); rest is
        // the point at angle 0 (bottom-right of the arc), full pull at 90°
        // (top-left). Padding leaves room for the tick/needle overhang.
        private const double Pad = 7;

        private double Radius => Math.Max(GaugeSize - 2 * Pad, 1);
        private Point Center => new(Pad, GaugeSize - Pad);

        private Point PointAt(double t, double r)
        {
            double a = Math.Clamp(t, 0.0, 1.0) * Math.PI / 2.0;
            var c = Center;
            return new Point(c.X + r * Math.Cos(a), c.Y - r * Math.Sin(a));
        }

        private PathGeometry BuildArc(double tFrom, double tTo, double r)
        {
            var figure = new PathFigure { StartPoint = PointAt(tFrom, r), IsClosed = false, IsFilled = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = PointAt(tTo, r),
                Size = new Size(r, r),
                RotationAngle = 0,
                IsLargeArc = false,
                SweepDirection = SweepDirection.Counterclockwise,
                IsStroked = true
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        public TriggerTravelArc()
        {
            InitializeComponent();
            Loaded += (_, _) => InitVisuals();
        }

        private void InitVisuals()
        {
            var canvas = ArcCanvas;
            canvas.Children.Clear();

            var steelBrush = TryFindResource("SteelLineBrush") as Brush
                ?? Application.Current.TryFindResource("SteelLineBrush") as Brush
                ?? (Brush)SteelFallbackBrush;
            var tickBrush = TryFindResource("TextFillColorTertiaryBrush") as Brush
                ?? Application.Current.TryFindResource("TextFillColorTertiaryBrush") as Brush
                ?? (Brush)TickFallbackBrush;
            var coldBrush = TryFindResource("ColdBrush") as Brush
                ?? Application.Current.TryFindResource("ColdBrush") as Brush
                ?? (Brush)ColdFallbackBrush;
            var emberBrush = TryFindResource("EmberBrush") as Brush
                ?? Application.Current.TryFindResource("EmberBrush") as Brush
                ?? (Brush)EmberFallbackBrush;

            // Dotted steel base arc, rest → full pull (0-length dashes with
            // round caps render as dots).
            _baseArc.Stroke = steelBrush;
            _baseArc.StrokeThickness = 1.6;
            _baseArc.StrokeDashArray = new DoubleCollection { 0, 3 };
            _baseArc.StrokeDashCap = PenLineCap.Round;
            canvas.Children.Add(_baseArc);

            // Steel tick stops at the range clamps.
            foreach (var tick in new[] { _tickMin, _tickMax })
            {
                tick.Stroke = tickBrush;
                tick.StrokeThickness = 1.5;
                canvas.Children.Add(tick);
            }

            // Ember sweep from rest to the post-curve output position.
            _sweepArc.Stroke = emberBrush;
            _sweepArc.StrokeThickness = 4;
            _sweepArc.StrokeStartLineCap = PenLineCap.Round;
            _sweepArc.StrokeEndLineCap = PenLineCap.Round;
            _sweepArc.Effect = SweepGlow;
            canvas.Children.Add(_sweepArc);

            // Thin cold needle at the raw pull position, on top.
            _needle.Stroke = coldBrush;
            _needle.StrokeThickness = 1.5;
            _needle.StrokeEndLineCap = PenLineCap.Round;
            canvas.Children.Add(_needle);

            DrawStatic();
            UpdateTicks();
            UpdateSweep();
            UpdateNeedle();
        }

        private void DrawStatic()
        {
            _baseArc.Data = BuildArc(0.0, 1.0, Radius);
        }

        private void UpdateTicks()
        {
            PositionTick(_tickMin, Math.Clamp(RangeMin, 0.0, 100.0) / 100.0);
            PositionTick(_tickMax, Math.Clamp(RangeMax, 0.0, 100.0) / 100.0);
        }

        private void PositionTick(Line tick, double t)
        {
            var inner = PointAt(t, Radius - 5);
            var outer = PointAt(t, Radius + 5);
            tick.X1 = inner.X; tick.Y1 = inner.Y;
            tick.X2 = outer.X; tick.Y2 = outer.Y;
        }

        private void UpdateSweep()
        {
            double t = Math.Clamp(OutValue, 0.0, 1.0);
            if (t <= 0.001)
            {
                _sweepArc.Visibility = Visibility.Collapsed;
                return;
            }
            _sweepArc.Visibility = Visibility.Visible;
            _sweepArc.Data = BuildArc(0.0, t, Radius);
        }

        private void UpdateNeedle()
        {
            double t = Math.Clamp(RawValue, 0.0, 1.0);
            var hub = PointAt(t, 6);
            var tip = PointAt(t, Radius + 3);
            _needle.X1 = hub.X; _needle.Y1 = hub.Y;
            _needle.X2 = tip.X; _needle.Y2 = tip.Y;
        }

        // ── Property change handlers ──

        private static void OnRawChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TriggerTravelArc arc && arc.IsLoaded)
                arc.UpdateNeedle();
        }

        private static void OnOutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TriggerTravelArc arc && arc.IsLoaded)
                arc.UpdateSweep();
        }

        private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TriggerTravelArc arc && arc.IsLoaded)
                arc.UpdateTicks();
        }

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TriggerTravelArc arc && arc.IsLoaded)
            {
                arc.DrawStatic();
                arc.UpdateTicks();
                arc.UpdateSweep();
                arc.UpdateNeedle();
            }
        }
    }
}
