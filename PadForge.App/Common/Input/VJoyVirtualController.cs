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

            // Build the entire joystick state in a single struct, then send
            // one IOCTL via UpdateVJD instead of 18+ individual SetAxis/SetBtn calls.
            // This is critical for multi-controller performance — each individual call
            // is a separate kernel roundtrip (~1-2ms), causing catastrophic polling
            // rate drops (1000Hz → 11Hz with 2 controllers, 5Hz with 3).
            var pos = new VJoyNative.JoystickPosition { bDevice = (byte)_deviceId };

            // Axes: signed short (-32768..32767) → vJoy range (0..32767)
            pos.wAxisX  = (gp.ThumbLX + 32768) / 2;
            pos.wAxisY  = 32767 - (gp.ThumbLY + 32768) / 2;  // Y inverted (HID Y-down = max)
            pos.wAxisXRot = (gp.ThumbRX + 32768) / 2;
            pos.wAxisYRot = 32767 - (gp.ThumbRY + 32768) / 2; // Y inverted
            pos.wAxisZ  = gp.LeftTrigger * 32767 / 255;
            pos.wAxisZRot = gp.RightTrigger * 32767 / 255;

            // Buttons: build bitmask (bit 0 = button 1, bit 10 = button 11)
            int buttons = 0;
            if (gp.IsButtonPressed(Gamepad.A))              buttons |= 1 << 0;
            if (gp.IsButtonPressed(Gamepad.B))              buttons |= 1 << 1;
            if (gp.IsButtonPressed(Gamepad.X))              buttons |= 1 << 2;
            if (gp.IsButtonPressed(Gamepad.Y))              buttons |= 1 << 3;
            if (gp.IsButtonPressed(Gamepad.LEFT_SHOULDER))  buttons |= 1 << 4;
            if (gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER)) buttons |= 1 << 5;
            if (gp.IsButtonPressed(Gamepad.BACK))           buttons |= 1 << 6;
            if (gp.IsButtonPressed(Gamepad.START))          buttons |= 1 << 7;
            if (gp.IsButtonPressed(Gamepad.LEFT_THUMB))     buttons |= 1 << 8;
            if (gp.IsButtonPressed(Gamepad.RIGHT_THUMB))    buttons |= 1 << 9;
            if (gp.IsButtonPressed(Gamepad.GUIDE))          buttons |= 1 << 10;
            pos.lButtons = buttons;

            // D-Pad → discrete POV hat packed in lower nibble of bHats.
            // Each nibble = one POV (0=Up, 1=Right, 2=Down, 3=Left, 0xF=centered).
            // All 4 POVs in bHats start as 0xFFFFFFFF (all centered), then set POV 1.
            bool up    = (gp.Buttons & Gamepad.DPAD_UP) != 0;
            bool right = (gp.Buttons & Gamepad.DPAD_RIGHT) != 0;
            bool down  = (gp.Buttons & Gamepad.DPAD_DOWN) != 0;
            bool left  = (gp.Buttons & Gamepad.DPAD_LEFT) != 0;
            uint povNibble = up ? 0u : right ? 1u : down ? 2u : left ? 3u : 0x0Fu;
            pos.bHats = 0xFFFFFFF0u | povNibble;
            pos.bHatsEx1 = 0xFFFFFFFF;
            pos.bHatsEx2 = 0xFFFFFFFF;
            pos.bHatsEx3 = 0xFFFFFFFF;

            if (!VJoyNative.UpdateVJD(_deviceId, ref pos))
            {
                // UpdateVJD failed — device may have been released or struct mismatch.
                // Re-check status; if device is no longer ours, mark disconnected.
                var status = VJoyNative.GetVJDStatus(_deviceId);
                if (status != VjdStat.VJD_STAT_OWN)
                    _connected = false;
            }
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

            // Always write device config (idempotent) so existing devices
            // pick up the gamepad layout (13 buttons, 1 POV, 6 axes).
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
        /// Configuration: 6 axes (X/Y/Z/RX/RY/RZ), 13 buttons, 1 discrete POV.
        /// </summary>
        internal static void WriteDeviceConfiguration(int count)
        {
            if (count < 1) return;
            try
            {
                // HID Report Descriptor for gamepad: 6 axes (32-bit each), 13 buttons, 1 discrete POV.
                // Built per USB HID 1.11 spec. The vJoy driver parses this to determine
                // which JOYSTICK_POSITION_V3 fields to include in the HID report.
                byte[] descriptor = BuildGamepadHidDescriptor();

                using var baseKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\services\vjoy\Parameters");

                for (int id = 1; id <= count; id++)
                {
                    string subKeyName = $"Device{id:D2}"; // Device01, Device02, ...
                    using var devKey = baseKey.CreateSubKey(subKeyName);
                    devKey.SetValue("HidReportDescriptor", descriptor, Microsoft.Win32.RegistryValueKind.Binary);
                    devKey.SetValue("HidReportDescriptorSize", descriptor.Length, Microsoft.Win32.RegistryValueKind.DWord);
                }

                Debug.WriteLine($"[vJoy] Wrote HID descriptor ({descriptor.Length} bytes) for {count} device(s)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] WriteDeviceConfiguration exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a HID Report Descriptor for a gamepad-like vJoy device:
        /// 6 axes (X, Y, Z, RX, RY, RZ), 13 buttons, 1 discrete POV hat.
        /// All axes are 32-bit with 0–32767 range (matching JOYSTICK_POSITION_V3).
        /// </summary>
        private static byte[] BuildGamepadHidDescriptor()
        {
            var d = new System.Collections.Generic.List<byte>();

            // ── Collection: Generic Desktop / Joystick ──
            d.AddRange(new byte[] { 0x05, 0x01 });       // USAGE_PAGE (Generic Desktop)
            d.AddRange(new byte[] { 0x09, 0x04 });       // USAGE (Joystick)
            d.AddRange(new byte[] { 0xA1, 0x01 });       // COLLECTION (Application)
            d.AddRange(new byte[] { 0x85, 0x01 });       //   REPORT_ID (1)

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

            // ── 13 Buttons ──
            d.AddRange(new byte[] { 0x05, 0x09 });       //   USAGE_PAGE (Button)
            d.AddRange(new byte[] { 0x19, 0x01 });       //   USAGE_MINIMUM (1)
            d.AddRange(new byte[] { 0x29, 0x0D });       //   USAGE_MAXIMUM (13)
            d.AddRange(new byte[] { 0x15, 0x00 });       //   LOGICAL_MINIMUM (0)
            d.AddRange(new byte[] { 0x25, 0x01 });       //   LOGICAL_MAXIMUM (1)
            d.AddRange(new byte[] { 0x75, 0x01 });       //   REPORT_SIZE (1)
            d.AddRange(new byte[] { 0x95, 0x0D });       //   REPORT_COUNT (13)
            d.AddRange(new byte[] { 0x81, 0x02 });       //   INPUT (Data, Var, Abs)
            // Padding to byte boundary (3 bits → 16 bits total)
            d.AddRange(new byte[] { 0x75, 0x01 });       //   REPORT_SIZE (1)
            d.AddRange(new byte[] { 0x95, 0x03 });       //   REPORT_COUNT (3)
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
    /// Uses UpdateVJD (batch API) for output — sends the entire joystick
    /// state in a single IOCTL instead of per-axis/per-button calls.
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

        /// <summary>
        /// Sends the entire joystick state to the vJoy driver in a single IOCTL.
        /// This is ~18x faster than individual SetAxis/SetBtn/SetDiscPov calls,
        /// each of which makes a separate kernel roundtrip.
        /// </summary>
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateVJD(uint rID, ref JoystickPosition pData);

        /// <summary>
        /// Maps to JOYSTICK_POSITION_V3 from vJoy SDK (public.h, API version 3).
        /// The SDK #defines USE_JOYSTICK_API_VERSION 3, which typedefs
        /// JOYSTICK_POSITION_V3 as JOYSTICK_POSITION. V3 adds 4 axes
        /// (Accelerator/Brake/Clutch/Steering) after wWheel and moves
        /// VZ/VBRX/VBRY/VBRZ to the end of the struct.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 124)]
        public struct JoystickPosition
        {
            [FieldOffset(0)]   public byte bDevice;      // 1-based device index
            [FieldOffset(4)]   public int wThrottle;
            [FieldOffset(8)]   public int wRudder;
            [FieldOffset(12)]  public int wAileron;
            [FieldOffset(16)]  public int wAxisX;        // Left stick X
            [FieldOffset(20)]  public int wAxisY;        // Left stick Y
            [FieldOffset(24)]  public int wAxisZ;        // Left trigger
            [FieldOffset(28)]  public int wAxisXRot;     // Right stick X
            [FieldOffset(32)]  public int wAxisYRot;     // Right stick Y
            [FieldOffset(36)]  public int wAxisZRot;     // Right trigger
            [FieldOffset(40)]  public int wSlider;
            [FieldOffset(44)]  public int wDial;
            [FieldOffset(48)]  public int wWheel;
            [FieldOffset(52)]  public int wAccelerator;  // V3: new axis
            [FieldOffset(56)]  public int wBrake;        // V3: new axis
            [FieldOffset(60)]  public int wClutch;       // V3: new axis
            [FieldOffset(64)]  public int wSteering;     // V3: new axis
            [FieldOffset(68)]  public int wAxisVX;
            [FieldOffset(72)]  public int wAxisVY;
            [FieldOffset(76)]  public int lButtons;      // Buttons 1–32 bitmask
            [FieldOffset(80)]  public uint bHats;        // Discrete POVs (4 per nibble)
            [FieldOffset(84)]  public uint bHatsEx1;
            [FieldOffset(88)]  public uint bHatsEx2;
            [FieldOffset(92)]  public uint bHatsEx3;
            [FieldOffset(96)]  public int lButtonsEx1;   // Buttons 33–64
            [FieldOffset(100)] public int lButtonsEx2;   // Buttons 65–96
            [FieldOffset(104)] public int lButtonsEx3;   // Buttons 97–128
            [FieldOffset(108)] public int wAxisVZ;       // V3: moved to end
            [FieldOffset(112)] public int wAxisVBRX;     // V3: moved to end
            [FieldOffset(116)] public int wAxisVBRY;     // V3: moved to end
            [FieldOffset(120)] public int wAxisVBRZ;     // V3: moved to end
        }
    }

}
