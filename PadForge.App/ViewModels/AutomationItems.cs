using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.Engine.Common;
using PadForge.Services.Install;

namespace PadForge.ViewModels
{
    /// <summary>
    /// A question a program's call put to the user (#597), as the Automation
    /// page's Waiting for You card and the request card above other windows
    /// show it. Both show the same item, and an answer from either ends the
    /// question for both (AutomationApprovals.Answer).
    /// </summary>
    public sealed class AutomationRequestItem
    {
        /// <summary>How many detail lines the request card shows. The rest
        /// are counted under them. The page shows every line.</summary>
        internal const int CardDetailLines = 6;

        private readonly Func<int, bool, bool> _answer;

        internal AutomationRequestItem(PendingApproval pending, Func<int, bool, bool> answer)
        {
            _answer = answer;
            Id = pending.Id;
            Expires = pending.Expires;
            var request = pending.Request;
            Title = ShownText.Clean(request.Title ?? string.Empty);
            From = request.From != null ? ShownText.Clean(request.From) : FromLine(request.Who);
            Code = request.Code == null ? null : PadForge.Services.Automation.Web.WebSecrets.Spaced(request.Code);
            CodeLead = request.Code == null ? null : ShownText.Clean(request.CodeLead ?? string.Empty);
            CodeAfter = request.Code == null ? null : ShownText.Clean(request.CodeAfter ?? string.Empty);
            var detail = new List<string>();
            if (request.Detail != null)
                foreach (string line in request.Detail)
                    if (!string.IsNullOrWhiteSpace(line)) detail.Add(ShownText.Clean(line));
            Detail = detail;
            CardDetail = detail.Count <= CardDetailLines ? detail : detail.GetRange(0, CardDetailLines);
            int rest = detail.Count - CardDetail.Count;
            CardDetailRest = rest > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.Automation_Request_MoreDetail_Format, rest)
                : null;
            AllowCommand = new RelayCommand(() => _answer?.Invoke(Id, true));
            DenyCommand = new RelayCommand(() => _answer?.Invoke(Id, false));
        }

        /// <summary>The question's ID in AutomationApprovals.</summary>
        public int Id { get; }

        /// <summary>When the question ends unanswered, which counts as
        /// no.</summary>
        internal DateTimeOffset Expires { get; }

        /// <summary>The change in a few words.</summary>
        public string Title { get; }

        /// <summary>Who asks: the program's image as Windows gives it, by
        /// its full path and its signer, and the name the program gave
        /// itself where it gave one.</summary>
        public string From { get; }

        /// <summary>Every line that says what would change.</summary>
        public IReadOnlyList<string> Detail { get; }

        /// <summary>A web sign-in's code as the card shows it, two groups of
        /// three digits, or null (#600 item 11). Only the request card binds
        /// it.</summary>
        public string Code { get; }

        /// <summary>The card's line above the code.</summary>
        public string CodeLead { get; }

        /// <summary>The card's line under the code.</summary>
        public string CodeAfter { get; }

        /// <summary>Whether the card shows a code.</summary>
        public bool HasCode => Code != null;

        /// <summary>The first <see cref="CardDetailLines"/> lines, for the
        /// request card.</summary>
        public IReadOnlyList<string> CardDetail { get; }

        /// <summary>How many lines the card leaves out, in words, or null
        /// when it shows them all.</summary>
        public string CardDetailRest { get; }

        /// <summary>The ids of the page's two buttons for this question.</summary>
        public string AllowId => "Automation_Allow_" + Id.ToString(CultureInfo.InvariantCulture);
        public string DenyId => "Automation_Deny_" + Id.ToString(CultureInfo.InvariantCulture);

        public RelayCommand AllowCommand { get; }
        public RelayCommand DenyCommand { get; }

        /// <summary>The line that says who asks. A name the program gave
        /// itself is its own claim, so it is shown as one, beside the image
        /// Windows gives. A name PadForge took from the image is no claim,
        /// and the image alone says it. The image is its full path, since a
        /// program can copy itself to any file name, and its signer when
        /// Windows trusts its signature.</summary>
        internal static string FromLine(AutomationProgram who)
        {
            var s = Strings.Instance;
            string image = ImageLine(who);
            string name = ShownText.Clean(who?.Name);
            if (string.IsNullOrEmpty(image))
                return string.IsNullOrEmpty(name)
                    ? s.Automation_Request_FromUnknown
                    : string.Format(CultureInfo.CurrentCulture, s.Automation_Request_FromClaimedOnly_Format, name);
            if (IsOwnClaim(who))
                return string.Format(CultureInfo.CurrentCulture, s.Automation_Request_FromClaimed_Format, name, image);
            return string.Format(CultureInfo.CurrentCulture, s.Automation_Request_From_Format, image);
        }

