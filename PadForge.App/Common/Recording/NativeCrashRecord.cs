using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Text;

namespace PadForge.Common.Recording
{
    /// <summary>Windows' own record of a crash in native code, as the
    /// Application log keeps it (Application Error, event 1000).</summary>
    internal sealed record NativeCrash(long RecordId, DateTime Time, string Program, string ProgramVersion,
        string Module, string ModuleVersion, string ExceptionCode, string Offset);

    /// <summary>
    /// A crash in native code (#600): SDL3.dll, HIDMaestro's library or any
    /// other module PadForge loads can end the process before a .NET handler
    /// runs, so crash.log gets nothing. Windows still writes its own record
    /// to the Application log (Application Error, event 1000: the faulting
    /// module, its version, the offset and the exception code). At its next
    /// start PadForge copies each such record of PadForge.exe that crash.log
    /// does not hold yet into crash.log, so a report carries it with no
    /// trip to the Event Viewer.
    ///
    /// <para>Only file names and numbers are copied: the record's folders
    /// stay out, as everything PadForge writes for a report does. Any
    /// program can write an Application Error event, so a record whose
    /// fields are not the file names, versions and hex numbers Windows
    /// writes is left out whole (<see cref="Copies"/>).</para>
    ///
    /// <para>The test host reads no log of the machine's: with
    /// <see cref="OffLimitsForTest"/> set, records come from
    /// <see cref="RecordsSeam"/> or not at all.</para>
    /// </summary>
    internal static class NativeCrashRecord
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: the records the log would give.</summary>
        internal static Func<IReadOnlyList<NativeCrash>> RecordsSeam;

        /// <summary>How far back a start looks: two weeks.</summary>
        internal static readonly TimeSpan LooksBack = TimeSpan.FromDays(14);

        /// <summary>The words that open a record's lines in crash.log, after
        /// its time and before its record number. A later start knows a
        /// record it copied by the time and the number together
        /// (<see cref="Key"/>), since a cleared log numbers its records
        /// from the start again.</summary>
        internal const string Opening = "Windows Application Error record ";

        /// <summary>The program whose records are copied.</summary>
        internal const string Program = "PadForge.exe";

        /// <summary>The exception code the .NET runtime ends a process with
        /// when an exception goes unhandled (e0434352 in this PC's log). That
        /// crash is in managed code, and crash.log has the runtime's own
        /// block for it, so its record is not copied.</summary>
        internal const ulong ManagedCode = 0xE0434352;

        /// <summary>The longest version and file name a record copied may
        /// carry.</summary>
        private const int LongestVersion = 32, LongestName = 255;

        /// <summary>PadForge.exe's records of the last two weeks, newest
        /// last. None when the log cannot be read.</summary>
        internal static IReadOnlyList<NativeCrash> Records()
        {
            if (OffLimitsForTest && RecordsSeam == null) return Array.Empty<NativeCrash>();
            var seam = RecordsSeam;
            if (seam != null) return seam();
            var found = new List<NativeCrash>();
            string query = "*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[timediff(@SystemTime) <= "
                + ((long)LooksBack.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "]]]";
            try
            {
                using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query));
                for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                {
                    using (record)
                    {
                        var crash = Of(record);
                        if (crash != null) found.Add(crash);
                    }
                }
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
            {
                // No log to read: nothing to copy.
            }
            found.Sort((a, b) => a.RecordId.CompareTo(b.RecordId));
            return found;
        }

        /// <summary>A record of PadForge.exe, or null for another program's.
        /// Event 1000's data is, in order: the program, its version, its
        /// stamp, the module, its version, its stamp, the exception code and
        /// the offset.</summary>
        private static NativeCrash Of(EventRecord record)
        {
            var data = record.Properties;
            if (data == null || data.Count < 8) return null;
            string Text(int i) => data[i]?.Value?.ToString() ?? string.Empty;
            if (!string.Equals(Path.GetFileName(Text(0)), Program, StringComparison.OrdinalIgnoreCase)) return null;
            return new NativeCrash(record.RecordId ?? 0, record.TimeCreated ?? DateTime.MinValue, Path.GetFileName(Text(0)), Text(1),
                Path.GetFileName(Text(3)), Text(4), Hex(Text(6)), Hex(Text(7)));
        }

        /// <summary>A code or an offset as the log writes it, hex digits
        /// with no prefix (e0434352, 00000000000c408a, read from this PC's
        /// log), with 0x before it. Anything else comes back as it is, which
        /// <see cref="Copies"/> then refuses.</summary>
        internal static string Hex(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
            foreach (char c in value)
                if (!char.IsAsciiHexDigit(c)) return value;
            return "0x" + value.ToUpperInvariant();
        }

