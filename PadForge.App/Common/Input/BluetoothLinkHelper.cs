using System;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Drops a Bluetooth controller's link from the host side (issue #162).
    /// The host radio is told to disconnect the ACL link via
    /// <c>IOCTL_BTH_DISCONNECT_DEVICE</c>, and the controller then puts
    /// itself to sleep on link loss. This is the one mechanism every proven
    /// Windows implementation uses (DS4Windows DS4Device.DisconnectBT,
    /// DsHidMini BluetoothHelper.DisconnectRemoteDevice, Special K
    /// bluetooth.cpp): there is no per-family "power off" HID command.
    ///
    /// <para>The target address comes from the device's HID serial string,
    /// which for Bluetooth controllers IS the controller's MAC (Special K
    /// documents this in-code; DS4Windows' <c>Mac</c> property is that same
    /// serial). The payload is the MAC as one 8-byte little-endian value:
    /// the six address bytes reversed, two zero pad bytes on top, matching
    /// DsHidMini's <c>{0,0} ++ MAC, reversed</c> construction.</para>
    ///
    /// <para>Walks every radio until one accepts the IOCTL, as DS4Windows
    /// and Special K both do. Multi-radio hosts exist and the device is
    /// only linked through one of them. Blocking: call from a worker, never
    /// from the polling thread.</para>
    /// </summary>
    public static class BluetoothLinkHelper
    {
        private const uint IOCTL_BTH_DISCONNECT_DEVICE = 0x41000C;

        /// <summary>Parses a HID serial string ("aa:bb:cc:dd:ee:ff",
        /// "aa-bb-cc-dd-ee-ff", or bare "aabbccddeeff") into the 8-byte
        /// little-endian address value the IOCTL takes. Returns false for
        /// anything that is not exactly six hex octets.</summary>
        public static bool TryParseAddress(string serial, out long address)
        {
            address = 0;
            if (string.IsNullOrWhiteSpace(serial)) return false;

            string hex = serial.Replace(":", "").Replace("-", "").Trim();
            if (hex.Length != 12) return false;

            ulong value = 0;
            for (int i = 0; i < 12; i++)
            {
                char c = hex[i];
                int nibble;
                if (c >= '0' && c <= '9') nibble = c - '0';
                else if (c >= 'a' && c <= 'f') nibble = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') nibble = c - 'A' + 10;
                else return false;
                value = (value << 4) | (uint)nibble;
            }

            address = unchecked((long)value);
            return true;
        }

        /// <summary>Disconnects the Bluetooth device whose HID serial is
        /// <paramref name="serial"/>. Returns true when a radio accepted
        /// the disconnect. Safe to call for any serial: unparseable input
        /// returns false without touching the radio.</summary>
        public static bool TryDisconnect(string serial)
        {
            if (!TryParseAddress(serial, out long address))
                return false;

            var findParams = new BLUETOOTH_FIND_RADIO_PARAMS
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>()
            };

            IntPtr radio = IntPtr.Zero;
            IntPtr find = BluetoothFindFirstRadio(ref findParams, ref radio);
            if (find == IntPtr.Zero)
                return false;

            bool success = false;
            try
            {
                // Walk every radio until one accepts the disconnect, the
                // DS4Windows / Special K loop shape.
                while (!success && radio != IntPtr.Zero)
                {
                    int bytesReturned = 0;
                    success = DeviceIoControl(radio, IOCTL_BTH_DISCONNECT_DEVICE,
                        ref address, 8, IntPtr.Zero, 0, ref bytesReturned, IntPtr.Zero);
                    CloseHandle(radio);
                    radio = IntPtr.Zero;
                    if (!success && !BluetoothFindNextRadio(find, ref radio))
                        break;
                }
            }
            finally
            {
                if (radio != IntPtr.Zero) CloseHandle(radio);
                BluetoothFindRadioClose(find);
            }

            return success;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public uint dwSize;
        }

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstRadio(
            ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, ref IntPtr phRadio);

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        private static extern bool BluetoothFindNextRadio(IntPtr hFind, ref IntPtr phRadio);

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            ref long lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize,
            ref int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
