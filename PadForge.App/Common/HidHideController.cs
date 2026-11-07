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
        // HidHide 1.2 and later (Logic.h, CTL_CODE function 2054).
        private const uint IOCTL_GET_WLINVERSE = 0x80016018;

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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(
            IntPtr hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private const uint PROCESS_NAME_NATIVE = 0x00000001;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

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

        /// <summary>One thread of this process talks to the control device at
        /// a time. Every open here asks for no sharing, so two threads opening
        /// it together refuse each other, and each refusal reads as a driver
        /// that did not answer. The apply runs on the UI thread, a driver
        /// install probes from a worker, and the release reads the blacklist
        /// from the thread pool each time a batch runs, often while the
        /// apply that queued it is still reading the list back
        /// (HiddenControllerRelease.ReadBlacklist). Taken inside
        /// <see cref="_lock"/> where both are held, never the other way
        /// round.</summary>
        private static readonly object _deviceGate = new();

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

        /// <summary>Test seam for <see cref="TryOpenAsThisProcess"/>: a HID
        /// interface path to (opened, Win32 error). Null in production, where
        /// the probe opens the interface itself.</summary>
        internal static Func<string, (bool opened, int error)> OpenSeam;

        /// <summary>Test seam for <see cref="HidInterfaceOf"/>: an instance
        /// id to its HID interface path, or null. Null in production, where
        /// cfgmgr32 answers.</summary>
        internal static Func<string, string> InterfaceSeam;

        /// <summary>Test seam for <see cref="ChainInstanceIds"/>: a row's HID
        /// instance id to the chain from that node up through the parents of
        /// the same device. Null in production, where cfgmgr32 walks the
        /// device tree.</summary>
        internal static Func<string, List<string>> ChainSeam;

        /// <summary>Test seam for the open of the control device behind
        /// <see cref="IsAvailable"/> and <see cref="TryProbe"/>: the Win32
        /// error the open returns, 0 when it opens. Null in production, where
        /// CreateFileW answers. It runs inside <see cref="_deviceGate"/>, as
        /// the open does.</summary>
        internal static Func<int> ControlOpenSeam;

        /// <summary>Test seam for <see cref="IsInstancePresent"/>: an instance
        /// id to whether its devnode is present. Null in production, where
        /// cfgmgr32 answers.</summary>
        internal static Func<string, bool> PresenceSeam;

        /// <summary>Test seam for the device tree
        /// <see cref="ExpandToBaseContainerAndChildren(string, Func{string, bool}, ICollection{string})"/>
        /// walks: a HID instance id to its chain, base container and the
        /// base's children, as <see cref="ComposeBlacklist"/> takes them, or
        /// null for a node with no tree. Null in production, where cfgmgr32
        /// walks the device tree.</summary>
        internal static Func<string, (IReadOnlyList<PnpNode> chain, PnpNode baseContainer, IReadOnlyList<PnpNode> children)?> TreeSeam;

        /// <summary>Test seam for <see cref="FindInstanceIdsByVidPid"/>: the
        /// instance ids of the present HID-class devices. Null in production,
        /// where setupapi lists them.</summary>
        internal static Func<IEnumerable<string>> PresentHidNodesSeam;

        /// <summary>Test seam for <see cref="CarriesXusbInterface"/>: an
        /// instance id to whether its devnode carries the XUSB device
        /// interface. Null in production, where cfgmgr32 answers.</summary>
        internal static Func<string, bool> XusbInterfaceSeam;

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests HidHideOffLimits), never by the app. A test host
        /// runs on a PC whose HidHide is installed and whose controllers are
        /// in use. A call that lost its seam there, in a test or in a mutant
        /// of this code, opened that PC's control device, where a write to
        /// the blacklist hides its devices and a write to the whitelist or
        /// the active flag changes which programs see them, and a release
        /// with no seam for its cycle disconnected a controller
        /// (HiddenControllerRelease.CyclePort). While this is set, a call
        /// no seam stands in for does nothing and answers as a failed call
        /// would: the control device does not open, as on a PC with no
        /// HidHide, no IOCTL is sent, and no port is cycled. False in
        /// production, where every path is as it was.</summary>
        internal static bool SeamsOnlyForTest;

        /// <summary>Test seam: told of each call that flag kept from this PC,
        /// by a short name ("open", "ioctl 0x8001600C", "cycle" and the USB
        /// device). The test assembly writes them to a file when asked
        /// (HidHideOffLimits), so a test run or a mutation run can show
        /// which calls it would have sent to the PC it ran on. Null in
        /// production, where nothing is refused.</summary>
        internal static Action<string> OffLimitsRefused;

        /// <summary>The last line before a call that changes this PC. It
        /// throws while <see cref="SeamsOnlyForTest"/> is set. Every path
        /// that would take the real device over a seam refuses before it
        /// gets here, so this is reached only when such a refusal has been
        /// lost, and then it stops the call where a test sees it.</summary>
        internal static void ThrowIfOffLimits(string call)
        {
            if (SeamsOnlyForTest)
            {
                OffLimitsRefused?.Invoke("past its refusal: " + call);
                throw new InvalidOperationException(call + " is off limits in a test host, and no seam stands in for it");
            }
        }

        private const int ERROR_FILE_NOT_FOUND = 2;

        /// <summary>Drops the in-process managed set, for tests that
        /// drive the sync through <see cref="IoSeam"/> from a clean
        /// slate. Production never calls it: the managed set is what
        /// RemoveManagedDevices needs to leave other tools' entries
        /// alone.</summary>
        internal static void ResetManagedForTests()
        {
            lock (_lock) _managedDeviceIds.Clear();
        }

        /// <summary>Whether the id is in the managed set, the blacklist
        /// entries PadForge counts as its own: the ones its last sync asked
        /// for and the driver carries, or the ones it adopted from the
        /// session before.</summary>
        internal static bool IsManagedDevice(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;
            lock (_lock) return _managedDeviceIds.Contains(instanceId);
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
                lock (_deviceGate)
                {
                    return OpenControlDevice(out _);
                }
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
                lock (_deviceGate)
                {
                    return OpenControlDevice(out win32Error);
                }
            }
            catch
            {
                win32Error = -1;
                return false;
            }
        }

        /// <summary>Opens the control device and closes it again, the open
        /// behind <see cref="IsAvailable"/> and <see cref="TryProbe"/>. True
        /// when it opened, else false with the Win32 error. The caller holds
        /// <see cref="_deviceGate"/>. A test's <see cref="ControlOpenSeam"/>
        /// answers in the device's place.</summary>
        private static bool OpenControlDevice(out int win32Error)
        {
            win32Error = 0;
            var open = ControlOpenSeam;
            if (open != null)
            {
                win32Error = open();
                return win32Error == 0;
            }
            // A fake driver installed through IoSeam stands in for the
            // control device, so a test can run the apply without the real one.
            if (IoSeam != null) return true;
            if (SeamsOnlyForTest)
            {
                // No seam, in a test host: the answer of a PC with no
                // HidHide, whose control device is not there.
                OffLimitsRefused?.Invoke("open");
                win32Error = ERROR_FILE_NOT_FOUND;
                return false;
            }
            ThrowIfOffLimits("Opening HidHide's control device");
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

        /// <summary>One configuration snapshot for the diagnostics log (#395):
        /// whether the driver answered, its active flag, the whitelist size,
        /// and for each named image whether some whitelist entry ends in that
        /// file name. Every read reports its own outcome. It describes
        /// configuration, not whether any process holds a device handle, and
        /// a file-name match is not a full-path match. Two IOCTLs however
        /// many names are asked about.</summary>
        public static string DescribeReach(params string[] exeNames)
        {
            var sb = new System.Text.StringBuilder();
            List<string> whitelist = null;
            try
            {
                if (!IsAvailable())
                {
                    sb.Append("hidhide=unavailable");
                }
                else
                {
                    // Each read reports its own outcome. GetActive folds a
                    // failed IOCTL into false and GetWhitelist into null, so
                    // the snapshot asks the outcome-bearing forms.
                    sb.Append("hidhide=ok active=");
                    try { sb.Append(TryGetActive(out bool active) ? (active ? "true" : "false") : "read-failed"); }
                    catch (Exception ex) { sb.Append("err:").Append(ex.GetType().Name); }
                    sb.Append(" whitelist=");
                    try { whitelist = GetWhitelist(); sb.Append(whitelist == null ? "read-failed" : whitelist.Count.ToString()); }
                    catch (Exception ex) { sb.Append("err:").Append(ex.GetType().Name); }
                }
            }
            catch (Exception ex)
            {
                sb.Append("hidhide=err:").Append(ex.GetType().Name);
            }
            foreach (var exe in exeNames ?? Array.Empty<string>())
            {
                string verdict = "unknown";
                if (whitelist != null)
                {
                    verdict = "not-listed";
                    foreach (var p in whitelist)
                        if (p != null && p.EndsWith("\\" + exe, StringComparison.OrdinalIgnoreCase)) { verdict = "name-listed"; break; }
                }
                sb.Append(' ').Append(exe).Append('=').Append(verdict);
            }
            return sb.ToString();
        }

        /// <summary>The entries of HidHide's device list that PadForge did
        /// not put there, in the list's order (#541). The read-back after a
        /// sync checks only PadForge's own entries, so an entry the user or
        /// another program added never reached a trace, and an entry naming
        /// a PadForge virtual controller hides it from every program HidHide
        /// does not list.</summary>
        internal static List<string> OthersIn(IEnumerable<string> listed, Func<string, bool> isPadForges)
        {
            var others = new List<string>();
            if (listed == null) return others;
            foreach (var id in listed)
                if (!string.IsNullOrEmpty(id) && !isPadForges(id)) others.Add(id);
            return others;
        }

        /// <summary>Test seam for <see cref="DescribeSnapshotState"/>: the
        /// paths of the present XUSB interfaces, the devices XInput opens.
        /// Null in production, where cfgmgr32 lists them.</summary>
        internal static Func<IEnumerable<string>> XusbInterfaceListSeam;

        /// <summary>Test seam for <see cref="DescribeSnapshotState"/>: an
        /// instance id to its devnode's problem code, 0 for none, or null
        /// for a node that is not present. Null in production, where
        /// cfgmgr32 answers.</summary>
        internal static Func<string, int?> ProblemSeam;

        /// <summary>The lines Save Snapshot adds after the event history
        /// (#541): HidHide's flags and device list with PadForge's entries
        /// counted apart from the rest, every XInput interface Windows
        /// lists, and each present PadForge virtual controller's HID node
        /// with its problem code, whether HidHide lists it and whether it
        /// carries an XInput interface. They say what other programs can
        /// open at that moment. A report of virtual controllers missing from
        /// XInput and Game Controllers while another device was plugged in
        /// had a log that could show none of it.</summary>
        internal static List<string> DescribeSnapshotState()
        {
            var lines = new List<string>();
            List<string> listed = null;
            try
            {
                if (!IsAvailable())
                {
                    lines.Add("STATE hidhide unavailable");
                }
                else
                {
                    string active = TryGetActive(out bool on) ? (on ? "true" : "false") : "read-failed";
                    string inverse = TryGetInverse(out bool inverted) ? (inverted ? "true" : "false") : "read-failed";
                    listed = GetBlacklist();
                    if (listed == null)
                    {
                        lines.Add($"STATE hidhide active={active} inverse={inverse} list=read-failed");
                    }
                    else
                    {
                        var others = OthersIn(listed, IsManagedDevice);
                        lines.Add($"STATE hidhide active={active} inverse={inverse} listed={listed.Count} padforge={listed.Count - others.Count} others={others.Count}"
                            + (others.Count > 0 ? $" [{string.Join(" | ", others)}]" : ""));
                        var virtuals = listed.Where(IsHidMaestroDevice).ToList();
                        if (virtuals.Count > 0)
                            lines.Add($"STATE hidhide lists PadForge virtual controllers: [{string.Join(" | ", virtuals)}]");
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add("STATE hidhide err:" + ex.GetType().Name);
            }

            try
            {
                var xusb = PresentXusbInterfaces();
                lines.Add($"STATE xinput interfaces n={xusb.Count}" + (xusb.Count > 0 ? $" [{string.Join(" | ", xusb)}]" : ""));
            }
            catch (Exception ex)
            {
                lines.Add("STATE xinput err:" + ex.GetType().Name);
            }

            try
            {
                var hidden = listed == null ? null : new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
                int count = 0;
                foreach (string id in PresentHidNodes())
                {
                    if (!IsHidMaestroDevice(id)) continue;
                    count++;
                    int? problem = ProblemOf(id);
                    lines.Add($"STATE virtual {id} problem={(problem == null ? "not-present" : problem == 0 ? "none" : problem.Value.ToString())}"
                        + $" hidden={(hidden == null ? "unknown" : hidden.Contains(id) ? "yes" : "no")}"
                        + $" xinput={(CarriesXusbInterface(id) ? "yes" : "no")}");
                }
                if (count == 0) lines.Add("STATE virtual none");
            }
            catch (Exception ex)
            {
                lines.Add("STATE virtual err:" + ex.GetType().Name);
            }
            return lines;
        }

        /// <summary>The paths of every present XUSB interface, the list
        /// xinput1_4 opens its controllers from.</summary>
        private static List<string> PresentXusbInterfaces()
        {
            var seam = XusbInterfaceListSeam;
            if (seam != null)
                return seam().Where(p => !string.IsNullOrEmpty(p)).ToList();

            var paths = new List<string>();
            var guid = PadForge.Engine.HidMaestroAncestry.XusbInterfaceGuid;
            // The list can grow between the size and the read, which the
            // read answers with CR_BUFFER_SMALL, so it asks again.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, null,
                        CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || len <= 1)
                    return paths;
                var buffer = new char[len];
                int cr = CM_Get_Device_Interface_ListW(ref guid, null, buffer, len,
                    CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
                if (cr == CR_BUFFER_SMALL) continue;
                if (cr != CR_SUCCESS) return paths;

                // Multi-SZ: each path null-terminated, an empty string ends it.
                int start = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    if (buffer[i] != '\0') continue;
                    if (i == start) break;
                    paths.Add(new string(buffer, start, i - start));
                    start = i + 1;
                }
                return paths;
            }
            return paths;
        }

        /// <summary>A devnode's problem code: 0 for none, null when the node
        /// is not present or its status does not read.</summary>
        private static int? ProblemOf(string instanceId)
        {
            var seam = ProblemSeam;
            if (seam != null) return seam(instanceId);
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS) return null;
            if (CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) != CR_SUCCESS) return null;
            return (status & DN_HAS_PROBLEM) != 0 ? (int)problem : 0;
        }

        private const int CR_BUFFER_SMALL = 0x1A;
        private const uint DN_HAS_PROBLEM = 0x00000400;

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

        /// <summary>
        /// Replaces the entire application whitelist. Returns whether the
        /// driver accepted the write.
        /// </summary>
        public static bool SetWhitelist(List<string> paths)
        {
            return SetMultiSzList(IOCTL_SET_WHITELIST, paths);
        }

        /// <summary>Takes the named entries off the application list and
        /// leaves every other entry. True when none was there or the write
        /// landed, false when the list could not be read or the write was
        /// refused.</summary>
        public static bool RemoveWhitelistEntries(IEnumerable<string> paths)
        {
            var list = GetWhitelist();
            if (list == null) return false;
            int removed = 0;
            foreach (var path in paths ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(path))
                    removed += list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            return removed == 0 || SetWhitelist(list);
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

        /// <summary>GetActive with the read outcome separated from the value
        /// (#395 diagnostics): a failed IOCTL is not "inactive".</summary>
        public static bool TryGetActive(out bool active)
        {
            byte[] outBuffer = new byte[1];
            active = false;
            if (!TryIo(IOCTL_GET_ACTIVE, null, outBuffer, out _))
                return false;
            active = outBuffer[0] != 0;
            return true;
        }

        /// <summary>Whether HidHide's application list is inverted (#484).
        /// With Inverse application cloak on, the driver hides a blacklisted
        /// device from the applications ON the list and shows it to every
        /// other one (Logic.c Whitelisted returns the negation), so an
        /// application that lists itself hides the device from itself. False
        /// means the flag could not be read, which a driver older than the
        /// flag answers by rejecting the IOCTL.</summary>
        public static bool TryGetInverse(out bool inverse)
        {
            byte[] outBuffer = new byte[1];
            inverse = false;
            if (!TryIo(IOCTL_GET_WLINVERSE, null, outBuffer, out _))
                return false;
            inverse = outBuffer[0] != 0;
            return true;
        }

        /// <summary>Opens a HID interface with no access rights and closes it
        /// again (#484). It is the open SDL's enumeration makes for every
        /// device (hidapi windows/hid.c, open_device(path, FALSE)), and a
        /// device that fails it drops out of SDL's next enumeration. HidHide
        /// refuses it with ERROR_ACCESS_DENIED for a process it does not count
        /// as whitelisted (Logic.c OnDeviceFileCreate). True when the open
        /// worked, else false with the Win32 error.</summary>
        internal static bool TryOpenAsThisProcess(string devicePath, out int error)
        {
            var seam = OpenSeam;
            if (seam != null)
            {
                var r = seam(devicePath);
                error = r.error;
                return r.opened;
            }
            error = 0;
            using var handle = CreateFileW(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle == null || handle.IsInvalid)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            return true;
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

        /// <summary>Removes these of PadForge's entries and leaves its others,
        /// for a touchpad PadForge stops reading while the cloaks it keeps
        /// between launches stay. Entries of other tools and ids PadForge
        /// did not write are untouched. False when the list could not be
        /// read or the write was refused.</summary>
        public static bool RemoveManaged(IEnumerable<string> ids)
        {
            lock (_lock)
            {
                var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in ids ?? Array.Empty<string>())
                    if (!string.IsNullOrEmpty(id) && _managedDeviceIds.Contains(id)) drop.Add(id);
                if (drop.Count == 0) return true;
                var list = GetBlacklist();
                // A failed read is never written back, as in RemoveManagedDevices.
                if (list == null) return false;
                list.RemoveAll(id => drop.Contains(id));
                if (!SetBlacklist(list)) return false;
                foreach (var id in drop) _managedDeviceIds.Remove(id);
                return true;
            }
        }

        /// <summary>Takes these ids off the driver's list, whoever wrote them,
        /// and leaves every other entry. For a touchpad given back whose hide a
        /// session before left behind, which no managed set of this session
        /// names. <paramref name="removed"/> lists the ids the list carried.
        /// False when the list could not be read or the write was
        /// refused.</summary>
        public static bool RemoveFromBlacklist(IEnumerable<string> ids, out List<string> removed)
        {
            removed = new List<string>();
            lock (_lock)
            {
                var list = GetBlacklist();
                // A failed read is never written back, as in RemoveManagedDevices.
                if (list == null) return false;
                foreach (var id in ids ?? Array.Empty<string>())
                    if (!string.IsNullOrEmpty(id) && list.Contains(id, StringComparer.OrdinalIgnoreCase))
                        removed.Add(id);
                if (removed.Count == 0) return true;
                var drop = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
                list.RemoveAll(id => drop.Contains(id));
                if (!SetBlacklist(list))
                {
                    removed.Clear();
                    return false;
                }
                foreach (var id in drop) _managedDeviceIds.Remove(id);
                return true;
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
        /// the driver accepted (or when there was nothing to write).</para>
        ///
        /// <para>With <paramref name="allowAdditions"/> false nothing new is
        /// hidden and removals run as usual (#484). PadForge holds new hides
        /// back while its own whitelist entry is unconfirmed, since a device
        /// hidden then can be hidden from PadForge itself. The managed set
        /// then keeps only the desired ids the driver already carries, so a
        /// held-back id is added by the next sync that allows it.</para></summary>
        public static bool SyncManagedDevices(HashSet<string> desiredIds, out List<string> added, out List<string> removed, bool allowAdditions = true)
            => SyncManagedDevices(desiredIds, out added, out removed, out _, allowAdditions);

        /// <summary>The sync above, also naming the desired ids it held back
        /// because <paramref name="allowAdditions"/> was false. The driver
        /// never received those, so a read-back that misses them is not a
        /// dropped write.</summary>
        public static bool SyncManagedDevices(HashSet<string> desiredIds, out List<string> added, out List<string> removed,
            out List<string> heldBack, bool allowAdditions)
        {
            added = new List<string>();
            removed = new List<string>();
            heldBack = new List<string>();
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
                    if (string.IsNullOrEmpty(id) || present.Contains(id)) continue;
                    if (allowAdditions) added.Add(id);
                    else heldBack.Add(id);
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
                    if (allowAdditions || present.Contains(id))
                        _managedDeviceIds.Add(id);
                return true;
            }
        }

        /// <summary>Seeds the managed set from the driver's CURRENT list, so
        /// a session that kept the previous session's cloaks can still take
        /// them away.
        ///
        /// <para>Without this the managed set starts empty,
        /// <see cref="SyncManagedDevices"/> computes its removals from that
        /// empty set, and the driver's list can only ever grow. An entry an
        /// older PadForge wrote too broadly then survives every apply, every
        /// restart and every reboot, with nothing in this session's state
        /// saying so. That is what left an upgrading Legion Go owner's
        /// touchpad dead after the #400 fix narrowed what gets written: the
        /// new build wrote the right set and never removed the old one
        /// (#397 follow-up).</para>
        ///
        /// <para>Adopting entries another tool may have written is no more
        /// aggressive than the cloaks-off path, which calls
        /// <see cref="ClearAll"/> and drops everyone's entries outright.
        /// Returns how many were adopted, or zero when the list could not
        /// be read.</para></summary>
        public static int AdoptExistingAsManaged()
        {
            lock (_lock)
            {
                var list = GetBlacklist();
                // -1, not 0. A failed read and an empty list are different
                // answers, and the caller latches "adopted, never again" on one
                // of them. Reporting both as zero let a single refused open
                // retire adoption for the life of the process, with every kept
                // cloak left in the driver and nothing able to name it.
                if (list == null) return -1;
                _managedDeviceIds.Clear();
                foreach (var id in list)
                    if (!string.IsNullOrEmpty(id))
                        _managedDeviceIds.Add(id);
                return _managedDeviceIds.Count;
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
                // A REFUSED write must not be recorded as a clear. The control
                // device is exclusive (HidHide ControlDevice.c calls
                // WdfDeviceInitSetExclusive), so any other client holding it
                // fails our open and this write with it. Emptying the managed
                // set on that path left the driver holding the previous
                // session's entries with nothing able to name them: the sync
                // only ever retires ids it finds in _managedDeviceIds, so those
                // devices stayed hidden forever. Adopting whatever the driver
                // still holds keeps them retirable, which is what
                // AdoptExistingAsManaged does for the keep-cloaks branch.
                if (SetBlacklist(new List<string>()))
                {
                    SetActive(false);
                    _managedDeviceIds.Clear();
                }
                else
                {
                    var stillThere = GetBlacklist();
                    if (stillThere != null)
                    {
                        _managedDeviceIds.Clear();
                        foreach (var id in stillThere)
                            if (!string.IsNullOrEmpty(id))
                                _managedDeviceIds.Add(id);
                    }
                }
            }
        }

        /// <summary>True when a device instance id or interface path names
        /// this vendor and product id in one of the forms Windows writes. The
        /// lookup below and BluetoothFirmwareService's check of an XInput
        /// slot's interface both match through it.</summary>
        internal static bool NamesVidPid(string id, ushort vendorId, ushort productId)
        {
            if (string.IsNullOrEmpty(id)) return false;

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

            // Match standard USB format, BLE GATT format, or BT Classic BR/EDR format.
            return id.Contains(vidPidUsb, StringComparison.OrdinalIgnoreCase)
                || (id.Contains(pidBle, StringComparison.OrdinalIgnoreCase)
                    && (id.Contains(vidBle02, StringComparison.OrdinalIgnoreCase)
                        || id.Contains(vidBle01, StringComparison.OrdinalIgnoreCase)
                        || id.Contains(vidBrEdr02, StringComparison.OrdinalIgnoreCase)
                        || id.Contains(vidBrEdr01, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Finds all present HID device instance IDs matching the given VID/PID.
        /// Used as a fallback when a device has a synthetic path (e.g., XInput#0)
        /// that can't be converted to a valid instance ID. HIDMaestro's nodes
        /// and nodes PadForge reads nothing from are left out
        /// (<see cref="IsOutsideVidPidLookup"/>).
        /// </summary>
        public static List<string> FindInstanceIdsByVidPid(ushort vendorId, ushort productId)
        {
            // Takes a node of this vendor and product id unless the lookup
            // leaves it out: HIDMaestro's own, or one PadForge reads nothing
            // from (IsOutsideVidPidLookup).
            var result = new List<string>();
            foreach (string instanceId in PresentHidNodes())
                if (NamesVidPid(instanceId, vendorId, productId) && !IsOutsideVidPidLookup(instanceId))
                    result.Add(instanceId);

            // setupapi hands these back in whatever order it enumerated
            // them, and the Devices page shows element [0] as the device's
            // path. On a composite pad every collection matches the same
            // VID/PID, so an unsorted [0] was whichever collection the bus
            // happened to list first: a Legion Go owner saw a digitizer
            // collection under MI_01 named as the path of the XInput
            // gamepad, and it persisted into PadForge.xml and into every
            // bug report copied from that page. Sorting makes the pick
            // deterministic, and it puts the IG_ game controller ahead of
            // the MI_ interface collections into the bargain.
            // StableXInputInstance.FindAll sorts for exactly this reason.
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>The instance ids of the present HID-class devnodes, in
        /// the order setupapi lists them, or from
        /// <see cref="PresentHidNodesSeam"/> when a test installed one.</summary>
        private static List<string> PresentHidNodes()
        {
            var ids = new List<string>();
            var presentSeam = PresentHidNodesSeam;
            if (presentSeam != null)
            {
                foreach (string instanceId in presentSeam())
                    if (!string.IsNullOrEmpty(instanceId)) ids.Add(instanceId);
                return ids;
            }

            var guid = GUID_DEVCLASS_HIDCLASS;
            IntPtr devInfoSet = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfoSet == (IntPtr)(-1)) return ids;

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
                        ids.Add(nullIdx >= 0 ? new string(buffer, 0, nullIdx) : new string(buffer));
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
            return ids;
        }

        /// <summary>Public alias so callers scrubbing stale cached entries can filter.</summary>
        public static bool IsHidMaestroDeviceInstance(string instanceId) => IsHidMaestroDevice(instanceId);

        /// <summary>One devnode on the walk from a node up through its
        /// parents, as the HIDMaestro test reads it.</summary>
        internal readonly record struct OwnerHop(string InstanceId, IReadOnlyList<string> HardwareIds, string Service, string Manufacturer);

        /// <summary>Test seam for the walk behind
        /// <see cref="IsHidMaestroDeviceInstance"/>: an instance id to its
        /// hops, the node itself first, or null for a node that is not
        /// present. Null in production, where cfgmgr32 walks the device
        /// tree.</summary>
        internal static Func<string, IEnumerable<OwnerHop>> OwnerChainSeam;

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
        ///
        /// A composite persona has no such marker. It is told by where it
        /// sits, under the usbip-win2 host controller, together with one of
        /// two things: HIDMaestro's own serial on its USB device, or a vendor
        /// and product id of a persona PadForge can own
        /// (<see cref="IsHidMaestroChain"/>). That host controller also
        /// carries real pads attached from another machine, and such a pad
        /// is not HIDMaestro's. Whether PadForge reads it, and so whether
        /// the lookup offers it, is a second question
        /// (<see cref="IsOutsideVidPidLookup"/>).
        /// </summary>
        private static bool IsHidMaestroDevice(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;

            // Fast string-pattern check on the leaf ID itself.
            if (MatchesHidMaestroPattern(instanceId)) return true;

            // If the device isn't present in PnP right now, the fast-path
            // check above is all we can do.
            var hops = OwnerChainOf(instanceId);
            if (hops == null) return false;

            return IsHidMaestroChain(hops, () => PersonaInScope(instanceId));
        }

        /// <summary>The walk from a node up through its parents, the node
        /// itself first, or null for a node that is not present.</summary>
        private static IEnumerable<OwnerHop> OwnerChainOf(string instanceId)
        {
            var seam = OwnerChainSeam;
            if (seam != null) return seam(instanceId);
            return CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0 ? null : WalkOwnerChain(devInst);
        }

        /// <summary>Whether the lookup by vendor and product id leaves a
        /// node out. It offers the nodes of real devices PadForge reads, so
        /// it leaves out a node that is HIDMaestro's
        /// (<see cref="IsHidMaestroChain"/>) and a node PadForge reads
        /// nothing from (<see cref="ReadersDrop"/>).
        ///
        /// <para>The second matters under a usbip-win2 host controller
        /// HIDMaestro has stamped. A pad attached there from another machine
        /// is a real device, but the readers drop it with the personas, so
        /// PadForge shows no row for it. Offered by the lookup, it counted
        /// as a second device of its model and a local pad lost its hide
        /// (InputService.HidHideSiblingSweepAllowed), or a local pad's row
        /// hid it and kept its node. A node of the model that is not present
        /// is judged by its own id alone, as before.</para></summary>
        internal static bool IsOutsideVidPidLookup(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return false;
            if (MatchesHidMaestroPattern(instanceId)) return true;

            var walk = OwnerChainOf(instanceId);
            if (walk == null) return false;
            // Both tests read the walk, so it is made once.
            var hops = walk as IReadOnlyList<OwnerHop> ?? new List<OwnerHop>(walk);

            return IsHidMaestroChain(hops, () => PersonaInScope(instanceId))
                || ReadersDrop(hops, CarriesXusbInterface);
        }

        /// <summary>Whether PadForge's readers drop the node a walk starts
        /// at, so that PadForge reads nothing from it: the walks of the SDL
        /// fork and the OpenXInput fork, read where the identity of an
        /// XInput pad reads them too
        /// (<see cref="PadForge.Engine.HidMaestroAncestry.ReadersDrop"/>).
        ///
        /// <para>SDL's GameInput backend makes no such test
        /// (SDL_gameinputjoystick.cpp:261-415), so PadForge reads what it
        /// lists wherever it sits. With the hints PadForge sets it lists
        /// only the instruments of its raw report table (lines 328-332 and
        /// 579-622, SDL_gameinput_rawtype.h:43-99). Those are Xbox One
        /// instruments, which Windows puts under xboxgip like an Xbox One
        /// pad, where neither walk reaches the stamp.</para></summary>
        internal static bool ReadersDrop(IReadOnlyList<OwnerHop> hops, Func<string, bool> carriesXusb)
            => hops != null && PadForge.Engine.HidMaestroAncestry.ReadersDrop(
                new List<PadForge.Engine.AncestryHop>(AncestryHops(hops)), carriesXusb);

        /// <summary>Whether a devnode carries a present device interface of
        /// the XUSB class, the one OpenXInput lists
        /// (<see cref="PadForge.Engine.HidMaestroAncestry.CarriesXusbInterface"/>).
        /// A test's <see cref="XusbInterfaceSeam"/> answers in its
        /// place.</summary>
        private static bool CarriesXusbInterface(string instanceId)
        {
            var seam = XusbInterfaceSeam;
            return seam != null ? seam(instanceId) : PadForge.Engine.HidMaestroAncestry.CarriesXusbInterface(instanceId);
        }

        /// <summary>The HIDMaestro test on a walk from a node up through its
        /// parents, the node itself first.
        ///
        /// <para>Three markers name a HIDMaestro device wherever it sits,
        /// and any one settles it: HIDMaestro in an instance id (or one of
        /// its ROOT\VID_ enumerators), HIDMaestro in a hardware id, or
        /// HIDMaestro as the manufacturer. Every HIDMaestro HID child
        /// carries HID\HIDMaestro in its hardware ids and the node above it
        /// carries root\HIDMaestro, so the test holds whether or not the
        /// manufacturer read succeeds (char[] marshalling of
        /// CM_Get_DevNode_Registry_Property can silently return empty for
        /// some devices).</para>
        ///
        /// <para>A composite USB persona carries none of them (#391
        /// follow-up). It enumerates through the REAL USB stack (vhci, UDE,
        /// usbccgp, hidusb) under its model's own vendor and product ids, so
        /// no instance id, hardware id or manufacturer name on its path says
        /// HIDMaestro. Two things set it apart. It sits under the usbip-win2
        /// emulated host controller, which HIDMaestro stamps with the
        /// hardware id ROOT\HIDMAESTRO_UDE (HM#42, v1.4.3), four hops up
        /// from the persona's HID interface, and whose service is
        /// usbip2_ude where the stamp is missing. And it serves a USB serial
        /// (HM#60): HIDMaestro's own, HM and twelve hex digits, unless its
        /// profile carries a captured one, so Windows names its USB device
        /// USB\VID_054C&amp;PID_0CE6\HM0123456789AB
        /// (HidMaestroAncestry.IsPersonaUsbInstanceId). Without this leg the
        /// VID/PID sweep that hides a physical DualSense also hid the
        /// virtual one the slot had just created (the reporter's r3503
        /// trace: a second 054C:0CE6 tree at depth 3 and 4 swept two seconds
        /// after the PlayStation slot's persona attached).</para>
        ///
        /// <para>That host controller carries every device attached through
        /// usbip-win2, a real pad forwarded from another machine included,
        /// so its stamp and its service name the transport and not the
        /// owner. Under it, a node at or below a USB device named by
        /// HIDMaestro's serial is a persona, whichever session made it and
        /// for as long as Windows lists it. The id scope ends
        /// InputManager.PersonaRetireGraceMs after a persona is disposed,
        /// Windows can list the device longer, and such a persona once read
        /// as a real pad of its model here. Any other node under the host
        /// controller is a persona only while
        /// <paramref name="personaInScope"/> says one of its vendor and
        /// product id can be PadForge's own. That covers the Steam Deck's
        /// persona, whose serial is a captured one, and it takes a real pad
        /// of the model of a live persona with it, since nothing tells the
        /// two apart.</para>
        ///
        /// <para>The instance ids, the hardware ids and the host controller
        /// are read by the rule Step 1's walk and the identity of an XInput
        /// pad read on the same hops
        /// (<see cref="PadForge.Engine.HidMaestroAncestry.IsHidMaestroWalk"/>,
        /// whose classification is <see cref="PadForge.Engine.HidMaestroAncestry.Classify"/>):
        /// the host controller's stamp against every other HIDMaestro id, the
        /// persona serial below it, the device's own node first and nine
        /// nodes at most. The hiding side has no copy of that rule, so it
        /// cannot judge a node one way while Step 1 judges the device it
        /// opens the other (InputManager.SelfVirtualSuppressions.PassOver).
        /// The manufacturer is the marker the hiding side reads alone, where
        /// Step 1 reads the device's path and serial.</para></summary>
        internal static bool IsHidMaestroChain(IEnumerable<OwnerHop> hops, Func<bool> personaInScope)
        {
            if (hops == null) return false;
            // Both readings take the walk, so it is made once.
            var walk = hops as IReadOnlyList<OwnerHop> ?? new List<OwnerHop>(hops);
            foreach (var hop in walk)
            {
                if (string.Equals(hop.Manufacturer, "HIDMaestro", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // The serial does not settle a node under the host controller
            // alone, so the rule asks the scope.
            return PadForge.Engine.HidMaestroAncestry.IsHidMaestroWalk(
                new List<PadForge.Engine.AncestryHop>(AncestryHops(walk)), personaInScope);
        }

        /// <summary>The hops of a walk as the shared rule reads
        /// them.</summary>
        private static IEnumerable<PadForge.Engine.AncestryHop> AncestryHops(IEnumerable<OwnerHop> walk)
        {
            foreach (var hop in walk)
                yield return new PadForge.Engine.AncestryHop(hop.InstanceId, hop.HardwareIds, hop.Service);
        }

        /// <summary>Test seam for <see cref="PersonaInScope"/>: a vendor and
        /// product id to whether a composite persona of that model can be
        /// PadForge's own. Null in production, where
        /// InputManager.ChecksUsbipAncestry answers.</summary>
        internal static Func<ushort, ushort, bool> PersonaScopeSeam;

        /// <summary>Whether a node under the usbip-win2 host controller, with
        /// no USB device named by HIDMaestro's serial on the way up, can be
        /// one of PadForge's own composite personas: a persona of its vendor
        /// and product id is live, being created, was disposed within the
        /// grace, or could be left from the last session while the launch
        /// sweep runs (InputManager.ChecksUsbipAncestry). Step 1 asks the
        /// same of a device it finds there with no such serial
        /// (InputManager.SelfVirtualSuppressions.PassOver). A persona is a
        /// USB device, so each of its nodes carries the
        /// VID_xxxx&amp;PID_yyyy pair, and a node without one is no
        /// persona.</summary>
        private static bool PersonaInScope(string instanceId)
        {
            string token = VidPidToken(instanceId);
            if (token == null) return false;
            // "VID_xxxx&PID_yyyy", the form VidPidToken writes.
            ushort vendorId = Convert.ToUInt16(token.Substring(4, 4), 16);
            ushort productId = Convert.ToUInt16(token.Substring(13, 4), 16);
            var seam = PersonaScopeSeam;
            return seam != null
                ? seam(vendorId, productId)
                : PadForge.Common.Input.InputManager.ChecksUsbipAncestry(vendorId, productId);
        }

        /// <summary>The live walk: the node and up to fifteen of its
        /// parents.</summary>
        private static IEnumerable<OwnerHop> WalkOwnerChain(uint devInst)
        {
            var idBuf = new System.Text.StringBuilder(512);
            for (int depth = 0; depth < 16; depth++)
            {
                idBuf.Clear();
                idBuf.EnsureCapacity(512);
                string id = CM_Get_Device_IDW(devInst, idBuf, idBuf.Capacity, 0) == 0 ? idBuf.ToString() : null;
                yield return new OwnerHop(id, ReadHardwareIds(devInst), GetDevNodeService(devInst), ReadManufacturer(devInst));

                if (CM_Get_Parent(out uint parent, devInst, 0) != 0) break;
                if (parent == 0 || parent == devInst) break;
                devInst = parent;
            }
        }

        /// <summary>An instance id that names HIDMaestro, by the shared rule
        /// (<see cref="PadForge.Engine.HidMaestroAncestry.NamesHidMaestro"/>).</summary>
        private static bool MatchesHidMaestroPattern(string id)
            => PadForge.Engine.HidMaestroAncestry.NamesHidMaestro(id);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, System.Text.StringBuilder buffer, int len, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Registry_PropertyW(
            uint devInst, uint property, out uint pulRegDataType,
            [Out] char[] buffer, ref int length, uint flags);

        private const uint CM_DRP_HARDWAREID = 0x02;
        private const uint CM_DRP_MFG = 0x0D;

        /// <summary>The devnode's hardware ids, empty when they do not
        /// read.</summary>
        private static List<string> ReadHardwareIds(uint devInst)
        {
            var ids = new List<string>();
            var buffer = new char[1024];
            int length = buffer.Length * 2;
            if (CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_HARDWAREID,
                    out _, buffer, ref length, 0) != 0)
                return ids;

            int charCount = length / 2;
            int start = 0;
            for (int i = 0; i < charCount; i++)
            {
                if (buffer[i] == '\0')
                {
                    if (i == start) break;
                    ids.Add(new string(buffer, start, i - start));
                    start = i + 1;
                }
            }
            return ids;
        }

        /// <summary>The devnode's manufacturer string, or null when it does
        /// not read.</summary>
        private static string ReadManufacturer(uint devInst)
        {
            var mfg = new char[128];
            int mfgLen = mfg.Length * 2;
            if (CM_Get_DevNode_Registry_PropertyW(devInst, CM_DRP_MFG, out _, mfg, ref mfgLen, 0) != 0)
                return null;
            int strLen = 0;
            while (strLen < mfg.Length && mfg[strLen] != '\0') strLen++;
            return new string(mfg, 0, strLen);
        }

        /// <summary>Whether an instance id that <see cref="DevicePathToInstanceId"/>
        /// made from a row's device path names a device node, as opposed to a
        /// synthetic path such as "XInput#0", which comes back as "XInput\0".
        /// A HID collection counts whatever follows "HID\": a collection of a
        /// root-enumerated device carries no vendor id, and vJoy's are
        /// "HID\HIDCLASS&amp;COLnn" (vJoyInstallLib.cpp:444), which a test for
        /// "VID_" alone sent to the lookup by vendor and product id, where no
        /// node matched and nothing was hidden (discussion #515). Any other id
        /// counts when it carries a USB or Bluetooth vendor id.</summary>
        internal static bool IsDeviceNodeId(string instanceId)
            => !string.IsNullOrEmpty(instanceId)
               && (instanceId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)
                   || instanceId.Contains("VID_", StringComparison.OrdinalIgnoreCase)
                   || instanceId.Contains("VID&", StringComparison.OrdinalIgnoreCase));

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

        /// <summary>The "VID_xxxx&PID_yyyy" token of an instance id, or null
        /// when it carries none. Every interface and HID collection of one USB
        /// composite device repeats it, and the composite parent above them (a
        /// hub) does not, so it bounds a parent walk to one physical device by
        /// construction.</summary>
        internal static string VidPidToken(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;

            // Scan every VID_ in the string, not only the first. ROOT\VID_045E&XI_00
            // carries one with no PID of its own, and taking the first blindly
            // let IndexOf reach across the backslash into the NEXT path segment
            // and return "VID_045E&XI_00\PID_0B13" as if it were one field.
            int v = -1;
            while ((v = instanceId.IndexOf("VID_", v + 1, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                // VID_ + exactly four hex, one separator, PID_ + exactly four
                // hex. Windows writes both fields at that width, and anything
                // wider or narrower is not the field pair we are matching on.
                int vid = v + 4;
                if (!IsHexRun(instanceId, vid, 4)) continue;
                int sep = vid + 4;
                if (sep >= instanceId.Length) continue;
                char c = instanceId[sep];
                if (c != '&' && c != '+') continue;
                int pid = sep + 1;
                if (!Substr(instanceId, pid, 4).Equals("PID_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsHexRun(instanceId, pid + 4, 4)) continue;
                // A fifth hex digit means the field is wider than the pair we
                // are reading, so it is not this shape.
                if (IsHexRun(instanceId, pid + 8, 1)) continue;

                // Normalized to the ampersand form so the two separators
                // Windows uses compare equal. FTDIBUS writes VID_0403+PID_6001
                // for the same pair USB writes with an ampersand, and a walk
                // that crosses the two would otherwise stop one hop early.
                return ("VID_" + instanceId.Substring(vid, 4)
                        + "&PID_" + instanceId.Substring(pid + 4, 4)).ToUpperInvariant();
            }
            return null;
        }

        private static bool IsHexRun(string s, int at, int count)
        {
            if (at < 0 || at + count > s.Length) return false;
            for (int i = 0; i < count; i++) if (!Uri.IsHexDigit(s[at + i])) return false;
            return true;
        }

        private static string Substr(string s, int at, int count)
            => at < 0 || at + count > s.Length ? string.Empty : s.Substring(at, count);

        /// <summary>Whether a devnode belongs to the same physical device as
        /// the node a walk started from.
        ///
        /// <para>Normally that is ContainerId equality. Inside the SYSTEM
        /// container it cannot be: GUID_CONTAINER_ID_SYSTEM is not "unknown",
        /// it means "built in and non-removable", and EVERY built-in device
        /// shares the one value, so a container-equality walk would climb to
        /// the ACPI root and blacklist half the machine. The USB composite VID
        /// and PID token bounds it instead, and stops at the hub.</para></summary>
        private static bool SameDeviceScope(uint devInst, bool systemScoped, Guid containerId, string vidPid)
        {
            string id = GetInstanceId(devInst);

            // Backstop, not a live fix, and the first comment here claimed
            // otherwise. A real pad's parents are its USB composite and its
            // hub, never a ROOT node, so a walk that STARTS on a real pad
            // cannot reach one of ours. Reaching one needs the walk to start
            // there, which needs a UserDevice row with hiding switched on, and
            // Step1's self-virtual guard hits `continue` before
            // FindOrCreateUserDevice, so no such row is ever built. Nothing
            // shows our own pads in the Devices list, so nothing can mark one.
            //
            // It stays because the layer that prevents it is documented as
            // having failed. That guard's own comment records a driver upgrade
            // recreating the virtual devnodes with fresh instance paths and
            // slipping past both the fork enumeration filter and the cloak. If
            // that recurs, this keeps the mistake to one node instead of the
            // ROOT parent and every sibling under it, because HIDMaestro roots
            // its nodes at ROOT\VID_xxxx&PID_yyyy&IG_nn and the child repeats
            // the token the parent carries.
            if (MatchesHidMaestroPattern(id)) return false;

            if (!systemScoped) return GetContainerId(devInst) == containerId;
            string token = VidPidToken(id);
            return token != null && string.Equals(token, vidPid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>One parent hop, refusing a devnode that names itself as its
        /// own parent. cfgmgr32 does not promise an acyclic walk, and none of
        /// the three token walks caps depth, so the guard IsHidMaestroDevice
        /// already carries belongs on them too.</summary>
        private static bool TryParent(uint node, out uint parent)
        {
            parent = 0;
            if (CM_Get_Parent(out uint p, node, 0) != CR_SUCCESS) return false;
            if (p == 0 || p == node) return false;
            parent = p;
            return true;
        }

        /// <summary>Deeper than any real PnP chain, so a cycle cfgmgr32 hands
        /// back ends the walk instead of spinning it.</summary>
        private const int MaxDevnodeDepth = 64;

        /// <summary>Wider than any real composite. The sibling ring is the one
        /// walk cfgmgr32 gives no natural end to.</summary>
        private const int MaxDevnodeSiblings = 256;

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

            // Every fallback below degrades to the node we were handed, and
            // that node still follows the keep-out set (#400). Returning it raw
            // skipped the one contract the compose step was moved here to own,
            // so a phantom or container-less node on a row the user left visible
            // got blacklisted anyway and never reached the kept list.
            List<string> PrimaryOnly() =>
                ComposeBlacklist(hidInstanceId, null, default, null, keepOut, keptOut);

            var seam = TreeSeam;
            if (seam != null)
            {
                return seam(hidInstanceId) is { } tree
                    ? ComposeBlacklist(hidInstanceId, tree.chain, tree.baseContainer, tree.children, keepOut, keptOut)
                    : PrimaryOnly();
            }

            if (CM_Locate_DevNodeW(out uint hidDevInst, hidInstanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
                return PrimaryOnly();

            Guid hidContainerId = GetContainerId(hidDevInst);

            // A SYSTEM container is not "cannot tell". It says the device is
            // built in and non-removable, which is exactly the handheld case
            // this rule was written for. Bailing here made a882ed19 chain
            // expansion inert on those pads: on this bench 21 of 33 HIDClass
            // devices carry GUID_CONTAINER_ID_SYSTEM, the internal composite
            // ones included, while a removable USB pad gets a real container,
            // which is why the bench line looked right. SameDeviceScope bounds
            // the walk by the USB composite instead, so it still cannot climb
            // past the composite parent to the ACPI root.
            bool systemScoped = hidContainerId == GUID_CONTAINER_ID_SYSTEM;
            string scopeToken = systemScoped ? VidPidToken(hidInstanceId) : null;
            if (hidContainerId == Guid.Empty || (systemScoped && scopeToken == null))
                return PrimaryOnly();

            // Walk parents while the node stays inside the same physical
            // device, recording each intermediate node. The last one that
            // matches is the base container.
            var chain = new List<PnpNode>();
            uint baseContainer = hidDevInst;
            try
            {
                uint current = hidDevInst;
                for (int depth = 0; depth < MaxDevnodeDepth && TryParent(current, out uint parent); depth++)
                {
                    if (!SameDeviceScope(parent, systemScoped, hidContainerId, scopeToken)) break;
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
                    for (int n = 0; n < MaxDevnodeSiblings; n++)
                    {
                        children.Add(new PnpNode(GetInstanceId(child), GetClassGuid(child)));
                        if (CM_Get_Sibling(out uint sibling, child, 0) != CR_SUCCESS) break;
                        if (sibling == child) break;
                        child = sibling;
                    }
                }

                return ComposeBlacklist(hidInstanceId, chain, baseNode, children, keepOut, keptOut);
            }
            catch
            {
                // Same contract as ContainerKeyOf and ChainInstanceIds, which
                // both already catch here: cfgmgr32 trouble degrades to the
                // node we were handed rather than escaping into the apply loop.
                return PrimaryOnly();
            }
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
            keepOut ??= _ => false;

            // The primary node follows the keep-out set like every other node.
            // Three of the four apply-loop call sites test it before calling
            // and skip. The real-path expansion did not, so a node belonging
            // to a row the user left visible was blacklisted anyway and never
            // showed up in the kept list. The test lives here now rather than
            // at the call sites, so a fifth caller inherits it (#400).
            if (keepOut(hidInstanceId))
            {
                if (keptOut != null && !keptOut.Contains(hidInstanceId, StringComparer.OrdinalIgnoreCase))
                    keptOut.Add(hidInstanceId);
                return result;
            }

            result.Add(hidInstanceId);

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
                    // Count only children whose id actually read. A child whose
                    // GetInstanceId failed used to count toward hidChildren and
                    // then skip the keepOut test, so hidChildren could reach
                    // totalChildren while anyChildKeptOut stayed false. One
                    // transient cfgmgr32 failure then blocked the base container
                    // over a row the user had left visible, which is the #400
                    // regression this rule exists to prevent.
                    if (string.IsNullOrEmpty(child.InstanceId)) continue;
                    hidChildren++;
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

        /// <summary>A key identifying the PHYSICAL device a devnode belongs
        /// to, or null when the node is unknown or nothing can identify it.
        ///
        /// <para>Every interface and every HID collection of ONE physical
        /// device shares the key, and two pads of the same model have two
        /// different ones. That is the fact the sibling-sweep gate needs and
        /// could not get from a VID/PID row count: on a composite handheld
        /// the gamepad, touchpad and keyboard are three PadForge rows of one
        /// device, and counting rows read that as three pads (#397).</para>
        ///
        /// <para>For a removable device the key is its ContainerId. A
        /// built-in device reports GUID_CONTAINER_ID_SYSTEM, which every
        /// built-in device on the machine shares, so the key is its USB
        /// composite parent instead.</para></summary>
        internal static string ContainerKeyOf(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            try
            {
                if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
                    return null;
                Guid containerId = GetContainerId(devInst);
                if (containerId == GUID_CONTAINER_ID_SYSTEM)
                {
                    // Every built-in device shares this one value, so it can
                    // never tell two pads apart, and returning null here sent
                    // the gate back to the row count that ffda86b5 replaced:
                    // a composite handheld three rows read as three pads and
                    // the sweep shut, which is the empty dev-list from #397.
                    // The USB composite parent IS a discriminator. Two
                    // interfaces of one pad walk to the same node. Two pads do
                    // not. Null still means "cannot tell" when there is no
                    // composite to reach.
                    string token = VidPidToken(instanceId);
                    if (token == null) return null;
                    uint node = devInst;
                    string compositeId = null;
                    for (int depth = 0; depth < MaxDevnodeDepth && TryParent(node, out uint parent); depth++)
                    {
                        if (!SameDeviceScope(parent, true, containerId, token)) break;
                        compositeId = GetInstanceId(parent);
                        node = parent;
                    }
                    return string.IsNullOrEmpty(compositeId) ? null : compositeId;
                }
                if (containerId == Guid.Empty) return null;
                return containerId.ToString("D");
            }
            catch
            {
                // cfgmgr32 trouble reads as "cannot tell", and the caller
                // falls back to the old rule rather than guessing.
                return null;
            }
        }

        /// <summary>A node and its same-container ancestors, the HID node
        /// first and the base container last: what a device row with
        /// hiding OFF contributes to the keep-out set (#400). Phantom
        /// nodes resolve too, matching the expansion's own locate.</summary>
        internal static List<string> ChainInstanceIds(string hidInstanceId)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(hidInstanceId)) return result;
            var seam = ChainSeam;
            if (seam != null) return seam(hidInstanceId) ?? new List<string> { hidInstanceId };
            result.Add(hidInstanceId);
            try
            {
                if (CM_Locate_DevNodeW(out uint devInst, hidInstanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
                    return result;
                Guid containerId = GetContainerId(devInst);
                // Same scope rule as the expansion: a built-in device is
                // bounded by its USB composite, never by the system container.
                bool systemScoped = containerId == GUID_CONTAINER_ID_SYSTEM;
                string scopeToken = systemScoped ? VidPidToken(hidInstanceId) : null;
                if (containerId == Guid.Empty || (systemScoped && scopeToken == null))
                    return result;
                uint current = devInst;
                for (int depth = 0; depth < MaxDevnodeDepth && TryParent(current, out uint parent); depth++)
                {
                    if (!SameDeviceScope(parent, systemScoped, containerId, scopeToken)) break;
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

        // ─────────────────────────────────────────────
        //  Releasing a controller opened before its hide
        // ─────────────────────────────────────────────

        // DEVPKEY_Device_LastArrivalDate {83da6326-97a6-4088-9453-a1923f573b29}, 102 (devpkey.h)
        private static readonly DEVPROPKEY DEVPKEY_Device_LastArrivalDate =
            new() { fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"), pid = 102 };
        private const uint DEVPROP_TYPE_FILETIME = 0x00000010;

        /// <summary>One devnode on the walk from a hidden node up to its USB
        /// hub, as the release decision sees it.</summary>
        internal readonly record struct ReleaseHop(string InstanceId, Guid ClassGuid, Guid ContainerId, string Service);

        /// <summary>How recently a controller may have connected and still be
        /// left alone. A hide that lands this soon after the device arrived
        /// came from the arrival itself, before any program could open it, and
        /// cycling it would only make it connect twice.</summary>
        internal static readonly TimeSpan ReleaseArrivalGrace = TimeSpan.FromSeconds(5);

        /// <summary>The USB device whose hub port PadForge cycles so that a
        /// program which opened a controller before it was hidden loses it, or
        /// null with the reason (#484 follow-up).
        ///
        /// <para>HidHide decides access only when a handle is opened (Logic.c
        /// OnDeviceFileCreate), so a handle opened before the hide keeps
        /// working. Windows will not remove a device while any handle stays
        /// open, its own documentation says so, and PadForge's SDL holds one
        /// on every controller it reads, so disabling the device is refused.
        /// A hub port cycle is a surprise removal that no handle can veto,
        /// which is how HandheldCompanion releases a hidden USB pad
        /// (IController.CyclePort, through Nefarius UsbPnPDevice.CyclePort).
        /// The device then comes back through HidHide, where only whitelisted
        /// programs can open it.</para>
        ///
        /// <para>The chain runs from the hidden node up to and including the
        /// first node whose service is a USB hub. Bluetooth is never cycled:
        /// that would mean dropping the link, and a controller turns itself
        /// off when its link drops. The USB device must be the controller
        /// itself (same container, or inside the SYSTEM container the same
        /// VID and PID, as <see cref="SameDeviceScope"/> judges it), must
        /// carry no second controller (a receiver would drop every pad on
        /// it), and must have been connected longer than
        /// <see cref="ReleaseArrivalGrace"/> when its ids were handed to the
        /// release (<paramref name="sinceArrival"/>).</para>
        ///
        /// <para>The controllers a USB device carries are the larger of two
        /// counts: its XUSB and XboxComposite nodes
        /// (<paramref name="controllerFunctions"/>), and the controller rows
        /// PadForge shows on it (<paramref name="controllerRowsOn"/>, given
        /// the USB device's instance id). The second count is what finds a
        /// receiver with no Xbox node, such as a Steam Controller dongle,
        /// whose pads are HID interfaces of the one USB device
        /// (SDL_hidapi_steam.c). On a node the engine has no row online for,
        /// it takes the rows that remember the node while Windows lists it
        /// as present (InputService.CountControllerRowsPerUsbDevice).</para></summary>
        internal static string PickUsbDeviceToCycle(IReadOnlyList<ReleaseHop> chain, int controllerFunctions,
            TimeSpan? sinceArrival, out string reason)
            => PickUsbDeviceToCycle(chain, controllerFunctions, null, sinceArrival, out reason);

        /// <summary>The decision above, with PadForge's controller rows per
        /// USB device counted as well. Null counts no rows.</summary>
        internal static string PickUsbDeviceToCycle(IReadOnlyList<ReleaseHop> chain, int controllerFunctions,
            Func<string, int> controllerRowsOn, TimeSpan? sinceArrival, out string reason)
        {
            reason = null;
            if (chain == null || chain.Count == 0) { reason = "not present"; return null; }
            int hub = -1;
            for (int i = 0; i < chain.Count; i++)
            {
                string id = chain[i].InstanceId ?? string.Empty;
                if (id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith(@"BTHLEDEVICE\", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith(@"BTHPS3BUS\", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith(@"BTH\", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "Bluetooth";
                    return null;
                }
                if (i > 0 && chain[i].Service != null
                    && chain[i].Service.StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase))
                {
                    hub = i;
                    break;
                }
            }
            if (hub < 1) { reason = "not on a USB port"; return null; }
            var top = chain[hub - 1];
            if (top.InstanceId == null || !top.InstanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
            {
                reason = "not on a USB port";
                return null;
            }
            var target = chain[0];
            bool systemScoped = target.ContainerId == Guid.Empty || target.ContainerId == GUID_CONTAINER_ID_SYSTEM;
            bool sameDevice = systemScoped
                ? VidPidToken(target.InstanceId) is string token
                    && string.Equals(token, VidPidToken(top.InstanceId), StringComparison.OrdinalIgnoreCase)
                : top.ContainerId == target.ContainerId;
            if (!sameDevice) { reason = "the USB device is not the controller"; return null; }
            int controllers = Math.Max(controllerFunctions, controllerRowsOn?.Invoke(top.InstanceId) ?? 0);
            if (controllers > 1) { reason = "the USB device carries more than one controller"; return null; }
            if (sinceArrival is TimeSpan age && age < ReleaseArrivalGrace) { reason = "connected moments ago"; return null; }
            return top.InstanceId;
        }

        /// <summary>Test seam for the walk behind
        /// <see cref="FindUsbDeviceToCycle"/>: a hidden instance id to its
        /// chain up to the hub, the count of XUSB and XboxComposite nodes on
        /// the USB device, and when that device last arrived, or null for a
        /// node that is not present. Null in production, where cfgmgr32
        /// walks the device tree.</summary>
        internal static Func<string, (IReadOnlyList<ReleaseHop> chain, int controllerFunctions, DateTime? arrivedUtc)?> ReleaseWalkSeam;

        /// <summary>The live walk behind <see cref="PickUsbDeviceToCycle"/>
        /// for one hidden instance id. <paramref name="controllerRowsOn"/>
        /// counts PadForge's controller rows on a USB device, or is null.
        /// <paramref name="handedOverUtc"/> is when the id was handed to the
        /// release, which is what the arrival grace is measured against
        /// (<see cref="ArrivalAge"/>).</summary>
        internal static string FindUsbDeviceToCycle(string hiddenInstanceId, Func<string, int> controllerRowsOn,
            DateTime handedOverUtc, out string reason)
        {
            reason = "not present";
            if (string.IsNullOrEmpty(hiddenInstanceId)) return null;
            try
            {
                var seam = ReleaseWalkSeam;
                var walk = seam != null ? seam(hiddenInstanceId) : WalkToUsbHub(hiddenInstanceId);
                if (walk is not { } found) return null;
                return PickUsbDeviceToCycle(found.chain, found.controllerFunctions, controllerRowsOn,
                    ArrivalAge(found.arrivedUtc, handedOverUtc), out reason);
            }
            catch (Exception ex)
            {
                reason = "the device tree could not be read (" + ex.GetType().Name + ")";
                return null;
            }
        }

        private static (IReadOnlyList<ReleaseHop> chain, int controllerFunctions, DateTime? arrivedUtc)? WalkToUsbHub(string hiddenInstanceId)
        {
            if (CM_Locate_DevNodeW(out uint node, hiddenInstanceId, 0) != CR_SUCCESS) return null;
            var chain = new List<ReleaseHop>();
            var nodes = new List<uint>();
            uint cur = node;
            for (int depth = 0; depth < MaxDevnodeDepth; depth++)
            {
                var hop = new ReleaseHop(GetInstanceId(cur), GetClassGuid(cur), GetContainerId(cur), GetDevNodeService(cur));
                chain.Add(hop);
                nodes.Add(cur);
                if (depth > 0 && hop.Service != null
                    && hop.Service.StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase)) break;
                if (!TryParent(cur, out uint parent)) break;
                cur = parent;
            }
            int top = chain.FindIndex(1, h => h.Service != null
                && h.Service.StartsWith("USBHUB", StringComparison.OrdinalIgnoreCase)) - 1;
            int functions = top >= 0 ? CountControllerFunctions(nodes[top]) : 0;
            DateTime? arrived = top >= 0 ? LastArrival(nodes[top]) : null;
            return (chain, functions, arrived);
        }

        /// <summary>How long a device had been connected at a given moment,
        /// or null when its arrival date does not read. Negative for a
        /// device that arrived after that moment, which the grace leaves
        /// alone like one that arrived just before it: it came back under
        /// its hide.</summary>
        internal static TimeSpan? ArrivalAge(DateTime? arrivedUtc, DateTime asOfUtc)
            => arrivedUtc is DateTime arrived ? asOfUtc - arrived : null;

        /// <summary>The XUSB and XboxComposite nodes in a USB device's subtree,
        /// itself included. Each is one game controller, so more than one means
        /// a receiver. A receiver whose pads are HID interfaces has no such
        /// node, and PadForge's own controller rows count those
        /// (<see cref="PickUsbDeviceToCycle(IReadOnlyList{ReleaseHop}, int, Func{string, int}, TimeSpan?, out string)"/>).</summary>
        private static int CountControllerFunctions(uint usbDevice)
        {
            int count = 0;
            var pending = new Stack<(uint node, int depth)>();
            pending.Push((usbDevice, 0));
            while (pending.Count > 0)
            {
                var (n, depth) = pending.Pop();
                Guid cls = GetClassGuid(n);
                if (cls == GUID_DEVCLASS_XUSBCLASS || cls == GUID_DEVCLASS_XBOXCOMPOSITE) count++;
                if (depth >= MaxDevnodeDepth) continue;
                if (CM_Get_Child(out uint child, n, 0) != CR_SUCCESS) continue;
                for (int guard = 0; guard < 256; guard++)
                {
                    pending.Push((child, depth + 1));
                    if (CM_Get_Sibling(out uint next, child, 0) != CR_SUCCESS || next == child) break;
                    child = next;
                }
            }
            return count;
        }

        /// <summary>When the node last arrived, UTC
        /// (DEVPKEY_Device_LastArrivalDate), or null when the date does not
        /// read.</summary>
        private static DateTime? LastArrival(uint devInst)
        {
            byte[] buf = new byte[8];
            uint size = (uint)buf.Length;
            int rc = CM_Get_DevNode_PropertyW(devInst, DEVPKEY_Device_LastArrivalDate,
                out uint type, buf, ref size, 0);
            if (rc != CR_SUCCESS || type != DEVPROP_TYPE_FILETIME || size != 8) return null;
            long fileTime = BitConverter.ToInt64(buf, 0);
            if (fileTime <= 0) return null;
            return DateTime.FromFileTimeUtc(fileTime);
        }

        /// <summary>Test seam for <see cref="LastArrivalUtc"/>. Null in
        /// production, where cfgmgr32 answers.</summary>
        internal static Func<string, DateTime?> ArrivalSeam;

        /// <summary>When the device with this instance id last arrived, UTC,
        /// or null when it is not present or the date does not read. Every
        /// node carries its own date, and a HID collection's moves with its
        /// USB device's return. The self-check uses it to tell a controller
        /// that is back on a new device stack from one still on the stack it
        /// had before its port was cycled.</summary>
        internal static DateTime? LastArrivalUtc(string instanceId)
        {
            var seam = ArrivalSeam;
            if (seam != null) return seam(instanceId);
            if (string.IsNullOrEmpty(instanceId)) return null;
            try
            {
                if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != CR_SUCCESS) return null;
                return LastArrival(node);
            }
            catch
            {
                return null;
            }
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
            var seam = PresenceSeam;
            if (seam != null) return seam(instanceId);
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

        /// <summary>The first present HID interface path of a devnode, or
        /// null when it has none (#484).</summary>
        internal static string HidInterfaceOf(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            var seam = InterfaceSeam;
            if (seam != null) return seam(instanceId);
            try
            {
                var guid = GUID_DEVINTERFACE_HID;
                if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, instanceId,
                        CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || len <= 1)
                    return null;
                var buffer = new char[len];
                if (CM_Get_Device_Interface_ListW(ref guid, instanceId, buffer, len,
                        CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
                    return null;
                int nul = Array.IndexOf(buffer, '\0');
                return nul <= 0 ? null : new string(buffer, 0, nul);
            }
            catch
            {
                return null;
            }
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
            ThrowIfOffLimits("Opening HidHide's control device");
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
        /// zero length, which is how the size probe is expressed. Every
        /// read and every write of HidHide's lists and flags goes through
        /// here, so in a test host with no seam none of them reaches the
        /// driver (<see cref="SeamsOnlyForTest"/>).</summary>
        private static bool TryIo(uint ioctl, byte[] inBuffer, byte[] outBuffer, out int bytesReturned)
        {
            // One call at a time, the seam included, so a test sees the same
            // order the driver would (_deviceGate).
            lock (_deviceGate)
            {
                var seam = IoSeam;
                if (seam != null)
                {
                    var r = seam(ioctl, inBuffer, outBuffer);
                    bytesReturned = r.bytes;
                    return r.ok;
                }

                bytesReturned = 0;
                // No seam, in a test host: the call fails as it does when
                // the control device does not open, and nothing is sent.
                if (SeamsOnlyForTest)
                {
                    OffLimitsRefused?.Invoke($"ioctl 0x{ioctl:X8}");
                    return false;
                }
                using var handle = OpenDevice();
                if (handle == null || handle.IsInvalid) return false;
                ThrowIfOffLimits("An IOCTL to HidHide's control device");
                return DeviceIoControl(handle, ioctl, inBuffer, inBuffer?.Length ?? 0,
                    outBuffer, outBuffer?.Length ?? 0, out bytesReturned, IntPtr.Zero);
            }
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

            // STOP AT THE MULTI-STRING TERMINATOR, because the driver reports
            // MORE bytes than it wrote. HidHide's Config.c
            // HidHideCollectionToMultiString accumulates
            // "neededSizeInCharacters += string.Length" over a UNICODE_STRING,
            // whose Length is in BYTES, so for N paths totaling C characters it
            // reports (2C + N + 1) characters while writing (C + N + 1). The
            // completion at Logic.c OnControlDeviceIoGetBlacklist hands that
            // inflated count back as Information, and the I/O manager copies it
            // out of the METHOD_BUFFERED system buffer, so the last 2C bytes are
            // pool the driver never wrote. Reading to the terminator ignores
            // them. HidHide's own client survives this only because it
            // over-allocates a zero-initialized vector.
            //
            // An empty list is a single terminator, so the first entry is empty
            // and the loop adds nothing, which is the correct empty result.
            foreach (string entry in fullString.Split('\0'))
            {
                if (entry.Length == 0) break;
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

        /// <summary>This process's image path in the form the driver compares
        /// (#484): the native path of the image file the kernel loaded.
        /// HidHide records a process by the name its first load-image
        /// notification carries (Logic.c OnSystemLoadImage) and matches that
        /// name against the list (Config.c
        /// HidHideProcessIdCheckFullImageNameAgainstWhitelist). The
        /// drive-letter conversion of the module path names a different path
        /// for the same file when the folder sits on a volume mounted inside
        /// another drive, on a SUBST drive, or behind a junction. Null when
        /// Windows does not answer.</summary>
        public static string CurrentProcessNativeImagePath()
        {
            try
            {
                var buffer = new char[32768];
                uint size = (uint)buffer.Length;
                if (!QueryFullProcessImageNameW(GetCurrentProcess(), PROCESS_NAME_NATIVE, buffer, ref size))
                    return null;
                return size == 0 ? null : new string(buffer, 0, (int)size);
            }
            catch
            {
                return null;
            }
        }

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
