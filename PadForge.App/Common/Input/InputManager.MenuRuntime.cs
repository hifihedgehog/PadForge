using System;
using System.Collections.Concurrent;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Radial / touch menus (#9 B-17)
        //
        //  Per-(slot, device, menu) hover-commit state, ticked from Step 2
        //  beside the gesture contexts. Menu definitions live on the
        //  slot's MappingSet (the same per-slot home ShiftActivators use);
        //  each definition's DeviceGuid filters which assigned devices
        //  drive it ("" = any device on the slot, the Workshop-import
        //  form). Fired items are read through
        //  SourceCoercion.MenuItemFiredProvider by mapping rows, shift
        //  activators, and macro descriptor triggers; items carrying a
        //  DIRECT binding (hand-authored) deliver through
        //  CollectMenuDirectOutputs in the Step 4b pass.
        // ─────────────────────────────────────────────

        /// <summary>Per-(slot, device, menu id) runtime state. Poll thread
        /// writes; the fired provider and the overlay snapshot read.</summary>
        internal readonly ConcurrentDictionary<(int Slot, Guid Device, int MenuId), MenuTickContext>
            MenuContexts = new();

        private readonly object _menuCtxSnapshotLock = new();
        private KeyValuePair<(int Slot, Guid Device, int MenuId), MenuTickContext>[] _menuCtxSnapshotCache;

        /// <summary>Cached array of the live menu contexts, rebuilt only when
        /// the SET of contexts changes.
        ///
        /// <para>The two fired-provider loops used to foreach the
        /// ConcurrentDictionary directly, which allocates a class-based
        /// enumerator on every call, once per direct-bound item per 1 kHz
        /// tick. The IsEmpty early-out those loops carried did not save it:
        /// UpdateMenuContexts re-stamps a context for every enabled menu on
        /// every assigned slot on every tick, so a single authored menu keeps
        /// the dictionary permanently non-empty and the guard never fires.</para>
        ///
        /// <para>Same shape as RestrictedSnapshot in Step1: lazily built and
        /// invalidated, both under one lock, so an invalidation cannot be
        /// overwritten by a rebuild that started before it. Entries are the
        /// live MenuTickContext references, so readers still observe per-tick
        /// LastTickMs and State mutation exactly as the direct enumeration
        /// did. Only the membership is snapshotted.</para></summary>
        private KeyValuePair<(int Slot, Guid Device, int MenuId), MenuTickContext>[] MenuContextsSnapshot()
        {
            lock (_menuCtxSnapshotLock)
            {
                var cached = _menuCtxSnapshotCache;
                if (cached != null) return cached;

                var a = new KeyValuePair<(int Slot, Guid Device, int MenuId), MenuTickContext>[MenuContexts.Count];
                int n = 0;
                foreach (var kv in MenuContexts)
                {
                    if (n >= a.Length) break;   // grew during the copy
                    a[n++] = kv;
                }
                if (n != a.Length) Array.Resize(ref a, n);
                _menuCtxSnapshotCache = a;
                return a;
            }
        }

        /// <summary>Drops the cached array. Called from every site that adds
        /// or removes a context. Missing one would strand a menu whose context
        /// exists but is invisible to the fired provider.</summary>
        private void InvalidateMenuContextsSnapshot()
        {
            lock (_menuCtxSnapshotLock) _menuCtxSnapshotCache = null;
        }

        /// <summary>A menu's tick state plus its cached host reads. The
        /// MappingSource wrappers are rebuilt only when the definition's
        /// host changes, so the 1 kHz tick never allocates.</summary>
        internal sealed class MenuTickContext
        {
            public readonly MenuRuntimeState State = new();
            // Last host signature the wrappers were built for. The four
            // descriptor fields are stored raw and compared individually
            // so the per-tick rebuild check allocates nothing on the
            // 1 kHz path.
            public string HostSigHost;
            public string HostSigCustomX;
            public string HostSigCustomY;
            public string HostSigClick;
            public int HostSigHalf = int.MinValue;
            /// <summary>Authored layer gate and stay-open flag last seen
            /// (#413). An EDIT to either resets the evaluator state, so a
            /// configuration change is never mistaken for a release edge that
            /// commits an old in-flight interaction. The effective engaged
            /// layer changing is NOT an edit and must still reach the
            /// evaluator as the release it is.</summary>
            public bool LayerSigInitialized;
            public string LayerSigMask;
            public bool LayerSigHoldsOpen;
            public bool IsStick;
            /// <summary>Button-pair host (translator v25): the hover vector
            /// is composed from four direction bools (a physical D-pad or
            /// the face-button diamond) instead of an axis pair. Steam
            /// hosts radial menus on both surfaces; the four/five buttons
            /// ARE the selector (an 8-way direction with diagonals on the
            /// D-pad, 4-way on the diamond).</summary>
            public bool IsButtonPair;
            public MappingSource SrcUp, SrcDown, SrcLeft, SrcRight;
            public MappingSource SrcX, SrcY, SrcEngage, SrcClick;
            /// <summary>Last tick timestamp. The fired provider treats a
            /// context nobody ticks (deleted menu, unmapped device) as
            /// expired so stale asserts can never wedge a row on.</summary>
            public long LastTickMs;
        }

        /// <summary>How long a context stays credible without a tick
        /// (poll hiccups ride through; a deleted menu expires).</summary>
        private const int MenuContextStaleMs = 250;

        /// <summary>Stay-open driver per (slot, menu) (#413). Every device
        /// assigned to the slot evaluates a layer-held menu, and at rest each
        /// one is active, so without arbitration the first-polled idle pad
        /// would hold the overlay and assert the resting center while another
        /// pad steers. The device with physical input takes the record, a
        /// resting device keeps it until another moves or the record goes
        /// stale (unplugged). Non-drivers still evaluate, so an interaction
        /// on a pad that just lost the record completes on its own release,
        /// but they hover nothing at rest and never publish.</summary>
        private sealed class MenuDriverRecord { public Guid Device; public long StampMs; }
        private readonly ConcurrentDictionary<(int Slot, int MenuId), MenuDriverRecord> _menuDrivers = new();

        private bool ResolveStayOpenDriver(int slot, int menuId, Guid device, bool physical, long nowMs)
        {
            var key = (slot, menuId);
            if (_menuDrivers.TryGetValue(key, out var rec))
            {
                if (rec.Device == device) { rec.StampMs = nowMs; return true; }
                if (!physical && nowMs - rec.StampMs <= MenuContextStaleMs) return false;
            }
            _menuDrivers[key] = new MenuDriverRecord { Device = device, StampMs = nowMs };
            return true;
        }

        private long _menuCtxLastPurgeMs;

        /// <summary>Clears every menu runtime context and the overlay
        /// snapshot. Called on profile apply: contexts keyed
        /// (slot, device, menu id) would otherwise survive the switch and
        /// the NEW profile's actions could fire from the OLD profile's
        /// in-flight gesture (a Touch Release commit consuming inherited
        /// engagement, Codex audit 2026-07-16).</summary>
        internal void ResetMenuRuntime()
        {
            MenuContexts.Clear();
            _menuDrivers.Clear();
            InvalidateMenuContextsSnapshot();
            _activeMenuOverlay = null;
        }

        /// <summary>Drops one slot's menu contexts, driver records and
        /// overlay ownership (#413). Called beside ClearShiftRuntime when an
        /// authored change ends a layer (an activator edit or delete): the
        /// gate failing on the next tick would otherwise land in a stay-open
        /// menu as the release edge and commit an interaction the user was
        /// mid-way through, a configuration operation firing a binding.</summary>
        internal void ClearMenuRuntimeForSlot(int slot)
        {
            bool removed = false;
            foreach (var kv in MenuContexts)
                if (kv.Key.Slot == slot)
                    removed |= MenuContexts.TryRemove(kv.Key, out _);
            if (removed) InvalidateMenuContextsSnapshot();
            foreach (var kv in _menuDrivers)
                if (kv.Key.Slot == slot)
                    _menuDrivers.TryRemove(kv.Key, out _);
            var cur = _activeMenuOverlay;
            if (cur != null && cur.Slot == slot)
                _activeMenuOverlay = null;
        }

        /// <summary>Drops one device's menu contexts (and its overlay
        /// ownership). Called when a device unregisters: a restricted
        /// Remote Link peer's fired context otherwise stays credible for
        /// the stale window AFTER its restriction was cleared, letting it
        /// inject one last key.</summary>
        internal void PurgeMenuContextsForDevice(Guid device)
        {
            bool removed = false;
            foreach (var kv in MenuContexts)
                if (kv.Key.Device == device)
                    removed |= MenuContexts.TryRemove(kv.Key, out _);
            if (removed) InvalidateMenuContextsSnapshot();
            foreach (var kv in _menuDrivers)
                if (kv.Value.Device == device)
                    _menuDrivers.TryRemove(kv.Key, out _);
            var cur = _activeMenuOverlay;
            if (cur != null && cur.Device == device)
                _activeMenuOverlay = null;
        }

        /// <summary>Overlay snapshot: the currently engaged menu, or null.
        /// Published by the poll thread, consumed by the UI timer at
        /// ~30 Hz (the same pull model every preview uses).</summary>
        public sealed class MenuOverlayState
        {
            public int Slot;
            public Guid Device;
            public MenuDefinitionEntry Menu;
            public int HoveredIndex;
            public long StampMs;
        }

        private volatile MenuOverlayState _activeMenuOverlay;

        /// <summary>The engaged menu the overlay should render, or null
        /// when no menu is engaged. First-engaged wins; the owner updates
        /// its hover every tick and clears the snapshot on disengage.</summary>
        public MenuOverlayState ActiveMenuOverlay => _activeMenuOverlay;

        /// <summary>Ticks every menu this device drives on every slot it
        /// is assigned to. Runs on the poll thread from Step 2, beside
        /// <see cref="UpdateGestureContexts"/>, and unlike that walk it is
        /// NOT gated on the device having touchpads (sticks host menus
        /// too).</summary>
        internal void UpdateMenuContexts(Engine.Data.UserDevice ud, CustomInputState newState)
        {
            if (ud == null || newState == null) return;

            var sets = SettingsManager.SlotMappingSets;
            if (sets == null) return;

            int[] assignedSlots = GetAssignedSlotsSnapshot(ud.InstanceGuid);
            if (assignedSlots.Length == 0) return;

            long nowMs = Environment.TickCount64;

            // Bounded growth: contexts key on (slot, device, menu id), menu
            // ids grow monotonically across add/delete cycles, and nothing
            // else removes entries, so a long session leaked dead contexts.
            // A slow sweep drops anything nobody has ticked for 10 s.
            if (nowMs - _menuCtxLastPurgeMs > 5000)
            {
                _menuCtxLastPurgeMs = nowMs;
                bool purged = false;
                foreach (var kv in MenuContexts)
                    if (nowMs - kv.Value.LastTickMs > 10000)
                        purged |= MenuContexts.TryRemove(kv.Key, out _);
                if (purged) InvalidateMenuContextsSnapshot();
            }

            foreach (int slot in assignedSlots)
            {
                if (slot < 0 || slot >= sets.Length) continue;
                var set = sets[slot];
                var menus = set?.Menus;
                if (menus == null || menus.Count == 0) continue;

                string engagedLayer = null;
                bool engagedLayerResolved = false;

                // Defensive index walk: the UI thread edits this list.
                for (int i = 0; i < menus.Count; i++)
                {
                    MenuDefinitionEntry def;
                    try { def = menus[i]; } catch { break; }
                    // Items are NOT required: a menu whose cells carry no
                    // direct bindings (or no items at all) still hovers,
                    // shows the overlay, and fires its cells as menu-item
                    // sources for mapping rows and macro triggers, exactly
                    // as the binding-kind tooltip promises. The old
                    // Items.Count == 0 skip silently killed pure-source
                    // menus (Codex audit 2026-07-16).
                    if (def == null || !def.Enabled)
                        continue;
                    if (!string.IsNullOrEmpty(def.DeviceGuid)
                        && !string.Equals(def.DeviceGuid, ud.InstanceGuidString,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    var key = (slot, ud.InstanceGuid, def.MenuId);
                    if (!MenuContexts.TryGetValue(key, out var ctx))
                    {
                        ctx = new MenuTickContext();
                        MenuContexts[key] = ctx;
                        // New membership, so the cached array is stale. Only on
                        // the create path: the re-stamp below runs every tick
                        // and must not invalidate anything, which is the whole
                        // point of caching by SET rather than by contents.
                        InvalidateMenuContextsSnapshot();
                    }
                    EnsureMenuSources(ctx, def);
                    ctx.LastTickMs = nowMs;

                    // An authored change to the gate or the stay-open flag
                    // (#413) resets the state rather than playing through the
                    // evaluator as a release. First sight only initializes.
                    string authoredMask = def.LayerMask ?? "";
                    if (!ctx.LayerSigInitialized)
                    {
                        ctx.LayerSigInitialized = true;
                        ctx.LayerSigMask = authoredMask;
                        ctx.LayerSigHoldsOpen = def.LayerHoldsOpen;
                    }
                    else if (!string.Equals(ctx.LayerSigMask, authoredMask, StringComparison.Ordinal)
                             || ctx.LayerSigHoldsOpen != def.LayerHoldsOpen)
                    {
                        ctx.State.Reset();
                        ctx.LayerSigMask = authoredMask;
                        ctx.LayerSigHoldsOpen = def.LayerHoldsOpen;
                    }

                    // Layer gate. Base menus are unconditionally eligible,
                    // including under an overlaying layer (unlike Base
                    // mapping rows, which need InheritUnmapped and can be
                    // overridden per target). Layered menus need their exact
                    // layer engaged, and the layer ending lands in the
                    // evaluator as the release edge (Steam's mode-shift-end
                    // commit).
                    bool layerOk;
                    string mask = authoredMask;
                    if (mask.Length == 0 || mask == "Base")
                    {
                        layerOk = true;
                    }
                    else
                    {
                        if (!engagedLayerResolved)
                        {
                            engagedLayer = GetEngagedLayerMask(slot, set);
                            engagedLayerResolved = true;
                        }
                        layerOk = string.Equals(engagedLayer, mask, StringComparison.Ordinal);
                    }

                    // Stay-open (#413): a real layer keeps the menu open and
                    // the surface only steers. Empty and Base cannot hold a
                    // menu open, there being no exit, so they take the
                    // surface path even with the flag set.
                    bool layerHolds = def.LayerHoldsOpen && mask.Length > 0 && mask != "Base";

                    double dz = Math.Clamp(def.EngageDeadzonePercent, 1, 95) / 100.0;
                    double dx = 0, dy = 0;
                    bool physical;
                    bool clicked = false;
                    if (ctx.IsButtonPair)
                    {
                        // Button-pair host (v25): the hover vector composes
                        // from the four direction bools. Diagonal D-pad
                        // chords land between wedges (Steam's own 8-way
                        // selection on a dpad-hosted radial); the press
                        // itself is the default click, so the Click fire
                        // type commits the pressed cell immediately and
                        // Touch Release commits on release.
                        bool up = SourceCoercion.EvaluateForButtonTarget(
                            newState, ctx.SrcUp, 50, slot, ud.InstanceGuidString);
                        bool down = SourceCoercion.EvaluateForButtonTarget(
                            newState, ctx.SrcDown, 50, slot, ud.InstanceGuidString);
                        bool left = SourceCoercion.EvaluateForButtonTarget(
                            newState, ctx.SrcLeft, 50, slot, ud.InstanceGuidString);
                        bool right = SourceCoercion.EvaluateForButtonTarget(
                            newState, ctx.SrcRight, 50, slot, ud.InstanceGuidString);
                        // Button-pair GRID (hotbar, v26): direction presses
                        // STEP a persistent selection; a four-bool vector
                        // could only reach a grid's edge cells. The
                        // evaluator owns the edge detector and the
                        // step-commit pulse.
                        if (def.Kind == MenuKind.Grid)
                        {
                            MenuEvaluator.StepButtonPairGrid(ctx.State, def, layerOk,
                                up, down, left, right, nowMs);
                            // Hotbars keep their step-and-pulse contract in
                            // every mode; only the overlay's lifetime follows
                            // the stay-open flag (#413), so a layer-held
                            // hotbar stays on screen between presses.
                            bool pairPhysical = up || down || left || right;
                            bool pairActive = MenuEvaluator.ComputeSurfaceActive(def, pairPhysical, layerOk);
                            if (layerHolds && pairActive)
                                pairActive = ResolveStayOpenDriver(slot, def.MenuId, ud.InstanceGuid, pairPhysical, nowMs);
                            PublishMenuOverlay(slot, ud.InstanceGuid, def, ctx.State, pairActive, nowMs, engagedLayer);
                            continue;
                        }
                        dx = (right ? 1 : 0) - (left ? 1 : 0);
                        dy = (down ? 1 : 0) - (up ? 1 : 0);
                        physical = up || down || left || right;
                        clicked = ctx.SrcClick != null
                            ? SourceCoercion.EvaluateForButtonTarget(
                                newState, ctx.SrcClick, 50, slot, ud.InstanceGuidString)
                            : physical;
                    }
                    else if (ctx.IsStick)
                    {
                        // Null sources = an unconfigured Custom opener
                        // (or one with no click assigned): axes read
                        // centered, so a surface-mode menu never engages.
                        // A stay-open one (#413) can still open from its
                        // layer; it just cannot steer, and centerAtRest
                        // below refuses it a resting center.
                        dx = ctx.SrcX != null ? SourceCoercion.EvaluateForBipolarAxisTarget(
                            newState, ctx.SrcX, slot, false, ud.InstanceGuidString) : 0;
                        dy = ctx.SrcY != null ? SourceCoercion.EvaluateForBipolarAxisTarget(
                            newState, ctx.SrcY, slot, false, ud.InstanceGuidString) : 0;
                        // In-Menu Sensitivity (v26): scales the hover
                        // vector, so engage / ring reach costs less (or
                        // more) physical deflection. Identity at 100.
                        if (def.SensitivityPercent > 0 && def.SensitivityPercent != 100)
                        {
                            double sens = def.SensitivityPercent / 100.0;
                            dx = Math.Clamp(dx * sens, -1.0, 1.0);
                            dy = Math.Clamp(dy * sens, -1.0, 1.0);
                        }
                        // Engage/release hysteresis (sc-controller's proven
                        // stick-menu shape: engage at 1/3 deflection, cancel
                        // near center at 1/8). Without it the stick surface
                        // DISENGAGED the moment it re-entered the deadzone,
                        // which made a radial CENTER cell unreachable on
                        // stick hosts: center selection requires resting
                        // inside the deadzone while the menu stays open.
                        // Scoped to radial-with-center menus on the click /
                        // hover fire modes: for Touch Release, re-centering
                        // IS the commit gesture (Steam: a stick inside the
                        // deadzone counts as untouched), so hysteresis there
                        // would break every no-click commit.
                        // Not in stay-open mode (#413): State.Engaged stays
                        // true at rest there, so the lowered threshold would
                        // apply before any new deflection and misread it.
                        bool centerNeedsHold = !layerHolds
                            && def.Kind == MenuKind.Radial
                            && def.HasCenter
                            && def.FireType != MenuFireType.TouchRelease;
                        double mag = Math.Sqrt(dx * dx + dy * dy);
                        double engageAt = centerNeedsHold && ctx.State.Engaged ? dz * 0.4 : dz;
                        physical = mag >= engageAt;
                        clicked = ctx.SrcClick != null && SourceCoercion.EvaluateForButtonTarget(
                            newState, ctx.SrcClick, 50, slot, ud.InstanceGuidString);
                    }
                    else
                    {
                        physical = SourceCoercion.EvaluateForButtonTarget(
                            newState, ctx.SrcEngage, 50, slot, ud.InstanceGuidString);
                        if (physical)
                        {
                            dx = SourceCoercion.EvaluateForBipolarAxisTarget(
                                newState, ctx.SrcX, slot, false, ud.InstanceGuidString);
                            dy = SourceCoercion.EvaluateForBipolarAxisTarget(
                                newState, ctx.SrcY, slot, false, ud.InstanceGuidString);
                            // In-Menu Sensitivity (v26), the stick branch's
                            // twin on the touch surface.
                            if (def.SensitivityPercent > 0 && def.SensitivityPercent != 100)
                            {
                                double sens = def.SensitivityPercent / 100.0;
                                dx = Math.Clamp(dx * sens, -1.0, 1.0);
                                dy = Math.Clamp(dy * sens, -1.0, 1.0);
                            }
                            clicked = SourceCoercion.EvaluateForButtonTarget(
                                newState, ctx.SrcClick, 50, slot, ud.InstanceGuidString);
                        }
                        else if (layerHolds && ctx.SrcClick != null)
                        {
                            // Stay-open (#413): the menu is open with the
                            // finger up, so a separately assigned click that
                            // is still held must read as held. Leaving it
                            // false here manufactured a click-release edge on
                            // every lift.
                            clicked = SourceCoercion.EvaluateForButtonTarget(
                                newState, ctx.SrcClick, 50, slot, ud.InstanceGuidString);
                        }
                    }

                    bool surfaceActive = MenuEvaluator.ComputeSurfaceActive(def, physical, layerOk);
                    bool publishActive = surfaceActive;
                    if (layerHolds)
                    {
                        // Only the driving device (see _menuDrivers) hovers
                        // the resting center and publishes the overlay. A
                        // configured stick resting at center hovers the
                        // center cell when the menu has one. An unconfigured
                        // Custom opener reads zero axes and must not.
                        bool driver = surfaceActive
                            && ResolveStayOpenDriver(slot, def.MenuId, ud.InstanceGuid, physical, nowMs);
                        bool centerAtRest = driver && ctx.IsStick && ctx.SrcX != null && ctx.SrcY != null;
                        MenuEvaluator.UpdateLayerEngaged(ctx.State, def, surfaceActive, physical, clicked,
                            centerAtRest, dx, dy, (dx + 1.0) / 2.0, (dy + 1.0) / 2.0, nowMs);
                        publishActive = driver;
                    }
                    else
                    {
                        MenuEvaluator.Update(ctx.State, def, surfaceActive, clicked,
                            dx, dy, (dx + 1.0) / 2.0, (dy + 1.0) / 2.0, nowMs);
                    }

                    PublishMenuOverlay(slot, ud.InstanceGuid, def, ctx.State, publishActive, nowMs, engagedLayer);
                }
            }
        }

        /// <summary>First-engaged-wins overlay ownership: an engaged menu
        /// claims the snapshot when it is free (or stale), the owner
        /// refreshes hover every tick, and releases on disengage.</summary>
        private void PublishMenuOverlay(int slot, Guid device, MenuDefinitionEntry def,
            MenuRuntimeState st, bool surfaceActive, long nowMs, string engagedLayer)
        {
            var cur = _activeMenuOverlay;
            bool owner = cur != null && cur.Slot == slot && cur.Device == device
                && ReferenceEquals(cur.Menu, def);

            if (surfaceActive)
            {
                // Stay-open handoff (#413): when a cell's macro switches this
                // slot to another layer, the parent's layer has ended THIS
                // tick and it will release on its own evaluation, but if the
                // child is evaluated first it would be refused until the next
                // poll and the overlay would blank for a whole poll. An owner
                // on the SAME slot whose real layer is no longer the engaged
                // one is provably departing, so the child may take the
                // snapshot now. Another slot's owner is never evicted, and a
                // null engagedLayer (only a real-layer candidate resolves
                // it) never authorizes preemption.
                bool ownerDeparting = cur != null && cur.Slot == slot
                    && engagedLayer != null && cur.Menu != null
                    && !string.IsNullOrEmpty(cur.Menu.LayerMask) && cur.Menu.LayerMask != "Base"
                    && !string.Equals(cur.Menu.LayerMask, engagedLayer, StringComparison.Ordinal);

                // Another engaged menu owns the snapshot and is still
                // refreshing it. First-engaged keeps winning.
                if (cur != null && !owner && !ownerDeparting && nowMs - cur.StampMs <= MenuContextStaleMs)
                    return;

                // The snapshot is immutable once published (the UI timer
                // reads it lock-free), so every refresh is an allocation.
                // Republish only when the hover moved or the stamp needs
                // renewing: the consumers' stale gates read 250 ms, so a
                // 100 ms heartbeat keeps an unchanged snapshot credible.
                if (owner && cur.HoveredIndex == st.HoveredIndex
                    && nowMs - cur.StampMs <= 100)
                    return;

                _activeMenuOverlay = new MenuOverlayState
                {
                    Slot = slot,
                    Device = device,
                    Menu = def,
                    HoveredIndex = st.HoveredIndex,
                    StampMs = nowMs,
                };
            }
            else if (owner)
            {
                _activeMenuOverlay = null;
            }
        }

        /// <summary>Builds (or rebuilds after a host edit) the cached
        /// MappingSource wrappers for a menu's opener. Sticks read the
        /// abstract "Gamepad {side}Stick{X|Y}" axes; touchpads read the
        /// absolute finger-0 position (half-windowed on single-pad halves,
        /// #9 B-1) and the contact bool; the Custom opener reads the two
        /// user-recorded raw axes (any device family, engage by deadzone
        /// like a stick). The Click source follows the host's DEFAULT
        /// (stick click / pad click / none for Custom) unless the user
        /// assigned ClickDescriptor, which overrides on EVERY host type:
        /// the old hard-wired under-stick click is a gamepad convention
        /// non-gamepad devices do not share.</summary>
        private static void EnsureMenuSources(MenuTickContext ctx, MenuDefinitionEntry def)
        {
            if (string.Equals(ctx.HostSigHost, def.HostDescriptor, StringComparison.Ordinal)
                && string.Equals(ctx.HostSigCustomX, def.CustomXDescriptor, StringComparison.Ordinal)
                && string.Equals(ctx.HostSigCustomY, def.CustomYDescriptor, StringComparison.Ordinal)
                && string.Equals(ctx.HostSigClick, def.ClickDescriptor, StringComparison.Ordinal)
                && ctx.HostSigHalf == def.HostHalf) return;
            // A host / axis / click reassignment on a stay-open menu (#413)
            // must not complete the previous physical interaction through
            // the new sources. Surface-mode menus never hold state across a
            // host edit worth preserving either, but only the stay-open case
            // can misfire from it, so the reset is scoped to it.
            if (def.LayerHoldsOpen) ctx.State.Reset();
            ctx.HostSigHost = def.HostDescriptor;
            ctx.HostSigCustomX = def.CustomXDescriptor;
            ctx.HostSigCustomY = def.CustomYDescriptor;
            ctx.HostSigClick = def.ClickDescriptor;
            ctx.HostSigHalf = def.HostHalf;

            string clickOverride = (def.ClickDescriptor ?? "").Trim();
            string host = (def.HostDescriptor ?? "").Trim();
            ctx.IsButtonPair = false; // every branch below reasserts its own shape

            if (host.Equals("Custom", StringComparison.Ordinal))
            {
                ctx.IsStick = true; // deadzone-engaged axis pair
                string cx = (def.CustomXDescriptor ?? "").Trim();
                string cy = (def.CustomYDescriptor ?? "").Trim();
                ctx.SrcX = cx.Length > 0 ? new MappingSource { Descriptor = cx } : null;
                ctx.SrcY = cy.Length > 0 ? new MappingSource { Descriptor = cy } : null;
                ctx.SrcEngage = null;
                ctx.SrcClick = clickOverride.Length > 0
                    ? new MappingSource { Descriptor = clickOverride } : null;
                return;
            }

            // Button-pair hosts (translator v25): Steam hosts radial menus
            // on the physical D-pad and the face-button diamond, where the
            // buttons are the selector. The hover vector composes from the
            // four direction bools (SDL frame, +Y down: diamond north = Y,
            // south = A, west = X, east = B); pressing any of them engages
            // the menu, and the press itself is the default click.
            if (host.Equals("Gamepad DPad", StringComparison.Ordinal)
                || host.Equals("Gamepad Diamond", StringComparison.Ordinal))
            {
                bool dpad = host[8] == 'D' && host.Length == 12; // "Gamepad DPad"
                ctx.IsStick = false;
                ctx.IsButtonPair = true;
                ctx.SrcUp = new MappingSource { Descriptor = dpad ? "Gamepad DPadUp" : "Gamepad ButtonY" };
                ctx.SrcDown = new MappingSource { Descriptor = dpad ? "Gamepad DPadDown" : "Gamepad ButtonA" };
                ctx.SrcLeft = new MappingSource { Descriptor = dpad ? "Gamepad DPadLeft" : "Gamepad ButtonX" };
                ctx.SrcRight = new MappingSource { Descriptor = dpad ? "Gamepad DPadRight" : "Gamepad ButtonB" };
                ctx.SrcX = null;
                ctx.SrcY = null;
                ctx.SrcEngage = null;
                ctx.SrcClick = clickOverride.Length > 0
                    ? new MappingSource { Descriptor = clickOverride } : null;
                return;
            }

            if (host.StartsWith("Gamepad ", StringComparison.Ordinal))
            {
                ctx.IsStick = true;
                ctx.SrcX = new MappingSource { Descriptor = host + "X" };
                ctx.SrcY = new MappingSource { Descriptor = host + "Y" };
                ctx.SrcEngage = null;
                ctx.SrcClick = new MappingSource
                {
                    Descriptor = clickOverride.Length > 0 ? clickOverride : host,
                };
                return;
            }

            ctx.IsStick = false;
            string sfx = def.HostHalf switch { 1 => " Left", 2 => " Right", _ => "" };
            ctx.SrcX = new MappingSource { Descriptor = $"{host} Finger 0 X{sfx}" };
            ctx.SrcY = new MappingSource { Descriptor = $"{host} Finger 0 Y{sfx}" };
            ctx.SrcEngage = new MappingSource { Descriptor = $"{host} Finger 0 Down{sfx}" };
            ctx.SrcClick = new MappingSource
            {
                Descriptor = clickOverride.Length > 0 ? clickOverride : $"{host} Click",
            };
        }

        /// <summary>The SourceCoercion.MenuItemFiredProvider body: true
        /// while menu <paramref name="menuId"/>'s item
        /// <paramref name="itemIndex"/> is asserted or commit-pulsed on
        /// <paramref name="slotIndex"/>. An empty device guid (preview
        /// contexts) matches any device driving the menu on the slot.
        /// Contexts nobody ticked recently read false, so a deleted menu
        /// can never wedge a row on.</summary>
        internal bool IsMenuItemFired(int slotIndex, string deviceGuid, int menuId, int itemIndex)
        {
            long nowMs = Environment.TickCount64;
            if (!string.IsNullOrEmpty(deviceGuid) && Guid.TryParse(deviceGuid, out var g))
            {
                if (MenuContexts.TryGetValue((slotIndex, g, menuId), out var ctx)
                    && nowMs - ctx.LastTickMs <= MenuContextStaleMs
                    && MenuEvaluator.IsItemFired(ctx.State, itemIndex, nowMs))
                    return true;

                // The reader layer folds an empty (any-device) source guid
                // onto whichever device is being evaluated, so a
                // multi-device slot could query the WRONG device's context
                // and lose another controller's fire (Codex audit
                // 2026-07-16). When the menu DEFINITION is any-device, any
                // driving device's context is a legitimate match; contexts
                // only ever exist for devices the definition admits, so
                // this cannot cross-match a scoped menu.
                if (!IsMenuDefinitionAnyDevice(slotIndex, menuId)) return false;
            }

            // No live contexts (no menus open anywhere): skip the
            // enumerator allocation — this runs per direct-bound item
            // per 1 kHz tick.
            var snap = MenuContextsSnapshot();
            if (snap.Length == 0) return false;

            for (int i = 0; i < snap.Length; i++)
            {
                ref readonly var kv = ref snap[i];
                if (kv.Key.Slot != slotIndex || kv.Key.MenuId != menuId) continue;
                var ctx = kv.Value;
                if (nowMs - ctx.LastTickMs > MenuContextStaleMs) continue;
                if (MenuEvaluator.IsItemFired(ctx.State, itemIndex, nowMs)) return true;
            }
            return false;
        }

        /// <summary>True while item is fired by at least one device that is
        /// NOT in <paramref name="restrictedDevices"/>. The key-injection
        /// lane uses this instead of the slot-wide restriction so a
        /// restricted Remote Link peer only mutes ITS OWN fires, not a
        /// local controller sharing the slot.</summary>
        private bool IsMenuItemFiredByUnrestricted(
            int slotIndex, int menuId, int itemIndex, Guid[] restrictedDevices)
        {
            var snap = MenuContextsSnapshot();
            if (snap.Length == 0) return false;
            long nowMs = Environment.TickCount64;
            for (int i = 0; i < snap.Length; i++)
            {
                ref readonly var kv = ref snap[i];
                if (kv.Key.Slot != slotIndex || kv.Key.MenuId != menuId) continue;
                if (restrictedDevices != null
                    && Array.IndexOf(restrictedDevices, kv.Key.Device) >= 0) continue;
                var ctx = kv.Value;
                if (nowMs - ctx.LastTickMs > MenuContextStaleMs) continue;
                if (MenuEvaluator.IsItemFired(ctx.State, itemIndex, nowMs)) return true;
            }
            return false;
        }

        /// <summary>True when slot's menu {menuId} exists with an empty
        /// DeviceGuid (the any-device form).</summary>
        private static bool IsMenuDefinitionAnyDevice(int slotIndex, int menuId)
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || slotIndex < 0 || slotIndex >= sets.Length) return false;
            var menus = sets[slotIndex]?.Menus;
            if (menus == null) return false;
            for (int i = 0; i < menus.Count; i++)
            {
                MenuDefinitionEntry def;
                try { def = menus[i]; } catch { break; }
                if (def != null && def.MenuId == menuId)
                    return string.IsNullOrEmpty(def.DeviceGuid);
            }
            return false;
        }

        /// <summary>Step 4b leg: delivers the DIRECT bindings of fired menu
        /// items (hand-authored keys / VC buttons). Keys join the ToggleKey
        /// desired-set reconcile, so a Click-held item holds its key and a
        /// commit pulse taps it, with the release edge guaranteed by the
        /// same diff that releases latches. VC buttons OR into the slot's
        /// combined output exactly like a macro ButtonPress: the Xbox mask
        /// on Xbox / PlayStation slots (the Sony packer translates it), the
        /// 1-based ExtendedButton number as a raw button-word bit on
        /// Extended slots (the macro CustomButtonWords shape,
        /// ApplyMacroLatchesRaw). Called by EvaluateMacros before
        /// ReconcileLatchedKeys, after Step 4 combined both output states.
        /// Imported Workshop items carry no direct bindings (their cells
        /// ride rows / macros), so this pass is hand-author-only by
        /// construction.</summary>
        private void CollectMenuDirectOutputs()
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null) return;

            // Per-DEVICE restriction for the key lane: gating on
            // IsSlotRestricted suppressed a local controller's keyboard
            // cells merely because a restricted peer shared the slot,
            // breaking many-device independence (Codex audit 2026-07-16).
            Guid[] restrictedDevices = RestrictedSnapshot();

            for (int slot = 0; slot < MaxPads && slot < sets.Length; slot++)
            {
                var menus = sets[slot]?.Menus;
                if (menus == null || menus.Count == 0) continue;
                bool extended = SlotRawHidSurface[slot];
                uint[] extButtons = extended ? CombinedRawHidStates[slot].Buttons : null;
                ushort orMask = 0;

                for (int i = 0; i < menus.Count; i++)
                {
                    MenuDefinitionEntry def;
                    try { def = menus[i]; } catch { break; }
                    if (def?.Items == null || !def.Enabled) continue;

                    for (int k = 0; k < def.Items.Count; k++)
                    {
                        MenuItemDefinition item;
                        try { item = def.Items[k]; } catch { break; }
                        if (item == null) continue;

                        // #390 macro cells: stamp the named macro while
                        // this cell is fired. This walk runs BEFORE the
                        // slot evaluators, which read a CURRENT stamp as
                        // an additional trigger source, so the macro's
                        // own trigger mode governs the semantics. A name
                        // the slot's macros do not declare is an inert
                        // no-op, the #377 stale-mask convention. First
                        // name match wins, the evaluator's own order.
                        if (!string.IsNullOrEmpty(item.MacroName)
                            && IsMenuItemFired(slot, null, def.MenuId, item.Index))
                        {
                            var slotMacros = MacroSnapshots[slot];
                            if (slotMacros != null)
                            {
                                for (int m = 0; m < slotMacros.Length; m++)
                                {
                                    var mac = slotMacros[m];
                                    if (mac != null && string.Equals(
                                        mac.Name, item.MacroName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        mac.MenuTriggerTick = MacroPassTick;
                                        break;
                                    }
                                }
                            }
                        }

                        if (item.VirtualKey <= 0 && item.XboxButtons == 0 && item.ExtendedButton <= 0)
                            continue;
                        if (!IsMenuItemFired(slot, null, def.MenuId, item.Index)) continue;
                        if (item.VirtualKey > 0
                            && IsMenuItemFiredByUnrestricted(slot, def.MenuId, item.Index, restrictedDevices))
                            _desiredLatchedKeys.Add((ushort)item.VirtualKey);

                        // Cross-type equivalence (MacroButtonNames.
                        // NumberedMaskOrder): a slot's output-type switch
                        // must not strand an authored binding, so a lone
                        // Xbox mask still fires on an Extended slot as its
                        // numbered equivalent and a lone raw number 1..11
                        // still fires on a mask slot as its button.
                        if (extended)
                        {
                            int number = item.ExtendedButton > 0
                                ? item.ExtendedButton
                                : ViewModels.MacroButtonNames.NumberFromMask(item.XboxButtons);
                            if (extButtons != null && number > 0)
                            {
                                int n = number - 1;
                                int w = n >> 5;
                                if (w < extButtons.Length)
                                    extButtons[w] |= 1u << (n & 31);
                            }
                        }
                        else
                        {
                            ushort mask = item.XboxButtons != 0
                                ? (ushort)item.XboxButtons
                                : ViewModels.MacroButtonNames.MaskFromNumber(item.ExtendedButton);
                            orMask |= mask;
                        }
                    }
                }

                if (orMask != 0 && !extended)
                    CombinedOutputStates[slot].Buttons |= orMask;
            }
        }
    }
}
