using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
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

    /// <summary>One place a check names: its state (current, older or
    /// missing), what it is for (python:3.14, pwsh, powershell, autohotkey)
    /// and its file.</summary>
    internal readonly record struct ScriptLibraryPlace(string State, string For, string File);

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
        private Func<string> _lineOf;
        private ScriptLibraryButton _button = ScriptLibraryButton.None;
        private bool _busy;

        internal ScriptLibraryRow(AutomationLibraryKind kind, Func<ScriptLibraryRow, Task> press)
        {
            Kind = kind;
            PressCommand = new AsyncRelayCommand(() => _busy || _button == ScriptLibraryButton.None ? Task.CompletedTask : press(this));
            _lineOf = () => Strings.Instance.Extras_Automation_Scripts_Checking;
            _line = _lineOf();
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
            private set => SetProperty(ref _line, value);
        }

        /// <summary>Sets the line from words that are read again in the
        /// language of the moment at each change of language
        /// (<see cref="Retell"/>).</summary>
        internal void Say(Func<string> line)
        {
            _lineOf = line;
            Line = line();
        }

        /// <summary>The row's line, button words and name again, in the
        /// language now.</summary>
        internal void Retell()
        {
            if (_lineOf != null) Line = _lineOf();
            OnPropertyChanged(nameof(ButtonWords));
            OnPropertyChanged(nameof(Name));
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
        internal IReadOnlyList<ScriptLibraryPlace> Places { get; set; } = Array.Empty<ScriptLibraryPlace>();

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
        private Func<string> _noteOf;
        private bool _anyInstalled;

        public ScriptLibraryRows()
        {
            foreach (var library in AutomationLibraries.All)
                Rows.Add(new ScriptLibraryRow(library.Kind, PressAsync));
            RemoveAllCommand = new AsyncRelayCommand(RemoveAllAsync);
            // The card's words follow a change of language: the page and this
            // model stay while the language changes (Strings holds this
            // handler weakly).
            Strings.CultureChanged += Retell;
        }

        /// <summary>Every row's words and the note again, in the language
        /// now.</summary>
        private void Retell()
        {
            foreach (var row in Rows) row.Retell();
            if (_noteOf != null) Note = _noteOf();
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

        private Task<InstallRun> Run(InstallRequest request) => (RunSeam ?? UserInstall.RunInCopyAsync)(request);

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

        /// <summary>
        /// A check's answer as the row's line and button. The line names the
        /// library and what it was found for in each state, by the places
        /// the check named: the Pythons it is current for, older for and
        /// missing for, so a row named Python never reads as if Python were
        /// what is missing. No Python found and Pythons that did not answer
        /// have lines of their own, apart from a check that failed.
        /// </summary>
        internal static void Show(ScriptLibraryRow row, InstallRun run)
        {
            var s = Strings.Instance;
            string F(string format, params object[] values) => string.Format(CultureInfo.CurrentCulture, format, values);
            if (run.Code is UserInstall.NoPython or UserInstall.NoPythonAnswered)
            {
                row.Places = Array.Empty<ScriptLibraryPlace>();
                row.Say(() => run.Code == UserInstall.NoPython
                    ? s.Extras_Automation_Reason_NoPython
                    : F(s.Extras_Automation_Reason_NoPythonAnswered_Format, UserInstall.PythonMs / 1000));
                row.Button = ScriptLibraryButton.None;
                return;
            }
            var places = UserInstall.IsAnswer(run.Code) ? Places(run.Said) : Array.Empty<ScriptLibraryPlace>();
            string current = Names(places, "current"), older = Names(places, "older"), missing = Names(places, "missing");
            if (current.Length + older.Length + missing.Length == 0)
            {
                // A check that failed, or an answer that named no place,
                // which no check of this PadForge gives.
                row.Places = Array.Empty<ScriptLibraryPlace>();
                var failed = UserInstall.IsAnswer(run.Code) ? run with { Reason = InstallReason.Other } : run;
                row.Say(() => F(s.Extras_Automation_Scripts_CheckFailed_Format, ReasonText(failed)));
                row.Button = ScriptLibraryButton.None;
                return;
            }
            row.Places = places;
            row.Say(() => (current.Length > 0, older.Length > 0, missing.Length > 0) switch
            {
                (true, false, false) => F(s.Extras_Automation_Scripts_State_Current_Format, current, row.StartsWith),
                (false, true, false) => F(s.Extras_Automation_Scripts_State_Older_Format, older),
                (false, false, true) => F(s.Extras_Automation_Scripts_State_Missing_Format, missing),
                (true, true, false) => F(s.Extras_Automation_Scripts_State_CurrentOlder_Format, current, older),
                (true, false, true) => F(s.Extras_Automation_Scripts_State_CurrentMissing_Format, current, missing),
                (false, true, true) => F(s.Extras_Automation_Scripts_State_OlderMissing_Format, older, missing),
                _ => F(s.Extras_Automation_Scripts_State_CurrentOlderMissing_Format, current, older, missing),
            });
            // Update writes every place over an older copy, Install every
            // place where one is missing.
            row.Button = older.Length > 0 ? ScriptLibraryButton.Update
                : missing.Length > 0 ? ScriptLibraryButton.Install
                : ScriptLibraryButton.OpenFolder;
        }

        /// <summary>
        /// Why a run did not finish, as a whole sentence in the user's
        /// language, from the reason it named (<see cref="InstallRun.Reason"/>),
        /// never the run's own English text: the Scripts card's lines and the
        /// assistant rows' both take it. A reason with no file or program to
        /// name, and one this PadForge does not know, read as Other.
        /// </summary>
        internal static string ReasonText(InstallRun run)
        {
            var s = Strings.Instance;
            string F(string format, params object[] values) => string.Format(CultureInfo.CurrentCulture, format, values);
            string detail = PadForge.Engine.Common.ShownText.Clean(run.Detail ?? string.Empty).Trim();
            string program = AssistantHosts.Find(run.Detail)?.Name ?? detail;
            return run.Reason switch
            {
                InstallReason.NotStarted => s.Extras_Automation_Reason_NotStarted,
                InstallReason.TimedOut => F(s.Extras_Automation_Reason_TimedOut_Format, run.Number),
                InstallReason.ExitCode => F(s.Extras_Automation_Reason_ExitCode_Format, run.Number),
                InstallReason.SettingsUnreadable when detail.Length > 0 => F(s.Extras_Automation_Reason_SettingsUnreadable_Format, detail),
                InstallReason.NotOurs when detail.Length > 0 => F(s.Extras_Automation_Reason_NotOurs_Format, detail),
                InstallReason.NoDocuments => s.Extras_Automation_Reason_NoDocuments,
                InstallReason.NoUserFolder when program.Length > 0 => F(s.Extras_Automation_Reason_NoUserFolder_Format, program),
                InstallReason.HostCommandFailed when program.Length > 0 => F(s.Extras_Automation_Reason_HostCommandFailed_Format, program, run.Number),
                InstallReason.HostCommandMissing when program.Length > 0 => F(s.Extras_Automation_Reason_HostCommandMissing_Format, program),
                InstallReason.FileRefused => s.Extras_Automation_Reason_FileRefused,
                InstallReason.NoPython => s.Extras_Automation_Reason_NoPython,
                InstallReason.NoPythonAnswered => F(s.Extras_Automation_Reason_NoPythonAnswered_Format, run.Number > 0 ? run.Number : UserInstall.PythonMs / 1000),
                _ => s.Extras_Automation_Reason_Other,
            };
        }

        /// <summary>The places a check's report names, each line
        /// <c>place &lt;state&gt; &lt;for&gt; &lt;file&gt;</c>.</summary>
        internal static IReadOnlyList<ScriptLibraryPlace> Places(string said)
        {
            var list = new List<ScriptLibraryPlace>();
            if (string.IsNullOrEmpty(said)) return list;
            foreach (string raw in said.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (!line.StartsWith("place ", StringComparison.Ordinal)) continue;
                string[] words = line.Split(' ', 4);
                if (words.Length < 4 || words[1] is not ("current" or "older" or "missing")) continue;
                if (!ProgramIcon.IsLocalPath(words[3])) continue;
                list.Add(new ScriptLibraryPlace(words[1], words[2], words[3]));
            }
            return list;
        }

        /// <summary>What the places in a state are for, by name and apart by
        /// commas, each once: Python 3.14 for a Python that named itself
        /// 3.14, PowerShell 7, Windows PowerShell or AutoHotkey v2.</summary>
        internal static string Names(IReadOnlyList<ScriptLibraryPlace> places, string state)
        {
            var names = new List<string>();
            foreach (var place in places)
            {
                if (place.State != state) continue;
                string name = NameOf(place);
                if (!names.Contains(name)) names.Add(name);
            }
            return string.Join(", ", names);
        }

        private static string NameOf(ScriptLibraryPlace place) => place.For switch
        {
            "pwsh" => "PowerShell 7",
            "powershell" => "Windows PowerShell",
            "autohotkey" => "AutoHotkey v2",
            _ when place.For.StartsWith("python:", StringComparison.Ordinal) && place.For.Length > 7 => "Python " + place.For.Substring(7),
            _ => place.File,
        };

        private async Task PressAsync(ScriptLibraryRow row)
        {
            if (row.Button == ScriptLibraryButton.OpenFolder)
            {
                string folder = row.Places.Where(p => p.State != "missing").Select(p => Path.GetDirectoryName(p.File)).FirstOrDefault();
                if (folder != null && Directory.Exists(folder))
                {
                    if (OpenSeam != null) OpenSeam(folder);
                    else PadForge.Common.ShellLaunch.TryStart(new System.Diagnostics.ProcessStartInfo(
                        PadForge.Common.ShellLaunch.Explorer, "\"" + folder + "\""), out _);
                }
                return;
            }
            row.Busy = true;
            row.Say(() => Strings.Instance.Extras_Automation_Scripts_Installing);
            try
            {
                var run = await Run(new InstallRequest(WhatOf(row.Kind), null)).ConfigureAwait(true);
                if (run.Code != UserInstall.Done)
                {
                    row.Say(() => string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_State_Failed_Format, ReasonText(run)));
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
            var failed = new List<(string Name, InstallRun Run)>();
            foreach (var row in Rows.Where(r => r.Places.Any(p => p.State != "missing")).ToList())
            {
                row.Busy = true;
                try
                {
                    var run = await Run(new InstallRequest(WhatOf(row.Kind), null, Remove: true)).ConfigureAwait(true);
                    if (run.Code != UserInstall.Done) failed.Add((row.Name, run));
                }
                finally
                {
                    row.Busy = false;
                }
            }
            _noteOf = () => failed.Count == 0
                ? Strings.Instance.Extras_Automation_Scripts_Removed
                : string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_State_Failed_Format,
                    string.Join(" ", failed.Select(f => f.Name + ": " + ReasonText(f.Run))));
            Note = _noteOf();
            await RefreshAsync().ConfigureAwait(true);
        }
    }
}
