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
        public async Task Relay_Handshake_CancelledReturnsNull()
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
