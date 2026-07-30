using System;
using PadForge.Engine.Common.Mapping;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Trackpad pointer profile is a PORT, so these tests check it against
    /// the reference rather than against taste.
    ///
    /// <para>Reference: libinput <c>src/filter-touchpad.c</c>,
    /// <c>touchpad_accel_profile_linear</c>, cloned to
    /// GitHub/libinput. Its shape is "a double incline with a plateau":
    /// deceleration to 0.3x below 7 mm/s, a 0.9 baseline plateau, then an
    /// incline capped at 4x the threshold.</para>
    ///
    /// <para>The port returns gain NORMALIZED to the 0.9 plateau. libinput
    /// returns an absolute scale (times speed_factor and TP_MAGIC_SLOWDOWN
    /// 0.2968) that is entangled with its own delta normalization, a job
    /// PadForge already does through MouseSensitivityX/Y. So every expected
    /// value below is libinput's factor divided by its 0.9 baseline, and the
    /// division is stated in each case rather than pre-computed, so a reader
    /// can check it against the reference line by line.</para>
    /// </summary>
    public class TrackpadPointerGainTests
    {
        private const float Thr = 130f;      // libinput: filter->threshold = 130
        private const float PadMm = 69f;     // libinput: touchpad_width_mm = 69

        /// <summary>Pad-widths/sec that produce a given mm/s, so each case can
        /// be written in the reference's own units.</summary>
        private static float AtMmPerSec(float mm) => mm / PadMm;

        private static float Gain(float mm) => SourceCoercion.TrackpadPointerGain(AtMmPerSec(mm), Thr, PadMm);

        [Fact]
        public void AtRest_DeceleratesToOneThird()
        {
            // libinput: factor = min(0.9, 0.1 * 0 + 0.3) = 0.3
            // normalized: 0.3 / 0.9
            Assert.Equal(0.3f / 0.9f, Gain(0f), 4);

            // This is the whole reason the profile exists: a value BELOW 1,
            // which neither Flat nor Simple can produce.
            Assert.True(Gain(0f) < 1f);
        }

        [Theory]
        // libinput: factor = min(0.9, 0.1 * speed + 0.3), for speed < 7
        [InlineData(1f, 0.4f)]     // 0.1*1 + 0.3
        [InlineData(3f, 0.6f)]     // 0.1*3 + 0.3
        [InlineData(6f, 0.9f)]     // 0.1*6 + 0.3 = 0.9, meets the baseline
        public void BelowTheDecelKnee_IsTheLinearRamp(float mm, float libinputFactor)
        {
            Assert.Equal(libinputFactor / 0.9f, Gain(mm), 4);
        }

        [Fact]
        public void TheRampIsClampedAtTheBaseline_NotAllowedToExceedIt()
        {
            // libinput wraps the ramp in min(baseline, ...), so 0.1x + 0.3
            // cannot climb past 0.9 even though the line would. At 6.9 mm/s the
            // raw line gives 0.99; the clamp holds it at 0.9.
            Assert.Equal(1.0f, Gain(6.9f), 4);
        }

        [Theory]
        [InlineData(7f)]        // exactly the knee: the plateau branch begins
        [InlineData(50f)]
        [InlineData(129.9f)]    // just under the threshold
        public void OnThePlateau_GainIsExactlyOne(float mm)
        {
            // libinput: factor = baseline. Normalized, the plateau IS 1.0, so a
            // normal-speed drag is bit-for-bit what it was before the profile
            // existed. That is what makes this safe to switch on.
            Assert.Equal(1.0f, Gain(mm), 4);
        }

        [Theory]
        // libinput: 0.0025 * (v/thr) * (v - thr) + 0.9
        [InlineData(260f)]      // 2x threshold
        [InlineData(390f)]      // 3x
        [InlineData(520f)]      // 4x, the cap
        public void AboveTheThreshold_MatchesTheReferenceIncline(float mm)
        {
            double expected = (0.0025 * (mm / Thr) * (mm - Thr) + 0.9) / 0.9;
            Assert.Equal(expected, Gain(mm), 3);
        }

        [Fact]
        public void AtTheCap_GainIsTheReferenceMaximum()
        {
            // 0.0025 * 4 * (520 - 130) + 0.9 = 3.9 + 0.9 = 4.8
            // normalized: 4.8 / 0.9 = 5.333...
            Assert.Equal(4.8f / 0.9f, Gain(520f), 3);
        }

        [Fact]
        public void PastTheCap_Plateaus()
        {
            // libinput: speed_in = min(speed_in, upper_threshold), because past
            // 4x "you're moving so fast that extra acceleration doesn't help".
            float atCap = Gain(520f);
            Assert.Equal(atCap, Gain(1000f), 4);
            Assert.Equal(atCap, Gain(100000f), 4);
        }

        [Fact]
        public void TheCurveIsMonotonic()
        {
            // Non-monotonic gain would make the cursor stutter as the finger
            // speeds up. Swept finely enough to cross both branch boundaries.
            float prev = -1f;
            for (float mm = 0f; mm <= 700f; mm += 0.25f)
            {
                float g = Gain(mm);
                Assert.True(g >= prev - 1e-5f, $"gain fell at {mm} mm/s: {g} after {prev}");
                prev = g;
            }
        }

        [Fact]
        public void SpansTheReferencesFullDynamicRange()
        {
            // 0.333x to 5.333x, a 16x span between the finest and fastest
            // response. Flat is 1.0 throughout and Simple starts at 1.0, so
            // this range is exactly what the other two profiles cannot reach.
            Assert.Equal(0.3f / 0.9f, Gain(0f), 4);
            Assert.Equal(4.8f / 0.9f, Gain(520f), 3);
            Assert.True(Gain(520f) / Gain(0f) > 15.9f);
        }

        [Fact]
        public void ThresholdMovesTheKnee_AndIsTheCalibrationKnob()
        {
            // The threshold has to be a setting: converting a gamepad pad's
            // normalized coordinates to mm/s needs a physical width nobody
            // publishes, so it is calibrated by feel. Halving it must make the
            // same finger speed accelerate more.
            float slowThr = SourceCoercion.TrackpadPointerGain(AtMmPerSec(200f), 130f, PadMm);
            float fastThr = SourceCoercion.TrackpadPointerGain(AtMmPerSec(200f), 65f, PadMm);
            Assert.True(fastThr > slowThr, $"lower threshold did not accelerate sooner: {fastThr} vs {slowThr}");
        }

        [Fact]
        public void AnInvalidThresholdFallsBackToTheReferenceDefault()
        {
            // A hand-edited XML could carry 0 or a negative. It must read as
            // libinput's 130 rather than dividing by zero.
            float expected = SourceCoercion.TrackpadPointerGain(AtMmPerSec(260f), 130f, PadMm);
            Assert.Equal(expected, SourceCoercion.TrackpadPointerGain(AtMmPerSec(260f), 0f, PadMm), 4);
            Assert.Equal(expected, SourceCoercion.TrackpadPointerGain(AtMmPerSec(260f), -5f, PadMm), 4);
        }

        [Fact]
        public void SpeedSignIsIrrelevant_GainIsAMagnitude()
        {
            // The caller applies one gain to both axes to keep a diagonal on
            // its line, so the function must not care about direction.
            Assert.Equal(Gain(260f), SourceCoercion.TrackpadPointerGain(-AtMmPerSec(260f), Thr, PadMm), 5);
        }


        // ── reachability: the finding that made the width a setting ────────

        [Fact]
        public void AtLibinputsAssumedWidth_TheDecelerationKneeIsOutOfReach()
        {
            // A pad cannot report a speed slower than one coordinate unit per
            // report. On a DS4 (1920 units, ~250 Hz) at libinput's assumed
            // 69 mm that quantum is ~8.98 mm/s, ABOVE the 7 mm/s knee, so the
            // entire precision half of the curve is dead and the profile can
            // only ever accelerate: the exact failure it exists to fix.
            //
            // Recorded as a test because it is the one thing about this feature
            // that cannot be seen by reading the curve. It was found by a lane
            // test failing, not by inspection.
            float quantum = SourceCoercion.TrackpadSpeedQuantumMmPerSec(
                padWidthMm: 69f, unitsAcrossPad: 1920, reportIntervalSec: 0.004f);

            Assert.InRange(quantum, 8.9f, 9.1f);
            Assert.True(quantum > 7f,
                "the knee is reachable at 69 mm after all; this test's premise is stale");

            // And the consequence, measured through the curve itself: the
            // slowest reportable movement already sits on the plateau.
            float gainAtQuantum = SourceCoercion.TrackpadPointerGain(
                quantum / 69f, Thr, 69f);
            Assert.Equal(1.0f, gainAtQuantum, 4);
        }

        [Fact]
        public void ANarrowerConfiguredWidthBringsThePrecisionRegionIntoRange()
        {
            // The fix available to the user. Below roughly 54 mm the same
            // one-unit-per-report quantum falls under the knee, so slow
            // movement decelerates and the profile does what it promises.
            const float narrow = 45f;
            float quantum = SourceCoercion.TrackpadSpeedQuantumMmPerSec(
                padWidthMm: narrow, unitsAcrossPad: 1920, reportIntervalSec: 0.004f);
            Assert.True(quantum < 7f, $"quantum {quantum} mm/s still above the knee");

            float gain = SourceCoercion.TrackpadPointerGain(quantum / narrow, Thr, narrow);
            Assert.True(gain < 1.0f, $"slowest movement was not decelerated: gain {gain}");
        }

        [Fact]
        public void TheCrossoverWidthIsWhereTheQuantumMeetsTheKnee()
        {
            // Pins the ~54 mm figure quoted in the setting's own docs, so the
            // number and the code cannot drift apart.
            float below = SourceCoercion.TrackpadSpeedQuantumMmPerSec(53f, 1920, 0.004f);
            float above = SourceCoercion.TrackpadSpeedQuantumMmPerSec(55f, 1920, 0.004f);
            Assert.True(below < 7f, $"53 mm gave {below} mm/s, expected under the knee");
            Assert.True(above > 7f, $"55 mm gave {above} mm/s, expected over the knee");
        }

        [Fact]
        public void AnInvalidWidthFallsBackToTheReferenceAssumption()
        {
            float expected = SourceCoercion.TrackpadPointerGain(AtMmPerSec(260f), Thr, 69f);
            Assert.Equal(expected, SourceCoercion.TrackpadPointerGain(AtMmPerSec(260f), Thr, 0f), 4);
            Assert.Equal(expected, SourceCoercion.TrackpadPointerGain(AtMmPerSec(260f), Thr, -1f), 4);
        }

        [Fact]
        public void ZeroGuardsOnTheQuantumHelper()
        {
            Assert.Equal(0f, SourceCoercion.TrackpadSpeedQuantumMmPerSec(69f, 0, 0.004f));
            Assert.Equal(0f, SourceCoercion.TrackpadSpeedQuantumMmPerSec(69f, 1920, 0f));
        }

        [Fact]
        public void PositiveControl_TheAssumedPadWidthIsTheReferencesOwn()
        {
            // 69 mm is libinput's stated assumption for a touchpad that reports
            // no resolution (tp_init_default_resolution), which is our exact
            // case: the Linux PlayStation driver defines the DS4 pad in units
            // and never calls input_abs_set_res. If this constant drifts, every
            // expected value above silently moves with it.
            Assert.Equal(69f, SourceCoercion.TrackpadAssumedPadWidthMm);
        }
    }
}
