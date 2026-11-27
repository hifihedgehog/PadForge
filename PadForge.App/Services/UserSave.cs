using System;
using System.IO;
using System.Threading.Tasks;
using PadForge.Services.Plugins;

namespace PadForge.Services
{
    /// <summary>
    /// The Automation and Plugins pages' saves (#600): Export Activity…,
    /// Save the Skill as a ZIP… and Save the Plugin Kit…, each written in
    /// this process.
    /// </summary>
    internal static class UserSave
    {
        /// <summary>A staged file's name starts with this.</summary>
        internal const string StagedPrefix = "PadForge Save ";

        /// <summary>And ends with this.</summary>
        internal const string StagedSuffix = ".part";

        /// <summary>Test seam: whether this process has rights the shell does
        /// not. Null reads both tokens.</summary>
        internal static Func<bool> RightsSeam;

        /// <summary>Whether a file in the recordings' folder is a save staged
        /// here, by its name.</summary>
        internal static bool IsStaged(string path)
        {
            string name = Path.GetFileName(path ?? string.Empty);
            return name.StartsWith(StagedPrefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(StagedSuffix, StringComparison.OrdinalIgnoreCase)
                && name.Length > StagedPrefix.Length + StagedSuffix.Length;
        }

        /// <summary>Writes what <paramref name="write"/> writes to
        /// <paramref name="target"/>. False when it could not be
        /// written.</summary>
        internal static Task<bool> FileAsync(Action<Stream> write, string target)
        {
            if (write == null || string.IsNullOrWhiteSpace(target)) return Task.FromResult(false);
            try
            {
                using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                write(file);
                return Task.FromResult(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return Task.FromResult(false);
            }
        }

        /// <summary>Writes the plugin kit into a new folder in
        /// <paramref name="parent"/> and gives its path, or null.</summary>
        internal static Task<string> KitAsync(string parent)
            => Task.FromResult(string.IsNullOrWhiteSpace(parent) ? null : KitHere(parent));

        private static string KitHere(string parent)
        {
            try { return PluginKit.Save(parent); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
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
