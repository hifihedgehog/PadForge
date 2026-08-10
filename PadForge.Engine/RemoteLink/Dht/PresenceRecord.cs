using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Engine.RemoteLink.Dht
{
    /// <summary>
    /// The encrypted presence a paired peer publishes to the DHT so the other
    /// finds its CURRENT endpoints after either side moves (#294). Corrected
    /// per the Codex adjudication: the record must NOT be encrypted under
    /// anything derived from the Ed25519 public key, because BEP 44 publishes
    /// that key (`k`) in the clear to every storage node.
    ///
    /// Instead a 32-byte PAIRWISE RENDEZVOUS CAPABILITY is established once at
    /// pairing time (derived from that pairing's X25519 secret + transcript and
    /// PERSISTED, because later sessions use fresh ephemeral keys) and known
    /// only to the two paired peers. From it, per pair AND per publishing
    /// direction:
    ///   slotSalt = HKDF(cap, "PadForge/BEP44/slot/v1" || dir)      -> BEP 44 salt
    ///   aeadKey  = HKDF(cap, "PadForge/BEP44/aead/v1" || dir)      -> ChaCha20 key
    ///   target   = SHA1(publisherEd25519Pub || slotSalt)          -> DHT key
    /// The value is nonce || ChaCha20-Poly1305(candidate list) with the target,
    /// seq, version and direction bound as associated data. BEP 44's own
    /// Ed25519 signature (over the whole value) authenticates it, so no outer
    /// application signature is added. One slot per (pair, direction) so peer
    /// removal and cross-peer privacy are clean.
    ///
    /// The candidate list is VERSIONED and open-ended (IPv6, multiple
    /// interfaces, future candidate kinds) rather than a fixed IPv4 pair, for a
    /// ten-year horizon.
    /// </summary>
    public static class PresenceRecord
    {
        private const byte Version = 1;
        private const int NonceLen = 12; // ChaCha20-Poly1305 nonce
        // AAD domain tags: the publishing direction (which of the two paired
        // peers authored this slot). A reader derives the peer's direction.
        public const byte DirectionA = 0;
        public const byte DirectionB = 1;

        private static readonly byte[] SlotInfo = Encoding.ASCII.GetBytes("PadForge/BEP44/slot/v1");
        private static readonly byte[] AeadInfo = Encoding.ASCII.GetBytes("PadForge/BEP44/aead/v1");

        /// <summary>One reachability candidate. Kind is open-ended so future
        /// transports (IPv6, relay-assisted, additional interfaces) extend the
        /// list without a format break.</summary>
        public sealed class Candidate
        {
            public const byte KindPublicV4 = 1;
            public const byte KindPrivateV4 = 2;
            public const byte KindV6 = 3;

            public byte Kind { get; init; }
            public IPEndPoint Endpoint { get; init; }
        }

        public sealed class Presence
        {
            public IReadOnlyList<Candidate> Candidates { get; init; }
            public DateTimeOffset IssuedAt { get; init; }
            public DateTimeOffset Expiry { get; init; }
            public bool IsExpired(DateTimeOffset now) => now > Expiry;
        }

        // ── capability derivation ──

        /// <summary>The BEP 44 salt for a (capability, direction) slot.</summary>
        public static byte[] SlotSalt(byte[] capability, byte direction)
            => PeerCrypto.DeriveKey(capability, InfoWithDir(SlotInfo, direction), ReadOnlySpan<byte>.Empty, 20);

        /// <summary>The AEAD key for a (capability, direction) slot.</summary>
        public static byte[] AeadKey(byte[] capability, byte direction)
            => PeerCrypto.DeriveKey(capability, InfoWithDir(AeadInfo, direction), ReadOnlySpan<byte>.Empty, 32);

        /// <summary>The DHT target for a slot: SHA1(publisherPub || slotSalt).</summary>
        public static byte[] Target(byte[] publisherEd25519Pub, byte[] capability, byte direction)
            => Bep44Record.ComputeTarget(publisherEd25519Pub, SlotSalt(capability, direction));

        // HKDF here takes the capability as the SALT position and the info as
        // the domain string; a fixed all-zero IKM keeps the two outputs (slot
        // salt, aead key) independent while both are bound to the capability.
        private static byte[] InfoWithDir(byte[] info, byte direction)
        {
            var buf = new byte[info.Length + 1];
            info.CopyTo(buf, 0);
            buf[info.Length] = direction;
            return buf;
        }

        // ── encode / decode ──

        /// <summary>Serializes and encrypts a presence into a BEP 44 value
        /// (nonce || AEAD ciphertext). Bound as AAD: version, direction, seq,
        /// and the slot target, so a value cannot be replayed into another slot
        /// or sequence.</summary>
        public static byte[] EncodeValue(Presence presence, byte[] capability, byte direction,
            byte[] publisherEd25519Pub, long seq)
        {
            var plain = SerializePlain(presence);
            var key = AeadKey(capability, direction);
            var nonce = new byte[NonceLen];
            RandomNumberGenerator.Fill(nonce);
            var target = Target(publisherEd25519Pub, capability, direction);
            var aad = BuildAad(direction, seq, target);
            var ct = PeerCrypto.Seal(key, nonce, aad, plain);

            var value = new byte[NonceLen + ct.Length];
            nonce.CopyTo(value, 0);
            ct.CopyTo(value, NonceLen);
            if (value.Length > Bep44Record.MaxValueBytes)
                throw new InvalidOperationException("Presence value exceeds the BEP 44 1000-byte cap.");
            return value;
        }

        /// <summary>Decrypts and parses a BEP 44 value back into a presence.
        /// Returns false on any AEAD failure (wrong capability, tamper, replay
        /// into a different slot/seq) or malformed plaintext.</summary>
        public static bool TryDecodeValue(byte[] value, byte[] capability, byte direction,
            byte[] publisherEd25519Pub, long seq, out Presence presence)
        {
            presence = null;
            if (value == null || value.Length < NonceLen + 16) return false;
            var key = AeadKey(capability, direction);
            var nonce = value.AsSpan(0, NonceLen).ToArray();
            var ct = value.AsSpan(NonceLen).ToArray();
            var target = Target(publisherEd25519Pub, capability, direction);
            var aad = BuildAad(direction, seq, target);
            if (!PeerCrypto.Open(key, nonce, aad, ct, out var plain)) return false;
            return TryDeserializePlain(plain, out presence);
        }

        private static byte[] BuildAad(byte direction, long seq, byte[] target)
        {
            var aad = new byte[1 + 1 + 8 + target.Length];
            aad[0] = Version;
            aad[1] = direction;
            BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(2), seq);
            target.CopyTo(aad, 10);
            return aad;
        }

        private static byte[] SerializePlain(Presence p)
        {
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(Version);
            var issued = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(issued, p.IssuedAt.ToUnixTimeSeconds()); ms.Write(issued);
            var exp = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(exp, p.Expiry.ToUnixTimeSeconds()); ms.Write(exp);
            byte count = (byte)Math.Min(p.Candidates?.Count ?? 0, 255);
            ms.WriteByte(count);
            for (int i = 0; i < count; i++)
            {
                var c = p.Candidates[i];
                ms.WriteByte(c.Kind);
                var ipBytes = c.Endpoint.Address.GetAddressBytes();
                ms.WriteByte((byte)ipBytes.Length); // 4 or 16
                ms.Write(ipBytes);
                var port = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)c.Endpoint.Port); ms.Write(port);
            }
            return ms.ToArray();
        }

        private static bool TryDeserializePlain(byte[] data, out Presence presence)
        {
            presence = null;
            try
            {
                int pos = 0;
                if (data.Length < 1 + 8 + 8 + 1 || data[pos++] != Version) return false;
                long issued = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos)); pos += 8;
                long expiry = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos)); pos += 8;
                int count = data[pos++];
                var list = new List<Candidate>(count);
                for (int i = 0; i < count; i++)
                {
                    byte kind = data[pos++];
                    int ipLen = data[pos++];
                    if (ipLen != 4 && ipLen != 16) return false;
                    if (pos + ipLen + 2 > data.Length) return false;
                    var ip = new IPAddress(data.AsSpan(pos, ipLen).ToArray()); pos += ipLen;
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)); pos += 2;
                    list.Add(new Candidate { Kind = kind, Endpoint = new IPEndPoint(ip, port) });
                }
                presence = new Presence
                {
                    Candidates = list,
                    IssuedAt = DateTimeOffset.FromUnixTimeSeconds(issued),
                    Expiry = DateTimeOffset.FromUnixTimeSeconds(expiry),
                };
                return true;
            }
            catch { return false; }
        }

        /// <summary>Derives the persistent 32-byte rendezvous capability from a
        /// pairing's shared secret and transcript. Both peers compute the SAME
        /// value from the SAME inputs, so it is stored once at pairing and
        /// reused for the life of the pairing (fresh session ephemerals never
        /// reproduce it). Directions are assigned by comparing fingerprints so
        /// both sides agree which peer is A and which is B.</summary>
        public static byte[] DeriveCapability(byte[] pairingSharedSecret, byte[] transcriptHash)
        {
            var info = Encoding.ASCII.GetBytes("PadForge/BEP44/capability/v1");
            return PeerCrypto.DeriveKey(pairingSharedSecret, transcriptHash, info, 32);
        }

        /// <summary>Which direction a given peer publishes under, so the two
        /// sides never collide on one slot. The peer with the lexicographically
        /// smaller fingerprint is A.</summary>
        public static byte DirectionFor(byte[] selfFingerprint, byte[] peerFingerprint)
        {
            int cmp = CompareBytes(selfFingerprint, peerFingerprint);
            return cmp <= 0 ? DirectionA : DirectionB;
        }

        private static int CompareBytes(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) { int d = a[i] - b[i]; if (d != 0) return d; }
            return a.Length - b.Length;
        }
    }
}
