using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PadForge.Common
{
    /// <summary>One language the interface is translated into.</summary>
    public sealed class LanguageInfo
    {
        internal LanguageInfo(string tag, string englishName, string nativeName, bool rightToLeft, bool complete,
            string script)
        {
            Tag = tag;
            EnglishName = englishName;
            NativeName = nativeName;
            IsRightToLeft = rightToLeft;
            IsComplete = complete;
            Script = script;
            Culture = CultureInfo.GetCultureInfo(tag);
        }

        /// <summary>The culture name, and the tag in the file name
        /// Strings.&lt;tag&gt;.resx. American English is "en-US", and its
        /// text is Strings.resx itself.</summary>
        public string Tag { get; }

        public string EnglishName { get; }

        /// <summary>The language's name in itself, as the picker lists it.</summary>
        public string NativeName { get; }

        public bool IsRightToLeft { get; }

        /// <summary>The script the table names for the language: an ISO 15924
        /// code, such as Latn or Deva, or Qaaa, the first code ISO 15924 leaves
        /// to private use, for the Aurebesh alphabet.</summary>
        public string Script { get; }

        /// <summary>True for Galactic Basic (#590): English written in the
        /// Aurebesh alphabet, which the interface draws in the Aurebesh
        /// font.</summary>
        public bool IsAurebesh => Script == LanguageCatalog.AurebeshScript;

        /// <summary>True when every string is translated. A language that
        /// is not complete shows English wherever its translation has no
        /// string: a gap is left where no correct wording was known, and
        /// nothing is made up to fill it.</summary>
        public bool IsComplete { get; }

        public CultureInfo Culture { get; }

        /// <summary>The English name as the picker shows it beside the
        /// language's own, and nothing where the two are the same.</summary>
        public string EnglishNameIfDifferent
            => string.Equals(EnglishName, NativeName, StringComparison.OrdinalIgnoreCase) ? "" : EnglishName;

        public override string ToString() => NativeName;
    }

    /// <summary>
    /// The languages the interface is translated into (#505). There is one
    /// row for each in Resources/Strings/Languages.tsv, beside the
    /// Strings.&lt;tag&gt;.resx it names, and that table is the only list:
    /// the language picker, the tests and the measuring tools all read it.
    ///
    /// <para>Windows and .NET know about half of these tags. The names come
    /// from the table so that every language is listed under its own name,
    /// whatever the PC knows about it.</para>
    /// </summary>
    internal static class LanguageCatalog
    {
        internal const string ResourceName = "PadForge.Languages.tsv";

        private static readonly Lazy<IReadOnlyList<LanguageInfo>> _all = new(Load);

        /// <summary>Every language, in the order the picker lists them: by
        /// own name, Latin first and then script by script.</summary>
        internal static IReadOnlyList<LanguageInfo> All => _all.Value;

        /// <summary>The tag of the interface's own language: American
        /// English, the text of Strings.resx.</summary>
        internal const string EnglishTag = "en-US";

        /// <summary>The script code of the Aurebesh alphabet in the table, Qaaa:
        /// ISO 15924 has no code for Aurebesh and leaves Qaaa to Qabx to private
        /// use.</summary>
        internal const string AurebeshScript = "Qaaa";

        internal static LanguageInfo English => Find(EnglishTag);

        /// <summary>The row for a tag, else the row its speakers read. A
        /// language saved as "en", before American and British English had
        /// rows of their own, reads American English.</summary>
        internal static LanguageInfo Find(string tag)
            => Exact(tag) ?? Exact(Alias(tag));

        private static LanguageInfo Exact(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            foreach (LanguageInfo language in All)
                if (string.Equals(language.Tag, tag, StringComparison.OrdinalIgnoreCase)) return language;
            return null;
        }

        /// <summary>The language a culture is shown in: its own row, else
        /// the nearest parent that has one (de-AT reads de, zh-TW reads
        /// zh-Hant, en-AU reads en-GB). Null when no row answers, which is
        /// English.</summary>
        internal static LanguageInfo Resolve(CultureInfo culture)
        {
            // The walk ends at the invariant culture, whose name is empty.
            CultureInfo c = culture;
            while (c != null && c.Name.Length > 0)
            {
                LanguageInfo hit = Find(c.Name);
                if (hit != null) return hit;
                c = c.Parent;
            }
            return null;
        }

        /// <summary>The culture the interface looks its strings up in while
        /// it follows <paramref name="culture"/>, the Windows display
        /// language: the culture of the row it resolves to, else the culture
        /// itself, whose strings are American English. ResourceManager walks
        /// CultureInfo.Parent, which never reaches a row the catalog finds
        /// through an alias or a CLDR parent: under its own culture, Windows
        /// set to nb-NO read English rather than Norwegian, and en-AU
        /// American English while the picker said British English.</summary>
        internal static CultureInfo InterfaceCulture(CultureInfo culture)
            => Resolve(culture)?.Culture ?? culture;

        /// <summary>The regions whose English follows British conventions:
        /// those CLDR gives the parent en-001, directly or through en-150,
        /// read from the CLDR data in Windows' own ICU. .NET's
        /// CultureInfo.Parent skips that step and hands every English region
        /// straight to en, so the list is kept here, and LanguageCatalogTests
        /// holds it to Windows' copy of CLDR.</summary>
        private static readonly HashSet<string> BritishEnglishRegions = new(StringComparer.OrdinalIgnoreCase)
        {
            "001", "150", "AG", "AI", "AT", "AU", "BB", "BE", "BM", "BS", "BW", "BZ", "CC", "CH", "CK", "CM", "CX",
            "CY", "DE", "DK", "DM", "ER", "FI", "FJ", "FK", "FM", "GB", "GD", "GG", "GH", "GI", "GM", "GY", "HK",
            "IE", "IL", "IM", "IN", "IO", "JE", "JM", "KE", "KI", "KN", "KY", "LC", "LR", "LS", "MG", "MO", "MS",
            "MT", "MU", "MV", "MW", "MY", "NA", "NF", "NG", "NL", "NR", "NU", "NZ", "PG", "PK", "PN", "PW", "RW",
            "SB", "SC", "SD", "SE", "SG", "SH", "SI", "SL", "SS", "SX", "SZ", "TC", "TK", "TO", "TT", "TV", "TZ",
            "UG", "VC", "VG", "VU", "WS", "ZA", "ZM", "ZW",
        };

        /// <summary>True for English of a region that writes British English.
        /// English of the United States and its territories, and of a region
        /// CLDR leaves under en itself (Canada, the Philippines), writes
        /// American English.</summary>
        internal static bool ReadsBritishEnglish(CultureInfo culture)
            => culture != null && EnglishRegion(culture.Name, out string region) && region != null
               && BritishEnglishRegions.Contains(region);

        /// <summary>True when the name is English, with its region subtag
        /// (two letters or three digits) if it has one.</summary>
        private static bool EnglishRegion(string name, out string region)
        {
            region = null;
            if (string.IsNullOrEmpty(name)) return false;
            string[] parts = name.Split('-');
            if (!string.Equals(parts[0], "en", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string part in parts.Skip(1))
            {
                if (part.Length == 4) continue; // a script subtag such as Latn
                if ((part.Length == 2 && part.All(char.IsLetter)) || (part.Length == 3 && part.All(char.IsDigit)))
                    region = part;
                break;
            }
            return true;
        }

        /// <summary>Where a culture's own parents never reach the row its
        /// speakers read. English reads American or British English by its
        /// region, Portuguese outside Brazil reads Portugal's, bare Chinese
        /// reads Simplified, Windows calls Tagalog fil and Central Kurdish
        /// ku-Arab.</summary>
        private static string Alias(string name)
        {
            if (EnglishRegion(name, out string region))
                return region != null && BritishEnglishRegions.Contains(region) ? "en-GB" : EnglishTag;
            switch (name)
            {
                case "pt": return "pt-PT";
                case "zh": return "zh-Hans";
                case "nb":
                case "nn": return "no";
                case "tl": return "fil";
                case "ku-Arab": return "ckb";
                default: return null;
            }
        }

        /// <summary>True when the interface reads right to left under this
        /// culture. Only a language in the table can say so: Hebrew Windows
        /// with no Hebrew translation shows English, left to right.</summary>
        internal static bool IsRightToLeft(CultureInfo culture) => Resolve(culture)?.IsRightToLeft ?? false;

        private static IReadOnlyList<LanguageInfo> Load()
        {
            var rows = new List<LanguageInfo>();
            using (Stream stream = typeof(LanguageCatalog).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("The language table " + ResourceName + " is not in the assembly.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] cells = line.Split('\t');
                    rows.Add(new LanguageInfo(cells[0], cells[1], cells[2], cells[3] == "rtl", cells[5] == "full", cells[4]));
                }
            }
            CompareInfo invariant = CultureInfo.InvariantCulture.CompareInfo;
            rows.Sort((a, b) => invariant.Compare(a.NativeName, b.NativeName,
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace));
            return rows;
        }
    }
}
