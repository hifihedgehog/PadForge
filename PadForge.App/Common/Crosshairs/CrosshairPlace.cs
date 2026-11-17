using System;
using System.Windows;

namespace PadForge.Common.Crosshairs
{
    /// <summary>
    /// Where a crosshair and its window go on a display, in pixels (#585).
    /// </summary>
    internal static class CrosshairPlace
    {
        /// <summary>The point a crosshair marks: its display's center, rounded
        /// to a whole pixel as Crosshair-Overlay rounds its window's center
        /// (Window.cpp GetOverlayCenter, round()), plus the offset in whole
        /// pixels, floored as its DrawOverlay floors flOffset.</summary>
        public static (int X, int Y) Center(Int32Rect display, double offsetX, double offsetY)
            => ((int)Math.Round(display.X + display.Width / 2.0, MidpointRounding.AwayFromZero) + (int)Math.Floor(offsetX),
                (int)Math.Round(display.Y + display.Height / 2.0, MidpointRounding.AwayFromZero) + (int)Math.Floor(offsetY));

        /// <summary>The window over what a crosshair draws: <paramref name="bounds"/>
        /// around <paramref name="center"/> with a pixel more on each side for a
        /// smoothed edge, cut to the display, since a crosshair belongs to its
        /// display. A rectangle of no size when nothing of it is on the
        /// display. <paramref name="origin"/> is the center in the window's
        /// own pixels.</summary>
        public static Int32Rect Window(Int32Rect display, (int X, int Y) center, Rect bounds, out Point origin)
        {
            origin = default;
            if (display.Width <= 0 || display.Height <= 0 || bounds.IsEmpty) return default;
            int left = Math.Max(display.X, (int)Math.Floor(center.X + bounds.Left) - 1);
            int top = Math.Max(display.Y, (int)Math.Floor(center.Y + bounds.Top) - 1);
            int right = Math.Min(display.X + display.Width, (int)Math.Ceiling(center.X + bounds.Right) + 1);
            int bottom = Math.Min(display.Y + display.Height, (int)Math.Ceiling(center.Y + bounds.Bottom) + 1);
            if (right <= left || bottom <= top) return default;
            origin = new Point(center.X - left, center.Y - top);
            return new Int32Rect(left, top, right - left, bottom - top);
        }
    }

    /// <summary>
    /// The magnifier's rectangles, in pixels (#585).
    /// </summary>
    internal static class MagnifierMath
    {
        /// <summary>A lens of <paramref name="width"/> by <paramref name="height"/>
        /// centered on (x, y) and kept on the display.</summary>
        public static Int32Rect Lens(Int32Rect display, int x, int y, int width, int height)
        {
            width = Math.Min(width, display.Width);
            height = Math.Min(height, display.Height);
            return new Int32Rect(Keep(x - width / 2, display.X, display.Width, width),
                Keep(y - height / 2, display.Y, display.Height, height), width, height);
        }

        /// <summary>What a lens shows: its size divided by the zoom, centered on
        /// (x, y) and kept on the display, as Microsoft's windowed magnifier
        /// sizes and keeps its source (Windows-classic-samples,
        /// Samples/Magnification/cpp/Windowed/MagnifierSample.cpp,
        /// UpdateMagWindow), there on the primary display.</summary>
        public static Int32Rect Source(Int32Rect display, int x, int y, int lensWidth, int lensHeight, double zoom)
        {
            int width = Math.Max(1, (int)(lensWidth / zoom)), height = Math.Max(1, (int)(lensHeight / zoom));
            return new Int32Rect(Keep(x - width / 2, display.X, display.Width, width),
                Keep(y - height / 2, display.Y, display.Height, height), width, height);
        }

        /// <summary>The sample's two tests in its order: no farther left than
        /// the display's edge, then no farther right than its other
        /// edge.</summary>
        private static int Keep(int start, int edge, int span, int size)
        {
            if (start < edge) start = edge;
            if (start > edge + span - size) start = edge + span - size;
            return start;
        }

        /// <summary>The offsets that zoom (x, y) to the display's center.
        /// Windows' full-screen magnifier shows a point S at (S − offset) ×
        /// zoom, counted from the primary display's corner: Microsoft's
        /// SetZoom example puts the primary display's center at its center
        /// with offsets of its size × (1 − 1/zoom) / 2, and its
        /// SetFullscreenMagnification example puts a window's corner at the
        /// virtual screen's with the window's corner less that corner / zoom
        /// (MagSetFullscreenTransform, Examples). The offsets are kept inside
        /// D.left × (1 − 1/zoom) to D.right × (1 − 1/zoom), so the display
        /// shows only itself, zoomed.</summary>
        public static (int X, int Y) FullScreenOffset(Int32Rect display, int x, int y, double zoom)
        {
            double keep = 1 - 1 / zoom;
            double ox = x - (display.X + display.Width / 2.0) / zoom;
            double oy = y - (display.Y + display.Height / 2.0) / zoom;
            ox = Math.Min(Math.Max(ox, display.X * keep), (display.X + display.Width) * keep);
            oy = Math.Min(Math.Max(oy, display.Y * keep), (display.Y + display.Height) * keep);
            return ((int)Math.Round(ox, MidpointRounding.AwayFromZero), (int)Math.Round(oy, MidpointRounding.AwayFromZero));
        }

        /// <summary>The spotlight's hole: a square of the circle's size
        /// centered on (x, y). The shade covers the display around it, and
        /// the hole's own window draws the circle's edge inside it.</summary>
        public static Int32Rect Hole(int x, int y, int size) => new(x - size / 2, y - size / 2, size, size);
    }
}
