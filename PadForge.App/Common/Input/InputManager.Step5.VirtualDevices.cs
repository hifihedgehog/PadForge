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

        /// <summary>
        /// Set once <see cref="DisposeHMaestroContextOnShutdown"/> has run a
        /// full synchronous teardown inside OnClosing's Task.Run. The AppDomain
        /// ProcessExit handler checks this flag and skips its static
        /// <see cref="HMContext.RemoveAllVirtualControllers"/> call — otherwise
        /// the safety-net sweep enumerates the PnP tree after Close() returns
        /// and adds 5–6s of lingering headless work after the window vanishes.
        /// </summary>
        private static volatile bool _cleanShutdownPerformed;

        /// <summary>Virtual controller targets (one per slot).</summary>
        private IVirtualController[] _virtualControllers = new IVirtualController[MaxPads];

        /// <summary>
        /// Configured virtual controller category per slot (Microsoft / PlayStation /
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
        /// preset gamepad pipeline (Microsoft / PlayStation category) that maps
        /// through the Gamepad struct.
        /// </summary>
        internal bool[] SlotExtendedIsCustom { get; } = new bool[MaxPads];

        /// <summary>
        /// Per-slot flag: true if the user has toggled the Customize master
        /// checkbox in the Extended config bar. Gates every override path
        /// (custom ProductString, custom HID descriptor, OEM name override)
        /// so the VC is built from the catalog profile with no mutations
        /// when Customize is off. Layout counts in
        /// <see cref="SlotCustomLayouts"/> stay populated either way because
        /// Step 3 reads them to shape the raw-state mapping grid — zeroing
        /// them out would silently drop every button/axis mapping for
        /// non-customized Extended slots.
        /// </summary>
        internal bool[] SlotExtendedCustomize { get; } = new bool[MaxPads];

        /// <summary>
        /// Per-slot flag: true if this slot should claim the DirectInput
        /// OEM-name table for its profile's VID:PID on create. Mirrored from
        /// PadViewModel.ExtendedConfig.OemNameOverride by InputService.
        /// </summary>
        internal bool[] SlotOemOverrideEnabled { get; } = new bool[MaxPads];

        /// <summary>
        /// Per-slot label pushed to <see cref="HIDMaestro.HMOemNameOverride.Set"/>
        /// when <see cref="SlotOemOverrideEnabled"/> is true. Mirrored from
        /// PadViewModel.ExtendedConfig.ProductString.
        /// </summary>
        internal string[] SlotOemOverrideLabel { get; } = new string[MaxPads];

        /// <summary>
        /// Ref count of active OEM-name claims per (VID, PID) tuple. Multiple
        /// Extended slots can target the same profile; HMOemNameOverride is
        /// global per VID:PID, so we track refs and only call Clear when the
        /// last slot releases. Keyed as (vid &lt;&lt; 16) | pid.
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<uint, int> _oemOverrideRefs
            = new System.Collections.Generic.Dictionary<uint, int>();

        /// <summary>
        /// Per-slot record of the (VID, PID) this slot currently has an OEM
        /// claim on, so destroy can undo exactly what create applied even if
        /// the user edited the profile or flag in between. -1 when inactive.
        /// </summary>
        private readonly uint[] _oemOverrideClaimedVidPid = new uint[MaxPads];

        /// <summary>
        /// Per-slot snapshot of the ProductString that was baked into the
        /// active VC's HMProfile on create. Compared against the current
        /// <see cref="SlotOemOverrideLabel"/> in Pass 1 to detect when the
        /// user edited the Extended config bar's Product String field — a
        /// live edit triggers destroy + recreate so HIDMaestro rebuilds
        /// the virtual with the updated iProduct string.
        /// </summary>
        private readonly string[] _extendedAppliedProductString = new string[MaxPads];

        /// <summary>
        /// Per-slot snapshot of the stick/trigger/POV/button layout that was
        /// baked into the active VC's HID descriptor on create. Compared
        /// against <see cref="SlotCustomLayouts"/> in Pass 1 to detect a
        /// layout-count edit; mismatch triggers destroy + recreate so
        /// HIDMaestro regenerates the descriptor via HidDescriptorBuilder.
        /// </summary>
        private readonly CustomControllerLayout[] _extendedAppliedLayout = new CustomControllerLayout[MaxPads];

        /// <summary>
        /// Per-slot last-applied OEM override label, compared against the
        /// desired <see cref="SlotOemOverrideLabel"/> on each polling cycle
        /// to detect product-string edits that should re-push the claim.
        /// Null when no OEM claim is currently held for this slot.
        /// </summary>
        private readonly string[] _lastAppliedOemLabel = new string[MaxPads];

        /// <summary>
        /// Apply any user toggles of the Extended OEM-override checkbox or
        /// edits to the Product String field that happened since the last
        /// polling cycle. Works live, without destroying the VC — HIDMaestro's
        /// HMOemNameOverride is purely a DirectInput registry operation
        /// (joy.cpl label) and doesn't intersect with the device lifecycle.
        ///
        /// Decisions per slot:
        ///   - VC missing, has claim → Clear and drop claim (defensive; destroy
        ///     should already have done this, but catch orphans here too).
        ///   - VC present, desired enabled, no claim → Set and record claim.
        ///   - VC present, desired enabled, claim on different (VID,PID) → Clear
        ///     the old claim, Set the new one.
        ///   - VC present, desired enabled, same claim, label differs → Set again
        ///     (SDK replaces the label but preserves the first-capture's original
        ///     so a chain of Sets always restores to the pre-HIDMaestro state).
        ///   - VC present, desired disabled, has claim → Clear.
        ///   - Otherwise → no-op.
        /// </summary>
        private void ApplyLiveOemOverrideUpdates()
        {
            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                var vc = _virtualControllers[padIndex] as HMaestroVirtualController;
                uint claimed = _oemOverrideClaimedVidPid[padIndex];

                if (vc == null)
                {
                    if (claimed != 0)
                    {
                        // Orphaned claim — release it so refs don't leak.
                        ReleaseOemOverrideClaim(padIndex, claimed, "orphan-no-vc");
                    }
                    continue;
                }

                bool desiredEnabled = SlotOemOverrideEnabled[padIndex];
                string desiredLabel = SlotOemOverrideLabel[padIndex] ?? string.Empty;
                ushort vid = vc.ProfileVendorId;
                ushort pid = vc.ProfileProductId;
                uint desiredKey = ((uint)vid << 16) | pid;
                string lastLabel = _lastAppliedOemLabel[padIndex];

                bool wantClaim = desiredEnabled && !string.IsNullOrEmpty(desiredLabel) && vid != 0 && pid != 0;

                if (!wantClaim)
                {
                    if (claimed != 0)
                        ReleaseOemOverrideClaim(padIndex, claimed, "override-disabled");
                    continue;
                }

                if (claimed != desiredKey)
                {
                    if (claimed != 0)
                        ReleaseOemOverrideClaim(padIndex, claimed, "vidpid-changed");
                    TryAcquireOemOverrideClaim(padIndex, vid, pid, desiredLabel);
                    continue;
                }

                // Same VID:PID — only re-push if the label actually changed.
                if (!string.Equals(lastLabel, desiredLabel, StringComparison.Ordinal))
                {
                    try
                    {
                        HIDMaestro.HMOemNameOverride.Set(vid, pid, desiredLabel);
                        _lastAppliedOemLabel[padIndex] = desiredLabel;
                    }
                    catch (Exception ex)
                    {
                    }
                }
            }
        }

        private void TryAcquireOemOverrideClaim(int padIndex, ushort vid, ushort pid, string label)
        {
            try
            {
                HIDMaestro.HMOemNameOverride.Set(vid, pid, label);
                uint key = ((uint)vid << 16) | pid;
                _oemOverrideRefs.TryGetValue(key, out int n);
                _oemOverrideRefs[key] = n + 1;
                _oemOverrideClaimedVidPid[padIndex] = key;
                _lastAppliedOemLabel[padIndex] = label;
            }
            catch (Exception ex)
            {
            }
        }

        private void ReleaseOemOverrideClaim(int padIndex, uint claimedKey, string reason)
        {
            _oemOverrideClaimedVidPid[padIndex] = 0;
            _lastAppliedOemLabel[padIndex] = null;
            if (!_oemOverrideRefs.TryGetValue(claimedKey, out int n)) return;
            n--;
            if (n <= 0)
            {
                _oemOverrideRefs.Remove(claimedKey);
                try
                {
                    ushort vid = (ushort)(claimedKey >> 16);
                    ushort pid = (ushort)(claimedKey & 0xFFFF);
                    HIDMaestro.HMOemNameOverride.Clear(vid, pid);
                }
                catch (Exception ex)
                {
                }
            }
            else
            {
                _oemOverrideRefs[claimedKey] = n;
            }
        }


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

        /// <summary>
        /// Per-slot async-dispose tracker. When a user-initiated swap/move
        /// calls <see cref="DestroyVirtualController(int, bool)"/> with
        /// <c>asyncDispose: true</c>, the thread-pool task that runs
        /// <c>vc.Disconnect()</c> + <c>vc.Dispose()</c> is recorded here.
        /// Pass 2 (creation) skips an entire pass while any of these tasks
        /// are still running, so new VCs are only created once every old
        /// xinputhid / XUSB companion has released its kernel slot. This
        /// preserves ascending-slot-order creation: xinputhid's lowest-
        /// available-slot allocation returns the expected kernel slots
        /// rather than whatever happened to be free mid-teardown.
        /// </summary>
        private readonly System.Threading.Tasks.Task[] _pendingDisposeTask = new System.Threading.Tasks.Task[MaxPads];

        /// <summary>
        /// Per-slot async-connect tracker. Pass 2 hands the
        /// <c>CreateController</c> + <c>Connect</c> + <c>RegisterFeedbackCallback</c>
        /// chain to a thread-pool task and stores the task here so the
        /// polling thread is not blocked on HIDMaestro driver bring-up
        /// (multi-second per controller for Microsoft xinputhid). Pass 1
        /// and Pass 2 both gate on this so the slot isn't re-processed
        /// while creation is in flight, and so only one HM create runs
        /// at a time globally (xinputhid serializes internally; honoring
        /// that on our side keeps kernel-slot allocation predictable).
        /// </summary>
        private readonly System.Threading.Tasks.Task[] _pendingConnectTask = new System.Threading.Tasks.Task[MaxPads];

        /// <summary>Per-slot latch: HM inactivity timeout already fired for
        /// this slot in the current offline window.  Prevents the polling
        /// thread from re-firing the event every tick after the threshold
        /// is crossed.  Cleared when the slot returns to active state.</summary>
        private readonly bool[] _hmInactivityFired = new bool[MaxPads];

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
        /// (creating a virtual controller or reconfiguring Extended descriptors).
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

            // Apply any live changes to OEM-name overrides that the user
            // toggled or edited on an active Extended slot. This is
            // independent of VC lifecycle — HMOemNameOverride is purely a
            // DirectInput registry claim, no device rebuild required.
            ApplyLiveOemOverrideUpdates();

            // --- Pass 1: Handle type changes, destruction, and activity tracking ---
            bool anyNeedsCreate = false;

            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
                // Skip this slot entirely while an async connect is in
                // flight: the wrapper is being driven through Connect on a
                // thread-pool task, and any Pass 1 mutation here would race
                // with that.  Re-evaluate next polling cycle once the task
                // completes.
                {
                    var inFlight = _pendingConnectTask[padIndex];
                    if (inFlight != null && !inFlight.IsCompleted)
                        continue;
                }

                var vc = _virtualControllers[padIndex];

                // Detect controller type change — destroy old if type differs.
                if (vc != null && vc.Type != SlotControllerTypes[padIndex])
                {
                    // Set Initializing BEFORE the destroy+create blocks so the
                    // UI's 30Hz read sees the flag during the full transition
                    // window — Xbox teardown alone can take 5-11 seconds per
                    // the HIDMaestro README. Without this the UI misses the
                    // state entirely because Pass 2 clears the flag in the
                    // same poll cycle as Pass 1 sets it.
                    if (IsSlotActive(padIndex)) BeginInitializing(padIndex);
                    else _slotInitializing[padIndex] = false;
                    DestroyVirtualController(padIndex, asyncDispose: vc is HMaestroVirtualController);
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
                        // Flag BEFORE destroy (see type-change comment above).
                        if (IsSlotActive(padIndex)) BeginInitializing(padIndex);
                        else _slotInitializing[padIndex] = false;
                        DestroyVirtualController(padIndex, asyncDispose: true);
                        _virtualControllers[padIndex] = null;
                        _createFailed[padIndex] = false; // Profile change — allow retry
                        vc = null;
                    }
                }

                // Detect Extended config edits on an already-connected slot:
                // ProductString edited, or stick/trigger/POV/button counts
                // changed. Both require a rebuild because HIDMaestro bakes
                // iProduct and the HID descriptor at CreateController time.
                // Compare the current desired config against the snapshot
                // recorded when the VC was last created.
                if (vc is HMaestroVirtualController hmExtVc
                    && SlotControllerTypes[padIndex] == VirtualControllerType.Extended)
                {
                    string desiredPs = SlotOemOverrideLabel[padIndex] ?? string.Empty;
                    var desiredLayout = SlotCustomLayouts[padIndex];
                    bool psChanged = !string.Equals(
                        desiredPs,
                        _extendedAppliedProductString[padIndex] ?? string.Empty,
                        StringComparison.Ordinal);
                    var appliedLayout = _extendedAppliedLayout[padIndex];
                    bool layoutChanged =
                        desiredLayout.Sticks != appliedLayout.Sticks ||
                        desiredLayout.Triggers != appliedLayout.Triggers ||
                        desiredLayout.Povs != appliedLayout.Povs ||
                        desiredLayout.Buttons != appliedLayout.Buttons;

                    if (psChanged || layoutChanged)
                    {
                        if (IsSlotActive(padIndex)) BeginInitializing(padIndex);
                        else _slotInitializing[padIndex] = false;
                        DestroyVirtualController(padIndex, asyncDispose: true);
                        _virtualControllers[padIndex] = null;
                        _createFailed[padIndex] = false;
                        vc = null;
                    }
                }

                // Slot deleted or disabled by user — destroy immediately.
                // The grace period only applies to transient device disconnects
                // (slot still created + enabled, but physical device offline).
                if (vc != null && (!SettingsManager.SlotCreated[padIndex] || !SettingsManager.SlotEnabled[padIndex]))
                {
                    DestroyVirtualController(padIndex, asyncDispose: vc is HMaestroVirtualController);
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
                    _slotInactiveCounter[padIndex] = 0;
                    _hmInactivityFired[padIndex] = false;

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
                    DestroyVirtualController(padIndex, asyncDispose: vc is HMaestroVirtualController);
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

                    bool isHMaestro = vc is HMaestroVirtualController;

                    if (!isHMaestro
                        && vc != null
                        && _slotInactiveCounter[padIndex] >= SlotDestroyGraceCycles)
                    {
                        // Non-HM (MIDI, KeyboardMouse) destroy on the short
                        // grace counter — teardown is cheap and there's no
                        // kernel-slot ordering concern.
                        DestroyVirtualController(padIndex);
                        _virtualControllers[padIndex] = null;
                        VibrationStates[padIndex].LeftMotorSpeed = 0;
                        VibrationStates[padIndex].RightMotorSpeed = 0;
                    }
                    else if (isHMaestro
                             && vc != null
                             && HmInactivityTimeoutSeconds > 0
                             && !_hmInactivityFired[padIndex])
                    {
                        // HM inactivity timeout.  Setting=0 disables (legacy
                        // never-destroy behavior — slot survives indefinitely).
                        // Otherwise: convert seconds to polling cycles, fire
                        // event once when threshold is crossed, latch so we
                        // don't re-fire each tick.  UI thread handler runs
                        // DeleteSlot + InputService.OnSlotDeleted(rebuildHmVcs:true)
                        // which tears down this VC and bubbles surviving
                        // Microsoft HM VCs down to lower kernel slots without
                        // touching slots in any other group.  The latch
                        // clears whenever the slot returns to active state
                        // (counter reset above).
                        int hmThresholdCycles =
                            (HmInactivityTimeoutSeconds * 1000) / System.Math.Max(1, PollingIntervalMs);
                        if (_slotInactiveCounter[padIndex] >= hmThresholdCycles)
                        {
                            _hmInactivityFired[padIndex] = true;
                            VibrationStates[padIndex].LeftMotorSpeed = 0;
                            VibrationStates[padIndex].RightMotorSpeed = 0;
                            HmVcInactivityDestroyed?.Invoke(this, padIndex);
                        }
                    }
                }
            }

            // --- Pass 1.5: S1 ascending-index preemption ---
            // Spec S1: HIDMaestro initialization must proceed in strictly
            // ascending pad index per HM-backed subgroup. If a lower-indexed
            // pad is eligible-but-not-created while a higher-indexed pad in
            // the SAME subgroup already has a live VC, tear down the higher
            // one so Pass 2 recreates them in ascending order. Enable order
            // is irrelevant — only pad index matters.
            //
            // xinputhid assigns kernel slots in creation order: first-in gets
            // slot 0, second gets slot 1, etc. Downstream code (slot mask,
            // InstanceGuid, profile routing) assumes the (pad, slot) pairing
            // is canonical-ascending, so an out-of-order creation sequence
            // would compound into identity drift. Applied per HM subgroup
            // because HIDMaestro's internal controller index also tracks
            // creation order within each subgroup; MIDI and KeyboardMouse
            // skip this because they have no external ordering concern.
            anyNeedsCreate |= ApplyAscendingIndexPreemption();

            // --- Pass 2: Create virtual controllers ---
            // HIDMaestro assigns its own controller indices internally; we
            // don't need ViGEm-style sequential ordering or Extended device-node
            // pre-provisioning. Each slot creates its HMController on demand.
            //
            // Gate on any pending async-dispose tasks (from user-initiated
            // swap/move paths) completing first. xinputhid allocates the
            // lowest-available kernel slot per CreateController, so new VCs
            // must not be created while old ones are still releasing kernel
            // slots — that would produce out-of-order kernel assignments
            // relative to PadForge slot indices. Skipping this pass lets
            // polling continue unblocked while teardown finishes; Step 5
            // retries next cycle (~1ms later).
            bool anyDisposePending = false;
            for (int i = 0; i < MaxPads; i++)
            {
                var t = _pendingDisposeTask[i];
                if (t != null)
                {
                    if (!t.IsCompleted) { anyDisposePending = true; break; }
                    _pendingDisposeTask[i] = null;
                }
            }
            // Gate on async-connect tasks too: Pass 2 hands HM creates to
            // the thread pool so the polling thread stays free to feed
            // every other live VC during the ~3-11s HIDMaestro driver
            // bring-up.  We still serialize HM creates one at a time
            // (xinputhid's lowest-available kernel-slot allocation
            // depends on previous create having fully bound), so a
            // single in-flight connect blocks the next create until it
            // completes.
            bool anyConnectPending = false;
            for (int i = 0; i < MaxPads; i++)
            {
                var t = _pendingConnectTask[i];
                if (t != null)
                {
                    if (!t.IsCompleted) { anyConnectPending = true; break; }
                    _pendingConnectTask[i] = null;
                }
            }
            if (anyNeedsCreate && !anyDisposePending && !anyConnectPending)
            {
                for (int padIndex = 0; padIndex < MaxPads; padIndex++)
                {
                    if (_virtualControllers[padIndex] == null &&
                        _slotInactiveCounter[padIndex] == 0)
                    {
                        // All HIDMaestro-backed slots (Microsoft / PlayStation / Extended)
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
                             || slotType == VirtualControllerType.PlayStation
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


                        // For Xbox profiles: ensure HIDMaestro context is up
                        // (which runs RemoveAllVirtualControllers to clean
                        // stale devices from prior sessions) BEFORE taking
                        // the XInput slot snapshot. Otherwise the snapshot
                        // includes old virtuals and the delta detection can't
                        // find the new one.
                        bool isMsSlot = SlotControllerTypes[padIndex] == VirtualControllerType.Microsoft;
                        if (isMsSlot) EnsureHMaestroContext();

                        bool isHmSlot = slotType == VirtualControllerType.Microsoft
                                     || slotType == VirtualControllerType.PlayStation
                                     || slotType == VirtualControllerType.Extended;

                        if (isHmSlot)
                        {
                            // Visual-order gate: only kick off the create for
                            // the visually-highest eligible HM slot in this
                            // group. Lower-visual-position slots in the same
                            // group wait until every visually-higher one has
                            // been created, so xinputhid's creation-order
                            // kernel-slot allocation matches the user's
                            // visual ordering. ApplyAscendingIndexPreemption
                            // handles the teardown half (lower-visual-pos
                            // active VCs get torn down when a higher-pos slot
                            // transitions to active); this gate handles the
                            // recreate ordering.
                            var orderList = SettingsManager.SlotOrders.GetOrderFor(slotType);
                            int myVisualPos = orderList.IndexOf(padIndex);
                            bool higherStillNeeds = false;
                            for (int p = 0; p < myVisualPos; p++)
                            {
                                int pi = orderList[p];
                                if (pi < 0 || pi >= MaxPads) continue;
                                if (_virtualControllers[pi] != null) continue;
                                if (!SettingsManager.SlotCreated[pi]) continue;
                                if (!SettingsManager.SlotEnabled[pi]) continue;
                                if (_createFailed[pi]) continue;
                                if (!IsSlotActive(pi)) continue;
                                higherStillNeeds = true;
                                break;
                            }
                            if (higherStillNeeds) continue;

                            // Hand the CreateController + Connect chain to the
                            // thread pool.  HIDMaestro driver bring-up takes
                            // multi-second per controller for Microsoft xinputhid
                            // profiles, and running it on the polling thread
                            // freezes input submission for every other live VC
                            // for the duration.  The async path lets polling
                            // continue at 1 kHz; only the slot whose connect is
                            // in flight is skipped (vc.SubmitGamepadState early-
                            // returns when _controller is null, which is the
                            // case until Connect inside the task completes).
                            // Gating ensures one HM connect at a time globally,
                            // so xinputhid's kernel-slot ordering stays
                            // deterministic.  FinalizeNames is the PnP friendly-
                            // name fixup (test/Program.cs:199 pattern) and runs
                            // inline at the tail of the same task so it sees
                            // the just-bound controller.
                            int capturedIndex = padIndex;
                            _pendingConnectTask[padIndex] = System.Threading.Tasks.Task.Run(() =>
                            {
                                try
                                {
                                    var vcAsync = CreateVirtualController(capturedIndex);
                                    if (vcAsync != null && vcAsync.IsConnected)
                                    {
                                        _virtualControllers[capturedIndex] = vcAsync;
                                        try { _hmaestroContext?.FinalizeNames(); }
                                        catch { /* best effort */ }
                                    }
                                    else if (vcAsync == null)
                                    {
                                        _createFailed[capturedIndex] = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    RaiseError($"Failed to create virtual controller for pad {capturedIndex}", ex);
                                    _createFailed[capturedIndex] = true;
                                }
                                finally
                                {
                                    _slotInitializing[capturedIndex] = false;
                                }
                            });
                            // One HM connect kicked off per polling cycle.
                            // The pendingConnect gate above blocks the next
                            // cycle's Pass 2 from kicking off another until
                            // this one completes, preserving the
                            // ascending-kernel-slot allocation guarantee.
                            break;
                        }
                        else
                        {
                            // MIDI / KeyboardMouse — cheap construction, fine
                            // to run inline.  No HIDMaestro driver bring-up.
                            var vc = CreateVirtualController(padIndex);
                            _virtualControllers[padIndex] = vc;

                            if (vc != null && vc.IsConnected)
                            {
                                _slotInitializing[padIndex] = false;
                                break;
                            }
                            else if (vc == null)
                            {
                                _createFailed[padIndex] = true;
                                _slotInitializing[padIndex] = false;
                            }
                        }
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
                    }

                    if (vc != null && _slotInactiveCounter[padIndex] == 0)
                    {
                        // Log first submit after create so we can verify the
                        // input path is actually running for the new VC.
                        if (!_loggedFirstSubmit[padIndex])
                        {
                            _loggedFirstSubmit[padIndex] = true;
                        }
                        // MIDI slots use SubmitMidiRawState for dynamic CC/note output.
                        // KBM slots use SubmitKbmState for keyboard/mouse output.
                        // PlayStation slots whose HIDMaestro profile matches a
                        // Sony USB Report 0x01 layout submit a packed raw report
                        // alongside the Gamepad state so games see the full
                        // touchpad / gyro / accel / battery surface — fields
                        // HMGamepadState can't carry. Other Microsoft / PlayStation /
                        // Extended-non-custom slots use plain SubmitGamepadState.
                        if (vc is MidiVirtualController midiVc)
                            midiVc.SubmitMidiRawState(CombinedMidiRawStates[padIndex]);
                        else if (vc is KeyboardMouseVirtualController kbmVc)
                            kbmVc.SubmitKbmState(CombinedKbmRawStates[padIndex]);
                        else if (SlotControllerTypes[padIndex] == VirtualControllerType.Extended
                                 && SlotExtendedIsCustom[padIndex]
                                 && vc is HMaestroVirtualController hmExt)
                        {
                            // Extended with dynamic profile layout: mappings live
                            // in ExtendedRawState (ExtendedAxis{N}/ExtendedBtn{N}/
                            // ExtendedPov{N} target keys populated by Step 3/4)
                            // not the standard Gamepad. Submit the raw state
                            // directly to HIDMaestro so we cover the full
                            // HMGamepadState surface — 6 axes, 13 buttons, and
                            // hat — without the lossy 11-button XInput Gamepad
                            // bitmap intermediate.
                            var layout = SlotCustomLayouts[padIndex];
                            hmExt.SubmitExtendedRawState(
                                CombinedExtendedRawStates[padIndex],
                                layout.Sticks,
                                layout.Triggers);
                        }
                        else
                        {
                            vc.SubmitGamepadState(CombinedOutputStates[padIndex]);

                            if (SlotControllerTypes[padIndex] == VirtualControllerType.PlayStation
                                && vc is HMaestroVirtualController hmPs)
                            {
                                var packer = SonyReportPackers.ForProfile(hmPs.ProfileId);
                                if (packer != null)
                                {
                                    Span<byte> raw = stackalloc byte[63];
                                    var motion = MotionSnapshots[padIndex];
                                    byte battery = (byte)Math.Clamp(BatteryPercents[padIndex], 0, 100);
                                    byte connectState = motion.HasMotion ? (byte)0x08 : (byte)0x00;
                                    packer(
                                        CombinedOutputStates[padIndex],
                                        CombinedTouchpadStates[padIndex],
                                        motion,
                                        battery,
                                        connectState,
                                        unchecked((uint)_sonyFrameCounter++),
                                        raw);
                                    hmPs.SubmitRawReport(raw);
                                }
                            }
                        }
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
                    try { HMContext.RemoveAllVirtualControllers(); }
                    catch (Exception cleanEx)
                    {
                    }

                    var ctx = new HMContext();
                    int n = ctx.LoadDefaultProfiles();
                    ctx.InstallDriver();
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
                            if (_cleanShutdownPerformed) return;
                            try { HMContext.RemoveAllVirtualControllers(); } catch { }
                        };
                    }
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
        // ─────────────────────────────────────────────

        /// <summary>
        /// Default HIDMaestro profile slug for each category. Used when a
        /// slot has no explicit SlotProfileIds[] value (e.g. v2 settings
        /// migrated to v3, or a new slot created via the Add Controller
        /// popup before the user picks a preset). Real per-slot preset
        /// selection lands in a follow-up checkpoint.
        /// </summary>
        // xbox-series-xs-bt rather than xbox-360-wired so new Microsoft
        // slots work out of the box with browser-sourced force feedback.
        // Browsers using WGI or GameInput paths (Chrome on Win11 in
        // particular) don't route FFB to the Xbox 360 XUSB companion, so
        // xbox-360-wired vibrates in native games but stays silent for
        // browser "Vibration, infinite" tests. xbox-series-xs-bt uses the
        // HID output path that browsers drive reliably.
        public const string DefaultMicrosoftProfileId = "xbox-series-xs-bt";
        public const string DefaultPlayStationProfileId = "dualshock-4-v2";
        // The synthetic "Custom" entry anchors Extended — new slots start
        // there with Customize auto-enabled and the user fills in the
        // VID/PID/ProductString/layout from scratch. Previous catalog-
        // inheritance default (logitech-f710) would have new users pick
        // up Logitech VID/PID surprise-unexpectedly.
        public const string DefaultExtendedProfileId = HMaestroProfileCatalog.CustomProfileId;

        /// <summary>
        /// Returns the default HIDMaestro profile slug for a given VC category,
        /// or null for categories that don't use HIDMaestro (MIDI, KeyboardMouse).
        /// Used by both CreateVirtualController (engine-side fallback when
        /// SlotProfileIds is null) and DeviceService.CreateSlot (populates the
        /// ViewModel's ProfileId so the profile-picker dropdown shows the
        /// selected default immediately on slot create).
        /// </summary>
        public static string GetDefaultProfileId(VirtualControllerType type) => type switch
        {
            VirtualControllerType.Microsoft => DefaultMicrosoftProfileId,
            VirtualControllerType.PlayStation => DefaultPlayStationProfileId,
            VirtualControllerType.Extended => DefaultExtendedProfileId,
            _ => null
        };

        private IVirtualController CreateVirtualController(int padIndex)
        {
            var controllerType = SlotControllerTypes[padIndex];

            // MIDI and KeyboardMouse stay on their dedicated implementations.
            // Microsoft / PlayStation / Extended now route through HIDMaestro.
            if (controllerType == VirtualControllerType.Microsoft
                || controllerType == VirtualControllerType.PlayStation
                || controllerType == VirtualControllerType.Extended)
            {
                EnsureHMaestroContext();
                if (_hmaestroContext == null)
                {
                    return null;
                }
            }

            // Resolve the per-slot HIDMaestro profile slug, falling back to
            // the category default if the slot has no explicit selection.
            string slotProfileId = SlotProfileIds[padIndex];
            string profileId = !string.IsNullOrEmpty(slotProfileId)
                ? slotProfileId
                : GetDefaultProfileId(controllerType);

            IVirtualController vc = null;
            try
            {
                vc = controllerType switch
                {
                    VirtualControllerType.Microsoft => CreateHMaestroController(VirtualControllerType.Microsoft, profileId, padIndex),
                    VirtualControllerType.PlayStation => CreateHMaestroController(VirtualControllerType.PlayStation, profileId, padIndex),
                    VirtualControllerType.Extended => CreateHMaestroController(VirtualControllerType.Extended, profileId, padIndex),
                    VirtualControllerType.Midi => CreateMidiController(padIndex),
                    VirtualControllerType.KeyboardMouse => new KeyboardMouseVirtualController(padIndex),
                    _ => null
                };

                if (vc == null) return null;

                // Claim the DirectInput OEM-name table entry for this slot's
                // profile BEFORE Connect, so the label is in place before
                // Windows enumerates the new virtual device. The live-update
                // pass at the top of UpdateVirtualDevices handles subsequent
                // toggles and edits; this is the initial acquisition.
                if (controllerType == VirtualControllerType.Extended
                    && SlotOemOverrideEnabled[padIndex]
                    && vc is HMaestroVirtualController hmOem)
                {
                    ushort vid = hmOem.ProfileVendorId;
                    ushort pid = hmOem.ProfileProductId;
                    string label = SlotOemOverrideLabel[padIndex];
                    if (!string.IsNullOrEmpty(label) && vid != 0 && pid != 0)
                        TryAcquireOemOverrideClaim(padIndex, vid, pid, label);
                }

                vc.Connect();

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
        /// Constructs a HIDMaestro-backed virtual controller using the named
        /// embedded profile. The profile slug must match a profile shipped in
        /// HIDMaestro.Core's embedded catalog (225 profiles across 32 vendors).
        ///
        /// For Extended slots, applies per-slot customizations on top of the
        /// catalog profile via <see cref="HMProfileBuilder"/>:
        ///   - ProductString override drives the iProduct string reported to
        ///     games and Device Manager (separate from OEM-name override,
        ///     which targets DirectInput's registry table).
        ///   - Custom stick/trigger/POV/button counts regenerate the HID
        ///     report descriptor via <see cref="HidDescriptorBuilder"/> so
        ///     the virtual actually presents the requested layout to
        ///     downstream consumers. Without this, editing those fields
        ///     only re-shaped the PadForge mapping grid without affecting
        ///     the real device.
        /// </summary>
        private IVirtualController CreateHMaestroController(VirtualControllerType type, string profileId, int padIndex)
        {
            if (_hmaestroContext == null)
            {
                return null;
            }
            // Look up via HIDMaestro's catalog first (the 125+ real profiles).
            // Fall back to HMaestroProfileCatalog for PadForge-injected
            // synthetic entries like "padforge-custom" that HIDMaestro
            // doesn't know about — those are built at runtime via
            // HMProfileBuilder and only live in PadForge's wrapper catalog.
            var baseProfile = _hmaestroContext.GetProfile(profileId)
                           ?? HMaestroProfileCatalog.GetProfileById(profileId);
            if (baseProfile == null)
            {
                RaiseError($"HIDMaestro profile '{profileId}' not found.", null);
                return null;
            }

            HMProfile effectiveProfile = baseProfile;

            if (type == VirtualControllerType.Extended && SlotExtendedCustomize[padIndex])
            {
                string userProductString = SlotOemOverrideLabel[padIndex];
                bool productStringOverrides =
                    !string.IsNullOrEmpty(userProductString)
                    && !string.Equals(userProductString, baseProfile.ProductString, StringComparison.Ordinal);

                var layout = SlotCustomLayouts[padIndex];
                int userSticks = layout.Sticks;
                int userTriggers = layout.Triggers;
                int userPovs = layout.Povs;
                int userButtons = layout.Buttons;

                // Compare against the profile's declared layout. Extended
                // profiles have an AxisCount/ButtonCount/HasHat on HMProfile;
                // if any user value differs from what the catalog descriptor
                // declares, regenerate.
                int profSticks = Math.Min(baseProfile.AxisCount, 4) / 2;
                int profTriggers = Math.Max(0, baseProfile.AxisCount - profSticks * 2);
                int profPovs = baseProfile.HasHat ? 1 : 0;
                int profButtons = baseProfile.ButtonCount;

                bool layoutOverrides =
                    (userSticks > 0 || userTriggers > 0 || userPovs > 0 || userButtons > 0) &&
                    (userSticks != profSticks
                     || userTriggers != profTriggers
                     || userPovs != profPovs
                     || userButtons != profButtons);

                if (productStringOverrides || layoutOverrides)
                {
                    try
                    {
                        var builder = new HMProfileBuilder().FromProfile(baseProfile);

                        if (productStringOverrides)
                            builder.ProductString(userProductString);

                        if (layoutOverrides)
                        {
                            // Mirror BuildCustomProfile. AddPidFfbBlock emits the
                            // SDK's minimum-viable PID FFB descriptor and auto-
                            // injects the Report ID 0x01 prefix; FromDescriptorBuilder
                            // derives InputReportSize from the builder's bit count
                            // plus the Report ID byte. HM v1.1.41 (issue #16).
                            var descBuilder = new HidDescriptorBuilder().Joystick();
                            for (int s = 0; s < userSticks; s++)
                                descBuilder.AddStick(s == 0 ? "Left" : "Right", 16);
                            for (int t = 0; t < userTriggers; t++)
                                descBuilder.AddTrigger(t == 0 ? "Left" : "Right", 16);
                            if (userPovs > 0)
                                descBuilder.AddHat();
                            if (userButtons > 0)
                                descBuilder.AddButtons(userButtons);
                            descBuilder.AddPidFfbBlock();
                            builder.FromDescriptorBuilder(descBuilder);
                        }

                        effectiveProfile = builder.Build();
                    }
                    catch (Exception ex)
                    {
                        effectiveProfile = baseProfile;
                    }
                }
                else
                {
                }
            }
            else
            {
            }

            // Record what configuration this VC was built with so Pass 1 can
            // detect config deltas and trigger a rebuild when the user edits
            // the Extended override fields on a live slot.
            _extendedAppliedProductString[padIndex] = SlotOemOverrideLabel[padIndex] ?? string.Empty;
            _extendedAppliedLayout[padIndex] = SlotCustomLayouts[padIndex];

            return new HMaestroVirtualController(_hmaestroContext, effectiveProfile, type);
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
            => DestroyVirtualController(padIndex, asyncDispose: false);

        /// <summary>
        /// Public entry point for the bubble-up cascade in InputService.
        /// Tears down the slot's VC asynchronously so the polling thread is
        /// not blocked, and Pass 2 picks up the now-null slot to recreate
        /// once any pending dispose has finished.
        /// </summary>
        public void DestroyVirtualControllerAsync(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return;
            DestroyVirtualController(padIndex, asyncDispose: true);
        }

        /// <summary>
        /// Returns true if the slot currently holds a Microsoft-type HM
        /// virtual controller. Used by InputService.OnSlotDeleted's rebuild
        /// step to decide whether to tear down a higher-pad-index Microsoft
        /// VC after a Microsoft delete, so xinputhid bubbles it down to a
        /// lower kernel slot. PlayStation/Extended VCs don't bind to
        /// xinputhid kernel slots and don't need this rebuild.
        /// </summary>
        public bool IsMicrosoftHmVcAt(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return false;
            var vc = _virtualControllers[padIndex];
            return vc is HMaestroVirtualController hm
                && hm.Type == VirtualControllerType.Microsoft;
        }

        /// <summary>
        /// Enforces visual-order kernel-slot allocation within each HM group.
        /// When the lowest visual position whose pad index needs to be created
        /// has any visually-lower active VCs in the same group, those lower-
        /// position VCs are torn down so they recreate AFTER the now-active
        /// higher-position pad. xinputhid (and HIDMaestro's per-subgroup
        /// internal index) allocates kernel slots in creation order, so
        /// rebuilding lower-visual-position slots last gives them higher
        /// kernel slots than the visually-higher ones, keeping the visual
        /// order in sync with the kernel-slot order.
        ///
        /// Triggered every tick: catches inactive→active transitions
        /// (waiting slot gets a device assigned, disabled slot toggled back
        /// on) and visual-order changes via drag. Per the per-group spec,
        /// teardown happens regardless of whether the lower-position slots
        /// share a profile with the transitioning one.
        ///
        /// Async dispose used so the polling thread is not blocked on
        /// HIDMaestro teardown (up to ~11s for xinputhid profiles). Pass 2's
        /// pending-dispose gate already waits for every queued teardown to
        /// complete before starting a new creation, so the preempted slots'
        /// kernel resources are fully released before any rebuild kicks off.
        /// </summary>
        private bool ApplyAscendingIndexPreemption()
        {
            bool displacedAny = false;
            var hmSubgroups = new[]
            {
                VirtualControllerType.Microsoft,
                VirtualControllerType.PlayStation,
                VirtualControllerType.Extended,
            };

            foreach (var subgroup in hmSubgroups)
            {
                var orderList = SettingsManager.SlotOrders.GetOrderFor(subgroup);

                int lowestNeedsCreatePos = -1;
                for (int pos = 0; pos < orderList.Count; pos++)
                {
                    int padIndex = orderList[pos];
                    if (padIndex < 0 || padIndex >= MaxPads) continue;
                    if (_virtualControllers[padIndex] != null) continue;
                    if (!SettingsManager.SlotCreated[padIndex]) continue;
                    if (!SettingsManager.SlotEnabled[padIndex]) continue;
                    if (_createFailed[padIndex]) continue;
                    if (!IsSlotActive(padIndex)) continue;
                    lowestNeedsCreatePos = pos;
                    break;
                }

                if (lowestNeedsCreatePos < 0) continue;

                for (int pos = lowestNeedsCreatePos + 1; pos < orderList.Count; pos++)
                {
                    int padIndex = orderList[pos];
                    if (padIndex < 0 || padIndex >= MaxPads) continue;
                    if (_virtualControllers[padIndex] == null) continue;

                    // Lower-visual-position pad keeps its slot data and
                    // SlotCreated/SlotEnabled flags. Only its live VC is torn
                    // down; Pass 2 recreates it after the higher-position
                    // pad's VC has bound, so xinputhid assigns this VC a
                    // higher kernel slot.
                    if (IsSlotActive(padIndex)) BeginInitializing(padIndex);
                    DestroyVirtualController(padIndex, asyncDispose: true);
                    _createFailed[padIndex] = false;
                    displacedAny = true;
                }
            }

            return displacedAny;
        }

        /// <summary>
        /// Destroy the virtual controller at <paramref name="padIndex"/>.
        /// When <paramref name="asyncDispose"/> is true, the fast housekeeping
        /// (hook-mask clear, SDL-teardown watch arm) runs synchronously on the
        /// caller's thread, but the slow HIDMaestro teardown call
        /// (<c>vc.Disconnect()</c> + <c>vc.Dispose()</c>, up to ~11s for
        /// Microsoft xinputhid profiles) is queued to the thread pool. The
        /// <c>_virtualControllers[padIndex]</c> slot is cleared here so Step 5
        /// sees the slot as empty on its next pass.
        ///
        /// Used by user-initiated swap/move paths so the UI thread does not
        /// block on HIDMaestro teardown. Recreation is gated by the existing
        /// SDL-teardown observation watch in Step 5, so the new VC won't come
        /// up before the old device leaves the SDL list.
        /// </summary>
        private void DestroyVirtualController(int padIndex, bool asyncDispose)
        {
            var vc = _virtualControllers[padIndex];
            if (vc == null) return;

            _loggedFirstSubmit[padIndex] = false;

            // Release this slot's OEM-name claim, if it held one. Ref count
            // gates the actual HMOemNameOverride.Clear call so sibling slots
            // targeting the same profile keep the override active until the
            // last holder releases. Also resets the applied-config snapshot
            // so a subsequent recreate rebuilds from scratch.
            uint claimedKey = _oemOverrideClaimedVidPid[padIndex];
            if (claimedKey != 0)
                ReleaseOemOverrideClaim(padIndex, claimedKey, "destroy");
            _extendedAppliedProductString[padIndex] = null;
            _extendedAppliedLayout[padIndex] = default;

            if (asyncDispose)
            {
                // Null the pointer so Step 5 / Dashboard see the slot as empty
                // immediately. The captured `vc` is disposed in the background.
                // Track the task so Pass 2 can skip creation until every
                // pending dispose has finished — this preserves ascending-
                // slot-order kernel allocation.
                _virtualControllers[padIndex] = null;
                _pendingDisposeTask[padIndex] = System.Threading.Tasks.Task.Run(() =>
                {
                    try { vc.Disconnect(); vc.Dispose(); }
                    catch { /* best effort */ }
                });
            }
            else
            {
                try
                {
                    vc.Disconnect();
                    vc.Dispose();
                }
                catch { /* best effort */ }
            }
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
            _cleanShutdownPerformed = true;
        }

        private void DestroyAllVirtualControllers()
        {
            for (int i = 0; i < MaxPads; i++)
            {
                DestroyVirtualController(i);
                _virtualControllers[i] = null;
            }
        }

        /// <summary>
        /// Block until every pending HM lifecycle task (connect or async
        /// dispose) finishes, with a 30-second cap so a hung SDK call
        /// can't deadlock shutdown.  Called from InputManager.Stop right
        /// before DestroyAllVirtualControllers so any connect that's
        /// currently building a kernel device finishes and stores its VC,
        /// letting DestroyAllVirtualControllers see and tear it down
        /// properly instead of leaking it.
        /// </summary>
        private void AwaitPendingLifecycleTasks()
        {
            var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task>(MaxPads * 2);
            for (int i = 0; i < MaxPads; i++)
            {
                var dispose = _pendingDisposeTask[i];
                var connect = _pendingConnectTask[i];
                if (dispose != null) tasks.Add(dispose);
                if (connect != null) tasks.Add(connect);
            }
            if (tasks.Count == 0) return;

            try
            {
                System.Threading.Tasks.Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(30));
            }
            catch
            {
                // Best effort — proceed to teardown regardless.  Any
                // task that threw will have set _createFailed/etc. on
                // its slot, and the catch keeps shutdown progressing.
            }
        }

    }
}
