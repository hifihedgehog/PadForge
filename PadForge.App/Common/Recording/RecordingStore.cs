using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
            if (!IsOwnName(zip, ProblemRecorder.ZipSuffix)) return false;
            string full;
            try { full = Path.GetFullPath(zip); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            string final = FolderFinalPath(Path.GetDirectoryName(full));
            return final != null && DeleteOwn(full, final, recording: true, nowUtc: default, keep: TimeSpan.Zero);
        }

        /// <summary>Test seam: runs between the sweep's listing and its
        /// deletes. Null in production.</summary>
        internal static Action SweepSeam;

        /// <summary>
        /// Deletes what waited too long: a recording whose zip is older than
        /// <paramref name="keep"/>, a file PadForge left half made, and a
        /// recording's text that no finish took. Gives how many files went.
        ///
        /// <para>This runs in the copy with rights, in a folder a program
        /// without rights can change, so a file is deleted only through the
        /// one handle every test of it is made on: opened without following a
        /// link at its name, refused when Windows' final path of it is not in
        /// the folder the sweep began on (a folder swapped for a link since),
        /// and deleted by that handle, never by its path again. The pattern
        /// is UserInstall.TakeReport's.</para>
        /// </summary>
        internal static int Sweep(string folder, DateTime nowUtc, TimeSpan keep)
        {
            int gone = 0;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return 0;
            string final = FolderFinalPath(folder);
            if (final == null) return 0;
            string[] files;
            try { files = Directory.GetFiles(folder, ProblemRecorder.FilePrefix + "*"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
            SweepSeam?.Invoke();
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                // A recording, a copy with parts left out that was never
                // saved, and a zip, a review's unpacked text or a copy's text
                // half made, each by the name PadForge gives it.
                bool recording = IsOwnName(name, ProblemRecorder.ZipSuffix);
                // A recording's text is finished at the start before the
                // sweep, and one that runs is written ten times a second, so
                // a text older than the keep is one no finish took: one past
                // the limits every read keeps, as a program could plant.
                bool unfinished = IsOwnName(name, ProblemRecorder.PartialSuffix);
                if (!recording && !unfinished && !IsCopyName(name) && !IsHalfMadeName(name)) continue;
                if (DeleteOwn(file, final, recording, nowUtc, keep)) gone++;
            }
            return gone;
        }

        /// <summary>True for a copy's name: a recording's name with a dot and
        /// the copy's tag of 8 hex digits before <see cref="RecordingReview.CopySuffix"/>
        /// (RecordingReview.WriteKept).</summary>
        internal static bool IsCopyName(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.EndsWith(RecordingReview.CopySuffix, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = name.Substring(0, name.Length - RecordingReview.CopySuffix.Length);
            int dot = rest.LastIndexOf('.');
            return dot > 0 && IsTag(rest.AsSpan(dot + 1), 8) && IsOwnName(rest.Substring(0, dot) + ProblemRecorder.ZipSuffix, ProblemRecorder.ZipSuffix);
        }

        /// <summary>True for the name of a file PadForge makes on the way to
        /// another and deletes when it is done: a recording's or a copy's
        /// name, a dot and 32 hex digits, then <see cref="ProblemRecorder.TempSuffix"/>
        /// (the zip ProblemRecorder.Finish packs, the text RecordingReview.Open
        /// unpacks), and a copy's text, a recording's stem, a dot, the copy's
        /// tag and <see cref="ProblemRecorder.PartialSuffix"/> before it
        /// (RecordingReview.WriteKept).</summary>
        internal static bool IsHalfMadeName(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.EndsWith(ProblemRecorder.TempSuffix, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = name.Substring(0, name.Length - ProblemRecorder.TempSuffix.Length);
            if (rest.EndsWith(ProblemRecorder.PartialSuffix, StringComparison.OrdinalIgnoreCase))
            {
                string text = rest.Substring(0, rest.Length - ProblemRecorder.PartialSuffix.Length);
                int tag = text.LastIndexOf('.');
                return tag > 0 && IsTag(text.AsSpan(tag + 1), 8) && IsOwnName(text.Substring(0, tag) + ProblemRecorder.ZipSuffix, ProblemRecorder.ZipSuffix);
            }
            int dot = rest.LastIndexOf('.');
            if (dot <= 0 || !IsTag(rest.AsSpan(dot + 1), 32)) return false;
            string made = rest.Substring(0, dot);
            return IsOwnName(made, ProblemRecorder.ZipSuffix) || IsCopyName(made);
        }

        private static bool IsTag(ReadOnlySpan<char> text, int length)
        {
            if (text.Length != length) return false;
            foreach (char c in text)
                if (!char.IsAsciiDigit(c) && !char.IsAsciiHexDigitLower(c)) return false;
            return true;
        }

        /// <summary>Windows' final path of a folder that is no link itself,
        /// or null.</summary>
        private static string FolderFinalPath(string folder)
        {
            using var handle = CreateFileW(folder, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
                FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
            if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.FileAttributes & (uint)FileAttributes.Directory) == 0) return null;
            return FinalPathOf(handle);
        }

        /// <summary>
        /// Deletes one file of the folder whose final path is
        /// <paramref name="folderFinal"/>, through one handle: opened without
        /// following a link at its name, the file itself and no link, its
        /// final path in that folder, older than <paramref name="keep"/> when
        /// a keep is given, a recording when <paramref name="recording"/> is
        /// set, all read on that handle, which then deletes it.
        /// </summary>
        private static bool DeleteOwn(string path, string folderFinal, bool recording, DateTime nowUtc, TimeSpan keep)
        {
            using var handle = CreateFileW(path, GenericRead | DeleteAccess, ShareAll, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            string final = FinalPathOf(handle);
            if (final == null || !string.Equals(final, Path.Combine(folderFinal, Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase)) return false;
            if (!GetFileInformationByHandle(handle, out var info)) return false;
            if ((info.FileAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
            if (keep > TimeSpan.Zero)
            {
                long written = ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
                if (nowUtc - DateTime.FromFileTimeUtc(written) < keep) return false;
            }
            if (recording)
            {
                // Read through the handle without handing it over, so the
                // same handle deletes the file below.
                using var view = new FileStream(new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false), FileAccess.Read, 1 << 12);
                if (!HoldsRecording(view)) return false;
            }
            // The name goes at once, as DeleteFileW takes it on NTFS, though
            // another program (a scanner) still holds the file open: POSIX
            // semantics (FILE_DISPOSITION_INFO_EX, winbase.h). A volume that
            // has no such delete takes the one that waits for the last
            // handle.
            var posix = new FileDispositionInfoEx { Flags = DispositionDelete | DispositionPosixSemantics };
            if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref posix, (uint)Marshal.SizeOf<FileDispositionInfoEx>())) return true;
            var delete = new FileDispositionInfo { DeleteFile = 1 };
            return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref delete, (uint)Marshal.SizeOf<FileDispositionInfo>());
        }

        /// <summary>True when a stream holds a recording: a zip whose one .log
        /// starts with a recording's first line, or that text itself, read as
        /// RecordingReader.OpenText reads a file.</summary>
        private static bool HoldsRecording(Stream stream)
        {
            try
            {
                Span<byte> head = stackalloc byte[4];
                int read = stream.Read(head);
                stream.Position = 0;
                List<string> first;
                if (read == 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4)
                {
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                    ZipArchiveEntry log = null;
                    foreach (var entry in archive.Entries)
                        if (entry.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) { log = entry; break; }
                    if (log == null) return false;
                    using var text = new StreamReader(log.Open(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                    first = FirstLines(text, 1);
                }
                else
                {
                    using var text = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 12, leaveOpen: true);
                    first = FirstLines(text, 1);
                }
                return first.Count == 1 && first[0].StartsWith(RecordingCodec.FirstLineStart, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                return false;
            }
        }

        private static string FinalPathOf(SafeFileHandle handle)
        {
            var text = new StringBuilder(1024);
            uint n = GetFinalPathNameByHandleW(handle, text, (uint)text.Capacity, 0);
            if (n == 0 || n >= text.Capacity) return null;
            string path = text.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path.Substring(8);
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }

        private const uint GenericRead = 0x80000000, DeleteAccess = 0x00010000, FileReadAttributes = 0x80;
        private const uint ShareAll = 1 | 2 | 4, OpenExisting = 3;
        private const uint FlagOpenReparsePoint = 0x00200000, FlagBackupSemantics = 0x02000000;
        // FILE_INFO_BY_HANDLE_CLASS (minwinbase.h): FileDispositionInfo 4,
        // FileDispositionInfoEx 21. FILE_DISPOSITION_FLAG_DELETE 1 and
        // FILE_DISPOSITION_FLAG_POSIX_SEMANTICS 2 (winbase.h).
        private const int FileDispositionInfoClass = 4, FileDispositionInfoExClass = 21;
        private const uint DispositionDelete = 0x1, DispositionPosixSemantics = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo { public byte DeleteFile; }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfoEx { public uint Flags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FileDispositionInfo info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref FileDispositionInfoEx info, uint size);
    }
}
