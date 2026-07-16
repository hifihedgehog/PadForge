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

        /// <summary>A menu's tick state plus its cached host reads. The
        /// MappingSource wrappers are rebuilt only when the definition's
        /// host changes, so the 1 kHz tick never allocates.</summary>
        internal sealed class MenuTickContext
        {
            public readonly MenuRuntimeState State = new();
            // Last host signature the wrappers were built for. Split into the
            // two raw fields so the per-tick rebuild check is two comparisons
            // instead of an interpolated-string allocation on the 1 kHz path.
            public string HostSigDescriptor = null;
            public int HostSigHalf = int.MinValue;
            public bool IsStick;
            public MappingSource SrcX, SrcY, SrcEngage, SrcClick;
            /// <summary>Last tick timestamp. The fired provider treats a
            /// context nobody ticks (deleted menu, unmapped device) as
            /// expired so stale asserts can never wedge a row on.</summary>
            public long LastTickMs;
        }

        /// <summary>How long a context stays credible without a tick
        /// (poll hiccups ride through; a deleted menu expires).</summary>
        private const int MenuContextStaleMs = 250;

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
        private void UpdateMenuContexts(Engine.Data.UserDevice ud, CustomInputState newState)
        {
            if (ud == null || newState == null) return;

            var sets = SettingsManager.SlotMappingSets;
            if (sets == null) return;

            int[] assignedSlots = GetAssignedSlotsSnapshot(ud.InstanceGuid);
            if (assignedSlots.Length == 0) return;

            long nowMs = Environment.TickCount64;
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
                    if (def == null || !def.Enabled || def.Items == null || def.Items.Count == 0)
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
                    }
                    EnsureMenuSources(ctx, def);
                    ctx.LastTickMs = nowMs;

                    // Layer gate. Base menus stay live under an overlaying
                    // layer (the same inherit-by-default posture Base rows
                    // have); layered menus need their exact layer engaged,
                    // and the layer ending lands in the evaluator as the
                    // release edge (Steam's mode-shift-end commit).
                    bool layerOk;
                    string mask = def.LayerMask ?? "";
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

                    double dz = Math.Clamp(def.EngageDeadzonePercent, 1, 95) / 100.0;
                    double dx = 0, dy = 0;
                    bool physical;
                    bool clicked = false;
                    if (ctx.IsStick)
                    {
                        dx = SourceCoercion.EvaluateForBipolarAxisTarget(
                            newState, ctx.SrcX, slot, false, ud.InstanceGuidString);
                        dy = SourceCoercion.EvaluateForBipolarAxisTarget(
                            newState, ctx.SrcY, slot, false, ud.InstanceGuidString);
                        physical = Math.Sqrt(dx * dx + dy * dy) >= dz;
                        clicked = SourceCoercion.EvaluateForButtonTarget(
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
                            clicked = SourceCoercion.EvaluateForButtonTarget(
                                newState, ctx.SrcClick, 50, slot, ud.InstanceGuidString);
                        }
                    }

                    bool surfaceActive = physical && layerOk;
                    MenuEvaluator.Update(ctx.State, def, surfaceActive, clicked,
                        dx, dy, (dx + 1.0) / 2.0, (dy + 1.0) / 2.0, nowMs);

                    PublishMenuOverlay(slot, ud.InstanceGuid, def, ctx.State, surfaceActive, nowMs);
                }
            }
        }

        /// <summary>First-engaged-wins overlay ownership: an engaged menu
        /// claims the snapshot when it is free (or stale), the owner
        /// refreshes hover every tick, and releases on disengage.</summary>
        private void PublishMenuOverlay(int slot, Guid device, MenuDefinitionEntry def,
            MenuRuntimeState st, bool surfaceActive, long nowMs)
        {
            var cur = _activeMenuOverlay;
            bool owner = cur != null && cur.Slot == slot && cur.Device == device
                && ReferenceEquals(cur.Menu, def);

            if (surfaceActive)
            {
                if (cur == null || owner || nowMs - cur.StampMs > MenuContextStaleMs)
                {
                    _activeMenuOverlay = new MenuOverlayState
                    {
                        Slot = slot,
                        Device = device,
                        Menu = def,
                        HoveredIndex = st.HoveredIndex,
                        StampMs = nowMs,
                    };
                }
            }
            else if (owner)
            {
                _activeMenuOverlay = null;
            }
        }

        /// <summary>Builds (or rebuilds after a host edit) the cached
        /// MappingSource wrappers for a menu's host surface. Sticks read
        /// the abstract "Gamepad {side}Stick{X|Y}" axes and the stick
        /// click; touchpads read the absolute finger-0 position (half-
        /// windowed on single-pad halves, #9 B-1), the contact bool, and
        /// the pad click.</summary>
        private static void EnsureMenuSources(MenuTickContext ctx, MenuDefinitionEntry def)
        {
            if (string.Equals(ctx.HostSigDescriptor, def.HostDescriptor, StringComparison.Ordinal)
                && ctx.HostSigHalf == def.HostHalf) return;
            ctx.HostSigDescriptor = def.HostDescriptor;
            ctx.HostSigHalf = def.HostHalf;

            string host = (def.HostDescriptor ?? "").Trim();
            if (host.StartsWith("Gamepad ", StringComparison.Ordinal))
            {
                ctx.IsStick = true;
                ctx.SrcX = new MappingSource { Descriptor = host + "X" };
                ctx.SrcY = new MappingSource { Descriptor = host + "Y" };
                ctx.SrcEngage = null;
                ctx.SrcClick = new MappingSource { Descriptor = host };
                return;
            }

            ctx.IsStick = false;
            string sfx = def.HostHalf switch { 1 => " Left", 2 => " Right", _ => "" };
            ctx.SrcX = new MappingSource { Descriptor = $"{host} Finger 0 X{sfx}" };
            ctx.SrcY = new MappingSource { Descriptor = $"{host} Finger 0 Y{sfx}" };
            ctx.SrcEngage = new MappingSource { Descriptor = $"{host} Finger 0 Down{sfx}" };
            ctx.SrcClick = new MappingSource { Descriptor = $"{host} Click" };
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
                return MenuContexts.TryGetValue((slotIndex, g, menuId), out var ctx)
                    && nowMs - ctx.LastTickMs <= MenuContextStaleMs
                    && MenuEvaluator.IsItemFired(ctx.State, itemIndex, nowMs);
            }

            foreach (var kv in MenuContexts)
            {
                if (kv.Key.Slot != slotIndex || kv.Key.MenuId != menuId) continue;
                var ctx = kv.Value;
                if (nowMs - ctx.LastTickMs > MenuContextStaleMs) continue;
                if (MenuEvaluator.IsItemFired(ctx.State, itemIndex, nowMs)) return true;
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

            for (int slot = 0; slot < MaxPads && slot < sets.Length; slot++)
            {
                var menus = sets[slot]?.Menus;
                if (menus == null || menus.Count == 0) continue;
                bool restricted = IsSlotRestricted(slot);
                bool extended = SlotExtendedIsCustom[slot];
                uint[] extButtons = extended ? CombinedExtendedRawStates[slot].Buttons : null;
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
                        if (item == null
                            || (item.VirtualKey <= 0 && item.XboxButtons == 0 && item.ExtendedButton <= 0))
                            continue;
                        if (!IsMenuItemFired(slot, null, def.MenuId, item.Index)) continue;
                        if (item.VirtualKey > 0 && !restricted)
                            _desiredLatchedKeys.Add((ushort)item.VirtualKey);
                        if (item.XboxButtons != 0)
                            orMask |= (ushort)item.XboxButtons;
                        if (extended && extButtons != null && item.ExtendedButton > 0)
                        {
                            int n = item.ExtendedButton - 1;
                            int w = n >> 5;
                            if (w < extButtons.Length)
                                extButtons[w] |= 1u << (n & 31);
                        }
                    }
                }

                if (orMask != 0 && !extended)
                    CombinedOutputStates[slot].Buttons |= orMask;
            }
        }
    }
}
