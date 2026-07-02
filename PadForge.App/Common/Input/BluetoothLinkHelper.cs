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

        /// <summary>Per-device debounce: a held chord retriggers its macro
        /// every frame (a dozen overlapping dispatches were recorded from one
        /// press), so each device gets one attempt per window.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, long> _lastAttemptTick = new();
        private const int DebounceMs = 3000;

        /// <summary>Device-aware disconnect (issue #162). The radio IOCTL below
        /// only drops BR/EDR ACL links (every reference uses it on {00001124}
        /// classic-BT pads), so BLE controllers need their own lane:
        /// XInput-backend pads (SDL path "XInput#N") get XInputPowerOff with a
        /// devnode-cycle fallback, Valve pads get the Steam protocol's
        /// power-off command (ID_TURN_OFF_CONTROLLER 0x9F, SDL
        /// controller_constants.h:74) on the device's own vendor collection,
        /// Switch 2 pads get their protocol's shutdown through the SDL fork's
        /// effect passthrough, HID-pathed Xbox pads get XInputPowerOff
        /// slot-matched by VID/PID. Everything else falls through to the
        /// BR/EDR link drop by serial (the DS4Windows path, hardware-confirmed
        /// on DualSense and Wii Remote).</summary>
        public static bool TryDisconnectDevice(ushort vendorId, ushort productId,
            string devicePath, string serial,
            System.Collections.Generic.IReadOnlyList<string> bthInstanceIds = null,
            IntPtr gamepadHandle = default)
        {
            string key = devicePath ?? string.Empty;
            long now = Environment.TickCount64;
            lock (_lastAttemptTick)
            {
                if (_lastAttemptTick.TryGetValue(key, out long last) && now - last < DebounceMs)
                    return false;
                _lastAttemptTick[key] = now;
            }

            if (TryParseXInputSlot(devicePath, out _))
            {
                // The stored slot digit goes stale across reconnects (a pad
                // recorded as XInput#1 was measured live on slot 0), so target
                // by VID/PID walk instead. In-process this is the OpenXInput
                // fork's packed index space, which contains ONLY physical pads
                // (HM virtuals never occupy an index, per the XInput filter
                // architecture), so the walk cannot hit our own slots.
                if (TryXInputPowerOff(vendorId, productId))
                    return true;

                // Last resort for Bluetooth LE pads whose driver rejects the
                // power-down (xinputhid refuses it for BLE Series pads,
                // hardware-measured): disable-cycle the pad's BTHLE device
                // node, severing the LE link at the stack. The pad blinks,
                // fails to reconnect, and powers itself down.
                if (TryCycleBthDevNodes(bthInstanceIds))
                    return true;
            }

            if (vendorId == ValveVid && TrySteamPowerOff(devicePath))
                return true;

            if (IsSwitch2(vendorId, productId))
            {
                // The shutdown must travel through SDL's own GATT session: a
                // second session cannot reach the command characteristic
                // (Windows requires ALL openers of a service to be shared, and
                // the driver's session is not). The fork's SendEffect
                // passthrough (SDL#9) is that session's entry point; the
                // direct GATT attempt remains as a fallback for SDL builds
                // that predate it.
                if (TrySwitch2EffectPassthrough(gamepadHandle))
                    return true;
                if (TrySwitch2PowerOff())
                    return true;
            }

            if (vendorId == MicrosoftVid && TryXInputPowerOff(vendorId, productId))
                return true;

            if (TryDisconnect(serial))
                return true;
            return TryCycleBthDevNodes(bthInstanceIds);
        }

        /// <summary>How long a cycled BTHLE node stays disabled. The first
        /// hardware round used 400 ms and the pad reconnected instantly on
        /// re-enable, never registering link loss. A Series pad that loses its
        /// link and cannot reconnect powers itself down within roughly 15
        /// seconds (the console-shutdown behavior), so the node stays down
        /// well past that, then re-enables in the background so the guide
        /// button reconnects normally afterward.</summary>
        private const int DevNodeReEnableMs = 30000;

        /// <summary>Devnodes disabled by <see cref="TryCycleBthDevNodes"/> whose
        /// delayed re-enable has not run yet. <see cref="ReEnablePendingDevNodes"/>
        /// flushes them at shutdown so an app exit inside the 30 s window cannot
        /// strand a pad's Bluetooth node disabled.</summary>
        private static readonly System.Collections.Generic.HashSet<uint> _pendingReEnable = new();

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

                if (CM_Locate_DevNodeW(out uint devInst, id, 0) != 0)
                    continue;
                if (CM_Disable_DevNode(devInst, 0) == 0)
                    disabled.Add(devInst);
            }

            if (disabled.Count == 0) return false;

            lock (_pendingReEnable)
            {
                foreach (uint devInst in disabled)
                    _pendingReEnable.Add(devInst);
            }

            uint[] toEnable = disabled.ToArray();
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(DevNodeReEnableMs).ConfigureAwait(false);
                foreach (uint devInst in toEnable)
                    ReEnableDevNode(devInst);
            });
            return true;
        }

        private static void ReEnableDevNode(uint devInst)
        {
            lock (_pendingReEnable)
            {
                if (!_pendingReEnable.Remove(devInst))
                    return; // already re-enabled (shutdown flush won the race)
            }
            CM_Enable_DevNode(devInst, 0);
        }

        /// <summary>Immediately re-enables every devnode still waiting on its
        /// delayed re-enable. Called from app shutdown: the delayed task dies
        /// with the process, and a stranded-disabled node would leave the pad
        /// unable to reconnect until the user finds it in Device Manager.</summary>
        public static void ReEnablePendingDevNodes()
        {
            uint[] pending;
            lock (_pendingReEnable)
            {
                pending = new uint[_pendingReEnable.Count];
                _pendingReEnable.CopyTo(pending);
                _pendingReEnable.Clear();
            }
            foreach (uint devInst in pending)
                CM_Enable_DevNode(devInst, 0);
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

        /// <summary>Device-aware overload: Switch 2 controllers connect through
        /// the SDL fork's BLE GATT driver, which populates neither DevicePath
        /// nor serial, so the path predicate alone cannot see them. Use this
        /// form at every gate site. Remote Link devices are excluded first:
        /// a peer:// pad relays its real VID/PID, so a linked Switch 2 would
        /// otherwise pass the gate on a machine that has no radio link to it
        /// and can only no-op.</summary>
        public static bool IsDisconnectTarget(string devicePath, ushort vendorId, ushort productId)
        {
            if (devicePath != null && devicePath.StartsWith("peer://", StringComparison.Ordinal))
                return false;
            return IsSwitch2(vendorId, productId) || IsDisconnectTarget(devicePath);
        }

        /// <summary>The Switch 2 family, mirroring SDL usb_ids.h:126-130 in
        /// full (Joy-Con 2 L/R/pair, Pro 2, NSO GameCube), never a hand-picked
        /// subset.</summary>
        public static bool IsSwitch2(ushort vendorId, ushort productId)
        {
            if (vendorId != 0x057E) return false;
            return productId == 0x2066 || productId == 0x2067 || productId == 0x2068
                || productId == 0x2069 || productId == 0x2073;
        }

        /// <summary>The Switch 2 BLE shutdown command, from the protocol
        /// research (switch2_controller_research commands.md, command 0x06
        /// subcommand 0x02, the request the console sends when it sleeps):
        /// 8-byte header (cmd 0x06, direction 0x91 host-to-device, transport
        /// 0x01 Bluetooth, subcmd 0x02, data length 0x0C) plus 12 zero bytes.</summary>
        public static byte[] BuildSwitch2ShutdownCommand()
        {
            var buf = new byte[20];
            buf[0] = 0x06;
            buf[1] = 0x91;
            buf[2] = 0x01;
            buf[3] = 0x02;
            buf[5] = 0x0C;
            return buf;
        }

        /// <summary>Sends the shutdown command to every connected Switch 2
        /// controller through a second GATT client session on the link the
        /// SDL BLE driver already holds. Targeting is family-wide: the driver
        /// exposes no per-joystick BLE address, so one chord shuts down every
        /// connected Switch 2 pad. Service and characteristic UUIDs per
        /// SDL_ble_switch2joystick.c:120,122; the command characteristic is
        /// write-without-response per the #153 hardware log. Blocking, worker
        /// thread only.</summary>
        private static bool TrySwitch2PowerOff()
        {
            try
            {
                return TrySwitch2PowerOffAsync().GetAwaiter().GetResult();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Sends the shutdown as an SDL raw effect: [cmd, subcmd,
        /// payload...], the frame the fork's BLE_JoystickSendEffect
        /// passthrough hands to BLE_SendCommand (which builds the 0x91 BLE
        /// header itself, SDL_ble_switch2joystick.c:793-811). Returns false
        /// while the fork still stubs SendEffect with SDL_Unsupported.</summary>
        private static bool TrySwitch2EffectPassthrough(IntPtr gamepadHandle)
        {
            if (gamepadHandle == IntPtr.Zero)
                return false;
            try
            {
                var effect = new byte[14];
                effect[0] = 0x06; // command: power
                effect[1] = 0x02; // subcommand: shutdown
                // 12 zero payload bytes follow
                return SDL3.SDL.SDL_SendGamepadEffect(gamepadHandle, effect, 0, effect.Length);
            }
            catch
            {
                return false;
            }
        }

        private static readonly Guid Switch2ServiceUuid = new("ab7de9be-89fe-49ad-828f-118f09df7fd0");
        private static readonly Guid Switch2CommandUuid = new("649d4ac9-8eb7-4e6c-af44-1ea54fe5f005");

        private static async System.Threading.Tasks.Task<bool> TrySwitch2PowerOffAsync()
        {
            // Discovery is by CONNECTED LE device, not by service interface:
            // the GattDeviceService selector only surfaces services of PAIRED
            // devices (measured: zero instances while the pad was connected),
            // and Switch 2 controllers connect unpaired (their pairing is a
            // vendor command exchange, not standard SMP, per the protocol
            // research). Each connected LE device gets an uncached service
            // probe over its already-open link; only a Switch 2 answers.
            string selector = Windows.Devices.Bluetooth.BluetoothLEDevice
                .GetDeviceSelectorFromConnectionStatus(
                    Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected);
            var infos = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
            if (infos.Count == 0)
                return false;

            bool any = false;
            foreach (var info in infos)
            {
                Windows.Devices.Bluetooth.BluetoothLEDevice dev = null;
                try
                {
                    dev = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(info.Id);
                    if (dev == null)
                        continue;
                    var svcResult = await dev.GetGattServicesForUuidAsync(Switch2ServiceUuid,
                        Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
                    if (svcResult.Status != Windows.Devices.Bluetooth.GenericAttributeProfile
                            .GattCommunicationStatus.Success
                        || svcResult.Services.Count == 0)
                    {
                        continue; // some other LE peripheral, not a Switch 2
                    }
                    var svc = svcResult.Services[0];
                    try
                    {
                        // Ask for access and open shared before touching
                        // characteristics: a bare query from a second session
                        // is refused while the SDL driver's session holds the
                        // service.
                        await svc.RequestAccessAsync();
                        await svc.OpenAsync(Windows.Devices.Bluetooth
                            .GenericAttributeProfile.GattSharingMode.SharedReadAndWrite);

                        var chars = await svc.GetCharacteristicsForUuidAsync(Switch2CommandUuid);
                        if (chars.Status != Windows.Devices.Bluetooth.GenericAttributeProfile
                                .GattCommunicationStatus.Success
                            || chars.Characteristics.Count == 0)
                        {
                            continue;
                        }
                        using var writer = new Windows.Storage.Streams.DataWriter();
                        writer.WriteBytes(BuildSwitch2ShutdownCommand());
                        var status = await chars.Characteristics[0].WriteValueAsync(
                            writer.DetachBuffer(),
                            Windows.Devices.Bluetooth.GenericAttributeProfile
                                .GattWriteOption.WriteWithoutResponse);
                        any |= status == Windows.Devices.Bluetooth.GenericAttributeProfile
                            .GattCommunicationStatus.Success;
                    }
                    finally
                    {
                        svc.Dispose();
                    }
                }
                catch
                {
                    // Not a Switch 2, or its link dropped mid-probe: skip.
                }
                finally
                {
                    dev?.Dispose();
                }
            }
            return any;
        }

        /// <summary>The XInput user-index space PadForge actually runs in:
        /// SDL enumerates 16 slots (SDL_xinput.h:45 XUSER_MAX_COUNT=16,
        /// SDL_xinputjoystick.c:246), and the bundled OpenXInput fork is
        /// built with OPENXINPUT_XUSER_MAX_COUNT=16, so its ordinals accept
        /// the full range in-process. Microsoft's 4-slot limit does not
        /// apply here.</summary>
        private const uint XInputSlotCount = 16;

        /// <summary>Parses SDL's XInput-backend joystick path ("XInput#N",
        /// SDL_xinputjoystick.c:211, where N is the XInput user index).</summary>
        public static bool TryParseXInputSlot(string devicePath, out uint slot)
        {
            slot = 0;
            const string prefix = "XInput#";
            if (string.IsNullOrEmpty(devicePath)) return false;
            if (!devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return uint.TryParse(devicePath.Substring(prefix.Length), out slot) && slot < XInputSlotCount;
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
                return false;

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

                // Report id by generation, primary pick from the collection's
                // own caps: featLen 64 is the Triton shape (report id 1),
                // featLen 65 the Gordon/Deck shape (report id 0). The other id
                // is tried as fallback. A wrong id fails instantly with
                // ERROR_INVALID_PARAMETER and writes nothing.
                byte primaryId = featLen == 65 ? (byte)0 : (byte)1;
                byte fallbackId = (byte)(1 - primaryId);
                foreach (byte id in new[] { primaryId, fallbackId })
                {
                    byte[] bare = BuildSteamPowerOffReport(featLen, id, withOffMagic: false);
                    bool okBare = HidD_SetFeature(h, bare, bare.Length);

                    byte[] magic = BuildSteamPowerOffReport(featLen, id, withOffMagic: true);
                    bool okMagic = HidD_SetFeature(h, magic, magic.Length);

                    if (okBare || okMagic)
                        return true;
                }
                return false;
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
            for (uint slot = 0; slot < XInputSlotCount; slot++)
            {
                try
                {
                    if (XInputGetCapabilitiesEx(1, slot, 0, out XINPUT_CAPABILITIES_EX caps) != 0)
                        continue;
                    if (caps.VendorId != vendorId || caps.ProductId != productId)
                        continue;
                    if (XInputPowerOff(slot) == 0)
                        any = true;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
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

        // ── BTHLE devnode cycle surface (cfgmgr32, elevation required) ──

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Disable_DevNode(uint devInst, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Enable_DevNode(uint devInst, uint flags);

    }
}
