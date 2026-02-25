using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for a single device row in the Devices page grid.
    /// Displays device identification, status, and basic capability info.
    /// </summary>
    public class DeviceRowViewModel : ObservableObject
    {
        // ─────────────────────────────────────────────
        //  Identity
        // ─────────────────────────────────────────────

        private Guid _instanceGuid;

        /// <summary>Unique instance GUID for this device.</summary>
        public Guid InstanceGuid
        {
            get => _instanceGuid;
            set => SetProperty(ref _instanceGuid, value);
        }

        private string _deviceName = string.Empty;

        /// <summary>Display name of the device.</summary>
        public string DeviceName
        {
            get => _deviceName;
            set => SetProperty(ref _deviceName, value);
        }

        private string _productName = string.Empty;

        /// <summary>Product name of the device.</summary>
        public string ProductName
        {
            get => _productName;
            set => SetProperty(ref _productName, value);
        }

        private Guid _productGuid;

        /// <summary>Product GUID in PIDVID format.</summary>
        public Guid ProductGuid
        {
            get => _productGuid;
            set => SetProperty(ref _productGuid, value);
        }

        // ─────────────────────────────────────────────
        //  USB identification
        // ─────────────────────────────────────────────

        private ushort _vendorId;

        /// <summary>USB Vendor ID.</summary>
        public ushort VendorId
        {
            get => _vendorId;
            set
            {
                if (SetProperty(ref _vendorId, value))
                    OnPropertyChanged(nameof(VendorIdHex));
            }
        }

        private ushort _productId;

        /// <summary>USB Product ID.</summary>
        public ushort ProductId
        {
            get => _productId;
            set
            {
                if (SetProperty(ref _productId, value))
                    OnPropertyChanged(nameof(ProductIdHex));
            }
        }

        /// <summary>Vendor ID as a hex string (e.g., "045E").</summary>
        public string VendorIdHex => _vendorId.ToString("X4");

        /// <summary>Product ID as a hex string (e.g., "028E").</summary>
        public string ProductIdHex => _productId.ToString("X4");

        // ─────────────────────────────────────────────
        //  Status
        // ─────────────────────────────────────────────

        private bool _isOnline;

        /// <summary>Whether the device is currently connected.</summary>
        public bool IsOnline
        {
            get => _isOnline;
            set
            {
                if (SetProperty(ref _isOnline, value))
                    OnPropertyChanged(nameof(StatusText));
            }
        }

        private bool _isEnabled = true;

        /// <summary>Whether the device is enabled for mapping.</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (SetProperty(ref _isEnabled, value))
                    OnPropertyChanged(nameof(StatusText));
            }
        }

        private bool _isHidden;

        /// <summary>Whether the device is hidden from the UI.</summary>
        public bool IsHidden
        {
            get => _isHidden;
            set => SetProperty(ref _isHidden, value);
        }

        /// <summary>Status text for display.</summary>
        public string StatusText
        {
            get
            {
                if (!_isEnabled) return "Disabled";
                if (_isOnline) return "Online";
                return "Offline";
            }
        }

        // ─────────────────────────────────────────────
        //  Capabilities
        // ─────────────────────────────────────────────

        private int _axisCount;

        /// <summary>Number of axes on the device.</summary>
        public int AxisCount
        {
            get => _axisCount;
            set => SetProperty(ref _axisCount, value);
        }

        private int _buttonCount;

        /// <summary>Number of buttons on the device.</summary>
        public int ButtonCount
        {
            get => _buttonCount;
            set => SetProperty(ref _buttonCount, value);
        }

        private int _povCount;

        /// <summary>Number of POV hat switches on the device.</summary>
        public int PovCount
        {
            get => _povCount;
            set => SetProperty(ref _povCount, value);
        }

        private string _deviceType = string.Empty;

        /// <summary>Device type description (e.g., "Gamepad", "Flight Stick", "Wheel").</summary>
        public string DeviceType
        {
            get => _deviceType;
            set => SetProperty(ref _deviceType, value);
        }

        private bool _hasRumble;

        /// <summary>Whether the device supports rumble vibration.</summary>
        public bool HasRumble
        {
            get => _hasRumble;
            set => SetProperty(ref _hasRumble, value);
        }

        private bool _hasGyro;

        /// <summary>Whether the device has a gyroscope sensor.</summary>
        public bool HasGyro
        {
            get => _hasGyro;
            set => SetProperty(ref _hasGyro, value);
        }

        private bool _hasAccel;

        /// <summary>Whether the device has an accelerometer sensor.</summary>
        public bool HasAccel
        {
            get => _hasAccel;
            set => SetProperty(ref _hasAccel, value);
        }

        // ─────────────────────────────────────────────
        //  Slot assignment
        // ─────────────────────────────────────────────

        private int _assignedSlot = -1;

        /// <summary>
        /// The pad slot this device is assigned to (0–3), or -1 if unassigned.
        /// </summary>
        public int AssignedSlot
        {
            get => _assignedSlot;
            set
            {
                if (SetProperty(ref _assignedSlot, value))
                    OnPropertyChanged(nameof(AssignedSlotText));
            }
        }

        /// <summary>Display text for the slot assignment.</summary>
        public string AssignedSlotText =>
            _assignedSlot >= 0 ? $"Player {_assignedSlot + 1}" : "Unassigned";

        // ─────────────────────────────────────────────
        //  Device path
        // ─────────────────────────────────────────────

        private string _devicePath = string.Empty;

        /// <summary>File system device path (for diagnostics).</summary>
        public string DevicePath
        {
            get => _devicePath;
            set => SetProperty(ref _devicePath, value);
        }

        // ─────────────────────────────────────────────
        //  Display
        // ─────────────────────────────────────────────

        /// <summary>Capabilities summary string for display.</summary>
        public string CapabilitiesSummary =>
            $"{_axisCount} axes, {_buttonCount} buttons, {_povCount} POV" +
            (_hasRumble ? ", Rumble" : "") +
            (_hasGyro ? ", Gyro" : "") +
            (_hasAccel ? ", Accel" : "");

        /// <summary>
        /// Refreshes computed display properties.
        /// </summary>
        public void NotifyDisplayChanged()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(AssignedSlotText));
            OnPropertyChanged(nameof(CapabilitiesSummary));
            OnPropertyChanged(nameof(VendorIdHex));
            OnPropertyChanged(nameof(ProductIdHex));
        }

        public override string ToString()
        {
            return $"{_deviceName} [{StatusText}]";
        }
    }
}
