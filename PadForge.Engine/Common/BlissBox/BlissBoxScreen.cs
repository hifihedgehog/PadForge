using System;
using System.Buffers.Binary;

namespace PadForge.Engine.Common.BlissBox
{
    /// <summary>
    /// The VMU picture a Bliss-Box shows on a Dreamcast pad (issue #469):
    /// 48 by 32 pixels, one bit each.
    ///
    /// <para>PadForge keeps a picture in image order: rows top to bottom,
    /// 6 bytes a row, the leftmost pixel in each byte's high bit, 1 for a dark
    /// pixel. The adapter takes it rotated 180 degrees, because the VMU sits
    /// upside down in the pad: byte k of the image, bit-reversed, is byte
    /// 191 - k on the wire. That is dLCD.cs writeToLCD (1bpp scan lines
    /// inverted, each byte bit-reversed, the order reversed) and ByteToImage
    /// the other way, and DeviceBuddy draws report 24's pixel (x, y) at
    /// (47 - x, 31 - y). The transform is its own inverse.</para>
    /// </summary>
    public static class BlissBoxScreen
    {
        public const int Width = 48;
        public const int Height = 32;
        public const int Bytes = BlissBoxProtocol.ScreenBytes;
        public const int RowBytes = Width / 8;

        /// <summary>A VMU Animator .lcd file: a 16-byte header, 4 bytes of
        /// frame information, then one byte per pixel in image order, 0x08
        /// for a dark one (dLCD.cs setImage reads the first frame so).</summary>
        public const int LcdPixelOffset = 20;
        public const byte LcdDark = 0x08;

        /// <summary>An ICONDATA_VMS file keeps the offset of its 32 by 32
        /// monochrome icon at byte 16 (dLCD.cs setImage), 128 bytes, one bit a
        /// pixel, the leftmost in the high bit, 1 dark.</summary>
        public const int VmsIconOffsetField = 16;
        public const int VmsIconSize = 32;
        public const int VmsIconBytes = VmsIconSize * VmsIconSize / 8;

        /// <summary>The wire bytes for an image, or the image for wire bytes.</summary>
        public static byte[] Rotate(ReadOnlySpan<byte> source)
        {
            if (source.Length != Bytes)
                throw new ArgumentException($"A VMU picture is {Bytes} bytes.", nameof(source));
            var result = new byte[Bytes];
            for (int k = 0; k < Bytes; k++)
                result[Bytes - 1 - k] = ReverseBits(source[k]);
            return result;
        }

        public static byte[] ToWire(ReadOnlySpan<byte> image) => Rotate(image);

        public static byte[] FromWire(ReadOnlySpan<byte> wire) => Rotate(wire);

        public static byte ReverseBits(byte b)
        {
            b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
            b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
            return (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
        }

        public static bool IsDark(ReadOnlySpan<byte> image, int x, int y)
            => (image[y * RowBytes + x / 8] & (0x80 >> (x % 8))) != 0;

        public static void SetDark(Span<byte> image, int x, int y, bool dark)
        {
            int index = y * RowBytes + x / 8;
            byte mask = (byte)(0x80 >> (x % 8));
            if (dark) image[index] |= mask;
            else image[index] &= (byte)~mask;
        }

        /// <summary>The first frame of a VMU Animator .lcd file, in image
        /// order, or null when the file is too short to hold one.</summary>
        public static byte[] FromLcd(ReadOnlySpan<byte> file)
        {
            if (file.Length < LcdPixelOffset + Width * Height) return null;
            var image = new byte[Bytes];
            for (int p = 0; p < Width * Height; p++)
                if (file[LcdPixelOffset + p] == LcdDark)
                    image[p / 8] |= (byte)(0x80 >> (p % 8));
            return image;
        }

        /// <summary>The monochrome icon of an ICONDATA_VMS file, centered on
        /// the screen with 8 blank pixels either side, or null when the file
        /// has no icon where its header says. The offset is read as the
        /// format's 32-bit little-endian field, where dLCD.cs reads its low
        /// byte.</summary>
        public static byte[] FromVms(ReadOnlySpan<byte> file)
        {
            if (file.Length < VmsIconOffsetField + 4) return null;
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(VmsIconOffsetField, 4));
            if (offset == 0 || offset > (uint)(file.Length - VmsIconBytes)) return null;
            var image = new byte[Bytes];
            var icon = file.Slice((int)offset, VmsIconBytes);
            for (int row = 0; row < VmsIconSize; row++)
                icon.Slice(row * 4, 4).CopyTo(image.AsSpan(row * RowBytes + 1, 4));
            return image;
        }
    }
}
