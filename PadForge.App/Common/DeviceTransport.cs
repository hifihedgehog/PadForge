using System;
using System.Collections.Generic;
using PadForge.Engine;

namespace PadForge.Common
{
    /// <summary>How a device reaches the PC, where PadForge knows it
    /// (<see cref="DeviceTransport.LinkOf(string, ushort, ushort)"/>). None
    /// makes no claim.</summary>
    public enum DeviceLink { None, Bluetooth, Usb, Ps2, I2c }

    /// <summary>
    /// Shared link-transport classifier for display surfaces (#175): the
    /// dossier LINK row and the slot-card transport glyph ask one question,
    /// "is this device linked over Bluetooth?", and both must answer from
    /// the fields the engine actually holds (DevicePath plus VID/PID).
    /// Distinct from <c>PlayStationEffectWriter.IsBluetoothPath</c>, which gates
    /// Sony OUTPUT-report framing (CRC footers) and the #162 disconnect
    /// lanes. That predicate stays untouched by design.
    /// </summary>
    internal static class DeviceTransport
    {
        private const ushort MicrosoftVid = 0x045E;

        /// <summary>
        /// True when the device is known to be linked over Bluetooth,
        /// classic BR/EDR or LE. False means "not claimed", never "wired":
        /// paths that carry no transport marker (USB HID, dongles, virtual
        /// sources) simply make no claim.
        /// </summary>
        internal static bool IsBluetooth(string devicePath, ushort vendorId, ushort productId)
        {
            // Microsoft Xbox One/Series pads enumerate with different
            // product IDs in Bluetooth mode than wired, so on VID 0x045E the
            // PID alone is a positive Bluetooth fact, independent of path
            // form. This is what answers for live pads (whose "XInput#N"
            // path carries no transport marker, see below) and for
            // cached/offline rows, whose VID/PID persist in PadForge.xml.
            if (vendorId == MicrosoftVid && IsXboxBluetoothPid(productId))
                return true;

            // Combined gen-1 Joy-Con pair (PID 0x2008): another identity
            // fact, and it must precede the path block because SDL gives the
            // pair a NON-EMPTY synthetic path ("nintendo_joycons_combined",
            // SDL_hidapijoystick.c:1300) that carries no BT marker, so that
            // block would answer false before any tail check ran. Both
            // halves hold the classic-BT links a single Joy-Con passes with
            // (BluetoothLinkHelper.IsJoyConPair documents exactly this
            // blind spot), and Joy-Cons only combine wirelessly, so the
            // pair is Bluetooth by construction. Without this the pair
            // failed the transport gate: its NFC reader never armed while
            // the picker still offered its tag sources (audit 2026-07-24,
            // lens 1m).
            if (Input.BluetoothLinkHelper.IsJoyConPair(vendorId, productId))
                return true;

            if (!string.IsNullOrEmpty(devicePath))
            {
                // Classic Bluetooth HID (BR/EDR): the HID service class UUID
                // 0x1124 leads every classic-BT HID path (measured live:
                // DualSense \\?\HID#{00001124-...}_VID&0002054c_PID&0ce6...),
                // and non-HID Bluetooth children enumerate under BTHENUM.
                if (devicePath.IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (devicePath.StartsWith(@"\\?\BTHENUM", StringComparison.OrdinalIgnoreCase))
                    return true;

                // The DS3 bridge's Bluetooth transport is the BthPS3 raw PDO, whose path
                // enumerates under BTHPS3BUS (not a standard BT HID class). It is a
                // Bluetooth link by construction. Its USB counterpart is a \\?\usb#... path
                // that carries no BT marker, so it correctly makes no claim (wired).
                if (devicePath.IndexOf("BTHPS3", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                // Bluetooth LE HID (HID over GATT): the HOGP service UUID
                // 0x1812 takes the same slot (measured live: Steam Controller
                // 2026 lizard collections
                // \\?\HID#{00001812-...}_Dev_VID&0228de_PID&1303...).
                if (devicePath.IndexOf("{00001812", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                // Xbox pads over Bluetooth surface as "XInput#N"
                // (SDL_xinputjoystick.c:181). The path carries no HID GUID
                // and no BTHENUM, and an SDL binding would not help either:
                // the XInput driver sets joystick->connection_state to
                // UNKNOWN when it opens a pad (SDL_xinputjoystick.c:427-428),
                // so SDL_GetJoystickConnectionState always answers UNKNOWN
                // there. XINPUT_CAPS_WIRELESS cannot answer either: pads in
                // Bluetooth mode speak XUSB 1.1, whose capability reply
                // carries no Flags field (GamepadCapabilities0101,
                // OpenXinput.cpp:1022-1050), so on that protocol the flag is
                // set only for the 360 2.4GHz wireless receiver
                // (OpenXinput.cpp:1037-1047). The Bluetooth answer for these
                // pads is the VID/PID check above. An XInput pad reaching
                // this line is a Microsoft pad in its non-Bluetooth identity
                // (USB, Xbox Wireless Adapter, or the 360 receiver) or a
                // third-party pad, and makes no claim.
                return false;
            }

            // Fork BLE Switch 2: the SDL fork's BLE GATT driver surfaces no
            // device path at all (BLE_PublishController records none,
            // SDL_ble_switch2joystick.c:658-675, so SDL_GetJoystickPath returns NULL,
            // SDL_joystick.c:3147-3152, and SDL3Minimal.SDL_GetJoystickPath
            // coalesces that to string.Empty). A Switch 2-family VID/PID with
            // an empty path is therefore that driver, which is Bluetooth LE by
            // construction (BLE_JoystickOpen sets
            // SDL_JOYSTICK_CONNECTION_WIRELESS, SDL_ble_switch2joystick.c:2702).
            // A wired Switch 2 rides hidapi/libusb and always carries a real
            // path (HIDAPI_CreateJoystickDevice records the HID device's path,
            // SDL_hidapijoystick.c:1619, which the open copies to the joystick,
            // SDL_joystick.c:2354-2355), and a Remote Link relay carries
            // peer://, so neither lands in this branch.
            return Input.BluetoothLinkHelper.IsSwitch2(vendorId, productId);
        }

        /// <summary>The device's link, from its path and ids: Bluetooth where
        /// <see cref="IsBluetooth"/> says so, and otherwise what the device
        /// tree shows (<see cref="LinkOf(IEnumerable{AncestryHop})"/>), read
        /// once for each path. A path the tree does not hold is answered None
        /// and kept like any other answer, so an offline row does not walk the
        /// tree at every read. A row coming online calls
        /// <see cref="ForgetLink"/>, so a device first seen while it was away,
        /// or read from a walk that ended before it settled, is read again
        /// then.</summary>
        internal static DeviceLink LinkOf(string devicePath, ushort vendorId, ushort productId)
            => LinkOf(devicePath, vendorId, productId, TreeWalk, s_links);

        /// <summary><see cref="LinkOf(string, ushort, ushort)"/> with the walk
        /// and the paths already read handed in, for the tests.</summary>
        internal static DeviceLink LinkOf(string devicePath, ushort vendorId, ushort productId,
            Func<string, IEnumerable<AncestryHop>> walk, Dictionary<string, DeviceLink> known)
        {
            if (IsBluetooth(devicePath, vendorId, productId)) return DeviceLink.Bluetooth;
            // Only a Windows device path names a node: XInput#N, an SDL
            // driver's own name and PadForge's own schemes (midi://, web://)
            // do not.
            if (devicePath == null || !devicePath.StartsWith(@"\\?\", StringComparison.Ordinal))
                return DeviceLink.None;
            lock (known)
                if (known.TryGetValue(devicePath, out var link)) return link;
            var read = LinkOf(walk(devicePath));
            lock (known) known[devicePath] = read;
            return read;
        }

        /// <summary>The walk <see cref="LinkOf(string, ushort, ushort)"/>
        /// reads the device tree with. The page pictures stand a walk of
        /// their own in for it.</summary>
        internal static Func<string, IEnumerable<AncestryHop>> TreeWalk = WalkTheTree;

        /// <summary>Set by the test host: the PC's device tree is not read,
        /// so a row's link is what its path and ids say, whatever devices
        /// the test machine holds. False in production.</summary>
        internal static bool OffLimitsForTest;

        private static IEnumerable<AncestryHop> WalkTheTree(string devicePath)
            => OffLimitsForTest ? null : Input.InputManager.AncestryHopsOf(devicePath);

        private static readonly Dictionary<string, DeviceLink> s_links = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forgets one path's link, so the next read walks the tree
        /// again. A row calls it when it comes online.</summary>
        internal static void ForgetLink(string devicePath)
        {
            if (devicePath == null) return;
            lock (s_links) s_links.Remove(devicePath);
        }

        /// <summary>Forgets every path's link, for the page pictures, which
        /// read their devices through a walk of their own.</summary>
        internal static void ForgetLinks()
        {
            lock (s_links) s_links.Clear();
        }

        /// <summary>The link the device tree shows, from the walk up from a
        /// device's node: its own node first, then each node above it, as
        /// Step 1's guard reads them (<see cref="HidMaestroAncestry.MaxHops"/>
        /// at most). The driver Windows loads names a device's transport, and
        /// the node a USB device hangs from tells hardware from software.
        ///
        /// <para>i8042prt drives a PS/2 keyboard or mouse. hidi2c is HID over
        /// I2C, a laptop's touchpad or keyboard. A node the Bluetooth stack
        /// enumerates, BTHENUM for classic HID and BTHLEDevice for HID over
        /// GATT, is Bluetooth. A USB node is USB once the walk reaches the
        /// hardware its host controller hangs from, a PCI or ACPI node. A node
        /// ROOT or SWD enumerates is software: a virtual controller, a remote
        /// desktop's mouse, or the host controller usbip-win2 or ViGEmBus
        /// puts a virtual USB device under. Software makes no claim, and
        /// neither does a walk that ends before it settles.</para>
        ///
        /// <para>Measured 2026-11-17 on a laptop: its keyboard
        /// HID\VID_048D&amp;PID_C197 under USB\VID_048D&amp;PID_C197 (HidUsb),
        /// two USB hubs and PCI. Its PS/2 keyboard ACPI\KBC8042 (i8042prt).
        /// Its touchpad HID\GXTP5100 under ACPI\GXTP5100 (hidi2c). A
        /// DualSense under BTHENUM (HidBth), an Xbox Series X controller
        /// under BTHLEDevice, a HIDMaestro controller under SWD, and a remote
        /// desktop's mouse under ROOT\HIDCLASS.</para></summary>
        internal static DeviceLink LinkOf(IEnumerable<AncestryHop> hops)
        {
            if (hops == null) return DeviceLink.None;
            bool usb = false;
            int read = 0;
            foreach (var hop in hops)
            {
                if (string.Equals(hop.Service, "i8042prt", StringComparison.OrdinalIgnoreCase)) return DeviceLink.Ps2;
                if (string.Equals(hop.Service, "hidi2c", StringComparison.OrdinalIgnoreCase)) return DeviceLink.I2c;
                ReadOnlySpan<char> enumerator = EnumeratorOf(hop.InstanceId);
                if (enumerator.Equals("BTHENUM", StringComparison.OrdinalIgnoreCase)
                    || enumerator.Equals("BTHLEDEVICE", StringComparison.OrdinalIgnoreCase)
                    || enumerator.Equals("BTHLE", StringComparison.OrdinalIgnoreCase)
                    || enumerator.Equals("BTH", StringComparison.OrdinalIgnoreCase))
                    return DeviceLink.Bluetooth;
                if (enumerator.Equals("ROOT", StringComparison.OrdinalIgnoreCase)
                    || enumerator.Equals("SWD", StringComparison.OrdinalIgnoreCase))
                    return DeviceLink.None;
                if (enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase))
                    usb = true;
                else if (enumerator.Equals("PCI", StringComparison.OrdinalIgnoreCase)
                    || enumerator.Equals("ACPI", StringComparison.OrdinalIgnoreCase))
                    return usb ? DeviceLink.Usb : DeviceLink.None;
                if (++read == HidMaestroAncestry.MaxHops) break;
            }
            return DeviceLink.None;
        }

        /// <summary>The bus that enumerated a node: its instance id up to the
        /// first backslash ("USB" of USB\VID_046D&amp;PID_C547\...).</summary>
        private static ReadOnlySpan<char> EnumeratorOf(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return ReadOnlySpan<char>.Empty;
            int end = instanceId.IndexOf('\\');
            return end < 0 ? instanceId.AsSpan() : instanceId.AsSpan(0, end);
        }

        /// <summary>Bluetooth-mode product IDs on VID 0x045E, mirroring every
        /// 0x045e entry the SDL fork's controller_list.h marks Bluetooth or
        /// BLE, in full, never a hand-picked subset. Microsoft firmware uses
        /// these PIDs exclusively over Bluetooth (wired and Xbox Wireless
        /// Adapter links report different PIDs), so a match is a positive
        /// Bluetooth fact. The 360-era "Wireless" entries
        /// (controller_list.h:204-209) are the 2.4GHz proprietary receiver,
        /// not Bluetooth, and are deliberately absent.</summary>
        private static bool IsXboxBluetoothPid(ushort productId)
        {
            switch (productId)
            {
                case 0x02E0: // Xbox One S Controller (Bluetooth), controller_list.h:370
                case 0x02FD: // Xbox One S Controller (Bluetooth), controller_list.h:372
                case 0x0B0C: // Xbox Adaptive Controller (Bluetooth), controller_list.h:375
                case 0x0B13: // Xbox Series X Controller (BLE), controller_list.h:377
                case 0x0B20: // Xbox One S Controller (BLE), controller_list.h:378
                case 0x0B21: // Xbox Adaptive Controller (BLE), controller_list.h:379
                case 0x0B05: // Xbox One Elite Series 2 Controller (Bluetooth), controller_list.h:659
                case 0x0B22: // Xbox One Elite Series 2 Controller (BLE), controller_list.h:660
                    return true;
                default:
                    return false;
            }
        }
    }
}
