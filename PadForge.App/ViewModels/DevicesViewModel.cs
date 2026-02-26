using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Common.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for the Devices page. Shows a list of all detected input
    /// devices (online and offline) and raw input state for the selected device.
    /// </summary>
    public partial class DevicesViewModel : ViewModelBase
    {
        public DevicesViewModel()
        {
            Title = "Devices";
        }

        // ─────────────────────────────────────────────
        //  Device list
        // ─────────────────────────────────────────────

        /// <summary>
        /// Collection of all known devices. Updated by InputService when
        /// the device list changes.
        /// </summary>
        public ObservableCollection<DeviceRowViewModel> Devices { get; } =
            new ObservableCollection<DeviceRowViewModel>();

        private DeviceRowViewModel _selectedDevice;

        /// <summary>
        /// The currently selected device in the device list.
        /// When selected, its raw input state is shown in the detail panel.
        /// </summary>
        public DeviceRowViewModel SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
                {
                    OnPropertyChanged(nameof(HasSelectedDevice));
                    _assignToSlotCommand?.NotifyCanExecuteChanged();
                    _removeDeviceCommand?.NotifyCanExecuteChanged();
                    RefreshSlotButtons();
                }
            }
        }

        /// <summary>Whether a device is currently selected.</summary>
        public bool HasSelectedDevice => _selectedDevice != null;

        // ─────────────────────────────────────────────
        //  Device counts
        // ─────────────────────────────────────────────

        private int _totalCount;

        /// <summary>Total number of detected devices.</summary>
        public int TotalCount
        {
            get => _totalCount;
            set => SetProperty(ref _totalCount, value);
        }

        private int _onlineCount;

        /// <summary>Number of currently connected devices.</summary>
        public int OnlineCount
        {
            get => _onlineCount;
            set => SetProperty(ref _onlineCount, value);
        }

        // ─────────────────────────────────────────────
        //  Raw state display (for selected device)
        // ─────────────────────────────────────────────

        private string _rawAxisDisplay = string.Empty;

        /// <summary>Formatted string showing all axis values for the selected device.</summary>
        public string RawAxisDisplay
        {
            get => _rawAxisDisplay;
            set => SetProperty(ref _rawAxisDisplay, value);
        }

        private string _rawButtonDisplay = string.Empty;

        /// <summary>Formatted string showing all button states for the selected device.</summary>
        public string RawButtonDisplay
        {
            get => _rawButtonDisplay;
            set => SetProperty(ref _rawButtonDisplay, value);
        }

        private string _rawPovDisplay = string.Empty;

        /// <summary>Formatted string showing all POV hat values for the selected device.</summary>
        public string RawPovDisplay
        {
            get => _rawPovDisplay;
            set => SetProperty(ref _rawPovDisplay, value);
        }

        private string _rawGyroDisplay = string.Empty;

        /// <summary>Formatted string showing gyroscope sensor data (rad/s).</summary>
        public string RawGyroDisplay
        {
            get => _rawGyroDisplay;
            set => SetProperty(ref _rawGyroDisplay, value);
        }

        private string _rawAccelDisplay = string.Empty;

        /// <summary>Formatted string showing accelerometer sensor data (m/s²).</summary>
        public string RawAccelDisplay
        {
            get => _rawAccelDisplay;
            set => SetProperty(ref _rawAccelDisplay, value);
        }

        // ─────────────────────────────────────────────
        //  Commands
        // ─────────────────────────────────────────────

        private RelayCommand _refreshCommand;

        /// <summary>Command to force-refresh the device list.</summary>
        public RelayCommand RefreshCommand =>
            _refreshCommand ??= new RelayCommand(
                () => RefreshRequested?.Invoke(this, EventArgs.Empty));

        private RelayCommand<int> _assignToSlotCommand;

        /// <summary>
        /// Command to assign the selected device to a pad slot.
        /// Parameter is the slot index (0–3).
        /// </summary>
        public RelayCommand<int> AssignToSlotCommand =>
            _assignToSlotCommand ??= new RelayCommand<int>(
                slotIndex => AssignToSlotRequested?.Invoke(this, slotIndex),
                _ => HasSelectedDevice);

        private RelayCommand _hideDeviceCommand;

        /// <summary>Command to hide the selected device from the list.</summary>
        public RelayCommand HideDeviceCommand =>
            _hideDeviceCommand ??= new RelayCommand(
                () =>
                {
                    if (_selectedDevice != null)
                    {
                        _selectedDevice.IsHidden = true;
                        HideDeviceRequested?.Invoke(this, _selectedDevice.InstanceGuid);
                    }
                },
                () => HasSelectedDevice);

        private RelayCommand _removeDeviceCommand;

        /// <summary>
        /// Command to remove the selected device from the device list entirely.
        /// Removes the device record, any associated user settings, and the UI row.
        /// Works for both online and offline devices — removing an online device
        /// will also unassign it from any slot and stop reading its input.
        /// </summary>
        public RelayCommand RemoveDeviceCommand =>
            _removeDeviceCommand ??= new RelayCommand(
                () =>
                {
                    if (_selectedDevice != null)
                    {
                        Guid guid = _selectedDevice.InstanceGuid;
                        Devices.Remove(_selectedDevice);
                        SelectedDevice = null;
                        RemoveDeviceRequested?.Invoke(this, guid);
                        RefreshCounts();
                    }
                },
                () => HasSelectedDevice && _selectedDevice != null);

        /// <summary>Raised when a refresh is requested.</summary>
        public event EventHandler RefreshRequested;

        /// <summary>Raised when the user assigns a device to a slot. Arg = slot index.</summary>
        public event EventHandler<int> AssignToSlotRequested;

        /// <summary>Raised when the user hides a device. Arg = instance GUID.</summary>
        public event EventHandler<Guid> HideDeviceRequested;

        /// <summary>Raised when the user removes a device. Arg = instance GUID.</summary>
        public event EventHandler<Guid> RemoveDeviceRequested;

        /// <summary>Raised when the user toggles a slot assignment. Arg = slot index.</summary>
        public event EventHandler<int> ToggleSlotRequested;

        // ─────────────────────────────────────────────
        //  Dynamic slot buttons
        // ─────────────────────────────────────────────

        /// <summary>
        /// Dynamic list of virtual controller slot buttons for device assignment.
        /// Only includes created/active slots.
        /// </summary>
        public ObservableCollection<SlotButtonItem> ActiveSlotItems { get; } = new();

        private RelayCommand<int> _toggleSlotCommand;

        /// <summary>Command to toggle the selected device's assignment to a slot.</summary>
        public RelayCommand<int> ToggleSlotCommand =>
            _toggleSlotCommand ??= new RelayCommand<int>(
                slotIndex =>
                {
                    ToggleSlotRequested?.Invoke(this, slotIndex);
                    RefreshSlotButtons();
                },
                _ => HasSelectedDevice);

        /// <summary>
        /// Rebuilds <see cref="ActiveSlotItems"/> based on which virtual controller
        /// slots are created and whether the selected device is assigned to each.
        /// </summary>
        public void RefreshSlotButtons()
        {
            var activeSlots = new System.Collections.Generic.List<int>();
            for (int i = 0; i < InputManager.MaxPads; i++)
            {
                if (SettingsManager.SlotCreated[i])
                    activeSlots.Add(i);
            }

            // Get the selected device's current assignments.
            var assignedSlots = _selectedDevice != null
                ? SettingsManager.GetAssignedSlots(_selectedDevice.InstanceGuid)
                : new System.Collections.Generic.List<int>();

            // Compute 1-based slot numbers.
            bool structureChanged = activeSlots.Count != ActiveSlotItems.Count;
            if (!structureChanged)
            {
                for (int i = 0; i < activeSlots.Count; i++)
                {
                    if (ActiveSlotItems[i].PadIndex != activeSlots[i])
                    {
                        structureChanged = true;
                        break;
                    }
                }
            }

            if (structureChanged)
            {
                ActiveSlotItems.Clear();
                int num = 0;
                foreach (int slot in activeSlots)
                {
                    num++;
                    ActiveSlotItems.Add(new SlotButtonItem
                    {
                        PadIndex = slot,
                        SlotNumber = num,
                        IsAssigned = assignedSlots.Contains(slot)
                    });
                }
            }
            else
            {
                // Just update IsAssigned on existing items.
                foreach (var item in ActiveSlotItems)
                    item.IsAssigned = assignedSlots.Contains(item.PadIndex);
            }

            _toggleSlotCommand?.NotifyCanExecuteChanged();
        }

        // ─────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds a device row by instance GUID.
        /// </summary>
        public DeviceRowViewModel FindByGuid(Guid instanceGuid)
        {
            foreach (var d in Devices)
            {
                if (d.InstanceGuid == instanceGuid)
                    return d;
            }
            return null;
        }

        /// <summary>
        /// Updates the device counts from the Devices collection.
        /// </summary>
        public void RefreshCounts()
        {
            TotalCount = Devices.Count;
            int online = 0;
            foreach (var d in Devices)
            {
                if (d.IsOnline)
                    online++;
            }
            OnlineCount = online;
        }
    }

    /// <summary>
    /// Represents a single virtual controller slot button on the Devices page.
    /// </summary>
    public class SlotButtonItem : ObservableObject
    {
        private int _padIndex;
        /// <summary>Zero-based pad slot index (0–3).</summary>
        public int PadIndex
        {
            get => _padIndex;
            set => SetProperty(ref _padIndex, value);
        }

        private int _slotNumber;
        /// <summary>1-based slot number among active slots.</summary>
        public int SlotNumber
        {
            get => _slotNumber;
            set => SetProperty(ref _slotNumber, value);
        }

        private bool _isAssigned;
        /// <summary>Whether the currently selected device is assigned to this slot.</summary>
        public bool IsAssigned
        {
            get => _isAssigned;
            set => SetProperty(ref _isAssigned, value);
        }
    }
}
