using System.Threading;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round twenty-four.
    ///
    /// <para>Lens: sweep the class "a comment asserts a safety invariant
    /// that is factually false". Two confirmed instances already. Round
    /// thirteen found TryAutoCalibrateGyros claiming "one local, read
    /// once" while three separate reads shipped, and round twenty-three
    /// found FanatecRawHidWriter claiming "poll thread is the sole caller"
    /// while the Remote Link output path reached it from the network
    /// thread. Grepping every comment claiming an exclusive caller or
    /// thread, then verifying each against the actual callers, surfaced a
    /// third instance immediately.</para>
    ///
    /// <para>THE DEFECT. XboxImpulseHidWriter carried the SAME false claim
    /// in two places, guarding two pieces of shared mutable state, and its
    /// second caller is the very same Remote Link path
    /// (InputService's effect apply, the impulse-trigger branch). Round
    /// twenty-three had actually READ that call site while fixing the
    /// Fanatec sibling and did not follow it through, which is the
    /// fix-the-whole-sibling-set failure this repo keeps repeating.</para>
    ///
    /// <para>This one was worse than the Fanatec instance. The shared byte
    /// buffer only produced blended motor values, but s_targets was a
    /// PLAIN Dictionary mutated (Remove and indexer-assign) from both
    /// threads with no lock. A concurrently mutated Dictionary can corrupt
    /// its buckets, and a corrupt Dictionary sends TryGetValue into an
    /// infinite loop, which on the 1 kHz poll thread is a hung input
    /// pipeline rather than a wrong value. Now a ConcurrentDictionary, with
    /// TryAdd so a lost cache race drops its own handle instead of
    /// orphaning one under an overwritten entry.</para></summary>
    public class AuditJuly26RoundTwentyFourTests
    {
        /// <summary>The GIP report frame the fix must not disturb: the
        /// fixed X1nput command bytes, with LT / RT / large / small at
        /// 2..5.</summary>
        [Fact]
        public void Report_HasTheDocumentedGipFrame()
        {
            var r = XboxImpulseHidWriter.BuildReport(lt: 0x11, rt: 0x22, lm: 0x33, rm: 0x44);

            Assert.Equal(9, r.Length);
            Assert.Equal(0x03, r[0]);
            Assert.Equal(0x0F, r[1]);
            Assert.Equal(0x11, r[2]);   // left trigger
            Assert.Equal(0x22, r[3]);   // right trigger
            Assert.Equal(0x33, r[4]);   // large motor
            Assert.Equal(0x44, r[5]);   // small motor
            Assert.Equal(0xFF, r[6]);
            Assert.Equal(0x00, r[7]);
            Assert.Equal(0xEB, r[8]);
        }

        /// <summary>THE FIX. The poll thread and the Remote Link receive
        /// thread must not share the report buffer, or a local rumble and a
        /// remote one interleave their four motor bytes and the pad is
        /// driven with a frame neither side sent.</summary>
        [Fact]
        public void ReportBuffer_IsPerThread()
        {
            byte[] fromA = null, fromB = null;

            var a = new Thread(() => fromA = XboxImpulseHidWriter.BuildReport(1, 2, 3, 4));
            var b = new Thread(() => fromB = XboxImpulseHidWriter.BuildReport(5, 6, 7, 8));
            a.Start(); a.Join();
            b.Start(); b.Join();

            Assert.NotNull(fromA);
            Assert.NotNull(fromB);
            Assert.NotSame(fromA, fromB);

            Assert.Equal(1, fromA[2]); Assert.Equal(4, fromA[5]);
            Assert.Equal(5, fromB[2]); Assert.Equal(8, fromB[5]);
        }

        /// <summary>Within one thread the buffer is still reused, which is
        /// the allocation-free property the shared static existed for.</summary>
        [Fact]
        public void ReportBuffer_IsReusedWithinAThread()
        {
            var first = XboxImpulseHidWriter.BuildReport(1, 1, 1, 1);
            var second = XboxImpulseHidWriter.BuildReport(9, 9, 9, 9);

            Assert.Same(first, second);
            Assert.Equal(9, second[2]);
        }

        /// <summary>The fixed command frame survives reuse: only bytes 2..5
        /// are rewritten, so a second call cannot corrupt the header the
        /// XUSB driver parses.</summary>
        [Fact]
        public void RepeatedBuilds_LeaveTheCommandFrameIntact()
        {
            XboxImpulseHidWriter.BuildReport(0xFF, 0xFF, 0xFF, 0xFF);
            var r = XboxImpulseHidWriter.BuildReport(0, 0, 0, 0);

            Assert.Equal(0x03, r[0]);
            Assert.Equal(0x0F, r[1]);
            Assert.Equal(0xFF, r[6]);
            Assert.Equal(0x00, r[7]);
            Assert.Equal(0xEB, r[8]);
        }

        /// <summary>Concurrent builds across many threads stay
        /// self-consistent. With one shared buffer this is where the
        /// interleaving showed up: a thread reading back its own frame
        /// would find another thread's motor bytes in it.</summary>
        [Fact]
        public void ConcurrentBuilds_EachSeeOnlyTheirOwnValues()
        {
            const int threads = 8;
            var mismatches = 0;
            var workers = new Thread[threads];

            for (int i = 0; i < threads; i++)
            {
                byte v = (byte)(i + 1);
                workers[i] = new Thread(() =>
                {
                    for (int n = 0; n < 500; n++)
                    {
                        var r = XboxImpulseHidWriter.BuildReport(v, v, v, v);
                        if (r[2] != v || r[3] != v || r[4] != v || r[5] != v)
                            Interlocked.Increment(ref mismatches);
                    }
                });
            }

            foreach (var t in workers) t.Start();
            foreach (var t in workers) t.Join();

            Assert.Equal(0, mismatches);
        }
    }
}
