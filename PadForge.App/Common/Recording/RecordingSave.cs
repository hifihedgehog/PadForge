using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using PadForge.Resources.Strings;
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
    /// when the shell's user has no lower right (UAC off, or the built-in
    /// Administrator), the copy is made in this process.
    /// </summary>
    internal static class RecordingSave
    {
        /// <summary>The words after <c>--record</c> that save.</summary>
        internal const string Verb = "save";

        /// <summary>How long a save started as the user may take.</summary>
        internal const int AsUserMs = 60000;

        /// <summary>What <see cref="Run"/> gives: saved, the file not
        /// written, and a source that is no recording of the user's own
        /// folder.</summary>
        internal const int Saved = 0, NotWritten = 1, NotTaken = 2;

        /// <summary>Test seam: the launcher. Null uses the plugins' own.</summary>
        internal static IPluginLauncher Launcher;

        /// <summary>Test seam: whether the save goes through a copy started
        /// as the user. Null applies the rule (<see cref="RunsWithRightsTheShellLacks"/>).</summary>
        internal static Func<bool> RightsSeam;

        /// <summary>Test seam: whether the desktop shell's user has no lower
        /// right than full rights. Null reads the shell's token
        /// (PluginLauncher.ShellUserHasNoLowerRight).</summary>
        internal static Func<bool> NoLowerRightSeam;

        /// <summary>Test seam: whether this process has administrator
        /// rights, null when its token cannot be read. Null reads the
        /// token.</summary>
        internal static Func<bool?> OwnRightsSeam;

        /// <summary>Test seam: the exe the save starts.</summary>
        internal static Func<string> Exe = () => Environment.ProcessPath;

        /// <summary>Copies <paramref name="source"/> to <paramref name="target"/>
        /// as the user. Null when it is saved, and otherwise the line the
        /// review shows, in the user's language.</summary>
        internal static async Task<string> AsUserAsync(string source, string target)
        {
            var s = Strings.Instance;
            if (!RunsWithRightsTheShellLacks()) return InThisProcess(source, target);
            // The test the copy makes, made here first, so a source refused
            // is said only when this process read it. The copy's own refusal
            // comes back as its exit code.
            if (!IsOwnRecording(source)) return s.Review_SaveNotOwn;
            string exe = Exe();
            var launcher = Launcher ?? new PluginLauncher();
            string line = "\"" + exe + "\" --record " + Verb + " " + Services.Install.UserInstall.Quote(source) + " " + Services.Install.UserInstall.Quote(target);
            var launch = launcher.Start(new PluginCommand(exe, line, Path.GetDirectoryName(exe)), new Dictionary<string, string>());
            // A shell with rights whose user has no lower right (UAC off, or
            // the built-in Administrator): every program of the user has
            // full rights, and the copy is made here. A shell with rights
            // whose user also has a lower right gets the refusal below.
            if (launch.Refusal == PluginRefusal.ShellElevated && (NoLowerRightSeam?.Invoke() ?? PluginLauncher.ShellUserHasNoLowerRight()))
                return InThisProcess(source, target);
            if (launch.Process == null)
            {
                return launch.Refusal switch
                {
                    PluginRefusal.NoShell => s.Review_SaveNoShell,
                    PluginRefusal.Failed when launch.Error != 0 => string.Format(CultureInfo.CurrentCulture, s.Review_SaveStartRefused_Format, launch.Error),
                    _ => s.Review_SaveNotStarted,
                };
            }
            using var process = launch.Process;
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += c => exited.TrySetResult(c);
            var done = await Task.WhenAny(exited.Task, Task.Delay(AsUserMs)).ConfigureAwait(false);
            if (done != exited.Task)
            {
                process.Stop();
                return string.Format(CultureInfo.CurrentCulture, s.Review_SaveTimedOut_Format, RecordingReviewModel.Length(TimeSpan.FromMilliseconds(AsUserMs)));
            }
            // Only 1 is the copy's own word for a file not written. Any other
            // code but 0, a crash's among them (0xE0434352, an unhandled .NET
            // exception) and a command line the copy did not take (2,
            // ClientRun.WrongCommandLine), is named as it came.
            int code = exited.Task.Result;
            return code switch
            {
                Saved => null,
                NotWritten => s.Review_SaveNotWritten,
                _ => string.Format(CultureInfo.CurrentCulture, s.Review_SaveEnded_Format, CodeText(code)),
            };
        }

        /// <summary>An exit code as a person reads it: small ones in
        /// decimal, a Windows or .NET code in hex.</summary>
        internal static string CodeText(int code)
            => code is >= 0 and < 0x10000
                ? code.ToString(CultureInfo.CurrentCulture)
                : "0x" + ((uint)code).ToString("X8", CultureInfo.InvariantCulture);

        private static string InThisProcess(string source, string target)
            => Run(source, target, out _) switch
            {
                Saved => null,
                NotWritten => Strings.Instance.Review_SaveNotWritten,
                _ => Strings.Instance.Review_SaveNotOwn,
            };

        /// <summary>Whether the save goes through a copy started as the
        /// user: this process has rights (or its token cannot be read) and
        /// the shell's user has a lower right. An Explorer started elevated
        /// and a shell token that cannot be read both have one, so neither
        /// saves with PadForge's rights. In place only without rights, or
        /// when every program of the user has full rights (UAC off, the
        /// built-in Administrator).</summary>
        private static bool RunsWithRightsTheShellLacks()
        {
            var seam = RightsSeam;
            if (seam != null) return seam();
            if ((OwnRightsSeam?.Invoke() ?? PluginLauncher.ThisProcessHasAdministratorRights()) == false) return false;
            return !(NoLowerRightSeam?.Invoke() ?? PluginLauncher.ShellUserHasNoLowerRight());
        }

        /// <summary>Whether a file is a recording in the user's own
        /// recordings folder: a waiting zip, or a copy the review made with
        /// parts left out. A folder that cannot be read or made holds
        /// none.</summary>
        internal static bool IsOwnRecording(string source)
        {
            string full;
            try { full = Path.GetFullPath(source ?? string.Empty); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            return IsOwnFull(full);
        }

        private static bool IsOwnFull(string full)
        {
            string folder;
            try { folder = RecordingFolder.Get(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
            string name = Path.GetFileName(full);
            // A save the Automation or Plugins page staged there (UserSave)
            // is copied as a recording is.
            bool staged = UserSave.IsStaged(full);
            bool own = staged || RecordingStore.IsOwnName(full, ProblemRecorder.ZipSuffix)
                || (name.StartsWith(ProblemRecorder.FilePrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(RecordingReview.CopySuffix, StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrEmpty(folder) && string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && own && !RecordingFolder.IsLink(full) && File.Exists(full) && (staged || RecordingStore.HoldsRecording(full));
        }

        /// <summary>
        /// The copy itself, run as the user: by <c>--record save</c>, or in
        /// this process when it runs as the user. The source has to be a
        /// recording in the user's own recordings folder (a waiting zip, or a
        /// copy the review made with parts left out), so the command saves
        /// nothing else. Gives <see cref="Saved"/>, <see cref="NotTaken"/>
        /// for a source that is none of those, and <see cref="NotWritten"/>
        /// when the file could not be written. The error is the client run's
        /// line on its error stream, in English as the command line's are.
        /// </summary>
        internal static int Run(string source, string target, out string error)
        {
            error = null;
            string full, into;
            try
            {
                full = Path.GetFullPath(source ?? string.Empty);
                into = Path.GetFullPath(target ?? string.Empty);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = ex.Message;
                return NotTaken;
            }
            if (!IsOwnFull(full))
            {
                error = "PadForge did not take this file as a recording of its own.";
                return NotTaken;
            }
            try
            {
                File.Copy(full, into, overwrite: true);
                return Saved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = ex.Message;
                return NotWritten;
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
