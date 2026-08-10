using System;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Remote Link connection codes (#294). Two shapes, one field:
    ///
    /// - A SHORT code (~8 chars) is a single-use rendezvous key: an opaque
    ///   random token the introducer maps to the receiver's current signed
    ///   endpoints. It carries no address itself, so it survives the receiver
    ///   moving networks before the dialer redeems it.
    /// - A LONG self-contained code (~42 chars, grouped in fives) embeds the receiver's candidate
    ///   endpoints (public-from-STUN and private-LAN), a fingerprint PREFIX for
    ///   an early identity-mismatch abort, and an expiry. It needs no server:
    ///   two users swap it over any chat and punch directly.
    ///
    /// The code is NOT a secret and authenticates no one. The existing SAS
    /// ceremony and Ed25519 mutual handshake gate trust on first contact
    /// (LinkHandshake), so a tampered long code simply fails to reach a peer or
    /// fails the handshake. The fingerprint prefix only enables the early abort.
    ///
    /// Alphabet: Crockford base32 (RFC-style, no I/L/O/U) so codes are
    /// case-insensitive, unambiguous when read aloud, and word-safe. Parsing
    /// maps the visually-confusable letters back (I/L -> 1, O -> 0).
    /// </summary>
    public static class LinkCode
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const byte Version = 1;
        // A host:port or bare host has a dot or a colon; a code never does. This
        // is how the Connect field tells the two apart (issue: "the field also
        // accepts a short code").
        public const int ShortCodeLength = 8;

        /// <summary>The 16-byte shared punch nonce for a two-way code exchange,
        /// derived from BOTH peers' fingerprint prefixes in sorted order so each
        /// side (which holds its own full fingerprint and the other's 8-byte
        /// prefix from the code) computes the identical value. Authenticates the
        /// punch probes; the handshake still gates trust.</summary>
        public static byte[] TwoWayPunchNonce(byte[] selfFingerprint, byte[] peerFingerprintPrefix)
        {
            byte[] a = selfFingerprint.Length >= 8 ? selfFingerprint[..8] : selfFingerprint;
            byte[] b = peerFingerprintPrefix.Length >= 8 ? peerFingerprintPrefix[..8] : peerFingerprintPrefix;
            byte[] lo, hi;
            if (CompareBytes(a, b) <= 0) { lo = a; hi = b; } else { lo = b; hi = a; }
            var ikm = new byte[16];
            lo.CopyTo(ikm, 0);
            hi.CopyTo(ikm, 8);
            var info = System.Text.Encoding.ASCII.GetBytes("PadForge/code-punch/v1");
            return PeerCrypto.DeriveKey(ikm, null, info, 16);
        }

        /// <summary>True when THIS peer should lead the handshake against a peer
        /// with the given fingerprint prefix: the lexicographically smaller
        /// fingerprint initiates, so the two sides never both lead or both
        /// follow. Both compute it consistently.</summary>
        public static bool IsHandshakeInitiator(byte[] selfFingerprint, byte[] peerFingerprintPrefix)
        {
            byte[] a = selfFingerprint.Length >= 8 ? selfFingerprint[..8] : selfFingerprint;
            byte[] b = peerFingerprintPrefix.Length >= 8 ? peerFingerprintPrefix[..8] : peerFingerprintPrefix;
            return CompareBytes(a, b) < 0;
        }

        private static int CompareBytes(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) { int d = a[i] - b[i]; if (d != 0) return d; }
            return a.Length - b.Length;
        }

        /// <summary>Mints a fresh single-use short rendezvous code
        /// (<see cref="ShortCodeLength"/> Crockford chars, ~40 bits). Random,
        /// opaque, no structure the dialer can forge into another peer's slot.</summary>
        public static string MintShortCode()
        {
            Span<byte> raw = stackalloc byte[5]; // 40 bits -> exactly 8 base32 chars
            RandomNumberGenerator.Fill(raw);
            return EncodeBase32(raw);
        }

        /// <summary>True when the trimmed field content is a code (as opposed to
        /// a host, host:port, or IP). Codes are pure base32; addresses carry a
        /// dot or colon.</summary>
        public static bool LooksLikeCode(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.IndexOf('.') >= 0 || s.IndexOf(':') >= 0) return false;
            foreach (char c in s)
            {
                if (c == '-' || c == ' ') continue; // grouping separators allowed
                if (DecodeChar(c) < 0) return false;
            }
            return true;
        }

        /// <summary>True when the code is a self-contained long code (decodes to
        /// a valid endpoint payload), false for a short rendezvous key.</summary>
        public static bool IsSelfContained(string code)
            => TryParseSelfContained(code, out _);

        /// <summary>
        /// Encodes a self-contained code from the receiver's candidates.
        /// Payload (little-endian, then base32): version(1) | flags(1) |
        /// publicIp(4) publicPort(2) | privateIp(4) privatePort(2) |
        /// fingerprintPrefix(8) | expiryUnixMinutes(4). 26 bytes -> 42 base32
        /// chars, grouped in fives with dashes for readability.
        /// </summary>
        public static string EncodeSelfContained(
            IPEndPoint publicEndpoint, IPEndPoint privateEndpoint,
            byte[] fingerprint, DateTimeOffset expiry)
        {
            if (fingerprint == null || fingerprint.Length < 8)
                throw new ArgumentException("Fingerprint must be at least 8 bytes.", nameof(fingerprint));

            var buf = new byte[26];
            buf[0] = Version;
            byte flags = 0;
            if (publicEndpoint != null) flags |= 0x01;
            if (privateEndpoint != null) flags |= 0x02;
            buf[1] = flags;
            WriteEndpoint(buf.AsSpan(2), publicEndpoint);
            WriteEndpoint(buf.AsSpan(8), privateEndpoint);
            fingerprint.AsSpan(0, 8).CopyTo(buf.AsSpan(14));
            long minutes = expiry.ToUnixTimeSeconds() / 60;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(22), (uint)minutes);

            return Group(EncodeBase32(buf));
        }

        public sealed class SelfContainedCode
        {
            public IPEndPoint PublicEndpoint { get; init; }
            public IPEndPoint PrivateEndpoint { get; init; }
            /// <summary>First 8 bytes of the receiver's Ed25519 fingerprint,
            /// for an early identity-mismatch abort. The full handshake still
            /// proves identity.</summary>
            public byte[] FingerprintPrefix { get; init; }
            public DateTimeOffset Expiry { get; init; }
            public bool IsExpired(DateTimeOffset now) => now > Expiry;
        }

        /// <summary>Parses a self-contained long code. Returns false for a short
        /// rendezvous key, a malformed code, or a version mismatch.</summary>
        public static bool TryParseSelfContained(string code, out SelfContainedCode parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(code)) return false;
            if (!TryDecodeBase32(Ungroup(code), out var buf)) return false;
            if (buf.Length != 26 || buf[0] != Version) return false;

            byte flags = buf[1];
            var pub = (flags & 0x01) != 0 ? ReadEndpoint(buf.AsSpan(2)) : null;
            var priv = (flags & 0x02) != 0 ? ReadEndpoint(buf.AsSpan(8)) : null;
            var fpPrefix = buf.AsSpan(14, 8).ToArray();
            uint minutes = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(22));
            parsed = new SelfContainedCode
            {
                PublicEndpoint = pub,
                PrivateEndpoint = priv,
                FingerprintPrefix = fpPrefix,
                Expiry = DateTimeOffset.FromUnixTimeSeconds((long)minutes * 60),
            };
            return true;
        }

        /// <summary>Normalizes a user-typed code: strips grouping, uppercases,
        /// maps confusable glyphs. Returns null if any character is invalid.</summary>
        public static string Normalize(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var sb = new System.Text.StringBuilder(code.Length);
            foreach (char c in code)
            {
                if (c == '-' || c == ' ') continue;
                int v = DecodeChar(c);
                if (v < 0) return null;
                sb.Append(Alphabet[v]);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        // ── endpoint pack ──

        private static void WriteEndpoint(Span<byte> dst, IPEndPoint ep)
        {
            if (ep == null) { dst.Slice(0, 6).Clear(); return; }
            var ip = ep.Address.MapToIPv4().GetAddressBytes();
            ip.CopyTo(dst);
            BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(4), (ushort)ep.Port);
        }

        private static IPEndPoint ReadEndpoint(ReadOnlySpan<byte> src)
        {
            var ip = new IPAddress(src.Slice(0, 4).ToArray());
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(4));
            return new IPEndPoint(ip, port);
        }

        // ── Crockford base32 ──

        private static string EncodeBase32(ReadOnlySpan<byte> data)
        {
            var sb = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    bits -= 5;
                    sb.Append(Alphabet[(buffer >> bits) & 0x1F]);
                }
            }
            if (bits > 0)
                sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
            return sb.ToString();
        }

        private static bool TryDecodeBase32(string s, out byte[] data)
        {
            data = null;
            if (string.IsNullOrEmpty(s)) return false;
            int buffer = 0, bits = 0;
            var outBytes = new System.Collections.Generic.List<byte>(s.Length * 5 / 8 + 1);
            foreach (char c in s)
            {
                int v = DecodeChar(c);
                if (v < 0) return false;
                buffer = (buffer << 5) | v;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    outBytes.Add((byte)((buffer >> bits) & 0xFF));
                }
            }
            data = outBytes.ToArray();
            return true;
        }

        private static int DecodeChar(char c)
        {
            c = char.ToUpperInvariant(c);
            switch (c)
            {
                case 'I': case 'L': return 1; // confusable with 1
                case 'O': return 0;           // confusable with 0
            }
            int idx = Alphabet.IndexOf(c);
            return idx; // -1 when invalid
        }

        private static string Group(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length + s.Length / 5);
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && i % 5 == 0) sb.Append('-');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static string Ungroup(string s)
        {
            var norm = Normalize(s);
            return norm ?? string.Empty;
        }
    }
}
