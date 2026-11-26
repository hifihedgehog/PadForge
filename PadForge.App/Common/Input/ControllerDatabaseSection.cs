using System;
using System.Collections.Generic;
using System.Globalization;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>How a stored row is used.</summary>
    internal enum ControllerDatabaseRowKind
    {
        /// <summary>A row for a controller this release has no Windows row
        /// for. SDL is given it only when such a controller connects and
        /// nothing else reads it as a gamepad.</summary>
        New,
        /// <summary>A newer form of a community row the SDL fork carries. SDL
        /// is given it as it starts, in place of the fork's copy.</summary>
        Changed,
    }

    /// <summary>A row of the section. <see cref="InUse"/> marks a new row
    /// that a controller was first read through: from then on the row is
    /// that controller's reading, whatever the user assigned it to
    /// since.</summary>
    internal sealed record ControllerDatabaseStoredRow(ControllerDatabaseRow Row, ControllerDatabaseRowKind Kind, bool InUse);

    /// <summary>
    /// The controller database section of PadForge.xml (#597): the rows of
    /// the community's current copy that are new or changed against what this
    /// release carries, and where and when they were fetched. Never changed
    /// once built. Update Now, the row's reset and a load replace the whole
    /// section (SettingsManager.ControllerDatabase), so the poll thread reads
    /// one without a lock.
    /// </summary>
    internal sealed class ControllerDatabaseSection
    {
        private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
        private const int MaxETagLength = 256;
        /// <summary>What the file writes on a row in use.</summary>
        private const string InUseMark = "true";

        private readonly Dictionary<string, ControllerDatabaseStoredRow> _newById = new(StringComparer.Ordinal);

        public DateTime FetchedUtc { get; }

        /// <summary>The address the rows were fetched from, in the form
        /// <see cref="ControllerDatabaseAddress.TryRead"/> gives, or null when
        /// the file named none it could read.</summary>
        public string Source { get; }

        /// <summary>The tag GitHub gave the download, or null.</summary>
        public string ETag { get; }

        /// <summary>SHA-256 of the download, or null.</summary>
        public string Sha256 { get; }

        /// <summary>Windows rows of the downloaded copy.</summary>
        public int WindowsRows { get; }

        /// <summary>The community commit this release's SDL fork pinned when
        /// the rows were chosen.</summary>
        public string BaselinePin { get; }

        /// <summary>The PadForge build that stored the section.</summary>
        public string AppVersion { get; }

        /// <summary>The stored rows, by controller ID.</summary>
        public IReadOnlyList<ControllerDatabaseStoredRow> Rows { get; }

        public int NewRows { get; }

        public int ChangedRows { get; }

        private ControllerDatabaseSection(DateTime fetchedUtc, string source, string etag, string sha256, int windowsRows,
            string baselinePin, string appVersion, List<ControllerDatabaseStoredRow> rows)
        {
            FetchedUtc = fetchedUtc;
            Source = source;
            ETag = etag;
            Sha256 = sha256;
            WindowsRows = windowsRows;
            BaselinePin = baselinePin;
            AppVersion = appVersion;
            rows.Sort((a, b) => string.CompareOrdinal(a.Row.Guid, b.Row.Guid));
            Rows = rows;
            foreach (var row in rows)
            {
                if (row.Kind == ControllerDatabaseRowKind.New)
                {
                    NewRows++;
                    _newById[row.Row.Guid] = row;
                }
                else
                {
                    ChangedRows++;
                }
            }
        }

        /// <summary>The new row for a controller ID, if the section has one.</summary>
        public bool TryFindNew(string id, out ControllerDatabaseStoredRow row)
        {
            if (id != null) return _newById.TryGetValue(id, out row);
            row = null;
            return false;
        }

        /// <summary>
        /// What a row of the community's current copy is to this release, or
        /// null when the section does not hold it.
        ///
        /// <para>A controller PadForge's own file has a row for takes none
        /// from here. The same row as the pinned one is carried already when
        /// the fork kept it, and a changed row the fork kept replaces the
        /// fork's copy. A controller SDL maps on other platforms only has no
        /// Windows row in this release, changed or not, since the fork leaves
        /// its row out, so the row is new on Windows. So is one for a
        /// controller ID the pinned copy does not have. A controller SDL maps
        /// in its own Windows section, or one the fork leaves out for another
        /// reason, takes no row from here.</para>
        /// </summary>
        internal static ControllerDatabaseRowKind? Classify(ControllerDatabaseBaseline baseline, ControllerDatabaseRow row)
        {
            if (baseline.HasOwnRow(row.Guid)) return null;
            if (!baseline.TryFind(row.Guid, out var entry)) return ControllerDatabaseRowKind.New;
            switch (entry.Verdict)
            {
                case ControllerDatabaseVerdict.Kept:
                    return string.Equals(entry.RowHash, row.Hash, StringComparison.Ordinal) ? null : ControllerDatabaseRowKind.Changed;
                case ControllerDatabaseVerdict.SdlElsewhere:
                    return ControllerDatabaseRowKind.New;
                default:
                    return null;
            }
        }

        /// <summary>The section a checked download from
        /// <paramref name="source"/> gives. From an address that is not the
        /// community's, only new rows are kept: no date in a file from
        /// another host tells a newer row from an older one, so a changed row
        /// could roll back one this release carries (#600). A new row in use
        /// keeps its mark, and stays when the downloaded copy no longer
        /// carries it: it is how a controller the user set up is read.</summary>
        internal static ControllerDatabaseSection FromDownload(ControllerDatabaseDownload download, string etag, DateTime fetchedUtc,
            ControllerDatabaseBaseline baseline, string appVersion, ControllerDatabaseSection previous,
            string source = ControllerDatabaseAddress.Community)
        {
            if (download == null) throw new ArgumentNullException(nameof(download));
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            bool community = ControllerDatabaseAddress.IsCommunity(source);
            var rows = new List<ControllerDatabaseStoredRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in download.Rows)
            {
                seen.Add(row.Guid);
                bool used = previous != null && previous.TryFindNew(row.Guid, out var before) && before.InUse;
                var kind = Classify(baseline, row);
                if (kind == null || (kind == ControllerDatabaseRowKind.Changed && !community)) continue;
                rows.Add(new ControllerDatabaseStoredRow(row, kind.Value, used && kind == ControllerDatabaseRowKind.New));
            }
            if (previous != null)
            {
                foreach (var before in previous.Rows)
                {
                    if (!before.InUse || before.Kind != ControllerDatabaseRowKind.New || seen.Contains(before.Row.Guid)) continue;
                    if (Classify(baseline, before.Row) == ControllerDatabaseRowKind.New) rows.Add(before);
                }
            }
            return new ControllerDatabaseSection(TrimToSeconds(fetchedUtc), source, CleanETag(etag), download.Sha256, download.WindowsRows,
                baseline.PinCommit, appVersion, rows);
        }

        /// <summary>
        /// The section a settings file holds, read against this release.
        /// <paramref name="changed"/> says the file no longer matches what
        /// was read, so the settings are saved again.
        ///
        /// <para>Every row is judged again by this release's baseline, so a
        /// row a newer release carries itself, in the fork's copy or in
        /// PadForge's own file, is dropped, and so is one for a
        /// controller SDL now maps in its Windows section or the fork now
        /// leaves out. A row whose text no longer passes the grammar is
        /// dropped too, whoever wrote it.</para>
        ///
        /// <para>Rows from an address that is not the community's are new
        /// rows only, as <see cref="FromDownload"/> keeps them, so a changed
        /// row a file holds from there goes.</para>
        ///
        /// <para>A release with another pin leaves the stored tag unused, so
        /// the next Update Now fetches the whole file and judges it against
        /// the new pin. A changed row fetched before that pin's commit is
        /// older than the fork's own copy, and goes.</para>
        /// </summary>
        internal static ControllerDatabaseSection FromData(ControllerDatabaseData data, ControllerDatabaseBaseline baseline,
            string appVersion, out bool changed)
        {
            changed = false;
            if (data == null) return null;
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            // An element that holds only the user's own rows or the address
            // of Download From (#600) holds no download.
            if (data.FetchedUtc == null && data.Source == null && (data.Rows == null || data.Rows.Length == 0)) return null;
            if (!DateTime.TryParseExact(data.FetchedUtc, TimeFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime fetched))
            {
                changed = true;
                return null;
            }

            bool otherPin = !string.Equals(data.BaselinePin, baseline.PinCommit, StringComparison.Ordinal);
            // The address the rows came from. One the file does not hold in
            // the form PadForge writes is no address, and its tag is sent to
            // none.
            bool sourceRead = ControllerDatabaseAddress.TryRead(data.Source, out string source)
                && string.Equals(source, data.Source, StringComparison.Ordinal);
            if (!sourceRead) source = null;
            string etag = otherPin || !sourceRead ? null : CleanETag(data.ETag);
            string sha256 = IsSha256(data.Sha256) ? data.Sha256 : null;
            if (!int.TryParse(data.WindowsRows, NumberStyles.None, CultureInfo.InvariantCulture, out int windowsRows))
                windowsRows = 0;

            var rows = new List<ControllerDatabaseStoredRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string last = null;
            foreach (var stored in data.Rows ?? Array.Empty<ControllerDatabaseRowData>())
            {
                if (stored == null || !ControllerDatabaseRow.TryParse(stored.Text, out var row) || !seen.Add(row.Guid))
                {
                    changed = true;
                    continue;
                }
                bool marked = string.Equals(stored.InUse, InUseMark, StringComparison.Ordinal);
                var kind = Classify(baseline, row);
                if (kind == ControllerDatabaseRowKind.Changed && otherPin && fetched < baseline.PinTimeUtc) kind = null;
                // Rows from an address that is not the community's are new
                // rows only (#600), whoever wrote the file.
                if (kind == ControllerDatabaseRowKind.Changed && !ControllerDatabaseAddress.IsCommunity(source)) kind = null;
                if (kind == null)
                {
                    changed = true;
                    continue;
                }
                bool used = marked && kind == ControllerDatabaseRowKind.New;
                if (!string.Equals(stored.InUse, used ? InUseMark : null, StringComparison.Ordinal)
                    || !string.Equals(stored.Kind, kind.Value.ToString(), StringComparison.Ordinal)
                    || (last != null && string.CompareOrdinal(last, row.Guid) > 0))
                    changed = true;
                last = row.Guid;
                rows.Add(new ControllerDatabaseStoredRow(row, kind.Value, used));
            }

            if (otherPin || !sourceRead
                || !string.Equals(windowsRows.ToString(CultureInfo.InvariantCulture), data.WindowsRows, StringComparison.Ordinal)
                || !string.Equals(etag ?? string.Empty, data.ETag ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(sha256 ?? string.Empty, data.Sha256 ?? string.Empty, StringComparison.Ordinal)
                || !string.Equals(appVersion ?? string.Empty, data.AppVersion ?? string.Empty, StringComparison.Ordinal))
                changed = true;
            return new ControllerDatabaseSection(fetched, source, etag, sha256, windowsRows, baseline.PinCommit, appVersion, rows);
        }

        /// <summary>The element the settings file holds for the downloaded
        /// rows, the user's own (#600) and the address the user set under
        /// Download From, or null, which leaves it out, when there are none
        /// of them.</summary>
        internal static ControllerDatabaseData Compose(ControllerDatabaseSection section, ControllerDatabaseMine mine,
            string downloadFrom = ControllerDatabaseAddress.Community)
        {
            var data = section?.ToData();
            var rows = mine?.ToData();
            var owed = mine?.PutBacksData();
            string from = downloadFrom == null || ControllerDatabaseAddress.IsCommunity(downloadFrom) ? null : downloadFrom;
            if (rows == null && owed == null && from == null) return data;
            data ??= new ControllerDatabaseData();
            data.Mine = rows;
            data.PutBack = owed;
            data.DownloadFrom = from;
            return data;
        }

        /// <summary>The section as the settings file holds it.</summary>
        internal ControllerDatabaseData ToData()
        {
            var rows = new ControllerDatabaseRowData[Rows.Count];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new ControllerDatabaseRowData
                {
                    Kind = Rows[i].Kind.ToString(),
                    InUse = Rows[i].InUse ? InUseMark : null,
                    Text = Rows[i].Row.Text,
                };
            return new ControllerDatabaseData
            {
                Source = Source,
                FetchedUtc = FetchedUtc.ToString(TimeFormat, CultureInfo.InvariantCulture),
                ETag = ETag,
                Sha256 = Sha256,
                WindowsRows = WindowsRows.ToString(CultureInfo.InvariantCulture),
                BaselinePin = BaselinePin,
                AppVersion = AppVersion,
                Rows = rows.Length > 0 ? rows : null,
            };
        }

        /// <summary>This section with the new row for a controller ID marked
        /// in use, or this section itself when it has no such row or the row
        /// is marked already.</summary>
        internal ControllerDatabaseSection WithRowInUse(string id)
        {
            if (!TryFindNew(id, out var found) || found.InUse) return this;
            var rows = new List<ControllerDatabaseStoredRow>(Rows.Count);
            foreach (var row in Rows)
                rows.Add(ReferenceEquals(row, found) ? row with { InUse = true } : row);
            return new ControllerDatabaseSection(FetchedUtc, Source, ETag, Sha256, WindowsRows, BaselinePin, AppVersion, rows);
        }

        /// <summary>A tag as it goes back to GitHub in If-None-Match:
        /// visible ASCII, of a sane length. Anything else is no tag.</summary>
        internal static string CleanETag(string etag)
        {
            if (string.IsNullOrEmpty(etag) || etag.Length > MaxETagLength) return null;
            foreach (char c in etag)
                if (c < 0x21 || c > 0x7E) return null;
            return etag;
        }

        private static bool IsSha256(string text)
        {
            if (text == null || text.Length != 64) return false;
            foreach (char c in text)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        /// <summary>The file holds whole seconds.</summary>
        private static DateTime TrimToSeconds(DateTime utc) =>
            new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }
}
