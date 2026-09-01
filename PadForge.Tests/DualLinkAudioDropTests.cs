using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The dual-link audio drop (#387, discussion #384). A dual-connected
    /// DualSense mutes its USB audio interface in both directions while
    /// the Bluetooth radio link is up (bench-measured with PadForge
    /// bypassed in the control test), so building a wired audio sink
    /// finishes the transport switch SDL's joystick de-dup starts: one
    /// radio-link drop per (device, wired path), re-armed when the record
    /// passes through a Bluetooth path, never repeated against a
    /// deliberate re-link. These tests pin the pure edge decision and the
    /// wiring the decision cannot see.
    /// </summary>
    public class DualLinkAudioDropTests
    {
        // ── The edge decision ──

        [Fact]
        public void DualDropWanted_FiresOncePerPath()
        {
            var map = new Dictionary<Guid, string>();
            var g = Guid.NewGuid();
            Assert.True(AudioPassthroughService.DualDropWanted(g, @"\\?\usb-path-a", map));
            Assert.False(AudioPassthroughService.DualDropWanted(g, @"\\?\usb-path-a", map));
            Assert.False(AudioPassthroughService.DualDropWanted(g, @"\\?\usb-path-a", map));
        }

        [Fact]
        public void DualDropWanted_PathChangeIsAFreshEdge()
        {
            var map = new Dictionary<Guid, string>();
            var g = Guid.NewGuid();
            Assert.True(AudioPassthroughService.DualDropWanted(g, @"\\?\port-1", map));
            Assert.True(AudioPassthroughService.DualDropWanted(g, @"\\?\port-2", map));
            Assert.False(AudioPassthroughService.DualDropWanted(g, @"\\?\port-2", map));
        }

        [Fact]
        public void DualDropWanted_RearmByRemovalRestoresTheEdge()
        {
            // The reconcile removes the entry when the record is
            // Bluetooth-pathed: the same wired path then fires again,
            // which is the unplug, Bluetooth session, replug shape.
            var map = new Dictionary<Guid, string>();
            var g = Guid.NewGuid();
            Assert.True(AudioPassthroughService.DualDropWanted(g, @"\\?\port-1", map));
            map.Remove(g);
            Assert.True(AudioPassthroughService.DualDropWanted(g, @"\\?\port-1", map));
        }

        [Fact]
        public void DualDropWanted_DevicesAreIndependent()
        {
            var map = new Dictionary<Guid, string>();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            Assert.True(AudioPassthroughService.DualDropWanted(a, @"\\?\shared-path", map));
            Assert.True(AudioPassthroughService.DualDropWanted(b, @"\\?\shared-path", map));
        }

        // ── Source contracts ──

        /// <summary>The drop runs at the top of the USB sink build, before
        /// endpoint resolution, so the firmware unmute is in flight while
        /// the endpoint spins up. The BT branch never reaches it.</summary>
        [Fact]
        public void DropSite_TopOfTheUsbBranch()
        {
            string aps = RepoText("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");
            int build = aps.IndexOf("private static void BuildTransportOnWorker", StringComparison.Ordinal);
            Assert.True(build > 0);
            string body = aps.Substring(build, 7000);
            int btBranch = body.IndexOf("if (s.IsBt)", StringComparison.Ordinal);
            int drop = body.IndexOf("TryDropStaleBtLinkOnce(s);", StringComparison.Ordinal);
            int container = body.IndexOf("GetContainerIdForDevicePath(s.HidPath)", StringComparison.Ordinal);
            Assert.True(btBranch > 0 && drop > btBranch && container > drop,
                "the drop must sit after the BT branch and before USB endpoint resolution");
        }

        /// <summary>The gates and the lane: the MAC parse with the
        /// all-zero rejection (#372 lesson), the once-per-path decision
        /// under the lock, the async #162 lane, and the diagnostic
        /// line.</summary>
        [Fact]
        public void DropHelper_GatesLaneAndDiagnostic()
        {
            string aps = RepoText("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");
            int at = aps.IndexOf("private static void TryDropStaleBtLinkOnce", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = aps.Substring(at, 1600);
            Assert.Contains("BluetoothLinkHelper.TryParseAddress(serial, out long addr) || addr == 0", body);
            Assert.Contains("DualDropWanted(s.DeviceGuid, s.HidPath, _dualDropDoneForPath)", body);
            Assert.Contains("Task.Run(() => BluetoothLinkHelper.TryDisconnect(serial))", body);
            Assert.Contains("SINK dual-link", body);
        }

        /// <summary>The re-arm: a Bluetooth-pathed record removes its map
        /// entry inside the reconcile's desired loop.</summary>
        [Fact]
        public void Reconcile_RearmsOnABluetoothPath()
        {
            string aps = RepoText("PadForge.App", "Common", "Input", "AudioPassthroughService.cs");
            Assert.Contains("if (isBt) lock (_dualDropLock) _dualDropDoneForPath.Remove(guid);", aps);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
