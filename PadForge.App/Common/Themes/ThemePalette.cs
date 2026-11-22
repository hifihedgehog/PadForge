using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PadForge.Common.Themes
{
    /// <summary>
    /// Suggests a theme's four colors from a picture (#591): the builder's
    /// Match the Picture, and the colors a Steam profile's game look takes.
    ///
    /// The picture is read as a few thousand samples in OKLCH. The
    /// background takes the hue of the picture's quiet tones at a ground's
    /// own lightness. The accent is the picture's strongest hue, counted by
    /// how many samples hold it and how vivid they are. The secondary and
    /// attention colors are the next two hues that stand apart from it, or
    /// hues set apart from the accent when the picture has one hue only.
    /// The engine then holds each to its contrast, as it holds a file's.
    /// </summary>
    internal static class ThemePalette
    {
        internal readonly record struct Suggestion(Color Background, Color Accent, Color Secondary, Color Attention);

        private const int Bins = 24;
        private const double BinSize = 360.0 / Bins;

        /// <summary>The four colors for a picture on a base.</summary>
        internal static Suggestion Suggest(BitmapSource picture, bool dark)
        {
            var samples = Sample(picture);
            if (samples.Count == 0)
                return new Suggestion(ThemeEngine.StockGround(dark), ThemeEngine.StockAccent, ThemeEngine.StockSecondary(dark), ThemeEngine.StockAttention(dark));

            // The ground: the hue the quiet samples lean to, at a ground's lightness.
            // A picture with few or none of them: all of it. A picture of a few
            // pixels can have none while a twentieth of it rounds to none.
            var quiet = samples.Where(s => dark ? s.L < 0.5 : s.L > 0.5).ToList();
            if (quiet.Count == 0 || quiet.Count < samples.Count / 20) quiet = samples;
            double groundHue = MeanHue(quiet, s => 0.02 + s.C);
            double groundChroma = Math.Min(dark ? 0.07 : 0.035, quiet.Average(s => s.C) * 0.9);
            Color background = new Lch(dark ? 0.16 : 0.93, groundChroma, groundHue).ToColor();

            // The hues, each by how much vivid picture holds it.
            var weight = new double[Bins];
            var chroma = new double[Bins];
            foreach (var s in samples)
            {
                if (s.C < 0.05 || s.L < 0.22 || s.L > 0.95) continue;
                int bin = (int)(s.H / BinSize) % Bins;
                double w = s.C * s.C;
                weight[bin] += w;
                chroma[bin] = Math.Max(chroma[bin], s.C);
            }
            var ranked = Enumerable.Range(0, Bins).Where(b => weight[b] > 0).OrderByDescending(b => weight[b]).ToList();

            double accentHue = ranked.Count > 0 ? Center(ranked[0]) : (groundHue + 180) % 360;
            double accentChroma = ranked.Count > 0 ? Math.Clamp(chroma[ranked[0]], 0.13, 0.26) : 0.14;
            Color accent = new Lch(dark ? 0.76 : 0.50, accentChroma, accentHue).ToColor();

            // The next two hues that stand apart: 50 degrees from the accent, 40 from each other.
            var taken = new List<double> { accentHue };
            double Next(double apart, double fallback)
            {
                foreach (int b in ranked)
                {
                    double hue = Center(b);
                    if (weight[b] < weight[ranked[0]] * 0.04) break;
                    if (taken.All(t => Distance(t, hue) >= apart))
                    {
                        taken.Add(hue);
                        return hue;
                    }
                }
                taken.Add(fallback);
                return fallback;
            }
            double secondaryHue = Next(50, (accentHue + 160) % 360);
            // Attention is a warm, wary color: a hue of the picture's when it has a warm one apart from the rest, else gold or orange.
            double warm = Distance(accentHue, 80) >= 40 && Distance(secondaryHue, 80) >= 40 ? 80 : (Distance(accentHue, 45) >= 40 ? 45 : 110);
            double attentionHue = Next(40, warm);

            Color secondary = new Lch(dark ? 0.83 : 0.45, 0.10, secondaryHue).ToColor();
            Color attention = new Lch(dark ? 0.80 : 0.48, 0.13, attentionHue).ToColor();
            return new Suggestion(background, accent, secondary, attention);
        }

        private static double Center(int bin) => bin * BinSize + BinSize / 2;

        private static double Distance(double a, double b)
        {
            double d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        /// <summary>The middle of a set of hues: an average on the circle,
        /// where the average of 350 and 10 is 0 and never 180.</summary>
        private static double MeanHue(List<Lch> samples, Func<Lch, double> weightOf)
        {
            double x = 0, y = 0;
            foreach (var s in samples)
            {
                double w = weightOf(s);
                x += Math.Cos(s.H * Math.PI / 180) * w;
                y += Math.Sin(s.H * Math.PI / 180) * w;
            }
            double hue = Math.Atan2(y, x) * 180 / Math.PI;
            return (hue + 360) % 360;
        }

        /// <summary>The picture as a grid of samples, 96 across. A clear
        /// pixel is no sample: a cutout's ground is not its color.</summary>
        private static List<Lch> Sample(BitmapSource picture)
        {
            var samples = new List<Lch>();
            if (picture == null || picture.PixelWidth <= 0 || picture.PixelHeight <= 0) return samples;
            const int across = 96;
            double scale = Math.Min(1.0, (double)across / picture.PixelWidth);
            BitmapSource small = scale < 1.0 ? new TransformedBitmap(picture, new ScaleTransform(scale, scale)) : picture;
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
            // A share of a very tall picture is enough.
            int rows = Math.Min(height, 512);
            var pixels = new byte[stride * rows];
            bgra.CopyPixels(new System.Windows.Int32Rect(0, 0, width, rows), pixels, stride, 0);
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128) continue;
                samples.Add(Lch.From(Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i])));
            }
            return samples;
        }
    }
}
