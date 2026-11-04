using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The baseline JPEG writer OpenRGB draws a Stream Deck key's color
    /// with, stb_image_write v1.16's stbi_write_jpg_to_func (OpenRGB
    /// dependencies/stb/stb_image_write.h:1242-1612, based on Jon Olick's
    /// jo_jpeg, public domain), ported line for line for the quality the
    /// Stream Deck family uses, 95, which writes no chroma subsampling
    /// (line 1479), so the bytes match OpenRGB's. The image is RGB, three
    /// bytes a pixel, row by row, not flipped.
    /// </summary>
    internal static class StreamDeckJpeg
    {
        private static readonly byte[] ZigZag =
        {
            0, 1, 5, 6, 14, 15, 27, 28, 2, 4, 7, 13, 16, 26, 29, 42, 3, 8, 12, 17, 25, 30, 41, 43, 9, 11, 18,
            24, 31, 40, 44, 53, 10, 19, 23, 32, 39, 45, 52, 54, 20, 22, 33, 38, 46, 51, 55, 60, 21, 34, 37, 47, 50, 56, 59, 61, 35, 36, 48, 49, 57, 58, 62, 63,
        };

        private static readonly byte[] DcLuminanceCodes = { 0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] DcLuminanceValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        private static readonly byte[] AcLuminanceCodes = { 0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
        private static readonly byte[] AcLuminanceValues =
        {
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
            0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
            0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
            0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
            0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
        };
        private static readonly byte[] DcChrominanceCodes = { 0, 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
        private static readonly byte[] DcChrominanceValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
        private static readonly byte[] AcChrominanceCodes = { 0, 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
        private static readonly byte[] AcChrominanceValues =
        {
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
            0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
            0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
            0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
            0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
            0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
        };

        /// <summary>The code and bit length of each Huffman symbol (YDC_HT,
        /// UVDC_HT, YAC_HT, UVAC_HT, lines 1425-1462), built from the code
        /// lengths and values above, the standard JPEG tables those arrays
        /// spell out.</summary>
        private static readonly ushort[,] YDcTable = Huffman(DcLuminanceCodes, DcLuminanceValues);
        private static readonly ushort[,] UvDcTable = Huffman(DcChrominanceCodes, DcChrominanceValues);
        private static readonly ushort[,] YAcTable = Huffman(AcLuminanceCodes, AcLuminanceValues);
        private static readonly ushort[,] UvAcTable = Huffman(AcChrominanceCodes, AcChrominanceValues);

        private static readonly int[] YQuant =
        {
            16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62, 18, 22,
            37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
        };
        private static readonly int[] UvQuant =
        {
            17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        };
        private static readonly float[] Aasf =
        {
            1.0f * 2.828427125f, 1.387039845f * 2.828427125f, 1.306562965f * 2.828427125f, 1.175875602f * 2.828427125f,
            1.0f * 2.828427125f, 0.785694958f * 2.828427125f, 0.541196100f * 2.828427125f, 0.275899379f * 2.828427125f,
        };

        /// <summary>The canonical Huffman codes of a JPEG table: codes of
        /// each length counted up from the last, shifted left one bit per
        /// length, indexed by symbol. A symbol the table has no code for
        /// stays 0, 0, as in stb's arrays.</summary>
        private static ushort[,] Huffman(byte[] counts, byte[] values)
        {
            var table = new ushort[256, 2];
            int code = 0, k = 0;
            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < counts[length]; i++, k++, code++)
                {
                    table[values[k], 0] = (ushort)code;
                    table[values[k], 1] = (ushort)length;
                }
                code <<= 1;
            }
            return table;
        }

        /// <summary>stbi_write_jpg_to_func with three components at
        /// <paramref name="quality"/> above 90, so no subsampling.</summary>
        public static byte[] Write(int width, int height, byte[] rgb, int quality)
        {
            var output = new List<byte>(1024);
            // Lines 1478-1495.
            quality = quality < 1 ? 1 : quality > 100 ? 100 : quality;
            quality = quality < 50 ? 5000 / quality : 200 - quality * 2;
            var yTable = new byte[64];
            var uvTable = new byte[64];
            for (int i = 0; i < 64; ++i)
            {
                int yti = (YQuant[i] * quality + 50) / 100;
                yTable[ZigZag[i]] = (byte)(yti < 1 ? 1 : yti > 255 ? 255 : yti);
                int uvti = (UvQuant[i] * quality + 50) / 100;
                uvTable[ZigZag[i]] = (byte)(uvti < 1 ? 1 : uvti > 255 ? 255 : uvti);
            }
            var fdtblY = new float[64];
            var fdtblUv = new float[64];
            for (int row = 0, k = 0; row < 8; ++row)
            {
                for (int col = 0; col < 8; ++col, ++k)
                {
                    fdtblY[k] = 1 / (yTable[ZigZag[k]] * Aasf[row] * Aasf[col]);
                    fdtblUv[k] = 1 / (uvTable[ZigZag[k]] * Aasf[row] * Aasf[col]);
                }
            }

            // The headers, lines 1497-1520, at subsample 0.
            output.AddRange(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xDB, 0, 0x84, 0 });
            output.AddRange(yTable);
            output.Add(1);
            output.AddRange(uvTable);
            output.AddRange(new byte[]
            {
                0xFF, 0xC0, 0, 0x11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
                3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xC4, 0x01, 0xA2, 0,
            });
            output.AddRange(DcLuminanceCodes[1..]);
            output.AddRange(DcLuminanceValues);
            output.Add(0x10);
            output.AddRange(AcLuminanceCodes[1..]);
            output.AddRange(AcLuminanceValues);
            output.Add(1);
            output.AddRange(DcChrominanceCodes[1..]);
            output.AddRange(DcChrominanceValues);
            output.Add(0x11);
            output.AddRange(AcChrominanceCodes[1..]);
            output.AddRange(AcChrominanceValues);
            output.AddRange(new byte[] { 0xFF, 0xDA, 0, 0xC, 3, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0 });

            // The 8 by 8 blocks, lines 1522-1598, without subsampling.
            int dcY = 0, dcU = 0, dcV = 0;
            int bitBuf = 0, bitCnt = 0;
            var y = new float[64];
            var u = new float[64];
            var v = new float[64];
            for (int top = 0; top < height; top += 8)
            {
                for (int left = 0; left < width; left += 8)
                {
                    int pos = 0;
                    for (int row = top; row < top + 8; ++row)
                    {
                        int clampedRow = row < height ? row : height - 1;
                        int baseP = clampedRow * width * 3;
                        for (int col = left; col < left + 8; ++col, ++pos)
                        {
                            int p = baseP + (col < width ? col : width - 1) * 3;
                            float r = rgb[p], g = rgb[p + 1], b = rgb[p + 2];
                            y[pos] = +0.29900f * r + 0.58700f * g + 0.11400f * b - 128;
                            u[pos] = -0.16874f * r - 0.33126f * g + 0.50000f * b;
                            v[pos] = +0.50000f * r - 0.41869f * g - 0.08131f * b;
                        }
                    }
                    dcY = ProcessDu(output, ref bitBuf, ref bitCnt, y, fdtblY, dcY, YDcTable, YAcTable);
                    dcU = ProcessDu(output, ref bitBuf, ref bitCnt, u, fdtblUv, dcU, UvDcTable, UvAcTable);
                    dcV = ProcessDu(output, ref bitBuf, ref bitCnt, v, fdtblUv, dcV, UvDcTable, UvAcTable);
                }
            }
            WriteBits(output, ref bitBuf, ref bitCnt, 0x7F, 7);
            output.Add(0xFF);
            output.Add(0xD9);
            return output.ToArray();
        }

        /// <summary>stbiw__jpg_writeBits (lines 1253-1268).</summary>
        private static void WriteBits(List<byte> output, ref int bitBuf, ref int bitCnt, int code, int length)
        {
            bitCnt += length;
            bitBuf |= code << (24 - bitCnt);
            while (bitCnt >= 8)
            {
                byte c = (byte)((bitBuf >> 16) & 255);
                output.Add(c);
                if (c == 255) output.Add(0);
                bitBuf <<= 8;
                bitCnt -= 8;
            }
        }

        /// <summary>stbiw__jpg_DCT on eight values of <paramref name="d"/>,
        /// <paramref name="stride"/> apart from <paramref name="at"/>
        /// (lines 1270-1316).</summary>
        private static void Dct(float[] d, int at, int stride)
        {
            float d0 = d[at], d1 = d[at + stride], d2 = d[at + stride * 2], d3 = d[at + stride * 3];
            float d4 = d[at + stride * 4], d5 = d[at + stride * 5], d6 = d[at + stride * 6], d7 = d[at + stride * 7];

            float tmp0 = d0 + d7;
            float tmp7 = d0 - d7;
            float tmp1 = d1 + d6;
            float tmp6 = d1 - d6;
            float tmp2 = d2 + d5;
            float tmp5 = d2 - d5;
            float tmp3 = d3 + d4;
            float tmp4 = d3 - d4;

            float tmp10 = tmp0 + tmp3;
            float tmp13 = tmp0 - tmp3;
            float tmp11 = tmp1 + tmp2;
            float tmp12 = tmp1 - tmp2;

            d0 = tmp10 + tmp11;
            d4 = tmp10 - tmp11;

            float z1 = (tmp12 + tmp13) * 0.707106781f;
            d2 = tmp13 + z1;
            d6 = tmp13 - z1;

            tmp10 = tmp4 + tmp5;
            tmp11 = tmp5 + tmp6;
            tmp12 = tmp6 + tmp7;

            float z5 = (tmp10 - tmp12) * 0.382683433f;
            float z2 = tmp10 * 0.541196100f + z5;
            float z4 = tmp12 * 1.306562965f + z5;
            float z3 = tmp11 * 0.707106781f;

            float z11 = tmp7 + z3;
            float z13 = tmp7 - z3;

            d[at + stride * 5] = z13 + z2;
            d[at + stride * 3] = z13 - z2;
            d[at + stride] = z11 + z4;
            d[at + stride * 7] = z11 - z4;

            d[at] = d0;
            d[at + stride * 2] = d2;
            d[at + stride * 4] = d4;
            d[at + stride * 6] = d6;
        }

        /// <summary>stbiw__jpg_calcBits (lines 1318-1326).</summary>
        private static (int Code, int Length) CalcBits(int val)
        {
            int tmp1 = val < 0 ? -val : val;
            val = val < 0 ? val - 1 : val;
            int length = 1;
            while ((tmp1 >>= 1) != 0) ++length;
            return ((ushort)(val & ((1 << length) - 1)), length);
        }

        /// <summary>stbiw__jpg_processDU for an 8 by 8 block with a stride
        /// of 8 (lines 1328-1396).</summary>
        private static int ProcessDu(List<byte> output, ref int bitBuf, ref int bitCnt, float[] cdu, float[] fdtbl, int dc,
            ushort[,] htdc, ushort[,] htac)
        {
            var du = new int[64];
            for (int dataOff = 0; dataOff < 64; dataOff += 8) Dct(cdu, dataOff, 1);
            for (int dataOff = 0; dataOff < 8; ++dataOff) Dct(cdu, dataOff, 8);
            for (int j = 0; j < 64; ++j)
            {
                float value = cdu[j] * fdtbl[j];
                du[ZigZag[j]] = (int)(value < 0 ? value - 0.5f : value + 0.5f);
            }

            int diff = du[0] - dc;
            if (diff == 0)
            {
                WriteBits(output, ref bitBuf, ref bitCnt, htdc[0, 0], htdc[0, 1]);
            }
            else
            {
                var (code, length) = CalcBits(diff);
                WriteBits(output, ref bitBuf, ref bitCnt, htdc[length, 0], htdc[length, 1]);
                WriteBits(output, ref bitBuf, ref bitCnt, code, length);
            }

            int end0pos = 63;
            for (; end0pos > 0 && du[end0pos] == 0; --end0pos) { }
            if (end0pos == 0)
            {
                WriteBits(output, ref bitBuf, ref bitCnt, htac[0x00, 0], htac[0x00, 1]);
                return du[0];
            }
            for (int i = 1; i <= end0pos; ++i)
            {
                int startpos = i;
                for (; du[i] == 0 && i <= end0pos; ++i) { }
                int nrzeroes = i - startpos;
                if (nrzeroes >= 16)
                {
                    int lng = nrzeroes >> 4;
                    for (int nrmarker = 1; nrmarker <= lng; ++nrmarker)
                        WriteBits(output, ref bitBuf, ref bitCnt, htac[0xF0, 0], htac[0xF0, 1]);
                    nrzeroes &= 15;
                }
                var (code, length) = CalcBits(du[i]);
                WriteBits(output, ref bitBuf, ref bitCnt, htac[(nrzeroes << 4) + length, 0], htac[(nrzeroes << 4) + length, 1]);
                WriteBits(output, ref bitBuf, ref bitCnt, code, length);
            }
            if (end0pos != 63) WriteBits(output, ref bitBuf, ref bitCnt, htac[0x00, 0], htac[0x00, 1]);
            return du[0];
        }
    }
}
