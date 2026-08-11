using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink.Dht
{
    /// <summary>Sends a KRPC datagram to a node and routes responses back by
    /// transaction id. The real implementation is a UDP socket; tests use an
    /// in-process simulated DHT. Abstracting it keeps the iterative lookup and
    /// the put/get orchestration deterministically testable without the live
    /// network (the one true residual).</summary>
    public interface IKrpcTransport
    {
        Task SendAsync(byte[] datagram, IPEndPoint node, CancellationToken ct);
        /// <summary>Owner routes inbound KRPC datagrams here (source + bytes).</summary>
        Action<IPEndPoint, byte[]> OnDatagram { get; set; }
    }

    /// <summary>
    /// The mainline-DHT presence store (#294): publishes and looks up BEP 44
    /// mutable items via an iterative Kademlia lookup. Implements the health and
    /// correctness rules from the adjudication:
    ///
    /// - Iterative find-the-closest via <c>get</c> (which returns closer nodes
    ///   AND a write token AND any stored value), not a single RPC. BEP 5-only
    ///   nodes answer "method unknown" without closer nodes, so the lookup must
    ///   traverse, keeping the closest responders and their tokens.
    /// - PUT to the closest token-bearing nodes; report the ack count so the
    ///   caller can require >= 8 before calling a publish healthy.
    /// - On GET, verify every returned item: the key must hash to the queried
    ///   target AND the BEP 44 signature must verify, then take the HIGHEST
    ///   valid sequence, never the first reply.
    /// - Read-only (BEP 43 ro=1); a random 20-byte node id per instance.
    /// </summary>
    public sealed class DhtPresenceStore : IPresenceStore, IDisposable
    {
        private const int K = 8;              // closest-node set size (Kademlia)
        private const int Alpha = 3;          // lookup parallelism
        private const int MaxRounds = 12;     // lookup convergence bound
        private static readonly TimeSpan RpcTimeout = TimeSpan.FromSeconds(2);

        private readonly IKrpcTransport _transport;
        private readonly IReadOnlyList<IPEndPoint> _bootstrap;
        private readonly byte[] _nodeId;
        private int _txnCounter;

        // Pending RPCs keyed by transaction id (hex), completed by the router.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<Krpc.Response>> _pending = new();

        public DhtPresenceStore(IKrpcTransport transport, IReadOnlyList<IPEndPoint> bootstrap, byte[] nodeId = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _bootstrap = bootstrap ?? Array.Empty<IPEndPoint>();
            _nodeId = nodeId ?? RandomNodeId();
            _transport.OnDatagram = OnDatagram;
        }

        /// <summary>The public bootstrap routers for the live mainline DHT.</summary>
        public static readonly (string Host, int Port)[] DefaultBootstrap =
        {
            ("router.bittorrent.com", 6881),
            ("dht.transmissionbt.com", 6881),
            ("router.utorrent.com", 6881),
            ("dht.libtorrent.org", 25401),
        };

        private static byte[] RandomNodeId()
        {
            var id = new byte[20];
            RandomNumberGenerator.Fill(id);
            return id;
        }

        private byte[] NextTxn()
        {
            int n = Interlocked.Increment(ref _txnCounter);
            return new[] { (byte)(n >> 8), (byte)n };
        }

        private static string TxnKey(byte[] txn) => Convert.ToHexString(txn);

        public void OnDatagram(IPEndPoint from, byte[] dg)
        {
            var resp = Krpc.ParseResponse(dg);
            if (resp?.Txn == null) return;
            if (_pending.TryRemove(TxnKey(resp.Txn), out var tcs))
                tcs.TrySetResult(resp);
        }

        private async Task<Krpc.Response> RpcAsync(byte[] datagram, byte[] txn, IPEndPoint node, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<Krpc.Response>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[TxnKey(txn)] = tcs;
            try
            {
                await _transport.SendAsync(datagram, node, ct).ConfigureAwait(false);
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timer.CancelAfter(RpcTimeout);
                try { return await tcs.Task.WaitAsync(timer.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; } // node didn't answer in time
            }
            catch { return null; }
            finally { _pending.TryRemove(TxnKey(txn), out _); }
        }

        // A node we've heard from during a lookup, with the token + any stored
        // item it returned. Ordered by XOR distance to the lookup target.
        private sealed class Contact
        {
            public DhtNode Node;
            public byte[] Token;
            public bool Queried;
            public Krpc.Response Stored; // the get response, if it carried an item
        }

        /// <summary>Iterative Kademlia lookup toward <paramref name="target"/>
        /// using <c>get</c> (so we collect tokens and any stored value along the
        /// way). Returns the K closest responders.</summary>
        private async Task<List<Contact>> LookupAsync(byte[] target, CancellationToken ct)
        {
            var shortlist = new List<Contact>();
            var seen = new HashSet<string>();
            var gate = new object();

            void Consider(DhtNode n)
            {
                if (n?.Id == null || n.Id.Length != 20) return;
                string key = n.Endpoint.ToString();
                // The Alpha parallel queries each feed newly-learned nodes here,
                // so the shared shortlist/seen must be mutated under a lock.
                lock (gate)
                {
                    if (!seen.Add(key)) return;
                    shortlist.Add(new Contact { Node = n });
                }
            }

            // Seed from bootstrap (unknown ids: use a random id so distance
            // sorting still functions; they're only a springboard).
            foreach (var ep in _bootstrap)
                Consider(new DhtNode { Id = RandomNodeId(), Endpoint = ep });

            for (int round = 0; round < MaxRounds && !ct.IsCancellationRequested; round++)
            {
                shortlist.Sort((a, b) => Krpc.XorCompare(a.Node.Id, b.Node.Id, target));
                var batch = shortlist.Where(c => !c.Queried).Take(Alpha).ToList();
                if (batch.Count == 0) break;

                var tasks = batch.Select(async c =>
                {
                    c.Queried = true;
                    var txn = NextTxn();
                    var resp = await RpcAsync(Krpc.Get(_nodeId, target, txn), txn, c.Node.Endpoint, ct).ConfigureAwait(false);
                    if (resp is { IsResponse: true })
                    {
                        c.Token = resp.Token;
                        if (resp.Value != null && resp.PublicKey != null) c.Stored = resp;
                        if (resp.Nodes != null) foreach (var n in resp.Nodes) Consider(n);
                    }
                }).ToList();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            shortlist.Sort((a, b) => Krpc.XorCompare(a.Node.Id, b.Node.Id, target));
            return shortlist.Where(c => c.Queried).Take(K).ToList();
        }

        public async Task<PublishResult> PublishAsync(
            byte[] publisherPublicKey, byte[] publisherPrivateKey,
            byte[] capability, byte direction,
            PresenceRecord.Presence presence, long seq, CancellationToken ct)
        {
            var salt = PresenceRecord.SlotSalt(capability, direction);
            var target = Bep44Record.ComputeTarget(publisherPublicKey, salt);
            var value = PresenceRecord.EncodeValue(presence, capability, direction, publisherPublicKey, seq);
            var sig = Bep44Record.Sign(publisherPrivateKey, value, seq, salt);

            var closest = await LookupAsync(target, ct).ConfigureAwait(false);

            // PUT to every closest node that gave us a token.
            var puts = closest.Where(c => c.Token != null).Select(async c =>
            {
                var txn = NextTxn();
                var dg = Krpc.PutMutable(_nodeId, publisherPublicKey, value, seq, sig, c.Token, salt, txn);
                var resp = await RpcAsync(dg, txn, c.Node.Endpoint, ct).ConfigureAwait(false);
                return resp is { IsResponse: true, IsError: false };
            }).ToList();

            var results = await Task.WhenAll(puts).ConfigureAwait(false);
            return new PublishResult { AckCount = results.Count(ok => ok) };
        }

        /// <summary>Publishes an arbitrary signed value at an explicit
        /// (keypair, salt) slot. Used by the code rendezvous (#294): the slot
        /// is derived from the connection code rather than a peer identity,
        /// because at FIRST contact the two sides share nothing else.</summary>
        public async Task<PublishResult> PublishRawAsync(
            byte[] publicKey, byte[] privateKey, byte[] salt, byte[] value, long seq, CancellationToken ct)
        {
            var target = Bep44Record.ComputeTarget(publicKey, salt);
            var sig = Bep44Record.Sign(privateKey, value, seq, salt);
            var closest = await LookupAsync(target, ct).ConfigureAwait(false);
            var puts = closest.Where(c => c.Token != null).Select(async c =>
            {
                var txn = NextTxn();
                var dg = Krpc.PutMutable(_nodeId, publicKey, value, seq, sig, c.Token, salt, txn);
                var resp = await RpcAsync(dg, txn, c.Node.Endpoint, ct).ConfigureAwait(false);
                return resp is { IsResponse: true, IsError: false };
            }).ToList();
            var results = await Task.WhenAll(puts).ConfigureAwait(false);
            return new PublishResult { AckCount = results.Count(ok => ok) };
        }

        /// <summary>Fetches the highest-sequence signed value at a (publicKey,
        /// salt) slot, or (null, 0). Verifies the key hashes to the requested
        /// target and that the signature covers the value, so a hostile node
        /// cannot substitute content.</summary>
        public async Task<(byte[] Value, long Seq)> FetchRawAsync(
            byte[] publicKey, byte[] salt, CancellationToken ct)
        {
            var target = Bep44Record.ComputeTarget(publicKey, salt);
            var closest = await LookupAsync(target, ct).ConfigureAwait(false);
            byte[] best = null; long bestSeq = 0;
            foreach (var c in closest)
            {
                var item = c.Stored;
                if (item?.Value == null || item.PublicKey == null || item.Signature == null || !item.HasSeq) continue;
                if (!item.PublicKey.AsSpan().SequenceEqual(publicKey)) continue;
                var itemTarget = Bep44Record.ComputeTarget(item.PublicKey, salt);
                if (!itemTarget.AsSpan().SequenceEqual(target)) continue;
                if (!Bep44Record.Verify(item.PublicKey, item.Value, item.Seq, item.Signature, salt)) continue;
                if (best != null && item.Seq <= bestSeq) continue;
                best = item.Value; bestSeq = item.Seq;
            }
            return (best, bestSeq);
        }

        public async Task<PresenceRecord.Presence> LookupAsync(
            byte[] peerPublicKey, byte[] capability, byte peerDirection, CancellationToken ct)
        {
            var salt = PresenceRecord.SlotSalt(capability, peerDirection);
            var target = Bep44Record.ComputeTarget(peerPublicKey, salt);

            var closest = await LookupAsync(target, ct).ConfigureAwait(false);

            // Take the HIGHEST valid sequence across everything returned, never
            // the first reply (finding: path-dependent DHT views mean the first
            // node may hold a stale copy).
            PresenceRecord.Presence best = null;
            long bestSeq = long.MinValue;
            foreach (var c in closest)
            {
                var item = c.Stored;
                if (item?.Value == null || item.PublicKey == null || item.Signature == null || !item.HasSeq) continue;
                // The key must hash to the target we asked for AND be the peer
                // we pinned, and the signature must verify over the value.
                if (!item.PublicKey.AsSpan().SequenceEqual(peerPublicKey)) continue;
                var itemTarget = Bep44Record.ComputeTarget(item.PublicKey, salt);
                if (!itemTarget.AsSpan().SequenceEqual(target)) continue;
                if (!Bep44Record.Verify(item.PublicKey, item.Value, item.Seq, item.Signature, salt)) continue;
                if (item.Seq <= bestSeq) continue;
                if (!PresenceRecord.TryDecodeValue(item.Value, capability, peerDirection, peerPublicKey, item.Seq, out var pres))
                    continue;
                best = pres;
                bestSeq = item.Seq;
            }
            return best;
        }

        public void Dispose()
        {
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            _pending.Clear();
        }
    }
}
