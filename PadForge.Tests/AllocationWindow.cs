using System;
using System.Runtime;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Counts what a piece of code allocates on the test thread, and only
    /// that. GC.GetAllocatedBytesForCurrentThread is the thread's whole count,
    /// and the runtime moves it without the code. In full runs the count grew
    /// by 1,000 to 8,008 bytes in windows that allocate nothing, a loop of
    /// arithmetic with no call in it among them, and each such window was one
    /// in which the runtime paused the threads (GC.GetTotalPauseDuration grew)
    /// with no collection counted. Each of those pauses came after a gen2
    /// collection had been counted. A window that held a counted collection
    /// grew by nothing. The runtime also compiles on the thread that runs the
    /// code, at a method's first call and when it swaps a long-running loop
    /// for an optimized copy mid-run (on-stack replacement), whose count
    /// carries over from one call of the method to the next and so lands in a
    /// theory's later case. A window holding a pause or a compile on this
    /// thread is counted again.
    /// </summary>
    internal static class AllocationWindow
    {
        /// <summary>The bytes this thread allocates over
        /// <paramref name="runs"/> runs of <paramref name="body"/> in each of
        /// three windows the runtime left alone, after one window of the same
        /// runs that the first calls, class constructors and caches
        /// spend.</summary>
        internal static long[] Clean(Action body, int runs)
        {
            Count(body, runs, out _);
            var clean = new long[3];
            int found = 0;
            for (int tries = 0; found < clean.Length && tries < 100; tries++)
            {
                long bytes = Count(body, runs, out bool disturbed);
                if (!disturbed) clean[found++] = bytes;
            }
            Assert.True(found == clean.Length, "the runtime paused or compiled in every window");
            return clean;
        }

        /// <summary>One window: the bytes the thread allocated over the runs,
        /// and whether the runtime paused the threads or compiled on this one
        /// meanwhile.</summary>
        private static long Count(Action body, int runs, out bool disturbed)
        {
            long methods = JitInfo.GetCompiledMethodCount(currentThread: true);
            TimeSpan paused = GC.GetTotalPauseDuration();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < runs; i++) body();
            long after = GC.GetAllocatedBytesForCurrentThread();
            disturbed = GC.GetTotalPauseDuration() != paused
                || JitInfo.GetCompiledMethodCount(currentThread: true) != methods;
            return after - before;
        }

        /// <summary>An object kept where the compiler cannot drop the
        /// allocation that made it: the positive controls' deliberate
        /// allocation.</summary>
        internal static object Kept;

        /// <summary>A hold-back test that allocates on every call and holds
        /// nothing back: a deliberate allocation inside a key read.</summary>
        internal static bool AllocatingHoldBack(int slotIndex, string deviceGuid, string descriptor)
        {
            Kept = new object();
            return false;
        }
    }

    /// <summary>
    /// Allocation tests whose windows run long. A pause of the threads
    /// anywhere in the process spoils the window it lands in, and in a full
    /// run, with the parallel classes allocating, one landed in each of the
    /// hundred 2000-poll windows of the wheel curve's test, which then had no
    /// window to count. xunit runs a DisableParallelization collection after
    /// the parallel ones have ended, and its classes one at a time, so these
    /// windows run beside no other test.
    /// </summary>
    [CollectionDefinition("AllocationMeasurements", DisableParallelization = true)]
    public sealed class AllocationMeasurementsCollection { }
}
