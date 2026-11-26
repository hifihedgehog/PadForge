using System;
using System.Collections.Generic;
using System.Globalization;
using PadForge.Resources.Strings;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The Controller Database row's status line (#597): what PadForge reads
    /// out of the box, and what the last Update Now added. Every number is
    /// counted from the baseline, PadForge's own rows and the section, so
    /// the line cannot say more than the files hold.
    /// </summary>
    internal static class ControllerDatabaseStatus
    {
        private const string DateFormat = "yyyy-MM-dd";

        /// <summary>
        /// The mappings PadForge reads with nothing downloaded: the rows of
        /// SDL's own Windows section, the community rows the SDL fork
        /// carries, and each row of PadForge's own that adds a mapping. A
        /// row of PadForge's for an ID the baseline lists as carried
        /// replaces that mapping and adds none.
        /// </summary>
        internal static int BuiltInCount(ControllerDatabaseBaseline baseline, IEnumerable<string> ownRows)
        {
            int count = baseline.SdlWindowsRows + baseline.KeptRows;
            foreach (string row in ownRows)
            {
                int comma = row.IndexOf(',');
                string id = comma < 0 ? null : row.Substring(0, comma).ToLowerInvariant();
                bool carried = baseline.TryFind(id, out var entry)
                    && entry.Verdict is ControllerDatabaseVerdict.Kept or ControllerDatabaseVerdict.SdlWindows;
                if (!carried) count++;
            }
            return count;
        }

        /// <summary>The status line, in the language in use when it is
        /// read. The pinned copy's date is its commit's day in UTC, and the
        /// update's day is the user's own.</summary>
        internal static string Facts(ControllerDatabaseBaseline baseline, IEnumerable<string> ownRows,
            ControllerDatabaseSection section, TimeZoneInfo zone)
        {
            if (baseline == null) return string.Empty;
            var culture = CultureInfo.CurrentCulture;
            int builtIn = BuiltInCount(baseline, ownRows);
            string pinned = baseline.PinTimeUtc.ToString(DateFormat, CultureInfo.InvariantCulture);
            if (section == null)
                return string.Format(culture, Strings.Instance.ControllerDatabase_BuiltIn_Format, builtIn, pinned);
            string fetched = TimeZoneInfo.ConvertTimeFromUtc(section.FetchedUtc, zone ?? TimeZoneInfo.Local)
                .ToString(DateFormat, CultureInfo.InvariantCulture);
            // Rows from an address the user set (#600) are new rows only,
            // and the line names where they came from.
            if (section.Source != null && !ControllerDatabaseAddress.IsCommunity(section.Source))
                return string.Format(culture, Strings.CountFormat(nameof(Strings.ControllerDatabase_BuiltInUpdatedFrom_Format), section.NewRows),
                    section.NewRows, builtIn, pinned, fetched, ControllerDatabaseAddress.HostOf(section.Source));
            return string.Format(culture, Strings.Instance.ControllerDatabase_BuiltInUpdated_Format,
                builtIn, pinned, fetched, section.NewRows, section.ChangedRows);
        }
    }
}
