using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.RemoteLink.Dht;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #294 relay fallback lane: when no direct path can be punched (both
    /// peers behind CGNAT/symmetric NAT), the UNMODIFIED handshake runs over
    /// an iroh relay instead. These tests prove the composition in-process:
    /// the handshake over relay-shaped transports, the rendezvous record's
    /// relay tail, and the BLAKE3 derive_key the live relay authenticates.
    /// The live residuals (websocket transport, the n0 relay itself) were
    /// verified against use1-1.relay.n0.iroh.link before the lane shipped.
    /// </summary>
    public class RelayLaneTests
    {
        private static readonly byte[] Caps = { 1, 0 };

        /// <summary>Two cross-wired in-memory datagram transports, the shape
        /// the relay lane presents: no endpoints, just a pipe that delivers
        /// whole datagrams to the other side's handler.</summary>
        private sealed class MemPipe : IDatagramTransport
        {
            public MemPipe Peer;
            public Action<byte[]> OnDatagram { get; set; }
            public Task SendAsync(byte[] datagram, CancellationToken ct)
            {
                var peer = Peer;
                _ = Task.Run(() => peer?.OnDatagram?.Invoke(datagram), ct);
                return Task.CompletedTask;
            }
            public static (MemPipe a, MemPipe b) CreatePair()
            {
                var a = new MemPipe(); var b = new MemPipe();
                a.Peer = b; b.Peer = a;
                return (a, b);
            }
        }

        private static RemotePeerDeviceInfo PadInfo() => new()
        {
            PeerLocalDeviceId = "relaypad", Name = "Relay Pad",
            VendorId = 0x054C, ProductId = 0x0CE6,
            NumAxes = 6, NumButtons = 17, NumHats = 1,
            HasRumble = true, InputDeviceType = InputDeviceType.Gamepad,
        };

        [Fact]
        public async Task Relay_Handshake_EstablishesWithMatchingDataKeys()
        {
            var (ta, tb) = MemPipe.CreatePair();
            var idA = PeerIdentity.Generate();
            var idB = PeerIdentity.Generate();
            var trustA = new PeerTrustStore();
            var trustB = new PeerTrustStore();
            var nonce = new byte[16]; for (int i = 0; i < 16; i++) nonce[i] = (byte)(i + 7);
            Func<PendingPairing, PairingApproval> approve = _ => true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var taskA = PunchedConnection.ConnectRelayAsync(
                ta, nonce, isInitiator: true,
                idA, trustA, Array.Empty<RemotePeerDeviceInfo>(), Caps, approve, "t", cts.Token);
            var taskB = PunchedConnection.ConnectRelayAsync(
                tb, nonce, isInitiator: false,
                idB, trustB, new[] { PadInfo() }, Caps, approve, "t", cts.Token);

            var rA = await taskA;
            var rB = await taskB;

            Assert.NotNull(rA);
            Assert.NotNull(rB);
            Assert.True(trustA.IsTrusted(idB.PublicKey));
            Assert.True(trustB.IsTrusted(idA.PublicKey));
            Assert.Single(rA.RemoteDevices);
            Assert.StartsWith("Relay Pad", rA.RemoteDevices[0].Name);

            // The negotiated data keys interoperate: a frame sealed by B's
            // session opens in A's, exactly as on the punched path.
            var sA = new LinkSession(rA.DataKey, rA.IsInitiator);
            var sB = new LinkSession(rB.DataKey, rB.IsInitiator);
            var state = CustomInputStateCodec.CreateNeutral();
            state.Buttons[1] = true;
            var dg = sB.Seal(LinkMessageType.Input, 0, 1,
                CustomInputStateCodec.Encode(state, new CustomInputStateCodec.Caps(false, false)));
            Assert.True(sA.Open(dg, out _, out _, out _, out var payload));
            rA.RemoteDevices[0].ApplyFramePayload(payload);
            Assert.True(rA.RemoteDevices[0].GetCurrentState().Buttons[1]);
        }

        [Fact]
        public async Task Relay_Handshake_CanceledReturnsNull()
        {
            // A relay lane that never hears from the peer must surface a clean
            // null (the caller then reports the failure), not an exception.
            var (ta, _) = MemPipe.CreatePair();
            var nonce = new byte[16];
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var r = await PunchedConnection.ConnectRelayAsync(
                ta, nonce, isInitiator: true,
                PeerIdentity.Generate(), new PeerTrustStore(), Array.Empty<RemotePeerDeviceInfo>(),
                Caps, _ => true, "t", cts.Token);
            Assert.Null(r);
        }

        [Fact]
        public void Rendezvous_RelayTail_RoundTrips()
        {
            var slot = CodeRendezvous.DeriveSlot("PF1-AAAA-BBBB-CCCC");
            var fp = new byte[32]; for (int i = 0; i < 32; i++) fp[i] = (byte)i;
            var relayKey = new byte[32]; for (int i = 0; i < 32; i++) relayKey[i] = (byte)(0x80 + i);
            var eps = new[] { new IPEndPoint(IPAddress.Parse("198.51.100.7"), 27500) };
            var now = DateTimeOffset.FromUnixTimeSeconds(1_754_900_000);
            long seq = CodeRendezvous.SequenceFor(now);

            var value = CodeRendezvous.EncodeRequest(slot, fp, eps, now, seq,
                relayKey, "use1-1.relay.n0.iroh.link");
            Assert.True(CodeRendezvous.TryDecodeRequest(slot, value, seq, out var call));
            Assert.Equal(relayKey, call.RelayKey);
            Assert.Equal("use1-1.relay.n0.iroh.link", call.RelayHost);
            Assert.Single(call.Candidates);
            Assert.Equal(eps[0], call.Candidates[0]);
        }

        [Fact]
        public void Rendezvous_WithoutRelayTail_DecodesWithNullRelay()
        {
            // Records from builds that predate the relay lane carry no tail;
            // the decoder must yield null relay fields, not fail.
            var slot = CodeRendezvous.DeriveSlot("PF1-DDDD-EEEE-FFFF");
            var fp = new byte[32];
            var eps = new[] { new IPEndPoint(IPAddress.Parse("203.0.113.9"), 27500) };
            var now = DateTimeOffset.FromUnixTimeSeconds(1_754_900_100);
            long seq = CodeRendezvous.SequenceFor(now);

            var value = CodeRendezvous.EncodeRequest(slot, fp, eps, now, seq);
            Assert.True(CodeRendezvous.TryDecodeRequest(slot, value, seq, out var call));
            Assert.Null(call.RelayKey);
            Assert.Null(call.RelayHost);
        }

        [Fact]
        public void CodeRelay_BothSidesDeriveTheSameRendezvous_FromTheCodeAlone()
        {
            // THE reliability fix (#294). Carrying the caller's relay key over
            // the DHT meant the host had to FIND that record, and two machines
            // on different ISPs query different DHT regions and need not
            // converge. Deriving the rendezvous from the shared code removes
            // the lookup entirely: same code in, same relay identity, host, and
            // channel out, on both machines, every time.
            const string code = "PF1-TEST-CODE-9999";
            var host = CodeRendezvous.DeriveRelay(code);
            var caller = CodeRendezvous.DeriveRelay(code);

            Assert.NotNull(host);
            Assert.Equal(host.PublicKey, caller.PublicKey);
            Assert.Equal(host.Host, caller.Host);
            Assert.Equal(host.Channel, caller.Channel);
            Assert.Equal(32, host.PublicKey.Length);
            Assert.Contains(host.Host, IrohRelayClient.DefaultRelays);
            // The public key really is the one the private seed authenticates
            // as, so the host can LISTEN at the address callers compute.
            Assert.Equal(host.PublicKey, PeerCrypto.DeriveEd25519PublicKey(host.PrivateKey));
        }

        [Fact]
        public void CodeRelay_NormalizesLikeTheDhtSlot()
        {
            // A retyped code with grouping dashes and different case must land
            // on the identical rendezvous, exactly as DeriveSlot promises.
            var a = CodeRendezvous.DeriveRelay("pf1testcode9999");
            var b = CodeRendezvous.DeriveRelay("PF1-TEST-CODE-9999");
            Assert.Equal(a.PublicKey, b.PublicKey);
            Assert.Equal(a.Channel, b.Channel);
            Assert.Equal(a.Host, b.Host);
        }

        [Fact]
        public void CodeRelay_DifferentCodes_GetDifferentRendezvous()
        {
            var a = CodeRendezvous.DeriveRelay("PF1-AAAA-AAAA-AAAA");
            var b = CodeRendezvous.DeriveRelay("PF1-BBBB-BBBB-BBBB");
            Assert.NotEqual(Convert.ToHexString(a.PublicKey), Convert.ToHexString(b.PublicKey));
            Assert.NotEqual(a.Channel, b.Channel);
        }

        [Fact]
        public void CodeRelay_SeededClient_AuthenticatesAsTheDerivedIdentity()
        {
            // The host's relay client must present the code-derived key, or
            // callers would address an identity nobody is listening on.
            var rdv = CodeRendezvous.DeriveRelay("PF1-SEED-CHEK-0001");
            using var client = new IrohRelayClient(rdv.PrivateKey);
            Assert.Equal(rdv.PublicKey, client.PublicKey);
        }

        [Fact]
        public void PathOffer_CandidatesRoundTrip()
        {
            // The relay-to-direct upgrade (#294) ships the sender's candidate
            // endpoints over the relayed session. A malformed or truncated
            // list must decode to nothing rather than throw on the receive
            // path, which runs on the datagram loop.
            var eps = new[]
            {
                new IPEndPoint(IPAddress.Parse("203.0.113.7"), 27500),
                new IPEndPoint(IPAddress.Parse("192.168.1.44"), 61000),
            };
            var encoded = LinkServer.EncodeCandidates(eps);
            var back = LinkServer.DecodeCandidates(encoded);
            Assert.Equal(2, back.Count);
            Assert.Equal(eps[0], back[0]);
            Assert.Equal(eps[1], back[1]);

            Assert.Empty(LinkServer.DecodeCandidates(Array.Empty<byte>()));
            Assert.Empty(LinkServer.DecodeCandidates(null));
            // Truncated mid-candidate: decode what is whole, never throw.
            var truncated = encoded.AsSpan(0, encoded.Length - 3).ToArray();
            var partial = LinkServer.DecodeCandidates(truncated);
            Assert.Single(partial);
        }

        [Fact]
        public void PathOffer_IsDistinctFromEveryOtherMessageType()
        {
            // PathOffer was appended as type 8. The first byte of a sealed
            // frame is (type<<4)|epoch, and the relay/punch lanes claim
            // 0xC0-0xC5, so type 8 (0x8_) must not collide with them.
            Assert.Equal(8, (byte)LinkMessageType.PathOffer);
            for (int epoch = 0; epoch < 16; epoch++)
            {
                byte first = (byte)(((byte)LinkMessageType.PathOffer << 4) | epoch);
                Assert.InRange(first, (byte)0x80, (byte)0x8F);
                Assert.True(first < 0xC0 || first > 0xC5);
            }
        }

        [Fact]
        public void IdentityRelay_IsStableAcrossRestarts_UnlikeTheCodeRelay()
        {
            // OWNER REPORT 2026-08-12: closing and relaunching never
            // reconnected a paired peer over the internet. The code-derived
            // rendezvous cannot serve reconnect, because relaunching mints a
            // new code (new endpoint, new expiry) and the derived identity
            // moves with it. The identity-derived one never moves.
            var key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)(i * 7 + 1);

            var first = CodeRendezvous.DeriveIdentityRelay(key);
            var afterRestart = CodeRendezvous.DeriveIdentityRelay(key);
            Assert.NotNull(first);
            Assert.Equal(first.PublicKey, afterRestart.PublicKey);
            Assert.Equal(first.Host, afterRestart.Host);
            Assert.Equal(first.PublicKey, PeerCrypto.DeriveEd25519PublicKey(first.PrivateKey));
            Assert.Contains(first.Host, IrohRelayClient.DefaultRelays);

            // Two different codes for the SAME machine give two different
            // rendezvous, which is precisely why the code lane cannot reconnect.
            var codeA = CodeRendezvous.DeriveRelay("PF1-AAAA-1111-2222");
            var codeB = CodeRendezvous.DeriveRelay("PF1-AAAA-3333-4444");
            Assert.NotEqual(Convert.ToHexString(codeA.PublicKey), Convert.ToHexString(codeB.PublicKey));

            // A different machine is a different address.
            var other = new byte[32];
            for (int i = 0; i < 32; i++) other[i] = (byte)(i * 11 + 3);
            Assert.NotEqual(Convert.ToHexString(first.PublicKey),
                            Convert.ToHexString(CodeRendezvous.DeriveIdentityRelay(other).PublicKey));
        }

        [Fact]
        public void CapabilityChannel_IsPerPair_AndStable()
        {
            // One listening identity serves every paired peer, so each pair
            // needs its own control channel or two peers reconnecting at once
            // would collide on the same ARQ stream.
            var capA = new byte[32]; var capB = new byte[32];
            for (int i = 0; i < 32; i++) { capA[i] = (byte)i; capB[i] = (byte)(i + 100); }

            Assert.Equal(CodeRendezvous.ChannelForCapability(capA),
                         CodeRendezvous.ChannelForCapability(capA));
            Assert.NotEqual(CodeRendezvous.ChannelForCapability(capA),
                            CodeRendezvous.ChannelForCapability(capB));
            Assert.Equal(0u, CodeRendezvous.ChannelForCapability(null));
        }

        [Fact]
        public void Blake3DeriveKey_MatchesOfficialVector()
        {
            // Official BLAKE3 test vector (BLAKE3-team/BLAKE3
            // test_vectors.json): derive_key with the reference context over
            // empty input. The same function feeds the relay handshake
            // signature, which the production relay verified live.
            var got = IrohRelayClient.Blake3DeriveKey(
                "BLAKE3 2019-12-27 16:29:52 test vectors context", Array.Empty<byte>());
            Assert.Equal(
                "2cc39783c223154fea8dfb7c1b1660f2ac2dcbd1c1de8277b0b0dd39b7e50d7d",
                Convert.ToHexString(got).ToLowerInvariant());
        }
    }
}
