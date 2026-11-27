using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using PadForge.Resources.Strings;
using PadForge.Services;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// Save… of the review (#600): the recording is copied where the user
    /// picked, in this process.
    /// </summary>
    internal static class RecordingSave
    {
        /// <summary>What <see cref="Run"/> gives: saved, the file not
        /// written, and a source that is no recording of the user's own
        /// folder.</summary>
        internal const int Saved = 0, NotWritten = 1, NotTaken = 2;

        /// <summary>Copies <paramref name="source"/> to <paramref name="target"/>
        /// in this process, on a worker thread. Null when it is saved, and
        /// otherwise the line the review shows, in the user's
        /// language.</summary>
        internal static async Task<string> SaveAsync(string source, string target)
        {
            int code = await Task.Run(() => Run(source, target, out _)).ConfigureAwait(false);
            return code switch
            {
                Saved => null,
                NotWritten => Strings.Instance.Review_SaveNotWritten,
                _ => Strings.Instance.Review_SaveNotOwn,
            };
        }

        /// <summary>Whether a file is a recording in the user's own
        /// recordings folder: a waiting zip, or a copy the review made with
        /// parts left out. A folder that cannot be read or made holds
        /// none.</summary>
        internal static bool IsOwnRecording(string source)
        {
            string full;
            try { full = Path.GetFullPath(source ?? string.Empty); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            return IsOwnFull(full);
        }

        private static bool IsOwnFull(string full)
        {
            string folder;
            try { folder = RecordingFolder.Get(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
            string name = Path.GetFileName(full);
            // A save the Automation or Plugins page staged there (UserSave)
            // is copied as a recording is.
            bool staged = UserSave.IsStaged(full);
            bool own = staged || RecordingStore.IsOwnName(full, ProblemRecorder.ZipSuffix)
                || (name.StartsWith(ProblemRecorder.FilePrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(RecordingReview.CopySuffix, StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrEmpty(folder) && string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && own && !RecordingFolder.IsLink(full) && File.Exists(full) && (staged || RecordingStore.HoldsRecording(full));
        }

        /// <summary>
        /// The copy itself, in this process. The source has to be a
        /// recording in the user's own recordings folder (a waiting zip, or a
        /// copy the review made with parts left out), so the command saves
        /// nothing else. Gives <see cref="Saved"/>, <see cref="NotTaken"/>
        /// for a source that is none of those, and <see cref="NotWritten"/>
        /// when the file could not be written, with the reason in English.
        /// </summary>
        internal static int Run(string source, string target, out string error)
        {
            error = null;
            string full, into;
            try
            {
                full = Path.GetFullPath(source ?? string.Empty);
                into = Path.GetFullPath(target ?? string.Empty);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = ex.Message;
                return NotTaken;
            }
            if (!IsOwnFull(full))
            {
                error = "PadForge did not take this file as a recording of its own.";
                return NotTaken;
            }
            try
            {
                File.Copy(full, into, overwrite: true);
                return Saved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = ex.Message;
                return NotWritten;
            }
        }
    }
}
