using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// Where a problem recording is written while it runs and waits for its
    /// review (#600): a folder of its own in PadForge's temp folder, which
    /// the user can open and empty. A file is deleted only when it is
    /// PadForge's own (<see cref="IsLink"/>).
    ///
    /// <para>The test host reads no folder of the machine's: with
    /// <see cref="OffLimitsForTest"/> set, the folder comes from
    /// <see cref="Seam"/>, or not at all.</para>
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

        /// <summary>The recordings' folder in PadForge's temp folder, made
        /// when missing, or null when it is a link.</summary>
        internal static string Get()
        {
            if (OffLimitsForTest && Seam == null) return null;
            var seam = Seam;
            if (seam != null) return seam();
            string folder = Path.Combine(Path.GetTempPath(), Name);
            if (IsLink(folder)) return null;
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Whether <paramref name="path"/> is a reparse point: a
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

        // KnownFolders.h.
        private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

        /// <summary>The user's Downloads folder, wherever the user moved it.
        /// Null when Windows names none.</summary>
        internal static string Downloads()
        {
            if (OffLimitsForTest && DownloadsSeam == null) return null;
            var seam = DownloadsSeam;
            if (seam != null) return seam();
            if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out IntPtr text) != 0) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { Marshal.FreeCoTaskMem(text); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);
    }
}
