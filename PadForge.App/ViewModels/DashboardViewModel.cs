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
            // SlotSummaries starts empty; populated dynamically by RefreshActiveSlots().
        }

        // ─────────────────────────────────────────────
        //  Slot summaries
        // ─────────────────────────────────────────────

        /// <summary>
        /// Summary information for virtual controller slots that have mapped devices.
        /// Displayed as cards on the Dashboard page.
        /// </summary>
        public ObservableCollection<SlotSummary> SlotSummaries { get; } =
            new ObservableCollection<SlotSummary>();

        /// <summary>
        /// Whether the "Add Controller" card should be visible (any controller type has capacity).
        /// </summary>
        private bool _showAddController = true;
        public bool ShowAddController
        {
            get => _showAddController;
            set => SetProperty(ref _showAddController, value);
        }

        /// <summary>
        /// Rebuilds the SlotSummaries to only include slots with mapped devices.
        /// Called from InputService after UpdatePadDeviceInfo().
        /// </summary>
        public void RefreshActiveSlots(System.Collections.Generic.IList<int> activeSlots, bool canAddMore)
        {
            // Check if the set of active slots has changed.
            bool changed = activeSlots.Count != SlotSummaries.Count;
            if (!changed)
            {
                for (int i = 0; i < activeSlots.Count; i++)
                {
                    if (SlotSummaries[i].PadIndex != activeSlots[i])
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
            {
                SlotSummaries.Clear();
                foreach (int slot in activeSlots)
                    SlotSummaries.Add(new SlotSummary(slot));
            }

            ShowAddController = canAddMore;
        }

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
            SlotLabel = $"Virtual Controller {padIndex + 1}";
        }

        /// <summary>Zero-based pad slot index.</summary>
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

        /// <summary>Status text for the slot (e.g., "Active", "Idle", "No mapping", "Disabled").</summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private bool _isEnabled = true;

        /// <summary>Whether this virtual controller slot is enabled for ViGEm output.</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        private int _slotNumber = 1;

        /// <summary>Overall controller number among active slots (1-based).</summary>
        public int SlotNumber
        {
            get => _slotNumber;
            set => SetProperty(ref _slotNumber, value);
        }

        private string _typeInstanceLabel = "#1";

        /// <summary>Per-type instance number label (e.g., "#1", "#2").</summary>
        public string TypeInstanceLabel
        {
            get => _typeInstanceLabel;
            set => SetProperty(ref _typeInstanceLabel, value);
        }

        private bool _isXboxType = true;

        /// <summary>True if Xbox 360 type, false if DualShock 4.</summary>
        public bool IsXboxType
        {
            get => _isXboxType;
            set => SetProperty(ref _isXboxType, value);
        }
    }
}
