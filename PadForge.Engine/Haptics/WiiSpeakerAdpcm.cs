using System;

namespace PadForge.Engine.Haptics
{
    /// <summary>
    /// Yamaha 4-bit ADPCM codec for the Wii Remote speaker (issue #146,
    /// sub-feature 2). The decoder is the public WiiBrew / Dolphin algorithm
    /// (facts from dolphin Speaker.cpp:24-54, GPLv2, read for the table and
    /// formula only). The encoder is the original inverse: per target PCM
    /// sample, try all 16 nibbles, keep the one whose reconstructed predictor
    /// lands closest, then advance the state with the exact decode formulas.
    /// Two samples pack per byte, high nibble first.
    ///
    /// A Wii Remote speaker is a low-rate single channel (ADPCM Hz =
    /// 6000000 / sample_rate, typically ~3 kHz), so this serves short alert
    /// cues, not music. The streaming sink (enable 0x14, the 7-byte config,
    /// paced 0x18 writes via HidD_SetOutputReport) is the hardware-gated
    /// remainder; this codec is its verifiable core.
    /// </summary>
    public static class WiiSpeakerAdpcm
    {
        // dolphin Speaker.cpp:24-28. The second 8 entries mirror the first.
        private static readonly int[] DiffLookup =
            { 1, 3, 5, 7, 9, 11, 13, 15, -1, -3, -5, -7, -9, -11, -13, -15 };
        private static readonly int[] IndexScale =
            { 230, 230, 230, 230, 307, 409, 512, 614, 230, 230, 230, 230, 307, 409, 512, 614 };

        // av_clip16 / av_clip (Speaker.cpp:31-46).
        private static short Clip16(int a)
            => a < short.MinValue ? short.MinValue : a > short.MaxValue ? short.MaxValue : (short)a;
        private static int Clip(int a, int lo, int hi) => a < lo ? lo : a > hi ? hi : a;

        /// <summary>Per-channel decoder state. Reset() init is predictor = 0,
        /// step = 127 (Speaker.cpp:146-147).</summary>
        public struct State
        {
            public int Predictor;
            public int Step;
            public static State Initial => new State { Predictor = 0, Step = 127 };
        }

        /// <summary>Expands one nibble, mutating <paramref name="s"/>, returning the
        /// reconstructed sample. Verbatim adpcm_yamaha_expand_nibble
        /// (Speaker.cpp:48-55).</summary>
        public static short ExpandNibble(ref State s, int nibble)
        {
            s.Predictor += (s.Step * DiffLookup[nibble]) / 8;
            s.Predictor = Clip16(s.Predictor);
            s.Step = (s.Step * IndexScale[nibble]) >> 8;
            s.Step = Clip(s.Step, 127, 24576);
            return (short)s.Predictor;
        }

        /// <summary>Decodes an ADPCM byte stream (2 samples/byte, high nibble
        /// first) into 16-bit PCM. Mirrors the Wii Remote / Dolphin decode.</summary>
        public static short[] Decode(byte[] adpcm)
        {
            if (adpcm == null || adpcm.Length == 0) return Array.Empty<short>();
            var s = State.Initial;
            var pcm = new short[adpcm.Length * 2];
            int o = 0;
            foreach (byte b in adpcm)
            {
                pcm[o++] = ExpandNibble(ref s, (b >> 4) & 0x0F); // high nibble first
                pcm[o++] = ExpandNibble(ref s, b & 0x0F);
            }
            return pcm;
        }

        /// <summary>Encodes 16-bit PCM into Yamaha ADPCM (2 samples/byte, high
        /// nibble first). Greedy nearest-reconstruction search over the 16
        /// nibbles, advancing the encoder's mirror of the decode state so the
        /// stream round-trips through <see cref="Decode"/>.</summary>
        public static byte[] Encode(short[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return Array.Empty<byte>();
            var s = State.Initial;
            int outLen = (pcm.Length + 1) / 2;
            var outBytes = new byte[outLen];

            for (int i = 0; i < pcm.Length; i++)
            {
                int nibble = ChooseNibble(s, pcm[i]);
                ExpandNibble(ref s, nibble); // advance state exactly as the decoder will

                if ((i & 1) == 0)
                    outBytes[i >> 1] = (byte)(nibble << 4);       // high nibble first
                else
                    outBytes[i >> 1] |= (byte)(nibble & 0x0F);    // low nibble
            }
            return outBytes;
        }

        /// <summary>Picks the nibble whose reconstructed predictor is closest to
        /// <paramref name="target"/>, given the current state. A trial copy keeps
        /// the live state untouched.</summary>
        private static int ChooseNibble(State s, short target)
        {
            int best = 0;
            int bestErr = int.MaxValue;
            for (int n = 0; n < 16; n++)
            {
                var trial = s;
                int recon = ExpandNibble(ref trial, n);
                int err = Math.Abs(recon - target);
                if (err < bestErr)
                {
                    bestErr = err;
                    best = n;
                }
            }
            return best;
        }
    }
}
