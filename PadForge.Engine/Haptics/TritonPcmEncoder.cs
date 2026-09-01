using System;

namespace PadForge.Engine.Haptics
{
    /// <summary>
    /// Steam Controller 2026 (Triton) PCM haptic stream encoding (#381,
    /// asked in discussion #371). The firmware accepts continuous PCM
    /// through a streaming interface beyond the 0x80-0x85 tone family
    /// Valve's SDL driver exposes: output report 0x86 configures the
    /// stream, 0x88 carries stereo sample data, and input report 0x44
    /// reports per-actuator stream state. Byte formats verified against
    /// three independent implementations that play audio through this
    /// exact interface (TritonLib src/TritonController.cpp:55-135 and
    /// 264-303, steam-controller-live-haptics haptics.cpp:202-218 and
    /// 555-570, sc2ds main.cpp:467-491) plus the firmware-derived tables
    /// in steam-controller-stuff (dissector.lua:63-201, readme.md:59-135).
    ///
    /// Everything here is pure byte assembly, unit-tested without
    /// hardware. Transport, pacing, and lifecycle live in
    /// HapticToneService.
    /// </summary>
    public static class TritonPcmEncoder
    {
        /// <summary>Output report: stream configure / enable / disable.
        /// Output-report namespace ONLY: the same 0x86 value used as the
        /// TYPE byte inside feature report 0x01 is the factory-reset
        /// command (TritonLib include/TritonController.h:230). This
        /// encoder never builds feature reports.</summary>
        public const byte ReportConfig = 0x86;

        /// <summary>Output report: stereo PCM data, de-interleaved.</summary>
        public const byte ReportStereoData = 0x88;

        /// <summary>0x86 target: both internal (grip) actuators. The 0x86
        /// target table differs from the 0x83 tone side table; never share
        /// an enum across the two reports (dissector.lua:167-174 vs
        /// 264-275).</summary>
        public const byte TargetInternalBoth = 2;

        /// <summary>0x86 target: both trackpad actuators.</summary>
        public const byte TargetTrackpadBoth = 5;

        /// <summary>Stream mode 0: 8 kHz stereo signed 16-bit. The wired
        /// pad's format. Modes are twelve discrete values, {8,4,2,1} kHz
        /// by {s16, s8, G.711 mu-law} (steam-controller-stuff
        /// readme.md:107-119).</summary>
        public const byte ModeWired8k16 = 0;

        /// <summary>Stream mode 8: 8 kHz stereo G.711 mu-law. The dongle
        /// (Puck) format: TritonLib hard-blocks 16-bit on wireless because
        /// the dongle's USB interrupt interval halves its bandwidth
        /// (TritonController.cpp:58).</summary>
        public const byte ModePuck8kMuLaw = 8;

        /// <summary>Stream sample rate for both shipped modes.</summary>
        public const int SampleRate = 8000;

        /// <summary>Stereo frames per 0x88 report in 16-bit mode
        /// (TritonController.cpp:67-71): 60 payload bytes, 30 per
        /// channel.</summary>
        public const int FramesPerPacket16 = 15;

        /// <summary>Stereo frames per 0x88 report in 8-bit modes
        /// (TritonController.cpp:64-65): 62 payload bytes, 31 per
        /// channel.</summary>
        public const int FramesPerPacketMuLaw = 31;

        /// <summary>The wire report length. The 0x88 layout is fixed:
        /// [0]=0x88, [1]=bytes per channel, left area at 2..32, right
        /// area at 33..63 (right ALWAYS at 33, even in 16-bit mode where
        /// only 30 of each area's 31 bytes carry samples).</summary>
        public const int PacketLength = 64;

        /// <summary>Packet period in microseconds for a mode's frame
        /// count at 8 kHz: 1875 for 16-bit, 3875 for mu-law
        /// (TritonController.cpp:88).</summary>
        public static int PacketPeriodMicroseconds(bool muLaw)
            => (FramesPerPacket(muLaw) * 1_000_000) / SampleRate;

        /// <summary>Frames carried per packet for the mode.</summary>
        public static int FramesPerPacket(bool muLaw)
            => muLaw ? FramesPerPacketMuLaw : FramesPerPacket16;

        /// <summary>Builds the 4-byte 0x86 stream command:
        /// [0x86, operation, target, mode]. Operation 1 disables, 2
        /// enables (TritonPCMOperation, TritonController.h:623-626).
        /// The arm sequence is disable BOTH targets, wait 10 ms, enable
        /// both with the mode (TritonController.cpp:284-302): the
        /// disable-first matters because reconfiguring a running stream
        /// is rejected (0x44 bit 6, dissector.lua:158). The transport
        /// pads to the interface's OutputReportByteLength, matching
        /// hidapi's internal padding in every reference.</summary>
        public static byte[] EncodeStreamCommand(bool enable, byte target, byte mode)
            => new byte[] { ReportConfig, (byte)(enable ? 2 : 1), target, enable ? mode : (byte)0 };

