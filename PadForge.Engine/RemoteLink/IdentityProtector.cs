using System;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>How the instance's static private identity key is protected at rest
    /// (issue #138). The public key and the trust list are never secret and stay in
    /// the clear; only the private key is wrapped.</summary>
    public enum IdentityProtectionMode : byte
    {
        /// <summary>DPAPI scoped to the MACHINE (not the account): every Windows user on
        /// THIS PC can use the identity — so a shared family PC works — but it can't be
        /// decrypted on another machine. Default. Not portable.</summary>
        Secure = 1,

        /// <summary>Password-wrapped (PBKDF2-SHA256 + AES-256-GCM): travels on a drive and
        /// unlocks with the user's password on any machine. A lost drive is useless without
        /// the password.</summary>
        PortablePassword = 2,

        /// <summary>Plaintext: travels freely with no prompt. The drive itself is the
        /// credential — anyone holding it holds the identity.</summary>
        PortableOpen = 3,
    }

    /// <summary>Outcome of recovering the stored private key. Only <see cref="Ok"/> and
    /// <see cref="Minted"/> yield a usable identity; the locked outcomes
    /// (<see cref="NeedsPassword"/> / <see cref="WrongPassword"/> / <see cref="WrongMachine"/>)
    /// mean a real identity exists but can't be opened here — the caller must surface that,
    /// never overwrite, so moving a drive or mistyping a password can't destroy the key.</summary>
    public enum IdentityUnprotect
    {
        Ok,
        Minted,        // nothing usable was stored; a fresh identity was generated (persist it)
        Empty,         // nothing stored
        Corrupt,       // unparseable / not our format (e.g. an old pre-v3 blob, or garbage)
        NeedsPassword, // password mode but no password supplied
        WrongPassword, // password mode, supplied password failed authentication
        WrongMachine,  // Secure mode blob minted under a different machine
    }

    /// <summary>
    /// Wrap/unwrap the instance's static Ed25519 private key for storage (issue #138).
    /// The blob is self-describing — a 1-byte version + 1-byte mode prefix — so a moved
    /// PadForge.xml decodes without any side channel, and the three protection modes
    /// (machine-bound, password-portable, open-portable) interoperate from one field.
    /// </summary>
    public static class IdentityProtector
    {
        private const byte Version = 1;
        private const int Pbkdf2Iterations = 600_000; // OWASP PBKDF2-HMAC-SHA256 floor
        // Bound into the DPAPI entropy so a LocalMachine blob can't be unwrapped by another
        // app's LocalMachine-scoped call without also knowing this constant.
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("PadForge.RemoteLink.Identity.v1");

        /// <summary>Wrap the 32-byte private key under <paramref name="mode"/> into a
        /// self-describing base64 blob.</summary>
        public static string Protect(byte[] privateKey, IdentityProtectionMode mode, string password = null)
        {
            if (privateKey == null || privateKey.Length != PeerCrypto.KeySize)
                throw new ArgumentException("Bad private key length.", nameof(privateKey));

            switch (mode)
            {
                case IdentityProtectionMode.Secure:
                    return Pack(mode, ProtectedData.Protect(privateKey, Entropy, DataProtectionScope.LocalMachine));

                case IdentityProtectionMode.PortablePassword:
                {
                    if (string.IsNullOrEmpty(password))
                        throw new ArgumentException("Password required for portable-password mode.", nameof(password));
                    byte[] salt = PeerCrypto.RandomBytes(16);
                    byte[] nonce = PeerCrypto.RandomBytes(12);
                    byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
                    byte[] ct = new byte[PeerCrypto.KeySize];
                    byte[] tag = new byte[16];
                    try
                    {
                        using var gcm = new AesGcm(key, 16);
                        gcm.Encrypt(nonce, privateKey, ct, tag);
                    }
                    finally { PeerCrypto.Zeroize(key); }

                    byte[] body = new byte[4 + 16 + 12 + 16 + PeerCrypto.KeySize];
                    BitConverter.GetBytes(Pbkdf2Iterations).CopyTo(body, 0);
                    salt.CopyTo(body, 4);
                    nonce.CopyTo(body, 20);
                    tag.CopyTo(body, 32);
                    ct.CopyTo(body, 48);
                    return Pack(mode, body);
                }

                case IdentityProtectionMode.PortableOpen:
                    return Pack(mode, (byte[])privateKey.Clone());

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        /// <summary>Recover the private key from a stored blob. <paramref name="privateKey"/>
        /// is non-null only on <see cref="IdentityUnprotect.Ok"/>; the caller must zeroize it.</summary>
        public static IdentityUnprotect TryUnprotect(string blobBase64, string password, out byte[] privateKey, out IdentityProtectionMode mode)
        {
            privateKey = null;
            mode = IdentityProtectionMode.Secure;
            if (string.IsNullOrEmpty(blobBase64)) return IdentityUnprotect.Empty;

            byte[] raw;
            try { raw = Convert.FromBase64String(blobBase64); }
            catch (FormatException) { return IdentityUnprotect.Corrupt; }
            if (raw.Length < 2 || raw[0] != Version || raw[1] < 1 || raw[1] > 3) return IdentityUnprotect.Corrupt;
            mode = (IdentityProtectionMode)raw[1];
            var body = new ReadOnlySpan<byte>(raw, 2, raw.Length - 2);

            switch (mode)
            {
                case IdentityProtectionMode.Secure:
                {
                    try
                    {
                        byte[] priv = ProtectedData.Unprotect(body.ToArray(), Entropy, DataProtectionScope.LocalMachine);
                        if (priv == null || priv.Length != PeerCrypto.KeySize) return IdentityUnprotect.Corrupt;
                        privateKey = priv;
                        return IdentityUnprotect.Ok;
                    }
                    catch (CryptographicException) { return IdentityUnprotect.WrongMachine; }
                }

                case IdentityProtectionMode.PortablePassword:
                {
                    if (body.Length != 4 + 16 + 12 + 16 + PeerCrypto.KeySize) return IdentityUnprotect.Corrupt;
                    if (string.IsNullOrEmpty(password)) return IdentityUnprotect.NeedsPassword;
                    int iter = BitConverter.ToInt32(body.Slice(0, 4));
                    if (iter < 1 || iter > 100_000_000) return IdentityUnprotect.Corrupt;
                    byte[] salt = body.Slice(4, 16).ToArray();
                    byte[] nonce = body.Slice(20, 12).ToArray();
                    byte[] tag = body.Slice(32, 16).ToArray();
                    byte[] ct = body.Slice(48, PeerCrypto.KeySize).ToArray();
                    byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iter, HashAlgorithmName.SHA256, 32);
                    byte[] priv = new byte[PeerCrypto.KeySize];
                    try
                    {
                        using var gcm = new AesGcm(key, 16);
                        gcm.Decrypt(nonce, ct, tag, priv);
                    }
                    catch (AuthenticationTagMismatchException) { PeerCrypto.Zeroize(priv); return IdentityUnprotect.WrongPassword; }
                    finally { PeerCrypto.Zeroize(key); }
                    privateKey = priv;
                    return IdentityUnprotect.Ok;
                }

                case IdentityProtectionMode.PortableOpen:
                {
                    if (body.Length != PeerCrypto.KeySize) return IdentityUnprotect.Corrupt;
                    privateKey = body.ToArray();
                    return IdentityUnprotect.Ok;
                }

                default:
                    return IdentityUnprotect.Corrupt;
            }
        }

        /// <summary>Load the stored identity, or mint a fresh one ONLY when nothing usable is
        /// stored (empty or unparseable/old format). A valid-but-locked identity (wrong
        /// machine, or a password-mode key with no/incorrect password) is never overwritten —
        /// it returns the locked status so the caller can prompt or warn. On
        /// <see cref="IdentityUnprotect.Minted"/> the out blobs must be persisted.</summary>
        public static IdentityUnprotect LoadOrMint(
            string protectedPrivateBase64, string publicKeyBase64, string password,
            IdentityProtectionMode mintMode, string mintPassword,
            out PeerIdentity identity, out string persistProtectedPrivate, out string persistPublic)
        {
            identity = null; persistProtectedPrivate = null; persistPublic = null;
            var status = TryUnprotect(protectedPrivateBase64, password, out byte[] priv, out _);
            try
            {
                if (status == IdentityUnprotect.Ok)
                {
                    byte[] pub = TryFromBase64(publicKeyBase64);
                    if (pub == null || pub.Length != PeerCrypto.KeySize)
                    {
                        // Private recovered but the stored public is missing/garbage. The public
                        // is fully determined by the private seed, so re-derive it and heal the
                        // stored field — discarding a recoverable identity here would change the
                        // fingerprint and break every existing pairing (#138 F26).
                        pub = PeerCrypto.DeriveEd25519PublicKey(priv);
                        persistPublic = Convert.ToBase64String(pub);
                    }
                    identity = new PeerIdentity(priv, pub);
                    return IdentityUnprotect.Ok;
                }

                if (status == IdentityUnprotect.Empty || status == IdentityUnprotect.Corrupt)
                {
                    identity = PeerIdentity.Generate();
                    byte[] fresh = identity.ExportPrivateKey();
                    try { persistProtectedPrivate = Protect(fresh, mintMode, mintPassword); }
                    finally { PeerCrypto.Zeroize(fresh); }
                    persistPublic = Convert.ToBase64String(identity.PublicKey);
                    return IdentityUnprotect.Minted;
                }

                // NeedsPassword / WrongPassword / WrongMachine: do NOT mint or overwrite.
                return status;
            }
            finally { if (priv != null) PeerCrypto.Zeroize(priv); }
        }

        private static string Pack(IdentityProtectionMode mode, byte[] body)
        {
            byte[] blob = new byte[2 + body.Length];
            blob[0] = Version;
            blob[1] = (byte)mode;
            body.CopyTo(blob, 2);
            return Convert.ToBase64String(blob);
        }

        private static byte[] TryFromBase64(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return Convert.FromBase64String(s); }
            catch (FormatException) { return null; }
        }
    }
}
