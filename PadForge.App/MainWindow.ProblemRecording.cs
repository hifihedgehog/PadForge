using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using PadForge.Common.Recording;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Automation;

namespace PadForge
{
    public partial class MainWindow
    {
        // ─────────────────────────────────────────────
        //  Record a Problem (#600): the window's share
        //
        //  The recorder runs on its own thread and knows no window. This
        //  keeps the Settings row in step with it, opens Read Your
        //  Recording when the user stopped a recording or took a snapshot
        //  from the page, offers on Home a recording that stopped while
        //  nobody was there, and finishes what a PadForge that closed left
        //  behind.
        // ─────────────────────────────────────────────

        private bool _problemRecorderRefreshQueued;
        private bool _reviewFinishedRecording;
        private RecordingReviewModel _review;
        private readonly LatestOpen _reviewOpens = new();

        /// <summary>Reads a review off the window's thread, one open at a
        /// time: a review read while a later one was asked for (a second
        /// click on Review before the first unpacking ended) is disposed
        /// unseen and comes back null, so its unpacked copy goes at once and
        /// only the later one shows.</summary>
        internal sealed class LatestOpen
        {
            private int _asked;

            internal async Task<T> Open<T>(Func<T> read) where T : class, IDisposable
            {
                int ticket = System.Threading.Interlocked.Increment(ref _asked);
                T made = await Task.Run(read);
                if (System.Threading.Volatile.Read(ref _asked) == ticket) return made;
                made?.Dispose();
                return null;
            }
        }

        /// <summary>What the Settings row asks of the recorder.</summary>
        internal enum RecorderAsk
        {
            StartOrStop,
            SaveLastMinute,
            Snapshot,
        }

        /// <summary>Joins the recorder to the window. Called once, after the
        /// settings have loaded and the services exist.</summary>
        private void InitializeProblemRecorder()
        {
            var recorder = ProblemRecorder.Instance;
            RecorderHost.Main = _viewModel;
            RecorderHost.ActivitySource = () => _automation?.Activity.ToText() ?? string.Empty;

            // The window's thread, watched while something is captured
            // (#600): whether it answers, how the window is shown, its
            // display scale, and every exception thrown anywhere.
            WindowWatch.Install(Dispatcher);
            Activated += (_, _) => RecordWindowShown();
            Deactivated += (_, _) => RecordWindowShown();
            StateChanged += (_, _) => RecordWindowShown();
            IsVisibleChanged += (_, _) => RecordWindowShown();
            Closed += (_, _) => WindowWatch.SetShown("closed");
            Loaded += (_, _) => RecordDisplayScale();
            DpiChanged += (_, _) => RecordDisplayScale();
            RecordWindowShown();
            int windowThread = Environment.CurrentManagedThreadId;
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (recorder.IsCapturing) recorder.TakeException(e.Exception, Environment.CurrentManagedThreadId == windowThread);
            };
            recorder.MaskTypedKeys = _viewModel.Settings.ProblemRecordingHideTypedKeys;
            recorder.Parts = _viewModel.Settings.ProblemRecordingParts;
            recorder.KeepLastMinute = _viewModel.Settings.ProblemRecordingKeepLastMinute;

