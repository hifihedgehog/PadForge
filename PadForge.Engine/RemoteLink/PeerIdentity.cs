using System;
using System.Security.Cryptography;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// This instance's long-lived cryptographic identity (issue #138): a static
    /// Ed25519 keypair generated once and pinned by every peer. The public key's
    /// SHA-256 is the transport-independent fingerprint a peer trusts — never an
    /// IP or a self-asserted name. Used to sign the per-handshake transcript so
    /// presence in a trust list alone is never treated as authentication.
    ///
    /// The private key is secret and must be stored encrypted at rest (DPAPI) by
    /// the persistence layer; this type only generates and uses it in memory.
    /// </summary>
    public sealed class PeerIdentity
    {
        private readonly byte[] _privateKey;

        public PeerIdentity(byte[] privateKey, byte[] publicKey)
        {
            if (privateKey == null || privateKey.Length != PeerCrypto.KeySize)
                throw new ArgumentException("Bad Ed25519 private key.", nameof(privateKey));
            if (publicKey == null || publicKey.Length != PeerCrypto.KeySize)
                throw new ArgumentException("Bad Ed25519 public key.", nameof(publicKey));
            _privateKey = (byte[])privateKey.Clone();
            PublicKey = (byte[])publicKey.Clone();
            Fingerprint = PeerCrypto.Fingerprint(PublicKey);
            FingerprintHex = Convert.ToHexString(Fingerprint);
        }

        /// <summary>Generate a fresh identity (first launch).</summary>
        public static PeerIdentity Generate()
        {
            var kp = PeerCrypto.GenerateEd25519KeyPair();
            return new PeerIdentity(kp.PrivateKey, kp.PublicKey);
        }

        /// <summary>32-byte Ed25519 public key — shared with peers, pinned by them.</summary>
        public byte[] PublicKey { get; }

        /// <summary>SHA-256 of the public key — the durable peer identity.</summary>
        public byte[] Fingerprint { get; }

        /// <summary>Uppercase hex of <see cref="Fingerprint"/>.</summary>
        public string FingerprintHex { get; }

        /// <summary>Sign a message (a fresh per-handshake transcript hash) with the static key.</summary>
        public byte[] Sign(ReadOnlySpan<byte> message) => PeerCrypto.Ed25519Sign(_privateKey, message);

        /// <summary>Copy of the private key for encrypted persistence. Zeroize the copy after use.</summary>
        public byte[] ExportPrivateKey() => (byte[])_privateKey.Clone();
    }
}
