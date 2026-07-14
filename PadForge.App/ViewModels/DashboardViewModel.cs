using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel for the Dashboard page. Shows an at-a-glance overview
    /// of all 16 controller slots, engine status, connected devices, and
    /// HIDMaestro / HidHide driver status.
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
            OnPropertyChanged(nameof(HidHideStatusText));
            OnPropertyChanged(nameof(MidiServicesStatusText));
            OnPropertyChanged(nameof(TouchpadOverlayStatus));
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

            // Re-apply the focus selection to (possibly rebuilt) summaries so a
            // structural refresh never drops the selected card's glow.
            foreach (var s in SlotSummaries)
                s.IsSelected = s.PadIndex == _selectedPadIndex;

            ShowAddController = canAddMore;
        }

        private int _selectedPadIndex = -1;

        /// <summary>Marks the slot whose pad page is in focus so its card wears
        /// the persistent selection glow. -1 clears it. Remembered so a later
        /// RefreshActiveSlots rebuild re-applies the flag to fresh summaries.</summary>
        public void SetSelectedPad(int padIndex)
        {
            _selectedPadIndex = padIndex;
            foreach (var s in SlotSummaries)
                s.IsSelected = s.PadIndex == padIndex;
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

        private RelayCommand _resetDsuPortCommand;
        public RelayCommand ResetDsuPortCommand =>
            _resetDsuPortCommand ??= new RelayCommand(() => DsuMotionServerPort = 26760);

        private string _dsuServerStatus = Strings.Instance.Common_Stopped;

        /// <summary>Current status of the DSU server for UI display.</summary>
        public string DsuServerStatus
        {
            get => _dsuServerStatus;
            set => SetProperty(ref _dsuServerStatus, value ?? Strings.Instance.Common_Stopped);
        }

        private bool _isDsuServerRunning;

        /// <summary>Serving truth for the DSU card flame (#175 phase 2 item
        /// 2): tracks the actual server lifecycle, not the enable checkbox.
        /// Set by InputService at the same points that write
        /// <see cref="DsuServerStatus"/>, so flame and status text agree.</summary>
        public bool IsDsuServerRunning
        {
            get => _isDsuServerRunning;
            set => SetProperty(ref _isDsuServerRunning, value);
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

        private RelayCommand _resetWebPortCommand;
        public RelayCommand ResetWebPortCommand =>
            _resetWebPortCommand ??= new RelayCommand(() => WebControllerPort = 8080);

        private string _webControllerStatus = Strings.Instance.Common_Stopped;

        /// <summary>Current status of the web controller server for UI display.</summary>
        public string WebControllerStatus
        {
            get => _webControllerStatus;
            set => SetProperty(ref _webControllerStatus, value ?? Strings.Instance.Common_Stopped);
        }

        private bool _isWebControllerRunning;

        /// <summary>Serving truth for the Web Controller card flame (#175
        /// phase 2 item 2): actual server lifecycle, not the enable checkbox.
        /// Set by InputService alongside <see cref="WebControllerStatus"/>.</summary>
        public bool IsWebControllerRunning
        {
            get => _isWebControllerRunning;
            set => SetProperty(ref _isWebControllerRunning, value);
        }

        private int _webControllerClientCount;

        /// <summary>Number of currently connected web controller clients.</summary>
        public int WebControllerClientCount
        {
            get => _webControllerClientCount;
            set => SetProperty(ref _webControllerClientCount, value);
        }

        // ─────────────────────────────────────────────
        //  Remote Link (issue #138)
        // ─────────────────────────────────────────────

        /// <summary>The Settings view model, which holds the Remote Link peer manager +
        /// identity-protection state. Set once at startup so the Dashboard's Remote Link
        /// section can show paired peers, identity mode, and nearby PCs in one place.</summary>
        public SettingsViewModel RemoteLink { get; set; }

        private bool _enableRemoteLink;

        /// <summary>Whether the Remote Link server is listening for paired peers.</summary>
        public bool EnableRemoteLink
        {
            get => _enableRemoteLink;
            set => SetProperty(ref _enableRemoteLink, value);
        }

        private bool _autoReconnect = true;

        /// <summary>Auto-reconnect: when a paired PC appears on the LAN, establish the link
        /// without a click (issue #138). Default on.</summary>
        public bool AutoReconnect
        {
            get => _autoReconnect;
            set => SetProperty(ref _autoReconnect, value);
        }

        private int _remoteLinkPort = 27500;

        /// <summary>TCP control + UDP data port for the Remote Link server.</summary>
        public int RemoteLinkPort
        {
            get => _remoteLinkPort;
            set => SetProperty(ref _remoteLinkPort, Math.Clamp(value, 1024, 65535));
        }

        private RelayCommand _resetRemoteLinkPortCommand;
        public RelayCommand ResetRemoteLinkPortCommand =>
            _resetRemoteLinkPortCommand ??= new RelayCommand(() => RemoteLinkPort = 27500);

        private string _remoteLinkStatus = Strings.Instance.Common_Stopped;

        /// <summary>Current Remote Link server status for UI display.</summary>
        public string RemoteLinkStatus
        {
            get => _remoteLinkStatus;
            set => SetProperty(ref _remoteLinkStatus, value ?? Strings.Instance.Common_Stopped);
        }

        private bool _isRemoteLinkRunning;

        /// <summary>Serving truth for the Remote Link card flame (#175 phase
        /// 2 item 2): true only while the link server object is live. The
        /// status text can carry identity-unlock errors while the server
        /// never started, so the flame keys on this, not on the text or the
        /// enable checkbox. Set by InputService.</summary>
        public bool IsRemoteLinkRunning
        {
            get => _isRemoteLinkRunning;
            set => SetProperty(ref _isRemoteLinkRunning, value);
        }

        private string _remoteLinkConnectHost = "";

        /// <summary>Host (or host:port) the user types to initiate an outbound pairing.</summary>
        public string RemoteLinkConnectHost
        {
            get => _remoteLinkConnectHost;
            set => SetProperty(ref _remoteLinkConnectHost, value);
        }

        /// <summary>Raised when the user asks to connect/pair with a typed host[:port].</summary>
        public event Action<string> ConnectToPeerRequested;

        private RelayCommand _connectToPeerCommand;
        public RelayCommand ConnectToPeerCommand =>
            _connectToPeerCommand ??= new RelayCommand(() =>
            {
                if (!string.IsNullOrWhiteSpace(_remoteLinkConnectHost))
                    ConnectToPeerRequested?.Invoke(_remoteLinkConnectHost.Trim());
            });

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

        private bool _enableMenuOverlay = true;

        /// <summary>Whether the radial / touch menu overlay renders while
        /// a menu is engaged (#9 B-17). Default on; menus still hover and
        /// commit blind when off (the runtime never depends on the
        /// window).</summary>
        public bool EnableMenuOverlay
        {
            get => _enableMenuOverlay;
            set => SetProperty(ref _enableMenuOverlay, value);
        }

        private double _touchpadOverlayOpacity = 0.25;

        /// <summary>Surface opacity of the touchpad overlay (0.0–1.0).</summary>
        public double TouchpadOverlayOpacity
        {
            get => _touchpadOverlayOpacity;
            set
            {
                if (SetProperty(ref _touchpadOverlayOpacity, Math.Clamp(value, 0.0, 1.0)))
                    OnPropertyChanged(nameof(TouchpadOverlayOpacityPercent));
            }
        }

        /// <summary>Opacity as 0–100 integer percentage for NumberBox binding.</summary>
        public int TouchpadOverlayOpacityPercent
        {
            get => (int)Math.Round(_touchpadOverlayOpacity * 100);
            set => TouchpadOverlayOpacity = value / 100.0;
        }

        private RelayCommand _resetOpacityCommand;
        public RelayCommand ResetOpacityCommand =>
            _resetOpacityCommand ??= new RelayCommand(() => TouchpadOverlayOpacity = 0.25);

        /// <summary>Raised when the user clicks Reset Position. Handler in
        /// InputService recenters the live overlay (or seeds defaults if not
        /// open) and clears persisted Left/Top.</summary>
        public event EventHandler ResetTouchpadOverlayPositionRequested;

        private RelayCommand _resetTouchpadOverlayPositionCommand;
        public RelayCommand ResetTouchpadOverlayPositionCommand =>
            _resetTouchpadOverlayPositionCommand ??= new RelayCommand(() =>
                ResetTouchpadOverlayPositionRequested?.Invoke(this, EventArgs.Empty));

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

        private bool _isTouchpadOverlayRunning;

        /// <summary>True when the overlay window is currently shown. Drives
        /// the localized TouchpadOverlayStatus text below.</summary>
        public bool IsTouchpadOverlayRunning
        {
            get => _isTouchpadOverlayRunning;
            set
            {
                if (SetProperty(ref _isTouchpadOverlayRunning, value))
                    OnPropertyChanged(nameof(TouchpadOverlayStatus));
            }
        }

        public string TouchpadOverlayStatus =>
            _isTouchpadOverlayRunning ? Strings.Instance.Common_Running : Strings.Instance.Common_Stopped;

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

        private System.Collections.ObjectModel.ObservableCollection<PadViewModel.MappedDeviceInfo> _mappedDevices;

        /// <summary>Live reference to the pad's mapped-device list, so the
        /// crucible card can render a per-device roster with battery glyphs
        /// (#175). Set by InputService; same-reference sets are no-ops.</summary>
        public System.Collections.ObjectModel.ObservableCollection<PadViewModel.MappedDeviceInfo> MappedDevices
        {
            get => _mappedDevices;
            set => SetProperty(ref _mappedDevices, value);
        }

        private string _deviceName = Strings.Instance.Dashboard_NoDevice;

        /// <summary>Name of the primary device mapped to this slot.</summary>
        public string DeviceName
        {
            get => _deviceName;
            set => SetProperty(ref _deviceName, value);
        }

        private string _batteryText = string.Empty;

        /// <summary>Battery of the first mapped device reporting one,
        /// e.g. "78%". Empty otherwise. Cold side (#175, issue #167 lane).</summary>
        public string BatteryText
        {
            get => _batteryText;
            set => SetProperty(ref _batteryText, value ?? string.Empty);
        }

        private bool _isActive;

        /// <summary>Whether this slot has at least one online mapped device.</summary>
        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }

        private bool _isSelected;

        /// <summary>Whether this slot's pad page is the one currently in focus.
        /// Drives the persistent selection glow on the card. Set from
        /// DashboardViewModel.SetSelectedPad on navigation, NOT the same as
        /// IsActive (online device).</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private bool _isVirtualControllerConnected;

        /// <summary>Whether the virtual controller for this slot is connected to its backend.</summary>
        public bool IsVirtualControllerConnected
        {
            get => _isVirtualControllerConnected;
            set
            {
                if (SetProperty(ref _isVirtualControllerConnected, value))
                    OnPropertyChanged(nameof(StatusText));
            }
        }

        private bool _isInitializing;

        /// <summary>Whether the virtual controller for this slot is currently initializing.</summary>
        public bool IsInitializing
        {
            get => _isInitializing;
            set
            {
                if (SetProperty(ref _isInitializing, value))
                    OnPropertyChanged(nameof(StatusText));
            }
        }

        private int _mappedDeviceCount;

        /// <summary>Number of devices mapped to this slot.</summary>
        public int MappedDeviceCount
        {
            get => _mappedDeviceCount;
            set
            {
                if (SetProperty(ref _mappedDeviceCount, value))
                {
                    OnPropertyChanged(nameof(HasMappedDevices));
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        /// <summary>True when at least one device is mapped to this slot.
        /// Ember heat gating (#175): cards with zero mappings stay cold
        /// (no ember rim, no glow, steel seg tile) even when enabled.</summary>
        public bool HasMappedDevices => _mappedDeviceCount > 0;

        private int _connectedDeviceCount;

        /// <summary>Number of mapped devices that are currently connected.</summary>
        public int ConnectedDeviceCount
        {
            get => _connectedDeviceCount;
            set
            {
                if (SetProperty(ref _connectedDeviceCount, value))
                    OnPropertyChanged(nameof(StatusText));
            }
        }

        private string _statusText = Strings.Instance.Common_Idle;

        /// <summary>Status text for the slot (e.g., "Active", "Cold",
        /// "Awaiting Devices", "Disabled"). Ember vocabulary (#175): an
        /// enabled slot with zero mappings reads "Cold"; mapped but with
        /// nothing connected reads as awaiting devices ONLY once the live
        /// VC is gone. While the VC survives the inactivity grace (60 s
        /// default), a device dropout must not flap the card to awaiting:
        /// the VC teardown is the status transition, exactly like the nav
        /// flame (the contract a churning Wii chain exposed, 2026-07-11).
        /// Other states pass through whatever the engine refresh
        /// assigned.</summary>
        public string StatusText
        {
            get
            {
                if (_isEnabled && !_isInitializing)
                {
                    if (_mappedDeviceCount == 0)
                        return Strings.Instance.Dashboard_StatusCold;
                    if (_connectedDeviceCount == 0 && !_isVirtualControllerConnected)
                        return Strings.Instance.Main_AwaitingDevices;
                }
                return _statusText;
            }
            set => SetProperty(ref _statusText, value);
        }

        private bool _isEnabled = true;

        /// <summary>Whether this virtual controller slot is enabled for output.</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (SetProperty(ref _isEnabled, value))
                    OnPropertyChanged(nameof(StatusText));
            }
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

        private VirtualControllerType _outputType = VirtualControllerType.Xbox;

        /// <summary>The virtual controller output type for this slot.</summary>
        public VirtualControllerType OutputType
        {
            get => _outputType;
            set => SetProperty(ref _outputType, value);
        }

        /// <summary>Pipeline stage ledger (#175 item 10): one entry per
        /// configuration stage the slot's assigned devices actually have
        /// (sticks / triggers / gyro / lighting / touchpad / audio), in
        /// that order. Rebuilt by InputService.RefreshSlotStageLedgers on
        /// its 1 s slow lane; entries mutate in place when membership is
        /// unchanged so the card doesn't re-template every refresh.</summary>
        public ObservableCollection<SlotStageInfo> StageLedger { get; } = new();
    }

    /// <summary>
    /// One stage entry in a slot card's pipeline heat ledger (#175 item
    /// 10). Carries the tab's glyph (font char for gyro / lighting /
    /// touchpad / audio; the Zacksly stick / trigger shapes for the two
    /// shape stages), the ember/ashen heat flag, and a mono summary of
    /// the real non-default values for the hover tooltip.
    /// </summary>
    public class SlotStageInfo : ObservableObject
    {
        public SlotStageInfo(string kind, string glyph, bool isStickShape = false, bool isTriggerShape = false)
        {
            Kind = kind;
            Glyph = glyph;
            IsStickShape = isStickShape;
            IsTriggerShape = isTriggerShape;
        }

        /// <summary>Stable stage identity ("Sticks".."Audio"). Membership
        /// comparisons key on this so in-place updates are possible.</summary>
        public string Kind { get; }

        /// <summary>Segoe font glyph for the font stages (same characters
        /// as the matching tab page headers). Empty for shape stages.</summary>
        public string Glyph { get; }

        /// <summary>True for the Sticks entry. Rendered with the Zacksly
        /// stick ring + disc geometries instead of a font glyph.</summary>
        public bool IsStickShape { get; }

        /// <summary>True for the Triggers entry. Rendered with the
        /// Zacksly trigger body geometry instead of a font glyph.</summary>
        public bool IsTriggerShape { get; }

        private bool _isHot;

        /// <summary>True when the stage carries a non-default
        /// configuration on any of the slot's assigned devices. Ember
        /// when hot, ashen steel when inert.</summary>
        public bool IsHot
        {
            get => _isHot;
            set => SetProperty(ref _isHot, value);
        }

        private string _summary = string.Empty;

        /// <summary>Composite change key for the tooltip readout (all
        /// lines joined). Empty when the stage is inert, which disables
        /// the tooltip. The rendered content is <see cref="SummaryLines"/>;
        /// this string exists so the 1 s refresh only touches the line
        /// collection when something actually changed.</summary>
        public string Summary
        {
            get => _summary;
            set
            {
                if (SetProperty(ref _summary, value ?? string.Empty))
                    OnPropertyChanged(nameof(HasSummary));
            }
        }

        /// <summary>Tooltip gate: no tooltip on inert stages.</summary>
        public bool HasSummary => !string.IsNullOrEmpty(_summary);

        /// <summary>Per-device readout lines for the hover tooltip, in
        /// binding order: every assigned device the stage covers gets a
        /// line (devices still at defaults read STOCK), so multi-device
        /// slots attribute each value to its device the same way the
        /// preview annotation readout does.</summary>
        public ObservableCollection<StageSummaryLine> SummaryLines { get; } = new();
    }

    /// <summary>One tooltip line of a stage's readout: device-class
    /// glyph, Body-face device name, then mono value tokens. Name sits
    /// BETWEEN glyph and tokens (user direction 2026-07-06): icon, name,
    /// settings, the same order as the card's main device label. The name
    /// carries its trailing "  ·  " separator and is empty for slot-level
    /// lines like the audio master volume.</summary>
    public class StageSummaryLine
    {
        public string DeviceGlyph { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string Tokens { get; set; } = string.Empty;
    }
}
