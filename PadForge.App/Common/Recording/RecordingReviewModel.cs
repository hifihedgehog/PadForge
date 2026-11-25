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
    /// Save…, Copy Summary and Delete. The window hands it what it needs of
    /// Windows (the save box, the clipboard, Explorer), so a test runs it
    /// with none.</summary>
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
        /// null), the copy as the user, Explorer, the clipboard, and what a
        /// save or a delete leaves behind.</summary>
        internal Func<string, string, string> PickTarget;
        internal Func<string, string, Task<string>> SaveAsUser = RecordingSave.AsUserAsync;
        internal Action<string> ShowInExplorer;
        internal Action<string> CopyText;
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
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Stop_Size_Format, what.Substring(0, what.Length - " compressed".Length))
                    : string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Stop_Time_Format, what);
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

        /// <summary>A size in KB or MB, one decimal under 10 MB.</summary>
        internal static string Size(long bytes)
        {
            if (bytes < 1024 * 1024)
                return (bytes == 0 ? 0 : Math.Max(1, (long)Math.Round(bytes / 1024.0))).ToString(CultureInfo.CurrentCulture) + " KB";
            double mb = bytes / (1024.0 * 1024.0);
            return (mb < 10 ? mb.ToString("0.0", CultureInfo.CurrentCulture) : Math.Round(mb).ToString("0", CultureInfo.CurrentCulture)) + " MB";
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
                    ProblemRecorder.Words(recorder.MaxBytes), ProblemRecorder.Words(recorder.MaxDuration));
            }
        }

        private RecordingReview.CheckResult _check;
        public string ScrubText => _check.Found > 0
            ? Strings.Counted(nameof(Strings.Review_Scrub_Found_Format), _check.Found)
            : string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_Scrub_Format,
                Strings.Counted(nameof(Strings.Review_ScrubValues_Format), _check.StandIns),
                Strings.Counted(nameof(Strings.Review_ScrubKinds_Format), _check.Kinds));

        /// <summary>Whether the scrub check found one of this PC's values in
        /// the parts kept. Save stays off until those parts are off.</summary>
        public bool CheckFailed => _check.Found > 0;

        private bool _busy;
        public bool CanSave => !_busy && !CheckFailed;
        public bool CanDelete => !_busy;

        private void RunCheck()
        {
            _check = _review.Check(_live());
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

        // ── Save, Copy Summary, Delete ──

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
        internal async Task SaveAsync()
        {
            if (!CanSave) return;
            string target = PickTarget?.Invoke(SaveFolder, Path.GetFileName(_review.Path));
            if (string.IsNullOrEmpty(target)) return;
            _busy = true;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanDelete));
            string copy = null;
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
                ShowInExplorer?.Invoke(target);
                Close(deleteRecording: true);
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

        private bool _closed;

        /// <summary>Closes the review: its unpacked text goes, and with
        /// <paramref name="deleteRecording"/> the waiting zip too.</summary>
        internal void Close(bool deleteRecording = false)
        {
            if (_closed) return;
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
