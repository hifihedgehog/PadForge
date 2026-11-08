using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PadForge.Common.Input.KeyboardScreens
{
    /// <summary>
    /// Draws what a keyboard screen shows (issue #519), as top-down B, G, R,
    /// A pixels at the screen's own size. The VMU's drawing
    /// (DreamcastScreenService's RenderText, Fit and Threshold) taken to any
    /// size and to color: a monochrome screen gets dark text on a light
    /// field cut to one bit at half luminance, a color screen light text on
    /// dark. WPF draws, so every call here runs on the UI thread.
    /// </summary>
    internal static class KeyboardScreenRenderer
    {
        /// <summary>The largest picture side kept in memory. A picture
        /// larger than the screen is shown a screen-sized window at a time,
        /// and a 2048 by 2048 picture is 16 MB of pixels.</summary>
        public const int MaxPictureSide = 2048;

        /// <summary>Text drawn as large as fits the screen, on up to as many
        /// lines as the words need, centered.</summary>
        public static byte[] RenderText(string text, KeyboardScreenKind kind)
        {
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            bool mono = KeyboardScreenFormats.IsMonochrome(kind);
            text = (text ?? string.Empty).Trim();
            var foreground = mono ? Brushes.Black : Brushes.White;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            // A margin of a twentieth keeps text off the color screens'
            // bezels. The monochrome screen has no pixel to spare.
            int margin = mono ? 0 : Math.Max(4, width / 20);
            double maxWidth = width - 2 * margin, maxHeight = height - 2 * margin;
            FormattedText fitted = null;
            for (double size = maxHeight; size >= 6; size -= 1)
            {
                var candidate = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, size, foreground, 1.0)
                {
                    MaxTextWidth = maxWidth,
                    TextAlignment = TextAlignment.Center,
                };
                fitted = candidate;
                if (candidate.MinWidth <= maxWidth && candidate.Height <= maxHeight) break;
            }
            var visual = new DrawingVisual();
            if (mono)
            {
                TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Aliased);
                TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
                RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
            }
            else
            {
                TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
            }
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(mono ? Brushes.White : Brushes.Black, null, new Rect(0, 0, width, height));
                if (fitted != null && text.Length > 0)
                    dc.DrawText(fitted, new Point(margin, margin + Math.Max(0, (maxHeight - fitted.Height) / 2)));
            }
            return Pixels(visual, kind);
        }

        /// <summary>A picture file, decoded no larger than
        /// <see cref="MaxPictureSide"/> on either side, or null when the file
        /// is missing, past the 16 MB read bound or not a picture WPF
        /// decodes. Never throws.</summary>
        public static BitmapSource LoadPicture(string storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath)) return null;
            try
            {
                // At its own size: Picture mode shows a window of its own pixels,
                // and Profile Picture never scales one up.
                return BoundedBitmap.FromBytes(MenuIconResolver.ReadCapped(IconPackageManager.ResolvePath(storedPath)),
                    MaxPictureSide, MaxPictureSide, scaleUp: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException or System.Security.SecurityException
                or OutOfMemoryException)
            {
                return null;
            }
        }

        /// <summary>How far a picture can be panned on a screen: its size less
        /// the screen's, in each direction, never below zero.</summary>
        public static (int X, int Y) PanLimits(BitmapSource picture, KeyboardScreenKind kind)
        {
            if (picture == null) return (0, 0);
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            return (Math.Max(0, picture.PixelWidth - width), Math.Max(0, picture.PixelHeight - height));
        }

        /// <summary>Picture mode: the picture at its own size. A picture
        /// larger than the screen in a direction shows the screen's width or
        /// height of it from the pan position, and one smaller is centered
        /// in that direction.</summary>
        public static byte[] RenderWindow(BitmapSource picture, KeyboardScreenKind kind, int panX, int panY)
        {
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            var (maxX, maxY) = PanLimits(picture, kind);
            double x = picture.PixelWidth > width ? -Math.Clamp(panX, 0, maxX) : (width - picture.PixelWidth) / 2.0;
            double y = picture.PixelHeight > height ? -Math.Clamp(panY, 0, maxY) : (height - picture.PixelHeight) / 2.0;
            return Draw(picture, kind, new Rect(Math.Round(x), Math.Round(y), picture.PixelWidth, picture.PixelHeight));
        }

        /// <summary>Profile Picture mode: the picture scaled down to fit and
        /// centered, never scaled up past its own size, the VMU's Fit.</summary>
        public static byte[] RenderFitted(BitmapSource picture, KeyboardScreenKind kind)
        {
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            double scale = Math.Min(1.0, Math.Min((double)width / picture.PixelWidth, (double)height / picture.PixelHeight));
            double w = picture.PixelWidth * scale, h = picture.PixelHeight * scale;
            return Draw(picture, kind, new Rect((width - w) / 2, (height - h) / 2, w, h));
        }

        private static byte[] Draw(BitmapSource picture, KeyboardScreenKind kind, Rect at)
        {
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            bool mono = KeyboardScreenFormats.IsMonochrome(kind);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                // The monochrome field is light, so transparency reads light,
                // the VMU's rule. A color screen's field is black.
                dc.DrawRectangle(mono ? Brushes.White : Brushes.Black, null, new Rect(0, 0, width, height));
                dc.DrawImage(picture, at);
            }
            return Pixels(visual, kind);
        }

        /// <summary>
        /// Lightbar mode: the whole screen in the lightbar's color. A
        /// monochrome screen shows the color's luminance as an ordered
        /// dither: a 4 by 4 Bayer matrix lights a pixel where the luminance
        /// falls at or below the matrix's step, so white lights none, black
        /// lights all and a mid gray lights half.
        /// </summary>
        public static byte[] RenderColor(byte r, byte g, byte b, KeyboardScreenKind kind)
        {
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            var bgra = new byte[width * height * 4];
            bool mono = KeyboardScreenFormats.IsMonochrome(kind);
            int luminance = (r * 299 + g * 587 + b * 114) / 1000;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    if (mono)
                    {
                        byte v = luminance <= Bayer4[(y & 3) * 4 + (x & 3)] * 16 + 8 ? (byte)0 : (byte)255;
                        bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
                    }
                    else
                    {
                        bgra[i] = b;
                        bgra[i + 1] = g;
                        bgra[i + 2] = r;
                    }
                    bgra[i + 3] = 255;
                }
            }
            return bgra;
        }

        private static readonly int[] Bayer4 = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        /// <summary>A rendered visual's pixels, cut to pure black and white
        /// at half luminance on a monochrome screen.</summary>
        private static byte[] Pixels(Visual visual, KeyboardScreenKind kind)
        {
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
            var bgra = new byte[width * height * 4];
            converted.CopyPixels(bgra, width * 4, 0);
            if (KeyboardScreenFormats.IsMonochrome(kind))
            {
                for (int p = 0; p < width * height; p++)
                {
                    byte v = KeyboardScreenFormats.IsDark(bgra, p) ? (byte)0 : (byte)255;
                    int i = p * 4;
                    bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
                    bgra[i + 3] = 255;
                }
            }
            else
            {
                for (int p = 0; p < width * height; p++) bgra[p * 4 + 3] = 255;
            }
            return bgra;
        }

        /// <summary>A picture as the screen shows it, for previews scaled up
        /// with nearest-neighbor: a monochrome screen's lit pixels dark on
        /// the LCD's pale field, a color screen's pixels as they are.</summary>
        public static BitmapSource ToBitmap(byte[] bgra, KeyboardScreenKind kind)
        {
            var (width, height) = KeyboardScreenFormats.SizeOf(kind);
            var pixels = new byte[width * height * 4];
            bool valid = bgra != null && bgra.Length == pixels.Length;
            bool mono = KeyboardScreenFormats.IsMonochrome(kind);
            for (int p = 0; p < width * height; p++)
            {
                int i = p * 4;
                if (mono)
                {
                    bool dark = valid && KeyboardScreenFormats.IsDark(bgra, p);
                    pixels[i] = dark ? (byte)0x1C : (byte)0xB4;
                    pixels[i + 1] = dark ? (byte)0x2A : (byte)0xD0;
                    pixels[i + 2] = dark ? (byte)0x1F : (byte)0xBF;
                }
                else if (valid)
                {
                    pixels[i] = bgra[i];
                    pixels[i + 1] = bgra[i + 1];
                    pixels[i + 2] = bgra[i + 2];
                }
                pixels[i + 3] = 255;
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
