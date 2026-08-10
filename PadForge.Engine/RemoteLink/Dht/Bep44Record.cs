using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Engine.RemoteLink.Dht
{
    /// <summary>
    /// BEP 44 mutable-item construction for the DHT presence store (#294).
    /// Everything here is byte-exact against the spec's official test vectors
    /// (Bep44RecordTests reproduces target 4a533d47… and 411eba73… and verifies
    /// the published signatures), because the target derivation and the signed
    /// preimage are the load-bearing correctness of the whole presence lane.
    ///
    /// - Target (the 20-byte DHT storage key): SHA1(publicKey [|| salt]).
    /// - Signed preimage: the bencoded, order-fixed concatenation
    ///     ["4:salt" &lt;len&gt;:&lt;salt&gt;] "3:seqi" &lt;seq&gt; "e1:v" &lt;len&gt;:&lt;value&gt;
    ///   i.e. the salt (if present) then seq then value, each in bencode form,
    ///   with NO surrounding dictionary. The Ed25519 signature covers exactly
    ///   these bytes.
    /// - Value: &lt;= 1000 bytes bencoded (the storage-node cap).
    /// </summary>
    public static class Bep44Record
    {
        public const int MaxValueBytes = 1000;
        public const int MaxSaltBytes = 64;

        /// <summary>SHA1(publicKey [|| salt]), the mutable item's DHT target.
        /// Raw key bytes, never hex text (Codex trap #1).</summary>
        public static byte[] ComputeTarget(byte[] publicKey, byte[] salt = null)
        {
            if (publicKey == null || publicKey.Length != 32)
                throw new ArgumentException("BEP 44 key must be 32 bytes.", nameof(publicKey));
            if (salt != null && salt.Length > MaxSaltBytes)
                throw new ArgumentException($"Salt exceeds {MaxSaltBytes} bytes.", nameof(salt));
            using var sha1 = SHA1.Create();
            if (salt == null || salt.Length == 0) return sha1.ComputeHash(publicKey);
            var buf = new byte[publicKey.Length + salt.Length];
            publicKey.CopyTo(buf, 0);
            salt.CopyTo(buf, publicKey.Length);
            return sha1.ComputeHash(buf);
        }

        /// <summary>The exact bytes an Ed25519 signature must cover
        /// (BEP 44 "the buffer to sign"). Salt is omitted entirely when null/empty.</summary>
        public static byte[] BuildSignaturePreimage(byte[] value, long seq, byte[] salt = null)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Length > MaxValueBytes)
                throw new ArgumentException($"Value exceeds {MaxValueBytes} bytes.", nameof(value));
            if (salt != null && salt.Length > MaxSaltBytes)
                throw new ArgumentException($"Salt exceeds {MaxSaltBytes} bytes.", nameof(salt));

            using var ms = new MemoryStream();
            void Ascii(string s) { foreach (char c in s) ms.WriteByte((byte)c); }

            if (salt != null && salt.Length > 0)
            {
                Ascii("4:salt");
                Ascii(salt.Length.ToString());
                ms.WriteByte((byte)':');
                ms.Write(salt, 0, salt.Length);
            }
            Ascii("3:seqi");
            Ascii(seq.ToString());
            Ascii("e1:v");
            Ascii(value.Length.ToString());
            ms.WriteByte((byte)':');
            ms.Write(value, 0, value.Length);
            return ms.ToArray();
        }

        /// <summary>Signs a mutable item. Returns the 64-byte Ed25519 signature
        /// over the canonical preimage.</summary>
        public static byte[] Sign(byte[] privateKey, byte[] value, long seq, byte[] salt = null)
            => PeerCrypto.Ed25519Sign(privateKey, BuildSignaturePreimage(value, seq, salt));

        /// <summary>Verifies a mutable item's signature against its own public
        /// key. The caller MUST also confirm the key hashes to the requested
        /// target and matches the pinned identity (Codex trap #1).</summary>
        public static bool Verify(byte[] publicKey, byte[] value, long seq, byte[] signature, byte[] salt = null)
            => PeerCrypto.Ed25519Verify(publicKey, BuildSignaturePreimage(value, seq, salt), signature);
    }
}
