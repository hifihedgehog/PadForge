using System;

namespace PadForge.Common
{
    /// <summary>
    /// Shared link-transport classifier for display surfaces (#175): the
    /// dossier LINK row and the slot-card transport glyph ask one question,
    /// "is this device linked over Bluetooth?", and both must answer from
    /// the fields the engine actually holds (DevicePath plus VID/PID).
    /// Distinct from <c>SonyEffectWriter.IsBluetoothPath</c>, which gates
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
                // (SDL_xinputjoystick.c:211). The path carries no HID GUID
                // and no BTHENUM, and an SDL binding would not help either:
                // the XInput driver never assigns joystick->connection_state
                // (zero occurrences in SDL_xinputjoystick.c), so
                // SDL_GetJoystickConnectionState always answers UNKNOWN
                // there. XINPUT_CAPS_WIRELESS cannot answer either: pads in
                // Bluetooth mode speak XUSB 1.1, whose capability reply
                // carries no Flags field (GamepadCapabilities0101,
                // OpenXinput.cpp:962-990), so on that protocol the flag is
                // set only for the 360 2.4GHz wireless receiver
                // (OpenXinput.cpp:977-987). The Bluetooth answer for these
                // pads is the VID/PID check above. An XInput pad reaching
                // this line is a Microsoft pad in its non-Bluetooth identity
                // (USB, Xbox Wireless Adapter, or the 360 receiver) or a
                // third-party pad, and makes no claim.
                return false;
            }

            // Fork BLE Switch 2: the SDL fork's BLE GATT driver surfaces no
            // device path at all (BLE_JoystickGetDevicePath returns NULL,
            // SDL_ble_switch2joystick.c:1884-1888; SDL3Minimal.SDL_GetJoystickPath
            // coalesces that to string.Empty). A Switch 2-family VID/PID with
            // an empty path is therefore that driver, which is Bluetooth LE by
            // construction (BLE_JoystickOpen sets
            // SDL_JOYSTICK_CONNECTION_WIRELESS, SDL_ble_switch2joystick.c:1969).
            // A wired Switch 2 rides hidapi/libusb and always carries a real
            // path (HIDAPI_JoystickGetDevicePath returns device->path,
            // SDL_hidapijoystick.c:1470-1481), and a Remote Link relay carries
            // peer://, so neither lands in this branch.
            return Input.BluetoothLinkHelper.IsSwitch2(vendorId, productId);
        }

        /// <summary>Bluetooth-mode product IDs on VID 0x045E, mirroring every
        /// 0x045e entry the SDL fork's controller_list.h marks Bluetooth or
        /// BLE, in full, never a hand-picked subset. Microsoft firmware uses
        /// these PIDs exclusively over Bluetooth (wired and Xbox Wireless
        /// Adapter links report different PIDs), so a match is a positive
        /// Bluetooth fact. The 360-era "Wireless" entries
        /// (controller_list.h:201-206) are the 2.4GHz proprietary receiver,
        /// not Bluetooth, and are deliberately absent.</summary>
        private static bool IsXboxBluetoothPid(ushort productId)
        {
            switch (productId)
            {
                case 0x02E0: // Xbox One S Controller (Bluetooth), controller_list.h:365
                case 0x02FD: // Xbox One S Controller (Bluetooth), controller_list.h:367
                case 0x0B0C: // Xbox Adaptive Controller (Bluetooth), controller_list.h:370
                case 0x0B13: // Xbox Series X Controller (BLE), controller_list.h:372
                case 0x0B20: // Xbox One S Controller (BLE), controller_list.h:373
                case 0x0B21: // Xbox Adaptive Controller (BLE), controller_list.h:374
                case 0x0B05: // Xbox One Elite Series 2 Controller (Bluetooth), controller_list.h:643
                case 0x0B22: // Xbox One Elite Series 2 Controller (BLE), controller_list.h:644
                    return true;
                default:
                    return false;
            }
        }
    }
}