        /// <summary>
        /// Whether a record is copied: a crash of PadForge.exe in native
        /// code whose fields have the shapes Windows writes, the module a
        /// file name, the two versions digits and dots, and the code and the
        /// offset hex of at most 16 digits. A record of the .NET runtime's
        /// own code (<see cref="ManagedCode"/>) is a crash in managed code.
        /// </summary>
        internal static bool Copies(NativeCrash crash)
            => crash != null
               && string.Equals(crash.Program, Program, StringComparison.OrdinalIgnoreCase)
               && IsFileName(crash.Module)
               && IsVersion(crash.ProgramVersion) && IsVersion(crash.ModuleVersion)
               && HexValue(crash.ExceptionCode) is ulong code && code != ManagedCode
               && HexValue(crash.Offset) != null;

        private static bool IsVersion(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > LongestVersion) return false;
            foreach (char c in value)
                if (!char.IsAsciiDigit(c) && c != '.') return false;
            return true;
        }

        private static bool IsFileName(string value)
            => !string.IsNullOrEmpty(value) && value.Length <= LongestName && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        /// <summary>The number a "0x" and 1 to 16 hex digits give, or null
        /// for any other text.</summary>
        private static ulong? HexValue(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("0x", StringComparison.Ordinal) || value.Length < 3 || value.Length > 18) return null;
            for (int i = 2; i < value.Length; i++)
                if (!char.IsAsciiHexDigit(value[i])) return null;
            return ulong.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        /// <summary>How crash.log names a record: its time and its record
        /// number, the opening of its first line. The time is the PC's
        /// local time as the log gives it, so a later start in another time
        /// zone reads the same record hours apart (<see cref="Holds"/>).</summary>
        internal static string Key(NativeCrash crash)
            => "[" + crash.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] "
               + Opening + crash.RecordId.ToString(CultureInfo.InvariantCulture) + ":";

        /// <summary>The lines crash.log gets for one record.</summary>
        internal static string Lines(NativeCrash crash)
        {
            var sb = new StringBuilder();
            sb.Append(Key(crash)).Append(' ')
              .Append(crash.Program).Append(' ').Append(crash.ProgramVersion).Append(" stopped in native code.\n")
              .Append("  Faulting module: ").Append(crash.Module).Append(' ').Append(crash.ModuleVersion).Append('\n')
              .Append("  Exception code: ").Append(crash.ExceptionCode).Append('\n')
              .Append("  Offset: ").Append(crash.Offset).Append("\n\n");
            return sb.ToString();
        }

        /// <summary>The widest a change of time zone moves a local time: from
        /// UTC-12 to UTC+14.</summary>
        internal static readonly TimeSpan ZonesApart = TimeSpan.FromHours(26);

        private static readonly System.Text.RegularExpressions.Regex HeldBlock = new(
            @"\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] " + System.Text.RegularExpressions.Regex.Escape(Opening) + @"(\d+):",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The records crash.log holds, by number, with the local
        /// times its blocks give them.</summary>
        private static Dictionary<long, List<DateTime>> HeldRecords(string held)
        {
            var records = new Dictionary<long, List<DateTime>>();
            foreach (System.Text.RegularExpressions.Match m in HeldBlock.Matches(held))
            {
                if (!long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long id)
                    || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
                if (!records.TryGetValue(id, out var times)) records[id] = times = new List<DateTime>();
                times.Add(time);
            }
            return records;
        }

        /// <summary>Whether crash.log holds a record: a block of its number
        /// whose time is no further from its own than a change of time zone
        /// moves it. A cleared log hands a number out again, and a crash days
        /// later of an old number is a record of its own.</summary>
        private static bool Holds(Dictionary<long, List<DateTime>> held, NativeCrash crash)
        {
            if (!held.TryGetValue(crash.RecordId, out var times)) return false;
            foreach (var time in times)
                if ((time - crash.Time).Duration() <= ZonesApart) return true;
            return false;
        }

        /// <summary>
        /// Copies into <paramref name="crashLog"/> each record it takes
        /// (<see cref="Copies"/>) that crash.log does not hold yet
        /// (<see cref="Holds"/>). Gives how many were copied.
        /// </summary>
        internal static int CopyNew(string crashLog)
        {
            var records = Records();
            if (records.Count == 0) return 0;
            string held = string.Empty;
            try
            {
                if (File.Exists(crashLog)) held = File.ReadAllText(crashLog);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
            var holds = HeldRecords(held);
            var sb = new StringBuilder();
            int copied = 0;
            foreach (var crash in records)
            {
                if (!Copies(crash) || Holds(holds, crash)) continue;
                sb.Append(Lines(crash));
                copied++;
            }
            if (copied == 0) return 0;
            try { File.AppendAllText(crashLog, sb.ToString()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
            return copied;
        }
    }
}
