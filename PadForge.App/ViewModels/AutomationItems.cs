using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;
using PadForge.Services.Automation;
using PadForge.Engine.Common;

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
            From = FromLine(request.Who);
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

        /// <summary>The program's image by its full path and its signer,
        /// under the row, or null when Windows named no image.</summary>
        public string ProgramImage { get; private set; }

        /// <summary>Whether <see cref="ProgramImage"/> has words.</summary>
        public bool HasProgramImage => !string.IsNullOrEmpty(ProgramImage);

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
            string name = ShownText.Clean(who?.Name);
            string imageName = ShownText.Clean(who?.ImageName);
            Program = !string.IsNullOrEmpty(name) ? name
                : !string.IsNullOrEmpty(imageName) ? imageName
                : s.Automation_Program_Unknown;
            ProgramImage = AutomationRequestItem.ImageLine(who);
            Asked = Entry.Asked.Text;
            Result = Entry.Succeeded ? s.Automation_Result_Done : RefusalWords(Entry.Result);
            OnPropertyChanged(nameof(Time));
            OnPropertyChanged(nameof(Program));
            OnPropertyChanged(nameof(ProgramImage));
            OnPropertyChanged(nameof(HasProgramImage));
            OnPropertyChanged(nameof(Asked));
            OnPropertyChanged(nameof(Result));
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
}
