using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The visual-order create gate is an ORDERING PREFERENCE
    /// (HIDMaestro allocates kernel slots in creation order, so PadForge
    /// creates visually-higher slots first). It used to be an UNBOUNDED
    /// wait, which let a preference outrank functioning entirely.
    ///
    /// <para>The deadlock, owner-reported 2026-07-26: a device flap aborts
    /// a higher slot's create. The abort path deliberately does not latch
    /// _createFailed, so a later genuine device arrival can still retry.
    /// That leaves the higher slot in exactly the blocking state
    /// (Created + Enabled + Active + not-failed + vc == null), and every
    /// visually-lower slot in the same type group then waited on it
    /// forever. Nothing appeared on the virtual controller in joy.cpl while
    /// PadForge's own preview kept updating, because the preview reads the
    /// combined state and only the SUBMIT needs a VC. The owner's
    /// workaround was switching a lower slot's type, which moves it into a
    /// different order group and out from behind the blocker.</para>
    ///
    /// <para>These pin the timing rule. The gate itself lives in a private
    /// poll-loop method needing a fully built InputManager, so the decision
    /// was split out to make it testable. That split is deliberate: the bug
    /// was found on hardware and the owner cannot reproduce it on demand,
    /// so a fix that could not be locked by a test would have been
    /// unacceptable.</para></summary>
    public class VisualOrderWaitBoundTests
    {
        // Mirrors InputManager.OrderWaitMaxMs. Kept as a literal so a
        // change to the production window fails these deliberately.
        private const long WindowMs = 45_000;

        /// <summary>Not waiting yet: a zero tick must never expire, or a
        /// slot would create out of order on its very first blocked
        /// cycle and defeat the ordering the gate exists for.</summary>
        [Fact]
        public void ZeroTick_NeverExpires()
        {
            Assert.False(InputManager.OrderWaitExpired(0, 0));
            Assert.False(InputManager.OrderWaitExpired(0, long.MaxValue / 2));
        }

        /// <summary>Inside the window the slot keeps waiting, so a normal
        /// serialized queue still creates in visual order.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(10_000)]
        [InlineData(WindowMs - 1)]
        public void InsideTheWindow_KeepsWaiting(long elapsed)
        {
            Assert.False(InputManager.OrderWaitExpired(1_000_000, 1_000_000 + elapsed));
        }

        /// <summary>THE FIX. Once one unchanged blocker has held for the
        /// whole window, the waiting slot creates anyway. Without this the
        /// slot stays dead until the user reconfigures it.</summary>
        [Theory]
        [InlineData(WindowMs)]
        [InlineData(WindowMs + 1)]
        [InlineData(WindowMs * 10)]
        public void PastTheWindow_BreaksTheWait(long elapsed)
        {
            Assert.True(InputManager.OrderWaitExpired(1_000_000, 1_000_000 + elapsed));
        }

        /// <summary>The window is long enough to clear a legitimate queue.
        /// A single HIDMaestro create runs 3-11 s and they serialize, so a
        /// four-slot queue is worst-case ~44 s. Tripping the escape on a
        /// healthy queue would create out of order for no reason.</summary>
        [Fact]
        public void Window_ClearsALegitimateFourSlotQueue()
        {
            const long worstCaseQueueMs = 4 * 11_000;
            Assert.True(WindowMs > worstCaseQueueMs,
                $"window {WindowMs}ms must exceed a worst-case serialized queue of {worstCaseQueueMs}ms");
            Assert.False(InputManager.OrderWaitExpired(0 + 1, 1 + worstCaseQueueMs));
        }

        /// <summary>The counter is monotonic-tick based, so it must not
        /// expire on a backwards or equal clock reading.</summary>
        [Fact]
        public void NonAdvancingClock_DoesNotExpire()
        {
            Assert.False(InputManager.OrderWaitExpired(5_000, 5_000));
            Assert.False(InputManager.OrderWaitExpired(5_000, 4_000));
        }
    }
}
