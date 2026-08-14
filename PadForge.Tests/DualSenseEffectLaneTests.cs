using System.Threading.Channels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guards for the DualSense effect lane's queueing contract (#300).
    ///
    /// The lane forwards game-written effect payloads to the physical pad and
    /// rents every payload from ArrayPool. That makes "what happens to an item
    /// the queue refuses" a memory-correctness question, not a throughput one,
    /// and the answer was got wrong on a belief about Channel semantics that
    /// nothing checked.
    /// </summary>
    public class DualSenseEffectLaneTests
    {
        // ── The belief that caused the leak ──
        //
        // The dispatcher used a bounded channel with FullMode.DropWrite and a
        // comment stating that TryWrite returns FALSE on overflow, so the
        // producer could return its rented buffer. It does not. Every Drop
        // mode accepts the write, reports success, and discards the item, so
        // the rental was never handed back to anyone.
        //
        // A field trace measured the cost: a title driving this lane at about
        // 18,000 packets per second showed roughly 10,500 per second
        // unaccounted for between what was enqueued and what was either
        // coalesced or written, with the drop counter reading zero throughout.
        // Those were the leaked rentals.
        //
        // This test pins the real semantics so the belief cannot come back.

        [Fact]
        public void DropWrite_AcceptsTheWriteAndDiscardsIt_SoAPooledPayloadWouldLeak()
        {
            var ch = Channel.CreateBounded<int>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });

            Assert.True(ch.Writer.TryWrite(1));
            Assert.True(ch.Writer.TryWrite(2));

            // The channel is full. This is the assertion that matters: the
            // write is REFUSED in effect but REPORTED as accepted, so a
            // producer keying its cleanup off the return value never cleans up.
            Assert.True(ch.Writer.TryWrite(3));

            Assert.True(ch.Reader.TryRead(out int first));
            Assert.True(ch.Reader.TryRead(out int second));
            Assert.False(ch.Reader.TryRead(out _));
            Assert.Equal(1, first);
            Assert.Equal(2, second);   // 3 was swallowed, never delivered
        }

        [Fact]
        public void WaitMode_ReportsAFullChannel_WhichIsWhatTheFeatureLaneNeeds()
        {
            // Vendor commands still queue, because they are events where order
            // and count matter. They use Wait precisely so a full channel comes
            // back as false and the producer can return the rental.
            var ch = Channel.CreateBounded<int>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

            Assert.True(ch.Writer.TryWrite(1));
            Assert.True(ch.Writer.TryWrite(2));
            Assert.False(ch.Writer.TryWrite(3));
        }
    }
}
