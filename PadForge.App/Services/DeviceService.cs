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
                _mainVm.SetStatus(Strings.Instance.Status_NoDeviceSelected, persist: true);
                return;
            }

            if (slotIndex < 0 || slotIndex >= InputManager.MaxPads)
            {
                _mainVm.SetStatus(string.Format(Strings.Instance.Status_InvalidSlotIndex_Format, slotIndex), persist: true);
                return;
            }

            _settingsService.FlushPendingDeviceEdits();

            // Auto-create the virtual controller slot if it doesn't exist yet.
            if (!SettingsManager.SlotCreated[slotIndex])
            {
                // Seed the category default, the same reason the create path in
                // CreateSlotsForDevices documents: the engine falls back to this
                // default silently when SlotProfileIds is null, so without the
                // seed the profile dropdown shows NO selection on a slot that is
                // in fact running the default. All three auto-create blocks in
                // this file missed it.
                _mainVm.Pads[slotIndex].ProfileId =
                    InputManager.GetDefaultProfileId(_mainVm.Pads[slotIndex].OutputType);
                // The new slot's data is authored under that profile. When
                // the pad already carried it, the setter did not run and the
                // stamp a deleted slot left unknown would stay unknown.
                SettingsManager.StampNintendoWire(slotIndex, _mainVm.Pads[slotIndex].ProfileId);
                SettingsManager.SlotCreated[slotIndex] = true;
                SettingsManager.SlotEnabled[slotIndex] = true;
                SettingsManager.SlotOrders.Add(slotIndex, _mainVm.Pads[slotIndex].OutputType);
            }

            Guid instanceGuid = selectedRow.InstanceGuid;

            // Create or update the UserSetting.
            var us = SettingsManager.AssignDeviceToSlot(instanceGuid, slotIndex);
            if (us == null)
            {
                _mainVm.SetStatus(Strings.Instance.Status_FailedAssignDevice, persist: true);
                return;
            }

            // Ensure ProductGuid is populated for fallback matching.
            var udForGuid = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
            if (udForGuid != null)
                us.ProductGuid = udForGuid.ProductGuid;

            // If no PadSetting exists, create defaults. Also re-auto-map when the
            // existing PadSetting is foreign — descriptors authored for a different
            // device that shared this slot (a wheel's raw Button 24 / IAxis 3 on a
            // DualSense). FillEmpty can't heal that: the foreign fields aren't empty,
            // so only the genuinely empty Share button gets filled.
            var existingPs = us.GetPadSetting();
            var outputType = _mainVm.Pads[slotIndex].OutputType;
            var slotProfileId = _mainVm.Pads[slotIndex].ProfileId;
            if (existingPs == null || IsForeignPadSetting(existingPs, udForGuid, outputType, slotProfileId))
            {
                if (existingPs != null)
                    SettingsService.StripDeviceFromSlot(instanceGuid, slotIndex);
                var ps = SettingsManager.CreateDefaultPadSetting(udForGuid, outputType, slotProfileId,
                    _mainVm.Pads[slotIndex].ExtendedConfig);
                us.SetPadSetting(ps);
                us.PadSettingChecksum = ps.PadSettingChecksum;
                // A Bliss-Box port with nothing to place its controls through
                // owes the slot its default. A fresh default replaces whatever
                // the assignment owed before.
                PadForge.Common.Input.BlissBoxRuntime.NoteOwedDefault(us, udForGuid, fresh: true);
            }
            else
            {
                // Defensive: an existing PadSetting may have been authored
                // before the touchpad auto-map shipped, or carried over from
                // an Xbox/Extended slot via XML load + slot-type change.
                // Fill empty touchpad mappings when assigning a Touchpad-type
                // device to a PlayStation slot so the user gets the
                // auto-map they expect on first assign.
                FillEmptyAutoMappingsIfApplicable(existingPs, udForGuid, outputType, slotProfileId);
                // A Bliss-Box port with no placement answered nothing here.
                PadForge.Common.Input.BlissBoxRuntime.NoteOwedDefault(us, udForGuid, fresh: false);
            }

            // A Workshop import parks its device tuning on the slot because it
            // runs before any device exists. Now that one does, fold those
            // stamps into the device's OWN settings. This is the SIBLING of
            // the call in AssignDeviceToSlot: the runtime overlays it replaced
            // applied on every assignment path by construction, so covering
            // one entry point and not the other silently drops the tuning for
            // whichever path the user happens to take.
            WorkshopTuningApplier.ApplyToAssignedDevice(slotIndex, us.GetPadSetting(), us.InstanceGuid.ToString());

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

            _settingsService.FlushPendingDeviceEdits();

            // Auto-create the virtual controller slot if it doesn't exist yet.
            if (!SettingsManager.SlotCreated[slotIndex])
            {
                // Seed the category default, the same reason the create path in
                // CreateSlotsForDevices documents: the engine falls back to this
                // default silently when SlotProfileIds is null, so without the
                // seed the profile dropdown shows NO selection on a slot that is
                // in fact running the default. All three auto-create blocks in
                // this file missed it.
                _mainVm.Pads[slotIndex].ProfileId =
                    InputManager.GetDefaultProfileId(_mainVm.Pads[slotIndex].OutputType);
                // The new slot's data is authored under that profile. When
                // the pad already carried it, the setter did not run and the
                // stamp a deleted slot left unknown would stay unknown.
                SettingsManager.StampNintendoWire(slotIndex, _mainVm.Pads[slotIndex].ProfileId);
                SettingsManager.SlotCreated[slotIndex] = true;
                SettingsManager.SlotEnabled[slotIndex] = true;
                SettingsManager.SlotOrders.Add(slotIndex, _mainVm.Pads[slotIndex].OutputType);
            }

            var us = SettingsManager.AssignDeviceToSlot(instanceGuid, slotIndex);
            if (us == null) return;

            var udForGuid = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
            if (udForGuid != null) us.ProductGuid = udForGuid.ProductGuid;

            var existingPs = us.GetPadSetting();
            var outputType = _mainVm.Pads[slotIndex].OutputType;
            var slotProfileId = _mainVm.Pads[slotIndex].ProfileId;
            if (existingPs == null || IsForeignPadSetting(existingPs, udForGuid, outputType, slotProfileId))
            {
                if (existingPs != null)
                    SettingsService.StripDeviceFromSlot(instanceGuid, slotIndex);
                var ps = SettingsManager.CreateDefaultPadSetting(udForGuid, outputType, slotProfileId,
                    _mainVm.Pads[slotIndex].ExtendedConfig);
                us.SetPadSetting(ps);
                us.PadSettingChecksum = ps.PadSettingChecksum;
                // A Bliss-Box port with nothing to place its controls through
                // owes the slot its default. A fresh default replaces whatever
                // the assignment owed before.
                PadForge.Common.Input.BlissBoxRuntime.NoteOwedDefault(us, udForGuid, fresh: true);
            }
            else
            {
                FillEmptyAutoMappingsIfApplicable(existingPs, udForGuid, outputType, slotProfileId);
                // A Bliss-Box port with no placement answered nothing here.
                PadForge.Common.Input.BlissBoxRuntime.NoteOwedDefault(us, udForGuid, fresh: false);
            }

            // A Workshop import parks its device tuning on the slot
            // because it runs before any device exists. Now that one
            // does, fold those stamps into the device's OWN settings so
            // the existing cards show and edit them, and the engine has
            // one place to read instead of an invisible override.
            WorkshopTuningApplier.ApplyToAssignedDevice(slotIndex, us.GetPadSetting(), us.InstanceGuid.ToString());

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

            SetDeviceSlotAssignment(selectedRow.InstanceGuid, slotIndex,
                !SettingsManager.GetAssignedSlots(selectedRow.InstanceGuid).Contains(slotIndex));
        }

        /// <summary>Set one route without changing the selected device. Repeated requests are no-ops.</summary>
        public bool SetDeviceSlotAssignment(Guid instanceGuid, int slotIndex, bool assigned)
        {
            if (slotIndex < 0 || slotIndex >= InputManager.MaxPads) return false;
            var row = _mainVm.Devices.FindByGuid(instanceGuid);
            if (row == null) return false;
            var current = SettingsManager.GetAssignedSlots(instanceGuid);
            row.SetAssignedSlots(current);
            if (current.Contains(slotIndex) == assigned) return true;
            if (assigned) AssignDeviceToSlot(instanceGuid, slotIndex);
            else UnassignFromSlot(row, slotIndex);
            return SettingsManager.GetAssignedSlots(instanceGuid).Contains(slotIndex) == assigned;
        }

        private void UnassignFromSlot(ViewModels.DeviceRowViewModel row, int slotIndex)
        {
            _settingsService.FlushPendingDeviceEdits();

            Guid instanceGuid = row.InstanceGuid;
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.RemoveAll(s => s.InstanceGuid == instanceGuid && s.MapTo == slotIndex);

            SettingsService.StripDeviceFromSlot(instanceGuid, slotIndex);
            _mainVm.Pads[slotIndex].RemoveDeviceConfig(instanceGuid);
            var remaining = SettingsManager.GetAssignedSlots(instanceGuid);
            if (remaining.Count == 0)
            {
                var device = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
                if (device != null)
                {
                    device.HidHideEnabled = false;
                    device.ConsumeInputEnabled = false;
                    row.HidHideEnabled = false;
                    row.ConsumeInputEnabled = false;
                }
            }
            row.SetAssignedSlots(remaining);
            _mainVm.StatusText = string.Format(Strings.Instance.Status_DeviceUnassignedSlot_Format,
                row.DeviceName, ResolveDisplaySlotNumber(slotIndex));
            _settingsService.MarkDirty();
            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
            DeviceHidingStateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Hide a device card without removing its settings.</summary>
        private void OnHideDevice(object sender, Guid instanceGuid)
        {
            var ud = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
            if (ud != null)
            {
                ud.IsHidden = true;
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
            _settingsService.FlushPendingDeviceEdits();

            SettingsManager.RemoveDevice(instanceGuid);
            foreach (var pad in _mainVm.Pads)
                pad.RemoveDeviceConfig(instanceGuid);
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
                // Force Raw Joystick Mode makes the device's mapping manual,
                // so nothing its Bliss-Box port owed a slot is filled.
                if (row.ForceRawJoystickMode && !ud.ForceRawJoystickMode)
                    CancelOwedMappingForDevice(instanceGuid);
                ud.ForceRawJoystickMode = row.ForceRawJoystickMode;
                ud.IdleDisconnectSeconds = Math.Max(0, row.IdleDisconnectMinutes) * 60;
                ud.QuickChargeEnabled = row.QuickChargeEnabled;
                ud.GunCalibration = row.GunCalibration ?? string.Empty;
                // A connected gun scales its aim by the new window at once.
                if (ud.Device is PadForge.Engine.SdlDeviceWrapper gun && gun.IsGunCon2)
                    gun.GunCon2Calibration = PadForge.Engine.GunCon2Calibration.Parse(ud.GunCalibration);
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
            _settingsService.FlushPendingDeviceEdits();

            SettingsManager.UnassignDevice(instanceGuid);

            // Strip every per-VC MappingSet source bound to this device.
            // Without this, an immediate reassign would still see the
            // device's prior auto-mapped / user-edited sources sitting
            // in the slot's MappingSet — they got carved out by the
            // legacy merge's "departed device" pass, but only once the
            // event-handler chain ran. Doing it synchronously here makes
            // unassign atomic: the device's mappings are GONE the moment
            // the call returns, and the autosave's PushUi pass can't
            // race in to write VM state back to the MappingSet before
            // the strip happens.
            SettingsService.StripDeviceFromAllSlots(instanceGuid);

            // The device's per-slot config (lighting, adaptive triggers,
            // audio chain) is per-assignment state and goes with the
            // assignment, for the same atomicity reason as the strip
            // above. UpdatePadDeviceInfo prunes too, on its next pass.
            foreach (var pad in _mainVm.Pads)
                pad.RemoveDeviceConfig(instanceGuid);

            var row = _mainVm.Devices.FindByGuid(instanceGuid);
            if (row != null)
                row.SetAssignedSlots(new System.Collections.Generic.List<int>());

            // Retiring EVERY route is the final-assignment case by definition, so
            // it owes the same cleanup the single-slot path does when the last
            // one leaves: hiding and consumption are assignment state, and a
            // device left flagged after its last route went stays cloaked with
            // nothing routing it.
            var device = SettingsManager.FindDeviceByInstanceGuid(instanceGuid);
            if (device != null)
            {
                device.HidHideEnabled = false;
                device.ConsumeInputEnabled = false;
                if (row != null)
                {
                    row.HidHideEnabled = false;
                    row.ConsumeInputEnabled = false;
                }
            }

            _settingsService.MarkDirty();

            DeviceAssignmentChanged?.Invoke(this, EventArgs.Empty);
            DeviceHidingStateChanged?.Invoke(this, EventArgs.Empty);
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
            // Per-type capacity (VR is capped at one, see
            // SettingsManager.CanSlotTakeType). Guarding here means every
            // present and future caller inherits it.
            if (!SettingsManager.CanSlotTakeType(controllerType,
                    pi => _mainVm.Pads[pi].OutputType))
                return -1;

            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (!SettingsManager.SlotCreated[i])
                {
                    _settingsService.FlushPendingDeviceEdits();

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
                    // The new slot's data is authored under that profile.
                    // When the pad already carried the type and the profile
                    // (a slot deleted and created again), neither setter ran
                    // and the stamp the delete cleared would stay unknown,
                    // so the first live preset change read as a restore: a
                    // pick of the DualShock 3 Full filled no pressure rows.
                    SettingsManager.StampNintendoWire(i, _mainVm.Pads[i].ProfileId);

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
            // The toast's slot number belongs in this same capture. It was the
            // one status message in this file using the raw slotIndex + 1 while
            // its three siblings resolve the DISPLAY number, so a reordered
            // slot was announced as deleted under a number the user never saw.
            // Resolving it after the removal below would not work either: the
            // slot is gone from the order by then.
            int displayNo = ResolveDisplaySlotNumber(slotIndex);

            _settingsService.FlushPendingDeviceEdits();

            SettingsManager.SlotCreated[slotIndex] = false;
            SettingsManager.SlotEnabled[slotIndex] = true; // Reset to default.
            SettingsManager.SlotOrders.Remove(slotIndex, deletedType);
            // The slot's mapping data goes with it, so no wire owns the
            // index any more.
            SettingsManager.StampNintendoWire(slotIndex, null);

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
            _mainVm.StatusText = string.Format(Strings.Instance.Status_VCDeleted_Format, displayNo);
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
        /// <summary>Runs the automap for <paramref name="profileId"/> over
        /// every device on a slot, filling targets that are EMPTY and
        /// leaving everything the user bound alone.
        ///
        /// <para>This is what a profile change within one output type needs.
        /// A type change re-authors the whole surface through
        /// SettingsManager.ReAutoMapSlot, but changing an Extended slot from
        /// one profile to another keeps the surface and only ADDS targets:
        /// the Steam Controller 2026 has a D-pad and a Quick Access button
        /// the Steam Deck's wire does not, and those rows came up empty
        /// because nothing re-ran the automap for them.</para>
        ///
        /// <para>Snapshot the slot under the UserSettings lock and resolve
        /// devices outside it. FindDeviceByInstanceGuid takes the
        /// UserDevices lock, and taking it while holding UserSettings
        /// inverts the canonical order into an ABBA deadlock with the
        /// disconnect and migration paths.</para></summary>
        public static void FillEmptyAutoMappingsForSlot(int padIndex,
            Engine.VirtualControllerType outputType, string profileId)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return;

            var slot = new System.Collections.Generic.List<UserSetting>();
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us.MapTo == padIndex) slot.Add(us);
            }

            foreach (var us in slot)
            {
                var ud = SettingsManager.FindDeviceByInstanceGuid(us.InstanceGuid);
                var ps = us.GetPadSetting();
                if (ud == null || ps == null) continue;
                FillEmptyAutoMappingsIfApplicable(ps, ud, outputType, profileId);
                ps.UpdateChecksum();
                us.PadSettingChecksum = ps.PadSettingChecksum;
                // A Bliss-Box port with no placement answered nothing. Its
                // default is owed until it can be placed.
                if (PadForge.Common.Input.BlissBoxRuntime.DefaultOwed(ud))
                    us.BlissBoxOwed |= BlissBoxOwedMapping.Default;
            }
        }

        /// <summary>The PlayStation targets whose default mapping depends on
        /// the preset (SettingsManager.CreateDefaultPadSetting): the DualSense
        /// family's Mic Mute, the Edge's paddles and Fn buttons, the touchpad
        /// on every preset whose report carries one, and the DualShock 3
        /// Full's button pressure.</summary>
        internal static readonly string[] PresetGatedPlayStationTargets =
            new[]
            {
                nameof(PadSetting.ButtonMute),
                nameof(PadSetting.RightPaddle), nameof(PadSetting.LeftPaddle),
                nameof(PadSetting.RightFunction), nameof(PadSetting.LeftFunction),
                nameof(PadSetting.TouchpadX1), nameof(PadSetting.TouchpadY1), nameof(PadSetting.TouchpadContact1),
                nameof(PadSetting.TouchpadX2), nameof(PadSetting.TouchpadY2), nameof(PadSetting.TouchpadContact2),
                nameof(PadSetting.TouchpadClick),
            }.Concat(MappingSetMigrator.PressureTargets).ToArray();

        /// <summary>Fills the empty button pressure fields (discussion #476)
        /// of every device on a slot from that device's default mapping under
        /// <paramref name="profileId"/>. Only those ten.
        ///
        /// <returns>True when a field was filled, so the caller merges the
        /// slot's set only when there is something to merge.</returns></summary>
        public static bool FillEmptyPressureMappingsForSlot(int padIndex, string profileId)
            => FillEmptyPlayStationMappingsForSlot(padIndex, null, profileId, MappingSetMigrator.PressureTargets);

        /// <summary>Fills, for a live change from one PlayStation preset to
        /// another, the empty fields of every device on a slot that the new
        /// preset's default maps and the outgoing preset's default leaves
        /// empty: the touchpad, Mic Mute and Edge rows a DualShock 3 preset
        /// withheld at assign, and the DualShock 3 Full's pressure rows. A
        /// field the outgoing default mapped and the user cleared on that
        /// wire stays cleared, and every other binding stays as the user left
        /// it.
        ///
        /// <returns>True when a field was filled, so the caller merges the
        /// slot's set only when there is something to merge.</returns></summary>
        public static bool FillEmptyPresetMappingsForSlot(int padIndex, string fromProfileId, string toProfileId)
            => FillEmptyPlayStationMappingsForSlot(padIndex, fromProfileId, toProfileId, PresetGatedPlayStationTargets);

        /// <summary>Snapshot under the UserSettings lock and resolve devices
        /// outside it, the order <see cref="FillEmptyAutoMappingsForSlot"/>
        /// gives for the same reason. A null <paramref name="fromProfileId"/>
        /// excludes nothing.</summary>
        private static bool FillEmptyPlayStationMappingsForSlot(int padIndex, string fromProfileId,
            string toProfileId, System.Collections.Generic.IReadOnlyList<string> targets)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return false;
            bool any = false;

            var slot = new System.Collections.Generic.List<UserSetting>();
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us.MapTo == padIndex) slot.Add(us);
            }

            foreach (var us in slot)
            {
                var ud = SettingsManager.FindDeviceByInstanceGuid(us.InstanceGuid);
                var ps = us.GetPadSetting();
                if (ud == null || ps == null) continue;
                // A Bliss-Box port with no placement answers no pressure yet,
                // so the ten rows are owed until it can be placed, filled
                // where empty then.
                if (PadForge.Common.Input.BlissBoxRuntime.DefaultOwed(ud)
                    && targets.Any(MappingSetMigrator.IsPressureTarget))
                    us.BlissBoxOwed |= BlissBoxOwedMapping.Pressure;
                var fresh = SettingsManager.CreateDefaultPadSetting(ud,
                    Engine.VirtualControllerType.PlayStation, toProfileId);
                if (fresh == null) continue;
                var outgoing = fromProfileId == null ? null
                    : SettingsManager.CreateDefaultPadSetting(ud, Engine.VirtualControllerType.PlayStation, fromProfileId);
                bool changed = false;
                foreach (var target in targets)
                {
                    var prop = typeof(PadSetting).GetProperty(target);
                    if (prop == null || !string.IsNullOrEmpty(prop.GetValue(ps) as string)) continue;
                    string value = prop.GetValue(fresh) as string;
                    if (string.IsNullOrEmpty(value)) continue;
                    if (outgoing != null && !string.IsNullOrEmpty(prop.GetValue(outgoing) as string)) continue;
                    prop.SetValue(ps, value);
                    changed = true;
                }
                if (!changed) continue;
                ps.UpdateChecksum();
                us.PadSettingChecksum = ps.PadSettingChecksum;
                any = true;
            }
            return any;
        }

        /// <summary>
        /// Completes what a Bliss-Box port owes each slot it is assigned to
        /// (<see cref="UserSetting.BlissBoxOwed"/>), now that its controls can
        /// be placed: through the controller identified in a port read raw
        /// (<paramref name="map"/>), or through SDL's gamepad mapping once a
        /// port read that way connects (<paramref name="map"/> null). The
        /// default fills the slot's empty fields, the pressure request only
        /// the ten pressure fields, and neither touches a target the user
        /// bound, recorded or cleared while it was owed
        /// (<see cref="UserSetting.BlissBoxKeptTargets"/>). The slot ends as
        /// an assignment made with the controller in the port would have left
        /// it, with the user's edits on top.
        ///
        /// <para>The request ends either way. A default with nothing left to
        /// fill, or a pressure request on a slot that no longer carries
        /// pressure, is not asked again. The caller flushes the grids' pending
        /// edits first, so a binding recorded a moment ago is in the
        /// setting.</para>
        /// </summary>
        /// <param name="slotShape">A slot's output type, profile and Extended
        /// layout, from its view model.</param>
        /// <param name="mapped">True when a slot's mapping changed, so the
        /// caller merges the sets and reloads the grids.</param>
        /// <returns>True when anything the port owed changed, so the caller
        /// saves.</returns>
        public static bool AutoMapIdentifiedPort(UserDevice ud, PadForge.Engine.Common.BlissBox.BlissBoxGamepadMap map,
            Func<int, (Engine.VirtualControllerType Type, string ProfileId, ViewModels.ExtendedSlotConfig Extended)> slotShape,
            out bool mapped)
        {
            mapped = false;
            if (ud == null || slotShape == null) return false;

            bool any = false;
            foreach (var us in AssignedSettings(ud))
            {
                var owed = us.BlissBoxOwed;
                if (owed == BlissBoxOwedMapping.None) continue;
                var kept = us.BlissBoxKeptTargets;
                us.BlissBoxOwed = BlissBoxOwedMapping.None;
                us.BlissBoxKeptTargets = null;
                any = true;

                var (type, profileId, extended) = slotShape(us.MapTo);
                var fresh = SettingsManager.CreateDefaultPadSetting(ud, type, profileId, extended, map);
                if (fresh == null) continue;
                var existing = us.GetPadSetting();
                if (existing == null)
                {
                    // Every owed assignment carries one. An empty one keeps
                    // the request to its own fields.
                    existing = new PadSetting();
                    existing.UpdateChecksum();
                    us.SetPadSetting(existing);
                }
                bool changed = (owed & BlissBoxOwedMapping.Default) != 0
                    ? MergeEmptyFrom(existing, fresh, kept)
                    : FillEmptyTargets(existing, fresh, MappingSetMigrator.PressureTargets, kept);
                us.PadSettingChecksum = existing.PadSettingChecksum;
                mapped |= changed;
            }
            return any;
        }

        /// <summary>True when the port owes a slot it is assigned to anything
        /// (<see cref="UserSetting.BlissBoxOwed"/>), the slots
        /// <see cref="AutoMapIdentifiedPort"/> completes. The caller asks
        /// before flushing the grids' edits.</summary>
        public static bool PortHasOwedMapping(UserDevice ud)
        {
            if (ud == null) return false;
            foreach (var us in AssignedSettings(ud))
                if (us.BlissBoxOwed != BlissBoxOwedMapping.None) return true;
            return false;
        }

        /// <summary>The device's slot assignments, snapshot under the
        /// UserSettings lock.</summary>
        private static System.Collections.Generic.List<UserSetting> AssignedSettings(UserDevice ud)
        {
            var slots = new System.Collections.Generic.List<UserSetting>();
            var settings = SettingsManager.UserSettings;
            if (settings == null) return slots;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us.InstanceGuid == ud.InstanceGuid && us.MapTo >= 0) slots.Add(us);
            }
            return slots;
        }

        /// <summary>The user bound, recorded or cleared a row of
        /// <paramref name="slot"/>, so whatever a Bliss-Box port still owes
        /// the slot leaves the row's targets as the user left them. Every port
        /// on the slot keeps them, whichever device the row names: the user's
        /// pick replaces the row's source, a port's default included. A shift
        /// layer's rows carry no default, so an edit there keeps
        /// nothing.</summary>
        /// <returns>True when a port's kept targets changed, so the caller
        /// saves.</returns>
        public static bool KeepAuthoredRow(int slot, string layer, string target, string negTarget)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null || slot < 0) return false;
            if (!string.IsNullOrEmpty(layer) && !string.Equals(layer, "Base", StringComparison.Ordinal)) return false;
            bool changed = false;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null || us.MapTo != slot || us.BlissBoxOwed == BlissBoxOwedMapping.None) continue;
                    var kept = new System.Collections.Generic.List<string>(us.BlissBoxKeptTargets ?? Array.Empty<string>());
                    int before = kept.Count;
                    foreach (var t in new[] { target, negTarget })
                        if (!string.IsNullOrEmpty(t) && !kept.Contains(t)) kept.Add(t);
                    if (kept.Count == before) continue;
                    us.BlissBoxKeptTargets = kept.ToArray();
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>The user replaced or cleared the slot's whole routing
        /// (Clear All, a paste, Copy From), so no Bliss-Box port owes the slot
        /// anything after.</summary>
        /// <returns>True when anything owed was dropped, so the caller
        /// saves.</returns>
        public static bool CancelOwedMappingForSlot(int slot)
            => CancelOwedMappings(us => us.MapTo == slot);

        /// <summary>Force Raw Joystick Mode went on for the device. Its
        /// mapping is manual from then on, so nothing its port owed a slot is
        /// filled.</summary>
        public static bool CancelOwedMappingForDevice(Guid instanceGuid)
            => CancelOwedMappings(us => us.InstanceGuid == instanceGuid);

        /// <summary>A paste or Copy From replaced one assignment's
        /// setting.</summary>
        public static bool CancelOwedMapping(UserSetting us)
        {
            if (us == null || (us.BlissBoxOwed == BlissBoxOwedMapping.None && us.BlissBoxKeptTargets == null))
                return false;
            us.BlissBoxOwed = BlissBoxOwedMapping.None;
            us.BlissBoxKeptTargets = null;
            return true;
        }

        private static bool CancelOwedMappings(Predicate<UserSetting> match)
        {
            var settings = SettingsManager.UserSettings;
            if (settings == null) return false;
            bool changed = false;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                    if (us != null && match(us)) changed |= CancelOwedMapping(us);
            }
            return changed;
        }

        /// <summary>Fills each of <paramref name="targets"/> that
        /// <paramref name="existingPs"/> leaves empty, outside
        /// <paramref name="keep"/>, from <paramref name="freshPs"/>.</summary>
        /// <returns>True when a field was filled.</returns>
        private static bool FillEmptyTargets(PadSetting existingPs, PadSetting freshPs,
            System.Collections.Generic.IEnumerable<string> targets, string[] keep)
        {
            bool changed = false;
            foreach (var target in targets)
            {
                if (keep != null && Array.IndexOf(keep, target) >= 0) continue;
                var prop = typeof(PadSetting).GetProperty(target);
                if (prop == null || !string.IsNullOrEmpty(prop.GetValue(existingPs) as string)) continue;
                string value = prop.GetValue(freshPs) as string;
                if (string.IsNullOrEmpty(value)) continue;
                prop.SetValue(existingPs, value);
                changed = true;
            }
            if (changed) existingPs.UpdateChecksum();
            return changed;
        }

        private static void FillEmptyAutoMappingsIfApplicable(PadSetting existingPs,
            UserDevice ud, Engine.VirtualControllerType outputType, string profileId = null)
        {
            if (existingPs == null || ud == null) return;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"FILLAUTO guid={ud.InstanceGuid.ToString().Substring(0, 8)} type={outputType}"
                // All three dictionary siblings (lens 1r): the merge below
                // handles Raw, Midi, and Kbm, so the pre-count must too.
                + $" preRows={(existingPs.RawMappingEntries?.Length ?? 0)
                    + (existingPs.MidiMappingEntries?.Length ?? 0)
                    + (existingPs.KbmMappingEntries?.Length ?? 0)}");

            var freshPs = SettingsManager.CreateDefaultPadSetting(ud, outputType, profileId);
            if (freshPs == null) return;
            MergeEmptyFrom(existingPs, freshPs);
        }

        /// <summary>Copies onto every field <paramref name="existingPs"/>
        /// leaves empty what <paramref name="freshPs"/> sets there, the
        /// mapping dictionaries included. Every field it already sets stays,
        /// and so does every target in <paramref name="keep"/>.</summary>
        /// <returns>True when a field was filled.</returns>
        private static bool MergeEmptyFrom(PadSetting existingPs, PadSetting freshPs, string[] keep = null)
        {
            bool Kept(string target) => keep != null && Array.IndexOf(keep, target) >= 0;
            bool changed = false;

            // Raw-surface automap (Nintendo): the positional defaults live
            // in the Extended mapping dictionary, which the string-property
            // reflection walk below cannot see. Merge missing keys first,
            // same fill-empty semantics: user-authored entries win.
            var freshExt = freshPs.RawMappingEntries;
            if (freshExt != null)
            {
                bool extChanged = false;
                foreach (var entry in freshExt)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Key) || Kept(entry.Key)) continue;
                    if (!string.IsNullOrEmpty(existingPs.GetRawMapping(entry.Key))) continue;
                    existingPs.SetRawMapping(entry.Key, entry.Value);
                    extChanged = true;
                }
                if (extChanged) existingPs.FlushRawMappings();
                changed |= extChanged;
            }

            // MIDI and KBM automap surfaces are dictionary siblings of the
            // raw surface and equally invisible to the reflection walk.
            // Same fill-empty semantics.
            var freshMidi = freshPs.MidiMappingEntries;
            if (freshMidi != null)
            {
                bool midiChanged = false;
                foreach (var entry in freshMidi)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Key) || Kept(entry.Key)) continue;
                    if (!string.IsNullOrEmpty(existingPs.GetMidiMapping(entry.Key))) continue;
                    existingPs.SetMidiMapping(entry.Key, entry.Value);
                    midiChanged = true;
                }
                if (midiChanged) existingPs.FlushMidiMappings();
                changed |= midiChanged;
            }
            var freshKbm = freshPs.KbmMappingEntries;
            if (freshKbm != null)
            {
                bool kbmChanged = false;
                foreach (var entry in freshKbm)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Key) || Kept(entry.Key)) continue;
                    if (!string.IsNullOrEmpty(existingPs.GetKbmMapping(entry.Key))) continue;
                    existingPs.SetKbmMapping(entry.Key, entry.Value);
                    kbmChanged = true;
                }
                if (kbmChanged) existingPs.FlushKbmMappings();
                changed |= kbmChanged;
            }
            var freshVr = freshPs.VrMappingEntries;
            if (freshVr != null)
            {
                bool vrChanged = false;
                foreach (var entry in freshVr)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Key) || Kept(entry.Key)) continue;
                    if (!string.IsNullOrEmpty(existingPs.GetVrMapping(entry.Key))) continue;
                    existingPs.SetVrMapping(entry.Key, entry.Value);
                    vrChanged = true;
                }
                if (vrChanged) existingPs.FlushVrMappings();
                changed |= vrChanged;
            }

            // Walk every copyable string mapping property and fill empty
            // ones on existingPs from freshPs. Reflection here mirrors the
            // pattern PadSetting.CopyFrom uses internally.
            var type = typeof(PadSetting);
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(string)) continue;
                if (!prop.CanRead || !prop.CanWrite) continue;
                if (prop.Name == nameof(PadSetting.PadSettingChecksum)) continue;
                if (Kept(prop.Name)) continue;
                string current = prop.GetValue(existingPs) as string;
                if (!string.IsNullOrEmpty(current)) continue;        // user-authored or already set
                string fresh = prop.GetValue(freshPs) as string;
                if (string.IsNullOrEmpty(fresh)) continue;           // auto-map didn't set this field
                prop.SetValue(existingPs, fresh);
                changed = true;
            }
            existingPs.UpdateChecksum();
            return changed;
        }

        /// <summary>The highest index+1 this device can legitimately carry on
        /// each input family, for the "is this descriptor past the device's
        /// inventory" test below.
        ///
        /// <para>Both counts prefer the RAW figure, and the axis side has to.
        /// CapAxeCount stopped being an upper bound the moment the axis list
        /// went sparse: it now carries how MANY slots a pad populates, not how
        /// high they go. A PlayStation Move Navigation records CapAxeCount 10
        /// against a highest real slot of 15, so its L1 and d-pad pressure
        /// axes read as out of inventory. That verdict does not merely warn.
        /// It strips the device from every slot and replaces the user's
        /// PadSetting with a fresh auto-map, and since the fresh map
        /// whitelists what it produces, the rows destroyed were exactly the
        /// hand-authored ones. CapButtonCount has the same sparse shape (a
        /// DualSense records 13 with a highest index of 16) and was already
        /// safe for this reason.</para>
        ///
        /// <para>Zero means unknown rather than empty, so the caller declines
        /// to guess. A device PadForge has never seen online has no raw counts
        /// and falls back to the cap ones.</para></summary>
        internal static (int Buttons, int Axes, int Povs) InventoryBounds(UserDevice ud)
        {
            if (ud == null) return (0, 0, 0);
            return (ud.RawButtonCount > 0 ? ud.RawButtonCount : ud.CapButtonCount,
                    ud.RawAxisCount > 0 ? ud.RawAxisCount : ud.CapAxeCount,
                    ud.CapPovCount);
        }

        /// <summary>
        /// True when <paramref name="existingPs"/> carries a standard mapping
        /// descriptor that points at a button / axis / POV index this device
        /// doesn't physically expose. That means the PadSetting was authored for
        /// a DIFFERENT device — a Copy From across device kinds, or a mapping
        /// inherited from another device sharing the slot (e.g. a racing wheel's
        /// raw <c>Button 24</c> / <c>IAxis 3</c> descriptors landing on a
        /// DualSense, whose buttons stop at 21). On (re)assign such a PadSetting
        /// must be re-auto-mapped fresh rather than preserved by the fill-empty
        /// pass, which only touches empty fields and so leaves the foreign
        /// descriptors in place (the "assigning my DualSense to a wheel's slot
        /// only maps the Share button" report).
        ///
        /// <para>Only Gamepads have a canonical auto-map, so the check is gated
        /// to them; wheels / joysticks keep their recorded mapping. Descriptors
        /// the device's OWN fresh auto-map produces are whitelisted, so a clean
        /// gamepad mapping is never flagged even when its auto-mapped indices sit
        /// near the device's reported counts.</para>
        /// </summary>
        private static bool IsForeignPadSetting(PadSetting existingPs, UserDevice ud,
            Engine.VirtualControllerType outputType, string profileId = null)
        {
            if (existingPs == null || ud == null) return false;
            if (ud.CapType != InputDeviceType.Gamepad) return false;
            // A port read raw here with no placement gets an empty default,
            // and its cached CapType is the last session's, so there is
            // nothing to judge the setting against.
            if (PadForge.Common.Input.BlissBoxRuntime.ReadsRawHere(ud)
                && PadForge.Common.Input.BlissBoxRuntime.GamepadMapFor(ud) == null) return false;

            var (buttons, axes, povs) = InventoryBounds(ud);
            if (buttons <= 0 && axes <= 0 && povs <= 0) return false; // unknown inventory — don't guess

            var fresh = SettingsManager.CreateDefaultPadSetting(ud, outputType, profileId);
            var freshSet = new System.Collections.Generic.HashSet<string>(
                fresh != null ? fresh.GetAllMappingDescriptors() : new System.Collections.Generic.List<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var desc in existingPs.GetAllMappingDescriptors())
            {
                foreach (var rawPart in desc.Split('|'))
                {
                    string part = rawPart.Trim();
                    if (part.Length == 0 || freshSet.Contains(part)) continue;
                    if (!TryParseInputRef(part, out char kind, out int idx)) continue;
                    switch (kind)
                    {
                        case 'b': if (buttons > 0 && idx >= buttons) return true; break;
                        case 'a': if (axes    > 0 && idx >= axes)    return true; break;
                        case 'p': if (povs    > 0 && idx >= povs)    return true; break;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Parses a single input descriptor (after splitting compound '|' lists)
        /// into its kind ('b' button, 'a' axis, 'p' POV) and index. Mirrors the
        /// engine's <c>ParseDescriptor</c> prefix handling: an optional
        /// invert/half marker (<c>I</c>, <c>H</c>, <c>IH</c>) precedes the type
        /// word. Slider / motion / touchpad / unknown descriptors return false.
        /// </summary>
        private static bool TryParseInputRef(string s, out char kind, out int index)
        {
            kind = '\0';
            index = -1;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();

            if (s.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            else if ((s.StartsWith("I", StringComparison.OrdinalIgnoreCase)
                      || s.StartsWith("H", StringComparison.OrdinalIgnoreCase))
                     && s.Length > 1 && !char.IsDigit(s[1])
                     && !PadForge.Engine.Common.Mapping.SourceCoercion.IsPrefixExemptDescriptor(s))
                s = s.Substring(1);

            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            switch (parts[0].ToLowerInvariant())
            {
                case "axis":   kind = 'a'; break;
                case "button": kind = 'b'; break;
                case "pov":    kind = 'p'; break;
                default: return false; // slider / unknown — not index-validated here
            }
            return int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out index);
        }

        // ─────────────────────────────────────────────
        //  Auto-enable hiding defaults
        // ─────────────────────────────────────────────

        /// <summary>
        /// Sets default input hiding options when a device is newly assigned to a slot.
        /// Gamepads: HidHide auto-ON (if installed). Keyboards and mice get
        /// nothing turned on, because consuming someone's only keyboard or
        /// mouse locks them out of Windows.
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
