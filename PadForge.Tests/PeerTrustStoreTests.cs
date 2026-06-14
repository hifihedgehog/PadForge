using System.IO;
using System.Xml.Serialization;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class PeerTrustStoreTests
    {
        [Fact]
        public void PeerTrust_XmlRoundTrips()
        {
            var peers = new[]
            {
                PeerTrust.FromPublicKey(Key(1), "Living Room", "2026-06-13T00:00:00Z", reconnect: true, gamepadOnly: false),
                PeerTrust.FromPublicKey(Key(2), "Office", "2026-06-13T00:00:00Z", reconnect: false, gamepadOnly: true),
            };
            var ser = new XmlSerializer(typeof(PeerTrust[]), new XmlRootAttribute("RemoteLinkPeers"));
            var sw = new StringWriter();
            ser.Serialize(sw, peers);
            var back = (PeerTrust[])ser.Deserialize(new StringReader(sw.ToString()));

            Assert.Equal(2, back.Length);
            Assert.Equal("Living Room", back[0].Name);
            Assert.True(back[0].ReconnectEnabled);
            Assert.False(back[0].GamepadOnly);
            Assert.Equal(peers[0].FingerprintHex, back[0].FingerprintHex);
            Assert.True(back[1].GamepadOnly);
            Assert.False(back[1].ReconnectEnabled);
        }

        private static byte[] Key(byte b)
        {
            var k = new byte[PeerCrypto.KeySize];
            for (int i = 0; i < k.Length; i++) k[i] = b;
            return k;
        }

        [Fact]
        public void UnknownKey_IsFirstContact()
        {
            var store = new PeerTrustStore();
            Assert.Equal(TrustDecision.FirstContact, store.Decide(Key(1)));
            Assert.False(store.IsTrusted(Key(1)));
        }

        [Fact]
        public void GrantThenDecide_AutoSelectVsManual()
        {
            var store = new PeerTrustStore();
            store.Grant(Key(7), "Living Room PC", "2026-06-13T00:00:00Z", reconnect: true, gamepadOnly: false);
            Assert.Equal(TrustDecision.KnownAutoSelect, store.Decide(Key(7)));
            Assert.True(store.IsTrusted(Key(7)));

            store.Grant(Key(8), "Office PC", "2026-06-13T00:00:00Z", reconnect: false, gamepadOnly: true);
            Assert.Equal(TrustDecision.KnownManual, store.Decide(Key(8)));
        }

        [Fact]
        public void Grant_UpdatesExistingInsteadOfDuplicating()
        {
            var store = new PeerTrustStore();
            store.Grant(Key(3), "Old Name", "t", reconnect: true, gamepadOnly: false);
            store.Grant(Key(3), "New Name", "t", reconnect: false, gamepadOnly: true);
            Assert.Single(store.Peers);
            Assert.Equal("New Name", store.Peers[0].Name);
            Assert.False(store.Peers[0].ReconnectEnabled);
            Assert.True(store.IsGamepadOnly(Key(3)));
        }

        [Fact]
        public void Revoke_RemovesAndFailsClosed()
        {
            var store = new PeerTrustStore();
            store.Grant(Key(5), "Peer", "t", true, false);
            Assert.True(store.Revoke(Key(5)));
            Assert.Equal(TrustDecision.FirstContact, store.Decide(Key(5))); // back to needing a fresh grant
            Assert.False(store.Revoke(Key(5))); // already gone
        }

        [Fact]
        public void RevokeAll_Clears()
        {
            var store = new PeerTrustStore(new[]
            {
                PeerTrust.FromPublicKey(Key(1), "a", "t", true, false),
                PeerTrust.FromPublicKey(Key(2), "b", "t", true, false),
            });
            Assert.Equal(2, store.Peers.Count);
            store.RevokeAll();
            Assert.Empty(store.Peers);
        }

        [Fact]
        public void IsGamepadOnly_DefaultsRestrictedForUnknown()
        {
            var store = new PeerTrustStore();
            Assert.True(store.IsGamepadOnly(Key(9))); // unknown -> fail safe
        }

        [Fact]
        public void PeerTrust_RoundTripsKeyAndFingerprint()
        {
            var id = PeerIdentity.Generate();
            var t = PeerTrust.FromPublicKey(id.PublicKey, "X", "2026-06-13T00:00:00Z", true, false);
            Assert.Equal(System.Convert.ToHexString(id.PublicKey), System.Convert.ToHexString(t.PublicKey));
            Assert.Equal(id.FingerprintHex, t.FingerprintHex);
        }

        [Fact]
        public void PeerTrust_MalformedKey_DegradesGracefully()
        {
            var t = new PeerTrust { PublicKeyBase64 = "not base64 @@@" };
            Assert.Null(t.PublicKey);
            Assert.Equal("", t.FingerprintHex);
        }

        [Fact]
        public void Constructor_IgnoresEntriesWithoutAKey()
        {
            var store = new PeerTrustStore(new[]
            {
                new PeerTrust { PublicKeyBase64 = "" },
                PeerTrust.FromPublicKey(Key(4), "ok", "t", true, false),
            });
            Assert.Single(store.Peers);
        }
    }

    public class IdentityProtectorTests
    {
        [Fact]
        public void Protect_Unprotect_RoundTrips()
        {
            var secret = PeerCrypto.RandomBytes(PeerCrypto.KeySize);
            string blob = IdentityProtector.ProtectToBase64(secret);
            Assert.NotEqual(System.Convert.ToBase64String(secret), blob); // actually encrypted

            byte[] back = IdentityProtector.UnprotectFromBase64(blob);
            Assert.Equal(System.Convert.ToHexString(secret), System.Convert.ToHexString(back));
        }

        [Fact]
        public void Unprotect_MalformedReturnsNull()
        {
            Assert.Null(IdentityProtector.UnprotectFromBase64(null));
            Assert.Null(IdentityProtector.UnprotectFromBase64("not-base64 @@@"));
            Assert.Null(IdentityProtector.UnprotectFromBase64(System.Convert.ToBase64String(new byte[] { 1, 2, 3 })));
        }

        [Fact]
        public void LoadOrCreate_MintsWhenAbsentThenLoadsWhatItPersisted()
        {
            var id1 = IdentityProtector.LoadOrCreate(null, null, out var protPriv, out var pub);
            Assert.NotNull(protPriv); // a fresh key was minted and needs persisting
            Assert.NotNull(pub);

            // Feeding back the persisted values must reload the SAME identity.
            var id2 = IdentityProtector.LoadOrCreate(protPriv, pub, out var protPriv2, out var pub2);
            Assert.Null(protPriv2); // nothing new to persist
            Assert.Null(pub2);
            Assert.Equal(id1.FingerprintHex, id2.FingerprintHex);
        }

        [Fact]
        public void LoadOrCreate_RegeneratesWhenStoredBlobUnreadable()
        {
            var id = IdentityProtector.LoadOrCreate("garbage", "garbage", out var protPriv, out var pub);
            Assert.NotNull(id);
            Assert.NotNull(protPriv); // had to mint a new one
            Assert.NotNull(pub);
        }
    }
}
