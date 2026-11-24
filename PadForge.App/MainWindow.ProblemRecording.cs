using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Services.Automation;

namespace PadForge
{
    public partial class MainWindow
    {
        // ─────────────────────────────────────────────
        //  Record a Problem (#597): the window's share
        //
        //  The recorder runs on its own thread and knows no window. This
        //  keeps the Settings row in step with it, says on the message line
        //  what became of a recording nobody watched, selects a file in
        //  Explorer when the user asked for one from the page, and finishes
        //  what a PadForge that closed left behind.
        // ─────────────────────────────────────────────

        private bool _problemRecorderRefreshQueued;
        private bool _selectFinishedRecording;

        /// <summary>Joins the recorder to the window. Called once, after the
        /// settings have loaded and the services exist.</summary>
        private void InitializeProblemRecorder()
        {
            var recorder = ProblemRecorder.Instance;
            RecorderHost.Main = _viewModel;
            recorder.MaskTypedKeys = _viewModel.Settings.ProblemRecordingHideTypedKeys;
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
                if (!result.Ok) _viewModel.SetStatus(ProblemRecordingRefusal(result), persist: true);
            };
            Views.SettingsPage.ProblemRecordingAsked = OnProblemRecordingButton;

            // A recording a PadForge that closed left unfinished gets its
            // summary now, off the UI thread, and the message line says
            // where it is.
            Task.Run(() =>
            {
                List<string> finished = recorder.FinishLeftOver();
                if (finished.Count == 0) return;
                Dispatcher.BeginInvoke(new Action(() => _viewModel.SetStatus(
                    string.Format(Strings.Instance.Settings_RecordProblem_LeftOver_Format, Path.GetFileName(finished[^1])), persist: true)));
            });
        }

        /// <summary>The page's two buttons: Start or Stop Recording, and
        /// Save the Last 60 Seconds. A file asked for here is selected in
        /// Explorer when it is finished, as Save Snapshot's is.</summary>
        private void OnProblemRecordingButton(bool saveLastMinute)
        {
            var recorder = ProblemRecorder.Instance;
            bool wasRecording = recorder.State.Recording;
            var result = saveLastMinute ? recorder.SaveLastMinute() : recorder.Toggle();
            if (!result.Ok)
            {
                _viewModel.Settings.ProblemRecordingStatus = ProblemRecordingRefusal(result);
                return;
            }
            // Starting a recording asks for no file yet. Stopping one does.
            _selectFinishedRecording = saveLastMinute || wasRecording;
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

        /// <summary>Sets the host's four recorder entries from
        /// <paramref name="recorder"/>. <paramref name="startRefused"/> is
        /// told of a start refused for the recorder's check or its file, the
        /// two refusals the user has to act on.</summary>
        internal static void ConnectRecorder(AutomationHost host, ProblemRecorder recorder, Action<RecorderResult> startRefused)
        {
            host.RecordStart = () =>
            {
                var result = recorder.Start();
                if (!result.Ok && result.Refusal is RecorderRefusal.Privacy or RecorderRefusal.File)
                    startRefused?.Invoke(result);
                // Start names the file it writes as the recording runs. The
                // caller is given the zip that file becomes at the stop.
                return result.Ok
                    ? AutomationRecordAnswer.Done(Path.GetFileName(ProblemRecorder.ZipPathOf(result.Path)))
                    : RecordRefusal(result.Refusal);
            };
            host.RecordStop = () =>
            {
                var result = recorder.Stop();
                return result.Ok ? AutomationRecordAnswer.Done(Path.GetFileName(result.Path)) : RecordRefusal(result.Refusal);
            };
            host.RecordMark = text =>
            {
                if (!recorder.Mark(string.IsNullOrEmpty(text) ? null : text)) return AutomationRecordAnswer.NotAvailable;
                // A marker with no recording running is held by Keep the
                // Last 60 Seconds, which has no file yet.
                var state = recorder.State;
                return AutomationRecordAnswer.Done(state.Recording && !string.IsNullOrEmpty(state.File)
                    ? Path.GetFileName(ProblemRecorder.ZipPathOf(state.File))
                    : string.Empty);
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

        private static string ProblemRecordingRefusal(RecorderResult result) => result.Refusal switch
        {
            RecorderRefusal.Privacy => Strings.Instance.Settings_RecordProblem_Refused,
            RecorderRefusal.File => string.Format(Strings.Instance.Settings_RecordProblem_FileError_Format, result.Detail),
            RecorderRefusal.NothingKept => Strings.Instance.Settings_RecordProblem_NothingKept,
            _ => Strings.Instance.Settings_RecordProblem_Busy,
        };

        private void RefreshProblemRecorder()
        {
            _problemRecorderRefreshQueued = false;
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
                settings.ProblemRecordingStatus = Strings.Instance.Settings_RecordProblem_Finishing;
            }
        }

        private void OnProblemRecordingFinished(string path)
        {
            string saved = string.Format(Strings.Instance.Settings_RecordProblem_Saved_Format, Path.GetFileName(path));
            _viewModel.Settings.IsProblemRecording = ProblemRecorder.Instance.State.Recording;
            _viewModel.Settings.ProblemRecordingStatus = saved;
            _viewModel.SetStatus(saved, persist: true);
            if (!_selectFinishedRecording) return;
            _selectFinishedRecording = false;
            try
            {
                PadForge.Common.ShellLaunch.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // A broken shell association must not take the window down.
                // The message line names the file.
            }
        }
    }
}
