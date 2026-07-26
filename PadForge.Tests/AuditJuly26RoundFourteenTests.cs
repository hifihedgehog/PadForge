using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round fourteen.
    ///
    /// <para>Round thirteen RECORDED a defect in the force-feedback
    /// directional lane and deliberately did not fix it, pending hardware.
    /// Round fourteen attacked that finding instead of trusting it, and it
    /// survived: ConstantForceEvaluator.Resolve fills the caller's scratch,
    /// sets HasDirectionalData on it, and returns THAT OBJECT. The per-slot
    /// loop in ApplyForceFeedback captured the returned reference as
    /// "the first slot that has directional data" and then handed the same
    /// scratch to the next slot's Resolve, which overwrote it in place. Two
    /// slots each running a constant force therefore sent the LAST one's
    /// direction to the wheel, under a guard and a comment that both
    /// promise the FIRST.</para>
    ///
    /// <para>WHAT THIS FILE GUARDS, stated plainly so nobody mistakes it
    /// for more than it is: it pins the ALIASING PREMISE that makes the
    /// copy load-bearing, not the copy itself. ApplyForceFeedback is a
    /// private instance method needing a fully built InputManager with
    /// populated slot buffers, so reverting the fix to a bare reference
    /// assignment would NOT turn these red. Three tests this session
    /// passed while proving nothing, every one caught only by mutation, so
    /// the honest label goes here rather than in a report nobody
    /// rereads.</para></summary>
    public class AuditJuly26RoundFourteenTests
    {
        private static PadSetting Force(string x, string y) => new PadSetting
        {
            ConstantForceEnabled = "1",
            ConstantForceX = x,
            ConstantForceY = y,
        };

        /// <summary>The premise, half one: when the override fires the
        /// evaluator hands back the very scratch it was given, so the
        /// caller holds a reference into shared per-tick state rather than
        /// a private value.</summary>
        [Fact]
        public void Resolve_ReturnsTheCallersScratch_WhenTheOverrideFires()
        {
            var scratch = new Vibration();
            var result = ConstantForceEvaluator.Resolve(new Vibration(), Force("1.0", "0"), scratch);

            Assert.Same(scratch, result);
            Assert.True(result.HasDirectionalData);
        }

        /// <summary>The premise, half two, and the whole reason the
        /// per-slot loop must COPY. A reference captured from the first
        /// call is silently rewritten by the second, so a loop that keeps
        /// "the first slot's force" in a local ends up holding the last
        /// slot's numbers.</summary>
        [Fact]
        public void SecondResolve_RewritesTheFirstResultInPlace()
        {
            var scratch = new Vibration();
            var raw = new Vibration();   // all zero, so no game force wins

            var first = ConstantForceEvaluator.Resolve(raw, Force("1.0", "0"), scratch);
            ushort capturedAtFirstSlot = first.Direction;

            var second = ConstantForceEvaluator.Resolve(raw, Force("0", "-1.0"), scratch);

            // Sanity: the two configurations really do point different ways,
            // or the rest of this proves nothing.
            Assert.NotEqual(capturedAtFirstSlot, second.Direction);

            // THE HAZARD: `first` was never reassigned, yet it now reports
            // the second slot's direction.
            Assert.Equal(second.Direction, first.Direction);
            Assert.NotEqual(capturedAtFirstSlot, first.Direction);
        }

        /// <summary>The guard that keeps the copy honest: game-driven force
        /// wins per slot, and in that case Resolve returns the caller's raw
        /// object untouched. That object is the stable per-pad
        /// VibrationStates entry rather than a shared scratch, which is why
        /// the game-FFB path never showed this bug and why the fix had to
        /// stay a no-op for it.</summary>
        [Fact]
        public void GameForce_WinsAndReturnsRawUntouched()
        {
            var scratch = new Vibration();
            var raw = new Vibration { LeftMotorSpeed = 4242 };

            var result = ConstantForceEvaluator.Resolve(raw, Force("1.0", "0"), scratch);

            Assert.Same(raw, result);
            Assert.False(scratch.HasDirectionalData);
        }
    }
}
