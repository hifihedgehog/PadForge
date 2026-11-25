using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// One plugin's fold card on the Plugins page (#597, #600 items 17 and
    /// 18): the name it gave, or its file's name until it has run, its state
    /// with the devices it added and its signer, its switch, the buttons its
    /// state calls for (Start after five failures or a refused start,
    /// Approve and Restart for a file that changed, and Remove), and under
    /// the fold the settings it declared.
    /// </summary>
    public sealed class PluginRowItem : ObservableObject
    {
        private readonly IPluginsPageActions _actions;
        private string _name, _state, _path;
        private bool _isOn, _showStart, _showApprove, _updating;

        internal PluginRowItem(object run, int number, IPluginsPageActions actions, PluginSettingsHost settings = null)
        {
            Run = run;
            Number = number;
            _actions = actions;
            Settings = settings;
            RemoveCommand = new RelayCommand(() => _actions?.Remove(this));
            StartCommand = new RelayCommand(() => _actions?.StartAgain(this));
        }

        /// <summary>The plugin this row shows, the plugins' part's own
        /// object.</summary>
        internal object Run { get; }

        /// <summary>The row's place, for the ids of its controls.</summary>
        public int Number { get; }

        /// <summary>The settings it declared, as the card draws them.</summary>
        public PluginSettingsHost Settings { get; }

        /// <summary>The key its fold's open state is kept under: its
        /// file.</summary>
        public string FoldKey => "Extras.Plugins." + (Path ?? Number.ToString(CultureInfo.InvariantCulture));

        public string Name
        {
            get => _name;
            private set => SetProperty(ref _name, value);
        }

        /// <summary>The file, for the row's tooltip.</summary>
        public string Path
        {
            get => _path;
            private set => SetProperty(ref _path, value);
        }

        /// <summary>Where it stands, in a few words.</summary>
        public string State
        {
            get => _state;
            private set => SetProperty(ref _state, value);
        }

        /// <summary>The plugin's switch. The user's change goes to the
        /// plugins' part.</summary>
        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (!SetProperty(ref _isOn, value) || _updating) return;
                _actions?.SetEnabled(this, value);
            }
        }

        public bool ShowStart
        {
            get => _showStart;
            private set => SetProperty(ref _showStart, value);
        }

        public bool ShowApprove
        {
            get => _showApprove;
            private set => SetProperty(ref _showApprove, value);
        }

        public RelayCommand RemoveCommand { get; }
        public RelayCommand StartCommand { get; }

        public string SwitchId => "Plugins_Switch_" + Number.ToString(CultureInfo.InvariantCulture);
        public string RemoveId => "Plugins_Remove_" + Number.ToString(CultureInfo.InvariantCulture);
        public string StartId => "Plugins_Start_" + Number.ToString(CultureInfo.InvariantCulture);
        public string ApproveId => "Plugins_Approve_" + Number.ToString(CultureInfo.InvariantCulture);
        public string ResetAllId => "Plugins_ResetAll_" + Number.ToString(CultureInfo.InvariantCulture);
        public string FoldId => "Plugins_Card_" + Number.ToString(CultureInfo.InvariantCulture);

        /// <summary>The plugins' part sets what the row shows. A switch set
        /// here is not sent back as the user's change.</summary>
        internal void Show(string name, string path, string state, bool on, bool showStart, bool showApprove)
        {
            _updating = true;
            try
            {
                Name = name;
                Path = path;
                State = state;
                IsOn = on;
                ShowStart = showStart;
                ShowApprove = showApprove;
            }
            finally { _updating = false; }
        }

        /// <summary>The name a screen reader says for the row's item in the
        /// log's list.</summary>
        public override string ToString() => Name ?? string.Empty;
    }
}
