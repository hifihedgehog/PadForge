using System;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// DPAPI wrap/unwrap for the instance's static private key at rest (issue #138).
    /// The key is encrypted to the current Windows user (DataProtectionScope.CurrentUser),
    /// so a stolen PadForge.xml carries no usable identity off the machine/account.
    /// Public keys and the trust list are not secret and are stored in the clear.
    /// </summary>
    public static class IdentityProtector
    {
        // Bound into the DPAPI entropy so this blob can't be unwrapped by another
        // app's CurrentUser-scoped call without also knowing this constant.
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("PadForge.RemoteLink.Identity.v1");

        /// <summary>Encrypt the private key for storage; returns base64 of the DPAPI blob.</summary>
        public static string ProtectToBase64(byte[] privateKey)
        {
            if (privateKey == null) throw new ArgumentNullException(nameof(privateKey));
            byte[] blob = ProtectedData.Protect(privateKey, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(blob);
        }

        /// <summary>Decrypt a base64 DPAPI blob back to the private key, or null if the
        /// blob is malformed or was protected under a different user/entropy.</summary>
        public static byte[] UnprotectFromBase64(string protectedBase64)
        {
            if (string.IsNullOrEmpty(protectedBase64)) return null;
            try
            {
                byte[] blob = Convert.FromBase64String(protectedBase64);
                return ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (FormatException) { return null; }
            catch (CryptographicException) { return null; }
        }

        /// <summary>
        /// Load the persisted identity from a (protected private key, public key) pair,
        /// or generate, protect, and hand back a fresh one when none is stored or the
        /// stored one can't be unwrapped. <paramref name="protectedPrivateBase64"/> is
        /// updated with the value to persist (non-null only when a new key was minted).
        /// </summary>
        public static PeerIdentity LoadOrCreate(string protectedPrivateBase64, string publicKeyBase64, out string toPersistProtectedPrivate, out string toPersistPublic)
        {
            byte[] priv = UnprotectFromBase64(protectedPrivateBase64);
            byte[] pub = TryFromBase64(publicKeyBase64);
            if (priv != null && priv.Length == PeerCrypto.KeySize && pub != null && pub.Length == PeerCrypto.KeySize)
            {
                toPersistProtectedPrivate = null;
                toPersistPublic = null;
                var loaded = new PeerIdentity(priv, pub);
                PeerCrypto.Zeroize(priv);
                return loaded;
            }

            var identity = PeerIdentity.Generate();
            byte[] freshPriv = identity.ExportPrivateKey();
            toPersistProtectedPrivate = ProtectToBase64(freshPriv);
            toPersistPublic = Convert.ToBase64String(identity.PublicKey);
            PeerCrypto.Zeroize(freshPriv);
            return identity;
        }

        private static byte[] TryFromBase64(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return Convert.FromBase64String(s); }
            catch (FormatException) { return null; }
        }
    }
}
