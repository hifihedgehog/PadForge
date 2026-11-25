using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.Services.Install;

namespace PadForge.ViewModels
{
    /// <summary>What a library row's one button does.</summary>
    public enum ScriptLibraryButton { Install, Update, OpenFolder, None }

    /// <summary>
    /// One script library on the Scripts card (#600 item 26): AutoHotkey v2,
    /// Python or PowerShell, whether PadForge's copy is installed where that
    /// language finds it, current or older, and one button: Install, Update
    /// or Open Folder. The state comes from <c>PadForge.exe --install
    /// &lt;library&gt; --check</c> run with the user's own rights (item 4),
    /// never from this process, whose token may be another account's.
    /// </summary>
    public sealed class ScriptLibraryRow : ObservableObject
    {
        private string _line;
        private ScriptLibraryButton _button = ScriptLibraryButton.None;
        private bool _busy;

        internal ScriptLibraryRow(AutomationLibraryKind kind, Func<ScriptLibraryRow, Task> press)
        {
            Kind = kind;
            PressCommand = new AsyncRelayCommand(() => _busy || _button == ScriptLibraryButton.None ? Task.CompletedTask : press(this));
            _line = Strings.Instance.Extras_Automation_Scripts_Checking;
        }

        internal AutomationLibraryKind Kind { get; }

        public string Name => AutomationLibraries.Of(Kind).Name;

        /// <summary>The line a script starts with to load the library.</summary>
        internal string StartsWith => Kind switch
        {
            AutomationLibraryKind.Python => "import padforge",
            AutomationLibraryKind.PowerShell => "Import-Module PadForge",
            _ => "#Include <PadForge>",
        };

        public string Line
        {
            get => _line;
            internal set => SetProperty(ref _line, value);
        }

        public ScriptLibraryButton Button
        {
            get => _button;
            internal set
            {
                if (!SetProperty(ref _button, value)) return;
                OnPropertyChanged(nameof(ButtonWords));
                OnPropertyChanged(nameof(HasButton));
            }
        }

        public string ButtonWords => _button switch
        {
            ScriptLibraryButton.Install => Strings.Instance.Common_Install,
            ScriptLibraryButton.Update => Strings.Instance.Extras_Automation_Update,
            ScriptLibraryButton.OpenFolder => Strings.Instance.Settings_OpenFolder,
            _ => string.Empty,
        };

        public bool HasButton => _button != ScriptLibraryButton.None;

        /// <summary>False while a check or an install runs.</summary>
        public bool IsIdle => !_busy;

        internal bool Busy
        {
            get => _busy;
            set
            {
                if (_busy == value) return;
                _busy = value;
                OnPropertyChanged(nameof(IsIdle));
            }
        }

        /// <summary>Each place the library goes and its state there, from
        /// the last check: current, older or missing.</summary>
        internal IReadOnlyList<(string State, string File)> Places { get; set; } = Array.Empty<(string, string)>();

        /// <summary>The automation ID of the row's button.</summary>
        public string ButtonId => "Automation_Scripts_" + Kind;

        public IAsyncRelayCommand PressCommand { get; }
    }

    /// <summary>
    /// The Scripts card (#600 item 26): a row for each library, checked
    /// each time the card shows, and under More, Save a Library… for a
    /// folder of the user's choosing and Remove All for the copies
    /// installed.
    /// </summary>
    public sealed class ScriptLibraryRows : ObservableObject
    {
        private string _note;
        private bool _anyInstalled;

        public ScriptLibraryRows()
        {
            foreach (var library in AutomationLibraries.All)
                Rows.Add(new ScriptLibraryRow(library.Kind, PressAsync));
            RemoveAllCommand = new AsyncRelayCommand(RemoveAllAsync);
        }

        public ObservableCollection<ScriptLibraryRow> Rows { get; } = new();

        public IAsyncRelayCommand RemoveAllCommand { get; }

        /// <summary>A line under More: what Remove All did.</summary>
        public string Note
        {
            get => _note;
            private set
            {
                if (SetProperty(ref _note, value)) OnPropertyChanged(nameof(HasNote));
            }
        }

        public bool HasNote => !string.IsNullOrEmpty(_note);

        /// <summary>Whether any library is installed, for Remove All.</summary>
        public bool AnyInstalled
        {
            get => _anyInstalled;
            private set => SetProperty(ref _anyInstalled, value);
        }

        /// <summary>Test seam: runs an install, a check or a removal. Null
        /// runs <c>PadForge.exe --install</c> with the user's own rights.</summary>
        internal Func<InstallRequest, Task<InstallRun>> RunSeam;

        /// <summary>Test seam: opens a folder. Null opens it in Explorer
        /// through the desktop shell, with the user's own rights.</summary>
        internal Func<string, bool> OpenSeam;

        private Task<InstallRun> Run(InstallRequest request) => (RunSeam ?? UserInstall.RunAsUserAsync)(request);

        private static InstallWhat WhatOf(AutomationLibraryKind kind) => AutomationLibraries.WhatOf(kind);

        /// <summary>Checks every library, all at once, each in its own run
        /// as the user.</summary>
        public async Task RefreshAsync()
        {
            var rows = Rows.Where(r => !r.Busy).ToList();
            await Task.WhenAll(rows.Select(CheckAsync)).ConfigureAwait(true);
            AnyInstalled = Rows.Any(r => r.Places.Any(p => p.State != "missing"));
        }

        private async Task CheckAsync(ScriptLibraryRow row)
        {
            row.Busy = true;
            try
            {
                var run = await Run(new InstallRequest(WhatOf(row.Kind), null, Check: true)).ConfigureAwait(true);
                Show(row, run);
            }
            finally
            {
                row.Busy = false;
            }
        }

