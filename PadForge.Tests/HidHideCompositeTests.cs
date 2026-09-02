using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common;
using Xunit;
using PnpNode = PadForge.Common.HidHideController.PnpNode;

namespace PadForge.Tests
{
    /// <summary>
    /// Composite pads and the rows the user left visible (#400, discussion
    /// #397). The Legion Go's built-in controller is one USB composite
    /// device whose XUSB node is interface 0 under the composite parent,
    /// with the touchpad, keyboard, and vendor interfaces as HID siblings.
    /// The old expansion, mirroring HidHide's client, blacklisted the HID
    /// node, the base container when blockable, and the base's immediate
    /// HID children, and never the nodes in between. XInput opened the
    /// XUSB interface freely while every HID sibling was hidden. These
    /// trees are the reporter's, the bench's, and the classic shapes the
    /// rule must keep byte-identical.
    /// </summary>
    public class HidHideCompositeTests
    {
        private static readonly Guid Hid = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
        private static readonly Guid Xusb = new("d61ca365-5af4-4486-998b-9db4734c6ca3");
        private static readonly Guid XboxComposite = new("05f5cfe2-4733-4950-a6bb-07aad01a3a84");
        private static readonly Guid Usb = new("36fc9e60-c465-11cf-8056-444553540000");
        private static readonly Guid Media = new("4d36e96c-e325-11ce-bfc1-08002be10318");

        private static readonly Func<string, bool> NothingKept = _ => false;

