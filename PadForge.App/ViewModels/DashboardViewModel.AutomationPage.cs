using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.Services.Catalog;
using PadForge.Services.Install;

namespace PadForge.ViewModels
{
    /// <summary>
    /// What the Automation extra's page shows beyond its switch (#597, #600):
    /// the level as a choice of four with one line on what it allows, a row
    /// for each assistant program on this PC with its state and one button,
    /// the copies for any other assistant, the questions that wait for the
    /// user, the programs the user allowed, and the record of what programs
    /// asked. Waiting for You, Allowed Programs and Activity show only while
    /// each holds something. The host's lists change on workers, and the
    /// page's copies follow on the thread the host was attached on.
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
            host.Clients.Changed += (_, _) => OnAutomationThread(ShowAssistantConnections);
            ShowAutomationActivity();
            ShowAutomationRequests();
            ShowAutomationProvided();
            ShowAssistantConnections();
        }

        private void OnAutomationThread(Action show)
        {
            var dispatcher = _automationDispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) show();
            else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(show);
        }

        /// <summary>The clock the page's times read: the host's.</summary>
        private DateTimeOffset AutomationNow() => _automationHost?.Now() ?? DateTimeOffset.Now;

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
        //  Any other assistant: Copy Settings and Copy a Message
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
            }
        }

        /// <summary>The block an assistant's MCP settings take: this exe as
        /// the command, with <c>--mcp</c>, in the JSON form most assistants
        /// read. Copy Settings puts it on the clipboard.</summary>
        public string AutomationAssistantJson => AssistantJson(_automationExePath ?? string.Empty);

        internal static string AssistantJson(string exe)
            => "{\n  \"mcpServers\": {\n    \"padforge\": { \"command\": " + QuotedString(exe) + ", \"args\": [\"--mcp\"] }\n  }\n}";

        /// <summary>A short text for any chat: how to connect to PadForge
        /// and what to ask first, and that Automation must be turned on
        /// while it is off. Copy a Message puts it on the clipboard.</summary>
        public string AutomationAssistantMessage
        {
            get
            {
                var s = Strings.Instance;
                string exe = _automationExePath ?? string.Empty;
                string text = string.Format(CultureInfo.CurrentCulture, s.Extras_Automation_Message_Format, exe, AssistantJson(exe).Replace("\n", " ").Replace("  ", " "));
                return EnableAutomation ? text : text + " " + s.Extras_Automation_Message_Off;
            }
        }

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

        /// <summary>Test seam: the clipboard. Null puts the text on the
        /// clipboard through ShellLaunch.</summary>
        internal Action<string> AutomationClipboardSeam;

        private string _automationCopyNote;

        /// <summary>What came of the last copy, beside its buttons:
        /// "Copied." or why not. Null before the first.</summary>
        public string AutomationCopyNote
        {
            get => _automationCopyNote;
            private set
            {
                if (SetProperty(ref _automationCopyNote, value)) OnPropertyChanged(nameof(HasAutomationCopyNote));
            }
        }

        public bool HasAutomationCopyNote => !string.IsNullOrEmpty(_automationCopyNote);

        private void CopyForAssistant(string text)
        {
            try
            {
                if (AutomationClipboardSeam != null) AutomationClipboardSeam(text);
                else ShellLaunch.SetClipboardText(text);
                AutomationCopyNote = Strings.Instance.Extras_Automation_Copied;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                // Another program holds the clipboard.
                AutomationCopyNote = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_NotCopied_Format, ex.Message);
            }
        }

        private RelayCommand _copyAutomationJsonCommand;
        public RelayCommand CopyAutomationJsonCommand => _copyAutomationJsonCommand ??= new RelayCommand(() => CopyForAssistant(AutomationAssistantJson));

        private RelayCommand _copyAutomationMessageCommand;
        public RelayCommand CopyAutomationMessageCommand => _copyAutomationMessageCommand ??= new RelayCommand(() => CopyForAssistant(AutomationAssistantMessage));

        // ─────────────────────────────────────────────
        //  The assistant rows
        // ─────────────────────────────────────────────

        /// <summary>Each assistant program on this PC whose maker documents
        /// its Windows setup, in AssistantHosts' order.</summary>
        public ObservableCollection<AssistantRowItem> AssistantRows { get; } = new();

        /// <summary>Test seam: runs an install as the user. Null runs it
        /// through UserInstall.RunAsUserAsync, the shell's route.</summary>
        internal Func<InstallRequest, Task<InstallRun>> AssistantInstallSeam;

        /// <summary>Test seam: a program's icon by its file. Null reads the
        /// icon from the file's resources.</summary>
        internal Func<string, ImageSource> AssistantIconSeam;

        private Task<InstallRun> RunAssistantInstall(InstallRequest request)
            => AssistantInstallSeam != null ? AssistantInstallSeam(request) : UserInstall.RunAsUserAsync(request);

        private bool _assistantsChecked;
        private string _assistantsCheckFailed;
        private Task _assistantCheck;
        private bool _assistantHooks;
        private readonly Dictionary<string, DateTimeOffset> _assistantAddedAt = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _assistantSeenAt = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _assistantNotes = new(StringComparer.Ordinal);
        private HashSet<string> _assistantsConnected = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AssistantIdentity.Identity> _assistantIdentities = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether the card says PadForge found no assistant
        /// program: the check ran and found none.</summary>
        public bool ShowsNoAssistants => _assistantsChecked && AssistantRows.Count == 0 && _assistantsCheckFailed == null;

        /// <summary>Why the check of the assistant programs failed, or null.</summary>
        public string AssistantsCheckFailed => _assistantsCheckFailed;

        public bool HasAssistantsCheckFailed => _assistantsCheckFailed != null;

        /// <summary>
        /// Asks, as the user, which assistant programs are on this PC and
        /// where PadForge stands in each: one install run for every row
        /// (<c>--install assistant --check</c>), whose report the rows are
        /// drawn from. The Automation page asks each time it shows and after
        /// each of a row's buttons. A check that runs already is the one
        /// the caller waits for.
        /// </summary>
        public Task RefreshAssistantRowsAsync()
        {
            // A row's note (a ZIP saved, a file not found, a failure) lasts
            // until the page shows again.
            _assistantNotes.Clear();
            if (_assistantCheck is { IsCompleted: false }) return _assistantCheck;
            return _assistantCheck = CheckAssistantsAsync();
        }

        /// <summary>A check that starts after what just changed: one already
        /// running is waited out first, since it may have read the files
        /// before the change.</summary>
        private async Task RecheckAssistantsAsync()
        {
            if (_assistantCheck is { IsCompleted: false } running) await running;
            await (_assistantCheck = CheckAssistantsAsync());
        }

        private async Task CheckAssistantsAsync()
        {
            HookAssistantLines();
            var run = await RunAssistantInstall(new InstallRequest(InstallWhat.Assistant, null, Check: true));
            if (run.Code != UserInstall.Done)
            {
                _assistantsCheckFailed = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_CheckFailed_Format,
                    run.Error ?? run.Code.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                _assistantsCheckFailed = null;
                ApplyAssistantChecks(UserInstall.ReadChecks(run.Said));
            }
            _assistantsChecked = true;
            OnPropertyChanged(nameof(ShowsNoAssistants));
            OnPropertyChanged(nameof(AssistantsCheckFailed));
            OnPropertyChanged(nameof(HasAssistantsCheckFailed));
        }

        /// <summary>Draws the rows from a check: one for each program found,
        /// in AssistantHosts' order, each kept while its program is.</summary>
        internal void ApplyAssistantChecks(IReadOnlyList<UserInstall.AssistantCheck> checks)
        {
            var found = checks.Where(c => !string.IsNullOrEmpty(c.Program)).ToDictionary(c => c.Key, StringComparer.Ordinal);
            for (int i = AssistantRows.Count - 1; i >= 0; i--)
                if (!found.ContainsKey(AssistantRows[i].Key)) AssistantRows.RemoveAt(i);
            int at = 0;
            foreach (var host in AssistantHosts.All)
            {
                if (!found.TryGetValue(host.Key, out var check)) continue;
                var row = AssistantRows.FirstOrDefault(r => r.Key == host.Key);
                if (row == null)
                {
                    row = new AssistantRowItem(host, PressAssistantAsync, SaveAssistantZip, OpenAssistantSettings);
                    AssistantRows.Insert(at, row);
                }
                if (!string.Equals(row.Program, check.Program, StringComparison.OrdinalIgnoreCase))
                {
                    row.Program = check.Program;
                    row.Icon = IconOf(check.Program);
                }
                row.Entry = check.Entry;
                row.Skill = check.Skill;
                row.SettingsFile = check.Settings;
                at++;
            }
            // A program found again may be another file now (an npm shim
            // installed over a link). Rows drawn after a program connected
            // see it.
            _assistantIdentities.Clear();
            ShowAssistantConnections();
        }

        private ImageSource IconOf(string program)
        {
            if (AssistantIconSeam != null) return AssistantIconSeam(program);
            return ProgramIcon.Read(program);
        }

        private void HookAssistantLines()
        {
            if (_assistantHooks) return;
            _assistantHooks = true;
            // The first row says that added assistants wait for the switch
            // while it is off, and a message for a chat says so too.
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(EnableAutomation)) return;
                ShowAssistantLines();
                OnPropertyChanged(nameof(AutomationAssistantMessage));
            };
        }

        /// <summary>Notes which added assistants are connected now, and when
        /// each was last seen: a row whose program is the one behind a
        /// connection (AssistantIdentity.Matches, by the path and script
        /// Windows gives), or whose name the connection gave
        /// (AssistantPrograms.KeyOfClient).</summary>
        private void ShowAssistantConnections()
        {
            var now = AutomationNow();
            var connected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var client in _automationHost?.Clients.Snapshot() ?? Array.Empty<AutomationClient>())
            {
                if (client.Kind != AutomationClientKind.Assistant) continue;
                if (AssistantPrograms.KeyOfClient(client.Name) is { } named) connected.Add(named);
                if (client.HostPath == null && client.HostScript == null) continue;
                foreach (var row in AssistantRows)
                    if (!string.IsNullOrEmpty(row.Program) && AssistantIdentity.Matches(IdentityOf(row.Program), client.HostPath, client.HostScript))
                        connected.Add(row.Key);
            }
            foreach (string key in connected) _assistantSeenAt[key] = now;
            _assistantsConnected = connected;
            ShowAssistantLines();
        }

        /// <summary>The program a row's file runs, read once a check.</summary>
        private AssistantIdentity.Identity IdentityOf(string program)
        {
            if (!_assistantIdentities.TryGetValue(program, out var identity))
                _assistantIdentities[program] = identity = AssistantIdentity.Of(program);
            return identity;
        }

        /// <summary>Each row's line, button and menu, from its check, the
        /// programs connected and what the row did last.</summary>
        internal void ShowAssistantLines()
        {
            var s = Strings.Instance;
            for (int i = 0; i < AssistantRows.Count; i++)
            {
                var row = AssistantRows[i];
                string line;
                var button = AssistantRowButton.Remove;
                bool added = row.Entry is "current" or "older";
                if (!added)
                {
                    line = s.Extras_Automation_State_NotAdded;
                    button = AssistantRowButton.Add;
                }
                else if (row.Entry == "older")
                {
                    line = s.Extras_Automation_State_EntryOlder;
                    button = AssistantRowButton.Update;
                }
                else if (row.Skill == "older")
                {
                    line = s.Extras_Automation_State_SkillOlder;
                    button = AssistantRowButton.Update;
                }
                else if (row.Skill == "missing")
                {
                    line = s.Extras_Automation_State_SkillMissing;
                    button = AssistantRowButton.Update;
                }
                else if (row.Skill == "newer")
                {
                    line = s.Extras_Automation_State_SkillNewer;
                }
                else if (_assistantsConnected.Contains(row.Key))
                {
                    line = s.Extras_Automation_State_ConnectedNow;
                }
                else if (AssistantPrograms.ClientNames.ContainsKey(row.Key)
                         && _assistantAddedAt.TryGetValue(row.Key, out var addedAt)
                         && !(_assistantSeenAt.TryGetValue(row.Key, out var seen) && seen >= addedAt))
                {
                    line = s.Extras_Automation_State_NotConnectedSince;
                }
                else
                {
                    line = s.Extras_Automation_State_Added;
                }
                if (added && row.TakesUploadOnly) line += " " + s.Extras_Automation_State_UploadOnly;
                if (i == 0 && !EnableAutomation) line += " " + s.Extras_Automation_State_OffNote;
                if (_assistantNotes.TryGetValue(row.Key, out string note)) line = note;
                if (row.IsBusy) line = row.Button switch
                {
                    AssistantRowButton.Remove => s.Extras_Automation_State_Removing,
                    AssistantRowButton.Update => s.Extras_Automation_State_Updating,
                    _ => s.Extras_Automation_State_Adding,
                };
                else row.Button = button;
                row.ShowMenuRemove = row.Button == AssistantRowButton.Update;
                row.Line = line;
            }
        }

        /// <summary>
        /// A row's button: Add writes PadForge's entry into the program's
        /// settings and the skill where the program reads skills, through
        /// <c>--install</c> as the user, and is the user's consent for that
        /// assistant. Update writes both again. Remove takes both out, and the
        /// consent with them. A step that fails stops the rest, and the row
        /// says why.
        /// </summary>
        internal async Task PressAssistantAsync(AssistantRowItem row, AssistantRowButton what)
        {
            if (row == null || row.IsBusy || what == AssistantRowButton.None) return;
            _assistantNotes.Remove(row.Key);
            row.Button = what;
            row.IsBusy = true;
            ShowAssistantLines();
            string failed = null;
            try
            {
                bool remove = what == AssistantRowButton.Remove;
                var steps = new List<InstallRequest>();
                if (remove)
                {
                    if (!row.TakesUploadOnly) steps.Add(new InstallRequest(InstallWhat.Skill, row.Key, Remove: true));
                    steps.Add(new InstallRequest(InstallWhat.Assistant, row.Key, Remove: true));
                }
                else
                {
                    steps.Add(new InstallRequest(InstallWhat.Assistant, row.Key));
                    if (!row.TakesUploadOnly) steps.Add(new InstallRequest(InstallWhat.Skill, row.Key));
                }
                foreach (var step in steps)
                {
                    var run = await RunAssistantInstall(step);
                    if (run.Code == UserInstall.Done) continue;
                    failed = run.Error ?? run.Code.ToString(CultureInfo.InvariantCulture);
                    break;
                }
                if (failed == null)
                {
                    if (remove)
                    {
                        _assistantAddedAt.Remove(row.Key);
                        _automationConsent?.ForgetAssistant(row.Key, row.Program);
                    }
                    else
                    {
                        // An assistant open now reads its settings when it
                        // starts: the row says so until it connects.
                        _assistantAddedAt[row.Key] = AutomationNow();
                        _automationConsent?.AllowAssistant(row.Key, row.Name, row.Program);
                    }
                }
            }
            finally
            {
                row.IsBusy = false;
            }
            if (failed != null)
                _assistantNotes[row.Key] = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_State_Failed_Format, failed);
            await RecheckAssistantsAsync();
            ShowAssistantLines();
        }

        /// <summary>Test seam: the Save dialog of Save the Skill as a ZIP…,
        /// which gives the file picked or null. Null shows Windows'
        /// own.</summary>
        internal Func<string, string> AssistantZipDialogSeam;

        /// <summary>Save the Skill as a ZIP…: the skill's folder, padforge,
        /// as the top level of a ZIP where the user picks, the form a
        /// program that takes skills only as an upload asks for (Claude:
        /// "The ZIP must contain the skill directory itself as its top
        /// level").</summary>
        private void SaveAssistantZip(AssistantRowItem row)
        {
            string file = AssistantZipDialogSeam != null ? AssistantZipDialogSeam(row.Name) : AskZipFile(row.Name);
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                WriteSkillZip(file);
                _assistantNotes[row.Key] = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_ZipSaved_Format,
                    row.Name, Path.GetFileName(file), AssistantPrograms.UploadPlace.TryGetValue(row.Key, out string place) ? place : row.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                _assistantNotes[row.Key] = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_NotSaved_Format, ex.Message);
            }
            ShowAssistantLines();
        }

        private static string AskZipFile(string program)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Strings.Instance.Extras_Automation_SaveZip.TrimEnd('…'),
                FileName = AssistantSkill.Name + "-skill.zip",
                DefaultExt = ".zip",
                Filter = "ZIP|*.zip",
                AddExtension = true,
            };
            return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
        }

        /// <summary>Writes the skill as a ZIP whose top level is its own
        /// folder, padforge, replacing a file of that name.</summary>
        internal static void WriteSkillZip(string file)
        {
            using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var f in AssistantSkill.Files)
            {
                var entry = zip.CreateEntry(AssistantSkill.Name + "/" + f.Path.Replace('\\', '/'), CompressionLevel.Optimal);
                using var to = entry.Open();
                to.Write(f.Bytes, 0, f.Bytes.Length);
            }
        }

        /// <summary>Test seam: what opens a settings file or its folder. Null
        /// hands it to the desktop shell with the user's rights.</summary>
        internal Action<string, bool> AssistantOpenSeam;

        /// <summary>
        /// Open Its Settings File: the program's settings file, as the user's
        /// shell opens a file of its kind, or its folder in Explorer while
        /// the file is not there yet. The path came from the run without
        /// rights, so only a .json or .toml file is opened as a file, and
        /// anything else is shown in its folder.
        /// </summary>
        private void OpenAssistantSettings(AssistantRowItem row)
        {
            string file = row.SettingsFile;
            if (!ProgramIcon.IsLocalPath(file))
            {
                _assistantNotes[row.Key] = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_NoSettingsFile_Format, row.Name);
                ShowAssistantLines();
                return;
            }
            string extension = Path.GetExtension(file);
            bool asFile = File.Exists(file) && (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(extension, ".toml", StringComparison.OrdinalIgnoreCase));
            string target = asFile ? file : Path.GetDirectoryName(file);
            if (!asFile && !Directory.Exists(target))
            {
                _assistantNotes[row.Key] = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_NoSettingsFile_Format, row.Name);
                ShowAssistantLines();
                return;
            }
            if (AssistantOpenSeam != null) { AssistantOpenSeam(target, asFile); return; }
            ShellLaunch.AsTheUser(asFile
                ? new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo(ShellLaunch.Explorer, "\"" + target + "\""), out _);
        }

        /// <summary>
        /// The rows as catalog entries (#600 item 1), through the one seam
        /// for live rows: each row's button and its menu, Save the Skill as
        /// a ZIP… naming the Save dialog it opens. Every one is the user's
        /// alone, as the page is: an install is never a program's to start.
        /// </summary>
        internal IReadOnlyList<LiveItem> AssistantCatalogItems()
        {
            var s = Strings.Instance;
            string where = s.Extras_Automation_Title + ", " + s.Extras_Automation_Card_Assistants;
            var items = new List<LiveItem>();
            foreach (var row in AssistantRows)
            {
                string id = "assistant/" + row.Key;
                var menu = new List<LiveItem>();
                if (row.ShowMenuRemove)
                    menu.Add(new LiveItem(id + "/remove", LiveKind.MenuItem, s.Common_Remove, ReachClass.UsersOwn)
                    { Press = () => row.RemoveCommand.Execute(null), Where = where });
                if (row.TakesUploadOnly)
                    menu.Add(new LiveItem(id + "/zip", LiveKind.MenuItem, s.Extras_Automation_SaveZip, ReachClass.UsersOwn)
                    { Press = () => row.SaveZipCommand.Execute(null), Opens = "SaveFileDialog", Where = where });
                menu.Add(new LiveItem(id + "/settings", LiveKind.MenuItem, s.Extras_Automation_OpenSettings, ReachClass.UsersOwn)
                { Press = () => row.OpenSettingsCommand.Execute(null), Where = where });
                items.Add(new LiveItem(id, LiveKind.Action, row.Name + ": " + row.ButtonWords, ReachClass.UsersOwn)
                {
                    Read = () => row.Line,
                    Press = () => row.PressCommand.Execute(null),
                    Menu = menu,
                    Where = where,
                });
            }
            return items;
        }

        // ─────────────────────────────────────────────
        //  What later parts provide
        // ─────────────────────────────────────────────

        /// <summary>The script libraries, or null in a build that carries
        /// none.</summary>
        internal IAutomationLibraries AutomationLibraries => _automationHost?.Libraries;

        /// <summary>Whether the Scripts card shows: the build carries the
        /// libraries.</summary>
        public bool HasAutomationLibraries => _automationHost?.Libraries != null;

        private void ShowAutomationProvided()
        {
            OnPropertyChanged(nameof(HasAutomationLibraries));
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
        //  Allowed Programs
        // ─────────────────────────────────────────────

        private IAutomationConsent _automationConsent;

        /// <summary>Hands the page the consent store (#600 item 5), once, on
        /// the interface's thread: its list shows on the Allowed Programs
        /// card, a row's Add and Remove give and take an assistant's
        /// consent.</summary>
        internal void AttachConsent(IAutomationConsent consent)
        {
            if (consent == null || _automationConsent != null) return;
            _automationConsent = consent;
            consent.Changed += (_, _) => OnAutomationThread(ShowAllowedPrograms);
            ShowAllowedPrograms();
        }

        /// <summary>The programs the user allowed, as the store lists
        /// them.</summary>
        public ObservableCollection<AllowedProgramItem> AllowedPrograms { get; } = new();

        /// <summary>Whether the Allowed Programs card shows: it lists a
        /// program.</summary>
        public bool HasAllowedPrograms => AllowedPrograms.Count > 0;

        private void ShowAllowedPrograms()
        {
            AllowedPrograms.Clear();
            foreach (var program in _automationConsent?.Allowed ?? Array.Empty<AllowedProgramView>())
                AllowedPrograms.Add(new AllowedProgramItem(program, key => _automationConsent?.Remove(key)));
            OnPropertyChanged(nameof(HasAllowedPrograms));
        }

        // ─────────────────────────────────────────────
        //  Activity
        // ─────────────────────────────────────────────

        /// <summary>What programs asked, newest line first. A line is one
        /// call, or a run of one program's identical calls that did nothing,
        /// which opens into them.</summary>
        public ObservableCollection<AutomationActivityItem> AutomationActivity { get; } = new();

        /// <summary>Whether the record holds a call: the Activity card shows
        /// only then.</summary>
        public bool HasAutomationActivity => AutomationActivity.Count > 0;

        private long _activityDropped;

        /// <summary>How many of the oldest calls went past the 100,000 kept,
        /// in words, or null while none did.</summary>
        public string AutomationActivityDropped => _activityDropped <= 0 ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.CountFormat(nameof(Strings.Extras_Automation_ActivityDropped_Format), _activityDropped),
                _activityDropped, Services.Automation.AutomationActivity.Capacity);

        public bool HasAutomationActivityDropped => _activityDropped > 0;

        private int _activityGeneration = -1;
        private long _activitySeen;
        // Every call the page holds, oldest first, with its line, so a call
        // the record let go leaves its line at once.
        private readonly Queue<(long Number, AutomationActivityItem Line)> _activityCalls = new();
        // Each program's line that its next identical call joins.
        private readonly Dictionary<string, AutomationActivityItem> _activityOpenRun = new(StringComparer.Ordinal);
        // The newest line of each run of changes, the one that offers Undo.
        private readonly Dictionary<int, AutomationActivityItem> _activityUndoLines = new();

        private RelayCommand _clearAutomationActivityCommand;

        /// <summary>Empties the record.</summary>
        public RelayCommand ClearAutomationActivityCommand => _clearAutomationActivityCommand ??= new RelayCommand(() =>
        {
            if (_automationHost != null) _automationHost.Activity.Clear();
            else ResetActivityLines();
        });

        /// <summary>The record as text for Export Activity…, or null with no
        /// host.</summary>
        internal string AutomationActivityText() => _automationHost?.Activity.ToText();

        private void ResetActivityLines()
        {
            AutomationActivity.Clear();
            _activityCalls.Clear();
            _activityOpenRun.Clear();
            _activityUndoLines.Clear();
            _activitySeen = 0;
            _activityDropped = 0;
            RaiseActivity();
        }

        /// <summary>Takes in what the record gained and lost since the last
        /// pass, so a pass costs what changed. A call joins its program's
        /// open run when it asks the same and did nothing, and the run's
        /// line moves to the top. A call the record let go leaves its line,
        /// and a line with no call left goes.</summary>
        private void ShowAutomationActivity()
        {
            Interlocked.Exchange(ref _automationActivityQueued, 0);
            var activity = _automationHost?.Activity;
            if (activity == null) return;
            var delta = activity.Since(_activityGeneration, _activitySeen);
            if (delta.Generation != _activityGeneration)
            {
                ResetActivityLines();
                _activityGeneration = delta.Generation;
            }
            while (_activityCalls.Count > 0 && _activityCalls.Peek().Number < delta.FirstKept)
            {
                var line = _activityCalls.Dequeue().Line;
                line.DropOldest();
                if (line.Calls.Count > 0) continue;
                // The oldest lines stand at the end of the list.
                for (int i = AutomationActivity.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(AutomationActivity[i], line)) continue;
                    AutomationActivity.RemoveAt(i);
                    break;
                }
                foreach (var pair in _activityOpenRun.Where(p => ReferenceEquals(p.Value, line)).ToList()) _activityOpenRun.Remove(pair.Key);
                foreach (var pair in _activityUndoLines.Where(p => ReferenceEquals(p.Value, line)).ToList()) _activityUndoLines.Remove(pair.Key);
            }
            long number = delta.Added - delta.New.Count + 1;
            foreach (var call in delta.New) AddActivityCall(call, number++);
            _activitySeen = delta.Added;
            _activityDropped = activity.Dropped;
            RefreshAutomationUndo();
            RaiseActivity();
        }

        private void AddActivityCall(AutomationActivityEntry call, long number)
        {
            string key = (call.Program ?? AutomationProgram.Unknown).Key;
            if (_activityOpenRun.TryGetValue(key, out var line) && line.Joins(call))
            {
                line.Add(call);
                int at = AutomationActivity.IndexOf(line);
                if (at > 0) AutomationActivity.Move(at, 0);
            }
            else
            {
                line = new AutomationActivityItem(call, UndoAutomationEntry);
                AutomationActivity.Insert(0, line);
                if (call.Keeps) _activityOpenRun.Remove(key);
                else _activityOpenRun[key] = line;
            }
            _activityCalls.Enqueue((number, line));
            if (call.Spell != 0)
            {
                if (_activityUndoLines.TryGetValue(call.Spell, out var older) && !ReferenceEquals(older, line)) older.CanUndo = false;
                _activityUndoLines[call.Spell] = line;
            }
        }

        private void RaiseActivity()
        {
            OnPropertyChanged(nameof(HasAutomationActivity));
            OnPropertyChanged(nameof(AutomationActivityDropped));
            OnPropertyChanged(nameof(HasAutomationActivityDropped));
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

        /// <summary>Asks the host again which runs of changes can be undone.
        /// Only the newest line of each run asks, so the page offers one
        /// Undo for a program's run of changes, the one step that undoes
        /// it.</summary>
        private void RefreshAutomationUndo()
        {
            var canUndo = _automationHost?.Undo != null ? _automationHost.CanUndo : null;
            foreach (var line in _activityUndoLines.Values)
            {
                bool can = false;
                if (canUndo != null)
                {
                    try { can = canUndo(line.Entry); } catch { can = false; }
                }
                line.CanUndo = can;
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
            OnPropertyChanged(nameof(AutomationAssistantMessage));
            OnPropertyChanged(nameof(AutomationActivityDropped));
            OnPropertyChanged(nameof(PluginsStatus));
            foreach (var item in AutomationActivity) item.RefreshText();
            foreach (var row in AssistantRows) row.RefreshWords();
            ShowAssistantLines();
            ShowAllowedPrograms();
            // A question's words were made in the old language.
            AutomationRequests.Clear();
            ShowAutomationRequests();
        }
    }

    /// <summary>
    /// A program's icon, read from the resources of its own file with
    /// ExtractIconEx, which loads the file as data and runs none of its
    /// code or any shell handler. Null for a file with no icon (a .cmd,
    /// say) or one that cannot be read.
    /// </summary>
    internal static class ProgramIcon
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconExW(string file, int index, IntPtr[] large, IntPtr[] small, uint icons);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        /// <summary>Whether a path the install run reported is a full path on
        /// a drive of this PC: never a share, which the app would reach out
        /// to with its own rights, nor a device path.</summary>
        internal static bool IsLocalPath(string file)
            => !string.IsNullOrEmpty(file) && Path.IsPathFullyQualified(file) && file.Length >= 3
               && char.IsAsciiLetter(file[0]) && file[1] == ':' && (file[2] == '\\' || file[2] == '/');

        internal static ImageSource Read(string file)
        {
            if (!IsLocalPath(file)
                || !string.Equals(Path.GetExtension(file), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                return null;
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            if (ExtractIconExW(file, 0, large, small, 1) == 0 || large[0] == IntPtr.Zero)
            {
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
                return null;
            }
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
            {
                return null;
            }
            finally
            {
                DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }
    }
}