        /// <summary>A check's answer as the row's line and button.</summary>
        internal static void Show(ScriptLibraryRow row, InstallRun run)
        {
            var s = Strings.Instance;
            if (run.Code is not (UserInstall.Done or UserInstall.IsOlder or UserInstall.IsMissing))
            {
                row.Places = Array.Empty<(string, string)>();
                row.Line = string.Format(CultureInfo.CurrentCulture, s.Extras_Automation_Scripts_CheckFailed_Format,
                    PadForge.Engine.Common.ShownText.Clean(run.Error ?? run.Said ?? string.Empty).Trim());
                row.Button = ScriptLibraryButton.None;
                return;
            }
            row.Places = Places(run.Said);
            string pythons = row.Kind == AutomationLibraryKind.Python ? Pythons(row.Places) : null;
            switch (run.Code)
            {
                case UserInstall.Done:
                    row.Line = string.IsNullOrEmpty(pythons)
                        ? string.Format(CultureInfo.CurrentCulture, s.Extras_Automation_Scripts_Current_Format, row.StartsWith)
                        : string.Format(CultureInfo.CurrentCulture, s.Extras_Automation_Scripts_CurrentFor_Format, pythons, row.StartsWith);
                    row.Button = ScriptLibraryButton.OpenFolder;
                    break;
                case UserInstall.IsOlder:
                    row.Line = string.IsNullOrEmpty(pythons)
                        ? s.Extras_Automation_Scripts_Older
                        : string.Format(CultureInfo.CurrentCulture, s.Extras_Automation_Scripts_OlderFor_Format, pythons);
                    row.Button = ScriptLibraryButton.Update;
                    break;
                default:
                    row.Line = s.Extras_Automation_Scripts_NotInstalled;
                    row.Button = ScriptLibraryButton.Install;
                    break;
            }
        }

        /// <summary>The places a check's report names, each line
        /// <c>place &lt;state&gt; &lt;file&gt;</c>.</summary>
        internal static IReadOnlyList<(string State, string File)> Places(string said)
        {
            var list = new List<(string, string)>();
            if (string.IsNullOrEmpty(said)) return list;
            foreach (string raw in said.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (!line.StartsWith("place ", StringComparison.Ordinal)) continue;
                int space = line.IndexOf(' ', 6);
                if (space < 0) continue;
                string file = line.Substring(space + 1);
                if (!ProgramIcon.IsLocalPath(file)) continue;
                list.Add((line.Substring(6, space - 6), file));
            }
            return list;
        }

        /// <summary>The Pythons a library is installed for, by the folder of
        /// each user site (…\Python314\site-packages is Python 3.14), apart
        /// by commas.</summary>
        internal static string Pythons(IReadOnlyList<(string State, string File)> places)
        {
            var names = new List<string>();
            foreach (var (state, file) in places)
            {
                if (state == "missing") continue;
                var match = Regex.Match(file, @"[\\/]Python(\d)(\d+)(?:-\d+)?[\\/]site-packages[\\/]", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                string name = "Python " + match.Groups[1].Value + "." + match.Groups[2].Value;
                if (!names.Contains(name)) names.Add(name);
            }
            return string.Join(", ", names);
        }

        private async Task PressAsync(ScriptLibraryRow row)
        {
            if (row.Button == ScriptLibraryButton.OpenFolder)
            {
                string folder = row.Places.Where(p => p.State != "missing").Select(p => Path.GetDirectoryName(p.File)).FirstOrDefault();
                if (folder != null && Directory.Exists(folder))
                {
                    if (OpenSeam != null) OpenSeam(folder);
                    else PadForge.Common.ShellLaunch.AsTheUser(new System.Diagnostics.ProcessStartInfo(
                        PadForge.Common.ShellLaunch.Explorer, "\"" + folder + "\""), out _);
                }
                return;
            }
            row.Busy = true;
            row.Line = Strings.Instance.Extras_Automation_Scripts_Installing;
            try
            {
                var run = await Run(new InstallRequest(WhatOf(row.Kind), null)).ConfigureAwait(true);
                if (run.Code != UserInstall.Done)
                {
                    row.Line = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_State_Failed_Format,
                        PadForge.Engine.Common.ShownText.Clean(run.Error ?? string.Empty).Trim());
                    return;
                }
            }
            finally
            {
                row.Busy = false;
            }
            await CheckAsync(row).ConfigureAwait(true);
            AnyInstalled = Rows.Any(r => r.Places.Any(p => p.State != "missing"));
        }

        /// <summary>Remove All: each installed copy goes, through the same
        /// run as the user. A file of that name that PadForge did not write
        /// stays.</summary>
        private async Task RemoveAllAsync()
        {
            var failed = new List<string>();
            foreach (var row in Rows.Where(r => r.Places.Any(p => p.State != "missing")).ToList())
            {
                row.Busy = true;
                try
                {
                    var run = await Run(new InstallRequest(WhatOf(row.Kind), null, Remove: true)).ConfigureAwait(true);
                    if (run.Code != UserInstall.Done)
                        failed.Add(row.Name + ": " + PadForge.Engine.Common.ShownText.Clean(run.Error ?? string.Empty).Trim());
                }
                finally
                {
                    row.Busy = false;
                }
            }
            Note = failed.Count == 0
                ? Strings.Instance.Extras_Automation_Scripts_Removed
                : string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_State_Failed_Format, string.Join(" ", failed));
            await RefreshAsync().ConfigureAwait(true);
        }
    }
}
