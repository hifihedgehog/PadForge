using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PadForge.Services.Plugins;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// Where a problem recording is written while it runs and waits for its
    /// review (#600): a folder of its own in the signed-in user's temp
    /// folder, which the user can open and empty. PadForge can run with
    /// rights another account gave it (over-the-shoulder elevation), and then
    /// its own temp folder is that account's. So the folder is read from the
    /// desktop shell's variables, which are the signed-in user's, as the
    /// plugin launcher reads them for a plugin it starts. Every program of
    /// the user can set those variables, so the folder is taken only inside
    /// the user's own profile, which Windows names for the shell's token
    /// (<see cref="TempOf"/>).
    ///
    /// <para>The folder is the user's to change, and PadForge writes into it
    /// with its rights, so the recorder never follows a link: a folder or a
    /// file that is a reparse point is refused, the folders from the profile
    /// down to the recordings' folder among them (<see cref="LinkOnTheWay"/>),
    /// files are made new and never
    /// written over, and a file is deleted only when it is PadForge's own
    /// (<see cref="IsLink"/>).</para>
    ///
    /// <para>The test host reads no folder of the machine's: with
    /// <see cref="OffLimitsForTest"/> set, the folder comes from
    /// <see cref="Seam"/>, or from the user <see cref="UserSeam"/> gives,
    /// or not at all.</para>
    /// </summary>
    internal static class RecordingFolder
    {
        /// <summary>The folder's name under the temp folder.</summary>
        internal const string Name = "PadForge Recordings";

        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: the folder. Null in production.</summary>
        internal static Func<string> Seam;

        /// <summary>Test seam: the user's Downloads folder. Null in
        /// production.</summary>
        internal static Func<string> DownloadsSeam;

        /// <summary>What <see cref="ReadUser"/> reads of the signed-in user:
        /// whether it read the desktop shell's (PadForge runs with rights the
        /// shell does not have) or this process's own, the variables as
        /// <c>name=value</c> lines, and the user's profile folder.</summary>
        internal readonly record struct UserView(bool Shell, IReadOnlyList<string> Variables, string Profile);

        /// <summary>Test seam: what the user's branch read, in place of the
        /// shell's or this process's. Null in production.</summary>
        internal static Func<UserView> UserSeam;

        /// <summary>Test seam: runs the folder's making as the user and says
        /// whether it could, in place of
        /// <see cref="PadForge.Common.ShellUser.AsTheUser"/>. Null in
        /// production.</summary>
        internal static Func<Action, bool> AsUserSeam;

        /// <summary>The recordings' folder, made when missing, or null when
        /// there is none to use: no temp folder of the user's inside the
        /// user's profile, one reached through a link below the profile, or
        /// no user to make it as. The folder is made as the user
        /// (<see cref="PadForge.Common.ShellUser.AsTheUser"/>), so a
        /// folder on the way swapped for a junction between the check and
        /// the making leads it only where the user could write
        /// anyway.</summary>
        internal static string Get()
        {
            if (OffLimitsForTest && Seam == null && UserSeam == null) return null;
            var seam = Seam;
            if (seam != null) return seam();
            var view = UserSeam?.Invoke() ?? ReadUser();
            string temp = TempOf(view.Variables, view.Profile);
            if (string.IsNullOrEmpty(temp)) return null;
            string folder = Path.Combine(temp, Name);
            if (LinkOnTheWay(folder, view.Profile)) return null;
            if (!Directory.Exists(folder))
            {
                Exception failed = null;
                bool ran = (AsUserSeam ?? PadForge.Common.ShellUser.AsTheUser)(() =>
                {
                    try { Directory.CreateDirectory(folder); }
                    catch (Exception ex) { failed = ex; }
                });
                if (!ran) return null;
                if (failed != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed).Throw();
            }
            return LinkOnTheWay(folder, view.Profile) ? null : folder;
        }

        /// <summary>True when <paramref name="path"/> is a reparse point: a
        /// junction, a symbolic link or a mount point.</summary>
        internal static bool IsLink(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists && !Directory.Exists(path)) return false;
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return true;
            }
        }

        /// <summary>
        /// What the signed-in user's branch reads: the desktop shell's
        /// variables and profile folder, read through the shell's token, when
        /// PadForge runs with rights the shell does not have, and this
        /// process's own otherwise, as in the copy the review's Save… starts
        /// as the user. The profile is null when Windows names none.
        /// </summary>
        internal static UserView ReadUser()
        {
            if (PluginLauncher.ThisProcessHasAdministratorRights() != false)
            {
                using var shell = PluginLauncher.OpenShell();
                if (shell != null && PluginLauncher.HasAdministratorRights(shell) == false)
                {
                    var variables = PluginLauncher.ShellVariables(shell, out _);
                    if (!OpenProcessToken(shell, TokenQuery | TokenImpersonate | TokenDuplicate, out var token)) return new UserView(true, variables, null);
                    using (token) return new UserView(true, variables, KnownFolder(FolderIdProfile, token.DangerousGetHandle()));
                }
            }
            return new UserView(false, System32Calls.Variables(), KnownFolder(FolderIdProfile, IntPtr.Zero));
        }

        /// <summary>
        /// The user's temp folder in <paramref name="variables"/>: TEMP, then
        /// TMP, then the Temp folder of LOCALAPPDATA, the first that is a full
        /// path inside <paramref name="profile"/>. Both branches read it this
        /// one way, so the save copy, whose <see cref="Path.GetTempPath"/>
        /// would read TMP first (GetTempPath2W), looks in the folder PadForge
        /// wrote the recording into. Every program of the user can set these
        /// variables (HKCU\Environment) and PadForge writes the folder with
        /// its rights, so a path outside the user's own profile is passed
        /// over. Null when none is, or with no profile.
        /// </summary>
        internal static string TempOf(IReadOnlyList<string> variables, string profile)
        {
            if (string.IsNullOrEmpty(profile) || !Path.IsPathFullyQualified(profile)) return null;
            string local = Variable(variables, "LOCALAPPDATA");
            foreach (string candidate in new[] { Variable(variables, "TEMP"), Variable(variables, "TMP"),
                         string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Temp") })
            {
                if (string.IsNullOrEmpty(candidate) || !Path.IsPathFullyQualified(candidate)) continue;
                string full;
                try { full = Path.GetFullPath(candidate); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (Below(full, profile) != null) return full;
            }
            return null;
        }

        /// <summary>True when the folder itself or a folder between
        /// <paramref name="profile"/> and <paramref name="folder"/> is a
        /// reparse point (<see cref="IsLink"/>), or when the folder does not
        /// lie inside the profile. A folder not made yet is no link.</summary>
        internal static bool LinkOnTheWay(string folder, string profile)
        {
            string root = Below(folder, profile);
            if (root == null) return true;
            for (string at = Path.GetFullPath(folder); at != null && at.Length > root.Length; at = Path.GetDirectoryName(at))
                if (IsLink(at)) return true;
            return false;
        }

        /// <summary>The spelling of <paramref name="profile"/> that
        /// <paramref name="path"/> starts with, compared by the long names of
        /// both, so a TEMP written in 8.3 names lies in the profile too, or
        /// null when the path does not lie inside the profile.</summary>
        private static string Below(string path, string profile)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(profile)) return null;
            string full, root;
            try
            {
                full = Path.GetFullPath(path);
                root = Path.GetFullPath(profile).TrimEnd('\\');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
            if (full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return root;
            string longFull = LongName(full), longRoot = LongName(root).TrimEnd('\\');
            if (!longFull.StartsWith(longRoot + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            // The path's own spelling of the profile: as many of its folders
            // as the profile's long name has.
            int depth = longRoot.Split('\\').Length;
            string[] parts = full.Split('\\');
            return parts.Length > depth ? string.Join("\\", parts, 0, depth) : null;
        }

        /// <summary>The long name of the deepest folder of
        /// <paramref name="path"/> that is there, with the rest of the path
        /// after it as written.</summary>
        private static string LongName(string path)
        {
            string rest = string.Empty;
            for (string at = path; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
            {
                var text = new System.Text.StringBuilder(1024);
                uint n = GetLongPathNameW(at, text, (uint)text.Capacity);
                if (n > 0 && n < text.Capacity) return text.ToString().TrimEnd('\\') + rest;
                string name = Path.GetFileName(at);
                if (string.IsNullOrEmpty(name)) break;
                rest = "\\" + name + rest;
            }
            return path;
        }

        private static string Variable(IReadOnlyList<string> variables, string name)
        {
            if (variables == null) return null;
            string prefix = name + "=";
            foreach (string entry in variables)
                if (entry != null && entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return entry.Substring(prefix.Length);
            return null;
        }

        // ── Downloads ──

        private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

        /// <summary>FOLDERID_Profile, the folder .NET's
        /// <see cref="Environment.SpecialFolder.UserProfile"/> reads
        /// (Environment.Win32.cs, KnownFolders.Profile).</summary>
        private static readonly Guid FolderIdProfile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
        private const uint TokenQuery = 0x0008, TokenImpersonate = 0x0004, TokenDuplicate = 0x0002;

        /// <summary>The signed-in user's Downloads folder, wherever the user
        /// moved it, read through the shell's token when PadForge runs with
        /// rights the shell does not have. Null when Windows names none.</summary>
        internal static string Downloads()
        {
            if (OffLimitsForTest && DownloadsSeam == null) return null;
            var seam = DownloadsSeam;
            if (seam != null) return seam();
            if (PluginLauncher.ThisProcessHasAdministratorRights() != false)
            {
                using var shell = PluginLauncher.OpenShell();
                if (shell != null && PluginLauncher.HasAdministratorRights(shell) == false)
                {
                    if (!OpenProcessToken(shell, TokenQuery | TokenImpersonate | TokenDuplicate, out var token)) return null;
                    using (token) return KnownFolder(FolderIdDownloads, token.DangerousGetHandle());
                }
            }
            return KnownFolder(FolderIdDownloads, IntPtr.Zero);
        }

        /// <summary>The signed-in user's profile folder as Windows names it
        /// for <paramref name="token"/>, this process's user with none. Null
        /// when Windows names none.</summary>
        internal static string ProfileOf(IntPtr token) => KnownFolder(FolderIdProfile, token);

        private static string KnownFolder(Guid folder, IntPtr token)
        {
            if (SHGetKnownFolderPath(folder, 0, token, out IntPtr text) != 0) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { Marshal.FreeCoTaskMem(text); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathNameW(string shortPath, System.Text.StringBuilder longPath, uint size);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    }
}
