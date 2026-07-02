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

        /// <summary>Appends one line to C:\PadForge\disconnect-trace.log so a
        /// failed hardware round reads as data instead of a guess. Every lane
        /// of the #162 dispatch logs its inputs and Win32 results. Best-effort:
        /// any I/O failure is swallowed, and the file restarts once it grows
        /// past a quarter megabyte.</summary>
        public static void Trace(string message)
        {
            try
            {
                const string path = @"C:\PadForge\disconnect-trace.log";
                var fi = new System.IO.FileInfo(path);
                if (fi.Exists && fi.Length > 256 * 1024)
                    fi.Delete();
                System.IO.File.AppendAllText(path,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }

        /// <summary>Device-aware disconnect (issue #162). The radio IOCTL below
        /// only drops BR/EDR ACL links (every reference uses it on {00001124}
        /// classic-BT pads), so BLE controllers need their own lane:
        /// XInput-backend pads (SDL path "XInput#N", the N IS the XInput user
        /// index per SDL_xinputjoystick.c:211) get XInputPowerOff on that slot,
        /// Valve pads get the Steam protocol's power-off command
        /// (ID_TURN_OFF_CONTROLLER 0x9F, SDL controller_constants.h:74) as a
        /// feature report on the device's own vendor collection, HID-pathed
        /// Xbox pads get XInputPowerOff slot-matched by VID/PID through
        /// XInputGetCapabilitiesEx (ordinal 108). Everything else falls
        /// through to the BR/EDR link drop by serial (the DS4Windows path).</summary>
        /// <summary>Per-device debounce: a held chord retriggers its macro every
        /// frame, and the first trace-instrumented round recorded a dozen
        /// overlapping dispatches from one press. One attempt per device per
        /// window is the intent.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, long> _lastAttemptTick = new();
        private const int DebounceMs = 3000;

        public static bool TryDisconnectDevice(ushort vendorId, ushort productId,
            string devicePath, string serial,
            System.Collections.Generic.IReadOnlyList<string> bthInstanceIds = null)
        {
            string key = devicePath ?? string.Empty;
            long now = Environment.TickCount64;
            lock (_lastAttemptTick)
            {
                if (_lastAttemptTick.TryGetValue(key, out long last) && now - last < DebounceMs)
                    return false; // debounced, deliberately untraced to keep the log readable
                _lastAttemptTick[key] = now;
            }

            Trace($"dispatch vid={vendorId:X4} pid={productId:X4} path='{devicePath}' serial='{serial}'");

            if (TryParseXInputSlot(devicePath, out _))
            {
                // The stored slot digit goes stale across reconnects (a pad
                // recorded as XInput#1 was measured live on slot 0), so target
                // by VID/PID walk instead. In-process this is the OpenXInput
                // fork's packed index space, which contains ONLY physical pads
                // (HM virtuals never occupy an index, per the XInput filter
                // architecture), so the walk cannot hit our own slots.
                bool off = TryXInputPowerOff(vendorId, productId);
                Trace($"xinput lane result={off}");
                if (off) return true;

                // Last resort for Bluetooth LE pads whose driver rejects the
                // power-down: disable-cycle the pad's BTHLE device node, which
                // severs the LE link at the stack. The node instance ids are
                // already cached per device for HidHide. The pad blinks,
                // retries briefly, then sleeps (same end state DS4Windows'
                // link drop produces on Sony pads).
                if (TryCycleBthDevNodes(bthInstanceIds))
                {
                    Trace("devnode cycle lane succeeded");
                    return true;
                }
            }

            if (vendorId == ValveVid && TrySteamPowerOff(devicePath))
            {
                Trace("steam lane succeeded");
                return true;
            }

            if (vendorId == MicrosoftVid && TryXInputPowerOff(vendorId, productId))
            {
                Trace("xinput lane (hid-pathed) succeeded");
                return true;
            }

            bool ioctl = TryDisconnect(serial);
            Trace($"br/edr ioctl lane result={ioctl}");
            if (!ioctl && TryCycleBthDevNodes(bthInstanceIds))
            {
                Trace("devnode cycle lane succeeded");
                return true;
            }
            return ioctl;
        }

        /// <summary>How long a cycled BTHLE node stays disabled. The first
        /// hardware round used 400 ms and the pad reconnected instantly on
        /// re-enable, never registering link loss. A Series pad that loses its
        /// link and cannot reconnect powers itself down within roughly 15
        /// seconds (the console-shutdown behavior), so the node stays down
        /// well past that, then re-enables in the background so the guide
        /// button reconnects normally afterward.</summary>
        private const int DevNodeReEnableMs = 30000;

        /// <summary>Disables every cached BTHLEDEVICE node for the device,
        /// forcing the Bluetooth LE link down, and re-enables them in the
        /// background after <see cref="DevNodeReEnableMs"/>. Requires
        /// elevation, which PadForge always runs with. Nodes of other paired
        /// pads in the cache go down for the same window; the cache cannot
        /// distinguish the connected pairing. Returns true when at least one
        /// node was disabled.</summary>
        private static bool TryCycleBthDevNodes(
            System.Collections.Generic.IReadOnlyList<string> instanceIds)
        {
            if (instanceIds == null) return false;

            var disabled = new System.Collections.Generic.List<uint>();
            foreach (string id in instanceIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (id.IndexOf("BTHLEDEVICE", StringComparison.OrdinalIgnoreCase) < 0) continue;

                int locate = CM_Locate_DevNodeW(out uint devInst, id, 0);
                if (locate != 0)
                {
                    Trace($"devnode '{id}': locate cr={locate}");
                    continue;
                }
                int disable = CM_Disable_DevNode(devInst, 0);
                Trace($"devnode '{id}': disable cr={disable}");
                if (disable == 0) disabled.Add(devInst);
            }

            if (disabled.Count == 0) return false;

            uint[] toEnable = disabled.ToArray();
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(DevNodeReEnableMs).ConfigureAwait(false);
                foreach (uint devInst in toEnable)
                {
                    int enable = CM_Enable_DevNode(devInst, 0);
                    Trace($"devnode re-enable cr={enable}");
                }
            });
            return true;
        }

        /// <summary>Whether a device can be targeted by the #162 disconnect at
        /// all: a Bluetooth HID path, or an SDL XInput-backend pad. XInput
        /// paths qualify unconditionally: the battery API was measured
        /// unreliable (a live Bluetooth pad reported BATTERY_TYPE_DISCONNECTED),
        /// and a power-off attempt on a wired pad is a harmless no-op. This
        /// predicate gates the macro candidates, the idle countdown, the
        /// Devices-page control, and the Specific-device picker, so all four
        /// surfaces agree.</summary>
        public static bool IsDisconnectTarget(string devicePath)
        {
            if (SonyEffectWriter.IsBluetoothPath(devicePath)) return true;
            return TryParseXInputSlot(devicePath, out _);
        }

        /// <summary>Parses SDL's XInput-backend joystick path ("XInput#N",
        /// SDL_xinputjoystick.c:211, where N is the XInput user index).</summary>
        public static bool TryParseXInputSlot(string devicePath, out uint slot)
        {
            slot = 0;
            const string prefix = "XInput#";
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (!devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return uint.TryParse(devicePath.Substring(prefix.Length), out slot) && slot < 4;
        }


        /// <summary>The Steam power-off feature report: a report id byte, then
        /// protocol id 0x9F (ID_TURN_OFF_CONTROLLER). The report id differs by
        /// generation, measured and cited: the 2026 Triton uses a 64-byte
        /// buffer with report id 1 (SDL_hidapi_steam_triton.c:130,
        /// buffer[HID_FEATURE_REPORT_BYTES] = { 1 }, and the pad's BLE
        /// collection reports FeatureReportByteLength=64; report id 0 there
        /// fails with ERROR_INVALID_PARAMETER, traced on hardware), while the
        /// 2015 Gordon and the Deck use 65 bytes with report id 0
        /// (SDL_hidapi_steamdeck.c:98, SDL_hidapi_steam.c's 0x00 + 64-byte
        /// blob). Payload split also by generation: bare command per
        /// steam_controller_tools controller.ts:204-206 (2026), the "off!"
        /// confirmation magic per HandheldCompanion GordonController.cs:94-105
        /// (2015).</summary>
        public static byte[] BuildSteamPowerOffReport(int featureReportLength, byte reportId, bool withOffMagic)
        {
            var buf = new byte[featureReportLength > 7 ? featureReportLength : 7];
            buf[0] = reportId;
            buf[1] = 0x9F; // ID_TURN_OFF_CONTROLLER
            if (withOffMagic)
            {
                buf[2] = 0x04; // payload size
                buf[3] = (byte)'o';
                buf[4] = (byte)'f';
                buf[5] = (byte)'f';
                buf[6] = (byte)'!';
            }
            return buf;
        }

        /// <summary>Sends the Steam power-off on the device's own HID handle,
        /// the raw-write channel HapticToneService already uses for the 2026
        /// controller's haptics (open from DevicePath, query
        /// FeatureReportByteLength from caps, HidD_SetFeature). Both framings
        /// go out back-to-back: a powered-off pad ignores the second write.</summary>
        private static bool TrySteamPowerOff(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return false;

            IntPtr h = CreateFileW(devicePath, GENERIC_READ | GENERIC_WRITE, SHARE_RW,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == INVALID_HANDLE)
            {
                Trace($"steam open FAILED err={Marshal.GetLastWin32Error()}");
                return false;
            }

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
                {
                    Trace("steam open ok but FeatureReportByteLength=0 (wrong collection)");
                    return false;
                }

                // Report id by generation, primary pick from the collection's
                // own caps: featLen 64 is the Triton shape (report id 1),
                // featLen 65 the Gordon/Deck shape (report id 0). The other id
                // is tried as fallback; a wrong id fails instantly with
                // ERROR_INVALID_PARAMETER and writes nothing.
                byte primaryId = featLen == 65 ? (byte)0 : (byte)1;
                byte fallbackId = (byte)(1 - primaryId);
                bool ok = false;
                foreach (byte id in new[] { primaryId, fallbackId })
                {
                    byte[] bare = BuildSteamPowerOffReport(featLen, id, withOffMagic: false);
                    bool okBare = HidD_SetFeature(h, bare, bare.Length);
                    int errBare = okBare ? 0 : Marshal.GetLastWin32Error();

                    byte[] magic = BuildSteamPowerOffReport(featLen, id, withOffMagic: true);
                    bool okMagic = HidD_SetFeature(h, magic, magic.Length);
                    int errMagic = okMagic ? 0 : Marshal.GetLastWin32Error();

                    Trace($"steam featLen={featLen} id={id} bare={okBare}(err={errBare}) magic={okMagic}(err={errMagic})");
                    if (okBare || okMagic) { ok = true; break; }
                }
                return ok;
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
            bool any = false;
            for (uint slot = 0; slot < 4; slot++)
            {
                try
                {
                    uint capsResult = XInputGetCapabilitiesEx(1, slot, 0, out XINPUT_CAPABILITIES_EX caps);
                    if (capsResult != 0)
                    {
                        Trace($"xinput slot {slot}: caps err={capsResult}");
                        continue;
                    }
                    if (caps.VendorId != vendorId || caps.ProductId != productId)
                    {
                        Trace($"xinput slot {slot}: vid={caps.VendorId:X4} pid={caps.ProductId:X4} (no match)");
                        continue;
                    }
                    uint offResult = XInputPowerOff(slot);
                    Trace($"xinput slot {slot}: MATCH, PowerOff={offResult}");
                    if (offResult == 0)
                        any = true;
                }
                catch (DllNotFoundException) { Trace("xinput: dll missing"); return false; }
                catch (EntryPointNotFoundException) { Trace("xinput: ordinal missing"); return false; }
            }
            return any;
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
            {
                Trace($"ioctl: serial '{serial}' not a MAC, skipped");
                return false;
            }

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

        // ── BTHLE devnode cycle surface (cfgmgr32, elevation required) ──

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Disable_DevNode(uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Enable_DevNode(uint devInst, uint flags);

    }
}
