using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Nefarius.ViGEm.Client;
using PadForge.Engine;
using PadForge.ViewModels;

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

        /// <summary>
        /// Shared HIDMaestro context (one per process). Replaces ViGEmBus +
        /// vJoy in v3. Initialized lazily on first use, owns all HMController
        /// instances created by HMaestroVirtualController.
        /// </summary>
        private static HMContext _hmaestroContext;
        private static readonly object _hmaestroContextLock = new object();
        private static bool _hmaestroContextFailed;

        /// <summary>Virtual controller targets (one per slot).</summary>
        private IVirtualController[] _virtualControllers = new IVirtualController[MaxPads];

        /// <summary>
        /// Configured virtual controller type per slot. The UI writes to this
        /// array via InputService at 30Hz. Step 5 reads it at ~1000Hz to detect
        /// type changes and recreate controllers accordingly.
        /// </summary>
        public VirtualControllerType[] SlotControllerTypes { get; } = new VirtualControllerType[MaxPads];

        /// <summary>
        /// Per-slot vJoy HID descriptor config (axes, buttons, POVs).
        /// Written by InputService from PadViewModel.VJoyConfig. Read by Step 5
        /// to pass per-device configs to EnsureDevicesAvailable.
        /// </summary>
        internal VJoyVirtualController.VJoyDeviceConfig[] SlotVJoyConfigs { get; } = new VJoyVirtualController.VJoyDeviceConfig[MaxPads];

        /// <summary>
        /// Per-slot flag: true if this vJoy slot uses Custom preset (raw axis/button pipeline),
        /// false if it uses a gamepad preset (Xbox360/DS4 → Gamepad struct pipeline).
        /// Written by InputService from PadViewModel.VJoyConfig.IsGamepadPreset.
        /// </summary>
        internal bool[] SlotVJoyIsCustom { get; } = new bool[MaxPads];

        /// <summary>
        /// Per-slot MIDI configuration snapshot. Written by InputService at 30Hz.
        /// Read by Step 5 to configure MIDI controllers on creation.
        /// </summary>
        internal MidiSlotConfig[] _midiConfigs = new MidiSlotConfig[MaxPads];

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
        private int _lastLoggedVJoyNeeded = -1;
        private bool _lastLoggedEnterSync;

        /// <summary>
        /// Grace period counter for vJoy sync. On startup, device enumeration may not
        /// have completed yet, causing a transient totalVJoyNeeded=0. This counter
        /// tracks total vJoy sync cycles since process start to skip descriptor
        /// cleanup (node removal) during the startup window.
        /// At 1000Hz, 5000 cycles = 5 seconds of grace.
        /// </summary>
        private int _vJoySyncCycleCount;
        private const int VJoyStartupGraceCycles = 5000;

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

        /// <summary>
        /// Per-slot flag: true while a virtual controller is being created or
        /// reconfigured (e.g., vJoy descriptor change → node restart). Set true
        /// just before creation, cleared when the controller reports IsConnected.
        /// Read by the UI thread via <see cref="IsVirtualControllerInitializing"/>.
        /// </summary>
        private readonly bool[] _slotInitializing = new bool[MaxPads];

        /// <summary>Cached per-slot vJoy configs for detecting content changes in Step 5.</summary>
        private VJoyVirtualController.VJoyDeviceConfig[] _lastStep5VJoyConfigs;

        /// <summary>
        /// Lock protecting vJoy descriptor sync from concurrent SwapSlotData.
        /// Without this, the polling thread can observe a half-swapped state
        /// (configs from one slot paired with the VC from another), causing
        /// spurious descriptor changes and device node restarts.
        /// </summary>
        internal readonly object VJoySyncLock = new();

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
        /// Returns true if the specified pad slot is currently initializing
        /// (creating a virtual controller or reconfiguring vJoy descriptors).
        /// Used by the UI to show a flashing green indicator.
        /// </summary>
        public bool IsVirtualControllerInitializing(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return false;
            return _slotInitializing[padIndex];
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
            if (!VirtualControllersEnabled)
                return;

            // ViGEm is only required for Xbox 360 and DS4 virtual controllers.
            // vJoy uses its own driver and works independently. Only try to
            // initialize ViGEm if it hasn't permanently failed.
            if (!_vigemClientFailed)
                EnsureViGEmClient();

            // --- Pass 1: Handle type changes, destruction, and activity tracking ---
            bool anyNeedsCreate = false;

            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                var vc = _virtualControllers[padIndex];

                // Detect controller type change — destroy old if type differs.
                if (vc != null && vc.Type != SlotControllerTypes[padIndex])
                {
                    VJoyVirtualController.DiagLog($"Pass1: Pad{padIndex} type changed {vc.Type}->{SlotControllerTypes[padIndex]}, destroying old VC");
                    RumbleLogger.Log($"[Step5] Pad{padIndex} type changed {vc.Type}->{SlotControllerTypes[padIndex]}, recreating");
                    DestroyVirtualController(padIndex);
                    _virtualControllers[padIndex] = null;
                    _createCooldown[padIndex] = 0; // Reset cooldown on type change
                    // Only show initializing if the slot has an active device that
                    // will trigger VC recreation. Empty slots (no device assigned)
                    // should show "Awaiting controllers" (yellow), not "Initializing".
                    _slotInitializing[padIndex] = IsSlotActive(padIndex);
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
                    _slotInitializing[padIndex] = false;
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
                    {
                        anyNeedsCreate = true;
                        _slotInitializing[padIndex] = true;
                    }
                }
                else if (vc != null && !HasAnyDeviceMapped(padIndex))
                {
                    // No devices mapped to this slot — user explicitly unassigned
                    // all devices. Destroy immediately (not a transient disconnect).
                    RumbleLogger.Log($"[Step5] Pad{padIndex} no devices mapped, destroying virtual controller immediately");
                    DestroyVirtualController(padIndex);
                    _virtualControllers[padIndex] = null;
                    _slotInactiveCounter[padIndex] = 0;
                    _slotInitializing[padIndex] = false;
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

            // --- Pass 1b: Sync vJoy registry descriptor count ---
            // Only count vJoy slots that have an actual running controller or are
            // about to create one (active device mapped). Empty vJoy slots with no
            // device assigned should NOT register descriptors in joy.cpl.
            // Hold VJoySyncLock to prevent SwapSlotData from changing configs/VCs
            // mid-scan, which would cause spurious descriptor changes.
            lock (VJoySyncLock)
            {
                int totalVJoyNeeded = 0;
                bool anyVJoySlotExists = false;
                for (int i = 0; i < MaxPads; i++)
                {
                    if (SlotControllerTypes[i] == VirtualControllerType.VJoy &&
                        SettingsManager.SlotCreated[i])
                        anyVJoySlotExists = true;

                    if (_virtualControllers[i] is VJoyVirtualController)
                        totalVJoyNeeded++;  // Already running — always count
                    else if (SlotControllerTypes[i] == VirtualControllerType.VJoy &&
                             SettingsManager.SlotCreated[i] &&
                             SettingsManager.SlotEnabled[i] &&
                             IsSlotActive(i))
                        totalVJoyNeeded++;  // Active device mapped — will create soon
                }
                // Enter sync block whenever vJoy slots exist (to handle future creation),
                // OR when stale descriptors need cleanup (count mismatch or leftover from
                // previous session). This ensures deletion of the last vJoy slot always
                // triggers descriptor cleanup even when _currentDescriptorCount was never set.
                bool enterVJoySync = anyVJoySlotExists || totalVJoyNeeded > 0 ||
                    VJoyVirtualController.CurrentDescriptorCount > 0;
                // Only log Pass1b when state actually changes to avoid 1000Hz log spam.
                if (totalVJoyNeeded != _lastLoggedVJoyNeeded || enterVJoySync != _lastLoggedEnterSync)
                {
                    // Diagnostic: log per-slot detail when totalVJoyNeeded=0 but vJoy slots exist
                    if (totalVJoyNeeded == 0 && anyVJoySlotExists)
                    {
                        for (int dbg = 0; dbg < MaxPads; dbg++)
                        {
                            if (SlotControllerTypes[dbg] == VirtualControllerType.VJoy && SettingsManager.SlotCreated[dbg])
                            {
                                bool slotActive = IsSlotActive(dbg);
                                int inactiveCount = _slotInactiveCounter[dbg];
                                var settings = SettingsManager.UserSettings;
                                int mappedCount = settings?.FindByPadIndex(dbg, _padIndexBuffer) ?? 0;
                                VJoyVirtualController.DiagLog(
                                    $"  vJoy slot {dbg}: active={slotActive}, inactive={inactiveCount}, " +
                                    $"mappedDevices={mappedCount}, enabled={SettingsManager.SlotEnabled[dbg]}");
                            }
                        }
                    }
                    VJoyVirtualController.DiagLog(
                        $"Pass1b: totalVJoyNeeded={totalVJoyNeeded}, anyVJoySlotExists={anyVJoySlotExists}, " +
                        $"currentDescCount={VJoyVirtualController.CurrentDescriptorCount}, enterSync={enterVJoySync}");
                    _lastLoggedVJoyNeeded = totalVJoyNeeded;
                    _lastLoggedEnterSync = enterVJoySync;
                }
                // Grace period: don't remove vJoy descriptors during startup.
                // On first few polling cycles, device enumeration may not have
                // completed yet, causing a transient totalVJoyNeeded=0 that would
                // delete the node only to recreate it moments later.
                if (enterVJoySync)
                    _vJoySyncCycleCount++;

                if (totalVJoyNeeded == 0 && enterVJoySync &&
                    _vJoySyncCycleCount < VJoyStartupGraceCycles)
                {
                    goto AfterVJoySync;
                }

                if (enterVJoySync)
                {
                    bool descriptorCountChanged = totalVJoyNeeded != VJoyVirtualController.CurrentDescriptorCount;

                    // If descriptor count is changing, destroy vJoy VCs that are no
                    // longer needed (slot inactive). VCs in active slots survive even
                    // if their DeviceId exceeds the new count — they'll re-acquire a
                    // lower ID after the node restart. This prevents destroying the
                    // only remaining active VC just because a swap gave it DeviceId=2.
                    if (descriptorCountChanged)
                    {
                        for (int i = 0; i < MaxPads; i++)
                        {
                            if (_virtualControllers[i] is VJoyVirtualController vjoy &&
                                !IsSlotActive(i))
                            {
                                DestroyVirtualController(i);
                                _virtualControllers[i] = null;
                            }
                            // Mark active vJoy slots as initializing during descriptor change.
                            // Slots with no device assigned stay yellow ("Awaiting controllers").
                            if (SlotControllerTypes[i] == VirtualControllerType.VJoy &&
                                SettingsManager.SlotCreated[i] && SettingsManager.SlotEnabled[i])
                                _slotInitializing[i] = IsSlotActive(i);
                        }
                        // Use |= to preserve any true state from Pass 1 (non-vJoy slots
                        // that need creation). Previously this unconditional assignment
                        // would override anyNeedsCreate=true from a vJoy→Xbox type switch,
                        // delaying creation of the Xbox VC by 1 cycle.
                        anyNeedsCreate |= totalVJoyNeeded > 0;
                    }

                    // Build per-device HID descriptor configs indexed by DEVICE ID, not slot.
                    // Existing VCs have fixed device IDs (1-based). After a slot swap,
                    // the VC's device ID doesn't change, but its slot position does.
                    // Building by slot order would reassign descriptors to wrong device IDs,
                    // causing a spurious content change → node restart → VC failure.
                    VJoyVirtualController.VJoyDeviceConfig[] deviceConfigs = null;
                    if (totalVJoyNeeded > 0)
                    {
                        deviceConfigs = new VJoyVirtualController.VJoyDeviceConfig[totalVJoyNeeded];
                        var usedIndices = new HashSet<int>();

                        // Pass A: place configs for existing VCs at their device ID position.
                        // If the VC's DeviceId exceeds the new count (e.g., DeviceId=2 but
                        // totalVJoyNeeded=1 after a slot was deactivated), it will be
                        // reassigned to a lower ID — queue its config for Pass B instead.
                        var overflowConfigs = new List<VJoyVirtualController.VJoyDeviceConfig>();
                        for (int i = 0; i < MaxPads; i++)
                        {
                            if (_virtualControllers[i] is VJoyVirtualController vjoy)
                            {
                                int idx = (int)vjoy.DeviceId - 1; // DeviceId is 1-based
                                if (idx >= 0 && idx < totalVJoyNeeded)
                                {
                                    deviceConfigs[idx] = SlotVJoyConfigs[i];
                                    usedIndices.Add(idx);
                                }
                                else if (IsSlotActive(i))
                                {
                                    // VC will be destroyed and recreated with a lower ID.
                                    // Preserve its config so the new descriptor matches.
                                    overflowConfigs.Add(SlotVJoyConfigs[i]);
                                }
                            }
                        }

                        // Pass B: fill remaining positions with overflow VCs and new slots.
                        int cfgIdx = 0;
                        // First, place overflow configs (active VCs that exceeded the count).
                        foreach (var cfg in overflowConfigs)
                        {
                            while (cfgIdx < totalVJoyNeeded && usedIndices.Contains(cfgIdx))
                                cfgIdx++;
                            if (cfgIdx < totalVJoyNeeded)
                            {
                                deviceConfigs[cfgIdx] = cfg;
                                usedIndices.Add(cfgIdx);
                                cfgIdx++;
                            }
                        }
                        // Then, place configs for slots that don't have VCs yet.
                        for (int i = 0; i < MaxPads; i++)
                        {
                            if (_virtualControllers[i] is not VJoyVirtualController &&
                                SlotControllerTypes[i] == VirtualControllerType.VJoy &&
                                SettingsManager.SlotCreated[i] &&
                                SettingsManager.SlotEnabled[i] &&
                                IsSlotActive(i))
                            {
                                while (cfgIdx < totalVJoyNeeded && usedIndices.Contains(cfgIdx))
                                    cfgIdx++;
                                if (cfgIdx < totalVJoyNeeded)
                                {
                                    deviceConfigs[cfgIdx] = SlotVJoyConfigs[i];
                                    cfgIdx++;
                                }
                            }
                        }
                    }

                    // Detect per-device config content changes (axes/buttons/POVs changed
                    // without changing the number of vJoy slots). This triggers a device
                    // node restart inside EnsureDevicesAvailable, so mark slots as initializing.
                    if (!descriptorCountChanged && deviceConfigs != null)
                    {
                        bool vjoyConfigContentChanged = false;
                        if (_lastStep5VJoyConfigs == null || _lastStep5VJoyConfigs.Length != deviceConfigs.Length)
                            vjoyConfigContentChanged = true;
                        else
                        {
                            for (int i = 0; i < deviceConfigs.Length; i++)
                            {
                                if (_lastStep5VJoyConfigs[i].Axes != deviceConfigs[i].Axes ||
                                    _lastStep5VJoyConfigs[i].Buttons != deviceConfigs[i].Buttons ||
                                    _lastStep5VJoyConfigs[i].Povs != deviceConfigs[i].Povs)
                                { vjoyConfigContentChanged = true; break; }
                            }
                        }
                        if (vjoyConfigContentChanged)
                        {
                            for (int i = 0; i < MaxPads; i++)
                            {
                                if (SlotControllerTypes[i] == VirtualControllerType.VJoy &&
                                    SettingsManager.SlotCreated[i] && SettingsManager.SlotEnabled[i])
                                    _slotInitializing[i] = IsSlotActive(i);
                            }
                        }
                    }
                    // Cache configs for next cycle's comparison.
                    _lastStep5VJoyConfigs = deviceConfigs != null
                        ? (VJoyVirtualController.VJoyDeviceConfig[])deviceConfigs.Clone()
                        : null;

                    VJoyVirtualController.EnsureDevicesAvailable(totalVJoyNeeded, deviceConfigs);

                    // After EnsureDevicesAvailable (which may restart the device node),
                    // force existing vJoy controllers to re-acquire their device IDs
                    // BEFORE creating new ones.
                    for (int padIndex = 0; padIndex < MaxPads; padIndex++)
                    {
                        if (_virtualControllers[padIndex] is VJoyVirtualController existingVjoy)
                            existingVjoy.ReAcquireIfNeeded();
                    }

                    // After a descriptor count change, surviving VCs may have device
                    // IDs that don't match their sequential position. Example: deleting
                    // the first of 3 vJoy slots leaves the second slot with ID 2, but
                    // it's now the FIRST vJoy slot and should have ID 1. Without this
                    // fix, input mapped to the Nth vJoy slot writes to the wrong device.
                    if (descriptorCountChanged)
                    {
                        int expectedId = 0;
                        for (int i = 0; i < MaxPads; i++)
                        {
                            // Only count slots that actually have (or will have) a VC.
                            // Inactive vJoy slots don't get VCs and don't consume device IDs,
                            // so they must not inflate the expectedId sequence.
                            bool countsAsVjoy =
                                _virtualControllers[i] is VJoyVirtualController ||
                                (SlotControllerTypes[i] == VirtualControllerType.VJoy &&
                                 SettingsManager.SlotCreated[i] &&
                                 SettingsManager.SlotEnabled[i] &&
                                 IsSlotActive(i));

                            if (countsAsVjoy)
                            {
                                expectedId++;
                                if (_virtualControllers[i] is VJoyVirtualController vjCheck &&
                                    vjCheck.DeviceId != (uint)expectedId)
                                {
                                    VJoyVirtualController.DiagLog(
                                        $"ID ordering fix: pad{i} has ID {vjCheck.DeviceId}, expected {expectedId} — destroying for recreation");
                                    DestroyVirtualController(i);
                                    _virtualControllers[i] = null;
                                    anyNeedsCreate = true;
                                }
                            }
                        }
                    }
                }
            }
            AfterVJoySync:

            // --- Pass 1c: Ensure ViGEm VC ordering across cycles ---
            // ViGEm assigns XInput/DS4 indices based on Connect() call order.
            // When a lower-numbered slot needs a new VC but higher-numbered slots
            // already have same-type VCs (created in a previous cycle), the new VC
            // would get a higher index than the existing ones — wrong order.
            // Fix: destroy any same-type VCs at higher slot indices so they can be
            // recreated in ascending order alongside the new one in Pass 2.
            if (anyNeedsCreate)
            {
                for (int padIndex = 0; padIndex < MaxPads; padIndex++)
                {
                    // Is this slot about to create a ViGEm VC in Pass 2?
                    if (_virtualControllers[padIndex] != null ||
                        _slotInactiveCounter[padIndex] != 0 ||
                        _createCooldown[padIndex] > 0)
                        continue;

                    var newType = SlotControllerTypes[padIndex];
                    if (newType != VirtualControllerType.Xbox360 &&
                        newType != VirtualControllerType.DualShock4)
                        continue;

                    // Destroy any same-type VCs at higher slot indices.
                    for (int j = padIndex + 1; j < MaxPads; j++)
                    {
                        var existingVc = _virtualControllers[j];
                        if (existingVc != null && existingVc.Type == newType)
                        {
                            RumbleLogger.Log($"[Step5] Pad{j} destroying {newType} VC for ordering (Pad{padIndex} needs creation first)");
                            DestroyVirtualController(j);
                            _virtualControllers[j] = null;
                            // Don't touch _slotInactiveCounter — slot is still active,
                            // Pass 2 will recreate it in the correct order.
                        }
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
                        // vJoy slots only get a VC when a physical device is assigned.
                        // Unlike ViGEm (Xbox/DS4), vJoy device IDs are scarce and the
                        // registry descriptor count must match exactly. Creating a VC
                        // for an inactive vJoy slot would consume a device ID that the
                        // active slot needs.
                        if (SlotControllerTypes[padIndex] == VirtualControllerType.VJoy &&
                            !IsSlotActive(padIndex))
                            continue;

                        // Skip if still in cooldown from a previous failed creation.
                        if (_createCooldown[padIndex] > 0)
                        {
                            _createCooldown[padIndex]--;
                            continue;
                        }

                        RumbleLogger.Log($"[Step5] Pad{padIndex} creating {SlotControllerTypes[padIndex]} virtual controller (ordered)");
                        var vc = CreateVirtualController(padIndex);
                        _virtualControllers[padIndex] = vc;

                        if (vc != null && vc.IsConnected)
                            _slotInitializing[padIndex] = false;
                        else if (vc == null)
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
                    // Clear initializing flag once the controller is connected.
                    if (vc != null && vc.IsConnected && _slotInitializing[padIndex])
                        _slotInitializing[padIndex] = false;

                    if (vc != null && _slotInactiveCounter[padIndex] == 0)
                    {
                        // Custom vJoy slots use SubmitRawState for arbitrary axis/button counts.
                        // MIDI slots use SubmitMidiRawState for dynamic CC/note output.
                        // KBM slots use SubmitKbmState for keyboard/mouse output.
                        if (vc is VJoyVirtualController vjoyVc && SlotVJoyIsCustom[padIndex])
                            vjoyVc.SubmitRawState(CombinedVJoyRawStates[padIndex]);
                        else if (vc is MidiVirtualController midiVc)
                            midiVc.SubmitMidiRawState(CombinedMidiRawStates[padIndex]);
                        else if (vc is KeyboardMouseVirtualController kbmVc)
                            kbmVc.SubmitKbmState(CombinedKbmRawStates[padIndex]);
                        else if (vc is DS4VirtualController ds4Vc)
                            ds4Vc.SubmitGamepadState(CombinedOutputStates[padIndex], CombinedTouchpadStates[padIndex]);
                        else
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
        //  HIDMaestro context lifecycle (v3)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Lazily initialize the shared HMContext, load embedded profiles, and
        /// install the HIDMaestro driver if needed. Idempotent — safe to call
        /// every Start(). The caller must already be elevated; PadForge
        /// auto-elevates on launch when virtual device drivers are present.
        /// </summary>
        private void EnsureHMaestroContext()
        {
            if (_hmaestroContext != null || _hmaestroContextFailed)
                return;

            lock (_hmaestroContextLock)
            {
                if (_hmaestroContext != null || _hmaestroContextFailed)
                    return;

                try
                {
                    var ctx = new HMContext();
                    ctx.LoadDefaultProfiles();
                    ctx.InstallDriver();
                    _hmaestroContext = ctx;
                }
                catch (Exception ex)
                {
                    _hmaestroContextFailed = true;
                    RaiseError("Failed to initialize HIDMaestro.", ex);
                }
            }
        }

        /// <summary>
        /// Static check: is HIDMaestro available on this machine? Currently
        /// returns true if the embedded SDK can construct a context (which
        /// it always can — the driver, profiles, and signing tools all ship
        /// inside HIDMaestro.Core.dll). Reserved for future use if we ever
        /// detect a missing prerequisite.
        /// </summary>
        public static bool CheckHMaestroInstalled()
        {
            try
            {
                using var ctx = new HMContext();
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

            // vJoy, MIDI, and KeyboardMouse don't need ViGEm, but Xbox 360 and DS4 do.
            if (controllerType != VirtualControllerType.VJoy
                && controllerType != VirtualControllerType.Midi
                && controllerType != VirtualControllerType.KeyboardMouse
                && _vigemClient == null)
                return null;

            IVirtualController vc = null;
            try
            {
                // Snapshot XInput slot mask BEFORE connecting (Xbox 360 only).
                uint maskBefore = 0;
                if (controllerType == VirtualControllerType.Xbox360)
                    maskBefore = GetXInputConnectedSlotMask();

                vc = controllerType switch
                {
                    VirtualControllerType.DualShock4 => new DS4VirtualController(_vigemClient),
                    VirtualControllerType.VJoy => CreateVJoyController(),
                    VirtualControllerType.Midi => CreateMidiController(padIndex),
                    VirtualControllerType.KeyboardMouse => new KeyboardMouseVirtualController(padIndex),
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
                vc?.Dispose();
                RaiseError($"Failed to create {SlotControllerTypes[padIndex]} virtual controller for pad {padIndex}", ex);
                return null;
            }
        }

        /// <summary>
        /// Creates a vJoy virtual controller using the next available device ID.
        /// Device nodes are pre-provisioned by UpdateVirtualDevices() before this
        /// method is called, so this just finds a free ID and returns a controller.
        /// Returns null if vJoy driver is not installed or no free devices found.
        /// </summary>
        private IVirtualController CreateVJoyController()
        {
            VJoyVirtualController.DiagLog($"CreateVJoyController called");

            VJoyVirtualController.EnsureDllLoaded();
            if (!VJoyVirtualController.IsDllLoaded)
            {
                RaiseError("vJoy driver is not installed (vJoyInterface.dll not found).", null);
                return null;
            }

            uint deviceId = VJoyVirtualController.FindFreeDeviceId();
            VJoyVirtualController.DiagLog($"CreateVJoyController: FindFreeDeviceId={deviceId}");
            if (deviceId == 0)
            {
                RaiseError("No free vJoy devices after node creation.", null);
                return null;
            }
            return new VJoyVirtualController(deviceId);
        }

        /// <summary>
        /// Creates a MIDI virtual controller for the given pad slot.
        /// Reads port name and config from the PadViewModel's MidiConfig.
        /// Returns null if the configured port is not found.
        /// </summary>
        private IVirtualController CreateMidiController(int padIndex)
        {
            var midiConfig = _midiConfigs[padIndex];
            if (midiConfig == null) return null;

            if (!MidiVirtualController.IsAvailable())
            {
                RaiseError("Windows MIDI Services is not available. MIDI output requires Windows 11 with MIDI Services enabled.", null);
                return null;
            }

            // Compute 1-based MIDI instance number (count of MIDI slots up to and including this one)
            int midiInstanceNum = 0;
            for (int i = 0; i <= padIndex; i++)
                if (SlotControllerTypes[i] == VirtualControllerType.Midi)
                    midiInstanceNum++;

            var vc = new MidiVirtualController(padIndex, midiConfig.Channel - 1, midiInstanceNum);
            vc.CcNumbers = midiConfig.GetCcNumbers();
            vc.NoteNumbers = midiConfig.GetNoteNumbers();
            vc.Velocity = midiConfig.Velocity;
            return vc;
        }

        private void DestroyVirtualController(int padIndex)
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

            // Single-node model: only 1 ROOT\HIDCLASS device node ever exists.
            // No node trimming needed — the node stays alive for the session.
        }

        private void DestroyAllVirtualControllers(bool preserveVJoyNodes = false)
        {
            for (int i = 0; i < MaxPads; i++)
            {
                DestroyVirtualController(i);
                _virtualControllers[i] = null;
            }

            _activeVigemCount = 0;
            _activeXbox360Count = 0;
            _activeDs4Count = 0;

            if (preserveVJoyNodes)
            {
                // Disable the node instead of removing it. The DLL's internal device
                // handle stays valid for when the node is re-enabled via RestartDeviceNode.
                // This matches the EnsureDevicesAvailable(0) pattern.
                try { VJoyVirtualController.DisableDeviceNode(); } catch { }
            }
            else
            {
                // Full removal — for final app shutdown. Prevents orphaned nodes
                // from showing in joy.cpl after PadForge exits.
                try { VJoyVirtualController.RemoveAllDeviceNodes(); } catch { }
            }
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

                // Brief wait for PnP to fully process the removals before SDL init.
                Thread.Sleep(500);
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
