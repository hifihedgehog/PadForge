using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Nefarius.Utilities.DeviceManagement.Extensions;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace PadForge.Services
{
    /// <summary>
    /// Installs and arms the Nefarius BthPS3 (L2CAP profile) + BthPS3PSM (BTHUSB lower
    /// class filter) drivers so a DualShock 3 can connect over the shared radio, and
    /// binds the docked pad to WinUSB so its magic sixpair report can be sent. Same
    /// eight-step, reboot-free sequence BthPS3's own MSI performs, but driven from the
    /// always-elevated app with the drivers embedded (no MSI, no DsHidMini). Every
    /// step is grounded in BthPS3's installer (BthPS3Installer/CustomActions.cs) and
    /// the drivers' own INFs.
    ///
    /// The critical detail vs. a naive install: the radio is re-enumerated with
    /// UsbPnPDevice.CyclePort() (IOCTL_USB_HUB_CYCLE_PORT), NOT Disable/Enable, which
    /// would leave the devnode flagged CM_PROB_NEED_RESTART (pending reboot). The
    /// profile driver ships RawPDO=0/ExclusivePDO=1; the DsHidMini-less raw reader
    /// needs RawPDO=1 (enumerate with no function driver) and ExclusivePDO=0 (shared
    /// open), so those are rewritten.
    /// </summary>
    internal static class Ds3DriverInstaller
    {
        // Bluetooth device setup class (BthPS3PSM registers as its lower filter).
        private static readonly Guid BluetoothClass = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
        // Radio device-interface GUID (robust radio locate, HostRadio.cs:134).
        private static readonly Guid RadioInterface = new Guid("92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2");
        // BTHPS3_SERVICE_GUID + name (BthPS3.h:59-60,81) - advertising this spawns the profile PDO.
        private static readonly Guid BthPs3ServiceGuid = new Guid("1cb831ea-79cd-4508-b0fc-85f7c85ae8e0");
        private const string BthPs3ServiceName = "BthPS3Service";

        private const string BthPs3ParamsKey =
            @"SYSTEM\CurrentControlSet\Services\BthPS3\Parameters";

        // IOCTL_BTHPS3PSM_ENABLE_PSM_PATCHING (BthPS3.h:400) + control device path.
        private const uint IOCTL_BTHPS3PSM_ENABLE_PSM_PATCHING = 0x2AAC04;
        private const string PsmControlPath = @"\\.\BthPS3PSMControl";

        // ── public entry points used by Ds3PairingService ────────────────────────

        /// <summary>Installs + arms the BthPS3 stack if it isn't already present.
        /// Idempotent: when the service already runs it only reconciles the two
        /// consumer registry values. Returns true when the stack is operable.</summary>
        public static bool EnsureInstalled(Action<string> log)
        {
            try
            {
                if (IsServiceInstalled("BthPS3"))
                {
                    EnsureConsumerParams();       // keep RawPDO=1/ExclusivePDO=0 if a prior install set them wrong
                    EnsurePsmPatch(log);
                    return true;
                }

                log("Installing PlayStation Bluetooth drivers (one time)...");
                string dir = ExtractDrivers();

                // 1. filter INF (driver store + BthPS3PSM kernel service)
                InstallInf(Path.Combine(dir, "BthPS3PSM_x64", "BthPS3PSM.inf"), log);
                // 2. register it as the Bluetooth-class lower filter
                DeviceClassFilters.AddLower(BluetoothClass, "BthPS3PSM");
                log("Registered PSM filter.");
                // 3. reboot-free radio re-enumeration so the filter attaches
                CycleBluetoothRadio(log);
                // 4/5. profile driver + raw-PDO placeholder into the store
                InstallInf(Path.Combine(dir, "BthPS3_x64", "BthPS3.inf"), log);
                InstallInf(Path.Combine(dir, "BthPS3_x64", "BthPS3_PDO_NULL_Device.inf"), log);
                // 6. consumer registry (raw, shared)
                EnsureConsumerParams();
                // 7. advertise the profile service -> spawns the PDO, loads BthPS3.sys
                EnableBthPs3Service(log);
                // 8. arm the PSM patch (belt-and-suspenders; AutoEnableFilter also does it)
                EnsurePsmPatch(log);

                bool ok = IsServiceInstalled("BthPS3");
                log(ok ? "Bluetooth drivers installed." : "Driver install did not register the service.");
                return ok;
            }
            catch (Exception ex) { log("Driver install failed: " + ex.Message); return false; }
        }

        /// <summary>Binds the docked DS3 to inbox WinUSB so its magic reports can be
        /// sent. No-op if the WinUSB interface is already present.</summary>
        public static bool EnsureWinUsbBound(Action<string> log, CancellationToken ct)
        {
            try
            {
                if (Devcon.FindByInterfaceGuid(new Guid("B35924D6-3E16-4A9E-9782-5524A4B79BAC"), out _))
                    return true;   // already bound

                string dir = ExtractDrivers();
                log("Preparing the controller over USB...");
                InstallInf(Path.Combine(dir, "WinUSB", "ds3_winusb.inf"), log);

                // The bind takes a moment to re-enumerate the USB node.
                for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
                {
                    if (Devcon.FindByInterfaceGuid(new Guid("B35924D6-3E16-4A9E-9782-5524A4B79BAC"), out _))
                        return true;
                    Thread.Sleep(250);
                }
                return Devcon.FindByInterfaceGuid(new Guid("B35924D6-3E16-4A9E-9782-5524A4B79BAC"), out _);
            }
            catch (Exception ex) { log("WinUSB bind failed: " + ex.Message); return false; }
        }

        /// <summary>Reboot-free radio re-enumeration (IOCTL_USB_HUB_CYCLE_PORT).</summary>
        public static void CycleBluetoothRadio(Action<string> log)
        {
            try
            {
                if (!Devcon.FindByInterfaceGuid(RadioInterface, out PnPDevice radio))
                {
                    log("No USB Bluetooth radio to cycle.");
                    return;
                }
                radio.ToUsbPnPDevice().CyclePort();
                log("Bluetooth radio re-enumerated.");
            }
            catch (Exception ex) { log("Radio cycle failed: " + ex.Message); }
        }

        // GUID_DEVINTERFACE_BTHPS3 {968E1849} - the raw PDO's IOCTL interface, present
        // only while a DS3 is connected. Used to find the node for removal.
        private static readonly Guid BthPs3Interface = new Guid("968E1849-73B1-4876-B80A-ED6DD171489B");

        /// <summary>Removes the BthPS3 DS3 child node if present (for a clean unpair /
        /// dry run). Best-effort: the node is transient (only up while connected), so a
        /// radio cycle re-enumerates it anyway.</summary>
        public static void RemoveDs3Node(Action<string> log)
        {
            try
            {
                for (int guard = 0; guard < 8; guard++)
                {
                    if (!Devcon.FindByInterfaceGuid(BthPs3Interface, out PnPDevice dev)) break;
                    string id = dev.InstanceId;
                    dev.Remove();
                    log("Removed device node " + id);
                }
            }
            catch (Exception ex) { log("Node removal: " + ex.Message); }
        }

        // ── BR/EDR link-key anchor (remembered-device persistence) ────────────────

        // Fixed non-zero 16-byte key. bthport only needs a link-key VALUE to exist for
        // the device MAC to flag it remembered+authenticated (BDIF_PAIRED); the DS3 does
        // no SSP so the value is never validated over the air. That remembered state is
        // what makes bthport serve the injected Name to BthPS3's IOCTL_BTH_GET_DEVICE_INFO
        // on every connect instead of overwriting it with the clone's blank over-air name,
        // and what keeps the Devices record from being pruned on radio re-enumeration.
        // Hardware-confirmed 2026-07-09 (rem/auth flags flipped, identified as SIXAXIS, no
        // encryption block). Constant is ScpToolkit's BdLink (GlobalConfiguration.cs).
        private static readonly byte[] Ds3LinkKey =
            { 0x56, 0xE8, 0x81, 0x38, 0x08, 0x06, 0x51, 0x41, 0xC0, 0x7F, 0x12, 0xAA, 0xD9, 0x66, 0x3C, 0xCE };

        private const string BthPortKeysKey =
            @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys\";

        /// <summary>Writes the link-key value that anchors the DS3's Name record in
        /// bthport's remembered-device set. Value name = device MAC (12 lowercase hex),
        /// under Keys\&lt;radiomac&gt;. The Keys subtree is SYSTEM-ACL'd, so it is opened
        /// with REG_OPTION_BACKUP_RESTORE after enabling the (held-but-disabled) backup
        /// and restore privileges of the elevated token.</summary>
        public static bool WriteLinkKeyAnchor(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)
        {
            IntPtr hk = OpenKeysBackupRestore(radioMacBigEndian, log);
            if (hk == IntPtr.Zero) return false;
            try
            {
                int rc = RegSetValueEx(hk, deviceMacHex, 0, REG_BINARY, Ds3LinkKey, Ds3LinkKey.Length);
                if (rc != 0) { log($"Writing the pairing key failed (rc={rc})."); return false; }
                return true;
            }
            finally { RegCloseKey(hk); }
        }

        /// <summary>Removes the link-key anchor for a clean unpair.</summary>
        public static void DeleteLinkKeyAnchor(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)
        {
            IntPtr hk = OpenKeysBackupRestore(radioMacBigEndian, log);
            if (hk == IntPtr.Zero) return;
            try { RegDeleteValue(hk, deviceMacHex); }
            finally { RegCloseKey(hk); }
        }

        private static IntPtr OpenKeysBackupRestore(byte[] radioMacBigEndian, Action<string> log)
        {
            EnablePrivilege("SeBackupPrivilege");
            EnablePrivilege("SeRestorePrivilege");
            var sb = new System.Text.StringBuilder(radioMacBigEndian.Length * 2);
            foreach (byte b in radioMacBigEndian) sb.Append(b.ToString("x2"));
            int rc = RegCreateKeyEx(HKLM, BthPortKeysKey + sb, 0, null, REG_OPTION_BACKUP_RESTORE,
                KEY_READ | KEY_WRITE, IntPtr.Zero, out IntPtr hk, out _);
            if (rc != 0) { log($"Opening the pairing-key store failed (rc={rc})."); return IntPtr.Zero; }
            return hk;
        }

        // ── install helpers ──────────────────────────────────────────────────────

        private static void InstallInf(string infPath, Action<string> log)
        {
            if (!File.Exists(infPath)) throw new FileNotFoundException("Bundled driver missing", infPath);
            Devcon.Install(infPath, out bool reboot);
            if (reboot) log("(a reboot was requested by " + Path.GetFileName(infPath) + ")");
        }

        private static void EnsureConsumerParams()
        {
            using var key = Registry.LocalMachine.CreateSubKey(BthPs3ParamsKey, writable: true);
            key?.SetValue("RawPDO", 1, RegistryValueKind.DWord);       // enumerate with no function driver
            key?.SetValue("ExclusivePDO", 0, RegistryValueKind.DWord); // allow our shared open
        }

        private static void EnsurePsmPatch(Action<string> log)
        {
            IntPtr h = CreateFile(PsmControlPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
            if (h == INVALID_HANDLE) { log("PSM control device not present (filter may still auto-arm)."); return; }
            try
            {
                byte[] deviceIndex = new byte[4]; // { ULONG DeviceIndex = 0 }
                DeviceIoControl(h, IOCTL_BTHPS3PSM_ENABLE_PSM_PATCHING, deviceIndex, deviceIndex.Length, null, 0, out _, IntPtr.Zero);
            }
            finally { CloseHandle(h); }
        }

        // ── native BluetoothSetLocalServiceInfo (the one Bluetooth-specific call) ──

        private static void EnableBthPs3Service(Action<string> log)
        {
            var fp = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
            IntPtr hFind = BluetoothFindFirstRadio(ref fp, out IntPtr hRadio);
            if (hFind == IntPtr.Zero) { log("No radio to advertise the service on."); return; }
            try
            {
                EnablePrivilege("SeLoadDriverPrivilege");
                var info = new BLUETOOTH_LOCAL_SERVICE_INFO { Enabled = 1, szName = BthPs3ServiceName };
                uint rc = BluetoothSetLocalServiceInfo(hRadio, ref BthPs3ServiceGuidLocal, 0, ref info);
                log(rc == 0 ? "Profile service advertised." : $"Advertise service rc={rc}.");
            }
            finally { CloseHandle(hRadio); BluetoothFindRadioClose(hFind); }
        }

        // ref needs a static field (can't ref a readonly through a property).
        private static Guid BthPs3ServiceGuidLocal = new Guid("1cb831ea-79cd-4508-b0fc-85f7c85ae8e0");

        private static void EnablePrivilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tok)) return;
            try
            {
                if (!LookupPrivilegeValue(null, name, out LUID luid)) return;
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(tok); }
        }

        // ── embedded driver extraction ─────────────────────────────────────────────

        private static string _extractedDir;
        private static string ExtractDrivers()
        {
            if (_extractedDir != null && Directory.Exists(_extractedDir)) return _extractedDir;
            string root = Path.Combine(Path.GetTempPath(), "PadForge", "BthPS3Drivers");
            var asm = Assembly.GetExecutingAssembly();
            foreach (string res in asm.GetManifestResourceNames().Where(n => n.StartsWith("BthPS3.", StringComparison.Ordinal)))
            {
                // LogicalName "BthPS3.BthPS3PSM_x64/BthPS3PSM.inf" -> path under root
                string rel = res.Substring("BthPS3.".Length).Replace('/', Path.DirectorySeparatorChar);
                string dest = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                using Stream s = asm.GetManifestResourceStream(res);
                using FileStream fs = File.Create(dest);
                s.CopyTo(fs);
            }
            _extractedDir = root;
            return root;
        }

        // ── service helpers ───────────────────────────────────────────────────────

        /// <summary>True once the driver's service key exists (i.e. the INF installed).
        /// A registry probe avoids a dependency on System.ServiceProcess and is the
        /// right "is it installed" question - running state is handled by the profile
        /// service advertisement + AutoEnableFilter.</summary>
        private static bool IsServiceInstalled(string name)
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            return k != null;
        }

        // ── P/Invoke ──────────────────────────────────────────────────────────────

        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
        private static readonly UIntPtr HKLM = unchecked((UIntPtr)0x80000002u);
        private const int REG_OPTION_BACKUP_RESTORE = 0x04, REG_BINARY = 3, KEY_READ = 0x20019, KEY_WRITE = 0x20006;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegCreateKeyEx(UIntPtr hKey, string subKey, int reserved, string cls, int options, int sam, IntPtr sa, out IntPtr res, out int disp);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegSetValueEx(IntPtr hKey, string name, int reserved, int type, byte[] data, int cb);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegDeleteValue(IntPtr hKey, string name);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern int RegCloseKey(IntPtr hKey);

        [StructLayout(LayoutKind.Sequential)] private struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_LOCAL_SERVICE_INFO
        {
            public int Enabled;
            public ulong btAddr;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szDeviceClass;
        }

        [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

        [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr phRadio);
        [DllImport("bthprops.cpl", SetLastError = true)] private static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("bthprops.cpl", SetLastError = true)] private static extern uint BluetoothSetLocalServiceInfo(IntPtr hRadio, ref Guid pClassGuid, uint ulInstance, ref BLUETOOTH_LOCAL_SERVICE_INFO info);

        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string sys, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr prevLen);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inb, int inl, byte[] outb, int outl, out int ret, IntPtr ov);
    }
}
