using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PadForge.Common.Recording;
using PadForge.Services.Plugins;

namespace PadForge.Services
{
    /// <summary>
    /// The Automation and Plugins pages' saves made as the user (#600): Export
    /// Activity…, Save the Skill as a ZIP… and Save the Plugin Kit…. PadForge
    /// often runs with rights, and its Save and folder dialogs with them, so a
    /// file opened by its path in this process would follow a link a program
    /// of the user put at that name and could land where only an
    /// administrator may write. Each save is made by a copy of PadForge
    /// started through the desktop shell with the user's rights, the route
    /// the review's Save… takes (RecordingSave.AsUserAsync).
    ///
    /// <para>What the page wrote in memory, the activity text and the skill's
    /// ZIP, is staged first as a new file in the recordings' folder
    /// (RecordingFolder), which PadForge writes with its rights by that
    /// folder's rules: never a link, a new file of a new name. The copy made
    /// as the user copies it to the file picked (<c>--record save</c>, which
    /// takes a staged file as it takes a recording), and the staged file is
    /// deleted after. The kit is PadForge's own, so the copy writes it itself
    /// (<c>--plugin-kit</c>).</para>
    ///
    /// <para>The save is made in this process only when it has no rights, or
    /// when the shell's user has no lower right (UAC off, the built-in
    /// Administrator), so every program of the user has full rights. With
    /// rights and a user who also has a lower right, an Explorer started
    /// elevated among them, the copy is started as the user and a
    /// ShellElevated refusal saves nothing (PluginLauncher.
    /// ShellUserHasNoLowerRight, the rule UserInstall and RecordingSave
    /// keep).</para>
    /// </summary>
    internal static class UserSave
    {
        /// <summary>A staged file's name starts with this.</summary>
        internal const string StagedPrefix = "PadForge Save ";

        /// <summary>And ends with this.</summary>
        internal const string StagedSuffix = ".part";

        /// <summary>How long the kit's copy may take.</summary>
        internal const int KitMs = 60000;

        /// <summary>Test seam: the launcher for the kit's copy. Null uses the
        /// plugins' own.</summary>
        internal static IPluginLauncher Launcher;

        /// <summary>Test seam: whether this process has rights the shell does
        /// not. Null reads both tokens.</summary>
        internal static Func<bool> RightsSeam;

        /// <summary>Test seam: whether this process has administrator rights.
        /// Null reads the process's own token.</summary>
        internal static Func<bool> HasRightsSeam;

        /// <summary>Test seam: whether the desktop shell's user has no lower
        /// right than full rights. Null reads the shell's token
        /// (PluginLauncher.ShellUserHasNoLowerRight).</summary>
        internal static Func<bool> NoLowerRightSeam;

        /// <summary>Test seam: the exe the kit's copy starts.</summary>
        internal static Func<string> Exe = () => Environment.ProcessPath;

