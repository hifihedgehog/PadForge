using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #206 long-press activator fire decision. With DelayMs = 0 the
    /// classic rising edge is preserved. With DelayMs &gt; 0 the edge
    /// modes fire exactly once when the hold crosses the threshold, a
    /// shorter tap fires nothing, and release re-arms. Pre-fix, the
    /// rising-edge-plus-delay gate meant Toggle/Latch/Sticky with a
    /// delay could never fire at all.
    /// </summary>
    public class ShiftLongPressTests
    {
        [Fact]
        public void NoDelay_KeepsClassicRisingEdge()
        {
            bool latch = false;
            Assert.True(InputManager.ComputeActivatorFire(true, false, 0, 0, ref latch));
            Assert.False(InputManager.ComputeActivatorFire(true, true, 100, 0, ref latch));
            Assert.False(InputManager.ComputeActivatorFire(false, true, 0, 0, ref latch));
        }

        [Fact]
        public void LongPress_FiresOnceAtThreshold()
        {
            bool latch = false;
            // Press frame: below threshold, no fire.
            Assert.False(InputManager.ComputeActivatorFire(true, false, 0, 500, ref latch));
            // Held, still below.
            Assert.False(InputManager.ComputeActivatorFire(true, true, 300, 500, ref latch));
            // Crosses threshold: fires exactly once.
            Assert.True(InputManager.ComputeActivatorFire(true, true, 520, 500, ref latch));
            // Continued hold past threshold: latched, no refire.
            Assert.False(InputManager.ComputeActivatorFire(true, true, 900, 500, ref latch));
            Assert.False(InputManager.ComputeActivatorFire(true, true, 5000, 500, ref latch));
        }

        [Fact]
        public void ShortTap_NeverFires_AndReleaseRearms()
        {
            bool latch = false;
            Assert.False(InputManager.ComputeActivatorFire(true, false, 0, 500, ref latch));
            Assert.False(InputManager.ComputeActivatorFire(true, true, 200, 500, ref latch));
            // Released before the threshold: nothing fired, latch clear.
            Assert.False(InputManager.ComputeActivatorFire(false, true, 0, 500, ref latch));
            Assert.False(latch);
            // Second, long hold fires again.
            Assert.False(InputManager.ComputeActivatorFire(true, false, 0, 500, ref latch));
            Assert.True(InputManager.ComputeActivatorFire(true, true, 500, 500, ref latch));
        }

        [Fact]
        public void ReleaseAfterFire_ClearsLatch()
        {
            bool latch = false;
            Assert.False(InputManager.ComputeActivatorFire(true, false, 0, 250, ref latch));
            Assert.True(InputManager.ComputeActivatorFire(true, true, 260, 250, ref latch));
            Assert.True(latch);
            Assert.False(InputManager.ComputeActivatorFire(false, true, 0, 250, ref latch));
            Assert.False(latch);
        }
    }
}
