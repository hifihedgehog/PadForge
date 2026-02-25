using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
}
