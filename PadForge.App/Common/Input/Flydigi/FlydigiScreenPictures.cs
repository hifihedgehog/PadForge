using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Engine.Flydigi;
using Format = PadForge.Engine.Flydigi.FlydigiScreenFormat;

namespace PadForge.Common.Input.Flydigi
{
    /// <summary>
    /// The pictures PadForge makes for a Flydigi Apex screen (#586): a
    /// picture or an animated GIF from a file, the active profile's picture,
    /// or its name or number drawn as text, each fitted to 160 by 80 and
    /// encoded as the pad takes it (<see cref="FlydigiScreenFormat"/>). WPF
    /// decodes and draws, so every call here runs on the UI thread.
    /// </summary>
    internal static class FlydigiScreenPictures
    {
        /// <summary>The time a still picture's one frame is given.</summary>
        public const int StillIntervalMs = 100;

        /// <summary>The time a GIF frame with no delay of its own shows,
        /// 10 hundredths, as browsers take a delay of 0 or 1.</summary>
        public const int DefaultGifDelayMs = 100;

        /// <summary>A picture or GIF file as the pad's frames, at most
        /// <paramref name="maximumFrames"/> of them. A GIF's frames are
        /// drawn over one another as the file says, and their time is the
        /// first frame's delay. Null when the file is not a picture WPF
        /// decodes. <paramref name="cut"/> says how many frames past the
        /// limit were left out.</summary>
        public static FlydigiScreenPicture FromFile(string path, FlydigiScreenFit fit, int maximumFrames, out int cut)
        {
            cut = 0;
            byte[] bytes;
            try { bytes = MenuIconResolver.ReadCapped(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or System.Security.SecurityException)
            {
                return null;
            }
            return FromBytes(bytes, fit, maximumFrames, out cut);
        }

        public static FlydigiScreenPicture FromBytes(byte[] bytes, FlydigiScreenFit fit, int maximumFrames, out int cut)
        {
            cut = 0;
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) return null;
                if (decoder is GifBitmapDecoder && decoder.Frames.Count > 1)
                    return FromGif(decoder, fit, maximumFrames, out cut);
                var frame = Encode(decoder.Frames[0], fit);
                return new FlydigiScreenPicture(new[] { frame }, StillIntervalMs);
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException
                or InvalidOperationException or OverflowException or IOException
                or System.Runtime.InteropServices.COMException or OutOfMemoryException)
            {
                return null;
            }
        }

