using System.Buffers.Binary;
using PadForge.Engine.RemoteLink;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round twenty-two.
    ///
    /// <para>NO DEFECT FOUND in the Remote Link crypto. The primitive
    /// choices are modern and correct (X25519, Ed25519,
    /// ChaCha20-Poly1305, HKDF-SHA256, RandomNumberGenerator for entropy,
    /// CryptographicOperations.FixedTimeEquals for comparisons). Nonce
    /// construction is sound: disjoint per-direction salts that mirror
    /// correctly between the two sides, an Interlocked sequence so
    /// concurrent senders cannot share a nonce, a lock around the shared
    /// nonce buffer and cipher so the atomic sequence is not undone by a
    /// buffer race, an exhaustion guard that THROWS rather than wrapping,
    /// and PeerCrypto.BuildNonce's byte layout matching what LinkSession
    /// writes.</para>
    ///
    /// <para>What was missing was coverage of the invariants that are
    /// security-critical rather than functional. The existing suite covers
    /// round-trip, replay, tamper and wrong-key. It did NOT cover
    /// verify-then-window, which is the one whose regression is a remote
    /// denial of service rather than a bug.</para></summary>
    public class AuditJuly26RoundTwentyTwoTests
    {
        private static (LinkSession initiator, LinkSession responder) Pair()
        {
            var key = PeerCrypto.RandomBytes(PeerCrypto.KeySize);
            return (new LinkSession(key, isInitiator: true),
                    new LinkSession(key, isInitiator: false));
        }

        /// <summary>Sequence lives at header bytes [2..5] as a little-endian
        /// u32 and is part of the AEAD's associated data, so rewriting it
        /// invalidates the tag. That is exactly what makes this a usable
        /// forgery for the test below.</summary>
        private static byte[] ForgeSequence(byte[] datagram, uint seq)
        {
            var forged = (byte[])datagram.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(2), seq);
            return forged;
        }

        /// <summary>THE ONE THAT MATTERS. The replay window must advance
        /// only AFTER the tag verifies.
        ///
        /// <para>If a forged sequence could advance it first, one spoofed
        /// UDP datagram carrying a huge sequence number would push the
        /// window past everything the real peer is about to send, and every
        /// subsequent legitimate datagram would be rejected as stale. That
        /// is a remote denial of service costing the attacker a single
        /// packet, and it needs no key, since the packet never has to
        /// verify to do the damage. The implementation orders this
        /// correctly today and its comment says so. Nothing guarded
        /// it.</para></summary>
        [Fact]
        public void ForgedDatagram_DoesNotAdvanceTheReplayWindow()
        {
            var (a, b) = Pair();

            // A legitimate datagram the receiver has not seen yet, at seq 0.
            var legit = a.Seal(LinkMessageType.Input, 0, 1, new byte[] { 7, 7, 7 });

            // An attacker replays it with the sequence rewritten AHEAD. The
            // value must be forward of seq 0 under RFC 1982 serial
            // arithmetic, which is what the window uses: a first attempt at
            // this test picked 0xFFFFFF00, and serial arithmetic reads that
            // as 256 BEHIND zero rather than far ahead, so it could never
            // have advanced the window and the test passed under a
            // deliberately broken build. Mutation caught it.
            var forged = ForgeSequence(legit, 50_000u);
            Assert.False(b.Open(forged, out _, out _, out _, out _));

            // ...and the rejection must have left the window untouched, so
            // the real datagram still opens.
            Assert.True(b.Open(legit, out _, out _, out _, out var got),
                "a rejected forgery advanced the replay window and locked out the real peer");
            Assert.Equal(new byte[] { 7, 7, 7 }, got);
        }

        /// <summary>The same property under repetition: a flood of forged
        /// sequences must not degrade the session at all.</summary>
        [Fact]
        public void ForgedFlood_LeavesTheSessionUsable()
        {
            var (a, b) = Pair();
            var legit = a.Seal(LinkMessageType.Input, 0, 1, new byte[] { 42 });

            for (uint s = 1; s <= 64; s++)
                Assert.False(b.Open(ForgeSequence(legit, s * 1000u), out _, out _, out _, out _));

            Assert.True(b.Open(legit, out _, out _, out _, out _),
                "a forged flood locked out the real peer");
        }

        /// <summary>Direction separation, proved on REAL sessions rather
        /// than on the nonce helper. The two sides share one key, so the
        /// only thing keeping their nonce spaces disjoint is the salt each
        /// side picks from isInitiator. A datagram must therefore open on
        /// the PEER and never on its own sender: if it opened on itself the
        /// salts would be identical, both directions would march through
        /// the same nonces under the same key, and ChaCha20-Poly1305 would
        /// be catastrophically broken.</summary>
        [Fact]
        public void DatagramOpensOnThePeerAndNeverOnItsOwnSender()
        {
            var (a, b) = Pair();
            var fromA = a.Seal(LinkMessageType.Input, 1, 5, new byte[] { 1, 2 });

            Assert.False(a.Open(fromA, out _, out _, out _, out _));
            Assert.True(b.Open(fromA, out _, out _, out _, out _));

            var fromB = b.Seal(LinkMessageType.Output, 2, 6, new byte[] { 3, 4 });
            Assert.False(b.Open(fromB, out _, out _, out _, out _));
            Assert.True(a.Open(fromB, out _, out _, out _, out _));
        }

        /// <summary>Both directions run their own counter from zero, and
        /// that is safe precisely BECAUSE the salts differ. This pins the
        /// pairing: independent counters plus disjoint salts.</summary>
        [Fact]
        public void EachDirectionCountsIndependently()
        {
            var (a, b) = Pair();
            Assert.Equal(0u, a.SendCounter);
            Assert.Equal(0u, b.SendCounter);

            a.Seal(LinkMessageType.Input, 0, 1, new byte[] { 1 });
            a.Seal(LinkMessageType.Input, 0, 2, new byte[] { 2 });

            Assert.Equal(2u, a.SendCounter);
            Assert.Equal(0u, b.SendCounter);
        }

        /// <summary>The counter never repeats within a session, which is
        /// the whole basis of nonce uniqueness on one key.</summary>
        [Fact]
        public void SendCounterIsStrictlyMonotonic()
        {
            var (a, _) = Pair();
            uint previous = a.SendCounter;
            for (int i = 0; i < 200; i++)
            {
                a.Seal(LinkMessageType.Input, 0, (ulong)i, new byte[] { (byte)i });
                Assert.True(a.SendCounter > previous, "send counter failed to advance");
                previous = a.SendCounter;
            }
        }

        /// <summary>UDP reorders, so datagrams arriving out of order within
        /// the window must still open. A window that only accepted
        /// strictly increasing sequences would drop real traffic on any
        /// jittery link.</summary>
        [Fact]
        public void OutOfOrderWithinTheWindowStillOpens()
        {
            var (a, b) = Pair();
            var d0 = a.Seal(LinkMessageType.Input, 0, 1, new byte[] { 0 });
            var d1 = a.Seal(LinkMessageType.Input, 0, 2, new byte[] { 1 });
            var d2 = a.Seal(LinkMessageType.Input, 0, 3, new byte[] { 2 });

            // Deliver 2, 0, 1.
            Assert.True(b.Open(d2, out _, out _, out _, out _));
            Assert.True(b.Open(d0, out _, out _, out _, out _));
            Assert.True(b.Open(d1, out _, out _, out _, out _));

            // Each is still single-use.
            Assert.False(b.Open(d1, out _, out _, out _, out _));
        }

        /// <summary>A truncated datagram fails closed rather than throwing
        /// into the receive loop. This runs on a socket thread facing
        /// arbitrary internet input, so an exception here is an outage.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(LinkSession.HeaderSize)]
        [InlineData(LinkSession.HeaderSize + 4)]
        public void ShortDatagramsFailClosed(int length)
        {
            var (a, b) = Pair();
            var dg = a.Seal(LinkMessageType.Input, 0, 1, new byte[] { 1, 2, 3, 4 });
            var truncated = new byte[length];
            System.Array.Copy(dg, truncated, System.Math.Min(length, dg.Length));

            Assert.False(b.Open(truncated, out _, out _, out _, out _));
        }

        [Fact]
        public void NullDatagramFailsClosed()
        {
            var (_, b) = Pair();
            Assert.False(b.Open(null, out _, out _, out _, out _));
        }
    }
}
