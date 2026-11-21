using PadForge.Engine.Common.Mapping;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the poll-frame gate on SourceCoercion's smoothing caches: the
    /// filters advance once per poll no matter how many mapping rows read
    /// the same source, so the Gyro group's per-(device, slot) smoothing
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

        [Fact]
        public void DualThreshold_EachReadIsTheMeanOfTheWindowsSamples()
        {
            // The ring keeps its sums as samples come and go, and takes
            // them from the samples again once a turn. Over seven turns of
            // a 50-sample ring, with a large sample among small ones every
            // so often, each read is the mean of the samples then in the
            // window.
            SourceCoercion.PollHzProvider = () => 1000f;
            try
            {
                var tuning = new SourceCoercion.GyroTuning
                {
                    TighteningRadPerSec = 1e6f, // every rate stays below: fully smoothed
                    SmoothingThresholdRadPerSec = 2e6f,
                    SmoothingWindowSeconds = 0.05f, // 50 samples at 1000 Hz
                };
                const int samples = 50;
                var window = new System.Collections.Generic.Queue<float>();
                var random = new System.Random(20261116);
                for (int frame = 0; frame < samples * 7 + 13; frame++)
                {
                    SourceCoercion.BeginPollFrame();
                    float rate = (float)((random.NextDouble() - 0.5) * (frame % 17 == 0 ? 4000 : 6));
                    window.Enqueue(rate);
                    if (window.Count > samples) window.Dequeue();
                    var (yaw, pitch) = SourceCoercion.ApplyDualThresholdSmoothing(
                        "ring-sum-dev", 0, rate, -rate, tuning);
                    double mean = 0;
                    foreach (float v in window) mean += v;
                    mean /= samples;
                    Assert.Equal(mean, yaw, 3);
                    Assert.Equal(-mean, pitch, 3);
                }
            }
            finally
            {
                SourceCoercion.PollHzProvider = null;
            }
        }

        [Fact]
        public void DualThreshold_ASampleThatIsNotANumberLeavesWithTheWindow()
        {
            // Nothing subtracts back out of not-a-number, so in sums kept
            // as samples come and go one such sample would stay for good.
            // The sums are taken from the samples again once a turn of the
            // ring, so it is gone within two turns, as it was gone within
            // one when every read summed the window afresh.
            SourceCoercion.PollHzProvider = () => 1000f;
            try
            {
                var tuning = new SourceCoercion.GyroTuning
                {
                    TighteningRadPerSec = 1e6f,
                    SmoothingThresholdRadPerSec = 2e6f,
                    SmoothingWindowSeconds = 0.05f, // 50 samples at 1000 Hz
                };
                float read = 0f;
                for (int frame = 0; frame < 10 + 1 + 3 * 50; frame++)
                {
                    SourceCoercion.BeginPollFrame();
                    float rate = frame == 10 ? float.NaN : 2f;
                    (read, _) = SourceCoercion.ApplyDualThresholdSmoothing(
                        "ring-nan-dev", 0, rate, 0f, tuning);
                }
                Assert.Equal(2f, read, 4);
            }
            finally
            {
                SourceCoercion.PollHzProvider = null;
            }
        }
    }
}
