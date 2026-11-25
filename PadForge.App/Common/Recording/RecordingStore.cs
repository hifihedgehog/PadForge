using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PadForge.Engine.Recording;
using PadForge.Services;

namespace PadForge.Common.Recording
{
    /// <summary>A finished recording that waits in the recordings' folder
    /// for its review (#600).</summary>
    /// <param name="Path">The zip.</param>
    /// <param name="Started">When it started, in local time.</param>
    /// <param name="Finished">When its zip was written, in UTC.</param>
    internal sealed record WaitingRecording(string Path, DateTime Started, DateTime Finished);

    /// <summary>
    /// The finished recordings in the recordings' folder (#600). Every zip
    /// there waits for its review, since a saved one leaves the folder when
    /// it is saved and a deleted one with its Delete. One that waited seven
    /// days goes by itself, since it holds what was typed.
    ///
    /// <para>The folder is the user's, so this touches only PadForge's own
    /// files there: a name PadForge gives, no link, and for a zip one whose
    /// .log starts as a recording does.</para>
    /// </summary>
    internal static class RecordingStore
    {
        /// <summary>The recordings that wait, newest first.</summary>
        internal static List<WaitingRecording> Waiting(string folder)
        {
            var waiting = new List<WaitingRecording>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return waiting;
            string[] files;
            try { files = Directory.GetFiles(folder, ProblemRecorder.FilePrefix + "*" + ProblemRecorder.ZipSuffix); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return waiting; }
            foreach (string file in files)
            {
                // The one test Delete takes too, so a card Home shows can
                // always be cleared.
                if (!IsOwnName(file, ProblemRecorder.ZipSuffix) || RecordingFolder.IsLink(file) || !HoldsRecording(file)) continue;
                DateTime finished;
                try { finished = File.GetLastWriteTimeUtc(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                waiting.Add(new WaitingRecording(file, StartedOf(file) ?? finished.ToLocalTime(), finished));
            }
            waiting.Sort((a, b) => b.Finished.CompareTo(a.Finished));
            return waiting;
        }

        /// <summary>The local time a recording's name gives, or null.</summary>
        internal static DateTime? StartedOf(string path)
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith(ProblemRecorder.FilePrefix, StringComparison.OrdinalIgnoreCase)) return null;
            string stamp = name.Substring(ProblemRecorder.FilePrefix.Length);
            if (stamp.Length < 15) return null;
            return DateTime.TryParseExact(stamp.Substring(0, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var started) ? started : null;
        }

        /// <summary>True for a name PadForge gives a recording's file: its
        /// prefix, a stamp, an optional number, and <paramref name="suffix"/>.</summary>
        internal static bool IsOwnName(string path, string suffix)
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith(ProblemRecorder.FilePrefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
            string middle = name.Substring(ProblemRecorder.FilePrefix.Length, name.Length - ProblemRecorder.FilePrefix.Length - suffix.Length);
            if (middle.Length < 15 || middle[8] != '-') return false;
            foreach (char c in middle)
                if (!(char.IsAsciiDigit(c) || c == '-' || c == '.' || char.IsAsciiHexDigitLower(c))) return false;
            return true;
        }

        /// <summary>True when the zip holds a recording: its one .log starts
        /// with a recording's first line.</summary>
        internal static bool HoldsRecording(string zip)
        {
            try
            {
                using var reader = RecordingReader.OpenText(zip);
                var first = FirstLines(reader, 1);
                return first.Count == 1 && first[0].StartsWith(RecordingCodec.FirstLineStart, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>The longest line <see cref="FirstLines"/> reads. A
        /// recording's opening lines are far shorter.</summary>
        internal const int LongestOpeningLine = 1024;

        /// <summary>Up to <paramref name="count"/> opening lines of a file
        /// in a folder the user can write, each read to
        /// <see cref="LongestOpeningLine"/> characters at most, so a file
        /// planted there with no line break is never read whole.</summary>
        internal static List<string> FirstLines(System.IO.TextReader reader, int count)
        {
            var lines = new List<string>(count);
            var sb = new System.Text.StringBuilder(128);
            while (lines.Count < count)
            {
                sb.Clear();
                int c;
                while ((c = reader.Read()) != -1 && c != '\n')
                {
                    if (sb.Length >= LongestOpeningLine) return lines;
                    if (c != '\r') sb.Append((char)c);
                }
                if (c == -1 && sb.Length == 0) break;
                lines.Add(sb.ToString());
                if (c == -1) break;
            }
            return lines;
        }

        /// <summary>Deletes a waiting recording, PadForge's own only. True
        /// when it is gone.</summary>
        internal static bool Delete(string zip)
        {
            if (string.IsNullOrEmpty(zip) || !File.Exists(zip)) return true;
            if (!IsOwnName(zip, ProblemRecorder.ZipSuffix) || RecordingFolder.IsLink(zip) || !HoldsRecording(zip)) return false;
            try
            {
                File.Delete(zip);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes what waited too long: a recording whose zip is older than
        /// <paramref name="keep"/>, and a zip left half made. Gives how many
        /// files went.
        /// </summary>
        internal static int Sweep(string folder, DateTime nowUtc, TimeSpan keep)
        {
            int gone = 0;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || RecordingFolder.IsLink(folder)) return 0;
            string[] files;
            try { files = Directory.GetFiles(folder, ProblemRecorder.FilePrefix + "*"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
            foreach (string file in files)
            {
                try
                {
                    if (RecordingFolder.IsLink(file)) continue;
                    var age = nowUtc - File.GetLastWriteTimeUtc(file);
                    if (age < keep) continue;
                    bool zip = IsOwnName(file, ProblemRecorder.ZipSuffix) && HoldsRecording(file);
                    // A zip half made, a review's unpacked text or a copy's
                    // text, and a copy with parts left out that was never
                    // saved.
                    bool halfMade = file.EndsWith(ProblemRecorder.TempSuffix, StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(RecordingReview.CopySuffix, StringComparison.OrdinalIgnoreCase);
                    if (!zip && !halfMade) continue;
                    File.Delete(file);
                    gone++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return gone;
        }
    }
}
