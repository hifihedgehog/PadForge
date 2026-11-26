using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PadForge.Search
{
    /// <summary>
    /// A stable ID for every entry of Search's index (#600), the one an
    /// assistant reads and sets a setting by. It is made from what the markup
    /// says and never from the words shown, so it is the same in every
    /// interface language: the page, the extra's key, the scope's name, the
    /// list a card per item belongs to, the label's string key or binding
    /// path, and the qualifier's. Entries that agree on all of those are told
    /// apart by their order on the page.
    ///
    /// <para>What a setting is shown for at run time follows after an
    /// <c>@</c>: the controller's number on the controller page, the item's
    /// position in its list for a card per item, and the device's short ID on
    /// the Devices page.</para>
    /// </summary>
    internal static class SearchIds
    {
        /// <summary>Gives each entry of an index its ID. The IDs of one
        /// index are distinct.</summary>
        internal static Dictionary<SearchEntry, string> Assign(IReadOnlyList<SearchEntry> index)
        {
            var ids = new Dictionary<SearchEntry, string>(ReferenceEqualityComparer.Instance);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in index)
            {
                string id = BaseOf(entry);
                seen.TryGetValue(id, out int count);
                seen[id] = count + 1;
                ids[entry] = count == 0 ? id : id + "~" + (count + 1).ToString(CultureInfo.InvariantCulture);
            }
            return ids;
        }

        internal static string PageWord(SearchPage page) => page switch
        {
            SearchPage.Pad => "pad",
            SearchPage.Settings => "settings",
            SearchPage.Extras => "extras",
            SearchPage.Devices => "devices",
            SearchPage.Dialog => "dialog",
            SearchPage.Window => "window",
            SearchPage.Home => "home",
            SearchPage.About => "about",
            _ => "profiles",
        };

        private static string BaseOf(SearchEntry entry)
        {
            var sb = new StringBuilder(PageWord(entry.Page));
            if (entry.Kind != SearchKind.Setting) sb.Append('.').Append(entry.Kind.ToString().ToLowerInvariant());
            sb.Append('/');
            if (!string.IsNullOrEmpty(entry.Dialog)) sb.Append(entry.Dialog).Append('/');
            if (!string.IsNullOrEmpty(entry.ExtraKey)) sb.Append(entry.ExtraKey).Append('/');
            if (!string.IsNullOrEmpty(entry.ScopeName)) sb.Append(entry.ScopeName).Append('/');
            if (!string.IsNullOrEmpty(entry.ItemsPath)) sb.Append('[').Append(entry.ItemsPath).Append("]/");
            sb.Append(Word(entry.Label));
            if (entry.Qualifier != null) sb.Append('#').Append(Word(entry.Qualifier));
            if (entry.RowOptions) sb.Append("#row");
            return sb.ToString();
        }

        private static string Word(SearchLabel label)
            => label == null ? "-" : label.Key ?? ("=" + label.Path);

        /// <summary>An entry's ID with what it is shown for: a controller's
        /// number, an item's position, a device's short ID. Null parts are
        /// left out.</summary>
        internal static string With(string id, int? controller, int? item, string device)
        {
            if (controller == null && item == null && device == null) return id;
            var sb = new StringBuilder(id).Append('@');
            if (controller != null) sb.Append('c').Append(controller.Value.ToString(CultureInfo.InvariantCulture));
            if (device != null) sb.Append('d').Append(device);
            if (item != null) sb.Append('i').Append(item.Value.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>Takes an ID apart into its entry's part and what it is
        /// shown for. False when the second part does not read.</summary>
        internal static bool TryRead(string full, out string id, out int? controller, out int? item, out string device)
        {
            id = full;
            controller = null;
            item = null;
            device = null;
            if (string.IsNullOrEmpty(full)) return false;
            int at = full.LastIndexOf('@');
            if (at < 0) return true;
            id = full.Substring(0, at);
            string rest = full.Substring(at + 1);
            int i = 0;
            while (i < rest.Length)
            {
                char kind = rest[i++];
                int start = i;
                if (kind == 'd')
                {
                    // A device's short ID is eight letters and digits.
                    while (i < rest.Length && char.IsLetterOrDigit(rest[i]) && i - start < 8) i++;
                    if (i == start) return false;
                    device = rest.Substring(start, i - start);
                    continue;
                }
                while (i < rest.Length && char.IsDigit(rest[i])) i++;
                if (i == start || !int.TryParse(rest.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                    return false;
                if (kind == 'c') controller = n;
                else if (kind == 'i') item = n;
                else return false;
            }
            return true;
        }
    }
}
