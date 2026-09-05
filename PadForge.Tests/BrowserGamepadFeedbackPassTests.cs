using System;
using System.Collections.Generic;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #415 (discussion #402): the feedback pass runs against a web pad
    /// that has left its last slot. It swaps the shared UserSettings, so it
    /// rides the statics collection like every other test that does.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class BrowserGamepadFeedbackPassTests
    {
        private static void RunFeedbackPass(InputManager im, UserDevice ud)
        {
            var m = typeof(InputManager).GetMethod("ApplyForceFeedback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);
            m.Invoke(im, new object[] { ud });
        }

        private static (UserDevice ud, WebControllerDevice web, List<(ushort, ushort)> sent) Rumbling(string key)
        {
            var web = new WebControllerDevice(key, "Browser Gamepad 1", false, "gamepad");
            var sent = new List<(ushort, ushort)>();
            web.RumbleRequested += (l, h) => sent.Add((l, h));
            var ud = new UserDevice();
            ud.LoadFromWebDevice(web);
            Assert.NotNull(ud.ForceFeedbackState);
            ud.ForceFeedbackState.SetDeviceForces(ud, web, new PadSetting(), new Vibration(65535, 0));
            Assert.True(ud.ForceFeedbackState.IsActive);
            Assert.Contains(sent, s => s.Item1 > 0);
            sent.Clear();
            return (ud, web, sent);
        }

        [Fact]
        public void AWebPadWithNoSlots_IsStoppedOnce_AndNotAgain()
        {
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();    // no assignments at all
                var (ud, _, sent) = Rumbling("fb-" + Guid.NewGuid().ToString("N"));
                var im = new InputManager();
                RunFeedbackPass(im, ud);
                Assert.Equal(new[] { ((ushort)0, (ushort)0) }, sent);         // exactly one zero
                Assert.False(ud.ForceFeedbackState.IsActive);
                RunFeedbackPass(im, ud);
                Assert.Single(sent);                                        // and never a second
            }
            finally { SettingsManager.UserSettings = saved; }
        }

        [Fact]
        public void AWebPadAPeerWroteLast_KeepsItsRumble_AfterTheLeaseLapses()
        {
            // A peer holding an unchanged rumble ships it once, so its lease
            // lapses in three seconds while the rumble is still meant. Who wrote
            // last does not lapse.
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var (ud, _, sent) = Rumbling("fb-" + Guid.NewGuid().ToString("N"));
                RemoteLinkOutputRouter.ClaimOutput(ud.DevicePath);
                var lease = (System.Collections.Concurrent.ConcurrentDictionary<string, long>)
                    typeof(RemoteLinkOutputRouter).GetField("_outputLease", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                lease[ud.DevicePath] = Environment.TickCount64 - 10_000;      // the lease is long gone
                Assert.False(RemoteLinkOutputRouter.IsClaimedByPeer(ud.DevicePath));
                Assert.True(RemoteLinkOutputRouter.PeerWroteLast(ud.DevicePath));
                RunFeedbackPass(new InputManager(), ud);
                Assert.Empty(sent);
                Assert.True(ud.ForceFeedbackState.IsActive);
                // Once the local pipeline writes, the peer is no longer the last
                // writer, and the stop applies again.
                RemoteLinkOutputRouter.NoteLocalWrite(ud.DevicePath);
                RunFeedbackPass(new InputManager(), ud);
                Assert.Equal(new[] { ((ushort)0, (ushort)0) }, sent);
            }
            finally { SettingsManager.UserSettings = saved; }
        }

        [Fact]
        public void APeerThatLeaves_ReleasesItsOwnership_AndTheStopRunsOnce()
        {
            // The peer's last session dropped without a zero. Its ownership is
            // released by fingerprint, and the next pass ends what it left.
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var (ud, _, sent) = Rumbling("fb-" + Guid.NewGuid().ToString("N"));
                string peer = "fp-" + Guid.NewGuid().ToString("N");
                RemoteLinkOutputRouter.ClaimOutput(ud.DevicePath, peer);
                var lease = (System.Collections.Concurrent.ConcurrentDictionary<string, long>)
                    typeof(RemoteLinkOutputRouter).GetField("_outputLease", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                lease[ud.DevicePath] = Environment.TickCount64 - 10_000;
                RunFeedbackPass(new InputManager(), ud);
                Assert.Empty(sent);                                         // still the peer's
                RemoteLinkOutputRouter.ReleasePeer("fp-someone-else");
                Assert.True(RemoteLinkOutputRouter.PeerWroteLast(ud.DevicePath));   // another peer's drop is not ours
                RemoteLinkOutputRouter.ReleasePeer(peer.ToUpperInvariant());        // fingerprints compare case-insensitively
                Assert.False(RemoteLinkOutputRouter.PeerWroteLast(ud.DevicePath));
                RunFeedbackPass(new InputManager(), ud);
                Assert.Equal(new[] { ((ushort)0, (ushort)0) }, sent);
                Assert.False(ud.ForceFeedbackState.IsActive);
            }
            finally { SettingsManager.UserSettings = saved; }
        }

        [Fact]
        public void ADeviceNoLongerShared_ReleasesItsOwnership()
        {
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var (ud, _, sent) = Rumbling("fb-" + Guid.NewGuid().ToString("N"));
                RemoteLinkOutputRouter.ClaimOutput(ud.DevicePath, "fp-x");
                Assert.True(RemoteLinkOutputRouter.IsClaimedByPeer(ud.DevicePath));
                RemoteLinkOutputRouter.ReleaseDevice(ud.DevicePath);
                Assert.False(RemoteLinkOutputRouter.IsClaimedByPeer(ud.DevicePath));  // the lease goes with it
                Assert.False(RemoteLinkOutputRouter.PeerWroteLast(ud.DevicePath));
                RunFeedbackPass(new InputManager(), ud);
                Assert.Single(sent);
            }
            finally { SettingsManager.UserSettings = saved; }
        }

        [Fact]
        public void TheReleasesAreWired_AtThePeerDrop_TheUnshare_AndTheClaim()
        {
            string svc = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("_linkServer.PeerDropped += RemoteLinkOutputRouter.ReleasePeer;", svc);
            Assert.Contains("RemoteLinkOutputRouter.ReleaseDevice(old.ud.DevicePath);", svc);
            Assert.Contains("if (!RemoteLinkOutputRouter.ClaimOutput(ud?.DevicePath ?? source.DevicePath, peerFingerprint)) return;", svc);
            Assert.Contains("_linkServer.PeerConnected += RemoteLinkOutputRouter.PeerConnected;", svc);
            string link = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "PadForge.Engine", "RemoteLink", "LinkServer.cs"));
            Assert.Contains("PeerDropped?.Invoke(fp)", link);
            Assert.Contains("foreach (var d in dupes) DropConnection(d, replaced: true);", link);   // no false departure
            Assert.Contains("PeerConnected?.Invoke(conn.PeerFingerprintHex)", link);
            string router = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "RemoteLinkOutputRouter.cs"));
            int clear = router.IndexOf("public static void Clear()", StringComparison.Ordinal);
            Assert.Contains("_peerWroteLast.Clear();", router.Substring(clear, 1200));
        }

        [Fact]
        public void AGonePeer_CannotReclaim_UntilItConnectsAgain()
        {
            // A frame decoded before the drop, claiming after the release, would
            // re-own the device for a peer with nothing left to release it.
            string path = "web://gone-" + Guid.NewGuid().ToString("N");
            string peer = "fp-" + Guid.NewGuid().ToString("N");
            Assert.True(RemoteLinkOutputRouter.ClaimOutput(path, peer));
            RemoteLinkOutputRouter.ReleasePeer(peer);
            Assert.False(RemoteLinkOutputRouter.ClaimOutput(path, peer));
            Assert.False(RemoteLinkOutputRouter.PeerWroteLast(path));
            Assert.False(RemoteLinkOutputRouter.IsClaimedByPeer(path));
            RemoteLinkOutputRouter.PeerConnected(peer);
            Assert.True(RemoteLinkOutputRouter.ClaimOutput(path, peer));
            Assert.True(RemoteLinkOutputRouter.PeerWroteLast(path));
            RemoteLinkOutputRouter.ReleaseDevice(path);
        }

        [Fact]
        public void ADepartingPeersRelease_LeavesAnotherPeersNewerClaim()
        {
            string path = "web://two-" + Guid.NewGuid().ToString("N");
            string a = "fp-a-" + Guid.NewGuid().ToString("N"), b = "fp-b-" + Guid.NewGuid().ToString("N");
            Assert.True(RemoteLinkOutputRouter.ClaimOutput(path, a));
            Assert.True(RemoteLinkOutputRouter.ClaimOutput(path, b));       // B took the device over
            RemoteLinkOutputRouter.ReleasePeer(a);
            Assert.True(RemoteLinkOutputRouter.PeerWroteLast(path));         // still B's
            Assert.True(RemoteLinkOutputRouter.IsClaimedByPeer(path));       // lease intact
            RemoteLinkOutputRouter.ReleasePeer(b);
            Assert.False(RemoteLinkOutputRouter.PeerWroteLast(path));
            RemoteLinkOutputRouter.PeerConnected(a); RemoteLinkOutputRouter.PeerConnected(b);
        }

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Fact]
        public void AWebPadARemotePeerIsDriving_KeepsItsRumble()
        {
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var (ud, _, sent) = Rumbling("fb-" + Guid.NewGuid().ToString("N"));
                RemoteLinkOutputRouter.ClaimOutput(ud.DevicePath);          // a relayed frame holds the lease
                RunFeedbackPass(new InputManager(), ud);
                Assert.Empty(sent);
                Assert.True(ud.ForceFeedbackState.IsActive);
            }
            finally { SettingsManager.UserSettings = saved; }
        }
    }
}
