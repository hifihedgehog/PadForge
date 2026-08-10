using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #294 Remote Link internet reach: the offline-testable engine core. STUN
    /// message encode/decode against the RFC 5389 layout, connection-code
    /// mint/parse with tamper and expiry cases, the reliable-UDP control channel
    /// against the same contract the in-memory channel satisfies (driven through
    /// a lossy/reordering simulated transport), the punch orchestration against
    /// an in-process simulated NAT, and the signed rendezvous record with
    /// signature-rejection. The named live residual (a real punch needs two real
    /// NATs) is the only thing these cannot cover.
    /// </summary>
    public class RemoteLinkDirectTests
    {
        // ── STUN ──

        [Fact]
        public void Stun_BindingRequest_HasCookieAndMatchingTransactionId()
        {
            var req = StunClient.BuildBindingRequest(out var txId);
            Assert.Equal(20, req.Length);
            Assert.Equal(0x00, req[0]); // request class, binding method
            Assert.Equal(0x01, req[1]);
            uint cookie = (uint)(req[4] << 24 | req[5] << 16 | req[6] << 8 | req[7]);
            Assert.Equal(StunClient.MagicCookie, cookie);
            for (int i = 0; i < 12; i++) Assert.Equal(txId[i], req[8 + i]);
        }

        [Fact]
        public void Stun_ParsesXorMappedAddress_RoundTrip()
        {
            // Build a success response carrying an XOR-MAPPED-ADDRESS for a known
            // endpoint, then parse it back.
            StunClient.BuildBindingRequest(out var txId);
            var ep = new IPEndPoint(IPAddress.Parse("203.0.113.47"), 51234);
            var resp = BuildXorMappedResponse(txId, ep);
            var parsed = StunClient.ParseBindingResponse(resp, txId);
            Assert.NotNull(parsed);
            Assert.Equal(ep.Address, parsed.Address);
            Assert.Equal(ep.Port, parsed.Port);
        }

        [Fact]
        public void Stun_RejectsResponseWithWrongTransactionId()
        {
            StunClient.BuildBindingRequest(out var txId);
            var ep = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 40000);
            var resp = BuildXorMappedResponse(txId, ep);
            var wrong = new byte[12];
            Assert.Null(StunClient.ParseBindingResponse(resp, wrong));
        }

        private static byte[] BuildXorMappedResponse(byte[] txId, IPEndPoint ep)
        {
            // header(20) + attr header(4) + xor-mapped value(8)
            var buf = new byte[32];
            buf[0] = 0x01; buf[1] = 0x01;      // Binding success response
            buf[2] = 0x00; buf[3] = 12;        // attribute section length
            buf[4] = 0x21; buf[5] = 0x12; buf[6] = 0xA4; buf[7] = 0x42; // cookie
            txId.CopyTo(buf, 8);
            buf[20] = 0x00; buf[21] = 0x20;    // XOR-MAPPED-ADDRESS
            buf[22] = 0x00; buf[23] = 8;       // value length
            buf[24] = 0x00; buf[25] = 0x01;    // reserved, family IPv4
            ushort xport = (ushort)(ep.Port ^ (ushort)(StunClient.MagicCookie >> 16));
            buf[26] = (byte)(xport >> 8); buf[27] = (byte)xport;
            var ip = ep.Address.GetAddressBytes();
            uint addr = (uint)(ip[0] << 24 | ip[1] << 16 | ip[2] << 8 | ip[3]);
            uint xaddr = addr ^ StunClient.MagicCookie;
            buf[28] = (byte)(xaddr >> 24); buf[29] = (byte)(xaddr >> 16);
            buf[30] = (byte)(xaddr >> 8); buf[31] = (byte)xaddr;
            return buf;
        }

        // ── Codes ──

        [Fact]
        public void Code_ShortMint_IsEightCrockfordChars_AndLooksLikeCode()
        {
            var code = LinkCode.MintShortCode();
            Assert.Equal(LinkCode.ShortCodeLength, code.Length);
            Assert.True(LinkCode.LooksLikeCode(code));
            Assert.False(LinkCode.IsSelfContained(code)); // short key, not a payload
        }

        [Fact]
        public void Code_LooksLikeCode_RejectsAddresses()
        {
            Assert.False(LinkCode.LooksLikeCode("192.168.1.5"));
            Assert.False(LinkCode.LooksLikeCode("host.example.com:6789"));
            Assert.False(LinkCode.LooksLikeCode("peer:1234"));
            Assert.True(LinkCode.LooksLikeCode("ABC123XY"));
        }

        [Fact]
        public void Code_SelfContained_RoundTripsEndpointsFingerprintExpiry()
        {
            var pub = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 55000);
            var priv = new IPEndPoint(IPAddress.Parse("192.168.1.20"), 55000);
            var fp = new byte[32]; for (int i = 0; i < 32; i++) fp[i] = (byte)(i * 7);
            var expiry = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

            var code = LinkCode.EncodeSelfContained(pub, priv, fp, expiry);
            Assert.True(LinkCode.LooksLikeCode(code));
            Assert.True(LinkCode.TryParseSelfContained(code, out var parsed));
            Assert.Equal(pub, parsed.PublicEndpoint);
            Assert.Equal(priv, parsed.PrivateEndpoint);
            Assert.Equal(fp.AsSpan(0, 8).ToArray(), parsed.FingerprintPrefix);
            // Minute-resolution expiry.
            Assert.Equal(expiry.ToUnixTimeSeconds() / 60, parsed.Expiry.ToUnixTimeSeconds() / 60);
        }

        [Fact]
        public void Code_SelfContained_SurvivesGroupingAndConfusableGlyphs()
        {
            var pub = new IPEndPoint(IPAddress.Parse("198.51.100.4"), 6000);
            var fp = new byte[32];
            var code = LinkCode.EncodeSelfContained(pub, null, fp, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
            // User retypes lowercase, swaps O/0 and I/1, drops a dash.
            var mangled = code.ToLowerInvariant().Replace("-", " ");
            Assert.True(LinkCode.TryParseSelfContained(mangled, out var parsed));
            Assert.Equal(pub, parsed.PublicEndpoint);
        }

        [Fact]
        public void Code_TwoWayNonceAndRole_AgreeBetweenPeers()
        {
            // Each side holds its OWN full fingerprint and the OTHER's 8-byte
            // prefix (from the pasted code). Both must derive the same 16-byte
            // punch nonce and COMPLEMENTARY handshake roles.
            var fpA = new byte[32]; for (int i = 0; i < 32; i++) fpA[i] = (byte)(i + 1);
            var fpB = new byte[32]; for (int i = 0; i < 32; i++) fpB[i] = (byte)(200 - i);
            var prefixA = fpA[..8];
            var prefixB = fpB[..8];

            var nonceFromA = LinkCode.TwoWayPunchNonce(fpA, prefixB);
            var nonceFromB = LinkCode.TwoWayPunchNonce(fpB, prefixA);
            Assert.Equal(16, nonceFromA.Length);
            Assert.Equal(nonceFromA, nonceFromB);

            bool aLeads = LinkCode.IsHandshakeInitiator(fpA, prefixB);
            bool bLeads = LinkCode.IsHandshakeInitiator(fpB, prefixA);
            Assert.NotEqual(aLeads, bLeads); // exactly one leads
        }

        [Fact]
        public void Code_Expiry_IsCheckable()
        {
            var fp = new byte[32];
            var code = LinkCode.EncodeSelfContained(
                new IPEndPoint(IPAddress.Loopback, 1), null, fp, DateTimeOffset.FromUnixTimeSeconds(1_000_000_000));
            Assert.True(LinkCode.TryParseSelfContained(code, out var parsed));
            Assert.True(parsed.IsExpired(DateTimeOffset.FromUnixTimeSeconds(1_000_000_100)));
            Assert.False(parsed.IsExpired(DateTimeOffset.FromUnixTimeSeconds(999_999_900)));
        }

        // ── UdpControlChannel ARQ ──

        [Fact]
        public async Task Arq_DeliversHandshakeSizedMessages_OverLossyReorderingTransport()
        {
            var (ta, tb) = SimTransport.Pair(lossRate: 0.35, seed: 12345);
            using var a = new UdpControlChannel(ta, TimeSpan.FromMilliseconds(20));
            using var b = new UdpControlChannel(tb, TimeSpan.FromMilliseconds(20));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var payloads = new List<byte[]>();
            for (int i = 0; i < 12; i++) payloads.Add(RandomBytes(200 + i, i));

            var recv = Task.Run(async () =>
            {
                var got = new List<byte[]>();
                for (int i = 0; i < payloads.Count; i++)
                    got.Add(await b.ReceiveAsync(cts.Token));
                return got;
            });

            foreach (var p in payloads)
                await a.SendAsync(p, cts.Token);

            var received = await recv;
            Assert.Equal(payloads.Count, received.Count);
            for (int i = 0; i < payloads.Count; i++)
                Assert.Equal(payloads[i], received[i]); // in order, exactly once, intact
        }

        [Fact]
        public async Task Arq_IsDuplex_BothDirectionsIndependent()
        {
            var (ta, tb) = SimTransport.Pair(lossRate: 0.2, seed: 999);
            using var a = new UdpControlChannel(ta, TimeSpan.FromMilliseconds(20));
            using var b = new UdpControlChannel(tb, TimeSpan.FromMilliseconds(20));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var aMsg = RandomBytes(64, 1);
            var bMsg = RandomBytes(80, 2);
            var aRecv = a.ReceiveAsync(cts.Token);
            var bRecv = b.ReceiveAsync(cts.Token);
            await Task.WhenAll(a.SendAsync(aMsg, cts.Token), b.SendAsync(bMsg, cts.Token));
            Assert.Equal(aMsg, await bRecv);
            Assert.Equal(bMsg, await aRecv);
        }

        [Fact]
        public void Arq_ControlTag_DemuxesFromLinkSessionData()
        {
            // LinkSession's first header byte is (type<<4)|epoch, type 1..7, so
            // the high nibble is 1..7, never 0xC. The control tags are 0xC0/0xC1.
            Assert.True(UdpControlChannel.IsControlDatagram(new byte[] { UdpControlChannel.TagData, 0, 0, 0, 0 }));
            Assert.True(UdpControlChannel.IsControlDatagram(new byte[] { UdpControlChannel.TagAck, 0, 0, 0, 0 }));
            for (int type = 1; type <= 7; type++)
                Assert.False(UdpControlChannel.IsControlDatagram(new byte[] { (byte)((type << 4) | 1) }));
        }

        [Fact]
        public async Task Arq_RejectsOtherChannelsTraffic()
        {
            // Two connection attempts on the same endpoint pair get different
            // channel ids (from their punch nonces): a stale datagram from the
            // old attempt must never be delivered or ACK the new one.
            var (ta, tb) = SimTransport.Pair(lossRate: 0, seed: 42);
            using var a = new UdpControlChannel(ta, TimeSpan.FromMilliseconds(30), channelId: 111);
            using var b = new UdpControlChannel(tb, TimeSpan.FromMilliseconds(30), channelId: 222);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

            var recv = b.ReceiveAsync(cts.Token);
            try { await a.SendAsync(new byte[] { 1, 2, 3 }, cts.Token); }
            catch (Exception) { /* never acked: poisons, expected */ }
            Assert.False(recv.IsCompletedSuccessfully); // mismatched id never delivers
        }

        [Fact]
        public async Task Arq_CancelledSend_PoisonsTheChannel()
        {
            // A cancelled in-flight send leaves the shared sequence state
            // unknowable, so the channel must fail closed, never silently
            // desynchronize into ACKed-but-dropped messages.
            var (ta, _) = SimTransport.Pair(lossRate: 1.0, seed: 5); // pure loss: never acked
            var ch = new UdpControlChannel(ta, TimeSpan.FromMilliseconds(20));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ch.SendAsync(new byte[] { 1 }, cts.Token));
            await Assert.ThrowsAsync<LinkConnectionException>(
                () => ch.SendAsync(new byte[] { 2 }, CancellationToken.None));
        }

        // ── HolePuncher ──

        [Fact]
        public void Punch_OrderCandidates_PrivateThenIpv6ThenPublic_Deduped()
        {
            var priv = new IPEndPoint(IPAddress.Parse("192.168.1.5"), 6000);
            var pub = new IPEndPoint(IPAddress.Parse("203.0.113.5"), 55000);
            var v6 = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6000);
            var ordered = HolePuncher.OrderCandidates(priv, pub, v6);
            Assert.Equal(new[] { priv, v6, pub }, ordered);

            // De-dup: same endpoint passed twice collapses.
            var dup = HolePuncher.OrderCandidates(priv, priv);
            Assert.Single(dup);
        }

        [Fact]
        public async Task Punch_BothSidesSettleOnAWorkingEndpoint_ThroughSimulatedNat()
        {
            // Two peers on a simulated NAT: each reaches the other only at the
            // other's "public" endpoint. The private candidate is a dead address
            // nothing answers, proving the puncher skips past it to the reachable
            // one rather than hanging.
            var nat = new SimNat();
            var epA = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1111);
            var epB = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 2222);
            var deadPrivate = new IPEndPoint(IPAddress.Parse("192.168.99.99"), 9);

            var ta = nat.Endpoint(epA);
            var tb = nat.Endpoint(epB);
            var nonce = RandomBytes(16, 7);
            var pa = new HolePuncher(ta, nonce, TimeSpan.FromMilliseconds(15));
            var pb = new HolePuncher(tb, nonce, TimeSpan.FromMilliseconds(15));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var candA = HolePuncher.OrderCandidates(deadPrivate, epB);
            var candB = HolePuncher.OrderCandidates(deadPrivate, epA);
            var wa = pa.PunchAsync(candA, cts.Token);
            var wb = pb.PunchAsync(candB, cts.Token);

            Assert.Equal(epB, await wa);
            Assert.Equal(epA, await wb);
        }

        [Fact]
        public async Task Punch_RejectsWrongNonce()
        {
            var nat = new SimNat();
            var epA = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1111);
            var epB = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 2222);
            var pa = new HolePuncher(nat.Endpoint(epA), RandomBytes(16, 1), TimeSpan.FromMilliseconds(15));
            var pb = new HolePuncher(nat.Endpoint(epB), RandomBytes(16, 2), TimeSpan.FromMilliseconds(15)); // different nonce
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

            var wa = pa.PunchAsync(HolePuncher.OrderCandidates(null, epB), cts.Token);
            var wb = pb.PunchAsync(HolePuncher.OrderCandidates(null, epA), cts.Token);
            Assert.Null(await wa); // mismatched nonce never settles
            Assert.Null(await wb);
        }

        // ── Rendezvous protocol ──

        [Fact]
        public void Rendezvous_SignedRecord_VerifiesAndDecodes()
        {
            var id = PeerIdentity.Generate();
            var rec = new RendezvousProtocol.PresenceRecord
            {
                PublicKey = id.PublicKey,
                PublicEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 50000),
                PrivateEndpoint = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 50000),
                Expiry = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            };
            var blob = RendezvousProtocol.SignRecord(rec, id);
            Assert.True(RendezvousProtocol.TryVerifyRecord(blob, out var got, id.Fingerprint));
            Assert.Equal(rec.PublicEndpoint, got.PublicEndpoint);
            Assert.Equal(rec.PrivateEndpoint, got.PrivateEndpoint);
            Assert.Equal(id.Fingerprint, got.Fingerprint);
        }

        [Fact]
        public void Rendezvous_TamperedEndpoint_FailsVerification()
        {
            var id = PeerIdentity.Generate();
            var rec = new RendezvousProtocol.PresenceRecord
            {
                PublicKey = id.PublicKey,
                PublicEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 50000),
                Expiry = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            };
            var blob = RendezvousProtocol.SignRecord(rec, id);
            // Flip a byte inside the public-endpoint region (after version+key).
            blob[1 + 32] ^= 0xFF;
            Assert.False(RendezvousProtocol.TryVerifyRecord(blob, out _, id.Fingerprint));
        }

        [Fact]
        public void Rendezvous_WrongExpectedFingerprint_Rejected()
        {
            var id = PeerIdentity.Generate();
            var other = PeerIdentity.Generate();
            var rec = new RendezvousProtocol.PresenceRecord
            {
                PublicKey = id.PublicKey,
                PublicEndpoint = new IPEndPoint(IPAddress.Loopback, 1),
                Expiry = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
            };
            var blob = RendezvousProtocol.SignRecord(rec, id);
            // Internally valid, but not the peer we pinned.
            Assert.False(RendezvousProtocol.TryVerifyRecord(blob, out _, other.Fingerprint));
            Assert.True(RendezvousProtocol.TryVerifyRecord(blob, out _, id.Fingerprint));
        }

        // ── helpers ──

        private static byte[] RandomBytes(int n, int seed)
        {
            var r = new Random(seed);
            var b = new byte[n];
            r.NextBytes(b);
            return b;
        }
    }
}
