using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace PadForge.Services
{
    /// <summary>
    /// In-app Bluetooth pairing for the Sony DualShock 3 (SIXAXIS, VID 054C PID 0268),
    /// the DS3 counterpart to <see cref="WiiPairingService"/> (issue #116). The DS3
    /// cannot be paired through Windows' "Add a device" dialog: it connects device-
    /// initiated on the reserved L2CAP HID PSMs the inbox stack refuses, which is why
    /// the BthPS3 filter driver exists. So pairing is a guided USB ceremony:
    ///
    ///   1. The pad is docked over USB and its interface bound to inbox WinUSB (its
    ///      magic reports 0xF2/0xF5 are absent from the HID descriptor, so HidUsb can't
    ///      send them; WinUSB sends the raw control transfers the firmware answers).
    ///   2. Sixpair: the PC's Bluetooth radio address is written into the pad
    ///      (SET_REPORT FEATURE 0xF5) so it pages THIS radio on PS-press.
    ///   3. A persistent identity record is written into the radio's device list so
    ///      BthPS3 recognises the pad by name on connect (survives reboot, unlike the
    ///      radio's volatile remote-name cache).
    ///   4. The radio is cycled so BthPS3/BthPS3PSM pick the record up.
    ///
    /// Then the user unplugs the pad and presses PS; <see cref="Common.Input.Ds3DirectService"/>
    /// opens the resulting BthPS3 raw PDO and streams it through SDL. All steps run from
    /// the always-elevated app: Administrators have FullControl of the BTHPORT device
    /// list and the DS3 does zero authentication, so no SYSTEM context or link key is
    /// needed.
    /// </summary>
    public sealed class Ds3PairingService
    {
        public const ushort DS3_VID = 0x054C;
        public const ushort DS3_PID = 0x0268;

        // The DS3's own remote name, matched by BthPS3's default SIXAXISSupportedNames.
        private const string DS3_REMOTE_NAME = "PLAYSTATION(R)3 Controller";

        private const string DevicesRoot =
            @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

        private readonly Action<string> _log;
        public Ds3PairingService(Action<string> log = null)
            => _log = msg => { LogLine(msg); log?.Invoke(msg); };

        /// <summary>Path of the human-readable pairing log, surfaced in the dialog
        /// so a failed pair can be diagnosed from real data (same convention as
        /// <see cref="WiiPairingService.LogPath"/>).</summary>
        public static string LogPath =>
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForge-Ds3Pair.log");

        private static readonly object _logLock = new();

        private static void LogLine(string message)
        {
            try
            {
                lock (_logLock)
                    System.IO.File.AppendAllText(LogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { /* logging must never break pairing */ }
        }

        public sealed class PairResult
        {
            /// <summary>The pad's own BT MAC (from 0xF2), lowercase hex no separators.</summary>
            public string Ds3Mac { get; set; }
            /// <summary>The radio MAC written into the pad (human/big-endian order).</summary>
            public byte[] RadioMac { get; set; }
            public bool Success { get; set; }
            /// <summary>One of: no-radio, no-ds3-usb, winusb-bind-failed, sixpair-failed,
            /// identity-inject-failed, install-failed, ok.</summary>
            public string Error { get; set; }
        }

        // ── the six-step ceremony ───────────────────────────────────────────────

        /// <summary>
        /// Runs the full USB pairing ceremony. The DS3 must be connected over USB.
        /// Progress is reported through the constructor log callback.
        /// </summary>
        public PairResult RunPairing(CancellationToken ct = default)
        {
            var r = new PairResult();

            // 0. Ensure the driver stack is present (install once). Filled after grounding.
            if (!EnsureBthPs3Installed())
            {
                _log("BthPS3 driver install failed.");
                r.Error = "install-failed";
                return r;
            }

            // 1. Read this PC's Bluetooth radio address (the pairing target).
            byte[] radio = ReadRadioMac();
            if (radio == null) { _log("No Bluetooth radio found."); r.Error = "no-radio"; return r; }
            r.RadioMac = radio;
            _log($"Bluetooth radio: {Hex(radio, ':')}");

            // 2. Ensure the docked DS3 is bound to WinUSB so we can send its magic reports.
            if (!EnsureWinUsbBound(ct))
            {
                _log("Could not bind the DS3 to WinUSB. Is it connected by USB cable?");
                r.Error = "winusb-bind-failed";
                return r;
            }

            string path = FindWinUsbDs3();
            if (path == null) { _log("DS3 not found on USB."); r.Error = "no-ds3-usb"; return r; }

            // WinUsb_Initialize requires the device handle opened FILE_FLAG_OVERLAPPED
            // (WinUSB contract; proven prototype ds3winusb Program.cs:153).
            IntPtr dev = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (dev == INVALID_HANDLE) { _log($"Opening the DS3 failed (err={Marshal.GetLastWin32Error()})."); r.Error = "no-ds3-usb"; return r; }
            try
            {
                if (!WinUsb_Initialize(dev, out IntPtr ifh))
                { _log($"WinUsb_Initialize failed (err={Marshal.GetLastWin32Error()})."); r.Error = "no-ds3-usb"; return r; }
                try
                {
                    // 3. Read the pad's own MAC (0xF2 bytes 4-9) - the registry key name.
                    byte[] f2 = new byte[17];
                    if (!GetFeature(ifh, 0xF2, f2)) { _log($"Reading the DS3 MAC failed (err={Marshal.GetLastWin32Error()})."); r.Error = "sixpair-failed"; return r; }
                    byte[] ds3mac = new byte[6];
                    Array.Copy(f2, 4, ds3mac, 0, 6);
                    r.Ds3Mac = Hex(ds3mac, null).ToLowerInvariant();
                    _log($"DS3 address: {Hex(ds3mac, ':')}");

                    // 4. Sixpair: write the radio MAC into the pad (SET_REPORT FEATURE 0xF5).
                    byte[] set = new byte[8];
                    set[0] = 0x01; set[1] = 0x00;
                    Array.Copy(radio, 0, set, 2, 6);
                    if (!SetFeature(ifh, 0xF5, set)) { _log($"Sixpair write failed (err={Marshal.GetLastWin32Error()})."); r.Error = "sixpair-failed"; return r; }
                    _log("Sixpair written.");
                }
                finally { WinUsb_Free(ifh); }
            }
            finally { CloseHandle(dev); }

            // 5. Persist the pad's identity into the radio's device list (BthPS3 reads it).
            if (!InjectIdentity(r.Ds3Mac)) { _log("Registering the pad failed."); r.Error = "identity-inject-failed"; return r; }
            _log("Pad registered with the Bluetooth stack.");

            // 6. Cycle the radio so the drivers pick up the new record.
            CycleRadio();
            _log("Bluetooth radio cycled. Unplug the DS3 and press the PS button.");

            r.Success = true;
            r.Error = "ok";
            return r;
        }

        /// <summary>Removes the pad's pairing + device node so a clean dry run (or a
        /// user "forget this controller") starts from a first-time state.</summary>
        public void Unpair(string ds3Mac)
        {
            if (!string.IsNullOrEmpty(ds3Mac))
            {
                try
                {
                    using var root = Registry.LocalMachine.OpenSubKey(DevicesRoot, writable: true);
                    if (root?.OpenSubKey(ds3Mac) != null)
                    {
                        root.DeleteSubKeyTree(ds3Mac, throwOnMissingSubKey: false);
                        _log($"Removed device record {ds3Mac}.");
                    }
                }
                catch (Exception ex) { _log("Removing the device record failed: " + ex.Message); }
            }
            RemoveBthPs3Node();
            CycleRadio();
            _log("Pairing cleared.");
        }

        // ── persistent identity record (admin-writable; DS3 does zero auth) ──────

        /// <summary>
        /// Writes the minimal device record BthPS3 needs to identify the pad by name:
        /// Name (ASCII + NUL), VID, PID. Deliberately minimal - cloning a full paired
        /// record (with a CachedServices HID SDP blob) makes Windows enumerate the pad
        /// under inbox hidbth and fight BthPS3 for it. Administrators have FullControl
        /// of this key, so no SYSTEM context is needed.
        /// </summary>
        private bool InjectIdentity(string ds3Mac)
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(DevicesRoot, writable: true);
                if (root == null) { _log("BTHPORT device list not found."); return false; }
                using var key = root.CreateSubKey(ds3Mac, writable: true);
                if (key == null) return false;

                byte[] name = new byte[DS3_REMOTE_NAME.Length + 1];
                System.Text.Encoding.ASCII.GetBytes(DS3_REMOTE_NAME, 0, DS3_REMOTE_NAME.Length, name, 0);
                key.SetValue("Name", name, RegistryValueKind.Binary);
                key.SetValue("VID", (int)DS3_VID, RegistryValueKind.DWord);
                key.SetValue("PID", (int)DS3_PID, RegistryValueKind.DWord);
                return true;
            }
            catch (Exception ex) { _log("Identity inject failed: " + ex.Message); return false; }
        }

        // ── local Bluetooth radio address (human/big-endian order per DsHidMini) ─

        /// <summary>The local radio's MAC in the byte order the DS3 expects (human /
        /// big-endian, i.e. rgBytes reversed - DsHidMini Ds3.c:364-368).</summary>
        public byte[] ReadRadioMac()
        {
            var fp = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
            IntPtr hFind = BluetoothFindFirstRadio(ref fp, out IntPtr hRadio);
            if (hFind == IntPtr.Zero) return null;
            try
            {
                var info = new BLUETOOTH_RADIO_INFO { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_RADIO_INFO>() };
                if (BluetoothGetRadioInfo(hRadio, ref info) != 0) return null;
                byte[] be = new byte[6];
                for (int i = 0; i < 6; i++) be[i] = (byte)((info.address.ullLong >> (8 * (5 - i))) & 0xFF);
                return be;
            }
            finally { CloseHandle(hRadio); BluetoothFindRadioClose(hFind); }
        }

        // ── WinUSB DS3 discovery + magic reports (proven prototype path) ─────────

        // Interface GUID from the shipped ds3_winusb.inf.
        private static readonly Guid DS3_WINUSB_IF = new Guid("B35924D6-3E16-4A9E-9782-5524A4B79BAC");

        private static string FindWinUsbDs3() => FindInterfacePath(DS3_WINUSB_IF);

        private static bool GetFeature(IntPtr ifh, byte reportId, byte[] buf)
        {
            var s = new WINUSB_SETUP_PACKET
            {
                RequestType = 0xA1, Request = 0x01,
                Value = (ushort)((0x03 << 8) | reportId), Index = 0, Length = (ushort)buf.Length
            };
            return WinUsb_ControlTransfer(ifh, s, buf, (uint)buf.Length, out _, IntPtr.Zero);
        }

        private static bool SetFeature(IntPtr ifh, byte reportId, byte[] buf)
        {
            var s = new WINUSB_SETUP_PACKET
            {
                RequestType = 0x21, Request = 0x09,
                Value = (ushort)((0x03 << 8) | reportId), Index = 0, Length = (ushort)buf.Length
            };
            return WinUsb_ControlTransfer(ifh, s, buf, (uint)buf.Length, out _, IntPtr.Zero);
        }

        // ── driver install + radio cycle + node removal (filled from grounding) ──

        private bool EnsureBthPs3Installed() => Ds3DriverInstaller.EnsureInstalled(_log);
        private bool EnsureWinUsbBound(CancellationToken ct) => Ds3DriverInstaller.EnsureWinUsbBound(_log, ct);
        private void CycleRadio() => Ds3DriverInstaller.CycleBluetoothRadio(_log);
        private void RemoveBthPs3Node() => Ds3DriverInstaller.RemoveDs3Node(_log);

        // ── helpers ─────────────────────────────────────────────────────────────

        private static string Hex(byte[] b, char? sep)
        {
            var sb = new System.Text.StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0 && sep.HasValue) sb.Append(sep.Value);
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private static string FindInterfacePath(Guid ifGuid)
        {
            IntPtr set = SetupDiGetClassDevs(ref ifGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE) return null;
            var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            try
            {
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref ifGuid, i, ref did); i++)
                {
                    int req = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref req, IntPtr.Zero);
                    IntPtr det = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(det, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref did, det, req, ref req, IntPtr.Zero))
                            return Marshal.PtrToStringUni(det + 4);
                    }
                    finally { Marshal.FreeHGlobal(det); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        // ── P/Invoke ────────────────────────────────────────────────────────────

        private static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        private const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)] private struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }
        [StructLayout(LayoutKind.Sequential)] private struct BLUETOOTH_ADDRESS { public ulong ullLong; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_RADIO_INFO
        {
            public uint dwSize; public BLUETOOTH_ADDRESS address;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
            public uint ulClassofDevice; public ushort lmpSubversion; public ushort manufacturer;
        }
        [DllImport("bthprops.cpl", SetLastError = true)] private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr phRadio);
        [DllImport("bthprops.cpl", SetLastError = true)] private static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("bthprops.cpl")] private static extern uint BluetoothGetRadioInfo(IntPtr hRadio, ref BLUETOOTH_RADIO_INFO info);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WINUSB_SETUP_PACKET { public byte RequestType; public byte Request; public ushort Value; public ushort Index; public ushort Length; }
        [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Initialize(IntPtr dev, out IntPtr ifh);
        [DllImport("winusb.dll", SetLastError = true)] private static extern bool WinUsb_Free(IntPtr ifh);
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ControlTransfer(IntPtr ifh, WINUSB_SETUP_PACKET setup, byte[] buf, uint len, out uint moved, IntPtr ov);

        [StructLayout(LayoutKind.Sequential)] private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr det, int ds, ref int req, IntPtr di);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    }
}
