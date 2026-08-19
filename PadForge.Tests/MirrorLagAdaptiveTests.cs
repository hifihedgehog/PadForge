using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    // Locks the adaptive mirror cushion (#325, discussion #320 by
    // Jobima1st): the proportional trim's deadband, scaling, clamp, and
    // sign, plus the escalation/decay ladder's floor, ceiling, and steps.
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
            // buffer is now sized from the same constant as the clamp;
            // this pins the coupling so they can never diverge again.
            int worstTrim = AudioPassthroughService.ComputeLagTrim(
                int.MaxValue / 2, 0, 240);
            Assert.Equal(AudioPassthroughService.BtMaxTrimFrames, worstTrim);
            int worstReadFloats = (512 + worstTrim) * 2;   // BtPullFrames + trim
            Assert.True(worstReadFloats <= AudioPassthroughService.MaxTickReadFloats,
                $"a max-trim tick reads {worstReadFloats} floats into a "
                + $"{AudioPassthroughService.MaxTickReadFloats}-float buffer");
        }

        [Fact]
        public void Trim_HoldsInsideTheDeadband()
        {
            Assert.Equal(0, AudioPassthroughService.ComputeLagTrim(960, 960, 240));
            Assert.Equal(0, AudioPassthroughService.ComputeLagTrim(1199, 960, 240));
            Assert.Equal(0, AudioPassthroughService.ComputeLagTrim(721, 960, 240));
        }

        [Fact]
        public void Trim_ScalesWithTheErrorAndClamps()
        {
            // A small breach trims gently, a large one hits the clamp:
            // recovery from a real burst takes ticks, not seconds.
            Assert.Equal(4, AudioPassthroughService.ComputeLagTrim(960 + 240, 960, 240));
            Assert.Equal(12, AudioPassthroughService.ComputeLagTrim(960 + 2000, 960, 240));
            Assert.Equal(-4, AudioPassthroughService.ComputeLagTrim(960 - 240, 960, 240));
            Assert.Equal(-12, AudioPassthroughService.ComputeLagTrim(28, 960, 240));
        }

        [Fact]
        public void Trim_FollowsTheLiveTarget()
        {
            // An escalated target moves the whole band: the same lag that
            // reads high against the floor reads low against a raised
            // target, which is what pulls the cushion up after underruns.
            Assert.Equal(12, AudioPassthroughService.ComputeLagTrim(2000, 960, 240));
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
        public void Decay_StepsOneMsPerCleanSecond_AndStopsAtTheFloor()
        {
            int t = 1440;
            t = AudioPassthroughService.DecayLagTarget(t);
            Assert.Equal(1392, t);
            for (int i = 0; i < 1000; i++) t = AudioPassthroughService.DecayLagTarget(t);
            Assert.Equal(960, t);
        }
    }
}
