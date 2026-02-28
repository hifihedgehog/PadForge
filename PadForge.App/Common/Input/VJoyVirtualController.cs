using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Virtual joystick controller via the vJoy driver.
    /// Uses direct P/Invoke to vJoyInterface.dll (no NuGet dependency).
    /// vJoy devices are configurable (axes, buttons, hats) via the
    /// vJoy Configuration utility — this controller maps the standard
    /// Gamepad struct to whatever axes/buttons the device supports.
    /// </summary>
    internal sealed class VJoyVirtualController : IVirtualController
    {
        private static bool _dllLoaded;

        /// <summary>
        /// Preloads vJoyInterface.dll from the vJoy installation directory.
        /// Once loaded into the process, all [DllImport] calls resolve to it.
        /// Only caches success — retries on next call if the DLL wasn't found
        /// (e.g., user installed vJoy after app startup).
        /// </summary>
        internal static void EnsureDllLoaded()
        {
            if (_dllLoaded) return;

            // Already loadable from default search paths?
            if (NativeLibrary.TryLoad("vJoyInterface.dll", out _))
            {
                _dllLoaded = true;
                return;
            }

            // Try vJoy installation directory (root first, then arch subdirectory for legacy installs).
            string vjoyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            string vjoyPath = Path.Combine(vjoyDir, "vJoyInterface.dll");
            if (!File.Exists(vjoyPath))
            {
                string arch = Environment.Is64BitProcess ? "x64" : "x86";
                vjoyPath = Path.Combine(vjoyDir, arch, "vJoyInterface.dll");
            }
            if (File.Exists(vjoyPath) && NativeLibrary.TryLoad(vjoyPath, out _))
                _dllLoaded = true;
        }

        private readonly uint _deviceId;
        private bool _connected;

        public VirtualControllerType Type => VirtualControllerType.VJoy;
        public bool IsConnected => _connected;

        public VJoyVirtualController(uint deviceId)
        {
            if (deviceId < 1 || deviceId > 16)
                throw new ArgumentOutOfRangeException(nameof(deviceId), "vJoy device ID must be 1–16.");
            _deviceId = deviceId;
        }

        public void Connect()
        {
            EnsureDllLoaded();
            var status = VJoyNative.GetVJDStatus(_deviceId);

            if (status != VjdStat.VJD_STAT_FREE && status != VjdStat.VJD_STAT_OWN)
                throw new InvalidOperationException($"vJoy device {_deviceId} is not available (status: {status}).");

            if (status == VjdStat.VJD_STAT_FREE)
            {
                if (!VJoyNative.AcquireVJD(_deviceId))
                    throw new InvalidOperationException($"Failed to acquire vJoy device {_deviceId}.");
            }

            VJoyNative.ResetVJD(_deviceId);
            _connected = true;
        }

        /// <summary>The vJoy device ID (1–16) this controller was created with.</summary>
        public uint DeviceId => _deviceId;

        public void Disconnect()
        {
            if (_connected)
            {
                VJoyNative.ResetVJD(_deviceId);
                VJoyNative.RelinquishVJD(_deviceId);
                _connected = false;
            }

            // Device nodes are persistent — no deletion here.
            // They stay alive for instant reuse on next Connect().
            // Nodes are only removed during driver uninstall.
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            if (!_connected) return;

            // Axes: Gamepad thumbsticks are signed short (-32768 to 32767).
            // vJoy axes are 0 to 32767 (default range).
            // Convert: (short + 32768) / 2 → 0..32767
            VJoyNative.SetAxis(ShortToVJoy(gp.ThumbLX), _deviceId, HID_USAGES.HID_USAGE_X);
            VJoyNative.SetAxis(ShortToVJoy(gp.ThumbLY), _deviceId, HID_USAGES.HID_USAGE_Y);
            VJoyNative.SetAxis(ShortToVJoy(gp.ThumbRX), _deviceId, HID_USAGES.HID_USAGE_RX);
            VJoyNative.SetAxis(ShortToVJoy(gp.ThumbRY), _deviceId, HID_USAGES.HID_USAGE_RY);

            // Triggers: 0–255 byte → 0–32767 vJoy axis
            VJoyNative.SetAxis(gp.LeftTrigger * 32767 / 255, _deviceId, HID_USAGES.HID_USAGE_Z);
            VJoyNative.SetAxis(gp.RightTrigger * 32767 / 255, _deviceId, HID_USAGES.HID_USAGE_RZ);

            // Buttons (1-based for vJoy)
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.A), _deviceId, 1);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.B), _deviceId, 2);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.X), _deviceId, 3);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.Y), _deviceId, 4);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.LEFT_SHOULDER), _deviceId, 5);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER), _deviceId, 6);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.BACK), _deviceId, 7);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.START), _deviceId, 8);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.LEFT_THUMB), _deviceId, 9);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.RIGHT_THUMB), _deviceId, 10);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.GUIDE), _deviceId, 11);

            // D-Pad → discrete POV hat (0=Up, 1=Right, 2=Down, 3=Left, -1=centered)
            int povValue = GamepadDPadToDiscPov(gp.Buttons);
            VJoyNative.SetDiscPov(povValue, _deviceId, 1);
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            // vJoy has no rumble/force feedback callback — no-op.
        }

        /// <summary>Converts signed short (-32768..32767) to vJoy range (0..32767).</summary>
        private static int ShortToVJoy(short value)
        {
            return (value + 32768) / 2;
        }

        /// <summary>Extracts D-Pad from Gamepad buttons to vJoy discrete POV value.</summary>
        private static int GamepadDPadToDiscPov(ushort buttons)
        {
            bool up = (buttons & Gamepad.DPAD_UP) != 0;
            bool down = (buttons & Gamepad.DPAD_DOWN) != 0;
            bool left = (buttons & Gamepad.DPAD_LEFT) != 0;
            bool right = (buttons & Gamepad.DPAD_RIGHT) != 0;

            // vJoy discrete POV: 0=Up, 1=Right, 2=Down, 3=Left, -1=centered
            // Diagonals not supported in discrete mode — prioritize cardinal directions.
            if (up) return 0;
            if (right) return 1;
            if (down) return 2;
            if (left) return 3;
            return -1; // centered
        }

        // ─────────────────────────────────────────────
        //  Static helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks if the vJoy driver is installed and enabled.
        /// Gracefully returns false if vJoyInterface.dll is not found.
        /// </summary>
        public static bool CheckVJoyInstalled()
        {
            try
            {
                EnsureDllLoaded();
                return VJoyNative.vJoyEnabled();
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the next available vJoy device ID (1–16), or 0 if none available.
        /// This is a fast, non-blocking scan — safe to call from the engine thread.
        /// Device creation must be done beforehand via <see cref="EnsureDeviceAvailable"/>.
        /// </summary>
        public static uint FindFreeDeviceId()
        {
            try
            {
                EnsureDllLoaded();
                if (!_dllLoaded) return 0;

                for (uint id = 1; id <= 16; id++)
                {
                    var status = VJoyNative.GetVJDStatus(id);
                    if (status == VjdStat.VJD_STAT_FREE)
                        return id;
                }
            }
            catch (DllNotFoundException ex)
            {
                Debug.WriteLine($"[vJoy] DllNotFoundException in FindFreeDeviceId: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] Exception in FindFreeDeviceId: {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// Checks whether the vjoy service is stuck in STOP_PENDING (zombie state).
        /// This happens when a previous uninstall failed to remove device nodes
        /// before stopping the service, and Windows Fast Startup preserved the
        /// broken kernel state across shutdowns. Only a full restart clears it.
        /// </summary>
        public static bool IsServiceStuck()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "query vjoy",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5_000);
                return output.Contains("STOP_PENDING");
            }
            catch { return false; }
        }

        /// <summary>
        /// Ensures at least one vJoy device is configured and ready.
        /// Must be called from the UI thread (shows UAC prompt if needed).
        /// Creates a device node via SetupAPI if none exist, then waits
        /// for PnP to bind the driver so FindFreeDeviceId can find it.
        /// Returns true if a free device is available.
        /// </summary>
        public static bool EnsureDeviceAvailable()
        {
            // Fast check: already have a free device?
            EnsureDllLoaded();
            if (_dllLoaded && FindFreeDeviceId() > 0)
                return true;

            // No free devices — create one via SetupAPI (needs UAC).
            Debug.WriteLine($"[vJoy] No free device found (dllLoaded={_dllLoaded}). Creating device 1...");
            if (!CreateVJoyDevice(1))
            {
                Debug.WriteLine("[vJoy] CreateVJoyDevice(1) failed (UAC cancelled or SetupAPI error)");
                return false;
            }

            // Wait for PnP to bind the driver to the new device node.
            // Reset _dllLoaded so we retry loading (driver may start now).
            _dllLoaded = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(250);
                EnsureDllLoaded();
                if (_dllLoaded && FindFreeDeviceId() > 0)
                {
                    Debug.WriteLine($"[vJoy] Device ready after {(attempt + 1) * 250}ms");
                    return true;
                }
            }

            Debug.WriteLine("[vJoy] Device created but not ready after 5 seconds");
            return false;
        }

        /// <summary>
        /// Removes a vJoy device node via pnputil so it no longer appears
        /// in Windows game controllers. Runs elevated in a hidden cmd window.
        /// </summary>
        internal static bool DeleteVJoyDevice(uint deviceId)
        {
            // vJoy device nodes are ROOT\HIDCLASS\NNNN (0-based, deviceId is 1-based).
            // Scan ROOT\HIDCLASS\0000–0015 and remove any that exist.
            // In practice there's usually just one per active device.
            try
            {
                string instanceId = $"ROOT\\HIDCLASS\\{(deviceId - 1):D4}";
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c pnputil /remove-device \"{instanceId}\" /subtree >nul 2>&1",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Debug.WriteLine($"[vJoy] Deleting device node {instanceId}...");
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(10_000);
                Debug.WriteLine($"[vJoy] pnputil remove-device exit code: {proc.ExitCode}");
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Debug.WriteLine("[vJoy] UAC cancelled by user");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] DeleteVJoyDevice exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates a vJoy device node using SetupAPI (same approach as devcon.exe).
        /// Sequence: SetupDiCreateDeviceInfoList → SetupDiCreateDeviceInfoW (class name,
        /// DICD_GENERATE_ID) → SetupDiSetDeviceRegistryPropertyW (SPDRP_HARDWAREID) →
        /// SetupDiCallClassInstaller (DIF_REGISTERDEVICE) → pnputil /scan-devices.
        /// Runs the SetupAPI calls via an elevated PowerShell script (single UAC prompt).
        /// </summary>
        internal static bool CreateVJoyDevice(uint deviceId)
        {
            try
            {
                // Build a self-contained PowerShell script that uses SetupAPI P/Invoke.
                string scriptPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_create_device.ps1");
                string logPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_create_device.log");

                string vjoyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");

                File.WriteAllText(scriptPath, $@"
$ErrorActionPreference = 'Continue'
$log = '{logPath.Replace("'", "''")}'
try {{
    # Ensure the vjoy service registry key exists (PnP can't create it when
    # a zombie STOP_PENDING ghost service blocks CreateService in the SCM).
    $svcPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\vjoy'
    if (-not (Test-Path $svcPath)) {{
        New-Item -Path $svcPath -Force | Out-Null
        Set-ItemProperty $svcPath -Name 'Type' -Value 1 -Type DWord
        Set-ItemProperty $svcPath -Name 'Start' -Value 3 -Type DWord
        Set-ItemProperty $svcPath -Name 'ErrorControl' -Value 0 -Type DWord
        Set-ItemProperty $svcPath -Name 'ImagePath' -Value 'System32\DRIVERS\vjoy.sys' -Type ExpandString
    }}
    # Ensure vjoy.sys is in the drivers folder.
    $src = '{vjoyDir.Replace("'", "''")}\vjoy.sys'
    $dst = ""$env:SystemRoot\System32\drivers\vjoy.sys""
    if (-not (Test-Path $dst) -and (Test-Path $src)) {{ Copy-Item $src $dst -Force }}

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PF_SetupApi {{
    public const int DIF_REGISTERDEVICE = 0x19;
    public const int SPDRP_HARDWAREID = 0x01;
    public const int DICD_GENERATE_ID = 0x01;
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA {{ public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }}
    [DllImport(""setupapi.dll"", SetLastError = true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);
    [DllImport(""setupapi.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName, ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport(""setupapi.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, int Property, byte[] PropertyBuffer, int PropertyBufferSize);
    [DllImport(""setupapi.dll"", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport(""setupapi.dll"", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
    [DllImport(""newdev.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string HardwareId, string FullInfPath, int InstallFlags, out bool bRebootRequired);
}}
'@
    $hidGuid = [Guid]::new('{{745a17a0-74d3-11d0-b6fe-00a0c90f57da}}')
    $hwid = 'root\VID_1234&PID_BEAD&REV_0222'
    $infPath = '{vjoyDir.Replace("'", "''")}\vjoy.inf'
    $hwidBytes = [System.Text.Encoding]::Unicode.GetBytes($hwid + [char]0 + [char]0)
    $dis = [PF_SetupApi]::SetupDiCreateDeviceInfoList([ref]$hidGuid, [IntPtr]::Zero)
    if ($dis -eq [IntPtr]::new(-1)) {{ 'FAIL: SetupDiCreateDeviceInfoList' | Out-File $log -Force; exit 1 }}
    $did = New-Object PF_SetupApi+SP_DEVINFO_DATA
    $did.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][PF_SetupApi+SP_DEVINFO_DATA])
    $ok = [PF_SetupApi]::SetupDiCreateDeviceInfoW($dis, 'HIDClass', [ref]$hidGuid, 'vJoy Device', [IntPtr]::Zero, [PF_SetupApi]::DICD_GENERATE_ID, [ref]$did)
    if (-not $ok) {{ $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error(); ""FAIL: CreateDeviceInfo err=$e"" | Out-File $log -Force; [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; exit 1 }}
    $ok = [PF_SetupApi]::SetupDiSetDeviceRegistryPropertyW($dis, [ref]$did, [PF_SetupApi]::SPDRP_HARDWAREID, $hwidBytes, $hwidBytes.Length)
    if (-not $ok) {{ $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error(); ""FAIL: SetHardwareID err=$e"" | Out-File $log -Force; [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; exit 1 }}
    $ok = [PF_SetupApi]::SetupDiCallClassInstaller([PF_SetupApi]::DIF_REGISTERDEVICE, $dis, [ref]$did)
    if (-not $ok) {{ $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error(); ""FAIL: RegisterDevice err=$e"" | Out-File $log -Force; [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; exit 1 }}
    [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null
    # Install the driver on the new device node (creates the service and starts the driver).
    # pnputil /scan-devices only matches but doesn't install — UpdateDriverForPlugAndPlayDevices does both.
    $reboot = $false
    $ok = [PF_SetupApi]::UpdateDriverForPlugAndPlayDevicesW([IntPtr]::Zero, $hwid, $infPath, 1, [ref]$reboot)
    if (-not $ok) {{ $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error(); ""FAIL: UpdateDriver err=$e"" | Out-File $log -Force; exit 1 }}
    'OK' | Out-File $log -Force
}} catch {{
    ""EXCEPTION: $_"" | Out-File $log -Force
    exit 1
}}
");
                // Delete stale log.
                try { File.Delete(logPath); } catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Debug.WriteLine("[vJoy] Creating device node via SetupAPI (elevated PowerShell)...");
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(30_000);

                // Check result from log file.
                string result = File.Exists(logPath) ? File.ReadAllText(logPath).Trim() : "NO_LOG";
                Debug.WriteLine($"[vJoy] CreateVJoyDevice result: {result} (exit code: {proc.ExitCode})");

                try { File.Delete(scriptPath); } catch { }
                try { File.Delete(logPath); } catch { }

                return result.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Debug.WriteLine("[vJoy] UAC cancelled by user");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] CreateVJoyDevice exception: {ex.Message}");
                return false;
            }
        }
    }

    // ─────────────────────────────────────────────
    //  vJoy P/Invoke declarations
    // ─────────────────────────────────────────────

    internal enum VjdStat
    {
        VJD_STAT_OWN = 0,
        VJD_STAT_FREE = 1,
        VJD_STAT_BUSY = 2,
        VJD_STAT_MISS = 3,
        VJD_STAT_UNKN = 4
    }

    internal enum HID_USAGES : uint
    {
        HID_USAGE_X = 0x30,
        HID_USAGE_Y = 0x31,
        HID_USAGE_Z = 0x32,
        HID_USAGE_RX = 0x33,
        HID_USAGE_RY = 0x34,
        HID_USAGE_RZ = 0x35,
        HID_USAGE_SL0 = 0x36,
        HID_USAGE_SL1 = 0x37,
    }

    /// <summary>
    /// Direct P/Invoke to vJoyInterface.dll (native C DLL from vJoy SDK).
    /// Only the minimal set of functions needed for virtual controller output.
    /// The DLL is loaded from the vJoy installation directory (must be in PATH
    /// or placed alongside the application).
    /// </summary>
    internal static class VJoyNative
    {
        private const string DLL = "vJoyInterface.dll";

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool vJoyEnabled();

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern VjdStat GetVJDStatus(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AcquireVJD(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        public static extern void RelinquishVJD(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ResetVJD(uint rID);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetAxis(int value, uint rID, HID_USAGES axis);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetBtn([MarshalAs(UnmanagedType.Bool)] bool value, uint rID, byte nBtn);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDiscPov(int value, uint rID, byte nPov);
    }

}
