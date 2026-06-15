using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
// The in-box System.Security.Cryptography.ChaCha20Poly1305 collides by name with
// the Bouncy Castle managed one and is gated to Windows 11 (build 22000+). Alias
// the BC type so this stays Win10-safe and there is no ambiguous reference.
using BcChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Primitive crypto layer for the Remote Link pairing/transport stack
    /// (issue #138). Wraps a single pinned suite — X25519 key agreement,
    /// Ed25519 static-key authentication, ChaCha20-Poly1305 AEAD, HKDF-SHA256
    /// key schedule — behind a small surface so the handshake and transport
    /// layers never touch a curve or cipher type directly.
    ///
    /// Pinned by design: no cipher negotiation, no crypto-agility. X25519 and
    /// Ed25519 come from Bouncy Castle (MIT, pure-managed, no OS gate). HKDF,
    /// SHA-256, and the CSPRNG are in-box. ChaCha20-Poly1305 is Bouncy Castle's
    /// managed implementation specifically so it runs on the app's Windows 10
    /// 1809 floor, where the in-box one throws PlatformNotSupportedException.
    ///
    /// All round-trips (agreement match, sign/verify with tamper rejection,
    /// seal/open with ciphertext/AAD/nonce tamper rejection) were validated
    /// before this landed.
    /// </summary>
    public static class PeerCrypto
    {
        /// <summary>Length of an X25519 or Ed25519 key, and of a SHA-256 fingerprint (bytes).</summary>
        public const int KeySize = 32;

        /// <summary>Ed25519 signature length (bytes).</summary>
        public const int SignatureSize = 64;

        /// <summary>ChaCha20-Poly1305 nonce length (bytes). 96-bit, fixed.</summary>
        public const int NonceSize = 12;

        /// <summary>ChaCha20-Poly1305 authentication tag length (bytes). 128-bit.</summary>
        public const int TagSize = 16;

        private const int TagBits = TagSize * 8;

        /// <summary>An asymmetric keypair as raw little-endian byte arrays.</summary>
        public readonly struct KeyPair
        {
            public KeyPair(byte[] privateKey, byte[] publicKey)
            {
                PrivateKey = privateKey;
                PublicKey = publicKey;
            }

            /// <summary>32-byte private scalar. Treat as secret; zeroize when done.</summary>
            public byte[] PrivateKey { get; }

            /// <summary>32-byte public key. Not secret.</summary>
            public byte[] PublicKey { get; }
        }

        // ── Random ──────────────────────────────────────────────────────────

        /// <summary>Cryptographically secure random bytes (in-box CSPRNG).</summary>
        public static byte[] RandomBytes(int count)
        {
            var buf = new byte[count];
            RandomNumberGenerator.Fill(buf);
            return buf;
        }

        // ── X25519 (key agreement) ──────────────────────────────────────────

        /// <summary>Generate an X25519 keypair for ECDH (ephemeral or static).</summary>
        public static KeyPair GenerateX25519KeyPair()
        {
            var gen = new X25519KeyPairGenerator();
            gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
            var kp = gen.GenerateKeyPair();
            return new KeyPair(
                ((X25519PrivateKeyParameters)kp.Private).GetEncoded(),
                ((X25519PublicKeyParameters)kp.Public).GetEncoded());
        }

        /// <summary>
        /// X25519 Diffie-Hellman: combine our private key with the peer's public
        /// key into a 32-byte shared secret. Symmetric — both peers reach the
        /// same value from the opposite pair. Feed the result through
        /// <see cref="DeriveKey"/> before use; never use the raw DH output as a key.
        /// </summary>
        public static byte[] X25519Agree(byte[] privateKey, byte[] peerPublicKey)
        {
            RequireLength(privateKey, KeySize, nameof(privateKey));
            RequireLength(peerPublicKey, KeySize, nameof(peerPublicKey));
            var agreement = new X25519Agreement();
            agreement.Init(new X25519PrivateKeyParameters(privateKey, 0));
            var secret = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(new X25519PublicKeyParameters(peerPublicKey, 0), secret, 0);
            return secret;
        }

        // ── Ed25519 (static-key authentication) ─────────────────────────────

        /// <summary>Generate an Ed25519 identity keypair for signing handshake nonces.</summary>
        public static KeyPair GenerateEd25519KeyPair()
        {
            var gen = new Ed25519KeyPairGenerator();
            gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var kp = gen.GenerateKeyPair();
            return new KeyPair(
                ((Ed25519PrivateKeyParameters)kp.Private).GetEncoded(),
                ((Ed25519PublicKeyParameters)kp.Public).GetEncoded());
        }

        /// <summary>Derive the Ed25519 public key from a 32-byte private seed. The public is
        /// fully determined by the private, so a stored identity whose public field is lost can
        /// be healed without changing its fingerprint (#138 F26).</summary>
        public static byte[] DeriveEd25519PublicKey(byte[] privateKey)
        {
            RequireLength(privateKey, KeySize, nameof(privateKey));
            return new Ed25519PrivateKeyParameters(privateKey, 0).GeneratePublicKey().GetEncoded();
        }

        /// <summary>Sign a message with an Ed25519 private key. Returns a 64-byte signature.</summary>
        public static byte[] Ed25519Sign(byte[] privateKey, ReadOnlySpan<byte> message)
        {
            RequireLength(privateKey, KeySize, nameof(privateKey));
            var signer = new Ed25519Signer();
            signer.Init(true, new Ed25519PrivateKeyParameters(privateKey, 0));
            var msg = message.ToArray();
            signer.BlockUpdate(msg, 0, msg.Length);
            return signer.GenerateSignature();
        }

        /// <summary>
        /// Verify an Ed25519 signature against the signer's pinned public key.
        /// Returns false on any mismatch (tampered message, tampered signature,
        /// wrong key) and never throws on a bad signature.
        /// </summary>
        public static bool Ed25519Verify(byte[] publicKey, ReadOnlySpan<byte> message, byte[] signature)
        {
            if (publicKey == null || publicKey.Length != KeySize) return false;
            if (signature == null || signature.Length != SignatureSize) return false;
            try
            {
                var verifier = new Ed25519Signer();
                verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
                var msg = message.ToArray();
                verifier.BlockUpdate(msg, 0, msg.Length);
                return verifier.VerifySignature(signature);
            }
            catch
            {
                return false;
            }
        }

        // ── Key schedule ────────────────────────────────────────────────────

        /// <summary>
        /// HKDF-SHA256 (in-box). Expands a DH shared secret (or any input keying
        /// material) into a fixed-length symmetric key bound to a context string.
        /// </summary>
        public static byte[] DeriveKey(byte[] inputKeyMaterial, byte[] salt, ReadOnlySpan<byte> info, int length = KeySize)
        {
            RequireNonEmpty(inputKeyMaterial, nameof(inputKeyMaterial));
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, length, salt, info.ToArray());
        }

        // ── AEAD (per-frame seal/open) ──────────────────────────────────────

        /// <summary>
        /// Seal a plaintext with ChaCha20-Poly1305. The 16-byte tag is appended,
        /// so the result is <c>plaintext.Length + 16</c> bytes. The nonce MUST be
        /// unique per key (the transport supplies a monotonic counter — see
        /// <see cref="BuildNonce"/>); reuse under a fixed key is catastrophic.
        /// <paramref name="associatedData"/> is authenticated but not encrypted
        /// (the frame header).
        /// </summary>
        public static byte[] Seal(byte[] key, byte[] nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
        {
            RequireLength(key, KeySize, nameof(key));
            RequireLength(nonce, NonceSize, nameof(nonce));
            var cipher = new BcChaCha20Poly1305();
            cipher.Init(true, new AeadParameters(new KeyParameter(key), TagBits, nonce, associatedData.ToArray()));
            var pt = plaintext.ToArray();
            var output = new byte[cipher.GetOutputSize(pt.Length)];
            int written = cipher.ProcessBytes(pt, 0, pt.Length, output, 0);
            cipher.DoFinal(output, written);
            return output;
        }

        /// <summary>
        /// Open a ChaCha20-Poly1305 ciphertext. Returns false (and an empty
        /// plaintext) on any authentication failure — tampered ciphertext, wrong
        /// AAD, wrong nonce, or wrong key — and never throws on a forgery. A
        /// caller advances its anti-replay window only after this returns true.
        /// </summary>
        public static bool Open(byte[] key, byte[] nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext, out byte[] plaintext)
        {
            plaintext = Array.Empty<byte>();
            if (key == null || key.Length != KeySize) return false;
            if (nonce == null || nonce.Length != NonceSize) return false;
            if (ciphertext.Length < TagSize) return false;
            try
            {
                var cipher = new BcChaCha20Poly1305();
                cipher.Init(false, new AeadParameters(new KeyParameter(key), TagBits, nonce, associatedData.ToArray()));
                var ct = ciphertext.ToArray();
                var output = new byte[cipher.GetOutputSize(ct.Length)];
                int written = cipher.ProcessBytes(ct, 0, ct.Length, output, 0);
                written += cipher.DoFinal(output, written);
                plaintext = written == output.Length ? output : output[..written];
                return true;
            }
            catch
            {
                // AEAD tag mismatch (InvalidCipherTextException) or malformed input.
                plaintext = Array.Empty<byte>();
                return false;
            }
        }

        /// <summary>
        /// Build a 12-byte AEAD nonce from a per-direction salt and a monotonic
        /// counter: <c>[salt u32 LE][counter u64 LE]</c>. Disjoint nonce space per
        /// direction comes from a distinct <paramref name="directionSalt"/> on each
        /// side; uniqueness within a session comes from the never-reset counter.
        /// Rekey before the counter wraps.
        /// </summary>
        public static byte[] BuildNonce(uint directionSalt, ulong counter)
        {
            var nonce = new byte[NonceSize];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(0), directionSalt);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
            return nonce;
        }

        // ── Identity fingerprint ────────────────────────────────────────────

        /// <summary>
        /// SHA-256 fingerprint of a static public key — the transport-independent
        /// peer identity. SHA-256, not MD5: this gates trust, so it must be
        /// collision-resistant (the MD5 device-GUID helpers elsewhere are for
        /// non-security device indexing only).
        /// </summary>
        public static byte[] Fingerprint(byte[] publicKey)
        {
            RequireLength(publicKey, KeySize, nameof(publicKey));
            return SHA256.HashData(publicKey);
        }

        /// <summary>
        /// Short human-comparison string of a fingerprint: groups of the
        /// uppercase hex, for the pairing UI. Display only — never compared in code.
        /// </summary>
        public static string FingerprintToDisplay(byte[] fingerprint, int groups = 8)
        {
            RequireLength(fingerprint, KeySize, nameof(fingerprint));
            string hex = Convert.ToHexString(fingerprint);
            var sb = new System.Text.StringBuilder(hex.Length + groups);
            for (int i = 0; i < hex.Length; i += 4)
            {
                if (i > 0 && i % 4 == 0 && sb.Length > 0) sb.Append(' ');
                sb.Append(hex, i, Math.Min(4, hex.Length - i));
                if (--groups <= 0) break;
            }
            return sb.ToString().Trim();
        }

        // ── Hygiene ─────────────────────────────────────────────────────────

        /// <summary>Overwrite secret bytes in place. Call when a private key or session key is retired.</summary>
        public static void Zeroize(byte[] secret)
        {
            if (secret != null) CryptographicOperations.ZeroMemory(secret);
        }

        /// <summary>Constant-time equality for comparing tags/fingerprints without leaking via timing.</summary>
        public static bool FixedTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
            => CryptographicOperations.FixedTimeEquals(a, b);

        private static void RequireLength(byte[] value, int expected, string name)
        {
            if (value == null) throw new ArgumentNullException(name);
            if (value.Length != expected) throw new ArgumentException($"Expected {expected} bytes, got {value.Length}.", name);
        }

        private static void RequireNonEmpty(byte[] value, string name)
        {
            if (value == null) throw new ArgumentNullException(name);
            if (value.Length == 0) throw new ArgumentException("Must not be empty.", name);
        }
    }
}
