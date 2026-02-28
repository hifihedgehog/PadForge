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

        /// <summary>Whether vJoyInterface.dll has been successfully loaded into the process.</summary>
        public static bool IsDllLoaded => _dllLoaded;

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

        public void Dispose()
        {
            Disconnect();
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

            // Use individual SetAxis/SetBtn/SetDiscPov calls.
            // Each call is a separate IOCTL (~0.1-0.2ms each), totaling ~18 calls.
            // This is the proven-working approach (same as test applet).
            uint id = _deviceId;

            // Axes: signed short (-32768..32767) → vJoy range (0..32767)
            VJoyNative.SetAxis((gp.ThumbLX + 32768) / 2, id, VJoyNative.HID_USAGE_X);
            VJoyNative.SetAxis(32767 - (gp.ThumbLY + 32768) / 2, id, VJoyNative.HID_USAGE_Y);  // Y inverted
            VJoyNative.SetAxis((gp.ThumbRX + 32768) / 2, id, VJoyNative.HID_USAGE_RX);
            VJoyNative.SetAxis(32767 - (gp.ThumbRY + 32768) / 2, id, VJoyNative.HID_USAGE_RY); // Y inverted
            VJoyNative.SetAxis(gp.LeftTrigger * 32767 / 255, id, VJoyNative.HID_USAGE_Z);
            VJoyNative.SetAxis(gp.RightTrigger * 32767 / 255, id, VJoyNative.HID_USAGE_RZ);

            // Buttons 1–11 (Xbox 360 layout: A/B/X/Y/LB/RB/Back/Start/LS/RS/Guide)
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.A), id, 1);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.B), id, 2);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.X), id, 3);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.Y), id, 4);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.LEFT_SHOULDER), id, 5);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER), id, 6);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.BACK), id, 7);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.START), id, 8);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.LEFT_THUMB), id, 9);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.RIGHT_THUMB), id, 10);
            VJoyNative.SetBtn(gp.IsButtonPressed(Gamepad.GUIDE), id, 11);

            // D-Pad → discrete POV hat.
            // vJoy discrete POV values: 0=Up, 1=Right, 2=Down, 3=Left, -1=centered.
            bool up    = (gp.Buttons & Gamepad.DPAD_UP) != 0;
            bool right = (gp.Buttons & Gamepad.DPAD_RIGHT) != 0;
            bool down  = (gp.Buttons & Gamepad.DPAD_DOWN) != 0;
            bool left  = (gp.Buttons & Gamepad.DPAD_LEFT) != 0;
            int pov = up ? 0 : right ? 1 : down ? 2 : left ? 3 : -1;
            VJoyNative.SetDiscPov(pov, id, 1);
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            // vJoy has no rumble/force feedback callback — no-op.
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
        /// Creates additional device nodes via SetupAPI if needed.
        /// Called by the engine thread (CreateVJoyController) on demand — device nodes
        /// only exist while virtual controllers are actively connected (like ViGEm).
        /// Returns true if enough free or existing devices are available.
        /// </summary>
        public static bool EnsureDevicesAvailable(int requiredCount = 1)
        {
            // Count actual PnP device nodes (reliable, not stale DLL state).
            int existing = CountExistingDevices();

            Debug.WriteLine($"[vJoy] EnsureDevicesAvailable: required={requiredCount}, existing={existing}");

            // Always write device config (idempotent) so existing devices
            // pick up the Xbox 360 gamepad layout (11 buttons, 1 POV, 6 axes).
            WriteDeviceConfiguration(Math.Max(requiredCount, existing));

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

        /// <summary>
        /// Writes vJoy device configuration to the registry for device IDs 1 through count.
        /// The vJoy driver reads HidReportDescriptor from
        /// HKLM\SYSTEM\CurrentControlSet\services\vjoy\Parameters\DeviceNN
        /// to determine the device's axis/button/POV layout.
        /// Must be called BEFORE the driver binds to new device nodes.
        ///
        /// Configuration: 6 axes (X/Y/Z/RX/RY/RZ), 11 buttons, 1 discrete POV.
        /// </summary>
        internal static void WriteDeviceConfiguration(int count)
        {
            if (count < 1) return;
            try
            {
                using var baseKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\services\vjoy\Parameters");

                for (int id = 1; id <= count; id++)
                {
                    // Each device gets a unique Report ID matching its 1-based ID.
                    // The vJoy driver's GetReportDescriptorFromRegistry() concatenates
                    // all DeviceNN descriptors and uses ParseIdInDescriptor() to extract
                    // the Report ID from the 0x85 tag in each descriptor.
                    byte[] descriptor = BuildGamepadHidDescriptor((byte)id);
                    string subKeyName = $"Device{id:D2}"; // Device01, Device02, ...
                    using var devKey = baseKey.CreateSubKey(subKeyName);
                    // NOTE: The vJoy driver source (vjoy.h) uses MISSPELLED registry key names:
                    //   DESC_NAME = L"HidReportDesctiptor"  (not "Descriptor")
                    //   DESC_SIZE = L"HidReportDesctiptorSize"
                    // We MUST match the driver's typo, or it falls back to hardcoded defaults.
                    devKey.SetValue("HidReportDesctiptor", descriptor, Microsoft.Win32.RegistryValueKind.Binary);
                    devKey.SetValue("HidReportDesctiptorSize", descriptor.Length, Microsoft.Win32.RegistryValueKind.DWord);
                    // Clean up stale correctly-spelled keys from older versions.
                    try { devKey.DeleteValue("HidReportDescriptor", false); } catch { }
                    try { devKey.DeleteValue("HidReportDescriptorSize", false); } catch { }
                }

                Debug.WriteLine($"[vJoy] Wrote HID descriptors for {count} device(s)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] WriteDeviceConfiguration exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a HID Report Descriptor for a gamepad-like vJoy device:
        /// 6 axes (X, Y, Z, RX, RY, RZ), 11 buttons, 1 discrete POV hat.
        /// Matches Xbox 360 controller layout. All axes are 32-bit with
        /// 0–32767 range (matching JOYSTICK_POSITION_V3).
        /// </summary>
        /// <param name="reportId">Report ID (1–16) — must match the vJoy device ID.
        /// The driver uses ParseIdInDescriptor() to extract this from the 0x85 tag.</param>
        private static byte[] BuildGamepadHidDescriptor(byte reportId)
        {
            var d = new System.Collections.Generic.List<byte>();

            // ── Collection: Generic Desktop / Joystick ──
            d.AddRange(new byte[] { 0x05, 0x01 });             // USAGE_PAGE (Generic Desktop)
            d.AddRange(new byte[] { 0x09, 0x04 });             // USAGE (Joystick)
            d.AddRange(new byte[] { 0xA1, 0x01 });             // COLLECTION (Application)
            d.AddRange(new byte[] { 0x85, reportId });          //   REPORT_ID (matches device ID)

            // ── 6 Axes: X, Y, Z, RX, RY, RZ — each 32-bit, range 0–32767 ──
            d.AddRange(new byte[] { 0x15, 0x00 });       //   LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x26, 0xFF, 0x7F }); //   LOGICAL_MAXIMUM (32767)
            d.AddRange(new byte[] { 0x75, 0x20 });       //   REPORT_SIZE (32)
            d.AddRange(new byte[] { 0x95, 0x01 });       //   REPORT_COUNT (1)
            // Each axis declared individually so the driver maps by Usage code.
            foreach (byte usage in new byte[] { 0x30, 0x31, 0x32, 0x33, 0x34, 0x35 })
            {
                d.AddRange(new byte[] { 0x09, usage }); //   USAGE (X/Y/Z/RX/RY/RZ)
                d.AddRange(new byte[] { 0x81, 0x02 });  //   INPUT (Data, Var, Abs)
            }

            // ── 11 Buttons (Xbox 360: A/B/X/Y/LB/RB/Back/Start/LS/RS/Guide) ──
            d.AddRange(new byte[] { 0x05, 0x09 });       //   USAGE_PAGE (Button)
            d.AddRange(new byte[] { 0x19, 0x01 });       //   USAGE_MINIMUM (1)
            d.AddRange(new byte[] { 0x29, 0x0B });       //   USAGE_MAXIMUM (11)
            d.AddRange(new byte[] { 0x15, 0x00 });       //   LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x25, 0x01 });       //   LOGICAL_MAXIMUM (1)
            d.AddRange(new byte[] { 0x75, 0x01 });       //   REPORT_SIZE (1)
            d.AddRange(new byte[] { 0x95, 0x0B });       //   REPORT_COUNT (11)
            d.AddRange(new byte[] { 0x81, 0x02 });       //   INPUT (Data, Var, Abs)
            // Padding to byte boundary (5 bits → 16 bits total)
            d.AddRange(new byte[] { 0x75, 0x01 });       //   REPORT_SIZE (1)
            d.AddRange(new byte[] { 0x95, 0x05 });       //   REPORT_COUNT (5)
            d.AddRange(new byte[] { 0x81, 0x01 });       //   INPUT (Cnst, Ary, Abs)

            // ── 1 Discrete POV (4-direction hat switch) ──
            d.AddRange(new byte[] { 0x05, 0x01 });       //   USAGE_PAGE (Generic Desktop)
            d.AddRange(new byte[] { 0x09, 0x39 });       //   USAGE (Hat Switch)
            d.AddRange(new byte[] { 0x15, 0x00 });       //   LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x25, 0x03 });       //   LOGICAL_MAXIMUM (3)
            d.AddRange(new byte[] { 0x35, 0x00 });       //   PHYSICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x46, 0x0E, 0x01 }); //   PHYSICAL_MAXIMUM (270)
            d.AddRange(new byte[] { 0x65, 0x14 });       //   UNIT (Eng Rotation: degrees)
            d.AddRange(new byte[] { 0x75, 0x04 });       //   REPORT_SIZE (4)
            d.AddRange(new byte[] { 0x95, 0x01 });       //   REPORT_COUNT (1)
            d.AddRange(new byte[] { 0x81, 0x42 });       //   INPUT (Data, Var, Abs, Null)
            // Padding (4 bits)
            d.AddRange(new byte[] { 0x75, 0x04 });       //   REPORT_SIZE (4)
            d.AddRange(new byte[] { 0x95, 0x01 });       //   REPORT_COUNT (1)
            d.AddRange(new byte[] { 0x81, 0x01 });       //   INPUT (Cnst)
            // Reset unit
            d.AddRange(new byte[] { 0x65, 0x00 });       //   UNIT (None)
            d.AddRange(new byte[] { 0x35, 0x00 });       //   PHYSICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x45, 0x00 });       //   PHYSICAL_MAXIMUM (0)

            d.Add(0xC0);                                  // END_COLLECTION

            return d.ToArray();
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

    /// <summary>
    /// Direct P/Invoke to vJoyInterface.dll (native C DLL from vJoy SDK).
    /// Uses individual SetAxis/SetBtn/SetDiscPov calls for output.
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

        // ── Individual axis/button/POV setters (proven working in test applet) ──

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetAxis(int value, uint rID, uint axis);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetBtn([MarshalAs(UnmanagedType.Bool)] bool value, uint rID, byte nBtn);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDiscPov(int value, uint rID, byte nPov);

        // HID Usage IDs for axes (Generic Desktop page 0x01)
        public const uint HID_USAGE_X  = 0x30;
        public const uint HID_USAGE_Y  = 0x31;
        public const uint HID_USAGE_Z  = 0x32;
        public const uint HID_USAGE_RX = 0x33;
        public const uint HID_USAGE_RY = 0x34;
        public const uint HID_USAGE_RZ = 0x35;
    }

}
