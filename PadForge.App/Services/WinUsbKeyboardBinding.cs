using System;
using System.Collections.Generic;
using System.Linq;
using Nefarius.Utilities.DeviceManagement.PnP;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.WinUsbKeyboards;

namespace PadForge.Services
{
    /// <summary>
    /// What Windows says about a keyboard PadForge reads over WinUSB (#549),
    /// or a mouse the same switch moved, read and never changed here: which
    /// bound devices are plugged in, what the Devices page offers a keyboard
    /// or mouse row, and what else leaves Windows when its node is bound. The binding and its restore
    /// are <see cref="VendorUsbDriverInstaller"/>'s, made only from the
    /// Devices page after its confirm, as a TouchSense mouse's are
    /// (<see cref="TouchSenseBinding"/>). The test host reads nothing from
    /// the PC it runs on (<see cref="RealTouchSenseAccess"/>): a test hands
    /// in the paths, nodes and collections it means.
    /// </summary>
    internal static class WinUsbKeyboardBinding
    {
        /// <summary>The drivers a keyboard's node is taken from: Windows'
        /// HID driver alone. A node with no driver has no keyboard row to
        /// offer the switch on, and a vendor's driver is not PadForge's to
        /// replace (<see cref="VendorUsbDriverInstaller.Plan"/>).</summary>
        internal static readonly string[] From = { "HidUsb" };

        /// <summary>The bound keyboards that are plugged in, or null when
        /// the look itself failed. A bound keyboard is no HID collection and
        /// no Raw Input device, so its node is found by the interface
        /// PadForge's package registers on it, and its row by the takeover
        /// that names the node.</summary>
        internal static List<WinUsbKeyboard> FindBound()
        {
            if (RealTouchSenseAccess.Refused) return null;
            var paths = VendorUsbDriverInstaller.ActiveInterfacePaths(VendorUsbDriverInstaller.InterfaceGuid);
            return paths == null ? null : BoundKeyboards(paths, KeyboardTakeovers.Current);
        }

        /// <summary>The keyboards among the interface paths of PadForge's
        /// packages: each path a takeover names, with the IDs and the row
        /// the takeover recorded. A path no takeover names is another
        /// device's, a TouchSense mouse's or a vendor pad's.</summary>
        internal static List<WinUsbKeyboard> BoundKeyboards(IEnumerable<string> interfacePaths,
            IReadOnlyList<KeyboardTakeoverEntry> takeovers)
        {
            var keyboards = new List<WinUsbKeyboard>();
            if (interfacePaths == null || takeovers == null || takeovers.Count == 0) return keyboards;
            foreach (string path in interfacePaths)
            {
                var entry = KeyboardTakeovers.ForInterfacePath(takeovers, path);
                if (entry == null || !KeyboardTakeovers.IsValid(entry)) continue;
                keyboards.Add(new WinUsbKeyboard(path, entry.VendorId, entry.ProductId, entry.Row, entry.Name, entry.IsMouse));
            }
            return keyboards;
        }

