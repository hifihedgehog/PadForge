using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Resources.Strings;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #505: the languages the interface is translated into are one table,
    /// Resources/Strings/Languages.tsv, read by <see cref="LanguageCatalog"/>.
    /// Before it, the list was spelled out in the settings view model, in
    /// seven test files and in two measuring tools, and a language added in
    /// one of them was missing from the rest.
    ///
    /// <para>These tests hold the table to the files beside it and to what
    /// the build made of them, so a row with no translation, a translation
    /// with no row, and a translation the build left out all fail here.</para>
    /// </summary>
    public class LanguageCatalogTests
    {
        /// <summary>How many languages the tree holds, English included.
        /// Hard-coded on purpose: a language dropped from the table and from
        /// the tree together would shrink every other test's coverage in
        /// silence, and this number would not move with it.</summary>
        internal const int LanguageCount = 112;

        private static string StringsDirectory()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return Path.Combine(d.FullName, "PadForge.App", "Resources", "Strings");
        }

        private static SortedSet<string> KeysOf(string fileName)
        {
            string xml = File.ReadAllText(Path.Combine(StringsDirectory(), fileName));
            return new SortedSet<string>(
                Regex.Matches(xml, @"<data name=""([^""]+)""").Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        }

        /// <summary>Every language but American English, for the tests that read a translation.</summary>
        public static IEnumerable<object[]> Translations()
            => LanguageCatalog.All.Where(l => l != LanguageCatalog.English).Select(l => new object[] { l.Tag });

        private static Dictionary<string, string> Values(string fileName)
            => XDocument.Load(Path.Combine(StringsDirectory(), fileName)).Root.Elements("data")
                .Where(d => d.Attribute("type") == null)
                .ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value") ?? "");

        [Fact]
        public void TheTableHasARowForEnglishAndOneForEachTranslationInTheTree()
        {
            var files = Directory.EnumerateFiles(StringsDirectory(), "Strings.*.resx")
                .Select(p => Path.GetFileNameWithoutExtension(p).Substring("Strings.".Length))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = LanguageCatalog.All.Select(l => l.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Contains(LanguageCatalog.EnglishTag, rows);
            Assert.True(File.Exists(Path.Combine(StringsDirectory(), "Strings.resx")));
            rows.Remove(LanguageCatalog.EnglishTag);
            Assert.Empty(rows.Except(files).OrderBy(t => t));   // a row with no translation
            Assert.Empty(files.Except(rows).OrderBy(t => t));   // a translation with no row
            Assert.Equal(LanguageCount, LanguageCatalog.All.Count);
        }

        [Fact]
        public void EveryRowIsWhole_AndNoTwoShareATagOrAName()
        {
            foreach (LanguageInfo language in LanguageCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(language.Tag));
                Assert.False(string.IsNullOrWhiteSpace(language.EnglishName), language.Tag);
                Assert.False(string.IsNullOrWhiteSpace(language.NativeName), language.Tag);
                Assert.Equal(language.NativeName, language.NativeName.Trim());
                Assert.Equal(language.Tag, language.Culture.Name, ignoreCase: true);
                Assert.Same(language, LanguageCatalog.Find(language.Tag));
                Assert.Same(language, LanguageCatalog.Find(language.Tag.ToUpperInvariant()));
            }
            Assert.Equal(LanguageCatalog.All.Count, LanguageCatalog.All.Select(l => l.Tag.ToLowerInvariant()).Distinct().Count());
            Assert.Equal(LanguageCatalog.All.Count, LanguageCatalog.All.Select(l => l.NativeName).Distinct().Count());
            Assert.Equal(LanguageCatalog.All.Count, LanguageCatalog.All.Select(l => l.EnglishName).Distinct().Count());
            Assert.Null(LanguageCatalog.Find(null));
            Assert.Null(LanguageCatalog.Find(""));
            Assert.Null(LanguageCatalog.Find("zz"));
            Assert.Equal("en-US", LanguageCatalog.English.Tag);
            Assert.Equal("English (United States)", LanguageCatalog.English.NativeName);
            Assert.Equal("", LanguageCatalog.English.EnglishNameIfDifferent);
            // A language saved as "en", before American and British English
            // had rows of their own, reads American English.
            Assert.Same(LanguageCatalog.English, LanguageCatalog.Find("en"));
            Assert.Same(LanguageCatalog.English, LanguageCatalog.Find("EN"));
        }

        [Fact]
        public void ThePickerListsThemByTheirOwnName()
        {
            CompareInfo invariant = CultureInfo.InvariantCulture.CompareInfo;
            var names = LanguageCatalog.All.Select(l => l.NativeName).ToList();
            foreach (var (before, after) in names.Zip(names.Skip(1)))
            {
                Assert.True(invariant.Compare(before, after, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) <= 0,
                    before + " is listed ahead of " + after);
            }
        }

        /// <summary>The table says which translations have every string.
        /// Counted here from the files, so the column cannot claim a
        /// translation is whole when it is not, or the other way round.
        /// Another variety of English holds only the strings it writes
        /// differently and is whole through Strings.resx, so each of its
        /// strings differs from the American one: a copy would keep the
        /// American text after the American text changed. Galactic Basic
        /// (#590) is American English drawn in the Aurebesh alphabet, so its
        /// file holds no string at all.</summary>
        [Theory]
        [MemberData(nameof(Translations))]
        public void TheCoverageColumnIsWhatTheFileHolds(string tag)
        {
            SortedSet<string> baseKeys = KeysOf("Strings.resx"), keys = KeysOf("Strings." + tag + ".resx");
            Assert.True(baseKeys.Count > 1000);
            LanguageInfo language = LanguageCatalog.Find(tag);
            if (language.IsAurebesh)
            {
                Assert.True(language.IsComplete, tag);
                Assert.True(LocaleFiles.IsOtherEnglish(language), tag);
                Assert.Empty(keys);
                return;
            }
            Assert.NotEmpty(keys);
            Assert.Empty(keys.Except(baseKeys));
            if (LocaleFiles.IsOtherEnglish(language))
            {
                Assert.True(language.IsComplete, tag);
                Dictionary<string, string> american = Values("Strings.resx");
                foreach (var (key, value) in Values("Strings." + tag + ".resx"))
                    Assert.True(value != american[key], tag + " repeats the American text of " + key);
                return;
            }
            Assert.Equal(baseKeys.IsSubsetOf(keys), language.IsComplete);
        }

        /// <summary>Every translation reaches the build as a satellite
        /// resource. MSBuild leaves a tag it does not know in the main
        /// assembly, where no culture finds it, unless the project names
        /// the culture itself (PadForge.App.csproj).</summary>
        [Theory]
        [MemberData(nameof(Translations))]
        public void TheBuildCarriesTheTranslation(string tag)
        {
            var set = Strings.ResourceManager.GetResourceSet(
                CultureInfo.GetCultureInfo(tag), createIfNotExists: true, tryParents: false);
            Assert.True(set != null, tag + " did not build into a satellite resource");
            string saved = Strings.ResourceManager.GetString("Common_Save", CultureInfo.GetCultureInfo(tag));
            Assert.False(string.IsNullOrEmpty(saved));
        }

        [Fact]
        public void NoTranslationWasLeftInTheMainAssembly()
        {
            string[] stray = typeof(Strings).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("PadForge.Resources.Strings.Strings.", StringComparison.Ordinal)
                            && n != "PadForge.Resources.Strings.Strings.resources")
                .ToArray();
            Assert.Empty(stray);
            Assert.Contains(LanguageCatalog.ResourceName, typeof(Strings).Assembly.GetManifestResourceNames());
        }

        [Theory]
        [InlineData("en", "en-US")]
        [InlineData("en-US", "en-US")]
        [InlineData("en-CA", "en-US")]
        [InlineData("en-PH", "en-US")]
        [InlineData("en-PR", "en-US")]
        [InlineData("en-US-POSIX", "en-US")]
        [InlineData("en-GB", "en-GB")]
        [InlineData("en-AU", "en-GB")]
        [InlineData("en-NZ", "en-GB")]
        [InlineData("en-IE", "en-GB")]
        [InlineData("en-IN", "en-GB")]
        [InlineData("en-ZA", "en-GB")]
        [InlineData("en-SG", "en-GB")]
        [InlineData("en-DE", "en-GB")]
        [InlineData("en-150", "en-GB")]
        [InlineData("en-001", "en-GB")]
        [InlineData("de", "de")]
        [InlineData("de-AT", "de")]
        [InlineData("fr-CA", "fr")]
        [InlineData("es-MX", "es")]
        [InlineData("it-CH", "it")]
        [InlineData("nl-BE", "nl")]
        [InlineData("ja-JP", "ja")]
        [InlineData("ko-KR", "ko")]
        [InlineData("pt-BR", "pt-BR")]
        [InlineData("zh-Hans", "zh-Hans")]
        [InlineData("zh-CN", "zh-Hans")]
        [InlineData("zh-SG", "zh-Hans")]
        public void ACultureReadsItsOwnRowOrItsNearestParents(string culture, string row)
            => Assert.Same(LanguageCatalog.Find(row), LanguageCatalog.Resolve(CultureInfo.GetCultureInfo(culture)));

        [Fact]
        public void ACultureWithNoRowReadsNothing_WhichIsEnglish()
        {
            Assert.Null(LanguageCatalog.Resolve(null));
            Assert.Null(LanguageCatalog.Resolve(CultureInfo.InvariantCulture));
            Assert.Null(LanguageCatalog.Resolve(CultureInfo.GetCultureInfo("he-IL")));
            Assert.Null(LanguageCatalog.Resolve(CultureInfo.GetCultureInfo("eo")));
        }

        /// <summary>The interface looks its strings up in the culture of the
        /// row the Windows language resolves to, and a language with no row
        /// keeps its own culture, whose strings are American English.</summary>
        [Theory]
        [InlineData("en-AU", "en-GB")]
        [InlineData("en-IN", "en-GB")]
        [InlineData("nb-NO", "no")]
        [InlineData("nn-NO", "no")]
        [InlineData("pt-AO", "pt-PT")]
        [InlineData("tl-PH", "fil")]
        [InlineData("de-AT", "de")]
        [InlineData("zh-TW", "zh-Hant")]
        [InlineData("en-US", "en-US")]
        [InlineData("en-CA", "en-US")]
        [InlineData("he-IL", "he-IL")]
        public void TheWindowsLanguageLooksItsStringsUpInItsRowsCulture(string windows, string expected)
            => Assert.Equal(expected, LanguageCatalog.InterfaceCulture(CultureInfo.GetCultureInfo(windows)).Name);

        /// <summary>ResourceManager walks CultureInfo.Parent, which takes
        /// nb-NO to nb and en-AU to en, never to the row the catalog finds
        /// for them: under its own culture each read English, Australian
        /// English the American spelling, while the picker named the row. The
        /// first assert shows the parent chain missing the row, the second the
        /// interface culture reaching it.</summary>
        [Theory]
        [InlineData("en-AU", "en-GB")]
        [InlineData("nb-NO", "no")]
        [InlineData("pt-AO", "pt-PT")]
        [InlineData("tl-PH", "fil")]
        public void ARowReachedOnlyThroughTheCatalogShowsItsOwnText(string windows, string row)
        {
            CultureInfo own = CultureInfo.GetCultureInfo(windows);
            CultureInfo rowCulture = LanguageCatalog.Find(row).Culture;
            string key = AKeyTheRowWritesItsOwnWay(rowCulture);
            string rowText = Strings.ResourceManager.GetString(key, rowCulture);
            Assert.NotEqual(rowText, Strings.ResourceManager.GetString(key, own));
            Assert.Equal(rowText, Strings.ResourceManager.GetString(key, LanguageCatalog.InterfaceCulture(own)));
        }

        /// <summary>A key whose text in the row's own file differs from the American English.</summary>
        private static string AKeyTheRowWritesItsOwnWay(CultureInfo row)
        {
            var set = Strings.ResourceManager.GetResourceSet(row, createIfNotExists: true, tryParents: false);
            Assert.NotNull(set);
            foreach (System.Collections.DictionaryEntry entry in set)
                if (entry.Value is string text
                    && text != Strings.ResourceManager.GetString((string)entry.Key, CultureInfo.InvariantCulture))
                    return (string)entry.Key;
            throw new Xunit.Sdk.XunitException(row.Name + " writes nothing its own way");
        }

        /// <summary>Startup reads the Windows language through the catalog
        /// before the update helper's windows and the window language start,
        /// and Reset to Defaults reads it the same way.</summary>
        [Fact]
        public void StartupAndResetReadTheWindowsLanguageThroughTheCatalog()
        {
            string app = File.ReadAllText(Path.Combine(StringsDirectory(), "..", "..", "App.xaml.cs"));
            int mapped = app.IndexOf("LanguageCatalog.InterfaceCulture(", StringComparison.Ordinal);
            Assert.True(mapped > 0, "App.OnStartup does not read the Windows language through the catalog");
            Assert.True(mapped < app.IndexOf("UiLanguage.Start();", StringComparison.Ordinal));
            Assert.True(mapped < app.IndexOf("UpdateService.TryRunApplyMode(", StringComparison.Ordinal));
            string settings = File.ReadAllText(Path.Combine(StringsDirectory(), "..", "..", "ViewModels", "SettingsViewModel.cs"));
            Assert.Contains("Strings.ChangeCulture(LanguageCatalog.InterfaceCulture(App.StartupUICulture", settings);
        }

        /// <summary>English outside the United States follows British
        /// conventions wherever CLDR gives the region en-001 as a parent,
        /// directly or through en-150. The catalog keeps the list of those
        /// regions because .NET's CultureInfo.Parent skips that step and
        /// hands every English region straight to en. Here the list is held
        /// to the CLDR data in the ICU that Windows ships, read through
        /// icu.dll, for every English culture .NET knows.</summary>
        [Fact]
        public void EnglishRegionsReadBritishEnglishWhereCldrSaysTheyDo()
        {
            var english = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                .Where(c => c.Name.StartsWith("en-", StringComparison.Ordinal)).ToList();
            Assert.True(english.Count > 90, "only " + english.Count + " English cultures");
            int british = 0;
            foreach (CultureInfo culture in english)
            {
                // en-001 heads the family, so its own chain is empty.
                bool cldr = culture.Name == "en-001" || CldrParents(culture.Name.Replace('-', '_')).Contains("en_001");
                Assert.True(cldr == LanguageCatalog.ReadsBritishEnglish(culture),
                    culture.Name + (cldr ? " has the CLDR parent en-001" : " has no CLDR parent en-001"));
                if (cldr) british++;
            }
            Assert.True(british > 80, british + " British English regions");
            Assert.True(LanguageCatalog.ReadsBritishEnglish(CultureInfo.GetCultureInfo("en-001")));
            Assert.True(LanguageCatalog.ReadsBritishEnglish(CultureInfo.GetCultureInfo("en-GB")));
            Assert.True(LanguageCatalog.ReadsBritishEnglish(CultureInfo.GetCultureInfo("en-AU")));
            Assert.False(LanguageCatalog.ReadsBritishEnglish(CultureInfo.GetCultureInfo("en-US")));
            Assert.False(LanguageCatalog.ReadsBritishEnglish(CultureInfo.GetCultureInfo("en")));
            Assert.False(LanguageCatalog.ReadsBritishEnglish(CultureInfo.GetCultureInfo("fr-GB")));
            Assert.False(LanguageCatalog.ReadsBritishEnglish(null));
        }

        /// <summary>The chain of CLDR parents ICU records for a locale in
        /// its %%Parent entries, which uloc_getParent does not follow.</summary>
        private static List<string> CldrParents(string locale)
        {
            var chain = new List<string>();
            string at = locale;
            while (chain.Count < 8)
            {
                int status = 0;
                IntPtr bundle = ures_open(null, at, ref status);
                Assert.True(bundle != IntPtr.Zero && status <= 0, "ICU could not open " + at);
                try
                {
                    int found = 0;
                    IntPtr parent = ures_getStringByKey(bundle, "%%Parent", out int length, ref found);
                    if (parent == IntPtr.Zero || found > 0) return chain;
                    at = Marshal.PtrToStringUni(parent, length);
                    chain.Add(at);
                }
                finally { ures_close(bundle); }
            }
            return chain;
        }

        [DllImport("icu.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr ures_open(string packageName, string locale, ref int status);

        [DllImport("icu.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr ures_getStringByKey(IntPtr bundle, string key, out int length, ref int status);

        [DllImport("icu.dll")]
        private static extern void ures_close(IntPtr bundle);

        /// <summary>Direction comes from the table, so a right-to-left
        /// Windows with no translation of its own shows English left to
        /// right. Where .NET knows a language's direction, the table agrees.</summary>
        [Fact]
        public void DirectionComesFromTheTable()
        {
            foreach (LanguageInfo language in LanguageCatalog.All)
            {
                Assert.Equal(language.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                    UiLanguage.Direction(language.Culture));
                Assert.Equal(language.IsRightToLeft, LanguageCatalog.IsRightToLeft(language.Culture));
                if (language.Culture.TextInfo.IsRightToLeft) Assert.True(language.IsRightToLeft, language.Tag);
            }
            Assert.Equal(FlowDirection.LeftToRight, UiLanguage.Direction(CultureInfo.GetCultureInfo("he-IL")));
            Assert.Equal(FlowDirection.LeftToRight, UiLanguage.Direction(null));
            Assert.Equal(FlowDirection.LeftToRight, UiLanguage.Direction(CultureInfo.InvariantCulture));
        }
    }
}
