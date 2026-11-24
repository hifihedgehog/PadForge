using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The Plugins extra's switch and state (#597). A plugin is a program
    /// that adds devices to PadForge. The switch is saved for the app, never
    /// in a profile, as Automation's is. The plugin manager sets the state:
    /// until it does, the line says only whether the switch is on.
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
                if (SetProperty(ref _enablePlugins, value) && _pluginsStatus == null)
                    OnPropertyChanged(nameof(PluginsStatus));
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
    }
}
