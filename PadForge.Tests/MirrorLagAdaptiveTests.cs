using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    // Locks the adaptive mirror cushion (#325, discussion #320 by
    // Jobima1st): the audible-phase trim's deadband, clamp, and sign,
    // plus the escalation/decay ladder's floor, ceiling, and steps.
    // The defect: a fixed 20 ms cushion with a fixed +/-4 trim collapsed
    // under bursty loopback delivery (a reporter's ring bottomed at 28
    // frames), and the zero-filled shortfall was the audible crack.
    [Collection("SettingsManagerStatics")]
    public class MirrorLagAdaptiveTests
    {
        [Fact]
        public void TickBuffer_HoldsTheMaximumPositiveTrim()
        {
            // The #325 regression: the trim clamp grew from 4 to 12 while
            // the pull buffer kept its +8-frame headroom, so a positive
            // trim past +8 overran the buffer, the tick's catch flagged
            // the sink TransportFailed, and the 5 s rebuild loop replayed
            // the crash forever (silent mirror, dead test tone). The
            // buffer is sized from a shared headroom constant that must
            // cover EVERY lane's trim; this pins the coupling so they can
            // never diverge again.
            int worstTrim = AudioPassthroughService.ComputeLagTrim(
                int.MaxValue / 2, 0, 240);
            Assert.Equal(AudioPassthroughService.BtMaxTrimFrames, worstTrim);
            Assert.True(AudioPassthroughService.BtMaxTrimFrames
                <= AudioPassthroughService.BtBufferHeadroomFrames,
                "the audible-phase trim clamp exceeds the tick buffer headroom");
            Assert.True(AudioPassthroughService.BtPeerTrimFrames
                <= AudioPassthroughService.BtBufferHeadroomFrames,
                "the peer lane trim exceeds the tick buffer headroom");
            int worstReadFloats = (512 + Math.Max(worstTrim,
                AudioPassthroughService.BtPeerTrimFrames)) * 2;
            Assert.True(worstReadFloats <= AudioPassthroughService.MaxTickReadFloats,
                $"a max-trim tick reads {worstReadFloats} floats into a "
                + $"{AudioPassthroughService.MaxTickReadFloats}-float buffer");
        }

        [Fact]
        public void SilentRecenter_FiresOnlyOutsideTheDeadband()
        {
            // Silence steering is free (a cursor jump over silent content),
            // but a steady silence must leave the cursor alone so a sound
            // onset starts from a stable, centered cushion.
            Assert.False(AudioPassthroughService.ShouldRecenterInSilence(960, 960, 240));
            Assert.False(AudioPassthroughService.ShouldRecenterInSilence(1199, 960, 240));
            Assert.False(AudioPassthroughService.ShouldRecenterInSilence(721, 960, 240));
            Assert.True(AudioPassthroughService.ShouldRecenterInSilence(1201, 960, 240));
            Assert.True(AudioPassthroughService.ShouldRecenterInSilence(719, 960, 240));
            Assert.True(AudioPassthroughService.ShouldRecenterInSilence(0, 4800, 240));
        }

        [Fact]
        public void Trim_HoldsInsideTheDeadband()
        {
            Assert.Equal(0, AudioPassthroughService.ComputeLagTrim(960, 960, 240));
            Assert.Equal(0, AudioPassthroughService.ComputeLagTrim(1199, 960, 240));
            Assert.Equal(0, AudioPassthroughService.ComputeLagTrim(721, 960, 240));
        }

        [Fact]
        public void Trim_ClampsAtTheInaudibleLevel()
        {
            // The audible-phase clamp is +/-1 (0.2%, ~3.5 cents, under the
            // pitch JND for steady tones): the trim IS a per-tick pitch
            // bend through the 16:15 compressor, and both 12 (+/-2.3%) and
            // 4 (+/-0.8%) were audible wobble on the test tone whenever
            // the adaptive target walked the error across the deadband.
            // Bursts are the adaptive TARGET's job, and cushion placement
            // is corrected exactly while the stream is silent, so drift is
            // all the trim has left to null.
            Assert.Equal(1, AudioPassthroughService.ComputeLagTrim(960 + 240, 960, 240));
            Assert.Equal(1, AudioPassthroughService.ComputeLagTrim(960 + 2000, 960, 240));
            Assert.Equal(-1, AudioPassthroughService.ComputeLagTrim(960 - 240, 960, 240));
            Assert.Equal(-1, AudioPassthroughService.ComputeLagTrim(28, 960, 240));
        }

        [Fact]
        public void Trim_FollowsTheLiveTarget()
        {
            // An escalated target moves the whole band: the same lag that
            // reads high against the floor reads low against a raised
            // target, which is what pulls the cushion up after underruns.
            Assert.Equal(1, AudioPassthroughService.ComputeLagTrim(2000, 960, 240));
            Assert.True(AudioPassthroughService.ComputeLagTrim(2000, 4800, 240) < 0);
        }

        [Fact]
        public void Escalation_StepsTenMs_AndStopsAtTheCeiling()
        {
            int t = 960;
            t = AudioPassthroughService.EscalateLagTarget(t);
            Assert.Equal(1440, t);
            for (int i = 0; i < 50; i++) t = AudioPassthroughService.EscalateLagTarget(t);
            Assert.Equal(4800, t);
        }

        [Fact]
        public void Decay_StepsOneMsPerSilentSecond_AndStopsAtTheFloor()
        {
            int t = 1440;
            t = AudioPassthroughService.DecayLagTarget(t);
            Assert.Equal(1392, t);
            for (int i = 0; i < 1000; i++) t = AudioPassthroughService.DecayLagTarget(t);
            Assert.Equal(960, t);
        }
    }
}
