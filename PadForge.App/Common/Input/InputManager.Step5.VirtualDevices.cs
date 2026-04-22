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
        /// Snapshot of SDL joystick instance IDs taken at the moment a
        /// Microsoft-type virtual was destroyed. Null when no teardown is
        /// in progress. Step1 diffs the current SDL enumeration against
        /// this snapshot each cycle; when the set changes and then holds
        /// stable for <see cref="MsTeardownStableDebounceMs"/>, the
        /// xinputhid teardown is complete and Step1 fires the forced SDL
        /// handle close+reopen. This recovers from the identity-swap
        /// where SDL binds the physical Xbox to the virtual's stale
        /// InstanceGuid during teardown, without depending on an
        /// arbitrary wall-clock timer.
        /// </summary>
        private HashSet<uint> _msTeardownSdlSnapshot;

        /// <summary>Last-observed SDL instance ID set during an active teardown watch.
        /// Compared cycle-to-cycle to detect a stable post-teardown state.</summary>
        private HashSet<uint> _msTeardownLastObservedIds;

        /// <summary>UTC timestamp of the first cycle where the current SDL enumeration
        /// matched the previous cycle's enumeration AND differed from
        /// <see cref="_msTeardownSdlSnapshot"/>. When this timestamp is
        /// <see cref="MsTeardownStableDebounceMs"/> ms old, the reopen fires.</summary>
        private DateTime _msTeardownStableSince = DateTime.MinValue;

        /// <summary>Max wall-clock window to keep a teardown watch active. If no
        /// SDL-observable settle happens in this window, the watch is cleared
        /// without firing a reopen. Catches the edge case where xinputhid
        /// teardown produces no SDL-visible transition.
        /// Set generously to accommodate slow hardware: 5-10s is typical on
        /// modern machines; older CPUs / busy Windows kernels can take
        /// noticeably longer. This is a failsafe against a stuck watch, not
        /// a normal-case deadline — the watch should complete on SDL
        /// enumeration stability long before this fires.</summary>
        private const double MsTeardownWatchMaxSeconds = 60.0;
        private DateTime _msTeardownStartedAt = DateTime.MinValue;

        /// <summary>
        /// Per-XInput-slot pending-clear flag. True for any slot whose most
        /// recent Microsoft-type DestroyVirtualController has not yet had its
        /// hook mask bit released. Each slot debounces independently via
        /// <see cref="_xinputSlotEmptySince"/> so rapid consecutive destroys
        /// (e.g. user resorts multiple Microsoft virtuals in one batch) do
        /// not clobber one another. The hook mask bit stays set through the
        /// 5-10s xinputhid/XUSB teardown window to prevent SDL's XInput
        /// backend from seeing the dying-but-not-gone kernel device and
        /// enumerating it as a phantom (issue #11).
        /// </summary>
        private readonly bool[] _pendingXInputMaskClearSlots = new bool[4];

        /// <summary>Per-slot UTC timestamp of when this slot's pending-clear
        /// bit was armed. Used to enforce a hard upper bound
        /// (<see cref="PendingMaskHardCapMs"/>) on how long a slot can stay
        /// pending — without this, repeated destroys can leave stale bits
        /// in the hook mask that eventually cover a physical controller that
        /// xinputhid reshuffles onto an old virtual's slot.</summary>
        private readonly DateTime[] _pendingXInputMaskClearSetAt = { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };

        /// <summary>Per-slot first-observed UTC timestamp of the XInput slot
        /// reporting ERROR_DEVICE_NOT_CONNECTED via GetStateOriginal. When
        /// this has held for <see cref="XInputSlotEmptyDebounceMs"/> ms, we
        /// release that slot's pending mask bit. Reset whenever the slot
        /// comes back occupied (xinputhid transients during teardown).
        /// MinValue = not started. Using a short debounce, not a wall-clock
        /// deadline: short enough to be unnoticeable on fast hardware, long
        /// enough to reject xinputhid flicker.</summary>
        private readonly DateTime[] _xinputSlotEmptySince = { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };
        private const int XInputSlotEmptyDebounceMs = 1500;

        /// <summary>Per-slot (vid, pid) of the virtual that was destroyed and
        /// set the pending-clear flag. Lets the probe identify when the
        /// current occupant is clearly NOT the dying virtual (different
        /// VID/PID) and release the pending bit immediately — that's the
        /// xinputhid-reshuffled-physical case. Keeps the virtual-teardown
        /// window safe without gating all clears behind the hard cap.</summary>
        private readonly (ushort vid, ushort pid)[] _pendingXInputMaskClearProfile = new (ushort, ushort)[4];

        /// <summary>Hard upper bound on pending-mask lifetime. Must exceed
        /// HIDMaestro's longest xinputhid teardown (~11s per the SDK
        /// docstring) or the bit will drop while the kernel slot is still
        /// bound to the dying virtual, letting SDL enumerate the phantom.
        /// 15 s gives comfortable headroom and is still well under any
        /// reasonable "user expected this to be gone already" threshold.</summary>
        private const int PendingMaskHardCapMs = 15000;

        /// <summary>
        /// Clears the pending XInput mask bit for the given slot (or all
        /// pending slots if <paramref name="slot"/> is -1). Called by the
        /// XInput-slot-empty probe when that slot confirms empty, and as
        /// fallback (slot=-1) from the _msTeardown watch timeout / exception
        /// paths so stale bits can never leak into a permanent mask that
        /// would block a future real or virtual controller.
        /// </summary>
        private void ReleaseDeferredXInputMask(int slot = -1)
        {
            int cleared = 0;
            if (slot >= 0 && slot < 4)
            {
                if (!_pendingXInputMaskClearSlots[slot]) return;
                _pendingXInputMaskClearSlots[slot] = false;
                _xinputSlotEmptySince[slot] = DateTime.MinValue;
                _pendingXInputMaskClearSetAt[slot] = DateTime.MinValue;
                _pendingXInputMaskClearProfile[slot] = (0, 0);
                cleared = 1 << slot;
            }
            else
            {
                for (int s = 0; s < 4; s++)
                {
                    if (!_pendingXInputMaskClearSlots[s]) continue;
                    _pendingXInputMaskClearSlots[s] = false;
                    _xinputSlotEmptySince[s] = DateTime.MinValue;
                    _pendingXInputMaskClearSetAt[s] = DateTime.MinValue;
                    _pendingXInputMaskClearProfile[s] = (0, 0);
                    cleared |= 1 << s;
                }
                if (cleared == 0) return;
            }
            XInputHook.SetIgnoreSlotMask(
                XInputHook.IgnoreSlotMask & ~cleared);
        }

        /// <summary>Debounce window in milliseconds. Once SDL's joystick list has
        /// changed from the pre-destroy snapshot, it must hold stable for this long
        /// before we consider xinputhid teardown complete. 500 ms is ~500 polling
        /// cycles at 1 kHz. Short enough to feel instant, long enough to filter
        /// xinputhid's transient reshuffles during teardown.</summary>
        private const int MsTeardownStableDebounceMs = 500;

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
        /// (creating a virtual controller or reconfiguring Extended descriptors).
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
        /// as a belt-and-suspenders fallback — the primary filter is the
        /// XInputHook mask which prevents SDL from enumerating the slot at all.
        /// </summary>
        public bool IsHidMaestroXInputSlot(int xinputSlot)
        {
            if (xinputSlot < 0) return false;
            for (int i = 0; i < MaxPads; i++)
            {
                if (_hiddenXInputSlot[i] == xinputSlot) return true;
            }
            // Also honor the pending-clear window: the slot's HM HID child
            // is being torn down from PnP, but xinputhid's kernel binding
            // still reports the slot as connected. Without this, AuthMask
            // briefly clears the bit between PnP-gone and new-virtual-
            // created, letting SDL enumerate the dying virtual as a phantom.
            if (xinputSlot < 4 && _pendingXInputMaskClearSlots[xinputSlot])
                return true;
            return false;
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

            // Track xinputhid slot reshuffles for any active Microsoft virtual.
            // xinputhid reassigns slot numbers at runtime; our static mask set
            // at create time drifts and causes 2s on / 2s off input stalls on
            // a coexisting real Xbox controller. This call reads the live slot
            // from PnP and updates the mask whenever xinputhid has moved the
            // virtual to a different slot. See
            // InputManager.Step5.XInputSlotReconcile.cs for the full rationale.
            ReconcileMicrosoftVirtualSlots();

            // Event-driven SDL re-enumeration after a Microsoft-type teardown.
            // Poll SDL's joystick list and compare against the pre-destroy
            // snapshot. Once the list differs from the snapshot AND holds
            // stable across consecutive cycles for MsTeardownStableDebounceMs,
            // xinputhid is done rearranging and we fire the forced close+reopen.
            // No arbitrary wall-clock wait: the trigger is SDL's own observation
            // that its enumeration has stabilized. A hard max-window prevents a
            // watch from lingering forever if xinputhid produces no SDL-visible
            // transition.
            if (_msTeardownSdlSnapshot != null)
            {
                try
                {
                    uint[] currentArr = SDL3.SDL.SDL_GetJoysticks() ?? System.Array.Empty<uint>();
                    var current = new HashSet<uint>(currentArr);

                    bool changedFromSnapshot = !current.SetEquals(_msTeardownSdlSnapshot);
                    bool stableVsLast = current.SetEquals(_msTeardownLastObservedIds);

                    if (changedFromSnapshot && stableVsLast)
                    {
                        if (_msTeardownStableSince == DateTime.MinValue)
                        {
                            _msTeardownStableSince = DateTime.UtcNow;
                        }
                        else if ((DateTime.UtcNow - _msTeardownStableSince).TotalMilliseconds
                                 >= MsTeardownStableDebounceMs)
                        {
                            // SDL enumeration has settled — force SDL handle
                            // reopen so any stale joystick handles are dropped.
                            // NOTE: this is NOT the correct signal to release
                            // the deferred XInput mask; SDL "stable" can mean
                            // "stable WITH the phantom already enumerated"
                            // (issue #11 failure mode). Mask release is gated
                            // separately below on direct XInputGetStateOriginal
                            // probes of the pending slot.
                            _sdlJoysticksNeedReopen = true;
                            _msTeardownSdlSnapshot = null;
                            _msTeardownLastObservedIds = null;
                            _msTeardownStableSince = DateTime.MinValue;
                            _msTeardownStartedAt = DateTime.MinValue;
                        }
                    }
                    else
                    {
                        _msTeardownStableSince = DateTime.MinValue;
                    }

                    _msTeardownLastObservedIds = current;

                    if (_msTeardownSdlSnapshot != null
                        && (DateTime.UtcNow - _msTeardownStartedAt).TotalSeconds
                           > MsTeardownWatchMaxSeconds)
                    {
                        ReleaseDeferredXInputMask();
                        _msTeardownSdlSnapshot = null;
                        _msTeardownLastObservedIds = null;
                        _msTeardownStableSince = DateTime.MinValue;
                        _msTeardownStartedAt = DateTime.MinValue;
                    }
                }
                catch
                {
                    ReleaseDeferredXInputMask();
                    _msTeardownSdlSnapshot = null;
                    _msTeardownLastObservedIds = null;
                    _msTeardownStableSince = DateTime.MinValue;
                    _msTeardownStartedAt = DateTime.MinValue;
                }
            }

            // Authoritative mask-release probe. When a Microsoft-type VC is
            // torn down we defer the XInputHook mask clear until we observe
            // the underlying XInput slot actually report ERROR_DEVICE_NOT_CONNECTED
            // via the real (unhooked) XInputGetState. SDL-enumeration
            // stability isn't reliable for this — it can stabilize WITH the
            // phantom already visible to SDL, at which point releasing the
            // mask is too late. Polling the real XInput state bypasses SDL
            // entirely and reflects the xinputhid driver's actual slot
            // occupancy, so we release exactly when the slot is empty.
            // Debounced to reject xinputhid flicker during teardown.
            // Per-slot: rapid consecutive destroys during a resort can leave
            // multiple slots pending simultaneously; each debounces
            // independently so no pending clear is ever forgotten.
            if (XInputHook.IsInstalled)
            {
                const int ErrorDeviceNotConnected = 0x048F;
                var nowProbe = DateTime.UtcNow;
                for (int s = 0; s < 4; s++)
                {
                    if (!_pendingXInputMaskClearSlots[s]) continue;

                    // Hard time cap: accumulated pending clears across rapid
                    // consecutive destroys (swap / resort) must not live
                    // forever. If this slot has been pending longer than
                    // PendingMaskHardCapMs, force clear. DefensiveMask's
                    // identity-based loop in Step 1 will re-add the bit on
                    // the next cycle if the slot still holds a HM-only
                    // virtual — so this can't strand a virtual, but it
                    // guarantees a real controller that xinputhid reshuffled
                    // onto our pending slot won't stay masked.
                    if (_pendingXInputMaskClearSetAt[s] != DateTime.MinValue
                        && (nowProbe - _pendingXInputMaskClearSetAt[s]).TotalMilliseconds
                           >= PendingMaskHardCapMs)
                    {
                        ReleaseDeferredXInputMask(s);
                        continue;
                    }

                    int probeRc = XInputHook.GetStateOriginal(s, out _);
                    if (probeRc == ErrorDeviceNotConnected)
                    {
                        // Slot kernel-empty right now. DO NOT release the
                        // pending bit on this signal alone — xinputhid
                        // teardown flickers the slot empty transiently
                        // before final binding, and if we drop the bit here
                        // the subsequent re-occupancy is admitted by SDL
                        // as a phantom. Empty + bit-set is harmless — SDL
                        // sees DEVICE_NOT_CONNECTED either way. Release is
                        // gated purely on (a) identity-mismatch below, and
                        // (b) the hard cap above.
                        continue;
                    }

                    // Slot occupied. Safe identity release path: ONLY if
                    // CapsEx reports a different VID/PID than the destroyed
                    // virtual's profile did, we know the dying virtual has
                    // left and something else (physical or a newly-created
                    // virtual) has taken the slot. Same VID/PID is ambiguous
                    // — keep the pending bit in that case; only the 15 s
                    // hard cap releases it. An "ambiguous" slot staying
                    // masked for the full teardown window costs at most a
                    // brief physical-preview pause if xinputhid reshuffles
                    // the physical onto our pending slot, and Phase 2's 2 s
                    // disconnect debounce re-discovers it cleanly.
                    var profile = _pendingXInputMaskClearProfile[s];
                    if (profile.vid != 0 || profile.pid != 0)
                    {
                        if (PadForge.Engine.StableXInputInstance.TryGetXInputSlotVidPid(
                                (uint)s, out var svid, out var spid))
                        {
                            if (svid != profile.vid || spid != profile.pid)
                            {
                                // Different identity — dying virtual is gone,
                                // this is something else. Release pending.
                                ReleaseDeferredXInputMask(s);
                                continue;
                            }
                        }
                    }
                    // Same VID/PID or CapsEx unavailable — keep pending.
                }
            }

            // --- Pass 1: Handle type changes, destruction, and activity tracking ---
            bool anyNeedsCreate = false;

            for (int padIndex = 0; padIndex < MaxPads; padIndex++)
            {
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
                        // Flag BEFORE destroy (see type-change comment above).
                        if (IsSlotActive(padIndex)) BeginInitializing(padIndex);
                        else _slotInitializing[padIndex] = false;
                        DestroyVirtualController(padIndex);
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
                        DestroyVirtualController(padIndex);
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
                        DestroyVirtualController(padIndex);
                        _virtualControllers[padIndex] = null;
                        VibrationStates[padIndex].LeftMotorSpeed = 0;
                        VibrationStates[padIndex].RightMotorSpeed = 0;
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
            bool anyHMaestroCreatedThisPass = false;
            if (anyNeedsCreate && !anyDisposePending)
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

                        // Pre-mask the predicted slot BEFORE creation. xinputhid
                        // assigns the lowest-available slot to new virtuals, so
                        // the lowest slot currently reporting DEVICE_NOT_CONNECTED
                        // is where this virtual will land. Masking that slot
                        // through the XInput hook BEFORE the virtual materializes
                        // means SDL's XInput backend sees DEVICE_NOT_CONNECTED
                        // for the entire kernel-creation window — no device-add
                        // event ever fires into SDL's internal joystick list,
                        // so the virtual cannot appear as a phantom even for
                        // one cycle. Any slot our own mask already hides is
                        // treated as occupied (from SDL's perspective the
                        // slot is empty, but xinputhid sees the real device
                        // there and won't pick it for the new virtual).
                        int xiBeforeMask = 0;
                        int predictedSlot = -1;
                        int preMaskBit = 0;
                        if (isMsSlot && XInputHook.IsInstalled)
                        {
                            int hookMask = XInputHook.IgnoreSlotMask;
                            for (int s = 0; s < 4; s++)
                            {
                                bool occupied =
                                    (hookMask & (1 << s)) != 0
                                    || XInputHook.GetStateOriginal(s, out _) == 0;
                                if (occupied) xiBeforeMask |= (1 << s);
                            }
                            for (int s = 0; s < 4; s++)
                            {
                                if ((xiBeforeMask & (1 << s)) == 0)
                                {
                                    predictedSlot = s;
                                    break;
                                }
                            }
                            if (predictedSlot >= 0)
                            {
                                preMaskBit = 1 << predictedSlot;
                                XInputHook.SetIgnoreSlotMask(hookMask | preMaskBit);
                            }
                        }

                        var vc = CreateVirtualController(padIndex);
                        _virtualControllers[padIndex] = vc;

                        // Record the profile VID/PID in the session-wide HM
                        // known-VID/PID set BEFORE identity match runs. This
                        // is the authoritative source-of-truth AuthMask
                        // consults when HIDMaestro's HID child has left PnP
                        // but xinputhid's kernel slot binding lingers — PnP
                        // enumeration lags kernel state by several seconds,
                        // and without this memo the post-PnP-before-kernel
                        // window is exactly when phantoms slip through.
                        if (vc is HMaestroVirtualController hmRecordProf)
                        {
                            PadForge.Engine.StableXInputInstance.RememberHidMaestroProfile(
                                hmRecordProf.ProfileVendorId, hmRecordProf.ProfileProductId);
                        }

                        // Detect which XInput slot the virtual claimed and
                        // update the hook mask so SDL never sees it.
                        //
                        // Event-driven poll. Some profiles (notably
                        // xbox-360-wired, which uses the XUSB companion
                        // instead of xinputhid) take a variable amount of
                        // time after CreateController returns before the
                        // XUSB slot becomes queryable via XInputGetState.
                        // That delay is machine-specific and cannot be
                        // safely estimated with a fixed timer. Instead we
                        // poll GetStateOriginal until a fresh slot is
                        // detected (Pass 1 or Pass 2 below), breaking
                        // immediately on success.
                        //
                        // Primary signal: a slot that went from EMPTY
                        // (before) to OCCUPIED (after) with a low packet
                        // number (fresh virtual). Unambiguous even when a
                        // real same-VID/PID device is already bound, since
                        // the real's slot stays occupied in both snapshots.
                        //
                        // Fallback: if xinputhid reshuffled and no
                        // empty-to-occupied transition exists, pick the
                        // slot with the lowest packet number among slots
                        // occupied after. The fresh virtual starts near 0
                        // while any pre-existing real device has a higher
                        // count from ongoing XInput polling. The
                        // `lowestPkt < 200` gate rejects latching onto a
                        // pre-existing real slot before XUSB has attached.
                        if (isMsSlot && vc != null && vc.IsConnected && XInputHook.IsInstalled)
                        {
                            // Post-create slot detection by VID/PID identity.
                            //
                            // xinputhid can reshuffle existing devices when a
                            // new one is added — e.g. it may place the new
                            // virtual at slot 0 and displace the physical to
                            // slot 1. A pure "which bit flipped in the
                            // occupancy mask" delta misidentifies the
                            // displaced physical's NEW slot as the virtual
                            // and masks the physical out of SDL's view.
                            //
                            // Instead, query each slot's VID/PID via
                            // XInputGetCapabilitiesEx and find the slot whose
                            // CapsEx matches the profile's (VendorId,
                            // ProductId) AND either (a) wasn't reporting that
                            // same identity before, or (b) has a low packet
                            // count (fresh virtual signature). The virtual's
                            // profile VID/PID comes from HIDMaestro's
                            // canonical profile definition — it's what the
                            // virtual will spoof in its HID descriptor.
                            ushort profileVid = 0, profilePid = 0;
                            if (vc is HMaestroVirtualController hmProf)
                            {
                                profileVid = hmProf.ProfileVendorId;
                                profilePid = hmProf.ProfileProductId;
                            }

                            // Capture post-create VID/PID per slot.
                            var postVid = new ushort[4];
                            var postPid = new ushort[4];
                            var postOccupied = new bool[4];
                            var postPkt = new uint[4];
                            int xiAfterMask = 0;
                            for (int s = 0; s < 4; s++)
                            {
                                int rc = XInputHook.GetStateOriginal(s, out var stAfter);
                                if (rc == 0)
                                {
                                    postOccupied[s] = true;
                                    postPkt[s] = stAfter.dwPacketNumber;
                                    xiAfterMask |= (1 << s);
                                    if (PadForge.Engine.StableXInputInstance.TryGetXInputSlotVidPid((uint)s, out var v, out var p))
                                    {
                                        postVid[s] = v;
                                        postPid[s] = p;
                                    }
                                }
                            }
                            int newSlotsMask = xiAfterMask & ~xiBeforeMask;

                            // Pick the slot whose post-create identity matches
                            // the profile AND has the LOWEST packet counter.
                            //
                            // Packet count is the reliable signal: a
                            // just-created HIDMaestro virtual has `pkt` in
                            // single digits (one or two SubmitState calls
                            // at most); a controller the user has been
                            // interacting with carries a much higher pkt
                            // across disconnect/reshuffle. xinputhid can
                            // reshuffle existing devices during creation
                            // (e.g. moving a physical from slot 0 to slot 1
                            // when a new virtual arrives), which makes
                            // "newly-occupied slot" a misleading signal —
                            // the displaced physical shows up as a "new"
                            // slot even though it isn't the virtual.
                            int virtualSlot = -1;
                            long bestPkt = long.MaxValue;
                            for (int s = 0; s < 4; s++)
                            {
                                if (!postOccupied[s]) continue;
                                if (postVid[s] != profileVid || postPid[s] != profilePid) continue;
                                // Don't steal a slot another pad already claims.
                                bool claimed = false;
                                for (int p = 0; p < MaxPads; p++)
                                    if (p != padIndex && _hiddenXInputSlot[p] == s) { claimed = true; break; }
                                if (claimed) continue;

                                long pkt = (long)postPkt[s];
                                if (pkt < bestPkt)
                                {
                                    bestPkt = pkt;
                                    virtualSlot = s;
                                }
                            }

                            try
                            {
                                var sb = new System.Text.StringBuilder();
                                sb.AppendLine($"[Step5-create @ {DateTime.Now:HH:mm:ss.fff}] pad={padIndex} profile={(vc is HMaestroVirtualController hmDiag ? hmDiag.ProfileId : "?")} profileVidPid={profileVid:X4}/{profilePid:X4}");
                                sb.Append($"  slots:");
                                for (int s = 0; s < 4; s++)
                                    sb.Append($" s{s}={(postOccupied[s] ? $"{postVid[s]:X4}/{postPid[s]:X4}(pkt={postPkt[s]})" : "-")}");
                                sb.AppendLine();
                                sb.AppendLine($"  xiBefore=0x{xiBeforeMask:X} xiAfter=0x{xiAfterMask:X} newSlots=0x{newSlotsMask:X}");
                                sb.AppendLine($"  predicted={predictedSlot} actual={virtualSlot}");
                                sb.Append("  _hiddenXInputSlot before=[");
                                for (int _i = 0; _i < MaxPads; _i++) sb.Append((_i == 0 ? "" : ",") + _hiddenXInputSlot[_i]);
                                sb.AppendLine($"]  mask=0x{XInputHook.IgnoreSlotMask:X}");
                                System.IO.File.AppendAllText(
                                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padforge-sort-diag.log"),
                                    sb.ToString());
                            }
                            catch { }

                            if (virtualSlot >= 0)
                            {
                                _hiddenXInputSlot[padIndex] = virtualSlot;
                                int needMaskBit = 1 << virtualSlot;
                                int currentMask = XInputHook.IgnoreSlotMask;
                                // Correct the mask if prediction was wrong:
                                // drop the bit we pre-masked (if different)
                                // and add the bit for the actual slot.
                                int newMask = (currentMask & ~preMaskBit) | needMaskBit;
                                if (newMask != currentMask)
                                    XInputHook.SetIgnoreSlotMask(newMask);
                                _sdlJoysticksNeedReopen = true;
                            }
                            else if (preMaskBit != 0)
                            {
                                // No matching slot detected — drop the pre-mask
                                // so we don't leak it into a stale mask.
                                XInputHook.SetIgnoreSlotMask(
                                    XInputHook.IgnoreSlotMask & ~preMaskBit);
                            }
                        }
                        else if (preMaskBit != 0)
                        {
                            // CreateVirtualController returned null / not
                            // connected — undo the pre-mask.
                            XInputHook.SetIgnoreSlotMask(
                                XInputHook.IgnoreSlotMask & ~preMaskBit);
                        }

                        if (vc != null && vc.IsConnected)
                        {
                            _slotInitializing[padIndex] = false;
                            if (vc is HMaestroVirtualController)
                                anyHMaestroCreatedThisPass = true;

                            // Create at most one virtual per polling cycle.
                            // The remaining passes in THIS polling cycle
                            // (FinalizeNames below, then Pass 3) submit the
                            // first state frame to this newly-created VC.
                            // Only on the NEXT polling cycle does Pass 2
                            // revisit to create the next slot. That's the
                            // universal "wait until the previous is actually
                            // submit-ready" gate — works for Microsoft,
                            // Sony, Extended, MIDI, and KeyboardMouse
                            // without relying on XInput-specific signals.
                            break;
                        }
                        else if (vc == null)
                        {
                            _createFailed[padIndex] = true;
                            _slotInitializing[padIndex] = false;
                            // Creation failed. Don't break — try the next
                            // slot in the same pass. _createFailed keeps us
                            // from looping on this one.
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
                        _hmaestroContext.FinalizeNames();
                    }
                    catch (Exception ex)
                    {
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
                        // Everything else (Microsoft / PlayStation / Extended via HIDMaestro)
                        // submits the standard Gamepad state. The DS4 raw report path
                        // (touchpad/gyro) lives inside HMaestroVirtualController and is
                        // dispatched there based on the active profile.
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
                            var descBuilder = new HidDescriptorBuilder().Gamepad();
                            for (int s = 0; s < userSticks; s++)
                                descBuilder.AddStick(s == 0 ? "Left" : "Right", 16);
                            for (int t = 0; t < userTriggers; t++)
                                descBuilder.AddTrigger(t == 0 ? "Left" : "Right", 8);
                            if (userPovs > 0)
                                descBuilder.AddHat();
                            if (userButtons > 0)
                                descBuilder.AddButtons(userButtons);
                            byte[] descBytes = descBuilder.Build();
                            builder.Descriptor(descBytes);
                            builder.InputReportSize(descBuilder.InputReportByteSize);
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
        /// Enforces S1: within each HIDMaestro-backed subgroup (Microsoft /
        /// PlayStation / Extended), virtuals must be created in strictly
        /// ascending pad index. If a lower-indexed pad is eligible-but-not-
        /// created while a higher-indexed sibling in the same subgroup already
        /// has a live VC, destroy the higher one so it recreates AFTER the
        /// lower pad does in subsequent Pass 2 cycles. Returns true if any
        /// displacement occurred (signals Pass 2 that there is more work to
        /// do this round).
        ///
        /// Async dispose is used so the polling thread is not blocked on
        /// HIDMaestro teardown (up to ~11s for xinputhid profiles). The
        /// existing pending-dispose gate in Pass 2 already waits for every
        /// queued teardown to complete before starting a new creation, so
        /// the preempted slot's kernel resources are fully released before
        /// the displaced pad is rebuilt.
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
                int lowestNeedsCreate = -1;
                for (int i = 0; i < MaxPads; i++)
                {
                    if (SlotControllerTypes[i] != subgroup) continue;
                    if (_virtualControllers[i] != null) continue;
                    if (!SettingsManager.SlotCreated[i]) continue;
                    if (!SettingsManager.SlotEnabled[i]) continue;
                    if (_createFailed[i]) continue;
                    if (!IsSlotActive(i)) continue;
                    lowestNeedsCreate = i;
                    break;
                }

                if (lowestNeedsCreate < 0) continue;

                for (int i = lowestNeedsCreate + 1; i < MaxPads; i++)
                {
                    if (SlotControllerTypes[i] != subgroup) continue;
                    if (_virtualControllers[i] == null) continue;

                    // The displaced higher-index pad stays enabled; only
                    // its current VC is torn down. Pass 2 in a later cycle
                    // (after the pending-dispose gate releases) will recreate
                    // it — this time AFTER the lower-index pad, preserving
                    // ascending kernel-slot allocation.
                    if (IsSlotActive(i)) BeginInitializing(i);
                    DestroyVirtualController(i, asyncDispose: true);
                    _createFailed[i] = false;
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

            // Defer the XInputHook mask clear until HIDMaestro's teardown
            // has observably completed (the _msTeardown watch below fires
            // _sdlJoysticksNeedReopen when SDL's enumeration stabilizes).
            // Clearing the mask immediately on Dispose lets SDL's XInput
            // backend enumerate the dying-but-not-gone XUSB slot during
            // the 5-10s xinputhid teardown window — which surfaces as a
            // "Xbox 360 Controller at XInput#N" phantom (issue #11).
            // Clearing _hiddenXInputSlot[padIndex] now (so the pad is
            // marked free for a new VC on the same slot) while keeping
            // the mask bit set is safe: the bit follows the Microsoft-type
            // teardown state, not the pad state.
            int hiddenSlot = _hiddenXInputSlot[padIndex];
            if (hiddenSlot >= 0)
            {
                _hiddenXInputSlot[padIndex] = -1;
                if (vc.Type == VirtualControllerType.Microsoft && hiddenSlot < 4)
                {
                    // Arm the per-slot deferred clear. The probe block at the
                    // top of UpdateVirtualDevices watches each pending slot
                    // independently so this destroy cannot clobber another
                    // pending clear from an earlier destroy in the same
                    // resort batch.
                    _pendingXInputMaskClearSlots[hiddenSlot] = true;
                    _xinputSlotEmptySince[hiddenSlot] = DateTime.MinValue;
                    _pendingXInputMaskClearSetAt[hiddenSlot] = DateTime.UtcNow;

                    // Record the profile VID/PID of the destroyed virtual so
                    // the probe can distinguish "kernel slot still bound to
                    // the dying virtual" (same VID/PID) from "xinputhid
                    // reshuffled a real/other device onto this slot"
                    // (different VID/PID). Only the latter is a safe
                    // early-release signal.
                    if (vc is HMaestroVirtualController hmDying)
                    {
                        _pendingXInputMaskClearProfile[hiddenSlot] =
                            (hmDying.ProfileVendorId, hmDying.ProfileProductId);
                    }
                    else
                    {
                        _pendingXInputMaskClearProfile[hiddenSlot] = (0, 0);
                    }
                }
                else
                {
                    // Non-Microsoft VCs don't hit the xinputhid teardown
                    // race (they don't claim XInput slots), so the
                    // immediate mask clear that worked before stays.
                    XInputHook.SetIgnoreSlotMask(
                        XInputHook.IgnoreSlotMask & ~(1 << hiddenSlot));
                }
            }

            // If this is a Microsoft-type virtual (xinputhid or XUSB backend),
            // arm an SDL-observation watch. On each subsequent Step5 cycle, we
            // compare the current SDL joystick list against the snapshot taken
            // here. When it changes AND then holds stable for
            // `MsTeardownStableDebounceMs` ms, xinputhid teardown is observably
            // complete and we fire the forced SDL close+reopen. This replaces
            // the prior arbitrary-timer approach with an event-driven trigger
            // that scales with actual machine performance instead of a fixed
            // wall-clock delay.
            if (vc.Type == VirtualControllerType.Microsoft)
            {
                try
                {
                    uint[] current = SDL3.SDL.SDL_GetJoysticks() ?? System.Array.Empty<uint>();
                    _msTeardownSdlSnapshot = new HashSet<uint>(current);
                    _msTeardownLastObservedIds = new HashSet<uint>(current);
                }
                catch
                {
                    _msTeardownSdlSnapshot = new HashSet<uint>();
                    _msTeardownLastObservedIds = new HashSet<uint>();
                }
                _msTeardownStableSince = DateTime.MinValue;
                _msTeardownStartedAt = DateTime.UtcNow;
            }

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

    }
}
