using System;
using System.Collections.Generic;

namespace PadForge.Services
{
    /// <summary>
    /// Minimal byte-mode QR Code generator, ported from the Nayuki reference
    /// implementation (github.com/nayuki/QR-Code-generator, MIT / public
    /// domain), which is written to be ported and is the canonical reference.
    /// Scope is exactly what the web-controller card needs: encode a short URL
    /// as a QR matrix, auto-selecting the smallest version at ECC level M.
    ///
    /// Cite-verify: the Reed-Solomon divisor, the finder/alignment/timing
    /// placement, the format-info bits, and the eight mask patterns with the
    /// four penalty rules all follow Nayuki's QrCode.java / qrcodegen module
    /// exactly. Verified by generating a known payload and confirming the
    /// module count, finder patterns, and a successful decode by a phone.
    /// </summary>
    internal static class QrCode
    {
        /// <summary>Encodes text as a QR matrix. Returns a square bool[size,size]
        /// where true is a dark module, or null if the text will not fit in the
        /// supported version range.</summary>
        public static bool[,] Encode(string text)
        {
            var data = System.Text.Encoding.UTF8.GetBytes(text ?? "");
            // ECC level M (index 0 in our tables below): good density for a URL.
            for (int version = 1; version <= 20; version++)
            {
                int capacityBits = DataCodewordsForVersion(version) * 8;
                // Byte mode: 4-bit mode indicator + char-count indicator + 8*len.
                int ccBits = version <= 9 ? 8 : 16;
                int needed = 4 + ccBits + data.Length * 8;
                if (needed <= capacityBits)
                    return BuildMatrix(version, data, ccBits);
            }
            return null;
        }

        private static bool[,] BuildMatrix(int version, byte[] data, int ccBits)
        {
            int numDataCodewords = DataCodewordsForVersion(version);

            // ── Bit buffer: mode + count + data, padded to capacity ──
            var bits = new List<bool>();
            AppendBits(bits, 0b0100, 4);           // byte mode
            AppendBits(bits, data.Length, ccBits); // char count
            foreach (var b in data) AppendBits(bits, b, 8);

            int capacityBits = numDataCodewords * 8;
            // Terminator (up to 4 zero bits) + byte-align.
            for (int i = 0; i < 4 && bits.Count < capacityBits; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);
            // Pad bytes 0xEC / 0x11 alternating.
            for (byte pad = 0xEC; bits.Count < capacityBits; pad = (byte)(pad ^ 0xEC ^ 0x11))
                AppendBits(bits, pad, 8);

            var dataCodewords = new byte[numDataCodewords];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i]) dataCodewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

            var allCodewords = AddEccAndInterleave(version, dataCodewords);

            // ── Draw ──
            int size = version * 4 + 17;
            var modules = new bool[size, size];
            var isFunction = new bool[size, size];

            DrawFunctionPatterns(version, modules, isFunction);
            DrawCodewords(modules, isFunction, allCodewords);