            recorder.Changed += () =>
            {
                // The recorder says so ten times a second while it writes.
                // One refresh waits at a time.
                if (_problemRecorderRefreshQueued) return;
                _problemRecorderRefreshQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshProblemRecorder));
            };
            recorder.Finished += path => Dispatcher.BeginInvoke(new Action(() => OnProblemRecordingFinished(path)));
            _inputService.ProblemRecordingAsked += result =>
            {
                if (!result.Ok && ProblemRecordingRefusal(result) is { } refusal) _viewModel.SetStatus(refusal, persist: true);
            };
            Views.SettingsPage.ProblemRecordingAsked = OnProblemRecordingButton;
            RecordingReviewView.CloseRequested += (s, e) => CloseRecordingReview();
            RecordingReviewHost.CloseRequested += (s, e) => CloseRecordingReview();
            HomePageView.RecordingWaitingCard.ReviewRequested += (s, path) => OpenRecordingReview(path);
            HomePageView.RecordingWaitingCard.DeleteRequested += (s, path) =>
            {
                RecordingStore.Delete(path);
                RefreshWaitingRecording();
            };

            // A recording a PadForge that closed left unfinished gets its
            // summary now, off the UI thread, and waits on Home for its
            // review. One that waited seven days goes.
            Task.Run(() =>
            {
                // A crash in native code at the last run leaves Windows' own
                // record, which crash.log takes now (#600).
                NativeCrashRecord.CopyNew(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"));
                List<string> finished = recorder.TidyAtStart(DateTime.UtcNow);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshWaitingRecording();
                    if (finished.Count == 0) return;
                    var started = RecordingStore.StartedOf(finished[^1]);
                    _viewModel.SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Settings_RecordProblem_LeftOver_Format,
                        started?.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty), persist: true);
                }));
            });

            // A capture already armed (Keep the Last 60 Seconds on at
            // launch) starts the window's beat now.
            RefreshProblemRecorder();
        }

        /// <summary>The Settings row's buttons: Start or Stop Recording, Save
        /// the Last 60 Seconds and Save a Snapshot…. What they make opens in
        /// the review when it is finished.</summary>
        private void OnProblemRecordingButton(RecorderAsk ask)
        {
            var recorder = ProblemRecorder.Instance;
            bool wasRecording = recorder.State.Recording;
            // A program can press the row's buttons too (the catalog's
            // press). The user is at the page only while the window is in
            // front: then the stop is the user's and the review opens.
            // Otherwise the stop is a program's and the file waits on Home.
            bool userHere = IsActive;
            var result = ask switch
            {
                RecorderAsk.SaveLastMinute => recorder.SaveLastMinute(),
                RecorderAsk.Snapshot => recorder.Snapshot(),
                _ => recorder.Toggle(userHere ? ProblemRecorder.HowUser : ProblemRecorder.HowCommand),
            };
            if (!result.Ok)
            {
                if (ProblemRecordingRefusal(result) != null) _viewModel.Settings.ShowProblemRecordingStatus(() => ProblemRecordingRefusal(result));
                return;
            }
            // Starting a recording makes no file yet. The rest do.
            _reviewFinishedRecording = userHere && (ask != RecorderAsk.StartOrStop || wasRecording);
            RefreshProblemRecorder();
        }

        /// <summary>Hands Automation's host the recorder, so the
        /// <c>record</c> lines and a starting copy's <c>--record</c> reach the
        /// one the Settings row and the macro use. A start the recorder
        /// refuses for its check or its file is said on the message line,
        /// as a macro's is.</summary>
        private void ConnectProblemRecorderToAutomation()
            => ConnectRecorder(_automation, ProblemRecorder.Instance,
                result => _viewModel.SetStatus(ProblemRecordingRefusal(result), persist: true));

        /// <summary>The word a program is told while a recording it started
        /// runs (#600): that one runs, never its file.</summary>
        internal const string RecordingWord = "recording";

        /// <summary>Sets the host's four recorder entries from
        /// <paramref name="recorder"/>. <paramref name="startRefused"/> is
        /// told of a start refused for the recorder's check or its file, the
        /// two refusals the user has to act on. A program is told that a
        /// recording runs or waits for the user's review, never where its
        /// file is (#600).</summary>
        internal static void ConnectRecorder(AutomationHost host, ProblemRecorder recorder, Action<RecorderResult> startRefused)
        {
            host.RecordStart = () =>
            {
                var result = recorder.Start();
                if (!result.Ok && result.Refusal is RecorderRefusal.Privacy or RecorderRefusal.File)
                    startRefused?.Invoke(result);
                return result.Ok ? AutomationRecordAnswer.Done(RecordingWord, result.Path) : RecordRefusal(result.Refusal);
            };
            host.RecordStop = () =>
            {
                var result = recorder.Stop(how: ProblemRecorder.HowCommand);
                return result.Ok ? AutomationRecordAnswer.Done(ProblemRecorder.WaitsWord) : RecordRefusal(result.Refusal);
            };
            host.RecordMark = text =>
            {
                if (!recorder.Mark(string.IsNullOrEmpty(text) ? null : text)) return AutomationRecordAnswer.NotAvailable;
                // A marker with no recording running is held by Keep the
                // Last 60 Seconds, which has no file yet.
                return AutomationRecordAnswer.Done(recorder.State.Recording ? RecordingWord : string.Empty);
            };
            host.Recording = () => recorder.State.Recording;
        }

        /// <summary>The command set's word for why the recorder did not do
        /// it: busy while it records already or finishes a file, and not
        /// available for everything it cannot do now.</summary>
        internal static AutomationRecordAnswer RecordRefusal(RecorderRefusal refusal) => refusal switch
        {
            RecorderRefusal.AlreadyRecording or RecorderRefusal.Busy => AutomationRecordAnswer.Busy,
            _ => AutomationRecordAnswer.NotAvailable,
        };

        /// <summary>What the row and the message line say of a refusal, or
        /// null for none. A stop that finds no recording running is no
        /// refusal: a write that failed ended the recording on the
        /// recorder's thread between the toggle's look and the stop, and
        /// that end's Changed already shows the row stopped.</summary>
        internal static string ProblemRecordingRefusal(RecorderResult result) => result.Refusal switch
        {
            RecorderRefusal.Privacy => Strings.Instance.Settings_RecordProblem_Refused,
            RecorderRefusal.File => string.Format(Strings.Instance.Settings_RecordProblem_FileError_Format, result.UserDetail ?? result.Detail),
            RecorderRefusal.NothingKept => Strings.Instance.Settings_RecordProblem_NothingKept,
            RecorderRefusal.NotRecording => null,
            _ => Strings.Instance.Settings_RecordProblem_Busy,
        };

        /// <summary>How the window is shown, for the recording's window part.</summary>
        private void RecordWindowShown()
            => WindowWatch.SetShown(!IsVisible ? "hidden in the tray"
                : WindowState == System.Windows.WindowState.Minimized ? "minimized"
                : IsActive ? "in front" : "behind another window");

        private void RecordDisplayScale()
        {
            try { WindowWatch.DisplayScale = (int)Math.Round(System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX * 100); }
            catch (InvalidOperationException) { }
        }

        private void RefreshProblemRecorder()
        {
            _problemRecorderRefreshQueued = false;
            WindowWatch.Beat(ProblemRecorder.Instance.IsCapturing);
            var state = ProblemRecorder.Instance.State;
            var settings = _viewModel.Settings;
            settings.IsProblemRecording = state.Recording;
            if (state.Recording)
            {
                var elapsed = state.Elapsed;
                string time = ((int)elapsed.TotalMinutes).ToString(CultureInfo.CurrentCulture) + ":" + elapsed.Seconds.ToString("00", CultureInfo.CurrentCulture);
                string size = (state.Bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.CurrentCulture) + " MB";
                settings.ProblemRecordingElapsed = string.Format(Strings.Instance.Settings_RecordProblem_Elapsed_Format, time, size);
                settings.ProblemRecordingStatus = string.Empty;
            }
            else if (state.Finishing)
            {
                settings.ShowProblemRecordingStatus(static () => Strings.Instance.Settings_RecordProblem_Finishing);
            }
        }

        /// <summary>A recording or a snapshot is finished. One the user asked
        /// for from the page opens in the review. Any other waits on Home,
        /// and the row and the message line say so.</summary>
        private void OnProblemRecordingFinished(string path)
        {
            _viewModel.Settings.IsProblemRecording = ProblemRecorder.Instance.State.Recording;
            RefreshWaitingRecording();
            bool review = _reviewFinishedRecording;
            _reviewFinishedRecording = false;
            if (review && path != null && path.EndsWith(ProblemRecorder.ZipSuffix, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                _viewModel.Settings.ProblemRecordingStatus = string.Empty;
                OpenRecordingReview(path);
                return;
            }
            _viewModel.Settings.ShowProblemRecordingStatus(static () => Strings.Instance.Settings_RecordProblem_Waiting);
            _viewModel.SetStatus(Strings.Instance.Settings_RecordProblem_Waiting, persist: true);
        }

        /// <summary>Home's card follows the newest recording that waits.</summary>
        private void RefreshWaitingRecording()
        {
            string folder = ProblemRecorder.Instance.Host.Folder;
            Task.Run(() => RecordingStore.Waiting(folder)).ContinueWith(t =>
            {
                var newest = t.IsCompletedSuccessfully && t.Result.Count > 0 ? t.Result[0] : null;
                // The recording the review has open is not offered twice.
                if (newest != null && _review != null && string.Equals(newest.Path, _review.Review.Path, StringComparison.OrdinalIgnoreCase))
                    newest = t.Result.Count > 1 ? t.Result[1] : null;
                _viewModel.Dashboard.SetWaitingRecording(newest?.Path, newest?.Started ?? default);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ─────────────────────────────────────────────
        //  Read Your Recording
        // ─────────────────────────────────────────────

        /// <summary>Opens the review of a waiting recording. It is read off
        /// this thread, then shown over the page area.</summary>
        private async void OpenRecordingReview(string path)
        {
            if (_review != null) CloseRecordingReview();
            RecordingReview review;
            try
            {
                review = await _reviewOpens.Open(() => RecordingReview.Open(path));
                if (review == null) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _viewModel.SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Review_SaveFailed_Format, ex.Message), persist: true);
                RefreshWaitingRecording();
                return;
            }
            var recorder = ProblemRecorder.Instance;
            var model = new RecordingReviewModel(review, recorder.CurrentLiveValues, recorder.Host.Environment())
            {
                SaveFolder = _viewModel.Settings.ProblemRecordingSaveFolder ?? RecordingFolder.Downloads(),
                PickTarget = PickRecordingTarget,
                // The message line names the file.
                ShowInExplorer = target => PadForge.Common.ShellLaunch.TryStart(
                    new System.Diagnostics.ProcessStartInfo(PadForge.Common.ShellLaunch.Explorer, $"/select,\"{target}\""), out _),
                CopyText = text =>
                {
                    try { PadForge.Common.ShellLaunch.SetClipboardText(text); }
                    catch (System.Runtime.InteropServices.ExternalException) { }
                },
                // Save and Post's page, in the browser, as About's GitHub
                // button opens the repository.
                OpenPage = url => PadForge.Common.ShellLaunch.TryStart(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }, out _),
            };
            model.Saved = target =>
            {
                _viewModel.Settings.ProblemRecordingSaveFolder = Path.GetDirectoryName(target);
                _viewModel.SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Settings_RecordProblem_Saved_Format, Path.GetFileName(target)), persist: true);
            };
            model.Closed = () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_review, model)) return;
                _review = null;
                RecordingReviewView.DataContext = null;
                RecordingReviewHost.Close(returnFocus: true);
                SetPagesTabStop(true);
                RefreshWaitingRecording();
            }));
            _review = model;
            RecordingReviewView.DataContext = model;
            RecordingReviewHost.Open(System.Windows.Input.Keyboard.FocusedElement);
            // As Add a Controller and Search do: the pages leave the Tab
            // order while the review stands over them, and the focus goes in.
            SetPagesTabStop(false);
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(RecordingReviewView.FocusFirstPart));
            RefreshWaitingRecording();
        }

        /// <summary>Closes the review. The recording keeps waiting on Home.</summary>
        private void CloseRecordingReview()
        {
            var model = _review;
            if (model == null) return;
            model.Close();
        }

        /// <summary>The save box: where the user last saved, or Downloads the
        /// first time, with the recording's own name. The file picked, or
        /// null.</summary>
        private string PickRecordingTarget(string folder, string name)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Strings.Instance.Review_SaveTitle,
                FileName = name,
                DefaultExt = ProblemRecorder.ZipSuffix,
                AddExtension = true,
                OverwritePrompt = true,
                Filter = Strings.Instance.Review_SaveFilter + " (*.zip)|*.zip",
            };
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dialog.InitialDirectory = folder;
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
    }
}
