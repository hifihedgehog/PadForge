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
        private const ushort ValveVid = 0x28DE;
        private const ushort MicrosoftVid = 0x045E;

        /// <summary>Marshaled size of XINPUT_CAPABILITIES_EX, pinned by test:
        /// 20-byte XINPUT_CAPABILITIES + VID/PID/version/pad + DWORD = 32.</summary>
        public static int CapabilitiesExSize => Marshal.SizeOf<XINPUT_CAPABILITIES_EX>();

        /// <summary>Device-aware disconnect (issue #162). The radio IOCTL below
        /// only drops BR/EDR ACL links (every reference uses it on {00001124}
        /// classic-BT pads), so BLE controllers need their own lane:
        /// Valve pads get the Steam protocol's power-off command
        /// (ID_TURN_OFF_CONTROLLER 0x9F, SDL controller_constants.h:74) as a
        /// feature report on the device's own vendor collection, and Xbox pads
        /// get XInputPowerOff (xinput1_4 ordinal 103), slot-matched by VID/PID
        /// through XInputGetCapabilitiesEx (ordinal 108). Everything else falls
        /// through to the BR/EDR link drop by serial (the DS4Windows path).</summary>
        public static bool TryDisconnectDevice(ushort vendorId, ushort productId,
            string devicePath, string serial)
        {
            if (vendorId == ValveVid && TrySteamPowerOff(devicePath))
                return true;

            if (vendorId == MicrosoftVid && TryXInputPowerOff(vendorId, productId))
                return true;

            return TryDisconnect(serial);
        }

        /// <summary>The Steam power-off feature report: report id 0x00, protocol
        /// id 0x9F, payload length 4, the firmware's "off!" confirmation magic.
        /// HandheldCompanion GordonController.cs:94-105 sends exactly these
        /// payload bytes (0x6f 0x66 0x66 0x21); steam_controller_tools
        /// controller.ts:204-206 sends the bare 0x9F to the 2026 controller.
        /// The magic-bearing form is the superset: a validating parser (the
        /// firmware images carry the "off!" string) accepts it, and a parser
        /// that ignores the payload still reads the 0x9F command.</summary>
        public static byte[] BuildSteamPowerOffReport(int featureReportLength)
        {
            var buf = new byte[featureReportLength > 7 ? featureReportLength : 7];
            buf[0] = 0x00; // report id (SDL_hidapi_steam.c sends 0x00 + 64-byte blob)
            buf[1] = 0x9F; // ID_TURN_OFF_CONTROLLER
            buf[2] = 0x04; // payload size
            buf[3] = (byte)'o';
            buf[4] = (byte)'f';
            buf[5] = (byte)'f';
            buf[6] = (byte)'!';
            return buf;
        }

        /// <summary>Sends the Steam power-off on the device's own HID handle,
        /// the raw-write channel HapticToneService already uses for the 2026
        /// controller's haptics (open from DevicePath, query
        /// FeatureReportByteLength from caps, HidD_SetFeature).</summary>
        private static bool TrySteamPowerOff(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;

            IntPtr h = CreateFileW(devicePath, GENERIC_READ | GENERIC_WRITE, SHARE_RW,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == INVALID_HANDLE) return false;

            try
            {
                int featLen = 0;
                if (HidD_GetPreparsedData(h, out IntPtr pp) && pp != IntPtr.Zero)
                {
                    try
                    {
                        if (HidP_GetCaps(pp, out HIDP_CAPS caps) >= 0)
                            featLen = caps.FeatureReportByteLength;
                    }
                    finally { HidD_FreePreparsedData(pp); }
                }
                if (featLen <= 0)
                    return false; // wrong collection: no feature report surface

                byte[] buf = BuildSteamPowerOffReport(featLen);
                return HidD_SetFeature(h, buf, buf.Length);
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>Powers off the XInput slot whose VID/PID matches the target
        /// pad. XInputGetCapabilitiesEx (ordinal 108) exposes VID/PID per slot;
        /// XInputPowerOff (ordinal 103) is what the Xbox Game Bar uses. Both
        /// signatures per Special K include/SpecialK/input/xinput.h:58,162-169,
        /// 187-193,210-212. PadForge's virtual pads carry different PIDs than a
        /// physical Series/One pad, so an exact VID+PID match does not hit our
        /// own slots.</summary>
        private static bool TryXInputPowerOff(ushort vendorId, ushort productId)
        {
            for (uint slot = 0; slot < 4; slot++)
            {
                try
                {
                    if (XInputGetCapabilitiesEx(1, slot, 0, out XINPUT_CAPABILITIES_EX caps) != 0)
                        continue;
                    if (caps.VendorId != vendorId || caps.ProductId != productId)
                        continue;
                    if (XInputPowerOff(slot) == 0)
                        return true;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
            }
            return false;
        }

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

        // ── Steam power-off surface (same HID surface as HapticToneService) ──

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint SHARE_RW = 0x3;
        private const uint OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string path, uint access, uint share,
            IntPtr sa, uint disp, uint flags, IntPtr template);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(IntPtr h, byte[] buffer, int bufferLength);

        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr preparsed);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        // ── Xbox power-off surface (xinput1_4 hidden ordinals, per Special K) ──

        [StructLayout(LayoutKind.Sequential)]
        internal struct XINPUT_CAPABILITIES_EX
        {
            // XINPUT_CAPABILITIES: Type, SubType, Flags, XINPUT_GAMEPAD, XINPUT_VIBRATION
            public byte Type;
            public byte SubType;
            public ushort Flags;
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
            public ushort wLeftMotorSpeed, wRightMotorSpeed;
            // The Ex tail (Special K xinput.h:162-169)
            public ushort VendorId;
            public ushort ProductId;
            public ushort ProductVersion;
            public ushort unk1;
            public uint unk2;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "#108")]
        private static extern uint XInputGetCapabilitiesEx(uint reserved, uint userIndex,
            uint flags, out XINPUT_CAPABILITIES_EX caps);

        [DllImport("xinput1_4.dll", EntryPoint = "#103")]
        private static extern uint XInputPowerOff(uint userIndex);
    }
}
