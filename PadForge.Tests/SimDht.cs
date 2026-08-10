using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink.Dht;

namespace PadForge.Tests
{
    /// <summary>
    /// In-process simulated mainline DHT for #294 KRPC tests: N nodes, each with
    /// a 20-byte id, that answer find_node/get by returning the closest nodes
    /// they know (global routing table for simplicity), issue per-(source,target)
    /// tokens, and store BEP 44 mutable items enforcing the real seq-monotonic
    /// and signature rules. A client attaches via <see cref="NewClient"/> and
    /// bootstraps against a fixed subset. Deterministic under a seed.
    /// </summary>
    internal sealed class SimDht
    {
        private readonly List<SimNode> _nodes = new();
        private readonly bool _requireToken;

        public SimDht(int nodeCount, int seed, bool requireToken = false)
        {
            _requireToken = requireToken;
            var rng = new Random(seed);
            for (int i = 0; i < nodeCount; i++)
            {
                var id = new byte[20];
                rng.NextBytes(id);
                // Distinct loopback-ish endpoints; only used as routing keys.
                var ep = new IPEndPoint(new IPAddress(new byte[] { 10, (byte)(i >> 8), (byte)i, 1 }), 6881);
                _nodes.Add(new SimNode(id, ep, this));
            }
        }

        public DhtPresenceStore NewClient()
        {
            var transport = new SimTransport(this);
            // Bootstrap against the first few nodes.
            var boot = _nodes.Take(4).Select(n => n.Endpoint).ToList();
            return new DhtPresenceStore(transport, boot);
        }

        private SimNode FindByEndpoint(IPEndPoint ep) => _nodes.FirstOrDefault(n => n.Endpoint.Equals(ep));

        // The K closest nodes to a target, as compact info the querier can chase.
        public List<DhtNode> Closest(byte[] target, int k)
            => _nodes.OrderBy(n => n.Id, new XorComparer(target)).Take(k)
                     .Select(n => new DhtNode { Id = n.Id, Endpoint = n.Endpoint }).ToList();

        private sealed class XorComparer : IComparer<byte[]>
        {
            private readonly byte[] _ref;
            public XorComparer(byte[] r) { _ref = r; }
            public int Compare(byte[] a, byte[] b) => Krpc.XorCompare(a, b, _ref);
        }

        // One simulated node: routing + token issuance + BEP 44 storage.
        private sealed class SimNode
        {
            public readonly byte[] Id;
            public readonly IPEndPoint Endpoint;
            private readonly SimDht _dht;
            private readonly Dictionary<string, StoredItem> _store = new();
            private readonly Dictionary<string, byte[]> _tokens = new(); // source|target -> token

            public SimNode(byte[] id, IPEndPoint ep, SimDht dht) { Id = id; Endpoint = ep; _dht = dht; }

            private sealed class StoredItem
            {
                public byte[] Key, Value, Sig, Salt;
                public long Seq;
            }

            public byte[] Handle(IPEndPoint from, byte[] datagram)
            {
                object decoded;
                try { decoded = Bencode.Decode(datagram); }
                catch { return null; }
                if (decoded is not IDictionary<string, object> top) return null;
                var txn = Bencode.GetBytes(top, "t");
                var q = Bencode.GetBytes(top, "q");
                if (txn == null || q == null) return null;
                string method = Encoding.ASCII.GetString(q);
                if (top["a"] is not IDictionary<string, object> a) return null;
                var target = Bencode.GetBytes(a, "target");

                var r = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["id"] = Id };