        /// <summary>A program's image as the user is shown it: its full
        /// path, or its file name where Windows gave no path, with who
        /// signed it, or that nobody did. Null when Windows named
        /// neither.</summary>
        internal static string ImageLine(AutomationProgram who)
        {
            string image = ShownText.Clean(!string.IsNullOrEmpty(who?.ImagePath) ? who.ImagePath : who?.ImageName);
            if (string.IsNullOrEmpty(image)) return null;
            var s = Strings.Instance;
            string signer = ShownText.Clean(who.Signer);
            return string.IsNullOrEmpty(signer)
                ? string.Format(CultureInfo.CurrentCulture, s.Automation_Program_NotSigned_Format, image)
                : string.Format(CultureInfo.CurrentCulture, s.Automation_Program_Signed_Format, image, signer);
        }

        /// <summary>Whether the program's name is its own word: a name other
        /// than the one PadForge makes from the image name.</summary>
        internal static bool IsOwnClaim(AutomationProgram who)
        {
            if (who == null || string.IsNullOrEmpty(who.Name)) return false;
            if (string.IsNullOrEmpty(who.ImageName)) return true;
            // The name PadForge makes from the image, made as the hello
            // makes it (AutomationSession.Hello).
            string made = ProgramDeviceDeclaration.Clean(PadForge.Common.Input.RunningProcessList.ShortName(who.ImageName),
                ProgramDeviceDeclaration.LongestName);
            return !string.Equals(ShownText.Clean(who.Name), made, StringComparison.Ordinal)
                && !string.Equals(who.Name, PadForge.Common.Input.RunningProcessList.ShortName(who.ImageName), StringComparison.Ordinal);
        }

