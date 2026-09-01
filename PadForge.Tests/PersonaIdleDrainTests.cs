using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The idle catch-up drain (discussion #371 follow-up). The stream
    /// loop idles at one 10 ms mixer read per ~16 ms coarse-timer wake,
    /// so wall-clock producers feeding the mixer (persona haptics at
    /// 100 callbacks a second, the loopback mirror) out-pace the reader
    /// and their BufferedWaveProviders accumulate backlog that becomes
    /// haptic onset latency, upstream of where the PcmPending cap can
    /// reach. The drain holds those buffers at one tick of depth. The
    /// policy is unit-tested directly, the sink's exact provider chain
    /// is exercised against a buried onset, and source contracts pin
    /// the wiring the policy cannot see.
    /// </summary>
    public class PersonaIdleDrainTests
    {
        // ── Policy ──

        [Fact]
        public void Policy_DrainsToTheKeepDepthAndStops()
        {
            double depth = 62; // ms of backlog
            int drained = HapticToneService.IdleCatchUpDrain(
                () => depth,
                () => { depth -= 10; return false; });
            // 62 -> 52 -> 42 -> 32 -> 22 -> 12: five blocks, then the
            // depth is under the keep threshold. The per-wake cap and
            // the target coincide here on purpose: both bounds hold.
            Assert.Equal(5, drained);
            Assert.True(depth <= HapticToneService.IdleDrainKeepMs);
        }

        [Fact]
        public void Policy_DoesNothingUnderTheKeepDepth()
        {
            int calls = 0;
            int drained = HapticToneService.IdleCatchUpDrain(
                () => HapticToneService.IdleDrainKeepMs, // at, not above
                () => { calls++; return false; });
            Assert.Equal(0, drained);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void Policy_CapsTheBurstPerWake()
        {
            // A post-suspend 250 ms backlog must not trigger a read
            // storm inside one wake: the cap bounds it and the next
            // wakes finish the recovery.
            double depth = 250;
            int drained = HapticToneService.IdleCatchUpDrain(
                () => depth,
                () => { depth -= 10; return false; });
            Assert.Equal(HapticToneService.IdleDrainMaxBlocks, drained);
            Assert.Equal(200, depth, 3);
        }

        [Fact]
        public void Policy_StopsTheMomentABlockCarriesContent()
        {
            // The third drained block finds the onset: the drain stops
            // there so the stream resumes at cadence and plays the rest,
            // instead of chewing through it faster than real time.
            double depth = 100;
            int calls = 0;
            int drained = HapticToneService.IdleCatchUpDrain(
                () => depth,
                () => { depth -= 10; return ++calls == 3; });
            Assert.Equal(3, drained);
            Assert.Equal(3, calls);
        }

        // ── The sink's chain, end to end ──
        // The exact provider shape BuildSink and the persona attach
        // build: BufferedWaveProvider (48 kHz stereo s16) -> sample
        // provider -> VolumeSampleProvider -> MixingSampleProvider
        // (ReadFully) -> Butterworth low-pass -> sinc resample to 8 kHz,
        // read in 80-frame tick blocks.

        private static (BufferedWaveProvider Buf, ISampleProvider Chain) BuildSinkShapedChain()
        {
            var buf = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            var vol = new VolumeSampleProvider(buf.ToSampleProvider());
            var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true };
            mixer.AddMixerInput(vol);
            var lp = new TritonPcmLowPassProvider(mixer, 250);
            var chain = new SincResamplingSampleProvider(lp, 8000);
            return (buf, chain);
        }

        private static void AddStereoS16(BufferedWaveProvider buf, Func<int, short> sample, int frames)
        {
            var bytes = new byte[frames * 4];
            for (int i = 0; i < frames; i++)
            {
                short v = sample(i);
                bytes[i * 4 + 0] = (byte)v;
                bytes[i * 4 + 1] = (byte)(v >> 8);
                bytes[i * 4 + 2] = (byte)v;
                bytes[i * 4 + 3] = (byte)(v >> 8);
            }
            buf.AddSamples(bytes, 0, bytes.Length);
        }

        [Fact]
        public void Chain_TickReadConsumesTenMillisecondsOfBacklog()
        {
            var (buf, chain) = BuildSinkShapedChain();
            AddStereoS16(buf, _ => 0, 48 * 200); // 200 ms of silence

            double before = buf.BufferedDuration.TotalMilliseconds;
            var f = new float[80 * 2];
            chain.Read(f, 0, f.Length); // one 8 kHz tick block
            double after = buf.BufferedDuration.TotalMilliseconds;

            // One 80-frame read at 8 kHz pulls 480 source frames at
            // 48 kHz: 10 ms. (The sinc kernel's history priming can pull
            // one extra source block on the very first read.)
            Assert.InRange(before - after, 9.0, 21.0);
        }

        [Fact]
        public void Chain_OnsetBuriedBehindBacklogSurvivesTheDrain()
        {
            var (buf, chain) = BuildSinkShapedChain();
            // 200 ms of silence, then 100 ms of a 160 Hz tone at half
            // scale: the swipe-tick pitch, comfortably under the 250 Hz
            // default cutoff.
            AddStereoS16(buf, _ => 0, 48 * 200);
            AddStereoS16(buf, i => (short)(16384 * Math.Sin(2 * Math.PI * 160 * i / 48000.0)), 48 * 100);

            var f = new float[80 * 2];
            int blocksUntilContent = -1;
            for (int b = 0; b < 40; b++)
            {
                int got = chain.Read(f, 0, f.Length);
                float pk = 0f;
                for (int i = 0; i < got; i++) { float a = f[i]; if (a < 0f) a = -a; if (a > pk) pk = a; }
                if (pk > 0.002f) { blocksUntilContent = b; break; }
            }

            // The onset sits behind 20 blocks of silence; the filter's
            // group delay can push detection a few blocks past that. It
            // must arrive, and near the backlog boundary, which is what
            // lets the drain's break-on-content hand a live onset to the
            // stream instead of discarding it.
            Assert.InRange(blocksUntilContent, 18, 28);
        }

        [Fact]
        public void Chain_ProducerConsumerDeficitAccumulatesWithoutTheDrain()
        {
            // The defect's arithmetic, run on the real buffer: produce
            // 10 ms per 10 ms of wall time, consume 10 ms per 15 ms wake
            // (the pre-fix idle cadence). After two simulated seconds the
            // buffer pins at its cap. The drain exists because of this.
            var (buf, chain) = BuildSinkShapedChain();
            var f = new float[80 * 2];
            double wallMs = 0, nextProduceMs = 0, nextConsumeMs = 0;
            while (wallMs < 2000)
            {
                if (wallMs >= nextProduceMs) { AddStereoS16(buf, _ => 0, 480); nextProduceMs += 10; }
                if (wallMs >= nextConsumeMs) { chain.Read(f, 0, f.Length); nextConsumeMs += 15.6; }
                wallMs += 1;
            }
            Assert.True(buf.BufferedDuration.TotalMilliseconds > 200,
                $"expected the pre-fix cadence to pin the buffer near its 250 ms cap, got {buf.BufferedDuration.TotalMilliseconds:F0} ms");
        }

        // ── Source contracts ──

        /// <summary>The drain is wired into the IDLE branch after the
        /// coarse sleep, gated on the two wall-clock producers, and the
        /// drain block preserves content: the PCM path runs the full
        /// tick behind a live-handle guard, the mono path marks
        /// LastContentMs so the next iteration streams.</summary>
        [Fact]
        public void StreamLoop_WiresTheDrainIntoTheIdleBranch()
        {
            string hts = RepoText("PadForge.App", "Common", "Input", "HapticToneService.cs");

            int sleep = hts.IndexOf("Thread.Sleep(15); // idle", StringComparison.Ordinal);
            Assert.True(sleep > 0, "the idle coarse sleep is gone");
            string idleTail = hts.Substring(sleep, 700);
            Assert.Contains("if (s.PersonaOn || s.MirrorOn)", idleTail);
            Assert.Contains("s.IdleDrainBlocks += IdleCatchUpDrain(drainDepthMs, drainBlock);", idleTail);

            int block = hts.IndexOf("Func<bool> drainBlock = () =>", StringComparison.Ordinal);
            Assert.True(block > 0, "the drain-block closure is gone");
            string blockBody = hts.Substring(block, 1200);
            Assert.Contains("s.Handle != IntPtr.Zero", blockBody);
            Assert.Contains("StreamTritonPcmTick(s, 0f, 0f, testActive: false, remoteActive: false, wakeMs)", blockBody);
            Assert.Contains("s.LastContentMs = wakeMs; return true;", blockBody);
        }

        /// <summary>The depth closure reads BOTH wall-clock producers:
        /// PersonaBuf and MirrorBuf, deepest wins. StartMirror publishes
        /// the buffer for it and StopMirror retracts it.</summary>
        [Fact]
        public void MirrorBuffer_IsVisibleToTheDrain()
        {
            string hts = RepoText("PadForge.App", "Common", "Input", "HapticToneService.cs");

            int depth = hts.IndexOf("Func<double> drainDepthMs = () =>", StringComparison.Ordinal);
            Assert.True(depth > 0, "the depth closure is gone");
            string depthBody = hts.Substring(depth, 800);
            Assert.Contains("s.PersonaBuf", depthBody);
            Assert.Contains("s.MirrorBuf", depthBody);

            Assert.Contains("s.MirrorBuf = buf;", hts);
            int stop = hts.IndexOf("private static void StopMirror", StringComparison.Ordinal);
            Assert.True(stop > 0);
            Assert.Contains("s.MirrorBuf = null;", hts.Substring(stop, 600));
        }

        /// <summary>The requester's diagnostic ask: input-stage depth is
        /// observable while idle (HAPTICBUF, any family) and while
        /// streaming (the TRITONPCM line), with the catch-up counter on
        /// both.</summary>
        [Fact]
        public void Diagnostics_ReportInputDepthArmedAndIdle()
        {
            string hts = RepoText("PadForge.App", "Common", "Input", "HapticToneService.cs");
            Assert.Contains("HAPTICBUF slot={s.Slot} personaMs={pMs:F0} mirrorMs={mMs:F0} idleDrained={s.IdleDrainBlocks}", hts);
            int stream = hts.IndexOf("TRITONPCM stream mulaw=", StringComparison.Ordinal);
            Assert.True(stream > 0);
            string streamLine = hts.Substring(stream, 500);
            Assert.Contains("personaMs=", streamLine);
            Assert.Contains("idleDrained={s.IdleDrainBlocks}", streamLine);
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