                if (method == "find_node" || method == "get")
                {
                    if (target != null)
                    {
                        var closest = _dht.Closest(target, 8);
                        r["nodes"] = CompactNodes(closest);
                        // Token bound to (source, target), the real contract.
                        var tok = IssueToken(from, target);
                        r["token"] = tok;
                        if (method == "get")
                        {
                            string tkey = Convert.ToHexString(target);
                            if (_store.TryGetValue(tkey, out var item))
                            {
                                r["k"] = item.Key;
                                r["v"] = item.Value;
                                r["seq"] = item.Seq;
                                r["sig"] = item.Sig;
                            }
                        }
                    }
                }
                else if (method == "put")
                {
                    var key = Bencode.GetBytes(a, "k");
                    var value = Bencode.GetBytes(a, "v");
                    var sig = Bencode.GetBytes(a, "sig");
                    var token = Bencode.GetBytes(a, "token");
                    var salt = Bencode.GetBytes(a, "salt");
                    long seq = Bencode.GetLong(a, "seq");
                    if (key == null || value == null || sig == null)
                        return Error(txn, 203, "bad put");
                    // Token check (BEP 44).
                    if (_dht._requireToken)
                    {
                        var expect = ExpectToken(from, Bep44Record.ComputeTarget(key, salt));
                        if (token == null || expect == null || !token.SequenceEqual(expect))
                            return Error(txn, 203, "bad token");
                    }
                    // Signature must verify (BEP 44).
                    if (!Bep44Record.Verify(key, value, seq, sig, salt))
                        return Error(txn, 206, "bad signature");
                    string tkey = Convert.ToHexString(Bep44Record.ComputeTarget(key, salt));
                    if (_store.TryGetValue(tkey, out var existing) && seq < existing.Seq)
                        return Error(txn, 302, "sequence downgrade"); // MUST NOT downgrade
                    _store[tkey] = new StoredItem { Key = key, Value = value, Sig = sig, Salt = salt, Seq = seq };
                }

                var resp = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["t"] = txn,
                    ["y"] = Encoding.ASCII.GetBytes("r"),
                    ["r"] = r,
                };
                return Bencode.Encode(resp);
            }

            private byte[] IssueToken(IPEndPoint from, byte[] target)
            {
                var tok = new byte[8];
                RandomNumberGenerator.Fill(tok);
                _tokens[from + "|" + Convert.ToHexString(target)] = tok;
                return tok;
            }

            private byte[] ExpectToken(IPEndPoint from, byte[] target)
                => _tokens.TryGetValue(from + "|" + Convert.ToHexString(target), out var t) ? t : null;

            private static byte[] CompactNodes(List<DhtNode> nodes)
            {
                var buf = new byte[nodes.Count * Krpc.CompactNodeLen];
                for (int i = 0; i < nodes.Count; i++)
                {
                    var n = nodes[i]; int o = i * Krpc.CompactNodeLen;
                    n.Id.CopyTo(buf, o);
                    n.Endpoint.Address.GetAddressBytes().CopyTo(buf, o + 20);
                    buf[o + 24] = (byte)(n.Endpoint.Port >> 8);
                    buf[o + 25] = (byte)n.Endpoint.Port;
                }
                return buf;
            }

            private static byte[] Error(byte[] txn, int code, string msg)
            {
                var resp = new SortedDictionary<string, object>(StringComparer.Ordinal)
                {
                    ["t"] = txn,
                    ["y"] = Encoding.ASCII.GetBytes("e"),
                    ["e"] = new List<object> { (long)code, Encoding.ASCII.GetBytes(msg) },
                };
                return Bencode.Encode(resp);
            }
        }

        // Transport that routes a client's datagram to the addressed node and
        // feeds the node's reply back to the client asynchronously.
        private sealed class SimTransport : IKrpcTransport
        {
            private readonly SimDht _dht;
            private readonly IPEndPoint _self = new(new IPAddress(new byte[] { 127, 0, 0, 99 }), 1);
            public Action<IPEndPoint, byte[]> OnDatagram { get; set; }
            public SimTransport(SimDht dht) { _dht = dht; }

            public Task SendAsync(byte[] datagram, IPEndPoint node, CancellationToken ct)
            {
                var target = _dht.FindByEndpoint(node);
                if (target == null) return Task.CompletedTask; // dead node, no reply
                var reply = target.Handle(_self, datagram);
                if (reply != null)
                    _ = Task.Run(() => OnDatagram?.Invoke(node, reply));
                return Task.CompletedTask;
            }
        }
    }
}
