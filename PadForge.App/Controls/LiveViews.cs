using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>
    /// The gyro card's live view (the sections pass of #574): the controller's
    /// tilt drawn as a horizon in a round dial, the way a flight instrument
    /// shows it. The horizon turns against a roll and drops under a nose that
    /// rises, while the mark at the dial's middle stands for the controller.
    /// It reads the accelerometer the Calibration readout shows, in g after
    /// the grip, which is the reaction to gravity: gravity's own direction is
    /// its negative, the convention the Motion Lean and Gyro Tilt inputs use
    /// (SourceCoercion). A reading too weak to be gravity (under a fifth of a
    /// g, or none from a device with no accelerometer) draws a level horizon.
    ///
    /// <para>It is a picture of numbers a screen reader is given as text
    /// beside it, so it is no automation element. It draws left to right in
    /// every language.</para>
    /// </summary>
    public sealed class GyroHorizon : FrameworkElement
    {
        public static readonly DependencyProperty AccelXProperty = Register(nameof(AccelX));
        public static readonly DependencyProperty AccelYProperty = Register(nameof(AccelY));
        public static readonly DependencyProperty AccelZProperty = Register(nameof(AccelZ));

        public static readonly DependencyProperty FaceProperty = RegisterBrush(nameof(Face));
        public static readonly DependencyProperty GroundProperty = RegisterBrush(nameof(Ground));
        public static readonly DependencyProperty RimProperty = RegisterBrush(nameof(Rim));
        public static readonly DependencyProperty HorizonProperty = RegisterBrush(nameof(Horizon));
        public static readonly DependencyProperty LadderProperty = RegisterBrush(nameof(Ladder));
        public static readonly DependencyProperty MarkProperty = RegisterBrush(nameof(Mark));

        private static DependencyProperty Register(string name) => DependencyProperty.Register(
            name, typeof(double), typeof(GyroHorizon), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
            name, typeof(Brush), typeof(GyroHorizon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public GyroHorizon()
        {
            FlowDirection = FlowDirection.LeftToRight;
        }

        /// <summary>The accelerometer across the controller, in g.</summary>
        public double AccelX { get => (double)GetValue(AccelXProperty); set => SetValue(AccelXProperty, value); }

        /// <summary>The accelerometer out of the controller's face, in g.</summary>
        public double AccelY { get => (double)GetValue(AccelYProperty); set => SetValue(AccelYProperty, value); }

        /// <summary>The accelerometer toward the player, in g.</summary>
        public double AccelZ { get => (double)GetValue(AccelZProperty); set => SetValue(AccelZProperty, value); }

        /// <summary>The dial's sky.</summary>
        public Brush Face { get => (Brush)GetValue(FaceProperty); set => SetValue(FaceProperty, value); }

        /// <summary>The dial's ground, under the horizon.</summary>
        public Brush Ground { get => (Brush)GetValue(GroundProperty); set => SetValue(GroundProperty, value); }

        /// <summary>The dial's edge and its marks around the top.</summary>
        public Brush Rim { get => (Brush)GetValue(RimProperty); set => SetValue(RimProperty, value); }

        /// <summary>The horizon, a measured line.</summary>
        public Brush Horizon { get => (Brush)GetValue(HorizonProperty); set => SetValue(HorizonProperty, value); }

        /// <summary>The short lines above and under the horizon.</summary>
        public Brush Ladder { get => (Brush)GetValue(LadderProperty); set => SetValue(LadderProperty, value); }

        /// <summary>The mark that stands for the controller, live.</summary>
        public Brush Mark { get => (Brush)GetValue(MarkProperty); set => SetValue(MarkProperty, value); }

        /// <summary>The dial's size when nothing sets it.</summary>
        internal const double Size = 112;

        /// <summary>The pitch, in degrees, that moves the horizon from the
        /// middle to the dial's edge.</summary>
        internal const double PitchToEdge = 60;

        /// <summary>The roll and the pitch in degrees for an accelerometer
        /// reading in g, or none when the reading is too weak to be gravity.
        /// A roll to the right and a nose that rises are positive.</summary>
        internal static (double Roll, double Pitch)? Attitude(double ax, double ay, double az)
        {
            double len = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (!double.IsFinite(len) || len < 0.2) return null;
            // Gravity points down: the negative of the reaction force.
            double gx = -ax / len, gy = -ay / len, gz = -az / len;
            double roll = Math.Atan2(gx, -gy) * 180.0 / Math.PI;
            double pitch = Math.Asin(Math.Clamp(gz, -1.0, 1.0)) * 180.0 / Math.PI;
            return (roll, pitch);
        }

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        {
            double w = double.IsNaN(Width) ? Size : Width;
            double h = double.IsNaN(Height) ? Size : Height;
            return new System.Windows.Size(w, h);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double r = Math.Min(w, h) / 2 - 6;
            if (r <= 4) return;
            var c = new Point(w / 2, h / 2 + 2);
            var dial = new EllipseGeometry(c, r, r);
            dial.Freeze();

            var att = Attitude(AccelX, AccelY, AccelZ);
            double roll = att?.Roll ?? 0, pitch = att?.Pitch ?? 0;
            double drop = Math.Clamp(pitch / PitchToEdge, -1, 1) * r;

            dc.DrawGeometry(Face, null, dial);
            dc.PushClip(dial);
            // The horizon turns against the roll, about the dial's middle.
            dc.PushTransform(new RotateTransform(-roll, c.X, c.Y));
            double y = c.Y + drop;
            dc.DrawRectangle(Ground, null, new Rect(c.X - 2 * r, y, 4 * r, 3 * r));
            if (Ladder != null)
            {
                var ladder = new Pen(Ladder, 1.2);
                foreach (var (dy, half) in new[] { (-12.0, 12.0), (-24.0, 7.0), (12.0, 12.0), (24.0, 7.0) })
                    dc.DrawLine(ladder, new Point(c.X - half, y + dy), new Point(c.X + half, y + dy));
            }
            if (Horizon != null)
                dc.DrawLine(new Pen(Horizon, 1.6), new Point(c.X - 2 * r, y), new Point(c.X + 2 * r, y));
            dc.Pop();
            dc.Pop();

            if (Rim != null)
            {
                dc.DrawEllipse(null, new Pen(Rim, 1), c, r, r);
                var tick = new Pen(Rim, 1.4);
                foreach (double deg in new[] { -60.0, -30.0, 0.0, 30.0, 60.0 })
                {
                    double a = (deg - 90) * Math.PI / 180;
                    double outer = r + 5, inner = r - 1;
                    dc.DrawLine(tick, new Point(c.X + outer * Math.Cos(a), c.Y + outer * Math.Sin(a)),
                        new Point(c.X + inner * Math.Cos(a), c.Y + inner * Math.Sin(a)));
                }
            }
            if (Mark != null)
            {
                var mark = new Pen(Mark, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                var wings = new StreamGeometry();
                using (StreamGeometryContext g = wings.Open())
                {
                    g.BeginFigure(new Point(c.X - 22, c.Y), false, false);
                    g.LineTo(new Point(c.X - 9, c.Y), true, true);
                    g.LineTo(new Point(c.X, c.Y + 6), true, true);
                    g.LineTo(new Point(c.X + 9, c.Y), true, true);
                    g.LineTo(new Point(c.X + 22, c.Y), true, true);
                }
                wings.Freeze();
                dc.DrawGeometry(null, mark, wings);
                dc.DrawEllipse(Mark, null, c, 2.2, 2.2);
            }
        }
    }

    /// <summary>
    /// A rate as a bar that grows from its middle (the sections pass of #574):
    /// right of the mark for a positive rate and left of it for a negative
    /// one. The length follows the square root of the rate over
    /// <see cref="FullScale"/>, so drift of a few degrees a second shows and a
    /// fast turn still fits. It is drawn beside the number it pictures, so it
    /// is no automation element.
    /// </summary>
    public sealed class CenterBar : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(CenterBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FullScaleProperty = DependencyProperty.Register(
            nameof(FullScale), typeof(double), typeof(CenterBar),
            new FrameworkPropertyMetadata(720.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
            nameof(Track), typeof(Brush), typeof(CenterBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(CenterBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TickProperty = DependencyProperty.Register(
            nameof(Tick), typeof(Brush), typeof(CenterBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public CenterBar()
        {
            FlowDirection = FlowDirection.LeftToRight;
        }

        /// <summary>The rate, in the units of <see cref="FullScale"/>.</summary>
        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

        /// <summary>The rate that fills half the bar.</summary>
        public double FullScale { get => (double)GetValue(FullScaleProperty); set => SetValue(FullScaleProperty, value); }

        public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

        public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

        public Brush Tick { get => (Brush)GetValue(TickProperty); set => SetValue(TickProperty, value); }

        /// <summary>The bar's height.</summary>
        internal const double BarHeight = 6;

        /// <summary>The part of half the bar a rate fills, signed: the square
        /// root of the rate over the full scale, at most one.</summary>
        internal static double Reach(double value, double fullScale)
        {
            if (!double.IsFinite(value) || fullScale <= 0) return 0;
            return Math.Sign(value) * Math.Sqrt(Math.Min(1.0, Math.Abs(value) / fullScale));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width;
            return new Size(w, 12);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0) return;
            double top = (h - BarHeight) / 2, mid = w / 2;
            var track = new Rect(0, top, w, BarHeight);
            dc.DrawRoundedRectangle(Track, null, track, 3, 3);
            double reach = Reach(Value, FullScale) * mid;
            if (Math.Abs(reach) >= 0.5)
            {
                var fill = reach > 0 ? new Rect(mid, top, reach, BarHeight) : new Rect(mid + reach, top, -reach, BarHeight);
                dc.DrawRoundedRectangle(Fill, null, fill, 3, 3);
            }
            if (Tick != null)
                dc.DrawRectangle(Tick, null, new Rect(Math.Round(mid) - 0.5, 0, 1, h));
        }
    }

    /// <summary>
    /// The touchpad card's live view (the sections pass of #574): the pad
    /// with each finger on it, as the device reports it now, and a short trail
    /// behind a finger that moves. Positions run from 0 to 1 across and down
    /// the pad. A lifted finger leaves no dot and its trail is dropped. It is
    /// drawn beside the numbers it pictures, so it is no automation element.
    /// </summary>
    public sealed class TouchSurface : FrameworkElement
    {
        public static readonly DependencyProperty X0Property = RegisterPoint(nameof(X0));
        public static readonly DependencyProperty Y0Property = RegisterPoint(nameof(Y0));
        public static readonly DependencyProperty X1Property = RegisterPoint(nameof(X1));
        public static readonly DependencyProperty Y1Property = RegisterPoint(nameof(Y1));

        public static readonly DependencyProperty Down0Property = DependencyProperty.Register(
            nameof(Down0), typeof(bool), typeof(TouchSurface),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnPointChanged));

        public static readonly DependencyProperty Down1Property = DependencyProperty.Register(
            nameof(Down1), typeof(bool), typeof(TouchSurface),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnPointChanged));

        public static readonly DependencyProperty PadProperty = RegisterBrush(nameof(Pad));
        public static readonly DependencyProperty EdgeProperty = RegisterBrush(nameof(Edge));
        public static readonly DependencyProperty SplitProperty = RegisterBrush(nameof(Split));
        public static readonly DependencyProperty FingerProperty = RegisterBrush(nameof(Finger));

        private static DependencyProperty RegisterPoint(string name) => DependencyProperty.Register(
            name, typeof(double), typeof(TouchSurface),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnPointChanged));

        private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
            name, typeof(Brush), typeof(TouchSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public TouchSurface()
        {
            FlowDirection = FlowDirection.LeftToRight;
        }

        public double X0 { get => (double)GetValue(X0Property); set => SetValue(X0Property, value); }
        public double Y0 { get => (double)GetValue(Y0Property); set => SetValue(Y0Property, value); }
        public bool Down0 { get => (bool)GetValue(Down0Property); set => SetValue(Down0Property, value); }
        public double X1 { get => (double)GetValue(X1Property); set => SetValue(X1Property, value); }
        public double Y1 { get => (double)GetValue(Y1Property); set => SetValue(Y1Property, value); }
        public bool Down1 { get => (bool)GetValue(Down1Property); set => SetValue(Down1Property, value); }

        /// <summary>The pad's face.</summary>
        public Brush Pad { get => (Brush)GetValue(PadProperty); set => SetValue(PadProperty, value); }

        /// <summary>The pad's edge.</summary>
        public Brush Edge { get => (Brush)GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }

        /// <summary>The line down the pad's middle, where a press clicks left or right.</summary>
        public Brush Split { get => (Brush)GetValue(SplitProperty); set => SetValue(SplitProperty, value); }

        /// <summary>A finger on the pad, live.</summary>
        public Brush Finger { get => (Brush)GetValue(FingerProperty); set => SetValue(FingerProperty, value); }

        public static readonly DependencyProperty TaperedProperty = DependencyProperty.Register(
            nameof(Tapered), typeof(bool), typeof(TouchSurface),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Whether the pad is a PlayStation pad, whose sides close in
        /// toward its foot. Any other pad is a rounded rectangle.</summary>
        public bool Tapered { get => (bool)GetValue(TaperedProperty); set => SetValue(TaperedProperty, value); }

        /// <summary>The pad's outline in a view of the given size. The
        /// tapered outline is the Motion board's, drawn for a view of
        /// <see cref="ViewWidth"/> by <see cref="ViewHeight"/> and scaled to
        /// this one. The other is a rectangle with rounded corners. Both
        /// leave 6 at each edge for a finger's halo.</summary>
        internal static Geometry Outline(double w, double h, bool tapered)
        {
            if (!tapered) return new RectangleGeometry(new Rect(6, 6, w - 12, h - 12), 12, 12);
            double sx = w / ViewWidth, sy = h / ViewHeight;
            Point P(double x, double y) => new(x * sx, y * sy);
            var outline = new StreamGeometry();
            using (StreamGeometryContext g = outline.Open())
            {
                g.BeginFigure(P(12, 6), true, true);
                g.LineTo(P(184, 6), true, false);
                g.QuadraticBezierTo(P(190, 6), P(189, 12), true, false);
                g.LineTo(P(182, 92), true, false);
                g.QuadraticBezierTo(P(181, 102), P(171, 102), true, false);
                g.LineTo(P(25, 102), true, false);
                g.QuadraticBezierTo(P(15, 102), P(14, 92), true, false);
                g.LineTo(P(7, 12), true, false);
                g.QuadraticBezierTo(P(6, 6), P(12, 6), true, false);
            }
            outline.Freeze();
            return outline;
        }

        /// <summary>The view's size when nothing sets it: the pad's own
        /// proportions with room for a finger's halo at its edges.</summary>
        internal const double ViewWidth = 196, ViewHeight = 108;

        /// <summary>The most points a trail keeps.</summary>
        internal const int TrailLength = 8;

        private readonly List<Point> _trail0 = new(), _trail1 = new();

        /// <summary>Adds a finger's position to its trail, or drops the trail
        /// once the finger is lifted. A position the trail already ends on
        /// adds nothing.</summary>
        internal static void Follow(List<Point> trail, bool down, double x, double y)
        {
            if (!down)
            {
                trail.Clear();
                return;
            }
            var p = new Point(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
            if (trail.Count > 0 && trail[^1] == p) return;
            trail.Add(p);
            if (trail.Count > TrailLength) trail.RemoveAt(0);
        }

        private static void OnPointChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (TouchSurface)d;
            Follow(view._trail0, view.Down0, view.X0, view.Y0);
            Follow(view._trail1, view.Down1, view.X1, view.Y1);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsNaN(Width) ? ViewWidth : Width;
            double h = double.IsNaN(Height) ? ViewHeight : Height;
            return new Size(w, h);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 8 || h <= 8) return;
            var pad = new Rect(6, 6, w - 12, h - 12);
            dc.DrawGeometry(Pad, Edge != null ? new Pen(Edge, 1.2) : null, Outline(w, h, Tapered));
            if (Split != null)
            {
                var split = new Pen(Split, 1) { DashStyle = new DashStyle(new[] { 3.0, 4.0 }, 0) };
                dc.DrawLine(split, new Point(pad.Left + pad.Width / 2, pad.Top), new Point(pad.Left + pad.Width / 2, pad.Bottom));
            }
            if (Finger == null) return;
            Point At(Point p) => new(pad.Left + 10 + p.X * (pad.Width - 20), pad.Top + 10 + p.Y * (pad.Height - 20));
            foreach (var trail in new[] { _trail0, _trail1 })
            {
                if (trail.Count == 0) continue;
                for (int i = 1; i < trail.Count; i++)
                {
                    var pen = new Pen(Finger, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.PushOpacity(0.08 + 0.28 * i / trail.Count);
                    dc.DrawLine(pen, At(trail[i - 1]), At(trail[i]));
                    dc.Pop();
                }
                Point tip = At(trail[^1]);
                dc.PushOpacity(0.18);
                dc.DrawEllipse(Finger, null, tip, 13, 13);
                dc.Pop();
                dc.DrawEllipse(Finger, null, tip, 7.5, 7.5);
            }
        }
    }
}
