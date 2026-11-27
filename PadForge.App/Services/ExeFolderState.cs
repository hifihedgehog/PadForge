using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>The folder PadForge runs from, as Settings shows it.</summary>
    public sealed class ExeFolderState : ObservableObject
    {
        public static ExeFolderState Instance { get; } = new();

        /// <summary>The line this state formats in code follows a language
        /// change (Strings.CultureChanged, which holds this weakly).</summary>
        public ExeFolderState()
        {
            Strings.CultureChanged += OnCultureChanged;
        }

        private void OnCultureChanged() => OnPropertyChanged(nameof(RunsFromText));

        private string _exePath = string.Empty;

        /// <summary>The exe this copy runs as.</summary>
        public string ExePath
        {
            get => _exePath;
            private set
            {
                if (SetProperty(ref _exePath, value ?? string.Empty)) OnPropertyChanged(nameof(RunsFromText));
            }
        }

        /// <summary>"PadForge runs from …", shown in Settings.</summary>
        public string RunsFromText => ExePath.Length == 0 ? string.Empty : string.Format(Strings.Instance.ExeFolder_RunsFrom_Format, ExePath);

        /// <summary>Notes the exe this copy runs as.</summary>
        internal void Start(string exe) => ExePath = exe;
    }
}
