using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// SBC (Low Complexity Subband Codec) encoder, fixed to the DualShock 4
    /// Bluetooth audio configuration: 32 kHz, stereo, 8 subbands, 16 blocks,
    /// JOINT_STEREO channel mode, SNR bit allocation, bitpool 48. One frame
    /// consumes 128 samples per channel (16 blocks × 8 subbands) and emits
    /// exactly 109 bytes (frame_length = 4 + (4·8·2)/8 + (8 + 16·48)/8).
    ///
    /// Clean-room implementation from the Bluetooth A2DP specification
    /// Appendix B (SBC): analysis filterbank per B.7.1
    /// (h_m[n] = hp[n]·cos((m+½)(n−M/2)·π/M), prototype Proto_8_80 from
    /// B.8 Table 8.24), scale factors per B.7.2/B.6.2, joint-stereo
    /// decision per B.7.3, combined SNR bit allocation per B.6.3.2,
    /// quantization per B.7.5, bitstream syntax per B.4 Tables 8.11-8.15,
    /// CRC-8 per B.6.1.1 (G(X)=X⁸+X⁴+X³+X²+1, init 0x0F, over the
    /// frame_header bits minus syncword/crc plus all scale factor bits).
    ///
    /// The DS4-compatible parameter set (32 kHz / Sb8 / bitpool 48 /
    /// JointStereo / SNR / Blk16) matches nefarius/DS4AudioStreamer
    /// SbcAudioStream.cs:44-51 (MIT) and ds4mac's audio documentation.
    /// No code is derived from libsbc (GPL) — algorithm and tables come
    /// from the specification text.
    /// </summary>
    internal sealed class Ds4SbcEncoder
    {
        public const int Subbands = 8;
        public const int Blocks = 16;
        public const int Bitpool = 48;
        public const int SamplesPerChannelPerFrame = Subbands * Blocks; // 128
        public const int PcmSamplesPerFrame = SamplesPerChannelPerFrame * 2; // interleaved stereo
        public const int FrameBytes = 109;

        // Header byte 1: sampling_frequency=01 (32 kHz), blocks=11 (16),
        // channel_mode=11 (JOINT_STEREO), allocation_method=1 (SNR),
        // subbands=1 (8)  →  0b01_11_11_1_1. Spec Tables 8.16-8.20.
        private const byte HeaderByte1 = 0x7F;

        // A2DP spec B.8 Table 8.24 "Proto_8_80", read row-wise. The table
        // lists rows alternating sign on the 4th/8th/... row groups exactly
        // as printed; values are the prototype filter hp[n], n = 0..79.
        private static readonly double[] Proto880 =
        {
            0.00000000E+00,  1.56575398E-04,  3.43256425E-04,  5.54620202E-04,
            8.23919506E-04,  1.13992507E-03,  1.47640169E-03,  1.78371725E-03,
            2.01182542E-03,  2.10371989E-03,  1.99454554E-03,  1.61656283E-03,
            9.02154502E-04, -1.78805361E-04, -1.64973098E-03, -3.49717454E-03,
            5.65949473E-03,  8.02941163E-03,  1.04584443E-02,  1.27472335E-02,
            1.46525263E-02,  1.59045603E-02,  1.62208471E-02,  1.53184106E-02,
            1.29371806E-02,  8.85757540E-03,  2.92408442E-03, -4.91578024E-03,
           -1.46404076E-02, -2.61098752E-02, -3.90751381E-02, -5.31873032E-02,
            6.79989431E-02,  8.29847578E-02,  9.75753918E-02,  1.11196689E-01,
            1.23264548E-01,  1.33264415E-01,  1.40753505E-01,  1.45389847E-01,
            1.46955068E-01,  1.45389847E-01,  1.40753505E-01,  1.33264415E-01,
            1.23264548E-01,  1.11196689E-01,  9.75753918E-02,  8.29847578E-02,
           -6.79989431E-02, -5.31873032E-02, -3.90751381E-02, -2.61098752E-02,
           -1.46404076E-02, -4.91578024E-03,  2.92408442E-03,  8.85757540E-03,
            1.29371806E-02,  1.53184106E-02,  1.62208471E-02,  1.59045603E-02,
            1.46525263E-02,  1.27472335E-02,  1.04584443E-02,  8.02941163E-03,
           -5.65949473E-03, -3.49717454E-03, -1.64973098E-03, -1.78805361E-04,
            9.02154502E-04,  1.61656283E-03,  1.99454554E-03,  2.10371989E-03,
            2.01182542E-03,  1.78371725E-03,  1.47640169E-03,  1.13992507E-03,
            8.23919506E-04,  5.54620202E-04,  3.43256425E-04,  1.56575398E-04,
        };

        // Filterbank conventions pinned empirically (2026-06-12) against
        // ffmpeg's independent SBC decoder: spec-literal cos((m+½)(n−M/2)·
        // π/M) with newest sample at history[0] measures 76.5 dB (L) /
        // 66.9 dB (R) roundtrip SNR on a dual-tone test; every other
        // combination collapses below 10 dB. Kept as fields so the offline
        // harness can re-sweep after any filterbank edit.
        internal static int CosOffsetSign = -1;   // cos((m+½)(n ± M/2)π/M)
        internal static bool NewestAtZero = true; // history orientation

        // Analysis matrix h_m[n] = hp[n] · cos((m + 0.5)(n − M/2)π / M),
        // precomputed (8 × 80). Direct-form per spec B.7.1.
        private static double[][] _hm;
        private static int _hmSign;

        private static double[][] AnalysisMatrix()
        {
            if (_hm != null && _hmSign == CosOffsetSign) return _hm;
            var hm = new double[Subbands][];
            for (int m = 0; m < Subbands; m++)
            {
                hm[m] = new double[80];
                for (int n = 0; n < 80; n++)
                {
                    // The spec's Table 8.24 prints the SEGMENT-FOLDED window
                    // C[n] = (−1)^⌊n/16⌋ · hp[n] (the cosine matrix has
                    // period 2M = 16 in n with sign alternation, and the
                    // table pre-absorbs it for the folded flow network in
                    // Figure 8.5). Un-fold to recover the true symmetric
                    // prototype hp for the direct form h_m[n] = hp[n]·cos(…).
                    double hp = Proto880[n] * (((n / 16) & 1) == 0 ? 1.0 : -1.0);
                    hm[m][n] = hp
                        * Math.Cos((m + 0.5) * (n + CosOffsetSign * Subbands / 2.0) * Math.PI / Subbands);
                }
            }
            _hmSign = CosOffsetSign;
            _hm = hm;
            return hm;
        }

        // Per-channel 80-sample analysis history, newest sample at [0]
        // (X[n] = x(t − n), so S[m] = Σ h_m[n]·X[n] is the convolution).
        private readonly double[] _histL = new double[80];
        private readonly double[] _histR = new double[80];

        // Scratch: subband samples per frame [block][channel][subband].
        private readonly double[,,] _sb = new double[Blocks, 2, Subbands];
        private readonly int[,] _scaleFactor = new int[2, Subbands];
        private readonly int[,] _bits = new int[2, Subbands];
        private readonly bool[] _join = new bool[Subbands];

        public void Reset()
        {
            Array.Clear(_histL, 0, _histL.Length);
            Array.Clear(_histR, 0, _histR.Length);
        }

        /// <summary>Encodes 256 interleaved stereo s16 samples (128 per
        /// channel) into one 109-byte SBC frame at <paramref name="dst"/>.
        /// Returns the frame length (always 109).</summary>
        public int Encode(ReadOnlySpan<short> pcmInterleaved, Span<byte> dst)
        {
            if (pcmInterleaved.Length < PcmSamplesPerFrame)
                throw new ArgumentException("need 256 interleaved samples");
            if (dst.Length < FrameBytes)
                throw new ArgumentException("need 109 byte output");

            // ── Analysis (spec B.7.1): per block, shift 8 new samples per
            // channel into the history and compute the 8 subband samples.
            double[][] hmTab = AnalysisMatrix();
            for (int blk = 0; blk < Blocks; blk++)
            {
                ShiftIn(_histL, pcmInterleaved, blk, channel: 0);
                ShiftIn(_histR, pcmInterleaved, blk, channel: 1);
                for (int m = 0; m < Subbands; m++)
                {
                    double[] h = hmTab[m];
                    double sl = 0, sr = 0;
                    for (int n = 0; n < 80; n++)
                    {
                        sl += h[n] * _histL[n];
                        sr += h[n] * _histR[n];
                    }
                    _sb[blk, 0, m] = sl;
                    _sb[blk, 1, m] = sr;
                }
            }

            // ── Joint-stereo decision (spec B.7.3): per subband, compare
            // the scale factors of L/R against the scale factors of the
            // sum/difference signals; pick joint coding when it is cheaper.
            // join[7] is always 0 (spec B.5.1).
            for (int sbI = 0; sbI < Subbands - 1; sbI++)
            {
                double maxL = 0, maxR = 0, maxS = 0, maxD = 0;
                for (int blk = 0; blk < Blocks; blk++)
                {
                    double l = _sb[blk, 0, sbI], r = _sb[blk, 1, sbI];
                    double s = (l + r) * 0.5, d = (l - r) * 0.5;
                    if (Math.Abs(l) > maxL) maxL = Math.Abs(l);
                    if (Math.Abs(r) > maxR) maxR = Math.Abs(r);
                    if (Math.Abs(s) > maxS) maxS = Math.Abs(s);
                    if (Math.Abs(d) > maxD) maxD = Math.Abs(d);
                }
                _join[sbI] = ScaleFactorOf(maxS) + ScaleFactorOf(maxD)
                           < ScaleFactorOf(maxL) + ScaleFactorOf(maxR);
                if (_join[sbI])
                {
                    for (int blk = 0; blk < Blocks; blk++)
                    {
                        double l = _sb[blk, 0, sbI], r = _sb[blk, 1, sbI];
                        _sb[blk, 0, sbI] = (l + r) * 0.5;
                        _sb[blk, 1, sbI] = (l - r) * 0.5;
                    }
                }
            }
            _join[Subbands - 1] = false;

            // ── Scale factors (spec B.7.2): smallest sf with
            // 2^(sf+1) > max|sample|, 4-bit field.
            for (int ch = 0; ch < 2; ch++)
                for (int sbI = 0; sbI < Subbands; sbI++)
                {
                    double max = 0;
                    for (int blk = 0; blk < Blocks; blk++)
                        if (Math.Abs(_sb[blk, ch, sbI]) > max)
                            max = Math.Abs(_sb[blk, ch, sbI]);
                    _scaleFactor[ch, sbI] = ScaleFactorOf(max);
                }

            AllocateBitsJointSnr();

            // ── Bitstream (spec B.4). MSB-first writer.
            dst.Slice(0, FrameBytes).Clear();
            var w = new BitWriter(dst);
            w.WriteByte(0x9C);          // syncword
            w.WriteByte(HeaderByte1);
            w.WriteByte(Bitpool);
            int crcPos = w.BytePosition;
            w.WriteByte(0x00);          // crc_check placeholder
            for (int sbI = 0; sbI < Subbands - 1; sbI++)
                w.WriteBits(_join[sbI] ? 1u : 0u, 1);
            w.WriteBits(0, 1);          // RFU
            for (int ch = 0; ch < 2; ch++)
                for (int sbI = 0; sbI < Subbands; sbI++)
                    w.WriteBits((uint)_scaleFactor[ch, sbI], 4);

            // Quantize + emit samples (spec B.7.5):
            // q = ((s/scalefactor + 1) · levels) / 2, levels = 2^bits − 1.
            for (int blk = 0; blk < Blocks; blk++)
                for (int ch = 0; ch < 2; ch++)
                    for (int sbI = 0; sbI < Subbands; sbI++)
                    {
                        int bits = _bits[ch, sbI];
                        if (bits == 0) continue;
                        uint levels = (1u << bits) - 1;
                        double scalefactor = (double)(1 << (_scaleFactor[ch, sbI] + 1));
                        double q = (_sb[blk, ch, sbI] / scalefactor + 1.0) * levels / 2.0;
                        long qi = (long)q; // floor toward zero; q >= 0 by construction
                        if (qi < 0) qi = 0;
                        if (qi > levels) qi = levels;
                        w.WriteBits((uint)qi, bits);
                    }
            // padding(): dst was pre-cleared; remaining bits are zero.

            // ── CRC-8 (spec B.6.1.1) over header byte 1, bitpool, join
            // bits (incl. RFU), and all scale factors — here exactly 11
            // whole bytes: dst[1], dst[2], dst[4..12].
            dst[crcPos] = Crc8(dst);
            return FrameBytes;
        }

        private static void ShiftIn(double[] hist, ReadOnlySpan<short> pcm, int blk, int channel)
        {
            for (int i = 79; i >= Subbands; i--) hist[i] = hist[i - Subbands];
            int baseIdx = blk * Subbands * 2;
            if (NewestAtZero)
            {
                // Newest sample of the block lands at hist[0]; X[n] = x(t − n).
                for (int j = 0; j < Subbands; j++)
                    hist[Subbands - 1 - j] = pcm[baseIdx + j * 2 + channel];
            }
            else
            {
                for (int j = 0; j < Subbands; j++)
                    hist[j] = pcm[baseIdx + j * 2 + channel];
            }
        }

        private static int ScaleFactorOf(double maxAbs)
        {
            int sf = 0;
            while (sf < 15 && (1 << (sf + 1)) <= maxAbs) sf++;
            return sf;
        }

        /// <summary>Combined two-channel SNR bit allocation, verbatim port
        /// of the spec B.6.3.2 pseudocode (SNR branch: bitneed = scale
        /// factor).</summary>
        private void AllocateBitsJointSnr()
        {
            Span<int> bitneed = stackalloc int[2 * Subbands];
            int maxBitneed = 0;
            for (int ch = 0; ch < 2; ch++)
                for (int sbI = 0; sbI < Subbands; sbI++)
                {
                    int bn = _scaleFactor[ch, sbI];
                    bitneed[ch * Subbands + sbI] = bn;
                    if (bn > maxBitneed) maxBitneed = bn;
                }

            int bitcount = 0, slicecount = 0, bitslice = maxBitneed + 1;
            do
            {
                bitslice--;
                bitcount += slicecount;
                slicecount = 0;
                for (int ch = 0; ch < 2; ch++)
                    for (int sbI = 0; sbI < Subbands; sbI++)
                    {
                        int bn = bitneed[ch * Subbands + sbI];
                        if (bn > bitslice + 1 && bn < bitslice + 16) slicecount++;
                        else if (bn == bitslice + 1) slicecount += 2;
                    }
            } while (bitcount + slicecount < Bitpool);

            if (bitcount + slicecount == Bitpool)
            {
                bitcount += slicecount;
                bitslice--;
            }

            for (int ch = 0; ch < 2; ch++)
                for (int sbI = 0; sbI < Subbands; sbI++)
                {
                    int bn = bitneed[ch * Subbands + sbI];
                    _bits[ch, sbI] = bn < bitslice + 2 ? 0 : Math.Min(bn - bitslice, 16);
                }

            int c = 0, s = 0;
            while (bitcount < Bitpool && s < Subbands)
            {
                if (_bits[c, s] >= 2 && _bits[c, s] < 16) { _bits[c, s]++; bitcount++; }
                else if (bitneed[c * Subbands + s] == bitslice + 1 && Bitpool > bitcount + 1)
                { _bits[c, s] = 2; bitcount += 2; }
                if (c == 1) { c = 0; s++; } else c = 1;
            }
            c = 0; s = 0;
            while (bitcount < Bitpool && s < Subbands)
            {
                if (_bits[c, s] < 16) { _bits[c, s]++; bitcount++; }
                if (c == 1) { c = 0; s++; } else c = 1;
            }
        }

        /// <summary>CRC-8, G(X)=X⁸+X⁴+X³+X²+1 (0x1D), init 0x0F, bitwise
        /// MSB-first over byte 1 (config), byte 2 (bitpool), and bytes
        /// 4..12 (join bits + scale factors) of the assembled frame.</summary>
        private static byte Crc8(ReadOnlySpan<byte> frame)
        {
            byte crc = 0x0F;
            void Feed(byte b)
            {
                for (int i = 7; i >= 0; i--)
                {
                    bool bit = (((b >> i) & 1) ^ (crc >> 7)) != 0;
                    crc = (byte)(crc << 1);
                    if (bit) crc ^= 0x1D;
                }
            }
            Feed(frame[1]);
            Feed(frame[2]);
            for (int i = 4; i < 13; i++) Feed(frame[i]);
            return crc;
        }

        /// <summary>MSB-first bit writer over a span.</summary>
        private ref struct BitWriter
        {
            private readonly Span<byte> _buf;
            private int _bitPos;

            public BitWriter(Span<byte> buf) { _buf = buf; _bitPos = 0; }

            public int BytePosition => _bitPos >> 3;

            public void WriteByte(byte b) => WriteBits(b, 8);

            public void WriteBits(uint value, int count)
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    if (((value >> i) & 1) != 0)
                        _buf[_bitPos >> 3] |= (byte)(0x80 >> (_bitPos & 7));
                    _bitPos++;
                }
            }
        }
    }
}
