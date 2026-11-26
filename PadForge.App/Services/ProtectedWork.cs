using System;
using System.IO;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// Folders under the protected folder's Work folder (#600) for what
    /// PadForge with rights writes and then starts, installs, loads or hands
    /// to a tool: an installer and what it unpacks, a driver package before it
    /// is signed and stocked, the DSX stand-in, a task's definition before
    /// schtasks reads it, a vendor library PadForge downloads.
    ///
    /// <para>Before #600 these went to the user's temp folder, which every
    /// program of the user can write, so a program without rights could swap
    /// a file between PadForge's check and its use, or put a library beside
    /// an installer, which Windows loads from the installer's own folder
    /// first. Only Administrators and SYSTEM can change anything under Work
    /// (<see cref="ProtectedFolder"/>), so a file there is the one PadForge
    /// wrote and checked. The test host has no protected folder, and every
    /// member answers null there.</para>
    /// </summary>
    internal static class ProtectedWork
    {
        /// <summary><c>Work\&lt;purpose&gt;</c>, made sound when it is not,
        /// for what is kept between uses. Null when it cannot be.</summary>
        internal static string Folder(string purpose)
        {
            string work = ProtectedFolder.Work;
            if (string.IsNullOrEmpty(work) || string.IsNullOrEmpty(purpose)) return null;
            if (!ProtectedFolder.MakeSound(work)) return null;
            string folder = Path.Combine(work, purpose);
            return ProtectedFolder.MakeSound(folder) ? folder : null;
        }

        /// <summary>
        /// <see cref="Folder"/> in the app. In the test host, which has no
        /// protected folder, <paramref name="testFolder"/>, where nothing is
        /// started, installed or loaded. In the app a folder that cannot be
        /// made is an error, never the user's temp folder.
        /// </summary>
        internal static string FolderInTheApp(string purpose, string testFolder)
            => Folder(purpose) ?? (ProtectedFolder.OffLimitsForTest ? testFolder
                : throw new IOException("The protected folder for " + purpose + " could not be made under " + ProtectedFolder.Work + "."));

        /// <summary>A new folder of its own under
        /// <c>Work\&lt;purpose&gt;</c>, for one install or one download.
        /// Null when it cannot be made.</summary>
        internal static string NewFolder(string purpose)
        {
            string parent = Folder(purpose);
            if (parent == null) return null;
            string folder = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A new folder like <see cref="NewFolder"/>, or an
        /// exception that names why there is none, for the callers that report
        /// failures that way.</summary>
        internal static string NewFolderOrThrow(string purpose)
            => NewFolder(purpose) ?? throw new IOException("The protected folder for " + purpose + " could not be made under " + ProtectedFolder.Work + ".");

        /// <summary>
        /// Removes the one-install folders <see cref="NewFolder"/> made that
        /// are older than <paramref name="age"/>: an installer that outlived
        /// its wait leaves its folder, and nothing else can tell when it is
        /// done. A folder still in use cannot be renamed and stays. Folders
        /// kept between uses (<see cref="Folder"/>) are not touched.
        /// </summary>
        internal static void RemoveOld(TimeSpan age)
        {
            string work = ProtectedFolder.Work;
            if (string.IsNullOrEmpty(work) || !Directory.Exists(work)) return;
            DateTime before = DateTime.UtcNow - age;
            try
            {
                foreach (string purpose in Directory.GetDirectories(work))
                    foreach (string folder in Directory.GetDirectories(purpose))
                    {
                        string name = Path.GetFileName(folder);
                        if (name.EndsWith(".removing", StringComparison.Ordinal))
                        {
                            try { Directory.Delete(folder, recursive: true); }
                            catch { }
                            continue;
                        }
                        if (!Guid.TryParseExact(name, "N", out _)) continue;
                        if (Directory.GetLastWriteTimeUtc(folder) > before) continue;
                        string aside = folder + ".removing";
                        try { Directory.Move(folder, aside); }
                        catch { continue; }
                        try { Directory.Delete(aside, recursive: true); }
                        catch { }
                    }
            }
            catch
            {
            }
        }

        /// <summary>Deletes a folder this class made, with all in it. Best
        /// effort.</summary>
        internal static void Remove(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            string work = ProtectedFolder.Work;
            if (string.IsNullOrEmpty(work)) return;
            string full = Path.GetFullPath(folder);
            if (!full.StartsWith(Path.GetFullPath(work).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return;
            try { Directory.Delete(full, recursive: true); }
            catch { }
        }
    }
}
