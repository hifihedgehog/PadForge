using System;
using System.Windows;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>
    /// The magnifier lens's border (#585), drawn in the display's pixels in
    /// its own window around the lens. The lens is cut to its shape by a
    /// window region, whose curved edge steps from pixel to pixel, so the
    /// border's inside reaches one pixel into the lens and covers the steps
    /// with a smoothed edge.
    /// </summary>
    internal sealed class LensFrameView : FrameworkElement
    {
        private Geometry _ring;
        private Brush _brush;

        /// <summary>A border <paramref name="thickness"/> pixels wide outside a
        /// lens of <paramref name="width"/> by <paramref name="height"/>, round
        /// or square, in <paramref name="color"/>. The view's window is the
        /// lens grown by the thickness on each side.</summary>
        internal void Show(int width, int height, int thickness, bool round, Color color)
        {
            // The ring runs from the window's edge to a pixel inside the
            // lens: thickness + 1 wide, its middle half that from the edge.
            double pen = thickness + 1, inset = pen / 2;
            var line = new Rect(inset, inset, width + thickness * 2 - inset * 2, height + thickness * 2 - inset * 2);
            Geometry shape = round
                ? new EllipseGeometry(line)
                : new RectangleGeometry(line);
            var ring = shape.GetWidenedPathGeometry(new Pen(Brushes.Black, pen));
            ring.Freeze();
            _ring = ring;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _brush = brush;
            InvalidateVisual();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_ring == null) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            dc.PushTransform(new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY));
            dc.DrawGeometry(_brush, null, _ring);
            dc.Pop();
        }
    }

    /// <summary>
    /// The spotlight's hole (#585): its square window filled with the
    /// shade's color except for a circle with a smoothed edge. The shade
    /// around it is a plain window of one color with this square cut out, so
    /// the two meet on whole pixels and the circle's edge is the only curve
    /// drawn.
    /// </summary>
    internal sealed class SpotlightHoleView : FrameworkElement
    {
        private Geometry _shade;
        private Brush _brush;

        /// <summary>A square of <paramref name="size"/> pixels shaded to
        /// <paramref name="dim"/> (0 to 1) outside the circle it holds, with
        /// the circle's center at <paramref name="origin"/> and the square
        /// cut to <paramref name="width"/> by <paramref name="height"/> where
        /// it meets a display's edge.</summary>
        internal void Show(int size, Point origin, int width, int height, double dim)
        {
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(new RectangleGeometry(new Rect(0, 0, width, height)));
            group.Children.Add(new EllipseGeometry(origin, size / 2.0, size / 2.0));
            group.Freeze();
            _shade = group;
            var brush = new SolidColorBrush(Color.FromArgb(ShadeAlpha(dim), 0, 0, 0));
            brush.Freeze();
            _brush = brush;
            InvalidateVisual();
        }

        /// <summary>The shade's alpha for a dimming of 0 to 1, the same byte
        /// the shade window's SetLayeredWindowAttributes takes, so the two
        /// windows are one shade.</summary>
        internal static byte ShadeAlpha(double dim) => (byte)Math.Round(Math.Clamp(dim, 0, 1) * 255);

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_shade == null) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            dc.PushTransform(new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY));
            dc.DrawGeometry(_brush, null, _shade);
            dc.Pop();
        }
    }
}
