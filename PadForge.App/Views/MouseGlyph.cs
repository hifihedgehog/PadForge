using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PadForge.Views
{
    /// <summary>THE mouse drawing. One copy, on purpose.
    ///
    /// <para>This lived twice, hand-copied between the Devices detail pane
    /// (<see cref="MousePreviewControl"/>) and the virtual keyboard-and-mouse
    /// preview (<c>KBMPreviewView</c>), down to the same constants. Reshaping
    /// one left the other on the old body, which is exactly the failure a
    /// duplicated drawing invites. Both now build from here, so the shape
    /// cannot drift again.</para>
    ///
    /// <para>Shape is a gaming mouse read top-down: clipped nose, hard
    /// shoulders, a pinched waist where the thumb sits, and a flared rear
    /// haunch. Buttons are CLIPPED TO THE BODY, so every outer edge is the
    /// shell's own curve rather than an approximation that can drift from
    /// it.</para>
    ///
    /// <para>Callers own behaviour: this returns the live shapes and attaches
    /// no handlers, no tooltips and no hit-testing policy, because the two
    /// surfaces differ there (the KBM preview is clickable for recording, the
    /// Devices pane is read-only).</para></summary>
    internal static class MouseGlyph
    {
        internal const double CenterX = 80;
        internal const double CanvasW = 160;
        internal const double BodyH = 188;
        internal const double MoveSize = 54;
        internal const double MoveTop = 99;
        internal const double WheelW = 15;
        internal const double WheelTop = 14;
        internal const double WheelH = 34;
        internal const double WheelBottom = WheelTop + WheelH;

        private const string BodyPath =
            "M 80,0 C 96,0 122,14 128,44 C 132,70 124,86 123,104" +
            " C 122,130 132,150 126,168 C 120,182 100,188 80,188" +
            " C 60,188 40,182 34,168 C 28,150 38,130 37,104" +
            " C 36,86 28,70 32,44 C 38,14 64,0 80,0 Z";

        // Button/palm seam: a shallow curve, not a straight cut.
        private const string SeamPath = "M 0,0 L 160,0 L 160,62 C 120,76 40,76 0,62 Z";

        // Flank keys sit ON the pinched waist and rake forward, the way a
        // thumb cluster actually does.
        private const string X1Path = "M 26,81 L 40,78 L 40,98 L 26,95 Z";
        private const string X2Path = "M 26,104 L 40,101 L 40,119 L 26,116 Z";

        internal sealed class Parts
        {
            public Path Lmb, Rmb, X1, X2, Dpi;
            public Rectangle Wheel;
            public Polygon ScrollUp, ScrollDown;
            public Ellipse MoveCircle, MoveDot;
            public double MoveX;
        }

        /// <summary>Populates <paramref name="canvas"/> and hands back the
        /// parts a render loop drives.</summary>
        internal static Parts Build(Canvas canvas, bool dark, Brush dim, Brush button,
                                    Brush mmb, Brush wheel, Brush dot)
        {
            var p = new Parts();
            double swL = CenterX - WheelW / 2, swR = CenterX + WheelW / 2;
            double gapL = swL - 2, gapR = swR + 2;

            var body = Geometry.Parse(BodyPath);
            body.Freeze();
            var seam = Geometry.Parse(SeamPath);
            seam.Freeze();

            // Shallow vertical gradient so the shell reads as curved rather
            // than as a flat cut-out. Built per theme rebuild, never per frame.
            var shell = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            };
            byte hi = dark ? (byte)0x5C : (byte)0xCD;
            byte lo = dark ? (byte)0x42 : (byte)0xB0;
            shell.GradientStops.Add(new GradientStop(Color.FromRgb(hi, hi, hi), 0));
            shell.GradientStops.Add(new GradientStop(Color.FromRgb(lo, lo, lo), 1));
            shell.Freeze();
            canvas.Children.Add(new Path { Data = body, Fill = shell, IsHitTestVisible = false });

            // Wheel channel recess.
            canvas.Children.Add(new Path
            {
                Data = new RectangleGeometry(new Rect(gapL, 0, gapR - gapL, 56), 3, 3),
                Clip = body,
                Fill = mmb,
                IsHitTestVisible = false,
            });

            Path Half(double x0, double x1) => new()
            {
                Data = new CombinedGeometry(GeometryCombineMode.Intersect, seam,
                           new RectangleGeometry(new Rect(x0, 0, x1 - x0, 200))),
                Clip = body,
                Fill = button,
                Stroke = dim,
                StrokeThickness = 1,
            };
            p.Lmb = Half(0, gapL);
            p.Rmb = Half(gapR, CanvasW);
            canvas.Children.Add(p.Lmb);
            canvas.Children.Add(p.Rmb);

            p.Wheel = new Rectangle
            {
                Width = WheelW,
                Height = WheelH,
                RadiusX = WheelW / 2,
                RadiusY = WheelW / 2,
                Fill = wheel,
                Stroke = dim,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(p.Wheel, swL);
            Canvas.SetTop(p.Wheel, WheelTop);
            canvas.Children.Add(p.Wheel);

            // Tread ridges.
            for (int k = 1; k < 5; k++)
            {
                double ty = WheelTop + k * WheelH / 5.0;
                canvas.Children.Add(new Line
                {
                    X1 = swL + 3, Y1 = ty, X2 = swR - 3, Y2 = ty,
                    Stroke = dim, StrokeThickness = 0.6, Opacity = 0.7,
                    IsHitTestVisible = false,
                });
            }

            p.ScrollUp = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(CenterX, WheelTop + 4),
                    new Point(CenterX - 4, WheelTop + 10),
                    new Point(CenterX + 4, WheelTop + 10),
                },
                Fill = dim,
            };
            canvas.Children.Add(p.ScrollUp);
            p.ScrollDown = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(CenterX, WheelBottom - 4),
                    new Point(CenterX - 4, WheelBottom - 10),
                    new Point(CenterX + 4, WheelBottom - 10),
                },
                Fill = dim,
            };
            canvas.Children.Add(p.ScrollDown);

            // DPI key under the wheel. Inert: nothing routes it, so it is
            // shell detail and never lights.
            p.Dpi = new Path
            {
                Data = new RectangleGeometry(new Rect(CenterX - 7, 58, 14, 11), 3, 3),
                Fill = button,
                Stroke = dim,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            canvas.Children.Add(p.Dpi);

            Path Flank(string d) => new()
            {
                Data = Geometry.Parse(d),
                Clip = body,
                Fill = button,
                Stroke = dim,
                StrokeThickness = 1,
            };
            p.X1 = Flank(X1Path);
            p.X2 = Flank(X2Path);
            canvas.Children.Add(p.X1);
            canvas.Children.Add(p.X2);

            p.MoveX = CenterX - MoveSize / 2;
            p.MoveCircle = new Ellipse
            {
                Width = MoveSize,
                Height = MoveSize,
                Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x88, 0x88, 0x88)),
                Stroke = dim,
                StrokeThickness = 1.4,
            };
            Canvas.SetLeft(p.MoveCircle, p.MoveX);
            Canvas.SetTop(p.MoveCircle, MoveTop);
            canvas.Children.Add(p.MoveCircle);

            // Quadrant ticks, so deflection reads against a reference.
            double ringC = MoveTop + MoveSize / 2, ringR = MoveSize / 2;
            foreach (var (dx, dy) in new[] { (0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0) })
            {
                canvas.Children.Add(new Line
                {
                    X1 = CenterX + dx * ringR * 0.80,
                    Y1 = ringC + dy * ringR * 0.80,
                    X2 = CenterX + dx * ringR * 0.98,
                    Y2 = ringC + dy * ringR * 0.98,
                    Stroke = dim,
                    StrokeThickness = 1.2,
                    IsHitTestVisible = false,
                });
            }

            p.MoveDot = new Ellipse { Width = 10, Height = 10, Fill = dot, IsHitTestVisible = false };
            Canvas.SetLeft(p.MoveDot, p.MoveX + MoveSize / 2 - 5);
            Canvas.SetTop(p.MoveDot, MoveTop + MoveSize / 2 - 5);
            canvas.Children.Add(p.MoveDot);

            return p;
        }

        /// <summary>Shell outline, added LAST by the caller so the edge stays
        /// one continuous line over the buttons and flank keys instead of
        /// being interrupted by them.</summary>
        internal static void AddOutline(Canvas canvas, Brush dim)
        {
            var body = Geometry.Parse(BodyPath);
            body.Freeze();
            canvas.Children.Add(new Path
            {
                Data = body,
                Fill = null,
                Stroke = dim,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            });
        }
    }
}
