using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// ViewModel of the Home page and of the ten Extras pages (#574). Home
    /// shows the engine's state, a card for each virtual controller slot,
    /// the offer for a device not in use and the extras that are on. The
    /// Extras pages hold the settings of the optional features. It keeps
    /// the name it had while the two were one page, the Dashboard.
    /// </summary>
    public partial class DashboardViewModel : ViewModelBase
    {
        public DashboardViewModel()
        {
            Title = Strings.Instance.Home_Title;
            ScreenOverlays.Add(new ScreenOverlayItem(this, 1));
            // SlotSummaries starts empty; populated dynamically by RefreshActiveSlots().
            // Home's line under the engine's state counts them, and Home shows
            // its first page while there are none.
            SlotSummaries.CollectionChanged += (_, e) =>
            {
                FollowSlots(e);
                FollowFirstRun();
            };
        }

        protected override void OnCultureChanged()
        {
            Title = Strings.Instance.Home_Title;
            RefreshHomeText();
            OnPropertyChanged(nameof(PollingFrequencyText));
            RaisePollingRateParts();
            OnPropertyChanged(nameof(TouchpadOverlayStatus));
            foreach (var overlay in ScreenOverlays) overlay.RefreshText();
            // The runtime picker's System Default row kept the previous
            // language's word, the audio mirror picker's old defect. A list
            // nobody has read yet is built in the new language anyway.
            if (_openXrRuntimes != null) RefreshOpenXrRuntimes();
            // The game picker's entries are built from strings (#531).
            foreach (var option in GameTelemetryOptions) option.RaiseDisplayChanged();
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
                // Home's first page is decided once the list is whole.
                _refreshingSlots = true;
                try
                {
                    SlotSummaries.Clear();
                    foreach (int slot in activeSlots)
                        SlotSummaries.Add(new SlotSummary(slot));
                }
                finally { _refreshingSlots = false; }
                FollowFirstRun();
            }

            // Update display labels to use sequential global numbering.
            for (int i = 0; i < SlotSummaries.Count; i++)
            {
                var label = string.Format(Strings.Instance.Main_VirtualController_Format, i + 1);
                if (SlotSummaries[i].SlotLabel != label)
                    SlotSummaries[i].SlotLabel = label;
            }

            // The summaries may have been made again: find the open
            // controller's among them.
            SyncSelectedSlotSummary();

            ShowAddController = canAddMore;
        }

        private int _selectedPadIndex = -1;

        /// <summary>Names the slot whose pad page is open, or -1 for none.
        /// Remembered, so a later RefreshActiveSlots finds that slot's
        /// summary among the ones it makes.</summary>
        public void SetSelectedPad(int padIndex)
        {
            _selectedPadIndex = padIndex;
            SyncSelectedSlotSummary();
        }

        private SlotSummary _selectedSlotSummary;

        /// <summary>The summary of the slot whose pad page is in focus, null
        /// while no pad page is. The pad page's header reads the slot's state
        /// from it (#574): the power button, the state word and the stage
        /// chips, the same values the slot's Home card shows. It follows a
        /// rebuild of the summaries, so the header never holds a summary
        /// that left the list.</summary>
        public SlotSummary SelectedSlotSummary
        {
            get => _selectedSlotSummary;
            private set => SetProperty(ref _selectedSlotSummary, value);
        }

        private void SyncSelectedSlotSummary()
        {
            SlotSummary found = null;
            foreach (var s in SlotSummaries)
            {
                if (s.PadIndex == _selectedPadIndex)
                {
                    found = s;
                    break;
                }
            }
            SelectedSlotSummary = found;
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
            set
            {
                if (SetProperty(ref _engineStateKey, value))
                {
                    OnPropertyChanged(nameof(IsEngineOn));
                    FollowEngineState();
                }
            }
        }

        /// <summary>Whether the engine is on, idle and stopping included: the
        /// state the engine button reports to a screen reader, where its
        /// flame shows it on screen.</summary>
        public bool IsEngineOn => _engineStateKey != "Stopped";

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
                {
                    OnPropertyChanged(nameof(PollingFrequencyText));
                    RaisePollingRateParts();
                }
            }
        }

        /// <summary>
        /// Formatted polling frequency string for display (e.g., "987.3 Hz").
        /// </summary>
        public string PollingFrequencyText =>
            PollingFrequency > 0 ? string.Format(Strings.Instance.Dashboard_PollingHz_Format, PollingFrequency) : Strings.Instance.Dashboard_PollingDash;

        // ─────────────────────────────────────────────
        //  Device count
        // ─────────────────────────────────────────────

        private int _onlineDevices;

        /// <summary>Number of currently connected (online) input devices.
        /// Home's engine card prints it.</summary>
        public int OnlineDevices
        {
            get => _onlineDevices;
            set => SetProperty(ref _onlineDevices, value);
        }

        // ─────────────────────────────────────────────
        //  HidHide status
        // ─────────────────────────────────────────────

        private bool _isHidHideInstalled;

        /// <summary>Whether HidHide is installed. The Dashboard's driver
        /// status strip is gone (Settings carries the full HidHide card), so
        /// this flag has no display text of its own here.</summary>
        public bool IsHidHideInstalled
        {
            get => _isHidHideInstalled;
            set => SetProperty(ref _isHidHideInstalled, value);
        }

        // ─────────────────────────────────────────────
        //  Windows MIDI Services status
        // ─────────────────────────────────────────────

        private bool _isMidiAvailable;

        /// <summary>Whether a MIDI API is available: the in-box API, the
        /// App SDK runtime where that is installed, else the legacy API. Gates
        /// the MIDI slot type button in the code-behind. The status text
        /// lives on the Settings card.</summary>
        public bool IsMidiAvailable
        {
            get => _isMidiAvailable;
            set => SetProperty(ref _isMidiAvailable, value);
        }

        private bool _isSteamVrInstalled;

        /// <summary>Whether SteamVR is installed (gates the VR slot type,
        /// issue #49). Kept current by MainWindow's periodic status refresh
        /// alongside the MIDI availability flag. The tiered SteamVR status row
        /// (#287) lives on the Settings card, which reads the live statics
        /// itself.</summary>
        public bool IsSteamVrInstalled
        {
            get => _isSteamVrInstalled;
            set => SetProperty(ref _isSteamVrInstalled, value);
        }

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
        //  Game Mods (#529)
        // ─────────────────────────────────────────────
        // The UDP server DSX game mods send to, a local server with a port
        // like the Motion Server above, so the same card.

        private bool _enableGameModServer;

        /// <summary>Whether the server for DSX game mods listens.</summary>
        public bool EnableGameModServer
        {
            get => _enableGameModServer;
            set => SetProperty(ref _enableGameModServer, value);
        }

        private int _gameModServerPort = Services.GameModServer.DefaultPort;

        /// <summary>The server's UDP port (default 6969, DSX's).</summary>
        public int GameModServerPort
        {
            get => _gameModServerPort;
            set => SetProperty(ref _gameModServerPort, Math.Clamp(value, 1024, 65535));
        }

        private RelayCommand _resetGameModPortCommand;
        public RelayCommand ResetGameModPortCommand =>
            _resetGameModPortCommand ??= new RelayCommand(() => GameModServerPort = Services.GameModServer.DefaultPort);

        private bool _gameModDsxStandIn;

        /// <summary>Whether a stand-in named DSX.exe runs while the server
        /// listens, for the mods that turn themselves off without one.</summary>
        public bool GameModDsxStandIn
        {
            get => _gameModDsxStandIn;
            set => SetProperty(ref _gameModDsxStandIn, value);
        }

        private string _gameModServerStatus = Strings.Instance.Common_Stopped;

        /// <summary>The server's line: why it is not listening, or who is
        /// sending to which virtual controller.</summary>
        public string GameModServerStatus
        {
            get => _gameModServerStatus;
            set => SetProperty(ref _gameModServerStatus, value ?? Strings.Instance.Common_Stopped);
        }

        private bool _isGameModServerRunning;

        /// <summary>Serving truth for the card's flame: the server listens,
        /// whatever the switch says.</summary>
        public bool IsGameModServerRunning
        {
            get => _isGameModServerRunning;
            set => SetProperty(ref _isGameModServerRunning, value);
        }

        // ─────────────────────────────────────────────
        //  Game Telemetry (#531)
        // ─────────────────────────────────────────────
        // A UDP listener for the telemetry driving games send, plus Assetto
        // Corsa's shared memory, surfaced as the Game Telemetry row. The
        // setters mirror into GameTelemetryRuntime, the head tracking card's
        // shape, so a change lands whoever writes it. The switch, the port,
        // the game, the forward port and Full Speed ride profiles. The custom
        // formats are one list for every profile.

        private bool _enableGameTelemetry;

        /// <summary>Whether the Game Telemetry row listens.</summary>
        public bool EnableGameTelemetry
        {
            get => _enableGameTelemetry;
            set
            {
                if (SetProperty(ref _enableGameTelemetry, value))
                    PadForge.Common.Input.Telemetry.GameTelemetryRuntime.Enabled = value;
            }
        }

        private int _gameTelemetryPort = PadForge.Common.Input.Telemetry.GameTelemetryRuntime.DefaultPort;

        /// <summary>The UDP port the games send to (default 20777).</summary>
        public int GameTelemetryPort
        {
            get => _gameTelemetryPort;
            set
            {
                int v = Math.Clamp(value, 1024, 65535);
                if (SetProperty(ref _gameTelemetryPort, v))
                    PadForge.Common.Input.Telemetry.GameTelemetryRuntime.Port = v;
            }
        }

        private RelayCommand _resetGameTelemetryPortCommand;
        public RelayCommand ResetGameTelemetryPortCommand =>
            _resetGameTelemetryPortCommand ??= new RelayCommand(() =>
                GameTelemetryPort = PadForge.Common.Input.Telemetry.GameTelemetryRuntime.DefaultPort);

        private PadForge.Common.Input.Telemetry.GameTelemetryFormat _gameTelemetryFormat;

        /// <summary>The game picked, or Auto. The user's pick also moves the
        /// port to and from a sender's fixed one (<see cref="PickGameTelemetry"/>).</summary>
        public PadForge.Common.Input.Telemetry.GameTelemetryFormat GameTelemetryFormat
        {
            get => _gameTelemetryFormat;
            set
            {
                if (!SetProperty(ref _gameTelemetryFormat, value)) return;
                PadForge.Common.Input.Telemetry.GameTelemetryRuntime.Format = value;
                OnPropertyChanged(nameof(SelectedGameTelemetryOption));
                OnPropertyChanged(nameof(GameTelemetryReadsUdp));
            }
        }

        /// <summary>False while Assetto Corsa is picked: the row reads its
        /// shared memory alone and opens no socket, so the port and the
        /// forward port are off.</summary>
        public bool GameTelemetryReadsUdp
            => PadForge.Common.Input.Telemetry.TelemetryReader.FormatReadsUdp(_gameTelemetryFormat);

        private string _gameTelemetryCustomFormat = string.Empty;

        /// <summary>The custom format read while the game is Custom.</summary>
        public string GameTelemetryCustomFormat
        {
            get => _gameTelemetryCustomFormat;
            set
            {
                if (!SetProperty(ref _gameTelemetryCustomFormat, value ?? string.Empty)) return;
                PadForge.Common.Input.Telemetry.GameTelemetryRuntime.CustomFormatName = _gameTelemetryCustomFormat;
                OnPropertyChanged(nameof(SelectedGameTelemetryOption));
            }
        }

        /// <summary>Auto, each game family, then each custom format.</summary>
        public ObservableCollection<GameTelemetryOption> GameTelemetryOptions { get; } = BuildGameTelemetryOptions(null);

        /// <summary>The picker's selection, the pair of the game and the
        /// custom format's name.</summary>
        public GameTelemetryOption SelectedGameTelemetryOption
        {
            get
            {
                foreach (var option in GameTelemetryOptions)
                    if (option.Matches(_gameTelemetryFormat, _gameTelemetryCustomFormat)) return option;
                return GameTelemetryOptions.Count > 0 ? GameTelemetryOptions[0] : null;
            }
            set
            {
                if (value == null) return;
                PickGameTelemetry(value.Format, value.CustomName);
            }
        }

        private RelayCommand _resetGameTelemetryGameCommand;
        public RelayCommand ResetGameTelemetryGameCommand =>
            _resetGameTelemetryGameCommand ??= new RelayCommand(() =>
                PickGameTelemetry(PadForge.Common.Input.Telemetry.GameTelemetryFormat.Auto, string.Empty));

        /// <summary>The user's pick, from the picker or its reset. Project
        /// CARS 2 and the ETS2 plugin cannot change their port, so picking
        /// either moves the port to theirs, and leaving either puts the
        /// default back while the port is still theirs. A port the player
        /// typed after the pick stays. A load or a profile applies its saved
        /// port as saved (<see cref="ApplyGameTelemetrySettings"/>).</summary>
        private void PickGameTelemetry(PadForge.Common.Input.Telemetry.GameTelemetryFormat format, string customName)
        {
            int leftPort = FixedTelemetryPort(_gameTelemetryFormat);
            GameTelemetryCustomFormat = customName;
            GameTelemetryFormat = format;
            int fixedPort = FixedTelemetryPort(format);
            if (fixedPort != 0)
                GameTelemetryPort = fixedPort;
            else if (leftPort != 0 && _gameTelemetryPort == leftPort)
                GameTelemetryPort = PadForge.Common.Input.Telemetry.GameTelemetryRuntime.DefaultPort;
        }

        /// <summary>The port a sender is fixed to, or 0 for one whose port
        /// the player sets.</summary>
        private static int FixedTelemetryPort(PadForge.Common.Input.Telemetry.GameTelemetryFormat format) => format switch
        {
            PadForge.Common.Input.Telemetry.GameTelemetryFormat.ProjectCars2 => PadForge.Common.Input.Telemetry.GameTelemetryRuntime.ProjectCars2Port,
            PadForge.Common.Input.Telemetry.GameTelemetryFormat.TruckSimulator => PadForge.Common.Input.Telemetry.GameTelemetryRuntime.TruckSimulatorPort,
            _ => 0,
        };

        private int _gameTelemetryForwardPort;

        /// <summary>The port every packet is copied to on 127.0.0.1, 0 for none.</summary>
        public int GameTelemetryForwardPort
        {
            get => _gameTelemetryForwardPort;
            set
            {
                int v = Math.Clamp(value, 0, 65535);
                if (SetProperty(ref _gameTelemetryForwardPort, v))
                    PadForge.Common.Input.Telemetry.GameTelemetryRuntime.ForwardPort = v;
            }
        }

        private RelayCommand _resetGameTelemetryForwardPortCommand;
        public RelayCommand ResetGameTelemetryForwardPortCommand =>
            _resetGameTelemetryForwardPortCommand ??= new RelayCommand(() => GameTelemetryForwardPort = 0);

        private int _gameTelemetryFullSpeed = PadForge.Common.Input.Telemetry.GameTelemetryRuntime.DefaultFullSpeedKmh;

        /// <summary>The speed, in km/h, that moves the Speed axis all the way.</summary>
        public int GameTelemetryFullSpeed
        {
            get => _gameTelemetryFullSpeed;
            set
            {
                int v = Math.Clamp(value, 10, 1000);
                if (SetProperty(ref _gameTelemetryFullSpeed, v))
                    PadForge.Common.Input.Telemetry.GameTelemetryRuntime.FullSpeedKmh = v;
            }
        }

        private RelayCommand _resetGameTelemetryFullSpeedCommand;
        public RelayCommand ResetGameTelemetryFullSpeedCommand =>
            _resetGameTelemetryFullSpeedCommand ??= new RelayCommand(() =>
                GameTelemetryFullSpeed = PadForge.Common.Input.Telemetry.GameTelemetryRuntime.DefaultFullSpeedKmh);

        private System.Collections.Generic.List<PadForge.Common.Input.Telemetry.CustomTelemetryFormat> _gameTelemetryFormats = new();

        /// <summary>The custom formats. Setting them rebuilds the picker and
        /// reopens the row, whose axes follow the variables.</summary>
        public System.Collections.Generic.List<PadForge.Common.Input.Telemetry.CustomTelemetryFormat> GameTelemetryFormats
        {
            get => _gameTelemetryFormats;
            set
            {
                _gameTelemetryFormats = value ?? new();
                PadForge.Common.Input.Telemetry.GameTelemetryRuntime.Formats = _gameTelemetryFormats.ToArray();
                // Each variable field carries the axis its name keeps, which
                // the settings file saves with the formats.
                PadForge.Common.Input.Telemetry.GameTelemetryRuntime.WriteAxes(_gameTelemetryFormats);
                // The expression variables' picker lists the variables.
                TelemetryChannelOptions.Refresh();
                var rebuilt = BuildGameTelemetryOptions(_gameTelemetryFormats);
                GameTelemetryOptions.Clear();
                foreach (var option in rebuilt) GameTelemetryOptions.Add(option);
                // A deleted or renamed format leaves nothing to read, so the
                // picker goes back to Auto-Detect rather than show a game
                // that is not listening.
                if (_gameTelemetryFormat == PadForge.Common.Input.Telemetry.GameTelemetryFormat.Custom)
                {
                    bool found = false;
                    foreach (var option in GameTelemetryOptions)
                        if (option.Matches(_gameTelemetryFormat, _gameTelemetryCustomFormat)) { found = true; break; }
                    if (!found)
                    {
                        GameTelemetryCustomFormat = string.Empty;
                        GameTelemetryFormat = PadForge.Common.Input.Telemetry.GameTelemetryFormat.Auto;
                    }
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedGameTelemetryOption));
            }
        }

        /// <summary>Applies a saved game, port, forward port and Full Speed,
        /// from the settings file or a profile, the switch excepted (its
        /// caller sets it last). A custom game whose format is gone reads as
        /// Auto-Detect, and an out-of-range number keeps the current value,
        /// the port rule every other card's load follows.</summary>
        public void ApplyGameTelemetrySettings(PadForge.Common.Input.Telemetry.GameTelemetryFormat format,
            string customName, int port, int forwardPort, int fullSpeed)
        {
            if (!Enum.IsDefined(format)) format = PadForge.Common.Input.Telemetry.GameTelemetryFormat.Auto;
            bool known = format != PadForge.Common.Input.Telemetry.GameTelemetryFormat.Custom;
            if (!known)
                foreach (var option in GameTelemetryOptions)
                    if (option.Matches(format, customName)) { known = true; break; }
            GameTelemetryCustomFormat = known && format == PadForge.Common.Input.Telemetry.GameTelemetryFormat.Custom
                ? customName ?? string.Empty : string.Empty;
            GameTelemetryFormat = known ? format : PadForge.Common.Input.Telemetry.GameTelemetryFormat.Auto;
            if (port >= 1024 && port <= 65535) GameTelemetryPort = port;
            if (forwardPort >= 0 && forwardPort <= 65535) GameTelemetryForwardPort = forwardPort;
            if (fullSpeed >= 10 && fullSpeed <= 1000) GameTelemetryFullSpeed = fullSpeed;
        }

        private static ObservableCollection<GameTelemetryOption> BuildGameTelemetryOptions(
            System.Collections.Generic.IEnumerable<PadForge.Common.Input.Telemetry.CustomTelemetryFormat> formats)
        {
            var list = new ObservableCollection<GameTelemetryOption>();
            foreach (PadForge.Common.Input.Telemetry.GameTelemetryFormat format in Enum.GetValues(typeof(PadForge.Common.Input.Telemetry.GameTelemetryFormat)))
                if (format != PadForge.Common.Input.Telemetry.GameTelemetryFormat.Custom)
                    list.Add(new GameTelemetryOption(format, string.Empty));
            // The runtime's rule: a name that cannot be read, or that
            // repeats an earlier one's (case aside), is left out.
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (formats != null)
                foreach (var custom in formats)
                    if (custom != null && PadForge.Common.Input.Telemetry.CustomTelemetryFormat.IsValidName(custom.Name)
                        && names.Add(custom.Name))
                        list.Add(new GameTelemetryOption(PadForge.Common.Input.Telemetry.GameTelemetryFormat.Custom, custom.Name));
            return list;
        }

        private string _gameTelemetryStatus = Strings.Instance.Common_Stopped;

        /// <summary>The listener's line: the game it hears, or why it hears
        /// nothing.</summary>
        public string GameTelemetryStatus
        {
            get => _gameTelemetryStatus;
            set => SetProperty(ref _gameTelemetryStatus, value ?? Strings.Instance.Common_Stopped);
        }

        private bool _isGameTelemetryRunning;

        /// <summary>Serving truth for the card's flame: the row is open,
        /// whatever the switch says.</summary>
        public bool IsGameTelemetryRunning
        {
            get => _isGameTelemetryRunning;
            set => SetProperty(ref _isGameTelemetryRunning, value);
        }

        private bool _isGameTelemetryReceiving;

        /// <summary>A game's telemetry reaches the row: the Extras page's status
        /// flame burns ember, and gold while the row waits for a game. Set with
        /// the status line, from the row's own status.</summary>
        public bool IsGameTelemetryReceiving
        {
            get => _isGameTelemetryReceiving;
            set => SetProperty(ref _isGameTelemetryReceiving, value);
        }

        // ─────────────────────────────────────────────
        //  Twitch Chat (#555)
        // ─────────────────────────────────────────────
        // The Twitch Chat row reads a channel's chat and presses the slots it
        // is assigned to when viewers type commands. The setters mirror into
        // TwitchChatRuntime, the Game Telemetry card's shape. The switch and
        // the channel are machine settings like the plain web address's and
        // stay out of profiles, so a profile switch never opens or closes the
        // connection.

        private bool _enableTwitchChat;

        /// <summary>Whether the Twitch Chat row reads the channel.</summary>
        public bool EnableTwitchChat
        {
            get => _enableTwitchChat;
            set
            {
                if (!SetProperty(ref _enableTwitchChat, value)) return;
                PadForge.Common.Input.Twitch.TwitchChatRuntime.Enabled = value;
                // Turned off and on again, chat control starts unpaused.
                if (!value) TwitchChatPaused = false;
            }
        }

        private string _twitchChatChannel = string.Empty;

        /// <summary>The channel as the streamer typed it, the name or the
        /// channel's address. The runtime keeps the name Twitch uses.</summary>
        public string TwitchChatChannel
        {
            get => _twitchChatChannel;
            set
            {
                if (SetProperty(ref _twitchChatChannel, value ?? string.Empty))
                    PadForge.Common.Input.Twitch.TwitchChatRuntime.Channel = _twitchChatChannel;
            }
        }

        private int _twitchChatClaimApprovalIndex;

        /// <summary>Who approves a viewer's !claim, a
        /// <see cref="PadForge.Common.Input.Twitch.TwitchClaimApproval"/> by
        /// number: 0 the streamer alone, 1 with moderators, 2 also any two
        /// viewers. A machine setting like the channel.</summary>
        public int TwitchChatClaimApprovalIndex
        {
            get => _twitchChatClaimApprovalIndex;
            set
            {
                // Out of range is a hand-edited file or the box's own -1
                // when it loses its selection, never a pick.
                if (value < 0 || value > (int)PadForge.Common.Input.Twitch.TwitchClaimApproval.Viewers) return;
                if (SetProperty(ref _twitchChatClaimApprovalIndex, value))
                    PadForge.Common.Input.Twitch.TwitchChatRuntime.ClaimApproval =
                        (PadForge.Common.Input.Twitch.TwitchClaimApproval)value;
            }
        }

        private string _twitchChatStatus = Strings.Instance.Common_Stopped;

        /// <summary>The row's line: the channel it reads, or why it reads
        /// nothing.</summary>
        public string TwitchChatStatus
        {
            get => _twitchChatStatus;
            set => SetProperty(ref _twitchChatStatus, value ?? Strings.Instance.Common_Stopped);
        }

        private bool _isTwitchChatRunning;

        /// <summary>Reading truth for the card's flame: the row has joined
        /// the channel's chat.</summary>
        public bool IsTwitchChatRunning
        {
            get => _isTwitchChatRunning;
            set => SetProperty(ref _isTwitchChatRunning, value);
        }

        private bool _isTwitchChatOpen;

        /// <summary>True while the row is open, which the streamer's
        /// controls act on.</summary>
        public bool IsTwitchChatOpen
        {
            get => _isTwitchChatOpen;
            set => SetProperty(ref _isTwitchChatOpen, value);
        }

        private bool _twitchChatPaused;

        /// <summary>The streamer's Pause: the row ignores every command, and
        /// pausing stops the command playing and clears the line. Not saved,
        /// so PadForge starts unpaused.</summary>
        public bool TwitchChatPaused
        {
            get => _twitchChatPaused;
            set
            {
                if (SetProperty(ref _twitchChatPaused, value))
                    PadForge.Common.Input.Twitch.TwitchChatRuntime.Paused = value;
            }
        }

        private RelayCommand _twitchChatPauseCommand;
        public RelayCommand TwitchChatPauseCommand =>
            _twitchChatPauseCommand ??= new RelayCommand(() => TwitchChatPaused = !TwitchChatPaused);

        private RelayCommand _twitchChatSkipCommand;
        public RelayCommand TwitchChatSkipCommand =>
            _twitchChatSkipCommand ??= new RelayCommand(() => PadForge.Common.Input.Twitch.TwitchChatRuntime.Skip());

        private RelayCommand _twitchChatClearCommand;
        public RelayCommand TwitchChatClearCommand =>
            _twitchChatClearCommand ??= new RelayCommand(PadForge.Common.Input.Twitch.TwitchChatRuntime.ClearLine);

        private RelayCommand _twitchChatEndClaimCommand;
        public RelayCommand TwitchChatEndClaimCommand =>
            _twitchChatEndClaimCommand ??= new RelayCommand(PadForge.Common.Input.Twitch.TwitchChatRuntime.EndClaim);

        private string _twitchChatDisplayUrl;

        /// <summary>The controller picture page on the plain HTTP address,
        /// code included, for an OBS browser source. Null while the plain
        /// address is not served.</summary>
        public string TwitchChatDisplayUrl
        {
            get => _twitchChatDisplayUrl;
            set => SetProperty(ref _twitchChatDisplayUrl, value);
        }

        private RelayCommand _copyTwitchChatDisplayUrlCommand;
        public RelayCommand CopyTwitchChatDisplayUrlCommand =>
            _copyTwitchChatDisplayUrlCommand ??= new RelayCommand(() =>
            {
                if (!string.IsNullOrEmpty(_twitchChatDisplayUrl))
                {
                    try { PadForge.Common.ShellLaunch.SetClipboardText(_twitchChatDisplayUrl); } catch { }
                }
            });

        // ─────────────────────────────────────────────
        //  Head Tracking (#355)
        // ─────────────────────────────────────────────
        // A UDP listener on OpenTrack's port plus a FreeTrack 2.0 shared
        // memory reader, surfaced as the Head Tracker device row. The
        // setters mirror into the static HeadTrackingRuntime the poll
        // thread's device sweep reads, so a change lands whoever writes it
        // (global load, profile apply, the user). Each input has an authored
        // nullable profile opinion. The port and the two ranges are global.

        private bool _headTrackingEnabled;

        /// <summary>Enables OpenTrack UDP input independently of FreeTrack.</summary>
        public bool HeadTrackingEnabled
        {
            get => _headTrackingEnabled;
            set
            {
                if (SetProperty(ref _headTrackingEnabled, value))
                    PadForge.Common.Input.HeadTrackingRuntime.Enabled = value;
            }
        }

        private int _headTrackingUdpPort = PadForge.Common.Input.HeadTrackingRuntime.DefaultUdpPort;

        private RelayCommand _resetHeadTrackingEnabledCommand;
        public RelayCommand ResetHeadTrackingEnabledCommand =>
            _resetHeadTrackingEnabledCommand ??= new RelayCommand(() => HeadTrackingEnabled = false);
        private RelayCommand _resetHeadTrackingFreeTrackCommand;
        public RelayCommand ResetHeadTrackingFreeTrackCommand =>
            _resetHeadTrackingFreeTrackCommand ??= new RelayCommand(() => HeadTrackingFreeTrack = false);

        /// <summary>UDP port OpenTrack's "UDP over network" output sends to.</summary>
        public int HeadTrackingUdpPort
        {
            get => _headTrackingUdpPort;
            set
            {
                int v = Math.Clamp(value, 1, 65535);
                if (SetProperty(ref _headTrackingUdpPort, v))
                    PadForge.Common.Input.HeadTrackingRuntime.UdpPort = v;
            }
        }

        private RelayCommand _resetHeadTrackingPortCommand;
        public RelayCommand ResetHeadTrackingPortCommand =>
            _resetHeadTrackingPortCommand ??= new RelayCommand(() =>
                HeadTrackingUdpPort = PadForge.Common.Input.HeadTrackingRuntime.DefaultUdpPort);

        private bool _headTrackingFreeTrack;

        /// <summary>Enables FreeTrack 2.0 shared memory input independently of UDP.</summary>
        public bool HeadTrackingFreeTrack
        {
            get => _headTrackingFreeTrack;
            set
            {
                if (SetProperty(ref _headTrackingFreeTrack, value))
                    PadForge.Common.Input.HeadTrackingRuntime.FreeTrackEnabled = value;
            }
        }

        private bool _headTrackingOpenXr;

        /// <summary>Reads the headset through an OpenXR runtime (issue #403).
        ///
        /// <para>Independent of the other two, and of SteamVR. It is the path
        /// for a headset running on Virtual Desktop's runtime, where the
        /// SteamVR consumer cannot help because it loads its native library
        /// out of a SteamVR install.</para></summary>
        public bool HeadTrackingOpenXr
        {
            get => _headTrackingOpenXr;
            set
            {
                if (!SetProperty(ref _headTrackingOpenXr, value)) return;
                PadForge.Common.Input.HeadTrackingRuntime.OpenXrEnabled = value;
                // Turning the input on reads the registry again, so a runtime
                // registered or made the default since the list was built
                // shows up. The source reads the system runtime only when the
                // input starts, which is why the user page's step for SteamVR
                // is to turn the input off and back on.
                if (value) RefreshOpenXrRuntimes();
            }
        }

        private RelayCommand _resetHeadTrackingOpenXrCommand;
        public RelayCommand ResetHeadTrackingOpenXrCommand =>
            _resetHeadTrackingOpenXrCommand ??= new RelayCommand(() => HeadTrackingOpenXr = false);

        /// <summary>One entry in the runtime picker. Empty
        /// <see cref="ManifestPath"/> is the machine's default.
        ///
        /// <para>Observable because a refresh keeps the instance, as the
        /// audio mirror picker's does: removing the selected entry makes WPF
        /// clear the selection, so a new caption reaches the box through the
        /// object rather than through the list.</para></summary>
        public sealed class OpenXrRuntimeChoice : ObservableObject
        {
            public string ManifestPath { get; init; } = string.Empty;

            private string _display = string.Empty;
            public string Display
            {
                get => _display;
                set => SetProperty(ref _display, value);
            }

            public override string ToString() => Display;
        }

        private System.Collections.ObjectModel.ObservableCollection<OpenXrRuntimeChoice> _openXrRuntimes;

        /// <summary>The runtimes installed on this machine, the system
        /// default first.
        ///
        /// <para>Populated once and then cached. An earlier version rebuilt
        /// on every read, which is what a user who installs Virtual Desktop
        /// mid-session would want, but the notification that made the picker
        /// see the rebuild recursed through the selection's getter and took
        /// the process down at launch. <see cref="RefreshOpenXrRuntimes"/> is
        /// the explicit path, and it never runs from here.</para></summary>
        public System.Collections.ObjectModel.ObservableCollection<OpenXrRuntimeChoice> OpenXrRuntimes
        {
            get
            {
                // Populate once, and never raise a change notification from
                // in here. SelectedOpenXrRuntime's getter reads this
                // collection, so notifying from this getter makes WPF read the
                // selection, which reads this getter again. That recursion
                // ends as a stack overflow, which kills the process with no
                // managed exception and no crash log.
                if (_openXrRuntimes == null)
                {
                    _openXrRuntimes =
                        new System.Collections.ObjectModel.ObservableCollection<OpenXrRuntimeChoice>();
                    Repopulate();
                }
                return _openXrRuntimes;
            }
        }

        private bool _refreshingOpenXrRuntimes;

        /// <summary>Test seam (InternalsVisibleTo PadForge.Tests): where the
        /// picker reads the installed runtimes, the registry in production. A
        /// fake list is how a runtime registered while PadForge runs is shown
        /// reaching the picker without writing HKLM.</summary>
        internal Func<System.Collections.Generic.IEnumerable<PadForge.Engine.Common.OpenXr.OpenXrRuntimeEntry>>
            DiscoverOpenXrRuntimes { get; set; } = PadForge.Engine.Common.OpenXr.OpenXrRuntimeCatalog.Discover;

        /// <summary>Reloads the picker from the registry. The saved choice
        /// stays listed and selected whether or not the registry still names
        /// it. Runs when the input is turned on, after a settings load and
        /// after a language switch, never from a property getter.
        ///
        /// <para>Guarded the way the audio mirror picker's refresh is: a
        /// rebuild that drops the selected entry makes WPF write an empty
        /// selection back, and the setter turned an empty selection into the
        /// system default.</para></summary>
        public void RefreshOpenXrRuntimes()
        {
            _refreshingOpenXrRuntimes = true;
            try
            {
                _openXrRuntimes ??=
                    new System.Collections.ObjectModel.ObservableCollection<OpenXrRuntimeChoice>();
                Repopulate();
                OnPropertyChanged(nameof(SelectedOpenXrRuntime));
            }
            finally { _refreshingOpenXrRuntimes = false; }
        }

        private void Repopulate()
        {
            string chosen = PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest;
            var desired = new System.Collections.Generic.List<OpenXrRuntimeChoice>
            {
                new OpenXrRuntimeChoice
                {
                    ManifestPath = string.Empty,
                    Display = Strings.Instance.Dashboard_HeadTrackingOpenXrSystemDefault,
                },
            };
            try
            {
                foreach (var entry in DiscoverOpenXrRuntimes())
                {
                    if (!entry.LibraryExists) continue;
                    desired.Add(new OpenXrRuntimeChoice
                    {
                        ManifestPath = entry.ManifestPath,
                        Display = entry.Name,
                    });
                }
            }
            catch (Exception) { }

            // A runtime the user picked and then uninstalled must still show,
            // or the box would silently read as the default while the saved
            // setting still names the missing one.
            if (!string.IsNullOrEmpty(chosen)
                && !desired.Any(r => string.Equals(r.ManifestPath, chosen,
                                                   StringComparison.OrdinalIgnoreCase)))
            {
                desired.Add(new OpenXrRuntimeChoice
                {
                    ManifestPath = chosen,
                    Display = System.IO.Path.GetFileNameWithoutExtension(chosen),
                });
            }

            // Synced in place and never cleared, the audio mirror picker's
            // rule. An entry whose manifest is still wanted keeps its
            // instance, so the box's selection rides through, and takes the
            // fresh caption.
            for (int i = 0; i < desired.Count; i++)
            {
                int j = -1;
                for (int k = i; k < _openXrRuntimes.Count; k++)
                    if (string.Equals(_openXrRuntimes[k].ManifestPath, desired[i].ManifestPath,
                                      StringComparison.OrdinalIgnoreCase)) { j = k; break; }
                if (j < 0) _openXrRuntimes.Insert(i, desired[i]);
                else
                {
                    _openXrRuntimes[j].Display = desired[i].Display;
                    if (j != i) _openXrRuntimes.Move(j, i);
                }
            }
            while (_openXrRuntimes.Count > desired.Count)
                _openXrRuntimes.RemoveAt(_openXrRuntimes.Count - 1);
        }

        /// <summary>Which runtime this process negotiates with. Changing it
        /// never touches the machine's ActiveRuntime.</summary>
        public OpenXrRuntimeChoice SelectedOpenXrRuntime
        {
            get
            {
                string chosen = PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest;
                return OpenXrRuntimes.FirstOrDefault(
                    r => string.Equals(r.ManifestPath, chosen, StringComparison.OrdinalIgnoreCase))
                    ?? OpenXrRuntimes.FirstOrDefault();
            }
            set
            {
                // A null is WPF clearing the box when its entry leaves the
                // list, never a pick, and reading it as the system default
                // would replace the saved runtime.
                if (_refreshingOpenXrRuntimes || value == null) return;
                string path = value.ManifestPath ?? string.Empty;
                if (string.Equals(PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest, path,
                                  StringComparison.OrdinalIgnoreCase))
                    return;
                PadForge.Common.Input.HeadTrackingRuntime.OpenXrRuntimeManifest = path;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeadTrackingOpenXrRuntimeChanged));
            }
        }

        /// <summary>Bumped when the chosen runtime changes, so the settings
        /// save picks it up the way it picks up the toggles.</summary>
        public object HeadTrackingOpenXrRuntimeChanged => null;

        private RelayCommand _resetHeadTrackingOpenXrRuntimeCommand;
        public RelayCommand ResetHeadTrackingOpenXrRuntimeCommand =>
            _resetHeadTrackingOpenXrRuntimeCommand ??= new RelayCommand(() =>
                SelectedOpenXrRuntime = OpenXrRuntimes.FirstOrDefault());

        private int _headTrackingRotationRange = PadForge.Common.Input.HeadTrackingRuntime.DefaultRotationRangeDeg;

        /// <summary>Degrees of head rotation at full axis deflection.</summary>
        public int HeadTrackingRotationRange
        {
            get => _headTrackingRotationRange;
            set
            {
                int v = Math.Clamp(value, 1, 180);
                if (SetProperty(ref _headTrackingRotationRange, v))
                    PadForge.Common.Input.HeadTrackingRuntime.RotationRangeDeg = v;
            }
        }

        private RelayCommand _resetHeadTrackingRotationRangeCommand;
        public RelayCommand ResetHeadTrackingRotationRangeCommand =>
            _resetHeadTrackingRotationRangeCommand ??= new RelayCommand(() =>
                HeadTrackingRotationRange = PadForge.Common.Input.HeadTrackingRuntime.DefaultRotationRangeDeg);

        private int _headTrackingTranslationRange = PadForge.Common.Input.HeadTrackingRuntime.DefaultTranslationRangeCm;

        /// <summary>Centimeters of head travel at full axis deflection.</summary>
        public int HeadTrackingTranslationRange
        {
            get => _headTrackingTranslationRange;
            set
            {
                int v = Math.Clamp(value, 1, 500);
                if (SetProperty(ref _headTrackingTranslationRange, v))
                    PadForge.Common.Input.HeadTrackingRuntime.TranslationRangeCm = v;
            }
        }

        /// <summary>
        /// The per-axis ranges, in HeadPose's order: yaw, pitch, roll, X, Y, Z.
        /// Zero means the axis follows its family's range.
        ///
        /// <para>Per axis because the three translations are not one setting.
        /// Head elevation on a bike wants a span of a few centimeters while
        /// leaning wants twenty, and a single shared number cannot be
        /// both.</para>
        /// </summary>
        public int HeadTrackingRangeYaw
        {
            get => AxisRange(0);
            set => SetAxisRange(0, value, nameof(HeadTrackingRangeYaw));
        }

        public int HeadTrackingRangePitch
        {
            get => AxisRange(1);
            set => SetAxisRange(1, value, nameof(HeadTrackingRangePitch));
        }

        public int HeadTrackingRangeRoll
        {
            get => AxisRange(2);
            set => SetAxisRange(2, value, nameof(HeadTrackingRangeRoll));
        }

        public int HeadTrackingRangeX
        {
            get => AxisRange(3);
            set => SetAxisRange(3, value, nameof(HeadTrackingRangeX));
        }

        public int HeadTrackingRangeY
        {
            get => AxisRange(4);
            set => SetAxisRange(4, value, nameof(HeadTrackingRangeY));
        }

        public int HeadTrackingRangeZ
        {
            get => AxisRange(5);
            set => SetAxisRange(5, value, nameof(HeadTrackingRangeZ));
        }

        // The box shows and writes the pin, not the range in force. Showing
        // the resolved range would put the family's number in all six boxes,
        // where it reads as six pinned axes, and typing that same number back
        // would pin nothing because it already matched. A zero would also
        // never survive the round trip: it would be written, then redisplayed
        // as the family's value.
        /// <summary>Tells the per-axis boxes and the runtime picker to
        /// re-read, after something outside the view model wrote the statics
        /// they display (a settings load, a reset to defaults).</summary>
        public void NotifyHeadTrackingRangesChanged()
        {
            foreach (var name in new[]
                     {
                         nameof(HeadTrackingRangeYaw), nameof(HeadTrackingRangePitch),
                         nameof(HeadTrackingRangeRoll), nameof(HeadTrackingRangeX),
                         nameof(HeadTrackingRangeY), nameof(HeadTrackingRangeZ),
                     })
                OnPropertyChanged(name);
            // A load rebuilds the picker. The list can be built before the
            // startup load sets the runtime, since the Dashboard has its data
            // first, and a saved runtime the registry no longer names is
            // listed only by a rebuild made after the runtime is known. The
            // refresh re-reads the selection too.
            RefreshOpenXrRuntimes();
        }

        private static int AxisRange(int axis)
            => PadForge.Common.Input.HeadTrackingRuntime.GetAxisRangeOverride(axis);

        private void SetAxisRange(int axis, int value, string property)
        {
            if (PadForge.Common.Input.HeadTrackingRuntime.GetAxisRangeOverride(axis) == value) return;
            PadForge.Common.Input.HeadTrackingRuntime.SetAxisRange(axis, value);
            // The clamp can land somewhere other than what was typed, so the
            // box is told to re-read rather than keep the entered number.
            OnPropertyChanged(property);
        }

        // Every setting row carries its own reset, per the project's own
        // paradigm. Zero is an axis's default because zero is what makes it
        // follow the shared range, so a reset here is "stop pinning this one".
        private RelayCommand _resetHeadTrackingRangeYawCommand;
        public RelayCommand ResetHeadTrackingRangeYawCommand =>
            _resetHeadTrackingRangeYawCommand ??= new RelayCommand(
                () => HeadTrackingRangeYaw = 0);

        private RelayCommand _resetHeadTrackingRangePitchCommand;
        public RelayCommand ResetHeadTrackingRangePitchCommand =>
            _resetHeadTrackingRangePitchCommand ??= new RelayCommand(
                () => HeadTrackingRangePitch = 0);

        private RelayCommand _resetHeadTrackingRangeRollCommand;
        public RelayCommand ResetHeadTrackingRangeRollCommand =>
            _resetHeadTrackingRangeRollCommand ??= new RelayCommand(
                () => HeadTrackingRangeRoll = 0);

        private RelayCommand _resetHeadTrackingRangeXCommand;
        public RelayCommand ResetHeadTrackingRangeXCommand =>
            _resetHeadTrackingRangeXCommand ??= new RelayCommand(
                () => HeadTrackingRangeX = 0);

        private RelayCommand _resetHeadTrackingRangeYCommand;
        public RelayCommand ResetHeadTrackingRangeYCommand =>
            _resetHeadTrackingRangeYCommand ??= new RelayCommand(
                () => HeadTrackingRangeY = 0);

        private RelayCommand _resetHeadTrackingRangeZCommand;
        public RelayCommand ResetHeadTrackingRangeZCommand =>
            _resetHeadTrackingRangeZCommand ??= new RelayCommand(
                () => HeadTrackingRangeZ = 0);

        private RelayCommand _headTrackingRecenterCommand;

        /// <summary>Makes wherever the user is sitting now the neutral.
        ///
        /// <para>Only the OpenXR source has a neutral to move. OpenTrack and
        /// FreeTrack carry whatever zero their own application was centered
        /// on, which is where their users already set it.</para></summary>
        public RelayCommand HeadTrackingRecenterCommand =>
            _headTrackingRecenterCommand ??= new RelayCommand(
                PadForge.Common.Input.HeadTrackingRuntime.Recenter);

        private RelayCommand _resetHeadTrackingTranslationRangeCommand;
        public RelayCommand ResetHeadTrackingTranslationRangeCommand =>
            _resetHeadTrackingTranslationRangeCommand ??= new RelayCommand(() =>
                HeadTrackingTranslationRange = PadForge.Common.Input.HeadTrackingRuntime.DefaultTranslationRangeCm);

        private string _headTrackingStatus = Strings.Instance.Common_Stopped;

        /// <summary>The Head Tracker row's source line (which source is live,
        /// or why neither is), pushed by InputService on the dashboard tick
        /// whenever the device's StatusVersion moves. Stopped while the
        /// feature is off or the engine is down.</summary>
        public string HeadTrackingStatus
        {
            get => _headTrackingStatus;
            set => SetProperty(ref _headTrackingStatus, value ?? Strings.Instance.Common_Stopped);
        }

        private bool _isHeadTrackingReceiving;

        /// <summary>A source delivers a pose to the Head Tracker row: the Extras
        /// page's status flame burns ember, and gold while the inputs wait. Set
        /// with the status line.</summary>
        public bool IsHeadTrackingReceiving
        {
            get => _isHeadTrackingReceiving;
            set => SetProperty(ref _isHeadTrackingReceiving, value);
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

        private string _webControllerUrl;

        /// <summary>The live server URL (https:// when the secure lane bound),
        /// shown on the card so a phone can type it. Empty when stopped.</summary>
        public string WebControllerUrl
        {
            get => _webControllerUrl;
            set => SetProperty(ref _webControllerUrl, value);
        }

        private System.Windows.Media.ImageSource _webControllerQr;

        /// <summary>A QR of <see cref="WebControllerUrl"/>, so the phone opens
        /// the controller by scanning instead of typing (#296). Null when
        /// stopped, which hides the image on the card.</summary>
        public System.Windows.Media.ImageSource WebControllerQr
        {
            get => _webControllerQr;
            set => SetProperty(ref _webControllerQr, value);
        }

        private bool _hasWebControllerQr;

        /// <summary>True when a QR is available (drives its visibility).</summary>
        public bool HasWebControllerQr
        {
            get => _hasWebControllerQr;
            set => SetProperty(ref _hasWebControllerQr, value);
        }

        private RelayCommand _copyWebControllerUrlCommand;
        /// <summary>Copies the web controller URL to the clipboard, the same
        /// affordance the Remote Link code field has.</summary>
        public RelayCommand CopyWebControllerUrlCommand =>
            _copyWebControllerUrlCommand ??= new RelayCommand(() =>
            {
                if (!string.IsNullOrEmpty(_webControllerUrl))
                {
                    try { PadForge.Common.ShellLaunch.SetClipboardText(_webControllerUrl); } catch { }
                }
            });

        private string _webControllerHttpsWarning;

        /// <summary>Why the main address fell back to plain HTTP and phone
        /// motion is off, or null while it serves HTTPS.</summary>
        public string WebControllerHttpsWarning
        {
            get => _webControllerHttpsWarning;
            set => SetProperty(ref _webControllerHttpsWarning, value);
        }

        // The plain HTTP address. These are machine settings like Remote
        // Link's, kept out of profiles: a foreground profile switch should
        // never open or close a network port, and a plain bool in a profile
        // reads as false in every profile saved before it existed.

        private bool _enableWebControllerPlainHttp;

        /// <summary>Also serve the web controller over plain HTTP on its own
        /// port, behind the access code.</summary>
        public bool EnableWebControllerPlainHttp
        {
            get => _enableWebControllerPlainHttp;
            set => SetProperty(ref _enableWebControllerPlainHttp, value);
        }

        private int _webControllerPlainHttpPort = Services.WebControllerServer.DefaultPlainPort;

        /// <summary>The plain HTTP address's port.</summary>
        public int WebControllerPlainHttpPort
        {
            get => _webControllerPlainHttpPort;
            set => SetProperty(ref _webControllerPlainHttpPort, Math.Clamp(value, 1024, 65535));
        }

        private RelayCommand _resetWebPlainPortCommand;
        public RelayCommand ResetWebPlainPortCommand =>
            _resetWebPlainPortCommand ??= new RelayCommand(
                () => WebControllerPlainHttpPort = Services.WebControllerServer.DefaultPlainPort);

        private bool _webControllerPlainHttpLocalOnly;

        /// <summary>Admit only this PC on the plain address, for a tunnel or
        /// reverse proxy running here. Also removes its firewall opening.</summary>
        public bool WebControllerPlainHttpLocalOnly
        {
            get => _webControllerPlainHttpLocalOnly;
            set => SetProperty(ref _webControllerPlainHttpLocalOnly, value);
        }

        private string _webControllerAccessCode = Services.WebControllerAccess.Generate();

        /// <summary>The code the plain address requires. Always valid: it
        /// starts as a fresh code, and a stored value that is not one of
        /// PadForge's codes is ignored, so the fresh one stays.</summary>
        public string WebControllerAccessCode
        {
            get => _webControllerAccessCode;
            set
            {
                if (!Services.WebControllerAccess.IsValid(value)) return;
                SetProperty(ref _webControllerAccessCode, Services.WebControllerAccess.Normalize(value));
            }
        }

        private RelayCommand _newWebAccessCodeCommand;
        /// <summary>Replaces the access code. The server drops every session
        /// that joined through the plain address with the old one.</summary>
        public RelayCommand NewWebAccessCodeCommand =>
            _newWebAccessCodeCommand ??= new RelayCommand(
                () => WebControllerAccessCode = Services.WebControllerAccess.Generate());

        private bool _isWebControllerPlainRunning;

        /// <summary>Serving truth for the plain address's flame.</summary>
        public bool IsWebControllerPlainRunning
        {
            get => _isWebControllerPlainRunning;
            set => SetProperty(ref _isWebControllerPlainRunning, value);
        }

        private string _webControllerPlainStatus;

        /// <summary>Where the plain address runs, or why it does not. Null
        /// while the server is stopped or the address is off.</summary>
        public string WebControllerPlainStatus
        {
            get => _webControllerPlainStatus;
            set => SetProperty(ref _webControllerPlainStatus, value);
        }

        private string _webControllerPlainUrl;

        /// <summary>The plain address with its code, or null when it is not
        /// served.</summary>
        public string WebControllerPlainUrl
        {
            get => _webControllerPlainUrl;
            set => SetProperty(ref _webControllerPlainUrl, value);
        }

        private System.Windows.Media.ImageSource _webControllerPlainQr;

        /// <summary>A QR of <see cref="WebControllerPlainUrl"/>. Null in This
        /// PC Only mode, where the address names localhost and a phone could
        /// not open it.</summary>
        public System.Windows.Media.ImageSource WebControllerPlainQr
        {
            get => _webControllerPlainQr;
            set => SetProperty(ref _webControllerPlainQr, value);
        }

        private bool _hasWebControllerPlainQr;

        /// <summary>True when a plain-address QR is available.</summary>
        public bool HasWebControllerPlainQr
        {
            get => _hasWebControllerPlainQr;
            set => SetProperty(ref _hasWebControllerPlainQr, value);
        }

        private RelayCommand _copyWebControllerPlainUrlCommand;
        /// <summary>Copies the plain address, code included.</summary>
        public RelayCommand CopyWebControllerPlainUrlCommand =>
            _copyWebControllerPlainUrlCommand ??= new RelayCommand(() =>
            {
                if (!string.IsNullOrEmpty(_webControllerPlainUrl))
                {
                    try { PadForge.Common.ShellLaunch.SetClipboardText(_webControllerPlainUrl); } catch { }
                }
            });

        /// <summary>The phones and browsers connected now, for the Phones card
        /// (#574). InputService adds a session on the UI thread when it
        /// connects and takes it out when that same session ends.</summary>
        public ObservableCollection<WebControllerPhone> WebControllerPhones { get; } = new();

        /// <summary>A session connected. A phone that reconnects under the
        /// same id replaces its old entry.</summary>
        internal void AddWebControllerPhone(WebControllerDevice device)
        {
            if (device == null) return;
            for (int i = 0; i < WebControllerPhones.Count; i++)
                if (WebControllerPhones[i].Id == device.InstanceGuid) { WebControllerPhones[i] = new WebControllerPhone(device); return; }
            WebControllerPhones.Add(new WebControllerPhone(device));
        }

        /// <summary>A session ended. Only its own entry goes: the end of a
        /// session a reconnect already replaced leaves the new one alone.</summary>
        internal void RemoveWebControllerPhone(WebControllerDevice device)
        {
            for (int i = 0; i < WebControllerPhones.Count; i++)
                if (ReferenceEquals(WebControllerPhones[i].Device, device)) { WebControllerPhones.RemoveAt(i); return; }
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

        private string _remoteLinkMyCode = "";

        /// <summary>This PC's current connection code (#294): a self-contained
        /// code embedding our public + private endpoints, minted while Remote
        /// Link runs. Empty until it is available (needs the STUN probe). The
        /// other person types this into their Connect field to reach us with no
        /// VPN. Set by InputService.</summary>
        public string RemoteLinkMyCode
        {
            get => _remoteLinkMyCode;
            set
            {
                if (SetProperty(ref _remoteLinkMyCode, value ?? ""))
                    OnPropertyChanged(nameof(HasRemoteLinkMyCode));
            }
        }

        /// <summary>True when a shareable code is available to show/copy.</summary>
        public bool HasRemoteLinkMyCode => !string.IsNullOrEmpty(_remoteLinkMyCode);

        private string _remoteLinkNatWarning = "";

        /// <summary>Actionable network warning (#294): set when the STUN probe
        /// found no public address (UDP blocked) or an endpoint-dependent
        /// (symmetric / carrier-grade) NAT that direct connections cannot
        /// traverse. A mobile hotspot is the classic case. Empty when the
        /// network looks punchable. Set by InputService.</summary>
        public string RemoteLinkNatWarning
        {
            get => _remoteLinkNatWarning;
            set
            {
                if (SetProperty(ref _remoteLinkNatWarning, value ?? ""))
                    OnPropertyChanged(nameof(HasRemoteLinkNatWarning));
            }
        }

        public bool HasRemoteLinkNatWarning => !string.IsNullOrEmpty(_remoteLinkNatWarning);

        private RelayCommand _copyRemoteLinkCodeCommand;
        /// <summary>Copies this PC's connection code to the clipboard.</summary>
        public RelayCommand CopyRemoteLinkCodeCommand =>
            _copyRemoteLinkCodeCommand ??= new RelayCommand(() =>
            {
                if (!string.IsNullOrEmpty(_remoteLinkMyCode))
                {
                    try { PadForge.Common.ShellLaunch.SetClipboardText(_remoteLinkMyCode); } catch { }
                }
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

        private bool _enableShiftLayerFlyout = true;

        /// <summary>Whether the shift-layer flyout appears while a slot is on
        /// a non-Base layer. Default on. Purely a display of engagement state
        /// the poll thread already computes, so turning it off changes nothing
        /// about which layer is active or how rows resolve.</summary>
        public bool EnableShiftLayerFlyout
        {
            get => _enableShiftLayerFlyout;
            set => SetProperty(ref _enableShiftLayerFlyout, value);
        }

        private bool _enableProfileOverlay = true;

        /// <summary>Whether the profile-switch overlay appears when a profile
        /// changes. Default on. The switch itself still happens when off, this
        /// only suppresses the announcement.</summary>
        public bool EnableProfileOverlay
        {
            get => _enableProfileOverlay;
            set => SetProperty(ref _enableProfileOverlay, value);
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

        private RelayCommand _resetTouchpadOverlayCardCommand;
        /// <summary>Reset All on Look and Place (#574): the opacity and the
        /// place, the card's two settings.</summary>
        public RelayCommand ResetTouchpadOverlayCardCommand =>
            _resetTouchpadOverlayCardCommand ??= new RelayCommand(() =>
            {
                ResetOpacityCommand.Execute(null);
                ResetTouchpadOverlayPositionCommand.Execute(null);
            });

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

        // ─────────────────────────────────────────────
        //  Screen Overlays (#560)
        // ─────────────────────────────────────────────

        /// <summary>The overlays on the card. There is always at least one, so the
        /// card reads like the Touchpad Overlay's until someone adds another.</summary>
        public ObservableCollection<ScreenOverlayItem> ScreenOverlays { get; } = new();

        /// <summary>Raised when the user clicks an overlay's Reset Position. The
        /// handler in InputService recenters a live overlay, or clears the saved
        /// place when it is not showing.</summary>
        public event EventHandler<ScreenOverlayItem> ResetScreenOverlayPositionRequested;

        /// <summary>Raised when the user clicks an overlay's Choose What It Shows.</summary>
        public event EventHandler<ScreenOverlayItem> ChooseScreenOverlayContentRequested;

        internal void RequestScreenOverlayResetPosition(ScreenOverlayItem item)
            => ResetScreenOverlayPositionRequested?.Invoke(this, item);

        internal void RequestScreenOverlayChoose(ScreenOverlayItem item)
            => ChooseScreenOverlayContentRequested?.Invoke(this, item);

        /// <summary>Raised by an overlay's Reset All (#574): what it shows goes
        /// back to the default. MainWindow holds the keyboard screens'
        /// choices and makes the change.</summary>
        public event EventHandler<ScreenOverlayItem> ResetScreenOverlayChoicesRequested;

        internal void RequestScreenOverlayResetChoices(ScreenOverlayItem item)
            => ResetScreenOverlayChoicesRequested?.Invoke(this, item);

        /// <summary>What an overlay shows, by its number, read from the saved
        /// choices. Set by MainWindow. Null leaves every overlay at the
        /// default mode.</summary>
        internal Func<int, PadForge.Services.KeyboardScreenMode> ScreenOverlayModeOf { get; set; }

        /// <summary>A screen's choices changed: the overlay that is that row
        /// says what it shows again.</summary>
        internal void ScreenOverlayChoicesChanged(Guid row)
        {
            foreach (var item in ScreenOverlays)
                if (PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo.OverlayRow(item.Number) == row) item.RefreshShows();
        }

        private RelayCommand _addScreenOverlayCommand;
        public RelayCommand AddScreenOverlayCommand =>
            _addScreenOverlayCommand ??= new RelayCommand(AddScreenOverlay);

        public bool CanAddScreenOverlay
            => ScreenOverlays.Count < PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo.MaxOverlays;

        /// <summary>A new overlay takes the lowest free number, so a macro that names
        /// a number finds the overlay that took it.</summary>
        internal void AddScreenOverlay()
        {
            if (!CanAddScreenOverlay) return;
            int number = 1;
            while (FindScreenOverlay(number) != null) number++;
            int at = 0;
            while (at < ScreenOverlays.Count && ScreenOverlays[at].Number < number) at++;
            ScreenOverlays.Insert(at, new ScreenOverlayItem(this, number));
            ScreenOverlayListChanged();
        }

        internal void RemoveScreenOverlay(ScreenOverlayItem item)
        {
            if (ScreenOverlays.Count <= 1) return;
            ScreenOverlays.Remove(item);
            ScreenOverlayListChanged();
        }

        internal ScreenOverlayItem FindScreenOverlay(int number)
        {
            foreach (var item in ScreenOverlays)
                if (item.Number == number) return item;
            return null;
        }

        /// <summary>The numbers on the card, for a macro that names every overlay.</summary>
        internal int[] ScreenOverlayNumbers()
        {
            var numbers = new int[ScreenOverlays.Count];
            for (int i = 0; i < numbers.Length; i++) numbers[i] = ScreenOverlays[i].Number;
            return numbers;
        }

        /// <summary>The list for the settings file.</summary>
        internal PadForge.Services.ScreenOverlayData[] SnapshotScreenOverlays()
        {
            var data = new PadForge.Services.ScreenOverlayData[ScreenOverlays.Count];
            for (int i = 0; i < data.Length; i++) data[i] = ScreenOverlays[i].ToData();
            return data;
        }

        /// <summary>Takes a list from the settings file. A file or profile from before
        /// these existed has none, which is one overlay, off. An entry whose number
        /// is out of range or already taken is dropped.</summary>
        internal void LoadScreenOverlays(PadForge.Services.ScreenOverlayData[] data)
        {
            _loadingScreenOverlays = true;
            try
            {
                ScreenOverlays.Clear();
                if (data != null)
                {
                    foreach (var entry in data)
                    {
                        if (entry == null || entry.Number < 1
                            || entry.Number > PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo.MaxOverlays
                            || FindScreenOverlay(entry.Number) != null) continue;
                        int at = 0;
                        while (at < ScreenOverlays.Count && ScreenOverlays[at].Number < entry.Number) at++;
                        var item = new ScreenOverlayItem(this, entry.Number);
                        ScreenOverlays.Insert(at, item);
                        item.Load(entry);
                    }
                }
                if (ScreenOverlays.Count == 0) ScreenOverlays.Add(new ScreenOverlayItem(this, 1));
            }
            finally { _loadingScreenOverlays = false; }
            ScreenOverlayListChanged();
        }

        private bool _loadingScreenOverlays;

        private void ScreenOverlayListChanged()
        {
            foreach (var item in ScreenOverlays) item.RefreshOneOfSeveral();
            OnPropertyChanged(nameof(CanAddScreenOverlay));
            ScreenOverlayEdited();
        }

        /// <summary>A saved value of an overlay changed. The property change marks the
        /// settings dirty and tells InputService to bring the windows into
        /// line.</summary>
        internal void ScreenOverlayEdited()
        {
            if (!_loadingScreenOverlays) OnPropertyChanged(nameof(ScreenOverlays));
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

        private bool _isCreateFailed;

        /// <summary>Whether the slot's latest virtual-controller create
        /// attempt failed (engine createFailed latch). Its own status:
        /// a failed slot with online devices is neither forging nor
        /// awaiting devices.</summary>
        public bool IsCreateFailed
        {
            get => _isCreateFailed;
            set
            {
                if (SetProperty(ref _isCreateFailed, value))
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

        private bool _engineStopped = true;

        /// <summary>Whether the engine is stopped. The dashboard tells each
        /// summary, so a card can say its heat without looking past
        /// itself.</summary>
        public bool EngineStopped
        {
            get => _engineStopped;
            set
            {
                if (SetProperty(ref _engineStopped, value))
                    FollowHeat();
            }
        }

        private string _heat = HeatCold;

        public const string HeatForging = "Forging";
        public const string HeatWaiting = "Waiting";
        public const string HeatCold = "Cold";

        /// <summary>The controller's heat in one word that never changes
        /// with the language, for the views to switch on: Forging while its
        /// virtual controller is live or being made, Waiting while it is on
        /// and has devices but the engine is stopped or its virtual
        /// controller is not there, Cold while it is off or has no device.
        /// It is the sidebar pill's rule (MainWindow.ComputeFlameHeat)
        /// without the engine's idle state, which the engine's own word
        /// carries.</summary>
        public string Heat => _heat;

        private void FollowHeat()
        {
            string heat = !_isEnabled ? HeatCold
                : _isInitializing ? HeatForging
                : _mappedDeviceCount == 0 ? HeatCold
                : _engineStopped || !_isVirtualControllerConnected ? HeatWaiting
                : HeatForging;
            if (heat == _heat) return;
            _heat = heat;
            OnPropertyChanged(nameof(Heat));
        }

        protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName is nameof(IsEnabled) or nameof(IsInitializing) or nameof(MappedDeviceCount)
                or nameof(IsVirtualControllerConnected))
                FollowHeat();
            // The pill's word and the waiting line are made of these.
            if (e.PropertyName is nameof(IsEnabled) or nameof(IsInitializing) or nameof(IsCreateFailed)
                or nameof(MappedDeviceCount) or nameof(ConnectedDeviceCount) or nameof(IsVirtualControllerConnected)
                or nameof(EngineStopped) or nameof(Heat))
                RefreshWords();
        }

        /// <summary>The state in the words of the card's pill, which is the
        /// controller's switch: Disabled while it is off, then what an
        /// enabled controller is doing. The order is the old flame
        /// tooltip's: being made, failed, nothing mapped, the engine
        /// stopped, waiting for a device, forging. Through the inactivity
        /// grace a controller is still live with its devices away, so it
        /// reads Forging until it is taken down.</summary>
        public string StateWord => !_isEnabled ? Strings.Instance.Common_Disabled
            : _isInitializing ? Strings.Instance.Main_Initializing
            : _isCreateFailed ? Strings.Instance.Main_VcFailed
            : _mappedDeviceCount == 0 ? Strings.Instance.Dashboard_StatusCold
            : _engineStopped ? Strings.Instance.Main_EngineStopped
            : !_isVirtualControllerConnected ? Strings.Instance.Main_AwaitingDevices
            : Strings.Instance.Main_Active;

        /// <summary>What a waiting controller waits for, in a sentence with
        /// no number in it, or nothing: the engine while it is stopped,
        /// else a device while none of its devices is connected.</summary>
        public string WaitHint => _heat != HeatWaiting || _isCreateFailed ? string.Empty
            : _engineStopped ? Strings.Instance.Home_Card_StartsWithEngine
            : _connectedDeviceCount > 0 ? string.Empty
            : _mappedDeviceCount == 1 ? Strings.Instance.Home_Card_StartsWhenThis
            : Strings.Instance.Home_Card_StartsWhenOne;

        /// <summary>Has the card read its words again: a state changed, or
        /// the language did.</summary>
        internal void RefreshWords()
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(StateWord)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(WaitHint)));
        }

        private string _pictureProfile;
        private VirtualControllerType _pictureType;
        private string _pictureFinishes;
        private bool _pictureStill;
        private bool _pictureMade;
        private bool _pictureComing;
        private System.Windows.Media.ImageSource _picture, _pictureGray;

        /// <summary>The controller's picture on the card's stage: what its
        /// own page shows, in the finish chosen there. Null for a type
        /// with no picture, whose card shows the type's symbol, and while
        /// a still of the 3D model is being made.</summary>
        public System.Windows.Media.ImageSource Picture => _picture;

        /// <summary>The same picture with its colors taken out, which a
        /// controller that is not forging shows.</summary>
        public System.Windows.Media.ImageSource PictureGray => _pictureGray;

        /// <summary>Whether the stage has a picture or is about to have
        /// one. While it waits for a still the stage is empty, and the
        /// type's symbol does not stand in for a moment.</summary>
        public bool HasPicture => _picture != null || _pictureComing;

        /// <summary>Whether the picture was made for this preset, this
        /// type, these finishes and this kind (a still of the 3D model, or
        /// the 2D art), so the one that sets it asks again only when one
        /// of them changed.</summary>
        internal bool PictureIsFor(string profileId, VirtualControllerType type, string finishes, bool still)
            => _pictureMade && _pictureStill == still && _pictureType == type
                && _pictureProfile == profileId && _pictureFinishes == finishes;

        /// <summary>Sets the picture and what it was made for.
        /// <paramref name="coming"/> says a still is being made, so there
        /// is no picture yet and there will be one.</summary>
        internal void SetPicture(string profileId, VirtualControllerType type, string finishes, bool still,
            System.Windows.Media.ImageSource picture, System.Windows.Media.ImageSource gray, bool coming = false)
        {
            _pictureMade = true;
            _pictureProfile = profileId;
            _pictureType = type;
            _pictureFinishes = finishes;
            _pictureStill = still;
            bool had = HasPicture;
            bool changed = !ReferenceEquals(picture, _picture) || !ReferenceEquals(gray, _pictureGray);
            _picture = picture;
            _pictureGray = gray;
            _pictureComing = coming && picture == null;
            if (changed)
            {
                OnPropertyChanged(nameof(Picture));
                OnPropertyChanged(nameof(PictureGray));
            }
            if (had != HasPicture) OnPropertyChanged(nameof(HasPicture));
        }

        /// <summary>Has the picture asked for again at the next refresh: a
        /// still it waited for may have been made.</summary>
        internal void ForgetPicture() => _pictureMade = false;

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
                    if (_isCreateFailed)
                        return Strings.Instance.Main_VcFailed;
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

        /// <summary>The card's screen-reader name, the "Slot 3" its header
        /// shows: the card is an item of SlotsItemsControl, and
        /// ItemAutomationPeer.GetNameCore falls back to the item's
        /// ToString().</summary>
        public override string ToString() => Common.AccessibleNames.SlotTitle(SlotNumber);

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
            set
            {
                if (SetProperty(ref _outputType, value))
                {
                    OnPropertyChanged(nameof(TypeGeometry));
                    OnPropertyChanged(nameof(TypeGlyph));
                }
            }
        }

        /// <summary>The type chip's branded logo, or null for the types drawn
        /// as a glyph. Same map as the Devices page slot badges.</summary>
        public System.Windows.Media.Geometry TypeGeometry => SlotTypeIconMap.GeometryFor(_outputType);

        /// <summary>The type chip's MDL2 glyph for the types without a logo,
        /// empty otherwise.</summary>
        public string TypeGlyph => SlotTypeIconMap.GlyphFor(_outputType);

        private string _presetName = string.Empty;

        /// <summary>The slot's Preset as its Pad page chip names it
        /// (PadViewModel.PresetDisplayName), printed under the card's title
        /// (discussion #543).</summary>
        public string PresetName
        {
            get => _presetName;
            set => SetProperty(ref _presetName, value ?? string.Empty);
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

        /// <summary>The stage's screen-reader name: the name of the
        /// controller tab whose glyph it shows. The stage is a list item,
        /// and ItemAutomationPeer.GetNameCore falls back to the item's
        /// ToString().</summary>
        public override string ToString() => Kind switch
        {
            "Sticks" => Strings.Instance.Pad_Sticks,
            "Triggers" => Strings.Instance.Pad_Triggers,
            "Gyro" => Strings.Instance.Pad_Gyro,
            "Lighting" => Strings.Instance.Pad_Lighting,
            "Touchpad" => Strings.Instance.Pad_Touchpad,
            "Audio" => Strings.Instance.Pad_Audio,
            _ => Kind,
        };

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
