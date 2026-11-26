using System;
using System.Reflection;
using HIDMaestro;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The axis of a trigger past a profile's named pair is looked up on
    /// every frame SubmitRawHidState sends, on the poll thread, which
    /// allocates nothing per pass. The candidates were a new array on each
    /// call, kept off the heap only once the JIT has optimized the method,
    /// and a program's first frames run unoptimized code.
    /// </summary>
    public class DeclaredAxisAllocationTests
    {
        [Fact]
        public void TheAxisPastTheNamedTriggersAllocatesNothing()
        {
            var method = typeof(HMaestroVirtualController).GetMethod("DeclaredAxisAt", BindingFlags.NonPublic | BindingFlags.Static);
            var axisAt = (Func<int, int, int, HMAxis>)method.CreateDelegate(typeof(Func<int, int, int, HMAxis>));
            Assert.Equal(HMAxis.Slider, axisAt(2, 4, 2));

            HMAxis last = HMAxis.None;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) last = axisAt(2, 4, 2 + (i & 1));
            long grew = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(HMAxis.Dial, last);
            Assert.Equal(0, grew);
        }
    }
}
