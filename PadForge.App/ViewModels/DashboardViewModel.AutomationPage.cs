using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;

namespace PadForge.ViewModels
{
    /// <summary>
    /// What the Automation extra's page shows beyond its switch (#597): the
    /// level as a choice of four with one line on what it allows, the block
    /// an assistant's settings take, the questions that wait for the user,
    /// and the record of what programs asked. The host's lists change on
    /// workers, and the page's copies follow on the thread the host was
    /// attached on.
    /// </summary>
    public partial class DashboardViewModel
    {
        private AutomationHost _automationHost;
        private Dispatcher _automationDispatcher;
        private int _automationActivityQueued;

        /// <summary>Hands the page Automation's host, once, on the
        /// interface's thread. The host's lists and the parts later builds
        /// provide show from then on.</summary>
        internal void AttachAutomation(AutomationHost host, Dispatcher dispatcher)
        {
            if (host == null || _automationHost != null) return;
            _automationHost = host;
            _automationDispatcher = dispatcher;
            host.Activity.Changed += (_, _) =>
            {
                // A busy script can call hundreds of times a second: one
                // pass on the interface's thread takes in every call so far.
                if (Interlocked.Exchange(ref _automationActivityQueued, 1) == 0)
                    OnAutomationThread(ShowAutomationActivity);
            };
            host.Approvals.PendingChanged += (_, _) => OnAutomationThread(ShowAutomationRequests);
            host.ProvidedChanged += (_, _) => OnAutomationThread(ShowAutomationProvided);
            ShowAutomationActivity();
            ShowAutomationRequests();
            ShowAutomationProvided();
        }

        private void OnAutomationThread(Action show)
        {
            var dispatcher = _automationDispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) show();
            else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(show);
        }

        // ─────────────────────────────────────────────
        //  The level, as a choice of four
        // ─────────────────────────────────────────────

        /// <summary>The four levels in their order, which is their saved
        /// number's.</summary>
        public IReadOnlyList<string> AutomationLevelNames => new[]
        {
            Strings.Instance.Extras_Automation_Level_Commands,
            Strings.Instance.Extras_Automation_Level_Read,
            Strings.Instance.Extras_Automation_Level_SendInputs,
            Strings.Instance.Extras_Automation_Level_ChangeSettings,
        };

        /// <summary>The level as the choice's place in
        /// <see cref="AutomationLevelNames"/>.</summary>
        public int AutomationLevelIndex
        {
            get => (int)AutomationLevel;
            set
            {
                if (value >= (int)AutomationLevel.Commands && value <= (int)AutomationLevel.ChangeSettings)
                    AutomationLevel = (AutomationLevel)value;
            }
        }

        /// <summary>What the chosen level allows a program, in the issue's
        /// own words, one line under the choice.</summary>
        public string AutomationLevelLine
        {
            get
            {
                var s = Strings.Instance;
                return AutomationLevel switch
                {
                    AutomationLevel.Read => s.Extras_Automation_LevelLine_Read,
                    AutomationLevel.SendInputs => s.Extras_Automation_LevelLine_SendInputs,
                    AutomationLevel.ChangeSettings => s.Extras_Automation_LevelLine_ChangeSettings,
                    _ => s.Extras_Automation_LevelLine_Commands,
                };
            }
        }

        // ─────────────────────────────────────────────
        //  Assistants
        // ─────────────────────────────────────────────

        private string _automationExePath = Environment.ProcessPath;

        /// <summary>The executable an assistant starts: this one. A test
        /// gives its own.</summary>
        internal string AutomationExePath
        {
            get => _automationExePath;
            set
            {
                if (_automationExePath == value) return;
                _automationExePath = value;
                OnPropertyChanged(nameof(AutomationAssistantJson));
                OnPropertyChanged(nameof(AutomationAssistantToml));
            }
        }

        /// <summary>The block an assistant's MCP settings take: this exe as
        /// the command, with <c>--mcp</c>, in the JSON form most assistants
        /// read.</summary>
        public string AutomationAssistantJson => AssistantJson(_automationExePath ?? string.Empty);

        /// <summary>The same in TOML, for an assistant whose settings file is
        /// TOML: the table <c>mcp_servers.padforge</c>.</summary>
        public string AutomationAssistantToml => AssistantToml(_automationExePath ?? string.Empty);

