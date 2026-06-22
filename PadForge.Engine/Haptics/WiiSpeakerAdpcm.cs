using System;

namespace PadForge.Engine.Haptics
{
    /// <summary>
    /// Yamaha 4-bit ADPCM codec for the Wii Remote speaker (issue #146,
    /// sub-feature 2). The expand-nibble math (DiffLookup / IndexScale / clip)
    /// is the public WiiBrew / Dolphin algorithm (facts from dolphin
    /// Speaker.cpp:24-54, GPLv2, read for the table and formula only). The
    /// dolphin clone is sparse, so that file is git-tracked but not on disk;
    /// retrieve it with
    /// <c>git -C dolphin show HEAD:Source/Core/Core/HW/WiimoteEmu/Speaker.cpp</c>
    /// (Read/Grep alone cannot see it). The encoder is the original inverse: per target PCM
    /// sample, try all 16 nibbles, keep the one whose reconstructed predictor
    /// lands closest, then advance the state with the exact decode formulas.
    ///
    /// Two samples pack per byte, LOW nibble first: the chronologically first
    /// sample (2n) goes in bits 0-3, the second (2n+1) in bits 4-7. This is the
    /// order the REAL Wii speaker hardware consumes, confirmed against this exact
    /// hardware: Touchmote converts cues with ffmpeg (-c:a adpcm_yamaha, which
    /// packs LOW-first) and WiimoteLib.StartPlayback streams those bytes UNCHANGED
    /// to the Wii Remote, and they play intelligibly on the user's own Wiimote.
    /// ffmpeg's adpcm_yamaha is low-first (ramp test). dolphin's EMULATED decoder
    /// reads high-first internally (Speaker.cpp deliberately deviates from the
    /// ffmpeg math it credits), but the authority for the real wire is the proven
    /// ffmpeg/WiimoteLib path verified on hardware, which is low-first. Yamaha
    /// ADPCM is a differential integrator, so swapping the nibble order scrambles
    /// the predictor into full garble. Do NOT "fix" this back to high-first; a
    /// hardware-verified Touchmote playback settled it.
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

        /// <summary>Decodes an ADPCM byte stream (2 samples/byte, low nibble
        /// first) into 16-bit PCM. Mirrors the real Wii Remote decode order.
        /// Resets state each call (whole-stream decode): decoding a captured
        /// chunked stream one chunk at a time through this overload would
        /// reintroduce a discontinuity at each boundary. Use
        /// <see cref="Decode(byte[], ref State)"/> to decode a stream in
        /// pieces.</summary>
        public static short[] Decode(byte[] adpcm)
        {
            var s = State.Initial;
            return Decode(adpcm, ref s);
        }

        /// <summary>Streaming decode: continues from the caller-held
        /// <paramref name="s"/> instead of resetting, so consecutive report
        /// payloads decode without a state discontinuity at each boundary (the
        /// Wii decoder keeps its predictor/step across reports, never resetting
        /// mid-cue). Output is always 2 samples per input byte.</summary>
        public static short[] Decode(byte[] adpcm, ref State s)
        {
            if (adpcm == null || adpcm.Length == 0) return Array.Empty<short>();
            var pcm = new short[adpcm.Length * 2];
            int o = 0;
            foreach (byte b in adpcm)
            {
                pcm[o++] = ExpandNibble(ref s, b & 0x0F);           // LOW nibble first (ffmpeg adpcm_yamaha = real Wii)
                pcm[o++] = ExpandNibble(ref s, (b >> 4) & 0x0F);
            }
            return pcm;
        }

        /// <summary>Encodes 16-bit PCM into Yamaha ADPCM (2 samples/byte, low
        /// nibble first). Greedy nearest-reconstruction search over the 16
        /// nibbles, advancing the encoder's mirror of the decode state so the
        /// stream round-trips through <see cref="Decode(byte[])"/>. Resets state
        /// each call (whole-cue encode).</summary>
        public static byte[] Encode(short[] pcm)
        {
            // One-shot whole-cue encode. Lenient on odd length: a self-contained
            // cue has no following chunk to desync, so the trailing odd sample
            // just occupies the final byte's low nibble (high nibble stays 0).
            var s = State.Initial;
            return EncodeCore(pcm, ref s);
        }

        /// <summary>Streaming encode: continues from the caller-held
        /// <paramref name="s"/>. A chunked stream (encode per report payload)
        /// must use this so the predictor/step carry forward; a fresh
        /// <see cref="Encode(short[])"/> per chunk would reset to
        /// predictor=0/step=127 while the Wii decoder keeps its running state,
        /// producing an amplitude discontinuity (audible click) at every report
        /// boundary. Chunks MUST be even length: an odd chunk leaves a padding
        /// high-nibble of 0 in its trailing byte that the Wii decoder consumes as
        /// a real sample, permanently desyncing decoder state for the rest of the
        /// stream (silent whole-stream corruption, not a local glitch). The
        /// overload throws on an odd length rather than corrupt silently; a
        /// caller streaming an odd final tail should pad it or finish with the
        /// one-shot <see cref="Encode(short[])"/>.</summary>
        public static byte[] Encode(short[] pcm, ref State s)
        {
            // Streaming chunks MUST be even length, or the trailing padding
            // nibble desyncs the decoder for the rest of the stream. Fail fast
            // instead of corrupting silently.
            if (pcm != null && (pcm.Length & 1) != 0)
                throw new ArgumentException(
                    "Streaming Encode requires an even-length chunk; an odd chunk desyncs decoder state for the rest of the stream.",
                    nameof(pcm));
            return EncodeCore(pcm, ref s);
        }

        private static byte[] EncodeCore(short[] pcm, ref State s)
        {
            if (pcm == null || pcm.Length == 0) return Array.Empty<byte>();
            int outLen = (pcm.Length + 1) / 2;
            var outBytes = new byte[outLen];

            for (int i = 0; i < pcm.Length; i++)
            {
                int nibble = ChooseNibble(s, pcm[i]);
                ExpandNibble(ref s, nibble); // advance state exactly as the decoder will

                if ((i & 1) == 0)
                    outBytes[i >> 1] = (byte)(nibble & 0x0F);           // first sample -> LOW nibble (ffmpeg/Wii order)
                else
                    outBytes[i >> 1] |= (byte)((nibble & 0x0F) << 4);   // second sample -> high nibble
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
