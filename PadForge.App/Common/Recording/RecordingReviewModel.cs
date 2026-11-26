using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using PadForge.Engine.Privacy;
using PadForge.Engine.Recording;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.Common.Recording
{
    /// <summary>What the review dialog binds to (#600): a recording's parts
    /// with their switches and sizes, the part shown, the scrub check, and
    /// Save…, Save and Post, Copy Summary and Delete. The window hands it
    /// what it needs of Windows (the save box, the clipboard, Explorer, the
    /// browser), so a test runs it with none.</summary>
    public sealed class RecordingReviewModel : INotifyPropertyChanged, IDisposable
    {
        /// <summary>One part's row: its switch, its name and its size.</summary>
        public sealed class PartRow : INotifyPropertyChanged
        {
            private readonly RecordingReviewModel _owner;
            internal RecordingReview.PartInfo Info { get; }

            internal PartRow(RecordingReviewModel owner, RecordingReview.PartInfo info)
            {
                _owner = owner;
                Info = info;
            }

            public string Name => PartName(Info.Part);
            public string ShowName => string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Show_Format, Name);
            public bool IsRecorded => Info.Recorded;
            public string AutomationKey => "Review_Part_" + (Info.Part == RecordingReview.Summary ? "Summary" : Info.Part.ToString());

            public bool Keep
            {
                get => Info.Keep;
                set
                {
                    if (!Info.Recorded || Info.Keep == value) return;
                    Info.Keep = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SizeText));
                    _owner.PartsChanged();
                }
            }

            public string SizeText => !Info.Recorded ? Strings.Instance.Review_NotRecorded
                : !Info.Keep ? Strings.Instance.Review_LeftOut
                : Size(Info.TextBytes);

            private bool _selected;
            public bool IsSelected
            {
                get => _selected;
                set
                {
                    if (_selected == value) return;
                    _selected = value;
                    OnPropertyChanged();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            internal void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private readonly RecordingReview _review;
        private readonly Func<IReadOnlyList<(PrivateKind Kind, string Value)>> _live;
        private readonly PrivacyEnvironment _environment;

        /// <summary>The window's share: the save box (the file picked, or
        /// null), the copy as the user, Explorer, the clipboard, the browser,
        /// and what a save or a delete leaves behind.</summary>
        internal Func<string, string, string> PickTarget;
        internal Func<string, string, Task<string>> SaveAsUser = RecordingSave.AsUserAsync;
        internal Action<string> ShowInExplorer;
        internal Action<string> CopyText;
        internal Action<string> OpenPage;
        internal Action<string> Saved;
        internal Action Closed;

        /// <summary>The folder Save… offers: where the user last saved, or
        /// Downloads the first time.</summary>
        internal string SaveFolder;

        internal RecordingReviewModel(RecordingReview review, Func<IReadOnlyList<(PrivateKind Kind, string Value)>> live, PrivacyEnvironment environment)
        {
            _review = review;
            _live = live;
            _environment = environment;
            RunsCheck = review.Check;
            foreach (var info in review.Parts) Parts.Add(new PartRow(this, info));
            Select(Parts.Count > 1 ? Parts[1] : Parts[0]);
            RunCheck();
        }

        internal RecordingReview Review => _review;

        public ObservableCollection<PartRow> Parts { get; } = new();

        public string Title => Strings.Instance.Review_Title;

        public string SpanText
        {
            get
            {
                var started = _review.Started;
                string start = started?.ToString("t", CultureInfo.CurrentCulture) ?? string.Empty;
                if (IsSnapshot) return string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Snapshot_Format, start);
                string end = started.HasValue ? (started.Value + _review.Length).ToString("t", CultureInfo.CurrentCulture) : string.Empty;
                return string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Span_Format, start, end, Length(_review.Length), StopWords(_review.Ending));
            }
        }

        private bool IsSnapshot => _review.Ending.StartsWith(ProblemRecorder.HowSnapshot, StringComparison.OrdinalIgnoreCase);

        /// <summary>A length in the words the review uses: whole minutes past
        /// one, seconds below.</summary>
        internal static string Length(TimeSpan span)
        {
            if (span.TotalMinutes >= 1)
            {
                int minutes = (int)Math.Round(span.TotalMinutes);
                return Strings.Counted(nameof(Strings.Review_Minutes_Format), minutes);
            }
            int seconds = (int)Math.Round(span.TotalSeconds);
            return Strings.Counted(nameof(Strings.Review_Seconds_Format), seconds);
        }

        /// <summary>Why it stopped, from its file's own words.</summary>
        internal static string StopWords(string ending)
        {
            string e = ending ?? string.Empty;
            bool Is(string how) => e.StartsWith(how, StringComparison.OrdinalIgnoreCase);
            if (Is(ProblemRecorder.HowUser)) return Strings.Instance.Review_Stop_User;
            if (Is(ProblemRecorder.HowMacro)) return Strings.Instance.Review_Stop_Macro;
            if (Is(ProblemRecorder.HowCommand)) return Strings.Instance.Review_Stop_Program;
            if (Is(ProblemRecorder.HowAssistant)) return Strings.Instance.Review_Stop_Assistant;
            if (Is(ProblemRecorder.HowClosed)) return Strings.Instance.Review_Stop_Closed;
            if (Is(ProblemRecorder.HowLastMinute)) return Strings.Instance.Review_Stop_LastMinute;
            if (Is(ProblemRecorder.HowTime))
            {
                string what = e.Substring(ProblemRecorder.HowTime.Length);
                return what.EndsWith(" compressed", StringComparison.Ordinal)
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Stop_Size_Format, LimitWords(what.Substring(0, what.Length - " compressed".Length)))
                    : string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Stop_Time_Format, LimitWords(what));
            }
            return e.Length == 0 ? string.Empty : e + ".";
        }

        internal static string PartName(RecordingParts part) => part switch
        {
            RecordingReview.Summary => Strings.Instance.Recording_Part_Summary,
            RecordingParts.SettingsAndDevices => Strings.Instance.Recording_Part_SettingsAndDevices,
            RecordingParts.Input => Strings.Instance.Recording_Part_Input,
            RecordingParts.Output => Strings.Instance.Recording_Part_Output,
            RecordingParts.Feedback => Strings.Instance.Recording_Part_Feedback,
            RecordingParts.Timing => Strings.Instance.Recording_Part_Timing,
            RecordingParts.WindowAndErrors => Strings.Instance.Recording_Part_WindowAndErrors,
            RecordingParts.Activity => Strings.Instance.Recording_Part_Activity,
            RecordingParts.DiagnosticsLines => Strings.Instance.Recording_Part_DiagnosticsLines,
            _ => part.ToString(),
        };

        /// <summary>A size in KB or MB, one decimal under 10 MB, in the
        /// user's language.</summary>
        internal static string Size(long bytes)
        {
            var s = Strings.Instance;
            if (bytes < 1024 * 1024)
                return string.Format(CultureInfo.CurrentCulture, s.Review_SizeKB_Format,
                    (bytes == 0 ? 0 : Math.Max(1, (long)Math.Round(bytes / 1024.0))).ToString(CultureInfo.CurrentCulture));
            double mb = bytes / (1024.0 * 1024.0);
            return string.Format(CultureInfo.CurrentCulture, s.Review_SizeMB_Format,
                mb < 10 ? mb.ToString("0.0", CultureInfo.CurrentCulture) : Math.Round(mb).ToString("0", CultureInfo.CurrentCulture));
        }

        /// <summary>The size or the length a stop line names in the file's
        /// own English (ProblemRecorder.Words: "20 MB", "30 minutes"), in the
        /// review's words, or the words as they are when they are neither.</summary>
        internal static string LimitWords(string words)
        {
            int space = words.IndexOf(' ');
            if (space <= 0 || !double.TryParse(words.AsSpan(0, space), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n)) return words;
            return words.Substring(space + 1) switch
            {
                "MB" => Size((long)(n * 1024 * 1024)),
                "KB" => Size((long)(n * 1024)),
                "minutes" => Length(TimeSpan.FromMinutes(n)),
                "seconds" => Length(TimeSpan.FromSeconds(n)),
                _ => words,
            };
        }

        // ── The part shown ──

        private PartRow _shown;
        public PartRow Shown => _shown;

        internal void Select(PartRow row)
        {
            if (row == null || ReferenceEquals(row, _shown)) return;
            if (_shown != null) _shown.IsSelected = false;
            _shown = row;
            row.IsSelected = true;
            ShownLines = _review.LinesOf(row.Info.Part);
            OnPropertyChanged(nameof(Shown));
            OnPropertyChanged(nameof(ShownHeading));
            OnPropertyChanged(nameof(ShownSizes));
            OnPropertyChanged(nameof(ShownLines));
            OnPropertyChanged(nameof(ShownEmpty));
        }

        public string ShownHeading => _shown == null ? string.Empty : string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_ShowPart_Format, _shown.Name);

        public string ShownSizes => _shown == null || !_shown.Info.Recorded ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_PartSize_Format, Size(_shown.Info.TextBytes), Size(_shown.Info.ZipBytes));

        public IReadOnlyList<string> ShownLines { get; private set; } = Array.Empty<string>();

        public bool ShownEmpty => ShownLines.Count == 0;

        // ── Sizes and the scrub check ──

        public string SizesText
        {
            get
            {
                var (text, zip) = _review.KeptSize();
                var recorder = ProblemRecorder.Instance;
                return string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Sizes_Format, Size(text), Size(zip),
                    Size(recorder.MaxBytes), Length(recorder.MaxDuration));
            }
        }

        private RecordingReview.CheckResult _check;
        private bool _checking, _unread;
        private int _checksAsked;

        public string ScrubText => _checking ? Strings.Instance.Review_Scrub_Checking
            : _unread ? Strings.Instance.Review_Scrub_Unread
            : _check.Found > 0
            ? Strings.Counted(nameof(Strings.Review_Scrub_Found_Format), _check.Found)
            : string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Scrub_Format,
                Strings.Counted(nameof(Strings.Review_ScrubValues_Format), _check.StandIns),
                Strings.Counted(nameof(Strings.Review_ScrubKinds_Format), _check.Kinds));

        /// <summary>Whether the scrub check found one of this PC's values in
        /// the parts kept. Save stays off until those parts are off.</summary>
        public bool CheckFailed => !_checking && !_unread && _check.Found > 0;

        private bool _busy;
        public bool CanSave => !_busy && !_checking && !_unread && !CheckFailed;
        public bool CanDelete => !_busy;

        /// <summary>The scrub check, run on a worker with this PC's values and
        /// the parts kept as they were when it was asked. The review's own,
        /// or a test's.</summary>
        internal Func<IReadOnlyList<(PrivateKind Kind, string Value)>, RecordingParts, RecordingReview.CheckResult> RunsCheck;

        /// <summary>The newest scrub check, done when its answer is in.</summary>
        internal Task Checked { get; private set; } = Task.CompletedTask;

        /// <summary>
        /// Starts the scrub check of what is kept now. It reads every line of
        /// the unpacked text, which takes long for a long recording, so it
        /// runs off the window's thread with Save off until its answer
        /// comes, and an answer a newer switch has outdated is dropped.
        /// </summary>
        private void RunCheck()
        {
            int asked = ++_checksAsked;
            var live = _live();
            var kept = _review.KeptNow;
            var check = RunsCheck;
            _checking = true;
            OnPropertyChanged(nameof(ScrubText));
            OnPropertyChanged(nameof(CheckFailed));
            OnPropertyChanged(nameof(CanSave));
            Checked = Answer(asked, Task.Run(() => check(live, kept)));
        }

        private async Task Answer(int asked, Task<RecordingReview.CheckResult> running)
        {
            RecordingReview.CheckResult result;
            try { result = await running.ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // The review closed under it, or its text could not be read:
                // no answer, the line says so, and Save stays off until a
                // check reads.
                if (asked != _checksAsked || _closed) return;
                _checking = false;
                _unread = true;
                OnPropertyChanged(nameof(ScrubText));
                OnPropertyChanged(nameof(CheckFailed));
                OnPropertyChanged(nameof(CanSave));
                return;
            }
            if (asked != _checksAsked || _closed) return;
            _check = result;
            _checking = false;
            _unread = false;
            OnPropertyChanged(nameof(ScrubText));
            OnPropertyChanged(nameof(CheckFailed));
            OnPropertyChanged(nameof(CanSave));
        }

        /// <summary>A part's switch changed: the sizes and the scrub check of
        /// what is kept are worked out again.</summary>
        internal void PartsChanged()
        {
            OnPropertyChanged(nameof(SizesText));
            RunCheck();
        }

        // ── Save, Save and Post, Copy Summary, Delete ──

        private string _status = string.Empty;
        public string Status
        {
            get => _status;
            private set
            {
                _status = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        private bool _copied;
        public string CopyWords => _copied ? Strings.Instance.Review_Copied : Strings.Instance.Review_CopySummary;

        internal void CopySummary()
        {
            CopyText?.Invoke(string.Join(Environment.NewLine, _review.SummaryLines));
            _copied = true;
            OnPropertyChanged(nameof(CopyWords));
        }

        /// <summary>Save…: the save box, then a copy with the kept parts when
        /// some are off, then the copy as the user, then the waiting
        /// recording leaves the folder and Explorer shows the file.</summary>
        internal Task SaveAsync() => SaveAsync(post: false);

        /// <summary>Save and Post (#600 item 29): Save… as it is, then the
        /// summary on the clipboard, the file in Explorer and the page where
        /// reports go (<see cref="UpdateService.ReportPage"/>), for the
        /// person to paste the summary and drop the file there. A save that
        /// did not happen copies and opens nothing.</summary>
        internal Task SaveAndPostAsync() => SaveAsync(post: true);

        private async Task SaveAsync(bool post)
        {
            if (!CanSave) return;
            string target = PickTarget?.Invoke(SaveFolder, Path.GetFileName(_review.Path));
            if (string.IsNullOrEmpty(target)) return;
            _busy = true;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanDelete));
            string copy = null;
            bool saved = false;
            try
            {
                var live = _live();
                copy = await Task.Run(() => _review.WriteKept(live, _environment)).ConfigureAwait(true);
                string error = await SaveAsUser(copy, target).ConfigureAwait(true);
                if (error != null)
                {
                    Status = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_SaveFailed_Format, error);
                    return;
                }
                Status = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Settings_RecordProblem_Saved_Format, Path.GetFileName(target));
                SaveFolder = Path.GetDirectoryName(target);
                Saved?.Invoke(target);
                // The summary is read before the close lets the review go.
                if (post) CopySummary();
                ShowInExplorer?.Invoke(target);
                if (post) OpenPage?.Invoke(UpdateService.ReportPage);
                saved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Status = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_SaveFailed_Format, ex.Message);
            }
            finally
            {
                if (copy != null && !string.Equals(copy, _review.Path, StringComparison.OrdinalIgnoreCase))
                    DeleteCopy(copy);
                _busy = false;
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(CanDelete));
                // A saved recording leaves the folder, and a close asked for
                // while the save ran is made now.
                if (saved || _closeWanted) Close(deleteRecording: saved);
            }
        }

        private static void DeleteCopy(string copy)
        {
            try
            {
                if (File.Exists(copy) && !RecordingFolder.IsLink(copy)) File.Delete(copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>Delete: the waiting recording goes, and the review
        /// closes.</summary>
        internal void Delete()
        {
            if (!CanDelete) return;
            Close(deleteRecording: true);
        }

        private bool _closed, _closeWanted;

        /// <summary>Closes the review: its unpacked text goes, and with
        /// <paramref name="deleteRecording"/> the waiting zip too. While a
        /// save runs, which reads that text and deletes the zip once it is
        /// saved, the close waits, and the save makes it as it ends.</summary>
        internal void Close(bool deleteRecording = false)
        {
            if (_closed) return;
            if (_busy)
            {
                _closeWanted = true;
                return;
            }
            _closed = true;
            string path = _review.Path;
            _review.Dispose();
            if (deleteRecording) RecordingStore.Delete(path);
            Closed?.Invoke();
        }

        public void Dispose() => Close();

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
