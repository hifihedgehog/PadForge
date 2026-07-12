using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// In-app Bluetooth pairing for Nintendo Wii controllers (issue #116).
    ///
    /// Wii Remotes, the Nunchuk/Classic extensions, and the Wii U Pro
    /// Controller use a legacy Bluetooth pairing ceremony that the Windows
    /// pairing UI cannot drive. This is a direct port of Dolphin's
    /// Source/Core/Core/HW/WiimoteReal/IOWin.cpp, the canonical decade-old
    /// Windows reference. The important part Dolphin establishes: do NOT use
    /// the BluetoothRegisterForAuthenticationEx callback path. Use the
    /// deprecated BluetoothAuthenticateDevice with the PIN passed directly as a
    /// wide-char array (each of the six Bluetooth-address bytes widened into one
    /// WCHAR, low byte first), then BluetoothEnumerateInstalledServices (which
    /// Dolphin notes "must be done to make the remote remember the pairing"),
    /// then BluetoothSetServiceState to enable the HID service.
    ///
    /// Two pairing modes, matching the controller's two sync methods:
    ///  - SYNC button (red button under the battery cover). PIN is the host
    ///    radio's address. Bonds persistently, so the controller reconnects on
    ///    any button press after a disconnect.
    ///  - 1+2 hold (temporary). PIN is the controller's own address.
    ///
    /// Requires the process to be elevated. PadForge always runs elevated.
    ///
    /// This service does the OS-level pairing only. Once paired, SDL's
    /// hidapi_wii driver enumerates and drives the controller (Wii Remote,
    /// Nunchuk, Classic, Wii U Pro) and PadForge's normal device pipeline maps
    /// it from there. SDL driving a Bluetooth Wii Remote on Windows 8+ relies on
    /// the SDL3 fork's hid_write fix (hifihedgehog/SDL#2): the remote's output
    /// reports must go via HidD_SetOutputReport, since the Microsoft Bluetooth
    /// stack rejects WriteFile for it.
    /// </summary>
    public sealed class WiiPairingService
    {
        /// <summary>Name prefix every Wii peripheral advertises over Bluetooth
        /// ("Nintendo RVL-CNT-01", "-TR", "-UC", and so on). A fresh inquiry
        /// often returns an empty name, so this is the preferred match but not
        /// the only one (see the Class-of-Device fallback below).</summary>
        private const string WiiNamePrefix = "Nintendo";

        // Known Wii controller Class-of-Device values, used as a fallback match
        // when the inquiry returns no name (common for a never-seen device).
        // 0x002504 = Wii Remote, 0x000508 = Wii Remote Plus / -TR. Matching the
        // exact values rather than the broad Peripheral major class avoids
        // attempting to pair a stray Bluetooth keyboard or mouse.
        private static bool IsWiiClassOfDevice(uint cod) =>
            cod == 0x002504 || cod == 0x000508;

        /// <summary>Result of one inquiry-and-pair pass.</summary>
        public sealed class PairPassResult
        {
            /// <summary>Wii controllers seen in this inquiry pass (by name).</summary>
            public List<string> Found { get; } = new();

            /// <summary>Controllers this pass successfully bonded (by name).</summary>
            public List<string> Paired { get; } = new();

            /// <summary>Total Bluetooth devices the inquiry returned this pass,
            /// Wii or not. Zero means the inquiry itself saw nothing.</summary>
            public int DiscoveredCount { get; set; }

            /// <summary>Set when the radio could not be opened or queried. Null
            /// on success even when no controllers were found.</summary>
            public string Error { get; set; }
        }

        /// <summary>
        /// Runs a single Bluetooth inquiry and attempts to pair every Wii
        /// controller in pairing mode, following Dolphin's
        /// FindAndAuthenticateWiimotes flow. Blocks for the inquiry duration
        /// (about three seconds), so call it from a background thread. The
        /// caller loops over passes (Dolphin runs three iterations per click)
        /// until a controller pairs or the user cancels.
        /// </summary>
        /// <param name="temporary">True to use the 1+2 temporary PIN (the
        /// controller's own address). False to use the SYNC-button PIN (the
        /// host address) which bonds persistently.</param>
        public PairPassResult RunPairingPass(bool temporary, CancellationToken ct)
        {
            var result = new PairPassResult();

            IntPtr hRadio = IntPtr.Zero;
            IntPtr hRadioFind = IntPtr.Zero;
            try
            {
                var radioParams = new BLUETOOTH_FIND_RADIO_PARAMS
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>()
                };
                hRadioFind = BluetoothFindFirstRadio(ref radioParams, out hRadio);
                if (hRadioFind == IntPtr.Zero || hRadio == IntPtr.Zero)
                {
                    Log($"no radio (win32={Marshal.GetLastWin32Error()})");
                    result.Error = "no-radio";
                    return result;
                }

                var radioInfo = new BLUETOOTH_RADIO_INFO
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_RADIO_INFO>()
                };
                uint riRc = BluetoothGetRadioInfo(hRadio, ref radioInfo);
                if (riRc != 0)
                {
                    Log($"BluetoothGetRadioInfo failed rc={riRc}");
                    result.Error = "radio-info";
                    return result;
                }

                Log($"=== pass start (temporary={temporary}) host={FormatAddr(radioInfo.address)} radio='{radioInfo.szName}' ===");

                // Force BthPS3 PSM patching off for the whole pass (issue #199).
                // A Wii Remote's incoming HID connection must not enter BthPS3's
                // identify/deny/destroy path, which is where the upstream
                // use-after-free lives (a stray Wii connect through that path
                // bugchecked the box on 2026-07-10). With patching off the Wii's
                // standard HID PSMs pass through to the inbox Bluetooth stack,
                // which is where a Wii Remote belongs anyway. Restored to policy
                // in the outer finally, on every exit path.
                if (Ds3DriverInstaller.IsBthPs3Installed())
                {
                    Log("PSM patching forced off for the Wii pass (issue #199).");
                    Ds3DriverInstaller.SetPsmPatching(false, Log);
                }

                var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                    // Return every state, not just unknown. A controller left
                    // in a stale half-paired state by an earlier attempt is
                    // "remembered", and filtering those out hides it so it can
                    // never be reset and re-paired.
                    fReturnAuthenticated = 1,
                    fReturnRemembered = 1,
                    fReturnUnknown = 1,
                    fReturnConnected = 1,
                    fIssueInquiry = 1,
                    cTimeoutMultiplier = 2, // about 2.5s of inquiry per pass
                    hRadio = hRadio
                };

                var deviceInfo = new BLUETOOTH_DEVICE_INFO
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>()
                };

                IntPtr hDevFind = BluetoothFindFirstDevice(ref search, ref deviceInfo);
                if (hDevFind == IntPtr.Zero)
                {
                    Log($"inquiry returned 0 devices (win32={Marshal.GetLastWin32Error()}). Close Windows' own 'Add a device' panel so the radio is free for this inquiry.");
                    return result;
                }

                try
                {
                    do
                    {
                        if (ct.IsCancellationRequested) break;

                        result.DiscoveredCount++;
                        string name = deviceInfo.szName ?? string.Empty;
                        uint cod = deviceInfo.ulClassofDevice;
                        bool nameMatch = !string.IsNullOrEmpty(name)
                            && name.StartsWith(WiiNamePrefix, StringComparison.OrdinalIgnoreCase);
                        bool codMatch = string.IsNullOrEmpty(name) && IsWiiClassOfDevice(cod);
                        bool isWii = nameMatch || codMatch;

                        Log($"  device addr={FormatAddr(deviceInfo.Address)} cod=0x{cod:X6} " +
                            $"name='{name}' conn={deviceInfo.fConnected} remem={deviceInfo.fRemembered} auth={deviceInfo.fAuthenticated} " +
                            $"=> {(isWii ? (nameMatch ? "WII(name)" : "WII(cod)") : "skip")}");

                        if (!isWii) continue;

                        string label = string.IsNullOrEmpty(name) ? FormatAddr(deviceInfo.Address) : name;

                        // Already connected and working. Leave it alone.
                        if (deviceInfo.fConnected != 0)
                        {
                            Log($"  {label} already connected");
                            result.Found.Add(label);
                            result.Paired.Add(label);
                            continue;
                        }

                        // Dolphin's RemoveUnusableWiimoteBluetoothDevices: a
                        // remembered-but-not-connected-and-not-authenticated
                        // record cannot reconnect and blocks re-pairing. Forget
                        // it and let the next pass rediscover it fresh.
                        if (deviceInfo.fRemembered != 0 && deviceInfo.fAuthenticated == 0)
                        {
                            uint rmRc = BluetoothRemoveDevice(ref deviceInfo.Address);
                            Log($"  {label} unusable remembered record, BluetoothRemoveDevice rc={rmRc} (rediscover next pass)");
                            continue;
                        }

                        result.Found.Add(label);

                        ulong pinSource = temporary ? deviceInfo.Address.ullLong : radioInfo.address.ullLong;
                        if (TryPairDevice(hRadio, ref deviceInfo, pinSource, label))
                        {
                            Log($"  PAIRED {label}");
                            result.Paired.Add(label);
                        }
                        else
                        {
                            Log($"  pair FAILED for {label}");
                        }
                    }
                    while (BluetoothFindNextDevice(hDevFind, ref deviceInfo));
                }
                finally
                {
                    BluetoothFindDeviceClose(hDevFind);
                }

                Log($"=== pass end: discovered={result.DiscoveredCount} wiiFound={result.Found.Count} paired={result.Paired.Count} ===");
            }
            catch (DllNotFoundException ex)
            {
                Log($"bluetooth stack not found: {ex.Message}");
                result.Error = "no-bluetooth-stack";
            }
            catch (Exception ex)
            {
                Log($"exception: {ex}");
                result.Error = "exception";
            }
            finally
            {
                if (hRadio != IntPtr.Zero) CloseHandle(hRadio);
                if (hRadioFind != IntPtr.Zero) BluetoothFindRadioClose(hRadioFind);
                // Restore PSM patching to its policy state (issue #199): armed
                // only if a DS3 is actually paired, off otherwise. Runs on every
                // exit path, including the no-radio / exception early returns
                // that never forced it off (a harmless no-op there).
                Ds3PairingService.ReconcilePsmPatchForCrashSafety("wii-pass-end");
            }

            return result;
        }

        /// <summary>
        /// Fast check (no inquiry) for whether a paired Wii controller is
        /// currently connected. Used after pairing to time the SDL
        /// re-enumeration to the moment the controller actually connects (it
        /// connects on a button press, which can be many seconds after the
        /// pair completes), rather than a fixed delay.
        /// </summary>
        public bool IsWiiConnected()
        {
            IntPtr hRadio = IntPtr.Zero, hRadioFind = IntPtr.Zero;
            try
            {
                var rp = new BLUETOOTH_FIND_RADIO_PARAMS
                { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
                hRadioFind = BluetoothFindFirstRadio(ref rp, out hRadio);
                if (hRadioFind == IntPtr.Zero || hRadio == IntPtr.Zero) return false;

                var search = new BLUETOOTH_DEVICE_SEARCH_PARAMS
                {
                    dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                    fReturnAuthenticated = 1,
                    fReturnRemembered = 1,
                    fReturnUnknown = 0,
                    fReturnConnected = 1,
                    fIssueInquiry = 0, // no inquiry: just read current state, fast
                    cTimeoutMultiplier = 0,
                    hRadio = hRadio
                };
                var di = new BLUETOOTH_DEVICE_INFO
                { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>() };

                IntPtr hFind = BluetoothFindFirstDevice(ref search, ref di);
                if (hFind == IntPtr.Zero) return false;
                try
                {
                    do
                    {
                        string name = di.szName ?? string.Empty;
                        if (di.fConnected != 0
                            && name.StartsWith(WiiNamePrefix, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    while (BluetoothFindNextDevice(hFind, ref di));
                }
                finally { BluetoothFindDeviceClose(hFind); }
                return false;
            }
            catch { return false; }
            finally
            {
                if (hRadio != IntPtr.Zero) CloseHandle(hRadio);
                if (hRadioFind != IntPtr.Zero) BluetoothFindRadioClose(hRadioFind);
            }
        }

        /// <summary>
        /// Bonds one discovered controller, porting Dolphin's AuthenticateWiimote
        /// plus the HID-service enable. Authenticates with the deprecated
        /// BluetoothAuthenticateDevice (PIN passed directly, no callback), then
        /// enumerates installed services so the remote remembers the pairing,
        /// then enables the HID service.
        /// </summary>
        private bool TryPairDevice(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO device, ulong pinSource, string label)
        {
            if (device.fAuthenticated == 0)
            {
                // The PIN is the six Bluetooth-address bytes (low byte first),
                // each widened into one WCHAR, length six. This exact shape is
                // what Dolphin passes and what the deprecated API expects.
                char[] passkey = new char[6];
                for (int i = 0; i < 6; i++)
                    passkey[i] = (char)((pinSource >> (8 * i)) & 0xFF);
                Log($"    BluetoothAuthenticateDevice {label} passkey={FormatPasskey(passkey)}");

                uint authRc = BluetoothAuthenticateDevice(IntPtr.Zero, hRadio, ref device, passkey, 6);
                Log($"    BluetoothAuthenticateDevice rc={authRc}" + (authRc != 0 ? $" ({DescribeError(authRc)})" : ""));
                if (authRc != 0)
                    return false;

                // "Apparently must be done to make the remote remember the
                // pairing." (Dolphin). Count-only query, null service array.
                uint pcServices = 0;
                uint enumRc = BluetoothEnumerateInstalledServices(hRadio, ref device, ref pcServices, IntPtr.Zero);
                Log($"    BluetoothEnumerateInstalledServices rc={enumRc} services={pcServices}");
                if (enumRc != 0 && enumRc != ERROR_MORE_DATA)
                    return false;
            }

            var hidGuid = HumanInterfaceDeviceServiceClass_UUID;
            uint rc = BluetoothSetServiceState(hRadio, ref device, ref hidGuid, BLUETOOTH_SERVICE_ENABLE);
            Log($"    BluetoothSetServiceState(HID, ENABLE) rc={rc}" + (rc != 0 ? $" ({DescribeError(rc)})" : ""));
            return rc == 0;
        }

        private static string FormatAddr(BLUETOOTH_ADDRESS a) => FormatAddr(a.ullLong);

        private static string FormatAddr(ulong a) =>
            $"{(a >> 40) & 0xFF:X2}:{(a >> 32) & 0xFF:X2}:{(a >> 24) & 0xFF:X2}:{(a >> 16) & 0xFF:X2}:{(a >> 8) & 0xFF:X2}:{a & 0xFF:X2}";

        private static string FormatPasskey(char[] p)
        {
            var parts = new string[p.Length];
            for (int i = 0; i < p.Length; i++) parts[i] = ((int)p[i]).ToString("X2");
            return string.Join(" ", parts);
        }

        private static string DescribeError(uint rc) => rc switch
        {
            5 => "ACCESS_DENIED (need elevation)",
            31 => "GEN_FAILURE",
            87 => "INVALID_PARAMETER",
            170 => "BUSY",
            234 => "MORE_DATA",
            259 => "NO_MORE_ITEMS",
            1167 => "DEVICE_NOT_CONNECTED",
            _ => "see winerror.h"
        };

        /// <summary>Pairing narration goes to the in-memory diagnostics
        /// ring (crash context) and to the dialog via the pairing
        /// callbacks. PadForge writes no pairing log file.</summary>
        private static void Log(string message)
            => PadForge.Engine.SdlDiagLog.WriteLine("WIIPAIR " + message);

        // ─────────────────────────────────────────────
        //  Win32 bluetoothapis interop
        // ─────────────────────────────────────────────

        private const uint BLUETOOTH_SERVICE_ENABLE = 0x01;
        private const uint ERROR_MORE_DATA = 234;

        // HumanInterfaceDeviceServiceClass_UUID {00001124-0000-1000-8000-00805F9B34FB}
        private static readonly Guid HumanInterfaceDeviceServiceClass_UUID =
            new Guid(0x00001124, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_FIND_RADIO_PARAMS
        {
            public uint dwSize;
        }

        // BLUETOOTH_ADDRESS is an 8-byte (ULONGLONG) union; the low six bytes
        // are the address, least significant first.
        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_ADDRESS
        {
            public ulong ullLong;
        }

        // Default packing is REQUIRED (not Pack = 1). BLUETOOTH_ADDRESS is
        // 8-byte aligned, so the native struct has 4 pad bytes after dwSize.
        // Pack = 1 undersizes it and BluetoothGetRadioInfo rejects dwSize with
        // ERROR_REVISION_MISMATCH (1306). Natural alignment gives 520 bytes.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_RADIO_INFO
        {
            public uint dwSize;
            public BLUETOOTH_ADDRESS address;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string szName;
            public uint ulClassofDevice;
            public ushort lmpSubversion;
            public ushort manufacturer;
        }

        // Default packing, same reason as BLUETOOTH_RADIO_INFO. Native 560 bytes.
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
        private struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        // hRadio is pointer-sized, so this struct must keep default alignment
        // (not Pack = 1) for the trailing handle to land on its 8-byte slot.
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

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstRadio(
            ref BLUETOOTH_FIND_RADIO_PARAMS pbtfrp, out IntPtr phRadio);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("bthprops.cpl")]
        private static extern uint BluetoothGetRadioInfo(
            IntPtr hRadio, ref BLUETOOTH_RADIO_INFO pRadioInfo);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstDevice(
            ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindNextDevice(
            IntPtr hFind, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("bthprops.cpl", SetLastError = true)]
        private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

        [DllImport("bthprops.cpl")]
        private static extern uint BluetoothRemoveDevice(ref BLUETOOTH_ADDRESS pAddress);

        // Deprecated but the proven Wiimote path (Dolphin). PIN is passed
        // directly as a wide-char array, length = number of address bytes (6).
        [DllImport("bthprops.cpl", CharSet = CharSet.Unicode)]
        private static extern uint BluetoothAuthenticateDevice(
            IntPtr hwndParent, IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi,
            char[] pszPasskey, uint ulPasskeyLength);

        [DllImport("bthprops.cpl")]
        private static extern uint BluetoothEnumerateInstalledServices(
            IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi,
            ref uint pcServiceInout, IntPtr pGuidServices);

        [DllImport("bthprops.cpl")]
        private static extern uint BluetoothSetServiceState(
            IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref Guid pGuidService,
            uint dwServiceFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
