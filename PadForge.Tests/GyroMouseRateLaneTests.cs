using System;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// <para>Gyro-to-mouse is a RATE, not a deflection (#79). It used to ride
    /// the [-1..+1] axis lane, which broke it two ways: it SATURATED at
    /// 500 deg/s (GyroScale's full scale, then a hard clamp) while an
    /// ordinary aiming flick is 300-800 and a fast one passes 1500, and it
    /// was CADENCE-COUPLED, spending a fixed 15 px per poll so the same wrist
    /// motion travelled sixteen times as far at 1 ms as at 16 ms.</para>
    /// <para>DS4Windows, the reference issue #79 named, multiplies the raw
    /// gyro value by elapsed time with no full scale and no clamp, adds a
    /// constant offset so the smallest rotation still registers, and bends
    /// the tremor band with a power curve instead of cutting it
    /// (MouseCursor.cs sixaxisMoved). JoyShockMapper agrees on the structure
    /// (main.cpp:1124). These pin all of it.</para>
    /// </summary>
    public class GyroMouseRateLaneTests
    {
        private const float DegToRad = (float)(Math.PI / 180.0);
        private const float NominalDt = 0.001f;

        private static CustomInputState StateWithYaw(float degPerSec)
        {
            var s = new CustomInputState();
            s.Gyro[1] = degPerSec * DegToRad;   // yaw lane
            return s;
        }

        private static MappingSource YawSource() => new() { Descriptor = "Gyro Yaw" };

        private static float CountsX(float degPerSec, float dt = NominalDt)
        {
            var (x, _) = SourceCoercion.ReadGyroMouseCounts(
                StateWithYaw(degPerSec), YawSource(), slotIndex: -1,
                deviceGuid: "", dtSeconds: dt, forX: true);
            return x;
        }

        // ── the ceiling is gone ───────────────────────────────────────────

        [Fact]
        public void RotationPastTheOldCeiling_KeepsProducingMoreMotion()
        {
            // The defect, stated as a test: at 500 deg/s the old lane hit its
            // clamp and everything faster produced the same motion.
            float at500 = CountsX(500f);
            float at1000 = CountsX(1000f);
            float at2000 = CountsX(2000f);

            Assert.True(at500 > 0f, "no motion at the old full scale; harness is wrong");
            Assert.True(at1000 > at500 * 1.5f,
                $"1000 deg/s produced {at1000} against {at500} at 500: still saturating");
            Assert.True(at2000 > at1000 * 1.5f,
                $"2000 deg/s produced {at2000} against {at1000} at 1000: still saturating");
        }

        [Fact]
        public void PositiveControl_TheDeflectionLaneStillSaturates()
        {
            // Proves the test above measures something. The deflection lane
            // is unchanged and correct for its own targets: a stick cannot
            // deflect past full, so clamping there is right. It is only
            // wrong for a mouse, and this pins WHY gyro had to leave it.
            float at500 = SourceCoercion.EvaluateForBipolarAxisTarget(StateWithYaw(500f), YawSource(), -1);
            float at2000 = SourceCoercion.EvaluateForBipolarAxisTarget(StateWithYaw(2000f), YawSource(), -1);
            Assert.Equal(1f, Math.Abs(at500), 3);
            Assert.Equal(Math.Abs(at500), Math.Abs(at2000), 6);

            // The rate lane, same two rotations, does not.
            Assert.True(CountsX(2000f) > CountsX(500f) * 3f);
        }

        [Fact]
        public void MotionIsLinearInRotationRate_AwayFromTheTremorBand()
        {
            // Above the jitter threshold the reference passes the value
            // through untouched, so doubling the rate doubles the motion.
            // The constant offset is a fixed addend, so compare differences.
            float a = CountsX(400f), b = CountsX(800f), c = CountsX(1600f);
            float d1 = b - a, d2 = c - b;
            Assert.True(Math.Abs(d2 / d1 - 2f) < 0.02f,
                $"not linear: steps were {d1} then {d2}");
        }

        // ── time, not polls ───────────────────────────────────────────────

        [Fact]
        public void MotionScalesWithElapsedTime_NotPollCount()
        {
            // Four polls at 1 ms must travel the same distance as one poll at
            // 4 ms. The old lane spent a fixed amount per poll, so the same
            // wrist motion moved four times as far at the faster cadence.
            float fourFast = 4f * CountsX(600f, 0.001f);
            float oneSlow = CountsX(600f, 0.004f);
            // Offset is per-emission, so discount the three extra it adds.
            float offsetPerEmission = CountsX(0.0001f);
            Assert.True(Math.Abs((fourFast - 3f * offsetPerEmission) - oneSlow) < 0.05f,
                $"cadence-coupled: 4x1ms gave {fourFast}, 1x4ms gave {oneSlow}");
        }

        [Fact]
        public void TheDefaultCadenceIsUnchangedFromTheOldLane()
        {
            // Calibration contract: at PollingRateMs = 1 and below the old
            // ceiling, the rate lane must spend what the deflection lane
            // spent, or every tuned profile changes feel. Old lane:
            // (rate * GyroScale) clamped, times 15 px.
            const float rate = 250f;                       // well under the ceiling
            float oldAxis = rate / 500f;                   // GyroScale normalization
            float oldCounts = oldAxis * 15.0f;             // KBM per-poll spend
            float newCounts = CountsX(rate) - CountsX(0.0001f);  // minus the offset addend
            Assert.True(Math.Abs(newCounts - oldCounts) < 0.01f,
                $"calibration drifted: old lane {oldCounts}, rate lane {newCounts}");
        }

        // ── the low-speed floor ───────────────────────────────────────────

        [Fact]
        public void TheSmallestRotationStillMovesTheCursor()
        {
            // DS4Windows adds a constant offset (mouseOffset = 0.2) so slow
            // gyro cannot vanish into the sub-count remainder. Without it a
            // tiny rotation accumulates forever and never crosses 1.
            float tiny = CountsX(0.5f);
            Assert.True(tiny > 0f, "a slow rotation produced no motion at all");
            Assert.True(tiny >= 0.1f,
                $"a slow rotation produced {tiny} counts, too small to ever reach a pixel");
        }

        [Fact]
        public void ZeroRotationProducesExactlyZero()
        {
            // The offset must ride ON motion, never create it: a resting
            // controller that emitted 0.2 counts a poll would drift.
            Assert.Equal(0f, CountsX(0f));
            var (x, y) = SourceCoercion.ReadGyroMouseCounts(
                new CustomInputState(), YawSource(), -1, "", NominalDt, true);
            Assert.Equal(0f, x);
            Assert.Equal(0f, y);
        }

        [Fact]
        public void MotionIsSignPreserving()
        {
            Assert.True(CountsX(600f) > 0f);
            Assert.True(CountsX(-600f) < 0f);
            Assert.Equal(CountsX(600f), -CountsX(-600f), 4);
        }

        // ── axis routing ──────────────────────────────────────────────────

        [Fact]
        public void ForXAndForY_RouteToTheirOwnAxisOnly()
        {
            var (x, y) = SourceCoercion.ReadGyroMouseCounts(
                StateWithYaw(600f), YawSource(), -1, "", NominalDt, forX: true);
            Assert.True(x != 0f);
            Assert.Equal(0f, y);

            var (x2, y2) = SourceCoercion.ReadGyroMouseCounts(
                StateWithYaw(600f), YawSource(), -1, "", NominalDt, forX: false);
            Assert.Equal(0f, x2);
            Assert.True(y2 != 0f);
        }

        [Fact]
        public void ANonGyroSource_ProducesNothingOnThisLane()
        {
            // The lane is gyro-only; a stick on the same row must go through
            // the deflection path, not get read as a rate.
            var (x, y) = SourceCoercion.ReadGyroMouseCounts(
                StateWithYaw(600f), new MappingSource { Descriptor = "LeftThumbAxisX" },
                -1, "", NominalDt, true);
            Assert.Equal(0f, x);
            Assert.Equal(0f, y);
        }

        [Fact]
        public void AGyroLeanSource_ProducesNothingOnThisLane()
        {
            // Lean is a POSITION read (a tilt angle), not a rate, so it has
            // no business on a rate-to-counts lane.
            var (x, _) = SourceCoercion.ReadGyroMouseCounts(
                StateWithYaw(600f), new MappingSource { Descriptor = "Gyro Lean X" },
                -1, "", NominalDt, true);
            Assert.Equal(0f, x);
        }

        // ── the double-count guard ────────────────────────────────────────

        [Fact]
        public void WhileTheMouseLaneIsScoped_GyroReadsZeroAsADeflection()
        {
            // Gyro is counted once, on the rate lane. If it also contributed
            // to the mouse target's deflection combine the cursor would move
            // twice as far, which is worse than the bug this replaced.
            var st = StateWithYaw(300f);
            var src = YawSource();

            float unscoped = SourceCoercion.EvaluateForBipolarAxisTarget(st, src, -1);
            Assert.True(Math.Abs(unscoped) > 0f, "harness read no deflection to suppress");

            using (new SourceCoercion.GyroMouseLaneScope(true))
                Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(st, src, -1));

            // And the scope restores, so a later stick target is unaffected.
            Assert.Equal(unscoped, SourceCoercion.EvaluateForBipolarAxisTarget(st, src, -1), 4);
        }

        [Fact]
        public void TheRateLaneReadsThroughItsOwnSuppression()
        {
            // The rate read happens inside Step 3, which may already be
            // inside a scoped combine. It must clear the flag for itself or
            // it would suppress the very lane it exists to feed.
            using (new SourceCoercion.GyroMouseLaneScope(true))
            {
                var (x, _) = SourceCoercion.ReadGyroMouseCounts(
                    StateWithYaw(600f), YawSource(), -1, "", NominalDt, true);
                Assert.True(x != 0f, "the rate lane suppressed itself");
            }
        }
    }
}
