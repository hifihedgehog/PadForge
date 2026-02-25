using System;
using PadForge.Common.Input;
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
            _mainVm.Devices.HideDeviceRequested += OnHideDevice;
            _mainVm.Devices.RemoveDeviceRequested += OnRemoveDevice;
        }

        /// <summary>
        /// Unwires event handlers.
        /// </summary>
        public void UnwireEvents()
        {
            _mainVm.Devices.AssignToSlotRequested -= OnAssignToSlot;
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

            if (slotIndex < 0 || slotIndex > 3)
            {
                _mainVm.StatusText = $"Invalid slot index: {slotIndex}";
                return;
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
            selectedRow.AssignedSlot = slotIndex;

            // Mark settings as dirty.
            _settingsService.MarkDirty();

            _mainVm.StatusText = $"Assigned \"{selectedRow.DeviceName}\" to Player {slotIndex + 1}.";

            // Notify listeners so PadPage dropdowns refresh immediately.
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
        /// Called when the user explicitly removes an offline device.
        /// The device record, any UserSettings pointing to it, and PadSettings
        /// are all deleted from SettingsManager.
        /// </summary>
        private void OnRemoveDevice(object sender, Guid instanceGuid)
        {
            SettingsManager.RemoveDevice(instanceGuid);
            _settingsService.MarkDirty();
            _mainVm.StatusText = "Device removed.";
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
                row.AssignedSlot = -1;

            _settingsService.MarkDirty();

            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
