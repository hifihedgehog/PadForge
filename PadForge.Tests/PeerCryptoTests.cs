using System.Text;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class PeerCryptoTests
    {
        [Fact]
        public void X25519_BothPeersDeriveSameSecret()
        {
            var a = PeerCrypto.GenerateX25519KeyPair();
            var b = PeerCrypto.GenerateX25519KeyPair();
            Assert.Equal(PeerCrypto.KeySize, a.PrivateKey.Length);
            Assert.Equal(PeerCrypto.KeySize, a.PublicKey.Length);

            var sa = PeerCrypto.X25519Agree(a.PrivateKey, b.PublicKey);
            var sb = PeerCrypto.X25519Agree(b.PrivateKey, a.PublicKey);

            Assert.Equal(PeerCrypto.KeySize, sa.Length);
            Assert.Equal(Convert.ToHexString(sa), Convert.ToHexString(sb));
        }

        [Fact]
        public void X25519_DifferentPairsDeriveDifferentSecrets()
        {
            var a = PeerCrypto.GenerateX25519KeyPair();
            var b = PeerCrypto.GenerateX25519KeyPair();
            var c = PeerCrypto.GenerateX25519KeyPair();
            var ab = PeerCrypto.X25519Agree(a.PrivateKey, b.PublicKey);
            var ac = PeerCrypto.X25519Agree(a.PrivateKey, c.PublicKey);
            Assert.NotEqual(Convert.ToHexString(ab), Convert.ToHexString(ac));
        }

        [Fact]
        public void Ed25519_SignVerifyRoundTrip()
        {
            var id = PeerCrypto.GenerateEd25519KeyPair();
            var msg = Encoding.UTF8.GetBytes("per-handshake challenge nonce");
            var sig = PeerCrypto.Ed25519Sign(id.PrivateKey, msg);
            Assert.Equal(PeerCrypto.SignatureSize, sig.Length);
            Assert.True(PeerCrypto.Ed25519Verify(id.PublicKey, msg, sig));
        }

        [Fact]
        public void Ed25519_RejectsTamperedMessageSignatureAndWrongKey()
        {
            var id = PeerCrypto.GenerateEd25519KeyPair();
            var other = PeerCrypto.GenerateEd25519KeyPair();
            var msg = Encoding.UTF8.GetBytes("challenge");
            var sig = PeerCrypto.Ed25519Sign(id.PrivateKey, msg);

            var badMsg = (byte[])msg.Clone(); badMsg[0] ^= 0xFF;
            var badSig = (byte[])sig.Clone(); badSig[5] ^= 0xFF;

            Assert.False(PeerCrypto.Ed25519Verify(id.PublicKey, badMsg, sig));
            Assert.False(PeerCrypto.Ed25519Verify(id.PublicKey, msg, badSig));
            Assert.False(PeerCrypto.Ed25519Verify(other.PublicKey, msg, sig));
        }

        [Fact]
        public void Ed25519_VerifyNeverThrowsOnGarbage()
        {
            Assert.False(PeerCrypto.Ed25519Verify(null, new byte[] { 1 }, new byte[64]));
            Assert.False(PeerCrypto.Ed25519Verify(new byte[32], new byte[] { 1 }, null));
            Assert.False(PeerCrypto.Ed25519Verify(new byte[5], new byte[] { 1 }, new byte[64]));
            Assert.False(PeerCrypto.Ed25519Verify(new byte[32], new byte[] { 1 }, new byte[10]));
        }

        [Fact]
        public void Hkdf_DeterministicAcrossPeersAndContextBound()
        {
            var a = PeerCrypto.GenerateX25519KeyPair();
            var b = PeerCrypto.GenerateX25519KeyPair();
            var sa = PeerCrypto.X25519Agree(a.PrivateKey, b.PublicKey);
            var sb = PeerCrypto.X25519Agree(b.PrivateKey, a.PublicKey);

            var info = Encoding.UTF8.GetBytes("padforge-link v1 session");
            var ka = PeerCrypto.DeriveKey(sa, null, info);
            var kb = PeerCrypto.DeriveKey(sb, null, info);
            Assert.Equal(Convert.ToHexString(ka), Convert.ToHexString(kb));

            var kOther = PeerCrypto.DeriveKey(sa, null, Encoding.UTF8.GetBytes("different context"));
            Assert.NotEqual(Convert.ToHexString(ka), Convert.ToHexString(kOther));
        }

        [Fact]
        public void Aead_SealOpenRoundTrip()
        {
            var key = PeerCrypto.RandomBytes(PeerCrypto.KeySize);
            var nonce = PeerCrypto.BuildNonce(0, 1);
            var aad = new byte[] { 1, 0, 0, 7 };
            var plain = Encoding.UTF8.GetBytes("absolute CustomInputState frame");

            var sealed_ = PeerCrypto.Seal(key, nonce, aad, plain);
            Assert.Equal(plain.Length + PeerCrypto.TagSize, sealed_.Length);

            Assert.True(PeerCrypto.Open(key, nonce, aad, sealed_, out var rt));
            Assert.Equal(Convert.ToHexString(plain), Convert.ToHexString(rt));
        }

        [Fact]
        public void Aead_RejectsTamperWrongAadWrongNonceWrongKey()
        {
            var key = PeerCrypto.RandomBytes(PeerCrypto.KeySize);
            var nonce = PeerCrypto.BuildNonce(0, 42);
            var aad = new byte[] { 9, 9 };
            var plain = Encoding.UTF8.GetBytes("payload");
            var sealed_ = PeerCrypto.Seal(key, nonce, aad, plain);

            var tampered = (byte[])sealed_.Clone(); tampered[2] ^= 0xFF;
            Assert.False(PeerCrypto.Open(key, nonce, aad, tampered, out _));
            Assert.False(PeerCrypto.Open(key, nonce, new byte[] { 1, 2 }, sealed_, out _));
            Assert.False(PeerCrypto.Open(key, PeerCrypto.BuildNonce(0, 43), aad, sealed_, out _));
            Assert.False(PeerCrypto.Open(PeerCrypto.RandomBytes(PeerCrypto.KeySize), nonce, aad, sealed_, out _));
        }

        [Fact]
        public void BuildNonce_DirectionAndCounterDisjoint()
        {
            // Same counter, different direction salt -> different nonce (no cross-direction collision).
            var n0 = PeerCrypto.BuildNonce(0, 100);
            var n1 = PeerCrypto.BuildNonce(1, 100);
            Assert.Equal(PeerCrypto.NonceSize, n0.Length);
            Assert.NotEqual(Convert.ToHexString(n0), Convert.ToHexString(n1));
            // Same direction, advancing counter -> different nonce.
            Assert.NotEqual(Convert.ToHexString(PeerCrypto.BuildNonce(0, 100)), Convert.ToHexString(PeerCrypto.BuildNonce(0, 101)));
        }

        [Fact]
        public void Fingerprint_StableAndSized()
        {
            var id = PeerCrypto.GenerateEd25519KeyPair();
            var fp1 = PeerCrypto.Fingerprint(id.PublicKey);
            var fp2 = PeerCrypto.Fingerprint(id.PublicKey);
            Assert.Equal(PeerCrypto.KeySize, fp1.Length);
            Assert.Equal(Convert.ToHexString(fp1), Convert.ToHexString(fp2));
            Assert.True(PeerCrypto.FixedTimeEquals(fp1, fp2));
            Assert.False(string.IsNullOrWhiteSpace(PeerCrypto.FingerprintToDisplay(fp1)));
        }
    }
}
