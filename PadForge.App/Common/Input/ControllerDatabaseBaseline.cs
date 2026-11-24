using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PadForge.Common.Input
{
    /// <summary>How this release's SDL reads one controller ID of the
    /// community database's Windows rows.</summary>
    internal enum ControllerDatabaseVerdict
    {
        /// <summary>The SDL fork carries the community row.</summary>
        Kept,
        /// <summary>SDL maps the controller in its own Windows section.</summary>
        SdlWindows,
        /// <summary>SDL maps the controller on other platforms only, so on
        /// Windows nothing does unless a driver reads it.</summary>
        SdlElsewhere,
        /// <summary>The fork leaves the row out for another reason: a driver
        /// reads the controller, SDL's controller list names it, SDL types
        /// it as a wheel or a flight stick, or it is vJoy's shared ID.</summary>
        LeftOut,
    }

    /// <summary>One line of the baseline. <see cref="RowHash"/> is null for a
    /// controller ID of SDL's own Windows section that the pinned copy has
    /// no row for.</summary>
    internal readonly record struct ControllerDatabaseBaselineEntry(ControllerDatabaseVerdict Verdict, string RowHash);

    /// <summary>
    /// What this release carries of the community controller database
    /// (#597): the copy the SDL fork pinned, and for each controller ID of
    /// its Windows rows a hash of the row and how SDL reads it. PadForge
    /// cannot ask SDL3.dll which community rows it holds, so
    /// tools/gen_controller_database_baseline.py writes this list from the
    /// fork's own files at each SDL take, and ControllerDatabaseBaselineTests
    /// holds it to both SDL3.dll files.
    /// </summary>
    internal sealed class ControllerDatabaseBaseline
    {
        internal const string ResourceName = "PadForge.gamecontrollerdb_baseline.txt";

        private readonly Dictionary<string, ControllerDatabaseBaselineEntry> _entries;

        /// <summary>The SDL fork commit the list was written from.</summary>
        public string ForkCommit { get; }

        /// <summary>The community repository's commit the fork pinned.</summary>
        public string PinCommit { get; }

        public DateTime PinTimeUtc { get; }

        /// <summary>SHA-256 of the pinned gamecontrollerdb.txt.</summary>
        public string PinnedSha256 { get; }

        /// <summary>Windows rows of the pinned copy.</summary>
        public int WindowsRows { get; }

        /// <summary>Rows of SDL's own Windows section.</summary>
        public int SdlWindowsRows { get; }

        /// <summary>Community rows the fork carries.</summary>
        public int KeptRows { get; }

        /// <summary>Controller IDs the pinned copy has a Windows row for.</summary>
        public IReadOnlyCollection<string> PinnedIds { get; }

        public int Count => _entries.Count;

        /// <summary>Every line: a controller ID, and how SDL reads it.</summary>
        public IEnumerable<KeyValuePair<string, ControllerDatabaseBaselineEntry>> Entries => _entries;

        private ControllerDatabaseBaseline(string forkCommit, string pinCommit, DateTime pinTimeUtc, string pinnedSha256,
            int windowsRows, int sdlWindowsRows, Dictionary<string, ControllerDatabaseBaselineEntry> entries)
        {
            ForkCommit = forkCommit;
            PinCommit = pinCommit;
            PinTimeUtc = pinTimeUtc;
            PinnedSha256 = pinnedSha256;
            WindowsRows = windowsRows;
            SdlWindowsRows = sdlWindowsRows;
            _entries = entries;
            var pinned = new List<string>();
            int kept = 0;
            foreach (var (id, entry) in entries)
            {
                if (entry.RowHash != null) pinned.Add(id);
                if (entry.Verdict == ControllerDatabaseVerdict.Kept) kept++;
            }
            PinnedIds = pinned;
            KeptRows = kept;
        }

        public bool TryFind(string id, out ControllerDatabaseBaselineEntry entry)
        {
            if (id != null) return _entries.TryGetValue(id, out entry);
            entry = default;
            return false;
        }

        private IReadOnlySet<string> _ownIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Whether PadForge's own embedded file has a row for a
        /// controller ID, as SDL's lookup reaches it. The release carries
        /// that row itself, so no downloaded row is kept for the ID.</summary>
        public bool HasOwnRow(string id) => id != null && _ownIds.Contains(id);

        /// <summary>This list with PadForge's own rows beside it
        /// (<see cref="ControllerDatabaseUse.OwnIds"/>).</summary>
        internal ControllerDatabaseBaseline WithOwnRows(IEnumerable<string> ownRows) =>
            new(ForkCommit, PinCommit, PinTimeUtc, PinnedSha256, WindowsRows, SdlWindowsRows, _entries)
            {
                _ownIds = ControllerDatabaseUse.OwnIds(ownRows ?? Array.Empty<string>()),
            };

        private static readonly Lazy<ControllerDatabaseBaseline> LazyEmbedded = new(() =>
        {
            using var stream = typeof(ControllerDatabaseBaseline).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidDataException("The embedded resource " + ResourceName + " is missing.");
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd()).WithOwnRows(ControllerDatabaseUse.OwnRows);
        });

        /// <summary>The list this build carries, with PadForge's own rows
        /// beside it. A build without a readable one fails
        /// ControllerDatabaseBaselineTests.</summary>
        public static ControllerDatabaseBaseline Embedded => LazyEmbedded.Value;

        /// <summary>Reads the list. A line it cannot read is an error: the
        /// file is written by a script and is never edited by hand.</summary>
        internal static ControllerDatabaseBaseline Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            string fork = null, pin = null, sha256 = null;
            DateTime pinTime = default;
            int format = 0, windowsRows = -1, sdlWindowsRows = -1;
            var entries = new Dictionary<string, ControllerDatabaseBaselineEntry>(StringComparer.Ordinal);
            int number = 0;
            foreach (string raw in text.Split('\n'))
            {
                number++;
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split(' ');
                switch (parts[0])
                {
                    case "format" when parts.Length == 2:
                        format = Number(parts[1], number);
                        break;
                    case "fork" when parts.Length == 2 && IsHex(parts[1], 40):
                        fork = parts[1];
                        break;
                    case "pin" when parts.Length == 3 && IsHex(parts[1], 40):
                        pin = parts[1];
                        if (!DateTime.TryParseExact(parts[2], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out pinTime))
                            throw Bad(number, line);
                        break;
                    case "sha256" when parts.Length == 2 && IsHex(parts[1], 64):
                        sha256 = parts[1];
                        break;
                    case "windows-rows" when parts.Length == 2:
                        windowsRows = Number(parts[1], number);
                        break;
                    case "sdl-windows-rows" when parts.Length == 2:
                        sdlWindowsRows = Number(parts[1], number);
                        break;
                    case "K" or "S" or "O" or "N" when parts.Length == 3 && IsHex(parts[1], 32)
                                                      && (parts[2] == "-" || IsHex(parts[2], 16)):
                        var verdict = parts[0] switch
                        {
                            "K" => ControllerDatabaseVerdict.Kept,
                            "S" => ControllerDatabaseVerdict.SdlWindows,
                            "O" => ControllerDatabaseVerdict.SdlElsewhere,
                            _ => ControllerDatabaseVerdict.LeftOut,
                        };
                        if (!entries.TryAdd(parts[1], new ControllerDatabaseBaselineEntry(verdict, parts[2] == "-" ? null : parts[2])))
                            throw Bad(number, line);
                        break;
                    default:
                        throw Bad(number, line);
                }
            }
            if (format != 1 || fork == null || pin == null || sha256 == null || windowsRows < 0 || sdlWindowsRows < 0)
                throw new InvalidDataException("The controller database baseline lacks a header line.");
            return new ControllerDatabaseBaseline(fork, pin, pinTime, sha256, windowsRows, sdlWindowsRows, entries);
        }

        private static InvalidDataException Bad(int number, string line) =>
            new("The controller database baseline cannot be read at line " + number.ToString(CultureInfo.InvariantCulture) + ": " + line);

        private static int Number(string text, int number) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : throw Bad(number, text);

        private static bool IsHex(string text, int length)
        {
            if (text.Length != length) return false;
            foreach (char c in text)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
    }
}
