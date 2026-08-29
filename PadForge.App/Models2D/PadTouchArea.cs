using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PadForge.Models2D
{
    /// <summary>A touchpad's touch area, in its click sprite's pixels.
    ///
    /// <para>The layout entry for a pad is a bounding BOX, and a pad is only
    /// rarely a box: the 2015 Steam Controller's pads are discs, the 2026's
    /// are rounded squares canted a few degrees, and the corners of those
    /// boxes are off the pad entirely. A touch reported near the rim drew
    /// its preview dot in mid-air, outside the pad it happened on.</para>
    ///
    /// <para>The area is what the click art ENCLOSES, not the art's own
    /// opaque pixels: some packs draw the click state as a bare outline and
    /// others as an outline with a washed interior, and both have to come
    /// out as the same filled region. Flooding the background in from the
    /// border and keeping the rest gives that for either authoring and for
    /// any shape, which is what lets one code path serve a disc, a rounded
    /// square and a canted rounded square with no per-layout shape
    /// table.</para></summary>
    public sealed class PadTouchArea
    {
        private readonly int _w, _h;
        private readonly bool[] _inside;
        private readonly double _cx, _cy;   // the point every clamp pulls toward

        private PadTouchArea(int w, int h, bool[] inside, double cx, double cy)
        {
            _w = w; _h = h; _inside = inside; _cx = cx; _cy = cy;
        }

        /// <summary>Sprite width and height, in pixels.</summary>
        public int Width => _w;
        /// <summary>Sprite height, in pixels.</summary>
        public int Height => _h;

        /// <summary>True when a normalized coordinate lands on the pad.</summary>
        public bool Contains(double u, double v) =>
            At((int)Math.Round(u * (_w - 1)), (int)Math.Round(v * (_h - 1)));

        private bool At(int x, int y) =>
            (uint)x < (uint)_w && (uint)y < (uint)_h && _inside[y * _w + x];

        /// <summary>The nearest point on the pad to a normalized coordinate,
        /// along the line back to the pad's middle. A coordinate already on
        /// the pad comes back unchanged.</summary>
        public (double X, double Y) Clamp(double u, double v)
        {
            double px = u * (_w - 1), py = v * (_h - 1);
            if (At((int)Math.Round(px), (int)Math.Round(py))) return (u, v);

            // Sixteen halvings resolve to well under a pixel on any sprite
            // this app ships, and land the point on the rim in the direction
            // the touch was heading rather than dropping it in the middle.
            double lo = 0, hi = 1;
            for (int i = 0; i < 16; i++)
            {
                double m = (lo + hi) / 2;
                if (At((int)Math.Round(_cx + (px - _cx) * m),
                       (int)Math.Round(_cy + (py - _cy) * m)))
                    lo = m;
                else
                    hi = m;
            }
            return ((_cx + (px - _cx) * lo) / (_w - 1),
                    (_cy + (py - _cy) * lo) / (_h - 1));
        }

        /// <summary>Measures a pad's touch area off a click sprite already
        /// decoded. Null when the bitmap is missing or encloses nothing,
        /// which leaves a caller on the layout's bounding box.</summary>
        public static PadTouchArea Measure(BitmapSource bmp)
        {
            if (bmp == null) return null;
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            if (w < 3 || h < 3 || (long)w * h > 4_000_000) return null;

            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            var open = new bool[w * h];
            var stack = new Stack<int>();
            void Seed(int i)
            {
                if (!open[i] && px[i * 4 + 3] <= 24) { open[i] = true; stack.Push(i); }
            }
            for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % w, y = i / w;
                if (x > 0) Seed(i - 1);
                if (x < w - 1) Seed(i + 1);
                if (y > 0) Seed(i - w);
                if (y < h - 1) Seed(i + w);
            }

            var inside = new bool[w * h];
            long n = 0, sx = 0, sy = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (open[i]) continue;
                    inside[i] = true;
                    n++; sx += x; sy += y;
                }
            if (n == 0) return null;
            return new PadTouchArea(w, h, inside, (double)sx / n, (double)sy / n);
        }
    }
}
