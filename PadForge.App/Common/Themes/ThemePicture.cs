using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace PadForge.Common.Themes
{
    /// <summary>
    /// Makes a theme's picture from any wide picture (#591): a Steam game's
    /// library art or a file of the user's. A theme's picture is 1176 by
    /// 240 units with a clear ground, drawn behind the top of the page, so
    /// the source is cut to that window, faded in from its left and out at
    /// its foot, toned toward the theme's background, and given the game's
    /// logo at its left when there is one.
    ///
    /// The numbers are the starters' own: their pictures stand in the right
    /// 72 hundredths of the window, clear of the page's title, with the logo
    /// between the title and the picture.
    /// </summary>
    internal static class ThemePicture
    {
        /// <summary>The picture's size in pixels: twice its units.</summary>
        internal const int Width = ThemeDefinition.PictureWidth, Height = ThemeDefinition.PictureHeight;

        /// <summary>The share of the window the source picture fills, from its right edge.</summary>
        internal const double PhotoShare = 0.72;

        /// <summary>The share of the photo's width it fades in over, and of the window's height it fades out over.</summary>
        internal const double FadeIn = 0.28, FadeOut = 0.20;

        /// <summary>How far the photo is toned toward the theme's background, of 255: a little on a dark ground, more on a light one, where dark text is read over it.</summary>
        internal const byte ToneDark = 40, ToneLight = 150;

        /// <summary>The logo's box and its middle, in pixels.</summary>
        internal const double LogoWidth = 300, LogoHeight = 116, LogoCenterX = 940, LogoCenterY = 84;

        /// <summary>
        /// The picture for a theme. <paramref name="focus"/> is where the
        /// window sits down a source taller than it: 0 its top, 1 its foot.
        /// Runs on a thread that can draw (the UI thread).
        /// </summary>
        internal static BitmapSource Compose(BitmapSource photo, BitmapSource logo, Color ground, bool dark, double focus)
        {
            if (photo == null) return null;
            focus = Math.Clamp(double.IsNaN(focus) ? 0.5 : focus, 0, 1);
            double boxLeft = Width * (1 - PhotoShare), boxWidth = Width * PhotoShare;
            // The source covers its box: as wide as the box, or as tall as the window when that is more.
            double scale = Math.Max(boxWidth / photo.PixelWidth, (double)Height / photo.PixelHeight);
            double drawnWidth = photo.PixelWidth * scale, drawnHeight = photo.PixelHeight * scale;
            double x = boxLeft + (boxWidth - drawnWidth) / 2;
            double y = -(drawnHeight - Height) * focus;

            var across = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(boxLeft, 0),
                EndPoint = new Point(boxLeft + boxWidth * FadeIn, 0),
            };
            across.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            across.GradientStops.Add(new GradientStop(Colors.Black, 1));
            across.Freeze();
            var down = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, Height * (1 - FadeOut)),
                EndPoint = new Point(0, Height),
            };
            down.GradientStops.Add(new GradientStop(Colors.Black, 0));
            down.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            down.Freeze();
            var tone = new SolidColorBrush(Color.FromArgb(dark ? ToneDark : ToneLight, ground.R, ground.G, ground.B));
            tone.Freeze();

            var root = new ContainerVisual();
            var picture = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            var box = new Rect(boxLeft, 0, boxWidth, Height);
            using (var dc = picture.RenderOpen())
            {
                dc.PushOpacityMask(across);
                dc.PushOpacityMask(down);
                dc.PushClip(new RectangleGeometry(box));
                dc.DrawImage(photo, new Rect(x, y, drawnWidth, drawnHeight));
                dc.DrawRectangle(tone, null, box);
                dc.Pop();
                dc.Pop();
                dc.Pop();
            }
            root.Children.Add(picture);

            if (logo != null && logo.PixelWidth > 0 && logo.PixelHeight > 0)
            {
                double fit = Math.Min(LogoWidth / logo.PixelWidth, LogoHeight / logo.PixelHeight);
                double w = logo.PixelWidth * fit, h = logo.PixelHeight * fit;
                var mark = new DrawingVisual
                {
                    // A soft dark ground under the logo, so it reads where the picture begins.
                    Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 28, ShadowDepth = 0, Opacity = dark ? 0.85 : 0.35 },
                };
                RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
                using (var dc = mark.RenderOpen())
                    dc.DrawImage(logo, new Rect(LogoCenterX - w / 2, LogoCenterY - h / 2, w, h));
                root.Children.Add(mark);
            }

            var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>The picture as a PNG, which keeps its clear ground.</summary>
        internal static byte[] EncodePng(BitmapSource picture)
        {
            if (picture == null) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(picture));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.ToArray();
        }

        /// <summary>A source picture from a file's bytes, decoded no
        /// larger than twice the window, or null when they hold none.</summary>
        internal static BitmapSource DecodeSource(byte[] bytes)
            => BoundedBitmap.FromBytes(bytes, Width * 2, Height * 4, scaleUp: false);
    }
}