        /// <summary>What a screen reader says for the item in a list.</summary>
        public override string ToString() => Title + ". " + From;
    }

    /// <summary>
    /// One line of the Activity card on the Automation page (#597, #600): a
    /// call a program made, or a run of one program's identical calls that
    /// did nothing, which shows once with its count and opens into the
    /// calls. Its time, the program, what it asked, what came of it, and
    /// Undo on the newest line of a run of changes the host can undo.
    /// </summary>
    public sealed class AutomationActivityItem : ObservableObject
    {
        private readonly Action<AutomationActivityItem> _undo;
        private readonly List<AutomationActivityEntry> _calls = new();
        private bool _canUndo, _isOpen;
        private int _asks;

        internal AutomationActivityItem(AutomationActivityEntry entry, Action<AutomationActivityItem> undo)
        {
            _undo = undo;
            UndoCommand = new RelayCommand(() => { if (_canUndo) _undo?.Invoke(this); });
            OpenCommand = new RelayCommand(() => IsOpen = !IsOpen);
            _calls.Add(entry);
            _asks = Math.Max(1, entry.Asked.Count);
            RefreshText();
        }

        /// <summary>The newest call of the line, as the record holds it. Its
        /// Undo is the line's.</summary>
        internal AutomationActivityEntry Entry => _calls[^1];

        /// <summary>The calls of the line, oldest first.</summary>
        internal IReadOnlyList<AutomationActivityEntry> Calls => _calls;

        /// <summary>Whether a call of the same program joins this line: the
        /// line did nothing, and the call asks the same, names the same and
        /// came to the same.</summary>
        internal bool Joins(AutomationActivityEntry call)
        {
            var e = Entry;
            return !e.Keeps && !call.Keeps && Equals(e.Program, call.Program)
                && e.Asked.Kind == call.Asked.Kind
                && string.Equals(e.Asked.Argument, call.Asked.Argument, StringComparison.Ordinal)
                && string.Equals(e.Asked.Detail, call.Asked.Detail, StringComparison.Ordinal)
                && e.Succeeded == call.Succeeded && e.ReadsOnly == call.ReadsOnly
                && string.Equals(e.Result, call.Result, StringComparison.Ordinal);
        }

        /// <summary>Adds a call that <see cref="Joins"/> the line.</summary>
        internal void Add(AutomationActivityEntry call)
        {
            _calls.Add(call);
            _asks += Math.Max(1, call.Asked.Count);
            RefreshText();
        }

        /// <summary>Lets the oldest call go, as the record did.</summary>
        internal void DropOldest()
        {
            if (_calls.Count == 0) return;
            _asks -= Math.Max(1, _calls[0].Asked.Count);
            _calls.RemoveAt(0);
            if (_calls.Count > 0) RefreshText();
        }

        /// <summary>When the newest call came, in the interface language's
        /// time of day.</summary>
        public string Time { get; private set; }

        /// <summary>The program that made it.</summary>
        public string Program { get; private set; }

        /// <summary>The program's image by its full path and its signer,
        /// under the row, or null when Windows named no image.</summary>
        public string ProgramImage { get; private set; }

        /// <summary>Whether <see cref="ProgramImage"/> has words.</summary>
        public bool HasProgramImage => !string.IsNullOrEmpty(ProgramImage);

        /// <summary>What it asked, in plain words, with the run's count.</summary>
        public string Asked { get; private set; }

        /// <summary>What came of it, in the interface's words: done, or why
        /// not. The answer's own fixed words stay in the record
        /// (<see cref="Entry"/>) and never show here.</summary>
        public string Result { get; private set; }

        /// <summary>Whether the line holds more than one call and
        /// opens.</summary>
        public bool IsRun => _calls.Count > 1;

        /// <summary>Whether the run is open, its calls listed under it.</summary>
        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                if (!SetProperty(ref _isOpen, value)) return;
                OnPropertyChanged(nameof(OpenWords));
                OnPropertyChanged(nameof(CallTimes));
            }
        }

        /// <summary>Open, or Close while open.</summary>
        public string OpenWords => _isOpen ? Strings.Instance.Common_Close : Strings.Instance.Extras_Automation_OpenRun;

        public RelayCommand OpenCommand { get; }

        /// <summary>The run's calls while it is open, newest first, each by
        /// its time to the second. Empty while closed.</summary>
        public IReadOnlyList<string> CallTimes
        {
            get
            {
                if (!_isOpen) return Array.Empty<string>();
                var times = new string[_calls.Count];
                for (int i = 0; i < _calls.Count; i++)
                {
                    var call = _calls[_calls.Count - 1 - i];
                    string time = call.Time.ToLocalTime().ToString("T", CultureInfo.CurrentUICulture);
                    times[i] = call.Asked.Count > 1
                        ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.Automation_Asked_Repeated_Format, time, call.Asked.Count)
                        : time;
                }
                return times;
            }
        }

        /// <summary>Whether the change can be undone now. The record's
        /// owner says so (AutomationHost.CanUndo), and the newest line of
        /// the run shows Undo only then.</summary>
        public bool CanUndo
        {
            get => _canUndo;
            internal set => SetProperty(ref _canUndo, value);
        }

        public RelayCommand UndoCommand { get; }

        /// <summary>The words above in the language in use now.</summary>
        internal void RefreshText()
        {
            var s = Strings.Instance;
            var entry = Entry;
            Time = entry.Time.ToLocalTime().ToString("T", CultureInfo.CurrentUICulture);
            var who = entry.Program;
            string name = ShownText.Clean(who?.Name);
            string imageName = ShownText.Clean(who?.ImageName);
            Program = !string.IsNullOrEmpty(name) ? name
                : !string.IsNullOrEmpty(imageName) ? imageName
                : s.Automation_Program_Unknown;
            ProgramImage = AutomationRequestItem.ImageLine(who);
            Asked = _calls.Count == 1 ? entry.Asked.Text : (entry.Asked with { Count = _asks, Mixed = false }).Text;
            Result = entry.Succeeded ? s.Automation_Result_Done : RefusalWords(entry.Result);
            OnPropertyChanged(nameof(Time));
            OnPropertyChanged(nameof(Program));
            OnPropertyChanged(nameof(ProgramImage));
            OnPropertyChanged(nameof(HasProgramImage));
            OnPropertyChanged(nameof(Asked));
            OnPropertyChanged(nameof(Result));
            OnPropertyChanged(nameof(IsRun));
            OnPropertyChanged(nameof(OpenWords));
            if (_isOpen) OnPropertyChanged(nameof(CallTimes));
        }

        /// <summary>Every error word the command set and the control pipe
        /// answer with (AutomationCommands, ExternalControlService, the
        /// profile switch in InputService, ClientRun) and every code the
        /// Automation pipe answers with (AutomationLine.WordsFor and the
        /// sessions), each with what happened from the user's side. A test
        /// walks the code for error words and holds each to an entry
        /// here.</summary>
        internal static readonly IReadOnlyDictionary<string, Func<Strings, string>> RefusalPhrases =
            new Dictionary<string, Func<Strings, string>>(StringComparer.Ordinal)
            {
                ["not-allowed"] = s => s.Automation_Result_NotAllowed,
                ["output-held"] = s => s.Automation_Result_Held,
                ["not-available"] = s => s.Automation_Result_NotAvailable,
                ["busy"] = s => s.Automation_Result_Busy,
                ["bad-argument"] = s => s.Automation_Result_BadArgument,
                ["unknown-profile"] = s => s.Automation_Result_UnknownProfile,
                ["unknown-slot"] = s => s.Automation_Result_UnknownSlot,
                ["unknown-command"] = s => s.Automation_Result_UnknownCommand,
                ["empty"] = s => s.Automation_Result_Empty,
                ["internal"] = s => s.Automation_Result_Internal,
                ["unknown-device"] = s => s.Automation_Result_UnknownDevice,
                ["offline"] = s => s.Automation_Result_Offline,
                ["not-owner"] = s => s.Automation_Result_NotOwner,
                ["engine-stopped"] = s => s.Automation_Result_EngineStopped,
                ["too-many"] = s => s.Automation_Result_TooMany,
                ["too-long"] = s => s.Automation_Result_TooLong,
                ["rate-limited"] = s => s.Automation_Result_RateLimited,
                ["bad-tag"] = s => s.Automation_Result_BadTag,
                ["bad-quote"] = s => s.Automation_Result_BadQuote,
                ["bad-declaration"] = s => s.Automation_Result_BadDeclaration,
                ["hello-first"] = s => s.Automation_Result_HelloFirst,
                ["unsupported-protocol"] = s => s.Automation_Result_UnsupportedProtocol,
                ["denied"] = s => s.Automation_Result_Denied,
                ["refused"] = s => s.Automation_Result_Refused,
                ["too-soon"] = s => s.Automation_Result_TooSoon,
                ["limit-reached"] = s => s.Automation_Result_LimitReached,
                // The codes an assistant's tools and a script's call lines
                // answer (#597, AssistantCalls and AssistantTools).
                ["unknown-tool"] = s => s.Automation_Result_UnknownTool,
                ["stale"] = s => s.Automation_Result_Stale,
                ["unknown-setting"] = s => s.Automation_Result_UnknownSetting,
                ["out-of-range"] = s => s.Automation_Result_OutOfRange,
                ["declined"] = s => s.Automation_Result_Declined,
                ["timed-out"] = s => s.Automation_Result_TimedOut,
                ["waiting"] = s => s.Automation_Result_Waiting,
                ["unknown-request"] = s => s.Automation_Result_UnknownRequest,
                ["nothing-to-undo"] = s => s.Automation_Result_NothingToUndo,
                ["users-own"] = s => s.Automation_Result_UsersOwn,
                // A program held to its own devices and the picked ones that
                // drove or changed something else (#600 DESIGN-2 N3).
                ["outside-reach"] = s => s.Automation_Result_OutsideReach,
                ["no-file"] = s => s.Automation_Result_NoFile,
                ["unknown-macro"] = s => s.Automation_Result_UnknownMacro,
                ["too-many-questions"] = s => s.Automation_Result_TooManyQuestions,
            };

        /// <summary>A refusal in the interface's words: the phrase of its
        /// error word, or Not done for an answer with no phrase. The answer's
        /// own words never show.</summary>
        internal static string RefusalWords(string answer)
        {
            // The control pipe answers error and a word, the Automation pipe
            // err, a code and words for a log.
            var s = Strings.Instance;
            string prefix = answer == null ? null
                : answer.StartsWith("error ", StringComparison.Ordinal) ? "error "
                : answer.StartsWith("err ", StringComparison.Ordinal) ? "err "
                : null;
            if (prefix != null)
            {
                string word = answer.Substring(prefix.Length).Trim();
                int space = word.IndexOf(' ');
                if (space >= 0) word = word.Substring(0, space);
                if (RefusalPhrases.TryGetValue(word, out var phrase)) return phrase(s);
            }
            return s.Automation_Result_NotDone;
        }

        /// <summary>What a screen reader says for the row in a list.</summary>
        public override string ToString() => Time + ", " + Program + (HasProgramImage ? " (" + ProgramImage + ")" : string.Empty)
            + ", " + Asked + ", " + Result;
    }

    /// <summary>What an assistant row's one button does.</summary>
    public enum AssistantRowButton { Add, Remove, Update, None }

    /// <summary>
    /// One assistant program on this PC, as the Assistants card on the
    /// Automation page lists it (#600 item 9): its icon, read from the
    /// program's own file, its name, its state in a line, its one button and
    /// its menu. The page fills it from the install run's check
    /// (UserInstall.CheckAll) and from the programs connected now.
    /// </summary>
    public sealed class AssistantRowItem : ObservableObject
    {
        private string _line;
        private AssistantRowButton _button = AssistantRowButton.Add;
        private bool _busy, _showMenuRemove;
        private ImageSource _icon;

        internal AssistantRowItem(AssistantHost host, Func<AssistantRowItem, AssistantRowButton, System.Threading.Tasks.Task> press,
            Action<AssistantRowItem> saveZip, Action<AssistantRowItem> openSettings)
        {
            Host = host;
            PressCommand = new RelayCommand(() => { if (!_busy && _button != AssistantRowButton.None) _ = press(this, _button); });
            RemoveCommand = new RelayCommand(() => { if (!_busy) _ = press(this, AssistantRowButton.Remove); });
            SaveZipCommand = new RelayCommand(() => saveZip?.Invoke(this));
            OpenSettingsCommand = new RelayCommand(() => openSettings?.Invoke(this));
        }

        /// <summary>The program PadForge adds itself to.</summary>
        internal AssistantHost Host { get; }

        /// <summary>The program's key: claude-code, codex, ...</summary>
        public string Key => Host.Key;

        /// <summary>The program's name, its maker's own.</summary>
        public string Name => Host.Name;

        /// <summary>The program's own file, whose icon the row shows.</summary>
        internal string Program { get; set; }

        /// <summary>The program's settings file, which Open Its Settings File
        /// opens, or null.</summary>
        internal string SettingsFile { get; set; }

        /// <summary>The check's words for PadForge's entry and skill.</summary>
        internal string Entry { get; set; } = "missing";
        internal string Skill { get; set; } = "missing";

        /// <summary>The program's icon, or null for the plain glyph.</summary>
        public ImageSource Icon
        {
            get => _icon;
            internal set { if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); }
        }

        public bool HasIcon => _icon != null;

        /// <summary>The state in words, the reason when the last try
        /// failed.</summary>
        public string Line
        {
            get => _line;
            internal set => SetProperty(ref _line, value);
        }

        /// <summary>What the button does.</summary>
        public AssistantRowButton Button
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
            AssistantRowButton.Remove => Strings.Instance.Common_Remove,
            AssistantRowButton.Update => Strings.Instance.Extras_Automation_Update,
            _ => Strings.Instance.Common_Add,
        };

        public bool HasButton => _button != AssistantRowButton.None;

        /// <summary>Whether an install of the row runs now: the buttons rest
        /// until it ends.</summary>
        public bool IsBusy
        {
            get => _busy;
            internal set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(IsIdle)); }
        }

        public bool IsIdle => !_busy;

        /// <summary>Whether the menu holds Remove: the button says Update, or
        /// the skill is from a newer PadForge, so Remove is not on the row's
        /// face.</summary>
        public bool ShowMenuRemove
        {
            get => _showMenuRemove;
            internal set => SetProperty(ref _showMenuRemove, value);
        }

        /// <summary>Whether the menu holds Save the Skill as a ZIP…: the
        /// program takes skills only as an upload.</summary>
        public bool TakesUploadOnly => Host.SkillsFolder == null;

        /// <summary>The menu button's words for a screen reader and hover.</summary>
        public string MenuName => string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_Automation_RowMenu_Format, Name);

        /// <summary>The IDs of the row's controls.</summary>
        public string ButtonId => "Automation_Assistant_" + Key;
        public string MenuId => "Automation_AssistantMenu_" + Key;

        public RelayCommand PressCommand { get; }
        public RelayCommand RemoveCommand { get; }
        public RelayCommand SaveZipCommand { get; }
        public RelayCommand OpenSettingsCommand { get; }

        /// <summary>The words above in the language in use now.</summary>
        internal void RefreshWords()
        {
            OnPropertyChanged(nameof(ButtonWords));
            OnPropertyChanged(nameof(MenuName));
        }

        /// <summary>What a screen reader says for the row in a list.</summary>
        public override string ToString() => Name + ". " + Line;
    }

    /// <summary>
    /// A program's reach as a row offers and says it (#600 DESIGN-2 N3): the
    /// two choices, All of PadForge and Only Its Own Devices and These, and
    /// the line that names the devices picked and says reads stay whole.
    /// </summary>
    public static class ReachWords
    {
        /// <summary>The choices, in the order the list shows them.</summary>
        public static IReadOnlyList<string> Choices => new[] { Strings.Instance.Reach_All, Strings.Instance.Reach_Own };

        /// <summary>What a limited reach drives, in a sentence, or null for
        /// all of PadForge.</summary>
        internal static string Line(ProgramReach reach)
        {
            if (reach == null) return null;
            var s = Strings.Instance;
            var names = reach.Devices.Select(d => ShownText.Clean(d.Name ?? string.Empty)).Where(n => n.Length > 0).Distinct().ToList();
            return names.Count == 0 ? s.Reach_Line_Own : string.Format(CultureInfo.CurrentCulture, s.Reach_Line_Format, MainWindow.JoinNames(names));
        }
    }

    /// <summary>One program the user allowed, as the Allowed Programs card
    /// lists it (#600 items 5 and 16), with its reach (#600 DESIGN-2 N3),
    /// Pick Devices… while it is held to its own devices, and Remove.</summary>
    public sealed class AllowedProgramItem
    {
        private readonly Action<string, ProgramReach> _setReach;
        private readonly ProgramReach _reach;

        internal AllowedProgramItem(AllowedProgramView program, Action<string> remove,
            Action<string, ProgramReach> setReach = null, Func<string, ProgramReach, ProgramReach> pick = null)
        {
            _setReach = setReach;
            _reach = program.Reach;
            Key = program.Key;
            Name = ShownText.Clean(program.Name ?? string.Empty);
            var s = Strings.Instance;
            string level = program.Level switch
            {
                AutomationLevel.Read => s.Extras_Automation_Level_Read,
                AutomationLevel.SendInputs => s.Extras_Automation_Level_SendInputs,
                AutomationLevel.ChangeSettings => s.Extras_Automation_Level_ChangeSettings,
                _ => s.Extras_Automation_Level_Commands,
            };
            string when = program.ConnectedNow ? s.Automation_Allowed_ConnectedNow
                : program.LastUsed is { } last ? string.Format(CultureInfo.CurrentCulture, s.Automation_Allowed_LastUsed_Format,
                    last.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture))
                : s.Automation_Allowed_NeverUsed;
            string who = ShownText.Clean(program.Who ?? string.Empty);
            Line = string.Format(CultureInfo.CurrentCulture, s.Automation_Allowed_Line_Format, who, level, when).Trim();
            // A local program can still send commands through the control
            // pipe, which asks no consent, so its row says so.
            if (_reach != null) Line += " " + ReachWords.Line(_reach) + " " + s.Reach_LocalCommands;
            RemoveCommand = new RelayCommand(() => remove?.Invoke(Key));
            PickCommand = new RelayCommand(() =>
            {
                var picked = pick?.Invoke(Name, _reach ?? ProgramReach.OwnDevices);
                if (picked != null) _setReach?.Invoke(Key, picked);
            });
        }

        /// <summary>The reach list's choices.</summary>
        public IReadOnlyList<string> ReachChoices => ReachWords.Choices;

        /// <summary>0 for All of PadForge, 1 for Only Its Own Devices and
        /// These. Picking the second keeps the devices picked before.</summary>
        public int SelectedReach
        {
            get => _reach == null ? 0 : 1;
            set
            {
                if (value == SelectedReach) return;
                _setReach?.Invoke(Key, value == 0 ? null : _reach ?? ProgramReach.OwnDevices);
            }
        }

        /// <summary>Whether Pick Devices… shows.</summary>
        public bool IsLimited => _reach != null;

        public RelayCommand PickCommand { get; }

        public string ReachId => "Automation_AllowedReach_" + LiveIdOf(Key);

        public string PickId => "Automation_AllowedPick_" + LiveIdOf(Key);

        internal string Key { get; }

        /// <summary>The program's name.</summary>
        public string Name { get; }

        /// <summary>Who it is, its level, and when it last connected.</summary>
        public string Line { get; }

        public string RemoveId => "Automation_AllowedRemove_" + LiveIdOf(Key);

        public RelayCommand RemoveCommand { get; }

        /// <summary>A key in a form an element's ID can carry.</summary>
        internal static string LiveIdOf(string key) => PadForge.Services.Catalog.LiveCatalog.ShortId(key ?? string.Empty);

        /// <summary>What a screen reader says for the row in a list.</summary>
        public override string ToString() => Name + ". " + Line;
    }
}
