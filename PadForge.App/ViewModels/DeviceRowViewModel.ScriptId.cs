using System;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;

namespace PadForge.ViewModels
{
    public partial class DeviceRowViewModel
    {
        private IRelayCommand _copyScriptIdCommand;
        private string _scriptIdNote;

        /// <summary>The device's script ID (#600 item 26): the short ID a
        /// script finds it by, the same in every run, made from its identity
        /// and naming nothing of its serial number or path.</summary>
        public string ScriptId => AutomationDevices.IdOf(InstanceGuid);

        /// <summary>The device's controls by the names scripts use, apart by
        /// commas, under its Test Inputs' More.</summary>
        public string ScriptControlNames
        {
            get
            {
                var list = PadForge.Common.Input.SettingsManager.UserDevices;
                if (list == null) return string.Empty;
                lock (list.SyncRoot)
                {
                    foreach (var ud in list.Items)
                        if (ud != null && ud.InstanceGuid == InstanceGuid)
                        {
                            // A keyboard's 256 keys read as one sentence.
                            if (PadForge.Common.Recording.RecordingKinds.Of(ud) == PadForge.Engine.Recording.RecordingControlKind.Keyboard)
                                return Strings.Instance.Devices_ScriptKeys;
                            string names = ScriptNames.List(ud);
                            return names.Length == 0 ? string.Empty
                                : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Instance.Devices_ScriptNames_Format, names);
                        }
                }
                return string.Empty;
            }
        }

        /// <summary>Whether the device has controls to name.</summary>
        public bool HasScriptControlNames => ScriptControlNames.Length > 0;

        /// <summary>Copies the script ID, and says so beside it.</summary>
        public IRelayCommand CopyScriptIdCommand => _copyScriptIdCommand ??= new RelayCommand(() =>
        {
            PadForge.Common.ShellLaunch.SetClipboardText(ScriptId);
            ScriptIdNote = Strings.Instance.Extras_Automation_Copied;
        });

        /// <summary>"Copied." after Copy, until the device shown changes
        /// (<see cref="ClearScriptIdNote"/>).</summary>
        public string ScriptIdNote
        {
            get => _scriptIdNote;
            private set => SetProperty(ref _scriptIdNote, value);
        }

        /// <summary>Takes the note away: the Devices page shows another
        /// device.</summary>
        internal void ClearScriptIdNote() => ScriptIdNote = null;
    }
}
