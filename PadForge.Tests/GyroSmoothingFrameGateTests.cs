using PadForge.Engine.Common.Mapping;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the poll-frame gate on SourceCoercion's smoothing caches: the
    /// filters advance once per poll no matter how many mapping rows read
    /// the same source, so the Gyro tab's per-(device, slot) smoothing
    /// settings deliver what they claim regardless of row count. Each test
    /// uses unique device keys because the caches are process-static.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class GyroSmoothingFrameGateTests
    {
        [Fact]
        public void LegacyEma_SecondReadInSamePoll_DoesNotAdvance()
        {
            SourceCoercion.BeginPollFrame();
            float first = SourceCoercion.ApplyGyroSmoothing("ema-gate-dev", 0, 0, 10f, 0.5f);
            float second = SourceCoercion.ApplyGyroSmoothing("ema-gate-dev", 0, 0, 10f, 0.5f);
            Assert.Equal(first, second); // re-served, not filtered twice

            SourceCoercion.BeginPollFrame();
            float next = SourceCoercion.ApplyGyroSmoothing("ema-gate-dev", 0, 0, 10f, 0.5f);
            Assert.NotEqual(first, next); // a new poll advances the filter
        }

        [Fact]
        public void LegacyEma_TwoSlots_KeepIndependentStates()
        {
            SourceCoercion.BeginPollFrame();
            SourceCoercion.ApplyGyroSmoothing("ema-slots-dev", 0, 0, 10f, 0.5f);
            // Slot 1 starts from its own zero state, unaffected by slot 0's step.
            float slot1 = SourceCoercion.ApplyGyroSmoothing("ema-slots-dev", 1, 0, 10f, 0.9f);
            Assert.Equal(10f * (1f - 0.9f), slot1, 3);
        }

        [Fact]
        public void DualThreshold_TwoRowsPerPoll_MatchesOneRowPerPoll()
        {
            // The core promise: row count no longer changes the smoothing.
            // Feed the same rate series through a one-read-per-poll key and
            // a two-reads-per-poll key; the outputs must be identical.
            SourceCoercion.PollHzProvider = () => 100f;
            try
            {
                var tuning = new SourceCoercion.GyroTuning
                {
                    TighteningRadPerSec = 100f, // rate stays below: fully smoothed
                    SmoothingThresholdRadPerSec = 200f,
                    SmoothingWindowSeconds = 0.05f, // 5 samples at 100 Hz
                };

                float lastSingle = 0f, lastDouble = 0f;
                for (int frame = 0; frame < 10; frame++)
                {
                    SourceCoercion.BeginPollFrame();
                    float rate = 1f + frame; // varying input
                    (lastSingle, _) = SourceCoercion.ApplyDualThresholdSmoothing(
                        "ring-single-dev", 0, rate, 0f, tuning);
                    SourceCoercion.ApplyDualThresholdSmoothing(
                        "ring-double-dev", 0, rate, 0f, tuning);
                    (lastDouble, _) = SourceCoercion.ApplyDualThresholdSmoothing(
                        "ring-double-dev", 0, rate, 0f, tuning);
                }
                Assert.Equal(lastSingle, lastDouble, 5);
            }
            finally
            {
                SourceCoercion.PollHzProvider = null;
            }
        }
    }
}
