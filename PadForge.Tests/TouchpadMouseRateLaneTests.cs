using System;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// <para>Touchpad-to-mouse recomputed its delta ONCE PER POLL from a
    /// finger position that only changes when a device report arrives. A
    /// DualSense pad reports near 250 Hz against a 1 kHz poll, so three polls
    /// in four saw no change and produced zero while the fourth carried the
    /// whole movement. That burst was then clamped to +/-1 (any delta past
    /// 1/128 of the pad saturated) and spent at the KBM controller's fixed
    /// 15 px per poll, so even the poll holding the motion could not deliver
    /// it. Quantised, clipped, and rationed: the stutter felt against Steam,
    /// reWASD and lizard mode.</para>
    /// <para>DS4Windows avoids it by being event driven, running TouchesMoved
    /// on the report with the device's own DeltaX (MouseCursor.cs). A poll
    /// loop does the equivalent: each report becomes a velocity, spent every
    /// poll until the next refreshes it.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class TouchpadMouseRateLaneTests
    {
        private const long Freq = 10_000_000;      // 100 ns ticks
        private const float PollDt = 0.001f;

        // The engine keys per-finger velocity and delta state by SLOT, and
        // both maps are static, so tests sharing a slot inherit each other's
        // last finger position and the next one reads a phantom jump.
        private static int _nextSlot = 900;
        private static int NewSlot() => System.Threading.Interlocked.Increment(ref _nextSlot);

        private static CustomInputState PadAt(float x, bool down = true)
        {
            var s = new CustomInputState();
            s.Touchpads = new[]
            {
                new TouchpadInputState
                {
                    MaxFingers = 2,
                    FingerX = new[] { x, 0f },
                    FingerY = new[] { 0.5f, 0f },
                    FingerPressure = new[] { 1f, 0f },
                    FingerDown = new[] { down, false },
                },
            };
            return s;
        }

        private static MappingSource XSource() => new() { Descriptor = "Touchpad 0 Finger 0 X" };
        private static long TicksAt(float seconds) => (long)(seconds * Freq);

        private static float Counts(CustomInputState st, long ticks, MappingSource src, int slot)
        {
            // The ball advances ONCE per poll frame and serves both axes from
            // that, so a poll never integrates twice. Each call here is one
            // poll, so it advances the frame exactly as the loop does.
            SourceCoercion.BeginPollFrame();
            var (x, _) = SourceCoercion.ReadTouchpadMouseCounts(
                st, src, slot, deviceGuid: "", dtSeconds: PollDt,
                forX: true, nowTicks: ticks, ticksPerSecond: Freq);
            return x;
        }

        /// <summary>The RELATIVE read, which is what a KBM mouse target uses.
        /// The same descriptor also serves an ABSOLUTE read for stick and
        /// extended-axis targets, chosen by this flag, so a test that omits
        /// it silently measures the wrong branch.</summary>
        private static float Deflection(CustomInputState st, MappingSource src, int slot)
        {
            // The delta is computed ONCE per poll frame and re-served within
            // it, so two reads in one frame give the second a stale 0. The
            // poll loop advances the frame each tick; a test that wants two
            // successive deltas has to do the same.
            SourceCoercion.BeginPollFrame();
            return SourceCoercion.EvaluateForBipolarAxisTarget(st, src, slot, relativeTouchpad: true);
        }

        // ── the stutter is gone ───────────────────────────────────────────

        [Fact]
        public void MotionContinuesOnPollsThatCarryNoNewReport()
        {
            // The defect, stated as a test. A finger moving steadily but
            // reporting at 250 Hz used to move the cursor one poll in four.
            var src = XSource(); int slot = NewSlot();
            Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
            Counts(PadAt(0.52f), TicksAt(0.004f), src, slot);

            float p1 = Counts(PadAt(0.52f), TicksAt(0.005f), src, slot);
            float p2 = Counts(PadAt(0.52f), TicksAt(0.006f), src, slot);
            float p3 = Counts(PadAt(0.52f), TicksAt(0.007f), src, slot);

            Assert.True(p1 > 0f, "a poll with no new report produced no motion: still bursting");
            Assert.True(p2 > 0f && p3 > 0f, "motion did not carry across the report gap");
            Assert.Equal(p1, p2, 4);
            Assert.Equal(p2, p3, 4);
        }

        [Fact]
        public void SettleJitter_CannotCreepTheCursorBackward()
        {
            // The owner-reported artifact (#291): a stopping fingertip
            // emits a few tiny backward deltas as it relaxes, and the old
            // fixed 25 ms velocity hold spent each one on the cursor long
            // after the pad went quiet, a visible few-pixel reverse step.
            // With the adaptive hold, a backward settle sample may only
            // bridge ~1.5 report gaps before the cursor stops.
            var src = XSource(); int slot = NewSlot();
            // A steady forward drag at a 4 ms report cadence.
            Counts(PadAt(0.500f), TicksAt(0.000f), src, slot);
            Counts(PadAt(0.520f), TicksAt(0.004f), src, slot);
            Counts(PadAt(0.540f), TicksAt(0.008f), src, slot);
            Counts(PadAt(0.560f), TicksAt(0.012f), src, slot);

            // The settle: a run of tiny BACKWARD deltas as the fingertip
            // relaxes. Enough of them to flush the drag samples out of the
            // ten-sample ring, which is what a real settle does, leaving a
            // jitter-scale backward mean.
            for (int i = 1; i <= 12; i++)
                Counts(PadAt(0.560f - i * 0.001f), TicksAt(0.012f + i * 0.004f), src, slot);

            // Silence. The mean is now jitter-scale (and backward), so the
            // speed-gated hold stops it after ~1.5 report gaps instead of
            // spending it for the full 25 ms window.
            float late1 = Counts(PadAt(0.548f), TicksAt(0.070f), src, slot);
            float late2 = Counts(PadAt(0.548f), TicksAt(0.074f), src, slot);
            Assert.Equal(0f, late1);
            Assert.Equal(0f, late2);
        }

        [Fact]
        public void AFling_SurvivesTheLiftGap_AndGlideOneStaysFrictionless()
        {
            // The owner-caught regression: a real lift trails the last
            // movement report by 10-20 ms (the finger decelerates leaving
            // the glass). The launch must still read the movement history
            // across that gap, and at glide 1.00 the coast must hold its
            // speed indefinitely.
            UseSettings(momentum: true, decay: 1.00f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.53f), TicksAt(0.004f), src, slot);
                Counts(PadAt(0.56f), TicksAt(0.008f), src, slot);
                // A quiet poll inside the 25 ms window: real-speed motion
                // keeps bridging, the shipped-for-months behavior (the
                // speed-gated hold only truncates jitter-scale spend).
                Assert.True(Counts(PadAt(0.56f), TicksAt(0.018f), src, slot) > 0f,
                    "the bridge stopped during real motion");
                // And the lift at 12 ms after the last report still
                // launches the fling.
                float c1 = Counts(PadAt(0.56f, down: false), TicksAt(0.020f), src, slot);
                Assert.True(c1 > 0f, "the lift gap swallowed the fling");

                // Frictionless: same counts a hundred polls later.
                float last = c1;
                for (int i = 0; i < 100; i++)
                    last = Counts(PadAt(0.56f, down: false), TicksAt(0.021f + i * 0.001f), src, slot);
                Assert.Equal(c1, last, 4);
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void TheAdaptiveHold_StillBridgesTheReportGap()
        {
            // The stutter fix must survive: polls BETWEEN reports at the
            // pad's own cadence keep emitting.
            var src = XSource(); int slot = NewSlot();
            Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
            Counts(PadAt(0.52f), TicksAt(0.004f), src, slot);
            Counts(PadAt(0.54f), TicksAt(0.008f), src, slot);
            // Gap polls 1-3 ms after the last report, inside the 6 ms hold.
            Assert.True(Counts(PadAt(0.54f), TicksAt(0.009f), src, slot) > 0f);
            Assert.True(Counts(PadAt(0.54f), TicksAt(0.010f), src, slot) > 0f);
            Assert.True(Counts(PadAt(0.54f), TicksAt(0.011f), src, slot) > 0f);
        }

        [Fact]
        public void AStillFingerStops_RatherThanCoasting()
        {
            // Velocity hold bridges a report gap; it must not become inertia.
            var src = XSource(); int slot = NewSlot();
            Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
            Counts(PadAt(0.52f), TicksAt(0.004f), src, slot);
            Assert.True(Counts(PadAt(0.52f), TicksAt(0.006f), src, slot) > 0f);
            Assert.Equal(0f, Counts(PadAt(0.52f), TicksAt(0.050f), src, slot));
        }

        [Fact]
        public void LiftingTheFingerStopsTheCursorImmediately()
        {
            var src = XSource(); int slot = NewSlot();
            Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
            Counts(PadAt(0.55f), TicksAt(0.004f), src, slot);
            Assert.True(Counts(PadAt(0.55f), TicksAt(0.005f), src, slot) > 0f);
            Assert.Equal(0f, Counts(PadAt(0.55f, down: false), TicksAt(0.006f), src, slot));
        }

        [Fact]
        public void ContactAloneDoesNotMoveTheCursor()
        {
            // The seed records where the finger landed; only movement after
            // it counts. Otherwise every touchdown throws the cursor.
            Assert.Equal(0f, Counts(PadAt(0.20f), TicksAt(0f), XSource(), NewSlot()));
        }

        // ── no saturation ─────────────────────────────────────────────────

        [Fact]
        public void AFastSwipeKeepsItsMagnitude()
        {
            var a = XSource(); var b = XSource();
            int sa = NewSlot(), sb = NewSlot();
            Counts(PadAt(0.50f), TicksAt(0.000f), a, sa);
            float slow = Counts(PadAt(0.50f + 1f / 128f), TicksAt(0.004f), a, sa);
            Counts(PadAt(0.50f), TicksAt(0.000f), b, sb);
            float fast = Counts(PadAt(0.50f + 4f / 128f), TicksAt(0.004f), b, sb);

            Assert.True(slow > 0f, "harness produced no slow motion");
            Assert.True(Math.Abs(fast / slow - 4f) < 0.05f,
                $"saturating: 4x the travel gave {fast / slow:F2}x the motion");
        }

        [Fact]
        public void PositiveControl_TheRelativeDeflectionLaneStillSaturates()
        {
            // Proves the test above measures something. The deflection lane
            // is unchanged; it is simply the wrong shape for a mouse.
            var src = XSource(); int slot = NewSlot();
            Deflection(PadAt(0.50f), src, slot);                       // seed
            float big = Deflection(PadAt(0.50f + 8f / 128f), src, slot);
            Assert.True(Math.Abs(big) >= 0.999f,
                $"expected the old lane to clamp at 1.0, got {big}");
        }

        // ── counted once ──────────────────────────────────────────────────

        [Fact]
        public void WhileTheMouseLaneIsScoped_TouchReadsZeroAsADeflection()
        {
            // Contributing to the mouse target's deflection combine as well
            // as the rate lane would move the cursor twice.
            var src = XSource(); int slot = NewSlot();
            Deflection(PadAt(0.50f), src, slot);
            float unscoped = Deflection(PadAt(0.55f), src, slot);
            Assert.True(Math.Abs(unscoped) > 0f, "harness read no deflection to suppress");

            int slot2 = NewSlot();
            Deflection(PadAt(0.50f), src, slot2);
            using (new SourceCoercion.TouchMouseLaneScope(true))
                Assert.Equal(0f, Deflection(PadAt(0.55f), src, slot2));
        }

        [Fact]
        public void TheScopeLeavesTheAbsoluteReadAlone()
        {
            // Stick and extended-axis targets take the ABSOLUTE variant.
            // Suppressing them would break touchpad-to-stick, which has
            // nothing to do with this fix.
            var src = XSource();
            float before = SourceCoercion.EvaluateForBipolarAxisTarget(
                PadAt(0.75f), src, NewSlot(), relativeTouchpad: false);
            Assert.True(Math.Abs(before) > 0f, "harness read no absolute value");
            using (new SourceCoercion.TouchMouseLaneScope(true))
                Assert.Equal(before, SourceCoercion.EvaluateForBipolarAxisTarget(
                    PadAt(0.75f), src, NewSlot(), relativeTouchpad: false), 4);
        }

        // ── shape ─────────────────────────────────────────────────────────

        [Fact]
        public void MotionIsSignPreserving()
        {
            var a = XSource(); var b = XSource();
            int sa = NewSlot(), sb = NewSlot();
            Counts(PadAt(0.50f), TicksAt(0.000f), a, sa);
            float fwd = Counts(PadAt(0.54f), TicksAt(0.004f), a, sa);
            Counts(PadAt(0.50f), TicksAt(0.000f), b, sb);
            float back = Counts(PadAt(0.46f), TicksAt(0.004f), b, sb);
            Assert.True(fwd > 0f && back < 0f);
            Assert.Equal(fwd, -back, 4);
        }

        // ── momentum and jitter reduction ─────────────────────────────────

        private static PadForge.Engine.Touchpad.TouchpadGestureSettings _tp;

        private static void UseSettings(bool momentum = false, float decay = 0.90f,
                                        bool jitter = true, float accel = 0f,
                                        float maxSpeed = 0f, float minLift = 0.286f,
                                        float flingGain = 1.0f, bool stacking = false)
        {
            _tp = new PadForge.Engine.Touchpad.TouchpadGestureSettings
            {
                MouseMomentum = momentum,
                MouseMomentumDecay = decay,
                MouseJitterReduction = jitter,
                MouseAcceleration = accel,
                MouseMomentumMaxSpeed = maxSpeed,
                MouseMomentumMinLift = minLift,
                MouseMomentumFlingGain = flingGain,
                MouseMomentumStacking = stacking,
            };
            SourceCoercion.TouchpadMouseSettingsProvider = (_, __, ___) => _tp;
        }

        private static void ClearSettings() => SourceCoercion.TouchpadMouseSettingsProvider = null;


        // ── cursor acceleration (per-pad card) ────────────────────────────
        //
        // Steam's mouse acceleration arrived on MappingSource.ParamAccel and
        // the engine honored it with no card showing it. The value lives on
        // the pad's own Mouse Acceleration setting now, so these tests prove
        // the ENGINE reads that setting: a card that saves a value the engine
        // ignores is the same defect one step further along.

        private static float DragCounts(float from, float to, float accel)
        {
            UseSettings(accel: accel);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(from), TicksAt(0.000f), src, slot);
                return Counts(PadAt(to), TicksAt(0.004f), src, slot);
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void Acceleration_AmplifiesTheDrag()
        {
            float plain = DragCounts(0.50f, 0.80f, accel: 0f);
            float boosted = DragCounts(0.50f, 0.80f, accel: 2f);

            Assert.True(plain > 0f, "the baseline drag produced no motion, so the comparison is vacuous");
            Assert.True(boosted > plain,
                $"acceleration did not reach the engine: {boosted} vs {plain}");
        }

        [Fact]
        public void Acceleration_IsRateDependentNotAFlatMultiplier()
        {
            // The whole reason this is its own knob and not a wider
            // sensitivity range: the gain must grow with speed. So the fast
            // drag gains proportionally MORE than the slow one.
            float slowPlain = DragCounts(0.50f, 0.55f, accel: 0f);
            float slowBoost = DragCounts(0.50f, 0.55f, accel: 2f);
            float fastPlain = DragCounts(0.50f, 0.90f, accel: 0f);
            float fastBoost = DragCounts(0.50f, 0.90f, accel: 2f);

            Assert.True(slowPlain > 0f && fastPlain > 0f, "a baseline drag produced no motion");
            double slowGain = slowBoost / slowPlain;
            double fastGain = fastBoost / fastPlain;
            Assert.True(fastGain > slowGain,
                $"gain did not grow with speed: slow x{slowGain:F3}, fast x{fastGain:F3}");
        }

        [Fact]
        public void Acceleration_Zero_IsExactlyIdentity()
        {
            // The default must not perturb the lane at all, so nobody's
            // existing feel changes when this setting appears.
            float withProvider = DragCounts(0.50f, 0.80f, accel: 0f);

            ClearSettings();                       // no provider at all
            var src = XSource(); int slot = NewSlot();
            Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
            float withoutProvider = Counts(PadAt(0.80f), TicksAt(0.004f), src, slot);

            Assert.Equal(withoutProvider, withProvider, 4);
        }


        [Fact]
        public void Acceleration_KeepsADiagonalOnItsLine()
        {
            // The gain rides the VECTOR speed, so a diagonal drag gains the
            // same factor on both axes and the pointer follows the line the
            // thumb drew. A per-axis gain would boost the longer axis more and
            // bow the path, which is invisible in any X-only test: this one
            // exists because a per-axis mutation survived the whole
            // acceleration suite.
            static (float X, float Y) Diagonal(float accel)
            {
                _tp = new PadForge.Engine.Touchpad.TouchpadGestureSettings { MouseAcceleration = accel };
                SourceCoercion.TouchpadMouseSettingsProvider = (_, __, ___) => _tp;
                try
                {
                    var src = XSource(); int slot = NewSlot();
                    // A drag with UNEQUAL components, so a per-axis gain
                    // cannot coincidentally match the isotropic one.
                    ReadBoth(PadAtXY(0.50f, 0.50f), TicksAt(0.000f), src, slot);
                    return ReadBoth(PadAtXY(0.80f, 0.60f), TicksAt(0.004f), src, slot);
                }
                finally { ClearSettings(); }
            }

            var plain = Diagonal(0f);
            var boosted = Diagonal(2f);

            Assert.True(Math.Abs(plain.X) > 0f && Math.Abs(plain.Y) > 0f,
                "the baseline diagonal moved on only one axis, so the test proves nothing");

            // Same direction: the X:Y ratio must survive the gain.
            double plainRatio = plain.Y / plain.X;
            double boostedRatio = boosted.Y / boosted.X;
            Assert.Equal(plainRatio, boostedRatio, 3);

            // And it really did accelerate, so the ratio did not hold merely
            // because nothing happened.
            Assert.True(Math.Abs(boosted.X) > Math.Abs(plain.X) * 1.001,
                $"no acceleration applied: {boosted.X} vs {plain.X}");
        }

        private static CustomInputState PadAtXY(float x, float y, bool down = true)
        {
            var s = new CustomInputState();
            s.Touchpads = new[]
            {
                new TouchpadInputState
                {
                    MaxFingers = 2,
                    FingerX = new[] { x, 0f },
                    FingerY = new[] { y, 0f },
                    FingerPressure = new[] { 1f, 0f },
                    FingerDown = new[] { down, false },
                },
            };
            return s;
        }

        /// <summary>Both axes from ONE poll. The lane returns a single axis per
        /// call (forX picks which, the other comes back 0), so both components
        /// take two reads inside ONE BeginPollFrame, exactly as the production
        /// caller does. A second BeginPollFrame here would advance the ball
        /// twice and desynchronise the pair.</summary>
        private static (float X, float Y) ReadBoth(CustomInputState st, long ticks, MappingSource src, int slot)
        {
            SourceCoercion.BeginPollFrame();
            var (x, _) = SourceCoercion.ReadTouchpadMouseCounts(
                st, src, slot, deviceGuid: "", dtSeconds: PollDt,
                forX: true, nowTicks: ticks, ticksPerSecond: Freq);
            var (_, y) = SourceCoercion.ReadTouchpadMouseCounts(
                st, src, slot, deviceGuid: "", dtSeconds: PollDt,
                forX: false, nowTicks: ticks, ticksPerSecond: Freq);
            return (x, y);
        }


        // ── the Trackpad profile, end to end ──────────────────────────────

        // 45 mm, not the 69 mm default. At 69 the slowest movement a pad can
        // report already exceeds libinput's 7 mm/s deceleration knee, so the
        // precision half of the curve is unreachable and a deceleration test
        // would silently measure the plateau instead. That is a real property
        // of the feature, pinned by
        // TrackpadPointerGainTests.AtLibinputsAssumedWidth_TheDecelerationKneeIsOutOfReach;
        // here it just means the width has to be set for the test to reach the
        // branch it is about.
        private const float NarrowPadMm = 45f;

        private static float TrackpadDrag(float from, float to,
                                          float thresholdMm = 130f,
                                          float padWidthMm = NarrowPadMm)
        {
            _tp = new PadForge.Engine.Touchpad.TouchpadGestureSettings
            {
                PointerResponse = "Trackpad",
                TrackpadThresholdMmPerSec = thresholdMm,
                TrackpadPadWidthMm = padWidthMm,
            };
            SourceCoercion.TouchpadMouseSettingsProvider = (_, __, ___) => _tp;
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(from), TicksAt(0.000f), src, slot);
                return Counts(PadAt(to), TicksAt(0.004f), src, slot);
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void Trackpad_DeceleratesASlowDrag_WhichSimpleCannotDo()
        {
            // THE point of the profile. libinput decelerates to 0.3x at rest,
            // and that sub-unity region is where a laptop trackpad's precision
            // lives. Simple's gain starts at 1 and only climbs, so no value of
            // it can produce this.
            // 0.0002 pad widths over 4 ms = 0.05 pad/s = 2.25 mm/s at 45 mm,
            // inside the sub-7 mm/s deceleration ramp.
            float flat = DragCounts(0.5000f, 0.5002f, accel: 0f);
            float trackpad = TrackpadDrag(0.5000f, 0.5002f);

            Assert.True(flat > 0f, "the baseline slow drag produced no motion");
            Assert.True(trackpad < flat,
                $"a slow drag was not decelerated: {trackpad} vs flat {flat}");
        }

        [Fact]
        public void Trackpad_AcceleratesAFastDrag()
        {
            float flat = DragCounts(0.10f, 0.95f, accel: 0f);        // big, fast
            float trackpad = TrackpadDrag(0.10f, 0.95f);

            Assert.True(flat > 0f, "the baseline fast drag produced no motion");
            Assert.True(trackpad > flat,
                $"a fast drag was not accelerated: {trackpad} vs flat {flat}");
        }

        [Fact]
        public void Trackpad_SpansBothDirectionsFromNeutral()
        {
            // Same profile, same pad: slow decelerates AND fast accelerates.
            // Checking them together catches a sign or normalization error that
            // either test alone would pass.
            float slowFlat = DragCounts(0.5000f, 0.5002f, accel: 0f);
            float slowTp = TrackpadDrag(0.5000f, 0.5002f);
            float fastFlat = DragCounts(0.10f, 0.95f, accel: 0f);
            float fastTp = TrackpadDrag(0.10f, 0.95f);

            Assert.True(slowTp / slowFlat < 1.0f, "slow end did not decelerate");
            Assert.True(fastTp / fastFlat > 1.0f, "fast end did not accelerate");
        }

        [Fact]
        public void TheDefaultProfileIsBehaviorPreserving()
        {
            // Simple with acceleration 0 is the identity, which is what makes
            // it safe as the default: a pad that has never touched these
            // settings must feel exactly as it did before they existed.
            _tp = new PadForge.Engine.Touchpad.TouchpadGestureSettings();   // all defaults
            SourceCoercion.TouchpadMouseSettingsProvider = (_, __, ___) => _tp;
            float withDefaults;
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                withDefaults = Counts(PadAt(0.80f), TicksAt(0.004f), src, slot);
            }
            finally { ClearSettings(); }

            var src2 = XSource(); int slot2 = NewSlot();
            Counts(PadAt(0.50f), TicksAt(0.000f), src2, slot2);
            float withNoProvider = Counts(PadAt(0.80f), TicksAt(0.004f), src2, slot2);

            Assert.Equal(withNoProvider, withDefaults, 4);
        }

        [Fact]
        public void AnUnknownProfileNameReadsAsTheDefault()
        {
            // The value round-trips through hand-editable XML.
            _tp = new PadForge.Engine.Touchpad.TouchpadGestureSettings { PointerResponse = "Nonsense" };
            SourceCoercion.TouchpadMouseSettingsProvider = (_, __, ___) => _tp;
            float unknown;
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                unknown = Counts(PadAt(0.80f), TicksAt(0.004f), src, slot);
            }
            finally { ClearSettings(); }

            Assert.Equal(DragCounts(0.50f, 0.80f, accel: 0f), unknown, 4);
        }

        [Fact]
        public void TrackpadProfile_DisablesSimpleAccelerationOnTheAxisLaneToo()
        {
            // Round 40. The AXIS lane (touchpad finger driving a stick axis)
            // read MouseAcceleration without consulting PointerResponse, so a
            // pad switched to Trackpad kept applying the leftover Simple
            // value on axis rows while the card HID the acceleration slider:
            // an invisible setting the user could neither see nor clear.
            float Drag(PadForge.Engine.Touchpad.TouchpadGestureSettings tp)
            {
                _tp = tp;
                if (tp != null)
                    SourceCoercion.TouchpadMouseSettingsProvider = (_, __, ___) => _tp;
                try
                {
                    var src = XSource(); int slot = NewSlot();
                    Deflection(PadAt(0.500f), src, slot);          // seed
                    // 0.002 pad widths: the relative lane reaches full scale
                    // at 1/128 of the pad, so anything bigger clamps at 1.0
                    // with or without acceleration and the comparison reads
                    // 1 vs 1 (this test's own positive control caught that).
                    return Deflection(PadAt(0.502f), src, slot);
                }
                finally { ClearSettings(); }
            }

            float plain = Drag(null);
            Assert.True(plain > 0f, "the baseline drag produced no deflection");

            // Positive control: with Simple, the acceleration really reaches
            // this lane, so the Trackpad assertion below cannot pass vacuously.
            float simple = Drag(new PadForge.Engine.Touchpad.TouchpadGestureSettings
            {
                PointerResponse = "Simple",
                MouseAcceleration = 5f,
            });
            Assert.True(simple > plain * 1.01f,
                $"Simple acceleration did not reach the axis lane: {simple} vs {plain}");

            // The find: a leftover acceleration under Trackpad must be inert.
            float trackpad = Drag(new PadForge.Engine.Touchpad.TouchpadGestureSettings
            {
                PointerResponse = "Trackpad",
                MouseAcceleration = 5f,
            });
            Assert.Equal(plain, trackpad, 4);
        }

        [Fact]
        public void MomentumOff_StopsTheCursorDeadOnRelease()
        {
            UseSettings(momentum: false);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.56f), TicksAt(0.004f), src, slot);
                Assert.True(Counts(PadAt(0.56f), TicksAt(0.005f), src, slot) > 0f);
                Assert.Equal(0f, Counts(PadAt(0.56f, down: false), TicksAt(0.006f), src, slot));
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void MomentumOn_CoastsAfterReleaseThenStops()
        {
            // The trackball feel: keep traveling, decay, end. It must END,
            // or a flick leaves the cursor drifting forever.
            UseSettings(momentum: true, decay: 0.82f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.60f), TicksAt(0.004f), src, slot);

                float c1 = Counts(PadAt(0.60f, down: false), TicksAt(0.005f), src, slot);
                float c2 = Counts(PadAt(0.60f, down: false), TicksAt(0.006f), src, slot);
                Assert.True(c1 > 0f, "the cursor stopped dead with momentum on");
                Assert.True(c2 > 0f && c2 < c1, $"the coast is not decaying: {c1} then {c2}");

                // Run it out. It has to reach exactly zero, not merely small.
                float last = c2;
                for (int i = 0; i < 4000 && last != 0f; i++)
                    last = Counts(PadAt(0.60f, down: false), TicksAt(0.007f + i * 0.001f), src, slot);
                Assert.Equal(0f, last);
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void LandingElsewhereMidCoast_DoesNotJump()
        {
            // The coasting entry OUTLIVES the lift, which is what makes it
            // coast. So the next touchdown finds a stored position from the
            // last lift, and reading the gap to the new spot as motion threw
            // the cursor across the screen. It is not motion; it is two
            // different places.
            UseSettings(momentum: true, decay: 0.90f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.60f), TicksAt(0.004f), src, slot);
                Assert.True(Counts(PadAt(0.60f, down: false), TicksAt(0.005f), src, slot) > 0f,
                    "harness produced no coast to interrupt");

                // Finger returns at the far end of the pad, mid-glide.
                float onLanding = Counts(PadAt(0.05f), TicksAt(0.010f), src, slot);
                Assert.Equal(0f, onLanding);

                // And it tracks normally from the NEW spot, not the old one.
                float after = Counts(PadAt(0.07f), TicksAt(0.014f), src, slot);
                Assert.True(after > 0f, "tracking did not resume after re-contact");
                float sane = Counts(PadAt(0.09f), TicksAt(0.018f), src, slot);
                Assert.True(Math.Abs(after - sane) < Math.Abs(after) * 0.5f,
                    $"re-contact left a distorted velocity: {after} then {sane}");
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void TouchingDownStopsTheGlide()
        {
            // Catching the ball stops it, which the feel chain's trackball
            // already documents. Without this the old glide keeps adding to
            // the new drag.
            UseSettings(momentum: true, decay: 0.95f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.62f), TicksAt(0.004f), src, slot);
                Assert.True(Counts(PadAt(0.62f, down: false), TicksAt(0.005f), src, slot) > 0f);

                // Land and hold perfectly still: no coast may leak through.
                Counts(PadAt(0.30f), TicksAt(0.010f), src, slot);
                Assert.Equal(0f, Counts(PadAt(0.30f), TicksAt(0.011f), src, slot));
                Assert.Equal(0f, Counts(PadAt(0.30f), TicksAt(0.012f), src, slot));
            }
            finally { ClearSettings(); }
        }

        // ── the ball model (sc-controller BallModifier) ───────────────────

        [Fact]
        public void TheFlingFollowsTheStretchOfTravel_NotTheLastSampleAlone()
        {
            // THE complaint, stated as a test. A finger never leaves a pad
            // cleanly: the last report or two are slow and often point
            // somewhere else. Taking the last delta alone let one stray
            // sample steer the whole fling. The release velocity is the mean
            // of the recent window, so a long rightward drag followed by one
            // small leftward twitch still flings RIGHT.
            UseSettings(momentum: true, decay: 0.90f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                float x = 0.20f, t = 0f;
                Counts(PadAt(x), TicksAt(t), src, slot);
                for (int i = 0; i < 8; i++)   // a firm drag to the right
                {
                    x += 0.05f; t += 0.004f;
                    Counts(PadAt(x), TicksAt(t), src, slot);
                }
                // One last twitch back the other way, then lift.
                x -= 0.004f; t += 0.004f;
                Counts(PadAt(x), TicksAt(t), src, slot);

                float fling = Counts(PadAt(x, down: false), TicksAt(t + 0.001f), src, slot);
                Assert.True(fling > 0f,
                    $"the fling followed the final twitch instead of the drag: {fling}");
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void ASlowLiftDoesNotFling()
        {
            // sc-controller's MIN_LIFT_VELOCITY. Setting a finger down and
            // picking it up must not throw the cursor on whatever residue
            // the last sample happened to hold.
            UseSettings(momentum: true, decay: 0.95f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.500f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.5004f), TicksAt(0.010f), src, slot);   // a crawl
                Assert.Equal(0f, Counts(PadAt(0.5004f, down: false), TicksAt(0.011f), src, slot));
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void FrictionIsConstantDeceleration_SoTheBallActuallyStops()
        {
            // A real ball sheds a fixed amount of speed per unit time and
            // therefore arrives at zero. Exponential decay only approaches
            // it, which is the long mushy tail. Successive speed DROPS must
            // be roughly equal, not proportional to the speed left.
            UseSettings(momentum: true, decay: 0.85f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.20f), TicksAt(0.000f), src, slot);
                float x = 0.20f, t = 0f;
                for (int i = 0; i < 6; i++) { x += 0.05f; t += 0.004f; Counts(PadAt(x), TicksAt(t), src, slot); }

                // Sample the WHOLE coast. A short window cannot tell the two
                // models apart: over a few milliseconds an exponential curve
                // is locally straight, and an earlier version of this test
                // passed with exponential decay for exactly that reason.
                var v = new System.Collections.Generic.List<float>();
                for (int i = 0; i < 20000; i++)
                {
                    float c = Counts(PadAt(x, down: false), TicksAt(t + 0.001f * (i + 1)), src, slot);
                    if (c <= 0f) break;
                    v.Add(c);
                }
                Assert.True(v.Count > 40, $"the coast was only {v.Count} polls; too short to characterise");

                // Compare the drop near the START against the drop near the
                // END of the same coast. Constant deceleration keeps them
                // equal; exponential decay makes the late one a small
                // fraction of the early one, because it scales with what is
                // left rather than with time.
                int a0 = v.Count / 10, b0 = (v.Count * 9) / 10;
                float dropEarly = v[a0] - v[a0 + 1];
                float dropLate = v[b0] - v[b0 + 1];
                Assert.True(dropEarly > 0f, "speed did not fall at all");
                Assert.True(Math.Abs(dropLate - dropEarly) < dropEarly * 0.25f,
                    $"decay looks exponential, not constant: {dropEarly} early, {dropLate} late");
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void FrictionNeverPushesTheBallBackwards()
        {
            // The deceleration is capped at the speed remaining. Uncapped it
            // would overshoot zero on the final tick and reverse the cursor.
            UseSettings(momentum: true, decay: 0.80f);   // strongest friction
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.20f), TicksAt(0.000f), src, slot);
                float x = 0.20f, t = 0f;
                for (int i = 0; i < 6; i++) { x += 0.05f; t += 0.004f; Counts(PadAt(x), TicksAt(t), src, slot); }
                for (int i = 0; i < 500; i++)
                {
                    float c = Counts(PadAt(x, down: false), TicksAt(t + 0.001f * (i + 1)), src, slot);
                    Assert.True(c >= 0f, $"the ball reversed on poll {i}: {c}");
                    if (c == 0f) return;
                }
                Assert.Fail("the ball never stopped under full friction");
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void APauseBeforeLiftingDoesNotFlingStaleSamples()
        {
            // Drag, stop, hold, then lift. The hand had come to rest, so
            // there is nothing to fling: the history must not survive the
            // pause and resurrect the earlier motion.
            UseSettings(momentum: true, decay: 0.95f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.20f), TicksAt(0.000f), src, slot);
                float x = 0.20f, t = 0f;
                for (int i = 0; i < 6; i++) { x += 0.05f; t += 0.004f; Counts(PadAt(x), TicksAt(t), src, slot); }

                // Held perfectly still, well past the rest window.
                for (int i = 0; i < 60; i++)
                    Counts(PadAt(x), TicksAt(t + 0.001f * (i + 1)), src, slot);

                Assert.Equal(0f, Counts(PadAt(x, down: false), TicksAt(t + 0.100f), src, slot));
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void MomentumGlide_LastsLongerAtAHigherDecay()
        {
            // What the slider is for: the knob has to change the distance.
            static int CoastPolls(float decay)
            {
                UseSettings(momentum: true, decay: decay);
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.60f), TicksAt(0.004f), src, slot);
                int n = 0;
                for (int i = 0; i < 4000; i++)
                {
                    if (Counts(PadAt(0.60f, down: false), TicksAt(0.005f + i * 0.001f), src, slot) == 0f)
                        break;
                    n++;
                }
                return n;
            }
            try
            {
                int shortGlide = CoastPolls(0.80f);
                int longGlide = CoastPolls(0.95f);
                Assert.True(shortGlide > 0 && longGlide > shortGlide,
                    $"glide did not lengthen: {shortGlide} polls at 0.80, {longGlide} at 0.95");
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void AtFullGlide_TheBallIsFrictionless_AndTouchingDownIsTheStop()
        {
            // 1.00 keeps the speed exactly. That is deliberate, the way a
            // spun trackball runs until you catch it, and it is only safe
            // because the down edge stops it. Both halves are asserted here
            // so neither can be removed on its own.
            UseSettings(momentum: true, decay: 1.00f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.60f), TicksAt(0.004f), src, slot);

                float first = Counts(PadAt(0.60f, down: false), TicksAt(0.005f), src, slot);
                Assert.True(first > 0f, "no coast at full glide");

                // Still going, undiminished, a long way later.
                float later = 0f;
                for (int i = 0; i < 2000; i++)
                    later = Counts(PadAt(0.60f, down: false), TicksAt(0.006f + i * 0.001f), src, slot);
                Assert.Equal(first, later, 4);

                // And a touch stops it dead.
                Counts(PadAt(0.30f), TicksAt(3.000f), src, slot);
                Assert.Equal(0f, Counts(PadAt(0.30f), TicksAt(3.001f), src, slot));
            }
            finally { ClearSettings(); }
        }

        // ── the #291 knobs: gain, gate, ceiling, stacking ────────────────

        /// <summary>One firm swipe then lift; returns the first coast poll.</summary>
        private static float FirstCoast(int slot, MappingSource src)
        {
            Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
            Counts(PadAt(0.60f), TicksAt(0.004f), src, slot);
            return Counts(PadAt(0.60f, down: false), TicksAt(0.005f), src, slot);
        }

        [Fact]
        public void FlingGain_ScalesTheCoast_NotTheDrag()
        {
            UseSettings(momentum: true, flingGain: 1.0f);
            float plainDrag, plainCoast, boostDrag, boostCoast;
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                plainDrag = Counts(PadAt(0.60f), TicksAt(0.004f), src, slot);
                plainCoast = Counts(PadAt(0.60f, down: false), TicksAt(0.005f), src, slot);
            }
            finally { ClearSettings(); }

            UseSettings(momentum: true, flingGain: 2.0f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                boostDrag = Counts(PadAt(0.60f), TicksAt(0.004f), src, slot);
                boostCoast = Counts(PadAt(0.60f, down: false), TicksAt(0.005f), src, slot);
            }
            finally { ClearSettings(); }

            Assert.Equal(plainDrag, boostDrag, 4);
            Assert.True(plainCoast > 0f, "no baseline coast");
            Assert.True(Math.Abs(boostCoast / plainCoast - 2f) < 0.1f,
                $"gain 2 did not double the launch: {plainCoast} vs {boostCoast}");
        }

        [Fact]
        public void LiftGate_RaisedAboveTheFling_StopsDead()
        {
            // A gentle swipe: 0.003 pad in 4 ms = 0.75 pad-widths/s of lift
            // velocity. Above the 0.286 default, below a 1.9 gate.
            static float GentleFling(int slot, MappingSource src)
            {
                Counts(PadAt(0.500f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.503f), TicksAt(0.004f), src, slot);
                return Counts(PadAt(0.503f, down: false), TicksAt(0.005f), src, slot);
            }

            UseSettings(momentum: true);
            try
            {
                Assert.True(GentleFling(NewSlot(), XSource()) > 0f,
                    "positive control: the gentle fling did not coast at the default gate");
            }
            finally { ClearSettings(); }

            UseSettings(momentum: true, minLift: 1.9f);
            try
            {
                Assert.Equal(0f, GentleFling(NewSlot(), XSource()));
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void LiftGate_AtZero_LetsATinyFlingCoast()
        {
            UseSettings(momentum: true, minLift: 0f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.500f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.502f), TicksAt(0.004f), src, slot);
                Assert.True(Counts(PadAt(0.502f, down: false), TicksAt(0.005f), src, slot) > 0f,
                    "a zero gate still refused a slow fling");
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void MaxFlingSpeed_CapsTheLaunch()
        {
            float unclamped, clamped;
            UseSettings(momentum: true);
            try { unclamped = FirstCoast(NewSlot(), XSource()); }
            finally { ClearSettings(); }

            UseSettings(momentum: true, maxSpeed: 1.0f);
            try { clamped = FirstCoast(NewSlot(), XSource()); }
            finally { ClearSettings(); }

            Assert.True(unclamped > 0f, "no baseline coast");
            Assert.True(clamped > 0f, "the cap killed the coast outright");
            Assert.True(clamped < unclamped * 0.5f,
                $"the cap did not bite: {unclamped} vs {clamped}");
        }

        [Fact]
        public void Stacking_ASecondFling_AddsToTheFirst()
        {
            float single, stacked;
            UseSettings(momentum: true, decay: 1.00f);
            try { single = FirstCoast(NewSlot(), XSource()); }
            finally { ClearSettings(); }

            UseSettings(momentum: true, decay: 1.00f, stacking: true);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Assert.True(FirstCoast(slot, src) > 0f, "no first fling");
                Counts(PadAt(0.50f), TicksAt(0.010f), src, slot);
                Counts(PadAt(0.60f), TicksAt(0.014f), src, slot);
                stacked = Counts(PadAt(0.60f, down: false), TicksAt(0.015f), src, slot);
            }
            finally { ClearSettings(); }

            Assert.True(stacked > single * 1.5f,
                $"the second fling did not stack: single {single}, stacked {stacked}");
        }

        [Fact]
        public void Stacking_AStillLift_StillStopsEverything()
        {
            UseSettings(momentum: true, decay: 1.00f, stacking: true);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Assert.True(FirstCoast(slot, src) > 0f, "no coast to stop");
                Counts(PadAt(0.30f), TicksAt(0.020f), src, slot);
                Counts(PadAt(0.30f), TicksAt(0.060f), src, slot);
                Assert.Equal(0f, Counts(PadAt(0.30f, down: false), TicksAt(0.061f), src, slot));
                Assert.Equal(0f, Counts(PadAt(0.30f, down: false), TicksAt(0.062f), src, slot));
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void Stacking_WithoutACap_CannotIntegrateWithoutBound()
        {
            UseSettings(momentum: true, decay: 1.00f, stacking: true);
            try
            {
                var src = XSource(); int slot = NewSlot();
                float last = FirstCoast(slot, src);
                float prev;
                float t = 0.010f;
                int grewPastCeiling = 0;
                for (int i = 0; i < 30; i++)
                {
                    prev = last;
                    Counts(PadAt(0.50f), TicksAt(t), src, slot); t += 0.004f;
                    Counts(PadAt(0.60f), TicksAt(t), src, slot); t += 0.001f;
                    last = Counts(PadAt(0.60f, down: false), TicksAt(t), src, slot); t += 0.005f;
                    if (last > prev * 1.01f) grewPastCeiling++;
                }
                // Thirty stacked flings: growth must have SATURATED, not
                // continued to the last iteration.
                Assert.True(grewPastCeiling < 25,
                    $"stacking grew on {grewPastCeiling}/30 rounds; no ceiling in effect");
                Assert.True(last > 0f, "the ceiling killed the coast entirely");
            }
            finally { ClearSettings(); }
        }

        // ── the #291 defect fixes: no frozen coasts, real reset sites ────

        [Fact]
        public void ASuppressionGap_StopsTheCoast_InsteadOfResumingIt()
        {
            // The defect: a layer suppression, a macro postpone, or an
            // offline device stopped the ball being ADVANCED, so the
            // velocity froze and the coast resumed when the path came back.
            // A gap longer than TouchStaleAdvanceSeconds now stops it.
            UseSettings(momentum: true, decay: 0.95f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.60f), TicksAt(0.004f), src, slot);
                Assert.True(Counts(PadAt(0.60f, down: false), TicksAt(0.005f), src, slot) > 0f,
                    "harness produced no coast to suppress");

                // The row goes unread for 300 ms (suppressed), then returns.
                float onResume = Counts(PadAt(0.60f, down: false), TicksAt(0.305f), src, slot);
                Assert.Equal(0f, onResume);

                // And it STAYS stopped rather than restarting a poll later.
                Assert.Equal(0f, Counts(PadAt(0.60f, down: false), TicksAt(0.306f), src, slot));
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void AFingerHeldThroughASuppressionGap_DoesNotFlingOnResume()
        {
            // The finger stays down while the row is suppressed and moves
            // meanwhile. On resume the gap must re-seed, not be read as one
            // giant delta.
            UseSettings(momentum: true, decay: 0.90f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.20f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.22f), TicksAt(0.004f), src, slot);

                // 300 ms unread; the finger travelled to the far edge.
                float onResume = Counts(PadAt(0.90f), TicksAt(0.305f), src, slot);
                Assert.Equal(0f, onResume);

                // Tracking resumes normally from the NEW position.
                float after = Counts(PadAt(0.92f), TicksAt(0.309f), src, slot);
                Assert.True(after > 0f, "tracking did not resume after the gap");
                float smallDrag = after;
                Assert.True(System.Math.Abs(smallDrag) < 500f,
                    $"the gap was read as one delta: {smallDrag} counts");
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void ResetTouchMomentum_KillsACoast()
        {
            // Profile-switch hygiene (#291): the table finally has a reset
            // site, and it must actually stop a live coast.
            UseSettings(momentum: true, decay: 0.95f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.60f), TicksAt(0.004f), src, slot);
                Assert.True(Counts(PadAt(0.60f, down: false), TicksAt(0.005f), src, slot) > 0f,
                    "harness produced no coast to reset");

                SourceCoercion.ResetTouchMomentum();
                Assert.Equal(0f, Counts(PadAt(0.60f, down: false), TicksAt(0.006f), src, slot));
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void ResetTouchMomentumForSlot_IsScopedToTheSlot()
        {
            UseSettings(momentum: true, decay: 0.95f);
            try
            {
                var a = XSource(); int slotA = NewSlot();
                var b = XSource(); int slotB = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), a, slotA);
                Counts(PadAt(0.60f), TicksAt(0.004f), a, slotA);
                Counts(PadAt(0.50f), TicksAt(0.000f), b, slotB);
                Counts(PadAt(0.60f), TicksAt(0.004f), b, slotB);
                Assert.True(Counts(PadAt(0.60f, down: false), TicksAt(0.005f), a, slotA) > 0f);
                Assert.True(Counts(PadAt(0.60f, down: false), TicksAt(0.005f), b, slotB) > 0f);

                SourceCoercion.ResetTouchMomentumForSlot(slotA);
                Assert.Equal(0f, Counts(PadAt(0.60f, down: false), TicksAt(0.006f), a, slotA));
                Assert.True(Counts(PadAt(0.60f, down: false), TicksAt(0.006f), b, slotB) > 0f,
                    "the per-slot reset leaked into another slot");
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void MomentumDecayIsClampedIntoTheBand()
        {
            // A persisted value outside the band must not make the toggle
            // inert or the glide wild. Below the floor still glides.
            UseSettings(momentum: true, decay: 0.10f);
            try
            {
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                Counts(PadAt(0.60f), TicksAt(0.004f), src, slot);
                Assert.True(Counts(PadAt(0.60f, down: false), TicksAt(0.005f), src, slot) > 0f,
                    "a persisted value under the floor killed the glide entirely");
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void TheDefaultGlideSitsAtTheMidpointOfTheBand()
        {
            // The default is meant to BE the middle of the slider, so the
            // knob has equal room either way. Pinned so the band and the
            // default cannot drift apart.
            var fresh = new PadForge.Engine.Touchpad.TouchpadGestureSettings();
            Assert.Equal(0.90f, fresh.MouseMomentumDecay, 3);
            Assert.Equal(0.90f, (0.80f + 1.00f) / 2f, 3);
        }

        [Fact]
        public void JitterReduction_DampsTheTremorBandWithoutDeletingIt()
        {
            // The point of a curve over a dead zone: small motion is made
            // SMALLER, never zero, so fine cursor work stays alive.
            static float Tiny(bool jitter)
            {
                UseSettings(jitter: jitter);
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                return Counts(PadAt(0.50f + 0.0004f), TicksAt(0.004f), src, slot);
            }
            try
            {
                float on = Tiny(true), off = Tiny(false);
                Assert.True(off > 0f, "harness produced no tremor-band motion");
                Assert.True(on > 0f, "jitter reduction deleted the motion; that is a dead zone");
                Assert.True(on < off, $"jitter reduction did not damp: {on} against {off}");
            }
            finally { ClearSettings(); }
        }

        [Fact]
        public void JitterReduction_LeavesRealMotionAlone()
        {
            static float Fast(bool jitter)
            {
                UseSettings(jitter: jitter);
                var src = XSource(); int slot = NewSlot();
                Counts(PadAt(0.50f), TicksAt(0.000f), src, slot);
                return Counts(PadAt(0.56f), TicksAt(0.004f), src, slot);
            }
            try { Assert.Equal(Fast(false), Fast(true), 4); }
            finally { ClearSettings(); }
        }

        [Fact]
        public void PressureIsNotMotion()
        {
            var (x, y) = SourceCoercion.ReadTouchpadMouseCounts(
                PadAt(0.5f), new MappingSource { Descriptor = "Touchpad 0 Finger 0 Pressure" },
                NewSlot(), "", PollDt, true, TicksAt(0.004f), Freq);
            Assert.Equal(0f, x);
            Assert.Equal(0f, y);
        }

        [Fact]
        public void ANonTouchSourceProducesNothingOnThisLane()
        {
            var (x, _) = SourceCoercion.ReadTouchpadMouseCounts(
                PadAt(0.5f), new MappingSource { Descriptor = "Gyro Yaw" },
                NewSlot(), "", PollDt, true, TicksAt(0.004f), Freq);
            Assert.Equal(0f, x);
        }
    }
}
