using System;
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
    /// plugin launcher reads them for a plugin it starts.
    ///
    /// <para>The folder is the user's to change, and PadForge writes into it
    /// with its rights, so the recorder never follows a link: a folder or a
    /// file that is a reparse point is refused, files are made new and never
    /// written over, and a file is deleted only when it is PadForge's own
    /// (<see cref="IsLink"/>).</para>
    ///
    /// <para>The test host reads no folder of the machine's: with
    /// <see cref="OffLimitsForTest"/> set, the folder comes from
    /// <see cref="Seam"/> or not at all.</para>
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

        /// <summary>The recordings' folder, made when missing, or null when
        /// there is none to use: no temp folder, or one that is a link.</summary>
        internal static string Get()
        {
            if (OffLimitsForTest && Seam == null) return null;
            var seam = Seam;
            if (seam != null) return seam();
            string temp = UserTemp();
            if (string.IsNullOrEmpty(temp)) return null;
            string folder = Path.Combine(temp, Name);
            Directory.CreateDirectory(folder);
            return IsLink(folder) ? null : folder;
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

        /// <summary>The signed-in user's temp folder: the shell's TEMP when
        /// PadForge runs with rights the shell does not have, and this
        /// process's own otherwise.</summary>
        private static string UserTemp()
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            if (PluginLauncher.HasAdministratorRights(self.SafeHandle) != false)
            {
                using var shell = PluginLauncher.OpenShell();
                if (shell != null && PluginLauncher.HasAdministratorRights(shell) == false)
                {
                    var variables = PluginLauncher.ShellVariables(shell, out _);
                    string temp = Variable(variables, "TEMP") ?? Variable(variables, "TMP");
                    if (!string.IsNullOrEmpty(temp) && Path.IsPathFullyQualified(temp)) return temp;
                    string local = Variable(variables, "LOCALAPPDATA");
                    if (!string.IsNullOrEmpty(local) && Path.IsPathFullyQualified(local)) return Path.Combine(local, "Temp");
                    return null;
                }
            }
            return Path.GetTempPath();
        }

        private static string Variable(string[] variables, string name)
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
        private const uint TokenQuery = 0x0008, TokenImpersonate = 0x0004, TokenDuplicate = 0x0002;

        /// <summary>The signed-in user's Downloads folder, wherever the user
        /// moved it, read through the shell's token when PadForge runs with
        /// rights the shell does not have. Null when Windows names none.</summary>
        internal static string Downloads()
        {
            if (OffLimitsForTest && DownloadsSeam == null) return null;
            var seam = DownloadsSeam;
            if (seam != null) return seam();
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            if (PluginLauncher.HasAdministratorRights(self.SafeHandle) != false)
            {
                using var shell = PluginLauncher.OpenShell();
                if (shell != null && PluginLauncher.HasAdministratorRights(shell) == false)
                {
                    if (!OpenProcessToken(shell, TokenQuery | TokenImpersonate | TokenDuplicate, out var token)) return null;
                    using (token) return KnownFolder(token.DangerousGetHandle());
                }
            }
            return KnownFolder(IntPtr.Zero);
        }

        private static string KnownFolder(IntPtr token)
        {
            if (SHGetKnownFolderPath(FolderIdDownloads, 0, token, out IntPtr text) != 0) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { Marshal.FreeCoTaskMem(text); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    }
}
