using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class AntiReplayWindowTests
    {
        [Fact]
        public void AcceptsMonotonicSequence()
        {
            var w = new AntiReplayWindow();
            for (uint i = 0; i < 1000; i++) Assert.True(w.CheckAndUpdate(i));
            Assert.Equal(999u, w.Highest);
        }

        [Fact]
        public void RejectsDuplicate()
        {
            var w = new AntiReplayWindow();
            Assert.True(w.CheckAndUpdate(10));
            Assert.False(w.CheckAndUpdate(10)); // replay
        }

        [Fact]
        public void AcceptsInWindowReorderOnce()
        {
            var w = new AntiReplayWindow();
            Assert.True(w.CheckAndUpdate(10));
            Assert.True(w.CheckAndUpdate(8));   // reordered but within window
            Assert.False(w.CheckAndUpdate(8));  // now a duplicate
            Assert.True(w.CheckAndUpdate(9));   // still fillable
        }

        [Fact]
        public void RejectsTooOld()
        {
            var w = new AntiReplayWindow();
            Assert.True(w.CheckAndUpdate(100));
            Assert.False(w.CheckAndUpdate(100 - 64)); // exactly at/over the window edge -> stale
            Assert.False(w.CheckAndUpdate(10));
        }

        [Fact]
        public void SurvivesU32Wrap()
        {
            var w = new AntiReplayWindow();
            Assert.True(w.CheckAndUpdate(uint.MaxValue - 1));
            Assert.True(w.CheckAndUpdate(uint.MaxValue));
            Assert.True(w.CheckAndUpdate(0));   // wrapped, still newest
            Assert.True(w.CheckAndUpdate(1));
            Assert.False(w.CheckAndUpdate(uint.MaxValue)); // pre-wrap duplicate rejected
        }

        [Fact]
        public void IsAfter_WrapSafe()
        {
            Assert.True(AntiReplayWindow.IsAfter(0, uint.MaxValue));        // 0 is after MAX (wrapped)
            Assert.False(AntiReplayWindow.IsAfter(uint.MaxValue, 0));
            Assert.True(AntiReplayWindow.IsAfter(5, 4));
            Assert.False(AntiReplayWindow.IsAfter(4, 4));
        }
    }

    public class LinkSessionTests
    {
        private static (LinkSession initiator, LinkSession responder) Pair()
        {
            var key = PeerCrypto.RandomBytes(PeerCrypto.KeySize);
            return (new LinkSession(key, isInitiator: true), new LinkSession(key, isInitiator: false));
        }

        [Fact]
        public void SealOpen_RoundTripsPayloadAndMetadata()
        {
            var (a, b) = Pair();
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            var datagram = a.Seal(LinkMessageType.Input, slotId: 2, timestampUs: 123456789UL, payload);

            Assert.True(b.Open(datagram, out var type, out var slot, out var ts, out var got));
            Assert.Equal(LinkMessageType.Input, type);
            Assert.Equal((byte)2, slot);
            Assert.Equal(123456789UL, ts);
            Assert.Equal(payload, got);
        }

        [Fact]
        public void Open_RejectsReplayedDatagram()
        {
            var (a, b) = Pair();
            var dg = a.Seal(LinkMessageType.Input, 0, 1, new byte[] { 9 });
            Assert.True(b.Open(dg, out _, out _, out _, out _));
            Assert.False(b.Open(dg, out _, out _, out _, out _)); // exact replay
        }

        [Fact]
        public void Open_RejectsTamperedHeaderAndCiphertext()
        {
            var (a, b) = Pair();
            var dg = a.Seal(LinkMessageType.Input, 0, 1, new byte[] { 9, 8, 7 });

            var tHeader = (byte[])dg.Clone(); tHeader[1] ^= 0xFF; // flip slot id (AAD)
            Assert.False(b.Open(tHeader, out _, out _, out _, out _));

            var tBody = (byte[])dg.Clone(); tBody[LinkSession.HeaderSize] ^= 0xFF; // flip ciphertext
            Assert.False(b.Open(tBody, out _, out _, out _, out _));
        }

        [Fact]
        public void Open_RejectsWrongKey()
        {
            var (a, _) = Pair();
            var stranger = new LinkSession(PeerCrypto.RandomBytes(PeerCrypto.KeySize), isInitiator: false);
            var dg = a.Seal(LinkMessageType.Input, 0, 1, new byte[] { 1 });
            Assert.False(stranger.Open(dg, out _, out _, out _, out _));
        }

        [Fact]
        public void Open_RejectsEpochMismatch()
        {
            var key = PeerCrypto.RandomBytes(PeerCrypto.KeySize);
            var a = new LinkSession(key, isInitiator: true, epoch: 1);
            var b = new LinkSession(key, isInitiator: false, epoch: 2);
            var dg = a.Seal(LinkMessageType.Input, 0, 1, new byte[] { 1 });
            Assert.False(b.Open(dg, out _, out _, out _, out _));
        }

        [Fact]
        public void SameDirectionTwoPeers_DoNotCrossOpen()
        {
            // Both sides sending: a's outbound must open on b's recv side and vice versa,
            // but a frame a sealed must NOT open on a's own recv (disjoint nonce space).
            var (a, b) = Pair();
            var fromA = a.Seal(LinkMessageType.Input, 0, 10, new byte[] { 1 });
            Assert.True(b.Open(fromA, out _, out _, out _, out _));
            Assert.False(a.Open(fromA, out _, out _, out _, out _)); // wrong direction salt
        }

        [Fact]
        public void Streaming_NewestWinsUnderReorderAndLoss()
        {
            var (a, b) = Pair();
            var datagrams = new System.Collections.Generic.List<byte[]>();
            for (int i = 0; i < 10; i++)
                datagrams.Add(a.Seal(LinkMessageType.Input, 0, (ulong)i, new byte[] { (byte)i }));

            // Deliver out of order with a gap (index 4 "lost"): 0,3,2,5,7,6,9
            int[] order = { 0, 3, 2, 5, 7, 6, 9 };
            foreach (var i in order)
                Assert.True(b.Open(datagrams[i], out _, out _, out _, out var p) && p[0] == (byte)i);

            // A late duplicate of an already-seen one is rejected.
            Assert.False(b.Open(datagrams[3], out _, out _, out _, out _));
        }

        [Fact]
        public void SealCounter_Advances()
        {
            var (a, _) = Pair();
            Assert.Equal(0u, a.SendCounter);
            a.Seal(LinkMessageType.Input, 0, 1, new byte[] { 1 });
            a.Seal(LinkMessageType.Input, 0, 2, new byte[] { 2 });
            Assert.Equal(2u, a.SendCounter);
        }

        [Fact]
        public void Open_RejectsTooShort()
        {
            var (_, b) = Pair();
            Assert.False(b.Open(new byte[] { 1, 2, 3 }, out _, out _, out _, out _));
        }
    }
}
