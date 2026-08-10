using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// The introducer wire protocol (#294 step 5), pure and signed so it is
    /// fully offline-testable. The rendezvous server maps "identity -> current
    /// signed endpoints" and carries ZERO game traffic. Every published record
    /// is signed by the publisher's Ed25519 static key, so the server (or a MITM)
    /// cannot forge or hijack an entry: a consumer verifies the signature against
    /// the fingerprint it already pinned. The code is a lookup key, never a
    /// secret; SAS + the handshake still gate first-contact trust.
    ///
    /// Records are transport-encoded here (compact binary, base64 for JSON/HTTP
    /// bodies). The HTTP framing and the server itself are a hosting decision
    /// outside this class; <see cref="RendezvousClient"/> is the HTTP client.
    /// </summary>
    public static class RendezvousProtocol
    {
        private const byte Version = 1;

        /// <summary>A signed presence record: who (fingerprint), where (candidate
        /// endpoints), and until when. The receiver publishes one under its short
        /// code on first contact and refreshes it (keyed by fingerprint) on every
        /// address change, so paired peers re-find each other after any move.</summary>
        public sealed class PresenceRecord
        {
            public byte[] Fingerprint { get; init; }
            public byte[] PublicKey { get; init; }
            public IPEndPoint PublicEndpoint { get; init; }
            public IPEndPoint PrivateEndpoint { get; init; }
            public DateTimeOffset Expiry { get; init; }
        }

        /// <summary>Serializes the SIGNED PORTION of a record (everything a
        /// signature must cover: identity + endpoints + expiry). The signature
        /// itself is appended by <see cref="SignRecord"/> and is NOT part of this
        /// buffer.</summary>
        public static byte[] EncodeSignedPortion(PresenceRecord record)
        {
            if (record?.PublicKey == null || record.PublicKey.Length != PeerCrypto.KeySize)
                throw new ArgumentException("Record needs a full public key.", nameof(record));

            var buf = new byte[1 + PeerCrypto.KeySize + 6 + 6 + 8];
            int pos = 0;
            buf[pos++] = Version;
            record.PublicKey.CopyTo(buf, pos); pos += PeerCrypto.KeySize;
            WriteEndpoint(buf.AsSpan(pos), record.PublicEndpoint); pos += 6;
            WriteEndpoint(buf.AsSpan(pos), record.PrivateEndpoint); pos += 6;
            BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(pos), record.Expiry.ToUnixTimeSeconds());
            return buf;
        }

        /// <summary>Signs a record with the publisher's identity, returning the
        /// full published blob (signed portion || 64-byte Ed25519 signature).</summary>
        public static byte[] SignRecord(PresenceRecord record, PeerIdentity identity)
        {
            var portion = EncodeSignedPortion(record);
            var sig = identity.Sign(portion);
            var blob = new byte[portion.Length + sig.Length];
            portion.CopyTo(blob, 0);
            sig.CopyTo(blob, portion.Length);
            return blob;
        }

        /// <summary>Verifies and decodes a published blob. Returns false on a
        /// version mismatch, a bad length, or a signature that does not verify
        /// against the record's OWN public key. The caller must still check that
        /// this public key's fingerprint matches the identity it expected (an
        /// entry can be internally valid yet belong to the wrong peer): use
        /// <paramref name="expectedFingerprint"/> to enforce that here.</summary>
        public static bool TryVerifyRecord(
            byte[] blob, out PresenceRecord record, byte[] expectedFingerprint = null)
        {
            record = null;
            int signedLen = 1 + PeerCrypto.KeySize + 6 + 6 + 8;
            if (blob == null || blob.Length != signedLen + PeerCrypto.SignatureSize) return false;
            if (blob[0] != Version) return false;

            var portion = blob.AsSpan(0, signedLen).ToArray();
            var sig = blob.AsSpan(signedLen).ToArray();

            int pos = 1;
            var pub = blob.AsSpan(pos, PeerCrypto.KeySize).ToArray(); pos += PeerCrypto.KeySize;

            if (!PeerCrypto.Ed25519Verify(pub, portion, sig)) return false;

            var fp = PeerCrypto.Fingerprint(pub);
            if (expectedFingerprint != null &&
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(fp, expectedFingerprint))
                return false;

            var pubEp = ReadEndpoint(blob.AsSpan(pos)); pos += 6;
            var privEp = ReadEndpoint(blob.AsSpan(pos)); pos += 6;
            long expiry = BinaryPrimitives.ReadInt64BigEndian(blob.AsSpan(pos));

            record = new PresenceRecord
            {
                Fingerprint = fp,
                PublicKey = pub,
                PublicEndpoint = pubEp,
                PrivateEndpoint = privEp,
                Expiry = DateTimeOffset.FromUnixTimeSeconds(expiry),
            };
            return true;
        }

        public static string ToBase64(byte[] blob) => Convert.ToBase64String(blob);
        public static byte[] FromBase64(string s)
        {
            try { return Convert.FromBase64String(s); } catch { return null; }
        }

        // An all-zero 6 bytes encodes "no endpoint" (address 0.0.0.0 / port 0).
        private static void WriteEndpoint(Span<byte> dst, IPEndPoint ep)
        {
            if (ep == null) { dst.Slice(0, 6).Clear(); return; }
            ep.Address.MapToIPv4().GetAddressBytes().CopyTo(dst);
            BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(4), (ushort)ep.Port);
        }

        private static IPEndPoint ReadEndpoint(ReadOnlySpan<byte> src)
        {
            uint addr = BinaryPrimitives.ReadUInt32BigEndian(src);
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(4));
            if (addr == 0 && port == 0) return null;
            var ip = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ip, addr);
            return new IPEndPoint(new IPAddress(ip), port);
        }
    }
}
