using System;
using NAudio.Wave;
using PadForge.Common.Input;

namespace PadForge.Tests
{
    /// <summary>
    /// #185 haptic-mirror engage gate. The gate wraps ONLY the mirror's mixer
    /// input: while disengaged it must keep draining the inner provider (so the
    /// loopback buffer never backs up or bursts stale audio on re-engage) and
    /// zero the output (so the reducer reads silence and the tone stops). The
    /// hold decision is the shared HoldEngaged helper the poll-thread updater
    /// uses, including the release delay and its 0..10000 clamp.
    /// </summary>
    public class HapticMirrorEngageTests
    {
        /// <summary>Counts reads and hands out a constant nonzero ramp, so a
        /// zeroed output is distinguishable from a skipped read.</summary>
        private sealed class CountingSource : ISampleProvider
        {
            public int Reads;
            public WaveFormat WaveFormat { get; } =
                WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            public int Read(float[] buffer, int offset, int count)
            {
                Reads++;
                for (int i = 0; i < count; i++) buffer[offset + i] = 0.5f;
                return count;
            }
        }

        private const int TestSlot = 7;

        [Fact]
        public void Engaged_PassesSamplesThrough()
        {
            var src = new CountingSource();
            var gate = new HapticToneService.GatedMirrorSampleProvider(src, TestSlot);
            HapticToneService.MirrorEngagedBySlot[TestSlot] = true;

            var buf = new float[64];
            int n = gate.Read(buf, 0, buf.Length);

            Assert.Equal(buf.Length, n);
            Assert.All(buf, v => Assert.Equal(0.5f, v));
        }

        [Fact]
        public void Disengaged_ZeroesOutputButKeepsDraining()
        {
            var src = new CountingSource();
            var gate = new HapticToneService.GatedMirrorSampleProvider(src, TestSlot);
            HapticToneService.MirrorEngagedBySlot[TestSlot] = false;
            try
            {
                var buf = new float[64];
                int n = gate.Read(buf, 0, buf.Length);

                // Full count returned: MixingSampleProvider never auto-removes
                // the input, and the inner provider WAS read (drained).
                Assert.Equal(buf.Length, n);
                Assert.Equal(1, src.Reads);
                Assert.All(buf, v => Assert.Equal(0f, v));
            }
            finally
            {
                HapticToneService.MirrorEngagedBySlot[TestSlot] = true;
            }
        }

        [Fact]
        public void ReengagedAfterDisengage_ResumesPassthrough()
        {
            var src = new CountingSource();
            var gate = new HapticToneService.GatedMirrorSampleProvider(src, TestSlot);
            var buf = new float[16];

            HapticToneService.MirrorEngagedBySlot[TestSlot] = false;
            gate.Read(buf, 0, buf.Length);
            HapticToneService.MirrorEngagedBySlot[TestSlot] = true;
            gate.Read(buf, 0, buf.Length);

            Assert.Equal(2, src.Reads);
            Assert.All(buf, v => Assert.Equal(0.5f, v));
        }

        [Fact]
        public void RemoteSinkSlot_FailsOpen()
        {
            // Remote sinks carry Slot = -1 and can never acquire a mirror, so
            // this is defense-in-depth: an out-of-range slot must pass samples
            // through rather than throw or mute.
            var src = new CountingSource();
            var gate = new HapticToneService.GatedMirrorSampleProvider(src, -1);

            var buf = new float[16];
            int n = gate.Read(buf, 0, buf.Length);

            Assert.Equal(buf.Length, n);
            Assert.All(buf, v => Assert.Equal(0.5f, v));
        }

        [Fact]
        public void DefaultState_IsEngagedForEverySlot()
        {
            // A fresh slot with no engage config must play always: the
            // InputManager fast path re-asserts true, and the initial state
            // matches so the mirror never stutters before the first poll.
            // Slot 7 is toggled by other tests, so assert the untouched ones.
            Assert.True(HapticToneService.MirrorEngagedBySlot[0]);
            Assert.True(HapticToneService.MirrorEngagedBySlot[15]);
        }

        // ── HoldEngaged: the release-delay decision the poll updater uses ──

        [Fact]
        public void HoldEngaged_ActiveSource_EngagesAndStampsTick()
        {
            long last = 0;
            Assert.True(HapticToneService.HoldEngaged(active: true, nowTick: 1000, ref last, releaseMs: 500));
            Assert.Equal(1000, last);
        }

        [Fact]
        public void HoldEngaged_WithinReleaseWindow_StaysEngaged()
        {
            long last = 0;
            HapticToneService.HoldEngaged(true, 1000, ref last, 500);
            Assert.True(HapticToneService.HoldEngaged(false, 1400, ref last, 500));
            Assert.True(HapticToneService.HoldEngaged(false, 1500, ref last, 500)); // inclusive edge
        }

        [Fact]
        public void HoldEngaged_PastReleaseWindow_Disengages()
        {
            long last = 0;
            HapticToneService.HoldEngaged(true, 1000, ref last, 500);
            Assert.False(HapticToneService.HoldEngaged(false, 1501, ref last, 500));
        }

        [Fact]
        public void HoldEngaged_ReactivationInsideWindow_RestampsTick()
        {
            long last = 0;
            HapticToneService.HoldEngaged(true, 1000, ref last, 500);
            HapticToneService.HoldEngaged(true, 1400, ref last, 500);
            Assert.Equal(1400, last);
            Assert.True(HapticToneService.HoldEngaged(false, 1900, ref last, 500));
            Assert.False(HapticToneService.HoldEngaged(false, 1901, ref last, 500));
        }

        [Theory]
        [InlineData(-100, 0)]      // negative clamps to 0: no hold at all
        [InlineData(999999, 10000)] // absurd clamps to the UI's documented max
        public void HoldEngaged_ClampsReleaseDelay(int releaseMs, int effectiveMs)
        {
            long last = 0;
            HapticToneService.HoldEngaged(true, 1000, ref last, releaseMs);
            Assert.True(HapticToneService.HoldEngaged(false, 1000 + effectiveMs, ref last, releaseMs));
            Assert.False(HapticToneService.HoldEngaged(false, 1000 + effectiveMs + 1, ref last, releaseMs));
        }
    }
}
