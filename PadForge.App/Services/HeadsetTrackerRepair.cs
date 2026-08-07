using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// Repairs a Sony headset whose head-tracker HID node is missing or
    /// stuck (issue #188). The reference implementation
    /// (NicholasSlattery/sony-head-tracker, MIT, bluetooth.cpp) documents
    /// the cold-boot state where Windows pairs the headset but never
    /// creates the head-tracker HID child as the NORMAL first-run state,
    /// and ships two fixes, both ported here to run in-process since
    /// PadForge is always elevated:
    ///
    ///  1. rebindBluetoothHid: for the paired Classic device matching the
    ///     row's name, request the HID service (GUID 0x1124). When the
    ///     Bluetooth database says the service is enabled but no live HID
    ///     child node exists, that stale state is cycled
    ///     (disable, 1.5 s, enable). A live HID service is never toggled.
    ///  2. useGenericHidDriver: a node parked at CM_PROB_FAILED_START
    ///     under a BTHENUM parent whose hardware ID carries
    ///     UP:0020_U:00E1 gets the inbox input.inf generic HID driver
    ///     rebound. Applied only when exactly one such node exists.
    ///
    /// The row button runs both passes. Pass 2 additionally runs
    /// UNATTENDED from the enumeration sweep (<see cref="TryAutoRebind"/>),
    /// because the first hardware encounter proved the failed-start state
    /// is the entry condition, not an edge case: with the node parked at
    /// Code 10 there is no device row, so a button-only repair is
    /// unreachable exactly when it is needed. The reference reaches the
    /// same conclusion ("Repair Tracker is the recommended first step
    /// when you open the app"). The service cycle of pass 1 stays
    /// button-only, since it can bounce a live Bluetooth HID service.
    /// </summary>
    internal static class HeadsetTrackerRepair
    {
        internal enum Outcome
        {
            /// <summary>The HID service was requested; PnP enumeration follows.</summary>
            ServiceRequested,
            /// <summary>The service was already live and a failed-start node was rebound.</summary>
            DriverRebound,
            /// <summary>Service already enabled with a live node; nothing to repair.</summary>
            NothingToRepair,
            /// <summary>No paired Bluetooth device matched the row's name.</summary>
            DeviceNotFound,
            /// <summary>Both passes ran and neither could change anything.</summary>
            Failed
        }

        /// <summary>
        /// Runs both repair passes for the paired device matching
        /// <paramref name="deviceName"/> (case-insensitive contains, the
        /// reference's --name path). Blocking Bluetooth and SetupAPI work:
        /// call from a worker thread.
        /// </summary>
        internal static Outcome Run(string deviceName, Action<string> log)
        {
            log ??= _ => { };
            // An empty name would "contain-match" every paired device and
            // cycle the HID service on unrelated mice and keyboards. The
            // reference's service cycle is likewise never run unfiltered
            // against devices that did not qualify via SDP.
            if (string.IsNullOrWhiteSpace(deviceName))
                return Outcome.DeviceNotFound;
            bool serviceRequested = false;
            bool matched = false;

            var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>()
            };
            IntPtr findRadio = BluetoothFindFirstRadio(ref radioParams, out IntPtr radio);
            if (findRadio == IntPtr.Zero)
            {
                log("No Bluetooth radio is available.");
                return Outcome.Failed;
            }
            try
            {
                do
                {
                    try
                    {
                        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
                        {
                            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                            fReturnAuthenticated = 1,
                            fReturnRemembered = 1,
                            fReturnConnected = 1,
                            hRadio = radio
                        };
                        var device = new BLUETOOTH_DEVICE_INFO
                        {
                            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>()
                        };
                        IntPtr find = BluetoothFindFirstDevice(ref search, ref device);
                        if (find == IntPtr.Zero) continue;
                        try
                        {
                            do
                            {
                                if (string.IsNullOrEmpty(device.szName)
                                    || device.szName.IndexOf(deviceName ?? "", StringComparison.OrdinalIgnoreCase) < 0
                                    && (deviceName ?? "").IndexOf(device.szName, StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    device = new BLUETOOTH_DEVICE_INFO
                                    { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
                                    continue;
                                }
                                matched = true;
                                bool liveNode = HasPresentBluetoothHidChild(device.Address);
                                var hidService = HidServiceGuid;
                                uint enable = BluetoothSetServiceState(radio, ref device, ref hidService, BLUETOOTH_SERVICE_ENABLE);
                                log($"HID service enable for '{device.szName}': rc={enable}, live child={liveNode}");
                                if (enable == ERROR_SUCCESS)
                                {
                                    serviceRequested = true;
                                }
                                else if (!liveNode && (enable == ERROR_INVALID_PARAMETER || enable == E_INVALIDARG))
                                {
                                    // Stale enabled state and no node: cycle
                                    // only the absent service (reference gate).
                                    uint disable = BluetoothSetServiceState(radio, ref device, ref hidService, BLUETOOTH_SERVICE_DISABLE);
                                    log($"Stale service state; disable rc={disable}");
                                    if (disable == ERROR_SUCCESS || disable == ERROR_INVALID_PARAMETER || disable == E_INVALIDARG)
                                    {
                                        Thread.Sleep(1500);
                                        uint recover = BluetoothSetServiceState(radio, ref device, ref hidService, BLUETOOTH_SERVICE_ENABLE);
                                        log($"Recovery enable rc={recover}");
                                        if (recover == ERROR_SUCCESS) serviceRequested = true;
                                    }
                                }
                                else if (liveNode && (enable == ERROR_INVALID_PARAMETER || enable == E_INVALIDARG))
                                {
                                    // Already enabled with a live node: the
                                    // failed-start rebind below is the only
                                    // remaining repair.
                                }
                                device = new BLUETOOTH_DEVICE_INFO
                                { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
                            } while (BluetoothFindNextDevice(find, ref device));
                        }
                        finally { BluetoothFindDeviceClose(find); }
                        if (matched) break;
                    }
                    finally
                    {
                        if (radio != IntPtr.Zero) { CloseHandle(radio); radio = IntPtr.Zero; }
                    }
                } while (BluetoothFindNextRadio(findRadio, out radio));
            }
            finally
            {
                if (radio != IntPtr.Zero) CloseHandle(radio);
                BluetoothFindRadioClose(findRadio);
            }

            if (!matched)
            {
                log($"No paired Bluetooth device matched '{deviceName}'.");
                return Outcome.DeviceNotFound;
            }
            if (serviceRequested)
            {
                // PnP enumeration follows the service request (reference
                // waits 5 s before rechecking).
                Thread.Sleep(5000);
                PadForge.Common.Input.SonyHeadsetMotionRuntime.InvalidateCache();
                return Outcome.ServiceRequested;
            }

            // Pass 2: rebind a failed-start head-tracker node to the inbox
            // generic HID driver.
            var rebind = RebindFailedStartNode(log);
            if (rebind == Outcome.DriverRebound)
                PadForge.Common.Input.SonyHeadsetMotionRuntime.InvalidateCache();
            return rebind;
        }

        /// <summary>
        /// Sweep-callable half of the repair: only the precisely-targeted
        /// generic-HID rebind of a CM_PROB_FAILED_START head-tracker node.
        /// Safe unattended because it requires exactly one matching node
        /// and touches nothing else. Returns DriverRebound when it changed
        /// the binding, NothingToRepair when no failed node exists.
        /// </summary>
        internal static Outcome TryAutoRebind(Action<string> log)
            => RebindFailedStartNode(log ?? (_ => { }));

        /// <summary>
        /// Re-requests the HID service for the paired device with this
        /// address, when that device is currently CONNECTED. This is the
        /// recovery for the observed drop cycle (hardware, 2026-08-07): the
        /// XM5 closes the sensor L2CAP channel spontaneously, Windows
        /// removes the HID child, and nothing recreates it while the
        /// headset stays connected for audio. Address-keyed so it never
        /// touches a device that did not already qualify as a tracker this
        /// session, and connection-gated so a powered-off headset is never
        /// paged on a loop.
        /// </summary>
        internal static Outcome RequestHidServiceByAddress(ulong address, Action<string> log)
        {
            log ??= _ => { };
            var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>()
            };
            IntPtr findRadio = BluetoothFindFirstRadio(ref radioParams, out IntPtr radio);
            if (findRadio == IntPtr.Zero) return Outcome.Failed;
            try
            {
                do
                {
                    try
                    {
                        var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
                        {
                            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                            fReturnAuthenticated = 1,
                            fReturnRemembered = 1,
                            fReturnConnected = 1,
                            hRadio = radio
                        };
                        var device = new BLUETOOTH_DEVICE_INFO
                        { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
                        IntPtr find = BluetoothFindFirstDevice(ref search, ref device);
                        if (find == IntPtr.Zero) continue;
                        try
                        {
                            do
                            {
                                if (AddressToUlong(device.Address) != address)
                                {
                                    device = new BLUETOOTH_DEVICE_INFO
                                    { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
                                    continue;
                                }
                                if (device.fConnected == 0)
                                {
                                    log($"'{device.szName}' is not connected; not re-requesting its tracker service");
                                    return Outcome.NothingToRepair;
                                }
                                bool liveNode = HasPresentBluetoothHidChild(device.Address);
                                if (liveNode)
                                    return Outcome.NothingToRepair;
                                var hidService = HidServiceGuid;
                                uint enable = BluetoothSetServiceState(radio, ref device, ref hidService, BLUETOOTH_SERVICE_ENABLE);
                                log($"HID service re-request for '{device.szName}': rc={enable}");
                                if (enable == ERROR_SUCCESS) return Outcome.ServiceRequested;
                                if (enable == ERROR_INVALID_PARAMETER || enable == E_INVALIDARG)
                                {
                                    // Stale enabled state with no node (the
                                    // reference gate): cycle it.
                                    uint disable = BluetoothSetServiceState(radio, ref device, ref hidService, BLUETOOTH_SERVICE_DISABLE);
                                    if (disable == ERROR_SUCCESS || disable == ERROR_INVALID_PARAMETER || disable == E_INVALIDARG)
                                    {
                                        Thread.Sleep(1500);
                                        uint recover = BluetoothSetServiceState(radio, ref device, ref hidService, BLUETOOTH_SERVICE_ENABLE);
                                        log($"Stale state cycled; recovery enable rc={recover}");
                                        if (recover == ERROR_SUCCESS) return Outcome.ServiceRequested;
                                    }
                                }
                                return Outcome.Failed;
                            } while (BluetoothFindNextDevice(find, ref device));
                        }
                        finally { BluetoothFindDeviceClose(find); }
                    }
                    finally
                    {
                        if (radio != IntPtr.Zero) { CloseHandle(radio); radio = IntPtr.Zero; }
                    }
                } while (BluetoothFindNextRadio(findRadio, out radio));
            }
            finally
            {
                if (radio != IntPtr.Zero) CloseHandle(radio);
                BluetoothFindRadioClose(findRadio);
            }
            return Outcome.DeviceNotFound;
        }

        /// <summary>Walks the PnP parent chain of a head-tracker HID node
        /// to its BTHENUM ancestor and extracts the 48-bit Bluetooth
        /// address. False when the chain or the parse fails.</summary>
        internal static bool TryResolveAddress(string hidInstanceId, out ulong address)
        {
            address = 0;
            if (string.IsNullOrEmpty(hidInstanceId)) return false;
            try
            {
                if (CM_Locate_DevNode(out uint node, hidInstanceId, 0) != CR_SUCCESS) return false;
                for (int depth = 0; depth < 6; depth++)
                {
                    if (CM_Get_Parent(out uint parent, node, 0) != CR_SUCCESS) return false;
                    node = parent;
                    var id = new StringBuilder(MAX_DEVICE_ID_LEN);
                    if (CM_Get_Device_ID(node, id, id.Capacity, 0) != CR_SUCCESS) continue;
                    string text = id.ToString();
                    if (!text.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (TryParseBthenumAddress(text, out address)) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves the paired Bluetooth device name owning a head-tracker
        /// HID node (reference bluetoothNameForHidInstance): walk the PnP
        /// parent chain to the BTHENUM node, extract the 48-bit address,
        /// and match it against the paired-device list. Null when any step
        /// fails, so callers keep their fallback name.
        /// </summary>
        internal static string ResolvePairedName(string hidInstanceId)
        {
            if (string.IsNullOrEmpty(hidInstanceId)) return null;
            try
            {
                uint rc = CM_Locate_DevNode(out uint node, hidInstanceId, 0);
                if (rc != CR_SUCCESS)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"Headset: name resolve, CM_Locate_DevNode rc={rc} for '{hidInstanceId}'");
                    return null;
                }
                ulong address = 0;
                bool resolved = false;
                for (int depth = 0; depth < 6 && !resolved; depth++)
                {
                    if (CM_Get_Parent(out uint parent, node, 0) != CR_SUCCESS) break;
                    node = parent;
                    var id = new StringBuilder(MAX_DEVICE_ID_LEN);
                    if (CM_Get_Device_ID(node, id, id.Capacity, 0) != CR_SUCCESS) continue;
                    string text = id.ToString();
                    if (!text.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase)) continue;
                    resolved = TryParseBthenumAddress(text, out address);
                }
                if (!resolved)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        "Headset: name resolve, no BTHENUM ancestor carried a parseable address");
                    return null;
                }

                var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                    fReturnAuthenticated = 1,
                    fReturnRemembered = 1,
                    fReturnConnected = 1
                };
                var device = new BLUETOOTH_DEVICE_INFO
                { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
                IntPtr find = BluetoothFindFirstDevice(ref search, ref device);
                if (find == IntPtr.Zero)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"Headset: name resolve, BluetoothFindFirstDevice returned null (Win32 {Marshal.GetLastWin32Error()})");
                    return null;
                }
                try
                {
                    do
                    {
                        if (AddressToUlong(device.Address) == address
                            && !string.IsNullOrWhiteSpace(device.szName))
                            return device.szName;
                        device = new BLUETOOTH_DEVICE_INFO
                        { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };
                    } while (BluetoothFindNextDevice(find, ref device));
                }
                finally { BluetoothFindDeviceClose(find); }
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"Headset: name resolve, no paired device matched address {address:X12}");
                return null;
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("Headset: name resolve threw: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Extracts the 48-bit Bluetooth address embedded in a BTHENUM PnP
        /// instance ID (reference addressFromBthenumId). Device nodes look
        /// like BTHENUM\Dev_F8DF15AABBCC\...; service children end with
        /// ...&amp;0&amp;F8DF15AABBCC_C00000000. Service GUIDs also contain
        /// 12-hex-digit runs, so the match anchors on a "DEV_" prefix or a
        /// '&amp;' delimiter, requires a non-hex follower, and rejects zero.
        /// </summary>
        internal static bool TryParseBthenumAddress(string instanceId, out ulong address)
        {
            address = 0;
            if (string.IsNullOrEmpty(instanceId)) return false;
            string text = instanceId.ToUpperInvariant();
            static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            bool TryParse(int pos, out ulong value)
            {
                value = 0;
                if (pos < 0 || pos + 12 > text.Length) return false;
                for (int i = 0; i < 12; i++) if (!IsHex(text[pos + i])) return false;
                if (pos + 12 < text.Length && IsHex(text[pos + 12])) return false;
                for (int i = 0; i < 12; i++)
                {
                    char c = text[pos + i];
                    value = (value << 4) | (ulong)(c <= '9' ? c - '0' : c - 'A' + 10);
                }
                return value != 0;
            }
            int dev = text.IndexOf("DEV_", StringComparison.Ordinal);
            if (dev >= 0 && TryParse(dev + 4, out address)) return true;
            for (int pos = text.IndexOf('&'); pos >= 0; pos = text.IndexOf('&', pos + 1))
                if (TryParse(pos + 1, out address)) return true;
            return false;
        }

        private static ulong AddressToUlong(BLUETOOTH_ADDRESS address)
            => address.ullLong & 0xFFFF_FFFF_FFFFUL;

        /// <summary>Reference hasPresentBluetoothHidChild: a present HID\*
        /// node whose parent is the BTHENUM HID-service node carrying the
        /// device's compact address.</summary>
        private static bool HasPresentBluetoothHidChild(BLUETOOTH_ADDRESS address)
        {
            string compact = AddressToUlong(address).ToString("X12");
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero,
                DIGCF_ALLCLASSES | DIGCF_PRESENT);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return false;
            try
            {
                var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                var id = new StringBuilder(MAX_DEVICE_ID_LEN);
                for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref dev); index++)
                {
                    id.Clear();
                    if (!SetupDiGetDeviceInstanceId(set, ref dev, id, id.Capacity, out _)) continue;
                    string instance = id.ToString();
                    if (!instance.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (CM_Get_Parent(out uint parent, dev.DevInst, 0) != CR_SUCCESS) continue;
                    var parentId = new StringBuilder(MAX_DEVICE_ID_LEN);
                    if (CM_Get_Device_ID(parent, parentId, parentId.Capacity, 0) != CR_SUCCESS) continue;
                    string parentText = parentId.ToString();
                    if (parentText.IndexOf("BTHENUM\\{00001124-0000-1000-8000-00805F9B34FB}",
                            StringComparison.OrdinalIgnoreCase) >= 0
                        && parentText.IndexOf(compact, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        /// <summary>Reference useGenericHidDriver: find the unique present
        /// node at CM_PROB_FAILED_START under a BTHENUM parent whose
        /// hardware ID carries UP:0020_U:00E1, and bind the inbox
        /// input.inf generic HID driver to it.</summary>
        private static Outcome RebindFailedStartNode(Action<string> log)
        {
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero,
                DIGCF_ALLCLASSES | DIGCF_PRESENT);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return Outcome.Failed;
            int matches = 0;
            string selectedHardwareId = null;
            string selectedInstance = null;
            try
            {
                var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref dev); index++)
                {
                    if (CM_Get_DevNode_Status(out _, out uint problem, dev.DevInst, 0) != CR_SUCCESS
                        || problem != CM_PROB_FAILED_START)
                        continue;
                    if (CM_Get_Parent(out uint parent, dev.DevInst, 0) != CR_SUCCESS) continue;
                    var parentId = new StringBuilder(MAX_DEVICE_ID_LEN);
                    if (CM_Get_Device_ID(parent, parentId, parentId.Capacity, 0) != CR_SUCCESS) continue;
                    if (!parentId.ToString().StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string[] hardwareIds = GetMultiSzProperty(set, ref dev, SPDRP_HARDWAREID);
                    if (hardwareIds == null || hardwareIds.Length == 0) continue;
                    bool headTracker = false;
                    foreach (var hw in hardwareIds)
                        if (hw.IndexOf("UP:0020_U:00E1", StringComparison.OrdinalIgnoreCase) >= 0)
                        { headTracker = true; break; }
                    if (!headTracker) continue;

                    var id = new StringBuilder(MAX_DEVICE_ID_LEN);
                    if (!SetupDiGetDeviceInstanceId(set, ref dev, id, id.Capacity, out _)) continue;
                    matches++;
                    selectedHardwareId = hardwareIds[0];
                    selectedInstance = id.ToString();
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            if (matches == 0)
            {
                log("No failed-start head-tracker node found; nothing to repair.");
                return Outcome.NothingToRepair;
            }
            if (matches != 1)
            {
                log($"Expected exactly one failed head-tracker node; found {matches}. No binding changed.");
                return Outcome.Failed;
            }

            string inputInf = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "input.inf");
            log($"Binding {selectedInstance} (hardware ID {selectedHardwareId}) to {inputInf}");
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, selectedHardwareId, inputInf,
                    INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, out _))
            {
                log($"Generic HID binding failed: {Marshal.GetLastWin32Error()}");
                return Outcome.Failed;
            }
            log("Generic HID binding succeeded.");
            return Outcome.DriverRebound;
        }

        private static string[] GetMultiSzProperty(IntPtr set, ref SP_DEVINFO_DATA dev, uint property)
        {
            SetupDiGetDeviceRegistryProperty(set, ref dev, property, out _, null, 0, out uint needed);
            if (needed == 0 || needed > 65536) return null;
            var buffer = new byte[needed];
            if (!SetupDiGetDeviceRegistryProperty(set, ref dev, property, out _, buffer, needed, out _))
                return null;
            string joined = Encoding.Unicode.GetString(buffer);
            return joined.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        // ─────────────────────────────────────────────
        //  Native
        // ─────────────────────────────────────────────

        private const uint ERROR_SUCCESS = 0;
        private const uint ERROR_INVALID_PARAMETER = 87;
        private const uint E_INVALIDARG = 0x80070057;
        private const uint BLUETOOTH_SERVICE_DISABLE = 0;
        private const uint BLUETOOTH_SERVICE_ENABLE = 1;
        private const uint DIGCF_PRESENT = 0x02;
        private const uint DIGCF_ALLCLASSES = 0x04;
        private const uint SPDRP_HARDWAREID = 0x01;
        private const uint CM_PROB_FAILED_START = 10;
        private const uint CR_SUCCESS = 0;
        private const int MAX_DEVICE_ID_LEN = 200;
        private const uint INSTALLFLAG_FORCE = 0x01;
        private const uint INSTALLFLAG_NONINTERACTIVE = 0x04;

        private static Guid HidServiceGuid =>
            new Guid(0x00001124, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);

        /// <summary>Test seams locking the marshaled struct sizes to the
        /// native ones. BluetoothFindFirstDevice validates dwSize and
        /// answers a mismatch with ERROR_REVISION_MISMATCH (1306), which
        /// reads as "no devices" at every call site.</summary>
        internal static int DeviceInfoMarshalSize => Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>();
        internal static int SearchParamsMarshalSize => Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>();

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }

        // WiiPairingService's proven layout: the native type is a union
        // with a ULONGLONG, so the managed struct must be the ulong (8-byte
        // alignment). A byte-wise 6+2 layout aligns to 2, undersizes every
        // containing struct by 4 (BLUETOOTH_DEVICE_INFO 556 vs native 560),
        // and the API rejects dwSize with ERROR_REVISION_MISMATCH (1306).
        // That exact defect shipped here first and silently broke every
        // Bluetooth enumeration in this file (hardware-diagnosed
        // 2026-08-07). rgBytes[0] is the least significant byte.
        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_ADDRESS
        {
            public ulong ullLong;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        // Default packing REQUIRED (WiiPairingService's proven layouts):
        // BLUETOOTH_ADDRESS is 8-byte aligned, Pack = 1 undersizes the
        // struct and the API rejects dwSize.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_DEVICE_INFO
        {
            public uint dwSize;
            public BLUETOOTH_ADDRESS Address;
            public uint ulClassofDevice;
            public int fConnected;
            public int fRemembered;
            public int fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string szName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public uint dwSize;
            public int fReturnAuthenticated;
            public int fReturnRemembered;
            public int fReturnUnknown;
            public int fReturnConnected;
            public int fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstRadio(
            ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr phRadio);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstDevice(
            ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

        [DllImport("bthprops.cpl")]
        private static extern uint BluetoothSetServiceState(
            IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref Guid pGuidService, uint dwServiceFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(
            IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA dev);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInstanceId(
            IntPtr set, ref SP_DEVINFO_DATA dev, StringBuilder id, int idSize, out int requiredSize);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr set, ref SP_DEVINFO_DATA dev, uint property, out uint propertyRegDataType,
            [Out] byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
        private static extern uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID(uint devInst, StringBuilder buffer, int bufferLen, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UpdateDriverForPlugAndPlayDevices(
            IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags, out bool rebootRequired);
    }
}
