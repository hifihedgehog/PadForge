using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PadForge.Services;
using PadForge.Services.Plugins;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// Save… of the review (#600): the recording is copied where the user
    /// picked, as the user. PadForge often runs with rights, and over the
    /// shoulder with another account's, so the copy is made by
    /// <c>PadForge.exe --record save &lt;recording&gt; &lt;file&gt;</c> started
    /// through the desktop shell, the route an install takes for the user
    /// (UserInstall.AsUserAsync, PluginLauncher): the file is the user's own,
    /// written where the user may write and nowhere else. Without rights, or
    /// when the shell has them too (UAC off), the copy is made in this
    /// process, which then runs as the user.
    /// </summary>
    internal static class RecordingSave
    {
        /// <summary>The words after <c>--record</c> that save.</summary>
        internal const string Verb = "save";

        /// <summary>How long a save started as the user may take.</summary>
        internal const int AsUserMs = 60000;

        /// <summary>Test seam: the launcher. Null uses the plugins' own.</summary>
        internal static IPluginLauncher Launcher;

        /// <summary>Test seam: whether this process has rights the shell
        /// does not. Null reads both tokens.</summary>
        internal static Func<bool> RightsSeam;

        /// <summary>Test seam: the exe the save starts.</summary>
        internal static Func<string> Exe = () => Environment.ProcessPath;

        /// <summary>Copies <paramref name="source"/> to <paramref name="target"/>
        /// as the user. Error is null when it is saved.</summary>
        internal static async Task<string> AsUserAsync(string source, string target)
        {
            if (!RunsWithRightsTheShellLacks())
                return Run(source, target, out string error) == 0 ? null : error;
            string exe = Exe();
            var launcher = Launcher ?? new PluginLauncher();
            string line = "\"" + exe + "\" --record " + Verb + " " + Services.Install.UserInstall.Quote(source) + " " + Services.Install.UserInstall.Quote(target);
            var launch = launcher.Start(new PluginCommand(exe, line, Path.GetDirectoryName(exe)), new Dictionary<string, string>());
            if (launch.Refusal == PluginRefusal.ShellElevated)
                return Run(source, target, out string inProcess) == 0 ? null : inProcess;
            if (launch.Process == null)
                return "Windows did not start the save with your rights (" + launch.Refusal + (launch.Error != 0 ? ", error " + launch.Error : string.Empty) + ").";
            using var process = launch.Process;
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += c => exited.TrySetResult(c);
            var done = await Task.WhenAny(exited.Task, Task.Delay(AsUserMs)).ConfigureAwait(false);
            if (done != exited.Task)
            {
                process.Stop();
                return "The save did not end in " + (AsUserMs / 1000) + " seconds.";
            }
            int code = exited.Task.Result;
            return code == 0 ? null : code == 2 ? "PadForge did not take this file as a recording of its own." : "Windows did not let the file be written there.";
        }

        private static bool RunsWithRightsTheShellLacks()
        {
            var seam = RightsSeam;
            if (seam != null) return seam();
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            if (PluginLauncher.HasAdministratorRights(self.SafeHandle) == false) return false;
            using var shell = PluginLauncher.OpenShell();
            return shell != null && PluginLauncher.HasAdministratorRights(shell) == false;
        }

        /// <summary>
        /// The copy itself, run as the user: by <c>--record save</c>, or in
        /// this process when it runs as the user. The source has to be a
        /// recording in the user's own recordings folder (a waiting zip, or a
        /// copy the review made with parts left out), so the command saves
        /// nothing else. Gives 0 when saved, 2 for a source that is none of
        /// those, 1 when the file could not be written.
        /// </summary>
        internal static int Run(string source, string target, out string error)
        {
            error = null;
            string folder = RecordingFolder.Get();
            string full, into;
            try
            {
                full = Path.GetFullPath(source ?? string.Empty);
                into = Path.GetFullPath(target ?? string.Empty);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = ex.Message;
                return 2;
            }
            string name = Path.GetFileName(full);
            bool own = RecordingStore.IsOwnName(full, ProblemRecorder.ZipSuffix)
                || (name.StartsWith(ProblemRecorder.FilePrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(RecordingReview.CopySuffix, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(folder) || !string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || !own || RecordingFolder.IsLink(full) || !File.Exists(full) || !RecordingStore.HoldsRecording(full))
            {
                error = "PadForge did not take this file as a recording of its own.";
                return 2;
            }
            try
            {
                File.Copy(full, into, overwrite: true);
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = ex.Message;
                return 1;
            }
        }

        /// <summary>The client run's <c>--record save</c>: the exit code, and
        /// the reason on the error stream.</summary>
        internal static int RunForClient(string source, string target, Action<string> sayError)
        {
            int code = Run(source, target, out string error);
            if (error != null) sayError?.Invoke(error);
            return code;
        }
    }
}
