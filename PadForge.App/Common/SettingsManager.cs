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

        /// <summary>Whether the external-control named pipe (#366) is served,
        /// so a launcher or script can activate and deactivate profiles from
        /// outside PadForge. Off by default; opt-in on the Profiles page.</summary>
        public static bool EnableExternalControl { get; set; }

        /// <summary>True while a profile was activated over the external
        /// control channel (#366). The foreground monitor early-returns while
        /// this holds, so focusing a game cannot clobber a scripted choice.
        /// Cleared by an external deactivate, by any manual switch (through
        /// NoteManualProfileSwitch), and by app restart. Never persisted.</summary>
        public static bool ExternalProfilePinActive { get; set; }

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

        /// <summary>Maximum number of VR virtual controllers. One: a single
        /// slot already drives both SteamVR hands, and SteamVR tracks one
        /// left+right pair, so a second slot would fight the first over the
        /// same two devices.</summary>
        public const int MaxVrSlots = 1;

        /// <summary>True when another slot may take
        /// <paramref name="type"/>. Only VR is capped below the global slot
        /// count today, and the cap must be enforced HERE rather than at
        /// each UI entry point: the add-popup checked it, the sidebar
        /// segment, the dashboard tile, and the shared type-change handler
        /// did not, so a type SWITCH could mint a second VR slot. Two VR
        /// slots do not fail loudly. HIDMaestro's shared-memory owner check
        /// accepts a second consumer from the SAME process, so both submit
        /// into one channel (latest writer wins), both read the haptic
        /// stream, and disposing either clears the shared owner.
        /// <paramref name="excludingSlot"/> is the slot being converted, so
        /// a slot that is ALREADY this type never blocks itself.</summary>
        public static bool CanSlotTakeType(Engine.VirtualControllerType type,
            System.Func<int, Engine.VirtualControllerType> slotType, int excludingSlot = -1)
        {
            if (type != Engine.VirtualControllerType.Vr) return true;
            if (slotType == null) return true;
            int count = 0;
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (i == excludingSlot) continue;
                if (SlotCreated[i] && slotType(i) == type) count++;
            }
            return count < MaxVrSlots;
        }

        /// <summary>
        /// True when at least one created slot is of <paramref name="type"/>.
        ///
        /// <para>The uninstall gates read this. A driver that a virtual
        /// controller depends on must not be removable while that controller
        /// exists: Windows MIDI Services under a MIDI slot, SteamVR under a VR
        /// slot. Reads the persisted slot topology rather than engine state, so
        /// the gate holds with the engine stopped, which is exactly when
        /// someone is most likely to be tidying up drivers.</para>
        /// </summary>
        public static bool HasCreatedSlotOfType(Engine.VirtualControllerType type)
        {
            try
            {
                var created = SlotCreated;
                if (created == null) return false;
                lock (SlotOrders.OrderSync)
                {
                    foreach (int padIndex in SlotOrders.GetOrderFor(type))
                        if (padIndex >= 0 && padIndex < created.Length && created[padIndex])
                            return true;
                }
            }
            catch { }
            return false;
        }

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
        public static List<int> VrSlotOrder { get; set; } = new();

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
            internal static readonly object OrderSync = new object();

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
                Engine.VirtualControllerType.Vr            => VrSlotOrder,
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
                int[] persistedNintendo = null,
                int[] persistedVr = null)
            {
                Reconcile(XboxSlotOrder,          persistedXbox,        slotType, Engine.VirtualControllerType.Xbox);
                Reconcile(PlayStationSlotOrder,   persistedPlayStation, slotType, Engine.VirtualControllerType.PlayStation);
                Reconcile(NintendoSlotOrder,      persistedNintendo,    slotType, Engine.VirtualControllerType.Nintendo);
                Reconcile(ExtendedSlotOrder,      persistedExtended,    slotType, Engine.VirtualControllerType.Extended);
                Reconcile(KeyboardMouseSlotOrder, persistedKbm,         slotType, Engine.VirtualControllerType.KeyboardMouse);
                Reconcile(MidiSlotOrder,          persistedMidi,        slotType, Engine.VirtualControllerType.Midi);
                Reconcile(VrSlotOrder,            persistedVr,          slotType, Engine.VirtualControllerType.Vr);
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
                    // Remove EVERY record with this GUID, not just the first.
                    // FindDeviceByInstanceGuid exists precisely because a
                    // mid-session duplicate can appear, and it scans all
                    // matches to prefer the capability-rich one. Removing one
                    // of a duplicate pair left the other behind, so the
                    // finder kept resolving a device the caller just deleted.
                    for (int i = devices.Items.Count - 1; i >= 0; i--)
                    {
                        if (devices.Items[i]?.InstanceGuid != instanceGuid) continue;
                        devices.Items.RemoveAt(i);
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

            // A haptic peripheral removed mid-rumble (#494) leaves no row for
            // Step 2 to stop, and its backend would play the stored level on
            // as soon as the row is found again. Its lighting goes back to the
            // vendor's software the same way.
            PadForge.Common.Input.Peripherals.PeripheralOutputs.StopHaptics(instanceGuid);
            PadForge.Common.Input.Peripherals.PeripheralOutputs.ReleaseDevice(instanceGuid);

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
                // Already assigned to this exact slot: return the existing row.
                var exactMatch = settings.Items.FirstOrDefault(
                    s => s.InstanceGuid == instanceGuid && s.MapTo == padIndex);

                if (exactMatch != null)
                    return exactMatch;

                // A PARKED row for this device is the same device with the
                // user's authoring on it, waiting to come back. ApplyProfile
                // already adopts one by matching the guid with no MapTo
                // filter, so this path must too. Creating a fresh row instead
                // left the device with two persisted rows and handed
                // DeviceService a null PadSetting, which then overwrote the
                // slot with a default automap. That was survivable while the
                // load purged every parked row. a5de3709 keeps the ones
                // carrying settings, so the duplicate became permanent.
                var parked = settings.Items.FirstOrDefault(
                    s => s.InstanceGuid == instanceGuid && s.MapTo < 0);

                if (parked != null)
                {
                    parked.MapTo = padIndex;
                    return parked;
                }

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

                // Adopt a parked row rather than duplicating the device, for
                // the reason spelled out in AssignDeviceToSlot above.
                var parked = settings.Items.FirstOrDefault(
                    s => s.InstanceGuid == instanceGuid && s.MapTo < 0);

                if (parked != null)
                {
                    parked.MapTo = padIndex;
                    return (true, parked);
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
        /// <param name="profileId">HIDMaestro profile the SLOT will use.
        /// Nintendo automaps are wire-relative and the two Switch families
        /// share almost no indices, so this decides which wire the defaults
        /// bind. Null falls back to the original Pro Controller's.</param>
        /// <param name="extended">The slot's own Extended layout, when the
        /// caller has one. An Extended slot reads the RAW surface unconditionally
        /// and its axis order interleaves sticks with triggers, so the target
        /// index for a stick or a trigger depends on how many of each the slot
        /// declares. Without it the generator fell through to the standard
        /// gamepad fields, which that surface never reads, and the slot came up
        /// with no automap at all.</param>
        public static PadSetting CreateDefaultPadSetting(UserDevice ud,
            Engine.VirtualControllerType outputType = Engine.VirtualControllerType.Xbox,
            string profileId = null,
            ViewModels.ExtendedSlotConfig extended = null)
            => CreateDefaultPadSetting(ud, outputType, profileId, extended,
                ud == null ? null
                : ud.CapType == InputDeviceType.TwitchChat ? Common.Input.Twitch.ChatGamepadPlacement.For()
                : Common.Input.BlissBoxRuntime.GamepadMapFor(ud));

        /// <param name="placement">Where the device puts its inputs in SDL's
        /// gamepad layout: the controller in a Bliss-Box port read raw
        /// (<see cref="Common.Input.BlissBoxRuntime.GamepadMapFor"/>) or the
        /// Twitch Chat row's usual words
        /// (<see cref="Common.Input.Twitch.ChatGamepadPlacement.For"/>, #555).
        /// Null for any other device.</param>
        // The D-pad's directions in GamepadPlacement.DPadButton's order.
        private static readonly string[] DPadDirections = { "Up", "Down", "Left", "Right" };

        internal static PadSetting CreateDefaultPadSetting(UserDevice ud,
            Engine.VirtualControllerType outputType, string profileId,
            ViewModels.ExtendedSlotConfig extended, Engine.Common.GamepadPlacement placement)
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
            // A Bliss-Box port read raw is a joystick, but the controller in
            // it has a place in that layout, so it maps the same way through
            // its placement, as SDL would map the console's pad.
            // Skip auto-mapping when Force Raw Joystick Mode is enabled. The
            // user wants to record raw mappings manually.
            //
            // A Bliss-Box port this PC reads raw maps only through the
            // controller's placement. A cached port's CapType records the
            // switch as its last session left it, so with the reader on, a
            // port cached as SDL's gamepad built SDL's positions, which the
            // raw read gives other meanings: LB read Select and Back read L1.
            // With no placement it gets an empty default, and the port owes
            // the slot its default (BlissBoxRuntime.DefaultOwed).
            bool sdlLayout = ud.CapType == InputDeviceType.Gamepad
                && !(placement == null && Common.Input.BlissBoxRuntime.ReadsRawHere(ud));
            if ((sdlLayout || placement != null) && !ud.ForceRawJoystickMode)
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

                // The source for each standardized gamepad position: the
                // position itself on an SDL gamepad, the input the placement
                // puts there on a Bliss-Box port. Btn and Ax cover the layout
                // HasButton and HasAxis gate. Extra covers the positions only
                // some pads report past it (Misc 1 at 11, the paddles at 12 to
                // 15, the touchpad click at 16, Misc 2 at 17), which bind only
                // when the device lists them.
                string Btn(int i) => placement != null ? OnDevice(placement.Button(i))
                    : HasButton(i) ? $"Button {i}" : null;
                string Ax(int i) => placement != null ? OnDevice(placement.Axis(i))
                    : HasAxis(i) ? $"Axis {i}" : null;
                string Extra(int i) => placement != null ? OnDevice(placement.Button(i))
                    : objs != null && objs.Any(o => o != null
                        && (o.ObjectType & DeviceObjectTypeFlags.PushButton) != 0
                        && o.InputIndex == i) ? $"Button {i}" : null;
                bool DPad() => (placement == null || placement.DPad) && HasHat();

                // One D-pad direction's source: the hat's, or the button a
                // placement puts there when the device's D-pad is four
                // buttons (the Twitch Chat row's !up and !down, #555).
                string DPadSource(string direction)
                {
                    if (DPad()) return "POV 0 " + direction;
                    int d = Array.IndexOf(DPadDirections, direction);
                    return placement != null && d >= 0 ? OnDevice(placement.DPadButton(d)) : null;
                }

                // A port's input binds only while the port's own object list
                // carries it, the rule HasButton and HasAxis keep above.
                string OnDevice(string source)
                {
                    if (source == null) return null;
                    bool axis = source.StartsWith("Axis ", StringComparison.Ordinal);
                    int index = int.Parse(source.AsSpan(axis ? 5 : 7), System.Globalization.CultureInfo.InvariantCulture);
                    return (axis ? HasAxis(index) : HasButton(index)) ? source : null;
                }

                if (outputType == Engine.VirtualControllerType.Midi)
                {
                    // MIDI auto-mapping: CC0-CC5 for axes, Note0-Note10 for buttons.
                    for (int i = 0; i < 6; i++)
                        if (Ax(i) is { } cc) ps.SetMidiMapping($"MidiCC{i}", cc);
                    for (int i = 0; i < 11; i++)
                        if (Btn(i) is { } note) ps.SetMidiMapping($"MidiNote{i}", note);
                    ps.FlushMidiMappings();

                    ps.UpdateChecksum();
                    return ps;
                }

                if (outputType == Engine.VirtualControllerType.Vr)
                {
                    // VR auto-mapping (issue #49): one gamepad drives both VR
                    // hands. Sticks and triggers land on the same-side hand;
                    // the right hand carries the A/B pair, the left hand gets
                    // its pair from X/Y (the touch-controller convention).
                    // Bumpers press grip click and drive the grip value,
                    // Back/Start press the System buttons. Trigger CLICK
                    // rides the same physical axis as the trigger pull via
                    // the standard axis-as-button coercion.
                    if (Ax(0) is { } lx) ps.SetVrMapping(Engine.VrLayout.LStickX, lx);
                    if (Ax(1) is { } ly) ps.SetVrMapping(Engine.VrLayout.LStickY, ly);
                    if (Ax(3) is { } rx) ps.SetVrMapping(Engine.VrLayout.RStickX, rx);
                    if (Ax(4) is { } ry) ps.SetVrMapping(Engine.VrLayout.RStickY, ry);
                    if (Ax(2) is { } lt)
                    {
                        ps.SetVrMapping(Engine.VrLayout.LTrigger, lt);
                        ps.SetVrMapping("VrLTriggerClick", lt);
                    }
                    if (Ax(5) is { } rt)
                    {
                        ps.SetVrMapping(Engine.VrLayout.RTrigger, rt);
                        ps.SetVrMapping("VrRTriggerClick", rt);
                    }
                    if (Btn(0) is { } south) ps.SetVrMapping("VrRA", south);
                    if (Btn(1) is { } east) ps.SetVrMapping("VrRB", east);
                    if (Btn(2) is { } west) ps.SetVrMapping("VrLA", west);
                    if (Btn(3) is { } north) ps.SetVrMapping("VrLB", north);
                    if (Btn(4) is { } lb)
                    {
                        ps.SetVrMapping("VrLGripClick", lb);
                        ps.SetVrMapping(Engine.VrLayout.LGrip, lb);
                    }
                    if (Btn(5) is { } rb)
                    {
                        ps.SetVrMapping("VrRGripClick", rb);
                        ps.SetVrMapping(Engine.VrLayout.RGrip, rb);
                    }
                    if (Btn(6) is { } back) ps.SetVrMapping("VrLSystem", back);
                    if (Btn(7) is { } start) ps.SetVrMapping("VrRSystem", start);
                    if (Btn(8) is { } ls) ps.SetVrMapping("VrLStickClick", ls);
                    if (Btn(9) is { } rs) ps.SetVrMapping("VrRStickClick", rs);
                    ps.FlushVrMappings();

                    ps.UpdateChecksum();
                    return ps;
                }

                // An unlettered Extended profile has no role table, so its grid
                // rows are numbered and the honest default is positional: the
                // source's Nth stick drives the slot's Nth stick and the same for
                // triggers and buttons. Both sides interleave, so the indices are
                // computed from the slot's declared counts rather than assumed.
                if (outputType == Engine.VirtualControllerType.Extended
                    && extended != null
                    && !Models2D.NintendoPreviewMap.IsLettered(profileId))
                {
                    extended.ComputeAxisLayout(out int[] slotX, out int[] slotY, out int[] slotTrig);
                    // The source is the standard order this generator already
                    // uses below: stick g at 3g and 3g+1, trigger t at 3t+2 for
                    // the interleaved groups, which is exactly LX LY LT RX RY RT
                    // on an ordinary pad.
                    int srcInterleave = Math.Min(2, 2);
                    for (int g = 0; g < slotX.Length; g++)
                    {
                        int sx = g < srcInterleave ? g * 3 : srcInterleave * 3 + (g - srcInterleave) * 2;
                        if (Ax(sx) is { } x) ps.SetRawMapping($"RawAxis{slotX[g]}", x);
                        if (Ax(sx + 1) is { } y) ps.SetRawMapping($"RawAxis{slotY[g]}", y);
                    }
                    for (int t = 0; t < slotTrig.Length; t++)
                    {
                        int st = t < srcInterleave ? t * 3 + 2 : -1;
                        if (st >= 0 && Ax(st) is { } trigger) ps.SetRawMapping($"RawAxis{slotTrig[t]}", trigger);
                    }
                    for (int b = 0; b < extended.ButtonCount; b++)
                        if (Btn(b) is { } button) ps.SetRawMapping($"RawBtn{b}", button);
                    if (extended.PovCount > 0)
                        foreach (string dir in DPadDirections)
                            if (DPadSource(dir) is { } pov) ps.SetRawMapping($"RawPov0{dir}", pov);
                    ps.FlushRawMappings();
                    ps.UpdateChecksum();
                    return ps;
                }

                // A lettered wire takes a pad by role, south button to south
                // button, from one table (NintendoPreviewMap.GamepadPositions),
                // the table Paste and Copy From read to carry a slot onto the
                // same wire. Each position is read where SDL's order puts it:
                // the eleven standard buttons and the six axes through the
                // gates above, the extras past button 10 only where the pad
                // lists them, and the D-pad from the hat. A role the wire lacks
                // binds nothing, and the wire decides whether its D-pad is a
                // hat or four buttons.
                void MapGamepadPositions(bool valve)
                {
                    foreach (var position in Models2D.NintendoPreviewMap.GamepadPositions)
                    {
                        string source = position.Input switch
                        {
                            Models2D.NintendoPreviewMap.GamepadInput.Axis => Ax(position.Index),
                            Models2D.NintendoPreviewMap.GamepadInput.Hat =>
                                DPadSource(position.Target.Substring(4)),
                            _ => position.Index <= 10 ? Btn(position.Index) : Extra(position.Index),
                        };
                        if (source == null) continue;
                        foreach (string role in position.Roles(valve))
                            if (Models2D.NintendoPreviewMap.ToRaw(role, profileId) is { } raw)
                                ps.SetRawMapping(raw, source);
                    }
                }

                if (outputType == Engine.VirtualControllerType.Extended
                    && Models2D.NintendoPreviewMap.IsValve(profileId))
                {
                    // Valve automap. The three Valve wires (Deck, 2015, 2026)
                    // share the table's Valve roles: Quick Access at misc 1,
                    // the rear buttons in SDL's RP1 LP1 RP2 LP2 order at 12 to
                    // 15 (R4 L4 R5 L5 on the Deck and 2026 wires, the two grips
                    // on the 2015 wire: SDL_gamepad.c 1458 / 1461 / 1464).
                    void MapValve(string role, string source)
                    {
                        if (source == null) return;
                        int i = Models2D.NintendoPreviewMap.IndexOf(profileId, role);
                        if (i >= 0) ps.SetRawMapping($"RawBtn{i}", source);
                    }
                    MapGamepadPositions(valve: true);

                    // Trackpads. All three Valve drivers register the left
                    // pad as touchpad 0 and the right as touchpad 1, one
                    // finger each (SDL_hidapi_steam.c 1654 / 1674,
                    // _steamdeck.c 268 / 280, _steam_triton.c 254 / 266),
                    // and the slot's touch surface splits the same way:
                    // finger 0 is the left pad, finger 1 the right. A one-
                    // pad source (a DualSense) lands on the left pad only.
                    // The clicks are the gamepad buttons the pad advertises,
                    // the picker's own convention (MappingDisplayResolver:
                    // one "Touchpad 0 Click", and a second pad's click
                    // surfaces as MISC2): SDL's Deck and 2026 mappings bind
                    // touchpad:b17 = left click at position 16 and
                    // misc2:b16 = right click at 17 (SDL_gamepad.c 1461 /
                    // 1464 against _steamdeck.c 42-43, _steam_triton.c
                    // 53-54). A pad that advertises neither (the 2015
                    // controller, mapping paddle1/paddle2 only) clicks
                    // through the wrapper's pressure descriptors instead.
                    int pads = ud.HasTouchpad ? Math.Max(1, ud.CapTouchpadCount) : 0;
                    if (pads >= 1)
                    {
                        ps.TouchpadX1 = "Touchpad 0 Finger 0 X";
                        ps.TouchpadY1 = "Touchpad 0 Finger 0 Y";
                        ps.TouchpadContact1 = "Touchpad 0 Finger 0 Down";
                        MapValve("LeftTouchpadClick", Extra(16) ?? "Touchpad 0 Click");
                    }
                    if (pads >= 2)
                    {
                        ps.TouchpadX2 = "Touchpad 1 Finger 0 X";
                        ps.TouchpadY2 = "Touchpad 1 Finger 0 Y";
                        ps.TouchpadContact2 = "Touchpad 1 Finger 0 Down";
                        MapValve("RightTouchpadClick", Extra(17) ?? "Touchpad 1 Click");
                    }

                    // Capacitive sensors: the Deck's and the 2026 pad's stick
                    // tops and the 2026 pad's grips land on the persona's own
                    // touch rows, where the source pad has the sensor and the
                    // wire carries it. The source is the pad's own picker
                    // entry (MappingDisplayResolver lists a channel under the
                    // pad when CapSenseChannelMask has it).
                    foreach (var (capDesc, channel) in PadForge.Engine.Common.Mapping.SourceCoercion.CapSenseTable)
                    {
                        if ((ud.CapSenseChannelMask & (1 << channel)) == 0) continue;
                        MapValve(capDesc.Substring("Gamepad ".Length), capDesc);
                    }

                    // IMU: every Valve frame carries gyro and accel, which
                    // the packer fills from the slot's motion channel.
                    if (ud.HasGyro)  ps.MotionGyro  = "Motion Gyro";
                    if (ud.HasAccel) ps.MotionAccel = "Motion Accel";
                    ps.FlushRawMappings();
                }
                else if (outputType == Engine.VirtualControllerType.Nintendo)
                {
                    // Nintendo automap: the PLACEMENT of the source pad's
                    // controls lands on the same placement on the virtual
                    // Switch pad (owner direction 2026-07-19), SDL's own
                    // positional semantics, so Xbox A lands on Switch B and
                    // Xbox X on Switch Y. The sticks take raw axes 0 to 3,
                    // and the trigger pulls press the digital ZL and ZR
                    // through the axis-as-button read. The table's roles are
                    // the Switch 2 Pro's too: a hardcoded Pro Controller
                    // index list once sent Back to that pad's D-pad Down.
                    MapGamepadPositions(valve: false);
                    ps.FlushRawMappings();
                }
                else
                {
                // Sticks and triggers (SDL3 axis order LX/LY/LT/RX/RY/RT).
                if (Ax(0) is { } lx) ps.LeftThumbAxisX = lx;
                if (Ax(1) is { } ly) ps.LeftThumbAxisY = ly;
                if (Ax(2) is { } lt) ps.LeftTrigger = lt;
                if (Ax(3) is { } rx) ps.RightThumbAxisX = rx;
                if (Ax(4) is { } ry) ps.RightThumbAxisY = ry;
                if (Ax(5) is { } rt) ps.RightTrigger = rt;

                // D-pad from hat switch (individual directions for UI display
                // and remapping), or from a placement's four D-pad buttons.
                if (DPadSource("Up") is { } dpadUp) ps.DPadUp = dpadUp;
                if (DPadSource("Down") is { } dpadDown) ps.DPadDown = dpadDown;
                if (DPadSource("Left") is { } dpadLeft) ps.DPadLeft = dpadLeft;
                if (DPadSource("Right") is { } dpadRight) ps.DPadRight = dpadRight;

                // SDL3 XInput backend button indices.
                if (Btn(0) is { } south) ps.ButtonA = south;
                if (Btn(1) is { } east) ps.ButtonB = east;
                if (Btn(2) is { } west) ps.ButtonX = west;
                if (Btn(3) is { } north) ps.ButtonY = north;
                if (Btn(4) is { } lb) ps.LeftShoulder = lb;
                if (Btn(5) is { } rb) ps.RightShoulder = rb;
                if (Btn(6) is { } back) ps.ButtonBack = back;
                if (Btn(7) is { } start) ps.ButtonStart = start;
                if (Btn(8) is { } ls) ps.LeftThumbButton = ls;
                if (Btn(9) is { } rs) ps.RightThumbButton = rs;
                if (Btn(10) is { } guide) ps.ButtonGuide = guide;
                }

                // Xbox Share auto-map: any controller that exposes
                // SDL_GAMEPAD_BUTTON_MISC1 (Xbox Share, DualSense Mic,
                // Switch Capture, etc., all reported by SDL3 at button
                // index 11) maps to the Xbox VC's ButtonShare output.
                // Gated on the device actually having the button so
                // controllers without it (Xbox 360, classic gamepads)
                // don't carry a dead binding through the mapping table.
                string misc1 = Extra(11);
                if (outputType == Engine.VirtualControllerType.Xbox && misc1 != null)
                    ps.ButtonShare = misc1;

                // PlayStation mirror of the same idea: the physical mic
                // button (SDL misc1, source position 11) lands on the
                // virtual DualSense's mic mute, and an Edge source's
                // paddles / Fn land on the virtual Edge's same-role
                // outputs (positions 12-15 per SDL's paddle order:
                // RP1, LP1, RP2=right Fn, LP2=left Fn).
                if (outputType == Engine.VirtualControllerType.PlayStation
                    && !string.IsNullOrEmpty(profileId)
                    && profileId.StartsWith("dualsense", StringComparison.OrdinalIgnoreCase))
                {
                    if (misc1 != null) ps.ButtonMute = misc1;
                    if (profileId.StartsWith("dualsense-edge", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Extra(12) is { } rp) ps.RightPaddle = rp;
                        if (Extra(13) is { } lp) ps.LeftPaddle = lp;
                        if (Extra(14) is { } rf) ps.RightFunction = rf;
                        if (Extra(15) is { } lf) ps.LeftFunction = lf;
                    }
                }

                // Button pressure (discussion #476): a DualShock 3 that
                // reports it in SDL's order, an original Xbox controller
                // (#483), or a DualShock 2 in a Bliss-Box port, on the preset
                // whose report carries it.
                if (outputType == Engine.VirtualControllerType.PlayStation
                    && HMaestroProfileCatalog.ReportCarriesPressure(profileId)
                    && (placement != null ? placement.PressureAxes : ButtonPressureSources.AxesFor(ud)) is { } pressureAxes)
                {
                    // A target the pad has no pressure for (-1, an original
                    // Xbox controller's D-pad), or a port's pressure axis its
                    // object list lacks, keeps the field's empty default.
                    string Axis(int i, string unset)
                    {
                        if (pressureAxes[i] < 0) return unset;
                        string source = "Axis " + pressureAxes[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                        return (placement != null ? OnDevice(source) : source) ?? unset;
                    }
                    ps.PressureButtonA = Axis(0, ps.PressureButtonA);
                    ps.PressureButtonB = Axis(1, ps.PressureButtonB);
                    ps.PressureButtonX = Axis(2, ps.PressureButtonX);
                    ps.PressureButtonY = Axis(3, ps.PressureButtonY);
                    ps.PressureLeftShoulder = Axis(4, ps.PressureLeftShoulder);
                    ps.PressureRightShoulder = Axis(5, ps.PressureRightShoulder);
                    ps.PressureDPadUp = Axis(6, ps.PressureDPadUp);
                    ps.PressureDPadDown = Axis(7, ps.PressureDPadDown);
                    ps.PressureDPadLeft = Axis(8, ps.PressureDPadLeft);
                    ps.PressureDPadRight = Axis(9, ps.PressureDPadRight);
                }

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
                ps.TriggerRumbleFold = "0";
                ps.AtVibrationToImpulseEnabled = "0";

                // Touchpad auto-mapping for PlayStation output + touchpad-capable device.
                // This branch is the CapType == Gamepad path — every gamepad
                // that reports HasTouchpad here (DualSense, DS4, DualSense Edge,
                // web-gamepad-with-touchpad) exposes a touchpad click. PTP
                // system touchpads have CapType == Touchpad and never reach
                // this branch, so no per-device gate is needed. A DualShock 3
                // preset has no touchpad and no rows to show these on.
                if (outputType == Engine.VirtualControllerType.PlayStation && ud.HasTouchpad
                    && HMaestroProfileCatalog.ReportCarriesTouchpad(profileId))
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
                // DSU reconciles the assigned sensors when these native
                // motion mappings are absent, including on Xbox outputs.
                if (outputType is Engine.VirtualControllerType.PlayStation
                    or Engine.VirtualControllerType.Nintendo)
                {
                    if (ud.HasGyro)  ps.MotionGyro  = "Motion Gyro";
                    if (ud.HasAccel) ps.MotionAccel = "Motion Accel";
                }

                ps.UpdateChecksum();
                return ps;
            }

            // A Novint Falcon (#521) takes the assignment @pobgithub's
            // RumbleRoute firmware gives it (RumbleRoute.c:45-54): the
            // handle's Left/Right and Up/Down on the right stick, and the
            // grip's buttons 1 to 4 on the right trigger, the left trigger,
            // the right bumper and the right stick's click. Up/Down reads up
            // low, as a gamepad's Y axis does (FalconDevice.GetCurrentState),
            // so the handle raised raises the stick. In/Out starts unmapped.
            // Xbox and PlayStation outputs, whose fields these are.
            if (ud.CapType == InputDeviceType.NovintFalcon && !ud.ForceRawJoystickMode
                && (outputType is Engine.VirtualControllerType.Xbox or Engine.VirtualControllerType.PlayStation))
            {
                ps.RightThumbAxisX = "Axis 0";
                ps.RightThumbAxisY = "Axis 1";
                ps.RightTrigger = "Button 0";
                ps.LeftTrigger = "Button 1";
                ps.RightShoulder = "Button 2";
                ps.RightThumbButton = "Button 3";
                ps.RightThumbDeadZoneX = "0";
                ps.RightThumbDeadZoneY = "0";
                ps.RightThumbAntiDeadZone = "0";
                ps.RightThumbLinear = "0";
                ps.ForceOverall = "100";
                ps.LeftMotorStrength = "100";
                ps.RightMotorStrength = "100";
                ps.ForceSwapMotor = "0";
                ps.TriggerRumbleFold = "0";
                ps.UpdateChecksum();
                return ps;
            }

            // Touchpad and tablet sources carry their contact surface to PlayStation.
            // Verified capabilities control finger count and click availability.
            // A DualShock 3 preset has no touchpad to carry it to.
            if ((ud.CapType == InputDeviceType.Touchpad || ud.IsTablet) && ud.HasTouchpad &&
                outputType == Engine.VirtualControllerType.PlayStation &&
                HMaestroProfileCatalog.ReportCarriesTouchpad(profileId))
            {
                ps.TouchpadX1 = "Touchpad 0 Finger 0 X";
                ps.TouchpadY1 = "Touchpad 0 Finger 0 Y";
                ps.TouchpadContact1 = "Touchpad 0 Finger 0 Down";
                int fingers = ud.CapTouchpadFingerCounts is { Length: > 0 } && ud.CapTouchpadFingerCounts[0] > 0
                    ? ud.CapTouchpadFingerCounts[0] : ud.IsTablet ? 1 : 2;
                if (fingers >= 2)
                {
                    ps.TouchpadX2 = "Touchpad 0 Finger 1 X";
                    ps.TouchpadY2 = "Touchpad 0 Finger 1 Y";
                    ps.TouchpadContact2 = "Touchpad 0 Finger 1 Down";
                }
                if (ud.SupportsTouchpadClick)
                    ps.TouchpadClick = "Touchpad 0 Click";

                ps.UpdateChecksum();
                return ps;
            }

            // Devices that are not gamepads, touchpads or Bliss-Box ports
            // with a placed controller are not auto-mapped. The user must
            // manually record mappings for these devices.

            ps.UpdateChecksum();
            return ps;
        }

        /// <summary>
        /// Move a Nintendo slot's raw mappings from one profile's wire to
        /// another's, preserving the ROLE each binding names.
        ///
        /// Raw targets are wire-relative and the two Switch families share
        /// almost no indices, so without this every existing binding silently
        /// changes meaning the moment the profile changes: a source bound to
        /// Minus (RawBtn8 on the original) would start pressing the Switch 2
        /// Pro's D-pad Down. Bindings whose role the target pad does not have
        /// are dropped rather than left pointing at wire that is not there.
        ///
        /// The "from" side is the WIRE STAMP, never a caller-supplied
        /// previous value. A slot's ProfileId property changes for two
        /// reasons that need opposite handling: the user re-targeting the
        /// SAME data to a new wire (translate), and the system re-describing
        /// the VM to match data that is ALREADY on the new wire (launch
        /// restore, profile apply, workshop import: do nothing). The setter
        /// cannot tell them apart, and keying on its previous value
        /// mistranslated consistent data on every one of those paths. The
        /// stamp tracks the data itself: restore/apply/import stamp the
        /// incoming wire BEFORE the VM assignment, so the setter's call
        /// finds from == to and no-ops; only a live user change leaves the
        /// stamp behind the new value, which is exactly the translate case.
        ///
        /// Everything the slot names by a raw position moves: each device's
        /// own entries (<see cref="MoveRawEntries"/>), the rows of every
        /// layer (<see cref="MoveRawRows"/>) and the buttons its SOCD pairs
        /// name (<see cref="MoveSocdPairs"/>). A copy of another slot's
        /// table onto this wire owes the same move
        /// (InputService.RawLanding.ByRole).
        /// </summary>
        public static void TranslateNintendoRawMappings(int padIndex, string toProfileId)
        {
            if (padIndex < 0 || padIndex >= _nintendoWireStamp.Length) return;
            string fromProfileId = _nintendoWireStamp[padIndex];

            // Unknown stamp: ADOPT, never guess. The data was persisted
            // together with the profile now being applied to the VM, so
            // they are already consistent; translating from a guessed
            // wire is the corruption this stamp exists to prevent.
            if (string.IsNullOrEmpty(fromProfileId))
            {
                _nintendoWireStamp[padIndex] = toProfileId;
                return;
            }
            if (string.Equals(fromProfileId, toProfileId, StringComparison.OrdinalIgnoreCase))
                return;
            _nintendoWireStamp[padIndex] = toProfileId;

            // Same wire FAMILY on both sides means every index keeps its
            // meaning and there is nothing to move.
            if (Models2D.NintendoPreviewMap.SameWireFamily(fromProfileId, toProfileId))
                return;

            Func<string, string> move = name =>
                Models2D.NintendoPreviewMap.TranslateRawTarget(name, fromProfileId, toProfileId);

            foreach (var us in GetSettingsForSlot(padIndex))
            {
                // The rows a Bliss-Box port's owed default leaves alone are
                // named on the same wire, so they move with it. A row the new
                // pad lacks is gone.
                if (us?.BlissBoxKeptTargets is { } keptTargets)
                    us.BlissBoxKeptTargets = keptTargets
                        .Select(move)
                        .Where(k => k != null)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

                var ps = us?.GetPadSetting();
                if (MoveRawEntries(ps, move)) us.PadSettingChecksum = ps.PadSettingChecksum;
            }

            // The grid does NOT read PadSetting raw entries. It reads the
            // slot's MappingSet rows, which are keyed by the same raw target
            // names, so translating only the PadSetting left every moved
            // binding without a row: the four roles that land past the
            // original's 14-button wire (Minus, LS, Home, Capture at 14-17)
            // rendered empty, and the rows still keyed to the old indices
            // kept their sources while now naming different buttons.
            var set = SlotMappingSets != null && padIndex < SlotMappingSets.Length
                ? SlotMappingSets[padIndex] : null;
            // The new list is built and its REFERENCE assigned. The poll
            // thread enumerates set.Rows concurrently, and an in-place
            // Clear + AddRange tears that enumeration. A reference swap
            // costs one stale tick, the same trade every other set
            // replacement in this codebase makes.
            if (set?.Rows != null) set.Rows = MoveRawRows(set.Rows, move);
            if (!string.IsNullOrEmpty(set?.SocdPairs)) set.SocdPairs = MoveSocdPairs(set.SocdPairs, move);
        }

        /// <summary>True for the name of a raw position: a raw button, a
        /// hat direction or a raw axis. A device's raw entries also hold
        /// tuning under names of their own ("GyroTiltRange"), which are no
        /// place on any wire.</summary>
        private static bool IsRawPosition(string name)
            => name.StartsWith("RawBtn", StringComparison.Ordinal)
               || name.StartsWith("RawPov", StringComparison.Ordinal)
               || name.StartsWith("RawAxis", StringComparison.Ordinal);

        /// <summary>
        /// Moves a device's own entries on one slot onto another wire: its
        /// raw mappings, and the deadzone and the Bidirectional flag an
        /// older save keeps under a mapping's raw target
        /// (PadSetting.GetMappingDeadZone). Each key that names a raw
        /// position goes through <paramref name="move"/>, which answers its
        /// name on the new wire, or null where that wire has no such
        /// control, and the entry goes. Where two keys land on one, the
        /// first keeps it, as with the rows (<see cref="MoveRawRows"/>).
        ///
        /// <para>Each family is built whole before any of it is written:
        /// a half-applied rewrite would be worse than either outcome. True
        /// when anything changed, with the arrays flushed and the checksum
        /// current.</para>
        /// </summary>
        internal static bool MoveRawEntries(PadSetting ps, Func<string, string> move)
        {
            if (ps == null || move == null) return false;
            // The arrays are what is read below, so they are brought up to
            // date with any entry set since they were last written.
            ps.FlushRawMappings();
            ps.FlushMappingDeadZones();
            ps.FlushMappingBidirectional();
            bool changed = MoveEntryFamily(ps.RawMappingEntries, move, ps.SetRawMapping);
            changed |= MoveEntryFamily(ps.MappingDeadZoneEntries, move, ps.SetMappingDeadZone);
            changed |= MoveEntryFamily(ps.MappingBidirectionalEntries, move, ps.SetMappingBidirectional);
            if (!changed) return false;
            ps.FlushRawMappings();
            ps.FlushMappingDeadZones();
            ps.FlushMappingBidirectional();
            ps.UpdateChecksum();
            return true;
        }

        private static bool MoveEntryFamily(RawMappingEntry[] entries, Func<string, string> move,
            Action<string, string> set)
        {
            if (entries == null || entries.Length == 0) return false;
            var moved = new List<(string Key, string Value)>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            bool changed = false;
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Key)) continue;
                string dst = IsRawPosition(e.Key) ? move(e.Key) : e.Key;
                // The role is absent on the new wire, or an earlier entry
                // holds the control already.
                if (dst == null || !taken.Add(dst)) { changed = true; continue; }
                if (!string.Equals(dst, e.Key, StringComparison.Ordinal)) changed = true;
                moved.Add((dst, e.Value));
            }
            if (!changed) return false;

            foreach (var e in entries)
                if (e != null && !string.IsNullOrEmpty(e.Key))
                    set(e.Key, null);
            foreach (var (k, v) in moved)
                set(k, v);
            return true;
        }

        /// <summary>
        /// The rows of a table moved onto another wire: each row's target
        /// through <paramref name="move"/>, and a row the new wire has no
        /// control for left out, an orphan included. The rows are named
        /// anew in place and answered as a new list, for the caller to
        /// assign by reference.
        ///
        /// <para>Two controls of one wire can be one control of another: a
        /// Steam Deck's R3 and its right pad's click are the 2015 Steam
        /// Controller's one right click. Where the move lands the rows of
        /// two targets on one, in one layer, the first that has a source
        /// keeps it, the rule a copy between two layouts keeps
        /// (InputService.ReplaceBaseRowsTranslated). Left as two, the grid
        /// would show one and the engine read both. Two rows the table
        /// already held on one target are no doing of the move and stay
        /// two, on the index that target moves to.</para>
        /// </summary>
        internal static List<Engine.Data.MappingRow> MoveRawRows(IList<Engine.Data.MappingRow> rows,
            Func<string, string> move)
        {
            var kept = new List<Engine.Data.MappingRow>(rows?.Count ?? 0);
            if (rows == null) return kept;
            // The row that holds each target of a layer, with the target it
            // came from: rows that came from one target were two before the
            // move, and rows from two targets are the move's doing.
            var landed = new Dictionary<(string Layer, string Target), (int At, string From)>();
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Target)) continue;
                string from = row.Target;
                string dst = move(from);
                if (dst == null) continue;   // role absent on the target pad, or an orphan
                var key = (string.IsNullOrEmpty(row.LayerMask) ? "Base" : row.LayerMask, dst);
                if (landed.TryGetValue(key, out var there)
                    && !string.Equals(there.From, from, StringComparison.Ordinal))
                {
                    if (HasSource(kept[there.At]) || !HasSource(row)) continue;
                    row.Target = dst;
                    kept[there.At] = row;
                    landed[key] = (there.At, from);
                    continue;
                }
                row.Target = dst;
                landed.TryAdd(key, (kept.Count, from));
                kept.Add(row);
            }
            return kept;
        }

        /// <summary>True when a row has a source that reads an input, a
        /// formula's blank placeholders aside (InputService.HasSource).</summary>
        private static bool HasSource(Engine.Data.MappingRow row)
            => row.Sources != null && row.Sources.Exists(s => s != null
                && !PadForge.Engine.Common.Mapping.SourceEvaluator.IsUnmappedDirect(s));

        /// <summary>
        /// A raw slot's SOCD pairs moved onto another wire. The pairs name
        /// their buttons by wire index ("14:15|8:9", SlotButtonSocd), so
        /// each index goes through <paramref name="move"/> as the raw
        /// button it is. A pair with a button the new wire lacks, or has as
        /// a hat direction or an axis, is dropped: left in place it would
        /// clean two controls nobody paired. A token that is no pair of
        /// indices names nothing on either wire and stays as it is.
        /// </summary>
        internal static string MoveSocdPairs(string pairs, Func<string, string> move)
        {
            if (string.IsNullOrEmpty(pairs) || move == null) return pairs;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var kept = new List<string>();
            foreach (string token in pairs.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = token.IndexOf(':');
                if (colon <= 0 || colon >= token.Length - 1
                    || !int.TryParse(token.Substring(0, colon).Trim(), out int a)
                    || !int.TryParse(token.Substring(colon + 1).Trim(), out int b))
                {
                    kept.Add(token);
                    continue;
                }
                int movedA = RawButtonIndex(move("RawBtn" + a.ToString(invariant)));
                int movedB = RawButtonIndex(move("RawBtn" + b.ToString(invariant)));
                if (movedA < 0 || movedB < 0 || movedA == movedB) continue;
                kept.Add(movedA.ToString(invariant) + ":" + movedB.ToString(invariant));
            }
            return string.Join("|", kept);
        }

        private static int RawButtonIndex(string rawName)
            => rawName != null && rawName.StartsWith("RawBtn", StringComparison.Ordinal)
               && int.TryParse(rawName.AsSpan(6), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out int index)
                ? index : -1;

        /// <summary>Which wire each Nintendo slot's raw mapping data is
        /// currently authored under, by profile id. See
        /// <see cref="TranslateNintendoRawMappings"/> for why this exists.
        /// Null/empty = unknown, which the translation treats as "adopt the
        /// next profile applied, translate nothing".</summary>
        private static readonly string[] _nintendoWireStamp =
            new string[Common.Input.InputManager.MaxPads];

        /// <summary>Records that the slot's raw mapping data belongs to
        /// <paramref name="profileId"/>'s wire WITHOUT translating anything.
        /// Every path that installs mapping data and profile id together
        /// (launch restore, profile apply, workshop import) calls this
        /// before assigning the VM's ProfileId, so the setter's translation
        /// sees from == to and stands down. A type switch stamps the new
        /// category's default and slot create stamps its profile for the same
        /// reason. Slot delete clears it.</summary>
        public static void StampNintendoWire(int padIndex, string profileId)
        {
            if (padIndex < 0 || padIndex >= _nintendoWireStamp.Length) return;
            _nintendoWireStamp[padIndex] = profileId;
        }

        /// <summary>The wire a slot's raw mapping data is currently authored
        /// under, or null when no wire owns it.
        ///
        /// <para>A caller reads this to tell a LIVE profile change from a
        /// restore: every path that installs data and profile together
        /// stamps first, so stamp == incoming means the data already belongs
        /// to that profile and must not be touched.</para></summary>
        public static string GetWireStamp(int padIndex)
            => padIndex >= 0 && padIndex < _nintendoWireStamp.Length
                ? _nintendoWireStamp[padIndex] : null;

        /// <summary>
        /// Re-automaps all devices assigned to a slot for the given output type.
        /// Called when switching virtual controller type so mappings match the new type.
        /// </summary>
        /// <param name="extended">The slot's Extended layout, threaded through
        /// so an unlettered Extended slot gets a raw automap rather than the
        /// standard gamepad fields its surface never reads.</param>
        public static void ReAutoMapSlot(int padIndex, Engine.VirtualControllerType outputType,
            string profileId = null, ViewModels.ExtendedSlotConfig extended = null)
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

            // The automap below authors every device's raw surface under
            // profileId's wire, so that becomes the slot's wire stamp
            // regardless of what the surface carried before.
            if (outputType == Engine.VirtualControllerType.Nintendo)
                StampNintendoWire(padIndex, profileId);

            foreach (var us in slotSettings)
            {
                var ud = FindDeviceByInstanceGuid(us.InstanceGuid);
                var ps = CreateDefaultPadSetting(ud, outputType, profileId, extended);
                us.SetPadSetting(ps);
                us.PadSettingChecksum = ps.PadSettingChecksum;
                Common.Input.BlissBoxRuntime.NoteOwedDefault(us, ud, fresh: true);
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
                    + (ps.KbmMappingEntries?.Length ?? 0)
                    + (ps.VrMappingEntries?.Length ?? 0);
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
