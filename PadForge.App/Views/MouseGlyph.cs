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
    /// duplicated drawing invites. Both build from here.</para>
    ///
    /// <para>The shell is no longer hand-authored. It is gaming-mouse vector
    /// art from Zergatul.Obs.InputOverlay (MIT, (c) 2021 Igor Budzhak),
    /// vendored at <c>2DModels/MOUSE/mouse.svg</c> with its licence beside it
    /// and converted to path geometry by <c>tools/gen_mouse_art.py</c> into
    /// <see cref="MouseArt"/>. Successive attempts to draw a convincing mouse
    /// by hand produced first a plain egg, then, chasing a gaming profile,
    /// something closer to a peanut.</para>
    ///
    /// <para>The one thing the art does NOT carry is any indication of
    /// movement, so the deflection ring is still ours, drawn into the palm
    /// where the artwork leaves a clear field.</para>
    ///
    /// <para>Callers own behaviour. This returns the live shapes and attaches
    /// no handlers, tooltips or hit-testing policy, because the two surfaces
    /// differ there (the KBM preview is clickable for recording, the Devices
    /// pane is read-only).</para></summary>
    internal static class MouseGlyph
    {
        internal const double CanvasW = MouseArt.W;
        internal const double BodyH = MouseArt.H;
        internal const double MoveSize = 66;

        /// <summary>The art's own axis of symmetry, which is NOT the canvas
        /// midpoint: the shell is drawn a shade right of centre.</summary>
        internal static readonly double CenterX;
        internal static readonly double MoveTop;
        internal static readonly double WheelTop;
        internal static readonly double WheelBottom;

        private static readonly Geometry WheelGeo, PalmGeo;

        static MouseGlyph()
        {
            WheelGeo = Frozen(MouseArt.WheelWell);
            PalmGeo = Frozen(MouseArt.Palm);
            var wb = WheelGeo.Bounds;
            var pb = PalmGeo.Bounds;
            // Measured off the geometry rather than written down, so a
            // re-export of the art cannot silently desynchronise them.
            CenterX = wb.Left + wb.Width / 2;
            WheelTop = wb.Top;
            WheelBottom = wb.Bottom;
            MoveTop = pb.Top + pb.Height * 0.42 - MoveSize / 2;
        }

        private static Geometry Frozen(string data)
        {
            var g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }

        internal sealed class Parts
        {
            public Path Lmb, Rmb, X1, X2, Wheel;
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

            canvas.Width = CanvasW;
            canvas.Height = BodyH;

            // Shell, with a shallow vertical gradient so it reads as a curved
            // body rather than a flat cut-out. Built per theme rebuild, never
            // per frame.
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

            canvas.Children.Add(new Path { Data = PalmGeo, Fill = shell, IsHitTestVisible = false });

            // Shell cutouts. Decorative: never lit, never routed.
            foreach (var vent in MouseArt.Vents)
            {
                canvas.Children.Add(new Path
                {
                    Data = Frozen(vent),
                    Fill = mmb,
                    Opacity = 0.55,
                    IsHitTestVisible = false,
                });
            }

            Path Region(Geometry g, Brush fill) => new()
            {
                Data = g,
                Fill = fill,
                Stroke = dim,
                StrokeThickness = 0.8,
            };

            p.Lmb = Region(Frozen(MouseArt.Lmb), button);
            p.Rmb = Region(Frozen(MouseArt.Rmb), button);
            p.Wheel = Region(WheelGeo, wheel);
            p.X1 = Region(Frozen(MouseArt.SideUpper), button);
            p.X2 = Region(Frozen(MouseArt.SideLower), button);
            canvas.Children.Add(p.Lmb);
            canvas.Children.Add(p.Rmb);
            canvas.Children.Add(p.Wheel);
            canvas.Children.Add(p.X1);
            canvas.Children.Add(p.X2);

            // Scroll direction arrows, placed off the wheel's measured bounds.
            p.ScrollUp = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(CenterX, WheelTop + 5),
                    new Point(CenterX - 4.5, WheelTop + 12),
                    new Point(CenterX + 4.5, WheelTop + 12),
                },
                Fill = dim,
            };
            p.ScrollDown = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(CenterX, WheelBottom - 5),
                    new Point(CenterX - 4.5, WheelBottom - 12),
                    new Point(CenterX + 4.5, WheelBottom - 12),
                },
                Fill = dim,
            };
            canvas.Children.Add(p.ScrollUp);
            canvas.Children.Add(p.ScrollDown);

            // Movement. The artwork shows a mouse but says nothing about which
            // way it is being moved, so the deflection ring is ours.
            p.MoveX = CenterX - MoveSize / 2;
            p.MoveCircle = new Ellipse
            {
                Width = MoveSize,
                Height = MoveSize,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)),
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
                    X1 = CenterX + dx * ringR * 0.78,
                    Y1 = ringC + dy * ringR * 0.78,
                    X2 = CenterX + dx * ringR * 0.99,
                    Y2 = ringC + dy * ringR * 0.99,
                    Stroke = dim,
                    StrokeThickness = 1.2,
                    IsHitTestVisible = false,
                });
            }

            p.MoveDot = new Ellipse { Width = 11, Height = 11, Fill = dot, IsHitTestVisible = false };
            Canvas.SetLeft(p.MoveDot, p.MoveX + MoveSize / 2 - 5.5);
            Canvas.SetTop(p.MoveDot, MoveTop + MoveSize / 2 - 5.5);
            canvas.Children.Add(p.MoveDot);

            return p;
        }

        /// <summary>The shell line-work, added LAST by the caller so the seams
        /// stay continuous over the filled regions instead of being cut by
        /// them. This is the upstream art drawn as authored.</summary>
        internal static void AddOutline(Canvas canvas, Brush dim)
        {
            foreach (var d in MouseArt.Outline)
            {
                canvas.Children.Add(new Path
                {
                    Data = Frozen(d),
                    Fill = null,
                    Stroke = dim,
                    StrokeThickness = 1.1,
                    IsHitTestVisible = false,
                });
            }
        }
    }
}
