using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.ViewModels
{
    /// <summary>What the Plugins page asks of the plugins' part: the app's
    /// controller, or a test's.</summary>
    internal interface IPluginsPageActions
    {
        /// <summary>What the confirmation shows of a file: its path, signer
        /// and SHA-256. Null when the file cannot be read.</summary>
        PluginApproval Describe(string path);

        /// <summary>Whether the file is on the list already.</summary>
        bool Has(string path);

        /// <summary>Adds the file for the bytes the confirmation showed.
        /// False when the list is full.</summary>
        bool Add(PluginApproval approved);

        void Remove(PluginRowItem row);
        void SetEnabled(PluginRowItem row, bool on);
        void Approve(PluginRowItem row, PluginApproval approved);
        void StartAgain(PluginRowItem row);

        /// <summary>Trust New Versions From This Signer.</summary>
        void SetTrust(PluginRowItem row, bool on);

        /// <summary>Reads a recording for Replay, off the interface's thread:
        /// the file is inflated and every line read, and nothing on the page
        /// changes.</summary>
        RecordingRead ReadRecording(string path);

        /// <summary>Opens a recording <see cref="ReadRecording"/> read, on
        /// the interface's thread. Null when it opened, or the reason in a
        /// word.</summary>
        string OpenRecording(RecordingRead read);

        void Play();
        void Stop();

        /// <summary>Writes the plugin kit into a new folder in
        /// <paramref name="parent"/> as the user (UserSave.KitAsync) and
        /// gives that folder, or null when it was not written.</summary>
        System.Threading.Tasks.Task<string> SaveKitAsync(string parent);
    }

    /// <summary>A recording as it was read for Replay: the file, or null
    /// and the reason in a word.</summary>
    internal sealed record RecordingRead(PadForge.Services.Plugins.ReplayFile File, string Error);

    /// <summary>A file as PadForge's confirmation shows it before the user
    /// approves it.</summary>
    internal sealed record PluginApproval(string Path, string Signer, string Sha256, string Key = null);

    /// <summary>
    /// The Plugins extra (#600): its switch and state, the two built-in
    /// plugins' settings, the rows of the user's plugins, Replay's file and
    /// place, and the log of the plugin the user picked. The switches are
    /// saved for the app, never in a profile, as Automation's is. The
    /// plugins' part fills the rows and the lines: until it does, the
    /// status says only whether the switch is on.
    /// </summary>
    public partial class DashboardViewModel
    {
        private bool _enablePlugins;

        /// <summary>Whether PadForge runs the plugins the user approved. Off
        /// until the user turns it on.</summary>
        public bool EnablePlugins
        {
            get => _enablePlugins;
            set
            {
                if (!SetProperty(ref _enablePlugins, value)) return;
                if (_pluginsStatus == null) OnPropertyChanged(nameof(PluginsStatus));
                RaiseReplayCommands();
            }
        }

        private bool _isPluginsRunning;

        /// <summary>Running truth for the status flame: a plugin runs now,
        /// whatever the switch says.</summary>
        public bool IsPluginsRunning
        {
            get => _isPluginsRunning;
            set => SetProperty(ref _isPluginsRunning, value);
        }

        private string _pluginsStatus;

        /// <summary>The plugin manager's line. Until it gives one: stopped
        /// while the switch is off, and that no plugin runs while it is
        /// on.</summary>
        public string PluginsStatus
        {
            get => _pluginsStatus ?? (_enablePlugins ? Strings.Instance.Extras_Plugins_Status_None : Strings.Instance.Common_Stopped);
            set => SetProperty(ref _pluginsStatus, value);
        }

        // ── What PadForge.xml holds ──

        /// <summary>The plugins as PadForge.xml holds them.</summary>
        internal PluginData[] PluginRecords { get; private set; } = Array.Empty<PluginData>();

        /// <summary>Raised after the settings load the plugins' part of the
        /// file, at the start and at a factory reset.</summary>
        internal event Action PluginSettingsLoaded;

        /// <summary>Takes the plugins' part of PadForge.xml.</summary>
        internal void LoadPluginSettings(PluginData[] plugins, bool testControllerOn, PluginValueData[] testControllerValues,
            bool replayOn, bool replayRepeat)
        {
            PluginRecords = plugins?.Where(p => p != null).ToArray() ?? Array.Empty<PluginData>();
            TestControllerOn = testControllerOn;
            TestControllerValues = testControllerValues?.Where(v => v != null).ToArray() ?? Array.Empty<PluginValueData>();
            ReplayOn = replayOn;
            ReplayRepeat = replayRepeat;
            PluginSettingsLoaded?.Invoke();
        }

        /// <summary>The plugins' part gives the list as it stands after a
        /// change, which marks the settings to be saved.</summary>
        internal void SetPluginRecords(PluginData[] records)
        {
            PluginRecords = records ?? Array.Empty<PluginData>();
            PluginListRevision++;
        }

        /// <summary>The Test Controller's values as PadForge.xml holds them
        /// (#600 item 19).</summary>
        internal PluginValueData[] TestControllerValues { get; private set; } = Array.Empty<PluginValueData>();

        /// <summary>The plugins' part gives the Test Controller's values
        /// after a change, which marks the settings to be saved.</summary>
        internal void SetTestControllerValues(PluginValueData[] values)
        {
            TestControllerValues = values ?? Array.Empty<PluginValueData>();
            PluginValuesRevision++;
        }

        private int _pluginValuesRevision;

        /// <summary>Counts the changes to the built-ins' declared values. The
        /// window saves on it. A plugin's own values ride the list of plugins
        /// and its revision.</summary>
        public int PluginValuesRevision
        {
            get => _pluginValuesRevision;
            private set => SetProperty(ref _pluginValuesRevision, value);
        }

        private int _pluginListRevision;

        /// <summary>Counts the changes to the list of plugins: a plugin added,
        /// removed, approved, switched or named. The window saves on
        /// it.</summary>
        public int PluginListRevision
        {
            get => _pluginListRevision;
            private set => SetProperty(ref _pluginListRevision, value);
        }

        // ── Built in ──

        private bool _testControllerOn;

        /// <summary>The Test Controller's switch: a controller that moves by
        /// itself is on the Devices page.</summary>
        public bool TestControllerOn
        {
            get => _testControllerOn;
            set => SetProperty(ref _testControllerOn, value);
        }

        private PluginSettingsHost _testControllerSettings;

        /// <summary>The Test Controller's declared settings (#600 item 19),
        /// drawn by the same templates as a plugin's. Set by the plugins'
        /// part.</summary>
        public PluginSettingsHost TestControllerSettings
        {
            get => _testControllerSettings;
            internal set => SetProperty(ref _testControllerSettings, value);
        }

        private string _testControllerLine;

        /// <summary>The Test Controller card's line: off, waiting, moving or
        /// by hand.</summary>
        public string TestControllerLine
        {
            get => _testControllerLine ?? (_testControllerOn ? Strings.Instance.Plugins_State_Waiting : Strings.Instance.Common_Off);
            set => SetProperty(ref _testControllerLine, value);
        }

        private bool _replayOn;

        /// <summary>Replay's switch: the open recording's devices are on the
        /// Devices page.</summary>
        public bool ReplayOn
        {
            get => _replayOn;
            set
            {
                if (SetProperty(ref _replayOn, value)) RaiseReplayCommands();
            }
        }

        private bool _replayRepeat;

        /// <summary>Whether Replay starts its recording again at the
        /// end.</summary>
        public bool ReplayRepeat
        {
            get => _replayRepeat;
            set => SetProperty(ref _replayRepeat, value);
        }

        private string _replayFileLine;

        /// <summary>The open recording's name and length, or null while none
        /// is open.</summary>
        public string ReplayFileLine
        {
            get => _replayFileLine;
            set
            {
                if (SetProperty(ref _replayFileLine, value))
                {
                    OnPropertyChanged(nameof(HasReplayFile));
                    RaiseReplayCommands();
                }
            }
        }

        public bool HasReplayFile => _replayFileLine != null;

        private string _replayPositionLine;

        /// <summary>Where playback is in the file: "0:12 of 1:30".</summary>
        public string ReplayPositionLine
        {
            get => _replayPositionLine;
            set => SetProperty(ref _replayPositionLine, value);
        }

        private bool _isReplayPlaying;

        public bool IsReplayPlaying
        {
            get => _isReplayPlaying;
            set
            {
                if (SetProperty(ref _isReplayPlaying, value)) RaiseReplayCommands();
            }
        }

        private RelayCommand _replayPlayCommand, _replayStopCommand;

        public RelayCommand ReplayPlayCommand => _replayPlayCommand ??= new RelayCommand(
            () => PluginActions?.Play(), () => CanPlayReplay && !_isReplayPlaying);

        public RelayCommand ReplayStopCommand => _replayStopCommand ??= new RelayCommand(
            () => PluginActions?.Stop(), () => CanPlayReplay && _isReplayPlaying);

        private bool CanPlayReplay => _enablePlugins && _replayOn && _replayFileLine != null;

        private void RaiseReplayCommands()
        {
            _replayPlayCommand?.NotifyCanExecuteChanged();
            _replayStopCommand?.NotifyCanExecuteChanged();
        }

        // ── The user's plugins ──

        /// <summary>One row for each plugin the user added, in the order
        /// added.</summary>
        public ObservableCollection<PluginRowItem> PluginRows { get; } = new();

        public bool HasPluginRows => PluginRows.Count > 0;

        /// <summary>The plugins' part, set once it is made.</summary>
        internal IPluginsPageActions PluginActions { get; set; }

        /// <summary>Called by the plugins' part after it changed the
        /// rows.</summary>
        internal void PluginRowsChanged()
        {
            OnPropertyChanged(nameof(HasPluginRows));
            if (_selectedLogPlugin != null && !PluginRows.Contains(_selectedLogPlugin)) SelectedLogPlugin = null;
            RaiseReplayCommands();
        }

        // ── The log ──

        private PluginRowItem _selectedLogPlugin;

        /// <summary>The plugin whose log the More row shows. None is picked
        /// as the page opens.</summary>
        public PluginRowItem SelectedLogPlugin
        {
            get => _selectedLogPlugin;
            set
            {
                if (!SetProperty(ref _selectedLogPlugin, value)) return;
                OnPropertyChanged(nameof(HasSelectedLogPlugin));
                OnPropertyChanged(nameof(IsPluginLogEmpty));
                SelectedLogPluginChanged?.Invoke();
            }
        }

        public bool HasSelectedLogPlugin => _selectedLogPlugin != null;

        /// <summary>A plugin is picked and its log holds no line.</summary>
        public bool IsPluginLogEmpty => _selectedLogPlugin != null && PluginLogLines.Count == 0;

        public bool HasPluginLogLines => PluginLogLines.Count > 0;

        /// <summary>Raised when the user picks another plugin's log.</summary>
        internal event Action SelectedLogPluginChanged;

        /// <summary>The picked plugin's log, oldest line first.</summary>
        public ObservableCollection<string> PluginLogLines { get; } = new();

        /// <summary>The log's lines say whether there are any. Called by the
        /// constructor.</summary>
        private void FollowPluginLog()
        {
            PluginLogLines.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(IsPluginLogEmpty));
                OnPropertyChanged(nameof(HasPluginLogLines));
            };
        }
    }
}
