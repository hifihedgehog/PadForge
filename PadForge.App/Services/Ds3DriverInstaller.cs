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
        // BTHPS3_SERVICE_GUID name (BthPS3.h:81) - advertising this service (via the
        // by-ref BthPs3ServiceGuidLocal below) spawns the profile PDO.
        private const string BthPs3ServiceName = "BthPS3Service";

        private const string BthPs3ParamsKey =
            @"SYSTEM\CurrentControlSet\Services\BthPS3\Parameters";

        // PSM filter control IOCTLs (BthPS3.h:400-405). Both take a 4-byte
        // { ULONG DeviceIndex } input; DeviceIndex is the plain index into the
        // filter's per-radio collection. A bad index completes with
        // STATUS_NO_SUCH_DEVICE (Sideband.c:317), surfaced as
        // ERROR_NO_SUCH_DEVICE, which ends the multi-radio sweep.
        private const uint IOCTL_BTHPS3PSM_ENABLE_PSM_PATCHING = 0x2AAC04;
        private const uint IOCTL_BTHPS3PSM_DISABLE_PSM_PATCHING = 0x2AAC08;
        private const int ERROR_NO_SUCH_DEVICE = 433;
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

        // Two radio cycles overlapping is a path into the BthPS3 freed-context BSOD
        // (0xD1, 2026-07-09). Ds3PairingService._radioGate serializes the pair/unpair
        // SEQUENCES; this lock serializes the cycle PRIMITIVE itself, so a caller
        // outside the gate (e.g. the one-time driver install) can't overlap a gated
        // cycle either.
        private static readonly object _cycleLock = new object();

        // GUID_DEVCLASS_USB: the setup class every USB function device enumerates under.
        private static readonly Guid UsbDeviceClass = new Guid("36FC9E60-C465-11CF-8056-444553540000");

        /// <summary>True only when a USB DualShock 3 (VID_054C&amp;PID_0268) is present AND
        /// still on the inbox HID driver (or no driver), meaning nothing is driving it and
        /// it needs our WinUSB bind. This is the ALLOWLIST that keeps the background bind
        /// safe: it fires only for the one state where binding is both needed and harmless.
        ///
        /// <para>Whether an existing WinUSB binding is OURS is not decided here. The caller
        /// (<see cref="Common.Input.Ds3DirectService"/>) first calls FindWinUsbDs3, which
        /// matches our own INF's interface GUID {B35924D6-...}; if that hits, the pad is
        /// opened directly with no rebind. Ownership is a PERSISTED devnode binding
        /// (DEVPKEY_Device_Service), so it survives our process dying or never running.
        /// An abrupt close therefore leaves the pad on WinUSB and the next run just reopens
        /// it. This method is reached only when our interface is ABSENT, and it returns
        /// false for every non-inbox state, so anything else driving the pad (DsHidMini,
        /// whose UMDF2 service reads WUDFRd; ScpToolkit; a stray WinUSB binding with a
        /// different GUID) is left strictly alone. The explicit pairing dialog is the only
        /// path that force-rebinds (via <see cref="EnsureWinUsbBound"/>).</para></summary>
        public static bool IsUsbDs3NeedingWinUsb()
        {
            try
            {
                if (!Devcon.FindInDeviceClassByHardwareId(UsbDeviceClass, @"USB\VID_054C&PID_0268", out var ids))
                    return false;
                foreach (var id in ids)
                {
                    try
                    {
                        var dev = PnPDevice.GetDeviceByInstanceId(id, DeviceLocationFlags.Normal);
                        string svc = dev.GetProperty<string>(DevicePropertyKey.Device_Service) ?? string.Empty;
                        // Allowlist: bind ONLY on the inbox HID driver or no driver. Every
                        // other service (WINUSB, WUDFRd/DsHidMini, ...) is left alone.
                        if (svc.Length == 0 || svc.Equals("HidUsb", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>Reboot-free radio re-enumeration (IOCTL_USB_HUB_CYCLE_PORT).</summary>
        public static void CycleBluetoothRadio(Action<string> log)
        {
            try
            {
                lock (_cycleLock)
                {
                    if (!Devcon.FindByInterfaceGuid(RadioInterface, out PnPDevice radio))
                    {
                        log("No USB Bluetooth radio to cycle.");
                        return;
                    }
                    radio.ToUsbPnPDevice().CyclePort();
                }
                log("Bluetooth radio re-enumerated.");
            }
            catch (Exception ex) { log("Radio cycle failed: " + ex.Message); }
        }

        // NOTE: there is deliberately no "remove the BthPS3 PDO node" helper. Forcibly
        // removing the raw PDO with PnP (dev.Remove()) frees BthPS3's per-connection
        // context out from under BTHport, and the next HCI disconnect faults on it
        // (BSOD 0xD1, BthPS3.sys, 2026-07-09). The PDO is transient: it self-destroys
        // when the pad disconnects, which the radio cycle triggers through BthPS3's own
        // in-order path against a valid context.

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
        private const string BthPortDevicesKey =
            @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\";
        private const string DS3_REMOTE_NAME = "PLAYSTATION(R)3 Controller";

        /// <summary>
        /// Writes the full remembered-device record for the DS3 so BthPS3 identifies it
        /// on every connect. Three parts, all hardware-confirmed load-bearing 2026-07-09:
        ///   1. Name/VID/PID into Devices\&lt;mac&gt;.
        ///   2. The Devices record's OWNER set to SYSTEM. bthport prunes device records
        ///      it doesn't own on radio re-enumeration, so an admin-owned record is
        ///      dropped and the pad stops identifying; a SYSTEM-owned record is kept.
        ///   3. A synthetic link key under Keys\&lt;radiomac&gt; flags the pad
        ///      remembered+authenticated so the stored Name is served instead of the
        ///      clone's blank over-air name.
        /// All native, from the elevated (admin, not SYSTEM) app: SYSTEM-ACL'd keys are
        /// written through REG_OPTION_BACKUP_RESTORE and the owner is set with
        /// SeTakeOwnership/SeRestore, both held by the elevated token.
        /// </summary>
        public static bool WriteRememberedDeviceRecord(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)
        {
            if (!WriteDeviceNameRecord(deviceMacHex, log)) return false;
            if (!SetDeviceRecordOwnerToSystem(deviceMacHex, log)) return false;
            return WriteLinkKeyAnchor(radioMacBigEndian, deviceMacHex, log);
        }

        // Name/VID/PID via REG_OPTION_BACKUP_RESTORE, so a pre-existing SYSTEM-owned
        // record from an earlier pairing can still be overwritten by the elevated app.
        private static bool WriteDeviceNameRecord(string deviceMacHex, Action<string> log)
        {
            EnablePrivilege("SeBackupPrivilege");
            EnablePrivilege("SeRestorePrivilege");
            int rc = RegCreateKeyEx(HKLM, BthPortDevicesKey + deviceMacHex, 0, null,
                REG_OPTION_BACKUP_RESTORE, KEY_READ | KEY_WRITE, IntPtr.Zero, out IntPtr hk, out _);
            if (rc != 0) { log($"Opening the device record failed (rc={rc})."); return false; }
            try
            {
                byte[] ascii = System.Text.Encoding.ASCII.GetBytes(DS3_REMOTE_NAME);
                byte[] name = new byte[ascii.Length + 1];
                Array.Copy(ascii, name, ascii.Length);
                RegSetValueEx(hk, "Name", 0, REG_BINARY, name, name.Length);
                RegSetValueEx(hk, "VID", 0, REG_DWORD, BitConverter.GetBytes(0x054C), 4);
                RegSetValueEx(hk, "PID", 0, REG_DWORD, BitConverter.GetBytes(0x0268), 4);
                return true;
            }
            finally { RegCloseKey(hk); }
        }

        // Owner -> SYSTEM (S-1-5-18). Requires SeRestore to assign an owner other than
        // the caller; SeTakeOwnership to touch the record's security at all.
        private static bool SetDeviceRecordOwnerToSystem(string deviceMacHex, Action<string> log)
        {
            EnablePrivilege("SeTakeOwnershipPrivilege");
            EnablePrivilege("SeRestorePrivilege");
            if (!ConvertStringSidToSid("S-1-5-18", out IntPtr pSid)) { log("SID convert failed."); return false; }
            try
            {
                int rc = SetNamedSecurityInfo(@"MACHINE\" + BthPortDevicesKey + deviceMacHex,
                    SE_REGISTRY_KEY, OWNER_SECURITY_INFORMATION, pSid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (rc != 0) { log($"Setting the record owner failed (rc={rc})."); return false; }
                return true;
            }
            finally { LocalFree(pSid); }
        }

        /// <summary>Deletes the DS3's remembered-device record + link key for a clean
        /// unpair. The Devices subkey is SYSTEM-owned, so ownership is taken back to
        /// Administrators first (SeTakeOwnership/SeRestore) before the delete.</summary>
        public static void DeleteRememberedDeviceRecord(byte[] radioMacBigEndian, string deviceMacHex, Action<string> log)
        {
            try
            {
                EnablePrivilege("SeTakeOwnershipPrivilege");
                EnablePrivilege("SeRestorePrivilege");
                if (ConvertStringSidToSid("S-1-5-32-544", out IntPtr admins)) // BUILTIN\Administrators
                {
                    try
                    {
                        SetNamedSecurityInfo(@"MACHINE\" + BthPortDevicesKey + deviceMacHex,
                            SE_REGISTRY_KEY, OWNER_SECURITY_INFORMATION, admins, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    }
                    finally { LocalFree(admins); }
                }
                // Log a nonzero rc the way WriteLinkKeyAnchor does. Discarding
                // it meant an unpair that silently left the device record in
                // place reported success, and the next pair attempt then hit a
                // stale record with nothing in the log to explain it.
                int rcDel = RegDeleteKey(HKLM, BthPortDevicesKey + deviceMacHex);
                if (rcDel != 0) log($"Removing the device record failed (rc={rcDel}).");
            }
            catch (Exception ex) { log("Removing the device record failed: " + ex.Message); }
            DeleteLinkKeyAnchor(radioMacBigEndian, deviceMacHex, log);
        }

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
            // AutoEnableFilter=0 hands PadForge sole ownership of PSM patching
            // (issue #199 crash mitigation). BthPS3's default (1) auto-arms
            // patching at radio power-up AND re-arms it ~10 s after it denies a
            // foreign device (BthPS3 L2CAP.Connect.c:242, the exact re-arm seen
            // in the 2026-07-10 crash log at 12:29:04). With it off, the filter
            // only patches when PadForge's SetPsmPatching enables it, so BthPS3
            // receives zero incoming connections whenever no DS3 is in play and
            // its use-after-free-on-disconnect path (upstream #48, unfixed at
            // v2.10.470.0) is unreachable. AutoDisableFilter stays default (1):
            // deny-then-off is a fail-safe we keep.
            //
            // NOT on a DsHidMini system (audit 2026-07-24, lens 1r): the
            // coexistence policy says PadForge never owns arming there,
            // because their DS3s connect only while patching is armed and
            // leave no BTHPORT record for AnyDs3Paired to find. Writing the
            // override here would re-take the ownership
            // ReconcilePsmPatchForCrashSafety just repaired, and it
            // outlives PadForge. The install/pair path is the one caller
            // that reached this line without consulting the policy.
            if (!IsDsHidMiniInstalled())
                key?.SetValue("AutoEnableFilter", 0, RegistryValueKind.DWord);
        }

        private static void EnsurePsmPatch(Action<string> log) => SetPsmPatching(true, log);

        /// <summary>True when the BthPS3 profile driver service is installed
        /// (the stack that carries the DS3 over Bluetooth). Cheap registry-free
        /// SCM query; the crash-safety reconcile no-ops when this is false.</summary>
        public static bool IsBthPs3Installed() => IsServiceInstalled("BthPS3");

        /// <summary>True when Nefarius DsHidMini is installed. DsHidMini is a
        /// UMDF driver (its INF's AddService entries are the generic WUDFRd /
        /// mshidumdf reflector, dshidmini.inf), so there is no "dshidmini"
        /// service key to probe; the stable footprints are the installed
        /// driver package (DriverDatabase\DriverPackages\dshidmini.inf_*) and
        /// the driver's own config root (%ProgramData%\DsHidMini,
        /// DsHidMini Configuration.c:680-716). Either marker counts. Gates
        /// the PSM-patch crash policy: a DsHidMini system's DS3s connect
        /// through BthPS3 patching, so PadForge must never disarm it there
        /// (the 2026-07-24 coexistence audit: the startup disarm was breaking
        /// foreign DsHidMini setups whose pads leave no BTHPORT VID/PID
        /// record for AnyDs3Paired to find).</summary>
        public static bool IsDsHidMiniInstalled()
        {
            try
            {
                using var pkgs = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\DriverDatabase\DriverPackages");
                if (pkgs != null)
                {
                    foreach (string name in pkgs.GetSubKeyNames())
                        if (name.StartsWith("dshidmini.inf_", StringComparison.OrdinalIgnoreCase))
                            return true;
                }
            }
            catch { /* fall through to the config-folder marker */ }
            try
            {
                string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.IsNullOrEmpty(pd)
                    && System.IO.Directory.Exists(System.IO.Path.Combine(pd, "DsHidMini")))
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>Restores BthPS3's own PSM-patch auto-arm by deleting the
        /// AutoEnableFilter override (the driver default is TRUE,
        /// BthPS3 Bluetooth.Context.c:279, read from the registry only when
        /// present). The repair half of the DsHidMini coexistence policy: a
        /// PadForge build before 2026-07-24 took sole ownership
        /// (AutoEnableFilter=0) on every BthPS3 system, which left foreign
        /// DsHidMini setups unable to re-arm on their own. Idempotent; no-op
        /// when the value is absent. Takes effect on the next BthPS3 load;
        /// SetPsmPatching drives the immediate state.</summary>
        public static void RestoreBthPs3AutoArm()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(BthPs3ParamsKey, writable: true);
                if (key != null && key.GetValue("AutoEnableFilter") != null)
                    key.DeleteValue("AutoEnableFilter", throwOnMissingValue: false);
            }
            catch { /* best effort; SetPsmPatching still governs the live state */ }
        }

        /// <summary>Asserts AutoEnableFilter=0 on the BthPS3 Parameters key so
        /// BthPS3 stops auto-arming PSM patching on its own (issue #199): it
        /// otherwise arms patching at radio power-up and re-arms it ~10 s after
        /// denying a foreign device (BthPS3 L2CAP.Connect.c:242, the exact
        /// re-arm in the 2026-07-10 crash log). With it off, PadForge's
        /// SetPsmPatching is the sole enabler, so a disable actually sticks.
        /// Takes effect on the next BthPS3 load (the running driver cached the
        /// value at init); SetPsmPatching drives the immediate state. Idempotent,
        /// only writes when the value isn't already 0, never creates the key.</summary>
        public static void EnsurePadForgeOwnsPsmPatch()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(BthPs3ParamsKey, writable: true);
                if (key != null && !(key.GetValue("AutoEnableFilter") is int v && v == 0))
                    key.SetValue("AutoEnableFilter", 0, RegistryValueKind.DWord);
            }
            catch { /* best effort; SetPsmPatching still governs the live state */ }
        }

        /// <summary>Enables or disables BthPS3 PSM patching on EVERY attached
        /// radio (issue #199 crash mitigation). Patching rewrites incoming HID
        /// L2CAP PSMs (0x11/0x13) to BthPS3's DS3 PSMs so the connection routes
        /// to BthPS3 (BthPS3PSM Filter.c:157-205). Disabled, the PSMs pass
        /// through untouched to the inbox Bluetooth HID stack, so BthPS3's
        /// profile driver sees no incoming connection and its racy
        /// connect/identify/disconnect/destroy path cannot run. The filter
        /// persists the state per radio devnode and restores it on attach, and
        /// with AutoEnableFilter=0 (EnsureConsumerParams) BthPS3 never flips it
        /// back, so a disable sticks across radio cycles and reboots until
        /// PadForge re-enables it.
        ///
        /// <para>Idempotent and safe when the filter is absent (logs and
        /// returns). Enumerates radios by DeviceIndex 0..N via GET until
        /// ERROR_NO_SUCH_DEVICE rather than assuming a single radio at index
        /// 0.</para></summary>
        public static void SetPsmPatching(bool enable, Action<string> log)
        {
            IntPtr h = CreateFile(PsmControlPath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
            if (h == INVALID_HANDLE)
            {
                log?.Invoke($"PSM control device not present; cannot {(enable ? "enable" : "disable")} patching.");
                return;
            }
            try
            {
                uint toggleCode = enable
                    ? IOCTL_BTHPS3PSM_ENABLE_PSM_PATCHING
                    : IOCTL_BTHPS3PSM_DISABLE_PSM_PATCHING;

                int count = 0;
                // Drive the sweep off the toggle IOCTL itself: the filter
                // completes it with STATUS_NO_SUCH_DEVICE for an index past the
                // last radio (Sideband.c:317, WdfCollectionGetItem == NULL).
                // Index 0 is always attempted, exactly as the proven single-
                // radio path did, so nothing regresses on a one-radio host. The
                // 32 cap is a spin guard; no host has that many radios. The
                // NO_SUCH_DEVICE early-out is only an optimization: attempting a
                // bad index is a harmless no-op, so correctness never depends on
                // the exact Win32 error mapping.
                for (int index = 0; index < 32; index++)
                {
                    byte[] payload = new byte[4]; // { ULONG DeviceIndex }
                    BitConverter.GetBytes(index).CopyTo(payload, 0);
                    if (DeviceIoControl(h, toggleCode, payload, payload.Length, null, 0, out _, IntPtr.Zero))
                    {
                        count++;
                        continue;
                    }
                    if (Marshal.GetLastWin32Error() == ERROR_NO_SUCH_DEVICE) break;
                }
                log?.Invoke($"PSM patching {(enable ? "enabled" : "disabled")} on {count} radio(s).");
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
        private const int REG_OPTION_BACKUP_RESTORE = 0x04, REG_BINARY = 3, REG_DWORD = 4, KEY_READ = 0x20019, KEY_WRITE = 0x20006;
        private const int SE_REGISTRY_KEY = 4, OWNER_SECURITY_INFORMATION = 0x1;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegCreateKeyEx(UIntPtr hKey, string subKey, int reserved, string cls, int options, int sam, IntPtr sa, out IntPtr res, out int disp);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegSetValueEx(IntPtr hKey, string name, int reserved, int type, byte[] data, int cb);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegDeleteValue(IntPtr hKey, string name);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int RegDeleteKey(UIntPtr hKey, string subKey);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern int RegCloseKey(IntPtr hKey);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int SetNamedSecurityInfo(string name, int objType, int secInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertStringSidToSid(string s, out IntPtr sid);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

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
