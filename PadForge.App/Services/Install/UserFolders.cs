using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PadForge.Services.Install
{
    /// <summary>The folders of the user's own an install writes into.</summary>
    internal enum UserFolder
    {
        /// <summary>The user's profile, %USERPROFILE%, where the command-line
        /// assistants keep their dot folders.</summary>
        Profile,
        /// <summary>Documents, wherever the user moved it: OneDrive's
        /// Documents when OneDrive backs it up.</summary>
        Documents,
        /// <summary>%APPDATA%.</summary>
        RoamingData,
        /// <summary>%LOCALAPPDATA%, which holds each packaged app's own data
        /// under Packages.</summary>
        LocalData,
    }

    /// <summary>
    /// The user's own folders, read through Windows' known-folder API
    /// (SHGetKnownFolderPath) for the token of the process that asks (#600).
    /// So a Documents folder the user moved, OneDrive's included, is the one
    /// Windows names, and an install run without rights reads the signed-in
    /// user's folders, never an administrator's.
    ///
    /// <para>A packaged app (MSIX) keeps what it writes under AppData in a
    /// folder of its package's own, and reads that copy first once it
    /// exists (Microsoft, "Understanding how packaged desktop apps run on
    /// Windows"). <see cref="PackagedRoaming"/> finds that folder.</para>
    ///
    /// <para>The test host reads no folder of the machine's: with
    /// <see cref="OffLimitsForTest"/> set, a folder comes from
    /// <see cref="Seam"/> or not at all.</para>
    /// </summary>
    internal static class UserFolders
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app: no real folder is named, so no install writes
        /// into the bench's own profile.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: the folder for each kind, under a test's own
        /// temporary folder. Null in production.</summary>
        internal static Func<UserFolder, string> Seam;

        // KnownFolders.h.
        private static readonly Guid FolderIdProfile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
        private static readonly Guid FolderIdDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
        private static readonly Guid FolderIdRoamingAppData = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
        private static readonly Guid FolderIdLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);

        /// <summary>The folder, or null when Windows names none.</summary>
        internal static string Of(UserFolder folder)
        {
            if (OffLimitsForTest && Seam == null) return null;
            var seam = Seam;
            if (seam != null) return seam(folder);
            Guid id = folder switch
            {
                UserFolder.Documents => FolderIdDocuments,
                UserFolder.RoamingData => FolderIdRoamingAppData,
                UserFolder.LocalData => FolderIdLocalAppData,
                _ => FolderIdProfile,
            };
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr text) != 0) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { Marshal.FreeCoTaskMem(text); }
        }

        /// <summary>
        /// Where a packaged app whose package family starts with
        /// <paramref name="familyPrefix"/> keeps its roaming data:
        /// %LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Roaming, or null
        /// when no such package wrote any. The family is matched by its
        /// name and the underscore before its publisher ID, so Claude_ never
        /// matches ClaudeTools_.
        /// </summary>
        internal static string PackagedRoaming(string familyPrefix)
        {
            string local = Of(UserFolder.LocalData);
            if (string.IsNullOrEmpty(local) || string.IsNullOrEmpty(familyPrefix)) return null;
            string packages = Path.Combine(local, "Packages");
            if (!Directory.Exists(packages)) return null;
            foreach (string family in Directory.EnumerateDirectories(packages, familyPrefix + "_*"))
            {
                string roaming = Path.Combine(family, "LocalCache", "Roaming");
                if (Directory.Exists(roaming)) return roaming;
            }
            return null;
        }
    }
}
