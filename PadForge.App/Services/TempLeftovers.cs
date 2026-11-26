using System;
using System.IO;

namespace PadForge.Services
{
    /// <summary>
    /// The copies builds before 69931c721 (#600) left in the user's temp
    /// folder, which no code reads any more: the voice model unpacked into
    /// PadForge\voice-models, 68 MB, and the DSX stand-in written into
    /// PadForge_DSX. A start that finds either deletes it, and the PadForge
    /// folder when that leaves it empty. The delete runs as the user: the
    /// temp folder is the user's own, and a recursive delete there with
    /// PadForge's rights could be led into another folder by a link a
    /// program of the user put in place of a folder while it ran.
    /// </summary>
    internal static class TempLeftovers
    {
        /// <summary>The two folders under a temp folder.</summary>
        internal static string[] Folders(string tempRoot) => new[]
        {
            Path.Combine(tempRoot, "PadForge", "voice-models"),
            Path.Combine(tempRoot, "PadForge_DSX"),
        };

        /// <summary>Deletes the folders left under <paramref name="tempRoot"/>
        /// through <paramref name="asUser"/>, which runs the delete as the
        /// user and says whether it could, and the PadForge folder they leave
        /// empty. Nothing is started when neither is there. Gives how many of
        /// the two are gone.</summary>
        internal static int Retire(string tempRoot, Func<Action, bool> asUser)
        {
            var folders = Folders(tempRoot);
            if (!Directory.Exists(folders[0]) && !Directory.Exists(folders[1])) return 0;
            int gone = 0;
            bool ran = asUser(() =>
            {
                foreach (string folder in folders)
                {
                    if (!Directory.Exists(folder)) continue;
                    try
                    {
                        Directory.Delete(folder, recursive: true);
                        gone++;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                string parent = Path.Combine(tempRoot, "PadForge");
                try
                {
                    if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0) Directory.Delete(parent);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            });
            return ran ? gone : 0;
        }

        /// <summary>At a start of the app, the user's temp folder, as the
        /// user. Never in the test host, whose temp folder is the developer's
        /// own. Never throws.</summary>
        internal static void RetireInTheApp()
        {
            if (PadForge.Common.ProtectedFolder.OffLimitsForTest) return;
            try
            {
                int gone = Retire(Path.GetTempPath(), PadForge.Common.ShellUser.AsTheUser);
                if (gone > 0) PadForge.Engine.SdlDiagLog.WriteLine("Removed what older builds left in the temp folder: " + gone + " of its two folders.");
            }
            catch (Exception error)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("The copies older builds left in the temp folder could not be removed: " + error.Message);
            }
        }
    }
}
