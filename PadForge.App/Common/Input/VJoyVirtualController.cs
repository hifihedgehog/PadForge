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
        /// Tracks how many device nodes were created by THIS session with the correct
        /// HID descriptor. Nodes from previous sessions may have stale descriptors
        /// and need to be removed+recreated so the driver re-reads the registry.
        /// Reset to 0 when all nodes are removed (RemoveAllDeviceNodes / engine stop).
        /// </summary>
        private static int _nodesCreatedThisSession;

        /// <summary>Whether we've already ensured the driver is in the Windows driver store this session.</summary>
        private static bool _driverStoreChecked;

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
            DiagLog($"Connect: deviceId={_deviceId}, status={status}, dllLoaded={_dllLoaded}");

            if (status != VjdStat.VJD_STAT_FREE && status != VjdStat.VJD_STAT_OWN)
                throw new InvalidOperationException($"vJoy device {_deviceId} is not available (status: {status}).");

            if (status == VjdStat.VJD_STAT_FREE)
            {
                bool acquired = VJoyNative.AcquireVJD(_deviceId);
                DiagLog($"AcquireVJD({_deviceId}): {acquired}");
                if (!acquired)
                    throw new InvalidOperationException($"Failed to acquire vJoy device {_deviceId}.");
            }

            VJoyNative.ResetVJD(_deviceId);
            _connected = true;

            // Verify output works: send a single test frame with non-zero axes.
            var testPos = new JoystickPositionV2 { bDevice = (byte)_deviceId, wAxisX = 16383, wAxisY = 16383 };
            testPos.bHats = 0xFFFF_FFFFu;
            testPos.bHatsEx1 = 0xFFFF_FFFFu;
            testPos.bHatsEx2 = 0xFFFF_FFFFu;
            testPos.bHatsEx3 = 0xFFFF_FFFFu;
            bool testOk = VJoyNative.UpdateVJD(_deviceId, ref testPos);
            DiagLog($"Post-connect test UpdateVJD({_deviceId}): {testOk}");
        }

        /// <summary>The vJoy device ID (1–16) this controller was created with.</summary>
        public uint DeviceId => _deviceId;

        public void Disconnect()
        {
            if (_connected)
            {
                DiagLog($"Disconnect: deviceId={_deviceId}, submitCalls={_submitCallCount}, submitFails={_submitFailCount}");
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
                _nodesCreatedThisSession = Math.Min(_nodesCreatedThisSession, activeCount);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] TrimDeviceNodes exception: {ex.Message}");
            }
        }

        private int _submitCallCount;
        private int _submitFailCount;

        private static readonly string _diagLogPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_diag.log");
        private static bool _diagLogCleared;

        internal static void DiagLog(string msg)
        {
            try
            {
                if (!_diagLogCleared)
                {
                    File.WriteAllText(_diagLogPath, "");
                    _diagLogCleared = true;
                }
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
                Debug.Write(line);
                File.AppendAllText(_diagLogPath, line);
            }
            catch { }
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            if (!_connected) return;

            uint id = _deviceId;

            // Axes: signed short (-32768..32767) → vJoy range (0..32767)
            int lx = (gp.ThumbLX + 32768) / 2;
            int ly = 32767 - (gp.ThumbLY + 32768) / 2;   // Y inverted (HID Y-down=max)
            int rx = (gp.ThumbRX + 32768) / 2;
            int ry = 32767 - (gp.ThumbRY + 32768) / 2;   // Y inverted
            int lt = gp.LeftTrigger * 32767 / 255;
            int rt = gp.RightTrigger * 32767 / 255;

            VJoyNative.SetAxis(lx, id, VJoyNative.HID_USAGE_X);
            VJoyNative.SetAxis(ly, id, VJoyNative.HID_USAGE_Y);
            VJoyNative.SetAxis(rx, id, VJoyNative.HID_USAGE_RX);
            VJoyNative.SetAxis(ry, id, VJoyNative.HID_USAGE_RY);
            VJoyNative.SetAxis(lt, id, VJoyNative.HID_USAGE_Z);
            VJoyNative.SetAxis(rt, id, VJoyNative.HID_USAGE_RZ);

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

            _submitCallCount++;

            // Log first call and periodic status (every ~5 seconds at 1000Hz)
            if (_submitCallCount == 1 || _submitCallCount % 5000 == 0)
            {
                DiagLog($"SubmitGamepadState(individual) devId={id} call#{_submitCallCount} X={lx} Y={ly} btns=0x{gp.Buttons:X} pov={pov}");
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
        /// Ensures the vJoy driver INF is in the Windows driver store.
        /// Without it, PnP won't apply UpperFilters=mshidkmdf from the INF
        /// when binding new device nodes — vjoy.sys handles IOCTLs but no
        /// HID reports reach Windows (joy.cpl shows no output).
        /// Safe to call once at session start, before any device nodes exist.
        /// </summary>
        private static void EnsureDriverInStore()
        {
            try
            {
                string vjoyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
                string infPath = Path.Combine(vjoyDir, "vjoy.inf");
                if (!File.Exists(infPath))
                {
                    DiagLog("EnsureDriverInStore: vjoy.inf not found, skipping");
                    return;
                }

                // Check if vjoy is already in the driver store.
                var checkPsi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/enum-drivers",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var checkProc = Process.Start(checkPsi);
                if (checkProc == null) return;
                string output = checkProc.StandardOutput.ReadToEnd();
                checkProc.WaitForExit(5_000);

                if (output.IndexOf("vjoy", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    DiagLog("EnsureDriverInStore: driver already in store");
                    return;
                }

                // Driver not in store — add it.
                DiagLog("EnsureDriverInStore: driver NOT in store, adding...");
                var addPsi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/add-driver \"{infPath}\" /install",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var addProc = Process.Start(addPsi);
                if (addProc == null) return;
                string addOutput = addProc.StandardOutput.ReadToEnd();
                addProc.WaitForExit(10_000);
                DiagLog($"EnsureDriverInStore: pnputil exit={addProc.ExitCode}, output={addOutput.Trim()}");
            }
            catch (Exception ex)
            {
                DiagLog($"EnsureDriverInStore exception: {ex.Message}");
            }
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
            // Ensure the vJoy driver INF is in the Windows driver store (once per session).
            // Without this, PnP won't set UpperFilters=mshidkmdf from the INF when binding
            // new device nodes, and the HID-KMDF bridge won't be created — vjoy.sys handles
            // IOCTLs (AcquireVJD/UpdateVJD succeed) but no HID reports reach Windows.
            if (!_driverStoreChecked)
            {
                _driverStoreChecked = true;
                EnsureDriverInStore();
            }

            // Count actual PnP device nodes (reliable, not stale DLL state).
            int existing = CountExistingDevices();

            DiagLog($"EnsureDevicesAvailable: required={requiredCount}, existing={existing}, createdThisSession={_nodesCreatedThisSession}");

            // Remove any custom DeviceNN registry keys so the driver uses its
            // built-in default HID descriptor. Custom descriptors cause a mismatch
            // between the described report layout and the driver's fixed 97-byte
            // HID_INPUT_REPORT format, which prevents data from reaching WinMM.
            CleanDeviceRegistryKeys();

            if (existing > requiredCount)
            {
                // More nodes than needed (all ours) — remove the extras from the end.
                var instanceIds = EnumerateVJoyInstanceIds();
                for (int i = instanceIds.Count - 1; i >= requiredCount; i--)
                    RemoveDeviceNode(instanceIds[i]);
                existing = requiredCount;
                _nodesCreatedThisSession = Math.Min(_nodesCreatedThisSession, requiredCount);
            }

            if (existing >= requiredCount)
            {
                // All nodes are ours and we have enough — no need to recreate.
                EnsureDllLoaded();
                return true;
            }

            // Create the needed device nodes (additional or all, depending on stale cleanup).
            int toCreate = requiredCount - existing;
            DiagLog($"Creating {toCreate} device node(s) (existing={existing})");

            if (!CreateVJoyDevices(toCreate))
            {
                DiagLog("CreateVJoyDevices FAILED");
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
                    DiagLog($"Devices ready after {(attempt + 1) * 250}ms");
                    _nodesCreatedThisSession = requiredCount;
                    return true;
                }
            }

            DiagLog("Devices created but not ready after 5 seconds");
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
                // Reset state so vJoyEnabled() is re-evaluated and stale detection works.
                _dllLoaded = false;
                _nodesCreatedThisSession = 0;
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
    $infPath = '{vjoyDir.Replace("'", "''")}\vjoy.inf'

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
        /// Removes all DeviceNN registry subkeys from the vJoy Parameters key.
        /// This ensures the driver uses its built-in default HID descriptor, which
        /// matches the fixed 97-byte HID_INPUT_REPORT format. Custom descriptors
        /// cause a layout mismatch (descriptor says 6 axes at specific offsets, but
        /// the driver always sends 16 axes + 4 POV hats + 128 buttons at fixed offsets)
        /// that prevents data from reaching WinMM/DirectInput.
        /// </summary>
        private static void CleanDeviceRegistryKeys()
        {
            try
            {
                using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\services\vjoy\Parameters", writable: true);
                if (baseKey == null) return;

                foreach (string subKeyName in baseKey.GetSubKeyNames())
                {
                    if (subKeyName.StartsWith("Device", StringComparison.OrdinalIgnoreCase))
                    {
                        try { baseKey.DeleteSubKeyTree(subKeyName, false); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[vJoy] CleanDeviceRegistryKeys exception: {ex.Message}");
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

    /// <summary>
    /// JOYSTICK_POSITION_V2 — matches public.h _JOYSTICK_POSITION_V2 struct (108 bytes).
    /// Used by UpdateVJD for single-IOCTL-per-frame output.
    /// Verified working against vJoyInterface.dll v2.2.2 by standalone test tool.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 108)]
    internal struct JoystickPositionV2
    {
        [FieldOffset(0)]  public byte bDevice;       // 1-based device index
        [FieldOffset(4)]  public int wThrottle;
        [FieldOffset(8)]  public int wRudder;
        [FieldOffset(12)] public int wAileron;
        [FieldOffset(16)] public int wAxisX;
        [FieldOffset(20)] public int wAxisY;
        [FieldOffset(24)] public int wAxisZ;
        [FieldOffset(28)] public int wAxisXRot;
        [FieldOffset(32)] public int wAxisYRot;
        [FieldOffset(36)] public int wAxisZRot;
        [FieldOffset(40)] public int wSlider;
        [FieldOffset(44)] public int wDial;
        [FieldOffset(48)] public int wWheel;
        [FieldOffset(52)] public int wAxisVX;
        [FieldOffset(56)] public int wAxisVY;
        [FieldOffset(60)] public int wAxisVZ;
        [FieldOffset(64)] public int wAxisVBRX;
        [FieldOffset(68)] public int wAxisVBRY;
        [FieldOffset(72)] public int wAxisVBRZ;
        [FieldOffset(76)] public int lButtons;       // Buttons 1-32 bitmask
        [FieldOffset(80)] public uint bHats;          // Discrete POV 1 (low nibble)
        [FieldOffset(84)] public uint bHatsEx1;
        [FieldOffset(88)] public uint bHatsEx2;
        [FieldOffset(92)] public uint bHatsEx3;
        [FieldOffset(96)]  public int lButtonsEx1;
        [FieldOffset(100)] public int lButtonsEx2;
        [FieldOffset(104)] public int lButtonsEx3;
    }

    /// <summary>
    /// Direct P/Invoke to vJoyInterface.dll (native C DLL from vJoy SDK).
    /// Uses UpdateVJD for single-IOCTL-per-frame output (fastest path).
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
        public static extern bool UpdateVJD(uint rID, ref JoystickPositionV2 pData);

        // ── Individual axis/button/POV setters ──

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
