using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests
{
    // Locks the feedback lane's test-rumble merge (4.1.0 validation,
    // 2026-07-21): the preview's test rumble lives only in
    // VibrationStates, so the lane max-merges it into the game-written
    // inbound pack per voice. Idempotent for game rumble, additive for
    // test rumble.
    public class LfeMaxMergeTests
    {
        [Fact]
        public void TestRumbleAloneBecomesAudible()
        {
            // Inbound pack empty (no game rumble), test writes motors.
            long merged = LfeOutputState.MaxMerge(0L, 40000, 20000, 0, 0);
            Assert.Equal(40000, LfeOutputState.Low(merged));
            Assert.Equal(20000, LfeOutputState.High(merged));
            Assert.Equal(0, LfeOutputState.TriggerLeft(merged));
        }

        [Fact]
        public void GameRumbleMergesToItself()
        {
            // Game rumble fills both the pack and VibrationStates with
            // identical values; the merge must not amplify or distort.
            long pack = LfeOutputState.Pack(30000, 10000, 500, 600);
            long merged = LfeOutputState.MaxMerge(pack, 30000, 10000, 500, 600);
            Assert.Equal(pack, merged);
        }

        [Fact]
        public void PerVoiceMaxNotOverwrite()
        {
            // Each voice independently takes the larger side.
            long pack = LfeOutputState.Pack(30000, 0, 800, 0);
            long merged = LfeOutputState.MaxMerge(pack, 10000, 25000, 0, 900);
            Assert.Equal(30000, LfeOutputState.Low(merged));
            Assert.Equal(25000, LfeOutputState.High(merged));
            Assert.Equal(800, LfeOutputState.TriggerLeft(merged));
            Assert.Equal(900, LfeOutputState.TriggerRight(merged));
        }
    }
}