        private static HashSet<string> Set(IEnumerable<string> ids)
            => new(ids, StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void FilteredClasses_AreTheThreeTheInstallerRegisters()
        {
            Assert.True(HidHideController.IsHidHideFilteredClass(Hid));
            Assert.True(HidHideController.IsHidHideFilteredClass(Xusb));
            Assert.True(HidHideController.IsHidHideFilteredClass(XboxComposite));
            Assert.False(HidHideController.IsHidHideFilteredClass(Usb));
            Assert.False(HidHideController.IsHidHideFilteredClass(Media));
            Assert.False(HidHideController.IsHidHideFilteredClass(Guid.Empty));
        }

        /// <summary>A wired Xbox 360 pad: the XUSB node IS the base
        /// container with one HID child, so the base is blocked, exactly as
        /// before. The xinputhid node between them is HID class and rides
        /// along, which the old rule already produced as the base's child.</summary>
        [Fact]
        public void Wired360_BlocksTheXusbBaseAsBefore()
        {
            const string hid = @"HID\VID_045E&PID_028E&IG_00\7&1A2B3C&0&0000";
            const string xinputHid = @"USB\VID_045E&PID_028E&IG_00\6&1A2B3C&0&0000";
            const string xusb = @"USB\VID_045E&PID_028E\3D5F2A1";
            var kept = new List<string>();

            var result = HidHideController.ComposeBlacklist(hid,
                new[] { new PnpNode(xinputHid, Hid) },
                new PnpNode(xusb, Xusb),
                new[] { new PnpNode(xinputHid, Hid) },
                NothingKept, kept);

            Assert.Equal(hid, result[0]);
            Assert.Equal(Set(new[] { hid, xinputHid, xusb }), Set(result));
            Assert.Equal(3, result.Count);
            Assert.Empty(kept);
        }

        /// <summary>The bench's own USB DualSense: a composite parent with
        /// a MEDIA sibling, so the parent stays unblocked and the list is
        /// the HID child plus its USB interface node. The diag line on
        /// 2026-09-01 read exactly "expanded=2 [HID child | USB MI_03 node]".</summary>
        [Fact]
        public void UsbDualSense_IsByteIdenticalToTheOldRule()
        {
            const string hid = @"HID\VID_054C&PID_0CE6&MI_03\8&127E3E0&0&0000";
            const string mi03 = @"USB\VID_054C&PID_0CE6&MI_03\7&2B4C6D8&0&0003";
            const string mi00 = @"USB\VID_054C&PID_0CE6&MI_00\7&2B4C6D8&0&0000";
            const string composite = @"USB\VID_054C&PID_0CE6\0C27565874D8";
            var kept = new List<string>();

            var result = HidHideController.ComposeBlacklist(hid,
                new[] { new PnpNode(mi03, Hid) },
                new PnpNode(composite, Usb),
                new[] { new PnpNode(mi00, Media), new PnpNode(mi03, Hid) },
                NothingKept, kept);

            Assert.Equal(Set(new[] { hid, mi03 }), Set(result));
            Assert.DoesNotContain(composite, result, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(mi00, result, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(kept);
        }

        private const string LegionHid = @"HID\VID_17EF&PID_6182&IG_00\9&3F1E2D0&0&0000";
        private const string LegionXinputHid = @"USB\VID_17EF&PID_6182&IG_00\8&3F1E2D0&0&0000";
        private const string LegionXusb = @"USB\VID_17EF&PID_6182&MI_00\7&1F2E3D4&0&0000";
        private const string LegionMi01 = @"USB\VID_17EF&PID_6182&MI_01\7&1F2E3D4&0&0001";
        private const string LegionMi02 = @"USB\VID_17EF&PID_6182&MI_02\7&1F2E3D4&0&0002";
        private const string LegionMi03 = @"USB\VID_17EF&PID_6182&MI_03\7&1F2E3D4&0&0003";
        private const string LegionTouchpadHid = @"HID\VID_17EF&PID_6182&MI_01&COL02\8&2A3B4C5&0&0001";
        private const string LegionComposite = @"USB\VID_17EF&PID_6182\5&2E4D6F8&0&3";

        private static List<string> ComposeLegion(Func<string, bool> keepOut, List<string> kept)
            => HidHideController.ComposeBlacklist(LegionHid,
                new[] { new PnpNode(LegionXinputHid, Hid), new PnpNode(LegionXusb, Xusb) },
                new PnpNode(LegionComposite, Usb),
                new[]
                {
                    new PnpNode(LegionXusb, Xusb),
                    new PnpNode(LegionMi01, Hid),
                    new PnpNode(LegionMi02, Hid),
                    new PnpNode(LegionMi03, Hid),
                },
                keepOut, kept);

        /// <summary>THE BUG. The XUSB node is interface 0 of the composite
        /// parent: not the base, not an immediate HID child. It is on the
        /// chain and of a class the driver filters, so it is blacklisted
        /// now. The composite parent itself stays unblocked (mixed
        /// children), which is HidHide's rule and the right one, since
        /// the driver is not on a USB-class stack anyway.</summary>
        [Fact]
        public void LegionGo_BlacklistsTheXusbInterfaceNode()
        {
            var kept = new List<string>();
            var result = ComposeLegion(NothingKept, kept);

            Assert.Contains(LegionXusb, result, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(LegionXinputHid, result, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Set(new[] { LegionHid, LegionXinputHid, LegionXusb, LegionMi01, LegionMi02, LegionMi03 }), Set(result));
            Assert.DoesNotContain(LegionComposite, result, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(kept);
        }

        /// <summary>The second half of the report: the touchpad is its own
        /// row with Hide from Games off. Its HID node and its interface
        /// node are in the keep-out set, so the pad's expansion leaves the
        /// touchpad interface alone and says so, while the keyboard and
        /// vendor interfaces, which PadForge shows as no row, stay with
        /// the pad.</summary>
        [Fact]
        public void LegionGo_LeavesTheVisibleTouchpadRowAlone()
        {
            var keepOutSet = Set(new[] { LegionTouchpadHid, LegionMi01, LegionComposite });
            var kept = new List<string>();
            var result = ComposeLegion(keepOutSet.Contains, kept);

            Assert.Equal(Set(new[] { LegionHid, LegionXinputHid, LegionXusb, LegionMi02, LegionMi03 }), Set(result));
            Assert.Equal(new[] { LegionMi01 }, kept);
        }

        /// <summary>The Xbox 360 wireless receiver: four XUSB nodes under one
        /// composite parent, none of them HID, so the old rule hid the HID
        /// child alone and XInput saw every pad on the receiver. The pad's
        /// own XUSB node is on its chain and is hidden now. The other three
        /// belong to other pads and are never touched.</summary>
        [Fact]
        public void WirelessReceiver_HidesOnlyThePadsOwnXusbNode()
        {
            const string hid = @"HID\VID_045E&PID_0719&IG_00\9&1&0&0000";
            const string xinputHid = @"USB\VID_045E&PID_0719&IG_00\8&1&0&0000";
            const string mi00 = @"USB\VID_045E&PID_0719&MI_00\7&1&0&0000";
            const string mi02 = @"USB\VID_045E&PID_0719&MI_02\7&1&0&0002";
            const string mi04 = @"USB\VID_045E&PID_0719&MI_04\7&1&0&0004";
            const string mi06 = @"USB\VID_045E&PID_0719&MI_06\7&1&0&0006";
            const string composite = @"USB\VID_045E&PID_0719\6&2&0&1";
            var kept = new List<string>();

            var result = HidHideController.ComposeBlacklist(hid,
                new[] { new PnpNode(xinputHid, Hid), new PnpNode(mi00, Xusb) },
                new PnpNode(composite, Usb),
                new[] { new PnpNode(mi00, Xusb), new PnpNode(mi02, Xusb), new PnpNode(mi04, Xusb), new PnpNode(mi06, Xusb) },
                NothingKept, kept);

            Assert.Equal(Set(new[] { hid, xinputHid, mi00 }), Set(result));
            Assert.DoesNotContain(mi02, result, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(composite, result, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A wired Xbox One or Series pad binds to XboxComposite,
        /// a class HidHide's installer filters and its client never treats
        /// as a blockable base. The registry on the bench carries
        /// UpperFilters=HidHide on that class, so the base is blocked here.
        /// Hardware-unverified: no wired GIP pad is on the bench.</summary>
        [Fact]
        public void WiredSeries_BlocksTheXboxCompositeBase()
        {
            const string hid = @"HID\VID_045E&PID_0B12&IG_00\7&1&0&0000";
            const string xinputHid = @"USB\VID_045E&PID_0B12&IG_00\6&1&0&0000";
            const string gip = @"USB\VID_045E&PID_0B12\3032363030303532323531333033";
            var kept = new List<string>();

            var result = HidHideController.ComposeBlacklist(hid,
                new[] { new PnpNode(xinputHid, Hid) },
                new PnpNode(gip, XboxComposite),
                new[] { new PnpNode(xinputHid, Hid) },
                NothingKept, kept);

            Assert.Contains(gip, result, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Set(new[] { hid, xinputHid, gip }), Set(result));
        }

        /// <summary>A Bluetooth pad is a stand-alone HID node: no chain, no
        /// base, no children. The list is the node alone, as it always was.</summary>
        [Fact]
        public void BluetoothPad_IsTheNodeAlone()
        {
            const string hid = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\9&1479B2EE&0&0000";
            var result = HidHideController.ComposeBlacklist(hid,
                Array.Empty<PnpNode>(), new PnpNode(null, Guid.Empty), Array.Empty<PnpNode>(), null, null);
            Assert.Equal(new[] { hid }, result);

            Assert.Empty(HidHideController.ComposeBlacklist(null, null, default, null, null, null));
            Assert.Empty(HidHideController.ComposeBlacklist("", null, default, null, null, null));
        }

        /// <summary>A kept-out child under a blockable base: blocking the
        /// base would hide that row through the parent, so the base is not
        /// blocked and the child is reported, never silently dropped.</summary>
        [Fact]
        public void KeptOutChild_PreventsTheBaseBlock()
        {
            const string hid = @"HID\VID_045E&PID_028E&IG_00\7&1&0&0000";
            const string xinputHid = @"USB\VID_045E&PID_028E&IG_00\6&1&0&0000";
            const string xusb = @"USB\VID_045E&PID_028E\3D5F2A1";
            var kept = new List<string>();

            var result = HidHideController.ComposeBlacklist(hid,
                new[] { new PnpNode(xinputHid, Hid) },
                new PnpNode(xusb, Xusb),
                new[] { new PnpNode(xinputHid, Hid) },
                id => string.Equals(id, xinputHid, StringComparison.OrdinalIgnoreCase), kept);

            Assert.Equal(new[] { hid }, result);
            Assert.Equal(new[] { xinputHid }, kept);
        }

        /// <summary>Case does not split a node: the same id in another
        /// case is one entry, and the keep-out predicate is consulted on it.</summary>
        [Fact]
        public void Composer_DedupesCaseInsensitively()
        {
            const string hid = @"HID\VID_054C&PID_0CE6&MI_03\8&1&0&0000";
            const string mi03Upper = @"USB\VID_054C&PID_0CE6&MI_03\7&1&0&0003";
            string mi03Lower = mi03Upper.ToLowerInvariant();
            var seen = new List<string>();

            var result = HidHideController.ComposeBlacklist(hid,
                new[] { new PnpNode(mi03Upper, Hid) },
                new PnpNode(@"USB\VID_054C&PID_0CE6\1", Usb),
                new[] { new PnpNode(@"USB\VID_054C&PID_0CE6&MI_00\7&1&0&0000", Media), new PnpNode(mi03Lower, Hid) },
                id => { seen.Add(id); return false; }, null);

            Assert.Equal(2, result.Count);
            Assert.Equal(mi03Upper, result[1]);
            Assert.Contains(seen, s => string.Equals(s, mi03Upper, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The keep-out set is built from online rows with a real
        /// HID path and hiding off: each contributes its own node and its
        /// same-container ancestors. Hidden rows, offline rows, and
        /// synthetic paths (XInput#N, web://) contribute nothing.</summary>
        [Fact]
        public void KeepOut_ComesFromVisibleOnlineRowsWithRealPaths()
        {
            var pad = new PadForge.Engine.Data.UserDevice
            {
                VendorId = 0x17EF, ProdId = 0x6182, IsOnline = true, HidHideEnabled = true, DevicePath = "XInput#0",
            };
            var touchpad = new PadForge.Engine.Data.UserDevice
            {
                VendorId = 0x17EF, ProdId = 0x6182, IsOnline = true, HidHideEnabled = false,
                DevicePath = @"\\?\HID#VID_17EF&PID_6182&MI_01&Col02#8&2a3b4c5&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            };
            var offlineKeyboard = new PadForge.Engine.Data.UserDevice
            {
                VendorId = 0x17EF, ProdId = 0x6182, IsOnline = false, HidHideEnabled = false,
                DevicePath = @"\\?\HID#VID_17EF&PID_6182&MI_02#8&2a3b4c5&0&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            };
            var visibleXinput = new PadForge.Engine.Data.UserDevice
            {
                VendorId = 0x045E, ProdId = 0x0B13, IsOnline = true, HidHideEnabled = false, DevicePath = "XInput#1",
            };
            var web = new PadForge.Engine.Data.UserDevice
            {
                IsOnline = true, HidHideEnabled = false, DevicePath = "web://controller/1",
            };
            var hiddenDualSense = new PadForge.Engine.Data.UserDevice
            {
                VendorId = 0x054C, ProdId = 0x0CE6, IsOnline = true, HidHideEnabled = true,
                DevicePath = @"\\?\HID#VID_054C&PID_0CE6&MI_03#8&127e3e0&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            };

            var asked = new List<string>();
            Func<string, IReadOnlyList<string>> chainOf = id =>
            {
                asked.Add(id);
                return new[] { id, id + @"\PARENT", @"USB\VID_17EF&PID_6182\5&2E4D6F8&0&3" };
            };

            var set = PadForge.Services.InputService.BuildHidHideKeepOut(
                new[] { pad, touchpad, offlineKeyboard, visibleXinput, web, null, hiddenDualSense }, chainOf);

            string touchpadId = HidHideController.DevicePathToInstanceId(touchpad.DevicePath);
            Assert.Equal(new[] { touchpadId }, asked);
            Assert.Equal(Set(new[] { touchpadId, touchpadId + @"\PARENT", @"USB\VID_17EF&PID_6182\5&2E4D6F8&0&3" }), set);

            Assert.Empty(PadForge.Services.InputService.BuildHidHideKeepOut(null, chainOf));
            Assert.Empty(PadForge.Services.InputService.BuildHidHideKeepOut(new[] { pad, hiddenDualSense }, chainOf));

            // A chain reader that throws leaves the row with its own id.
            var throwing = PadForge.Services.InputService.BuildHidHideKeepOut(
                new[] { touchpad }, _ => throw new InvalidOperationException());
            Assert.Equal(Set(new[] { touchpadId }), throwing);
        }

        /// <summary>The apply loop threads the keep-out set through every
        /// expansion it performs: the resolved-path branch, the sibling
        /// sweep, and both cached-id loops of the synthetic branch, and the
        /// sweep candidates and cached ids are themselves checked before
        /// expanding. No expansion in that body runs without it.</summary>
        [Fact]
        public void ApplyDeviceHiding_ThreadsTheKeepOutSetEverywhere()
        {
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = svc.IndexOf("public void ApplyDeviceHiding()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = svc.Substring(at, 34000);

            Assert.Contains("var keepOutSet = BuildHidHideKeepOut(snapshot, null);", body);
            Assert.Contains("HIDHIDE keepout n={keepOutSet.Count}", body);
            Assert.Contains("ExpandToBaseContainerAndChildren(instanceId, keepOut, kept)", body);
            Assert.Contains("if (keepOut(realId)) { kept.Add(realId); continue; }", body);
            Assert.Contains("ExpandToBaseContainerAndChildren(realId, keepOut, kept)", body);
            Assert.Contains("realIds.RemoveAll(id =>", body);
            Assert.Contains("if (keepOut(id)) { kept.Add(id); continue; }", body);
            Assert.Contains("ExpandToBaseContainerAndChildren(id, keepOut, kept)", body);
            Assert.Contains("if (keepOut(cachedId)) { kept.Add(cachedId); continue; }", body);
            Assert.Contains("ExpandToBaseContainerAndChildren(cachedId, keepOut, kept)", body);
            Assert.Contains("+ HidHideKeptNote(kept)", body);

            // Every expansion call inside the apply body carries the set.
            int idx = 0;
            while ((idx = body.IndexOf("ExpandToBaseContainerAndChildren(", idx, StringComparison.Ordinal)) >= 0)
            {
                int close = body.IndexOf(')', idx);
                string call = body.Substring(idx, close - idx + 1);
                Assert.Contains("keepOut, kept)", call);
                idx = close;
            }
        }

        private static string RepoText(params string[] parts)
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException(string.Join("/", parts));
        }
    }
}