        internal static string AssistantJson(string exe)
            => "{\n  \"mcpServers\": {\n    \"padforge\": { \"command\": " + QuotedString(exe) + ", \"args\": [\"--mcp\"] }\n  }\n}";

        internal static string AssistantToml(string exe)
            => "[mcp_servers.padforge]\ncommand = " + QuotedString(exe) + "\nargs = [\"--mcp\"]";

        /// <summary>A string as JSON and a TOML basic string both write it:
        /// the backslash, the quote and the control characters escaped, as
        /// both specs list them, and every other letter as it is, so a path
        /// reads as the user wrote it.</summary>
        internal static string QuotedString(string text)
        {
            var b = new StringBuilder(text.Length + 2).Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\\': b.Append("\\\\"); break;
                    case '"': b.Append("\\\""); break;
                    case '\b': b.Append("\\b"); break;
                    case '\t': b.Append("\\t"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\f': b.Append("\\f"); break;
                    case '\r': b.Append("\\r"); break;
                    default:
                        if (c < 0x20 || c == 0x7F) b.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        else b.Append(c);
                        break;
                }
            }
            return b.Append('"').ToString();
        }

        private RelayCommand _copyAutomationJsonCommand;
        public RelayCommand CopyAutomationJsonCommand => _copyAutomationJsonCommand ??= new RelayCommand(() =>
        {
            try { PadForge.Common.ShellLaunch.SetClipboardText(AutomationAssistantJson); } catch { }
        });

        private RelayCommand _copyAutomationTomlCommand;
        public RelayCommand CopyAutomationTomlCommand => _copyAutomationTomlCommand ??= new RelayCommand(() =>
        {
            try { PadForge.Common.ShellLaunch.SetClipboardText(AutomationAssistantToml); } catch { }
        });

        // ─────────────────────────────────────────────
        //  What later parts provide
        // ─────────────────────────────────────────────

        /// <summary>The script libraries, or null in a build that carries
        /// none.</summary>
        internal IAutomationLibraries AutomationLibraries => _automationHost?.Libraries;

        /// <summary>The call that writes the skill's folder, or null in a
        /// build that has none.</summary>
        internal Action<string> AutomationSaveSkill => _automationHost?.SaveSkill;

        /// <summary>Whether the Scripts card shows: the build carries the
        /// libraries.</summary>
        public bool HasAutomationLibraries => _automationHost?.Libraries != null;

        /// <summary>Whether More shows Save the Skill: the build can write
        /// it.</summary>
        public bool CanSaveAutomationSkill => _automationHost?.SaveSkill != null;

        /// <summary>The More row's hint: what it holds, which is the TOML
        /// copy, and the skill while the build can write it.</summary>
        public string AutomationMoreHint => CanSaveAutomationSkill
            ? Strings.Instance.Extras_More_Automation_WithSkill
            : Strings.Instance.Extras_More_Automation;

        private void ShowAutomationProvided()
        {
            OnPropertyChanged(nameof(HasAutomationLibraries));
            OnPropertyChanged(nameof(CanSaveAutomationSkill));
            OnPropertyChanged(nameof(AutomationMoreHint));
            RefreshAutomationUndo();
        }

        // ─────────────────────────────────────────────
        //  Waiting for You
        // ─────────────────────────────────────────────

        /// <summary>The questions that wait, oldest first. The page lists
        /// them all, and the request card shows the first.</summary>
        public ObservableCollection<AutomationRequestItem> AutomationRequests { get; } = new();

        /// <summary>Whether a question waits: the Waiting for You card
        /// shows.</summary>
        public bool HasAutomationRequests => AutomationRequests.Count > 0;

        private void ShowAutomationRequests()
        {
            var pending = _automationHost?.Approvals.Pending ?? Array.Empty<PendingApproval>();
            var waiting = new HashSet<int>();
            foreach (var p in pending) waiting.Add(p.Id);
            for (int i = AutomationRequests.Count - 1; i >= 0; i--)
                if (!waiting.Contains(AutomationRequests[i].Id)) AutomationRequests.RemoveAt(i);
            // What waits is oldest first, and a question only ever joins at
            // the end, so the ones kept are already in order.
            for (int i = 0; i < pending.Count; i++)
            {
                if (i < AutomationRequests.Count && AutomationRequests[i].Id == pending[i].Id) continue;
                AutomationRequests.Insert(i, new AutomationRequestItem(pending[i], AnswerAutomationRequest));
            }
            OnPropertyChanged(nameof(HasAutomationRequests));
        }

        private bool AnswerAutomationRequest(int id, bool allowed)
            => _automationHost != null && _automationHost.Approvals.Answer(id, allowed);

        // ─────────────────────────────────────────────
        //  Activity
        // ─────────────────────────────────────────────

        /// <summary>What programs asked, newest first.</summary>
        public ObservableCollection<AutomationActivityItem> AutomationActivity { get; } = new();

        /// <summary>Whether the record holds a call. While it holds none, the
        /// card says so in the list's place.</summary>
        public bool HasAutomationActivity => AutomationActivity.Count > 0;

        private RelayCommand _clearAutomationActivityCommand;

        /// <summary>Empties the record.</summary>
        public RelayCommand ClearAutomationActivityCommand => _clearAutomationActivityCommand ??= new RelayCommand(() =>
        {
            if (_automationHost != null) _automationHost.Activity.Clear();
            else
            {
                AutomationActivity.Clear();
                OnPropertyChanged(nameof(HasAutomationActivity));
            }
        });

        /// <summary>Brings the list in step with the record, newest first.
        /// The record adds at its end, and an entry can leave from anywhere
        /// in it: a merged one moves to the end with its new count, and one
        /// a program gave up goes (AutomationActivity). An item stays while
        /// its entry does, so a row on screen is not drawn again.</summary>
        private void ShowAutomationActivity()
        {
            Interlocked.Exchange(ref _automationActivityQueued, 0);
            var record = _automationHost?.Activity.Snapshot() ?? Array.Empty<AutomationActivityEntry>();
            var items = new Dictionary<AutomationActivityEntry, AutomationActivityItem>(ReferenceEqualityComparer.Instance);
            foreach (var item in AutomationActivity) items[item.Entry] = item;
            var wanted = new List<AutomationActivityItem>(record.Count);
            for (int i = record.Count - 1; i >= 0; i--)
                wanted.Add(items.TryGetValue(record[i], out var item) ? item : new AutomationActivityItem(record[i], UndoAutomationEntry));
            var keep = new HashSet<AutomationActivityItem>(wanted);
            for (int i = AutomationActivity.Count - 1; i >= 0; i--)
                if (!keep.Contains(AutomationActivity[i])) AutomationActivity.RemoveAt(i);
            for (int i = 0; i < wanted.Count; i++)
            {
                if (i < AutomationActivity.Count && ReferenceEquals(AutomationActivity[i], wanted[i])) continue;
                int at = AutomationActivity.IndexOf(wanted[i]);
                if (at >= 0) AutomationActivity.Move(at, i);
                else AutomationActivity.Insert(i, wanted[i]);
            }
            RefreshAutomationUndo();
            OnPropertyChanged(nameof(HasAutomationActivity));
        }

        /// <summary>Asks the host again which calls can be undone, from any
        /// thread: the undo journal says so when a run of changes starts or
        /// is undone (#597).</summary>
        internal void RefreshAutomationUndoFromHost()
        {
            var dispatcher = _automationDispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) RefreshAutomationUndo();
            else dispatcher.BeginInvoke(new System.Action(RefreshAutomationUndo));
        }

        /// <summary>Asks the host again which calls can be undone.</summary>
        private void RefreshAutomationUndo()
        {
            var canUndo = _automationHost?.Undo != null ? _automationHost.CanUndo : null;
            foreach (var item in AutomationActivity)
            {
                bool can = false;
                if (canUndo != null)
                {
                    try { can = canUndo(item.Entry); } catch { can = false; }
                }
                item.CanUndo = can;
            }
        }

        private void UndoAutomationEntry(AutomationActivityItem item)
        {
            var undo = _automationHost?.Undo;
            if (undo == null) return;
            try { undo(item.Entry); }
            finally { RefreshAutomationUndo(); }
        }

        /// <summary>The page's words in the new language. Called from
        /// OnCultureChanged.</summary>
        private void RefreshAutomationPageText()
        {
            OnPropertyChanged(nameof(AutomationLevelNames));
            OnPropertyChanged(nameof(AutomationLevelIndex));
            OnPropertyChanged(nameof(AutomationLevelLine));
            OnPropertyChanged(nameof(AutomationMoreHint));
            OnPropertyChanged(nameof(PluginsStatus));
            foreach (var item in AutomationActivity) item.RefreshText();
            // A question's words were made in the old language.
            AutomationRequests.Clear();
            ShowAutomationRequests();
        }
    }
}
