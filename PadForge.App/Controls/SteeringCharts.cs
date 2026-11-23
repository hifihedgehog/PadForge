using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using PadForge.ViewModels;

namespace PadForge.Controls
{
    /// <summary>What the two steering charts share: the plot's frame, its
    /// grid and its labels, in the theme's own colors.</summary>
    public abstract class SteeringChartBase : FrameworkElement
    {
        /// <summary>The label size, the 12 of the smallest text.</summary>
        protected const double LabelSize = 12;

        /// <summary>Room under the plot for the numbers along its foot.</summary>
        protected const double FootRoom = 18;

        /// <summary>Room left of the plot for the numbers up its side.</summary>
        protected virtual double SideRoom => 30;

        // The colors a bare host has, a test's, where the theme gives none.
        private static readonly Brush FieldFallback = Frozen(new SolidColorBrush(Color.FromRgb(0x0D, 0x13, 0x1D)));
        private static readonly Brush LineFallback = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x27, 0x34)));
        private static readonly Brush GridFallback = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x1E, 0x2B)));
        private static readonly Brush AxisFallback = Frozen(new SolidColorBrush(Color.FromRgb(0x26, 0x31, 0x42)));
        private static readonly Brush LabelFallback = Frozen(new SolidColorBrush(Color.FromRgb(0x86, 0x93, 0xA6)));
        private static readonly Brush EmberFallback = Frozen(new SolidColorBrush(PadForge.Common.EmberTheme.Accent));
        private static readonly Brush ColdFallback = Frozen(new SolidColorBrush(PadForge.Common.Themes.ThemeEngine.StockSecondary(true)));

        protected static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        protected SteeringChartBase()
        {
            // Left stays left when the interface reads right to left: an
            // angle's sign and a time's order are not the reading order's.
            FlowDirection = FlowDirection.LeftToRight;
            SnapsToDevicePixels = true;
            // The labels take the body face by resource reference, so a chart
            // redraws in the face of the interface language (#590).
            PadForge.Common.TextFaces.Follow(this, PadForge.Common.TextFaces.BodyKey);
            // The colors are read while it draws, which records no resource
            // reference, so a look put on while it shows draws it again (#591).
            PadForge.Common.Themes.ThemeTokens.Follow(this, InvalidateVisual);
        }

        // The plot's fill and its edge, the grid a step fainter than the
        // edge, the two axes a step stronger, and the labels. The line that
        // is no live reading (the car at 25 mph, the release before the
        // test) takes the labels' color.
        protected Brush Field => TryFindResource("SteelFieldBrush") as Brush ?? FieldFallback;
        protected Brush Line => TryFindResource("SteelLineSoftBrush") as Brush ?? LineFallback;
        protected Brush GridLine => TryFindResource("SteelWellLineBrush") as Brush ?? GridFallback;
        protected Brush Axis => TryFindResource("SteelPillLineBrush") as Brush ?? AxisFallback;
        protected Brush Label => TryFindResource("TextFillColorTertiaryBrush") as Brush ?? LabelFallback;
        protected Brush Ember => TryFindResource("EmberBrush") as Brush ?? EmberFallback;
        protected Brush Cold => TryFindResource("ColdBrush") as Brush ?? ColdFallback;

        protected FormattedText Text(string text, Brush brush)
        {
            return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                PadForge.Common.TextFaces.TypefaceOf(this, FontWeights.Normal), LabelSize, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        /// <summary>The plot's rectangle inside the control.</summary>
        protected Rect Plot => new(SideRoom, 0, Math.Max(1, ActualWidth - SideRoom - 1), Math.Max(1, ActualHeight - FootRoom));

        /// <summary>A whole-pixel line's place, so a one pixel line is one
        /// pixel.</summary>
        protected static double Crisp(double value) => Math.Round(value) + 0.5;

        /// <summary>A step for a scale that reaches <paramref name="most"/>:
        /// one, two or five times a power of ten, so the scale has two to
        /// four steps.</summary>
        internal static double NiceStep(double most)
        {
            if (!(most > 0)) return 1;
            double power = Math.Pow(10, Math.Floor(Math.Log10(most / 2.0)));
            foreach (double times in new[] { 1.0, 2.0, 5.0, 10.0 })
                if (most / (times * power) <= 4.0) return times * power;
            return 10 * power;
        }

        /// <summary>A number for a label, with a real minus sign.</summary>
        protected static string Number(double value, string format)
            => (value < 0 ? "−" : string.Empty) + Math.Abs(value).ToString(format, CultureInfo.CurrentCulture);

        protected override AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);

        /// <summary>A screen reader hears a picture with the name the page
        /// gave it.</summary>
        private sealed class ChartPeer : FrameworkElementAutomationPeer
        {
            public ChartPeer(FrameworkElement owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

            protected override string GetClassNameCore() => Owner.GetType().Name;

            protected override bool IsContentElementCore() => true;

            protected override bool IsControlElementCore() => true;
        }
    }

    /// <summary>
    /// The Centering card's chart (#588): steering torque at the rim against
    /// steering angle. The car at the speed in use is a band, the torque
    /// with friction added above it and taken off below it, so the band's
    /// height at center is the friction felt there. A second line is the
    /// same car at 25 mph, dashes mark the most the wheel gives, and a mark
    /// rides the curve where the wheel stands.
    /// </summary>
    public sealed class SteeringTorqueChart : SteeringChartBase
    {
        public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(
            nameof(Preview), typeof(CenteringPreview), typeof(SteeringTorqueChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LiveShownProperty = DependencyProperty.Register(
            nameof(LiveShown), typeof(bool), typeof(SteeringTorqueChart),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LiveAngleProperty = DependencyProperty.Register(
            nameof(LiveAngle), typeof(double), typeof(SteeringTorqueChart),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LiveTorqueProperty = DependencyProperty.Register(
            nameof(LiveTorque), typeof(double), typeof(SteeringTorqueChart),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LimitLabelFormatProperty = DependencyProperty.Register(
            nameof(LimitLabelFormat), typeof(string), typeof(SteeringTorqueChart),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The curves to draw, or null for an empty plot.</summary>
        public CenteringPreview Preview
        {
            get => (CenteringPreview)GetValue(PreviewProperty);
            set => SetValue(PreviewProperty, value);
        }

        /// <summary>Whether the live mark shows, and where: degrees, and
        /// N·m with the angle's sign.</summary>
        public bool LiveShown { get => (bool)GetValue(LiveShownProperty); set => SetValue(LiveShownProperty, value); }
        public double LiveAngle { get => (double)GetValue(LiveAngleProperty); set => SetValue(LiveAngleProperty, value); }
        public double LiveTorque { get => (double)GetValue(LiveTorqueProperty); set => SetValue(LiveTorqueProperty, value); }

        /// <summary>The wheel limit's label, with a place for the figure.</summary>
        public string LimitLabelFormat
        {
            get => (string)GetValue(LimitLabelFormatProperty);
            set => SetValue(LimitLabelFormatProperty, value);
        }

        private const int Samples = 180;

        public SteeringTorqueChart()
        {
            MinHeight = 132;
        }

        /// <summary>How far up and down the plot reaches, in N·m: a tenth
        /// past the band's top, or past the wheel's limit where that is
        /// near enough to draw. A limit more than twice the band's top is
        /// off the plot, since drawing to it would flatten the curve.</summary>
        internal static double Reach(CenteringPreview preview, out bool limitShown)
        {
            double top = Math.Min(preview.PeakTorque + preview.Friction, preview.WheelTorque);
            limitShown = preview.WheelTorque <= 2.0 * top;
            double reach = (limitShown ? Math.Max(top, preview.WheelTorque) : top) * 1.1;
            return reach > 0.01 ? reach : 1.0;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var plot = Plot;
            if (ActualWidth <= SideRoom + 2 || ActualHeight <= FootRoom + 2) return;
            var label = Label;
            dc.DrawRectangle(Field, new Pen(Line, 1), new Rect(Crisp(plot.Left) - 0.5, 0.5, plot.Width, plot.Height - 1));

            var preview = Preview;
            double span = preview?.SpanDegrees ?? CenteringPreview.WidestSpanDegrees;
            bool limitShown = false;
            double reach = preview != null ? Reach(preview, out limitShown) : 1.0;

            double X(double degrees) => plot.Left + (degrees + span) / (2 * span) * plot.Width;
            double Y(double torque) => plot.Top + (0.5 - torque / (2 * reach)) * plot.Height;

            // The grid: a third and two thirds of the span either side, and the torque's steps.
            var gridPen = new Pen(GridLine, 1);
            var axisPen = new Pen(Axis, 1);
            double angleStep = AngleStep(span);
            for (double degrees = angleStep; degrees < span - 0.01; degrees += angleStep)
            {
                foreach (double side in new[] { -1.0, 1.0 })
                {
                    double x = Crisp(X(side * degrees));
                    dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                }
            }
            double torqueStep = NiceStep(reach);
            string torqueFormat = torqueStep < 1 ? (torqueStep < 0.1 ? "F2" : "F1") : "F0";
            for (double torque = torqueStep; torque < reach; torque += torqueStep)
            {
                foreach (double side in new[] { -1.0, 1.0 })
                {
                    double y = Crisp(Y(side * torque));
                    dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                }
            }
            dc.DrawLine(axisPen, new Point(Crisp(X(0)), plot.Top), new Point(Crisp(X(0)), plot.Bottom));
            dc.DrawLine(axisPen, new Point(plot.Left, Crisp(Y(0))), new Point(plot.Right, Crisp(Y(0))));

            // The numbers up the side and along the foot.
            for (double torque = -Math.Floor(reach / torqueStep) * torqueStep; torque <= reach; torque += torqueStep)
            {
                if (Math.Abs(torque) < torqueStep / 2) torque = 0;
                var text = Text(Number(torque, torqueFormat), label);
                dc.DrawText(text, new Point(SideRoom - 8 - text.Width, Y(torque) - text.Height / 2));
            }
            for (double degrees = -Math.Floor(span / angleStep) * angleStep; degrees <= span + 0.01; degrees += angleStep)
            {
                if (Math.Abs(degrees) < angleStep / 2) degrees = 0;
                var text = Text(Number(degrees, "F0") + "°", label);
                double x = Math.Clamp(X(degrees) - text.Width / 2, plot.Left, plot.Right - text.Width);
                dc.DrawText(text, new Point(x, plot.Bottom + 2));
            }

            if (preview == null) return;

            dc.PushClip(new RectangleGeometry(plot));
            try
            {
                if (limitShown)
                {
                    var limitPen = new Pen(label, 1) { DashStyle = new DashStyle(new[] { 5.0, 4.0 }, 0) };
                    foreach (double side in new[] { -1.0, 1.0 })
                    {
                        double y = Crisp(Y(side * preview.WheelTorque));
                        dc.DrawLine(limitPen, new Point(plot.Left, y), new Point(plot.Right, y));
                    }
                }

                // The same car at 25 mph, a line.
                var reference = new StreamGeometry();
                using (var g = reference.Open())
                {
                    for (int i = 0; i <= Samples * 2; i++)
                    {
                        double degrees = -span + span * i / Samples;
                        var point = new Point(X(degrees), Y(Held(preview.Reference.Torque(degrees * Math.PI / 180.0), preview.WheelTorque)));
                        if (i == 0) g.BeginFigure(point, false, false);
                        else g.LineTo(point, true, true);
                    }
                }
                reference.Freeze();
                dc.DrawGeometry(null, new Pen(label, 1.25) { LineJoin = PenLineJoin.Round }, reference);

                // The car at the speed in use: the torque with friction added, across, and with it taken off,
                // back. Neither edge passes what the wheel gives, friction and all.
                var band = new StreamGeometry();
                using (var g = band.Open())
                {
                    for (int i = 0; i <= Samples * 2; i++)
                    {
                        double degrees = -span + span * i / Samples;
                        double torque = preview.Curve.Torque(degrees * Math.PI / 180.0);
                        var point = new Point(X(degrees), Y(Held(torque + preview.Friction, preview.WheelTorque)));
                        if (i == 0) g.BeginFigure(point, true, true);
                        else g.LineTo(point, true, true);
                    }
                    for (int i = Samples * 2; i >= 0; i--)
                    {
                        double degrees = -span + span * i / Samples;
                        double torque = preview.Curve.Torque(degrees * Math.PI / 180.0);
                        g.LineTo(new Point(X(degrees), Y(Held(torque - preview.Friction, preview.WheelTorque))), true, true);
                    }
                }
                band.Freeze();
                var ember = Ember;
                Brush fill = Frozen(new SolidColorBrush(ColorOf(ember)) { Opacity = 0.2 });
                dc.DrawGeometry(fill, new Pen(ember, 1.5) { LineJoin = PenLineJoin.Round }, band);

                if (LiveShown)
                {
                    var at = new Point(X(Math.Clamp(LiveAngle, -span, span)), Y(Math.Clamp(LiveTorque, -reach, reach)));
                    dc.DrawEllipse(Frozen(new SolidColorBrush(ColorOf(ember)) { Opacity = 0.28 }), null, at, 8, 8);
                    dc.DrawEllipse(ember, null, at, 5, 5);
                }
            }
            finally { dc.Pop(); }

            if (limitShown && !string.IsNullOrEmpty(LimitLabelFormat))
            {
                var text = Text(string.Format(CultureInfo.CurrentCulture, LimitLabelFormat,
                    preview.WheelTorque.ToString("F1", CultureInfo.CurrentCulture)), label);
                // Under the upper dashes, clear of the curve, which rises on the right.
                dc.DrawText(text, new Point(plot.Left + 8, Y(preview.WheelTorque) + 2));
            }
        }

        /// <summary>A torque held to what the wheel gives, which is what
        /// the wheel will do.</summary>
        private static double Held(double torque, double limit) => Math.Clamp(torque, -limit, limit);

        /// <summary>The step between the angle's labels: a third of the
        /// span, as a round number of degrees.</summary>
        internal static double AngleStep(double span)
        {
            double third = span / 3.0;
            foreach (double step in new[] { 30.0, 20.0, 15.0, 10.0, 5.0, 2.0, 1.0 })
                if (step <= third + 0.01) return step;
            return 1.0;
        }

        private static Color ColorOf(Brush brush) => brush is SolidColorBrush solid ? solid.Color : PadForge.Common.EmberTheme.Accent;
    }

    /// <summary>
    /// The Release Test window's chart (#588): the wheel's angle over the
    /// two and a half seconds after each release, as it centered before the
    /// test and as it centers with the measurement. The test lets the wheel
    /// go from the side it stands on, which may be either, so a release from
    /// the far side is drawn turned over: the two lines start together and a
    /// swing past center is under the middle line for both.
    /// </summary>
    public sealed class ReleaseTraceChart : SteeringChartBase
    {
        public static readonly DependencyProperty BeforeProperty = DependencyProperty.Register(
            nameof(Before), typeof(IReadOnlyList<double>), typeof(ReleaseTraceChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AfterProperty = DependencyProperty.Register(
            nameof(After), typeof(IReadOnlyList<double>), typeof(ReleaseTraceChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ReleaseAngleProperty = DependencyProperty.Register(
            nameof(ReleaseAngle), typeof(double), typeof(ReleaseTraceChart),
            new FrameworkPropertyMetadata(90.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SecondsProperty = DependencyProperty.Register(
            nameof(Seconds), typeof(double), typeof(ReleaseTraceChart),
            new FrameworkPropertyMetadata(2.5, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The two releases, one angle a millisecond in degrees.</summary>
        public IReadOnlyList<double> Before
        {
            get => (IReadOnlyList<double>)GetValue(BeforeProperty);
            set => SetValue(BeforeProperty, value);
        }

        public IReadOnlyList<double> After
        {
            get => (IReadOnlyList<double>)GetValue(AfterProperty);
            set => SetValue(AfterProperty, value);
        }

        /// <summary>The angle the releases start from, in degrees, which
        /// sets how far up and down the plot reaches.</summary>
        public double ReleaseAngle { get => (double)GetValue(ReleaseAngleProperty); set => SetValue(ReleaseAngleProperty, value); }

        /// <summary>The time the plot spans.</summary>
        public double Seconds { get => (double)GetValue(SecondsProperty); set => SetValue(SecondsProperty, value); }

        public ReleaseTraceChart()
        {
            MinHeight = 172;
        }

        // The release angle with its minus sign and its degree mark is wider
        // than a torque.
        protected override double SideRoom => 40;

        protected override void OnRender(DrawingContext dc)
        {
            var plot = Plot;
            if (ActualWidth <= SideRoom + 2 || ActualHeight <= FootRoom + 2) return;
            var label = Label;
            double angle = Math.Max(1.0, Math.Abs(ReleaseAngle));
            double reach = angle * 1.1;
            double seconds = Math.Max(0.1, Seconds);

            double X(double time) => plot.Left + time / seconds * plot.Width;
            double Y(double degrees) => plot.Top + (0.5 - degrees / (2 * reach)) * plot.Height;

            var gridPen = new Pen(GridLine, 1);
            var axisPen = new Pen(Axis, 1);
            for (double time = 0.5; time < seconds - 0.01; time += 0.5)
                dc.DrawLine(gridPen, new Point(Crisp(X(time)), plot.Top), new Point(Crisp(X(time)), plot.Bottom));
            foreach (double side in new[] { -1.0, 1.0 })
                dc.DrawLine(gridPen, new Point(plot.Left, Crisp(Y(side * angle))), new Point(plot.Right, Crisp(Y(side * angle))));
            dc.DrawLine(axisPen, new Point(Crisp(plot.Left), plot.Top), new Point(Crisp(plot.Left), plot.Bottom));
            dc.DrawLine(axisPen, new Point(plot.Left, Crisp(Y(0))), new Point(plot.Right, Crisp(Y(0))));

            foreach (double degrees in new[] { angle, 0.0, -angle })
            {
                var text = Text(Number(degrees, "F0") + "°", label);
                dc.DrawText(text, new Point(SideRoom - 6 - text.Width, Y(degrees) - text.Height / 2));
            }
            for (double time = 0; time <= seconds + 0.01; time += 0.5)
            {
                bool last = time + 0.5 > seconds + 0.01;
                var text = Text(time.ToString(time == 0 ? "F0" : "F1", CultureInfo.CurrentCulture) + (last ? " s" : string.Empty), label);
                double x = Math.Clamp(X(time) - text.Width / 2, plot.Left, plot.Right - text.Width);
                dc.DrawText(text, new Point(x, plot.Bottom + 2));
            }

            dc.PushClip(new RectangleGeometry(plot));
            try
            {
                Trace(dc, Before, new Pen(label, 1.5) { LineJoin = PenLineJoin.Round }, X, Y);
                Trace(dc, After, new Pen(Cold, 2) { LineJoin = PenLineJoin.Round }, X, Y);
            }
            finally { dc.Pop(); }
        }

        /// <summary>One release as a line, a point every ten milliseconds,
        /// from the upper side whichever side the wheel was let go on.</summary>
        private static void Trace(DrawingContext dc, IReadOnlyList<double> trace, Pen pen, Func<double, double> x, Func<double, double> y)
        {
            if (trace == null || trace.Count < 2) return;
            double side = Side(trace);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(new Point(x(0), y(side * trace[0])), false, false);
                for (int ms = 10; ms < trace.Count; ms += 10)
                    g.LineTo(new Point(x(ms / 1000.0), y(side * trace[ms])), true, true);
                g.LineTo(new Point(x((trace.Count - 1) / 1000.0), y(side * trace[trace.Count - 1])), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, pen, line);
        }

        /// <summary>What turns a release over so it starts above the
        /// middle line: -1 for one let go on the negative side.</summary>
        internal static double Side(IReadOnlyList<double> trace)
            => trace != null && trace.Count > 0 && trace[0] < 0 ? -1.0 : 1.0;
    }
}