        /// <summary>
        /// What the Devices page offers a keyboard row, or with
        /// <paramref name="kind"/> Mouse a mouse row, or null. A Raw Input
        /// device whose collection sits on a USB node that Windows' HID
        /// driver serves is offered the switch to PadForge's driver: the
        /// node is the collection's parent, the whole device or the
        /// interface of a composite device or receiver, so only that
        /// interface moves. The row of a device PadForge's driver serves,
        /// whose path is its WinUSB interface path, is offered the restore.
        /// A device on Bluetooth, PS/2, I2C or a virtual bus has no USB node
        /// and is offered nothing.
        /// </summary>
        internal static VendorUsbDriverInstaller.OptInOffer QueryOffer(string devicePath,
            IReadOnlyList<VendorUsbDriverInstaller.UsbNode> nodes,
            Func<string, string> parentOf = null, Func<string, bool> ours = null,
            VendorUsbDriverInstaller.OptInKind kind = VendorUsbDriverInstaller.OptInKind.Keyboard)
        {
            if (string.IsNullOrEmpty(devicePath) || nodes == null) return null;
            parentOf ??= TouchSenseBinding.ParentOf;
            ours ??= VendorUsbDriverInstaller.HasActiveInterface;

            if (TryParseInterfacePath(devicePath, out string boundNode))
            {
                foreach (var n in nodes)
                {
                    if (!string.Equals(n.InstanceId, boundNode, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(n.Service, "WINUSB", StringComparison.OrdinalIgnoreCase) && ours(n.InstanceId))
                        return new VendorUsbDriverInstaller.OptInOffer(kind, null, From, n.InstanceId, true);
                    return null;
                }
                return null;
            }

            string parent;
            try { parent = parentOf(devicePath); }
            catch { parent = null; }
            if (string.IsNullOrEmpty(parent)) return null;
            foreach (var n in nodes)
            {
                if (!string.Equals(n.InstanceId, parent, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(n.Service ?? string.Empty, "HidUsb", StringComparison.OrdinalIgnoreCase)) return null;
                return new VendorUsbDriverInstaller.OptInOffer(kind,
                    new VendorUsbDriverInstaller.BindPlan(VendorUsbDriverInstaller.BindId(n),
                        VendorUsbDriverInstaller.NameOf(kind)),
                    From, n.InstanceId, false);
            }
            return null;
        }

        /// <summary>The restore offered for a takeover's row whatever path the
        /// row holds. A row whose WinUSB channel never opened, for a
        /// descriptor PadForge cannot read, keeps its Raw Input path, which
        /// names a collection that is gone, so the restore is offered by the
        /// node the takeover recorded. A node that is not present is offered
        /// it too: a keyboard moved to another port has a new node, and the
        /// old one may never return, so this is the way out of its takeover.
        /// Null when the node is present on another driver. Restore itself
        /// removes only a package PadForge wrote.</summary>
        internal static VendorUsbDriverInstaller.OptInOffer RestoreOffer(KeyboardTakeoverEntry takeover,
            IReadOnlyList<VendorUsbDriverInstaller.UsbNode> nodes)
        {
            if (takeover == null || nodes == null || !KeyboardTakeovers.IsValid(takeover)) return null;
            foreach (var n in nodes)
            {
                if (!string.Equals(n.InstanceId, takeover.NodeId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(n.Service, "WINUSB", StringComparison.OrdinalIgnoreCase)) return null;
                break;
            }
            return new VendorUsbDriverInstaller.OptInOffer(
                takeover.IsMouse ? VendorUsbDriverInstaller.OptInKind.Mouse : VendorUsbDriverInstaller.OptInKind.Keyboard,
                null, From, takeover.NodeId, true);
        }

        /// <summary>The keyboards and mice on PadForge's package that no
        /// takeover names, read from this PC, or null when the look failed.
        /// The USB nodes are read only when such an interface is there, so a
        /// PC with nothing left over pays for one look at the interfaces. The
        /// test host reads nothing from the PC it runs on.</summary>
        internal static List<KeyboardTakeoverEntry> FindOrphans(IEnumerable<string> optIns)
        {
            if (RealTouchSenseAccess.Refused) return null;
            var paths = VendorUsbDriverInstaller.ActiveInterfacePaths(VendorUsbDriverInstaller.InterfaceGuid);
            if (paths == null) return null;
            var takeovers = KeyboardTakeovers.Current;
            if (paths.All(path => KeyboardTakeovers.ForInterfacePath(takeovers, path) != null))
                return new List<KeyboardTakeoverEntry>();
            return Orphans(paths, VendorUsbDriverInstaller.ListPresentUsbNodes(), takeovers, optIns, NameInTheTree);
        }

        /// <summary>
        /// A takeover for each keyboard or mouse left on PadForge's package
        /// with none, which is what deleting or replacing PadForge.xml leaves:
        /// Windows has no driver for the device and PadForge no record of it,
        /// so it has no row and no Restore.
        ///
        /// <para>Only the take switch puts a HID-class node on the package that
        /// nothing else claims, so a node qualifies when it is on WinUSB, its
        /// compatible IDs say HID class, and no opted-in ID, no opt-in row and
        /// no row of the vendor tables names it. Those keep their own paths. No
        /// class ID, a read that failed included, adopts nothing, and the next
        /// look asks again.</para>
        ///
        /// <para>The kind comes from the interface's boot protocol: Prot_02 is
        /// a mouse. Prot_01 is a keyboard, and so is a HID interface with no
        /// boot protocol, the form an NKRO keyboard's takes. A mouse among
        /// those opens no channel, and its row still offers the Restore. The
        /// row ID is the one a WinUSB row takes with none recorded, so a second
        /// loss of the file finds the same row.</para>
        /// </summary>
        internal static List<KeyboardTakeoverEntry> Orphans(IEnumerable<string> interfacePaths,
            IReadOnlyList<VendorUsbDriverInstaller.UsbNode> nodes, IReadOnlyList<KeyboardTakeoverEntry> takeovers,
            IEnumerable<string> optIns, Func<string, string> nameOf)
        {
            var found = new List<KeyboardTakeoverEntry>();
            if (interfacePaths == null || nodes == null) return found;
            foreach (string path in interfacePaths)
            {
                if (KeyboardTakeovers.ForInterfacePath(takeovers, path) != null) continue;
                if (!TryParseInterfacePath(path, out string nodeId)) continue;
                foreach (var node in nodes)
                {
                    if (!string.Equals(node.InstanceId, nodeId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(node.Service, "WINUSB", StringComparison.OrdinalIgnoreCase)
                        && VendorUsbDriverInstaller.HasCompatibleId(node, HidClassId)
                        && !VendorUsbDriverInstaller.IsClaimedByATable(node)
                        && (optIns == null || !optIns.Any(id => VendorUsbDriverInstaller.Names(node, id))))
                    {
                        bool mouse = node.CompatibleIds.Any(c => c.EndsWith("&Prot_02", StringComparison.OrdinalIgnoreCase));
                        var kind = mouse ? VendorUsbDriverInstaller.OptInKind.Mouse : VendorUsbDriverInstaller.OptInKind.Keyboard;
                        string name = nameOf?.Invoke(node.InstanceId);
                        found.Add(new KeyboardTakeoverEntry
                        {
                            NodeId = node.InstanceId,
                            BindId = VendorUsbDriverInstaller.BindId(node),
                            RowId = (mouse ? WinUsbMouseDevice.IdentityFor(path) : WinUsbKeyboardDevice.IdentityFor(path)).ToString(),
                            Name = string.IsNullOrWhiteSpace(name) ? VendorUsbDriverInstaller.NameOf(kind) : name,
                            VendorId = node.Vid,
                            ProductId = node.Pid,
                            Kind = mouse ? KeyboardTakeoverEntry.MouseKind : null,
                        });
                    }
                    break;
                }
            }
            return found;
        }

        /// <summary>The compatible ID Windows builds from a HID interface.</summary>
        internal const string HidClassId = @"USB\Class_03";

        /// <summary>The name the device itself reports. The package's own name
        /// is what Device Manager shows, and says nothing of which keyboard
        /// this is.</summary>
        private static string NameInTheTree(string instanceId)
        {
            try
            {
                return PnPDevice.GetDeviceByInstanceId(instanceId, DeviceLocationFlags.Normal)
                    .GetProperty<string>(DevicePropertyKey.Device_BusReportedDeviceDesc);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The pending takeovers to give back at a start: nobody kept
        /// them, and the process that asked is gone. A node present on any
        /// driver but PadForge's needs nothing of the restore, the boot task
        /// having got there first, and <see cref="MovedBack"/> drops its
        /// takeover. A node that is not present still has its package in the
        /// driver store, which would take it again on its return.</summary>
        internal static List<KeyboardTakeoverEntry> PendingToRestore(IEnumerable<KeyboardTakeoverEntry> takeovers,
            IReadOnlyList<VendorUsbDriverInstaller.UsbNode> nodes)
        {
            var restore = new List<KeyboardTakeoverEntry>();
            if (takeovers == null || nodes == null) return restore;
            foreach (var entry in takeovers)
            {
                if (entry == null || !entry.Pending || !KeyboardTakeovers.IsValid(entry)) continue;
                bool elsewhere = false;
                foreach (var n in nodes)
                    if (string.Equals(n.InstanceId, entry.NodeId, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(n.Service, "WINUSB", StringComparison.OrdinalIgnoreCase))
                        elsewhere = true;
                if (!elsewhere) restore.Add(entry);
            }
            return restore;
        }

        /// <summary>The node's instance ID in a WinUSB interface path of
        /// PadForge's package, <c>\\?\usb#vid_046d&amp;pid_c54d&amp;mi_01#8&amp;340f50e3&amp;0&amp;0001#{fd826c66-...}</c>:
        /// the instance ID with # for each backslash, then PadForge's
        /// interface GUID. False for any other path, a Raw Input
        /// keyboard's among them.</summary>
        internal static bool TryParseInterfacePath(string path, out string instanceId)
        {
            instanceId = null;
            const string prefix = @"\\?\";
            if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string[] parts = path.Substring(prefix.Length).Split('#');
            if (parts.Length != 4 || !parts[0].Equals("usb", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Guid.TryParse(parts[3], out var guid) || guid != VendorUsbDriverInstaller.InterfaceGuid) return false;
            string id = parts[0] + "\\" + parts[1] + "\\" + parts[2];
            if (!VendorUsbDriverInstaller.TryParseUsbInstanceId(id, out _, out _, out _)) return false;
            instanceId = id;
            return true;
        }

        /// <summary>What else leaves Windows when a node is bound, by the
        /// HID collections whose parent is the node.</summary>
        [Flags]
        internal enum Takes
        {
            None = 0,
            Mouse = 1,
            MediaKeys = 2,
            SystemKeys = 4,
            GameController = 8,
            VendorControls = 16,
            Other = 32,
            Keyboard = 64,
        }

        /// <summary>The collections on the node besides the switched
        /// keyboard's own, or a mouse's own with <paramref name="kind"/>
        /// Mouse, read from this PC, or <see cref="Takes.None"/> when they
        /// cannot be read. The test host reads no real collection.</summary>
        internal static Takes WhatLeaves(string nodeId, VendorUsbDriverInstaller.OptInKind kind = VendorUsbDriverInstaller.OptInKind.Keyboard)
        {
            if (RealHidAccess.Refused || string.IsNullOrEmpty(nodeId)) return Takes.None;
            try
            {
                return WhatLeaves(nodeId, HidCollectionRuntime.Enumerate(), TouchSenseBinding.ParentOf, kind);
            }
            catch
            {
                return Takes.None;
            }
        }

        /// <summary>The same answer from parts a caller hands in. A
        /// collection belongs to the node when the node is its parent, so a
        /// device of the same model on another port answers for itself.
        /// The switched device's own collections (a keyboard's Generic
        /// Desktop keyboard and keypad, a mouse's mouse and pointer) are
        /// the switch's subject, not something it takes along.</summary>
        internal static Takes WhatLeaves(string nodeId, IReadOnlyList<HidCollectionInfo> collections,
            Func<string, string> parentOf,
            VendorUsbDriverInstaller.OptInKind kind = VendorUsbDriverInstaller.OptInKind.Keyboard)
        {
            var takes = Takes.None;
            if (string.IsNullOrEmpty(nodeId) || collections == null || parentOf == null) return takes;
            foreach (var c in collections)
            {
                if (c == null || string.IsNullOrEmpty(c.Path)) continue;
                string parent;
                try { parent = parentOf(c.Path); }
                catch { continue; }
                if (!string.Equals(parent, nodeId, StringComparison.OrdinalIgnoreCase)) continue;
                takes |= Classify(c.UsagePage, c.Usage, kind);
            }
            return takes;
        }

        /// <summary>One collection's kind by its top-level usage (HID Usage
        /// Tables: Generic Desktop 0x01 with Pointer 0x01, Mouse 0x02,
        /// Joystick 0x04, Gamepad 0x05, Keyboard 0x06, Keypad 0x07 and
        /// System Control 0x80, Consumer 0x0C, vendor pages 0xFF00 and up).
        /// The kind the switch moves is its subject and takes nothing.</summary>
        internal static Takes Classify(ushort usagePage, ushort usage,
            VendorUsbDriverInstaller.OptInKind kind = VendorUsbDriverInstaller.OptInKind.Keyboard)
        {
            bool mouse = kind == VendorUsbDriverInstaller.OptInKind.Mouse;
            if (usagePage == 0x01)
            {
                return usage switch
                {
                    0x06 or 0x07 => mouse ? Takes.Keyboard : Takes.None,
                    0x01 or 0x02 => mouse ? Takes.None : Takes.Mouse,
                    0x04 or 0x05 => Takes.GameController,
                    0x80 => Takes.SystemKeys,
                    _ => Takes.Other,
                };
            }
            if (usagePage == 0x0C) return Takes.MediaKeys;
            if (usagePage >= 0xFF00) return Takes.VendorControls;
            return Takes.Other;
        }

        /// <summary>Whether each of these takeovers' nodes is on Windows' own HID
        /// driver, where the Devices page offers its switch again. A node on a
        /// vendor's driver, or on none, gets no such offer.</summary>
        internal static bool AllOnWindowsDriver(IEnumerable<KeyboardTakeoverEntry> takeovers,
            IReadOnlyList<VendorUsbDriverInstaller.UsbNode> nodes)
        {
            foreach (var entry in takeovers)
            {
                bool onHid = false;
                foreach (var n in nodes)
                    if (string.Equals(n.InstanceId, entry.NodeId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(n.Service, "HidUsb", StringComparison.OrdinalIgnoreCase))
                        onHid = true;
                if (!onHid) return false;
            }
            return true;
        }

        /// <summary>The user kept a switch. The package serves every device of
        /// the ID, so each takeover of that ID stands with it: one left pending
        /// would go back at the next start and take the kept device along.</summary>
        internal static void ConfirmKept(IEnumerable<KeyboardTakeoverEntry> takeovers, string bindId)
        {
            foreach (var entry in takeovers)
                if (string.Equals(entry.BindId, bindId, StringComparison.OrdinalIgnoreCase))
                    entry.Pending = false;
        }

        /// <summary>The takeovers whose node left PadForge's driver, for the
        /// check at each start: a node of the takeover that is present on
        /// Windows' HID driver again, or on a vendor's, which no Restore is
        /// offered for. A pending one also goes with no driver, since nothing
        /// restores that. A kept node with no driver stays, as it can be on its
        /// way back to PadForge's. A keyboard plugged into another port is a
        /// new node, which Windows gives its own driver, and is no
        /// takeover's.</summary>
        internal static List<KeyboardTakeoverEntry> MovedBack(IEnumerable<KeyboardTakeoverEntry> takeovers,
            IReadOnlyList<VendorUsbDriverInstaller.UsbNode> nodes)
        {
            var moved = new List<KeyboardTakeoverEntry>();
            if (takeovers == null || nodes == null) return moved;
            foreach (var entry in takeovers)
            {
                if (!KeyboardTakeovers.IsValid(entry)) continue;
                foreach (var n in nodes)
                {
                    if (!string.Equals(n.InstanceId, entry.NodeId, StringComparison.OrdinalIgnoreCase)) continue;
                    string service = n.Service ?? string.Empty;
                    if (!string.Equals(service, "WINUSB", StringComparison.OrdinalIgnoreCase)
                        && (service.Length > 0 || entry.Pending))
                        moved.Add(entry);
                    break;
                }
            }
            return moved;
        }
    }
}