        /// <summary>Whether a file in the recordings' folder is a save staged
        /// here, by its name.</summary>
        internal static bool IsStaged(string path)
        {
            string name = Path.GetFileName(path ?? string.Empty);
            return name.StartsWith(StagedPrefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(StagedSuffix, StringComparison.OrdinalIgnoreCase)
                && name.Length > StagedPrefix.Length + StagedSuffix.Length;
        }

        /// <summary>Saves what <paramref name="write"/> writes to
        /// <paramref name="target"/> as the user. True when it is saved.</summary>
        internal static async Task<bool> FileAsync(Action<Stream> write, string target)
        {
            if (write == null || string.IsNullOrWhiteSpace(target)) return false;
            if (SavesInPlace())
            {
                // This process runs as the user: the file is the user's.
                try
                {
                    using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    write(file);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    return false;
                }
            }
            // Get makes the folder, which a file of its name or the folder's
            // rights refuse: the save then says false, as RecordingSave's own
            // read of the folder does.
            string folder;
            try { folder = RecordingFolder.Get(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
            if (string.IsNullOrEmpty(folder)) return false;
            string staged = Path.Combine(folder, StagedPrefix + Guid.NewGuid().ToString("N") + StagedSuffix);
            try
            {
                try
                {
                    // A new file of a new name: an existing one, a link
                    // planted at the name among them, is refused.
                    using var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    write(stream);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    return false;
                }
                return await RecordingSave.AsUserAsync(staged, target).ConfigureAwait(false) == null;
            }
            finally
            {
                try { if (File.Exists(staged) && !RecordingFolder.IsLink(staged)) File.Delete(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>The folder the kit goes into in <paramref name="parent"/>:
        /// <see cref="PluginKit.FolderName"/>, or that name and a number when
        /// one of that name is there, as PluginKit.Save names it.</summary>
        internal static string KitFolder(string parent)
        {
            string folder = Path.Combine(parent, PluginKit.FolderName);
            for (int n = 2; Directory.Exists(folder) || File.Exists(folder); n++)
                folder = Path.Combine(parent, PluginKit.FolderName + " " + n);
            return folder;
        }

        /// <summary>Writes the plugin kit into a new folder in
        /// <paramref name="parent"/> as the user: <c>PadForge.exe --plugin-kit
        /// &lt;folder&gt;</c> started through the desktop shell. The folder, or
        /// null when it was not written. The name is picked here and the
        /// folder made there, so every write the kit takes is the user's: a
        /// folder made at that name in between is the kit's, as on the
        /// command line.</summary>
        internal static async Task<string> KitAsync(string parent)
        {
            if (string.IsNullOrWhiteSpace(parent)) return null;
            if (SavesInPlace()) return KitHere(parent);
            string folder = KitFolder(parent);
            string exe = Exe();
            var launcher = Launcher ?? new PluginLauncher();
            string line = "\"" + exe + "\" --plugin-kit " + Install.UserInstall.Quote(folder);
            var launch = launcher.Start(new PluginCommand(exe, line, Path.GetDirectoryName(exe)), new Dictionary<string, string>());
            // A shell with rights: the kit is written here only when its user
            // has no lower right to start the copy with (UAC off, the built-in
            // Administrator), as RecordingSave.AsUserAsync and every other
            // copy started as the user decide.
            if (launch.Refusal == PluginRefusal.ShellElevated) return NoLowerRight() ? KitHere(parent) : null;
            if (launch.Process == null) return null;
            using var process = launch.Process;
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += c => exited.TrySetResult(c);
            var done = await Task.WhenAny(exited.Task, Task.Delay(KitMs)).ConfigureAwait(false);
            if (done != exited.Task)
            {
                process.Stop();
                return null;
            }
            return exited.Task.Result == 0 ? folder : null;
        }

        private static string KitHere(string parent)
        {
            try { return PluginKit.Save(parent); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>Whether a save is made in this process: it has no rights,
        /// read for certain, or the shell's user has no lower right. A token
        /// that cannot be read counts as one with rights.</summary>
        internal static bool SavesInPlace()
        {
            var seam = HasRightsSeam;
            bool rights = seam != null ? seam() : PluginLauncher.ThisProcessHasAdministratorRights() != false;
            return !rights || NoLowerRight();
        }

        private static bool NoLowerRight()
        {
            var seam = NoLowerRightSeam;
            return seam != null ? seam() : PluginLauncher.ShellUserHasNoLowerRight();
        }

        /// <summary>Whether this process has administrator rights the
        /// desktop shell lacks, which the shell user's search of an
        /// assistant and the Plugins page's file drop read. No save decides
        /// by it (<see cref="SavesInPlace"/>): then a file it opens by a path the user chose
        /// is opened with rights the user does not have.</summary>
        internal static bool RunsWithRightsTheShellLacks()
        {
            var seam = RightsSeam;
            if (seam != null) return seam();
            if (PluginLauncher.ThisProcessHasAdministratorRights() == false) return false;
            using var shell = PluginLauncher.OpenShell();
            return shell != null && PluginLauncher.HasAdministratorRights(shell) == false;
        }
    }
}