        /// <summary>Encodes one full 0x88 stereo packet from interleaved
        /// s16 frames (L,R,L,R...). <paramref name="frameCount"/> may be
        /// short on the final packet of a burst: the tail of each channel
        /// area is filled with the mode's TRUE silence value, 0x00 for
        /// 16-bit and 0xFF for mu-law. TritonLib pads mu-law tails with
        /// zero, which decodes to near-full-scale negative (-8031) and
        /// clicks at every track end (TritonController.cpp:109, their
        /// bug); G.711 silence is the encoding of sample 0, which is 0xFF.
        /// The length byte stays the mode's full per-channel byte count so
        /// the packet always represents a whole period, silence included:
        /// the stream must never starve (the live-haptics lesson, its
        /// firmware has an underrun-recovery path that can itself fail).</summary>
        public static byte[] EncodeStereoPacket(ReadOnlySpan<short> interleaved, int frameCount, bool muLaw)
        {
            var b = new byte[PacketLength];
            EncodeStereoPacketInto(b, interleaved, frameCount, muLaw);
            return b;
        }

        /// <summary>Allocation-free variant for the streaming hot path:
        /// encodes into the caller's buffer (at least
        /// <see cref="PacketLength"/> bytes).</summary>
        public static void EncodeStereoPacketInto(Span<byte> b, ReadOnlySpan<short> interleaved, int frameCount, bool muLaw)
        {
            int frames = FramesPerPacket(muLaw);
            if (frameCount > frames) frameCount = frames;
            if (frameCount > interleaved.Length / 2) frameCount = interleaved.Length / 2;

            b.Slice(0, PacketLength).Clear();
            b[0] = ReportStereoData;
            b[1] = (byte)(muLaw ? 31 : 30);

            if (muLaw)
            {
                for (int i = 0; i < frameCount; i++)
                {
                    b[2 + i] = MuLawEncode(interleaved[i * 2]);
                    b[33 + i] = MuLawEncode(interleaved[i * 2 + 1]);
                }
                for (int i = frameCount; i < frames; i++)
                {
                    b[2 + i] = 0xFF;
                    b[33 + i] = 0xFF;
                }
            }
            else
            {
                for (int i = 0; i < frameCount; i++)
                {
                    short l = interleaved[i * 2], r = interleaved[i * 2 + 1];
                    b[2 + i * 2] = (byte)(l & 0xFF);
                    b[3 + i * 2] = (byte)((l >> 8) & 0xFF);
                    b[33 + i * 2] = (byte)(r & 0xFF);
                    b[34 + i * 2] = (byte)((r >> 8) & 0xFF);
                }
                // 16-bit silence tail is already the cleared buffer.
            }
        }

        // G.711 mu-law segment table: the exponent for each value of
        // (biased sample >> 7). Identical tables in
        // steam-controller-live-haptics haptics.cpp:79-90 and sc2ds
        // main.cpp:88-94; this is the standard Sun/G.711 encoder, written
        // here from the algorithm.
        private static readonly byte[] MuLawSegments = BuildMuLawSegments();

        private static byte[] BuildMuLawSegments()
        {
            // The reference table reads 0,0,1,1,2,2,2,2,3.. : entry i is
            // the bit length of (i >> 1), capped at 7.
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int v = i >> 1;
                int seg = 0;
                while (v > 0) { v >>= 1; seg++; }
                t[i] = (byte)Math.Min(seg, 7);
            }
            return t;
        }

        /// <summary>G.711 mu-law compression of one s16 sample: bias
        /// 0x84, clip 32635, complemented output. Silence (0) encodes to
        /// 0xFF, +full scale to 0x80, -full scale to 0x00. Matches the
        /// byte-identical encoders in live-haptics haptics.cpp:91-99 and
        /// sc2ds main.cpp:96-110. Input -32768 is clamped to -32767
        /// first: the C references negate an int16 in place, which
        /// overflows on that one value.</summary>
        public static byte MuLawEncode(short sample)
        {
            int s = sample;
            if (s == short.MinValue) s = -short.MaxValue;
            int sign = 0;
            if (s < 0) { sign = 0x80; s = -s; }
            if (s > 32635) s = 32635;
            s += 0x84;
            int exponent = MuLawSegments[(s >> 7) & 0xFF];
            int mantissa = (s >> (exponent + 3)) & 0x0F;
            return (byte)~(sign | (exponent << 4) | mantissa);
        }

        /// <summary>Float sample (nominal [-1, 1]) to s16 with hard
        /// clipping. Non-finite input (a NaN or infinity escaping a
        /// filter) becomes silence rather than full-scale noise.</summary>
        public static short FloatToS16(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0;
            if (v >= 1f) return short.MaxValue;
            if (v <= -1f) return -short.MaxValue;
            return (short)(v * 32767f);
        }
    }
}
