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
                FillEmptyTouchpadMappingsIfApplicable(existingPs, udForGuid,
                    _mainVm.Pads[slotIndex].OutputType);
            }

            // Update the row display.
            selectedRow.SetAssignedSlots(SettingsManager.GetAssignedSlots(instanceGuid));

            // Auto-enable input hiding defaults for newly assigned devices.
            AutoEnableHidingDefaults(udForGuid, selectedRow);

            // Mark settings as dirty.
            _settingsService.MarkDirty();

            _mainVm.StatusText = string.Format(Strings.Instance.Status_DeviceAssigned_Format, selectedRow.DeviceName, slotIndex + 1);

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
                FillEmptyTouchpadMappingsIfApplicable(existingPs, udForGuid,
                    _mainVm.Pads[slotIndex].OutputType);
            }

            row.SetAssignedSlots(SettingsManager.GetAssignedSlots(instanceGuid));

            // Auto-enable input hiding defaults for newly assigned devices.
            AutoEnableHidingDefaults(udForGuid, row);

            _settingsService.MarkDirty();
            _mainVm.StatusText = string.Format(Strings.Instance.Status_DeviceAssigned_Format, row.DeviceName, slotIndex + 1);
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

                // Auto-enable input hiding defaults for newly assigned devices.
                AutoEnableHidingDefaults(udForGuid, selectedRow);

                _mainVm.StatusText = string.Format(Strings.Instance.Status_DeviceAssignedSlot_Format, selectedRow.DeviceName, slotIndex + 1);
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

        /// <summary>Defensive auto-map: when assigning a touchpad-type
        /// device to a PlayStation slot, fill any EMPTY touchpad mapping
        /// slots on the existing PadSetting with the canonical
        /// "Touchpad 0 Finger M X|Y|Down" / "Touchpad 0 Click" descriptors.
        /// Does NOT touch fields the user has already authored — only
        /// populates blank ones. Covers cases where the PadSetting was
        /// authored before the touchpad auto-map shipped, or carried over
        /// from a non-PlayStation slot type, so first-assign produces the
        /// expected default touchpad mapping every time.</summary>
        private static void FillEmptyTouchpadMappingsIfApplicable(PadSetting ps,
            UserDevice ud, Engine.VirtualControllerType outputType)
        {
            if (ps == null || ud == null) return;
            if (outputType != Engine.VirtualControllerType.PlayStation) return;
            if (!ud.HasTouchpad) return;

            if (string.IsNullOrEmpty(ps.TouchpadX1))      ps.TouchpadX1      = "Touchpad 0 Finger 0 X";
            if (string.IsNullOrEmpty(ps.TouchpadY1))      ps.TouchpadY1      = "Touchpad 0 Finger 0 Y";
            if (string.IsNullOrEmpty(ps.TouchpadContact1)) ps.TouchpadContact1 = "Touchpad 0 Finger 0 Down";
            if (string.IsNullOrEmpty(ps.TouchpadX2))      ps.TouchpadX2      = "Touchpad 0 Finger 1 X";
            if (string.IsNullOrEmpty(ps.TouchpadY2))      ps.TouchpadY2      = "Touchpad 0 Finger 1 Y";
            if (string.IsNullOrEmpty(ps.TouchpadContact2)) ps.TouchpadContact2 = "Touchpad 0 Finger 1 Down";
            if (string.IsNullOrEmpty(ps.TouchpadClick))   ps.TouchpadClick   = "Touchpad 0 Click";
            ps.UpdateChecksum();
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
