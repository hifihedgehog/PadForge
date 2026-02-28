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
        }

        /// <summary>
        /// Trims vJoy device nodes to match <paramref name="activeCount"/>.
        /// Removes excess nodes (from the end) so dormant devices don't
        /// appear in Game Controllers. Call after destroying a vJoy VC.
        /// </summary>
        public static void TrimDeviceNodes(int activeCount)
        {
            try
            {
                var instanceIds = EnumerateVJoyInstanceIds();
                int excess = instanceIds.Count - activeCount;
                Debug.WriteLine($"[vJoy] TrimDeviceNodes: active={activeCount}, nodes={instanceIds.Count}, excess={excess}");
                for (int i = instanceIds.Count - 1; i >= 0 && excess > 0; i--, excess--)
                    RemoveDeviceNode(instanceIds[i]);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] TrimDeviceNodes exception: {ex.Message}");
            }
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            if (!_connected) return;

            // Axes: Gamepad thumbsticks are signed short (-32768 to 32767).
            // vJoy axes are 0 to 32767 (default range).
            // Convert: (short + 32768) / 2 → 0..32767
            VJoyNative.SetAxis(ShortToVJoy(gp.ThumbLX), _deviceId, HID_USAGES.HID_USAGE_X);
            VJoyNative.SetAxis(ShortToVJoyInverted(gp.ThumbLY), _deviceId, HID_USAGES.HID_USAGE_Y);
            VJoyNative.SetAxis(ShortToVJoy(gp.ThumbRX), _deviceId, HID_USAGES.HID_USAGE_RX);
            VJoyNative.SetAxis(ShortToVJoyInverted(gp.ThumbRY), _deviceId, HID_USAGES.HID_USAGE_RY);

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

        /// <summary>
        /// Converts signed short to vJoy range with Y-axis inversion.
        /// HID convention: Y-down = max value (32767). Gamepad convention: Y-up = positive.
        /// </summary>
        private static int ShortToVJoyInverted(short value)
        {
            return 32767 - (value + 32768) / 2;
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
        /// Counts existing vJoy device nodes by querying PnP (pnputil).
        /// More reliable than GetVJDStatus which can return stale data.
        /// </summary>
        public static int CountExistingDevices()
        {
            return EnumerateVJoyInstanceIds().Count;
        }

        /// <summary>
        /// Ensures at least <paramref name="requiredCount"/> vJoy device nodes exist.
        /// Creates additional device nodes via SetupAPI (single UAC prompt) if needed.
        /// Returns true if enough free or existing devices are available.
        /// </summary>
        public static bool EnsureDevicesAvailable(int requiredCount = 1)
        {
            // Count actual PnP device nodes (reliable, not stale DLL state).
            int existing = CountExistingDevices();

            Debug.WriteLine($"[vJoy] EnsureDevicesAvailable: required={requiredCount}, existing={existing}");

            if (existing >= requiredCount)
            {
                // Enough nodes exist — ensure DLL is loaded so the engine can use them.
                EnsureDllLoaded();
                return true;
            }

            // Create enough new device nodes to satisfy the requirement.
            int toCreate = requiredCount - existing;
            Debug.WriteLine($"[vJoy] Need to create {toCreate} device node(s)");

            if (!CreateVJoyDevices(toCreate))
            {
                Debug.WriteLine("[vJoy] CreateVJoyDevices failed");
                return false;
            }

            // Wait for PnP to bind the driver to the new device nodes.
            _dllLoaded = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(250);
                EnsureDllLoaded();
                if (_dllLoaded && FindFreeDeviceId() > 0)
                {
                    Debug.WriteLine($"[vJoy] Devices ready after {(attempt + 1) * 250}ms");
                    return true;
                }
            }

            Debug.WriteLine("[vJoy] Devices created but not ready after 5 seconds");
            return false;
        }

        /// <summary>
        /// Enumerates vJoy device instance IDs via pnputil.
        /// Looks for ROOT\HIDCLASS\* devices whose description contains "vJoy".
        /// </summary>
        internal static System.Collections.Generic.List<string> EnumerateVJoyInstanceIds()
        {
            var results = new System.Collections.Generic.List<string>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/enum-devices /class HIDClass",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return results;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5_000);

                // Parse pnputil output: blocks separated by blank lines.
                // Each block has "Instance ID:", "Device Description:", etc.
                string currentInstanceId = null;
                foreach (string rawLine in output.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.IndexOf("Instance ID", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        line.Contains(":"))
                    {
                        currentInstanceId = line.Substring(line.IndexOf(':') + 1).Trim();
                    }
                    else if (currentInstanceId != null &&
                             line.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) >= 0 &&
                             currentInstanceId.StartsWith("ROOT\\HIDCLASS\\", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(currentInstanceId);
                        currentInstanceId = null;
                    }
                    else if (string.IsNullOrEmpty(line))
                    {
                        currentInstanceId = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] EnumerateVJoyInstanceIds exception: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Removes a single device node by instance ID via pnputil.
        /// App must be running elevated (which it is when vJoy is installed).
        /// </summary>
        internal static bool RemoveDeviceNode(string instanceId)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/remove-device \"{instanceId}\" /subtree",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                Debug.WriteLine($"[vJoy] Removing device node: {instanceId}");
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(5_000);
                Debug.WriteLine($"[vJoy] pnputil exit code: {proc.ExitCode}");
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] RemoveDeviceNode exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes ALL vJoy device nodes via direct pnputil calls.
        /// App must be running elevated (auto-elevation happens at startup when vJoy is installed).
        /// No PowerShell, no UAC prompt — fast and synchronous.
        /// </summary>
        internal static bool RemoveAllDeviceNodes()
        {
            try
            {
                var instanceIds = EnumerateVJoyInstanceIds();
                Debug.WriteLine($"[vJoy] RemoveAllDeviceNodes: found {instanceIds.Count} device(s)");

                int removed = 0;
                foreach (var id in instanceIds)
                {
                    if (RemoveDeviceNode(id))
                        removed++;
                }

                Debug.WriteLine($"[vJoy] Removed {removed}/{instanceIds.Count} device node(s)");
                // Reset DLL loaded state so vJoyEnabled() is re-evaluated after node removal.
                _dllLoaded = false;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] RemoveAllDeviceNodes exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates one or more vJoy device nodes using SetupAPI in a single
        /// elevated PowerShell script (one UAC prompt for the whole batch).
        /// Each node gets DICD_GENERATE_ID so Windows picks a unique instance ID.
        /// </summary>
        internal static bool CreateVJoyDevices(int count = 1)
        {
            if (count < 1) return true;
            try
            {
                string scriptPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_create_device.ps1");
                string logPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_create_device.log");

                string vjoyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");

                File.WriteAllText(scriptPath, $@"
$ErrorActionPreference = 'Continue'
$log = '{logPath.Replace("'", "''")}'
try {{
    $svcPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\vjoy'
    if (-not (Test-Path $svcPath)) {{
        New-Item -Path $svcPath -Force | Out-Null
        Set-ItemProperty $svcPath -Name 'Type' -Value 1 -Type DWord
        Set-ItemProperty $svcPath -Name 'Start' -Value 3 -Type DWord
        Set-ItemProperty $svcPath -Name 'ErrorControl' -Value 0 -Type DWord
        Set-ItemProperty $svcPath -Name 'ImagePath' -Value 'System32\DRIVERS\vjoy.sys' -Type ExpandString
    }}
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
    $created = 0
    for ($i = 0; $i -lt {count}; $i++) {{
        $dis = [PF_SetupApi]::SetupDiCreateDeviceInfoList([ref]$hidGuid, [IntPtr]::Zero)
        if ($dis -eq [IntPtr]::new(-1)) {{ continue }}
        $did = New-Object PF_SetupApi+SP_DEVINFO_DATA
        $did.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][PF_SetupApi+SP_DEVINFO_DATA])
        $ok = [PF_SetupApi]::SetupDiCreateDeviceInfoW($dis, 'HIDClass', [ref]$hidGuid, 'vJoy Device', [IntPtr]::Zero, [PF_SetupApi]::DICD_GENERATE_ID, [ref]$did)
        if (-not $ok) {{ [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; continue }}
        $ok = [PF_SetupApi]::SetupDiSetDeviceRegistryPropertyW($dis, [ref]$did, [PF_SetupApi]::SPDRP_HARDWAREID, $hwidBytes, $hwidBytes.Length)
        if (-not $ok) {{ [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; continue }}
        $ok = [PF_SetupApi]::SetupDiCallClassInstaller([PF_SetupApi]::DIF_REGISTERDEVICE, $dis, [ref]$did)
        if ($ok) {{ $created++ }}
        [PF_SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null
    }}
    if ($created -eq 0) {{ 'FAIL: No devices created' | Out-File $log -Force; exit 1 }}
    # Bind the driver to all unmatched device nodes at once.
    $reboot = $false
    $ok = [PF_SetupApi]::UpdateDriverForPlugAndPlayDevicesW([IntPtr]::Zero, $hwid, $infPath, 1, [ref]$reboot)
    if (-not $ok) {{ $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error(); ""FAIL: UpdateDriver err=$e (created=$created)"" | Out-File $log -Force; exit 1 }}
    ""OK:$created"" | Out-File $log -Force
}} catch {{
    ""EXCEPTION: $_"" | Out-File $log -Force
    exit 1
}}
");
                try { File.Delete(logPath); } catch { }

                // App runs elevated when vJoy is installed (auto-elevation in App.xaml.cs),
                // so no Verb="runas" needed. Use redirected output for better control.
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                Debug.WriteLine($"[vJoy] Creating {count} device node(s) via SetupAPI (PowerShell)...");
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(30_000);

                string result = File.Exists(logPath) ? File.ReadAllText(logPath).Trim() : "NO_LOG";
                Debug.WriteLine($"[vJoy] CreateVJoyDevices result: {result} (exit code: {proc.ExitCode})");

                try { File.Delete(scriptPath); } catch { }
                try { File.Delete(logPath); } catch { }

                return result.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] CreateVJoyDevices exception: {ex.Message}");
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
