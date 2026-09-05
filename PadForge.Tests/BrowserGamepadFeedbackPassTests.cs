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
            var savedQuery = RemoteLinkOutputRouter.IsPeerConnected;
            var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RemoteLinkOutputRouter.IsPeerConnected = fp => connected.Contains(fp);
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var (ud, _, sent) = Rumbling("fb-" + Guid.NewGuid().ToString("N"));
                string peer = "fp-" + Guid.NewGuid().ToString("N");
                connected.Add(peer);
                Assert.True(RemoteLinkOutputRouter.ClaimOutput(ud.DevicePath, peer));
                var lease = (System.Collections.Concurrent.ConcurrentDictionary<string, long>)
                    typeof(RemoteLinkOutputRouter).GetField("_outputLease", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                lease[ud.DevicePath] = Environment.TickCount64 - 10_000;
                RunFeedbackPass(new InputManager(), ud);
                Assert.Empty(sent);                                         // still the peer's
                RemoteLinkOutputRouter.ReleasePeer("fp-someone-else");
                Assert.True(RemoteLinkOutputRouter.PeerWroteLast(ud.DevicePath));   // another peer's drop is not ours
                connected.Remove(peer);                                             // its last session is gone
                RemoteLinkOutputRouter.ReleasePeer(peer.ToUpperInvariant());        // fingerprints compare case-insensitively
                Assert.False(RemoteLinkOutputRouter.PeerWroteLast(ud.DevicePath));
                RunFeedbackPass(new InputManager(), ud);
                Assert.Equal(new[] { ((ushort)0, (ushort)0) }, sent);
                Assert.False(ud.ForceFeedbackState.IsActive);
            }
            finally { SettingsManager.UserSettings = saved; RemoteLinkOutputRouter.IsPeerConnected = savedQuery; }
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
            Assert.Contains("RemoteLinkOutputRouter.IsPeerConnected = fp => ReferenceEquals(_linkServer, link) && link.IsPeerConnected(fp);", svc);
            string link = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "PadForge.Engine", "RemoteLink", "LinkServer.cs"));
            Assert.Contains("PeerDropped?.Invoke(fp)", link);
            Assert.Contains("foreach (var d in dupes) DropConnection(d, replaced: true);", link);   // no false departure
            Assert.Contains("public bool IsPeerConnected(string fingerprintHex)", link);
            string router = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "RemoteLinkOutputRouter.cs"));
            int clear = router.IndexOf("public static void Clear()", StringComparison.Ordinal);
            Assert.Contains("_peerWroteLast.Clear();", router.Substring(clear, 1200));
        }

        [Fact]
        public void AClaim_NeedsALiveSession_AndALateReleaseAfterAReconnect_DoesNothing()
        {
            // Membership decides, not the order of connect and drop notices.
            var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var savedQuery = RemoteLinkOutputRouter.IsPeerConnected;
            RemoteLinkOutputRouter.IsPeerConnected = fp => connected.Contains(fp);
            try
            {
                string path = "web://live-" + Guid.NewGuid().ToString("N");
                string peer = "fp-" + Guid.NewGuid().ToString("N");
                // A frame from a peer with no session (dropped before the claim) is refused.
                Assert.False(RemoteLinkOutputRouter.ClaimOutput(path, peer));
                Assert.False(RemoteLinkOutputRouter.PeerWroteLast(path));
                connected.Add(peer);
                Assert.True(RemoteLinkOutputRouter.ClaimOutput(path, peer.ToUpperInvariant()));
                // The old session's drop notice arrives late, after the reconnect:
                // the peer has a session, so nothing is released.
                RemoteLinkOutputRouter.ReleasePeer(peer);
                Assert.True(RemoteLinkOutputRouter.PeerWroteLast(path));
                Assert.True(RemoteLinkOutputRouter.IsClaimedByPeer(path));
                // The peer really leaves: the release lands.
                connected.Remove(peer);
                RemoteLinkOutputRouter.ReleasePeer(peer);
                Assert.False(RemoteLinkOutputRouter.PeerWroteLast(path));
                Assert.False(RemoteLinkOutputRouter.IsClaimedByPeer(path));
            }
            finally { RemoteLinkOutputRouter.IsPeerConnected = savedQuery; }
        }

        [Fact]
        public void ADepartingPeersRelease_LeavesAnotherPeersNewerClaim()
        {
            var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var savedQuery = RemoteLinkOutputRouter.IsPeerConnected;
            RemoteLinkOutputRouter.IsPeerConnected = fp => connected.Contains(fp);
            try
            {
                string path = "web://two-" + Guid.NewGuid().ToString("N");
                string a = "fp-a-" + Guid.NewGuid().ToString("N"), b = "fp-b-" + Guid.NewGuid().ToString("N");
                connected.Add(a); connected.Add(b);
                Assert.True(RemoteLinkOutputRouter.ClaimOutput(path, a));
                Assert.True(RemoteLinkOutputRouter.ClaimOutput(path, b));       // B took the device over
                connected.Remove(a);
                RemoteLinkOutputRouter.ReleasePeer(a);
                Assert.True(RemoteLinkOutputRouter.PeerWroteLast(path));         // still B's
                Assert.True(RemoteLinkOutputRouter.IsClaimedByPeer(path));       // lease intact
                connected.Remove(b);
                RemoteLinkOutputRouter.ReleasePeer(b);
                Assert.False(RemoteLinkOutputRouter.PeerWroteLast(path));
            }
            finally { RemoteLinkOutputRouter.IsPeerConnected = savedQuery; }
        }

        [Fact]
        public async System.Threading.Tasks.Task TheFeedbackPass_HoldsTheDeviceOutputGate_AcrossItsStop()
        {
            // A thread standing in for a peer's frame mid-apply holds the gate.
            // The pass must not decide or stop until that thread lets go.
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var (ud, _, sent) = Rumbling("fb-" + Guid.NewGuid().ToString("N"));
                var held = new System.Threading.ManualResetEventSlim();
                var release = new System.Threading.ManualResetEventSlim();
                long releasedAt = 0;
                var holder = new System.Threading.Thread(() =>
                {
                    lock (ud.OutputSync)
                    {
                        held.Set();
                        release.Wait();
                        releasedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                });
                holder.Start();
                held.Wait();
                var im = new InputManager();
                var pass = System.Threading.Tasks.Task.Run(() => { RunFeedbackPass(im, ud); return System.Diagnostics.Stopwatch.GetTimestamp(); });
                try
                {
                    Assert.NotSame(pass, await System.Threading.Tasks.Task.WhenAny(pass, System.Threading.Tasks.Task.Delay(300)));  // still held
                    Assert.Empty(sent);
                }
                finally { release.Set(); holder.Join(5000); }       // never leave the holder blocked on a failure
                long passDone = await pass;
                Assert.True(passDone >= releasedAt, "the pass finished before the gate was released");
                Assert.Equal(new[] { ((ushort)0, (ushort)0) }, sent);
            }
            finally { SettingsManager.UserSettings = saved; }
        }

        [Fact]
        public void TheReceiveCallback_IsGated_AndBoundToItsServer()
        {
            string svc = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("origin.OutputReceived += (fp, slot, payload) => OnRemoteOutputReceived(origin, fp, slot, payload);", svc);
            int at = svc.IndexOf("private void OnRemoteOutputReceived(LinkServer origin,", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = svc.Substring(at, 1400);
            int gate = body.IndexOf("lock (ud?.OutputSync ?? _unresolvedOutputSync)", StringComparison.Ordinal);
            int check = body.IndexOf("if (!ReferenceEquals(Volatile.Read(ref _linkServer), origin)) return;", StringComparison.Ordinal);
            int apply = body.IndexOf("ApplyRemoteOutput(effect, source, ud, peerFingerprint);", StringComparison.Ordinal);
            Assert.True(gate > 0 && check > gate && apply > check, "gate, then the server check, then the apply");
            string step2 = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs"));
            int zero = step2.IndexOf("if (slotCount == 0)", StringComparison.Ordinal);
            string block = step2.Substring(zero, 2600);
            Assert.True(block.IndexOf("lock (ud.OutputSync)", StringComparison.Ordinal) < block.IndexOf("StopDeviceForces", StringComparison.Ordinal));
            // The gate is taken for web pads only, so a native device's pending write never holds the polling thread.
            Assert.True(block.IndexOf("is PadForge.Engine.WebControllerDevice web", StringComparison.Ordinal) < block.IndexOf("lock (ud.OutputSync)", StringComparison.Ordinal));
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
