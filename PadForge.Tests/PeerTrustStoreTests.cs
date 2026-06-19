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
        public void ResolvePeerLabel_PrefersCustomNameThenHostNameElseNull()
        {
            var store = new PeerTrustStore();

            // Custom name set -> use it.
            var named = store.Grant(Key(21), "John's PC", "t", reconnect: true, gamepadOnly: false);
            Assert.Equal("John's PC", store.ResolvePeerLabel(named.FingerprintHex));
            // Fingerprint match is case-insensitive (Convert.ToHexString is upper-case).
            Assert.Equal("John's PC", store.ResolvePeerLabel(named.FingerprintHex.ToLowerInvariant()));

            // No custom name, but a discovery-learned host name -> fall back to the host name.
            var hostOnly = store.Grant(Key(22), "", "t", reconnect: true, gamepadOnly: false);
            hostOnly.HostName = "OFFICE-DESKTOP";
            Assert.Equal("OFFICE-DESKTOP", store.ResolvePeerLabel(hostOnly.FingerprintHex));

            // Neither known yet -> null (no suffix appended).
            var bare = store.Grant(Key(23), "", "t", reconnect: true, gamepadOnly: false);
            Assert.Null(store.ResolvePeerLabel(bare.FingerprintHex));

            // Unknown / null / blank fingerprint -> null.
            Assert.Null(store.ResolvePeerLabel("DEADBEEF"));
            Assert.Null(store.ResolvePeerLabel(null));
            Assert.Null(store.ResolvePeerLabel("   "));
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
        private static byte[] Key() => PeerCrypto.RandomBytes(PeerCrypto.KeySize);
        private static string Hex(byte[] b) => System.Convert.ToHexString(b);

        [Fact]
        public void Secure_RoundTrips_OnThisMachine()
        {
            var secret = Key();
            string blob = IdentityProtector.Protect(secret, IdentityProtectionMode.Secure);
            Assert.NotEqual(System.Convert.ToBase64String(secret), blob); // actually wrapped

            Assert.Equal(IdentityUnprotect.Ok,
                IdentityProtector.TryUnprotect(blob, null, out var back, out var mode));
            Assert.Equal(IdentityProtectionMode.Secure, mode);
            Assert.Equal(Hex(secret), Hex(back));
        }

        [Fact]
        public void PortablePassword_RoundTrips_AndRejectsWrongOrMissingPassword()
        {
            var secret = Key();
            string blob = IdentityProtector.Protect(secret, IdentityProtectionMode.PortablePassword, "hunter2");

            Assert.Equal(IdentityUnprotect.Ok,
                IdentityProtector.TryUnprotect(blob, "hunter2", out var back, out var mode));
            Assert.Equal(IdentityProtectionMode.PortablePassword, mode);
            Assert.Equal(Hex(secret), Hex(back));

            Assert.Equal(IdentityUnprotect.WrongPassword,
                IdentityProtector.TryUnprotect(blob, "wrong", out var none, out _));
            Assert.Null(none);

            Assert.Equal(IdentityUnprotect.NeedsPassword,
                IdentityProtector.TryUnprotect(blob, null, out _, out _));
        }

        [Fact]
        public void PortableOpen_RoundTrips_WithoutPassword()
        {
            var secret = Key();
            string blob = IdentityProtector.Protect(secret, IdentityProtectionMode.PortableOpen);
            Assert.Equal(IdentityUnprotect.Ok,
                IdentityProtector.TryUnprotect(blob, null, out var back, out var mode));
            Assert.Equal(IdentityProtectionMode.PortableOpen, mode);
            Assert.Equal(Hex(secret), Hex(back));
        }

        [Fact]
        public void TryUnprotect_EmptyAndCorrupt()
        {
            Assert.Equal(IdentityUnprotect.Empty, IdentityProtector.TryUnprotect(null, null, out _, out _));
            Assert.Equal(IdentityUnprotect.Empty, IdentityProtector.TryUnprotect("", null, out _, out _));
            Assert.Equal(IdentityUnprotect.Corrupt, IdentityProtector.TryUnprotect("not-base64 @@@", null, out _, out _));
            Assert.Equal(IdentityUnprotect.Corrupt,
                IdentityProtector.TryUnprotect(System.Convert.ToBase64String(new byte[] { 9, 9, 9 }), null, out _, out _));
        }

        [Fact]
        public void LoadOrMint_MintsWhenAbsentThenReloadsSameIdentity()
        {
            var status = IdentityProtector.LoadOrMint(null, null, null, IdentityProtectionMode.Secure, null,
                out var id1, out var protPriv, out var pub);
            Assert.Equal(IdentityUnprotect.Minted, status);
            Assert.NotNull(protPriv);
            Assert.NotNull(pub);

            var status2 = IdentityProtector.LoadOrMint(protPriv, pub, null, IdentityProtectionMode.Secure, null,
                out var id2, out var protPriv2, out var pub2);
            Assert.Equal(IdentityUnprotect.Ok, status2);
            Assert.Null(protPriv2); // nothing new to persist
            Assert.Null(pub2);
            Assert.Equal(id1.FingerprintHex, id2.FingerprintHex);
        }

        [Fact]
        public void LoadOrMint_RemintsOnGarbageButNeverOverwritesALockedIdentity()
        {
            // Garbage / old format → safe to re-mint (nothing recoverable to destroy).
            Assert.Equal(IdentityUnprotect.Minted,
                IdentityProtector.LoadOrMint("garbage", "garbage", null, IdentityProtectionMode.Secure, null,
                    out var minted, out var p, out var pub));
            Assert.NotNull(minted); Assert.NotNull(p); Assert.NotNull(pub);

            // A valid password-mode identity opened with NO password must NOT be overwritten.
            string lockedBlob = IdentityProtector.Protect(Key(), IdentityProtectionMode.PortablePassword, "pw");
            string anyPub = System.Convert.ToBase64String(Key());
            Assert.Equal(IdentityUnprotect.NeedsPassword,
                IdentityProtector.LoadOrMint(lockedBlob, anyPub, null, IdentityProtectionMode.Secure, null,
                    out var none1, out var persist1, out _));
            Assert.Null(none1);
            Assert.Null(persist1); // CRITICAL: no overwrite

            // Wrong password likewise must not overwrite.
            Assert.Equal(IdentityUnprotect.WrongPassword,
                IdentityProtector.LoadOrMint(lockedBlob, anyPub, "nope", IdentityProtectionMode.Secure, null,
                    out var none2, out var persist2, out _));
            Assert.Null(none2);
            Assert.Null(persist2); // CRITICAL: no overwrite
        }

        [Fact]
        public void ModeSwitch_PreservesIdentity()
        {
            // Mint Secure, recover the raw private, re-wrap under password: the fingerprint
            // (i.e. the identity peers trust) must survive the switch unchanged.
            IdentityProtector.LoadOrMint(null, null, null, IdentityProtectionMode.Secure, null,
                out var id, out var securePriv, out var pub);
            Assert.Equal(IdentityUnprotect.Ok, IdentityProtector.TryUnprotect(securePriv, null, out var raw, out _));

            string pwBlob = IdentityProtector.Protect(raw, IdentityProtectionMode.PortablePassword, "secret");
            Assert.Equal(IdentityUnprotect.Ok,
                IdentityProtector.LoadOrMint(pwBlob, pub, "secret", IdentityProtectionMode.PortablePassword, "secret",
                    out var switched, out _, out _));
            Assert.Equal(id.FingerprintHex, switched.FingerprintHex);
        }
    }
}
