using System;
using System.Globalization;
using System.Windows.Media;

namespace PadForge.Common.Themes
{
    /// <summary>A color as lightness, chroma and hue in OKLCH (#591), the
    /// space a theme's colors are worked out in: two colors of one
    /// lightness look equally light there, whatever their hue, so a brush
    /// can keep its place in a family while the family's hue turns.</summary>
    internal readonly struct Lch
    {
        internal readonly double L, C, H;

        internal Lch(double l, double c, double h)
        {
            L = l;
            C = c;
            H = ((h % 360) + 360) % 360;
        }

        internal static Lch From(Color color)
        {
            double r = ThemeColor.Linear(color.R), g = ThemeColor.Linear(color.G), b = ThemeColor.Linear(color.B);
            double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
            double lightness = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
            double a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
            double bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
            return new Lch(lightness, Math.Sqrt(a * a + bb * bb), Math.Atan2(bb, a) * 180 / Math.PI);
        }

        private static bool Rgb(double lightness, double chroma, double hue, out double r, out double g, out double b)
        {
            double a = chroma * Math.Cos(hue * Math.PI / 180), bb = chroma * Math.Sin(hue * Math.PI / 180);
            double l = lightness + 0.3963377774 * a + 0.2158037573 * bb;
            double m = lightness - 0.1055613458 * a - 0.0638541728 * bb;
            double s = lightness - 0.0894841775 * a - 1.2914855480 * bb;
            l = l * l * l;
            m = m * m * m;
            s = s * s * s;
            r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
            g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
            b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
            const double slack = 0.0005;
            return r >= -slack && r <= 1 + slack && g >= -slack && g <= 1 + slack && b >= -slack && b <= 1 + slack;
        }

        private static byte Encode(double linear)
        {
            linear = Math.Clamp(linear, 0, 1);
            double c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            return (byte)Math.Round(Math.Clamp(c, 0, 1) * 255);
        }

        /// <summary>The color in sRGB, its chroma brought in until it fits.</summary>
        internal Color ToColor()
        {
            double chroma = C;
            if (!Rgb(L, chroma, H, out double r, out double g, out double b))
            {
                double low = 0, high = chroma;
                for (int i = 0; i < 24; i++)
                {
                    double mid = (low + high) / 2;
                    if (Rgb(L, mid, H, out _, out _, out _)) low = mid; else high = mid;
                }
                Rgb(L, low, H, out r, out g, out b);
            }
            return Color.FromRgb(Encode(r), Encode(g), Encode(b));
        }
    }

    /// <summary>The measures a theme's colors are held to (#591): how light
    /// a color is, how well one reads on another, and the two steps that
    /// move a color until it fits.</summary>
    internal static class ThemeColor
    {
        /// <summary>Reads "#RRGGBB" or "RRGGBB". A theme file's colors are
        /// opaque, so no other form is a color.</summary>
        internal static bool TryParse(string text, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.StartsWith('#')) text = text.Substring(1);
            if (text.Length != 6) return false;
            // Hex digits alone: HexNumber also lets white space through,
            // which would read "# 12345" as #012345.
            if (!uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)) return false;
            color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }

        internal static string Format(Color color)
            => "#" + color.R.ToString("X2", CultureInfo.InvariantCulture)
                   + color.G.ToString("X2", CultureInfo.InvariantCulture)
                   + color.B.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>How light the color is as it is stored, 0 to 1: the
        /// measure a ground's steps are counted in.</summary>
        internal static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        internal static double Linear(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>The light the color gives off, 0 to 1: the measure
        /// contrast is counted in.</summary>
        internal static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

        /// <summary>How well one color reads on the other, 1 to 21.</summary>
        internal static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>The color, moved away from the ground's lightness
        /// until it reads on it at the ratio. A color that already reads
        /// is returned as it is.</summary>
        internal static Color Reads(Color color, Color on, double ratio) => Reads(color, on, ratio, Luminance(on) < 0.2);

        /// <summary>The color, moved lighter or darker as asked until it
        /// reads on the ground at the ratio, or as far as it goes.</summary>
        internal static Color Reads(Color color, Color on, double ratio, bool lighter)
        {
            if (Contrast(color, on) >= ratio) return color;
            var c = Lch.From(color);
            for (int i = 0; i < 200; i++)
            {
                c = new Lch(Math.Clamp(c.L + (lighter ? 0.005 : -0.005), 0, 1), c.C, c.H);
                Color tried = c.ToColor();
                if (Contrast(tried, on) >= ratio) return tried;
            }
            return lighter ? Colors.White : Colors.Black;
        }

        /// <summary>A color of the hue and chroma whose stored lightness
        /// is the target.</summary>
        internal static Color WithLuma(double target, double chroma, double hue)
        {
            double low = 0, high = 1;
            Color best = Colors.Black;
            for (int i = 0; i < 28; i++)
            {
                double mid = (low + high) / 2;
                best = new Lch(mid, chroma, hue).ToColor();
                if (Luma(best) < target) low = mid; else high = mid;
            }
            return best;
        }
    }
}
