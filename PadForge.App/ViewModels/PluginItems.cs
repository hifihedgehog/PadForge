using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// One plugin's row on the Plugins page (#597): the name it gave, or its
    /// file's name until it has run, its state in a few words, its switch,
    /// and the buttons its state calls for: Start after five failures or a
    /// refused start, Approve for a file that changed, and Remove.
    /// </summary>
    public sealed class PluginRowItem : ObservableObject
    {
        private readonly IPluginsPageActions _actions;
        private string _name, _state, _path;
        private bool _isOn, _showStart, _showApprove, _updating;

        internal PluginRowItem(object run, int number, IPluginsPageActions actions)
        {
            Run = run;
            Number = number;
            _actions = actions;
            RemoveCommand = new RelayCommand(() => _actions?.Remove(this));
            StartCommand = new RelayCommand(() => _actions?.StartAgain(this));
        }

        /// <summary>The plugin this row shows, the plugins' part's own
        /// object.</summary>
        internal object Run { get; }

        /// <summary>The row's place, for the ids of its controls.</summary>
        public int Number { get; }

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