        /// <summary>A GIF's frames drawn in turn on a canvas of the file's
        /// own size, each at its place, with the canvas cleared to black
        /// where a frame says to restore the background before the next
        /// (GIF89a disposal method 2). The delays come from each frame's
        /// graphic control extension in hundredths of a second, and the
        /// pad takes one time for all frames, so the first frame's is
        /// used.</summary>
        private static FlydigiScreenPicture FromGif(BitmapDecoder decoder, FlydigiScreenFit fit, int maximumFrames, out int cut)
        {
            int count = Math.Min(decoder.Frames.Count, Math.Max(1, maximumFrames));
            cut = decoder.Frames.Count - count;
            int width = ReadUShort(decoder.Metadata as BitmapMetadata, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
            int height = ReadUShort(decoder.Metadata as BitmapMetadata, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var frames = new List<byte[]>(count);
            int interval = DefaultGifDelayMs;
            var canvas = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            for (int i = 0; i < count; i++)
            {
                var source = decoder.Frames[i];
                var metadata = source.Metadata as BitmapMetadata;
                int left = ReadUShort(metadata, "/imgdesc/Left") ?? 0;
                int top = ReadUShort(metadata, "/imgdesc/Top") ?? 0;
                int delay = (ReadUShort(metadata, "/grctlext/Delay") ?? 0) * 10;
                int disposal = ReadByte(metadata, "/grctlext/Disposal") ?? 0;
                if (i == 0 && delay > 10) interval = delay;

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    if (i == 0) dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                    dc.DrawImage(source, new Rect(left, top, source.PixelWidth, source.PixelHeight));
                }
                canvas.Render(visual);
                frames.Add(Encode(canvas, fit));

                if (disposal == 2)
                {
                    var clear = new DrawingVisual();
                    using (var dc = clear.RenderOpen())
                        dc.DrawRectangle(Brushes.Black, null, new Rect(left, top, source.PixelWidth, source.PixelHeight));
                    canvas.Render(clear);
                }
            }
            return new FlydigiScreenPicture(frames, interval);
        }

        /// <summary>A picture already in memory, as one frame.</summary>
        public static FlydigiScreenPicture FromBitmap(BitmapSource picture, FlydigiScreenFit fit)
            => picture == null ? null : new FlydigiScreenPicture(new[] { Encode(picture, fit) }, StillIntervalMs);

        /// <summary>Text drawn as large as fits, white on black, on as many
        /// lines as its words need, centered, the color screens' rule
        /// (KeyboardScreenRenderer.RenderText).</summary>
        public static FlydigiScreenPicture FromText(string text)
        {
            text = (text ?? string.Empty).Trim();
            int margin = Math.Max(4, Format.Width / 20);
            double maxWidth = Format.Width - 2 * margin, maxHeight = Format.Height - 2 * margin;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            FormattedText fitted = null;
            for (double size = maxHeight; size >= 6; size -= 1)
            {
                var candidate = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, size, Brushes.White, 1.0)
                {
                    MaxTextWidth = maxWidth,
                    TextAlignment = TextAlignment.Center,
                };
                fitted = candidate;
                if (candidate.MinWidth <= maxWidth && candidate.Height <= maxHeight) break;
            }
            var visual = new DrawingVisual();
            TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, Format.Width, Format.Height));
                if (fitted != null && text.Length > 0)
                    dc.DrawText(fitted, new Point(margin, margin + Math.Max(0, (maxHeight - fitted.Height) / 2)));
            }
            var target = new RenderTargetBitmap(Format.Width, Format.Height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            return new FlydigiScreenPicture(new[] { Format.EncodeFrame(Rgb(target)) }, StillIntervalMs);
        }

        /// <summary>One picture as one frame: its pixels over black, then
        /// fitted by <see cref="FlydigiScreenFormat.Fit"/>.</summary>
        private static byte[] Encode(BitmapSource picture, FlydigiScreenFit fit)
        {
            var rgb = Rgb(picture);
            return Format.EncodeFrame(Format.Fit(rgb, picture.PixelWidth, picture.PixelHeight, fit));
        }

        /// <summary>A picture's pixels as RGB, three bytes each, row by row,
        /// with its transparency laid over black.</summary>
        internal static byte[] Rgb(BitmapSource picture)
        {
            var bgra = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);
            var rgb = new byte[width * height * 3];
            for (int i = 0, o = 0; i < pixels.Length; i += 4, o += 3)
            {
                int alpha = pixels[i + 3];
                rgb[o] = (byte)(pixels[i + 2] * alpha / 255);
                rgb[o + 1] = (byte)(pixels[i + 1] * alpha / 255);
                rgb[o + 2] = (byte)(pixels[i] * alpha / 255);
            }
            return rgb;
        }

        /// <summary>A frame the pad would get, back as a picture for the
        /// preview: what the dialog shows is the bytes that go out.</summary>
        public static BitmapSource Preview(byte[] frame)
        {
            var rgb = Format.DecodeFrame(frame);
            var bitmap = BitmapSource.Create(Format.Width, Format.Height, 96, 96, PixelFormats.Rgb24, null, rgb, Format.Width * 3);
            bitmap.Freeze();
            return bitmap;
        }

        private static int? ReadUShort(BitmapMetadata metadata, string query)
        {
            try
            {
                return metadata?.GetQuery(query) switch
                {
                    ushort value => value,
                    byte value => value,
                    short value => value,
                    int value => value,
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException
                or System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }

        private static int? ReadByte(BitmapMetadata metadata, string query) => ReadUShort(metadata, query);
    }
}
