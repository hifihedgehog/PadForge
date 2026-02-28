using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Nefarius.ViGEm.Client;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Step 5: UpdateVirtualDevices
        //  Feeds combined Gamepad states to ViGEmBus virtual controllers
        //  (Xbox 360 or DualShock 4) via the IVirtualController abstraction.
        // ─────────────────────────────────────────────

        /// <summary>Shared ViGEmClient instance (one per process).</summary>
        private static ViGEmClient _vigemClient;
        private static readonly object _vigemClientLock = new object();
        private static bool _vigemClientFailed;

        /// <summary>Virtual controller targets (one per slot).</summary>
        private IVirtualController[] _virtualControllers = new IVirtualController[MaxPads];

        /// <summary>
        /// Configured virtual controller type per slot. The UI writes to this
        /// array via InputService at 30Hz. Step 5 reads it at ~1000Hz to detect
        /// type changes and recreate controllers accordingly.
        /// </summary>
        public VirtualControllerType[] SlotControllerTypes { get; } = new VirtualControllerType[MaxPads];

        /// <summary>
        /// Count of currently active ViGEm virtual controllers.
        /// Used by IsViGEmVirtualDevice() in Step 1 for zero-VID/PID heuristic.
        /// </summary>
        private int _activeVigemCount;

        /// <summary>
        /// Count of currently active ViGEm Xbox 360 virtual controllers.
        /// Used by IsViGEmVirtualDevice() to filter the correct number of 045E:028E devices.
        /// </summary>
        private int _activeXbox360Count;

        /// <summary>
        /// Count of currently active ViGEm DS4 virtual controllers.
        /// Used by IsViGEmVirtualDevice() to filter the correct number of 054C:05C4 devices.
        /// </summary>
        private int _activeDs4Count;

        /// <summary>
        /// Expected ViGEm Xbox 360 / DS4 virtual controller counts, pre-initialized
        /// from slot configuration BEFORE the polling loop starts. Used by
        /// IsViGEmVirtualDevice() to filter ViGEm devices on the very first
        /// UpdateDevices() call — before Step 5 has created any actual VCs.
        /// Without this, _activeXbox360Count is 0 on the first cycle, causing
        /// all stale ViGEm 045E:028E devices to pass through the filter as
        /// "real" Xbox controllers.
        /// </summary>
        private int _expectedXbox360Count;
        private int _expectedDs4Count;

        /// <summary>
        /// Pre-initializes expected ViGEm counts from the slot configuration.
        /// Must be called before Start() so the first UpdateDevices() cycle
        /// filters ViGEm devices correctly.
        /// </summary>
        public void PreInitializeVigemCounts(int xbox360Count, int ds4Count)
        {
            _expectedXbox360Count = xbox360Count;
            _expectedDs4Count = ds4Count;
        }

        /// <summary>
        /// Tracks how many consecutive polling cycles each slot has been inactive.
        /// Virtual controllers are only destroyed after a sustained inactivity period
        /// to prevent transient <see cref="IsSlotActive"/> false returns from
        /// destroying/recreating controllers (which kills vibration feedback).
        /// </summary>
        private readonly int[] _slotInactiveCounter = new int[MaxPads];

        /// <summary>
        /// Number of consecutive inactive cycles before a virtual controller is destroyed.
        /// At ~1000Hz polling, 10000 cycles ≈ 10 seconds of sustained inactivity.
        /// </summary>
        private const int SlotDestroyGraceCycles = 10000;

        /// <summary>
        /// Per-slot cooldown counter after a failed virtual controller creation.
        /// Prevents per-frame retry of FindFreeDeviceId (16 GetVJDStatus calls)
        /// when creation fails. Counts down each cycle; creation retries at 0.
        /// At ~1000Hz polling, 2000 cycles ≈ 2 seconds between retries.
        /// </summary>
        private readonly int[] _createCooldown = new int[MaxPads];
        private const int CreateCooldownCycles = 2000;

        /// <summary>Whether virtual controller output is enabled.</summary>
        public bool VirtualControllersEnabled { get; set; } = true;

        /// <summary>Whether ViGEmBus driver is reachable.</summary>
        public bool IsViGEmAvailable => _vigemClient != null;

        /// <summary>
        /// Returns true if the specified pad slot has an active ViGEm virtual controller.
        /// Used by the UI to show connected status on dashboard cards.
        /// </summary>
        public bool IsVirtualControllerConnected(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return false;
            var vc = _virtualControllers[padIndex];
            return vc != null && vc.IsConnected;
        }

        /// <summary>
        /// Step 5: Feed each slot's combined gamepad state to ViGEmBus.
        /// Receives vibration feedback from games via the virtual controller.
        ///
        /// Uses a grace period before destroying inactive virtual controllers to
        /// prevent transient IsSlotActive(false) from killing vibration feedback.
        /// Destroying a virtual controller severs the game's vibration connection
        /// (FeedbackReceived stops firing), and recreating it requires the game to
        /// rediscover the controller and re-send XInputSetState — causing a gap.
        ///
        /// Virtual controllers are created in ascending slot order so that ViGEm
        /// assigns sequential indices matching the PadForge slot numbers.
        /// </summary>
        private void UpdateVirtualDevices()
        {
            if (!VirtualControllersEnabled || _vigemClientFailed)
                return;

            EnsureViGEmClient();
            if (_vigemClient == null)
                return;

            // --- Pass 1: Handle type changes, destruction, and activity tracking ---
            bool anyNeedsCreate = false;

            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                var vc = _virtualControllers[padIndex];

                // Detect controller type change — destroy old if type differs.
                if (vc != null && vc.Type != SlotControllerTypes[padIndex])
                {
                    RumbleLogger.Log($"[Step5] Pad{padIndex} type changed {vc.Type}->{SlotControllerTypes[padIndex]}, recreating");
                    DestroyVirtualController(padIndex);
                    _virtualControllers[padIndex] = null;
                    _createCooldown[padIndex] = 0; // Reset cooldown on type change
                    vc = null;
                }

                // Slot deleted or disabled by user — destroy immediately.
                // The grace period only applies to transient device disconnects
                // (slot still created + enabled, but physical device offline).
                if (vc != null && (!SettingsManager.SlotCreated[padIndex] || !SettingsManager.SlotEnabled[padIndex]))
                {
                    RumbleLogger.Log($"[Step5] Pad{padIndex} slot {(SettingsManager.SlotCreated[padIndex] ? "disabled" : "deleted")}, destroying virtual controller immediately");
                    DestroyVirtualController(padIndex);
                    _virtualControllers[padIndex] = null;
                    _slotInactiveCounter[padIndex] = 0;
                    VibrationStates[padIndex].LeftMotorSpeed = 0;
                    VibrationStates[padIndex].RightMotorSpeed = 0;
                    continue;
                }

                bool slotActive = IsSlotActive(padIndex);

                if (slotActive)
                {
                    if (_slotInactiveCounter[padIndex] > 0)
                        RumbleLogger.Log($"[Step5] Pad{padIndex} active again after {_slotInactiveCounter[padIndex]} inactive cycles");

                    _slotInactiveCounter[padIndex] = 0;

                    if (vc == null)
                        anyNeedsCreate = true;
                }
                else if (vc != null && !HasAnyDeviceMapped(padIndex))
                {
                    // No devices mapped to this slot — user explicitly unassigned
                    // all devices. Destroy immediately (not a transient disconnect).
                    RumbleLogger.Log($"[Step5] Pad{padIndex} no devices mapped, destroying virtual controller immediately");
                    DestroyVirtualController(padIndex);
                    _virtualControllers[padIndex] = null;
                    _slotInactiveCounter[padIndex] = 0;
                    VibrationStates[padIndex].LeftMotorSpeed = 0;
                    VibrationStates[padIndex].RightMotorSpeed = 0;
                }
                else
                {
                    // Device(s) mapped but offline — transient disconnect.
                    // Grace period preserves rumble feedback through USB hiccups.
                    _slotInactiveCounter[padIndex]++;

                    if (_slotInactiveCounter[padIndex] == 1)
                        RumbleLogger.Log($"[Step5] Pad{padIndex} !slotActive (vc={vc != null}) VibL={VibrationStates[padIndex].LeftMotorSpeed} VibR={VibrationStates[padIndex].RightMotorSpeed}");

                    if (vc != null && _slotInactiveCounter[padIndex] >= SlotDestroyGraceCycles)
                    {
                        RumbleLogger.Log($"[Step5] Pad{padIndex} destroying virtual controller after {SlotDestroyGraceCycles} inactive cycles");
                        DestroyVirtualController(padIndex);
                        _virtualControllers[padIndex] = null;
                        VibrationStates[padIndex].LeftMotorSpeed = 0;
                        VibrationStates[padIndex].RightMotorSpeed = 0;
                    }
                }
            }

            // --- Pass 2: Create virtual controllers in ascending slot order ---
            // ViGEm assigns indices sequentially on Connect(), so creation order
            // must match slot order. This applies to both Xbox 360 (XInput index)
            // and DS4 (ViGEm DS4 index) controllers.
            if (anyNeedsCreate)
            {
                for (int padIndex = 0; padIndex < MaxPads; padIndex++)
                {
                    if (_virtualControllers[padIndex] == null &&
                        _slotInactiveCounter[padIndex] == 0)
                    {
                        // Skip if still in cooldown from a previous failed creation.
                        if (_createCooldown[padIndex] > 0)
                        {
                            _createCooldown[padIndex]--;
                            continue;
                        }

                        RumbleLogger.Log($"[Step5] Pad{padIndex} creating {SlotControllerTypes[padIndex]} virtual controller (ordered)");
                        var vc = CreateVirtualController(padIndex);
                        _virtualControllers[padIndex] = vc;

                        if (vc == null)
                            _createCooldown[padIndex] = CreateCooldownCycles;
                    }
                }
            }

            // --- Pass 3: Submit reports for active slots ---
            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                try
                {
                    var vc = _virtualControllers[padIndex];
                    if (vc != null && _slotInactiveCounter[padIndex] == 0)
                    {
                        vc.SubmitGamepadState(CombinedOutputStates[padIndex]);
                    }
                }
                catch (Exception ex)
                {
                    RaiseError($"Error updating virtual controller for pad {padIndex}", ex);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  ViGEm client lifecycle
        // ─────────────────────────────────────────────

        private void EnsureViGEmClient()
        {
            if (_vigemClient != null || _vigemClientFailed)
                return;

            lock (_vigemClientLock)
            {
                if (_vigemClient != null || _vigemClientFailed)
                    return;

                try
                {
                    _vigemClient = new ViGEmClient();
                }
                catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException)
                {
                    _vigemClientFailed = true;
                    RaiseError("ViGEmBus driver is not installed.", null);
                }
                catch (Exception ex)
                {
                    _vigemClientFailed = true;
                    RaiseError("Failed to initialize ViGEmClient.", ex);
                }
            }
        }

        /// <summary>
        /// Static check: is ViGEmBus driver installed?
        /// Called by the UI on startup to populate SettingsViewModel.
        /// </summary>
        public static bool CheckViGEmInstalled()
        {
            try
            {
                using var client = new ViGEmClient();
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ─────────────────────────────────────────────
        //  Slot activity check
        // ─────────────────────────────────────────────

        private bool IsSlotActive(int padIndex)
        {
            // Slot must be explicitly created AND enabled.
            if (!SettingsManager.SlotCreated[padIndex] || !SettingsManager.SlotEnabled[padIndex])
                return false;

            var settings = SettingsManager.UserSettings;
            if (settings == null) return false;

            // Use non-allocating overload with pre-allocated buffer.
            int slotCount = settings.FindByPadIndex(padIndex, _padIndexBuffer);
            if (slotCount == 0)
                return false;

            for (int i = 0; i < slotCount; i++)
            {
                var us = _padIndexBuffer[i];
                if (us == null) continue;
                var ud = FindOnlineDeviceByInstanceGuid(us.InstanceGuid);
                if (ud != null && ud.IsOnline)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if any device (online or offline) is mapped to this slot.
        /// Used to distinguish "user unassigned all devices" (no mappings → destroy
        /// immediately) from "device temporarily offline" (mapping exists → grace period).
        /// </summary>
        private bool HasAnyDeviceMapped(int padIndex)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return false;
            return settings.FindByPadIndex(padIndex, _padIndexBuffer) > 0;
        }

        // ─────────────────────────────────────────────
        //  Virtual controller management
        //  Uses XInput slot mask delta to detect which
        //  slot the new virtual controller occupies.
        // ─────────────────────────────────────────────

        private IVirtualController CreateVirtualController(int padIndex)
        {
            var controllerType = SlotControllerTypes[padIndex];

            // vJoy doesn't need ViGEm, but Xbox 360 and DS4 do.
            if (controllerType != VirtualControllerType.VJoy && _vigemClient == null)
                return null;

            try
            {
                // Snapshot XInput slot mask BEFORE connecting (Xbox 360 only).
                uint maskBefore = 0;
                if (controllerType == VirtualControllerType.Xbox360)
                    maskBefore = GetXInputConnectedSlotMask();

                IVirtualController vc = controllerType switch
                {
                    VirtualControllerType.DualShock4 => new DS4VirtualController(_vigemClient),
                    VirtualControllerType.VJoy => CreateVJoyController(),
                    _ => new Xbox360VirtualController(_vigemClient)
                };

                if (vc == null) return null;
                vc.Connect();

                // Wait for the new XInput slot to appear (Xbox 360 only).
                // DS4 and vJoy virtual controllers don't appear in the XInput stack.
                if (controllerType == VirtualControllerType.Xbox360)
                {
                    var waitSw = Stopwatch.StartNew();
                    while (waitSw.ElapsedMilliseconds < 50)
                    {
                        uint maskAfter = GetXInputConnectedSlotMask();
                        if (maskAfter != maskBefore)
                            break;
                        Thread.SpinWait(100);
                    }
                }

                if (controllerType == VirtualControllerType.Xbox360)
                {
                    _activeVigemCount++;
                    _activeXbox360Count++;
                }
                else if (controllerType == VirtualControllerType.DualShock4)
                {
                    _activeVigemCount++;
                    _activeDs4Count++;
                }
                vc.RegisterFeedbackCallback(padIndex, VibrationStates);

                return vc;
            }
            catch (Exception ex)
            {
                RaiseError($"Failed to create {SlotControllerTypes[padIndex]} virtual controller for pad {padIndex}", ex);
                return null;
            }
        }

        /// <summary>
        /// Creates a vJoy virtual controller using the next available device ID.
        /// Creates a device node on demand if needed (like ViGEm creates USB nodes).
        /// Returns null if vJoy driver is not installed or node creation fails.
        /// </summary>
        private IVirtualController CreateVJoyController()
        {
            // Light check: can the DLL be loaded? Don't use CheckVJoyInstalled()
            // (calls vJoyEnabled()) because that requires active device nodes —
            // which don't exist yet in the on-demand model.
            VJoyVirtualController.EnsureDllLoaded();
            if (!VJoyVirtualController.IsDllLoaded)
            {
                RaiseError("vJoy driver is not installed (vJoyInterface.dll not found).", null);
                return null;
            }

            // Count how many vJoy device nodes we need (this new one + existing connected ones).
            int activeVJoy = 0;
            for (int i = 0; i < MaxPads; i++)
                if (_virtualControllers[i]?.Type == VirtualControllerType.VJoy)
                    activeVJoy++;
            int needed = activeVJoy + 1;

            // Ensure enough device nodes exist. WriteDeviceConfiguration runs
            // inside EnsureDevicesAvailable to set the correct HID descriptor
            // (11 buttons, 6 axes, 1 POV) before the driver binds.
            if (!VJoyVirtualController.EnsureDevicesAvailable(needed))
            {
                RaiseError("Failed to create vJoy device node.", null);
                return null;
            }

            uint deviceId = VJoyVirtualController.FindFreeDeviceId();
            if (deviceId == 0)
            {
                RaiseError("No free vJoy devices after node creation.", null);
                return null;
            }
            return new VJoyVirtualController(deviceId);
        }

        /// <param name="trimVJoyNodes">
        /// When true, trims vJoy device nodes after disconnecting. Set to false
        /// during bulk destroy (DestroyAllVirtualControllers) — the caller handles
        /// node cleanup via RemoveAllDeviceNodes() once at the end.
        /// </param>
        private void DestroyVirtualController(int padIndex, bool trimVJoyNodes = true)
        {
            var vc = _virtualControllers[padIndex];
            if (vc == null) return;

            var vcType = vc.Type;

            try
            {
                // Snapshot for Xbox 360 slot mask wait.
                uint maskBefore = 0;
                if (vcType == VirtualControllerType.Xbox360)
                    maskBefore = GetXInputConnectedSlotMask();

                vc.Disconnect();

                // Brief wait for the slot to disappear from the XInput stack.
                if (vcType == VirtualControllerType.Xbox360)
                {
                    var waitSw = Stopwatch.StartNew();
                    while (waitSw.ElapsedMilliseconds < 50)
                    {
                        uint maskAfter = GetXInputConnectedSlotMask();
                        if (maskAfter != maskBefore)
                            break;
                        Thread.SpinWait(100);
                    }
                }

                // Dispose releases the native ViGEm target handle (vigem_target_free).
                // Without this, the ViGEm target leaks and phantom USB devices remain.
                vc.Dispose();
            }
            catch { /* best effort */ }
            finally
            {
                // Counter decrements MUST happen even if Disconnect/Dispose throws.
                // Otherwise _activeXbox360Count stays inflated and the filter
                // over-filters on subsequent UpdateDevices cycles.
                if (vcType == VirtualControllerType.Xbox360)
                {
                    _activeVigemCount = Math.Max(0, _activeVigemCount - 1);
                    _activeXbox360Count = Math.Max(0, _activeXbox360Count - 1);
                }
                else if (vcType == VirtualControllerType.DualShock4)
                {
                    _activeVigemCount = Math.Max(0, _activeVigemCount - 1);
                    _activeDs4Count = Math.Max(0, _activeDs4Count - 1);
                }
            }

            // vJoy: trim device nodes so dormant devices don't appear in joy.cpl.
            // Skipped during bulk destroy — RemoveAllDeviceNodes handles it.
            try
            {
                if (trimVJoyNodes && vcType == VirtualControllerType.VJoy)
                {
                    int remainingVJoy = 0;
                    for (int i = 0; i < MaxPads; i++)
                        if (i != padIndex && _virtualControllers[i]?.Type == VirtualControllerType.VJoy)
                            remainingVJoy++;
                    VJoyVirtualController.TrimDeviceNodes(remainingVJoy);
                }
            }
            catch { /* best effort */ }
        }

        private void DestroyAllVirtualControllers()
        {
            // Skip per-VC device node trimming — RemoveAllDeviceNodes()
            // in InputService.Stop() handles bulk cleanup in one pass.
            for (int i = 0; i < MaxPads; i++)
            {
                DestroyVirtualController(i, trimVJoyNodes: false);
                _virtualControllers[i] = null;
            }

            _activeVigemCount = 0;
            _activeXbox360Count = 0;
            _activeDs4Count = 0;
        }

        // ─────────────────────────────────────────────
        //  Stale ViGEm device cleanup
        //
        //  ViGEm bus driver may leave orphaned USB device nodes
        //  when a feeder app exits without calling Dispose() on
        //  its virtual controller targets. These stale nodes
        //  appear as real Xbox 360 / DS4 controllers to SDL.
        // ─────────────────────────────────────────────

        /// <summary>
        /// Removes stale ViGEm USB device nodes that survived from previous sessions.
        /// ViGEm assigns short numeric serials (01, 02, ..., 16) to Xbox 360 targets.
        /// Real Xbox 360 controllers have longer hardware serials.
        /// Must be called BEFORE Start() so SDL doesn't enumerate the stale nodes.
        /// </summary>
        public static void CleanupStaleVigemDevices()
        {
            try
            {
                // ViGEm Xbox 360 virtual controllers are in the XnaComposite class
                // with instance IDs like USB\VID_045E&PID_028E\01.
                var staleIds = EnumerateStaleVigemIds("XnaComposite");

                if (staleIds.Count == 0) return;

                Debug.WriteLine($"[ViGEm] Cleaning up {staleIds.Count} stale device node(s)");
                foreach (string id in staleIds)
                {
                    try
                    {
                        var removePsi = new ProcessStartInfo
                        {
                            FileName = "pnputil.exe",
                            Arguments = $"/remove-device \"{id}\" /subtree",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        using var removeProc = Process.Start(removePsi);
                        removeProc?.WaitForExit(5_000);
                        Debug.WriteLine($"[ViGEm] Removed stale device: {id}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ViGEm] Failed to remove {id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViGEm] CleanupStaleVigemDevices exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Enumerates stale ViGEm device instance IDs in the given device class.
        /// ViGEm Xbox 360 targets: USB\VID_045E&amp;PID_028E\NN (short numeric serial).
        /// Real Xbox controllers have longer alphanumeric serials.
        /// </summary>
        private static List<string> EnumerateStaleVigemIds(string deviceClass)
        {
            var results = new List<string>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = $"/enum-devices /class {deviceClass}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return results;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5_000);

                string currentInstanceId = null;
                foreach (string rawLine in output.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.IndexOf("Instance ID", StringComparison.OrdinalIgnoreCase) >= 0
                        && line.Contains(":"))
                    {
                        currentInstanceId = line.Substring(line.IndexOf(':') + 1).Trim();
                    }
                    else if (string.IsNullOrEmpty(line))
                    {
                        if (currentInstanceId != null && IsVigemInstanceId(currentInstanceId))
                            results.Add(currentInstanceId);
                        currentInstanceId = null;
                    }
                }
                if (currentInstanceId != null && IsVigemInstanceId(currentInstanceId))
                    results.Add(currentInstanceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViGEm] EnumerateStaleVigemIds({deviceClass}) exception: {ex.Message}");
            }
            return results;
        }

        /// <summary>
        /// Checks if a PnP instance ID looks like a ViGEm virtual controller.
        /// ViGEm assigns short numeric serials (1-2 digits): USB\VID_045E&amp;PID_028E\01
        /// Real Xbox controllers have long alphanumeric serials.
        /// </summary>
        private static bool IsVigemInstanceId(string instanceId)
        {
            // USB\VID_045E&PID_028E\NN (Xbox 360)
            if (!instanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                return false;

            int lastBackslash = instanceId.LastIndexOf('\\');
            if (lastBackslash < 0) return false;

            string serial = instanceId.Substring(lastBackslash + 1);
            // ViGEm serials are short numeric strings (1-2 digits).
            // Real USB controllers have longer alphanumeric serials.
            if (serial.Length > 2 || serial.Length == 0) return false;
            foreach (char c in serial)
                if (!char.IsDigit(c)) return false;

            string upperPath = instanceId.ToUpperInvariant();
            return upperPath.Contains("VID_045E&PID_028E") ||
                   upperPath.Contains("VID_054C&PID_05C4");
        }

        // ─────────────────────────────────────────────
        //  XInput slot mask — direct P/Invoke to xinput1_4.dll
        //
        //  Used for ViGEm virtual controller management only
        //  (detecting when a newly created Xbox 360 virtual
        //  controller appears in the XInput stack).
        // ─────────────────────────────────────────────

        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        private static extern uint XInputGetStateEx(
            uint dwUserIndex, ref XInputStateInternal pState);

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepadInternal
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputStateInternal
        {
            public uint dwPacketNumber;
            public XInputGamepadInternal Gamepad;
        }

        private const uint XINPUT_ERROR_DEVICE_NOT_CONNECTED = 0x048F;

        /// <summary>
        /// Returns a bitmask of connected XInput slots (bit 0 = slot 0, etc.).
        /// Probes slots 0–3 directly via xinput1_4.dll.
        /// </summary>
        private static uint GetXInputConnectedSlotMask()
        {
            uint mask = 0;
            for (uint i = 0; i < 4; i++)
            {
                var state = new XInputStateInternal();
                if (XInputGetStateEx(i, ref state) != XINPUT_ERROR_DEVICE_NOT_CONNECTED)
                    mask |= (1u << (int)i);
            }
            return mask;
        }
    }
}
