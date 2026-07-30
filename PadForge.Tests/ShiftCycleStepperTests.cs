using System.Collections.Generic;
using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Covers the #119 Shift Cycle cursor math: Next/Previous,
    /// wrap vs clamp, and whether Base is a stop in the ring. The runtime in
    /// InputManager.Step3 calls <see cref="ShiftCycleStepper.Step"/> directly,
    /// so these sequences are the shipped behavior, not a re-implementation.</summary>
    public class ShiftCycleStepperTests
    {
        // Press the button `presses` times from Base (pos 0) and collect the
        // cursor path so the whole sequence is asserted, not just one step.
        private static List<int> Walk(int n, bool previous, bool wrap, bool includeBase, int presses)
        {
            var path = new List<int>();
            int pos = 0;
            for (int i = 0; i < presses; i++)
            {
                pos = ShiftCycleStepper.Step(pos, n, previous, wrap, includeBase);
                path.Add(pos);
            }
            return path;
        }

        [Fact]
        public void Next_LayersOnly_Wrap_LoopsWithoutHittingBase()
        {
            // n=2, the reporter's case. Base -> 1 -> 2 -> 1 -> 2, never 0.
            Assert.Equal(new[] { 1, 2, 1, 2, 1 }, Walk(2, previous: false, wrap: true, includeBase: false, presses: 5));
        }

        [Fact]
        public void Previous_LayersOnly_Wrap_LoopsBackwardWithoutBase()
        {
            // First Previous from Base lands on the last layer, then walks back and wraps.
            Assert.Equal(new[] { 2, 1, 2, 1, 2 }, Walk(2, previous: true, wrap: true, includeBase: false, presses: 5));
        }

        [Fact]
        public void Next_LayersOnly_NoWrap_ClampsAtLast()
        {
            Assert.Equal(new[] { 1, 2, 2, 2 }, Walk(2, previous: false, wrap: false, includeBase: false, presses: 4));
        }

        [Fact]
        public void Previous_LayersOnly_NoWrap_ClampsAtFirst()
        {
            // First Previous (no wrap) from Base goes to the first layer, then clamps there.
            Assert.Equal(new[] { 1, 1, 1 }, Walk(2, previous: true, wrap: false, includeBase: false, presses: 3));
        }

        [Fact]
        public void Next_LayersOnly_Wrap_ThreeLayers()
        {
            Assert.Equal(new[] { 1, 2, 3, 1, 2, 3, 1 }, Walk(3, previous: false, wrap: true, includeBase: false, presses: 7));
        }

        [Fact]
        public void Next_IncludeBase_Wrap_BaseIsAStop()
        {
            // Base -> 1 -> 2 -> Base -> 1 ... the unshifted state is in the ring.
            Assert.Equal(new[] { 1, 2, 0, 1, 2, 0 }, Walk(2, previous: false, wrap: true, includeBase: true, presses: 6));
        }

        [Fact]
        public void Next_IncludeBase_NoWrap_ClampsAtLast()
        {
            Assert.Equal(new[] { 1, 2, 2, 2 }, Walk(2, previous: false, wrap: false, includeBase: true, presses: 4));
        }

        [Fact]
        public void Previous_IncludeBase_NoWrap_ClampsAtBase()
        {
            // Walk forward to the last layer, then Previous back down to Base and clamp there.
            int pos = ShiftCycleStepper.Step(0, 2, previous: false, wrap: false, includeBase: true); // 1
            pos = ShiftCycleStepper.Step(pos, 2, previous: false, wrap: false, includeBase: true);   // 2
            pos = ShiftCycleStepper.Step(pos, 2, previous: true, wrap: false, includeBase: true);    // 1
            pos = ShiftCycleStepper.Step(pos, 2, previous: true, wrap: false, includeBase: true);    // 0 (Base)
            pos = ShiftCycleStepper.Step(pos, 2, previous: true, wrap: false, includeBase: true);    // 0 (clamped)
            Assert.Equal(0, pos);
        }

        [Fact]
        public void SingleLayer_NeverWrapsToBaseOrAnythingElse()
        {
            // The reporter's broken setup: a one-item list has nothing to walk to.
            Assert.Equal(new[] { 1, 1, 1 }, Walk(1, previous: false, wrap: true, includeBase: false, presses: 3));
        }
    
        // ── Round 34: a cursor left over from a longer ring ──────────────
        // Editing CycleLayers from "A|B|C" down to "A" changes n without
        // touching the stored cursor. Both of these returned a position past
        // the end, and the caller uses the result as layers[pos - 1], so the
        // next Previous press threw IndexOutOfRangeException on the poll
        // thread and the device lost its whole mapping evaluation for that
        // frame. Every result must now be a valid stop in [0..n].

        [Theory]
        [InlineData(3, 1)]
        [InlineData(9, 2)]
        [InlineData(2, 1)]
        public void StaleCursor_LayersOnlyPrevious_StaysInRange(int stale, int n)
        {
            foreach (bool wrap in new[] { true, false })
            {
                int r = ShiftCycleStepper.Step(stale, n, previous: true, wrap, includeBase: false);
                Assert.InRange(r, 1, n);
            }
        }

        [Theory]
        [InlineData(3, 1)]
        [InlineData(9, 2)]
        public void StaleCursor_IncludeBase_StaysInRange(int stale, int n)
        {
            foreach (bool wrap in new[] { true, false })
                foreach (bool prev in new[] { true, false })
                {
                    int r = ShiftCycleStepper.Step(stale, n, prev, wrap, includeBase: true);
                    Assert.InRange(r, 0, n);
                }
        }

        [Fact]
        public void StaleCursor_Next_StaysInRange()
        {
            foreach (bool wrap in new[] { true, false })
            {
                int r = ShiftCycleStepper.Step(5, 2, previous: false, wrap, includeBase: false);
                Assert.InRange(r, 1, 2);
            }
        }

        [Fact]
        public void NegativeCursor_IsTreatedAsBase()
        {
            // Nothing writes a negative cursor today; the clamp covers it so
            // the range guarantee holds for every input, not just the one
            // that was observed.
            Assert.Equal(1, ShiftCycleStepper.Step(-4, 3, previous: false, wrap: false, includeBase: false));
            Assert.Equal(0, ShiftCycleStepper.Step(-4, 3, previous: true, wrap: false, includeBase: true));
        }

        [Fact]
        public void InRangeCursor_BehaviorIsUnchangedByTheClamp()
        {
            // The clamp must not move any cursor that was already valid.
            for (int n = 1; n <= 4; n++)
                for (int pos = 0; pos <= n; pos++)
                    foreach (bool wrap in new[] { true, false })
                        foreach (bool prev in new[] { true, false })
                            foreach (bool inc in new[] { true, false })
                            {
                                int r = ShiftCycleStepper.Step(pos, n, prev, wrap, inc);
                                Assert.InRange(r, inc ? 0 : (pos == 0 && !prev ? 1 : 0), n);
                            }
        }
    }
}
