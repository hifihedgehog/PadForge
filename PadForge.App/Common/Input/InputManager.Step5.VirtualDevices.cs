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
        //  Feeds combined Gamepad states to HIDMaestro virtual controllers
        //  (Xbox / PlayStation / Extended), plus MIDI and KB+M, via the
        //  IVirtualController abstraction.
        // ─────────────────────────────────────────────
        //
        //  VC mapping, sorting, and swapping rules
        //  ---------------------------------------
        //  These rules govern how PadForge maps pads to virtual controllers
        //  and what happens on reorder. They are the source of truth for
        //  any code in this file that touches _virtualControllers, the
        //  per-VC state arrays, or SettingsManager.SlotOrders.
        //
        //  (a) Pad indices are data identity.
        //      A pad's mappings, profile, devices, settings, and dirty
        //      flags live at its pad index and never move on reorder.
        //
        //  (b) Visual position is the kernel-slot anchor.
        //      Within an HM-backed group (Xbox / PlayStation / Extended),
        //      the VC at visual position V holds kernel slot V. The order
        //      list SlotOrders[group][V] = padIndex says which pad's data
        //      the VC at slot V is serving.
        //
        //  (c) Reorder repoints, not rebuilds.
        //      When the user drags or swaps within a group, SlotOrders
        //      mutates. The kernel VC at each visual position stays put;
        //      the pad-index pointer in _virtualControllers[] moves so
        //      the data at the new pad-at-position-V feeds into V's
        //      kernel slot.
        //
        //  (d) Same-profile reorders are zero-flicker.
        //      If pad-old-at-V and pad-new-at-V both want the same HM
        //      profile slug, the VC at slot V is reused. Pure pointer
        //      swap in _virtualControllers[] plus FeedbackPadIndex update
        //      on the moved VC. Per-VC state arrays follow the VC:
        //      _extendedAppliedProductString, _extendedAppliedLayout,
        //      _oemOverrideClaimedVidPid, _lastAppliedOemLabel.
        //
        //  (e) Different-profile positions destroy + recreate.
        //      Only the positions whose profile actually changed.
        //      Matching positions in the same reorder still pointer-swap.
        //
        //  (f) Per-pad state stays at pad index.
        //      _slotInactiveCounter, _createFailed, _hmInactivityFired,
        //      _slotInitializing, _pendingDisposeTask, _pendingConnectTask
        //      describe the pad's lifecycle, not the VC, so they don't
        //      move on reorder.
        //
        //  (g) Pass 2's visual-order gate and ApplyAscendingIndexPreemption
        //      handle fresh creates (a slot transitions to active for the
        //      first time) and recreates after profile-mismatch destroys.
        //      They don't run on the swap-only path.
        //
        //  (h) Non-HM groups (KBM, MIDI) skip the reroute logic.
        //      Their slot order isn't tied to a kernel-side index
        //      allocation.
        //
        //  (i) Cross-group moves go through MoveSlotToGroupTail, which
        //      relies on Pass 1 type-change detection to destroy the
        //      old-group VC; the new group's ordinary creation logic
        //      spins up the new VC at the tail. The reroute logic is
        //      intra-group only.
        //
        //  (j) Bubble-down cascade applies to every HM subgroup.
        //      When an HM-backed VC at position V transitions to
        //      non-active for any reason — slot deletion, sidebar
        //      disable, all devices unassigned, HM inactivity timeout
        //      — every surviving HM VC in the same subgroup at a
        //      strictly higher position is queued for async destroy
        //      via DestroyVirtualControllerAsync. Pass 2 then recreates
        //      them in ascending position order so each lands at a
        //      kernel slot one step lower than before. Applies to
        //      Xbox / PlayStation / Extended uniformly: external
        //      observers all care about creation order — xinputhid
        //      for Xbox, DirectInput / SDL / raw HID for the others.
        //      MIDI and KeyboardMouse have no kernel-slot ordering
        //      and skip the cascade entirely.
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

        /// <summary>
        /// Virtual controller targets, indexed by pad index. The VC at
        /// index P serves pad P's data, and (for HM-backed groups) its
        /// kernel slot equals pad P's current visual position within the
        /// group. Reorder updates these pointers in place; the kernel VC
        /// at each visual position stays put. See the rules block at the
        /// top of this file (rules b, c, d).
        /// </summary>
        private IVirtualController[] _virtualControllers = new IVirtualController[MaxPads];

        /// <summary>Read-only access to the per-pad virtual controller
        /// array for InputService's device-update hook so it can dispatch
        /// re-apply calls into HMaestroVirtualController on hot-plug.
        /// Null entries are slots without an active VC.</summary>
        public IVirtualController[] GetVirtualControllers() => _virtualControllers;

        /// <summary>The slot's inbound game-feedback pack (issue #236),
        /// resolved through the CURRENT <c>_virtualControllers</c> position
        /// so a reorder's array re-point retargets the read atomically
        /// with the VC move (the pack is controller-local; see
        /// HMaestroVirtualController.InboundRumblePack). 0 for empty /
        /// non-HM / freshly-created slots.</summary>
        public long GetInboundRumblePack(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return 0L;
            return (_virtualControllers[padIndex] as HMaestroVirtualController)
                ?.InboundRumblePack ?? 0L;
        }

        // ── Button SOCD (#240) ──
        // Per-slot cleaners for the final combined output, configured
        // lazily from the slot's MappingSet each tick (Configure is a
        // no-op on identical strings, the SocdCleaner contract, so the
        // per-tick refresh costs two string compares).
        private readonly SlotButtonSocd[] _slotButtonSocd = new SlotButtonSocd[MaxPads];

        /// <summary>Returns the slot's configured button-SOCD cleaner, or
        /// null when the slot authors no active SOCD. Poll thread only.</summary>
        private SlotButtonSocd ResolveSlotSocd(int padIndex, bool extendedIndices)
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || padIndex < 0 || padIndex >= sets.Length) return null;
            var ms = sets[padIndex];
            if (ms == null) return null;
            string mode = ms.SocdMode;
            string pairs = ms.SocdPairs;
            if (string.IsNullOrEmpty(mode) || string.IsNullOrEmpty(pairs)) return null;
            var socd = _slotButtonSocd[padIndex] ??= new SlotButtonSocd();
            socd.Configure(mode, pairs, extendedIndices);
            return socd.IsActive ? socd : null;
        }

        /// <summary>
        /// The dedicated slot-scoped feedback lane (issue #236): once per
        /// poll tick, per slot, evaluate the four fixed voice bindings
        /// (inbound pack masked by per-voice enables) and publish the
        /// result to RumbleAudioService. Runs INSIDE the non-idle poll
        /// loop only, so idle entry / engine stop must (and do) publish
        /// their own explicit silence edges. Deliberately NOT part of the
        /// per-device mapping pipeline: rumble is slot-global, points the
        /// opposite direction (game → VC, not device → VC), and must keep
        /// publishing zeros for unconfigured slots so a just-disabled
        /// config self-heals within one tick. Layer-independent in v1 (a
        /// shift layer must not silently kill shaker routing). Mapping
        /// math, four voice masks, one volatile store per slot; no
        /// allocations, no syscalls.
        /// </summary>
        private void UpdateRumbleAudioLane()
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null) return;
            int n = System.Math.Min(sets.Length, MaxPads);
            for (int slot = 0; slot < n; slot++)
            {
                int gen = RumbleAudioService.GetGeneration(slot);
                long pack = 0L;
                var cfg = sets[slot]?.RumbleAudio;
                if (cfg != null && cfg.Enabled)
                {
                    pack = GetInboundRumblePack(slot);
                    // The preview's test rumble buttons write
                    // VibrationStates directly and never cross the VC's
                    // inbound callback, so the pack alone leaves the
                    // shakers silent during a test while the physical pad
                    // shakes. Per-voice max of the slot's live vibration
                    // state closes that: test rumble becomes audible, and
                    // game rumble (which fills both sides with the same
                    // values) merges to itself.
                    var vibe = VibrationStates[slot];
                    if (vibe != null)
                    {
                        pack = Engine.Common.LfeOutputState.MaxMerge(pack,
                            vibe.LeftMotorSpeed, vibe.RightMotorSpeed,
                            vibe.LeftTriggerMotorSpeed, vibe.RightTriggerMotorSpeed);
                    }
                    if (pack != 0)
                    {
                        // Per-voice enable masks (the four fixed unipolar
                        // evals). Gain and carrier are render-side DSP.
                        var voices = cfg.Voices;
                        if (voices != null && voices.Count > 0)
                        {
                            for (int v = 0; v < voices.Count; v++)
                            {
                                var voice = voices[v];
                                if (voice == null || voice.Enabled) continue;
                                int idx = System.Array.IndexOf(
                                    Engine.Data.RumbleAudioConfig.SourceOrder, voice.Source);
                                if (idx >= 0)
                                    pack &= ~(0xFFFFL << (idx * 16));
                            }
                        }
                    }
                }
                RumbleAudioService.PublishIfCurrent(slot, gen, pack);
            }
        }

        /// <summary>
        /// Configured virtual controller category per slot (Xbox / PlayStation /
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
        /// preset gamepad pipeline (Xbox / PlayStation category) that maps
        /// through the Gamepad struct.
        /// </summary>
        internal bool[] SlotRawHidSurface { get; } = new bool[MaxPads];

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

        /// <summary>Per-slot VID/PID override (0 = use the active profile's value).
        /// Set by SyncExtendedConfigToSlot only when Customize is on; applied at
        /// VC-build time via HMProfileBuilder.Vid/.Pid in CreateHMaestroController.</summary>
        internal int[] SlotExtendedVendorId { get; } = new int[MaxPads];
        internal int[] SlotExtendedProductId { get; } = new int[MaxPads];

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
        /// Per-slot Extended FFB-enabled flag. Pushed from
        /// <c>ExtendedConfig.ForceFeedbackEnabled</c> by InputService. Default
        /// true: existing slots keep the HID PID 1.0 force-feedback descriptor
        /// block. False causes <see cref="CreateVirtualController"/> to take
        /// the custom-descriptor branch unconditionally and rebuild the HID
        /// descriptor without <c>HidDescriptorBuilder.AddPidFfbBlock()</c>.
        /// Only honored when <see cref="SlotExtendedCustomize"/> is true.
        /// </summary>
        internal bool[] SlotExtendedFfbEnabled { get; } = InitFfbEnabledArray();

        private static bool[] InitFfbEnabledArray()
        {
            var a = new bool[MaxPads];
            for (int i = 0; i < a.Length; i++) a[i] = true;
            return a;
        }

        /// <summary>
        /// Per-slot snapshot of the FFB-enabled flag baked into the active VC's
        /// HID descriptor on create. Compared against
        /// <see cref="SlotExtendedFfbEnabled"/> in Pass 1 to detect a toggle;
        /// mismatch triggers destroy + recreate so HIDMaestro regenerates the
        /// descriptor with or without the PID block to match.
        /// </summary>
        private readonly bool[] _extendedAppliedFfbEnabled = new bool[MaxPads];

        /// <summary>Applied VID/PID snapshot per slot. Mismatch vs the desired
        /// SlotExtendedVendorId/ProductId triggers destroy + recreate so
        /// HIDMaestro regenerates the descriptor with the new identity.</summary>
        private readonly int[] _extendedAppliedVendorId = new int[MaxPads];
        private readonly int[] _extendedAppliedProductId = new int[MaxPads];

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
                    catch (Exception)
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
            catch (Exception)
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
                catch (Exception)
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
        /// Per-slot KBM configuration reference (SOCD / Snap Tap, discussion
        /// #205). Written by InputService at 30Hz alongside _midiConfigs.
        /// Read by Pass 3, where the KBM controller's reference-compare fast
        /// path picks up edits live without a VC rebuild.
        /// </summary>
        internal KbmSlotConfig[] _kbmConfigs = new KbmSlotConfig[MaxPads];

        /// <summary>Per-slot PlayStation configuration reference (Adaptive
        /// Triggers + Lighting). Mirrors the slot's currently-selected
        /// device's per-device config — the Lighting tab is per-device,
        /// so this entry shifts as the user switches SelectedMappedDevice.
        /// Used by HMaestroVirtualController.AttachDeviceConfig as
        /// the dispatcher's "anchor" for animation-timer state and event
        /// subscriptions; per-device synthesis happens inside the
        /// dispatcher via <see cref="_perDeviceSlotConfigs"/>.
        /// Null entries skip Feature B effect synthesis on that slot.</summary>
        internal DeviceSlotConfig[] _deviceSlotConfigs = new DeviceSlotConfig[MaxPads];

        /// <summary>Per-(slot, device) lighting configs. Lookup keyed by
        /// physical device InstanceGuid. Source of truth for the
        /// dispatcher's per-device synthesis loop and for macro
        /// lightbar fan-out (every assigned device gets the same
        /// override / mode write). Mirrored from
        /// <c>PadViewModel.PerDeviceSlotConfigs</c> by
        /// <c>InputService.SyncViewModelToPadSettings</c>.</summary>
        internal IReadOnlyDictionary<Guid, DeviceSlotConfig>[] _perDeviceSlotConfigs
            = new IReadOnlyDictionary<Guid, DeviceSlotConfig>[MaxPads];

        // Parallel non-HM dispatcher ownership. HM-backed slots get their
        // UserEffectsDispatcher created inside HMaestroVirtualController.
        // AttachDeviceConfig and disposed in HM's Disconnect — that
        // lifecycle is untouched. For KBM / MIDI slots there's no HM VC,
        // so without a parallel owner here Sony pads mapped to those slots
        // would have NO writer at all (Step 2 ApplyForceFeedback skips Sony
        // VID/PID, expecting the dispatcher to handle them). This array
        // gives those slots a dispatcher of their own, registered in the
        // same static _instances map the polling-tick poke reads.
        private readonly UserEffectsDispatcher[] _nonHmDispatchers
            = new UserEffectsDispatcher[MaxPads];

        /// <summary>Non-HM mirror of
        /// <see cref="HMaestroVirtualController.AttachDeviceConfig"/>.
        /// Rebinds the slot's parallel dispatcher to
        /// <paramref name="config"/> so it follows the slot's DeviceConfig
        /// anchor when that anchor is REPLACED (device-selection switch,
        /// profile apply). The dispatcher holds a direct PropertyChanged
        /// subscription to the instance it was built with, so without this
        /// it stays wired to the orphaned config and every later lighting /
        /// trigger edit on a KBM / MIDI slot is silently dropped.
        /// Never constructs: a null entry means the slot is HM-backed (the
        /// HM VC owns that dispatcher) or has no VC at all, and building one
        /// here would give an HM slot a second writer.</summary>
        public void AttachNonHmDeviceConfig(int padIndex, DeviceSlotConfig config)
        {
            if (config == null) return;
            if (padIndex < 0 || padIndex >= _nonHmDispatchers.Length) return;
            _nonHmDispatchers[padIndex]?.Rebind(config);   // Rebind runs ApplyOnce internally
        }

        /// <summary>Re-binds each live non-HM dispatcher to its slot's
        /// current config anchor and fires an apply pass. Called from
        /// InputService's DevicesUpdated handler so a Sony pad reconnecting
        /// on a KBM / MIDI slot gets its lightbar / triggers / mic LED
        /// re-pushed, matching the HM-side AttachDeviceConfig +
        /// <see cref="HMaestroVirtualController.ReApplyUserEffects"/> pair.
        /// The rebind leg matters when the anchor was replaced while the
        /// pad was away. An ApplyOnce alone would re-push from the stale
        /// config.</summary>
        public void ReApplyNonHmUserEffects()
        {
            for (int i = 0; i < _nonHmDispatchers.Length; i++)
            {
                var d = _nonHmDispatchers[i];
                if (d == null) continue;
                var cfg = _deviceSlotConfigs[i];
                if (cfg != null) d.Rebind(cfg);   // Rebind runs ApplyOnce internally
                else d.ApplyOnce();
            }
        }

        /// <summary>
        /// Tracks how many consecutive polling cycles each slot has been inactive.
        /// Virtual controllers are only destroyed after a sustained inactivity period
        /// to prevent transient <see cref="IsSlotActive"/> false returns from
        /// destroying/recreating controllers (which kills vibration feedback).
        /// </summary>
        private readonly int[] _slotInactiveCounter = new int[MaxPads];

        // The former non-HM short grace (SlotDestroyGraceCycles, 10 s) is
        // retired: every VC type now rides the same HmInactivityTimeoutSeconds
        // contract (60 s default, 0 = never), so a flaky assigned device is
        // masked identically regardless of slot type.

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
        /// Step 5: Feed each slot's combined gamepad state to its virtual
        /// controller (HIDMaestro for Xbox / PlayStation / Extended, plus
        /// MIDI and KB+M). Receives vibration feedback from games via the
        /// virtual controller.
        ///
        /// Uses a grace period before destroying inactive virtual controllers to
        /// prevent transient IsSlotActive(false) from killing vibration feedback.
        /// Destroying a virtual controller severs the game's vibration connection
        /// (FeedbackReceived stops firing), and recreating it requires the game to
        /// rediscover the controller and re-send XInputSetState — causing a gap.
        ///
        /// Virtual controllers are created in ascending slot order so the
        /// kernel assigns sequential indices matching the PadForge slot numbers.
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
                // ProductString edited, stick/trigger/POV/button counts
                // changed, or the force-feedback toggle flipped. Each requires
                // a rebuild because HIDMaestro bakes iProduct and the HID
                // descriptor at CreateController time. Compare the current
                // desired config against the snapshot recorded when the VC was
                // last created.
                if (vc is HMaestroVirtualController hmExtVc
                    && SlotControllerTypes[padIndex] == VirtualControllerType.Extended)
                {
                    string desiredPs = SlotOemOverrideLabel[padIndex] ?? string.Empty;
                    var desiredLayout = SlotCustomLayouts[padIndex];
                    bool desiredFfb = SlotExtendedFfbEnabled[padIndex];
                    int desiredVid = SlotExtendedVendorId[padIndex];
                    int desiredPid = SlotExtendedProductId[padIndex];
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
                    bool ffbChanged = desiredFfb != _extendedAppliedFfbEnabled[padIndex];
                    bool vidPidChanged =
                        desiredVid != _extendedAppliedVendorId[padIndex]
                        || desiredPid != _extendedAppliedProductId[padIndex];

                    if (psChanged || layoutChanged || ffbChanged || vidPidChanged)
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

                // Slot deleted or disabled with NO live VC: the destroy
                // path above never runs (vc is null), but the
                // Initializing/create-failed latches may still be armed
                // (failed create, or the slot went away mid-create).
                // Left set, the flag paints an eternal flashing
                // "Initializing" on the dead card, and CreateSlot's
                // pad-index reuse hands both latches to the next slot
                // born at this index.
                if (vc == null
                    && (!SettingsManager.SlotCreated[padIndex]
                        || !SettingsManager.SlotEnabled[padIndex]))
                {
                    _slotInitializing[padIndex] = false;
                    _createFailed[padIndex] = false;
                }

                bool slotActive = IsSlotActive(padIndex);

                if (slotActive)
                {
                    _slotInactiveCounter[padIndex] = 0;
                    _hmInactivityFired[padIndex] = false;

                    if (vc == null)
                    {
                        if (_createFailed[padIndex])
                        {
                            // Creation is latched off after a failed HM
                            // create; Pass 2 will not retry until a
                            // user-driven change clears the latch.
                            // Re-arming Initializing here painted an
                            // eternal flashing "Initializing" for a VC
                            // that will never arrive. Show rest instead.
                            _slotInitializing[padIndex] = false;
                        }
                        else
                        {
                            anyNeedsCreate = true;
                            if (!_slotInitializing[padIndex]) BeginInitializing(padIndex);
                        }
                    }
                }
                else if (vc != null
                         && (!HasAnyDeviceMapped(padIndex)
                             || !SettingsManager.SlotEnabled[padIndex]))
                {
                    // Two deliberate user-driven non-active transitions:
                    //  - All mapped devices explicitly unassigned (user
                    //    cleared the slot's mapping panel).
                    //  - Slot disabled via the sidebar power toggle
                    //    (SlotEnabled flipped false).
                    // Both are "I want this slot off NOW" — destroy
                    // immediately rather than leaning on the inactivity
                    // grace period, which exists to bridge transient USB
                    // hiccups, not deliberate teardowns.
                    bool wasHmVc = vc is HMaestroVirtualController;
                    DestroyVirtualController(padIndex, asyncDispose: wasHmVc);
                    _virtualControllers[padIndex] = null;
                    _slotInactiveCounter[padIndex] = 0;
                    _slotInitializing[padIndex] = false;
                    VibrationStates[padIndex].LeftMotorSpeed = 0;
                    VibrationStates[padIndex].RightMotorSpeed = 0;

                    // For HM-backed slots, fire the bubble-down cascade
                    // so survivors at higher positions in the same
                    // subgroup drop their kernel slot. Slot stays in the
                    // order list at its current position; only the live
                    // VC is gone. xinputhid (Xbox) / DirectInput / SDL /
                    // raw HID (PlayStation, Extended) all observe creation
                    // order so the cascade applies uniformly across HM
                    // subgroups.
                    if (wasHmVc)
                        HmVcWentNonActive?.Invoke(this, padIndex);
                }
                else
                {
                    // Device(s) mapped but offline — transient disconnect.
                    // Grace period preserves rumble feedback through USB hiccups.
                    _slotInactiveCounter[padIndex]++;

                    bool isHMaestro = vc is HMaestroVirtualController;

                    if (!isHMaestro
                        && vc != null
                        && HmInactivityTimeoutSeconds > 0
                        && _slotInactiveCounter[padIndex]
                            >= (HmInactivityTimeoutSeconds * 1000) / System.Math.Max(1, PollingIntervalMs))
                    {
                        // Non-HM (MIDI, KeyboardMouse) teardown is cheap and
                        // has no kernel-slot ordering concern, but the
                        // device-dropout grace is ONE user-facing contract:
                        // the same inactivity timeout (60 s default, 0 =
                        // never) governs every VC type, so a flaky assigned
                        // device rides out identically regardless of slot
                        // type (the 10 s short grace predated the contract).
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
                        // InputService.OnSlotInactivityTimedOut(padIndex),
                        // which tears down THIS VC (kernel slot frees) and
                        // runs the bubble-down cascade across the same HM
                        // subgroup (Xbox / PlayStation / Extended) so
                        // survivors at higher visual positions drop their
                        // kernel slot.  The slot configuration is preserved
                        // end-to-end — only the live VC is destroyed, so the
                        // slot transitions to "awaiting devices" and the same
                        // VC is recreated automatically by Pass 2 once its
                        // mapped devices come back online.  The latch clears
                        // whenever the slot returns to active state (counter
                        // reset above).
                        //
                        // Both events fire so that listeners that care about
                        // the inactivity-timeout-specific case (e.g. status
                        // text "VC torn down due to inactivity") still get
                        // it, while the unified non-active cascade entry
                        // point also runs.
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
                        // All HIDMaestro-backed slots (Xbox / PlayStation / Extended)
                        // only get a VC when at least one assigned device is
                        // online. Unlike v2 ViGEm — which was cheap enough to
                        // spin up silent empty slots — HIDMaestro creation
                        // takes seconds per device (SetupController + driver
                        // bind), so empty slots must stay empty and present as
                        // "Awaiting devices" in the sidebar tooltip. MIDI and
                        // KeyboardMouse slots don't need device input to
                        // function and continue to create unconditionally.
                        var slotType = SlotControllerTypes[padIndex];
                        if ((slotType == VirtualControllerType.Xbox
                             || slotType == VirtualControllerType.PlayStation
                             || slotType == VirtualControllerType.Nintendo
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
                        bool isMsSlot = SlotControllerTypes[padIndex] == VirtualControllerType.Xbox;
                        if (isMsSlot) EnsureHMaestroContext();

                        bool isHmSlot = slotType == VirtualControllerType.Xbox
                                     || slotType == VirtualControllerType.PlayStation
                                     || slotType == VirtualControllerType.Nintendo
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
                            //
                            // Scope per rule (g): this gate runs on fresh
                            // creates (slot transitions to active) and on
                            // recreates after a profile-mismatch destroy. It
                            // does NOT run on the swap-only reorder path,
                            // which routes through
                            // RerouteVirtualControllersForReorder and re-
                            // points _virtualControllers without touching
                            // the kernel.
                            var orderList = SettingsManager.SlotOrders.GetOrderSnapshotFor(slotType);
                            int myVisualPos = System.Array.IndexOf(orderList, padIndex);
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
                                        // Claim the slot only if it is still
                                        // empty. HM bring-up takes seconds
                                        // (see the retry note above), and the
                                        // UI-thread slot reorder can install a
                                        // reused VC at this index while we were
                                        // connecting. A blind assign overwrote
                                        // that pointer, and since the array was
                                        // its only handle, the reused kernel
                                        // controller leaked: still live, still
                                        // holding its kernel slot, never
                                        // reached by DestroyVirtualController.
                                        // Losing the race means WE are the
                                        // spare, so dispose ourselves. This
                                        // also covers the poll thread starting
                                        // a spurious create in the reorder's
                                        // clear-to-repopulate gap: that VC now
                                        // loses the swap and tears itself down
                                        // instead of displacing the real one.
                                        var prior = System.Threading.Interlocked.CompareExchange(
                                            ref _virtualControllers[capturedIndex], vcAsync, null);
                                        if (prior != null)
                                        {
                                            try { vcAsync.Dispose(); }
                                            catch { /* best effort */ }

                                            // The spare we just tore down owned
                                            // a UserEffectsDispatcher, and it
                                            // registered that dispatcher under
                                            // this pad's key while it was
                                            // connecting. If it registered
                                            // AFTER the winner did, it replaced
                                            // the winner in the static registry
                                            // and its Dispose just removed the
                                            // key (its own instance being the
                                            // registered one, the "don't yank a
                                            // fresh dispatcher's key" guard
                                            // does not fire). The winner is
                                            // then live but unreachable from
                                            // the registry, so battery /
                                            // sound-routing pokes for this slot
                                            // silently stop. Re-attach it to
                                            // re-claim the key. Idempotent when
                                            // the key already points at the
                                            // winner.
                                            if (prior is HMaestroVirtualController priorHm)
                                            {
                                                var cfg = _deviceSlotConfigs[capturedIndex];
                                                if (cfg != null)
                                                {
                                                    try { priorHm.AttachDeviceConfig(cfg); }
                                                    catch { /* best effort */ }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            try { _hmaestroContext?.FinalizeNames(); }
                                            catch { /* best effort */ }
                                        }
                                    }
                                    else if (vcAsync == null)
                                    {
                                        _createFailed[capturedIndex] = true;
                                    }
                                    else
                                    {
                                        // Connect() returned but the VC is not connected
                                        // (device disconnected mid-bring-up, kernel reject,
                                        // etc.). Without disposing here the IVirtualController
                                        // and its HM kernel resources leak; without latching
                                        // _createFailed the next polling cycle sees the slot
                                        // as eligible-but-unbuilt and kicks off another
                                        // connect, accumulating leaked VCs.
                                        try { vcAsync.Dispose(); } catch { /* best effort */ }
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
                            else
                            {
                                // Created but not connected — dispose and latch
                                // failure so we don't loop on the next cycle.
                                try { vc.Dispose(); } catch { /* best effort */ }
                                _virtualControllers[padIndex] = null;
                                _createFailed[padIndex] = true;
                                _slotInitializing[padIndex] = false;
                            }
                        }
                    }
                }
            }

            // --- Pass 3: Submit reports for active slots ---
            // Sony raw-report scratch buffer hoisted out of the loop:
            // stackalloc inside a per-slot loop accumulates across
            // iterations until the method returns (CA2014). With
            // MaxPads=16 × 63 bytes the worst-case stack lifetime is
            // ~1KB, but reusing one span keeps it bounded as the loop
            // bound grows.
            Span<byte> rawReportScratch = stackalloc byte[63];
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

                    // One neutral submit on the active->inactive transition
                    // (counter hits exactly 1 on the first inactive poll; Pass 1
                    // incremented it earlier this same call). Without it the
                    // virtual controller wire holds whatever report was last
                    // submitted — a pedal or key held at unplug stays held
                    // in-game until the HM inactivity teardown (default 60s,
                    // indefinitely when disabled). Step 4 already recomputed
                    // Combined* this poll from the frozen per-device states, so
                    // neutralize them here exactly like Step 4's empty-slot path
                    // (Clear keeps arrays allocated; every Submit* path
                    // null-guards regardless), then fall through to the normal
                    // submit block once.
                    if (vc != null && _slotInactiveCounter[padIndex] == 1)
                    {
                        CombinedOutputStates[padIndex].Clear();
                        CombinedRawHidStates[padIndex].Clear();
                        CombinedMidiRawStates[padIndex].Clear();
                        CombinedKbmRawStates[padIndex].Clear();
                        CombinedTouchpadStates[padIndex] = default;
                    }

                    if (vc != null && _slotInactiveCounter[padIndex] <= 1)
                    {
                        // MIDI slots use SubmitMidiRawState for dynamic CC/note output.
                        // KBM slots use SubmitKbmState for keyboard/mouse output.
                        // PlayStation slots whose HIDMaestro profile matches a
                        // Sony USB Report 0x01 layout submit a packed raw report
                        // alongside the Gamepad state so games see the full
                        // touchpad / gyro / accel / battery surface — fields
                        // HMGamepadState can't carry. Other Xbox / PlayStation /
                        // Extended-non-custom slots use plain SubmitGamepadState.
                        if (vc is MidiVirtualController midiVc)
                            midiVc.SubmitMidiRawState(CombinedMidiRawStates[padIndex]);
                        else if (vc is KeyboardMouseVirtualController kbmVc)
                        {
                            // SOCD config (discussion #205): live reference from
                            // the slot's KbmSlotConfig, applied through the VC's
                            // reference-compare fast path.
                            var kbmCfg = _kbmConfigs[padIndex];
                            if (kbmCfg != null)
                                kbmVc.ApplySocdConfig(kbmCfg.SocdMode, kbmCfg.SocdPairs);
                            // A gamepad-only-restricted peer feeding this slot must not
                            // reach the OS via the KBM controller: submit neutral (which
                            // releases anything held) instead of its mapped state.
                            kbmVc.SubmitKbmState(IsSlotRestricted(padIndex) ? default : CombinedKbmRawStates[padIndex]);
                        }
                        else if (SlotControllerTypes[padIndex] is VirtualControllerType.Extended
                                     or VirtualControllerType.Nintendo
                                 && SlotRawHidSurface[padIndex]
                                 && vc is HMaestroVirtualController hmExt)
                        {
                            // Extended with dynamic profile layout: mappings live
                            // in RawHidState (RawAxis{N}/RawBtn{N}/
                            // RawPov{N} target keys populated by Step 3/4)
                            // not the standard Gamepad. Submit the raw state
                            // directly to HIDMaestro so we cover the full
                            // HMGamepadState surface — 6 axes, 13 buttons, and
                            // hat — without the lossy 11-button XInput Gamepad
                            // bitmap intermediate.
                            var layout = SlotCustomLayouts[padIndex];
                            // Button SOCD (#240): clean the final combined
                            // raw buttons right before submit, flat-index
                            // grammar on the word array.
                            var socdExt = ResolveSlotSocd(padIndex, extendedIndices: true);
                            if (socdExt != null)
                                socdExt.ApplyExtended(CombinedRawHidStates[padIndex].Buttons);
                            hmExt.SubmitRawHidState(
                                CombinedRawHidStates[padIndex],
                                layout.Sticks,
                                layout.Triggers,
                                // IMU channel (HM v1.3.18): the slot's
                                // aggregated motion snapshot rides beside
                                // the raw surface. HasMotion=false (no
                                // motion rows mapped) submits zeroes.
                                MotionSnapshots[padIndex]);
                        }
                        else
                        {
                            // PlayStation slots: thread the touchpad-click bool
                            // into Gamepad.TOUCHPAD so HMaestroVirtualController.
                            // MapButtons can route it to HMButton.Touchpad and
                            // the profile's buttonMap can land it at the correct
                            // descriptor button (e.g. button 14 on DS4 / DualSense).
                            // Auto-map populates tp.Click from "Touchpad 0 Click"
                            // input descriptors but never sets the gp bit, so
                            // BT profiles (no raw packer) lose the press without
                            // this OR. USB profiles still ride SubmitRawReport
                            // for the full byte-level layout, but consistent
                            // SubmitGamepadState output keeps the GIP / XInput
                            // surface in agreement.
                            var gpOut = CombinedOutputStates[padIndex];
                            if (SlotControllerTypes[padIndex] == VirtualControllerType.PlayStation
                                && CombinedTouchpadStates[padIndex].Click)
                            {
                                gpOut.Buttons |= Gamepad.TOUCHPAD;
                            }

                            // Button SOCD (#240): clean the final combined
                            // bitmask right before submit, so physical,
                            // mapped, and macro contributions are treated
                            // uniformly and the winner's release re-presses
                            // the held partner the same frame.
                            var socdGp = ResolveSlotSocd(padIndex, extendedIndices: false);
                            if (socdGp != null)
                                gpOut.Buttons = socdGp.ApplyGamepad(gpOut.Buttons);

                            // PlayStation slots backed by an HM virtual go
                            // through the extended SubmitGamepadState overload
                            // so HMGamepadState's touchpad / IMU / battery
                            // fields populate from the assigned physical pad's
                            // SDL sensor reads. BT profiles depend on this
                            // entirely (no SubmitRawReport packer for BT —
                            // their input report is the vendor-blob 0x31
                            // shape, written by HM's encoder from the state
                            // fields). USB profiles pick up the same data
                            // here too, then SubmitRawReport below overrides
                            // the byte layout with the full Sony USB Report
                            // 0x01 packing — both paths consistent.
                            int pctNow = BatteryPercents[padIndex];
                            byte pctByte = pctNow < 0 ? (byte)100 : (byte)Math.Clamp(pctNow, 0, 100);

                            if (SlotControllerTypes[padIndex] == VirtualControllerType.PlayStation
                                && vc is HMaestroVirtualController hmExtState)
                            {
                                // USB Sony (a raw packer exists): skip this
                                // leg. The raw report below is the
                                // authoritative full-layout frame, the
                                // driver's worker consumes only the LATEST
                                // frame per event wake (driver.c
                                // SharedInputWorkerProc), and the extended
                                // frame's touchpad/IMU fields are consumed
                                // only by armed-BT codecs USB profiles never
                                // arm. Publishing both was two seqlock
                                // writes + two kernel SetEvents per tick.
                                // SubmitRawReport ticks FFB itself now.
                                if (SonyReportPackers.ForProfile(hmExtState.ProfileId) == null)
                                    hmExtState.SubmitGamepadState(
                                        gpOut,
                                        CombinedTouchpadStates[padIndex],
                                        MotionSnapshots[padIndex],
                                        pctByte,
                                        BatteryCharging[padIndex]);
                            }
                            else
                            {
                                vc.SubmitGamepadState(gpOut);
                            }

                            if (SlotControllerTypes[padIndex] == VirtualControllerType.PlayStation
                                && vc is HMaestroVirtualController hmPs)
                            {
                                var packer = SonyReportPackers.ForProfile(hmPs.ProfileId);
                                if (packer != null)
                                {
                                    // rawReportScratch is hoisted above the
                                    // for-loop. Each call overwrites all 63
                                    // bytes via the packer; SubmitRawReport
                                    // copies the span into native memory and
                                    // does not retain a reference.
                                    int pct = BatteryPercents[padIndex];
                                    byte battery = pct < 0 ? (byte)100 : (byte)Math.Clamp(pct, 0, 100);
                                    // gpOut, not CombinedOutputStates: the
                                    // raw frame must carry the same touch-
                                    // click OR and button-SOCD cleaning the
                                    // extended leg applied, since it is the
                                    // sole submission on USB Sony slots.
                                    packer(
                                        gpOut,
                                        CombinedTouchpadStates[padIndex],
                                        MotionSnapshots[padIndex],
                                        battery,
                                        BatteryCharging[padIndex],
                                        unchecked((uint)_sonyFrameCounter++),
                                        rawReportScratch);
                                    hmPs.SubmitRawReport(rawReportScratch);
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
                    catch (Exception)
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
        // xbox-series-xs-bt rather than xbox-360-wired so new Xbox
        // slots work out of the box with browser-sourced force feedback.
        // Browsers using WGI or GameInput paths (Chrome on Win11 in
        // particular) don't route FFB to the Xbox 360 XUSB companion, so
        // xbox-360-wired vibrates in native games but stays silent for
        // browser "Vibration, infinite" tests. xbox-series-xs-bt uses the
        // HID output path that browsers drive reliably.
        public const string DefaultXboxProfileId = "xbox-series-xs-bt";
        public const string DefaultPlayStationProfileId = "dualshock-4-v2";
        // The synthetic "Custom" entry anchors Extended — new slots start
        // there with Customize auto-enabled and the user fills in the
        // VID/PID/ProductString/layout from scratch. Previous catalog-
        // inheritance default (logitech-f710) would have new users pick
        // up Logitech VID/PID surprise-unexpectedly.
        public const string DefaultRawProfileId = HMaestroProfileCatalog.CustomProfileId;
        /// <summary>The Nintendo category's only profile for now (owner
        /// call 2026-07-18). Matches HMaestroProfileCatalog.IsNintendoProfile.</summary>
        public const string DefaultNintendoProfileId = "switch-pro";

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
            VirtualControllerType.Xbox => DefaultXboxProfileId,
            VirtualControllerType.PlayStation => DefaultPlayStationProfileId,
            VirtualControllerType.Extended => DefaultRawProfileId,
            VirtualControllerType.Nintendo => DefaultNintendoProfileId,
            _ => null
        };

        private IVirtualController CreateVirtualController(int padIndex)
        {
            var controllerType = SlotControllerTypes[padIndex];

            // MIDI and KeyboardMouse stay on their dedicated implementations.
            // Xbox / PlayStation / Nintendo / Extended route through HIDMaestro.
            if (controllerType == VirtualControllerType.Xbox
                || controllerType == VirtualControllerType.PlayStation
                || controllerType == VirtualControllerType.Nintendo
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
                    VirtualControllerType.Xbox => CreateHMaestroController(VirtualControllerType.Xbox, profileId, padIndex),
                    VirtualControllerType.PlayStation => CreateHMaestroController(VirtualControllerType.PlayStation, profileId, padIndex),
                    VirtualControllerType.Extended => CreateHMaestroController(VirtualControllerType.Extended, profileId, padIndex),
                    VirtualControllerType.Nintendo => CreateHMaestroController(VirtualControllerType.Nintendo, profileId, padIndex),
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

                // Attach Feature B's user-effects dispatcher when this is
                // a virtual DualSense slot. Hook is a no-op on non-DS5
                // virtuals (the inner IsDualSenseVirtual check short-
                // circuits). Reference is stored from InputService.Start
                // / live-edit hooks alongside MidiConfig / ExtendedConfig.
                if (vc is HMaestroVirtualController hmVc)
                {
                    var psCfg = _deviceSlotConfigs[padIndex];
                    if (psCfg != null)
                        hmVc.AttachDeviceConfig(psCfg);
                }
                else
                {
                    // KBM / MIDI: no HM VC means no HM-owned dispatcher.
                    // Create one inline here so any Sony pad mapped to the
                    // slot still receives effect packets. Step 2's
                    // ApplyForceFeedback returns early for Sony VID/PID,
                    // and the per-slot poke loop calls
                    // UserEffectsDispatcher.OnPollingTick — both expect a
                    // dispatcher to exist in _instances[padIndex]. The
                    // dispatcher's runtime resolve gates on physical Sony
                    // VID/PID, so attaching for every non-HM slot is cheap
                    // when no Sony pad is mapped.
                    var psCfg = _deviceSlotConfigs[padIndex];
                    if (psCfg != null)
                    {
                        var d = new UserEffectsDispatcher(padIndex, psCfg);
                        d.ApplyOnce();
                        _nonHmDispatchers[padIndex] = d;
                    }
                }

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

                int profSticks = baseProfile.StickCount;
                int profTriggers = baseProfile.TriggerCount;
                int profPovs = baseProfile.HasHat ? 1 : 0;
                int profButtons = baseProfile.ButtonCount;

                bool layoutOverrides =
                    (userSticks > 0 || userTriggers > 0 || userPovs > 0 || userButtons > 0) &&
                    (userSticks != profSticks
                     || userTriggers != profTriggers
                     || userPovs != profPovs
                     || userButtons != profButtons);

                // FFB toggle. When Customize is on, the user's checkbox is
                // the source of truth in BOTH directions. Most HIDMaestro
                // catalog profiles ship without a PID FFB block — only the
                // `padforge-custom` synthetic profile and a small handful
                // of catalog profiles include it — so we cannot assume
                // catalog defaults match the user's intent. Treat FFB as
                // an override unconditionally and rebuild the descriptor
                // (with AddPidFfbBlock when forceFeedbackEnabled is true,
                // without when false) so the wire descriptor always
                // reflects the checkbox.
                //
                // Previously this read `ffbOverrides = !forceFeedbackEnabled`
                // — only rebuilt when DISABLING FFB. That left FFB silently
                // broken on catalog-derived Extended profiles whenever the
                // user enabled the FFB checkbox: ffbOverrides was false,
                // descriptor was reused as-is, and a no-FFB catalog
                // descriptor stayed no-FFB regardless of the toggle.
                bool forceFeedbackEnabled = SlotExtendedFfbEnabled[padIndex];
                bool ffbOverrides = true;

                // VID/PID override (0 = use the profile's value). Counts as an
                // override only when non-zero AND different from the base profile,
                // so re-displaying the profile's own VID/PID doesn't force a rebuild.
                int userVid = SlotExtendedVendorId[padIndex];
                int userPid = SlotExtendedProductId[padIndex];
                bool vidPidOverrides =
                    (userVid > 0 && userVid != baseProfile.VendorId)
                    || (userPid > 0 && userPid != baseProfile.ProductId);

                if (productStringOverrides || layoutOverrides || ffbOverrides || vidPidOverrides)
                {
                    try
                    {
                        var builder = new HMProfileBuilder().FromProfile(baseProfile);

                        if (productStringOverrides)
                            builder.ProductString(userProductString);

                        if (userVid > 0)
                            builder.Vid((ushort)userVid);
                        if (userPid > 0)
                            builder.Pid((ushort)userPid);

                        if (layoutOverrides || ffbOverrides)
                        {
                            // Use the user's layout when they overrode it, else
                            // fall back to the active profile's layout — we still
                            // need the same axis/button/POV counts so the device
                            // looks identical aside from the PID block.
                            int sticks = layoutOverrides ? userSticks : profSticks;
                            int triggers = layoutOverrides ? userTriggers : profTriggers;
                            int povs = layoutOverrides ? userPovs : profPovs;
                            int buttons = layoutOverrides ? userButtons : profButtons;

                            // Mirror BuildCustomProfile. AddPidFfbBlock emits the
                            // SDK's minimum-viable PID FFB descriptor and auto-
                            // injects the Report ID 0x01 prefix; FromDescriptorBuilder
                            // derives InputReportSize from the builder's bit count
                            // plus the Report ID byte. HM v1.1.41 (issue #16).
                            var descBuilder = new HidDescriptorBuilder().Joystick();
                            for (int s = 0; s < sticks; s++)
                                descBuilder.AddStick(s == 0 ? "Left" : "Right", 16);
                            for (int t = 0; t < triggers; t++)
                                descBuilder.AddTrigger(t == 0 ? "Left" : "Right", 16);
                            if (povs > 0)
                                descBuilder.AddHat();
                            if (buttons > 0)
                                descBuilder.AddButtons(buttons);
                            if (forceFeedbackEnabled)
                                descBuilder.AddPidFfbBlock();
                            builder.FromDescriptorBuilder(descBuilder);
                        }

                        effectiveProfile = builder.Build();
                    }
                    catch (Exception)
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
            _extendedAppliedFfbEnabled[padIndex] = SlotExtendedFfbEnabled[padIndex];
            _extendedAppliedVendorId[padIndex] = SlotExtendedVendorId[padIndex];
            _extendedAppliedProductId[padIndex] = SlotExtendedProductId[padIndex];

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
        /// Returns true if the slot currently holds any HM-backed virtual
        /// controller (Xbox / PlayStation / Extended). Used by the
        /// bubble-down cascade in InputService when a slot at a lower
        /// position transitions to non-active for any reason — delete,
        /// disable, all-devices-unassigned, HM inactivity timeout —
        /// so survivors at higher positions in the same subgroup get
        /// destroyed and recreated, dropping their kernel slot by one.
        /// External observers (xinputhid for Xbox, DirectInput / SDL /
        /// raw HID for PlayStation and Extended) all see this as the
        /// natural disconnect/reconnect shape.
        /// </summary>
        public bool IsHmVcAt(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return false;
            return _virtualControllers[padIndex] is HMaestroVirtualController;
        }

        /// <summary>
        /// Xbox-only variant kept for any callers that still need to
        /// distinguish Xbox specifically (e.g. an Xbox-only diagnostic).
        /// New code should use <see cref="IsHmVcAt"/> instead.
        /// </summary>
        public bool IsXboxHmVcAt(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return false;
            var vc = _virtualControllers[padIndex];
            return vc is HMaestroVirtualController hm
                && hm.Type == VirtualControllerType.Xbox;
        }

        /// <summary>
        /// True when the slot at <paramref name="padIndex"/> currently holds
        /// any live virtual controller. Used by InputService's reorder paths
        /// to gate the kernel-order rebuild: a slot with no live VC has no
        /// kernel-slot presence, so its visual position cannot perturb
        /// xinputhid / HM allocation.
        /// </summary>
        public bool HasVirtualControllerAt(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return false;
            return _virtualControllers[padIndex] != null;
        }

        /// <summary>
        /// Re-route the live VCs after a same-group visual reorder.
        /// Implements rules (b), (c), (d), (e) from the rules block at
        /// the top of this file.
        ///
        /// Active VCs constitute their own ordering within an HM-backed
        /// group. The kernel slot at visual position V (= the kernel slot
        /// the VC at <c>oldOrder[V]</c> currently holds) is anchored to V.
        /// When the user reorders, the data identity at each visual
        /// position changes (per <paramref name="newOrder"/>) but the
        /// kernel slot at V stays put. Its serving VC just gets re-routed
        /// to feed the new data identity at position V.
        ///
        /// Per-position decision:
        /// <list type="bullet">
        /// <item>If the VC at position V (pre-reorder) and the new pad
        /// at position V (post-reorder) share the same profile slug,
        /// reuse the VC. Pointer-only swap: <c>_virtualControllers</c>
        /// and per-VC state arrays move together; <c>FeedbackPadIndex</c>
        /// is updated so feedback callbacks find the right vibration
        /// entry. No teardown.</item>
        /// <item>If the profiles differ, the VC at position V is
        /// destroyed (via the regular async-dispose path). Pass 2's
        /// visual-order gate plus <c>ApplyAscendingIndexPreemption</c>
        /// recreate it with the new pad's profile, taking the lowest
        /// free kernel slot. That slot is V because all surviving VCs at
        /// positions &lt; V keep their slots.</item>
        /// </list>
        ///
        /// Same-profile cycles (Example: insert a Profile-A slot at the
        /// top of an all-Profile-A group) collapse to a pure pointer
        /// rotation across <c>_virtualControllers</c> with no kernel
        /// teardown. Different-profile cycles destroy only the positions
        /// where the profile actually changed; matching positions in the
        /// same reorder still reuse via pointer swap.
        ///
        /// Intra-group only. Non-HM groups (KBM, MIDI) early-return per
        /// rule (h). Cross-group moves go through MoveSlotToGroupTail
        /// per rule (i).
        /// </summary>
        public void RerouteVirtualControllersForReorder(
            VirtualControllerType groupType,
            IReadOnlyList<int> oldOrder,
            IReadOnlyList<int> newOrder)
        {
            if (groupType != VirtualControllerType.Xbox
                && groupType != VirtualControllerType.PlayStation
                && groupType != VirtualControllerType.Nintendo
                && groupType != VirtualControllerType.Extended)
                return;

            if (oldOrder == null || newOrder == null) return;
            if (oldOrder.Count != newOrder.Count) return;
            int n = oldOrder.Count;
            if (n == 0) return;

            // Decide per visual position: reuse the existing VC at this
            // kernel slot, or destroy it. Snapshot the per-VC state at
            // the same time so we can move it with the VC.
            var reuseAtPosition = new IVirtualController[n];
            var stateExtendedAppliedProductString = new string[n];
            var stateExtendedAppliedLayout = new CustomControllerLayout[n];
            var stateExtendedAppliedFfbEnabled = new bool[n];
            var stateExtendedAppliedVendorId = new int[n];
            var stateExtendedAppliedProductId = new int[n];
            var stateOemOverrideClaimedVidPid = new uint[n];
            var stateLastAppliedOemLabel = new string[n];
            var destroyOldPads = new List<int>();

            for (int V = 0; V < n; V++)
            {
                int oldPad = oldOrder[V];
                if (oldPad < 0 || oldPad >= MaxPads) continue;
                var oldVC = _virtualControllers[oldPad];
                if (oldVC == null) continue;

                int newPad = newOrder[V];
                if (newPad < 0 || newPad >= MaxPads)
                {
                    destroyOldPads.Add(oldPad);
                    continue;
                }

                // If newPad has no current VC and isn't going to be activated
                // by this reorder (no online device assigned), this visual
                // position is effectively a placeholder. Skip — don't destroy
                // oldPad's VC just because the profile slug on an inactive
                // neighbor differs. The visual order changes, but the kernel
                // VC stays at oldPad's pad index.
                if (_virtualControllers[newPad] == null && !IsSlotActive(newPad))
                    continue;

                string oldProfile = (oldVC is HMaestroVirtualController hmOld) ? hmOld.ProfileId : null;
                string newProfile = SlotProfileIds[newPad];

                if (string.Equals(oldProfile ?? string.Empty, newProfile ?? string.Empty, StringComparison.Ordinal))
                {
                    reuseAtPosition[V] = oldVC;
                    stateExtendedAppliedProductString[V] = _extendedAppliedProductString[oldPad];
                    stateExtendedAppliedLayout[V] = _extendedAppliedLayout[oldPad];
                    stateExtendedAppliedFfbEnabled[V] = _extendedAppliedFfbEnabled[oldPad];
                    stateExtendedAppliedVendorId[V] = _extendedAppliedVendorId[oldPad];
                    stateExtendedAppliedProductId[V] = _extendedAppliedProductId[oldPad];
                    stateOemOverrideClaimedVidPid[V] = _oemOverrideClaimedVidPid[oldPad];
                    stateLastAppliedOemLabel[V] = _lastAppliedOemLabel[oldPad];
                }
                else
                {
                    destroyOldPads.Add(oldPad);
                }
            }

            // Step 1: Destroy mismatched VCs. This releases their OEM
            // override claims and queues async dispose. Per-pad state at
            // these old pads is cleared by DestroyVirtualController.
            foreach (int oldPad in destroyOldPads)
            {
                DestroyVirtualController(oldPad, asyncDispose: true);
            }

            // Step 2: Clear per-pad state at old pads whose VCs are
            // moving to a different pad. The OEM override claim is NOT
            // released here — it's preserved on the moving VC and re-
            // attached at the new pad in step 3.
            for (int V = 0; V < n; V++)
            {
                if (reuseAtPosition[V] == null) continue;
                int oldPad = oldOrder[V];
                int newPad = newOrder[V];
                if (oldPad == newPad) continue;
                _virtualControllers[oldPad] = null;
                _extendedAppliedProductString[oldPad] = null;
                _extendedAppliedLayout[oldPad] = default;
                _extendedAppliedFfbEnabled[oldPad] = false;
                _extendedAppliedVendorId[oldPad] = 0;
                _extendedAppliedProductId[oldPad] = 0;
                _oemOverrideClaimedVidPid[oldPad] = 0;
                _lastAppliedOemLabel[oldPad] = null;
            }

            // Step 3: Write the new arrangement. For each reused VC,
            // its destination pad gets the VC pointer + the snapshot of
            // its per-VC state; its FeedbackPadIndex is updated so
            // vibration callbacks write to the correct VibrationStates
            // entry.
            for (int V = 0; V < n; V++)
            {
                int newPad = newOrder[V];
                if (newPad < 0 || newPad >= MaxPads) continue;
                var vc = reuseAtPosition[V];
                if (vc == null) continue;

                _virtualControllers[newPad] = vc;
                _extendedAppliedProductString[newPad] = stateExtendedAppliedProductString[V];
                _extendedAppliedLayout[newPad] = stateExtendedAppliedLayout[V];
                _extendedAppliedFfbEnabled[newPad] = stateExtendedAppliedFfbEnabled[V];
                _extendedAppliedVendorId[newPad] = stateExtendedAppliedVendorId[V];
                _extendedAppliedProductId[newPad] = stateExtendedAppliedProductId[V];
                _oemOverrideClaimedVidPid[newPad] = stateOemOverrideClaimedVidPid[V];
                _lastAppliedOemLabel[newPad] = stateLastAppliedOemLabel[V];

                // Re-point the VC's effect dispatchers too, not just its
                // feedback index. They capture their pad in a readonly field
                // and resolve physical targets from it, so a moved VC kept
                // driving the OLD pad's controllers. _deviceSlotConfigs is
                // keyed by pad index, which is data identity and does not move
                // in a reorder, so newPad's entry is already the right config.
                if (vc is HMaestroVirtualController hm)
                    hm.RetargetToPad(newPad, _deviceSlotConfigs[newPad]);
            }
        }

        /// <summary>
        /// Enforces visual-order kernel-slot allocation within each HM group
        /// for fresh creates and for recreates after a profile-mismatch
        /// destroy. Does NOT run on the swap-only reorder path: pure
        /// reorders that share a profile per position go through
        /// <see cref="RerouteVirtualControllersForReorder"/> and never
        /// touch the kernel. See rule (g).
        ///
        /// When the lowest visual position whose pad index needs to be
        /// created has any visually-lower active VCs in the same group,
        /// those lower-position VCs are torn down so they recreate AFTER
        /// the now-active higher-position pad. xinputhid (and HIDMaestro's
        /// per-subgroup internal index) allocates kernel slots in creation
        /// order, so rebuilding lower-visual-position slots last gives them
        /// higher kernel slots than the visually-higher ones, keeping the
        /// visual order in sync with the kernel-slot order.
        ///
        /// Triggered every tick. Catches inactive→active transitions
        /// (waiting slot gets a device assigned, disabled slot toggled back
        /// on) and the recreate half of profile-mismatch reorders. Per the
        /// per-group spec, teardown happens regardless of whether the
        /// lower-position slots share a profile with the transitioning one.
        ///
        /// Async dispose used so the polling thread is not blocked on
        /// HIDMaestro teardown (up to ~11s for xinputhid profiles). Pass 2's
        /// pending-dispose gate already waits for every queued teardown to
        /// complete before starting a new creation, so the preempted slots'
        /// kernel resources are fully released before any rebuild kicks off.
        /// </summary>
        private static readonly VirtualControllerType[] s_hmSubgroups =
        {
            VirtualControllerType.Xbox,
            VirtualControllerType.PlayStation,
            VirtualControllerType.Nintendo,
            VirtualControllerType.Extended,
        };

        private bool ApplyAscendingIndexPreemption()
        {
            bool displacedAny = false;

            // Lock-free pre-gate: the per-subgroup order snapshots below
            // clone under OrderSync every call, and this runs every poll
            // tick. A VC-less created+enabled un-latched slot is a
            // necessary condition for any preemption decision, so the
            // steady state (every slot settled) skips the snapshots
            // entirely. Decisions are unchanged: the full walk still
            // applies IsSlotActive and ordering when the gate passes.
            bool anyCandidate = false;
            for (int i = 0; i < MaxPads; i++)
            {
                if (_virtualControllers[i] == null
                    && SettingsManager.SlotCreated[i]
                    && SettingsManager.SlotEnabled[i]
                    && !_createFailed[i])
                {
                    anyCandidate = true;
                    break;
                }
            }
            if (!anyCandidate) return false;

            foreach (var subgroup in s_hmSubgroups)
            {
                var orderList = SettingsManager.SlotOrders.GetOrderSnapshotFor(subgroup);

                int lowestNeedsCreatePos = -1;
                for (int pos = 0; pos < orderList.Length; pos++)
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

                for (int pos = lowestNeedsCreatePos + 1; pos < orderList.Length; pos++)
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

            // #236: VC destruction is an explicit silence edge for ALL
            // FOUR voices (the legacy lifecycle zeroing below touches only
            // the two body motors of VibrationStates). The vacated route
            // resolves to zero before the async disposal below can run,
            // and the generation bump discards any racing lane publish.
            RumbleAudioService.SilenceSlot(padIndex);

            // #240: forget SOCD winner state with the VC. Without this a
            // recreate with identical config strings no-ops Configure and
            // a stale Winner mis-suppresses the first press.
            _slotButtonSocd[padIndex]?.Reset();

            // Non-HM dispatcher (KBM / MIDI) lives outside the VC, so the
            // VC's Disconnect doesn't dispose it. Tear down explicitly here.
            // HM-owned dispatchers are disposed inside HM VC.Disconnect; this
            // array stays null for HM slots and is a no-op for them.
            var nonHmDisp = _nonHmDispatchers[padIndex];
            if (nonHmDisp != null)
            {
                _nonHmDispatchers[padIndex] = null;
                try { nonHmDisp.Dispose(); }
                catch { /* best effort */ }
            }

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
            _extendedAppliedFfbEnabled[padIndex] = false;
            _extendedAppliedVendorId[padIndex] = 0;
            _extendedAppliedProductId[padIndex] = 0;

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
