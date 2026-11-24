using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;

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
            Title = request.Title ?? string.Empty;
            From = FromLine(request.Who);
            var detail = new List<string>();
            if (request.Detail != null)
                foreach (string line in request.Detail)
                    if (!string.IsNullOrWhiteSpace(line)) detail.Add(line);
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

        /// <summary>Who asks: the program's image name as Windows gives it,
        /// and the name the program gave itself where it gave one.</summary>
        public string From { get; }

        /// <summary>Every line that says what would change.</summary>
        public IReadOnlyList<string> Detail { get; }

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
        /// name Windows gives. A name PadForge took from the image is no
        /// claim, and the image name alone says it.</summary>
        internal static string FromLine(AutomationProgram who)
        {
            var s = Strings.Instance;
            string image = who?.ImageName;
            string name = who?.Name;
            if (string.IsNullOrEmpty(image))
                return string.IsNullOrEmpty(name)
                    ? s.Automation_Request_FromUnknown
                    : string.Format(CultureInfo.CurrentCulture, s.Automation_Request_FromClaimedOnly_Format, name);
            if (IsOwnClaim(who))
                return string.Format(CultureInfo.CurrentCulture, s.Automation_Request_FromClaimed_Format, name, image);
            return string.Format(CultureInfo.CurrentCulture, s.Automation_Request_From_Format, image);
        }

        /// <summary>Whether the program's name is its own word: a name other
        /// than the one PadForge makes from the image name.</summary>
        internal static bool IsOwnClaim(AutomationProgram who)
        {
            if (who == null || string.IsNullOrEmpty(who.Name)) return false;
            if (string.IsNullOrEmpty(who.ImageName)) return true;
            return !string.Equals(who.Name, PadForge.Common.Input.RunningProcessList.ShortName(who.ImageName), StringComparison.Ordinal);
        }

        /// <summary>What a screen reader says for the item in a list.</summary>
        public override string ToString() => Title + ". " + From;
    }

    /// <summary>
    /// One call a program made, as the Activity card on the Automation page
    /// lists it (#597): its time, the program, what it asked, what came of
    /// it, and Undo while the change can be undone.
    /// </summary>
    public sealed class AutomationActivityItem : ObservableObject
    {
        private readonly Action<AutomationActivityItem> _undo;
        private bool _canUndo;

        internal AutomationActivityItem(AutomationActivityEntry entry, Action<AutomationActivityItem> undo)
        {
            Entry = entry;
            _undo = undo;
            UndoCommand = new RelayCommand(() => { if (_canUndo) _undo?.Invoke(this); });
            RefreshText();
        }

        /// <summary>The call as the record holds it.</summary>
        internal AutomationActivityEntry Entry { get; }

        /// <summary>When the call came, in the interface language's time of
        /// day.</summary>
        public string Time { get; private set; }

        /// <summary>The program that made it.</summary>
        public string Program { get; private set; }

        /// <summary>What it asked, in plain words.</summary>
        public string Asked { get; private set; }

        /// <summary>What came of it, in the interface's words: done, or why
        /// not. The answer's own fixed words stay in the record
        /// (<see cref="Entry"/>) and never show here.</summary>
        public string Result { get; private set; }

        /// <summary>Whether the change can be undone now. The record's
        /// owner says so (AutomationHost.CanUndo), and the row shows Undo
        /// only then.</summary>
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
            Time = Entry.Time.ToLocalTime().ToString("T", CultureInfo.CurrentUICulture);
            var who = Entry.Program;
            Program = !string.IsNullOrEmpty(who?.Name) ? who.Name
                : !string.IsNullOrEmpty(who?.ImageName) ? who.ImageName
                : s.Automation_Program_Unknown;
            Asked = Entry.Asked.Text;
            Result = Entry.Succeeded ? s.Automation_Result_Done : RefusalWords(Entry.Result);
            OnPropertyChanged(nameof(Time));
            OnPropertyChanged(nameof(Program));
            OnPropertyChanged(nameof(Asked));
            OnPropertyChanged(nameof(Result));
        }

        /// <summary>Every error word the command set and the pipe answer
        /// with (AutomationCommands, ExternalControlService, the profile
        /// switch in InputService, ClientRun), each with what happened from
        /// the user's side. A test walks the code for error words and holds
        /// each to an entry here.</summary>
        internal static readonly IReadOnlyDictionary<string, Func<Strings, string>> RefusalPhrases =
            new Dictionary<string, Func<Strings, string>>(StringComparer.Ordinal)
            {
                ["not-allowed"] = s => s.Automation_Result_NotAllowed,
                ["not-available"] = s => s.Automation_Result_NotAvailable,
                ["busy"] = s => s.Automation_Result_Busy,
                ["bad-argument"] = s => s.Automation_Result_BadArgument,
                ["unknown-profile"] = s => s.Automation_Result_UnknownProfile,
                ["unknown-slot"] = s => s.Automation_Result_UnknownSlot,
                ["unknown-command"] = s => s.Automation_Result_UnknownCommand,
                ["empty"] = s => s.Automation_Result_Empty,
                ["internal"] = s => s.Automation_Result_Internal,
            };

        /// <summary>A refusal in the interface's words: the phrase of its
        /// error word, or Not done for an answer with no phrase. The answer's
        /// own words never show.</summary>
        internal static string RefusalWords(string answer)
        {
            const string Error = "error ";
            var s = Strings.Instance;
            if (answer != null && answer.StartsWith(Error, StringComparison.Ordinal))
            {
                string word = answer.Substring(Error.Length).Trim();
                int space = word.IndexOf(' ');
                if (space >= 0) word = word.Substring(0, space);
                if (RefusalPhrases.TryGetValue(word, out var phrase)) return phrase(s);
            }
            return s.Automation_Result_NotDone;
        }

        /// <summary>What a screen reader says for the row in a list.</summary>
        public override string ToString() => Time + ", " + Program + ", " + Asked + ", " + Result;
    }
}
