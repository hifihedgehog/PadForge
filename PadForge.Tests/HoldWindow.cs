using System;
using System.Diagnostics;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Checks that a Steam read holds its last change for its hold, by the
    /// clock readings taken around the reads.
    ///
    /// <para>The hold runs from the clock reading the changing read takes
    /// itself (BoundedReads: <c>latch.Until = now + hold</c>), so a read
    /// taken inside it must hold. Under a loaded run the test thread can be
    /// paused past a hold of 1/120 s between two reads, and the second read
    /// then lets go, as the read should. A pause that straddles the hold's
    /// end proves neither. On 2026-11-17 the full suite failed
    /// Ring_DpadEdge_HoldsAChangeFor120thOfASecond once at the read that
    /// expected the hold, which assumed two reads fall within 8.3 ms.</para>
    /// </summary>
    internal static class HoldWindow
    {
        /// <summary>Presses, changes the input, and reads again: held while
        /// the second read fell inside the hold, let go once it fell past
        /// it.</summary>
        internal static void AssertHeld(Func<bool> press, Action change, Func<bool> read, long holdTicks)
        {
            long beforePress = Stopwatch.GetTimestamp();
            Assert.True(press());
            long afterPress = Stopwatch.GetTimestamp();
            change();
            long beforeRead = Stopwatch.GetTimestamp();
            bool held = read();
            long afterRead = Stopwatch.GetTimestamp();
            if (afterRead < beforePress + holdTicks) Assert.True(held);
            else if (beforeRead >= afterPress + holdTicks) Assert.False(held);
        }
    }
}
