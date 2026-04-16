using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
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

        /// <summary>
        /// Shared HIDMaestro context (one per process). Owns all HMController
        /// instances created by HMaestroVirtualController. Initialized lazily
        /// on first use; the embedded UMDF2 driver is installed via pnputil
        /// (idempotent) the first time CreateController is called.
        /// </summary>
        private static HMContext _hmaestroContext;
        private static readonly object _hmaestroContextLock = new object();
        private static bool _hmaestroContextFailed;
        private static bool _processExitHookRegistered;

        /// <summary>Virtual controller targets (one per slot).</summary>
        private IVirtualController[] _virtualControllers = new IVirtualController[MaxPads];

        /// <summary>
        /// Configured virtual controller category per slot (Microsoft / Sony /
        /// Extended / MIDI / KBM). The UI writes this via InputService at 30Hz;
        /// Step 5 reads it at ~1000Hz to detect type changes and recreate
        /// controllers accordingly.
        /// </summary>
        public VirtualControllerType[] SlotControllerTypes { get; } = new VirtualControllerType[MaxPads];

        /// <summary>
        /// Per-slot HIDMaestro profile slug. Identifies which of the 225
        /// embedded profiles the slot uses (e.g. "xbox-360-wired",
        /// "dualsense", "logitech-g920"). Empty string falls back to a
        /// category-appropriate default in CreateHMaestroController.
        /// Ignored for MIDI and KeyboardMouse slots.
        /// </summary>
        public string[] SlotProfileIds { get; } = new string[MaxPads];

        /// <summary>
        /// Per-slot HID descriptor layout (axis/button/POV counts) for the
        /// Extended virtual controller pipeline. Written by InputService from
        /// the slot's per-type config; read by Step 3 / Step 5 to translate
        /// per-mapping output into raw HID report indices.
        /// </summary>
        internal CustomControllerLayout[] SlotCustomLayouts { get; } = new CustomControllerLayout[MaxPads];

        /// <summary>
        /// Per-slot flag: true if this Extended slot uses the raw custom-axis
        /// pipeline (arbitrary axis/button/POV counts), false if it uses a
        /// preset gamepad pipeline (Microsoft / Sony category) that maps
        /// through the Gamepad struct.
        /// </summary>
        internal bool[] SlotExtendedIsCustom { get; } = new bool[MaxPads];

        /// <summary>
        /// Per-slot MIDI configuration snapshot. Written by InputService at 30Hz.
        /// Read by Step 5 to configure MIDI controllers on creation.
        /// </summary>
        internal MidiSlotConfig[] _midiConfigs = new MidiSlotConfig[MaxPads];

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
        /// Counts down each cycle; creation retries at 0. At ~1000Hz polling,
        /// 2000 cycles ≈ 2 seconds between retries.
        /// </summary>
        // Per-slot "creation failed" latch. Set when CreateVirtualController
        // returns null (HIDMaestro exception or early abort). Cleared only on
        // a meaningful state change — type switch, profile switch, or slot
        // toggle. Hammering creation in a tight retry loop is wrong for
        // HIDMaestro: SetupController already does its own adaptive waits
        // (WaitForHidChild 10s, WaitForDeviceStarted 5s, WaitForXInputSlotClaim
        // 15s) and a failure is a real failure, not a timing flake.
        private readonly bool[] _createFailed = new bool[MaxPads];

        // Debug: track "first submit after create" per slot so the lifecycle
        // log captures whether Pass 3 is actually reaching the new VC after a
        // profile change. Cleared whenever a VC is destroyed.
        private readonly bool[] _loggedFirstSubmit = new bool[MaxPads];

        // Per-slot XInput slot number that we're hiding via the hook.
        // -1 = no XInput slot claimed by the virtual at this pad index.
        // MUST be initialized to -1 (not default 0) — otherwise the
        // detection loop thinks slot 0 is "already hidden by another pad"
        // and skips it, causing the virtual at slot 0 to go undetected.
        private readonly int[] _hiddenXInputSlot = InitHiddenSlotArray();

        /// <summary>
        /// Set by Step 5 after updating the XInput hook mask. Step 1
        /// consumes this to close all SDL joystick handles so they get
        /// re-enumerated — SDL's XInput backend will now skip the masked
        /// slots and the virtual never enters PadForge's device list.
        /// </summary>
        internal bool _sdlJoysticksNeedReopen;

        private static int[] InitHiddenSlotArray()
        {
            var arr = new int[MaxPads];
            for (int i = 0; i < arr.Length; i++) arr[i] = -1;
            return arr;
        }

        /// <summary>
        /// Set by Step 5 whenever a new HIDMaestro virtual controller lands
        /// on an XInput slot. Creating a virtual XInput device invalidates
        /// <summary>
        /// Per-slot flag: true while a virtual controller is being created.
        /// Set true just before creation, cleared when the controller reports
        /// IsConnected. Read by the UI thread via
        /// <see cref="IsVirtualControllerInitializing"/>.
        /// </summary>
        private readonly bool[] _slotInitializing = new bool[MaxPads];

        // Minimum wall-clock time the initializing flag must remain true after
        // being set, so the UI overlay's "Initializing → Active" animation is
        // visible even when HIDMaestro creates a controller synchronously in
        // <10ms. Without this guard the flag flips in one poll cycle and the
        // overlay never gets to render the initializing stage.
        private void BeginInitializing(int padIndex)
        {
            _slotInitializing[padIndex] = true;
        }

        /// <summary>Whether virtual controller output is enabled.</summary>
        public bool VirtualControllersEnabled { get; set; } = true;

        /// <summary>
        /// Returns true if the specified pad slot has an active virtual controller.
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
        /// Returns true if the given XInput slot (0..3) is currently claimed
        /// by one of PadForge's HIDMaestro virtual controllers. Used by Step 1
        /// to filter SDL's synthetic XInput#N device paths — without this the
        /// virtual and the real Xbox both show up in the Devices list with
        /// non-deterministic slot ordering.
        /// </summary>
        public bool IsHidMaestroXInputSlot(int xinputSlot)
        {
            if (xinputSlot < 0) return false;
            var sb = new System.Text.StringBuilder();
            sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] IsHidMaestroXInputSlot({xinputSlot}) scan: ");
            bool match = false;
            for (int i = 0; i < MaxPads; i++)
            {
                if (_virtualControllers[i] is HMaestroVirtualController hm)
                {
                    sb.Append($"pad{i}={hm.ProfileId}/slot={hm.XInputSlot?.ToString() ?? "null"}; ");
                    if (hm.XInputSlot == xinputSlot)
                    {
                        match = true;
                    }
                }
            }
            sb.Append($"→ {(match ? "MATCH" : "NO MATCH")}\n");
            try { System.IO.File.AppendAllText(@"C:\PadForge\filter-debug.log", sb.ToString()); } catch { }
            return match;
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

            // --- Pass 1: Handle type changes, destruction, and activity tracking ---
            bool anyNeedsCreate = false;

            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                var vc = _virtualControllers[padIndex];

                // Detect controller type change — destroy old if type differs.
                if (vc != null && vc.Type != SlotControllerTypes[padIndex])
                {
                    VcLifecycleLog.Log($"pad{padIndex} DESTROY type-change {vc.Type}->{SlotControllerTypes[padIndex]}");
                    RumbleLogger.Log($"[Step5] Pad{padIndex} type changed {vc.Type}->{SlotControllerTypes[padIndex]}, recreating");
                    // Set Initializing BEFORE the destroy+create blocks so the
                    // UI's 30Hz read sees the flag during the full transition
                    // window — Xbox teardown alone can take 5-11 seconds per
                    // the HIDMaestro README. Without this the UI misses the
                    // state entirely because Pass 2 clears the flag in the
                    // same poll cycle as Pass 1 sets it.
                    if (IsSlotActive(padIndex)) BeginInitializing(padIndex);
                    else _slotInitializing[padIndex] = false;
                    DestroyVirtualController(padIndex);
                    _virtualControllers[padIndex] = null;
                    _createFailed[padIndex] = false; // Type change — allow retry
                    // The old profile slug belongs to the old category and is
                    // not valid for the new one. Clear it so CreateVirtualController
                    // falls back to the new category's default profile.
                    SlotProfileIds[padIndex] = null;
                    vc = null;
                }

                // Detect HIDMaestro profile change on an already-connected slot —
                // destroy so the next pass recreates with the new profile.
                if (vc is HMaestroVirtualController hmVc)
                {
                    string desired = SlotProfileIds[padIndex];
                    if (!string.IsNullOrEmpty(desired) && desired != hmVc.ProfileId)
                    {
                        VcLifecycleLog.Log($"pad{padIndex} DESTROY profile-change '{hmVc.ProfileId}'->'{desired}'");
                        RumbleLogger.Log($"[Step5] Pad{padIndex} profile changed {hmVc.ProfileId}->{desired}, recreating");
                        // Flag BEFORE destroy (see type-change comment above).
                        if (IsSlotActive(padIndex)) BeginInitializing(padIndex);
                        else _slotInitializing[padIndex] = false;
                        DestroyVirtualController(padIndex);
                        _virtualControllers[padIndex] = null;
                        _createFailed[padIndex] = false; // Profile change — allow retry
                        vc = null;
                    }
                }

                // Slot deleted or disabled by user — destroy immediately.
                // The grace period only applies to transient device disconnects
                // (slot still created + enabled, but physical device offline).
                if (vc != null && (!SettingsManager.SlotCreated[padIndex] || !SettingsManager.SlotEnabled[padIndex]))
                {
                    VcLifecycleLog.Log($"pad{padIndex} DESTROY slot-{(SettingsManager.SlotCreated[padIndex] ? "disabled" : "deleted")}");
                    RumbleLogger.Log($"[Step5] Pad{padIndex} slot {(SettingsManager.SlotCreated[padIndex] ? "disabled" : "deleted")}, destroying virtual controller immediately");
                    DestroyVirtualController(padIndex);
                    _virtualControllers[padIndex] = null;
                    _slotInactiveCounter[padIndex] = 0;
                    _slotInitializing[padIndex] = false;
                    _createFailed[padIndex] = false; // Slot toggle — allow retry
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
                        if (!_slotInitializing[padIndex]) BeginInitializing(padIndex);
                    }
                }
                else if (vc != null && !HasAnyDeviceMapped(padIndex))
                {
                    // No devices mapped to this slot — user explicitly unassigned
                    // all devices. Destroy immediately (not a transient disconnect).
                    VcLifecycleLog.Log($"pad{padIndex} DESTROY no-devices-mapped");
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

                    // Grace-period destroy applies to non-HIDMaestro virtual
                    // types (MIDI, KeyboardMouse). HIDMaestro VCs are NEVER
                    // destroyed by the inactive grace: teardown of an Xbox
                    // Series BT profile takes ~11 seconds, which creates a
                    // catastrophic destroy→real-reshuffles-back→create loop
                    // whenever xinputhid transiently invalidates SDL's view
                    // of the real controller. As long as the user's slot
                    // mapping exists (the !HasAnyDeviceMapped branch didn't
                    // fire above), keep the virtual alive. It will only be
                    // destroyed on explicit slot delete, slot disable, type
                    // change, or profile change.
                    bool isHMaestro = vc is HMaestroVirtualController;
                    if (!isHMaestro
                        && vc != null
                        && _slotInactiveCounter[padIndex] >= SlotDestroyGraceCycles)
                    {
                        VcLifecycleLog.Log($"pad{padIndex} DESTROY inactive-grace-elapsed (slotActive=false for {SlotDestroyGraceCycles} cycles, HasAnyDeviceMapped={HasAnyDeviceMapped(padIndex)})");
                        RumbleLogger.Log($"[Step5] Pad{padIndex} destroying virtual controller after {SlotDestroyGraceCycles} inactive cycles");
                        DestroyVirtualController(padIndex);
                        _virtualControllers[padIndex] = null;
                        VibrationStates[padIndex].LeftMotorSpeed = 0;
                        VibrationStates[padIndex].RightMotorSpeed = 0;
                    }
                }
            }

            // --- Pass 2: Create virtual controllers ---
            // HIDMaestro assigns its own controller indices internally; we
            // don't need ViGEm-style sequential ordering or vJoy device-node
            // pre-provisioning. Each slot creates its HMController on demand.
            bool anyHMaestroCreatedThisPass = false;
            if (anyNeedsCreate)
            {
                for (int padIndex = 0; padIndex < MaxPads; padIndex++)
                {
                    if (_virtualControllers[padIndex] == null &&
                        _slotInactiveCounter[padIndex] == 0)
                    {
                        // All HIDMaestro-backed slots (Microsoft / Sony / Extended)
                        // only get a VC when at least one assigned device is
                        // online. Unlike v2 ViGEm — which was cheap enough to
                        // spin up silent empty slots — HIDMaestro creation
                        // takes seconds per device (SetupController + driver
                        // bind), so empty slots must stay empty and present as
                        // "Awaiting devices" in the sidebar tooltip. MIDI and
                        // KeyboardMouse slots don't need device input to
                        // function and continue to create unconditionally.
                        var slotType = SlotControllerTypes[padIndex];
                        if ((slotType == VirtualControllerType.Microsoft
                             || slotType == VirtualControllerType.Sony
                             || slotType == VirtualControllerType.Extended)
                            && !IsSlotActive(padIndex))
                            continue;

                        // Skip if a prior attempt failed. HIDMaestro's
                        // CreateController does its own adaptive waits
                        // internally (WaitForHidChild/WaitForDeviceStarted/
                        // WaitForXInputSlotClaim — up to 30s combined), so
                        // fast-looping retries here accomplish nothing except
                        // hammering the driver. Only a user-driven change
                        // (profile switch, slot toggle) clears the latch.
                        if (_createFailed[padIndex])
                            continue;

                        RumbleLogger.Log($"[Step5] Pad{padIndex} creating {SlotControllerTypes[padIndex]} virtual controller (ordered)");

                        // For Xbox profiles: ensure HIDMaestro context is up
                        // (which runs RemoveAllVirtualControllers to clean
                        // stale devices from prior sessions) BEFORE taking
                        // the XInput slot snapshot. Otherwise the snapshot
                        // includes old virtuals and the delta detection can't
                        // find the new one.
                        bool isMsSlot = SlotControllerTypes[padIndex] == VirtualControllerType.Microsoft;
                        if (isMsSlot) EnsureHMaestroContext();

                        int xiBeforeMask = 0;
                        if (isMsSlot && XInputHook.IsInstalled)
                        {
                            // Give XInput time to notice the cleanup.
                            System.Threading.Thread.Sleep(500);
                            var bsb = new System.Text.StringBuilder("Before (post-cleanup): ");
                            for (int s = 0; s < 4; s++)
                            {
                                if (XInputHook.GetStateOriginal(s, out var bst) == 0)
                                {
                                    xiBeforeMask |= (1 << s);
                                    bsb.Append($"s{s}=pkt{bst.dwPacketNumber},LX{bst.Gamepad.sThumbLX} ");
                                }
                                else bsb.Append($"s{s}=empty ");
                            }
                            XInputHook.Log(bsb.ToString());
                        }

                        var vc = CreateVirtualController(padIndex);
                        _virtualControllers[padIndex] = vc;

                        // Detect which XInput slot the virtual claimed and
                        // update the hook mask so SDL never sees it.
                        if (isMsSlot && vc != null && vc.IsConnected && XInputHook.IsInstalled)
                        {
                            // Brief settle for XInput slot claim.
                            System.Threading.Thread.Sleep(500);
                            // The virtual always has the LOWEST packet number
                            // (freshly created, hasn't been polled). Bitmask
                            // delta can't detect slot REPLACEMENT (xinputhid
                            // may move the real to a different slot and give
                            // slot 0 to the virtual — both slots occupied
                            // before and after, delta = 0). Packet count is
                            // the only reliable signal.
                            int virtualSlot = -1;
                            uint lowestPkt = uint.MaxValue;
                            var sb = new System.Text.StringBuilder("After: ");
                            for (int s = 0; s < 4; s++)
                            {
                                if (XInputHook.GetStateOriginal(s, out var st) == 0)
                                {
                                    sb.Append($"s{s}=pkt{st.dwPacketNumber},LX{st.Gamepad.sThumbLX} ");
                                    // Only consider slots not already hidden by
                                    // a DIFFERENT pad's virtual controller.
                                    bool alreadyHidden = false;
                                    for (int p = 0; p < MaxPads; p++)
                                        if (p != padIndex && _hiddenXInputSlot[p] == s)
                                        { alreadyHidden = true; break; }

                                    if (!alreadyHidden && st.dwPacketNumber < lowestPkt)
                                    {
                                        lowestPkt = st.dwPacketNumber;
                                        virtualSlot = s;
                                    }
                                }
                                else sb.Append($"s{s}=empty ");
                            }
                            XInputHook.Log(sb.ToString());

                            if (virtualSlot >= 0 && lowestPkt < 200)
                            {
                                _hiddenXInputSlot[padIndex] = virtualSlot;
                                XInputHook.SetIgnoreSlotMask(
                                    XInputHook.IgnoreSlotMask | (1 << virtualSlot));
                                _sdlJoysticksNeedReopen = true;
                                XInputHook.Log($"Hiding XInput slot {virtualSlot} (pkt={lowestPkt}) for pad{padIndex}, mask=0x{XInputHook.IgnoreSlotMask:X}");
                            }
                            else
                            {
                                XInputHook.Log($"WARNING: no fresh virtual slot found (lowestPkt={lowestPkt}) for pad{padIndex}");
                            }
                        }
                        else if (isMsSlot && vc != null && vc.IsConnected && !XInputHook.IsInstalled)
                        {
                            XInputHook.Log($"WARNING: hook not installed, cannot hide XInput slot for pad{padIndex}");
                        }

                        if (vc != null && vc.IsConnected)
                        {
                            _slotInitializing[padIndex] = false;
                            if (vc is HMaestroVirtualController)
                                anyHMaestroCreatedThisPass = true;
                        }
                        else if (vc == null)
                        {
                            _createFailed[padIndex] = true;
                            _slotInitializing[padIndex] = false;
                        }
                    }
                }

                // PnP race fix: after creating one or more HIDMaestro
                // controllers in this round, wait for every live HID child to
                // reach DN_STARTED and re-apply friendly names. Matches the
                // HIDMaestro test app pattern (test/Program.cs:199), where the
                // SDK docstring explicitly calls out a Windows PnP race in
                // which the first controller's friendly name gets overwritten
                // by the second controller's driver-bind activity. The call
                // is adaptive — polls DN_STARTED and exits early when all
                // controllers are bound.
                if (anyHMaestroCreatedThisPass && _hmaestroContext != null)
                {
                    try
                    {
                        VcLifecycleLog.Log("FinalizeNames() after Pass 2 batch create");
                        _hmaestroContext.FinalizeNames();
                    }
                    catch (Exception ex)
                    {
                        VcLifecycleLog.Log($"FinalizeNames threw (non-fatal): {ex.Message}");
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
                    {
                        _slotInitializing[padIndex] = false;
                        VcLifecycleLog.Log($"pad{padIndex} Pass3 cleared _slotInitializing (vc connected)");
                    }

                    if (vc != null && _slotInactiveCounter[padIndex] == 0)
                    {
                        // Log first submit after create so we can verify the
                        // input path is actually running for the new VC.
                        if (!_loggedFirstSubmit[padIndex])
                        {
                            _loggedFirstSubmit[padIndex] = true;
                            VcLifecycleLog.Log($"pad{padIndex} Pass3 first submit ({vc.GetType().Name}, IsConnected={vc.IsConnected})");
                        }
                        // MIDI slots use SubmitMidiRawState for dynamic CC/note output.
                        // KBM slots use SubmitKbmState for keyboard/mouse output.
                        // Everything else (Microsoft / Sony / Extended via HIDMaestro)
                        // submits the standard Gamepad state. The DS4 raw report path
                        // (touchpad/gyro) lives inside HMaestroVirtualController and is
                        // dispatched there based on the active profile.
                        if (vc is MidiVirtualController midiVc)
                            midiVc.SubmitMidiRawState(CombinedMidiRawStates[padIndex]);
                        else if (vc is KeyboardMouseVirtualController kbmVc)
                            kbmVc.SubmitKbmState(CombinedKbmRawStates[padIndex]);
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
                    // Preflight: sweep any leftover HIDMaestro virtual devices
                    // from prior sessions (crash, forced kill, ungraceful exit).
                    // Without this, InstallDriver's internal RemoveOldDriverPackages
                    // step fails with "device using INF" because stale device nodes
                    // still reference the old driver package. Matches the HIDMaestro
                    // test app pattern (test/Program.cs:94) and SDK contract.
                    VcLifecycleLog.Log("EnsureHMaestroContext: RemoveAllVirtualControllers() preflight");
                    try { HMContext.RemoveAllVirtualControllers(); }
                    catch (Exception cleanEx)
                    {
                        VcLifecycleLog.Log($"  preflight RemoveAllVirtualControllers threw (non-fatal): {cleanEx.Message}");
                    }

                    VcLifecycleLog.Log("EnsureHMaestroContext: new HMContext()");
                    var ctx = new HMContext();
                    VcLifecycleLog.Log("EnsureHMaestroContext: LoadDefaultProfiles()");
                    int n = ctx.LoadDefaultProfiles();
                    VcLifecycleLog.Log($"  -> loaded {n} profiles");
                    VcLifecycleLog.Log("EnsureHMaestroContext: InstallDriver()");
                    ctx.InstallDriver();
                    VcLifecycleLog.Log("EnsureHMaestroContext: InstallDriver OK");
                    _hmaestroContext = ctx;

                    // Safety net: purge any devices we created if the process
                    // exits ungracefully without disposing HMController instances.
                    // Matches test/Program.cs:88-91. Registered exactly once per
                    // process since _hmaestroContext init is one-shot.
                    if (!_processExitHookRegistered)
                    {
                        _processExitHookRegistered = true;
                        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                        {
                            try { HMContext.RemoveAllVirtualControllers(); } catch { }
                        };
                    }
                }
                catch (Exception ex)
                {
                    VcLifecycleLog.Log($"EnsureHMaestroContext FAILED: {ex.GetType().Name}: {ex.Message}\n{ex}");
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
        // ─────────────────────────────────────────────

        /// <summary>
        /// Default HIDMaestro profile slug for each category. Used when a
        /// slot has no explicit SlotProfileIds[] value (e.g. v2 settings
        /// migrated to v3, or a new slot created via the Add Controller
        /// popup before the user picks a preset). Real per-slot preset
        /// selection lands in a follow-up checkpoint.
        /// </summary>
        private const string DefaultMicrosoftProfileId = "xbox-360-wired";
        private const string DefaultSonyProfileId = "dualshock-4-v2";
        private const string DefaultExtendedProfileId = "xbox-360-wired";

        private IVirtualController CreateVirtualController(int padIndex)
        {
            var controllerType = SlotControllerTypes[padIndex];

            // MIDI and KeyboardMouse stay on their dedicated implementations.
            // Microsoft / Sony / Extended now route through HIDMaestro.
            if (controllerType == VirtualControllerType.Microsoft
                || controllerType == VirtualControllerType.Sony
                || controllerType == VirtualControllerType.Extended)
            {
                EnsureHMaestroContext();
                if (_hmaestroContext == null)
                {
                    VcLifecycleLog.Log($"pad{padIndex} CreateVirtualController: _hmaestroContext is null (failed={_hmaestroContextFailed})");
                    return null;
                }
            }

            // Resolve the per-slot HIDMaestro profile slug, falling back to
            // the category default if the slot has no explicit selection.
            string slotProfileId = SlotProfileIds[padIndex];
            string profileId = !string.IsNullOrEmpty(slotProfileId)
                ? slotProfileId
                : controllerType switch
                {
                    VirtualControllerType.Microsoft => DefaultMicrosoftProfileId,
                    VirtualControllerType.Sony => DefaultSonyProfileId,
                    VirtualControllerType.Extended => DefaultExtendedProfileId,
                    _ => null
                };

            IVirtualController vc = null;
            try
            {
                vc = controllerType switch
                {
                    VirtualControllerType.Microsoft => CreateHMaestroController(VirtualControllerType.Microsoft, profileId),
                    VirtualControllerType.Sony => CreateHMaestroController(VirtualControllerType.Sony, profileId),
                    VirtualControllerType.Extended => CreateHMaestroController(VirtualControllerType.Extended, profileId),
                    VirtualControllerType.Midi => CreateMidiController(padIndex),
                    VirtualControllerType.KeyboardMouse => new KeyboardMouseVirtualController(padIndex),
                    _ => null
                };

                if (vc == null) return null;
                vc.Connect();

                vc.RegisterFeedbackCallback(padIndex, VibrationStates);

                return vc;
            }
            catch (Exception ex)
            {
                VcLifecycleLog.Log($"pad{padIndex} CREATE EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex}");
                vc?.Dispose();
                RaiseError($"Failed to create {SlotControllerTypes[padIndex]} virtual controller for pad {padIndex}", ex);
                return null;
            }
        }

        /// <summary>
        /// Constructs a HIDMaestro-backed virtual controller using the named
        /// embedded profile. The profile slug must match a profile shipped in
        /// HIDMaestro.Core's embedded catalog (225 profiles across 32 vendors).
        /// </summary>
        private IVirtualController CreateHMaestroController(VirtualControllerType type, string profileId)
        {
            if (_hmaestroContext == null)
            {
                VcLifecycleLog.Log($"CreateHMaestroController: _hmaestroContext is null");
                return null;
            }
            var profile = _hmaestroContext.GetProfile(profileId);
            if (profile == null)
            {
                VcLifecycleLog.Log($"CreateHMaestroController: GetProfile('{profileId}') returned null");
                RaiseError($"HIDMaestro profile '{profileId}' not found.", null);
                return null;
            }
            VcLifecycleLog.Log($"CreateHMaestroController: profile '{profileId}' resolved, constructing wrapper");
            return new HMaestroVirtualController(_hmaestroContext, profile, type);
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

            _loggedFirstSubmit[padIndex] = false;

            // Clear the XInput hook mask BEFORE teardown so HIDMaestro's
            // internal TeardownController can query XInput slots cleanly.
            int hiddenSlot = _hiddenXInputSlot[padIndex];
            if (hiddenSlot >= 0)
            {
                XInputHook.SetIgnoreSlotMask(
                    XInputHook.IgnoreSlotMask & ~(1 << hiddenSlot));
                _hiddenXInputSlot[padIndex] = -1;
            }

            try
            {
                vc.Disconnect();
                vc.Dispose();
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Explicitly disposes the long-lived static HMContext on app shutdown.
        /// Called from InputManager.Stop() AFTER DestroyAllVirtualControllers()
        /// so the synchronous HIDMaestro teardown (each Xbox Series BT profile
        /// takes ~11s per the README) runs inside OnClosing's Task.Run and
        /// keeps the shutdown overlay visible the whole time. Without this
        /// explicit call the actual teardown would happen in the AppDomain
        /// ProcessExit handler, which fires AFTER the window has closed —
        /// making it look like the window closed early with cleanup still
        /// running headless.
        /// </summary>
        private void DisposeHMaestroContextOnShutdown()
        {
            HMContext ctx;
            lock (_hmaestroContextLock)
            {
                ctx = _hmaestroContext;
                _hmaestroContext = null;
                _hmaestroContextFailed = false;
            }
            if (ctx != null)
            {
                try { ctx.Dispose(); }
                catch (Exception ex) { RaiseError("Error disposing HIDMaestro context", ex); }
            }
        }

        private void DestroyAllVirtualControllers()
        {
            for (int i = 0; i < MaxPads; i++)
            {
                DestroyVirtualController(i);
                _virtualControllers[i] = null;
            }
        }

        private static class VcLifecycleLog
        {
            private static readonly object _lock = new object();
            private const string Path = @"C:\PadForge\vc-lifecycle.log";

            public static void Log(string msg)
            {
                try
                {
                    lock (_lock)
                        System.IO.File.AppendAllText(Path,
                            $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
                }
                catch { }
            }
        }
    }
}
