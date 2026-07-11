using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Stick-trim combine level math (#155): the row's last source is a
    /// trim stick whose signed deflection slides a stored trigger level.
    /// SDL's raw stick convention (up = negative) reaches the trim read
    /// unflipped, so NEGATIVE trim raises the level.
    /// </summary>
    public class StickTrimTests
    {
        [Fact]
        public void InsideDeadzone_LeavesLevelAlone()
        {
            Assert.Equal(0.8f, InputManager.AdvanceStickTrimLevel(0.8f, 0.24f, 25, 100, 0.5), 5);
            Assert.Equal(0.8f, InputManager.AdvanceStickTrimLevel(0.8f, -0.25f, 25, 100, 0.5), 5);
            Assert.Equal(0.8f, InputManager.AdvanceStickTrimLevel(0.8f, 0f, 25, 100, 0.5), 5);
        }

        [Fact]
        public void StickUp_Negative_RaisesLevel()
        {
            float after = InputManager.AdvanceStickTrimLevel(0.5f, -1f, 0, 100, 0.25);
            Assert.Equal(0.75f, after, 5);
        }

        [Fact]
        public void StickDown_Positive_LowersLevel()
        {
            float after = InputManager.AdvanceStickTrimLevel(0.5f, 1f, 0, 100, 0.25);
            Assert.Equal(0.25f, after, 5);
        }

        [Fact]
        public void FullDeflection_SweepsFullRangeInOneSecond_AtRate100()
        {
            Assert.Equal(1f, InputManager.AdvanceStickTrimLevel(0f, -1f, 0, 100, 1.0), 5);
            Assert.Equal(0f, InputManager.AdvanceStickTrimLevel(1f, 1f, 0, 100, 1.0), 5);
        }

        [Fact]
        public void SpeedRescalesFromZero_AtDeadzoneEdge()
        {
            // Just past a 25% deadzone: effective deflection ~0, so the
            // level barely moves even across a long dt.
            float after = InputManager.AdvanceStickTrimLevel(0.5f, -0.26f, 25, 100, 1.0);
            Assert.True(after - 0.5f < 0.02f, $"expected near-zero speed at deadzone edge, moved {after - 0.5f}");

            // Halfway between deadzone edge and full: half speed.
            float half = InputManager.AdvanceStickTrimLevel(0.5f, -0.625f, 25, 100, 0.5);
            Assert.Equal(0.75f, half, 2);
        }

        [Fact]
        public void Clamps_AtBothEnds()
        {
            Assert.Equal(1f, InputManager.AdvanceStickTrimLevel(0.95f, -1f, 0, 400, 1.0), 5);
            Assert.Equal(0f, InputManager.AdvanceStickTrimLevel(0.05f, 1f, 0, 400, 1.0), 5);
        }

        [Fact]
        public void RateScales_TwoHundredPercent_DoubleSpeed()
        {
            float after = InputManager.AdvanceStickTrimLevel(0f, -1f, 0, 200, 0.25);
            Assert.Equal(0.5f, after, 5);
        }
    }
}