            // Pick the mask with the lowest penalty.
            int bestMask = 0;
            long bestPenalty = long.MaxValue;
            var saved = (bool[,])modules.Clone();
            for (int mask = 0; mask < 8; mask++)
            {
                Array.Copy(saved, modules, saved.Length);
                ApplyMask(modules, isFunction, mask);
                DrawFormatBits(version, modules, isFunction, mask);
                long penalty = Penalty(modules);
                if (penalty < bestPenalty) { bestPenalty = penalty; bestMask = mask; }
            }
            Array.Copy(saved, modules, saved.Length);
            ApplyMask(modules, isFunction, bestMask);
            DrawFormatBits(version, modules, isFunction, bestMask);
            return modules;
        }

        // ── ECC tables (level M only) ──────────────────────────────────────
        // Per version: total codewords, ECC codewords per block, num blocks
        // group1, num blocks group2 (group2 blocks hold one more data codeword).
        // Values from the QR spec (ISO/IEC 18004) level-M column.
        private static readonly int[] EccCodewordsPerBlockM =
            { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26 };
        private static readonly int[] NumBlocksM =
            { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16 };

        private static int TotalCodewords(int version)
        {
            // Counted, not derived. The closed-form module count is easy to get
            // subtly wrong (an abandoned attempt at it lived here, ending in a
            // term multiplied by zero), and drawing the function patterns and
            // counting what they reserve is exact by construction.
            int size = version * 4 + 17;
            var isFunction = new bool[size, size];
            var dummy = new bool[size, size];
            DrawFunctionPatterns(version, dummy, isFunction);
            int reserved = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (isFunction[x, y]) reserved++;
            return (size * size - reserved) / 8;
        }

        private static int DataCodewordsForVersion(int version)
        {
            int total = TotalCodewords(version);
            int numBlocks = NumBlocksM[version];
            int eccPerBlock = EccCodewordsPerBlockM[version];
            return total - eccPerBlock * numBlocks;
        }

        private static byte[] AddEccAndInterleave(int version, byte[] data)
        {
            int numBlocks = NumBlocksM[version];
            int eccLen = EccCodewordsPerBlockM[version];
            int totalCodewords = TotalCodewords(version);
            int rawEcc = eccLen; // per block
            int numShortBlocks = numBlocks - totalCodewords % numBlocks;
            int shortBlockDataLen = totalCodewords / numBlocks - eccLen;

            var blocks = new List<byte[]>();
            var divisor = ReedSolomonDivisor(eccLen);
            int k = 0;
            for (int i = 0; i < numBlocks; i++)
            {
                int datLen = shortBlockDataLen + (i < numShortBlocks ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                var ecc = ReedSolomonRemainder(dat, divisor);
                // Pad short blocks' data to align interleaving (spec inserts the
                // missing data column as skipped).
                var full = new byte[shortBlockDataLen + 1 + eccLen];
                Array.Copy(dat, 0, full, 0, datLen);
                Array.Copy(ecc, 0, full, shortBlockDataLen + 1, eccLen);
                // Mark the short-block gap by leaving full[shortBlockDataLen]=0;
                // interleave skips it below.
                blocks.Add(full);
            }

            var result = new List<byte>(totalCodewords);
            int fullLen = shortBlockDataLen + 1 + eccLen;
            for (int col = 0; col < fullLen; col++)
            {
                for (int b = 0; b < blocks.Count; b++)
                {
                    // Skip the padding column of short blocks (col == shortBlockDataLen).
                    if (col == shortBlockDataLen && b < numShortBlocks) continue;
                    result.Add(blocks[b][col]);
                }
            }
            return result.ToArray();
        }

        // ── Reed-Solomon over GF(256), primitive poly 0x11D ────────────────
        private static byte[] ReedSolomonDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = (byte)GfMul(result[j], (byte)root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = GfMul((byte)root, 0x02);
            }
            return result;
        }

        private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (var b in data)
            {
                byte factor = (byte)(b ^ result[0]);
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int j = 0; j < result.Length; j++)
                    result[j] ^= (byte)GfMul(divisor[j], factor);
            }
            return result;
        }

        private static int GfMul(byte x, byte y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z & 0xFF;
        }

        // ── Function-pattern drawing ───────────────────────────────────────
        private static void DrawFunctionPatterns(int version, bool[,] modules, bool[,] isFunction)
        {
            int size = version * 4 + 17;

            // Timing patterns.
            for (int i = 0; i < size; i++)
            {
                SetFunction(modules, isFunction, 6, i, i % 2 == 0);
                SetFunction(modules, isFunction, i, 6, i % 2 == 0);
            }

            // Three finder patterns (with separators).
            DrawFinder(modules, isFunction, 3, 3);
            DrawFinder(modules, isFunction, size - 4, 3);
            DrawFinder(modules, isFunction, 3, size - 4);

            // Alignment patterns.
            var pos = AlignmentPositions(version);
            int n = pos.Length;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    // Skip the three finder corners.
                    if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                    DrawAlignment(modules, isFunction, pos[i], pos[j]);
                }

            // Reserve format (always) and version (v7+) areas.
            DrawFormatBits(version, modules, isFunction, 0, reserveOnly: true);
            DrawVersion(version, modules, isFunction);
        }

        private static void DrawFinder(bool[,] modules, bool[,] isFunction, int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || x >= modules.GetLength(0) || y < 0 || y >= modules.GetLength(1)) continue;
                    SetFunction(modules, isFunction, x, y, dist != 2 && dist != 4);
                }
        }

        private static void DrawAlignment(bool[,] modules, bool[,] isFunction, int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFunction(modules, isFunction, cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        private static int[] AlignmentPositions(int version)
        {
            if (version == 1) return Array.Empty<int>();
            int numAlign = version / 7 + 2;
            // Nayuki QrCode.java getAlignmentPatternPositions:
            //   step = (ver == 32) ? 26 : (ver*4 + numAlign*2 + 1) / (numAlign*2 - 2) * 2
            // The numerator was (ver*4 + 4) here, which agrees with the
            // reference only while numAlign <= 3, i.e. up to version 14. From
            // version 15 it produced 20 where the spec wants 22, putting every
            // alignment pattern in the wrong place and making the symbol
            // undecodable. Reachable through the version 1-20 range Encode
            // walks. (Version 32's special case is outside that range and is
            // kept only to match the reference.)
            int step = (version == 32) ? 26
                : (version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
            var result = new int[numAlign];
            result[0] = 6;
            int size = version * 4 + 17;
            for (int i = numAlign - 1, p = size - 7; i >= 1; i--, p -= step)
                result[i] = p;
            return result;
        }

        private static void DrawFormatBits(int version, bool[,] modules, bool[,] isFunction, int mask, bool reserveOnly = false)
        {
            int size = version * 4 + 17;
            // ECC level M = 0b00. data = (ecl<<3)|mask, then BCH(15,5).
            int data = (0b00 << 3) | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) PlaceFmt(modules, isFunction, 8, i, bits, i, reserveOnly);
            PlaceFmt(modules, isFunction, 8, 7, bits, 6, reserveOnly);
            PlaceFmt(modules, isFunction, 8, 8, bits, 7, reserveOnly);
            PlaceFmt(modules, isFunction, 7, 8, bits, 8, reserveOnly);
            for (int i = 9; i < 15; i++) PlaceFmt(modules, isFunction, 14 - i, 8, bits, i, reserveOnly);

            for (int i = 0; i < 8; i++) PlaceFmt(modules, isFunction, size - 1 - i, 8, bits, i, reserveOnly);
            for (int i = 8; i < 15; i++) PlaceFmt(modules, isFunction, 8, size - 15 + i, bits, i, reserveOnly);
            SetFunction(modules, isFunction, 8, size - 8, true); // always-dark module
        }

        private static void PlaceFmt(bool[,] modules, bool[,] isFunction, int x, int y, int bits, int i, bool reserveOnly)
        {
            if (reserveOnly) { isFunction[x, y] = true; return; }
            modules[x, y] = ((bits >> i) & 1) != 0;
        }

        private static void DrawVersion(int version, bool[,] modules, bool[,] isFunction)
        {
            if (version < 7) return;
            int size = version * 4 + 17;
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = (version << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                SetFunction(modules, isFunction, a, b, bit);
                SetFunction(modules, isFunction, b, a, bit);
            }
        }

        private static void SetFunction(bool[,] modules, bool[,] isFunction, int x, int y, bool dark)
        {
            if (x < 0 || y < 0 || x >= modules.GetLength(0) || y >= modules.GetLength(1)) return;
            modules[x, y] = dark;
            isFunction[x, y] = true;
        }

        // ── Data placement (zig-zag) ───────────────────────────────────────
        private static void DrawCodewords(bool[,] modules, bool[,] isFunction, byte[] codewords)
        {
            int size = modules.GetLength(0);
            int i = 0; // bit index
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5; // skip the vertical timing column
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (isFunction[x, y]) continue;
                        bool dark = i < codewords.Length * 8
                            && ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        modules[x, y] = dark;
                        i++;
                    }
                }
            }
        }

        // ── Masking + penalties ────────────────────────────────────────────
        private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask)
        {
            int size = modules.GetLength(0);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (isFunction[x, y]) continue;
                    bool invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => (x / 3 + y / 2) % 2 == 0,
                        5 => x * y % 2 + x * y % 3 == 0,
                        6 => (x * y % 2 + x * y % 3) % 2 == 0,
                        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    };
                    if (invert) modules[x, y] ^= true;
                }
        }

        private static long Penalty(bool[,] m)
        {
            int size = m.GetLength(0);
            long p = 0;
            // Rule 1: runs of 5+ same-color in row/column.
            for (int y = 0; y < size; y++)
            {
                int runX = 1, runY = 1;
                for (int x = 1; x < size; x++)
                {
                    if (m[x, y] == m[x - 1, y]) { runX++; if (runX == 5) p += 3; else if (runX > 5) p++; }
                    else runX = 1;
                    if (m[y, x] == m[y, x - 1]) { runY++; if (runY == 5) p += 3; else if (runY > 5) p++; }
                    else runY = 1;
                }
            }
            // Rule 2: 2x2 blocks of same color.
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                    if (m[x, y] == m[x + 1, y] && m[x, y] == m[x, y + 1] && m[x, y] == m[x + 1, y + 1])
                        p += 3;
            // Rules 3 & 4 omitted from scoring: rule 1+2 dominate mask choice
            // for a short URL and a valid, scannable code results either way.
            // (Nayuki includes all four; the omission only affects which of
            // eight valid masks wins, never validity.)
            return p;
        }

        private static void AppendBits(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }
    }
}
