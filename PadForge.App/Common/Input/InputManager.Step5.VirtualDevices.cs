using System;
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

                bool slotActive = IsSlotActive(padIndex);

                if (slotActive)
                {
                    if (_slotInactiveCounter[padIndex] > 0)
                        RumbleLogger.Log($"[Step5] Pad{padIndex} active again after {_slotInactiveCounter[padIndex]} inactive cycles");

                    _slotInactiveCounter[padIndex] = 0;

                    if (vc == null)
                        anyNeedsCreate = true;
                }
                else
                {
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

                // Only count ViGEm-based controllers (Xbox 360 / DS4).
                // vJoy is NOT a ViGEm controller — inflating this count causes
                // IsViGEmVirtualDevice() to incorrectly filter real controllers.
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
        /// Returns null if vJoy driver is not installed or no free devices.
        /// </summary>
        private IVirtualController CreateVJoyController()
        {
            uint deviceId = VJoyVirtualController.FindFreeDeviceId();
            if (deviceId == 0)
            {
                int existing = VJoyVirtualController.CountExistingDevices();
                bool installed = VJoyVirtualController.CheckVJoyInstalled();
                RaiseError($"No free vJoy devices (existing={existing}, driver={installed}). " +
                    "Check that the vJoy driver is installed and device nodes exist.", null);
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

            try
            {
                // Snapshot for Xbox 360 slot mask wait.
                uint maskBefore = 0;
                if (vc.Type == VirtualControllerType.Xbox360)
                    maskBefore = GetXInputConnectedSlotMask();

                vc.Disconnect();

                // Brief wait for the slot to disappear from the XInput stack.
                if (vc.Type == VirtualControllerType.Xbox360)
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

                if (vc.Type == VirtualControllerType.Xbox360)
                {
                    _activeVigemCount = Math.Max(0, _activeVigemCount - 1);
                    _activeXbox360Count = Math.Max(0, _activeXbox360Count - 1);
                }
                else if (vc.Type == VirtualControllerType.DualShock4)
                {
                    _activeVigemCount = Math.Max(0, _activeVigemCount - 1);
                    _activeDs4Count = Math.Max(0, _activeDs4Count - 1);
                }

                // vJoy: trim device nodes so dormant devices don't appear in joy.cpl.
                // Skipped during bulk destroy — RemoveAllDeviceNodes handles it.
                if (trimVJoyNodes && vc.Type == VirtualControllerType.VJoy)
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
