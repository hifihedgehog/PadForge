using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.WinUsbKeyboards;

namespace PadForge.Services
{
    /// <summary>
    /// What Windows says about a keyboard PadForge reads over WinUSB (#549),
    /// read and never changed here: which bound keyboards are plugged in,
    /// what the Devices page offers a keyboard row, and what else leaves
    /// Windows when a keyboard's node is bound. The binding and its restore
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
                keyboards.Add(new WinUsbKeyboard(path, entry.VendorId, entry.ProductId, entry.Row, entry.Name));
            }
            return keyboards;
        }

        /// <summary>
        /// What the Devices page offers a keyboard row, or null. A Raw Input
        /// keyboard whose collection sits on a USB node that Windows' HID
        /// driver serves is offered the switch to PadForge's driver: the
        /// node is the collection's parent, the whole device or the
        /// interface of a composite device or receiver, so only that
        /// interface moves. The row of a keyboard PadForge's driver serves,
        /// whose path is its WinUSB interface path, is offered the restore.
        /// A keyboard on Bluetooth, PS/2 or a virtual bus has no USB node
        /// and is offered nothing.
        /// </summary>
        internal static VendorUsbDriverInstaller.OptInOffer QueryOffer(string devicePath,
            IReadOnlyList<VendorUsbDriverInstaller.UsbNode> nodes,
            Func<string, string> parentOf = null, Func<string, bool> ours = null)
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
                        return new VendorUsbDriverInstaller.OptInOffer(VendorUsbDriverInstaller.OptInKind.Keyboard,
                            null, From, n.InstanceId, true);
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
                return new VendorUsbDriverInstaller.OptInOffer(VendorUsbDriverInstaller.OptInKind.Keyboard,
                    new VendorUsbDriverInstaller.BindPlan(VendorUsbDriverInstaller.BindId(n),
                        VendorUsbDriverInstaller.NameOf(VendorUsbDriverInstaller.OptInKind.Keyboard)),
                    From, n.InstanceId, false);
            }
            return null;
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
        }

        /// <summary>The collections on the node besides the keyboard's own,
        /// read from this PC, or <see cref="Takes.None"/> when they cannot
        /// be read. The test host reads no real collection.</summary>
        internal static Takes WhatLeaves(string nodeId)
        {
            if (RealHidAccess.Refused || string.IsNullOrEmpty(nodeId)) return Takes.None;
            try
            {
                return WhatLeaves(nodeId, HidCollectionRuntime.Enumerate(), TouchSenseBinding.ParentOf);
            }
            catch
            {
                return Takes.None;
            }
        }

        /// <summary>The same answer from parts a caller hands in. A
        /// collection belongs to the node when the node is its parent, so a
        /// keyboard of the same model on another port answers for itself.
        /// The keyboard's own collections (Generic Desktop keyboard and
        /// keypad) are the switch's subject, not something it takes
        /// along.</summary>
        internal static Takes WhatLeaves(string nodeId, IReadOnlyList<HidCollectionInfo> collections,
            Func<string, string> parentOf)
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
                takes |= Classify(c.UsagePage, c.Usage);
            }
            return takes;
        }

        /// <summary>One collection's kind by its top-level usage (HID Usage
        /// Tables: Generic Desktop 0x01 with Pointer 0x01, Mouse 0x02,
        /// Joystick 0x04, Gamepad 0x05, Keyboard 0x06, Keypad 0x07 and
        /// System Control 0x80, Consumer 0x0C, vendor pages 0xFF00 and up).</summary>
        internal static Takes Classify(ushort usagePage, ushort usage)
        {
            if (usagePage == 0x01)
            {
                return usage switch
                {
                    0x06 or 0x07 => Takes.None,
                    0x01 or 0x02 => Takes.Mouse,
                    0x04 or 0x05 => Takes.GameController,
                    0x80 => Takes.SystemKeys,
                    _ => Takes.Other,
                };
            }
            if (usagePage == 0x0C) return Takes.MediaKeys;
            if (usagePage >= 0xFF00) return Takes.VendorControls;
            return Takes.Other;
        }

        /// <summary>The takeovers whose node Windows put back on its own
        /// driver, for the check at each start: a node of the takeover that
        /// is present on Windows' HID driver again. A keyboard plugged into
        /// another port is a new node, which Windows gives its own driver,
        /// and is no takeover's.</summary>
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
                    if (string.Equals(n.Service ?? string.Empty, "HidUsb", StringComparison.OrdinalIgnoreCase))
                        moved.Add(entry);
                    break;
                }
            }
            return moved;
        }
    }
}
