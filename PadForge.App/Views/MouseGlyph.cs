using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PadForge.Views
{
    /// <summary>THE mouse drawing. One copy, on purpose.
    ///
    /// <para>This lived twice, hand-copied between the Devices detail pane
    /// (<see cref="MousePreviewControl"/>) and the virtual keyboard-and-mouse
    /// preview (<c>KBMPreviewView</c>). Reshaping one left the other on the old
    /// body, which is exactly the failure a duplicated drawing invites. Both
    /// build from here.</para>
    ///
    /// <para>The shell is gaming-mouse vector art from Zergatul.Obs.InputOverlay
    /// (MIT, (c) 2021 Igor Budzhak), vendored at <c>2DModels/MOUSE/mouse.svg</c>
    /// with its licence beside it and rendered into layers by
    /// <c>tools/gen_mouse_art.py</c>.</para>
    ///
    /// <para>NOTHING HERE REDRAWS THE ART. An earlier version traced each
    /// control into a polygon and stroked it, which produced a visibly faceted
    /// copy of the real curves AND, because the art's own line-work was drawn
    /// on top, a border around every border. Instead the artwork is one image
    /// and each control is a full-canvas ALPHA MASK over it, the same technique
    /// the controller previews use for their 2DModels overlays. Every layer
    /// shares one canvas, so they composite at (0,0) with no arithmetic.</para>
    ///
    /// <para>The line-work is itself drawn as a mask over the theme's own
    /// brush, so it reads correctly in light and dark without shipping two
    /// copies of the art.</para>
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
        internal const double CenterX = MouseArt.CenterX;
        internal const double WheelTop = MouseArt.WheelTop;
        internal const double WheelBottom = MouseArt.WheelBottom;

        internal const double MoveSize = 62;

        /// <summary>Hover tint. Deliberately a wash rather than a solid, so
        /// hovering reads as "this is pickable" without impersonating the
        /// pressed state the render loop paints.</summary>
        private static readonly Brush HoverWash = MakeWash();

        private static Brush MakeWash()
        {
            var b = new SolidColorBrush(Color.FromArgb(0x66, 0xE8, 0x7A, 0x2E));
            b.Freeze();
            return b;
        }
        /// <summary>Sits in the lower palm, clear of both the button seam and
        /// the shell vents, which are the only busy areas of the artwork.</summary>
        internal const double MoveTop =
            MouseArt.BodyTop + (MouseArt.BodyBottom - MouseArt.BodyTop) * 0.72 - MoveSize / 2;

        internal sealed class Parts
        {
            public Shape Lmb, Rmb, X1, X2, Wheel;
            /// <summary>Per-control hover wash. Its own layer because Fill on
            /// the visual is owned by the per-frame render loop and the flash
            /// animation, so hover cannot borrow that channel.</summary>
            public Shape LmbHover, RmbHover, X1Hover, X2Hover, WheelHover;
            /// <summary>Clickable geometry. A masked rectangle hit-tests over
            /// its whole rect, not its mask, so input needs real shapes.</summary>
            public Path LmbHit, RmbHit, X1Hit, X2Hit, WheelHit;
            public Polygon ScrollUp, ScrollDown;
            public Ellipse MoveCircle, MoveDot;
            public double MoveX;
        }

        /// <summary>One art layer, tinted by using it as an alpha mask over a
        /// flat brush. The shape comes from the artwork, the colour from us,
        /// so a control can light up without the art being recoloured or
        /// redrawn.</summary>
        private static Shape Layer(string file, Brush fill, Rect at)
        {
            var bmp = EmbeddedBitmaps.Load(MouseArt.Dir + file);
            var rect = new Rectangle
            {
                Width = at.Width,
                Height = at.Height,
                Fill = fill,
                IsHitTestVisible = false,
            };
            if (bmp != null)
            {
                var mask = new ImageBrush(bmp) { Stretch = Stretch.Fill };
                mask.Freeze();
                rect.OpacityMask = mask;
            }
            Canvas.SetLeft(rect, at.X);
            Canvas.SetTop(rect, at.Y);
            return rect;
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
            // body. Built per theme rebuild, never per frame.
            var shell = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            };
            byte hi = dark ? (byte)0x55 : (byte)0xCD;
            byte lo = dark ? (byte)0x3A : (byte)0xAE;
            shell.GradientStops.Add(new GradientStop(Color.FromRgb(hi, hi, hi), 0));
            shell.GradientStops.Add(new GradientStop(Color.FromRgb(lo, lo, lo), 1));
            shell.Freeze();

            canvas.Children.Add(Layer(MouseArt.Body, shell, MouseArt.BodyRect));

            p.Lmb = Layer(MouseArt.Lmb, button, MouseArt.LmbRect);
            p.Rmb = Layer(MouseArt.Rmb, button, MouseArt.RmbRect);
            p.Wheel = Layer(MouseArt.Wheel, wheel, MouseArt.WheelRect);
            p.X1 = Layer(MouseArt.SideUpper, button, MouseArt.SideUpperRect);
            p.X2 = Layer(MouseArt.SideLower, button, MouseArt.SideLowerRect);
            canvas.Children.Add(p.Lmb);
            canvas.Children.Add(p.Rmb);
            canvas.Children.Add(p.Wheel);
            canvas.Children.Add(p.X1);
            canvas.Children.Add(p.X2);

            // Hover washes, hidden until asked for. Same masks, so the
            // highlight follows the artwork's own curve exactly.
            Shape Hover(string file, Rect at)
            {
                var h = Layer(file, HoverWash, at);
                h.Visibility = Visibility.Collapsed;
                canvas.Children.Add(h);
                return h;
            }
            p.LmbHover = Hover(MouseArt.Lmb, MouseArt.LmbRect);
            p.RmbHover = Hover(MouseArt.Rmb, MouseArt.RmbRect);
            p.WheelHover = Hover(MouseArt.Wheel, MouseArt.WheelRect);
            p.X1Hover = Hover(MouseArt.SideUpper, MouseArt.SideUpperRect);
            p.X2Hover = Hover(MouseArt.SideLower, MouseArt.SideLowerRect);

            // Scroll direction arrows, off the wheel's measured bounds.
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
                Fill = new SolidColorBrush(Color.FromArgb(0x1E, 0x00, 0x00, 0x00)),
                Stroke = dim,
                StrokeThickness = 1.2,
            };
            Canvas.SetLeft(p.MoveCircle, p.MoveX);
            Canvas.SetTop(p.MoveCircle, MoveTop);
            canvas.Children.Add(p.MoveCircle);

            double ringC = MoveTop + MoveSize / 2, ringR = MoveSize / 2;
            foreach (var (dx, dy) in new[] { (0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0) })
            {
                canvas.Children.Add(new Line
                {
                    X1 = CenterX + dx * ringR * 0.76,
                    Y1 = ringC + dy * ringR * 0.76,
                    X2 = CenterX + dx * ringR * 0.98,
                    Y2 = ringC + dy * ringR * 0.98,
                    Stroke = dim,
                    StrokeThickness = 1.1,
                    IsHitTestVisible = false,
                });
            }

            p.MoveDot = new Ellipse { Width = 10, Height = 10, Fill = dot, IsHitTestVisible = false };
            Canvas.SetLeft(p.MoveDot, p.MoveX + MoveSize / 2 - 5);
            Canvas.SetTop(p.MoveDot, MoveTop + MoveSize / 2 - 5);
            canvas.Children.Add(p.MoveDot);

            // Hit shapes LAST, so they sit above every visual layer and win
            // the mouse. Transparent fill still answers hit tests; null does
            // not, which is the difference between clickable and inert.
            Path Hit(string data)
            {
                var g = Geometry.Parse(data);
                g.Freeze();
                var h = new Path { Data = g, Fill = Brushes.Transparent };
                canvas.Children.Add(h);
                return h;
            }
            p.LmbHit = Hit(MouseArt.LmbHit);
            p.RmbHit = Hit(MouseArt.RmbHit);
            p.WheelHit = Hit(MouseArt.WheelHit);
            p.X1Hit = Hit(MouseArt.SideUpperHit);
            p.X2Hit = Hit(MouseArt.SideLowerHit);

            return p;
        }

        /// <summary>The artwork's own line-work, added LAST so the seams read
        /// over the tinted controls. Drawn as a mask over the theme brush, so
        /// it is the real art in the right colour, not a re-stroked copy.</summary>
        internal static void AddOutline(Canvas canvas, Brush dim)
        {
            canvas.Children.Add(Layer(MouseArt.Line, dim, new Rect(0, 0, CanvasW, BodyH)));
        }
    }
}
