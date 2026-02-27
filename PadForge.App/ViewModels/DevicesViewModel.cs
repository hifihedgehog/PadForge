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
        //  Raw state display (structured, for selected device)
        // ─────────────────────────────────────────────

        /// <summary>Structured axis values for visual display (progress bars).</summary>
        public ObservableCollection<AxisDisplayItem> RawAxes { get; } = new();

        /// <summary>Structured button states for visual display (circles).</summary>
        public ObservableCollection<ButtonDisplayItem> RawButtons { get; } = new();

        /// <summary>Structured POV hat values for visual display (compass).</summary>
        public ObservableCollection<PovDisplayItem> RawPovs { get; } = new();

        private int _selectedButtonTotal;

        /// <summary>Total number of buttons on the selected device.</summary>
        public int SelectedButtonTotal
        {
            get => _selectedButtonTotal;
            set => SetProperty(ref _selectedButtonTotal, value);
        }

        private bool _hasRawData;

        /// <summary>Whether raw state data is available for the selected device.</summary>
        public bool HasRawData
        {
            get => _hasRawData;
            set => SetProperty(ref _hasRawData, value);
        }

        // Gyroscope / Accelerometer individual values

        private bool _hasGyroData;
        public bool HasGyroData { get => _hasGyroData; set => SetProperty(ref _hasGyroData, value); }

        private bool _hasAccelData;
        public bool HasAccelData { get => _hasAccelData; set => SetProperty(ref _hasAccelData, value); }

        private double _gyroX, _gyroY, _gyroZ;
        public double GyroX { get => _gyroX; set => SetProperty(ref _gyroX, value); }
        public double GyroY { get => _gyroY; set => SetProperty(ref _gyroY, value); }
        public double GyroZ { get => _gyroZ; set => SetProperty(ref _gyroZ, value); }

        private double _accelX, _accelY, _accelZ;
        public double AccelX { get => _accelX; set => SetProperty(ref _accelX, value); }
        public double AccelY { get => _accelY; set => SetProperty(ref _accelY, value); }
        public double AccelZ { get => _accelZ; set => SetProperty(ref _accelZ, value); }

        /// <summary>Tracks which device's collections are currently populated.</summary>
        internal Guid LastRawStateDeviceGuid { get; set; }

        /// <summary>
        /// Rebuilds the raw state collections for a new device with the given capabilities.
        /// </summary>
        internal void RebuildRawStateCollections(int axisCount, int buttonCount, int povCount)
        {
            RawAxes.Clear();
            for (int i = 0; i < axisCount; i++)
                RawAxes.Add(new AxisDisplayItem { Index = i, Name = $"Axis {i}" });

            RawButtons.Clear();
            for (int i = 0; i < buttonCount; i++)
                RawButtons.Add(new ButtonDisplayItem { Index = i });

            RawPovs.Clear();
            for (int i = 0; i < povCount; i++)
                RawPovs.Add(new PovDisplayItem { Index = i });

            SelectedButtonTotal = buttonCount;
        }

        /// <summary>Clears all raw state display data.</summary>
        internal void ClearRawState()
        {
            RawAxes.Clear();
            RawButtons.Clear();
            RawPovs.Clear();
            HasRawData = false;
            HasGyroData = false;
            HasAccelData = false;
            LastRawStateDeviceGuid = Guid.Empty;
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

    /// <summary>Visual display item for a single axis value.</summary>
    public class AxisDisplayItem : ObservableObject
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;

        private double _normalizedValue;
        /// <summary>Axis value normalized to 0.0–1.0 range.</summary>
        public double NormalizedValue
        {
            get => _normalizedValue;
            set => SetProperty(ref _normalizedValue, value);
        }

        private int _rawValue;
        /// <summary>Raw axis value (0–65535).</summary>
        public int RawValue
        {
            get => _rawValue;
            set => SetProperty(ref _rawValue, value);
        }
    }

    /// <summary>Visual display item for a single button state.</summary>
    public class ButtonDisplayItem : ObservableObject
    {
        public int Index { get; set; }

        private bool _isPressed;
        /// <summary>Whether the button is currently pressed.</summary>
        public bool IsPressed
        {
            get => _isPressed;
            set => SetProperty(ref _isPressed, value);
        }
    }

    /// <summary>Visual display item for a single POV hat switch.</summary>
    public class PovDisplayItem : ObservableObject
    {
        public int Index { get; set; }

        private int _centidegrees = -1;
        /// <summary>POV value in centidegrees (0–35900), or -1 for centered.</summary>
        public int Centidegrees
        {
            get => _centidegrees;
            set
            {
                if (SetProperty(ref _centidegrees, value))
                {
                    OnPropertyChanged(nameof(IsCentered));
                    OnPropertyChanged(nameof(AngleDegrees));
                }
            }
        }

        /// <summary>Whether the POV is centered (no direction).</summary>
        public bool IsCentered => _centidegrees < 0;

        /// <summary>Direction in degrees (0–359) for rotation transforms.</summary>
        public double AngleDegrees => _centidegrees >= 0 ? _centidegrees / 100.0 : 0;
    }
}
