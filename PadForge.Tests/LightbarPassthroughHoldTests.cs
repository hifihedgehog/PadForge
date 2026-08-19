using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #334: the DualSense lightbar stopped following the Lighting tab and
    /// sat on its firmware default blue.
    ///
    /// <para>The identity writer yields the bar to the pass-through while
    /// the pass-through holds it. It asked
    /// <c>IsHoldingState</c>, which is true whenever the lane has forwarded
    /// ANY effect payload: adaptive triggers, rumble, audio, the player
    /// LED. A host driving triggers and never touching the bar therefore
    /// took the lightbar away from the user for the whole 15 s idle
    /// window, and any host that keeps talking holds it indefinitely, which
    /// is why toggling the engine did not always give it back. The pips and
    /// the mic LED gate through GateMirroredSubsystem instead, so they kept
    /// working, which is what made it read as "only the lightbar".</para>
    ///
    /// <para>The distinction under test: driving the LANE is not driving the
    /// BAR. Only a payload that asserts validFlag1 bit 2 hands the lightbar
    /// over, and the #300 flashing fix still depends on that case holding
    /// for the full window.</para>
    /// </summary>
    public class LightbarPassthroughHoldTests : IDisposable
    {
        private const int Slot = 6;
        private readonly DualSensePassthroughDispatcher _d;

        public LightbarPassthroughHoldTests()
        {
            _d = new DualSensePassthroughDispatcher(Slot);
            // The real path: the worker is what sets the lane's driving
            // state, and HoldingLightbar deliberately requires it. With no
            // device assigned to this slot DispatchOne resolves zero targets
            // and returns, so the worker touches no hardware.
            _d.Start();
        }

        public void Dispose() => _d.Dispose();

        /// <summary>Waits for the worker to have processed a payload. Bounded,
        /// so a wedged worker fails the test instead of hanging it.</summary>
        private static void WaitForLaneDriving(int timeoutMs = 4000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (DualSensePassthroughDispatcher.IsHoldingState(Slot)) return;
                System.Threading.Thread.Sleep(15);
            }
        }

        /// <summary>Effect payload of the shape the mirror decodes:
        /// payload[0] validFlag0, payload[1] validFlag1, RGB at 44..46.</summary>
        private static byte[] Payload(byte validFlag1, byte r = 0, byte g = 0, byte b = 0)
        {
            var p = new byte[47];
            p[1] = validFlag1;
            p[44] = r; p[45] = g; p[46] = b;
            return p;
        }

        // validFlag1 bit 0 = rumble/mic lane, bit 2 = lightbar, bit 4 = pips.
        private const byte Vf1Lightbar = 0x04;
        private const byte Vf1NoLightbar = 0x01 | 0x10;

        /// <summary>THE REGRESSION. A host that drives the lane without ever
        /// asserting the lightbar must not take the bar. Before the fix this
        /// returned true and the Lighting tab was inert for 15 s at a time,
        /// renewed by every further packet.</summary>
        [Fact]
        public void EffectTrafficWithoutALightbarWrite_DoesNotHoldTheBar()
        {
            _d.Enqueue(0x02, Payload(Vf1NoLightbar));
            WaitForLaneDriving();

            Assert.False(DualSensePassthroughDispatcher.IsHoldingLightbar(Slot),
                "a payload that never asserts the lightbar enable bit must leave "
                + "the bar with the user (#334)");
        }

        /// <summary>The #300 case, unchanged: a game actually driving the bar
        /// holds it, so the identity writer stands down and the two writers
        /// stop alternating.</summary>
        [Fact]
        public void ALightbarWrite_HoldsTheBar()
        {
            _d.Enqueue(0x02, Payload(Vf1Lightbar, 255, 0, 0));
            WaitForLaneDriving();

            Assert.True(DualSensePassthroughDispatcher.IsHoldingLightbar(Slot),
                "a payload asserting the lightbar enable bit hands the bar to "
                + "the pass-through for its hold window (#300)");
        }

        /// <summary>Positive control for the test above: the lane IS driving
        /// state in the no-lightbar case, so the first test is measuring the
        /// per-subsystem distinction and not an inert dispatcher.</summary>
        [Fact]
        public void TheLaneIsDrivingStateEvenWhenItDoesNotDriveTheBar()
        {
            _d.Enqueue(0x02, Payload(Vf1NoLightbar));
            WaitForLaneDriving();

            Assert.True(DualSensePassthroughDispatcher.IsHoldingState(Slot),
                "the lane must be holding state, or the lightbar assertion "
                + "above proves nothing");
        }

        /// <summary>Mixed traffic: once the bar has been driven, later
        /// non-bar packets do not revoke the hold inside the window.</summary>
        [Fact]
        public void NonBarTrafficAfterABarWrite_KeepsTheHold()
        {
            _d.Enqueue(0x02, Payload(Vf1Lightbar, 0, 255, 0));
            _d.Enqueue(0x02, Payload(Vf1NoLightbar));
            WaitForLaneDriving();

            Assert.True(DualSensePassthroughDispatcher.IsHoldingLightbar(Slot));
        }

        /// <summary>An unknown slot never holds anything: the identity writer
        /// asks per slot and a missing dispatcher must not read as a claim.</summary>
        [Fact]
        public void ASlotWithNoDispatcher_HoldsNothing()
        {
            Assert.False(DualSensePassthroughDispatcher.IsHoldingLightbar(Slot + 1));
            Assert.False(DualSensePassthroughDispatcher.IsHoldingState(Slot + 1));
        }

        /// <summary>A payload too short to carry validFlag1 must not be read
        /// as a lightbar claim (and must not throw).</summary>
        [Fact]
        public void AShortPayload_IsNotALightbarClaim()
        {
            _d.Enqueue(0x02, new byte[] { 0x00 });

            Assert.False(DualSensePassthroughDispatcher.IsHoldingLightbar(Slot));
        }
    }
}
