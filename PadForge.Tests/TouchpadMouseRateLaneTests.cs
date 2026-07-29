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
