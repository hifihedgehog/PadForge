using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Engine.RemoteLink.Dht
{
    /// <summary>
    /// First-contact signalling over the public BitTorrent DHT (#294).
    ///
    /// WHY THIS EXISTS. A NAT hole punch needs BOTH peers to send toward each
    /// other at roughly the same moment. An idle host cannot simply "listen":
    /// the caller's probes arrive at the host's router, which has never sent
    /// anything to the caller and therefore drops them, so the host never
    /// learns anyone is calling and never fires back. Neither side can go
    /// first. That deadlock is invisible on a LAN (no filtering) and total
    /// across the internet, which is exactly the field failure: LAN worked,
    /// Verizon-to-Comcast reported "0 inbound probes" on every attempt.
    ///
    /// The missing piece is a signalling channel that carries the CALLER'S
    /// address to the host. The DHT is that channel: free, third-party, and
    /// operator-independent (measured reachable from a Verizon hotspot).
    ///
    /// HOW THE SLOT IS DERIVED. Both sides already share one secret: the
    /// host's connection code. Everything is derived from it deterministically,
    /// so no server assigns anything:
    ///   seed  = HKDF(code, "…/seed/v1") -> Ed25519 private key (BEP 44 signer)
    ///   salt  = HKDF(code, "…/salt/v1") -> BEP 44 salt
    ///   aead  = HKDF(code, "…/aead/v1") -> ChaCha20-Poly1305 key
    ///   target = SHA1(pub || salt)      -> the DHT address both compute
    /// The caller PUTs an encrypted "I am calling you, here are my addresses"
    /// record there; the host GETs its own slot on a timer and punches back.
    ///
    /// SECURITY. Whoever holds the code can read and write this slot, which is
    /// correct: the code IS the invitation. The record is encrypted so DHT
    /// nodes never see endpoints, and the code is never published (only values
    /// derived through HKDF). Reachability is all this establishes. Identity is
    /// still proven by the unchanged SAS + Ed25519 handshake, so a slot squatter
    /// gains nothing beyond knowing someone is dialling.
    /// </summary>
    public static class CodeRendezvous
    {
        private static readonly byte[] SeedInfo = Encoding.ASCII.GetBytes("PadForge/code-rdv/seed/v1");
        private static readonly byte[] SaltInfo = Encoding.ASCII.GetBytes("PadForge/code-rdv/salt/v1");
        private static readonly byte[] AeadInfo = Encoding.ASCII.GetBytes("PadForge/code-rdv/aead/v1");

        private const byte Version = 1;
        private const int NonceLen = 12;
        private const int PrefixLen = 8;

        /// <summary>The deterministic DHT slot a code addresses.</summary>
        public sealed class Slot
        {
            public byte[] PrivateKey { get; init; }
            public byte[] PublicKey { get; init; }
            public byte[] Salt { get; init; }
            public byte[] AeadKey { get; init; }
            /// <summary>SHA1(PublicKey || Salt): where the record lives.</summary>
            public byte[] Target => Bep44Record.ComputeTarget(PublicKey, Salt);
        }

        /// <summary>Derives the slot both peers compute from the same code.
        /// Normalised first, so grouping dashes and confusable glyphs in a
        /// retyped code still land on the identical slot.</summary>
        public static Slot DeriveSlot(string code)
        {
            string norm = LinkCode.Normalize(code);
            if (string.IsNullOrEmpty(norm)) return null;
            var ikm = Encoding.ASCII.GetBytes(norm);
            var seed = PeerCrypto.DeriveKey(ikm, salt: null, SeedInfo, 32);
            return new Slot
            {
                PrivateKey = seed,
                PublicKey = PeerCrypto.DeriveEd25519PublicKey(seed),
                Salt = PeerCrypto.DeriveKey(ikm, salt: null, SaltInfo, 20),
                AeadKey = PeerCrypto.DeriveKey(ikm, salt: null, AeadInfo, 32),
            };
        }

        /// <summary>A caller's announcement: who is dialling and where to reach
        /// them.</summary>
        public sealed class CallRequest
        {
            public byte[] CallerFingerprintPrefix { get; init; }
            public IReadOnlyList<IPEndPoint> Candidates { get; init; }
            public DateTimeOffset IssuedAt { get; init; }
            /// <summary>Stale requests are ignored so an old record cannot make
            /// a host punch at an address nobody is listening on.</summary>
            public bool IsFresh(DateTimeOffset now, TimeSpan window)
                => now - IssuedAt <= window && IssuedAt - now <= TimeSpan.FromMinutes(2);
        }

        /// <summary>Encrypts a call request into a BEP 44 value. The sequence is
        /// bound as associated data, so a record cannot be replayed under a
        /// different seq.</summary>
        public static byte[] EncodeRequest(Slot slot, byte[] callerFingerprint,
            IReadOnlyList<IPEndPoint> candidates, DateTimeOffset issuedAt, long seq)
        {
            var plain = new List<byte> { Version };
            var stamp = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(stamp, issuedAt.ToUnixTimeSeconds());
            plain.AddRange(stamp);
            for (int i = 0; i < PrefixLen; i++)
                plain.Add(callerFingerprint != null && i < callerFingerprint.Length ? callerFingerprint[i] : (byte)0);

            var list = candidates ?? Array.Empty<IPEndPoint>();
            byte count = (byte)Math.Min(list.Count, 16);
            plain.Add(count);
            for (int i = 0; i < count; i++)
            {
                var ipBytes = list[i].Address.GetAddressBytes();
                plain.Add((byte)ipBytes.Length);
                plain.AddRange(ipBytes);
                var port = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)list[i].Port);
                plain.AddRange(port);
            }

            var nonce = new byte[NonceLen];
            RandomNumberGenerator.Fill(nonce);
            var ct = PeerCrypto.Seal(slot.AeadKey, nonce, Aad(slot, seq), plain.ToArray());
            var value = new byte[NonceLen + ct.Length];
            nonce.CopyTo(value, 0);
            ct.CopyTo(value, NonceLen);
            if (value.Length > Bep44Record.MaxValueBytes)
                throw new InvalidOperationException("Call request exceeds the BEP 44 value cap.");
            return value;
        }

        /// <summary>Decrypts a call request. False on any AEAD failure (wrong
        /// code, tamper, replayed seq) or malformed content.</summary>
        public static bool TryDecodeRequest(Slot slot, byte[] value, long seq, out CallRequest request)
        {
            request = null;
            if (slot == null || value == null || value.Length < NonceLen + 16) return false;
            var nonce = value.AsSpan(0, NonceLen).ToArray();
            var ct = value.AsSpan(NonceLen).ToArray();
            if (!PeerCrypto.Open(slot.AeadKey, nonce, Aad(slot, seq), ct, out var plain)) return false;
            try
            {
                int o = 0;
                if (plain.Length < 1 + 8 + PrefixLen + 1 || plain[o++] != Version) return false;
                long stamp = BinaryPrimitives.ReadInt64BigEndian(plain.AsSpan(o)); o += 8;
                var prefix = plain.AsSpan(o, PrefixLen).ToArray(); o += PrefixLen;
                int count = plain[o++];
                var eps = new List<IPEndPoint>(count);
                for (int i = 0; i < count; i++)
                {
                    int len = plain[o++];
                    if (len != 4 && len != 16) return false;
                    if (o + len + 2 > plain.Length) return false;
                    var ip = new IPAddress(plain.AsSpan(o, len).ToArray()); o += len;
                    ushort port = BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(o)); o += 2;
                    eps.Add(new IPEndPoint(ip, port));
                }
                request = new CallRequest
                {
                    CallerFingerprintPrefix = prefix,
                    Candidates = eps,
                    IssuedAt = DateTimeOffset.FromUnixTimeSeconds(stamp),
                };
                return true;
            }
            catch { return false; }
        }

        private static byte[] Aad(Slot slot, long seq)
        {
            var target = slot.Target;
            var aad = new byte[1 + 8 + target.Length];
            aad[0] = Version;
            BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(1), seq);
            target.CopyTo(aad, 9);
            return aad;
        }

        /// <summary>Sequence for a call record: unix seconds, so a later call
        /// always supersedes an earlier one on the storing nodes.</summary>
        public static long SequenceFor(DateTimeOffset issuedAt) => issuedAt.ToUnixTimeSeconds();
    }
}
