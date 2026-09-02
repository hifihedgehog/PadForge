using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.Wave;
using PadForge.Engine.Haptics;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Steam Controller 2026 native PCM haptics (#381, discussion #371).
    /// The byte formats are pinned against the reference-verified wire
    /// protocol (TritonLib, steam-controller-live-haptics, sc2ds, and the
    /// steam-controller-stuff firmware tables): 0x86 config commands,
    /// the 0x88 de-interleaved stereo layout, G.711 mu-law with its
    /// 0xFF silence, and the Butterworth low-pass response.
    /// </summary>
    public class TritonPcmTests
    {
        // ── 0x86 stream commands ──

        [Fact]
        public void StreamCommand_EnableAndDisableBytes()
        {
            // [0x86, op, target, mode]; op 1 = disable, 2 = enable.
            Assert.Equal(new byte[] { 0x86, 2, 2, 8 },
                TritonPcmEncoder.EncodeStreamCommand(true, TritonPcmEncoder.TargetInternalBoth, TritonPcmEncoder.ModePuck8kMuLaw));
            Assert.Equal(new byte[] { 0x86, 2, 5, 0 },
                TritonPcmEncoder.EncodeStreamCommand(true, TritonPcmEncoder.TargetTrackpadBoth, TritonPcmEncoder.ModeWired8k16));
            // A disable always carries mode 0, the TritonLib teardown shape.
            Assert.Equal(new byte[] { 0x86, 1, 2, 0 },
                TritonPcmEncoder.EncodeStreamCommand(false, TritonPcmEncoder.TargetInternalBoth, TritonPcmEncoder.ModePuck8kMuLaw));
        }

        [Fact]
        public void PacketPeriods_MatchTheReferenceMath()
        {
            // frames * 1e6 / 8000: 15 -> 1875 us wired, 31 -> 3875 us puck.
            Assert.Equal(1875, TritonPcmEncoder.PacketPeriodMicroseconds(muLaw: false));
            Assert.Equal(3875, TritonPcmEncoder.PacketPeriodMicroseconds(muLaw: true));
        }

        // ── 0x88 stereo data layout ──

        [Fact]
        public void WiredPacket_DeinterleavesLittleEndianAtFixedOffsets()
        {
            var frames = new short[TritonPcmEncoder.FramesPerPacket16 * 2];
            for (int i = 0; i < TritonPcmEncoder.FramesPerPacket16; i++)
            {
                frames[i * 2] = (short)(0x1100 + i);      // left
                frames[i * 2 + 1] = (short)(-0x2200 - i); // right
            }
            var b = TritonPcmEncoder.EncodeStereoPacket(frames, TritonPcmEncoder.FramesPerPacket16, muLaw: false);

            Assert.Equal(64, b.Length);
            Assert.Equal(0x88, b[0]);
            Assert.Equal(30, b[1]); // BYTES per channel, not frames
            for (int i = 0; i < TritonPcmEncoder.FramesPerPacket16; i++)
            {
                short l = (short)(b[2 + i * 2] | (b[3 + i * 2] << 8));
                short r = (short)(b[33 + i * 2] | (b[34 + i * 2] << 8));
                Assert.Equal(frames[i * 2], l);
                Assert.Equal(frames[i * 2 + 1], r);
            }
            // The 31st byte of each area is pad in 16-bit mode: right area
            // starts at the FIXED offset 33 regardless of mode.
            Assert.Equal(0, b[32]);
            Assert.Equal(0, b[63]);
        }

        [Fact]
        public void MuLawPacket_FillsShortTailsWithTrueSilence()
        {
            // 10 of 31 frames: the tail must be 0xFF (mu-law silence), never
            // 0x00, which decodes to near-full-scale negative and clicks
            // (TritonLib's tail bug, TritonController.cpp:109).
            var frames = new short[10 * 2]; // all zero samples
            var b = TritonPcmEncoder.EncodeStereoPacket(frames, 10, muLaw: true);

            Assert.Equal(0x88, b[0]);
            Assert.Equal(31, b[1]);
            for (int i = 0; i < 31; i++)
            {
                Assert.Equal(0xFF, b[2 + i]);   // encoded zeros AND fill agree
                Assert.Equal(0xFF, b[33 + i]);
            }
        }

        [Fact]
        public void ShortWiredTail_IsZeroFilled()
        {
            var frames = new short[4 * 2];
            for (int i = 0; i < frames.Length; i++) frames[i] = 12345;
            var b = TritonPcmEncoder.EncodeStereoPacket(frames, 4, muLaw: false);
            for (int i = 4 * 2; i < 30; i++)
            {
                Assert.Equal(0, b[2 + i]);
                Assert.Equal(0, b[33 + i]);
            }
        }

        // ── G.711 mu-law ──

        [Theory]
        [InlineData((short)0, (byte)0xFF)]        // silence
        [InlineData((short)32767, (byte)0x80)]    // +full scale
        [InlineData((short)-32767, (byte)0x00)]   // -full scale
        [InlineData((short)-32768, (byte)0x00)]   // clamped, the C refs overflow here
        [InlineData((short)1000, (byte)0xCE)]     // hand-computed: seg 3, mantissa 1
        [InlineData((short)-1000, (byte)0x4E)]
        public void MuLaw_GoldenVectors(short sample, byte expected)
        {
            Assert.Equal(expected, TritonPcmEncoder.MuLawEncode(sample));
        }

        [Fact]
        public void MuLaw_RoundTripsWithinSegmentError()
        {
            // The Sun/G.711 bias-0x84 encoder pairs with the bias-0x84
            // inverse: v = ((mantissa << 3) + 0x84) << exponent, minus
            // 0x84. Quantization error within a segment is bounded, so a
            // spread of values must come back close in a relative sense.
            static short Decode(byte u)
            {
                u = (byte)~u;
                int sign = (u & 0x80) != 0 ? -1 : 1;
                int e = (u >> 4) & 7;
                int m = u & 15;
                int v = ((m << 3) + 0x84) << e;
                return (short)(sign * (v - 0x84));
            }
            foreach (int v in new[] { 50, 200, 800, 3000, 12000, 30000, -50, -200, -800, -3000, -12000, -30000 })
            {
                short back = Decode(TritonPcmEncoder.MuLawEncode((short)v));
                Assert.True(Math.Abs(back - v) <= Math.Max(16, Math.Abs(v) / 8),
                    $"mu-law round trip {v} -> {back}");
            }
        }

        // ── Float conversion ──

        [Fact]
        public void FloatToS16_ClipsAndSanitizes()
        {
            Assert.Equal(0, TritonPcmEncoder.FloatToS16(float.NaN));
            Assert.Equal(0, TritonPcmEncoder.FloatToS16(float.PositiveInfinity));
            Assert.Equal(short.MaxValue, TritonPcmEncoder.FloatToS16(2f));
            Assert.Equal((short)-short.MaxValue, TritonPcmEncoder.FloatToS16(-2f));
            Assert.Equal((short)16383, TritonPcmEncoder.FloatToS16(0.5f));
        }

        // ── Butterworth response ──

        private sealed class SineSource : ISampleProvider
        {
            private readonly float _hz;
            private double _phase;
            public SineSource(float hz) { _hz = hz; }
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            public int Read(float[] buffer, int offset, int count)
            {
                for (int i = 0; i < count; i += 2)
                {
                    float v = (float)Math.Sin(_phase);
                    _phase += 2 * Math.PI * _hz / 48000.0;
                    buffer[offset + i] = v;
                    if (i + 1 < count) buffer[offset + i + 1] = v;
                }
                return count;
            }
        }

        private static double FilterGainAt(float hz, int cutoff)
        {
            var lp = new PadForge.Common.Input.TritonPcmLowPassProvider(new SineSource(hz), cutoff);
            var buf = new float[9600]; // 100 ms stereo at 48 kHz
            lp.Read(buf, 0, buf.Length);          // settle the transient
            lp.Read(buf, 0, buf.Length);
            double sum = 0;
            for (int i = 0; i < buf.Length; i += 2) sum += buf[i] * buf[i];
            double rms = Math.Sqrt(sum / (buf.Length / 2));
            return rms / 0.7071; // input sine RMS
        }

        [Fact]
        public void Butterworth_MinusThreeDbAtCutoff()
        {
            double g = FilterGainAt(250f, 250);
            Assert.InRange(g, 0.64, 0.78); // -3 dB = 0.707
        }

        [Fact]
        public void Butterworth_SteepAboveCutoff()
        {
            double g = FilterGainAt(500f, 250);
            Assert.InRange(g, 0.02, 0.11); // 4th order: about -24 dB an octave up
        }

        [Fact]
        public void Butterworth_FlatWellBelowCutoff()
        {
            double g = FilterGainAt(100f, 250);
            Assert.InRange(g, 0.95, 1.05);
        }

        // ── Source contracts ──

        /// <summary>The persona routing fix: the actuator-sink submit runs
        /// BEFORE the Sony-target early return, so a slot whose only
        /// physical device is an actuator pad still receives persona
        /// haptics (the requester's routing find).</summary>
        [Fact]
        public void PersonaSubmit_RunsBeforeTheSonyTargetGate()
        {
            string aps = RepoText("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");
            int fn = aps.IndexOf("private static void OnPersonaFrames", StringComparison.Ordinal);
            Assert.True(fn > 0);
            string body = aps.Substring(fn, 3600);
            int submit = body.IndexOf("HapticToneService.SubmitPersonaHaptics(", StringComparison.Ordinal);
            int gate = body.IndexOf("if (targets.Length == 0) return;", StringComparison.Ordinal);
            Assert.True(submit > 0, "the actuator submit is gone from OnPersonaFrames");
            Assert.True(gate > 0, "the Sony-target gate is gone from OnPersonaFrames");
            Assert.True(submit < gate, "the actuator submit must run before the Sony-target early return");
        }

        /// <summary>The service contracts: PCM capability rides the same
        /// transport split as the serialized tone lane, the Steam2026
        /// dispatch branches to the PCM tick, the arm sequence is
        /// disable-pair, 10 ms, enable-pair, teardown fences BEFORE it
        /// disarms the stream (F15: a stream thread that outlived the
        /// Join cannot re-enable past the disable), and an armed stream
        /// absorbs swipe pulses instead of racing 0x82.</summary>
        [Fact]
        public void ServiceContracts_ArmDispatchTeardownAndPulses()
        {
            string hts = RepoText("PadForge.App", "Common", "Input", "HapticToneService.cs");

            Assert.Contains("s.PcmCapable = usbTriton || puckTriton;", hts);
            Assert.Contains("s.PcmMuLaw = puckTriton;", hts);
            Assert.Contains("streaming = StreamTritonPcmTick(s, toneHz, amp, testActive, remoteActive, nowMs);", hts);

            int arm = hts.IndexOf("private static bool ArmTritonPcm", StringComparison.Ordinal);
            Assert.True(arm > 0);
            string armBody = hts.Substring(arm, 1600);
            int d1 = armBody.IndexOf("enable: false, Engine.Haptics.TritonPcmEncoder.TargetInternalBoth", StringComparison.Ordinal);
            int d2 = armBody.IndexOf("enable: false, Engine.Haptics.TritonPcmEncoder.TargetTrackpadBoth", StringComparison.Ordinal);
            int sl = armBody.IndexOf("Thread.Sleep(10);", StringComparison.Ordinal);
            int e1 = armBody.IndexOf("enable: true, Engine.Haptics.TritonPcmEncoder.TargetInternalBoth", StringComparison.Ordinal);
            int e2 = armBody.IndexOf("enable: true, Engine.Haptics.TritonPcmEncoder.TargetTrackpadBoth", StringComparison.Ordinal);
            Assert.True(d1 > 0 && d2 > d1 && sl > d2 && e1 > sl && e2 > e1,
                "the arm sequence must be disable both, settle 10 ms, enable both");

            int td = hts.IndexOf("if (s.PcmArmed)\n                                try { DisarmTritonPcm(s); } catch { }".Replace("\n", "\r\n"), StringComparison.Ordinal);
            if (td < 0) td = hts.IndexOf("try { DisarmTritonPcm(s); } catch { }", StringComparison.Ordinal);
            int fence = hts.IndexOf("s.TornDown = true;", StringComparison.Ordinal);
            Assert.True(td > 0 && fence > 0 && fence < td, "teardown must raise the fence before it disarms the stream");

            // The fence is read by ArmTritonPcm before its first config
            // write, so the disable pair above is the last 0x86 the pad
            // sees from a torn-down sink.
            int armTornDown = armBody.IndexOf("if (s.TornDown || !s.Running) return false;", StringComparison.Ordinal);
            Assert.True(armTornDown > 0 && armTornDown < d1, "ArmTritonPcm must check TornDown before its first TritonPcmConfigWrite");

            Assert.Contains("if (pulseSides != 0 && s.PcmCapable && s.PcmArmed)", hts);
            Assert.Contains("s.PcmPulseEnv = Math.Max(s.PcmPulseEnv,", hts);
        }

        /// <summary>F13: the PCM overlay branch consumes PulseAmp the way
        /// SendTouchpadPulses does. PulseAmp is max-wins from the polling
        /// thread, and the overlay branch bypasses SendTouchpadPulses, so
        /// without the zero a lowered slider kept the loudest tick.</summary>
        [Fact]
        public void PcmOverlay_ConsumesPulseAmp()
        {
            string hts = RepoText("PadForge.App", "Common", "Input", "HapticToneService.cs");
            int at = hts.IndexOf("if (pulseSides != 0 && s.PcmCapable && s.PcmArmed)", StringComparison.Ordinal);
            Assert.True(at > 0);
            int seed = hts.IndexOf("s.PcmPulseEnv = Math.Max(", at, StringComparison.Ordinal);
            int zero = hts.IndexOf("s.PulseAmp = 0f;", at, StringComparison.Ordinal);
            int next = hts.IndexOf("else if (pulseSides != 0", at, StringComparison.Ordinal);
            Assert.True(seed > at, "the PcmPulseEnv seed is gone from the overlay branch");
            Assert.True(zero > seed && next > zero, "the overlay branch must zero PulseAmp after seeding PcmPulseEnv");
        }

        /// <summary>F14: a failed 0x86 arm burst waits PcmArmRetryGapMs
        /// before the next attempt. Zero means no failure yet.</summary>
        [Fact]
        public void ArmRetryGate_WaitsAfterAFailure()
        {
            Assert.Equal(250, PadForge.Common.Input.HapticToneService.PcmArmRetryGapMs);
            Assert.True(PadForge.Common.Input.HapticToneService.ShouldRetryPcmArm(nowMs: 12345, lastFailMs: 0));
            Assert.False(PadForge.Common.Input.HapticToneService.ShouldRetryPcmArm(nowMs: 1005, lastFailMs: 1000));
            Assert.False(PadForge.Common.Input.HapticToneService.ShouldRetryPcmArm(nowMs: 1249, lastFailMs: 1000));
            Assert.True(PadForge.Common.Input.HapticToneService.ShouldRetryPcmArm(nowMs: 1250, lastFailMs: 1000));
            Assert.True(PadForge.Common.Input.HapticToneService.ShouldRetryPcmArm(nowMs: 1300, lastFailMs: 1000));
        }

        /// <summary>F16: a zero event handle would leave its slot
        /// unreclaimable and the ring silent, so the constructor refuses
        /// the ring and releases what it built. The factory hands out two
        /// real events and then fails, so the release path closes real
        /// handles.</summary>
        [Fact]
        public void WriteRing_RefusesAZeroEvent()
        {
            int calls = 0;
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new PadForge.Common.Input.TritonPcmWriteRing(64, () =>
                    ++calls <= 2 ? CreateEventW(IntPtr.Zero, true, true, null) : IntPtr.Zero));
            Assert.Equal(3, calls);
            Assert.Contains("slot 2", ex.Message);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, string name);

        /// <summary>F18: the ring holds every packet one tick can produce
        /// plus one of headroom. Wired 16-bit mode packs 15 frames per
        /// packet and the tick yields 80, so six packets leave per tick.
        /// Four slots refused the fifth and sixth every tick.</summary>
        [Fact]
        public void WriteRing_HoldsATickOfPackets()
        {
            int perTick = (PadForge.Common.Input.HapticToneService.PcmFramesPerTick + TritonPcmEncoder.FramesPerPacket16 - 1)
                / TritonPcmEncoder.FramesPerPacket16;
            Assert.Equal(6, perTick);
            Assert.True(PadForge.Common.Input.TritonPcmWriteRing.Slots >= perTick + 1,
                $"Slots={PadForge.Common.Input.TritonPcmWriteRing.Slots} cannot carry {perTick} packets a tick plus headroom");
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
