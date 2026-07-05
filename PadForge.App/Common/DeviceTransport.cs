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
        /// <summary>
        /// True when the device is known to be linked over Bluetooth,
        /// classic BR/EDR or LE. False means "not claimed", never "wired":
        /// paths that carry no transport marker (USB HID, dongles, virtual
        /// sources, XInput#N) simply make no claim.
        /// </summary>
        internal static bool IsBluetooth(string devicePath, ushort vendorId, ushort productId)
        {
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

                // Bluetooth LE HID (HID over GATT): the HOGP service UUID
                // 0x1812 takes the same slot (measured live: Steam Controller
                // 2026 lizard collections
                // \\?\HID#{00001812-...}_Dev_VID&0228de_PID&1303...).
                if (devicePath.IndexOf("{00001812", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                // Xbox pads over Bluetooth surface as "XInput#N"
                // (SDL_xinputjoystick.c:211). The path carries no HID GUID
                // and no BTHENUM, so nothing engine-held distinguishes a BT
                // pad from a wired or dongle one. SDL itself knows
                // (joystick->connection_state, SDL_JOYSTICK_CONNECTION_*),
                // but SDL3Minimal does not bind SDL_GetJoystickConnectionState
                // and UserDevice carries no transport field. The honest
                // answer from engine-held fields is "unknown": no glyph
                // rather than a guess.
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
    }
}
