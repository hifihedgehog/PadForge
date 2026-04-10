using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Engine;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for the Dashboard page. Shows an at-a-glance overview
    /// of all 16 controller slots, engine status, connected devices, and
    /// ViGEmBus driver status.
    /// </summary>
    public partial class DashboardViewModel : ViewModelBase
    {
        public DashboardViewModel()
        {
            Title = Strings.Instance.Dashboard_Title;
            // SlotSummaries starts empty; populated dynamically by RefreshActiveSlots().
        }

        protected override void OnCultureChanged()
        {
            Title = Strings.Instance.Dashboard_Title;
            OnPropertyChanged(nameof(PollingFrequencyText));
            OnPropertyChanged(nameof(ViGEmStatusText));
            OnPropertyChanged(nameof(HidHideStatusText));
            OnPropertyChanged(nameof(VJoyStatusText));
            OnPropertyChanged(nameof(MidiServicesStatusText));
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

            // Update display labels to use sequential global numbering.
            for (int i = 0; i < SlotSummaries.Count; i++)
            {
                var label = string.Format(Strings.Instance.Main_VirtualController_Format, i + 1);
                if (SlotSummaries[i].SlotLabel != label)
                    SlotSummaries[i].SlotLabel = label;
            }

            ShowAddController = canAddMore;
        }

        // ─────────────────────────────────────────────
        //  Engine status
        // ─────────────────────────────────────────────

        private string _engineStatus = Strings.Instance.Common_Stopped;

        /// <summary>
        /// Current engine status text (localized) for display.
        /// </summary>
        public string EngineStatus
        {
            get => _engineStatus;
            set => SetProperty(ref _engineStatus, value);
        }

        private string _engineStateKey = "Stopped";

        /// <summary>
        /// Non-localized engine state key ("Running", "Stopped", "Idle") for XAML DataTriggers.
        /// </summary>
        public string EngineStateKey
        {
            get => _engineStateKey;
            set => SetProperty(ref _engineStateKey, value);
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
            PollingFrequency > 0 ? string.Format(Strings.Instance.Dashboard_PollingHz_Format, PollingFrequency) : Strings.Instance.Dashboard_PollingDash;

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
        public string ViGEmStatusText => IsViGEmInstalled ? Strings.Instance.Common_Installed : Strings.Instance.Common_NotInstalled;

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
        public string HidHideStatusText => IsHidHideInstalled ? Strings.Instance.Common_Installed : Strings.Instance.Common_NotInstalled;

        // ─────────────────────────────────────────────
        //  vJoy status
        // ─────────────────────────────────────────────

        private bool _isVJoyInstalled;

        /// <summary>Whether the vJoy driver is installed.</summary>
        public bool IsVJoyInstalled
        {
            get => _isVJoyInstalled;
            set
            {
                if (SetProperty(ref _isVJoyInstalled, value))
                    OnPropertyChanged(nameof(VJoyStatusText));
            }
        }

        /// <summary>Display text for vJoy status.</summary>
        public string VJoyStatusText => IsVJoyInstalled ? Strings.Instance.Common_Installed : Strings.Instance.Common_NotInstalled;

        // ─────────────────────────────────────────────
        //  Windows MIDI Services status
        // ─────────────────────────────────────────────

        private bool _isMidiServicesInstalled;

        /// <summary>Whether Windows MIDI Services is installed.</summary>
        public bool IsMidiServicesInstalled
        {
            get => _isMidiServicesInstalled;
            set
            {
                if (SetProperty(ref _isMidiServicesInstalled, value))
                    OnPropertyChanged(nameof(MidiServicesStatusText));
            }
        }

        /// <summary>Display text for MIDI Services status.</summary>
        public string MidiServicesStatusText => IsMidiServicesInstalled ? Strings.Instance.Common_Installed : Strings.Instance.Common_NotInstalled;

        // ─────────────────────────────────────────────
        //  DSU Motion Server
        // ─────────────────────────────────────────────

        private bool _enableDsuMotionServer;

        /// <summary>Whether the DSU (cemuhook) motion server is enabled.</summary>
        public bool EnableDsuMotionServer
        {
            get => _enableDsuMotionServer;
            set => SetProperty(ref _enableDsuMotionServer, value);
        }

        private int _dsuMotionServerPort = 26760;

        /// <summary>UDP port for the DSU motion server (default 26760).</summary>
        public int DsuMotionServerPort
        {
            get => _dsuMotionServerPort;
            set => SetProperty(ref _dsuMotionServerPort, Math.Clamp(value, 1024, 65535));
        }

        private string _dsuServerStatus = Strings.Instance.Common_Stopped;

        /// <summary>Current status of the DSU server for UI display.</summary>
        public string DsuServerStatus
        {
            get => _dsuServerStatus;
            set => SetProperty(ref _dsuServerStatus, value ?? Strings.Instance.Common_Stopped);
        }

        // ─────────────────────────────────────────────
        //  Web Controller Server
        // ─────────────────────────────────────────────

        private bool _enableWebController;

        /// <summary>Whether the web controller server is enabled.</summary>
        public bool EnableWebController
        {
            get => _enableWebController;
            set => SetProperty(ref _enableWebController, value);
        }

        private int _webControllerPort = 8080;

        /// <summary>HTTP/WebSocket port for the web controller server (default 8080).</summary>
        public int WebControllerPort
        {
            get => _webControllerPort;
            set => SetProperty(ref _webControllerPort, Math.Clamp(value, 1024, 65535));
        }

        private string _webControllerStatus = Strings.Instance.Common_Stopped;

        /// <summary>Current status of the web controller server for UI display.</summary>
        public string WebControllerStatus
        {
            get => _webControllerStatus;
            set => SetProperty(ref _webControllerStatus, value ?? Strings.Instance.Common_Stopped);
        }

        private int _webControllerClientCount;

        /// <summary>Number of currently connected web controller clients.</summary>
        public int WebControllerClientCount
        {
            get => _webControllerClientCount;
            set => SetProperty(ref _webControllerClientCount, value);
        }

        // ─────────────────────────────────────────────
        //  Touchpad Overlay
        // ─────────────────────────────────────────────

        private bool _enableTouchpadOverlay;

        /// <summary>Whether the touchpad overlay is enabled (visible).</summary>
        public bool EnableTouchpadOverlay
        {
            get => _enableTouchpadOverlay;
            set => SetProperty(ref _enableTouchpadOverlay, value);
        }

        private double _touchpadOverlayOpacity = 0.25;

        /// <summary>Surface opacity of the touchpad overlay (0.0–1.0).</summary>
        public double TouchpadOverlayOpacity
        {
            get => _touchpadOverlayOpacity;
            set
            {
                if (SetProperty(ref _touchpadOverlayOpacity, Math.Clamp(value, 0.05, 1.0)))
                    OnPropertyChanged(nameof(TouchpadOverlayOpacityPercent));
            }
        }

        /// <summary>Opacity as 5–100 integer percentage for NumberBox binding.</summary>
        public int TouchpadOverlayOpacityPercent
        {
            get => (int)Math.Round(_touchpadOverlayOpacity * 100);
            set => TouchpadOverlayOpacity = value / 100.0;
        }

        private int _touchpadOverlayMonitor;

        /// <summary>Monitor index for the touchpad overlay (0 = primary).</summary>
        public int TouchpadOverlayMonitor
        {
            get => _touchpadOverlayMonitor;
            set => SetProperty(ref _touchpadOverlayMonitor, value);
        }

        private double _touchpadOverlayLeft = -1;
        public double TouchpadOverlayLeft
        {
            get => _touchpadOverlayLeft;
            set => SetProperty(ref _touchpadOverlayLeft, value);
        }

        private double _touchpadOverlayTop = -1;
        public double TouchpadOverlayTop
        {
            get => _touchpadOverlayTop;
            set => SetProperty(ref _touchpadOverlayTop, value);
        }

        private double _touchpadOverlayWidth = 500;
        public double TouchpadOverlayWidth
        {
            get => _touchpadOverlayWidth;
            set => SetProperty(ref _touchpadOverlayWidth, Math.Max(150, value));
        }

        private double _touchpadOverlayHeight = 250;
        public double TouchpadOverlayHeight
        {
            get => _touchpadOverlayHeight;
            set => SetProperty(ref _touchpadOverlayHeight, Math.Max(80, value));
        }

        private string _touchpadOverlayStatus = Strings.Instance.Common_Stopped;

        public string TouchpadOverlayStatus
        {
            get => _touchpadOverlayStatus;
            set => SetProperty(ref _touchpadOverlayStatus, value ?? Strings.Instance.Common_Stopped);
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
            SlotLabel = string.Format(Strings.Instance.Main_VirtualController_Format, padIndex + 1);
        }

        /// <summary>Zero-based pad slot index.</summary>
        public int PadIndex { get; }

        private string _slotLabel;
        /// <summary>Display label (e.g., "Virtual Controller 1").</summary>
        public string SlotLabel
        {
            get => _slotLabel;
            set => SetProperty(ref _slotLabel, value);
        }

        private string _deviceName = Strings.Instance.Dashboard_NoDevice;

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

        private bool _isInitializing;

        /// <summary>Whether the virtual controller for this slot is currently initializing.</summary>
        public bool IsInitializing
        {
            get => _isInitializing;
            set => SetProperty(ref _isInitializing, value);
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

        private string _statusText = Strings.Instance.Common_Idle;

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

        private string _typeInstanceLabel = "1";

        /// <summary>Per-type instance number label (e.g., "1", "2").</summary>
        public string TypeInstanceLabel
        {
            get => _typeInstanceLabel;
            set => SetProperty(ref _typeInstanceLabel, value);
        }

        private VirtualControllerType _outputType = VirtualControllerType.Xbox360;

        /// <summary>The virtual controller output type for this slot.</summary>
        public VirtualControllerType OutputType
        {
            get => _outputType;
            set => SetProperty(ref _outputType, value);
        }
    }
}
