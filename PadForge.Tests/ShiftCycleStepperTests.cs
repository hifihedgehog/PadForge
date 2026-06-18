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
    }
}
