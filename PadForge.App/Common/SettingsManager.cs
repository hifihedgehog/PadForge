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
            /// <summary>Returns the 1-based global slot number for
            /// <paramref name="padIndex"/>, walking type-group order
            /// (Xbox → PlayStation → Extended → KbM → MIDI) so it matches
            /// the dashboard cards, sidebar, Pad page header, and the
            /// Devices-page assignment badges. Returns 0 when the slot
            /// isn't created or isn't in any group's order list (caller
            /// should treat 0 as "not assigned to a visible global
            /// position" and fall back to padIndex + 1).</summary>
            public static int GetGlobalSlotNumber(int padIndex)
            {
                if (padIndex < 0 || padIndex >= SlotCreated.Length) return 0;
                if (!SlotCreated[padIndex]) return 0;
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

            /// <summary>Return the order list for the given VC type group.</summary>
            public static List<int> GetOrderFor(Engine.VirtualControllerType type) => type switch
            {
                Engine.VirtualControllerType.Xbox     => XboxSlotOrder,
                Engine.VirtualControllerType.PlayStation   => PlayStationSlotOrder,
                Engine.VirtualControllerType.Extended      => ExtendedSlotOrder,
                Engine.VirtualControllerType.KeyboardMouse => KeyboardMouseSlotOrder,
                Engine.VirtualControllerType.Midi          => MidiSlotOrder,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
            };

            /// <summary>Append <paramref name="padIndex"/> to its group's tail
            /// if it isn't already present. No-op when already present.</summary>
            public static void Add(int padIndex, Engine.VirtualControllerType type)
            {
                var list = GetOrderFor(type);
                if (!list.Contains(padIndex)) list.Add(padIndex);
            }

            /// <summary>Remove <paramref name="padIndex"/> from its group's
            /// list. No-op when absent.</summary>
            public static void Remove(int padIndex, Engine.VirtualControllerType type)
            {
                GetOrderFor(type).Remove(padIndex);
            }

            /// <summary>Move <paramref name="padIndex"/> from its current group
            /// to <paramref name="newType"/>'s tail. Used by type-change paths.</summary>
            public static void MoveToGroupTail(int padIndex,
                Engine.VirtualControllerType oldType,
                Engine.VirtualControllerType newType)
            {
                if (oldType == newType) return;
                Remove(padIndex, oldType);
                Add(padIndex, newType);
            }

            /// <summary>Move within a single group from
            /// <paramref name="oldPos"/> to <paramref name="newPos"/>.</summary>
            public static void MoveWithinGroup(Engine.VirtualControllerType type, int oldPos, int newPos)
            {
                var list = GetOrderFor(type);
                if (oldPos < 0 || oldPos >= list.Count) return;
                if (newPos < 0 || newPos >= list.Count) return;
                if (oldPos == newPos) return;
                int padIndex = list[oldPos];
                list.RemoveAt(oldPos);
                list.Insert(newPos, padIndex);
            }

            /// <summary>Swap two pad indices' positions within their (shared)
            /// group's order list. Throws if the two pads aren't in the same
            /// group's list.</summary>
            public static void SwapWithinGroup(int padA, int padB, Engine.VirtualControllerType type)
            {
                var list = GetOrderFor(type);
                int ia = list.IndexOf(padA);
                int ib = list.IndexOf(padB);
                if (ia < 0 || ib < 0) return;
                (list[ia], list[ib]) = (list[ib], list[ia]);
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
                int[] persistedMidi = null)
            {
                Reconcile(XboxSlotOrder,          persistedXbox,        slotType, Engine.VirtualControllerType.Xbox);
                Reconcile(PlayStationSlotOrder,   persistedPlayStation, slotType, Engine.VirtualControllerType.PlayStation);
                Reconcile(ExtendedSlotOrder,      persistedExtended,    slotType, Engine.VirtualControllerType.Extended);
                Reconcile(KeyboardMouseSlotOrder, persistedKbm,         slotType, Engine.VirtualControllerType.KeyboardMouse);
                Reconcile(MidiSlotOrder,          persistedMidi,        slotType, Engine.VirtualControllerType.Midi);
            }

            private static void Reconcile(List<int> target, int[] persisted,
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
                return devices.Items.FirstOrDefault(d => d.InstanceGuid == instanceGuid);
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
                if (outputType == Engine.VirtualControllerType.Midi)
                {
                    // MIDI auto-mapping: 6 CCs for axes, 11 notes for buttons.
                    // Maps SDL3 gamepad axes to CC0-CC5 and buttons to Note0-Note10.
                    for (int i = 0; i < 6; i++)
                        ps.SetMidiMapping($"MidiCC{i}", $"Axis {i}");
                    for (int i = 0; i < 11; i++)
                        ps.SetMidiMapping($"MidiNote{i}", $"Button {i}");
                    ps.FlushMidiMappings();

                    ps.UpdateChecksum();
                    return ps;
                }

                ps.LeftThumbAxisX = "Axis 0";
                ps.LeftThumbAxisY = "Axis 1";
                ps.LeftTrigger = "Axis 2";
                ps.RightThumbAxisX = "Axis 3";
                ps.RightThumbAxisY = "Axis 4";
                ps.RightTrigger = "Axis 5";

                // D-pad from hat switch (individual directions for UI display and remapping).
                ps.DPadUp = "POV 0 Up";
                ps.DPadDown = "POV 0 Down";
                ps.DPadLeft = "POV 0 Left";
                ps.DPadRight = "POV 0 Right";

                // SDL3 XInput backend button indices.
                ps.ButtonA = "Button 0";
                ps.ButtonB = "Button 1";
                ps.ButtonX = "Button 2";
                ps.ButtonY = "Button 3";
                ps.LeftShoulder = "Button 4";
                ps.RightShoulder = "Button 5";
                ps.ButtonBack = "Button 6";
                ps.ButtonStart = "Button 7";
                ps.LeftThumbButton = "Button 8";
                ps.RightThumbButton = "Button 9";
                ps.ButtonGuide = "Button 10";

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

                // Motion passthrough auto-mapping for PlayStation output +
                // sensor-capable device. The bundled-source descriptor
                // markers ("Motion Gyro" / "Motion Accel") flag this
                // device as contributing its sensor stream to the
                // slot's motion channel. EnsureMotionRows mirrors the
                // marker into the per-slot MappingSet so the engine
                // sees the row.
                if (outputType == Engine.VirtualControllerType.PlayStation)
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

            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us.MapTo != padIndex) continue;
                    var ud = FindDeviceByInstanceGuid(us.InstanceGuid);
                    var ps = CreateDefaultPadSetting(ud, outputType);
                    us.SetPadSetting(ps);
                    us.PadSettingChecksum = ps.PadSettingChecksum;
                }
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
