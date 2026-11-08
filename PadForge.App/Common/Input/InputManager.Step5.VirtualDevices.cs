using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using PadForge.Engine;
using PadForge.Resources.Strings;
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

        /// <summary>When the last HIDMaestro setup failed, as
        /// Environment.TickCount64, or 0. A failed setup used to stay failed
        /// until the engine stopped, so no slot could get a virtual controller
        /// again without an engine restart. Now the next create after
        /// <see cref="HmSetupRetryHoldMs"/> runs the setup again. A slot whose
        /// create failed keeps its own latch, which only a change to that slot
        /// clears, so a retry follows the user's next change to a slot, or a
        /// slot created later, and never loops on its own. Inside the hold the
        /// other slots of the same burst fail fast instead of each running
        /// the setup again, since creates run one at a time.</summary>
        private static long _hmaestroSetupFailedTick;
        private const long HmSetupRetryHoldMs = 5_000;
        private const string HmSetupFailedMessage = "Failed to initialize HIDMaestro.";

        internal static bool HmSetupHeldAt(long failedTick, long nowTick)
            => failedTick != 0 && nowTick - failedTick < HmSetupRetryHoldMs;

        private static bool HmSetupHeld()
            => HmSetupHeldAt(Volatile.Read(ref _hmaestroSetupFailedTick), Environment.TickCount64);

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

        /// <summary>Keep Controller Awake (#270): holds the configured idle
        /// deflection on one axis of the final combined gamepad output.
        /// Pure over its inputs (internal for the unit tests).
        ///
        /// <para>The hold engages only while BOTH axes of the stick that
        /// owns the held axis sit below the configured deflection level.
        /// The level IS the rest margin (owner ruling, 2026-08-19): a fixed
        /// epsilon fails on real hardware, because a cheap or aging stick
        /// can rest 5% off neutral with no deadzone configured, and any
        /// margin smaller than that stops the hold from ever engaging on
        /// exactly the pads that need it. The level is already the
        /// feature's own definition of "invisible to the game" (the card
        /// instructs keeping it below the game's dead zone), so drift under
        /// the level is rest by the same contract, and input past the level
        /// on either axis is real use.</para>
        ///
        /// <para>The first cut gated on the held axis ALONE, which
        /// @HaraDaya caught on hardware (discussion #263, 2026-08-19):
        /// push the stick straight up and the gate saw X at rest, so it
        /// kept injecting the X hold, an inverse deadzone that bent
        /// straight-forward movement sideways and made X jump side to side
        /// as it wobbled around center. Gating on the pair releases the
        /// hold whenever the player actually uses the stick.</para>
        ///
        /// <para>With Add Motion on, the injected value sweeps but the
        /// GATE uses the steady base level, so the engage boundary does
        /// not flutter with the sweep.</para>
        ///
        /// <para>Deflection 0 (unset) applies the default 25%; the axis
        /// string falls back to LX. Values are clamped to 1-90%.</para></summary>
        internal static void ApplyKeepAwake(Engine.Data.MappingSet ms, ref Gamepad gp, long nowMs = 0)
        {
            if (ms == null || !ms.KeepAwakeEnabled) return;
            short baseLevel = KeepAwakeLevel(ms.KeepAwakeDeflection, motion: false, nowMs: 0);
            short level = KeepAwakeLevel(ms.KeepAwakeDeflection, ms.KeepAwakeMotion, nowMs);
            switch (ms.KeepAwakeAxis)
            {
                case "LY":
                    if (StickAtRest(gp.ThumbLX, gp.ThumbLY, baseLevel)) gp.ThumbLY = level;
                    break;
                case "RX":
                    if (StickAtRest(gp.ThumbRX, gp.ThumbRY, baseLevel)) gp.ThumbRX = level;
                    break;
                case "RY":
                    if (StickAtRest(gp.ThumbRX, gp.ThumbRY, baseLevel)) gp.ThumbRY = level;
                    break;
                default: // "" and "LX"
                    if (StickAtRest(gp.ThumbLX, gp.ThumbLY, baseLevel)) gp.ThumbLX = level;
                    break;
            }
        }

        private static bool StickAtRest(short x, short y, short margin)
            => Math.Abs((int)x) < margin
            && Math.Abs((int)y) < margin;

        /// <summary>One full sweep of the keep-awake motion, in ms. 500 gives
        /// a 250 ms travel each way, so a title sampling at 60 Hz sees the
        /// axis change by roughly a fifteenth of the sweep width every frame.
        /// Unmistakably moving, and slow enough that it reads as a deliberate
        /// stick push rather than noise.</summary>
        internal const long KeepAwakeMotionPeriodMs = 500;

        /// <summary>The deflection to hold this instant.
        ///
        /// <para>Constant at the configured percent unless motion is on.
        /// @HaraDaya measured (discussion #263) that some titles gate
        /// vibration on the stick MOVING rather than on where it sits, Prey
        /// (2017) being the example: hold the stick still and the vibration
        /// stops however far over it is held. No constant value satisfies
        /// that, so the value has to travel.</para>
        ///
        /// <para>The sweep is CENTERED on the configured percent, a quarter of
        /// it wide, so the user's number stays the average rather than
        /// becoming the floor or the ceiling. Sweeping upward from it would
        /// push the axis further than the user asked and risk crossing a
        /// game's deadzone into real character movement, which is the exact
        /// thing this feature exists to avoid. Sweeping downward from it
        /// would dip under whatever threshold the game uses to call the
        /// controller active.</para>
        ///
        /// <para>Pure over its inputs, clock included, so the sweep is
        /// testable without a controller or a running poll loop.</para></summary>
        internal static short KeepAwakeLevel(int pct, bool motion, long nowMs)
        {
            if (pct <= 0) pct = 25;
            pct = Math.Clamp(pct, 1, 90);
            int level = 32767 * pct / 100;
            if (!motion) return (short)level;

            // Triangle rather than a sine: the corners are inaudible at this
            // rate, and it keeps the whole thing in integer arithmetic on a
            // per-slot, per-tick path.
            const long half = KeepAwakeMotionPeriodMs / 2;
            long phase = ((nowMs % KeepAwakeMotionPeriodMs) + KeepAwakeMotionPeriodMs)
                         % KeepAwakeMotionPeriodMs;                    // negative-safe
            long travel = phase < half ? phase : KeepAwakeMotionPeriodMs - phase;

            int band = level / 4;
            int lo = level - band / 2;
            int add = (int)(band * travel / half);
            return (short)Math.Clamp(lo + add, 1, 32767);
        }

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
            long now = System.Environment.TickCount64;
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
                    // A game that plays the DualSense's haptics through the
                    // composite persona's audio sends no rumble, so the
                    // haptic channels' loudness joins the merge (discussion
                    // #522). Read through the CURRENT feed position, which
                    // a reorder moves with its controller, as the pack is.
                    pack = WithPersonaHaptics(slot, pack, now);
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
        /// Per-slot HIDMaestro profile slug. Identifies which of the 233
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
        /// the user edited the profile or flag in between. Zero when inactive.
        /// </summary>
        private readonly uint[] _oemOverrideClaimedVidPid = new uint[MaxPads];

        // A connecting worker owns this claim until publication. Reordering
        // a live controller moves only the published claim arrays.
        private sealed record PendingOemClaim(uint Key, string Label);
        private readonly PendingOemClaim[] _pendingOemClaims = new PendingOemClaim[MaxPads];
        internal Action<ushort, ushort, string> OemOverrideSet = HIDMaestro.HMOemNameOverride.Set;
        internal Action<ushort, ushort> OemOverrideClear = HIDMaestro.HMOemNameOverride.Clear;

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
        /// The configuration a HIDMaestro controller under construction was
        /// built with, written by the create worker on the thread pool and
        /// copied into the applied arrays by <see cref="PublishExtendedApplied"/>
        /// under the lifecycle lock when that controller wins its slot. The
        /// applied arrays are read and moved by the reorder under the lock,
        /// so the worker must not write them outside it.
        /// </summary>
        private readonly string[] _extendedPendingProductString = new string[MaxPads];
        private readonly CustomControllerLayout[] _extendedPendingLayout = new CustomControllerLayout[MaxPads];
        private readonly bool[] _extendedPendingFfbEnabled = new bool[MaxPads];
        private readonly bool[] _extendedPendingCustomize = new bool[MaxPads];
        private readonly int[] _extendedPendingVendorId = new int[MaxPads];
        private readonly int[] _extendedPendingProductId = new int[MaxPads];

        /// <summary>
        /// The Extended configuration a controller is built from, captured on
        /// the polling thread under the lifecycle lock when the create is
        /// kicked, and carried through construction into the applied state.
        /// The worker never rereads the live Slot* arrays: the UI writes them
        /// at any time, and a controller built from one value with applied
        /// state recorded from a later value would never be rebuilt, which
        /// can leave two pads on one VID/PID while the configuration names
        /// two.
        /// </summary>
        private readonly struct ExtendedBuild
        {
            public readonly bool Customize;
            public readonly bool OemEnabled;
            public readonly string ProductString;
            public readonly CustomControllerLayout Layout;
            public readonly bool Ffb;
            public readonly int Vid;
            public readonly int Pid;
            public ExtendedBuild(bool customize, bool oemEnabled, string productString, CustomControllerLayout layout, bool ffb, int vid, int pid)
            {
                Customize = customize; OemEnabled = oemEnabled; ProductString = productString;
                Layout = layout; Ffb = ffb; Vid = vid; Pid = pid;
            }
        }

        private ExtendedBuild CaptureExtendedBuild(int padIndex) => new ExtendedBuild(
            SlotExtendedCustomize[padIndex], SlotOemOverrideEnabled[padIndex], SlotOemOverrideLabel[padIndex],
            SlotCustomLayouts[padIndex], SlotExtendedFfbEnabled[padIndex], SlotExtendedVendorId[padIndex], SlotExtendedProductId[padIndex]);

        /// <summary>
        /// The virtual keyboard and mouse a Keyboard + Mouse slot is built
        /// with (HIDMaestro 1.12.0), captured with the type and the profile
        /// when the create is kicked, for the reason <see cref="ExtendedBuild"/>
        /// is. Each is a HIDMaestro profile id, or empty for Windows input.
        /// </summary>
        internal readonly struct KbmBuild
        {
            private readonly string _keyboard, _mouse;
            public KbmBuild(string keyboard, string mouse) { _keyboard = keyboard; _mouse = mouse; }
            public string Keyboard => _keyboard ?? string.Empty;
            public string Mouse => _mouse ?? string.Empty;
            public bool UsesDevices => Keyboard.Length > 0 || Mouse.Length > 0;
            public bool Matches(string keyboard, string mouse) =>
                string.Equals(Keyboard, keyboard ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Mouse, mouse ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            public bool Matches(in KbmBuild other) => Matches(other.Keyboard, other.Mouse);
            public override string ToString() =>
                $"keyboard={(Keyboard.Length > 0 ? Keyboard : "windows")} mouse={(Mouse.Length > 0 ? Mouse : "windows")}";
        }

        /// <summary>The devices the slot's Keyboard + Mouse configuration asks
        /// for now. A half the surface mode turns off asks for none: a
        /// virtual mouse on a keyboard-only slot would sit on the bus with
        /// nothing to send.</summary>
        internal KbmBuild DesiredKbmBuild(int padIndex)
        {
            var cfg = _kbmConfigs[padIndex];
            if (cfg == null) return default;
            return new KbmBuild(cfg.KeyboardEnabled ? cfg.KeyboardDevice : null,
                cfg.MouseEnabled ? cfg.MouseDevice : null);
        }

        private KbmBuild CaptureKbmBuild(int padIndex) => DesiredKbmBuild(padIndex);

        /// <summary>The lock lights Windows last set on the slot's virtual
        /// keyboard as HMKeyboardLeds bits, or -1 when the slot has no virtual
        /// keyboard or Windows has set none. Read off the live controller,
        /// which the UI thread reaches by reference like every snapshot.</summary>
        internal int KbmKeyboardLedsOf(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return -1;
            return _virtualControllers[padIndex] is KeyboardMouseVirtualController kbm
                   && kbm.KeyboardLeds is { } leds
                ? (int)leds
                : -1;
        }

        /// <summary>True when a Keyboard + Mouse controller runs on other
        /// devices than its slot now asks for.</summary>
        private bool KbmDevicesChanged(int padIndex, KeyboardMouseVirtualController kbm)
            => !DesiredKbmBuild(padIndex).Matches(kbm.KeyboardDeviceId, kbm.MouseDeviceId);

        /// <summary>
        /// Set by <see cref="DestroyAllVirtualControllers"/> under the lifecycle
        /// lock and cleared by every Step 5 cycle. A create worker that
        /// finishes after the engine tore everything down (a shutdown past the
        /// 30 s lifecycle wait) finds it set and disposes its result instead of
        /// publishing a controller nothing will ever destroy.
        /// </summary>
        private bool _lifecycleClosed;

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
        private readonly bool[] _extendedAppliedCustomize = new bool[MaxPads];

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
                        // A create worker acquires the claim before Connect
                        // and publishes its controller seconds later. Until
                        // then the slot reads empty here, and releasing the
                        // claim would strip the label while Windows is
                        // enumerating the new device. Not an orphan while
                        // that worker is in flight.
                        var connecting = _pendingConnectTask[padIndex];
                        if (connecting != null && !connecting.IsCompleted) continue;
                        // Orphaned claim. Release it so refs don't leak.
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
                        OemOverrideSet(vid, pid, desiredLabel);
                        _lastAppliedOemLabel[padIndex] = desiredLabel;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        internal void TryAcquireOemOverrideClaim(int padIndex, ushort vid, ushort pid, string label, bool pending = false)
        {
            lock (_vcLifecycleLock)
            {
                if (_lifecycleClosed) return;
                try
                {
                    OemOverrideSet(vid, pid, label);
                    uint key = ((uint)vid << 16) | pid;
                    uint previous = pending ? _pendingOemClaims[padIndex]?.Key ?? 0 : _oemOverrideClaimedVidPid[padIndex];
                    if (previous != key)
                    {
                        if (previous != 0) ReleaseOemOverrideReference(previous);
                        _oemOverrideRefs.TryGetValue(key, out int n);
                        _oemOverrideRefs[key] = n + 1;
                    }
                    if (pending)
                        _pendingOemClaims[padIndex] = new PendingOemClaim(key, label);
                    else
                    {
                        _oemOverrideClaimedVidPid[padIndex] = key;
                        _lastAppliedOemLabel[padIndex] = label;
                    }
                }
                catch (Exception) { }
            }
        }

        private void ReleaseOemOverrideClaim(int padIndex, uint claimedKey, string reason)
        {
            lock (_vcLifecycleLock)
            {
                if (_oemOverrideClaimedVidPid[padIndex] != claimedKey) return;
                _oemOverrideClaimedVidPid[padIndex] = 0;
                _lastAppliedOemLabel[padIndex] = null;
                ReleaseOemOverrideReference(claimedKey);
            }
        }

        internal void ReleasePendingOemOverrideClaim(int padIndex)
        {
            lock (_vcLifecycleLock)
            {
                var claim = _pendingOemClaims[padIndex];
                if (claim == null) return;
                _pendingOemClaims[padIndex] = null;
                ReleaseOemOverrideReference(claim.Key);
            }
        }

        private void PublishPendingOemOverrideClaim(int padIndex)
        {
            var claim = _pendingOemClaims[padIndex];
            if (claim == null) return;
            uint previous = _oemOverrideClaimedVidPid[padIndex];
            if (previous != 0) ReleaseOemOverrideClaim(padIndex, previous, "replace-orphan");
            _oemOverrideClaimedVidPid[padIndex] = claim.Key;
            _lastAppliedOemLabel[padIndex] = claim.Label;
            _pendingOemClaims[padIndex] = null;
        }

        private void ReleaseOemOverrideReference(uint claimedKey)
        {
            if (!_oemOverrideRefs.TryGetValue(claimedKey, out int n)) return;
            if (n <= 1)
            {
                _oemOverrideRefs.Remove(claimedKey);
                try { OemOverrideClear((ushort)(claimedKey >> 16), (ushort)claimedKey); }
                catch (Exception) { }
            }
            else _oemOverrideRefs[claimedKey] = n - 1;
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

        /// <summary>Wall-clock stamp of each slot's grace start. The
        /// inactivity timeout measures real elapsed milliseconds so a
        /// polling-rate change mid-grace cannot rescale a pending teardown
        /// (audit 2026-07-24).</summary>
        private readonly long[] _slotInactiveSinceMs = new long[MaxPads];

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
        // (WaitForHidChild 10 s, WaitForDeviceStarted 5 s, and a 500 ms
        // XInput slot-claim wait) and a failure is a real failure, not a
        // timing flake.
        private readonly bool[] _createFailed = new bool[MaxPads];

        /// <summary>What configuration the createFailed latch was set FOR:
        /// the slot's type and profile at failure time. A failed slot has a
        /// null VC, so the vc-based type/profile change branches in Pass 1
        /// never ran for it and the latch could survive a reconfigure
        /// forever (owner repro 2026-07-23: MIDI-unavailable failure left
        /// the slot yellow through a switch back to Xbox). Pass 1 clears
        /// the latch whenever the current configuration no longer matches
        /// this key.</summary>
        private readonly VirtualControllerType[] _createFailedType = new VirtualControllerType[MaxPads];
        private readonly string[] _createFailedProfile = new string[MaxPads];

        /// <summary>The legacy MIDI output port a failed MIDI create was
        /// started with. Under the legacy API the commonest failure is a
        /// slot with no port picked yet, and picking one is the
        /// reconfigure that must clear the latch.</summary>
        private readonly string[] _createFailedMidiPort = new string[MaxPads];

        /// <summary>The keyboard and mouse devices a failed Keyboard + Mouse
        /// create was started with. Picking another device, or Windows input,
        /// is the reconfigure that clears the latch.</summary>
        private readonly KbmBuild[] _createFailedKbm = new KbmBuild[MaxPads];

        /// <summary>The MIDI API in use when a MIDI create failed. A slot that
        /// failed under one API gets a fresh attempt once the probe settles
        /// on another, as when the runtime goes in under the legacy API. A
        /// create that failed for want of an API keeps None here, whatever
        /// the probe has answered since the failure, so an answer that came
        /// between the failure and the latch still starts the retry.</summary>
        private readonly MidiApiKind[] _createFailedMidiApi = new MidiApiKind[MaxPads];

        /// <summary>Set by the create when it failed for want of a MIDI API:
        /// its own ask found none, or none answered on the instance its own
        /// service restart brought up (<see cref="MidiServiceUnavailableException"/>).
        /// The latch that follows on the same worker then keeps no API. A
        /// probe on a new service instance answers in about the time an ask
        /// waits for it, so the answer came a few milliseconds after the
        /// create gave up, the latch stamped the API that had just started,
        /// and nothing that failed changed again: the slot stayed failed with
        /// MIDI running (bench 2026-10-28). Every create starts with it
        /// cleared.</summary>
        private readonly bool[] _createNoMidiApi = new bool[MaxPads];

        /// <summary>The MIDI generation (<see cref="MidiVirtualController.Generation"/>)
        /// when a MIDI create failed, read after the create's own service
        /// restart. A failed slot gets a fresh attempt once a later reset
        /// starts a new generation (a service restart from any path, an
        /// install or an uninstall), the rule a running slot already follows,
        /// since the service the create failed against is gone.</summary>
        private readonly int[] _createFailedMidiGen = new int[MaxPads];

        /// <summary>The legacy MIDI output port the slot's MIDI bar names
        /// now.</summary>
        private string MidiOutputPortOf(int padIndex) => _midiConfigs[padIndex]?.OutputPort ?? string.Empty;

        /// <summary>The legacy MIDI output ports, listed on a worker while a
        /// legacy MIDI slot needs them: one listing in flight, the polling
        /// thread reading the newest, as the MIDI input sweep does (Phase
        /// 1e). WinMM numbers a port by an index that shifts when a device
        /// comes or goes, and a handle opened with CALLBACK_NULL hears
        /// nothing when its device leaves, so the listing is how a slot
        /// learns its port went away or came back.</summary>
        private sealed class LegacyPortListing
        {
            public List<string> Ports;
            public long Generation;
        }

        private volatile LegacyPortListing _legacyPortListing;
        private volatile bool _legacyPortListingRunning;
        private long _legacyPortListingKicks;
        private long _legacyPortListingKickMs;
        private long _legacyPortListingRescueMs;

        /// <summary>The least time between two listings. Tests set 0.</summary>
        internal int LegacyPortListingIntervalMs = 1_000;

        /// <summary>How long a listing may stay in flight before the
        /// watchdog acts: the availability probe's bound. A healthy WinMM
        /// listing reads cached port state and returns in milliseconds.
        /// Tests shorten it.</summary>
        internal int LegacyPortListingHungMs = 10_000;

        /// <summary>The listing generation a failed MIDI create latched at,
        /// and whether its port was missing: the create found it unlisted,
        /// or a listing started since then left it out.</summary>
        private readonly long[] _createFailedListingGen = new long[MaxPads];
        private readonly bool[] _createFailedPortMissing = new bool[MaxPads];

        /// <summary>Set by the create when its legacy port was not listed
        /// (<see cref="MidiPortNotConnectedException"/>), for the latch
        /// that follows on the same worker. Every create starts with it
        /// cleared.</summary>
        private readonly bool[] _createPortMissing = new bool[MaxPads];

        /// <summary>Whether a failed MIDI create found its legacy port in use
        /// with another MIDI slot set to the same port: set by the create
        /// for the latch that follows on the same worker, then kept with the
        /// latch. Such a slot tries again once no other MIDI slot is set to
        /// its port, so the status line's advice works on either slot.
        /// Every create starts with it cleared, like the mark above, so a
        /// failure that takes no latch leaves nothing for the next
        /// one.</summary>
        private readonly bool[] _createPortShared = new bool[MaxPads];
        private readonly bool[] _createFailedPortShared = new bool[MaxPads];

        /// <summary>The legacy MIDI controller Pass 1 last saw on each slot,
        /// with the listing generation at that sighting, so only a listing
        /// started after the controller connected can call its port
        /// missing.</summary>
        private readonly MidiVirtualController[] _legacyWatchedVc = new MidiVirtualController[MaxPads];
        private readonly long[] _legacyWatchedGen = new long[MaxPads];

        /// <summary>Starts a listing on a worker when none is running, the
        /// interval has passed and the legacy API is the one in use. Polling
        /// thread only.
        ///
        /// <para>A listing that stays in flight past
        /// <see cref="LegacyPortListingHungMs"/> is stuck behind the MIDI
        /// service: on the new MIDI stack wdmaud2 answers the port count
        /// under the lock a port open or close holds across its service RPC
        /// (Microsoft's MidiSrvPorts.cpp), so a close orphaned in a wedged
        /// service blocks it. Its gate then never cleared, and the slot's
        /// port-gone and port-back checks stopped for the session with
        /// nothing in the log. The watchdog writes a diagnostics line that
        /// says what follows and asks for a service restart, as the probe's
        /// recovery does, which fails the RPC holding the lock, so the
        /// listing returns and the generation bump rebuilds the legacy slots.
        /// Where WinMM talks to classic drivers instead (Windows before 24H2,
        /// Legacy API mode, Hybrid mode) no restart reaches the listing, none
        /// is asked for, the line says so, and the listing's gate stays set
        /// until the driver answers. Inside another restart's cooldown none
        /// is asked for either, and the watchdog looks again when it next
        /// fires. One listing stays in flight: no second worker starts beside
        /// a stuck one.</para></summary>
        private void KickLegacyPortListing()
        {
            long now = Environment.TickCount64;
            if (_legacyPortListingRunning)
            {
                long inFlight = now - _legacyPortListingKickMs;
                if (inFlight >= LegacyPortListingHungMs && now - _legacyPortListingRescueMs >= LegacyPortListingHungMs)
                {
                    bool first = _legacyPortListingRescueMs < _legacyPortListingKickMs;
                    _legacyPortListingRescueMs = now;
                    string follows = MidiVirtualController.DescribeRestartRequest(legacyCall: true, out bool restarts);
                    if (first)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"VCTRACE legacy MIDI port listing in flight {inFlight} ms: {follows}");
                    if (restarts)
                        System.Threading.Tasks.Task.Run(() =>
                            MidiVirtualController.RestartService("a legacy MIDI port listing hung", legacyCall: true));
                }
                return;
            }
            if (now - _legacyPortListingKickMs < LegacyPortListingIntervalMs) return;
            if (MidiVirtualController.Backend is not MidiBackendLegacy legacy) return;
            _legacyPortListingKickMs = now;
            long generation = Interlocked.Increment(ref _legacyPortListingKicks);
            _legacyPortListingRunning = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                // The watchdog above reads this answer on the poll thread.
                try { _ = MidiServiceRecovery.ServiceExists; } catch { }
                try { _legacyPortListing = new LegacyPortListing { Ports = legacy.EnumerateOutputPorts(), Generation = generation }; }
                catch (Exception ex)
                {
                    // The next kick lists again.
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"VCTRACE legacy MIDI port listing failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                }
                finally { _legacyPortListingRunning = false; }
            });
        }

        /// <summary>Whether the newest listing started after
        /// <paramref name="afterGeneration"/> names the port: true or false,
        /// or null while there is no such listing yet.</summary>
        private bool? LegacyPortListed(string port, long afterGeneration)
        {
            KickLegacyPortListing();
            var listing = _legacyPortListing;
            if (listing == null || listing.Generation <= afterGeneration) return null;
            return listing.Ports.Contains(port);
        }

        /// <summary>Whether a legacy MIDI slot that failed for want of its
        /// output port may try again: the port was missing, at the create or
        /// in a listing since, and a listing started after the failure shows
        /// it back, as when a synth or interface is switched on after
        /// PadForge starts. A port that never went missing (another program
        /// holds it) keeps the slot failed until it is reconfigured, as
        /// before.</summary>
        private bool LegacyMidiPortCameBack(int padIndex)
        {
            if (MidiVirtualController.ActiveApi != MidiApiKind.Legacy) return false;
            string port = MidiOutputPortOf(padIndex);
            if (port.Length == 0) return false;
            bool? listed = LegacyPortListed(port, Volatile.Read(ref _createFailedListingGen[padIndex]));
            if (listed == false) _createFailedPortMissing[padIndex] = true;
            return listed == true && _createFailedPortMissing[padIndex];
        }

        /// <summary>Whether a running legacy MIDI slot's port is gone: a
        /// listing started after Pass 1 first saw the controller leaves it
        /// out. The slot then rebuilds, its create fails until the port
        /// returns, and <see cref="LegacyMidiPortCameBack"/> lets it try
        /// again.</summary>
        private bool LegacyMidiPortGone(int padIndex, MidiVirtualController midi)
        {
            if (!ReferenceEquals(_legacyWatchedVc[padIndex], midi))
            {
                _legacyWatchedVc[padIndex] = midi;
                _legacyWatchedGen[padIndex] = Interlocked.Read(ref _legacyPortListingKicks);
            }
            return midi.OutputPort.Length > 0
                && LegacyPortListed(midi.OutputPort, _legacyWatchedGen[padIndex]) == false;
        }

        /// <summary>Why a failed slot may try its create again, for the
        /// latch's CLEAR trace, or null while nothing that failed has changed.
        /// The tests run in the order the clear has always run them, and
        /// <see cref="LegacyMidiPortCameBack"/> runs last and only when every
        /// other test is false, since it starts a port listing and marks a
        /// missing port. Called only while the slot's latch is set.</summary>
        private string CreateFailedClearReason(int padIndex)
        {
            var type = SlotControllerTypes[padIndex];
            if (_createFailedType[padIndex] != type)
                return $"type changed to {type}";
            if (!string.Equals(_createFailedProfile[padIndex], SlotProfileIds[padIndex], StringComparison.Ordinal))
                return "profile changed";
            if (type == VirtualControllerType.KeyboardMouse && !_createFailedKbm[padIndex].Matches(DesiredKbmBuild(padIndex)))
                return "keyboard or mouse device changed";
            if (type != VirtualControllerType.Midi)
                return null;
            if (!string.Equals(_createFailedMidiPort[padIndex], MidiOutputPortOf(padIndex), StringComparison.Ordinal))
                return "MIDI port changed";
            // Both API tests wait for the probe to settle, so the retry runs
            // on an API that answers.
            var api = MidiVirtualController.ActiveApi;
            if (api != MidiApiKind.None)
            {
                if (_createFailedMidiApi[padIndex] != api)
                    return $"MIDI API is now {api}";
                if (_createFailedMidiGen[padIndex] != MidiVirtualController.Generation)
                    return "MIDI started in a newer generation";
            }
            // The other slot was moved to another port, disabled or deleted.
            // Pass 2 creates nothing while a teardown is pending, so the
            // retry opens the port after that slot has closed it.
            if (_createFailedPortShared[padIndex] && OtherMidiSlotOnPort(padIndex, MidiOutputPortOf(padIndex)) < 0)
                return "no other MIDI slot is set to its port";
            if (LegacyMidiPortCameBack(padIndex))
                return "MIDI port is back";
            return null;
        }

        private void LatchCreateFailed(int padIndex)
            => LatchCreateFailed(padIndex, SlotControllerTypes[padIndex], SlotProfileIds[padIndex], MidiOutputPortOf(padIndex),
                DesiredKbmBuild(padIndex));

        /// <summary>The async overload records the configuration the create
        /// was STARTED for, not the slot's current one: a retype during the
        /// create window (the MIDI probe alone can run 10 s) would otherwise
        /// stamp the failure onto the NEW type and permanently block it
        /// (owner race, 2026-07-23).</summary>
        private void LatchCreateFailed(int padIndex, VirtualControllerType failedType, string failedProfile, string failedMidiPort,
            KbmBuild failedKbm)
        {
            _createFailedType[padIndex] = failedType;
            _createFailedProfile[padIndex] = failedProfile;
            _createFailedMidiPort[padIndex] = failedMidiPort ?? string.Empty;
            _createFailedKbm[padIndex] = failedKbm;
            _createFailedMidiApi[padIndex] = _createNoMidiApi[padIndex] ? MidiApiKind.None : MidiVirtualController.ActiveApi;
            _createNoMidiApi[padIndex] = false;
            _createFailedMidiGen[padIndex] = MidiVirtualController.Generation;
            _createFailedListingGen[padIndex] = Interlocked.Read(ref _legacyPortListingKicks);
            _createFailedPortMissing[padIndex] = _createPortMissing[padIndex];
            _createPortMissing[padIndex] = false;
            _createFailedPortShared[padIndex] = _createPortShared[padIndex];
            _createPortShared[padIndex] = false;
            _createFailed[padIndex] = true;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"VCTRACE slot={padIndex} createFailed LATCH type={failedType} profile={failedProfile ?? "<null>"}"
                + (failedType == VirtualControllerType.KeyboardMouse ? " " + failedKbm : ""));
        }

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
        /// One lock over virtual controller lifecycle state:
        /// <see cref="_virtualControllers"/>, <see cref="_pendingDisposeTask"/>,
        /// the per-VC applied-state arrays, and the inactivity latch.
        /// <see cref="UpdateVirtualDevices"/> holds it for a whole cycle. The
        /// UI-thread entries (<see cref="DestroyVirtualControllerAsync"/> from
        /// the bubble-down cascades, <see cref="TryInactivityTeardown"/>,
        /// <see cref="RerouteVirtualControllersForReorder"/>) each take it for
        /// their whole change, so a cycle sees none or all of a caller's
        /// stores. Before this, the cascade destroy nulled the slot pointer
        /// and published the dispose task as two stores beside a running
        /// cycle. A cycle between them saw an empty slot with no dispose
        /// pending, and Pass 2 created the replacement while the old pad was
        /// still on the bus. Two pads with one VID/PID attached at once shift
        /// DirectInput's per-VID/PID instance GUID ordinal, and a game that
        /// saved the temporary GUID is left pointing at a pad that no longer
        /// reports it (discussion #395). A cycle holds the lock for
        /// microseconds when nothing changes. HM creates and disposes run on
        /// the thread pool, outside it.
        /// </summary>
        private readonly object _vcLifecycleLock = new object();

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
        private long _disposeGateSinceTick;
        private bool _disposeGateLogged;

        // Connect-gate watchdog, the sibling of the dispose pair above. A
        // single connect task that never completes holds anyConnectPending
        // true forever and blocks the create of EVERY slot, not just its
        // own. The dispose half has had a watchdog since it was written and
        // this half never got one, so that freeze was invisible.
        private long _connectGateSinceTick;
        private bool _connectGateLogged;

        // Visual-order wait bound. A single HM create runs 3-11 s, and a
        // legitimate queue of several slots serializes those, so the window
        // must clear a real queue comfortably. It only ever trips when ONE
        // slot blocks continuously for the whole window, because a changing
        // blocker resets the timer.
        private const long OrderWaitMaxMs = 45_000;
        // Seeded to the not-blocked sentinel, not to zero. Zero is pad 0, a
        // real blocker index, so a slot blocked by pad 0 read as already
        // waiting on it, skipped the branch that stamps the start tick, and
        // left the tick at zero, which the expiry test treats as never. The
        // 45 second bound that breaks an ordering deadlock was off for that
        // one case. Same shape as the FFB array's initializer above.
        private readonly int[] _orderWaitBlocker = InitOrderWaitBlockers();

        private static int[] InitOrderWaitBlockers()
        {
            var a = new int[MaxPads];
            for (int i = 0; i < a.Length; i++) a[i] = -1;
            return a;
        }
        private readonly long[] _orderWaitSinceTick = new long[MaxPads];

        /// <summary>True when the visual-order wait has run its full window
        /// against ONE unchanged blocker, so the waiting slot should create
        /// out of order rather than stay dead. Split out from the poll loop
        /// so the timing rule is testable without an InputManager: the
        /// deadlock it guards was found on hardware and cannot be
        /// reproduced on demand, which makes an unlockable fix
        /// unacceptable. A zero tick means "not waiting yet" and never
        /// expires.</summary>
        internal static bool OrderWaitExpired(long waitSinceTick, long nowTick)
            => waitSinceTick != 0 && nowTick - waitSinceTick >= OrderWaitMaxMs;

        // Gate heartbeat. Writes the create-gate inputs on a cadence
        // WHENEVER a slot wants a VC and has not got one, so a stuck gate
        // is visible instead of silent. The 2026-07-26 investigation lost
        // hours to exactly that ambiguity: the diagnostics channel went
        // quiet and the quiet was read as "nothing is happening" when it
        // meant "the thing that logs is not running". A heartbeat makes
        // silence mean one thing only.
        private long _vcGateHeartbeatTick;

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

        // Raises the initializing flag. NOTE: this used to be prefaced by a
        // comment describing a minimum wall-clock hold, so the overlay's
        // "Initializing → Active" animation would still be visible when
        // HIDMaestro creates a controller synchronously in under 10 ms. No such
        // hold is implemented here or at any of the four sites that clear the
        // flag, and there is no timestamp anywhere to implement it from. The
        // comment described an intention, and reading it as a guarantee would
        // mean debugging a missed animation by looking everywhere except the
        // place that is supposed to cause it. Left unimplemented rather than
        // invented: adding a hold changes how long the overlay sits on screen,
        // which is an owner-visible timing decision.
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
        /// True while the slot's latest virtual-controller create attempt
        /// has failed for the currently configured type and profile (the
        /// createFailed latch; Pass 1 clears it on any reconfigure). The
        /// UI must present this as its own state: a failed slot with
        /// online devices is neither forging nor awaiting devices.
        /// </summary>
        public bool IsVirtualControllerCreateFailed(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return false;
            return _createFailed[padIndex];
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
            lock (_vcLifecycleLock)
            {
                _lifecycleClosed = false;
                UpdateVirtualDevicesLocked();
            }
        }

        private void UpdateVirtualDevicesLocked()
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

                // The line a MIDI create left for this slot has nothing to
                // report once the slot is no longer a MIDI slot, latched or
                // not. A create that fails after its slot lost its device
                // raises its line and takes no latch, so this clear cannot
                // sit under the latch: it did, and a slot retyped after such
                // a create kept its MIDI line on the status bar. One read a
                // pass while no line is remembered.
                if (Volatile.Read(ref _midiCreateError[padIndex]) != null
                    && SlotControllerTypes[padIndex] != VirtualControllerType.Midi)
                    ResolveMidiCreateError(padIndex);

                // A failed slot has no VC, so the vc-based change detection
                // below cannot clear its latch. Clear it here once the slot
                // has moved away from what failed, so it gets a new create
                // attempt: its type or profile changed, or for a MIDI slot,
                // its port changed, the probe settled on another API or in a
                // newer generation (both wait for the probe, so the retry
                // runs on an API that answers), the other slot set to its
                // legacy port let go of it, or its legacy port came back
                // (CreateFailedClearReason). A MIDI slot's retry clears or
                // replaces its line.
                if (_createFailed[padIndex] && CreateFailedClearReason(padIndex) is string clearReason)
                {
                    _createFailed[padIndex] = false;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"VCTRACE slot={padIndex} createFailed CLEAR ({clearReason})");
                }

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
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"VCTRACE slot={padIndex} type-change destroy {vc.Type} -> {SlotControllerTypes[padIndex]}");
                    DestroyVirtualController(padIndex,
                        asyncDispose: vc is HMaestroVirtualController or MidiVirtualController);
                    _virtualControllers[padIndex] = null;
                    _createFailed[padIndex] = false; // Type change — allow retry
                    // A slug from the OLD category is not valid for the new one,
                    // so it goes and the create falls back to the new category's
                    // default. A slug that already belongs to the NEW category
                    // stays: the profile-apply path publishes the type and the
                    // slug together, and clearing it there built the slot on the
                    // default and then tore it down and rebuilt it on the intended
                    // profile, which on an Xbox-family device is two multi-second
                    // drops the user sees as a reconnect.
                    if (!ProfileBelongsToType(SlotProfileIds[padIndex], SlotControllerTypes[padIndex]))
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

                // Extended name, layout, force feedback, and identity edits
                // require a rebuild. The creation gate uses the same comparison
                // if the UI changes a slot after this pass visited it.
                if (vc is HMaestroVirtualController
                    && SlotControllerTypes[padIndex] == VirtualControllerType.Extended
                    && ExtendedConfigurationChanged(padIndex))
                {
                    if (IsSlotActive(padIndex)) BeginInitializing(padIndex);
                    else _slotInitializing[padIndex] = false;
                    DestroyVirtualController(padIndex, asyncDispose: true);
                    _virtualControllers[padIndex] = null;
                    _createFailed[padIndex] = false;
                    vc = null;
                }

                // A Keyboard + Mouse slot rebuilds on the keyboard and mouse it
                // now asks for: a device picked, put back to Windows input, or a
                // half the surface mode turned on or off. The creation gate
                // uses the same comparison.
                if (vc is KeyboardMouseVirtualController kbmOnDevices
                    && KbmDevicesChanged(padIndex, kbmOnDevices))
                {
                    if (IsSlotActive(padIndex)) BeginInitializing(padIndex);
                    else _slotInitializing[padIndex] = false;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"VCTRACE slot={padIndex} keyboard-mouse device change destroy -> {DesiredKbmBuild(padIndex)}");
                    DestroyVirtualController(padIndex, asyncDispose: true);
                    _virtualControllers[padIndex] = null;
                    _createFailed[padIndex] = false;
                    vc = null;
                }

                // Under the legacy API a MIDI slot sends to the port its bar
                // names. A new pick closes the old port and the next pass
                // opens the new one, and a port whose device went away
                // closes the same way, so the slot reopens it once it is
                // back. So does a port whose handle died with its device
                // while the name stayed listed, as after a replug faster
                // than one listing: its sends fail for good. A slot built on
                // one API also rebuilds once the probe settles on another:
                // installing the runtime moves legacy slots onto ports of
                // their own. So does a slot built before the backend was
                // last torn down, as every service restart ends, PadForge's
                // own and one made under it: its endpoint talks to a backend
                // or a service that is gone, and it went silent.
                if (vc is MidiVirtualController midiOnPort
                    && ((MidiVirtualController.ActiveApi == MidiApiKind.Legacy
                         && (!string.Equals(midiOnPort.OutputPort, MidiOutputPortOf(padIndex), StringComparison.Ordinal)
                             || LegacyMidiPortGone(padIndex, midiOnPort)
                             || midiOnPort.EndpointLost))
                        || (MidiVirtualController.ActiveApi != MidiApiKind.None
                            && (midiOnPort.ApiKind != MidiVirtualController.ActiveApi
                                || midiOnPort.CreatedGeneration != MidiVirtualController.Generation))))
                {
                    if (IsSlotActive(padIndex)) BeginInitializing(padIndex);
                    else _slotInitializing[padIndex] = false;
                    DestroyVirtualController(padIndex, asyncDispose: true);
                    _virtualControllers[padIndex] = null;
                    _createFailed[padIndex] = false;
                    vc = null;
                }

                // Slot deleted or disabled by user — destroy immediately.
                // The grace period only applies to transient device disconnects
                // (slot still created + enabled, but physical device offline).
                if (vc != null && (!SettingsManager.SlotCreated[padIndex] || !SettingsManager.SlotEnabled[padIndex]))
                {
                    bool wasHmHere = vc is HMaestroVirtualController;
                    DestroyVirtualController(padIndex,
                        asyncDispose: wasHmHere || vc is MidiVirtualController);
                    _virtualControllers[padIndex] = null;
                    _slotInactiveCounter[padIndex] = 0;
                    _slotInitializing[padIndex] = false;
                    _createFailed[padIndex] = false; // Slot toggle. Allow retry.
                    VibrationStates[padIndex].LeftMotorSpeed = 0;
                    VibrationStates[padIndex].RightMotorSpeed = 0;
                    // Round 33, C2: this branch consumed the SIDEBAR-DISABLE
                    // reason before the documented cascade branch below could
                    // see it, so surviving HM VCs at higher positions in the
                    // same subgroup never compacted and an XInput game bound
                    // to player 1 saw no controller. Fire the non-active
                    // cascade here. Deletes are safe to fire too: the UI
                    // handler no-ops when SlotCreated is already false, and
                    // the delete path runs its own cascade.
                    if (wasHmHere)
                        HmVcWentNonActive?.Invoke(this, padIndex);
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
                    ResolveMidiCreateError(padIndex);
                }

                bool slotActive = IsSlotActive(padIndex);

                if (slotActive)
                {
                    if (_slotInactiveCounter[padIndex] > 0)
                    {
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"VCTRACE slot={padIndex} devices ONLINE (grace ends after {_slotInactiveCounter[padIndex]} cycles)");
                        // A genuine device arrival is a meaningful state
                        // change per the latch's own contract (audit
                        // 2026-07-25, C1): a create that failed while the
                        // devices were absent deserves a retry now that
                        // they are back. A genuinely broken config retries
                        // once per offline->online edge, which is the old
                        // cooldown's cadence, not a hammer loop.
                        if (_createFailed[padIndex])
                        {
                            _createFailed[padIndex] = false;
                            PadForge.Engine.SdlDiagLog.WriteLine(
                                $"VCTRACE slot={padIndex} createFailed CLEAR (devices returned)");
                        }
                    }
                    _slotInactiveCounter[padIndex] = 0;
                    // Volatile: this clear is the STALENESS HANDSHAKE read by
                    // the UI-thread inactivity teardown (see
                    // InactivityFireStillValid). A device arriving between
                    // the poll-thread fire and the marshaled destroy must
                    // veto the destroy, not lose the race to a torn read.
                    System.Threading.Volatile.Write(ref _hmInactivityFired[padIndex], false);

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
                    if (_slotInactiveCounter[padIndex] == 0)
                    {
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"VCTRACE slot={padIndex} devices OFFLINE (grace begins, vc={(vc == null ? "null" : vc.GetType().Name)})");
                        // Stamp the wall clock at the grace's first tick
                        // (audit 2026-07-24). The counter below stays for
                        // the trace and for the "grace ended" report, but
                        // the TIMEOUT is measured in milliseconds: the
                        // thresholds used to convert seconds into cycles
                        // with the LIVE PollingIntervalMs, so changing the
                        // polling rate mid-grace rescaled a pending
                        // timeout (60 s at 1 ms became ~4 s after a switch
                        // to 16 ms, and the reverse stretched it).
                        _slotInactiveSinceMs[padIndex] = Environment.TickCount64;
                    }
                    // Saturate rather than wrap. Only the zero/non-zero
                    // distinction is read (Pass 2's eligibility test), and an
                    // unbounded increment passes back through 0 after 2^32
                    // ticks, roughly 49.7 days at 1 kHz on a tool people
                    // leave running. Elapsed time comes from
                    // _slotInactiveSinceMs, so clamping loses nothing.
                    if (_slotInactiveCounter[padIndex] < int.MaxValue)
                        _slotInactiveCounter[padIndex]++;
                    long inactiveMs = Environment.TickCount64 - _slotInactiveSinceMs[padIndex];

                    // No create can proceed while every mapped device is
                    // offline (Pass 2 skips inactive slots), so a stale
                    // Initializing latch from a type-change destroy would
                    // paint "Forging" forever over what is honestly
                    // "Awaiting devices" (owner repro: the three-stage
                    // type switchover with the pad idle-disconnected
                    // mid-fiddle). Clear it; the active branch re-arms it
                    // the moment a device returns and a create is due.
                    if (vc == null && _slotInitializing[padIndex])
                        _slotInitializing[padIndex] = false;

                    bool isHMaestro = vc is HMaestroVirtualController;

                    if (!isHMaestro
                        && vc != null
                        && HmInactivityTimeoutSeconds > 0
                        && inactiveMs >= HmInactivityTimeoutSeconds * 1000L)
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
                        // HM inactivity timeout. Setting=0 disables (legacy
                        // never-destroy behavior, slot survives indefinitely).
                        // Otherwise the grace's wall-clock time (inactiveMs)
                        // is compared with the threshold, the event fires
                        // once, and a latch stops it re-firing each tick. The
                        // UI thread handler runs
                        // InputService.OnSlotInactivityTimedOut(padIndex),
                        // which tears down THIS VC (kernel slot frees) and
                        // runs the bubble-down cascade across the same HM
                        // group (Xbox / PlayStation / Nintendo / Extended) so
                        // survivors at higher visual positions drop their
                        // kernel slot. The slot configuration is preserved
                        // end-to-end. Only the live VC is destroyed, so the
                        // slot transitions to "awaiting devices" and Pass 2
                        // recreates the VC once its mapped devices come back
                        // online. The latch clears whenever the slot returns
                        // to active state (counter reset above).
                        //
                        // Only HmVcInactivityDestroyed fires here.
                        // TryInactivityTeardown runs its own cascade, so
                        // HmVcWentNonActive is not raised.
                        if (inactiveMs >= HmInactivityTimeoutSeconds * 1000L)
                        {
                            System.Threading.Volatile.Write(ref _hmInactivityFired[padIndex], true);
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
            // Visibility watchdog: a dispose that outlives every documented
            // bound is a defect worth naming, not a silent creation freeze.
            if (anyDisposePending)
            {
                if (_disposeGateSinceTick == 0) _disposeGateSinceTick = Environment.TickCount64;
                else if (!_disposeGateLogged && Environment.TickCount64 - _disposeGateSinceTick > 60_000)
                {
                    _disposeGateLogged = true;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        "VCTRACE dispose gate held Pass 2 for 60+ s; a teardown task is stuck past its bound");
                }
            }
            else { _disposeGateSinceTick = 0; _disposeGateLogged = false; }
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
            // A keyboard menu's own HIDMaestro keyboard (#566) takes the same
            // one-create-at-a-time turn.
            if (!anyConnectPending && MenuKeyboardLifecyclePending) anyConnectPending = true;
            // Connect-gate watchdog: mirror of the dispose watchdog above.
            if (anyConnectPending)
            {
                if (_connectGateSinceTick == 0) _connectGateSinceTick = Environment.TickCount64;
                else if (!_connectGateLogged && Environment.TickCount64 - _connectGateSinceTick > 60_000)
                {
                    _connectGateLogged = true;
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        "VCTRACE connect gate held Pass 2 for 60+ s; a create task is stuck and is blocking EVERY slot");
                }
            }
            else { _connectGateSinceTick = 0; _connectGateLogged = false; }

            // Gate heartbeat: while any slot wants a VC and lacks one, write
            // the gate inputs every 5 s. This answers "why did no create
            // fire" directly, instead of leaving an absence of KICK lines to
            // be interpreted.
            if (anyNeedsCreate && Environment.TickCount64 - _vcGateHeartbeatTick > 5_000)
            {
                _vcGateHeartbeatTick = Environment.TickCount64;
                var hb = new System.Text.StringBuilder(160);
                hb.Append("VCGATE needCreate=1")
                  .Append(" disposePend=").Append(anyDisposePending ? '1' : '0')
                  .Append(" connectPend=").Append(anyConnectPending ? '1' : '0');
                for (int i = 0; i < MaxPads; i++)
                {
                    if (!SettingsManager.SlotCreated[i] || !SettingsManager.SlotEnabled[i]) continue;
                    if (_virtualControllers[i] != null) continue;
                    hb.Append(" | s").Append(i)
                      .Append(" type=").Append(SlotControllerTypes[i])
                      .Append(" inactCtr=").Append(_slotInactiveCounter[i])
                      .Append(" active=").Append(IsSlotActive(i) ? '1' : '0')
                      .Append(" failed=").Append(_createFailed[i] ? '1' : '0');
                }
                PadForge.Engine.SdlDiagLog.WriteLine(hb.ToString());
            }

            // The UI writes slot configuration (created, enabled, type,
            // profile) outside the lifecycle lock, so a change can land after
            // Pass 1 visited the slot it retires and before this point. That
            // slot still holds its pad. Creating a sibling now would put two
            // pads of one VID/PID on the bus at once. Wait one cycle: Pass 1
            // retires it next time, and the dispose gate then holds.
            bool anyRetiring = anyNeedsCreate && AnySlotRetiring();

            if (anyNeedsCreate && !anyDisposePending && !anyConnectPending && !anyRetiring)
            {
                for (int padIndex = 0; padIndex < MaxPads; padIndex++)
                {
                    if (_virtualControllers[padIndex] == null &&
                        _slotInactiveCounter[padIndex] == 0)
                    {
                        // Never build a VC for a slot the user deleted or
                        // switched off. Pass 1's disable branch destroys the
                        // VC, zeroes the inactive counter, clears the failure
                        // latch and continues, which left the slot matching
                        // the two conditions above in the SAME tick. For a
                        // KeyboardMouse slot that produced a livelock: Pass 2
                        // walks ascending, so a disabled low slot was rebuilt
                        // and broke the loop before the slot actually waiting
                        // on a create ever got one, and Pass 1 tore it down
                        // again next tick, forever. The type gate below only
                        // covers the HIDMaestro types, so nothing else caught
                        // it. It also closes the counter-wrap window, where a
                        // disabled MIDI or KBM slot passed the counter test
                        // for one tick after ~49.7 days of uptime.
                        if (!SettingsManager.SlotCreated[padIndex]
                            || !SettingsManager.SlotEnabled[padIndex])
                            continue;

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
                        // (WaitForHidChild, WaitForDeviceStarted, and a
                        // 500 ms XInput slot-claim wait), so fast-looping
                        // retries here accomplish nothing except hammering
                        // the driver. The latch clears on a configuration
                        // change (type, profile, or Extended layout), a slot
                        // toggle, or the slot's devices coming back online.
                        if (_createFailed[padIndex])
                            continue;


                        // HIDMaestro context initialization (the stale device
                        // sweep, profile load, driver install, seconds on a
                        // first run) happens inside the create worker on the
                        // thread pool, in CreateVirtualController. Never here:
                        // this runs on the polling thread under the lifecycle
                        // lock, and the XInput slot snapshot an inline call
                        // once had to precede no longer exists.

                        // MIDI rides the same async chain: its Connect()
                        // drives Windows MIDI Services over WinRT RPC
                        // (session + virtual device + endpoint open), which
                        // can block for seconds or hang outright when the
                        // service is slow or missing. Inline on the polling
                        // thread it wedged the engine and the close path
                        // (owner repro 2026-07-23). Every type uses the async
                        // chain, including KeyboardMouse. Effects initialization
                        // after publication can block on physical output, and
                        // VR's first Connect registers the SteamVR driver.
                        // Neither runs on the polling thread under this lock.
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
                            int blockerIndex = -1;
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
                                blockerIndex = pi;
                                break;
                            }
                            // BOUND THE WAIT. The gate above is an ORDERING
                            // PREFERENCE (kernel slot allocation matching the
                            // user's visual order), and it used to be
                            // unbounded, so a preference could outrank
                            // functioning entirely.
                            //
                            // The deadlock, owner-reported 2026-07-26 and
                            // reproduced from the diag log: a device flap
                            // aborts a higher slot's create, and the abort
                            // path deliberately does NOT latch _createFailed
                            // so a later arrival can retry. That leaves the
                            // higher slot in precisely the blocking state
                            // (Created + Enabled + Active + not-failed +
                            // vc == null), and every visually-lower slot in
                            // the same type group then waits on it forever.
                            // The owner's workaround was switching a lower
                            // slot's TYPE, which moves it into a different
                            // order group and out from behind the blocker.
                            //
                            // A changing blocker means the queue is
                            // progressing, so the timer resets on a new
                            // blocker and only a single slot stuck for the
                            // whole window trips the escape.
                            if (higherStillNeeds)
                            {
                                long nowTick = Environment.TickCount64;
                                if (_orderWaitBlocker[padIndex] != blockerIndex)
                                {
                                    _orderWaitBlocker[padIndex] = blockerIndex;
                                    _orderWaitSinceTick[padIndex] = nowTick;
                                    continue;
                                }
                                if (!OrderWaitExpired(_orderWaitSinceTick[padIndex], nowTick))
                                    continue;
                                PadForge.Engine.SdlDiagLog.WriteLine(
                                    $"VCTRACE slot={padIndex} visual-order wait exceeded {OrderWaitMaxMs}ms behind slot={blockerIndex}; creating OUT OF ORDER rather than staying dead");
                                _orderWaitBlocker[padIndex] = -1;
                                _orderWaitSinceTick[padIndex] = 0;
                            }
                            else
                            {
                                _orderWaitBlocker[padIndex] = -1;
                                _orderWaitSinceTick[padIndex] = 0;
                            }

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
                            var capturedType = slotType;
                            var capturedProfile = SlotProfileIds[padIndex];
                            var capturedMidiPort = MidiOutputPortOf(padIndex);
                            var capturedBuild = CaptureExtendedBuild(padIndex);
                            var capturedKbm = CaptureKbmBuild(padIndex);
                            var capturedPersonaOwner = _personaAudioOwner;
                            PadForge.Engine.SdlDiagLog.WriteLine(
                                $"VCTRACE slot={padIndex} async create KICK type={slotType}");
                            _pendingConnectTask[padIndex] = System.Threading.Tasks.Task.Run(() =>
                            {
                                IVirtualController vcAsync = null;
                                bool published = false;
                                try
                                {
                                    vcAsync = CreateVirtualController(capturedIndex, capturedType, capturedProfile, capturedBuild, capturedKbm);
                                    PadForge.Engine.SdlDiagLog.WriteLine(
                                        $"VCTRACE slot={capturedIndex} async create RESULT vc={(vcAsync == null ? "null" : vcAsync.GetType().Name)} connected={vcAsync?.IsConnected ?? false}");
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
                                        // Under the lifecycle lock: the reorder
                                        // snapshots and rewrites this array under
                                        // it, and a publication beside that
                                        // rewrite let the reorder's plain store
                                        // overwrite a just-published controller,
                                        // which then lived on the bus with no
                                        // handle in any slot. The applied-state
                                        // arrays the reorder moves are published
                                        // in the same critical section.
                                        bool closed = !TryPublishCreatedController(capturedIndex, vcAsync,
                                            out var prior, out var effects, out var personaFeed, capturedPersonaOwner);
                                        published = !closed && prior == null;
                                        if (closed)
                                        {
                                            // The engine tore everything down
                                            // while this create was running.
                                            // Nothing would ever destroy a
                                            // controller published now.
                                            PadForge.Engine.SdlDiagLog.WriteLine(
                                                $"VCTRACE slot={capturedIndex} async create finished after teardown, disposing");
                                            try { vcAsync.Dispose(); }
                                            catch { /* best effort */ }
                                        }
                                        else if (prior != null)
                                        {
                                            try { vcAsync.Dispose(); }
                                            catch { /* best effort */ }

                                            // Unpublished controllers never register user effects,
                                            // so disposing the spare leaves the winner's binding alone.
                                        }
                                        else
                                        {
                                            // A MIDI slot whose port failed before starts now, so
                                            // its status line goes.
                                            ResolveMidiCreateError(capturedIndex);
                                            // Provider walks, timers, and physical output run after
                                            // publication, outside the lifecycle lock. Teardown can
                                            // dispose this captured dispatcher before it starts.
                                            try { AudioPassthroughService.RequestPersonaReconcile(personaFeed); }
                                            catch (Exception ex) { RaiseError($"Failed to reconcile persona audio for pad {capturedIndex}", ex); }
                                            try { effects?.StartDeferredEffects(); }
                                            catch (Exception ex)
                                            {
                                                RaiseError($"Failed to apply effects for pad {capturedIndex}", ex);
                                            }
                                            if (vcAsync is HMaestroVirtualController
                                                || vcAsync is KeyboardMouseVirtualController { HasHidDevices: true })
                                            {
                                                try { _hmaestroContext?.FinalizeNames(); }
                                                catch { /* best effort */ }
                                            }
                                        }
                                    }
                                    else if (vcAsync == null)
                                    {
                                        // Latch only REAL failures (audit 2026-07-25, C1).
                                        // CreateVirtualController re-reads live slot state,
                                        // so a slot that lost its devices, its enabled bit,
                                        // or its type between the KICK and the task body
                                        // returns null as an ABORT, not a driver failure.
                                        // The live diag log showed a one-tick device flap at
                                        // launch arming the latch 4/4 launches, and no
                                        // clear path fires on a later genuine device
                                        // arrival, so the abort latched the slot dead until
                                        // a manual reconfigure.
                                        // Own buffer: this runs on the async
                                        // task thread, and the parameterless
                                        // IsSlotActive would race the poll
                                        // thread's shared scratch (round 33,
                                        // C20). Rare path; the allocation is
                                        // irrelevant.
                                        bool stillEligible =
                                            SettingsManager.SlotCreated[capturedIndex]
                                            && SettingsManager.SlotEnabled[capturedIndex]
                                            && SlotControllerTypes[capturedIndex] == capturedType
                                            && IsSlotActive(capturedIndex, new Engine.Data.UserSetting[64]);
                                        if (stillEligible)
                                        {
                                            LatchCreateFailed(capturedIndex, capturedType, capturedProfile, capturedMidiPort, capturedKbm);
                                        }
                                        else
                                        {
                                            PadForge.Engine.SdlDiagLog.WriteLine(
                                                $"VCTRACE slot={capturedIndex} async create ABANDONED (slot no longer eligible, no latch)");
                                            // Retyped while this create was
                                            // under way: the line it raised a
                                            // moment ago is for a port the
                                            // slot no longer wants, so it goes
                                            // here and not a pass later.
                                            if (SlotControllerTypes[capturedIndex] != capturedType)
                                                ResolveMidiCreateError(capturedIndex);
                                        }
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
                                        LatchCreateFailed(capturedIndex, capturedType, capturedProfile, capturedMidiPort, capturedKbm);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    if (!published)
                                    {
                                        try { vcAsync?.Dispose(); } catch { }
                                    }
                                    PadForge.Engine.SdlDiagLog.WriteLine(
                                        $"VCTRACE slot={capturedIndex} async create THREW {ex.GetType().Name}: {ex.Message}");
                                    // The slot by the name the UI shows, as
                                    // the create's own failures name it. This
                                    // line read "for pad" and the pad index.
                                    // The log gets its English line.
                                    string threwLine = string.Format(Strings.Instance.Status_SlotCreateFailed_Format, SlotLabel(capturedIndex));
                                    string threwLog = CreateFailedLogLine(capturedIndex, capturedType);
                                    if (capturedType == VirtualControllerType.Midi) RaiseMidiCreateError(capturedIndex, threwLine, threwLog, ex);
                                    else RaiseError(threwLine, threwLog, ex);
                                    LatchCreateFailed(capturedIndex, capturedType, capturedProfile, capturedMidiPort, capturedKbm);
                                }
                                finally
                                {
                                    // Publication transfers the claim. A failed, spare, or
                                    // closed worker releases only its unpublished claim.
                                    ReleasePendingOemOverrideClaim(capturedIndex);
                                    _slotInitializing[capturedIndex] = false;
                                    PadForge.Engine.SdlDiagLog.WriteLine(
                                        $"VCTRACE slot={capturedIndex} async create DONE");
                                }
                            });
                            // One HM connect kicked off per polling cycle.
                            // The pendingConnect gate above blocks the next
                            // cycle's Pass 2 from kicking off another until
                            // this one completes, preserving the
                            // ascending-kernel-slot allocation guarantee.
                            break;
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
                        CombinedRawHidStates[padIndex].ClearToRest(SlotCustomLayouts[padIndex]);
                        CombinedMidiRawStates[padIndex].Clear();
                        CombinedKbmRawStates[padIndex].Clear();
                        CombinedVrRawStates[padIndex].Clear();
                        CombinedTouchpadStates[padIndex] = default;
                        CombinedPressureStates[padIndex] = default;
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
                        {
                            // The slot's MIDI bar, live: a channel, number
                            // or velocity edit reaches the running
                            // controller without closing its port.
                            var midiCfg = _midiConfigs[padIndex];
                            if (midiCfg != null)
                                midiVc.ApplyLayout(midiCfg.Channel - 1, midiCfg.StartCc, midiCfg.CcCount,
                                    midiCfg.StartNote, midiCfg.NoteCount, midiCfg.Velocity);
                            midiVc.SubmitMidiRawState(CombinedMidiRawStates[padIndex]);
                        }
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
                        else if (vc is HMaestroVRController vrVc)
                        {
                            // Same restricted-peer rule as KBM: a
                            // gamepad-only-restricted peer feeding this slot
                            // must not reach SteamVR, so submit neutral.
                            var vrOut = IsSlotRestricted(padIndex) ? default : CombinedVrRawStates[padIndex];
                            LogVrSubmit(padIndex, in vrOut);
                            vrVc.SubmitVrState(in vrOut);
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
                            // A game mod's TriggerThreshold (#529) on the raw
                            // trigger axes, in a copy, as the gamepad path
                            // below applies it to its local state: the pad
                            // page still reads the real pull. Every submit
                            // below takes rawOut.
                            var rawOut = CombinedRawHidStates[padIndex];
                            if (GameModEffects.AnyHeld)
                            {
                                GameModEffects.ThresholdsForSlot(padIndex, out byte modLeftPoint, out byte modRightPoint);
                                if (modLeftPoint != 0 || modRightPoint != 0)
                                    rawOut = GameModEffects.ApplyRawThresholds(in rawOut, in layout,
                                        modLeftPoint, modRightPoint, ref _modRawAxesScratch);
                            }
                            // Valve persona (#338): a slot on a Valve
                            // profile submits the pad's native frame instead
                            // of the field-encoded raw surface, exactly the
                            // SubmitRawReport split the Sony USB personas
                            // ride below. The packer table carries all four
                            // Valve profiles, and HMaestroVirtualController
                            // picks the verbatim extended path for the ones
                            // whose descriptors need it (HM#58).
                            var valvePacker = ValveReportPackers.ForProfile(hmExt.ProfileId);
                            if (valvePacker != null)
                            {
                                valvePacker.Pack(
                                    rawOut,
                                    CombinedTouchpadStates[padIndex],
                                    MotionSnapshots[padIndex],
                                    unchecked((uint)_deckFrameCounter++),
                                    ValveReportPackers.MicrosNow(),
                                    _deckReportScratch);
                                hmExt.SubmitRawReport(new ReadOnlySpan<byte>(_deckReportScratch, 0, valvePacker.Size));
                            }
                            // A persona that builds its own reports takes no
                            // finished frame, whatever its layout counts say.
                            // The raw submit throws on it, and would on every
                            // poll, so its slot always submits state.
                            else if (!hmExt.BuildsItsOwnReports && ExtendedReportPacker.NeedsRawReport(layout))
                            {
                                // Past 32 buttons or one hat the fixed gamepad
                                // state has nowhere to put the rest, so the
                                // frame is packed against the descriptor this
                                // slot's own build emitted and submitted raw.
                                int packedLen = ExtendedReportPacker.Pack(
                                    rawOut, layout, _extendedReportScratch);
                                if (packedLen > 0)
                                    hmExt.SubmitPackedExtendedReport(
                                        new ReadOnlySpan<byte>(_extendedReportScratch, 0, packedLen));
                            }
                            else
                            hmExt.SubmitRawHidState(
                                rawOut,
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
                            // this OR. USB profiles with a packer submit only
                            // the raw frame, which the packer builds from this
                            // same gpOut, so it carries the OR too.
                            var gpOut = CombinedOutputStates[padIndex];
                            if (SlotControllerTypes[padIndex] == VirtualControllerType.PlayStation
                                && CombinedTouchpadStates[padIndex].Click)
                            {
                                gpOut.Buttons |= Gamepad.TOUCHPAD;
                            }

                            // Keep Controller Awake (#270): hold the
                            // configured idle deflection on the chosen axis
                            // of the FINAL combined output, so games that
                            // cut vibration on mouse/keyboard input keep
                            // treating this controller as active. Applied
                            // to the local copy right before submit (the
                            // SOCD precedent): the mapping pipeline's
                            // curves and deadzones never see it, and real
                            // output at or above the held level passes
                            // through unchanged.
                            // Live frames only. Two paths arrive here with a
                            // deliberately cleared output, and the hold turned both
                            // back into a pushed stick: the disconnect transition
                            // frame, which is the LAST submit before the wire
                            // freezes for the whole grace period, and the focus
                            // suspend edge plus its upkeep, which neutralize
                            // precisely so the virtual pad reads at rest while the
                            // engine is stopped.
                            if (_slotInactiveCounter[padIndex] == 0 && !_focusSuspended)
                            {
                                var kaSets = SettingsManager.SlotMappingSets;
                                var kaMs = (kaSets != null && padIndex < kaSets.Length)
                                    ? kaSets[padIndex] : null;
                                if (kaMs != null)
                                    ApplyKeepAwake(kaMs, ref gpOut, Environment.TickCount64);
                            }

                            // Button SOCD (#240): clean the final combined
                            // bitmask right before submit, so physical,
                            // mapped, and macro contributions are treated
                            // uniformly and the winner's release re-presses
                            // the held partner the same frame.
                            var socdGp = ResolveSlotSocd(padIndex, extendedIndices: false);
                            if (socdGp != null)
                                gpOut.Buttons = socdGp.ApplyGamepad(gpOut.Buttons);

                            // A game mod's TriggerThreshold (#529): below its
                            // point the virtual controller reports the trigger
                            // as 0. On this local copy right before submit, as
                            // above, so the pad page and the impulse
                            // translation still read the real pull.
                            if (GameModEffects.AnyHeld)
                            {
                                GameModEffects.ThresholdsForSlot(padIndex, out byte modLeftPoint, out byte modRightPoint);
                                if (modLeftPoint != 0)
                                    gpOut.LeftTrigger = GameModEffects.ApplyThreshold(gpOut.LeftTrigger, modLeftPoint);
                                if (modRightPoint != 0)
                                    gpOut.RightTrigger = GameModEffects.ApplyThreshold(gpOut.RightTrigger, modRightPoint);
                            }

                            // PlayStation slots backed by an HM virtual go
                            // through the extended SubmitGamepadState overload
                            // so HMGamepadState's touchpad / IMU / battery
                            // fields populate from the assigned physical pad's
                            // SDL sensor reads. BT profiles depend on this
                            // entirely (no SubmitRawReport packer for BT —
                            // their input report is the vendor-blob 0x31
                            // shape, written by HM's encoder from the state
                            // fields). USB profiles with a packer skip this
                            // call and submit only the raw frame below.
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
                                // The DualShock 3 (SIXAXIS): Full rides this leg
                                // (no packer), and the button pressure rides
                                // with it (discussion #476). gpOut decides
                                // which buttons may send theirs.
                                if (SonyReportPackers.ForProfile(hmExtState.ProfileId) == null)
                                    hmExtState.SubmitGamepadState(
                                        gpOut,
                                        CombinedTouchpadStates[padIndex],
                                        MotionSnapshots[padIndex],
                                        pctByte,
                                        BatteryCharging[padIndex],
                                        CombinedPressureStates[padIndex]);
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
                                    // (#433) A game with adaptive triggers reads
                                    // the pad's trigger feedback bytes back. Carry
                                    // the assigned physical DualSense's bytes
                                    // 40..47 the way a real pad reports them.
                                    // Zero, and so a no-op, until the SDL fork
                                    // publishes them.
                                    if (SonyReportPackers.IsDualSenseProfile(hmPs.ProfileId))
                                        SonyReportPackers.ApplyDualSenseStatusBytes(
                                            rawReportScratch, Ds5StatusBytes[padIndex]);
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
            if (_hmaestroContext != null || HmSetupHeld())
                return;
            // The test host installs no driver and sets up no context.
            if (HMaestroVirtualController.OffLimitsForTest) return;

            lock (_hmaestroContextLock)
            {
                if (_hmaestroContext != null || HmSetupHeld())
                    return;

                HMContext ctx = null;
                try
                {
                    // Preflight: remove what programs that have ended left
                    // behind, a crashed or killed PadForge's controllers among
                    // them. Since HIDMaestro 2.0.0 this sweep never touches a
                    // running program's controllers, this one's included
                    // (HMContext.RemoveAllVirtualControllers). InstallDriver
                    // reclaims the devices of ended programs by itself before
                    // it deploys, because a device bound to the old INF keeps
                    // pnputil from replacing the package. The sweep also prunes
                    // HIDMaestro's registry ledger and the records Windows
                    // keeps of removed devices (OwnerSweep.RemoveOrphaned).
                    //
                    // preserveInstall: TRUE. The flag guards the driver
                    // packages and HKLM\SOFTWARE\HIDMaestro, and HIDMaestro
                    // names a program's own launch sweep as the case for
                    // keeping them. That tree holds the manifest hash behind
                    // InstallDriver's fast path, so removing it costs the
                    // ~3 s full deploy for nothing. It also holds the VR
                    // driver's install hash and the SteamVRPath hint, and an
                    // earlier preflight that removed it sent a VR slot back to
                    // extracting driver_hidmaestro.dll into a running
                    // vrserver.exe whenever a session mixed a conventional
                    // slot with a VR slot.
                    try { HMaestroVirtualController.SweepVirtualControllers(); }
                    catch (Exception)
                    {
                    }

                    ctx = new HMContext();
                    int n = ctx.LoadDefaultProfiles();
                    ctx.InstallDriver();
                    bool recovered = _hmaestroSetupFailedTick != 0;
                    Volatile.Write(ref _hmaestroSetupFailedTick, 0);
                    if (recovered) RaiseErrorResolved(HmSetupFailedMessage);

                    // No exit sweep of PadForge's own. Through 1.x the sweep
                    // removed every controller on the PC, so a ProcessExit
                    // hook here was the safety net for a process that ended
                    // without disposing its controllers. Since 2.0.0 the sweep
                    // leaves a running program's controllers alone, this
                    // one's included, and HIDMaestro's own ProcessExit handler,
                    // added with its owner claim at the program's first
                    // cleanup, InstallDriver or create (MachineClaims.EnsureOwner),
                    // removes the controllers a program did not dispose. A killed
                    // PadForge's controllers go with the next sweep of any
                    // program on the PC, this preflight's among them.

                    // Published last, after everything that can throw. A
                    // context published before a throw stayed in the field
                    // disposed, and every later call returned early on it.
                    // A created controller is published after its Connect.
                    _hmaestroContext = ctx;
                }
                catch (Exception ex)
                {
                    // Never published, so it holds no controllers, and its
                    // Dispose returns at once (HMContext.Dispose).
                    try { ctx?.Dispose(); } catch { }
                    Volatile.Write(ref _hmaestroSetupFailedTick, Math.Max(1, Environment.TickCount64));
                    RaiseError(HmSetupFailedMessage, ex);
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

        /// <summary>UI-thread staleness guard for the marshaled inactivity
        /// teardown. The poll thread SETS the fired latch when the countdown
        /// expires and CLEARS it the moment the slot goes active again
        /// (devices online). A teardown that arrives on the UI thread after
        /// the clear is stale: a new controller was assigned/came online in
        /// the BeginInvoke gap, and destroying now would kill the live VC of
        /// an active slot (owner repro 2026-07-27: assign a new controller
        /// while the slot's other devices sit offline, moments before the
        /// timeout expires -- the VC vanishes). Latch-only on purpose: the
        /// poll thread owns slot-activity evaluation, and IsSlotActive's
        /// shared buffer must not be raced from the UI thread.</summary>
        public bool InactivityFireStillValid(int padIndex)
            => padIndex >= 0 && padIndex < MaxPads
               && System.Threading.Volatile.Read(ref _hmInactivityFired[padIndex]);

        /// <summary>Last VR state logged per slot, so the trace below fires
        /// on CHANGE rather than at poll rate.</summary>
        private readonly VrRawState[] _lastVrLogged = new VrRawState[MaxPads];
        private readonly bool[] _lastVrLoggedValid = new bool[MaxPads];

        /// <summary>Records what actually goes over the VR wire, per hand.
        /// Diagnostics-ring only, so a normal session writes nothing; it
        /// reaches disk when PADFORGE_DIAG is set. This exists because
        /// every static layer of the VR path reads symmetric (mappings,
        /// Step 3 evaluation, the SDK's pack, the shared-memory stride, the
        /// driver's per-hand routing), so a hand that shows no input in
        /// SteamVR can only be settled by seeing the submitted bytes.</summary>
        private void LogVrSubmit(int padIndex, in VrRawState vr)
        {
            if (!PadForge.Engine.SdlDiagLog.IsMirroring) return;
            if (_lastVrLoggedValid[padIndex]
                && _lastVrLogged[padIndex].Left.Buttons == vr.Left.Buttons
                && _lastVrLogged[padIndex].Right.Buttons == vr.Right.Buttons
                && _lastVrLogged[padIndex].Left.Trigger == vr.Left.Trigger
                && _lastVrLogged[padIndex].Right.Trigger == vr.Right.Trigger
                && _lastVrLogged[padIndex].Left.Grip == vr.Left.Grip
                && _lastVrLogged[padIndex].Right.Grip == vr.Right.Grip
                && _lastVrLogged[padIndex].Left.StickX == vr.Left.StickX
                && _lastVrLogged[padIndex].Right.StickX == vr.Right.StickX
                && _lastVrLogged[padIndex].Left.StickY == vr.Left.StickY
                && _lastVrLogged[padIndex].Right.StickY == vr.Right.StickY)
                return;
            _lastVrLogged[padIndex] = vr;
            _lastVrLoggedValid[padIndex] = true;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"VRSUBMIT slot={padIndex} "
                + $"L[btn=0x{vr.Left.Buttons:X2} trg={vr.Left.Trigger} grip={vr.Left.Grip} "
                + $"x={vr.Left.StickX} y={vr.Left.StickY}] "
                + $"R[btn=0x{vr.Right.Buttons:X2} trg={vr.Right.Trigger} grip={vr.Right.Grip} "
                + $"x={vr.Right.StickX} y={vr.Right.StickY}]");
        }

        private bool IsSlotActive(int padIndex) => IsSlotActive(padIndex, _padIndexBuffer);

        /// <summary>Buffer-explicit form. The parameterless overload uses
        /// the poll thread's preallocated scratch; any OTHER thread (the
        /// async create-failure validation, round 33 C20) must pass its
        /// own buffer, or its read races the poll thread's reuse of the
        /// shared scratch and misclassifies eligibility.</summary>
        private bool IsSlotActive(int padIndex, Engine.Data.UserSetting[] buffer)
        {
            // Slot must be explicitly created AND enabled.
            if (!SettingsManager.SlotCreated[padIndex] || !SettingsManager.SlotEnabled[padIndex])
                return false;

            var settings = SettingsManager.UserSettings;
            if (settings == null) return false;

            int slotCount = settings.FindByPadIndex(padIndex, buffer);
            if (slotCount == 0)
                return false;

            for (int i = 0; i < slotCount; i++)
            {
                var us = buffer[i];
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
        // dualsense-composite rather than dualshock-4-v2: it is the most
        // capable PlayStation persona, the only one carrying the speaker,
        // the microphone and the channel 3/4 voice-coil haptics lane, so a
        // DualSense-aware game gets its native features on a new slot with
        // no configuration. HM v1.4.2 renamed it away from "(composite USB)"
        // so the picker's most-capable entry and the default agree, and
        // v1.8.1 settled the separator: it reads "DualSense (PS5): Full".
        // The id is what this constant and every saved profile carry, and
        // that has never moved, so the rename costs nothing here.
        //
        // One first-run cost this carries that dualshock-4-v2 did not: the
        // first composite create triggers HM's one-time embedded
        // usbip-win2 deploy, and installing that filter restarts the USB
        // root hubs, so every USB device blinks once. It is a one-time
        // event per machine, not per slot.
        public const string DefaultPlayStationProfileId = "dualsense-composite";
        // The synthetic "Custom" entry anchors Extended — new slots start
        // there with Customize auto-enabled and the user fills in the
        // VID/PID/ProductString/layout from scratch. Previous catalog-
        // inheritance default (logitech-f710) would have new users pick
        // up Logitech VID/PID surprise-unexpectedly.
        public const string DefaultRawProfileId = HMaestroProfileCatalog.CustomProfileId;
        /// <summary>The Nintendo category's default profile (owner call
        /// 2026-07-18). Matches HMaestroProfileCatalog.IsNintendoProfile.</summary>
        public const string DefaultNintendoProfileId = "switch-pro";

        /// <summary>True when a catalog slug is one the given controller type
        /// can actually be built from. An empty slug belongs to every type,
        /// because the create resolves it to that type's default.</summary>
        private static bool ProfileBelongsToType(string profileId, VirtualControllerType type)
        {
            if (string.IsNullOrEmpty(profileId)) return true;
            var list = type switch
            {
                VirtualControllerType.Xbox => HMaestroProfileCatalog.XboxProfiles,
                VirtualControllerType.PlayStation => HMaestroProfileCatalog.PlayStationProfiles,
                VirtualControllerType.Nintendo => HMaestroProfileCatalog.NintendoProfiles,
                VirtualControllerType.Extended => HMaestroProfileCatalog.ExtendedProfiles,
                _ => null,
            };
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i]?.Id, profileId, StringComparison.Ordinal)) return true;
            return false;
        }

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

        /// <summary>Builds and connects the controller for a slot. The type
        /// is the caller's captured decision, not a fresh read of
        /// <see cref="SlotControllerTypes"/>: the UI can change the type
        /// between the decision and this call, and the polling thread's
        /// inline path must not find itself building a MIDI controller,
        /// whose Connect waits on the MIDI service, under the lifecycle
        /// lock. A controller built for a type the slot no longer has is
        /// retired by Pass 1's type check on the next cycle.</summary>
        private IVirtualController CreateVirtualController(int padIndex, VirtualControllerType controllerType,
            string capturedProfileId, in ExtendedBuild build, in KbmBuild kbm)
        {
            // No mark of an earlier create outlives it. A create that
            // failed and took no latch, as when its slot lost its device
            // meanwhile, left its shared-port mark for the latch of the
            // slot's next failure, whatever that failure was.
            _createPortMissing[padIndex] = false;
            _createPortShared[padIndex] = false;
            _createNoMidiApi[padIndex] = false;

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
            // the category default if the slot has no explicit selection. The
            // slug is the caller's capture, taken with the type and the build.
            string profileId = !string.IsNullOrEmpty(capturedProfileId)
                ? capturedProfileId
                : GetDefaultProfileId(controllerType);

            IVirtualController vc = null;
            try
            {
                vc = controllerType switch
                {
                    VirtualControllerType.Xbox => CreateHMaestroController(VirtualControllerType.Xbox, profileId, padIndex, in build),
                    VirtualControllerType.PlayStation => CreateHMaestroController(VirtualControllerType.PlayStation, profileId, padIndex, in build),
                    VirtualControllerType.Extended => CreateHMaestroController(VirtualControllerType.Extended, profileId, padIndex, in build),
                    VirtualControllerType.Nintendo => CreateHMaestroController(VirtualControllerType.Nintendo, profileId, padIndex, in build),
                    VirtualControllerType.Midi => CreateMidiController(padIndex),
                    VirtualControllerType.KeyboardMouse => CreateKeyboardMouseController(padIndex, in kbm),
                    VirtualControllerType.Vr => new HMaestroVRController(),
                    _ => null
                };

                if (vc == null) return null;

                // Claim the DirectInput OEM-name table entry for this slot's
                // profile BEFORE Connect, so the label is in place before
                // Windows enumerates the new virtual device. The live-update
                // pass at the top of UpdateVirtualDevices handles subsequent
                // toggles and edits; this is the initial acquisition.
                if (controllerType == VirtualControllerType.Extended
                    && build.OemEnabled
                    && vc is HMaestroVirtualController hmOem)
                {
                    ushort vid = hmOem.ProfileVendorId;
                    ushort pid = hmOem.ProfileProductId;
                    string label = build.ProductString;
                    if (!string.IsNullOrEmpty(label) && vid != 0 && pid != 0)
                        TryAcquireOemOverrideClaim(padIndex, vid, pid, label, pending: true);
                }

                vc.Connect();

                vc.RegisterFeedbackCallback(padIndex, VibrationStates);

                return vc;
            }
            catch (Exception ex)
            {
                vc?.Dispose();
                _createPortMissing[padIndex] = ex is MidiPortNotConnectedException;
                _createNoMidiApi[padIndex] = ex is MidiServiceUnavailableException;
                RaiseCreateError(padIndex, controllerType, ex);
                return null;
            }
        }

        /// <summary>Builds a Keyboard + Mouse controller. A half on Windows
        /// input needs nothing more. A half on a device needs the HIDMaestro
        /// context, set up here on the create worker as it is for the
        /// HIDMaestro types, and its profile: the catalog's own context first,
        /// then PadForge's wrapper catalog for user-imported profiles. A
        /// device the slot cannot have fails the create, and the slot does not
        /// fall back to Windows input it was not set to.</summary>
        private IVirtualController CreateKeyboardMouseController(int padIndex, in KbmBuild kbm)
        {
            if (!kbm.UsesDevices) return new KeyboardMouseVirtualController(padIndex);
            EnsureHMaestroContext();
            if (_hmaestroContext == null) return null;
            HMProfile keyboard = null, mouse = null;
            if (kbm.Keyboard.Length > 0 && (keyboard = ResolveKbmProfile(kbm.Keyboard)) == null) return null;
            if (kbm.Mouse.Length > 0 && (mouse = ResolveKbmProfile(kbm.Mouse)) == null) return null;
            return new KeyboardMouseVirtualController(padIndex, _hmaestroContext, keyboard, mouse);
        }

        private HMProfile ResolveKbmProfile(string profileId)
        {
            var profile = _hmaestroContext.GetProfile(profileId)
                          ?? HMaestroProfileCatalog.GetProfileById(profileId);
            if (profile == null) RaiseError($"HIDMaestro profile '{profileId}' not found.", null);
            return profile;
        }

        /// <summary>Raises a failed create's status line, naming the slot the
        /// way the UI does, in the UI language. Every failure of a MIDI
        /// create is remembered for its slot
        /// (<see cref="RaiseMidiCreateError"/>), whether or not the failure
        /// latches. The remembered line clears at exactly these points:
        /// when the slot's controller is published, which is when the slot
        /// starts, when the slot is disabled or deleted (Pass 1), when the
        /// slot is no longer a MIDI slot (Pass 1, or the create worker at
        /// once when it finds the slot retyped as its create returns), and
        /// when a later failure of the slot raises another line in its
        /// place. A failure that is not the port's (a session the service
        /// refused, a create that hung) is one the slot can get past by
        /// itself, since a later generation retries it, and its line used
        /// to stay up after the slot ran. That line says MIDI is not running
        /// when no API answered on the instance the create's own service
        /// restart brought up, or no API is in use. Otherwise it says Windows
        /// MIDI Services did not create the port while one of its APIs is the
        /// one in use. The diagnostics log gets its own line, in English
        /// (<see cref="CreateFailedLogLine"/>).</summary>
        private void RaiseCreateError(int padIndex, VirtualControllerType controllerType, Exception ex)
        {
            string logLine = CreateFailedLogLine(padIndex, controllerType);
            if (ex is MidiPortException portFault)
            {
                int other = portFault.Problem == MidiPortProblem.InUse
                    ? OtherMidiSlotOnPort(padIndex, portFault.PortKey) : -1;
                _createPortShared[padIndex] = other >= 0;
                RaiseMidiCreateError(padIndex, MidiPortMessage(padIndex, portFault, other), logLine, ex);
            }
            else if (controllerType == VirtualControllerType.Midi)
            {
                string format = ex is not MidiServiceUnavailableException
                                && MidiVirtualController.ActiveApi is MidiApiKind.InBox or MidiApiKind.AppSdk
                    ? Strings.Instance.Status_MidiCreateFailed_Format
                    : Strings.Instance.Status_MidiNotRunning_Format;
                RaiseMidiCreateError(padIndex, string.Format(format, SlotLabel(padIndex)), logLine, ex);
            }
            else
            {
                RaiseError(string.Format(Strings.Instance.Status_SlotCreateFailed_Format, SlotLabel(padIndex)), logLine, ex);
            }
        }

        /// <summary>The slot's name as the UI shows it: "Virtual Controller"
        /// and the number its card, its page and the Devices badges carry,
        /// which counts the slots in group order
        /// (<see cref="SettingsManager.SlotOrders.GetGlobalSlotNumber"/>),
        /// not the pad index. A slot in no group's order has no shown number
        /// and falls back to its pad.</summary>
        private static string SlotLabel(int padIndex)
            => string.Format(Strings.Instance.Main_VirtualController_Format, ShownSlotNumber(padIndex));

        private static int ShownSlotNumber(int padIndex)
        {
            int shown = SettingsManager.SlotOrders.GetGlobalSlotNumber(padIndex);
            return shown > 0 ? shown : padIndex + 1;
        }

        /// <summary>A failed create's line for the diagnostics log: English
        /// whatever the UI language, since the people who read the log need
        /// not share the user's, and naming the controller type, the slot by
        /// the number the UI shows, and its pad, which the VCTRACE lines
        /// beside it carry. The status text names the slot alone, in the UI
        /// language, and the log line had become that text.</summary>
        private static string CreateFailedLogLine(int padIndex, VirtualControllerType controllerType)
            => $"Failed to create {controllerType} virtual controller for Virtual Controller {ShownSlotNumber(padIndex)} (pad {padIndex})";

        /// <summary>A MIDI create failure's line: the status text, in the UI
        /// language, and the diagnostics log's, in English. Swapped as one,
        /// so the line that clears is logged as the line that was
        /// raised.</summary>
        private sealed class CreateErrorLine
        {
            public CreateErrorLine(string status, string log)
            {
                Status = status;
                Log = log;
            }

            public readonly string Status, Log;
        }

        /// <summary>The line a MIDI create failure raised for each slot, kept
        /// so it clears once the slot starts or is reconfigured.</summary>
        private readonly CreateErrorLine[] _midiCreateError = new CreateErrorLine[MaxPads];

        /// <summary>What the user can do about a legacy port that did not
        /// open, naming the slot the way the UI does. The exception's own
        /// text reached only the diagnostics log, so a new legacy slot read
        /// "Failed to create Midi virtual controller for pad 0" with no word
        /// about picking a port.
        ///
        /// <para>A port that is already in use names the other MIDI slot
        /// that is set to it (<paramref name="other"/>, from
        /// <see cref="OtherMidiSlotOnPort"/>, or -1): on the classic MIDI
        /// stack a port serves one client, and PadForge's own second slot is
        /// such a client, so a line that blamed another program sent the user
        /// looking for one that did not exist. With no such slot the line
        /// says the port is in use and names no culprit.</para></summary>
        private static string MidiPortMessage(int padIndex, MidiPortException fault, int other)
        {
            if (fault.Problem == MidiPortProblem.InUse && other >= 0)
                return string.Format(Strings.Instance.Status_MidiPortShared_Format,
                    SlotLabel(padIndex), fault.PortKey, SlotLabel(other));
            string format = fault.Problem switch
            {
                MidiPortProblem.NotPicked => Strings.Instance.Status_MidiPortNotPicked_Format,
                MidiPortProblem.NotConnected => Strings.Instance.Status_MidiPortNotConnected_Format,
                MidiPortProblem.InUse => Strings.Instance.Status_MidiPortInUse_Format,
                _ => Strings.Instance.Status_MidiPortDidNotOpen_Format,
            };
            return string.Format(format, SlotLabel(padIndex), fault.PortKey);
        }

        /// <summary>Another created, enabled MIDI slot whose bar names
        /// <paramref name="port"/>, or -1. The bar is asked, not the running
        /// controllers: two slots that start together open the port from
        /// two workers, and the one that loses can fail before the other is
        /// published.</summary>
        private int OtherMidiSlotOnPort(int padIndex, string port)
        {
            if (string.IsNullOrEmpty(port)) return -1;
            for (int i = 0; i < MaxPads; i++)
            {
                if (i == padIndex || SlotControllerTypes[i] != VirtualControllerType.Midi) continue;
                if (!SettingsManager.SlotCreated[i] || !SettingsManager.SlotEnabled[i]) continue;
                if (string.Equals(MidiOutputPortOf(i), port, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        /// <summary>Raises a MIDI create failure and remembers it for the
        /// slot, clearing the one it replaces: <paramref name="message"/> for
        /// the status line, <paramref name="logLine"/> for the diagnostics
        /// log.</summary>
        private void RaiseMidiCreateError(int padIndex, string message, string logLine, Exception ex)
        {
            var prior = Interlocked.Exchange(ref _midiCreateError[padIndex], new CreateErrorLine(message, logLine));
            if (prior != null && prior.Status != message) RaiseErrorResolved(prior.Status, prior.Log);
            RaiseError(message, logLine, ex);
        }

        /// <summary>Clears the slot's MIDI create failure from the status line
        /// once it no longer holds: the slot started, or was reconfigured,
        /// disabled or deleted.</summary>
        private void ResolveMidiCreateError(int padIndex)
        {
            var prior = Interlocked.Exchange(ref _midiCreateError[padIndex], null);
            if (prior != null) RaiseErrorResolved(prior.Status, prior.Log);
        }

        private readonly AudioPassthroughService.PersonaFeed[] _personaAudioFeeds = new AudioPassthroughService.PersonaFeed[MaxPads];

        /// <summary>The slot's persona haptic level as a pack, the left
        /// actuator on the low motor and the right on the high motor, or 0
        /// when the slot has no persona feed or the level is stale
        /// (discussion #522, <see cref="PersonaHapticRumble"/>). Read through
        /// the CURRENT feed position, which a reorder moves with its
        /// controller. The level is read before its time, the reverse of the
        /// pacing thread's writes. Any thread.</summary>
        internal long PersonaHapticLevel(int slot, long nowMs)
        {
            if ((uint)slot >= MaxPads) return 0L;
            var feed = Volatile.Read(ref _personaAudioFeeds[slot]);
            if (feed == null) return 0L;
            long level = Volatile.Read(ref feed.HapticLevel);
            long levelMs = Volatile.Read(ref feed.HapticLevelMs);
            return PersonaHapticRumble.Fresh(level, levelMs, nowMs);
        }

        /// <summary><paramref name="raw"/> with the slot's persona haptics
        /// layered on for one device, the step every reader of the slot's
        /// rumble takes before macro rumble and the device's settings:
        /// <paramref name="raw"/> itself unless the slot has a fresh level and
        /// the device takes it (<see cref="PersonaHapticRumble.TakesRumble"/>),
        /// else <paramref name="scratch"/>. Any thread, with a scratch of the
        /// caller's own.</summary>
        internal Vibration WithPersonaHaptics(int slot, Guid device, Vibration raw, Vibration scratch, long nowMs)
        {
            long level = PersonaHapticLevel(slot, nowMs);
            if (level == 0L || !PersonaHapticRumble.TakesRumble(slot, device, nowMs)) return raw;
            return PersonaHapticRumble.Merge(raw, level, scratch);
        }

        /// <summary><paramref name="pack"/> with the slot's persona haptics
        /// layered on its two body voices as a per-voice max, the trigger
        /// voices untouched: the step every reader of the slot's inbound
        /// pack takes (the shaker lane and the Rumble source family). A game
        /// that plays the DualSense's haptics through the persona's audio
        /// sends no rumble, so without it those readers read silence
        /// (discussion #522). Any thread.</summary>
        internal long WithPersonaHaptics(int slot, long pack, long nowMs)
        {
            long level = PersonaHapticLevel(slot, nowMs);
            if (level == 0L) return pack;
            return Engine.Common.LfeOutputState.MaxMerge(pack,
                Engine.Common.LfeOutputState.Low(level),
                Engine.Common.LfeOutputState.High(level), 0, 0);
        }

        private volatile AudioPassthroughService.PersonaOwner _personaAudioOwner = new();
        internal Func<IVirtualController, HIDMaestro.HMUsbAudio> PersonaAudioProvider =
            controller => (controller as HMaestroVirtualController)?.UsbAudio;

        internal bool TryPublishCreatedController(int padIndex, IVirtualController controller,
            out IVirtualController prior, out UserEffectsDispatcher effects)
            => TryPublishCreatedController(padIndex, controller, out prior, out effects, out _);

        internal bool TryPublishCreatedController(int padIndex, IVirtualController controller,
            out IVirtualController prior, out UserEffectsDispatcher effects,
            out AudioPassthroughService.PersonaFeed personaFeed,
            AudioPassthroughService.PersonaOwner personaOwner = null)
        {
            lock (_vcLifecycleLock)
            {
                effects = null;
                personaFeed = null;
                prior = _virtualControllers[padIndex];
                if (_lifecycleClosed) return false;
                if (prior != null) return true;

                // Every effects dispatcher registers only for a winning controller.
                // Construction here does no provider walk or physical output.
                // A constructor failure leaves the controller unpublished.
                if (controller is HMaestroVirtualController hm)
                    effects = hm.PrepareDeviceEffectsForPublication(_deviceSlotConfigs[padIndex]);
                else if (_deviceSlotConfigs[padIndex] != null)
                {
                    effects = new UserEffectsDispatcher(padIndex, _deviceSlotConfigs[padIndex], startTimer: false);
                    _nonHmDispatchers[padIndex] = effects;
                }
                // Composite audio creates demand even with mirror audio and macros off.
                // Registration is managed. Native reconciliation follows publication.
                var audioOwner = personaOwner ?? _personaAudioOwner;
                if (!audioOwner.Closed)
                {
                    var audio = PersonaAudioProvider(controller);
                    if (audio != null)
                        personaFeed = AudioPassthroughService.RegisterPersonaFeed(audioOwner, padIndex, audio,
                            (controller as HMaestroVirtualController)?.ProfileId);
                }
                _personaAudioFeeds[padIndex] = personaFeed;
                _virtualControllers[padIndex] = controller;
                PublishExtendedApplied(padIndex);
                PublishPendingOemOverrideClaim(padIndex);
                return true;
            }
        }

        /// <summary>
        /// Constructs a HIDMaestro-backed virtual controller using the named
        /// embedded profile. The profile slug must match a profile shipped in
        /// HIDMaestro.Core's embedded catalog (337 profiles in HIDMaestro 2.0.2),
        /// or a user profile the catalog loaded beside it.
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
        private IVirtualController CreateHMaestroController(VirtualControllerType type, string profileId, int padIndex, in ExtendedBuild build)
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

            // A profile with its own packer submits a fixed native frame, chosen
            // by profile id in the submit path, so its descriptor is not derived
            // from the layout counts and must not be rebuilt from them. Customize
            // was rebuilding it anyway, which swapped the persona's descriptor
            // for a generic one while the packer kept emitting the persona's
            // bytes. Identity overrides still apply; only the descriptor rebuild
            // is off the table here.
            bool fixedWireFrame = ValveReportPackers.ForProfile(profileId) != null;

            if (type == VirtualControllerType.Extended && build.Customize)
            {
                string userProductString = build.ProductString;
                bool productStringOverrides =
                    !string.IsNullOrEmpty(userProductString)
                    && !string.Equals(userProductString, baseProfile.ProductString, StringComparison.Ordinal);

                var layout = build.Layout;
                int userSticks = layout.Sticks;
                int userTriggers = layout.Triggers;
                int userPovs = layout.Povs;
                int userButtons = layout.Buttons;

                int profSticks = baseProfile.StickCount;
                int profTriggers = baseProfile.TriggerCount;
                int profPovs = baseProfile.HasHat ? 1 : 0;
                int profButtons = baseProfile.ButtonCount;

                bool layoutOverrides =
                    !fixedWireFrame &&
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
                bool forceFeedbackEnabled = build.Ffb;
                bool ffbOverrides = !fixedWireFrame;

                // VID/PID override (0 = use the profile's value). Counts as an
                // override only when non-zero AND different from the base profile,
                // so re-displaying the profile's own VID/PID doesn't force a rebuild.
                int userVid = build.Vid;
                int userPid = build.Pid;
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
                            // The SDK's trigger allocator draws from a pool of
                            // four usages (Rx, Ry, Slider, Dial) that the stick
                            // allocator claims from first, and THROWS when the
                            // pool runs out. A legal trigger-heavy layout (no
                            // sticks and up to eight triggers) exhausted it, and
                            // the catch below quietly ran the catalog profile
                            // instead of the layout the user configured. The
                            // extras take their own Generic Desktop usages,
                            // which the SDK's role classifier claims none of, so
                            // they arrive as plain axes and cannot steal the
                            // left or right trigger role from the real ones.
                            int pooledTriggers = System.Math.Min(triggers,
                                System.Math.Max(0, 4 - System.Math.Max(0, sticks - 2) * 2));
                            for (int t = 0; t < pooledTriggers; t++)
                                descBuilder.AddTrigger(t == 0 ? "Left" : "Right", 16);
                            for (int t = pooledTriggers; t < triggers; t++)
                                descBuilder.AddAxis(ExtraTriggerAxis(t - pooledTriggers), 16, 0, 65535);
                            // One hat per declared POV. A single AddHat left
                            // three of a four-hat layout with no field at all,
                            // so the grid offered POV rows the wire could never
                            // carry.
                            for (int h = 0; h < povs; h++)
                                descBuilder.AddHat();
                            if (buttons > 0)
                                descBuilder.AddButtons(buttons);
                            if (forceFeedbackEnabled)
                                descBuilder.AddPidFfbBlock();
                            builder.FromDescriptorBuilder(descBuilder);
                        }

                        effectiveProfile = builder.Build();
                    }
                    catch (Exception ex)
                    {
                        // Fall back so one bad field does not cost the user the
                        // whole slot, but SAY SO. The applied snapshot below
                        // records the requested values either way, and the
                        // rebuild gate compares against that snapshot, so a
                        // silent fallback left the live descriptor on the catalog
                        // profile with nothing to ever reconcile it: the card
                        // showed a custom layout the device never had.
                        effectiveProfile = baseProfile;
                        RaiseError($"Extended customization for pad {padIndex} could not be built; "
                                   + $"the slot is running the catalog profile '{profileId}' instead.", ex);
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
            // the Extended override fields on a live slot. Pending until the
            // worker wins the slot and publishes under the lifecycle lock.
            _extendedPendingProductString[padIndex] = build.ProductString ?? string.Empty;
            _extendedPendingLayout[padIndex] = build.Layout;
            _extendedPendingFfbEnabled[padIndex] = build.Ffb;
            _extendedPendingCustomize[padIndex] = build.Customize;
            _extendedPendingVendorId[padIndex] = build.Vid;
            _extendedPendingProductId[padIndex] = build.Pid;

            // The key anchors to this pad's position in its own family's order
            // list, not to its pad index (rules b and c at the top of this
            // file). Snapshot rather than walk the live list: this runs on the
            // async connect task and the list is the UI thread's to mutate.
            return new HMaestroVirtualController(_hmaestroContext, effectiveProfile, type,
                HMaestroVirtualController.IdentityKeyForPad(padIndex, type,
                    SettingsManager.SlotOrders.GetOrderSnapshotFor(type)));
        }

        /// <summary>The usage a trigger past the SDK's four-slot pool is
        /// declared on. Generic Desktop, and deliberately outside every usage
        /// the SDK's role classifier names, so an extra trigger is addressable
        /// by its own key and can never be reclassified as the left or right
        /// trigger over a real one. Four is the most any legal layout needs:
        /// eight triggers is only reachable with no sticks, and the pool
        /// carries the first four.</summary>
        private static HIDMaestro.HMAxis ExtraTriggerAxis(int index) => index switch
        {
            0 => HIDMaestro.HMAxis.Vbrx,
            1 => HIDMaestro.HMAxis.Vbry,
            2 => HIDMaestro.HMAxis.Vbrz,
            _ => HIDMaestro.HMAxis.Vno,
        };

        /// <summary>Copies the configuration a just-published HIDMaestro
        /// controller was built with into the applied arrays Pass 1 compares
        /// against. Called under the lifecycle lock, right after the
        /// controller won its slot.</summary>
        private void PublishExtendedApplied(int padIndex)
        {
            _extendedAppliedProductString[padIndex] = _extendedPendingProductString[padIndex];
            _extendedAppliedLayout[padIndex] = _extendedPendingLayout[padIndex];
            _extendedAppliedFfbEnabled[padIndex] = _extendedPendingFfbEnabled[padIndex];
            _extendedAppliedCustomize[padIndex] = _extendedPendingCustomize[padIndex];
            _extendedAppliedVendorId[padIndex] = _extendedPendingVendorId[padIndex];
            _extendedAppliedProductId[padIndex] = _extendedPendingProductId[padIndex];
        }

        /// <summary>
        /// Creates a MIDI virtual controller for the given pad slot from the
        /// slot's MidiConfig: channel, CC and note numbers, velocity, and the
        /// output port the legacy API sends to. Returns null when the slot has
        /// no config or no MIDI API started.
        /// </summary>
        private IVirtualController CreateMidiController(int padIndex)
        {
            var midiConfig = _midiConfigs[padIndex];
            if (midiConfig == null) return null;

            if (!MidiVirtualController.IsAvailable())
            {
                _createNoMidiApi[padIndex] = true;
                RaiseMidiCreateError(padIndex, string.Format(Strings.Instance.Status_MidiNotRunning_Format, SlotLabel(padIndex)),
                    CreateFailedLogLine(padIndex, VirtualControllerType.Midi), null);
                return null;
            }

            // Compute 1-based MIDI instance number (count of MIDI slots up to and including this one)
            int midiInstanceNum = 0;
            for (int i = 0; i <= padIndex; i++)
                if (SlotControllerTypes[i] == VirtualControllerType.Midi)
                    midiInstanceNum++;

            var vc = new MidiVirtualController(padIndex, midiConfig.Channel - 1, midiInstanceNum);
            vc.ApplyLayout(midiConfig.Channel - 1, midiConfig.StartCc, midiConfig.CcCount,
                midiConfig.StartNote, midiConfig.NoteCount, midiConfig.Velocity);
            vc.OutputPort = midiConfig.OutputPort ?? string.Empty;
            return vc;
        }

        private void DestroyVirtualController(int padIndex)
            => DestroyVirtualController(padIndex, asyncDispose: false);

        /// <summary>
        /// Entry point for the bubble-down cascades in InputService, called
        /// from the UI thread. Tears down the slot's VC asynchronously so no
        /// thread blocks on HIDMaestro, under the lifecycle lock, so the slot
        /// reads as empty and its dispose reads as pending together, and
        /// Pass 2 waits for that dispose before it creates anything.
        /// </summary>
        public void DestroyVirtualControllerAsync(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return;
            lock (_vcLifecycleLock) DestroyVirtualController(padIndex, asyncDispose: true);
        }

        /// <summary>
        /// The inactivity teardown, from the UI thread, as one operation
        /// under the lifecycle lock: the fired latch is re-checked under the
        /// same lock the polling thread clears it under, so a device that
        /// came back in the dispatcher hop vetoes the whole teardown, not
        /// just its first half. When the latch holds, the slot's VC is torn
        /// down and every HM VC at a strictly higher position in the same
        /// group's order follows it (the bubble-down cascade), so Pass 2
        /// recreates them in ascending position order once every dispose
        /// has finished. Returns false when nothing was done: the latch was
        /// cleared, or the index is out of range.
        /// </summary>
        public bool TryInactivityTeardown(int padIndex, VirtualControllerType slotType)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return false;
            lock (_vcLifecycleLock)
            {
                if (!InactivityFireStillValid(padIndex)) return false;
                DestroyVirtualController(padIndex, asyncDispose: true);
                if (slotType == VirtualControllerType.Xbox
                    || slotType == VirtualControllerType.PlayStation
                    || slotType == VirtualControllerType.Nintendo
                    || slotType == VirtualControllerType.Extended)
                {
                    var order = SettingsManager.SlotOrders.GetOrderSnapshotFor(slotType);
                    int pos = Array.IndexOf(order, padIndex);
                    if (pos >= 0)
                    {
                        for (int p = pos + 1; p < order.Length; p++)
                        {
                            int higher = order[p];
                            if (!IsHmVcAt(higher)) continue;
                            DestroyVirtualController(higher, asyncDispose: true);
                        }
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Returns true if the slot currently holds an
        /// HMaestroVirtualController (Xbox / PlayStation / Nintendo /
        /// Extended). A VR slot's HMaestroVRController does not count. Used
        /// by the bubble-down cascades (TryInactivityTeardown and
        /// InputService's) when a slot at a lower position transitions to
        /// non-active for any reason (delete, disable,
        /// all-devices-unassigned, HM inactivity timeout), so survivors at
        /// higher positions in the same group get destroyed and recreated,
        /// dropping their kernel slot by one. External observers (xinputhid
        /// for Xbox, DirectInput / SDL / raw HID for the others) all see
        /// this as the natural disconnect/reconnect shape.
        /// </summary>
        public bool IsHmVcAt(int padIndex)
        {
            if (padIndex < 0 || padIndex >= MaxPads) return false;
            return _virtualControllers[padIndex] is HMaestroVirtualController;
        }

        private bool ExtendedConfigurationChanged(int padIndex)
        {
            var desired = SlotCustomLayouts[padIndex];
            var applied = _extendedAppliedLayout[padIndex];
            return !string.Equals(SlotOemOverrideLabel[padIndex] ?? string.Empty,
                    _extendedAppliedProductString[padIndex] ?? string.Empty, StringComparison.Ordinal)
                || desired.Sticks != applied.Sticks
                || desired.Triggers != applied.Triggers
                || desired.Povs != applied.Povs
                || desired.Buttons != applied.Buttons
                || SlotExtendedFfbEnabled[padIndex] != _extendedAppliedFfbEnabled[padIndex]
                || SlotExtendedVendorId[padIndex] != _extendedAppliedVendorId[padIndex]
                || SlotExtendedProductId[padIndex] != _extendedAppliedProductId[padIndex]
                // The Customize flag decides whether the descriptor is the
                // catalog profile's own or a generic build, so flipping it
                // changes the wire. It had no applied counterpart, and the
                // other eight values can be identical across the flip (they
                // are pushed as Customize-gated defaults when it is off), so
                // ticking or clearing the box moved nothing this compared and
                // the live device kept whichever descriptor it was born with.
                || SlotExtendedCustomize[padIndex] != _extendedAppliedCustomize[padIndex];
        }

        /// <summary>
        /// True while a controller awaits retirement after a slot, type,
        /// profile, or Extended configuration change. Pass 2 checks this
        /// under the lifecycle lock before starting another create.
        /// </summary>
        internal bool AnySlotRetiring()
        {
            for (int i = 0; i < MaxPads; i++)
            {
                var vc = _virtualControllers[i];
                if (vc == null) continue;
                if (!SettingsManager.SlotCreated[i] || !SettingsManager.SlotEnabled[i]) return true;
                if (!IsSlotActive(i) && !HasAnyDeviceMapped(i)) return true;
                if (vc.Type != SlotControllerTypes[i]) return true;
                if (vc is HMaestroVirtualController hm)
                {
                    string desired = SlotProfileIds[i];
                    if (!string.IsNullOrEmpty(desired) && !string.Equals(desired, hm.ProfileId, StringComparison.Ordinal)) return true;
                    if (SlotControllerTypes[i] == VirtualControllerType.Extended
                        && ExtendedConfigurationChanged(i)) return true;
                }
                if (vc is KeyboardMouseVirtualController kbm && KbmDevicesChanged(i, kbm)) return true;
            }
            return false;
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
            // Runs on the UI thread. The lifecycle lock keeps a Step 5 cycle
            // from reading the arrangement half written, and keeps this
            // method's destroys from landing after a cycle's Pass 2 gate
            // check and before its creates.
            List<AudioPassthroughService.PersonaFeed> feeds;
            lock (_vcLifecycleLock)
                feeds = RerouteVirtualControllersForReorderLocked(groupType, oldOrder, newOrder);
            foreach (var feed in feeds) AudioPassthroughService.RequestPersonaReconcile(feed);
        }

        private List<AudioPassthroughService.PersonaFeed> RerouteVirtualControllersForReorderLocked(
            VirtualControllerType groupType,
            IReadOnlyList<int> oldOrder,
            IReadOnlyList<int> newOrder)
        {
            var movedFeeds = new List<AudioPassthroughService.PersonaFeed>();
            if (groupType != VirtualControllerType.Xbox
                && groupType != VirtualControllerType.PlayStation
                && groupType != VirtualControllerType.Nintendo
                && groupType != VirtualControllerType.Extended)
                return movedFeeds;

            if (oldOrder == null || newOrder == null) return movedFeeds;
            if (oldOrder.Count != newOrder.Count) return movedFeeds;
            int n = oldOrder.Count;
            if (n == 0) return movedFeeds;

            // Decide per visual position: reuse the existing VC at this
            // kernel slot, or destroy it. Snapshot the per-VC state at
            // the same time so we can move it with the VC.
            var reuseAtPosition = new IVirtualController[n];
            var reuseAudioAtPosition = new AudioPassthroughService.PersonaFeed[n];
            // This method runs on the UI thread. IsSlotActive's parameterless
            // overload reads through _padIndexBuffer, the POLL thread's
            // preallocated scratch, and the buffer-explicit overload's own doc
            // states the rule: any other thread must pass its own buffer or its
            // read races the poll thread's reuse and misclassifies eligibility.
            // One allocation per reorder, which is a user action, not a tick.
            var slotScanBuffer = new Engine.Data.UserSetting[64];
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
                if (_virtualControllers[newPad] == null && !IsSlotActive(newPad, slotScanBuffer))
                    continue;

                string oldProfile = (oldVC is HMaestroVirtualController hmOld) ? hmOld.ProfileId : null;
                string newProfile = SlotProfileIds[newPad];

                if (string.Equals(oldProfile ?? string.Empty, newProfile ?? string.Empty, StringComparison.Ordinal))
                {
                    reuseAtPosition[V] = oldVC;
                    reuseAudioAtPosition[V] = _personaAudioFeeds[oldPad];
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
                _personaAudioFeeds[oldPad] = null;
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

                // A controller the placeholder skip above left in place still
                // owns its pad index, and nothing cleared it in step 2. Writing
                // over the pointer stranded it: no reference left to dispose it,
                // so its kernel device stayed on the bus and its audio feed was
                // never retired. Its own destination was an inactive position, so
                // it is not needed there either. Retire it before taking the index.
                var displaced = _virtualControllers[newPad];
                if (displaced != null && !ReferenceEquals(displaced, vc))
                    DestroyVirtualController(newPad, asyncDispose: true);

                _virtualControllers[newPad] = vc;
                _personaAudioFeeds[newPad] = reuseAudioAtPosition[V];
                _extendedAppliedProductString[newPad] = stateExtendedAppliedProductString[V];
                _extendedAppliedLayout[newPad] = stateExtendedAppliedLayout[V];
                _extendedAppliedFfbEnabled[newPad] = stateExtendedAppliedFfbEnabled[V];
                _extendedAppliedVendorId[newPad] = stateExtendedAppliedVendorId[V];
                _extendedAppliedProductId[newPad] = stateExtendedAppliedProductId[V];
                _oemOverrideClaimedVidPid[newPad] = stateOemOverrideClaimedVidPid[V];
                _lastAppliedOemLabel[newPad] = stateLastAppliedOemLabel[V];
            }
            var audioMoves = new List<(AudioPassthroughService.PersonaFeed Feed, int Slot)>();
            for (int v = 0; v < n; v++)
                if (reuseAtPosition[v] != null && oldOrder[v] != newOrder[v]
                    && newOrder[v] >= 0 && newOrder[v] < MaxPads)
                    audioMoves.Add((reuseAudioAtPosition[v], newOrder[v]));
            AudioPassthroughService.ReroutePersonaFeeds(audioMoves);
            foreach (var move in audioMoves)
                if (move.Feed != null) movedFeeds.Add(move.Feed);

            // Keep the existing dispatcher retarget order. All controller and
            // audio routing metadata is now in place before these live calls.
            // Dispatchers capture pad identity. The destination's configuration
            // remains keyed by that pad index and must follow the reused VC.
            for (int v = 0; v < n; v++)
            {
                int newPad = newOrder[v];
                if (newPad < 0 || newPad >= MaxPads) continue;
                if (reuseAtPosition[v] is HMaestroVirtualController hm)
                    hm.RetargetToPad(newPad, _deviceSlotConfigs[newPad]);
            }
            return movedFeeds;
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
            System.Threading.Volatile.Write(ref _steeringAngleFrames[padIndex], 0);
            var vc = _virtualControllers[padIndex];
            var personaFeed = _personaAudioFeeds[padIndex];
            _personaAudioFeeds[padIndex] = null;
            AudioPassthroughService.RetirePersonaFeed(personaFeed);
            if (vc == null)
            {
                ReleaseSlotEffectsAndClaims(padIndex);
                return;
            }

            // The exact audio owner is retired before SDK disposal. Its native
            // capture/reader cleanup runs separately from this lifecycle lock.

            // #236: VC destruction is an explicit silence edge for ALL
            // FOUR voices (the legacy lifecycle zeroing below touches only
            // the two body motors of VibrationStates). The vacated route
            // resolves to zero before the async disposal below can run,
            // and the generation bump discards any racing lane publish.
            RumbleAudioService.SilenceSlot(padIndex);

            // 2026-07-25 audit: make VibrationStates itself honor that
            // edge, all four motors. The per-site clears zero only the
            // body pair (and the recreate-transition sites clear nothing),
            // so stale Left/RightTriggerMotorSpeed survived destruction
            // with no remaining writer. The LFE lane MaxMerges
            // VibrationStates every tick, latching a trigger shaker voice
            // on until the next game write. A destroyed VC has no game
            // feedback by definition; the successor VC starts from zero.
            // 2026-07-25 audit (C38): detach the driver-side feedback
            // callbacks SYNCHRONOUSLY before zeroing. The async dispose
            // below can take seconds (xinputhid ~11 s worst case) and the
            // OutputDecoded/OutputReceived handlers die only when the
            // controller disposes, so a late game write between the zero
            // and the driver-side close repopulated the motors for a slot
            // that no longer owns them. FeedbackPadIndex = -1 makes every
            // late callback no-op at its own guard.
            if (vc is HMaestroVirtualController hmFb)
                hmFb.UnregisterFeedback();

            var vib = VibrationStates[padIndex];
            if (vib != null)
            {
                vib.LeftMotorSpeed = 0;
                vib.RightMotorSpeed = 0;
                vib.LeftTriggerMotorSpeed = 0;
                vib.RightTriggerMotorSpeed = 0;
                // 2026-07-25 audit (C33): the PID directional/condition
                // projection latches too. Step 2 selects on these flags
                // with no VC-existence check, and the only writer that
                // clears them (HMaestroFfbDecoder.Apply via TickFfb) dies
                // with the VC, so a destroyed slot kept issuing the last
                // constant/spring force to a physical wheel forever, and
                // the stale HasDirectionalData also suppressed the user's
                // own Constant Force feature.
                vib.HasDirectionalData = false;
                vib.HasConditionData = false;
                vib.EffectType = 0;
                vib.SignedMagnitude = 0;
                vib.Direction = 0;
                vib.Period = 0;
                vib.ConditionAxisCount = 0;
                vib.DeviceGain = 255;
            }

            // #240: forget SOCD winner state with the VC. Without this a
            // recreate with identical config strings no-ops Configure and
            // a stale Winner mis-suppresses the first press.
            _slotButtonSocd[padIndex]?.Reset();

            // Keep live teardown ordering: silence feedback before disposing
            // non-HM effects and releasing claims, then disconnect the controller.
            ReleaseSlotEffectsAndClaims(padIndex);
            // Reset the applied snapshot so a later creation starts fresh.
            _extendedAppliedProductString[padIndex] = null;
            _extendedAppliedLayout[padIndex] = default;
            _extendedAppliedFfbEnabled[padIndex] = false;
            _extendedAppliedCustomize[padIndex] = false;
            _extendedAppliedVendorId[padIndex] = 0;
            _extendedAppliedProductId[padIndex] = 0;

            // MIDI teardown ordering: our own input scanner may hold an
            // open loopback client connection to this endpoint, and the
            // service wedges when a virtual endpoint is torn down under a
            // live client from the same process (deterministic on the
            // bench, 2026-07-23). Demote the endpoint claim so the scanner
            // cannot reopen it, close the loopback, give the disconnect
            // RPC a beat to land, THEN tear down the device side.
            var midiVc = vc as MidiVirtualController;
            midiVc?.MarkClosing();

            // MIDI always goes async, whatever the caller asked for. The
            // teardown talks to Windows MIDI Services over WinRT, and an
            // inline MIDI service call on the 1 kHz polling thread wedged
            // both the engine and the close path (owner repro 2026-07-23);
            // the 250 ms beat below is also what keeps the same-process
            // service wedge from firing. Every deliberate MIDI call site
            // already passes true, including shutdown, whose own comment
            // says a hung service must not hang Stop. Two paths reached
            // here with false anyway: the all-devices-unassigned destroy,
            // which only asked for async when the VC was HM, and the
            // inactivity timeout, which uses the parameterless overload.
            if (midiVc != null) asyncDispose = true;

            // A Keyboard + Mouse controller on a virtual keyboard or mouse goes
            // async too: removing the device waits on Windows PnP, which no
            // poll may sit through. Shutdown keeps it synchronous, the way it
            // keeps the HIDMaestro controllers, so the context's own dispose
            // that follows never races a teardown of the same device.
            if (vc is KeyboardMouseVirtualController { HasHidDevices: true } && !_lifecycleClosed)
                asyncDispose = true;

            if (asyncDispose)
            {
                // Publish the dispose task, then empty the slot. Pass 2 gates
                // creation on the task, so a reader that finds the slot empty
                // must already find the task pending. The captured `vc` is
                // disposed in the background. This preserves ascending-
                // slot-order kernel allocation.
                var disposeTask = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (midiVc != null)
                        {
                            CloseMidiInputsForEndpoint(midiVc.UniqueEndpointId);
                            System.Threading.Thread.Sleep(250);
                        }
                        vc.Disconnect();
                        vc.Dispose();
                    }
                    catch { /* best effort */ }
                });
                // A slot can be emptied, refilled by a reorder, and emptied
                // again while the first dispose still runs. The gate reads one
                // task per slot, so an overwrite would drop the first dispose
                // from the record while its pad is still on the bus. Chain.
                var earlier = _pendingDisposeTask[padIndex];
                _pendingDisposeTask[padIndex] = (earlier != null && !earlier.IsCompleted)
                    ? System.Threading.Tasks.Task.WhenAll(earlier, disposeTask)
                    : disposeTask;
                _virtualControllers[padIndex] = null;
            }
            else
            {
                try
                {
                    // MIDI never reaches here (forced async above), so this
                    // branch is the HM / KBM / stub path only.
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
        /// explicit call the teardown would happen in HIDMaestro's own
        /// ProcessExit handler, which runs AFTER the window has closed, so
        /// the window would look closed with cleanup still running headless.
        /// </summary>
        private void DisposeHMaestroContextOnShutdown()
        {
            HMContext ctx;
            lock (_hmaestroContextLock)
            {
                ctx = _hmaestroContext;
                _hmaestroContext = null;
                Volatile.Write(ref _hmaestroSetupFailedTick, 0);
            }
            if (ctx != null)
            {
                try { ctx.Dispose(); }
                catch (Exception ex) { RaiseError("Error disposing HIDMaestro context", ex); }
            }
        }

        private void ReleaseSlotEffectsAndClaims(int padIndex)
        {
            var nonHmEffects = _nonHmDispatchers[padIndex];
            _nonHmDispatchers[padIndex] = null;
            try { nonHmEffects?.Dispose(); } catch { }
            var connecting = _pendingConnectTask[padIndex];
            if (_lifecycleClosed || connecting == null || connecting.IsCompleted)
                ReleasePendingOemOverrideClaim(padIndex);
            uint claimedKey = _oemOverrideClaimedVidPid[padIndex];
            if (claimedKey != 0) ReleaseOemOverrideClaim(padIndex, claimedKey, "destroy");
        }

        private void DestroyAllVirtualControllers()
        {
            lock (_vcLifecycleLock)
            {
                // A create worker still running past the lifecycle wait
                // must not publish into the slots emptied here.
                _lifecycleClosed = true;
                AudioPassthroughService.ClosePersonaOwner(_personaAudioOwner);
                for (int i = 0; i < MaxPads; i++)
                {
                    // MIDI teardown talks to Windows MIDI Services (WinRT); a
                    // hung service must not hang Stop. HM teardown stays
                    // synchronous and ordered (kernel-slot semantics).
                    DestroyVirtualController(i,
                        asyncDispose: _virtualControllers[i] is MidiVirtualController);
                    _virtualControllers[i] = null;
                }
                // The keyboard menus' own keyboards (#566) go with the slots'.
                DestroyMenuKeyboardsLocked();
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
            AddMenuKeyboardTasks(tasks);
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
