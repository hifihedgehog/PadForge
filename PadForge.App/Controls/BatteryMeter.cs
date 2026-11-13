using System;
using System.Windows;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>A battery drawn as a meter (#574, the world-class pass): an
    /// outline with a cap at its end and a bar inside that is as long as
    /// the charge. The page's styles color it by state: the text color at
    /// rest, gold while low, cold blue with a bolt while charging. A level
    /// read faster than its number, in the one shape every page uses.
    ///
    /// <para>It draws itself, so it has the same crisp edge at every size
    /// and needs no icon font: a run of text in an icon font drew two boxes
    /// while it was empty (seen 2026-11-13). It is 26 by 12 unless a size
    /// is set, and it keeps those proportions. With no battery to show
    /// (<see cref="Percent"/> under zero) it draws nothing and takes no
    /// room.</para>
    ///
    /// <para>It is a picture beside the percentage, which carries the
    /// number, so it is no automation element.</para></summary>
    public sealed class BatteryMeter : FrameworkElement
    {
        /// <summary>The meter's own width and height, in its own units.</summary>
        internal const double UnitWidth = 26, UnitHeight = 12;

        /// <summary>The bar's room inside the outline, and the least of it a
        /// battery with any charge at all shows.</summary>
        internal const double BarRoom = 17.5, BarLeast = 2.5;

        public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
            nameof(Percent), typeof(int), typeof(BatteryMeter),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(
            nameof(Charging), typeof(bool), typeof(BatteryMeter),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LowProperty = DependencyProperty.Register(
            nameof(Low), typeof(bool), typeof(BatteryMeter),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(BatteryMeter),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(BatteryMeter),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GroundProperty = DependencyProperty.Register(
            nameof(Ground), typeof(Brush), typeof(BatteryMeter),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The charge from 0 to 100, or under zero for no battery.</summary>
        public int Percent
        {
            get => (int)GetValue(PercentProperty);
            set => SetValue(PercentProperty, value);
        }

        /// <summary>Whether the battery is charging: a bolt lies over the bar.</summary>
        public bool Charging
        {
            get => (bool)GetValue(ChargingProperty);
            set => SetValue(ChargingProperty, value);
        }

        /// <summary>Whether the charge is at or under the level the user
        /// asked to be told of. The styles turn the meter gold.</summary>
        public bool Low
        {
            get => (bool)GetValue(LowProperty);
            set => SetValue(LowProperty, value);
        }

        /// <summary>The outline's and the cap's color.</summary>
        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        /// <summary>The bar's color.</summary>
        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        /// <summary>The color of what the meter stands on, which the bolt is
        /// cut in.</summary>
        public Brush Ground
        {
            get => (Brush)GetValue(GroundProperty);
            set => SetValue(GroundProperty, value);
        }

        /// <summary>The bar's length, in the meter's own units, for a charge:
        /// in proportion to it, and never so short that a low battery shows
        /// no bar at all. No bar for an empty or unknown battery.</summary>
        internal static double BarLength(int percent)
            => percent <= 0 ? 0 : Math.Max(BarLeast, BarRoom * Math.Min(percent, 100) / 100.0);

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Percent < 0) return default;
            double height = double.IsNaN(Height) ? UnitHeight : Height;
            return new Size(height * UnitWidth / UnitHeight, height);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Percent < 0 || ActualHeight <= 0) return;
            double u = ActualHeight / UnitHeight;
            Brush stroke = Stroke, fill = Fill ?? Stroke;
            if (stroke == null) return;

            var pen = new Pen(stroke, 1.5 * u);
            pen.Freeze();
            dc.DrawRoundedRectangle(null, pen, new Rect(0.75 * u, 0.75 * u, 21.5 * u, 10.5 * u), 2.75 * u, 2.75 * u);
            dc.DrawRoundedRectangle(stroke, null, new Rect(23.5 * u, 3.5 * u, 2 * u, 5 * u), u, u);
            double bar = BarLength(Percent);
            if (bar > 0)
                dc.DrawRoundedRectangle(fill, null, new Rect(2.75 * u, 2.75 * u, bar * u, 6.5 * u), 1.25 * u, 1.25 * u);

            if (!Charging) return;
            // The bolt, cut in the ground's color over the bar's middle.
            var bolt = new StreamGeometry();
            using (var g = bolt.Open())
            {
                g.BeginFigure(new Point(12.6 * u, 1.6 * u), true, true);
                g.LineTo(new Point(9.2 * u, 6.4 * u), true, true);
                g.LineTo(new Point(11.8 * u, 6.4 * u), true, true);
                g.LineTo(new Point(10.4 * u, 10.4 * u), true, true);
                g.LineTo(new Point(14.0 * u, 5.4 * u), true, true);
                g.LineTo(new Point(11.4 * u, 5.4 * u), true, true);
            }
            bolt.Freeze();
            Brush ground = Ground ?? Brushes.Black;
            var edge = new Pen(ground, 0.6 * u) { LineJoin = PenLineJoin.Round };
            edge.Freeze();
            dc.DrawGeometry(ground, edge, bolt);
        }
    }
}
