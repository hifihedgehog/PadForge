using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace PadForge.Common
{
    /// <summary>
    /// Decodes a picture to fit a box of pixels, whatever its shape, into a
    /// bitmap that holds nothing but its own pixels.
    ///
    /// <para>DecodePixelWidth alone lets the height follow the aspect ratio,
    /// so a tall picture decodes tall: a 227-byte PNG one pixel wide and
    /// 20,000 high came out 80 by 1,600,000 at a width of 80, 512 MB of
    /// memory, and one 60,000 high threw OverflowException at a width of 256.
    /// The size is read from the header first and both decode dimensions are
    /// set from it, worked out in 64-bit arithmetic, since WPF's own
    /// calculation of the missing one multiplies in 32 bits. The bound is on
    /// the bitmap returned: reading the header still sets up the decoder,
    /// which lists every frame of a multi-frame picture, and the callers'
    /// 16 MB read bound is what limits that.</para>
    ///
    /// <para>A picture wider than the box's shape decodes as a width bound
    /// always decoded it. A taller one now fits the box's height instead: in a
    /// square box a 100 by 200 picture comes out 40 by 80 at a box of 80,
    /// where it came out 80 by 160.</para>
    ///
    /// <para>The pixels are copied out of the decoded image, because a
    /// BitmapImage built on a stream keeps that stream, and the stream keeps
    /// the picture's encoded bytes, for as long as the image lives. A cached
    /// icon held its whole file that way.</para>
    /// </summary>
    internal static class BoundedBitmap
    {
        /// <summary>The picture in <paramref name="bytes"/>, decoded no wider
        /// than <paramref name="maxWidth"/> and no taller than
        /// <paramref name="maxHeight"/>, and frozen. Null when the bytes hold
        /// no picture WPF decodes. Never throws for a bad picture.</summary>
        internal static BitmapSource FromBytes(byte[] bytes, int maxWidth, int maxHeight)
        {
            if (bytes == null || bytes.Length == 0 || maxWidth <= 0 || maxHeight <= 0) return null;
            try
            {
                long width, height;
                using (var header = new MemoryStream(bytes, writable: false))
                {
                    var frames = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation,
                        BitmapCacheOption.None).Frames;
                    if (frames.Count == 0) return null;
                    width = frames[0].PixelWidth;
                    height = frames[0].PixelHeight;
                }
                if (width <= 0 || height <= 0) return null;

                // Taller than the box's shape: the height binds and the width
                // follows. Otherwise the width binds, as it always did.
                long decodeWidth, decodeHeight;
                if (height * maxWidth > width * maxHeight)
                {
                    decodeHeight = maxHeight;
                    decodeWidth = Math.Max(1, width * maxHeight / height);
                }
                else
                {
                    decodeWidth = maxWidth;
                    decodeHeight = Math.Max(1, height * maxWidth / width);
                }

                var img = new BitmapImage();
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    img.BeginInit();
                    img.StreamSource = stream;
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.DecodePixelWidth = (int)decodeWidth;
                    img.DecodePixelHeight = (int)decodeHeight;
                    img.EndInit();
                }

                int stride = (img.PixelWidth * img.Format.BitsPerPixel + 7) / 8;
                var pixels = new byte[(long)stride * img.PixelHeight];
                img.CopyPixels(pixels, stride, 0);
                var copy = BitmapSource.Create(img.PixelWidth, img.PixelHeight, img.DpiX, img.DpiY,
                    img.Format, img.Palette, pixels, stride);
                copy.Freeze();
                return copy;
            }
            // FileFormatException is what WPF raises for a truncated or corrupt
            // picture, OverflowException what it raised for a decode size past
            // its limit, OutOfMemoryException what it makes of WIC's own
            // out-of-memory result, and COMException (an ExternalException) what
            // it makes of a WIC result it has no mapping for.
            catch (Exception ex) when (ex is IOException or NotSupportedException
                or ArgumentException or InvalidOperationException or FileFormatException
                or OverflowException or OutOfMemoryException
                or System.Runtime.InteropServices.ExternalException)
            {
                return null;
            }
        }
    }
}
