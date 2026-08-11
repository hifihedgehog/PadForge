using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.RemoteLink.Dht;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #294 DHT presence store: the bencode codec, BEP 44 mutable-item
    /// construction proven against the spec's OFFICIAL test vectors, and the
    /// corrected pairwise-capability presence crypto (the Ed25519 public key is
    /// published by BEP 44, so the record is encrypted under a per-pairing
    /// capability the storage nodes never see). All fully offline: the KRPC
    /// network client is the live residual, not this.
    /// </summary>
    public class DhtPresenceTests
    {
        // ── Bencode ──

        [Fact]
        public void Bencode_RoundTripsKrpcShapes()
        {
            var dict = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["a"] = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["id"] = Encoding.ASCII.GetBytes("abcdefghij0123456789"),
                    ["seq"] = 42L,
                },
                ["q"] = Encoding.ASCII.GetBytes("get"),
                ["y"] = Encoding.ASCII.GetBytes("q"),
            };
            var encoded = Bencode.Encode(dict);
            var decoded = Bencode.Decode(encoded);
            Assert.Equal(42L, Bencode.GetLong(Bencode.Decode(Bencode.Encode(((IDictionary<string, object>)decoded)["a"])), "seq"));
            // Deterministic byte output (keys sorted).
            Assert.Equal(encoded, Bencode.Encode(decoded));
        }

        [Fact]
        public void Bencode_KeysSortByRawByteOrder()
        {
            // Out-of-order insertion must still encode in sorted key order.
            var dict = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var encoded = Bencode.Encode(new Dictionary<string, object> { ["z"] = 1L, ["a"] = 2L });
            Assert.Equal(Encoding.ASCII.GetBytes("d1:ai2e1:zi1ee"), encoded);
        }

        [Fact]
        public void Bencode_StringsAreBytesNotText()
        {
            // A value with bytes that are not valid UTF-8 must survive intact.
            var raw = new byte[] { 0x00, 0xFF, 0x80, 0x41 };
            var encoded = Bencode.Encode(raw);
            var decoded = (byte[])Bencode.Decode(encoded);
            Assert.Equal(raw, decoded);
        }

        // ── BEP 44 official test vectors ──
        // From bittorrent.org/beps/bep_0044.html.

        private static byte[] Hex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        private static readonly byte[] VectorPubKey =
            Hex("77ff84905a91936367c01360803104f92432fcd904a43511876df5cdf3e7e548");

        [Fact]
        public void Bep44_Vector_NoSalt_TargetMatches()
        {
            var target = Bep44Record.ComputeTarget(VectorPubKey);
            Assert.Equal(Hex("4a533d47ec9c7d95b1ad75f576cffc641853b750"), target);
        }

        [Fact]
        public void Bep44_Vector_WithSalt_TargetMatches()
        {
            var target = Bep44Record.ComputeTarget(VectorPubKey, Encoding.ASCII.GetBytes("foobar"));
            Assert.Equal(Hex("411eba73b6f087ca51a3795d9c8c938d365e32c1"), target);
        }

        [Fact]
        public void Bep44_Vector_NoSalt_PreimageIsExact()
        {
            // BEP 44: seq=1, value bencoded "12:Hello World!" -> the item value
            // passed to sign is the bencoded string. The signed preimage is
            // "3:seqi1e1:v12:Hello World!".
            var value = Encoding.ASCII.GetBytes("Hello World!"); // raw v; the preimage bencodes it as a string
            var preimage = Bep44Record.BuildSignaturePreimage(value, 1);
            Assert.Equal(Encoding.ASCII.GetBytes("3:seqi1e1:v12:Hello World!"), preimage);
        }

        [Fact]
        public void Bep44_Vector_WithSalt_PreimageIsExact()
        {
            var value = Encoding.ASCII.GetBytes("Hello World!"); // raw v; the preimage bencodes it as a string
            var preimage = Bep44Record.BuildSignaturePreimage(value, 1, Encoding.ASCII.GetBytes("foobar"));
            Assert.Equal(Encoding.ASCII.GetBytes("4:salt6:foobar3:seqi1e1:v12:Hello World!"), preimage);
        }

        [Fact]
        public void Bep44_Vector_PublishedSignatureVerifies()
        {
            // The spec's published signature for the no-salt vector must verify
            // against the vector public key over our preimage: proves the
            // preimage bytes are exactly what real DHT nodes validate.
            var value = Encoding.ASCII.GetBytes("Hello World!"); // raw v; the preimage bencodes it as a string
            var sig = Hex("305ac8aeb6c9c151fa120f120ea2cfb923564e11552d06a5d856091e5e853cff1260d3f39e4999684aa92eb73ffd136e6f4f3ecbfda0ce53a1608ecd7ae21f01");
            Assert.True(Bep44Record.Verify(VectorPubKey, value, 1, sig));
        }

        [Fact]
        public void Bep44_Vector_WithSalt_PublishedSignatureVerifies()
        {
            var value = Encoding.ASCII.GetBytes("Hello World!"); // raw v; the preimage bencodes it as a string
            var sig = Hex("6834284b6b24c3204eb2fea824d82f88883a3d95e8b4a21b8c0ded553d17d17ddf9a8a7104b1258f30bed3787e6cb896fca78c58f8e03b5f18f14951a87d9a08");
            Assert.True(Bep44Record.Verify(VectorPubKey, value, 1, sig, Encoding.ASCII.GetBytes("foobar")));
        }

        [Fact]
        public void Bep44_SignRoundTrips_AndTamperFails()
        {
            var id = PeerIdentity.Generate();
            var priv = id.ExportPrivateKey();
            var value = Bencode.Encode(new byte[] { 1, 2, 3, 4 });
            var sig = Bep44Record.Sign(priv, value, 7, Encoding.ASCII.GetBytes("slot"));
            Assert.True(Bep44Record.Verify(id.PublicKey, value, 7, sig, Encoding.ASCII.GetBytes("slot")));
            // Wrong seq, wrong salt, tampered value all fail.
            Assert.False(Bep44Record.Verify(id.PublicKey, value, 8, sig, Encoding.ASCII.GetBytes("slot")));
            Assert.False(Bep44Record.Verify(id.PublicKey, value, 7, sig, Encoding.ASCII.GetBytes("SLOT")));
            value[0] ^= 0xFF;
            Assert.False(Bep44Record.Verify(id.PublicKey, value, 7, sig, Encoding.ASCII.GetBytes("slot")));
        }

        // ── code rendezvous: first-contact signalling (#294) ──

        [Fact]
        public void CodeRendezvous_BothSidesDeriveTheIdenticalSlotFromTheCode()
        {
            // The host and the caller share only the code, so every DHT
            // coordinate must fall out of it deterministically. Grouping dashes
            // and confusable glyphs in a retyped code must not move the slot.
            var code = LinkCode.EncodeSelfContained(
                new IPEndPoint(IPAddress.Parse("203.0.113.7"), 27500), null,
                new byte[32], DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));

            var host = CodeRendezvous.DeriveSlot(code);
            var caller = CodeRendezvous.DeriveSlot(code.ToLowerInvariant().Replace("-", " "));
            Assert.NotNull(host);
            Assert.Equal(host.Target, caller.Target);
            Assert.Equal(host.PublicKey, caller.PublicKey);
            Assert.Equal(host.AeadKey, caller.AeadKey);
            Assert.Equal(20, host.Target.Length);
            // The signer must actually match the published key (BEP 44 nodes
            // verify sig against k, and k must hash to the target).
            Assert.Equal(PeerCrypto.DeriveEd25519PublicKey(host.PrivateKey), host.PublicKey);
        }

        [Fact]
        public void CodeRendezvous_DifferentCodes_LandOnDifferentSlots()
        {
            var a = CodeRendezvous.DeriveSlot(LinkCode.MintShortCode());
            var b = CodeRendezvous.DeriveSlot(LinkCode.MintShortCode());
            Assert.NotEqual(a.Target, b.Target);
            Assert.NotEqual(a.AeadKey, b.AeadKey);
        }

        [Fact]
        public void CodeRendezvous_CallRequest_RoundTripsThroughTheSlot()
        {
            // This is the payload that breaks the internet deadlock: the host
            // cannot answer probes its router drops, so the caller has to
            // publish where it can be reached.
            var code = LinkCode.MintShortCode();
            var slot = CodeRendezvous.DeriveSlot(code);
            var callerFp = new byte[32]; for (int i = 0; i < 32; i++) callerFp[i] = (byte)(i + 5);
            var eps = new[]
            {
                new IPEndPoint(IPAddress.Parse("174.235.248.62"), 63332), // CGNAT public
                new IPEndPoint(IPAddress.Parse("10.19.90.40"), 27500),    // LAN
            };
            var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            long seq = CodeRendezvous.SequenceFor(now);

            var value = CodeRendezvous.EncodeRequest(slot, callerFp, eps, now, seq);
            Assert.True(value.Length <= Bep44Record.MaxValueBytes);
            Assert.True(CodeRendezvous.TryDecodeRequest(slot, value, seq, out var call));
            Assert.Equal(callerFp.AsSpan(0, 8).ToArray(), call.CallerFingerprintPrefix);
            Assert.Equal(eps, call.Candidates);
            Assert.Equal(now.ToUnixTimeSeconds(), call.IssuedAt.ToUnixTimeSeconds());
            Assert.True(call.IsFresh(now.AddSeconds(30), TimeSpan.FromMinutes(3)));
            Assert.False(call.IsFresh(now.AddMinutes(10), TimeSpan.FromMinutes(3))); // stale ignored
        }

        [Fact]
        public void CodeRendezvous_AnotherCode_CannotReadTheRequest()
        {
            // The record is encrypted under the code, so DHT nodes (and anyone
            // without the invitation) never see the endpoints.
            var slot = CodeRendezvous.DeriveSlot(LinkCode.MintShortCode());
            var other = CodeRendezvous.DeriveSlot(LinkCode.MintShortCode());
            var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            long seq = CodeRendezvous.SequenceFor(now);
            var value = CodeRendezvous.EncodeRequest(slot, new byte[32],
                new[] { new IPEndPoint(IPAddress.Loopback, 1) }, now, seq);

            Assert.False(CodeRendezvous.TryDecodeRequest(other, value, seq, out _));
            Assert.False(CodeRendezvous.TryDecodeRequest(slot, value, seq + 1, out _)); // seq bound
            value[value.Length - 1] ^= 0xFF;
            Assert.False(CodeRendezvous.TryDecodeRequest(slot, value, seq, out _));     // tamper
        }

        [Fact]
        public void CodeRendezvous_RequestIsSignableAndVerifiableAtTheSlot()
        {
            // Real DHT nodes verify the BEP 44 signature against the published
            // key and reject a mismatch, so the slot's derived keypair must sign
            // its own value correctly.
            var slot = CodeRendezvous.DeriveSlot(LinkCode.MintShortCode());
            var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            long seq = CodeRendezvous.SequenceFor(now);
            var value = CodeRendezvous.EncodeRequest(slot, new byte[32],
                new[] { new IPEndPoint(IPAddress.Parse("203.0.113.1"), 5000) }, now, seq);

            var sig = Bep44Record.Sign(slot.PrivateKey, value, seq, slot.Salt);
            Assert.True(Bep44Record.Verify(slot.PublicKey, value, seq, sig, slot.Salt));
            Assert.Equal(Bep44Record.ComputeTarget(slot.PublicKey, slot.Salt), slot.Target);
        }

        // ── pairwise-capability presence crypto ──

        private static PresenceRecord.Presence SamplePresence() => new()
        {
            Candidates = new[]
            {
                new PresenceRecord.Candidate { Kind = PresenceRecord.Candidate.KindPublicV4, Endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 55000) },
                new PresenceRecord.Candidate { Kind = PresenceRecord.Candidate.KindPrivateV4, Endpoint = new IPEndPoint(IPAddress.Parse("192.168.1.9"), 55000) },
                new PresenceRecord.Candidate { Kind = PresenceRecord.Candidate.KindV6, Endpoint = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 55000) },
            },
            IssuedAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            Expiry = DateTimeOffset.FromUnixTimeSeconds(1_800_003_600),
        };

        [Fact]
        public void Presence_EncryptsAndDecryptsUnderTheSharedCapability()
        {
            var cap = new byte[32]; for (int i = 0; i < 32; i++) cap[i] = (byte)(i + 1);
            var pub = PeerIdentity.Generate().PublicKey;
            var p = SamplePresence();

            var value = PresenceRecord.EncodeValue(p, cap, PresenceRecord.DirectionA, pub, seq: 5);
            Assert.True(value.Length <= Bep44Record.MaxValueBytes);
            Assert.True(PresenceRecord.TryDecodeValue(value, cap, PresenceRecord.DirectionA, pub, 5, out var got));
            Assert.Equal(3, got.Candidates.Count);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 55000), got.Candidates[0].Endpoint);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 55000), got.Candidates[2].Endpoint);
            Assert.Equal(p.Expiry.ToUnixTimeSeconds(), got.Expiry.ToUnixTimeSeconds());
        }

        [Fact]
        public void Presence_WrongCapability_FailsToDecrypt()
        {
            var cap = new byte[32]; var wrong = new byte[32]; wrong[0] = 1;
            var pub = PeerIdentity.Generate().PublicKey;
            var value = PresenceRecord.EncodeValue(SamplePresence(), cap, PresenceRecord.DirectionA, pub, 1);
            Assert.False(PresenceRecord.TryDecodeValue(value, wrong, PresenceRecord.DirectionA, pub, 1, out _));
        }

        [Fact]
        public void Presence_ReplayIntoWrongSeqOrDirection_Fails()
        {
            var cap = new byte[32];
            var pub = PeerIdentity.Generate().PublicKey;
            var value = PresenceRecord.EncodeValue(SamplePresence(), cap, PresenceRecord.DirectionA, pub, 5);
            // Same value, different seq or direction: the AAD binding rejects it.
            Assert.False(PresenceRecord.TryDecodeValue(value, cap, PresenceRecord.DirectionA, pub, 6, out _));
            Assert.False(PresenceRecord.TryDecodeValue(value, cap, PresenceRecord.DirectionB, pub, 5, out _));
        }

        [Fact]
        public void Presence_TargetIsPerDirection_AndStableForACapability()
        {
            var cap = new byte[32]; for (int i = 0; i < 32; i++) cap[i] = 9;
            var pub = PeerIdentity.Generate().PublicKey;
            var tA = PresenceRecord.Target(pub, cap, PresenceRecord.DirectionA);
            var tB = PresenceRecord.Target(pub, cap, PresenceRecord.DirectionB);
            Assert.Equal(20, tA.Length);
            Assert.NotEqual(tA, tB); // the two publishing slots never collide
            Assert.Equal(tA, PresenceRecord.Target(pub, cap, PresenceRecord.DirectionA)); // deterministic
        }

        [Fact]
        public void Presence_CapabilityAndDirection_AgreeBetweenPeers()
        {
            // Both peers derive the SAME capability from the same pairing
            // secret + transcript, and pick COMPLEMENTARY directions.
            var secret = new byte[32]; for (int i = 0; i < 32; i++) secret[i] = (byte)(i * 3);
            var transcript = new byte[32]; for (int i = 0; i < 32; i++) transcript[i] = (byte)(i + 100);
            var capA = PresenceRecord.DeriveCapability(secret, transcript);
            var capB = PresenceRecord.DeriveCapability(secret, transcript);
            Assert.Equal(capA, capB);

            var fpA = new byte[] { 1, 2, 3 };
            var fpB = new byte[] { 9, 9, 9 };
            Assert.Equal(PresenceRecord.DirectionA, PresenceRecord.DirectionFor(fpA, fpB));
            Assert.Equal(PresenceRecord.DirectionB, PresenceRecord.DirectionFor(fpB, fpA));
            // The peer reads the OTHER side's slot: A publishes DirectionA, and
            // B looks up DirectionA for A's presence. Complementary, no collision.
        }
    }
}
