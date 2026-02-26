using System;
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
                _mainVm.StatusText = "No device selected.";
                return;
            }

            if (slotIndex < 0 || slotIndex >= InputManager.MaxPads)
            {
                _mainVm.StatusText = $"Invalid slot index: {slotIndex}";
                return;
            }

            // Auto-create the virtual controller slot if it doesn't exist yet.
            if (!SettingsManager.SlotCreated[slotIndex])
            {
                SettingsManager.SlotCreated[slotIndex] = true;
                SettingsManager.SlotEnabled[slotIndex] = true;
            }

            Guid instanceGuid = selectedRow.InstanceGuid;

            // Create or update the UserSetting.
            var us = SettingsManager.AssignDeviceToSlot(instanceGuid, slotIndex);
            if (us == null)
            {
                _mainVm.StatusText = "Failed to assign device.";
                return;
            }

            // Ensure ProductGuid is populated for fallback matching.
            var udForGuid = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
            if (udForGuid != null)
                us.ProductGuid = udForGuid.ProductGuid;

            // If no PadSetting exists or it has no mappings, create defaults.
            // The PadSetting is stored in SettingsManager only — NOT pushed into the
            // ViewModel here, because the slot's ViewModel may be displaying a different
            // device's settings. The correct settings load when the user selects this
            // device in the dropdown (OnSelectedDeviceChanged → LoadPadSettingToViewModel).
            var existingPs = us.GetPadSetting();
            if (existingPs == null || !existingPs.HasAnyMapping)
            {
                var ps = SettingsManager.CreateDefaultPadSetting(udForGuid);
                us.SetPadSetting(ps);
                us.PadSettingChecksum = ps.PadSettingChecksum;
            }

            // Update the row display.
            selectedRow.SetAssignedSlots(SettingsManager.GetAssignedSlots(instanceGuid));

            // Mark settings as dirty.
            _settingsService.MarkDirty();

            _mainVm.StatusText = $"Assigned \"{selectedRow.DeviceName}\" to Player {slotIndex + 1}.";

            // Notify listeners so PadPage dropdowns refresh immediately.
            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);

            // Navigate to the assigned controller page.
            NavigateToSlotRequested?.Invoke(this, slotIndex);
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
            }

            Guid instanceGuid = selectedRow.InstanceGuid;
            var (assigned, us) = SettingsManager.ToggleDeviceSlotAssignment(instanceGuid, slotIndex);

            if (assigned && us != null)
            {
                // Populate device info on the new UserSetting.
                var udForGuid = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
                if (udForGuid != null)
                    us.ProductGuid = udForGuid.ProductGuid;

                // Create default PadSetting if needed.
                var existingPs = us.GetPadSetting();
                if (existingPs == null || !existingPs.HasAnyMapping)
                {
                    var ps = SettingsManager.CreateDefaultPadSetting(udForGuid);
                    us.SetPadSetting(ps);
                    us.PadSettingChecksum = ps.PadSettingChecksum;
                }

                _mainVm.StatusText = $"Assigned \"{selectedRow.DeviceName}\" to slot #{slotIndex + 1}.";
            }
            else
            {
                _mainVm.StatusText = $"Unassigned \"{selectedRow.DeviceName}\" from slot #{slotIndex + 1}.";
            }

            // Update device row display.
            selectedRow.SetAssignedSlots(SettingsManager.GetAssignedSlots(instanceGuid));

            _settingsService.MarkDirty();
            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
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
            _mainVm.StatusText = $"Device hidden. (Can be restored in settings file.)";
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
            _mainVm.StatusText = "Device removed.";

            // Refresh sidebar/dashboard device info (slot persists, just empty now).
            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
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
        /// Returns the slot index (0–3) or -1 if all slots are taken.
        /// </summary>
        public int CreateSlot(VirtualControllerType controllerType = VirtualControllerType.Xbox360)
        {
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i])
                {
                    // Set OutputType BEFORE SlotCreated so that the PropertyChanged
                    // handler's call to RefreshNavControllerItems() sees SlotCreated[i]=false
                    // and doesn't trigger a premature sidebar rebuild.
                    _mainVm.Pads[i].OutputType = controllerType;
                    SettingsManager.SlotCreated[i] = true;
                    SettingsManager.SlotEnabled[i] = true;
                    _settingsService.MarkDirty();
                    DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Deletes a virtual controller slot. Unassigns all devices from it.
        /// </summary>
        public void DeleteSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return;

            SettingsManager.SlotCreated[slotIndex] = false;
            SettingsManager.SlotEnabled[slotIndex] = true; // Reset to default.

            // Unassign all devices mapped to this slot.
            var settings = SettingsManager.UserSettings;
            if (settings != null)
            {
                lock (settings.SyncRoot)
                {
                    foreach (var us in settings.Items)
                    {
                        if (us.MapTo == slotIndex)
                            us.MapTo = -1;
                    }
                }
            }

            _settingsService.MarkDirty();
            _mainVm.StatusText = $"Virtual Controller {slotIndex + 1} deleted.";
            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
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
    }
}
