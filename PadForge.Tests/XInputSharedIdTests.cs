using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #583, from discussion #567: a DualShock 3 and a Navigation controller on
    /// DsHidMini's XInput mode both report 045E:02FF, and SDL's XInput backend
    /// names each only by its XInput slot, which OpenXInput hands out in the
    /// order pads connect. The rumble writer took the Nth device in Windows'
    /// list for slot N, so the DS3's rumble went to the Navigator, and with
    /// both connected Hide from Games hid neither. Both now reach the pad on
    /// the row's slot through OpenXInput's identity call
    /// (XInputSlotIdentity). Which row a pad gets is unchanged: pads of one
    /// model follow the order they connect (e002074ed).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class XInputSharedIdTests : IDisposable
    {
        private const ushort Vid = 0x045E;
        private const ushort Pid = 0x02FF;

        // Two HID nodes of one id, sorted as StableXInputInstance.FindAll
        // sorts them.
        private const string Ds3Node = @"HID\VID_045E&PID_02FF&IG_00\8&1D7C1E6&0&0000";
        private const string NavNode = @"HID\VID_045E&PID_02FF&IG_00\8&3A90F5B&0&0000";
        private static readonly string[] Nodes = { Ds3Node, NavNode };

        private readonly string _root;

        public XInputSharedIdTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "padforge-583-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            XInputSlotIdentity.AnswerForTest = null;
            XInputSlotIdentity.InstanceIdForTest = null;
            XboxImpulseHidWriter.InvalidateCachedTargets();
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        // ── Which row a pad gets ──

        /// <summary>Same-model XInput pads keep rows keyed to their slot, so
        /// pads of one model follow the order they connect (e002074ed,
        /// dee641bd7). The row's identity never comes from OpenXInput's
        /// per-device answer, which only routes output and hiding.</summary>
        [Fact]
        public void SameModelXInputPads_KeepRowsKeyedToTheirSlot()
        {
            string wrapper = RepoFile("PadForge.Engine", "Common", "SdlDeviceWrapper.cs");
            Assert.Contains(": (slot >= 0 ? Math.Min(slot, candidates.Count - 1) : 0);", wrapper);
            Assert.Contains("? $\"pnp:{physicalPath}:slot{(slot >= 0 ? slot : idx)}\"", wrapper);
            Assert.DoesNotContain("XInputSlotIdentity", wrapper);
        }

        // ── Rumble ──

        /// <summary>The report goes to the device OpenXInput serves the slot
        /// from. The position walk is never asked while there is an identity
        /// call.</summary>
        [Fact]
        public void Rumble_GoesToThePadOnTheRowsSlot()
        {
            string target = Interface("VID_045E&PID_02FF&IG_00#ds3");
            int walks = 0;
            var saved = XboxImpulseHidWriter.Resolver;
            XboxImpulseHidWriter.Resolver = _ => { walks++; return Interface("VID_045E&PID_02FF&IG_00#nav"); };
            XInputSlotIdentity.AnswerForTest = slot => slot == 1
                ? new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Found, target, 7)
                : new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.None);
            try
            {
                XboxImpulseHidWriter.InvalidateCachedTargets();
                Assert.True(XboxImpulseHidWriter.Write(Pad("XInput#1"), 65535, 32768, 0, 0, force: true));
                XboxImpulseHidWriter.InvalidateCachedTargets();
                Assert.Equal(new byte[] { 0x03, 0x0F, 0, 0, 100, 50, 0xFF, 0x00, 0xEB }, File.ReadAllBytes(target));
                Assert.Equal(0, walks);
            }
            finally
            {
                XboxImpulseHidWriter.Resolver = saved;
            }
        }

        /// <summary>A slot whose device carries another id has changed hands
        /// since SDL listed the pad: no write, and the cooldown.</summary>
        [Fact]
        public void Rumble_ToASlotHoldingAnotherId_IsNotWritten()
        {
            string other = Interface("VID_045E&PID_0B13&IG_00#series");
            XInputSlotIdentity.AnswerForTest = _ => new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Found, other, 3);
            var pad = Pad("XInput#2");
            XboxImpulseHidWriter.InvalidateCachedTargets();
            Assert.False(XboxImpulseHidWriter.Write(pad, 65535, 65535, 0, 0, force: true));
            Assert.Empty(File.ReadAllBytes(other));
            Assert.True(XboxImpulseHidWriter.CoolingDown(pad.DevicePath, Environment.TickCount64));
        }

        /// <summary>When another pad takes the slot, the next write goes to
        /// the device that serves it now.</summary>
        [Fact]
        public void Rumble_FollowsTheSlotToItsNewPad()
        {
            string first = Interface("VID_045E&PID_02FF&IG_00#first");
            string second = Interface("VID_045E&PID_02FF&IG_00#second");
            var answer = new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Found, first, 1);
            XInputSlotIdentity.AnswerForTest = _ => answer;
            var pad = Pad("XInput#3");
            XboxImpulseHidWriter.InvalidateCachedTargets();
            Assert.True(XboxImpulseHidWriter.Write(pad, 65535, 0, 0, 0, force: true));

            answer = new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Found, second, 2);
            Assert.True(XboxImpulseHidWriter.Write(pad, 0, 65535, 0, 0, force: true));
            XboxImpulseHidWriter.InvalidateCachedTargets();

            Assert.Equal(9, File.ReadAllBytes(first).Length);
            Assert.Equal(new byte[] { 0x03, 0x0F, 0, 0, 0, 100, 0xFF, 0x00, 0xEB }, File.ReadAllBytes(second));
        }

        /// <summary>A kept handle serves its slot while the device and the
        /// generation match. Another generation, another device or an empty
        /// slot drops it. A busy answer, or no identity call, keeps it.</summary>
        [Fact]
        public void AKeptHandle_ServesItsSlot_UntilTheSlotChangesHands()
        {
            const string path = @"\\?\HID#VID_045E&PID_02FF&IG_00#8&3a90f5b&0&0000#{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}";
            var kept = new XboxImpulseHidWriter.CachedTarget { InterfacePath = path, Generation = 4 };
            Assert.True(kept.Serves(new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Found, path, 4)));
            Assert.True(kept.Serves(new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Found, path.ToUpperInvariant(), 4)));
            Assert.False(kept.Serves(new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Found, path, 5)));
            Assert.False(kept.Serves(new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Found, path.Replace("3a90f5b", "1d7c1e6"), 4)));
            Assert.False(kept.Serves(new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.None)));
            Assert.True(kept.Serves(new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Busy)));
            Assert.True(kept.Serves(new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Unsupported)));
        }

        /// <summary>Only a process with no identity call walks positions.</summary>
        [Fact]
        public void TheTarget_IsThePositionWalk_OnlyWithNoIdentityCall()
        {
            int walks = 0;
            var saved = XboxImpulseHidWriter.Resolver;
            XboxImpulseHidWriter.Resolver = _ => { walks++; return "walked"; };
            try
            {
                var pad = Pad("XInput#0");
                Assert.Equal("walked", XboxImpulseHidWriter.TargetOf(pad, new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Unsupported)));
                Assert.Null(XboxImpulseHidWriter.TargetOf(pad, new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Busy)));
                Assert.Null(XboxImpulseHidWriter.TargetOf(pad, new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.None)));
                Assert.Equal(1, walks);
            }
            finally
            {
                XboxImpulseHidWriter.Resolver = saved;
            }
        }

        // ── Hide from Games ──

        /// <summary>An online XInput row's node is the pad OpenXInput serves
        /// its slot from, in the spelling of the id's node list.</summary>
        [Fact]
        public void TheRowsNode_IsThePadOnItsSlot()
        {
            const string iface = @"\\?\HID#VID_045E&PID_02FF&IG_00#8&3a90f5b&0&0000#{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}";
            XInputSlotIdentity.AnswerForTest = slot => slot == 1
                ? new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Found, iface, 9)
                : new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.None);
            XInputSlotIdentity.InstanceIdForTest = p => p == iface ? NavNode.ToLowerInvariant() : null;

            var nav = Pad("XInput#1", online: true);
            Assert.Equal(NavNode, InputService.OwnXInputNode(nav, Nodes));

            // Offline, another slot, a node outside the list: no node.
            Assert.Null(InputService.OwnXInputNode(Pad("XInput#1", online: false), Nodes));
            Assert.Null(InputService.OwnXInputNode(Pad("XInput#0", online: true), Nodes));
            Assert.Null(InputService.OwnXInputNode(nav, new[] { Ds3Node }));

            // A device that carries another id is another pad's.
            var series = new UserDevice { VendorId = 0x045E, ProdId = 0x0B13, DevicePath = "XInput#1", IsOnline = true };
            Assert.Null(InputService.OwnXInputNode(series, Nodes));

            // A device that serves more than one slot names no single pad.
            XInputSlotIdentity.AnswerForTest = _ => new XInputSlotIdentity.Answer(XInputSlotIdentity.Kind.Found, iface, 9, 4);
            Assert.Null(InputService.OwnXInputNode(nav, Nodes));
        }

        /// <summary>A connected XInput row with hiding off keeps its pad and
        /// the nodes above it out of every other row's hide, as a row with a
        /// real HID path does. Before, it kept nothing out, and an offline row
        /// of the same id set to hide swept the visible pad.</summary>
        [Fact]
        public void AVisibleXInputRow_KeepsItsPadOutOfOtherHides()
        {
            var visibleNav = Pad("XInput#0", online: true);
            var hiddenDs3 = Pad("XInput#1", online: false);
            hiddenDs3.HidHideEnabled = true;
            var noNode = Pad("XInput#2", online: true);
            var asked = new List<UserDevice>();

            var set = InputService.BuildHidHideKeepOut(
                new[] { visibleNav, hiddenDs3, noNode },
                id => new[] { id, @"USB\VID_054C&PID_042F\NAV" },
                rowsCurrent: true, alreadyHidden: null,
                xinputNodeOf: ud => { asked.Add(ud); return ReferenceEquals(ud, visibleNav) ? NavNode : null; });

            Assert.Equal(new[] { NavNode, @"USB\VID_054C&PID_042F\NAV" }.OrderBy(s => s), set.OrderBy(s => s));
            Assert.DoesNotContain(hiddenDs3, asked);
        }

        /// <summary>The slot parser takes SDL's form, "XInput#N" with N below
        /// the fork's sixteen slots, and nothing after it.</summary>
        [Theory]
        [InlineData("XInput#0", 0)]
        [InlineData("XInput#15", 15)]
        [InlineData("xinput#3", 3)]
        [InlineData("XInput#16", -1)]
        [InlineData("XInput#", -1)]
        [InlineData("XInput#1a", -1)]
        [InlineData(@"\\?\HID#VID_045E&PID_02FF&IG_00#8&3a90f5b&0&0000", -1)]
        [InlineData(null, -1)]
        public void TheSlotParser_TakesSdlsForm(string path, int expected)
        {
            Assert.Equal(expected >= 0, XInputSlotIdentity.TryParseSlot(path, out int slot));
            Assert.Equal(expected, slot);
        }

        /// <summary>The Devices row shows the pad on its slot as the node
        /// PadForge hides, ahead of the saved nodes and the id lookup, which
        /// gave both pads of one id the same node. Pinned in source, since the
        /// row needs Windows' device tree.</summary>
        [Fact]
        public void TheDevicesRow_ShowsThePadOnItsSlot()
        {
            string svc = RepoFile("PadForge.App", "Services", "InputService.cs");
            int start = svc.IndexOf("private void PopulateDeviceRow(DeviceRowViewModel row, UserDevice ud, UserDevice[] snapshot)", StringComparison.Ordinal);
            Assert.True(start > 0);
            int own = svc.IndexOf("&& OwnXInputNode(ud, FindInstanceIdsForDevice(ud)) is string ownNode)", start, StringComparison.Ordinal);
            int cached = svc.IndexOf("else if (ud.HidHideInstanceIds.Count > 0)", start, StringComparison.Ordinal);
            Assert.True(own > start && own < cached);
        }

        // ── Helpers ──

        private static UserDevice Pad(string path, bool online = true)
            => new UserDevice { VendorId = Vid, ProdId = Pid, DevicePath = path, IsOnline = online };

        /// <summary>A file standing in for an XUSB interface, under a folder
        /// whose name carries the id the way an interface path does.</summary>
        private string Interface(string name)
        {
            string dir = Path.Combine(_root, "HID#" + name);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "xusb.bin");
            File.WriteAllBytes(file, Array.Empty<byte>());
            return file;
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
