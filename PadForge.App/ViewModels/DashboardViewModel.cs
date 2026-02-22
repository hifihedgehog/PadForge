using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for the Dashboard page. Shows an at-a-glance overview
    /// of all 4 controller slots, engine status, connected devices, and
    /// ViGEmBus driver status.
    /// </summary>
    public partial class DashboardViewModel : ViewModelBase
    {
        public DashboardViewModel()
        {
            Title = "Dashboard";

            // Create slot summary entries for each of the 4 pads.
            for (int i = 0; i < 4; i++)
            {
                SlotSummaries.Add(new SlotSummary(i));
            }
        }

        // ─────────────────────────────────────────────
        //  Slot summaries
        // ─────────────────────────────────────────────

        /// <summary>
        /// Summary information for each of the 4 virtual controller slots.
        /// Displayed as cards on the Dashboard page.
        /// </summary>
        public ObservableCollection<SlotSummary> SlotSummaries { get; } =
            new ObservableCollection<SlotSummary>();

        // ─────────────────────────────────────────────
        //  Engine status
        // ─────────────────────────────────────────────

        private string _engineStatus = "Stopped";

        /// <summary>
        /// Current engine status text: "Running", "Stopped", etc.
        /// </summary>
        public string EngineStatus
        {
            get => _engineStatus;
            set => SetProperty(ref _engineStatus, value);
        }

        private double _pollingFrequency;

        /// <summary>
        /// Current polling frequency in Hz.
        /// </summary>
        public double PollingFrequency
        {
            get => _pollingFrequency;
            set
            {
                if (SetProperty(ref _pollingFrequency, value))
                    OnPropertyChanged(nameof(PollingFrequencyText));
            }
        }

        /// <summary>
        /// Formatted polling frequency string for display (e.g., "987.3 Hz").
        /// </summary>
        public string PollingFrequencyText =>
            PollingFrequency > 0 ? $"{PollingFrequency:F1} Hz" : "—";

        // ─────────────────────────────────────────────
        //  Device counts
        // ─────────────────────────────────────────────

        private int _totalDevices;

        /// <summary>Total number of detected input devices (online + offline).</summary>
        public int TotalDevices
        {
            get => _totalDevices;
            set => SetProperty(ref _totalDevices, value);
        }

        private int _onlineDevices;

        /// <summary>Number of currently connected (online) input devices.</summary>
        public int OnlineDevices
        {
            get => _onlineDevices;
            set => SetProperty(ref _onlineDevices, value);
        }

        private int _mappedDevices;

        /// <summary>Number of devices that have an active mapping to a pad slot.</summary>
        public int MappedDevices
        {
            get => _mappedDevices;
            set => SetProperty(ref _mappedDevices, value);
        }

        // ─────────────────────────────────────────────
        //  ViGEmBus status
        // ─────────────────────────────────────────────

        private bool _isViGEmInstalled;

        /// <summary>Whether ViGEmBus is installed.</summary>
        public bool IsViGEmInstalled
        {
            get => _isViGEmInstalled;
            set
            {
                if (SetProperty(ref _isViGEmInstalled, value))
                    OnPropertyChanged(nameof(ViGEmStatusText));
            }
        }

        /// <summary>Display text for ViGEmBus status.</summary>
        public string ViGEmStatusText => IsViGEmInstalled ? "Installed" : "Not Installed";

        private string _vigemVersion = string.Empty;

        /// <summary>ViGEmBus driver version string (if installed).</summary>
        public string ViGEmVersion
        {
            get => _vigemVersion;
            set => SetProperty(ref _vigemVersion, value);
        }

        // ─────────────────────────────────────────────
        //  HidHide status
        // ─────────────────────────────────────────────

        private bool _isHidHideInstalled;

        /// <summary>Whether HidHide is installed.</summary>
        public bool IsHidHideInstalled
        {
            get => _isHidHideInstalled;
            set
            {
                if (SetProperty(ref _isHidHideInstalled, value))
                    OnPropertyChanged(nameof(HidHideStatusText));
            }
        }

        /// <summary>Display text for HidHide status.</summary>
        public string HidHideStatusText => IsHidHideInstalled ? "Installed" : "Not Installed";

        // ─────────────────────────────────────────────
        //  XInput library info
        // ─────────────────────────────────────────────

        private string _xinputLibraryInfo = string.Empty;

        /// <summary>Information about the loaded XInput DLL.</summary>
        public string XInputLibraryInfo
        {
            get => _xinputLibraryInfo;
            set => SetProperty(ref _xinputLibraryInfo, value);
        }
    }

    /// <summary>
    /// Summary information for a single virtual controller slot,
    /// displayed as a card on the Dashboard page.
    /// </summary>
    public class SlotSummary : ObservableObject
    {
        public SlotSummary(int padIndex)
        {
            PadIndex = padIndex;
            SlotLabel = $"Player {padIndex + 1}";
        }

        /// <summary>Zero-based pad slot index (0–3).</summary>
        public int PadIndex { get; }

        /// <summary>Display label (e.g., "Player 1").</summary>
        public string SlotLabel { get; }

        private string _deviceName = "No device";

        /// <summary>Name of the primary device mapped to this slot.</summary>
        public string DeviceName
        {
            get => _deviceName;
            set => SetProperty(ref _deviceName, value);
        }

        private bool _isActive;

        /// <summary>Whether this slot has at least one online mapped device.</summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        private bool _isVirtualControllerConnected;

        /// <summary>Whether the ViGEm virtual controller for this slot is connected.</summary>
        public bool IsVirtualControllerConnected
        {
            get => _isVirtualControllerConnected;
            set => SetProperty(ref _isVirtualControllerConnected, value);
        }

        private int _mappedDeviceCount;

        /// <summary>Number of devices mapped to this slot.</summary>
        public int MappedDeviceCount
        {
            get => _mappedDeviceCount;
            set => SetProperty(ref _mappedDeviceCount, value);
        }

        private int _connectedDeviceCount;

        /// <summary>Number of mapped devices that are currently connected.</summary>
        public int ConnectedDeviceCount
        {
            get => _connectedDeviceCount;
            set => SetProperty(ref _connectedDeviceCount, value);
        }

        private string _statusText = "Idle";

        /// <summary>Status text for the slot (e.g., "Active", "Idle", "No mapping").</summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }
    }
}
