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
    /// stay out, as everything PadForge writes for a report does.</para>
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

        /// <summary>The words that open a record's lines in crash.log, with
        /// its record number, by which a later start knows it copied it.</summary>
        internal const string Opening = "Windows Application Error record ";

        /// <summary>The program whose records are copied.</summary>
        internal const string Program = "PadForge.exe";

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
        /// log), with 0x before it.</summary>
        internal static string Hex(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
            foreach (char c in value)
                if (!char.IsAsciiHexDigit(c)) return value;
            return "0x" + value.ToUpperInvariant();
        }

        /// <summary>The lines crash.log gets for one record.</summary>
        internal static string Lines(NativeCrash crash)
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(crash.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ")
              .Append(Opening).Append(crash.RecordId.ToString(CultureInfo.InvariantCulture)).Append(": ")
              .Append(crash.Program).Append(' ').Append(crash.ProgramVersion).Append(" stopped in native code.\n")
              .Append("  Faulting module: ").Append(crash.Module).Append(' ').Append(crash.ModuleVersion).Append('\n')
              .Append("  Exception code: ").Append(crash.ExceptionCode).Append('\n')
              .Append("  Offset: ").Append(crash.Offset).Append("\n\n");
            return sb.ToString();
        }

        /// <summary>
        /// Copies into <paramref name="crashLog"/> each record crash.log does
        /// not hold yet, by its record number. Gives how many were copied.
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
            var sb = new StringBuilder();
            int copied = 0;
            foreach (var crash in records)
            {
                if (held.Contains(Opening + crash.RecordId.ToString(CultureInfo.InvariantCulture) + ":", StringComparison.Ordinal)) continue;
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
