using System;
using System.Linq;
using PadForge.Common;
using PadForge.Resources.Strings;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Service that handles device management operations triggered by the UI:
    ///   - Assigning a device to a controller slot
    ///   - Unassigning a device
    ///   - Hiding/showing devices
    ///   - Creating default mappings for newly assigned devices
    /// 
    /// Bridges <see cref="DevicesViewModel"/> commands → <see cref="SettingsManager"/>
    /// and <see cref="SettingsService"/>.
    /// </summary>
    public class DeviceService
    {
        private readonly MainViewModel _mainVm;
        private readonly SettingsService _settingsService;

        /// <summary>
        /// Raised after a device is assigned to or unassigned from a slot.
        /// MainWindow subscribes to refresh PadViewModel device info.
        /// </summary>
        public event EventHandler DeviceAssignmentChanged;

        /// <summary>
        /// Raised after a device is assigned to a slot, carrying the slot index.
        /// MainWindow subscribes to navigate to the newly assigned controller page.
        /// </summary>
        public event EventHandler<int> NavigateToSlotRequested;

        public DeviceService(MainViewModel mainVm, SettingsService settingsService)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        }

        /// <summary>
        /// Wires event handlers from the DevicesViewModel and PadViewModels
        /// to this service's handler methods.
        /// </summary>
        public void WireEvents()
        {
            _mainVm.Devices.AssignToSlotRequested += OnAssignToSlot;
            _mainVm.Devices.ToggleSlotRequested += OnToggleSlot;
            _mainVm.Devices.HideDeviceRequested += OnHideDevice;
            _mainVm.Devices.RemoveDeviceRequested += OnRemoveDevice;
            _mainVm.Devices.DeviceHidingChanged += OnDeviceHidingChanged;
        }

        /// <summary>
        /// Unwires event handlers.
        /// </summary>
        public void UnwireEvents()
        {
            _mainVm.Devices.AssignToSlotRequested -= OnAssignToSlot;
            _mainVm.Devices.ToggleSlotRequested -= OnToggleSlot;
            _mainVm.Devices.HideDeviceRequested -= OnHideDevice;
            _mainVm.Devices.RemoveDeviceRequested -= OnRemoveDevice;
            _mainVm.Devices.DeviceHidingChanged -= OnDeviceHidingChanged;
        }

        // ─────────────────────────────────────────────
        //  Assign to slot
        // ─────────────────────────────────────────────

        /// <summary>
        /// Assigns the currently selected device to a controller slot.
        /// Creates a default PadSetting if the device doesn't have one.
        /// </summary>
        private void OnAssignToSlot(object sender, int slotIndex)
        {
            var selectedRow = _mainVm.Devices.SelectedDevice;
            if (selectedRow == null)
            {
                _mainVm.StatusText = Strings.Instance.Status_NoDeviceSelected;
                return;
            }

            if (slotIndex < 0 || slotIndex >= InputManager.MaxPads)
            {
                _mainVm.StatusText = string.Format(Strings.Instance.Status_InvalidSlotIndex_Format, slotIndex);
                return;
            }

            // Auto-create the virtual controller slot if it doesn't exist yet.
            if (!SettingsManager.SlotCreated[slotIndex])
            {
                SettingsManager.SlotCreated[slotIndex] = true;
                SettingsManager.SlotEnabled[slotIndex] = true;
                SettingsManager.SlotOrders.Add(slotIndex, _mainVm.Pads[slotIndex].OutputType);
            }

            Guid instanceGuid = selectedRow.InstanceGuid;

            // Create or update the UserSetting.
            var us = SettingsManager.AssignDeviceToSlot(instanceGuid, slotIndex);
            if (us == null)
            {
                _mainVm.StatusText = Strings.Instance.Status_FailedAssignDevice;
                return;
            }

            // Ensure ProductGuid is populated for fallback matching.
            var udForGuid = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
            if (udForGuid != null)
                us.ProductGuid = udForGuid.ProductGuid;

            // If no PadSetting exists, create defaults.
            var existingPs = us.GetPadSetting();
            if (existingPs == null)
            {
                var outputType = _mainVm.Pads[slotIndex].OutputType;
                var ps = SettingsManager.CreateDefaultPadSetting(udForGuid, outputType);
                us.SetPadSetting(ps);
                us.PadSettingChecksum = ps.PadSettingChecksum;
            }
            else
            {
                // Defensive: an existing PadSetting may have been authored
                // before the touchpad auto-map shipped, or carried over from
                // an Xbox/Extended slot via XML load + slot-type change.
                // Fill empty touchpad mappings when assigning a Touchpad-type
                // device to a PlayStation slot so the user gets the
                // auto-map they expect on first assign.
                FillEmptyAutoMappingsIfApplicable(existingPs, udForGuid,
                    _mainVm.Pads[slotIndex].OutputType);
            }

            // Update the row display.
            selectedRow.SetAssignedSlots(SettingsManager.GetAssignedSlots(instanceGuid));

            // Auto-enable input hiding defaults for newly assigned devices.
            AutoEnableHidingDefaults(udForGuid, selectedRow);

            // Mark settings as dirty.
            _settingsService.MarkDirty();

            // Rebuild the per-VC MappingSet from every assigned device's
            // PadSetting on this slot so the Mappings tab + engine see
            // the just-auto-mapped sources without waiting for a save +
            // reload cycle. The merge is additive — existing rows / user
            // edits on the per-VC MappingSet are preserved.
            SettingsService.RefreshMappingSetsFromLegacy();

            _mainVm.StatusText = string.Format(Strings.Instance.Status_DeviceAssigned_Format, selectedRow.DeviceName, ResolveDisplaySlotNumber(slotIndex));

            // Notify listeners so PadPage dropdowns refresh immediately.
            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);

            // Re-apply hiding with the new device included.
            DeviceHidingStateChanged?.Invoke(this, EventArgs.Empty);

            // Navigate to the assigned controller page.
            NavigateToSlotRequested?.Invoke(this, slotIndex);
        }

        /// <summary>
        /// Assigns a device (by GUID) to a specific slot. Used by cross-panel drag-and-drop.
        /// </summary>
        public void AssignDeviceToSlot(Guid instanceGuid, int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return;

            var row = _mainVm.Devices.Devices
                .OfType<ViewModels.DeviceRowViewModel>()
                .FirstOrDefault(d => d.InstanceGuid == instanceGuid);
            if (row == null) return;

            // Check if already assigned to this slot.
            if (row.AssignedSlots.Contains(slotIndex)) return;

            // Auto-create the virtual controller slot if it doesn't exist yet.
            if (!SettingsManager.SlotCreated[slotIndex])
            {
                SettingsManager.SlotCreated[slotIndex] = true;
                SettingsManager.SlotEnabled[slotIndex] = true;
                SettingsManager.SlotOrders.Add(slotIndex, _mainVm.Pads[slotIndex].OutputType);
            }

            var us = SettingsManager.AssignDeviceToSlot(instanceGuid, slotIndex);
            if (us == null) return;

            var udForGuid = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
            if (udForGuid != null) us.ProductGuid = udForGuid.ProductGuid;

            var existingPs = us.GetPadSetting();
            if (existingPs == null)
            {
                var outputType = _mainVm.Pads[slotIndex].OutputType;
                var ps = SettingsManager.CreateDefaultPadSetting(udForGuid, outputType);
                us.SetPadSetting(ps);
                us.PadSettingChecksum = ps.PadSettingChecksum;
            }
            else
            {
                FillEmptyAutoMappingsIfApplicable(existingPs, udForGuid,
                    _mainVm.Pads[slotIndex].OutputType);
            }

            row.SetAssignedSlots(SettingsManager.GetAssignedSlots(instanceGuid));

            // Auto-enable input hiding defaults for newly assigned devices.
            AutoEnableHidingDefaults(udForGuid, row);

            _settingsService.MarkDirty();
            SettingsService.RefreshMappingSetsFromLegacy();
            _mainVm.StatusText = string.Format(Strings.Instance.Status_DeviceAssigned_Format, row.DeviceName, ResolveDisplaySlotNumber(slotIndex));
            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
            DeviceHidingStateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────────────────────
        //  Toggle slot assignment (multi-slot)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Toggles the selected device's assignment to a specific slot.
        /// Supports multi-slot: a device can be assigned to multiple slots.
        /// </summary>
        private void OnToggleSlot(object sender, int slotIndex)
        {
            var selectedRow = _mainVm.Devices.SelectedDevice;
            if (selectedRow == null) return;

            if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return;

            // Auto-create the virtual controller slot if it doesn't exist yet.
            if (!SettingsManager.SlotCreated[slotIndex])
            {
                SettingsManager.SlotCreated[slotIndex] = true;
                SettingsManager.SlotEnabled[slotIndex] = true;
                SettingsManager.SlotOrders.Add(slotIndex, _mainVm.Pads[slotIndex].OutputType);
            }

            Guid instanceGuid = selectedRow.InstanceGuid;
            var (assigned, us) = SettingsManager.ToggleDeviceSlotAssignment(instanceGuid, slotIndex);

            if (assigned && us != null)
            {
                // Populate device info on the new UserSetting.
                var udForGuid = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
                if (udForGuid != null)
                    us.ProductGuid = udForGuid.ProductGuid;

                // Create PadSetting for the new assignment.
                var existingPs = us.GetPadSetting();
                if (existingPs == null)
                {
                    var outputType = _mainVm.Pads[slotIndex].OutputType;
                    var ps = SettingsManager.CreateDefaultPadSetting(udForGuid, outputType);
                    us.SetPadSetting(ps);
                    us.PadSettingChecksum = ps.PadSettingChecksum;
                }
                else
                {
                    FillEmptyAutoMappingsIfApplicable(existingPs, udForGuid,
                        _mainVm.Pads[slotIndex].OutputType);
                }

                // Auto-enable input hiding defaults for newly assigned devices.
                AutoEnableHidingDefaults(udForGuid, selectedRow);

                // Rebuild the per-VC MappingSet so the Mappings tab + engine
                // pick up the just-auto-mapped sources immediately.
                SettingsService.RefreshMappingSetsFromLegacy();

                _mainVm.StatusText = string.Format(Strings.Instance.Status_DeviceAssignedSlot_Format, selectedRow.DeviceName, ResolveDisplaySlotNumber(slotIndex));
            }
            else
            {
                // Device was unassigned from this slot.
                // If device has no more slot assignments, auto-disable hiding.
                var remainingSlots = SettingsManager.GetAssignedSlots(instanceGuid);
                if (remainingSlots == null || remainingSlots.Count == 0)
                {
                    var udForGuid = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
                    if (udForGuid != null)
                    {
                        udForGuid.HidHideEnabled = false;
                        udForGuid.ConsumeInputEnabled = false;
                        selectedRow.HidHideEnabled = false;
                        selectedRow.ConsumeInputEnabled = false;
                    }
                }

                _mainVm.StatusText = string.Format(Strings.Instance.Status_DeviceUnassignedSlot_Format, selectedRow.DeviceName, slotIndex + 1);
            }

            // Update device row display.
            selectedRow.SetAssignedSlots(SettingsManager.GetAssignedSlots(instanceGuid));

            _settingsService.MarkDirty();
            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
            DeviceHidingStateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────────────────────
        //  Hide device
        // ─────────────────────────────────────────────

        /// <summary>
        /// Hides a device from the device list. The device remains in
        /// SettingsManager but is marked as hidden and won't be shown.
        /// </summary>
        private void OnHideDevice(object sender, Guid instanceGuid)
        {
            var ud = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
            if (ud != null)
            {
                ud.IsHidden = true;
            }

            // Also update the ViewModel row.
            var row = _mainVm.Devices.FindByGuid(instanceGuid);
            if (row != null)
            {
                row.IsHidden = true;
            }

            _settingsService.MarkDirty();
            _mainVm.StatusText = Strings.Instance.Status_DeviceHidden;
        }

        // ─────────────────────────────────────────────
        //  Remove device
        // ─────────────────────────────────────────────

        /// <summary>
        /// Removes a device and its associated settings entirely.
        /// The device record, any UserSettings pointing to it, and PadSettings
        /// are all deleted from SettingsManager. The virtual controller slot
        /// itself is NOT deleted — it remains as an empty slot.
        /// </summary>
        private void OnRemoveDevice(object sender, Guid instanceGuid)
        {
            SettingsManager.RemoveDevice(instanceGuid);
            _settingsService.MarkDirty();
            _mainVm.StatusText = Strings.Instance.Status_DeviceRemoved;

            // Refresh sidebar/dashboard device info (slot persists, just empty now).
            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────────────────────
        //  Device hiding toggle
        // ─────────────────────────────────────────────

        /// <summary>
        /// Raised when a device's hiding toggle (HidHide or ConsumeInput) changes.
        /// InputService subscribes to re-apply device hiding.
        /// </summary>
        public event EventHandler DeviceHidingStateChanged;

        /// <summary>
        /// Handles a device hiding toggle change from the UI. Writes the new state
        /// to UserDevice and notifies listeners to re-apply hiding.
        /// </summary>
        private void OnDeviceHidingChanged(object sender, Guid instanceGuid)
        {
            var row = _mainVm.Devices.FindByGuid(instanceGuid);
            if (row == null) return;

            var ud = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
            if (ud != null)
            {
                ud.HidHideEnabled = row.HidHideEnabled;
                ud.ConsumeInputEnabled = row.ConsumeInputEnabled;
                ud.ForceRawJoystickMode = row.ForceRawJoystickMode;
            }

            _settingsService.MarkDirty();
            DeviceHidingStateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────────────────────
        //  Unassign
        // ─────────────────────────────────────────────

        /// <summary>
        /// Unassigns a device from its current slot.
        /// </summary>
        /// <param name="instanceGuid">Device to unassign.</param>
        public void UnassignDevice(Guid instanceGuid)
        {
            SettingsManager.UnassignDevice(instanceGuid);

            var row = _mainVm.Devices.FindByGuid(instanceGuid);
            if (row != null)
                row.SetAssignedSlots(new System.Collections.Generic.List<int>());

            _settingsService.MarkDirty();

            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────────────────────
        //  Virtual controller slot management
        // ─────────────────────────────────────────────

        /// <summary>
        /// Creates the next available virtual controller slot with the specified type.
        /// Returns the slot index (0–15) or -1 if all slots are taken.
        /// </summary>
        public int CreateSlot(VirtualControllerType controllerType = VirtualControllerType.Xbox)
        {
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i])
                {
                    // Set OutputType BEFORE SlotCreated so that the PropertyChanged
                    // handler's call to RefreshNavControllerItems() sees SlotCreated[i]=false
                    // and doesn't trigger a premature sidebar rebuild.
                    _mainVm.Pads[i].OutputType = controllerType;

                    // Populate ProfileId with the category default so the
                    // profile-picker dropdown shows the selected default
                    // immediately on create. Engine-side CreateVirtualController
                    // falls back to the same default when SlotProfileIds is
                    // null, but that fallback is silent — without this the
                    // dropdown would show no selection until the user picks
                    // one manually.
                    _mainVm.Pads[i].ProfileId = InputManager.GetDefaultProfileId(controllerType);

                    SettingsManager.SlotCreated[i] = true;
                    SettingsManager.SlotEnabled[i] = true;
                    SettingsManager.SlotOrders.Add(i, controllerType);
                    _settingsService.MarkDirty();
                    DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Deletes a virtual controller slot. Unassigns all devices from it.
        /// Returns the type of the deleted slot AND its pre-removal
        /// position in the matching group's order list so callers can
        /// drive the per-group <c>OnSlotDeleted</c> bubble-down cascade
        /// without re-querying after the order list has been mutated.
        /// </summary>
        public SlotDeletionInfo DeleteSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= InputManager.MaxPads)
                return new SlotDeletionInfo(VirtualControllerType.Xbox, -1);

            // Capture before the reset wipes OutputType and SlotOrders.Remove
            // mutates the group's position list. Both are inputs to the
            // bubble-down cascade in InputService.OnSlotDeleted.
            var deletedType = _mainVm.Pads[slotIndex].OutputType;
            int oldPosition = SettingsManager.SlotOrders.GetOrderFor(deletedType).IndexOf(slotIndex);

            SettingsManager.SlotCreated[slotIndex] = false;
            SettingsManager.SlotEnabled[slotIndex] = true; // Reset to default.
            SettingsManager.SlotOrders.Remove(slotIndex, deletedType);

            // Reset PadViewModel so stale settings (deadzone, sensitivity, etc.)
            // don't leak into the next controller created in this slot.
            _mainVm.Pads[slotIndex].ResetAllSettings();

            // Clear the PadVm's selected device too — otherwise the mapping
            // grid retains the previously-loaded device's mapping rows even
            // after the UserSetting it was driven by has been removed. With
            // SelectedMappedDevice null, RebuildMappings produces an empty
            // grid and a new occupant of this slot starts clean.
            _mainVm.Pads[slotIndex].SelectedMappedDevice = null;

            // Unassign all devices mapped to this slot.
            // Remove entries that are ONLY mapped to this slot (orphans).
            // Keep entries that are also mapped to other slots via separate UserSetting instances.
            var settings = SettingsManager.UserSettings;
            if (settings != null)
            {
                lock (settings.SyncRoot)
                {
                    for (int i = settings.Items.Count - 1; i >= 0; i--)
                    {
                        var us = settings.Items[i];
                        if (us.MapTo == slotIndex)
                        {
                            // Remove entirely — no reason to keep a MapTo=-1 entry.
                            // If the device is later assigned to a new slot, a fresh
                            // UserSetting will be created by the assignment logic.
                            settings.Items.RemoveAt(i);
                        }
                    }
                }
            }

            _settingsService.MarkDirty();
            _mainVm.StatusText = string.Format(Strings.Instance.Status_VCDeleted_Format, slotIndex + 1);
            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
            return new SlotDeletionInfo(deletedType, oldPosition);
        }

        /// <summary>
        /// Sets the enabled state of a virtual controller slot.
        /// </summary>
        public void SetSlotEnabled(int slotIndex, bool enabled)
        {
            if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return;

            SettingsManager.SlotEnabled[slotIndex] = enabled;
            _settingsService.MarkDirty();
        }

        /// <summary>Maps a raw 0-based <paramref name="slotIndex"/> to the
        /// 1-based global slot number the user sees in the UI (badges,
        /// dashboard, sidebar, Pad page header). That ordering walks the
        /// VC type groups (Xbox → PlayStation → Extended → KbM → MIDI),
        /// so a PlayStation slot at padIndex=2 might be VC #1 in the UI
        /// — the prior <c>slotIndex + 1</c> form silently disagreed with
        /// every other display, producing the "Assigned to Virtual
        /// Controller 3" status while the user clicked badge #2. Falls
        /// back to <c>slotIndex + 1</c> when the slot isn't in any
        /// group's order list yet (auto-created during this same call).
        /// </summary>
        private static int ResolveDisplaySlotNumber(int slotIndex)
        {
            int global = SettingsManager.SlotOrders.GetGlobalSlotNumber(slotIndex);
            return global > 0 ? global : slotIndex + 1;
        }

        /// <summary>Defensive auto-map for an existing PadSetting on assign.
        /// Generates a fresh auto-map from <see cref="SettingsManager.CreateDefaultPadSetting"/>
        /// and copies any populated field over to <paramref name="existingPs"/>
        /// only when the corresponding field on <paramref name="existingPs"/>
        /// is empty. Preserves every user-authored value while guaranteeing
        /// that a Gamepad-CapType device gets its standard auto-map (ButtonA
        /// → Button 0, LeftThumbAxisX → Axis 0, etc.) AND its touchpad mapping
        /// when the slot is PlayStation, and a Touchpad-CapType device gets
        /// the touchpad mapping on PlayStation.
        ///
        /// <para>Why this exists as a separate "fill empties" path rather
        /// than relying on "if (existingPs == null) create defaults" alone:
        /// the engine writes per-VC <see cref="MappingSet"/> rows on save
        /// from each ViewModel's current state, then on next load each
        /// device's PadSetting is hydrated from a Sources slice the
        /// MappingSet drives. A device that landed on a slot whose
        /// MappingSet only had OTHER devices' rows (e.g. a touchpad
        /// previously assigned that left only touchpad rows in the per-VC
        /// MappingSet for slot 2) loads with an existing-but-empty
        /// PadSetting — the if/else would then SKIP the auto-map for the
        /// new device. The XML bug "DualSense assigned after Web Touchpad
        /// inherited the touchpad-only PadSetting" came from exactly this
        /// path.</para></summary>
        private static void FillEmptyAutoMappingsIfApplicable(PadSetting existingPs,
            UserDevice ud, Engine.VirtualControllerType outputType)
        {
            if (existingPs == null || ud == null) return;

            var freshPs = SettingsManager.CreateDefaultPadSetting(ud, outputType);
            if (freshPs == null) return;

            // Walk every copyable string mapping property and fill empty
            // ones on existingPs from freshPs. Reflection here mirrors the
            // pattern PadSetting.CopyFrom uses internally.
            var type = typeof(PadSetting);
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(string)) continue;
                if (!prop.CanRead || !prop.CanWrite) continue;
                if (prop.Name == nameof(PadSetting.PadSettingChecksum)) continue;
                string current = prop.GetValue(existingPs) as string;
                if (!string.IsNullOrEmpty(current)) continue;        // user-authored or already set
                string fresh = prop.GetValue(freshPs) as string;
                if (string.IsNullOrEmpty(fresh)) continue;           // auto-map didn't set this field
                prop.SetValue(existingPs, fresh);
            }
            existingPs.UpdateChecksum();
        }

        // ─────────────────────────────────────────────
        //  Auto-enable hiding defaults
        // ─────────────────────────────────────────────

        /// <summary>
        /// Sets default input hiding options when a device is newly assigned to a slot.
        /// Gamepads: HidHide auto-ON (if installed). Keyboards/Mice: ConsumeInput auto-ON.
        /// </summary>
        private void AutoEnableHidingDefaults(UserDevice ud, DeviceRowViewModel row)
        {
            if (ud == null || row == null) return;

            bool isGamepad = ud.CapType == InputDeviceType.Gamepad ||
                             ud.CapType == InputDeviceType.Joystick ||
                             ud.CapType == InputDeviceType.Driving ||
                             ud.CapType == InputDeviceType.Flight ||
                             ud.CapType == InputDeviceType.FirstPerson;

            if (isGamepad)
            {
                // Auto-enable HidHide if the driver is available.
                if (HidHideController.IsAvailable())
                {
                    ud.HidHideEnabled = true;
                    row.HidHideEnabled = true;
                }
            }
            // Keyboards and mice: do NOT auto-enable consumption — blocking
            // someone's only mouse/keyboard locks them out of Windows.
        }

    }

    /// <summary>
    /// Pair of values returned by <see cref="DeviceService.DeleteSlot"/>:
    /// the deleted slot's <see cref="VirtualControllerType"/> and its
    /// pre-removal index in the matching group's order list. The position
    /// is captured before <c>SlotOrders.Remove</c> mutates the list so
    /// the bubble-down cascade in InputService.OnSlotDeleted knows which
    /// post-removal entries are survivors that just shifted up.
    /// <see cref="OldGroupPosition"/> is -1 when the slot wasn't in any
    /// order list (defensive; shouldn't happen in normal flow).
    /// </summary>
    public readonly record struct SlotDeletionInfo(
        VirtualControllerType Type,
        int OldGroupPosition);
}
