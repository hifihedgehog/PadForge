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
    /// The action is surfaced on the headset's device row, so it covers
    /// every machine that has enumerated the tracker at least once. A
    /// first-ever cold boot with no row (and so no repair button) is the
    /// stated gap; reconnecting the headset normally creates the node.
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

        /// <summary>Reference hasPresentBluetoothHidChild: a present HID\*
        /// node whose parent is the BTHENUM HID-service node carrying the
        /// device's compact address.</summary>
        private static bool HasPresentBluetoothHidChild(BLUETOOTH_ADDRESS address)
        {
            string compact = string.Concat(
                address.rgBytes5.ToString("X2"), address.rgBytes4.ToString("X2"),
                address.rgBytes3.ToString("X2"), address.rgBytes2.ToString("X2"),
                address.rgBytes1.ToString("X2"), address.rgBytes0.ToString("X2"));
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

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_ADDRESS
        {
            public byte rgBytes0, rgBytes1, rgBytes2, rgBytes3, rgBytes4, rgBytes5;
            private ushort _pad;
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

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID(uint devInst, StringBuilder buffer, int bufferLen, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UpdateDriverForPlugAndPlayDevices(
            IntPtr hwndParent, string hardwareId, string fullInfPath, uint installFlags, out bool rebootRequired);
    }
}
