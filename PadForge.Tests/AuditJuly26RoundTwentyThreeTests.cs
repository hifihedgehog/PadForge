using System.Threading;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round twenty-three.
    ///
    /// <para>Lens: a sibling-set sweep of a defect class with TWO confirmed
    /// instances already this session. Round fourteen found
    /// ConstantForceEvaluator returning its scratch while the caller held
    /// the reference across the next refill, and round fifteen found the
    /// slot combine aliasing a device's published arrays. Both are the same
    /// shape: a shared buffer whose reference outlives the call that filled
    /// it. So every shared scratch buffer in App and Engine was enumerated
    /// and checked.</para>
    ///
    /// <para>Most came back clean. GetSlotDeviceStates returns a
    /// ThreadStatic list under a "consume before the next call" contract,
    /// and all three of its callers honour it: each is a distinct method
    /// with its own local, each consumes it in the loop immediately below,
    /// the three never call one another, and the only work inside those
    /// loops is SourceEvaluator in PadForge.Engine, which cannot reach an
    /// App-private static. The wheel writers allocate a fresh report per
    /// call.</para>
    ///
    /// <para>ONE DEFECT FOUND AND FIXED. FanatecRawHidWriter's pedal report
    /// used a single shared static byte[] justified by a comment claiming
    /// "poll thread is the sole caller". That claim was false: the Remote
    /// Link output path (#138) reaches the same writer from the network
    /// receive thread whenever a remote peer drives a Fanatec pedal. Two
    /// unlocked threads writing byte 5 and byte 6 of one buffer could
    /// interleave and send a blend of the local and remote values. Now
    /// per-thread.</para></summary>
    public class AuditJuly26RoundTwentyThreeTests
    {
        /// <summary>The report layout, which the fix must not disturb:
        /// report ID at [0], the fixed Fanatec header, throttle at [5] and
        /// brake at [6].</summary>
        [Fact]
        public void PedalReport_HasTheDocumentedLayout()
        {
            var r = FanatecRawHidWriter.BuildPedalReport(throttle: 0xAB, brake: 0xCD);

            Assert.Equal(8, r.Length);
            Assert.Equal(0x01, r[0]);   // report ID IS byte 0, not a placeholder
            Assert.Equal(0xF8, r[1]);
            Assert.Equal(0x09, r[2]);
            Assert.Equal(0x01, r[3]);
            Assert.Equal(0x04, r[4]);
            Assert.Equal(0xAB, r[5]);   // throttle
            Assert.Equal(0xCD, r[6]);   // brake
            Assert.Equal(0x00, r[7]);
        }

        /// <summary>THE FIX. Two threads must never share the report
        /// buffer, or the poll thread's local pedal rumble and the Remote
        /// Link receive thread's remote one interleave their throttle and
        /// brake bytes and the pedal is driven with a value neither side
        /// asked for.</summary>
        [Fact]
        public void PedalReportBuffer_IsPerThread()
        {
            byte[] fromA = null, fromB = null;

            var a = new Thread(() => fromA = FanatecRawHidWriter.BuildPedalReport(1, 2));
            var b = new Thread(() => fromB = FanatecRawHidWriter.BuildPedalReport(3, 4));
            a.Start(); a.Join();
            b.Start(); b.Join();

            Assert.NotNull(fromA);
            Assert.NotNull(fromB);
            Assert.NotSame(fromA, fromB);

            // And each thread's buffer kept its OWN values.
            Assert.Equal(1, fromA[5]);
            Assert.Equal(2, fromA[6]);
            Assert.Equal(3, fromB[5]);
            Assert.Equal(4, fromB[6]);
        }

        /// <summary>Within one thread the buffer is still reused, which is
        /// the allocation-free property the shared static existed for. A
        /// fix that allocated per call would trade a race for garbage at
        /// pedal-rumble rate.</summary>
        [Fact]
        public void PedalReportBuffer_IsReusedWithinAThread()
        {
            var first = FanatecRawHidWriter.BuildPedalReport(10, 20);
            var second = FanatecRawHidWriter.BuildPedalReport(30, 40);

            Assert.Same(first, second);
            Assert.Equal(30, second[5]);
            Assert.Equal(40, second[6]);
        }

        /// <summary>The constant header survives reuse: only bytes 5 and 6
        /// are rewritten, so a second call cannot corrupt the command
        /// prefix the device parses.</summary>
        [Fact]
        public void RepeatedBuilds_LeaveTheHeaderIntact()
        {
            FanatecRawHidWriter.BuildPedalReport(0xFF, 0xFF);
            var r = FanatecRawHidWriter.BuildPedalReport(0x00, 0x00);

            Assert.Equal(0x01, r[0]);
            Assert.Equal(0xF8, r[1]);
            Assert.Equal(0x09, r[2]);
            Assert.Equal(0x01, r[3]);
            Assert.Equal(0x04, r[4]);
            Assert.Equal(0x00, r[7]);
        }
    }
}
