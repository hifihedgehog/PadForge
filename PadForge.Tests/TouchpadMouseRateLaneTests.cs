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
                                        bool jitter = true)
        {
            _tp = new PadForge.Engine.Touchpad.TouchpadGestureSettings
            {
                MouseMomentum = momentum,
                MouseMomentumDecay = decay,
                MouseJitterReduction = jitter,
            };
            SourceCoercion.TouchpadMouseSettingsProvider = (_, __, ___) => _tp;
        }

        private static void ClearSettings() => SourceCoercion.TouchpadMouseSettingsProvider = null;

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
            // The trackball feel: keep travelling, decay, end. It must END,
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
