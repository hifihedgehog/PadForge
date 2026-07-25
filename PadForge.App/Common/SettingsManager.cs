using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Central manager for device records and mapping settings.
    /// 
    /// This is a static class shared between the background engine thread
    /// and the UI thread. All access to <see cref="UserDevices"/> and
    /// <see cref="UserSettings"/> must be done inside a lock on the
    /// respective collection's SyncRoot.
    /// 
    /// Lifecycle:
    ///   1. SettingsService.Initialize() creates the collections and loads from XML.
    ///   2. InputManager.Step1 adds/updates UserDevice records as devices connect/disconnect.
    ///   3. InputService (UI thread) reads collections to sync ViewModels.
    ///   4. SettingsService.Save() serializes collections to XML on save.
    /// 
    /// This file is the canonical partial; additional partial declarations exist
    /// in InputManager.cs and InputManager.Step1.UpdateDevices.cs for the
    /// UserDevices/UserSettings property declarations and collection class definitions.
    /// </summary>
    public static partial class SettingsManager
    {
        // UserDevices and UserSettings properties are declared in
        // InputManager.Step1.UpdateDevices.cs (partial class).

        // ─────────────────────────────────────────────
        //  Profiles
        // ─────────────────────────────────────────────

        /// <summary>All saved profiles. Empty list = no profiles configured.</summary>
        public static List<ProfileData> Profiles { get; set; } = new();

        /// <summary>The ID of the currently active profile, or null for the default (root) profile.</summary>
        public static string ActiveProfileId { get; set; }

        /// <summary>
        /// Snapshot of the default profile's state captured during settings load,
        /// before the active named profile's topology is applied. Used by
        /// InputService.Start to initialize _defaultProfileSnapshot correctly
        /// when the app restarts with a named profile active.
        /// </summary>
        public static ProfileData PendingDefaultSnapshot { get; set; }

        /// <summary>Whether auto-switching profiles based on foreground application is enabled.</summary>
        public static bool EnableAutoProfileSwitching { get; set; }

        /// <summary>Global macros for profile shortcuts and other app-wide actions.</summary>
        public static GlobalMacroData[] GlobalMacros { get; set; }

        // ─────────────────────────────────────────────
        //  Virtual Controller Slots
        // ─────────────────────────────────────────────

        /// <summary>Maximum number of Xbox virtual controllers.
        /// XInput only sees 4, but SDL / HIDMaestro support up to MaxPads.
        /// Constant name (<c>MaxXbox360Slots</c>) preserved from v2.</summary>
        public const int MaxXbox360Slots = InputManager.MaxPads;

        /// <summary>Maximum number of PlayStation virtual controllers.</summary>
        public const int MaxPlayStationSlots = InputManager.MaxPads;

        /// <summary>Maximum number of Extended virtual controllers (Extended driver limit).</summary>
        public const int MaxExtendedSlots = 16;

        /// <summary>Maximum number of Nintendo virtual controllers.</summary>
        public const int MaxNintendoSlots = InputManager.MaxPads;

        /// <summary>Maximum number of MIDI virtual controllers.</summary>
        public const int MaxMidiSlots = InputManager.MaxPads;

        /// <summary>Maximum number of Keyboard+Mouse virtual controllers.</summary>
        public const int MaxKeyboardMouseSlots = InputManager.MaxPads;

        /// <summary>Whether each slot has been explicitly created. Persisted to settings.</summary>
        public static bool[] SlotCreated { get; set; } = new bool[InputManager.MaxPads];

        /// <summary>Per-VC mapping table (Issue #61 multi-source / shift layer).
        /// One <see cref="Engine.Data.MappingSet"/> per slot. Phase 1b populates
        /// these from the legacy per-(VC × Device) <see cref="Engine.Data.PadSetting"/>
        /// fields on every load; Phase 1c flips Step 3 over to read from here
        /// and PadSetting's mapping fields stop being authoritative.</summary>
        public static Engine.Data.MappingSet[] SlotMappingSets { get; set; }
            = new Engine.Data.MappingSet[InputManager.MaxPads];

        /// <summary>Whether each slot is enabled for virtual-controller output. Persisted to settings.</summary>
        public static bool[] SlotEnabled { get; set; } = new bool[InputManager.MaxPads]
            { true, true, true, true, true, true, true, true,
              true, true, true, true, true, true, true, true };

        /// <summary>
        /// Per-group ordered list of pad indices in user-facing visual order.
        /// Each pad index appears in exactly one list iff <see cref="SlotCreated"/>[i]
        /// is true; the group it appears in is determined by the slot's
        /// <c>OutputType</c>. Ordering within a list drives sidebar / dashboard
        /// rendering. Cross-group operations are forbidden by design — mutate
        /// only via the helpers in <see cref="SlotOrders"/>. Persisted to
        /// settings.
        /// </summary>
        public static List<int> XboxSlotOrder { get; set; } = new();
        public static List<int> PlayStationSlotOrder { get; set; } = new();
        public static List<int> NintendoSlotOrder { get; set; } = new();
        public static List<int> ExtendedSlotOrder { get; set; } = new();
        public static List<int> KeyboardMouseSlotOrder { get; set; } = new();
        public static List<int> MidiSlotOrder { get; set; } = new();

        /// <summary>
        /// Per-group order helpers. All slot-membership / ordering operations
        /// route through this surface so the five group lists stay in lockstep
        /// with <see cref="SlotCreated"/> and the engine's
        /// <c>SlotControllerTypes</c>.
        /// </summary>
        public static class SlotOrders
        {
            /// <summary>Guards the six order lists for the readers
            /// that run off the UI thread: GetGlobalSlotNumber (the Sony
            /// effects dispatcher calls it per dispatch from its
            /// anim-timer and the polling thread, #191) and the Step 5
            /// polling-thread walkers, which read through
            /// GetOrderSnapshotFor. Every mutator locks it too, so a
            /// topology change mid-enumeration can't throw on a non-UI
            /// thread (which would kill the process). UI-thread readers
            /// walking GetOrderFor directly stay unsynchronized, as all
            /// mutations also happen on the UI thread. Leaf lock:
            /// nothing inside it acquires another lock.</summary>
            private static readonly object OrderSync = new object();

            /// <summary>Returns the 1-based global slot number for
            /// <paramref name="padIndex"/>, walking type-group order
            /// (Xbox → PlayStation → Nintendo → Extended → KbM → MIDI) so it matches
            /// the dashboard cards, sidebar, Pad page header, and the
            /// Devices-page assignment badges. Returns 0 when the slot
            /// isn't created or isn't in any group's order list (caller
            /// should treat 0 as "not assigned to a visible global
            /// position" and fall back to padIndex + 1).</summary>
            public static int GetGlobalSlotNumber(int padIndex)
            {
                if (padIndex < 0 || padIndex >= SlotCreated.Length) return 0;
                if (!SlotCreated[padIndex]) return 0;
                lock (OrderSync)
                {
                    int globalCount = 0;
                    foreach (var groupType in Engine.VirtualControllerGroups.InOrder)
                    {
                        foreach (int idx in GetOrderFor(groupType))
                        {
                            if (idx < 0 || idx >= SlotCreated.Length) continue;
                            if (!SlotCreated[idx]) continue;
                            globalCount++;
                            if (idx == padIndex) return globalCount;
                        }
                    }
                    return 0;
                }
            }

            /// <summary>Player-identity precedence for a device feeding
            /// more than one virtual controller: the controller with the
            /// smallest global (displayed) player number owns the device's
            /// player-identity outputs (Wii/Switch player LEDs, DualSense
            /// pips, Sony player lightbar color, DS3 LED).
            /// Without a single winner, each slot's identity writer pushes
            /// its own number to the shared device and the LEDs flicker
            /// between players. Returns the winning 1-based display number,
            /// or 0 when the device isn't assigned to any visible slot.
            /// Mirrors the DualShock 3 idle floor's lowest-slot fold
            /// (InputManager.UpdateDs3PlayerNumber), generalized to compare
            /// DISPLAY numbers so group ordering (Xbox before KbM, user
            /// reorders) decides precedence, matching what the user sees.
            /// Takes UserSettings.SyncRoot then the leaf OrderSync; callers
            /// holding UserDevices.SyncRoot stay inside the documented
            /// devices-before-settings lock order.</summary>
            public static int GetIdentityPlayerNumber(Guid deviceGuid)
            {
                var settings = UserSettings;
                if (settings == null || deviceGuid == Guid.Empty) return 0;
                int best = 0;
                lock (settings.SyncRoot)
                {
                    foreach (var us in settings.Items)
                    {
                        if (us == null || us.InstanceGuid != deviceGuid || us.MapTo < 0)
                            continue;
                        int n = GetGlobalSlotNumber(us.MapTo);
                        if (n > 0 && (best == 0 || n < best)) best = n;
                    }
                }
                return best;
            }

            /// <summary>Copy of a group's order list, taken under the
            /// lock. The polling thread must read through this (a live
            /// list can be mutated mid-walk by the UI thread).</summary>
            public static int[] GetOrderSnapshotFor(Engine.VirtualControllerType type)
            {
                lock (OrderSync)
                {
                    return GetOrderFor(type).ToArray();
                }
            }

            /// <summary>Return the order list for the given VC type group.</summary>
            public static List<int> GetOrderFor(Engine.VirtualControllerType type) => type switch
            {
                Engine.VirtualControllerType.Xbox     => XboxSlotOrder,
                Engine.VirtualControllerType.PlayStation   => PlayStationSlotOrder,
                Engine.VirtualControllerType.Nintendo      => NintendoSlotOrder,
                Engine.VirtualControllerType.Extended      => ExtendedSlotOrder,
                Engine.VirtualControllerType.KeyboardMouse => KeyboardMouseSlotOrder,
                Engine.VirtualControllerType.Midi          => MidiSlotOrder,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
            };

            /// <summary>Append <paramref name="padIndex"/> to its group's tail
            /// if it isn't already present. No-op when already present.</summary>
            public static void Add(int padIndex, Engine.VirtualControllerType type)
            {
                lock (OrderSync)
                {
                    var list = GetOrderFor(type);
                    if (!list.Contains(padIndex)) list.Add(padIndex);
                }
            }

            /// <summary>Remove <paramref name="padIndex"/> from its group's
            /// list. No-op when absent.</summary>
            public static void Remove(int padIndex, Engine.VirtualControllerType type)
            {
                lock (OrderSync)
                {
                    GetOrderFor(type).Remove(padIndex);
                }
            }

            /// <summary>Move <paramref name="padIndex"/> from its current group
            /// to <paramref name="newType"/>'s tail. Used by type-change paths.</summary>
            public static void MoveToGroupTail(int padIndex,
                Engine.VirtualControllerType oldType,
                Engine.VirtualControllerType newType)
            {
                if (oldType == newType) return;
                // One lock over the pair: a cross-thread reader landing
                // between Remove and Add would see the pad in NO group
                // and briefly resolve global number 0 (Monitor is
                // reentrant, so the nested locks are fine).
                lock (OrderSync)
                {
                    Remove(padIndex, oldType);
                    Add(padIndex, newType);
                }
            }

            /// <summary>Move within a single group from
            /// <paramref name="oldPos"/> to <paramref name="newPos"/>.</summary>
            public static void MoveWithinGroup(Engine.VirtualControllerType type, int oldPos, int newPos)
            {
                lock (OrderSync)
                {
                    var list = GetOrderFor(type);
                    if (oldPos < 0 || oldPos >= list.Count) return;
                    if (newPos < 0 || newPos >= list.Count) return;
                    if (oldPos == newPos) return;
                    int padIndex = list[oldPos];
                    list.RemoveAt(oldPos);
                    list.Insert(newPos, padIndex);
                }
            }

            /// <summary>Swap two pad indices' positions within their (shared)
            /// group's order list. Throws if the two pads aren't in the same
            /// group's list.</summary>
            public static void SwapWithinGroup(int padA, int padB, Engine.VirtualControllerType type)
            {
                lock (OrderSync)
                {
                    var list = GetOrderFor(type);
                    int ia = list.IndexOf(padA);
                    int ib = list.IndexOf(padB);
                    if (ia < 0 || ib < 0) return;
                    (list[ia], list[ib]) = (list[ib], list[ia]);
                }
            }

            /// <summary>
            /// Reconcile each group's order list against the current
            /// engine-side topology (<see cref="SlotCreated"/> and the supplied
            /// <paramref name="slotTypes"/> map of pad index → VC type).
            /// For each group: filter the persisted list to entries that are
            /// still created and still in this group; then append any
            /// currently-in-this-group pads that the persisted list omitted,
            /// in ascending pad-index order. Called on settings load and
            /// profile activation. Both paths supply the persisted lists from
            /// the loaded settings (or null when none exist) plus the current
            /// types from the engine. The resulting lists are written back
            /// onto the static <see cref="XboxSlotOrder"/> &amp;c.
            /// </summary>
            public static void RebuildFromCurrentTopology(
                System.Func<int, Engine.VirtualControllerType> slotType,
                int[] persistedXbox = null,
                int[] persistedPlayStation = null,
                int[] persistedExtended = null,
                int[] persistedKbm = null,
                int[] persistedMidi = null,
                int[] persistedNintendo = null)
            {
                Reconcile(XboxSlotOrder,          persistedXbox,        slotType, Engine.VirtualControllerType.Xbox);
                Reconcile(PlayStationSlotOrder,   persistedPlayStation, slotType, Engine.VirtualControllerType.PlayStation);
                Reconcile(NintendoSlotOrder,      persistedNintendo,    slotType, Engine.VirtualControllerType.Nintendo);
                Reconcile(ExtendedSlotOrder,      persistedExtended,    slotType, Engine.VirtualControllerType.Extended);
                Reconcile(KeyboardMouseSlotOrder, persistedKbm,         slotType, Engine.VirtualControllerType.KeyboardMouse);
                Reconcile(MidiSlotOrder,          persistedMidi,        slotType, Engine.VirtualControllerType.Midi);
            }

            private static void Reconcile(List<int> target, int[] persisted,
                System.Func<int, Engine.VirtualControllerType> slotType,
                Engine.VirtualControllerType groupType)
            {
                lock (OrderSync)
                {
                    ReconcileLocked(target, persisted, slotType, groupType);
                }
            }

            private static void ReconcileLocked(List<int> target, int[] persisted,
                System.Func<int, Engine.VirtualControllerType> slotType,
                Engine.VirtualControllerType groupType)
            {
                target.Clear();
                if (persisted != null)
                {
                    foreach (int pi in persisted)
                    {
                        if (pi < 0 || pi >= InputManager.MaxPads) continue;
                        if (!SlotCreated[pi]) continue;
                        if (slotType(pi) != groupType) continue;
                        if (target.Contains(pi)) continue;
                        target.Add(pi);
                    }
                }
                for (int pi = 0; pi < InputManager.MaxPads; pi++)
                {
                    if (!SlotCreated[pi]) continue;
                    if (slotType(pi) != groupType) continue;
                    if (target.Contains(pi)) continue;
                    target.Add(pi);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  Initialization
        // ─────────────────────────────────────────────

        /// <summary>
        /// Ensures the manager's collections are initialized.
        /// Safe to call multiple times.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (UserDevices == null)
                UserDevices = new DeviceCollection();
            if (UserSettings == null)
                UserSettings = new SettingsCollection();
        }

        // ─────────────────────────────────────────────
        //  Device management
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds a UserDevice by instance GUID. Thread-safe.
        /// </summary>
        /// <returns>The device, or null if not found.</returns>
        public static UserDevice FindDeviceByInstanceGuid(Guid instanceGuid)
        {
            var devices = UserDevices;
            if (devices == null) return null;

            lock (devices.SyncRoot)
            {
                // Duplicate-guid defense: the load path dedupes ghost
                // records, but a mid-session duplicate (whatever lane
                // minted it) must not let a capability-less ghost shadow
                // the real record for automap and identity decisions.
                // Prefer the record that actually carries capabilities.
                UserDevice best = null;
                foreach (var d in devices.Items)
                {
                    if (d == null || d.InstanceGuid != instanceGuid) continue;
                    if (best == null) { best = d; continue; }
                    bool dRich = d.CapType != 0
                        || (d.DeviceObjects != null && d.DeviceObjects.Length > 0);
                    bool bestRich = best.CapType != 0
                        || (best.DeviceObjects != null && best.DeviceObjects.Length > 0);
                    if (dRich && !bestRich) best = d;
                }
                return best;
            }
        }

        /// <summary>
        /// Removes a UserDevice by instance GUID. Thread-safe.
        /// Also removes any associated UserSettings.
        /// </summary>
        /// <returns>True if removed.</returns>
        public static bool RemoveDevice(Guid instanceGuid)
        {
            bool removed = false;
            var devices = UserDevices;
            if (devices != null)
            {
                lock (devices.SyncRoot)
                {
                    int idx = devices.Items.FindIndex(d => d.InstanceGuid == instanceGuid);
                    if (idx >= 0)
                    {
                        devices.Items.RemoveAt(idx);
                        removed = true;
                    }
                }
            }

            // Also remove associated settings.
            var settings = UserSettings;
            if (settings != null)
            {
                lock (settings.SyncRoot)
                {
                    settings.Items.RemoveAll(s => s.InstanceGuid == instanceGuid);
                }
            }

            return removed;
        }

        // ─────────────────────────────────────────────
        //  UserSetting management
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds the UserSetting for a device. Thread-safe.
        /// Shorthand for <c>UserSettings.FindByInstanceGuid(guid)</c>.
        /// </summary>
        public static UserSetting FindSettingByInstanceGuid(Guid instanceGuid)
        {
            var settings = UserSettings;
            if (settings == null) return null;
            return settings.FindByInstanceGuid(instanceGuid);
        }

        /// <summary>
        /// Finds the UserSetting for a device on a specific pad slot. Thread-safe.
        /// Required when the same device is mapped to multiple slots.
        /// Returns null if the device is not assigned to the specified slot.
        /// </summary>
        public static UserSetting FindSettingByInstanceGuidAndSlot(Guid instanceGuid, int padIndex)
        {
            var settings = UserSettings;
            if (settings == null) return null;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us.InstanceGuid == instanceGuid && us.MapTo == padIndex)
                        return us;
                }
                return null;
            }
        }

        /// <summary>
        /// Creates or retrieves a UserSetting that links a device to a pad slot.
        /// Supports multi-slot: if the device is already assigned to other slots,
        /// a new UserSetting is created for the additional slot.
        /// Thread-safe.
        /// </summary>
        /// <param name="instanceGuid">Device instance GUID.</param>
        /// <param name="padIndex">Target pad slot (0–7).</param>
        /// <returns>The UserSetting (existing or new).</returns>
        public static UserSetting AssignDeviceToSlot(Guid instanceGuid, int padIndex)
        {
            if (padIndex < 0 || padIndex >= InputManager.MaxPads)
                throw new ArgumentOutOfRangeException(nameof(padIndex), $"Must be 0–{InputManager.MaxPads - 1}.");

            var settings = UserSettings;
            if (settings == null) return null;

            lock (settings.SyncRoot)
            {
                // Check if already assigned to this exact slot — return existing.
                var exactMatch = settings.Items.FirstOrDefault(
                    s => s.InstanceGuid == instanceGuid && s.MapTo == padIndex);

                if (exactMatch != null)
                    return exactMatch;

                // Create a new UserSetting for this slot (supports multi-slot assignment).
                var us = new UserSetting
                {
                    InstanceGuid = instanceGuid,
                    MapTo = padIndex
                };

                // Don't create a PadSetting here — let the caller (DeviceService)
                // create proper defaults based on the device type via CreateDefaultPadSetting().

                settings.Items.Add(us);
                return us;
            }
        }

        /// <summary>
        /// Unassigns a device from its pad slot by removing its UserSetting.
        /// Thread-safe.
        /// </summary>
        public static bool UnassignDevice(Guid instanceGuid)
        {
            var settings = UserSettings;
            if (settings == null) return false;

            lock (settings.SyncRoot)
            {
                return settings.Items.RemoveAll(
                    s => s.InstanceGuid == instanceGuid) > 0;
            }
        }

        /// <summary>
        /// Toggles a device's assignment to a specific slot.
        /// If assigned → removes that UserSetting (unassign). If not → creates one (assign).
        /// Supports multi-slot: a device can have UserSettings for multiple slots.
        /// Thread-safe.
        /// </summary>
        /// <returns>(true, UserSetting) if assigned; (false, null) if unassigned.</returns>
        public static (bool Assigned, UserSetting Setting) ToggleDeviceSlotAssignment(Guid instanceGuid, int padIndex)
        {
            if (padIndex < 0 || padIndex >= InputManager.MaxPads)
                throw new ArgumentOutOfRangeException(nameof(padIndex), $"Must be 0–{InputManager.MaxPads - 1}.");

            var settings = UserSettings;
            if (settings == null) return (false, null);

            lock (settings.SyncRoot)
            {
                var existing = settings.Items.FirstOrDefault(
                    s => s.InstanceGuid == instanceGuid && s.MapTo == padIndex);

                if (existing != null)
                {
                    // Unassign from this slot.
                    settings.Items.Remove(existing);
                    return (false, null);
                }

                // Assign to this slot (new UserSetting entry).
                var us = new UserSetting
                {
                    InstanceGuid = instanceGuid,
                    MapTo = padIndex
                };
                settings.Items.Add(us);
                return (true, us);
            }
        }

        /// <summary>
        /// Returns all slot indices that a device is assigned to.
        /// Thread-safe.
        /// </summary>
        public static List<int> GetAssignedSlots(Guid instanceGuid)
        {
            var result = new List<int>();
            var settings = UserSettings;
            if (settings == null) return result;

            lock (settings.SyncRoot)
            {
                foreach (var s in settings.Items)
                {
                    if (s.InstanceGuid == instanceGuid && s.MapTo >= 0)
                        result.Add(s.MapTo);
                }
            }
            result.Sort();
            return result;
        }

        /// <summary>
        /// Returns a snapshot of all UserSettings assigned to a pad slot.
        /// Thread-safe.
        /// </summary>
        public static List<UserSetting> GetSettingsForSlot(int padIndex)
        {
            var settings = UserSettings;
            if (settings == null) return new List<UserSetting>();
            return settings.FindByPadIndex(padIndex);
        }

        // ─────────────────────────────────────────────
        //  Slot swap
        // ─────────────────────────────────────────────

        // ─────────────────────────────────────────────
        //  PadSetting helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Creates a default PadSetting with standard Xbox controller mappings
        /// auto-detected from the device's capability count.
        /// </summary>
        /// <param name="ud">The device to create defaults for.</param>
        /// <returns>A PadSetting with sensible default mappings.</returns>
        public static PadSetting CreateDefaultPadSetting(UserDevice ud,
            Engine.VirtualControllerType outputType = Engine.VirtualControllerType.Xbox)
        {
            var ps = new PadSetting();

            if (ud == null)
            {
                ps.UpdateChecksum();
                return ps;
            }

            // SDL3 gamepads (CapType == Gamepad) get auto-mapped using the
            // standardized SDL3 gamepad axis/button order:
            //   Axes: LX(0), LY(1), LT(2), RX(3), RY(4), RT(5)
            //   Buttons: A(0), B(1), X(2), Y(3), LB(4), RB(5),
            //            Back(6), Start(7), LS(8), RS(9), Guide(10)
            //   Hats: 1 (D-pad)
            // Skip auto-mapping when Force Raw Joystick Mode is enabled —
            // the user wants to record raw mappings manually.
            if (ud.CapType == InputDeviceType.Gamepad && !ud.ForceRawJoystickMode)
            {
                // Only auto-map inputs the device actually exposes. Binding an
                // output to a source the device lacks is NOT harmless: a missing
                // axis reads 0, and the stick mapper turns 0 into a hard
                // upper-left deflection instead of resting at center (a Wii Remote
                // with no analog sticks pinned both sticks to the corner). So gate
                // every axis/button/hat the way the Misc1/Share binding below
                // already does. When DeviceObjects is unavailable (a
                // capability-less ghost), fall back to the full standard layout so
                // a real gamepad assigned offline still maps.
                var objs = ud.DeviceObjects;
                bool haveCaps = objs != null && objs.Length > 0;
                bool HasAxis(int idx) => !haveCaps || objs.Any(o => o != null
                    && (o.ObjectType & DeviceObjectTypeFlags.AbsoluteAxis) != 0 && o.InputIndex == idx);
                bool HasButton(int idx) => !haveCaps || objs.Any(o => o != null
                    && (o.ObjectType & DeviceObjectTypeFlags.PushButton) != 0 && o.InputIndex == idx);
                bool HasHat() => !haveCaps || objs.Any(o => o != null
                    && (o.ObjectType & DeviceObjectTypeFlags.PointOfViewController) != 0);

                if (outputType == Engine.VirtualControllerType.Midi)
                {
                    // MIDI auto-mapping: CC0-CC5 for axes, Note0-Note10 for buttons.
                    for (int i = 0; i < 6; i++)
                        if (HasAxis(i)) ps.SetMidiMapping($"MidiCC{i}", $"Axis {i}");
                    for (int i = 0; i < 11; i++)
                        if (HasButton(i)) ps.SetMidiMapping($"MidiNote{i}", $"Button {i}");
                    ps.FlushMidiMappings();

                    ps.UpdateChecksum();
                    return ps;
                }

                if (outputType == Engine.VirtualControllerType.Nintendo)
                {
                    // Nintendo (switch-pro) positional automap: the physical
                    // PLACEMENT of the source pad's controls lands on the
                    // same placement on the virtual Switch Pro (owner
                    // direction 2026-07-19), matching SDL's own positional
                    // gamepad semantics. SDL button indices are positional
                    // (0=south 1=east 2=west 3=north), and switch-pro raw
                    // indices letter B0 A1 Y2 X3 (see NintendoExtendedLabel),
                    // so index N maps straight to RawBtnN and the
                    // letters land Xbox A on Switch B, Xbox X on Switch Y.
                    // Sticks: the profile has no analog triggers, so
                    // ComputeAxisLayout packs LX LY RX RY at 0-3. Physical
                    // trigger PULLS press the digital ZL/ZR buttons through
                    // the standard axis-as-button coercion.
                    if (HasAxis(0)) ps.SetRawMapping("RawAxis0", "Axis 0");
                    if (HasAxis(1)) ps.SetRawMapping("RawAxis1", "Axis 1");
                    if (HasAxis(3)) ps.SetRawMapping("RawAxis2", "Axis 3");
                    if (HasAxis(4)) ps.SetRawMapping("RawAxis3", "Axis 4");

                    if (HasButton(0)) ps.SetRawMapping("RawBtn0", "Button 0");   // south → B
                    if (HasButton(1)) ps.SetRawMapping("RawBtn1", "Button 1");   // east  → A
                    if (HasButton(2)) ps.SetRawMapping("RawBtn2", "Button 2");   // west  → Y
                    if (HasButton(3)) ps.SetRawMapping("RawBtn3", "Button 3");   // north → X
                    if (HasButton(4)) ps.SetRawMapping("RawBtn4", "Button 4");   // LB → L
                    if (HasButton(5)) ps.SetRawMapping("RawBtn5", "Button 5");   // RB → R
                    if (HasAxis(2)) ps.SetRawMapping("RawBtn6", "Axis 2");       // LT pull → ZL
                    if (HasAxis(5)) ps.SetRawMapping("RawBtn7", "Axis 5");       // RT pull → ZR
                    if (HasButton(6)) ps.SetRawMapping("RawBtn8", "Button 6");   // Back → Minus
                    if (HasButton(7)) ps.SetRawMapping("RawBtn9", "Button 7");   // Start → Plus
                    if (HasButton(8)) ps.SetRawMapping("RawBtn10", "Button 8");  // LS click
                    if (HasButton(9)) ps.SetRawMapping("RawBtn11", "Button 9");  // RS click
                    if (HasButton(10)) ps.SetRawMapping("RawBtn12", "Button 10"); // Guide → Home
                    // Misc1 (Xbox Share / DualSense Mic / Switch Capture,
                    // SDL index 11) → Capture, gated on the device
                    // actually exposing it like the Xbox Share automap.
                    bool hasCaptureSource = ud.DeviceObjects != null
                        && ud.DeviceObjects.Any(o => o != null
                            && (o.ObjectType & DeviceObjectTypeFlags.PushButton) != 0
                            && o.InputIndex == 11);
                    if (hasCaptureSource) ps.SetRawMapping("RawBtn13", "Button 11");

                    if (HasHat())
                    {
                        ps.SetRawMapping("RawPov0Up", "POV 0 Up");
                        ps.SetRawMapping("RawPov0Down", "POV 0 Down");
                        ps.SetRawMapping("RawPov0Left", "POV 0 Left");
                        ps.SetRawMapping("RawPov0Right", "POV 0 Right");
                    }
                    ps.FlushRawMappings();
                }
                else
                {
                // Sticks and triggers (SDL3 axis order LX/LY/LT/RX/RY/RT).
                if (HasAxis(0)) ps.LeftThumbAxisX = "Axis 0";
                if (HasAxis(1)) ps.LeftThumbAxisY = "Axis 1";
                if (HasAxis(2)) ps.LeftTrigger = "Axis 2";
                if (HasAxis(3)) ps.RightThumbAxisX = "Axis 3";
                if (HasAxis(4)) ps.RightThumbAxisY = "Axis 4";
                if (HasAxis(5)) ps.RightTrigger = "Axis 5";

                // D-pad from hat switch (individual directions for UI display and remapping).
                if (HasHat())
                {
                    ps.DPadUp = "POV 0 Up";
                    ps.DPadDown = "POV 0 Down";
                    ps.DPadLeft = "POV 0 Left";
                    ps.DPadRight = "POV 0 Right";
                }

                // SDL3 XInput backend button indices.
                if (HasButton(0)) ps.ButtonA = "Button 0";
                if (HasButton(1)) ps.ButtonB = "Button 1";
                if (HasButton(2)) ps.ButtonX = "Button 2";
                if (HasButton(3)) ps.ButtonY = "Button 3";
                if (HasButton(4)) ps.LeftShoulder = "Button 4";
                if (HasButton(5)) ps.RightShoulder = "Button 5";
                if (HasButton(6)) ps.ButtonBack = "Button 6";
                if (HasButton(7)) ps.ButtonStart = "Button 7";
                if (HasButton(8)) ps.LeftThumbButton = "Button 8";
                if (HasButton(9)) ps.RightThumbButton = "Button 9";
                if (HasButton(10)) ps.ButtonGuide = "Button 10";
                }

                // Xbox Share auto-map: any controller that exposes
                // SDL_GAMEPAD_BUTTON_MISC1 (Xbox Share, DualSense Mic,
                // Switch Capture, etc., all reported by SDL3 at button
                // index 11) maps to the Xbox VC's ButtonShare output.
                // Gated on the device actually having the button so
                // controllers without it (Xbox 360, classic gamepads)
                // don't carry a dead binding through the mapping table.
                bool hasMisc1 = ud.DeviceObjects != null
                    && ud.DeviceObjects.Any(o => o != null
                        && (o.ObjectType & DeviceObjectTypeFlags.PushButton) != 0
                        && o.InputIndex == 11);
                if (outputType == Engine.VirtualControllerType.Xbox && hasMisc1)
                    ps.ButtonShare = "Button 11";

                // Default deadzones and gains.
                ps.LeftThumbDeadZoneX = "0";
                ps.LeftThumbDeadZoneY = "0";
                ps.RightThumbDeadZoneX = "0";
                ps.RightThumbDeadZoneY = "0";
                ps.LeftThumbAntiDeadZone = "0";
                ps.RightThumbAntiDeadZone = "0";
                ps.LeftThumbLinear = "0";
                ps.RightThumbLinear = "0";
                ps.ForceOverall = "100";
                ps.LeftMotorStrength = "100";
                ps.RightMotorStrength = "100";
                ps.ForceSwapMotor = "0";

                // Touchpad auto-mapping for PlayStation output + touchpad-capable device.
                // This branch is the CapType == Gamepad path — every gamepad
                // that reports HasTouchpad here (DualSense, DS4, DualSense Edge,
                // web-gamepad-with-touchpad) exposes a touchpad click. PTP
                // system touchpads have CapType == Touchpad and never reach
                // this branch, so no per-device gate is needed.
                if (outputType == Engine.VirtualControllerType.PlayStation && ud.HasTouchpad)
                {
                    ps.TouchpadX1 = "Touchpad 0 Finger 0 X";
                    ps.TouchpadY1 = "Touchpad 0 Finger 0 Y";
                    ps.TouchpadContact1 = "Touchpad 0 Finger 0 Down";
                    ps.TouchpadX2 = "Touchpad 0 Finger 1 X";
                    ps.TouchpadY2 = "Touchpad 0 Finger 1 Y";
                    ps.TouchpadContact2 = "Touchpad 0 Finger 1 Down";
                    ps.TouchpadClick = "Touchpad 0 Click";
                }

                // Motion passthrough auto-mapping for motion-capable
                // output families (PlayStation; Nintendo since the virtual
                // Switch Pro's IMU surface, HM v1.3.18) + sensor-capable
                // device. The bundled-source descriptor
                // markers ("Motion Gyro" / "Motion Accel") flag this
                // device as contributing its sensor stream to the
                // slot's motion channel. EnsureMotionRows mirrors the
                // marker into the per-slot MappingSet so the engine
                // sees the row.
                if (outputType is Engine.VirtualControllerType.PlayStation
                    or Engine.VirtualControllerType.Nintendo)
                {
                    if (ud.HasGyro)  ps.MotionGyro  = "Motion Gyro";
                    if (ud.HasAccel) ps.MotionAccel = "Motion Accel";
                }

                ps.UpdateChecksum();
                return ps;
            }

            // Touchpad-type devices (web touchpad, overlay, PTP) auto-map
            // touchpad data to PlayStation. TouchpadClick is dropped only
            // for PTP system touchpads, which are uniquely identified by
            // having no ISdlInputDevice wrapper attached (they're read by
            // PrecisionTouchpadReader, not SDL). Web touchpad clients and
            // TouchpadOverlayDevice both attach a wrapper and expose a
            // virtual click button.
            if (ud.CapType == InputDeviceType.Touchpad && ud.HasTouchpad &&
                outputType == Engine.VirtualControllerType.PlayStation)
            {
                ps.TouchpadX1 = "Touchpad 0 Finger 0 X";
                ps.TouchpadY1 = "Touchpad 0 Finger 0 Y";
                ps.TouchpadContact1 = "Touchpad 0 Finger 0 Down";
                ps.TouchpadX2 = "Touchpad 0 Finger 1 X";
                ps.TouchpadY2 = "Touchpad 0 Finger 1 Y";
                ps.TouchpadContact2 = "Touchpad 0 Finger 1 Down";
                if (ud.Device != null)
                    ps.TouchpadClick = "Touchpad 0 Click";

                ps.UpdateChecksum();
                return ps;
            }

            // Non-gamepad, non-touchpad devices are not auto-mapped.
            // The user must manually record mappings for these devices.

            ps.UpdateChecksum();
            return ps;
        }

        /// <summary>
        /// Re-automaps all devices assigned to a slot for the given output type.
        /// Called when switching virtual controller type so mappings match the new type.
        /// </summary>
        public static void ReAutoMapSlot(int padIndex, Engine.VirtualControllerType outputType)
        {
            var settings = UserSettings;
            if (settings == null) return;

            // Snapshot the slot's UserSettings under the lock, then resolve
            // devices and write PadSettings OUTSIDE it. FindDeviceByInstanceGuid
            // takes the UserDevices lock; acquiring it while holding UserSettings
            // inverts the canonical UserDevices -> UserSettings order and pairs
            // into an ABBA deadlock with the disconnect/migration paths that
            // nest Devices-first. SetPadSetting/PadSettingChecksum are per-object
            // writes (the lock guards the Items collection, not the entries), the
            // same pattern the device-assign flow already uses.
            var slotSettings = new System.Collections.Generic.List<UserSetting>();
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us.MapTo == padIndex) slotSettings.Add(us);
                }
            }

            foreach (var us in slotSettings)
            {
                var ud = FindDeviceByInstanceGuid(us.InstanceGuid);
                var ps = CreateDefaultPadSetting(ud, outputType);
                us.SetPadSetting(ps);
                us.PadSettingChecksum = ps.PadSettingChecksum;
                // Permanent automap-decision diagnostics (2026-07-22): a
                // type switch that authors an EMPTY PadSetting is silent
                // and latent until the user notices dead inputs. Name the
                // gate that decided, every time.
                // All three dictionary siblings, not raw alone: a MIDI or
                // KBM slot's automap logged rawRows=0 with rows authored,
                // defeating this line's own purpose (audit 2026-07-24,
                // lens 1r).
                int rawCount = (ps.RawMappingEntries?.Length ?? 0)
                    + (ps.MidiMappingEntries?.Length ?? 0)
                    + (ps.KbmMappingEntries?.Length ?? 0);
                Engine.SdlDiagLog.WriteLine(
                    $"AUTOMAP slot={padIndex} type={outputType} guid={us.InstanceGuid.ToString().Substring(0, 8)}"
                    + (ud == null
                        ? " device=NOT-FOUND -> empty defaults"
                        : $" cap={ud.CapType} forceRaw={ud.ForceRawJoystickMode} objs={ud.DeviceObjects?.Length ?? 0} rawRows={rawCount}"));
            }
        }

        // ─────────────────────────────────────────────
        //  Diagnostics
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns a summary string for diagnostics.
        /// </summary>
        public static string GetSummary()
        {
            int deviceCount = 0, onlineCount = 0, settingCount = 0;

            var devices = UserDevices;
            if (devices != null)
            {
                lock (devices.SyncRoot)
                {
                    deviceCount = devices.Items.Count;
                    onlineCount = devices.Items.Count(d => d.IsOnline);
                }
            }

            var settings = UserSettings;
            if (settings != null)
            {
                lock (settings.SyncRoot)
                {
                    settingCount = settings.Items.Count;
                }
            }

            return $"Devices: {onlineCount}/{deviceCount} online, Settings: {settingCount}";
        }
    }
}
