using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common
{
    /// <summary>
    /// Provides direct access to the HidHide control device via P/Invoke IOCTLs.
    /// Manages device blacklisting, application whitelisting, and cloaking state.
    ///
    /// Buffer format for GET/SET operations: Multi-SZ (null-separated UTF-16 strings,
    /// double-null terminated). SET operations replace the entire list.
    /// </summary>
    public static class HidHideController
    {
        // ─────────────────────────────────────────────
        //  IOCTL codes
        // ─────────────────────────────────────────────

        private const uint IOCTL_GET_WHITELIST = 0x80016000;
        private const uint IOCTL_SET_WHITELIST = 0x80016004;
        private const uint IOCTL_GET_BLACKLIST = 0x80016008;
        private const uint IOCTL_SET_BLACKLIST = 0x8001600C;
        private const uint IOCTL_GET_ACTIVE    = 0x80016010;
        private const uint IOCTL_SET_ACTIVE    = 0x80016014;

        private const string DevicePath = @"\\.\HidHide";

        // ─────────────────────────────────────────────
        //  P/Invoke
        // ─────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            byte[] lpInBuffer,
            int nInBufferSize,
            byte[] lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint QueryDosDeviceW(
            string lpDeviceName,
            [Out] char[] lpTargetPath,
            uint ucchMax);

        // SetupAPI for enumerating HID devices by VID/PID.
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(
            ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr DeviceInfoSet, uint MemberIndex, ref SetupApiInterop.SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr DeviceInfoSet, ref SetupApiInterop.SP_DEVINFO_DATA DeviceInfoData,
            char[] DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        // SP_DEVINFO_DATA shared via SetupApiInterop.SP_DEVINFO_DATA

        private static readonly Guid GUID_DEVCLASS_HIDCLASS = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
        private static readonly Guid GUID_DEVCLASS_XUSBCLASS = new("d61ca365-5af4-4486-998b-9db4734c6ca3");
        /// <summary>The Xbox One and Series GIP class (xboxgipsynthetic.inf,
        /// and the class table of every Windows 10 and 11 build). HidHide's
        /// installer registers the driver as an upper filter on this class
        /// beside HIDClass and XnaComposite (HidHideMSI.wxs), so an
        /// instance path of this class on the blacklist is honored on
        /// create the same way (#400).</summary>
        private static readonly Guid GUID_DEVCLASS_XBOXCOMPOSITE = new("05f5cfe2-4733-4950-a6bb-07aad01a3a84");
        private static readonly Guid GUID_CONTAINER_ID_SYSTEM = new("00000000-0000-0000-ffff-ffffffffffff");
        private const uint DIGCF_PRESENT = 0x02;

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        // ─────────────────────────────────────────────
        //  HID interface + serial string (the sweep's identity check)
        // ─────────────────────────────────────────────

        private static readonly Guid GUID_DEVINTERFACE_HID = new("4d1e55b2-f16f-11cf-88cb-001111000030");
        private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_List_SizeW(
            out uint pulLen, ref Guid interfaceClassGuid,
            [MarshalAs(UnmanagedType.LPWStr)] string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_ListW(
            ref Guid interfaceClassGuid, [MarshalAs(UnmanagedType.LPWStr)] string pDeviceID,
            [Out] char[] buffer, uint bufferLen, uint ulFlags);

        // BOOLEAN return, one byte, the same marshaling BluetoothLinkHelper
        // uses for its own copy of this import.
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetSerialNumberString(
            SafeFileHandle hidDeviceObject, byte[] buffer, uint bufferLength);

        // ─────────────────────────────────────────────
        //  cfgmgr32 P/Invoke for base-container expansion
        // ─────────────────────────────────────────────

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern int CM_Locate_DevNodeW(
            out uint pdnDevInst, [MarshalAs(UnmanagedType.LPWStr)] string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(
            uint dnDevInst, in DEVPROPKEY propertyKey,
            out uint propertyType, byte[] propertyBuffer,
            ref uint propertyBufferSize, uint ulFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // DEVPKEY_Device_ContainerId   {8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c}, 2
        // DEVPKEY_Device_ClassGuid     {a45c254e-df1c-4efd-8020-67d146a850e0}, 10
        // DEVPKEY_Device_InstanceId    {78c34fc8-104a-4aca-9ea4-524d52996e57}, 256
        private static readonly DEVPROPKEY DEVPKEY_Device_ContainerId =
            new() { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
        private static readonly DEVPROPKEY DEVPKEY_Device_ClassGuid =
            new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 10 };
        private static readonly DEVPROPKEY DEVPKEY_Device_InstanceId =
            new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };

        private const int CR_SUCCESS = 0;
        private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x01;
        private const uint DEVPROP_TYPE_GUID = 0x0000000D;
        private const uint DEVPROP_TYPE_STRING = 0x00000012;

        // ─────────────────────────────────────────────
        //  Tracking: device IDs managed by PadForge
        // ─────────────────────────────────────────────

        /// <summary>
        /// Set of device instance IDs that PadForge has added to the HidHide blacklist.
        /// Used during cleanup to remove only our entries, not those added by other tools.
        /// </summary>
        private static readonly HashSet<string> _managedDeviceIds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        /// <summary>Test seam (InternalsVisibleTo PadForge.Tests): the
        /// control-device IOCTL, as (ioctl, inBuffer, outBuffer) to (ok,
        /// bytesReturned). Null in production, where <see cref="TryIo"/>
        /// opens the driver and calls DeviceIoControl. A fake here can
        /// mirror the driver's contract for the two-call read and the
        /// whole-list write, which is how the exact-size read and the
        /// driver-first sync are proven without the driver.</summary>
        internal static Func<uint, byte[], byte[], (bool ok, int bytes)> IoSeam;

        /// <summary>Test seam, the same shape as the presence probe the
        /// sweep gate takes: instance id to the node's serial string, or
        /// null. Null in production, where <see cref="ReadInstanceSerial"/>
        /// reads cfgmgr32 and hid.dll. A fake here lets the serial-scoped
        /// sweep selection be proven without a pad on the bench.</summary>
        internal static Func<string, string> SerialReader;

        /// <summary>Drops the in-process managed set, for tests that
        /// drive the sync through <see cref="IoSeam"/> from a clean
        /// slate. Production never calls it: the managed set is what
        /// RemoveManagedDevices needs to leave other tools' entries
        /// alone.</summary>
        internal static void ResetManagedForTests()
        {
            lock (_lock) _managedDeviceIds.Clear();
        }

        // ─────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns true if the HidHide control device can be opened.
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                using var handle = OpenDevice();
                return handle != null && !handle.IsInvalid;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The availability check with its reason (#391): true
        /// when the control device opens, else false with the Win32
        /// error the open returned. The hiding path gates on this open
        /// while the status surfaces read the MSI registry scan, and the
        /// two are different signals. When the open fails, the error is
        /// what a trace needs, since a bare false read as "nothing to
        /// hide" and the whole hiding path went silent.</summary>
        public static bool TryProbe(out int win32Error)
        {
            win32Error = 0;
            try
            {
                var handle = CreateFileW(DevicePath, GENERIC_READ | GENERIC_WRITE, 0,
                    IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (handle == null || handle.IsInvalid)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    handle?.Dispose();
                    return false;
                }
                handle.Dispose();
                return true;
            }
            catch
            {
                win32Error = -1;
                return false;
            }
        }

        /// <summary>Read-back verification (#391): the desired instance
        /// IDs the driver's blacklist does NOT carry after a write. Null
        /// when the list could not be read. A non-empty result means a
        /// write the caller believes landed did not.</summary>
        public static List<string> MissingFromBlacklist(IEnumerable<string> desiredIds)
        {
            var present = GetBlacklist();
            if (present == null) return null;
            return ComputeMissing(desiredIds, present);
        }

        /// <summary>Pure set difference, case-insensitive, for the
        /// read-back verification and its tests.</summary>
        internal static List<string> ComputeMissing(IEnumerable<string> desired, IEnumerable<string> present)
        {
            var have = new HashSet<string>(present ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var id in desired ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(id) && !have.Contains(id)) missing.Add(id);
            return missing;
        }

        /// <summary>
        /// Gets the current device blacklist (device instance IDs), or NULL if
        /// the driver could not be read.
        ///
        /// <para>Null and empty mean different things here and callers must not
        /// conflate them. Every consumer does read-modify-write, so treating a
        /// failed read as "the list is empty" writes an empty list back and
        /// destroys entries the user set outside PadForge. That is exactly what
        /// SyncManagedDevices promises never to do.</para>
        /// </summary>
        public static List<string> GetBlacklist()
        {
            return GetMultiSzList(IOCTL_GET_BLACKLIST);
        }

        /// <summary>
        /// Replaces the entire device blacklist. Returns whether the
        /// driver accepted the write, since a refused SET leaves the
        /// driver's list as it was and the caller must not record the
        /// new list as landed.
        /// </summary>
        public static bool SetBlacklist(List<string> instanceIds)
        {
            return SetMultiSzList(IOCTL_SET_BLACKLIST, instanceIds);
        }

        /// <summary>
        /// Gets the current application whitelist (DOS device paths).
        /// </summary>
        public static List<string> GetWhitelist()
        {
            return GetMultiSzList(IOCTL_GET_WHITELIST);
        }

        /// <summary>
        /// Replaces the entire application whitelist. Returns whether the
        /// driver accepted the write.
        /// </summary>
        public static bool SetWhitelist(List<string> paths)
        {
            return SetMultiSzList(IOCTL_SET_WHITELIST, paths);
        }

        /// <summary>
        /// Gets whether cloaking (device hiding) is currently active.
        /// </summary>
        public static bool GetActive()
        {
            byte[] outBuffer = new byte[1];
            if (!TryIo(IOCTL_GET_ACTIVE, null, outBuffer, out _))
                return false;

            return outBuffer[0] != 0;
        }

        /// <summary>
        /// Enables or disables cloaking (device hiding).
        /// </summary>
        public static void SetActive(bool active)
        {
            byte[] inBuffer = new byte[] { active ? (byte)1 : (byte)0 };
            TryIo(IOCTL_SET_ACTIVE, inBuffer, null, out _);
        }

        /// <summary>
        /// Removes all device IDs that PadForge previously added to the blacklist.
        /// Leaves entries added by other tools untouched.
        /// </summary>
        public static void RemoveManagedDevices()
        {
            lock (_lock)
            {
                if (_managedDeviceIds.Count == 0) return;

                var list = GetBlacklist();
                // Bail on a failed read. Writing back what we could not read
                // would clear the user's whole blacklist.
                if (list == null) return;
                list.RemoveAll(id => _managedDeviceIds.Contains(id));
                // A refused write leaves our entries in the driver, and they
                // stay ours to remove on the next call. The engine-start
                // stale-cloak sweep clears whatever a crash leaves behind.
                if (SetBlacklist(list))
                    _managedDeviceIds.Clear();
            }
        }

        /// <summary>
        /// Syncs the blacklist to match the desired set of managed device IDs.
        /// Only adds/removes the diff and never clears the entire blacklist,
        /// so there is no window where HidHide briefly un-hides devices.
        /// Returns false when the driver could not be read or refused the
        /// write.
        /// </summary>
        public static bool SyncManagedDevices(HashSet<string> desiredIds)
            => SyncManagedDevices(desiredIds, out _, out _);

        /// <summary>The sync with its diff reported (#391), so the caller
        /// can log exactly what changed in the driver's list.
        ///
        /// <para>The diff is taken against the DRIVER's list, not against
        /// the in-process managed set. The managed set only remembers
        /// which entries are PadForge's to remove. Diffing against it
        /// alone had two holes: an entry another tool dropped (the HidHide
        /// client saves its whole list, and the driver's SET is a full
        /// replace, Logic.c OnControlDeviceIoSetBlacklist) was never
        /// re-added because the managed set still listed it, and a SET the
        /// driver refused was recorded as landed all the same. Either way
        /// the read-back printed MISSING on every apply and nothing fixed
        /// it. Now: an id is added when the driver lacks it, removed when
        /// it left the desired set and the driver still carries it, and
        /// the managed set moves to the desired set only after a write
        /// the driver accepted (or when there was nothing to write).</para></summary>
        public static bool SyncManagedDevices(HashSet<string> desiredIds, out List<string> added, out List<string> removed)
        {
            added = new List<string>();
            removed = new List<string>();
            lock (_lock)
            {
                var list = GetBlacklist();
                // Bail on a failed read. This method's own contract is
                // "never clears the entire blacklist", and a failed read
                // that fell through to SetBlacklist did precisely that.
                if (list == null) return false;
                var present = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);

                foreach (var id in desiredIds)
                {
                    if (!string.IsNullOrEmpty(id) && !present.Contains(id))
                        added.Add(id);
                }
                foreach (var id in _managedDeviceIds)
                {
                    if (!desiredIds.Contains(id) && present.Contains(id))
                        removed.Add(id);
                }

                if (added.Count > 0 || removed.Count > 0)
                {
                    foreach (var id in removed)
                        list.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
                    list.AddRange(added);
                    // A refused write leaves the driver's list as it was, so
                    // the managed set stays as it was too: the next apply
                    // diffs against the driver again and retries.
                    if (!SetBlacklist(list)) return false;
                }

                _managedDeviceIds.Clear();
                foreach (var id in desiredIds)
                    _managedDeviceIds.Add(id);
                return true;
            }
        }

        /// <summary>
        /// Clears the entire HidHide blacklist and disables cloaking.
        /// Called on startup to remove stale entries from a previous crash
        /// (since <see cref="_managedDeviceIds"/> is in-memory and lost on restart).
        /// </summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                SetBlacklist(new List<string>());
                SetActive(false);
                _managedDeviceIds.Clear();
            }
        }

        /// <summary>
        /// Finds all present HID device instance IDs matching the given VID/PID.
        /// Used as a fallback when a device has a synthetic path (e.g., XInput#0)
        /// that can't be converted to a valid instance ID.
        /// </summary>
        public static List<string> FindInstanceIdsByVidPid(ushort vendorId, ushort productId)
        {
            var result = new List<string>();

            // USB HID format: VID_045E&PID_0B13
            string vidPidUsb = $"VID_{vendorId:X4}&PID_{productId:X4}";
            // BLE HID-over-GATT format: VID&02045E (02 = USB-assigned VID source) + PID&0B13
            // Also match source 01 (Bluetooth SIG-assigned).
            string vidBle02 = $"VID&02{vendorId:X4}";
            string vidBle01 = $"VID&01{vendorId:X4}";
            string pidBle = $"PID&{productId:X4}";
            // BT Classic HID-over-RFCOMM (Profile 0x1124) format: VID&0002054C
            // 4-hex source ("0002" = USB-IF, "0001" = SIG) + 4-hex VID. PID is
            // unchanged (PID&XXXX).  Without these patterns DualSense over
            // Bluetooth Classic isn't picked up by the synthetic-path fallback.
            string vidBrEdr02 = $"VID&0002{vendorId:X4}";
            string vidBrEdr01 = $"VID&0001{vendorId:X4}";

            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == (IntPtr)(-1)) return result;

            try
            {
                var devInfoData = new SetupApiInterop.SP_DEVINFO_DATA();
                devInfoData.cbSize = Marshal.SizeOf<SetupApiInterop.SP_DEVINFO_DATA>();

                for (uint i = 0; SetupDiEnumDeviceInfo(devInfoSet, i, ref devInfoData); i++)
                {
                    char[] buffer = new char[512];
                    if (SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, buffer, (uint)buffer.Length, out _))
                    {
                        int nullIdx = Array.IndexOf(buffer, '\0');
                        string instanceId = nullIdx >= 0 ? new string(buffer, 0, nullIdx) : new string(buffer);

                        // Match standard USB format, BLE GATT format, or BT Classic BR/EDR format.
                        bool match = instanceId.Contains(vidPidUsb, StringComparison.OrdinalIgnoreCase)
                            || (instanceId.Contains(pidBle, StringComparison.OrdinalIgnoreCase)
                                && (instanceId.Contains(vidBle02, StringComparison.OrdinalIgnoreCase)
                                    || instanceId.Contains(vidBle01, StringComparison.OrdinalIgnoreCase)
                                    || instanceId.Contains(vidBrEdr02, StringComparison.OrdinalIgnoreCase)
                                    || instanceId.Contains(vidBrEdr01, StringComparison.OrdinalIgnoreCase)));

                        if (match && !IsHidMaestroDevice(instanceId))
                            result.Add(instanceId);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return result;
        }

        /// <summary>
        /// Returns true if the specified PnP device instance (or any of its
        /// ancestors) belongs to HIDMaestro. HIDMaestro virtual devices share
        /// their spoofed VID/PID with the real hardware they impersonate, so
        /// <see cref="FindInstanceIdsByVidPid"/> would otherwise return them
        /// alongside real devices and PadForge would accidentally HidHide its
        /// own virtuals — making them invisible to DirectInput / joy.cpl.
        ///
        /// Uses the canonical DEVPKEY_Device_Manufacturer = "HIDMaestro"
        /// property written by every HIDMaestro INF. Nothing else on a
        /// Windows system reports that manufacturer string.
        /// </summary>
        /// <summary>Public alias so callers scrubbing stale cached entries can filter.</summary>
        public static bool IsHidMaestroDeviceInstance(string instanceId) => IsHidMaestroDevice(instanceId);

        private static bool IsHidMaestroDevice(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;

            // Fast string-pattern check on the leaf ID itself.
            if (MatchesHidMaestroPattern(instanceId)) return true;

            // If the device isn't present in PnP right now, the fast-path
            // check above is all we can do.
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0)
                return false;

            // Depth-0 hardware ID check: every HIDMaestro HID child has
            // "HID\HIDMaestro" in its Hardware IDs. Most reliable single
            // call — catches all profiles immediately.
            if (HasHidMaestroHardwareId(devInst))
                return true;

            // Walk the parent chain. At each level test the INSTANCE ID
            // string, the manufacturer registry value, the HARDWARE IDS,
            // and the driver service. Catching any one lets us filter
            // HIDMaestro-parented HID children correctly regardless of
            // whether the MFG property read succeeds (char[] marshalling of
            // CM_Get_DevNode_Registry_Property can silently return empty for
            // some devices; the string check is the reliable backstop).
            //
            // The hardware-id and service legs are the composite USB persona
            // (#391 follow-up). A persona enumerates through the REAL USB
            // stack (vhci, UDE, usbccgp, hidusb) and every identifier on it
            // is genuine Sony by design, so neither the instance ids nor the
            // manufacturer name anywhere on its path say HIDMaestro. The ONE
            // marker is an additive hardware id on the emulated host
            // controller, ROOT\HIDMAESTRO_UDE (HM#42, v1.4.3), four hops up
            // from the persona's HID interface, with the usbip2_ude service
            // as the fallback for a pre-1.4.3 driver. This mirrors
            // InputManager.IsOnUsbipVhci, the guard that keeps PadForge from
            // ingesting its own persona. Without these legs the VID/PID sweep
            // that hides a physical DualSense also hid the virtual one the
            // slot had just created (the reporter's r3503 trace: a second
            // 054C:0CE6 tree at depth 3 and 4 swept two seconds after the
            // PlayStation slot's persona attached).
            var idBuf = new System.Text.StringBuilder(512);
            for (int depth = 0; depth < 16; depth++)
            {
                // --- parent instance ID string check ---
                idBuf.Clear();
                idBuf.EnsureCapacity(512);
                if (CM_Get_Device_IDW(devInst, idBuf, idBuf.Capacity, 0) == 0)
                {
                    string curId = idBuf.ToString();
                    if (MatchesHidMaestroPattern(curId))
                        return true;
                }

                // --- hardware ids + service (the persona's host controller) ---
                if (depth > 0 && HasHidMaestroHardwareId(devInst))
                    return true;
                if (string.Equals(GetDevNodeService(devInst), "usbip2_ude", StringComparison.OrdinalIgnoreCase))
                    return true;

                // --- manufacturer property check ---
                var mfg = new char[128];
                int mfgLen = mfg.Length * 2;
                if (CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_MFG, out _, mfg, ref mfgLen, 0) == 0)
                {
                    int strLen = 0;
                    while (strLen < mfg.Length && mfg[strLen] != '\0') strLen++;
                    if (string.Equals(new string(mfg, 0, strLen),
                                      "HIDMaestro", StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                if (CM_Get_Parent(out uint parent, devInst, 0) != 0) break;
                if (parent == 0 || parent == devInst) break;
                devInst = parent;
            }
            return false;
        }

        private static bool MatchesHidMaestroPattern(string id)
        {
            if (id == null) return false;
            if (id.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("HMCOMPANION", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("HMXINPUT", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // ROOT\VID_*&IG_* / ROOT\VID_*&XI_* are HIDMaestro's xinputhid
            // and XUSB root enumerators. Real devices never root at ROOT\VID_.
            if (id.StartsWith(@"ROOT\VID_", StringComparison.OrdinalIgnoreCase)
                && (id.IndexOf("&IG_", StringComparison.OrdinalIgnoreCase) >= 0
                    || id.IndexOf("&XI_", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            return false;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, System.Text.StringBuilder buffer, int len, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Registry_PropertyW(
            uint devInst, uint property, out uint pulRegDataType,
            [Out] char[] buffer, ref int length, uint flags);

        private const uint CM_DRP_HARDWAREID = 0x02;
        private const uint CM_DRP_MFG = 0x0D;

        private static bool HasHidMaestroHardwareId(uint devInst)
        {
            var buffer = new char[1024];
            int length = buffer.Length * 2;
            if (CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_HARDWAREID,
                    out _, buffer, ref length, 0) != 0)
                return false;

            int charCount = length / 2;
            int start = 0;
            for (int i = 0; i < charCount; i++)
            {
                if (buffer[i] == '\0')
                {
                    if (i == start) break;
                    var id = new string(buffer, start, i - start);
                    if (id.IndexOf("HIDMaestro", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                    start = i + 1;
                }
            }
            return false;
        }

        /// <summary>
        /// Converts a device path (\\?\HID#VID_...) to a PnP device instance ID
        /// (HID\VID_...\...) suitable for the HidHide blacklist.
        /// </summary>
        public static string DevicePathToInstanceId(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath))
                return null;

            string path = devicePath;

            // Strip \\?\ prefix.
            if (path.StartsWith(@"\\?\"))
                path = path.Substring(4);

            // Remove device interface GUID suffix ({...}).
            int guidIdx = path.LastIndexOf('{');
            if (guidIdx > 0)
                path = path.Substring(0, guidIdx);

            // Replace # with \ (device path uses # as separator).
            path = path.Replace('#', '\\');
            path = path.TrimEnd('\\');

            return string.IsNullOrEmpty(path) ? null : path;
        }

        // ─────────────────────────────────────────────
        //  Base-container expansion
        // ─────────────────────────────────────────────

        /// <summary>One PnP devnode as the expansion sees it: its instance
        /// id and its setup class.</summary>
        internal readonly record struct PnpNode(string InstanceId, Guid ClassGuid);

        /// <summary>The setup classes HidHide installs its upper filter on
        /// (HidHideMSI.wxs: HIDClass, XnaComposite, XboxComposite). A
        /// blacklisted instance path of any other class is inert, because
        /// the driver is not on that stack to see the create.</summary>
        internal static bool IsHidHideFilteredClass(Guid classGuid)
            => classGuid == GUID_DEVCLASS_HIDCLASS
            || classGuid == GUID_DEVCLASS_XUSBCLASS
            || classGuid == GUID_DEVCLASS_XBOXCOMPOSITE;

        /// <summary>
        /// Expand a HID instance ID into the full set of instance paths to
        /// blacklist for the device it belongs to. The shape follows
        /// HidHide Configuration Client
        /// (<c>HidHide/HidHideClient/src/BlacklistDlg.cpp:294-345</c>), and
        /// adds the one thing the client cannot express (#400):
        ///
        /// <list type="number">
        /// <item>Walk parents via Container ID until the parent has a
        /// different Container ID; the last device with the same ID is the
        /// base container (typically the USB or XUSB device).</item>
        /// <item>Every node between the HID node and the base container
        /// whose class HidHide filters is blacklisted too. On a pad whose
        /// XUSB node is an INTERFACE of a USB composite parent (the Legion
        /// Go's built-in controller, a pad on the Xbox 360 wireless
        /// receiver) that node is neither the base nor an immediate HID
        /// child, and without this step XInput opened it freely while
        /// every HID interface beside it was hidden. The client's tree
        /// lists HID devices only, so it has no way to name that node.
        /// The driver honors it: HidHide is an upper filter on
        /// XnaComposite and matches the device's own path on create.</item>
        /// <item>If the base container is HID, XUSB, or XboxComposite class
        /// AND every child is a HID, add the base container instance path
        /// too (lets HidHide hide the device at the parent boundary so
        /// XInput / WGI can't see it through any of the other child
        /// paths).</item>
        /// <item>Always add every HID-child instance path.</item>
        /// </list>
        ///
        /// <para><paramref name="keepOut"/> names nodes that belong to a
        /// device PadForge lists as its own row with hiding off. Those are
        /// never added, and a base container is never blocked while one of
        /// its HID children is kept out, since blocking the parent would
        /// hide that row too. HidHide's own client offers the same
        /// per-child selection under one device entry. Every id the
        /// predicate removed is reported through <paramref name="keptOut"/>
        /// for the diag line.</para>
        ///
        /// Returns the list with the input <paramref name="hidInstanceId"/>
        /// preserved (so a single-blacklist call still works) plus any
        /// additional paths discovered.
        /// </summary>
        public static List<string> ExpandToBaseContainerAndChildren(string hidInstanceId)
            => ExpandToBaseContainerAndChildren(hidInstanceId, null, null);

        public static List<string> ExpandToBaseContainerAndChildren(
            string hidInstanceId, Func<string, bool> keepOut, ICollection<string> keptOut)
        {
            if (string.IsNullOrEmpty(hidInstanceId)) return new List<string>();

            if (CM_Locate_DevNodeW(out uint hidDevInst, hidInstanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
                return new List<string> { hidInstanceId };

            Guid hidContainerId = GetContainerId(hidDevInst);
            if (hidContainerId == Guid.Empty || hidContainerId == GUID_CONTAINER_ID_SYSTEM)
                return new List<string> { hidInstanceId };

            // Walk parents while Container ID stays the same, recording
            // each intermediate node. The last matching parent is the base
            // container.
            var chain = new List<PnpNode>();
            uint baseContainer = hidDevInst;
            uint current = hidDevInst;
            while (CM_Get_Parent(out uint parent, current, 0) == CR_SUCCESS)
            {
                if (GetContainerId(parent) != hidContainerId) break;
                if (baseContainer != hidDevInst)
                    chain.Add(new PnpNode(GetInstanceId(baseContainer), GetClassGuid(baseContainer)));
                baseContainer = parent;
                current = parent;
            }

            var baseNode = baseContainer == hidDevInst
                ? new PnpNode(null, Guid.Empty)
                : new PnpNode(GetInstanceId(baseContainer), GetClassGuid(baseContainer));

            // Enumerate immediate children of base container.
            var children = new List<PnpNode>();
            if (baseContainer != hidDevInst
                && CM_Get_Child(out uint firstChild, baseContainer, 0) == CR_SUCCESS)
            {
                uint child = firstChild;
                while (true)
                {
                    children.Add(new PnpNode(GetInstanceId(child), GetClassGuid(child)));
                    if (CM_Get_Sibling(out uint sibling, child, 0) != CR_SUCCESS) break;
                    child = sibling;
                }
            }

            return ComposeBlacklist(hidInstanceId, chain, baseNode, children, keepOut, keptOut);
        }

        /// <summary>The pure rule behind <see cref="ExpandToBaseContainerAndChildren(string, Func{string, bool}, ICollection{string})"/>,
        /// separated from cfgmgr32 so the trees can be pinned in tests.
        /// <paramref name="chain"/> runs from the HID node's parent up to,
        /// and excluding, the base container. <paramref name="baseContainer"/>
        /// carries a null id when the HID node is its own base (a
        /// stand-alone device), and <paramref name="children"/> are the
        /// base container's immediate children.</summary>
        internal static List<string> ComposeBlacklist(
            string hidInstanceId,
            IReadOnlyList<PnpNode> chain,
            PnpNode baseContainer,
            IReadOnlyList<PnpNode> children,
            Func<string, bool> keepOut,
            ICollection<string> keptOut)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(hidInstanceId)) return result;
            result.Add(hidInstanceId);
            keepOut ??= _ => false;

            void Add(string id)
            {
                if (string.IsNullOrEmpty(id)) return;
                if (result.Contains(id, StringComparer.OrdinalIgnoreCase)) return;
                if (keepOut(id))
                {
                    // A node can be both on the chain and a base child
                    // (the xinputhid node under a wired 360 pad). One report.
                    if (keptOut != null && !keptOut.Contains(id, StringComparer.OrdinalIgnoreCase))
                        keptOut.Add(id);
                    return;
                }
                result.Add(id);
            }

            // Step 2: the nodes between the HID node and the base, on
            // classes the driver filters.
            if (chain != null)
                foreach (var node in chain)
                    if (IsHidHideFilteredClass(node.ClassGuid))
                        Add(node.InstanceId);

            // Step 3: the base container, by HidHide's own rule, with the
            // GIP class beside the XUSB one, and never over a kept-out row.
            int totalChildren = 0;
            int hidChildren = 0;
            bool anyChildKeptOut = false;
            var hidChildInstanceIds = new List<string>();
            if (children != null)
            {
                foreach (var child in children)
                {
                    totalChildren++;
                    if (child.ClassGuid != GUID_DEVCLASS_HIDCLASS) continue;
                    hidChildren++;
                    if (string.IsNullOrEmpty(child.InstanceId)) continue;
                    hidChildInstanceIds.Add(child.InstanceId);
                    if (keepOut(child.InstanceId)) anyChildKeptOut = true;
                }
            }

            bool blockBase = totalChildren > 0
                && hidChildren == totalChildren
                && !anyChildKeptOut
                && (baseContainer.ClassGuid == GUID_DEVCLASS_HIDCLASS
                 || baseContainer.ClassGuid == GUID_DEVCLASS_XUSBCLASS
                 || baseContainer.ClassGuid == GUID_DEVCLASS_XBOXCOMPOSITE)
                && !string.IsNullOrEmpty(baseContainer.InstanceId);

            if (blockBase)
                Add(baseContainer.InstanceId);

            // Step 4: every HID child of the base.
            foreach (var id in hidChildInstanceIds)
                Add(id);

            return result;
        }

        /// <summary>A node and its same-container ancestors, the HID node
        /// first and the base container last: what a device row with
        /// hiding OFF contributes to the keep-out set (#400). Phantom
        /// nodes resolve too, matching the expansion's own locate.</summary>
        internal static List<string> ChainInstanceIds(string hidInstanceId)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(hidInstanceId)) return result;
            result.Add(hidInstanceId);
            try
            {
                if (CM_Locate_DevNodeW(out uint devInst, hidInstanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
                    return result;
                Guid containerId = GetContainerId(devInst);
                if (containerId == Guid.Empty || containerId == GUID_CONTAINER_ID_SYSTEM)
                    return result;
                uint current = devInst;
                while (CM_Get_Parent(out uint parent, current, 0) == CR_SUCCESS)
                {
                    if (GetContainerId(parent) != containerId) break;
                    string id = GetInstanceId(parent);
                    if (!string.IsNullOrEmpty(id)) result.Add(id);
                    current = parent;
                }
            }
            catch
            {
                // cfgmgr32 trouble leaves the row's own id as its whole chain.
            }
            return result;
        }

        private static readonly DEVPROPKEY DEVPKEY_Device_Service =
            new DEVPROPKEY { fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), pid = 6 };

        /// <summary>The devnode's driver service name, or null. Used by the
        /// HIDMaestro walk to recognize the usbip-win2 emulated host
        /// controller under a composite persona (#391 follow-up).</summary>
        private static string GetDevNodeService(uint devInst)
        {
            byte[] buf = new byte[512];
            uint size = (uint)buf.Length;
            int rc = CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_Service,
                out uint type, buf, ref size, 0);
            if (rc != CR_SUCCESS || type != DEVPROP_TYPE_STRING) return null;
            string s = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }

        private static Guid GetContainerId(uint devInst)
        {
            byte[] buf = new byte[16];
            uint size = (uint)buf.Length;
            int rc = CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_ContainerId,
                out uint type, buf, ref size, 0);
            if (rc != CR_SUCCESS || type != DEVPROP_TYPE_GUID)
                return Guid.Empty;
            return new Guid(buf);
        }

        private static Guid GetClassGuid(uint devInst)
        {
            byte[] buf = new byte[16];
            uint size = (uint)buf.Length;
            int rc = CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_ClassGuid,
                out uint type, buf, ref size, 0);
            if (rc != CR_SUCCESS || type != DEVPROP_TYPE_GUID)
                return Guid.Empty;
            return new Guid(buf);
        }

        private static string GetInstanceId(uint devInst)
        {
            // Two-call pattern: first call with empty buffer learns the
            // required size, second call retrieves the string.
            uint size = 0;
            CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_InstanceId,
                out _, null, ref size, 0);
            if (size == 0) return null;
            byte[] buf = new byte[size];
            int rc = CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_InstanceId,
                out uint type, buf, ref size, 0);
            if (rc != CR_SUCCESS || type != DEVPROP_TYPE_STRING) return null;
            // Strings are UTF-16 with trailing null.
            int chars = (int)(size / 2);
            if (chars > 0 && BitConverter.ToInt16(buf, (chars - 1) * 2) == 0) chars--;
            return Encoding.Unicode.GetString(buf, 0, chars * 2);
        }

        /// <summary>Whether a PnP instance id names a devnode that is
        /// present right now. CM_Locate_DevNodeW WITHOUT the PHANTOM flag
        /// fails for a node Windows remembers but has no device behind
        /// (the expansion walk above asks for phantoms on purpose, since
        /// it hides offline pads pre-emptively). The sibling-sweep gate
        /// uses this to count only the records of a product that are
        /// actually plugged in, so a stale offline record of a pad the
        /// user re-paired under a new serial cannot switch the sweep
        /// off for the live one.</summary>
        internal static bool IsInstancePresent(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;
            try
            {
                return CM_Locate_DevNodeW(out _, instanceId, 0) == CR_SUCCESS;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The serial string of a HID node, by instance id, or
        /// null when nothing is readable (#391 follow-up). The present-node
        /// sweep hides a node only when this equals the record's serial,
        /// so a second pad of the same model is never swept up with the
        /// first.
        ///
        /// <para>A Sony pad reports its Bluetooth MAC as the HID serial on
        /// both transports. Over USB, HidD_GetSerialNumberString returns
        /// the twelve hex digits (SDL SDL_hidapi_ps5.c InitDevice reformats
        /// a twelve-character device serial into aa-bb-cc-dd-ee-ff, and
        /// SDL_hidapi_ps4.c does the same). Over Bluetooth Classic the
        /// BTHENUM node carries the MAC in the last segment of its
        /// instance id. The HID node itself is the BTHENUM node's child
        /// and has no MAC in its own id, so the text check runs on the
        /// node and then on its parent, which needs no handle at all.
        /// Only then is the node's HID interface opened (cfgmgr32
        /// interface list for GUID_DEVINTERFACE_HID, PRESENT flag) with
        /// access 0 and read/write sharing, the hidapi enumeration open
        /// (SDL src/hidapi/windows/hid.c open_device, share_mode
        /// FILE_SHARE_READ|FILE_SHARE_WRITE), and HidD_GetSerialNumberString
        /// is asked (hid.c 909). PadForge is whitelisted in HidHide, so the
        /// open succeeds for a node already on the blacklist. BLE HID
        /// nodes answer HidD with nothing (hid.c 613), and a node with no
        /// readable serial falls back to the sole-record gate.</para>
        ///
        /// <para>Goes through <see cref="SerialReader"/> when a test
        /// installed one.</para></summary>
        internal static string ReadInstanceSerial(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            var seam = SerialReader;
            if (seam != null) return seam(instanceId);
            try
            {
                string serial = ParseBluetoothAddressSegment(instanceId);
                if (serial != null) return serial;

                if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) == CR_SUCCESS)
                {
                    // Two hops cover HID child under BTHENUM service node
                    // under the BTHENUM device node.
                    var idBuf = new StringBuilder(512);
                    uint current = devInst;
                    for (int hop = 0; hop < 2; hop++)
                    {
                        if (CM_Get_Parent(out uint parent, current, 0) != CR_SUCCESS) break;
                        if (parent == 0 || parent == current) break;
                        idBuf.Clear();
                        idBuf.EnsureCapacity(512);
                        if (CM_Get_Device_IDW(parent, idBuf, idBuf.Capacity, 0) == CR_SUCCESS)
                        {
                            serial = ParseBluetoothAddressSegment(idBuf.ToString());
                            if (serial != null) return serial;
                        }
                        current = parent;
                    }
                }

                return ReadHidSerialString(instanceId);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Pure text check: the twelve-hex-digit Bluetooth address
        /// in a BTHENUM instance id, else null. Two shapes carry it. The
        /// service node ends its last segment with the address and a
        /// channel suffix (BTHENUM\{...}_VID&amp;0002054C_PID&amp;0CE6\9&amp;1479B2EE&amp;0&amp;0C27565874D8_C00000000),
        /// and the device node names it after DEV_ and again after
        /// BLUETOOTHDEVICE_ (BTHENUM\DEV_0C27565874D8\9&amp;1479B2EE&amp;0&amp;BLUETOOTHDEVICE_0C27565874D8).
        /// An all-zero address is no address.</summary>
        internal static string ParseBluetoothAddressSegment(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            if (!instanceId.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase)) return null;

            int slash = instanceId.LastIndexOf('\\');
            if (slash < 0 || slash + 1 >= instanceId.Length) return null;
            string leaf = instanceId.Substring(slash + 1);
            int amp = leaf.LastIndexOf('&');
            string tail = amp >= 0 ? leaf.Substring(amp + 1) : leaf;
            foreach (var token in tail.Split('_'))
            {
                if (IsBluetoothAddressToken(token)) return token;
            }

            // BTHENUM\DEV_<mac>\...: the second segment.
            const string devPrefix = @"BTHENUM\DEV_";
            if (instanceId.StartsWith(devPrefix, StringComparison.OrdinalIgnoreCase)
                && instanceId.Length >= devPrefix.Length + 12)
            {
                string token = instanceId.Substring(devPrefix.Length, 12);
                if (IsBluetoothAddressToken(token)) return token;
            }
            return null;
        }

        private static bool IsBluetoothAddressToken(string token)
        {
            if (token == null || token.Length != 12) return false;
            bool nonZero = false;
            foreach (char c in token)
            {
                if (!Uri.IsHexDigit(c)) return false;
                if (c != '0') nonZero = true;
            }
            return nonZero;
        }

        /// <summary>Opens each present HID interface of the instance and
        /// returns the first non-empty serial string. Null when the
        /// instance has no present HID interface, none opens, or the
        /// device answers HidD with an empty string.</summary>
        private static string ReadHidSerialString(string instanceId)
        {
            var guid = GUID_DEVINTERFACE_HID;
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, instanceId,
                    CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || len <= 1)
                return null;
            var buffer = new char[len];
            if (CM_Get_Device_Interface_ListW(ref guid, instanceId, buffer, len,
                    CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
                return null;

            // Multi-SZ: each path null-terminated, an empty string ends it.
            int start = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != '\0') continue;
                if (i == start) break;
                string path = new string(buffer, start, i - start);
                start = i + 1;
                string serial = ReadHidSerialFromInterface(path);
                if (serial != null) return serial;
            }
            return null;
        }

        private static string ReadHidSerialFromInterface(string interfacePath)
        {
            using var handle = CreateFileW(interfacePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == null || handle.IsInvalid) return null;
            var bytes = new byte[512];
            if (!HidD_GetSerialNumberString(handle, bytes, (uint)bytes.Length)) return null;
            string s = Encoding.Unicode.GetString(bytes);
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s.Substring(0, nul);
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        // ─────────────────────────────────────────────
        //  Private helpers
        // ─────────────────────────────────────────────

        private static SafeFileHandle OpenDevice()
        {
            var handle = CreateFileW(
                DevicePath,
                GENERIC_READ | GENERIC_WRITE,
                0, // No sharing
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            return handle;
        }

        /// <summary>One control-device IOCTL: through <see cref="IoSeam"/>
        /// when a test installed one, else open the driver and call
        /// DeviceIoControl. Null buffers marshal as NULL pointers with a
        /// zero length, which is how the size probe is expressed.</summary>
        private static bool TryIo(uint ioctl, byte[] inBuffer, byte[] outBuffer, out int bytesReturned)
        {
            var seam = IoSeam;
            if (seam != null)
            {
                var r = seam(ioctl, inBuffer, outBuffer);
                bytesReturned = r.bytes;
                return r.ok;
            }

            bytesReturned = 0;
            using var handle = OpenDevice();
            if (handle == null || handle.IsInvalid) return false;
            return DeviceIoControl(handle, ioctl, inBuffer, inBuffer?.Length ?? 0,
                outBuffer, outBuffer?.Length ?? 0, out bytesReturned, IntPtr.Zero);
        }

        /// <summary>
        /// Reads a multi-SZ string list from HidHide via a GET IOCTL, or
        /// returns NULL when the driver could not be read. An empty list is a
        /// successful read of an empty list, which is a different fact.
        ///
        /// <para>Two calls, the CLI's shape (HidHideCLI FilterDriverProxy.cpp
        /// GetBlacklist): a probe with NO output buffer, which the driver
        /// answers with the byte count it needs (Logic.c
        /// OnControlDeviceIoGetBlacklist completes the zero-length case with
        /// neededSizeInCharacters * sizeof(WCHAR)), then a read into a buffer
        /// of exactly that many bytes. The old guess-and-grow read (4096
        /// bytes, then 65536) failed on every list past 2048 characters,
        /// because the driver copies with RtlStringCchCopyUnicodeStringEx
        /// (Config.c HidHideCollectionToMultiString) whose validator rejects
        /// any destination over NTSTRSAFE_UNICODE_STRING_MAX_CCH = 32767
        /// characters with STATUS_INVALID_PARAMETER, and 65536 bytes is
        /// 32768 characters. A user with a few pads' worth of expanded
        /// instance paths therefore read as "driver unreadable" and the
        /// null-bail consumers hid nothing. The exact-size buffer passes
        /// the validator for every list the driver can serve at all: the
        /// driver's own cap is the character count it reports, so a list
        /// it cannot hand out is one no client, the CLI included, can read.</para>
        /// </summary>
        private static List<string> GetMultiSzList(uint ioctl)
        {
            var result = new List<string>();

            if (!TryIo(ioctl, null, null, out int needed) || needed <= 0)
                return null;

            byte[] outBuffer = new byte[needed];
            if (!TryIo(ioctl, null, outBuffer, out int bytesReturned))
                return null;
            if (bytesReturned > outBuffer.Length)
                bytesReturned = outBuffer.Length;

            // An EMPTY list is a 2-byte reply, and calling that malformed broke
            // hiding outright. The driver serializes a list as each string plus
            // its terminator plus ONE multi-string terminator, so zero entries
            // is exactly one L'\0' (HidHide Logic.c OnControlDeviceIoGetBlacklist
            // completes with neededSizeInCharacters * sizeof(WCHAR), and
            // Config.c HidHideCollectionToMultiString computes 0 + 1 characters
            // for an empty collection). The old "< 4, at minimum a double-null"
            // assumption therefore misread every successful empty read as a
            // failed read, and the null-bail consumers this round added then
            // skipped SetBlacklist forever. Engine start makes the state
            // routine, since the stale-cloak purge CLEARS the blacklist, so
            // nothing was ever hidden again: the owner's DualSense stayed
            // visible to games with "hide" flagged on.
            //
            // Zero bytes or an odd count IS malformed: the driver always
            // reports at least the terminator character.
            if (bytesReturned < 2 || bytesReturned % 2 != 0)
                return null;

            // Parse multi-SZ: null-separated UTF-16 strings, double-null terminated.
            string fullString = Encoding.Unicode.GetString(outBuffer, 0, bytesReturned);

            // Split on null characters, filter empty entries.
            foreach (string entry in fullString.Split('\0'))
            {
                if (!string.IsNullOrEmpty(entry))
                    result.Add(entry);
            }

            return result;
        }

        /// <summary>
        /// Writes a multi-SZ string list to HidHide via a SET IOCTL and
        /// returns whether the driver accepted it. The result used to be
        /// discarded, so a refused SET (the driver failed the request, or
        /// the control device would not open) was recorded by the sync as
        /// landed and never retried.
        /// </summary>
        private static bool SetMultiSzList(uint ioctl, List<string> entries)
        {
            // Build multi-SZ buffer: each string null-terminated, plus final null.
            var sb = new StringBuilder();
            foreach (string entry in entries)
            {
                if (!string.IsNullOrEmpty(entry))
                {
                    sb.Append(entry);
                    sb.Append('\0');
                }
            }
            sb.Append('\0'); // Double-null terminator.

            byte[] inBuffer = Encoding.Unicode.GetBytes(sb.ToString());
            return TryIo(ioctl, inBuffer, null, out _);
        }

        /// <summary>
        /// Converts a Windows file path to a DOS device path (\Device\HarddiskVolumeN\...).
        /// </summary>
        public static string ToDosDevicePathPublic(string filePath) => ToDosDevicePath(filePath);

        private static string ToDosDevicePath(string filePath)
        {
            try
            {
                string fullPath = Path.GetFullPath(filePath);
                string drive = Path.GetPathRoot(fullPath);
                if (string.IsNullOrEmpty(drive)) return null;

                // Get the drive letter without trailing backslash (e.g., "C:")
                string driveLetter = drive.TrimEnd('\\');

                // Query the DOS device name for this drive letter.
                char[] buffer = new char[512];
                uint result = QueryDosDeviceW(driveLetter, buffer, (uint)buffer.Length);
                if (result == 0) return null;

                // QueryDosDevice returns a multi-SZ; take the first entry.
                int nullIdx = Array.IndexOf(buffer, '\0');
                if (nullIdx < 0) return null;
                string dosDevice = new string(buffer, 0, nullIdx);

                // Build full DOS path: \Device\HarddiskVolumeN + \rest\of\path
                string relativePath = fullPath.Substring(drive.Length);
                return dosDevice + @"\" + relativePath;
            }
            catch
            {
                return null;
            }
        }
    }
}
