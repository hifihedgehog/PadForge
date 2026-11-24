using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// The folder the single-file host unpacked this process's native
    /// libraries into, and the clean-up of the protected folder's old ones
    /// (#597).
    ///
    /// <para>The host unpacks into
    /// <c>&lt;base&gt;\&lt;exe name&gt;\&lt;bundle ID&gt;</c> (extractor.cpp,
    /// extraction_dir), where the base is DOTNET_BUNDLE_EXTRACT_BASE_DIR, which
    /// <see cref="ProtectedStart"/> sets for every start with rights, or the
    /// temp folder's <c>.net</c> when it is not set. The folder is the one a
    /// loaded module sits in directly below <c>&lt;base&gt;\&lt;exe name&gt;</c>.
    /// Nothing else is ever taken for it: before #597 the app fell back to the
    /// newest folder in the user's temp folder that held an xinput1_4.dll and
    /// put it on the DLL search path, and any program of the user can make
    /// such a folder.</para>
    /// </summary>
    internal static class UnpackFolder
    {
        /// <summary>The unpack folder among <paramref name="modulePaths"/>, or
        /// null.</summary>
        internal static string Find(IEnumerable<string> modulePaths, string baseFolder, string exeName)
        {
            if (string.IsNullOrEmpty(baseFolder) || string.IsNullOrEmpty(exeName)) return null;
            string prefix = Path.GetFullPath(Path.Combine(baseFolder, exeName)).TrimEnd('\\') + "\\";
            foreach (string path in modulePaths)
            {
                if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string folder = Path.GetDirectoryName(path);
                // Directly below, the bundle's ID: one name, no further folder.
                if (folder != null && folder.Length > prefix.Length && folder.IndexOf('\\', prefix.Length) < 0) return folder;
            }
            return null;
        }

        /// <summary>The base this process was unpacked under.</summary>
        internal static string BaseFolder()
            => Environment.GetEnvironmentVariable(ProtectedStart.BaseVariable) is { Length: > 0 } set
                ? set
                : Path.Combine(Path.GetTempPath(), ".net");

        /// <summary>This process's unpack folder, or null when no loaded
        /// module sits in one.</summary>
        internal static string Current()
        {
            var paths = new List<string>();
            try
            {
                using var self = Process.GetCurrentProcess();
                foreach (ProcessModule module in self.Modules)
                {
                    try { paths.Add(module.FileName); }
                    catch { }
                }
            }
            catch
            {
                return null;
            }
            return Find(paths, BaseFolder(), Path.GetFileNameWithoutExtension(Environment.ProcessPath));
        }

        /// <summary>
        /// Removes what the protected folder no longer needs: the unpack
        /// folders of other builds of this exe, and the folders a start moved
        /// aside as not sound. Each is renamed first, which fails while a
        /// process uses a file in it, and only then deleted, so a copy of
        /// another build that still runs keeps its libraries. Only below the
        /// protected folder, never in the user's temp folder. Best effort.
        /// </summary>
        internal static void CleanUp(string current)
        {
            string natives = ProtectedFolder.Natives, root = ProtectedFolder.Root;
            if (natives.Length == 0 || current == null) return;
            if (!SameFolder(BaseFolder(), natives)) return;
            // Natives\<exe name>\<bundle ID>, for every name the exe had.
            foreach (string exeFolder in SafeFolders(natives))
            {
                foreach (string folder in SafeFolders(exeFolder))
                {
                    if (SameFolder(folder, current)) continue;
                    RemoveUnused(folder);
                }
                try { if (Directory.GetFileSystemEntries(exeFolder).Length == 0) Directory.Delete(exeFolder); }
                catch { }
            }
            foreach (string folder in SafeFolders(root))
                if (Path.GetFileName(folder).Contains(".untrusted-", StringComparison.Ordinal)) RemoveUnused(folder);
            string parent = Path.GetDirectoryName(root);
            if (parent != null)
                foreach (string folder in SafeFolders(parent))
                    if (Path.GetFileName(folder).StartsWith(Path.GetFileName(root) + ".untrusted-", StringComparison.OrdinalIgnoreCase))
                        RemoveUnused(folder);
        }

        private static bool SameFolder(string a, string b)
            => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> SafeFolders(string folder)
        {
            try { return Directory.GetDirectories(folder); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>Renames a folder aside and deletes it. A link is deleted
        /// as the link, never followed (Directory.Delete leaves what a link
        /// points at alone).</summary>
        private static void RemoveUnused(string folder)
        {
            string aside = folder + ".removing-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                if (folder.Contains(".removing-", StringComparison.Ordinal)) aside = folder;
                else Directory.Move(folder, aside);
            }
            catch
            {
                return;
            }
            try { Directory.Delete(aside, recursive: true); }
            catch { }
        }
    }
}
