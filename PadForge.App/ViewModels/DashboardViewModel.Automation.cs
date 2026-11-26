using PadForge.Resources.Strings;
using PadForge.Services.Automation;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Automation's switch, level and state (#600). The one switch for every
    /// outside program: launchers, scripts and assistants. It replaces the
    /// External Control switch of #366, which the Profiles page held, and is
    /// saved for the app, never in a profile. The Profiles page's External
    /// Control card shows <see cref="AutomationAllowsLine"/> and has no
    /// switch of its own.
    /// </summary>
    public partial class DashboardViewModel
    {
        private bool _enableAutomation;

        /// <summary>Whether outside programs reach PadForge. Off until the
        /// user turns it on. The main window hands a change to the host
        /// (AutomationHost.Apply).</summary>
        public bool EnableAutomation
        {
            get => _enableAutomation;
            set
            {
                if (SetProperty(ref _enableAutomation, value))
                    OnPropertyChanged(nameof(AutomationAllowsLine));
            }
        }

        private AutomationLevel _automationLevel = AutomationLevel.Commands;

        /// <summary>What a program may do while Automation is on. Commands
        /// until the user raises it.</summary>
        public AutomationLevel AutomationLevel
        {
            get => _automationLevel;
            set
            {
                if (SetProperty(ref _automationLevel, value))
                    OnPropertyChanged(nameof(AutomationAllowsLine));
            }
        }

        private string _automationStatus = Strings.Instance.Common_Stopped;

        /// <summary>The host's line: running, stopped, or why programs
        /// cannot connect.</summary>
        public string AutomationStatus
        {
            get => _automationStatus;
            set => SetProperty(ref _automationStatus, value ?? Strings.Instance.Common_Stopped);
        }

        private bool _isAutomationRunning;

        /// <summary>Serving truth for a status flame: the control pipe
        /// answers programs, whatever the switch says.</summary>
        public bool IsAutomationRunning
        {
            get => _isAutomationRunning;
            set => SetProperty(ref _isAutomationRunning, value);
        }

        /// <summary>One line that says what the switch and the level in
        /// force allow a program to do, as far as profiles go and beyond.
        /// The Profiles page's External Control card prints it.</summary>
        public string AutomationAllowsLine
        {
            get
            {
                var s = Strings.Instance;
                if (!_enableAutomation) return s.Profiles_Automation_Off;
                return _automationLevel switch
                {
                    AutomationLevel.Read => s.Profiles_Automation_Read,
                    AutomationLevel.SendInputs => s.Profiles_Automation_SendInputs,
                    AutomationLevel.ChangeSettings => s.Profiles_Automation_ChangeSettings,
                    _ => s.Profiles_Automation_Commands,
                };
            }
        }

        /// <summary>The lines above in the new language. Called from
        /// OnCultureChanged. The status line is the host's to say again,
        /// which the main window asks it for.</summary>
        private void RefreshAutomationText() => OnPropertyChanged(nameof(AutomationAllowsLine));
    }
}
