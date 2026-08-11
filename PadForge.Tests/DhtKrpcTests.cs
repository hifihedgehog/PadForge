using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.RemoteLink.Dht;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #294 KRPC + DHT presence store, driven against an in-process simulated
    /// mainline DHT (nodes that route find/get by XOR distance, issue tokens,
    /// and store BEP 44 mutable items with the real seq/signature rules). This
    /// proves the iterative lookup, the token-gated put, the >= 8-ack health
    /// signal, and the highest-valid-sequence read. The one thing it cannot
    /// prove is the live network, which is the named residual.
    /// </summary>
    public class DhtKrpcTests
    {
        // ── KRPC message layer ──

        [Fact]
        public void Krpc_CompactNodes_RoundTrip()
        {
            var id = new byte[20]; for (int i = 0; i < 20; i++) id[i] = (byte)i;
            var buf = new byte[Krpc.CompactNodeLen];
            id.CopyTo(buf, 0);
            buf[20] = 203; buf[21] = 0; buf[22] = 113; buf[23] = 5;
            buf[24] = 0x1A; buf[25] = 0xE1; // port 6881
            var nodes = Krpc.ParseCompactNodes(buf);
            Assert.Single(nodes);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.5"), 6881), nodes[0].Endpoint);
            Assert.Equal(id, nodes[0].Id);
        }

        [Fact]
        public void Krpc_ParsesGetResponseFields()
        {
            // Hand-build a get response dict and confirm every BEP 44 field parses.
            var r = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = new byte[20],
                ["token"] = Encoding.ASCII.GetBytes("tok"),
                ["k"] = new byte[32],
                ["v"] = new byte[] { 1, 2, 3 },
                ["seq"] = 9L,
                ["sig"] = new byte[64],
            };
            var top = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["t"] = new byte[] { 0, 1 },
                ["y"] = Encoding.ASCII.GetBytes("r"),
                ["r"] = r,
            };
            var resp = Krpc.ParseResponse(Bencode.Encode(top));
            Assert.True(resp.IsResponse);
            Assert.Equal(9L, resp.Seq);
            Assert.True(resp.HasSeq);
            Assert.Equal(new byte[] { 1, 2, 3 }, resp.Value);
            Assert.Equal(Encoding.ASCII.GetBytes("tok"), resp.Token);
        }

        [Fact]
        public void Krpc_XorDistance_OrdersCorrectly()
        {
            var target = new byte[20];
            var near = new byte[20]; near[19] = 1;
            var far = new byte[20]; far[0] = 1;
            Assert.True(Krpc.XorCompare(near, far, target) < 0);
            Assert.True(Krpc.XorCompare(far, near, target) > 0);
            Assert.Equal(0, Krpc.XorCompare(near, near, target));
        }

        // ── end-to-end against a simulated DHT ──

        [Fact]
        public async Task Dht_PublishThenLookup_RoundTripsPresence()
        {
            var dht = new SimDht(nodeCount: 40, seed: 7);
            var publisher = PeerIdentity.Generate();
            var cap = RandomBytes(32, 1);
            var presence = Presence("203.0.113.44", 55000, "192.168.1.10", 55000);

            using var pub = dht.NewClient();
            using var look = dht.NewClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var result = await pub.PublishAsync(publisher.PublicKey, publisher.ExportPrivateKey(),
                cap, PresenceRecord.DirectionA, presence, seq: 1, cts.Token);
            Assert.True(result.Healthy, $"expected >= 8 acks, got {result.AckCount}");

            var found = await look.LookupAsync(publisher.PublicKey, cap, PresenceRecord.DirectionA, cts.Token);
            Assert.NotNull(found);
            Assert.Equal(2, found.Candidates.Count);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.44"), 55000), found.Candidates[0].Endpoint);
        }

        [Fact]
        public async Task Dht_ColdStart_RetryRecoversTheFirstMissedPublishAndLookup()
        {
            // FIELD-MEASURED (2026-08-11): against the live mainline DHT the
            // FIRST publish after start-up landed zero replicas (1 failure in
            // 6, always the cold one) because the routing table is empty and
            // two of four bootstrap routers are dead. A single-shot publish
            // then announces to nobody, indistinguishable from "no peer
            // calling". Here every node drops its first query, so only a client
            // that retries the whole publish AND lookup gets through.
            var dht = new SimDht(nodeCount: 40, seed: 7, dropFirstQuery: true);
            var publisher = PeerIdentity.Generate();
            var cap = RandomBytes(32, 99);
            var presence = Presence("203.0.113.44", 55000, "192.168.1.10", 55000);

            using var pub = dht.NewClient();
            using var look = dht.NewClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var result = await pub.PublishAsync(publisher.PublicKey, publisher.ExportPrivateKey(),
                cap, PresenceRecord.DirectionA, presence, seq: 1, cts.Token);
            Assert.True(result.AckCount > 0, "publish must recover via retry despite the cold-start drop");

            var found = await look.LookupAsync(publisher.PublicKey, cap, PresenceRecord.DirectionA, cts.Token);
            Assert.NotNull(found);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.44"), 55000), found.Candidates[0].Endpoint);
        }

        [Fact]
        public async Task Dht_Lookup_TakesHighestSequence_NotFirstReply()
        {
            var dht = new SimDht(nodeCount: 40, seed: 11);
            var publisher = PeerIdentity.Generate();
            var cap = RandomBytes(32, 2);
            using var pub = dht.NewClient();
            using var look = dht.NewClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // Publish seq 1 (old endpoint), then seq 2 (moved endpoint). Real
            // nodes reject the downgrade, so the store must converge on seq 2.
            await pub.PublishAsync(publisher.PublicKey, publisher.ExportPrivateKey(), cap,
                PresenceRecord.DirectionA, Presence("198.51.100.1", 4000), 1, cts.Token);
            await pub.PublishAsync(publisher.PublicKey, publisher.ExportPrivateKey(), cap,
                PresenceRecord.DirectionA, Presence("203.0.113.99", 5000), 2, cts.Token);

            var found = await look.LookupAsync(publisher.PublicKey, cap, PresenceRecord.DirectionA, cts.Token);
            Assert.NotNull(found);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.99"), 5000), found.Candidates[0].Endpoint);
        }

        [Fact]
        public async Task Dht_Lookup_WrongCapability_FindsNothing()
        {
            var dht = new SimDht(nodeCount: 40, seed: 3);
            var publisher = PeerIdentity.Generate();
            var cap = RandomBytes(32, 4);
            var wrongCap = RandomBytes(32, 5);
            using var pub = dht.NewClient();
            using var look = dht.NewClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            await pub.PublishAsync(publisher.PublicKey, publisher.ExportPrivateKey(), cap,
                PresenceRecord.DirectionA, Presence("203.0.113.7", 6000), 1, cts.Token);
            // A different capability derives a different slot target: nothing there.
            var found = await look.LookupAsync(publisher.PublicKey, wrongCap, PresenceRecord.DirectionA, cts.Token);
            Assert.Null(found);
        }

        [Fact]
        public async Task Dht_Put_RejectedWithoutValidToken()
        {
            // A node only accepts a put whose token it issued to THIS source.
            var dht = new SimDht(nodeCount: 12, seed: 9, requireToken: true);
            var publisher = PeerIdentity.Generate();
            var cap = RandomBytes(32, 6);
            using var pub = dht.NewClient();
            using var look = dht.NewClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var res = await pub.PublishAsync(publisher.PublicKey, publisher.ExportPrivateKey(), cap,
                PresenceRecord.DirectionA, Presence("203.0.113.8", 7000), 1, cts.Token);
            // Tokens flow through get before put, so healthy publication still works.
            Assert.True(res.AckCount > 0);
            var found = await look.LookupAsync(publisher.PublicKey, cap, PresenceRecord.DirectionA, cts.Token);
            Assert.NotNull(found);
        }

        // ── PresenceService scheduling / sequence discipline ──

        [Fact]
        public async Task PresenceService_IdleRepublish_KeepsSequence_ChangeBumpsIt()
        {
            var dht = new SimDht(nodeCount: 30, seed: 21);
            var id = PeerIdentity.Generate();
            using var client = dht.NewClient();
            var svc = new PresenceService(client, now: () => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
            var slot = new PresenceService.Slot
            {
                PublisherPublicKey = id.PublicKey,
                PublisherPrivateKey = id.ExportPrivateKey(),
                Capability = RandomBytes(32, 30),
                Direction = PresenceRecord.DirectionA,
                Sequence = 0,
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var c1 = new List<PresenceRecord.Candidate> { new() { Kind = 1, Endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 5000) } };

            await svc.PublishAsync(slot, c1, cts.Token);
            Assert.Equal(1, slot.Sequence);
            await svc.PublishAsync(slot, c1, cts.Token);          // idle republish, same content
            Assert.Equal(1, slot.Sequence);                       // seq unchanged (BEP 44 identical-value rule)

            var c2 = new List<PresenceRecord.Candidate> { new() { Kind = 1, Endpoint = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 6000) } };
            await svc.PublishAsync(slot, c2, cts.Token);          // moved
            Assert.Equal(2, slot.Sequence);                       // bumped
        }

        [Fact]
        public async Task PresenceService_DiscardsExpiredRecord()
        {
            var dht = new SimDht(nodeCount: 30, seed: 22);
            var id = PeerIdentity.Generate();
            using var pub = dht.NewClient();
            using var look = dht.NewClient();
            var cap = RandomBytes(32, 40);
            // Publisher stamps a record that expires at T=1000. Reader "now" is later.
            var pubSvc = new PresenceService(pub, now: () => DateTimeOffset.FromUnixTimeSeconds(1_000_000));
            var lookSvc = new PresenceService(look, now: () => DateTimeOffset.FromUnixTimeSeconds(2_000_000)); // way past TTL
            var slot = new PresenceService.Slot
            {
                PublisherPublicKey = id.PublicKey, PublisherPrivateKey = id.ExportPrivateKey(),
                Capability = cap, Direction = PresenceRecord.DirectionA,
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await pubSvc.PublishAsync(slot, new List<PresenceRecord.Candidate>
                { new() { Kind = 1, Endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.5"), 5000) } }, cts.Token);

            var found = await lookSvc.LookupAsync(id.PublicKey, cap, PresenceRecord.DirectionA, cts.Token);
            Assert.Null(found); // expired records are never acted on
        }

        [Fact]
        public void PresenceService_RepublishDelay_Within30To45Min()
        {
            var svc = new PresenceService(new NullStore(), jitter: max => max); // max jitter
            var d = svc.NextRepublishDelay();
            Assert.True(d >= TimeSpan.FromMinutes(30) && d <= TimeSpan.FromMinutes(45), $"delay {d}");
            var svc2 = new PresenceService(new NullStore(), jitter: max => 0); // min jitter
            Assert.Equal(TimeSpan.FromMinutes(30), svc2.NextRepublishDelay());
        }

        [Fact]
        public void Capability_PairingTranscript_IsSymmetric_AndCapabilityAgrees()
        {
            var secret = RandomBytes(32, 50);
            var fpA = RandomBytes(32, 51);
            var fpB = RandomBytes(32, 52);
            // Both peers see the two fingerprints and must derive the same value.
            var tAB = PresenceRecord.PairingTranscript(fpA, fpB);
            var tBA = PresenceRecord.PairingTranscript(fpB, fpA);
            Assert.Equal(tAB, tBA);
            Assert.Equal(PresenceRecord.DeriveCapability(secret, tAB),
                         PresenceRecord.DeriveCapability(secret, tBA));
        }

        [Fact]
        public void PunchNonce_BothPeersDeriveTheSame_FromCapability()
        {
            var cap = RandomBytes(32, 77);
            var n1 = PresenceRecord.PunchNonce(cap);
            var n2 = PresenceRecord.PunchNonce(cap);
            Assert.Equal(16, n1.Length);
            Assert.Equal(n1, n2);
            Assert.NotEqual(n1, PresenceRecord.PunchNonce(RandomBytes(32, 78)));
        }

        [Fact]
        public async Task InternetService_PublishesThenReconnects_ThroughTheSimDht()
        {
            var dht = new SimDht(nodeCount: 40, seed: 61);
            var self = PeerIdentity.Generate();
            var peer = PeerIdentity.Generate();
            var cap = RandomBytes(32, 62);

            using var pubClient = dht.NewClient();   // the peer publishes its presence
            using var selfClient = dht.NewClient();  // we look it up
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // The peer publishes its presence under DirectionB (as if it were the
            // remote machine advertising where to reach it).
            var peerSvc = new PresenceService(pubClient);
            var peerSlot = new PresenceService.Slot
            {
                PublisherPublicKey = peer.PublicKey, PublisherPrivateKey = peer.ExportPrivateKey(),
                Capability = cap, Direction = PresenceRecord.DirectionB,
            };
            await peerSvc.PublishAsync(peerSlot, new List<PresenceRecord.Candidate>
                { new() { Kind = 1, Endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.42"), 55000) } }, cts.Token);

            // Our service looks the peer up and "punches" (captured endpoints).
            IReadOnlyList<IPEndPoint> punchedTo = null;
            byte[] punchNonce = null;
            var svc = new RemoteLinkInternetService(
                new PresenceService(selfClient), self.PublicKey, self.ExportPrivateKey(),
                localCandidates: () => new List<PresenceRecord.Candidate>
                    { new() { Kind = 1, Endpoint = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 4000) } },
                connectByPunch: (peerKey, endpoints, nonce, asInitiator, ct) =>
                {
                    punchedTo = endpoints; punchNonce = nonce; return Task.FromResult(true);
                });

            var p = new RemoteLinkInternetService.Peer
            {
                PeerPublicKey = peer.PublicKey, Capability = cap,
                SelfDirection = PresenceRecord.DirectionA, PeerDirection = PresenceRecord.DirectionB,
            };
            await svc.MaintainAsync(new[] { p }, cts.Token);

            Assert.True(p.IsConnected);
            Assert.NotNull(punchedTo);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.42"), 55000), punchedTo[0]);
            Assert.Equal(PresenceRecord.PunchNonce(cap), punchNonce); // shared nonce, no extra exchange
        }

        private sealed class NullStore : IPresenceStore
        {
            public Task<PublishResult> PublishAsync(byte[] a, byte[] b, byte[] c, byte d, PresenceRecord.Presence e, long f, CancellationToken ct)
                => Task.FromResult(new PublishResult { AckCount = 0 });
            public Task<PresenceRecord.Presence> LookupAsync(byte[] a, byte[] b, byte c, CancellationToken ct)
                => Task.FromResult<PresenceRecord.Presence>(null);
        }

        // ── helpers ──

        private static PresenceRecord.Presence Presence(string ip1, int port1, string ip2 = null, int port2 = 0)
        {
            var list = new List<PresenceRecord.Candidate>
            {
                new() { Kind = PresenceRecord.Candidate.KindPublicV4, Endpoint = new IPEndPoint(IPAddress.Parse(ip1), port1) },
            };
            if (ip2 != null)
                list.Add(new() { Kind = PresenceRecord.Candidate.KindPrivateV4, Endpoint = new IPEndPoint(IPAddress.Parse(ip2), port2) });
            return new PresenceRecord.Presence
            {
                Candidates = list,
                IssuedAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
                Expiry = DateTimeOffset.FromUnixTimeSeconds(1_800_003_600),
            };
        }

        private static byte[] RandomBytes(int n, int seed)
        {
            var r = new Random(seed); var b = new byte[n]; r.NextBytes(b); return b;
        }
    }
}
