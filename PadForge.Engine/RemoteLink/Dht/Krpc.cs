using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace PadForge.Engine.RemoteLink.Dht
{
    /// <summary>A DHT node: its 20-byte id and its endpoint.</summary>
    public sealed class DhtNode
    {
        public byte[] Id { get; init; }
        public IPEndPoint Endpoint { get; init; }
    }

    /// <summary>
    /// KRPC message construction and parsing for the mainline DHT
    /// (BEP 5 + BEP 44), on top of <see cref="Bencode"/>. All strings are byte
    /// strings; compact node info is 26 bytes (20 id + 4 IPv4 + 2 port BE).
    /// The client sets read-only (BEP 43 <c>ro=1</c>) because PadForge answers
    /// no queries, only issues them.
    /// </summary>
    public static class Krpc
    {
        public const int CompactNodeLen = 26;

        private static SortedDictionary<string, object> Query(string method, SortedDictionary<string, object> args, byte[] txn)
            => new(StringComparer.Ordinal)
            {
                ["t"] = txn,
                ["y"] = Encoding.ASCII.GetBytes("q"),
                ["q"] = Encoding.ASCII.GetBytes(method),
                ["a"] = args,
                ["ro"] = 1L, // BEP 43: read-only, we never answer queries
                ["v"] = Encoding.ASCII.GetBytes("PF01"),
            };

        public static byte[] FindNode(byte[] myId, byte[] target, byte[] txn)
            => Bencode.Encode(Query("find_node", new(StringComparer.Ordinal)
            {
                ["id"] = myId,
                ["target"] = target,
                ["want"] = new List<object> { Encoding.ASCII.GetBytes("n4") },
            }, txn));

        /// <summary>BEP 44 get: returns the stored item (k/v/seq/sig), a write
        /// token, and closer nodes.</summary>
        public static byte[] Get(byte[] myId, byte[] target, byte[] txn)
            => Bencode.Encode(Query("get", new(StringComparer.Ordinal)
            {
                ["id"] = myId,
                ["target"] = target,
            }, txn));

        /// <summary>BEP 44 put of a mutable item. Salt omitted when null/empty.</summary>
        public static byte[] PutMutable(byte[] myId, byte[] publicKey, byte[] value,
            long seq, byte[] signature, byte[] token, byte[] salt, byte[] txn)
        {
            var a = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = myId,
                ["k"] = publicKey,
                ["seq"] = seq,
                ["sig"] = signature,
                ["token"] = token,
                ["v"] = value, // byte string; bencodes as <len>:<bytes>
            };
            if (salt != null && salt.Length > 0) a["salt"] = salt;
            return Bencode.Encode(Query("put", a, txn));
        }

        /// <summary>Parses a compact "nodes" byte string into DhtNodes (IPv4).
        /// Silently stops at a partial trailing entry.</summary>
        public static List<DhtNode> ParseCompactNodes(byte[] nodes)
        {
            var list = new List<DhtNode>();
            if (nodes == null) return list;
            for (int i = 0; i + CompactNodeLen <= nodes.Length; i += CompactNodeLen)
            {
                var id = new byte[20];
                Array.Copy(nodes, i, id, 0, 20);
                var ip = new IPAddress(new[] { nodes[i + 20], nodes[i + 21], nodes[i + 22], nodes[i + 23] });
                int port = BinaryPrimitives.ReadUInt16BigEndian(nodes.AsSpan(i + 24, 2));
                if (port == 0) continue;
                list.Add(new DhtNode { Id = id, Endpoint = new IPEndPoint(ip, port) });
            }
            return list;
        }

        /// <summary>The decoded fields of a KRPC response (y=r). Absent fields
        /// are null. Callers read what a given query type promises.</summary>
        public sealed class Response
        {
            public byte[] Txn { get; init; }
            public bool IsResponse { get; init; }
            public bool IsError { get; init; }
            public byte[] NodeId { get; init; }
            public List<DhtNode> Nodes { get; init; }
            public byte[] Token { get; init; }
            public byte[] PublicKey { get; init; } // BEP 44 get: k
            public byte[] Value { get; init; }     // BEP 44 get: v
            public long Seq { get; init; }
            public byte[] Signature { get; init; } // BEP 44 get: sig
            public bool HasSeq { get; init; }
        }

        /// <summary>Parses a KRPC response datagram. Returns null if it is not a
        /// well-formed bencoded dict with a transaction id.</summary>
        public static Response ParseResponse(byte[] datagram)
        {
            object decoded;
            try { decoded = Bencode.Decode(datagram); }
            catch { return null; }
            if (decoded is not IDictionary<string, object> top) return null;
            var txn = Bencode.GetBytes(top, "t");
            if (txn == null) return null;
            var y = Bencode.GetBytes(top, "y");
            string yStr = y != null ? Encoding.ASCII.GetString(y) : "";

            if (yStr == "e")
                return new Response { Txn = txn, IsError = true };
            if (yStr != "r") return new Response { Txn = txn };

            if (!top.TryGetValue("r", out var rObj) || rObj is not IDictionary<string, object> r)
                return new Response { Txn = txn, IsResponse = true };

            bool hasSeq = r.TryGetValue("seq", out var seqObj) && seqObj is long;
            return new Response
            {
                Txn = txn,
                IsResponse = true,
                NodeId = Bencode.GetBytes(r, "id"),
                Nodes = ParseCompactNodes(Bencode.GetBytes(r, "nodes")),
                Token = Bencode.GetBytes(r, "token"),
                PublicKey = Bencode.GetBytes(r, "k"),
                Value = Bencode.GetBytes(r, "v"),
                Seq = hasSeq ? (long)seqObj : 0,
                HasSeq = hasSeq,
                Signature = Bencode.GetBytes(r, "sig"),
            };
        }

        // ── XOR distance (Kademlia metric) ──

        /// <summary>Compares |a xor ref| vs |b xor ref|: negative if a is
        /// closer to ref than b. Both ids are 20 bytes.</summary>
        public static int XorCompare(byte[] a, byte[] b, byte[] reference)
        {
            for (int i = 0; i < 20; i++)
            {
                byte xa = (byte)(a[i] ^ reference[i]);
                byte xb = (byte)(b[i] ^ reference[i]);
                if (xa != xb) return xa < xb ? -1 : 1;
            }
            return 0;
        }
    }
}
