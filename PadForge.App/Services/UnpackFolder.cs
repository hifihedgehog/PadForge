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
    /// (#600).
    ///
    /// <para>The host unpacks into
    /// <c>&lt;base&gt;\&lt;exe name&gt;\&lt;bundle ID&gt;</c> (extractor.cpp,
    /// extraction_dir), where the base is DOTNET_BUNDLE_EXTRACT_BASE_DIR, which
    /// <see cref="ProtectedStart"/> sets for every start with rights, or the
    /// temp folder's <c>.net</c> when it is not set. The folder is the one a
    /// loaded module sits in directly below <c>&lt;base&gt;\&lt;exe name&gt;</c>.
    /// Nothing else is ever taken for it: before #600 the app fell back to the
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
        internal static void CleanUp(string current) => CleanUp(current, BaseFolder());

        /// <summary>The same, for a process unpacked under
        /// <paramref name="baseFolder"/>. A test gives the protected folder
        /// of its own (ProtectedFolder.RootSeam).</summary>
        internal static void CleanUp(string current, string baseFolder)
        {
            string natives = ProtectedFolder.Natives, root = ProtectedFolder.Root;
            if (natives.Length == 0 || current == null || baseFolder == null) return;
            if (!SameFolder(baseFolder, natives)) return;
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
                if (IsSetAside(Path.GetFileName(folder), null)) RemoveUnused(folder);
            // In %ProgramData%, where any account can make a folder, only the
            // names MakeSound gives the root's set-aside folders are touched.
            string parent = Path.GetDirectoryName(root);
            if (parent != null)
                foreach (string folder in SafeFolders(parent))
                    if (IsSetAside(Path.GetFileName(folder), Path.GetFileName(root)))
                        RemoveUnused(folder);
        }

        private static readonly System.Text.RegularExpressions.Regex SetAside = new(
            @"^(?<name>[^\\/]+)\.untrusted-[0-9a-f]{1,16}-[0-9a-f]{1,16}-[0-9a-f]{1,16}(\.removing-[0-9]{1,10})?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Whether a folder's name is one ProtectedFolder.MakeSound
        /// gives a folder it moves aside (ProtectedFolder.AsideName), with
        /// the name this clean-up gives it while it is deleted, for a folder
        /// of the name <paramref name="of"/>, or of any name when that is
        /// null.</summary>
        internal static bool IsSetAside(string name, string of)
        {
            var match = SetAside.Match(name ?? string.Empty);
            return match.Success && (of == null || string.Equals(match.Groups["name"].Value, of, StringComparison.OrdinalIgnoreCase));
        }

        private static bool SameFolder(string a, string b)
            => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> SafeFolders(string folder)
        {
            try { return Directory.GetDirectories(folder); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>Renames a folder aside and deletes it by handles
        /// (<see cref="HandleTree.Delete"/>): another account may change a
        /// set-aside folder while it is deleted, and a link anywhere in it,
        /// one swapped in during the delete included, is deleted as the link
        /// and never followed.</summary>
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
            HandleTree.Delete(aside);
        }
    }
}
