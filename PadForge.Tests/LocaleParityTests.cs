using System.Collections;
using System.Text.RegularExpressions;
using System.IO;
using System.Globalization;
using System.Resources;
using PadForge.Resources.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// Every shipped locale must define every key the base resx defines.
    /// A missing key is invisible at runtime: ResourceManager silently falls
    /// back to the invariant culture, so the control renders English on a
    /// localized build and nothing logs. That is exactly how three keys
    /// (Pad_Touchpad_RecordTargetLabel, Workshop_Tr_MouseModeTuningDropped,
    /// Workshop_Tr_AxisInversionNotApplied) shipped English-only across all
    /// nine locales until an audit diffed the key sets by hand.
    ///
    /// <para>Runs against the compiled satellite assemblies rather than the
    /// .resx source, so it also catches a resx that fails to compile into a
    /// satellite at all, and needs no repo-relative path math.</para>
    /// </summary>
    public class LocaleParityTests
    {
        /// <summary>The locales shipped alongside the base (invariant) resx.
        /// Hard-coded rather than discovered: a locale silently dropped from
        /// the build should fail this test, not shrink its own coverage.</summary>
        public static readonly string[] Locales =
            { "de", "es", "fr", "it", "ja", "ko", "nl", "pt-BR", "zh-Hans" };

        public static IEnumerable<object[]> LocaleCases() =>
            Locales.Select(l => new object[] { l });

        private static SortedSet<string> KeysFor(CultureInfo culture)
        {
            // tryParents must stay false. With it, a satellite missing a key
            // inherits the base resx's copy and every locale passes.
            var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
            Assert.True(set != null, $"No resource set for '{culture.Name}'. The satellite assembly did not build or deploy.");

            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (DictionaryEntry e in set)
                keys.Add((string)e.Key);
            return keys;
        }

        [Fact]
        public void BaseResx_HasKeys()
        {
            // Guards the two tests below from passing vacuously if the base
            // resource set ever came back empty.
            Assert.True(KeysFor(CultureInfo.InvariantCulture).Count > 1000);
        }

        [Theory]
        [MemberData(nameof(LocaleCases))]
        public void Locale_DefinesEveryBaseKey(string locale)
        {
            var baseKeys = KeysFor(CultureInfo.InvariantCulture);
            var locKeys = KeysFor(new CultureInfo(locale));

            var missing = baseKeys.Except(locKeys).ToArray();
            Assert.True(missing.Length == 0,
                $"{locale} is missing {missing.Length} key(s) the base resx defines: {string.Join(", ", missing)}");
        }

        [Theory]
        [MemberData(nameof(LocaleCases))]
        public void TheLockedHintNamesTheDefaultProfileAsTheListShowsIt(string locale)
        {
            // The Profiles list shows the built-in profile under
            // Profile_Default, so a hint naming it in English named a row the
            // list does not show in any other language.
            var culture = new CultureInfo(locale);
            string name = Strings.ResourceManager.GetString("Profile_Default", culture);
            string hint = Strings.ResourceManager.GetString("Profiles_DefaultLockedHint", culture);
            Assert.False(string.IsNullOrEmpty(name));
            Assert.Contains(name, hint);
        }

        [Theory]
        [MemberData(nameof(LocaleCases))]
        public void Locale_DefinesNoKeyOutsideBase(string locale)
        {
            var baseKeys = KeysFor(CultureInfo.InvariantCulture);
            var locKeys = KeysFor(new CultureInfo(locale));

            // An extra key is dead weight at best and a renamed-key typo at
            // worst (the base rename lands, the locale keeps the old spelling,
            // and the locale silently serves English for the new name).
            var extra = locKeys.Except(baseKeys).ToArray();
            Assert.True(extra.Length == 0,
                $"{locale} defines {extra.Length} key(s) the base resx does not: {string.Join(", ", extra)}");
        }
        /// <summary>
        /// A .resx value is XML text, not a C# string literal, so a backslash
        /// is a backslash. A value written with \n renders as those two
        /// characters, and the crash dialog read "An unexpected error
        /// occurred:\n\nIndexOutOfRangeException...".
        ///
        /// <para>Not hypothetical. App_UnexpectedError_Format carried them in
        /// all ten locales and the 4.5.0 capture run photographed the dialog
        /// into four shipped screenshots. Real newlines survive the build
        /// because every value element carries xml:space="preserve".</para>
        ///
        /// <para>Reads the .resx SOURCE rather than the compiled resources.
        /// An incremental build does not reliably recompile a changed resx on
        /// this tree (OneDrive mtime lag defeats MSBuild's up-to-date check),
        /// so a satellite-based check can pass against a stale assembly and
        /// prove nothing. The source is what a regression guard should pin.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(AllResxFiles))]
        public void NoStringRendersALiteralEscapeSequence(string fileName)
        {
            string path = Path.Combine(ResxDirectory(), fileName);
            Assert.True(File.Exists(path), "missing resx: " + path);
            string xml = File.ReadAllText(path);

            var offenders = LiteralEscapeOffenders(xml);

            Assert.True(offenders.Count == 0,
                fileName + " has " + offenders.Count
                + " value(s) carrying a literal escape sequence, which renders"
                + " verbatim instead of breaking the line: "
                + string.Join(", ", offenders));
        }


        /// <summary>Keys whose value carries a literal escape sequence.
        /// Separate from the file walk so it can be driven with input the
        /// test controls, which is the only way to show the check can
        /// fail.</summary>
        internal static List<string> LiteralEscapeOffenders(string xml)
        {
            var offenders = new List<string>();
            foreach (Match m in Regex.Matches(
                xml, @"<data name=""([^""]+)""[^>]*>\s*<value>(.*?)</value>",
                RegexOptions.Singleline))
            {
                string v = m.Groups[2].Value;
                // A \uXXXX escape is the same defect in the form a JSON
                // round trip or a translation tool leaves behind. A \xNN
                // form is left out on purpose: a value holding a Windows
                // path such as Resources\x64\SDL3.dll would match it.
                if (v.Contains(@"\n") || v.Contains(@"\r") || v.Contains(@"\t")
                    || Regex.IsMatch(v, @"\\u[0-9A-Fa-f]{4}"))
                    offenders.Add(m.Groups[1].Value);
            }
            return offenders;
        }

        /// <summary>The check above reports zero on every shipped resx, which
        /// is worthless unless it can report more than zero. Fed a value that
        /// carries the defect and one that does not, it must separate them.</summary>
        [Fact]
        public void TheLiteralEscapeCheckSeparatesGoodFromBad()
        {
            string good = "<data name=" + Q + "Good" + Q + " xml:space=" + Q + "preserve" + Q + "><value>line one" + nl_literal + "line two</value></data>";
            string bad  = "<data name=" + Q + "Bad" + Q + "  xml:space=" + Q + "preserve" + Q + "><value>line one" + backslash_n + "line two</value></data>";

            Assert.Empty(LiteralEscapeOffenders(good));
            Assert.Equal(new[] { "Bad" }, LiteralEscapeOffenders(bad));
            Assert.Equal(new[] { "Bad" }, LiteralEscapeOffenders(good + bad));

            // The degree sign spelled as an escape, and the path that only
            // looks like one.
            string unicodeEscape = "<data name=" + Q + "Degrees" + Q + " xml:space=" + Q + "preserve" + Q + "><value>90" + backslash + "u00B0</value></data>";
            string windowsPath   = "<data name=" + Q + "Path" + Q + " xml:space=" + Q + "preserve" + Q + "><value>Resources" + backslash + "x64" + backslash + "SDL3.dll</value></data>";
            Assert.Equal(new[] { "Degrees" }, LiteralEscapeOffenders(unicodeEscape));
            Assert.Empty(LiteralEscapeOffenders(windowsPath));
        }

        /// <summary>
        /// No string may render a literal character reference such as
        /// &amp;#176; where the character itself was meant.
        ///
        /// <para>The resx value is XML, so an ampersand written as
        /// &amp;amp; decodes to a bare ampersand and the reference that
        /// follows it never decodes at all. Settings_FlickCountsPer360
        /// shipped as "Dots per 360&amp;amp;#176;" and the Flick Stick card
        /// drew the six characters instead of the degree sign, while the
        /// sibling tooltip on the next line and all nine other locales
        /// carried the real character. One layer of escaping too many is
        /// invisible in the file and obvious on screen.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(AllResxFiles))]
        public void NoStringRendersALiteralCharacterReference(string fileName)
        {
            string path = Path.Combine(ResxDirectory(), fileName);
            Assert.True(File.Exists(path), "missing resx: " + path);
            string xml = File.ReadAllText(path);

            var offenders = DoubleEscapedEntityOffenders(xml);

            Assert.True(offenders.Count == 0,
                fileName + " has " + offenders.Count
                + " value(s) whose escaped ampersand leaves a character"
                + " reference to render verbatim: "
                + string.Join(", ", offenders));
        }

        /// <summary>Keys whose value carries an escaped ampersand followed by
        /// what would otherwise be a character or entity reference. Split out
        /// from the file walk for the same reason as the check above: a
        /// detector that only ever reports zero has not been shown to
        /// work.</summary>
        internal static List<string> DoubleEscapedEntityOffenders(string xml)
        {
            var offenders = new List<string>();
            foreach (Match m in Regex.Matches(
                xml, @"<data name=""([^""]+)""[^>]*>\s*<value>(.*?)</value>",
                RegexOptions.Singleline))
            {
                if (Regex.IsMatch(m.Groups[2].Value,
                        @"&amp;(#[0-9]+|#[xX][0-9a-fA-F]+|[a-zA-Z]+);"))
                    offenders.Add(m.Groups[1].Value);
            }
            return offenders;
        }

        /// <summary>A real ampersand and a real character reference are both
        /// legitimate. Only the two together are the defect, so the detector
        /// has to pass the first two and catch the third.</summary>
        [Fact]
        public void TheCharacterReferenceCheckSeparatesGoodFromBad()
        {
            string amp  = "<data name=" + Q + "Amp" + Q + "><value>Black " + Amp + "amp; White</value></data>";
            string deg  = "<data name=" + Q + "Deg" + Q + "><value>Dots per 360" + Amp + "#176;</value></data>";
            string bad  = "<data name=" + Q + "Bad" + Q + "><value>Dots per 360" + Amp + "amp;#176;</value></data>";

            Assert.Empty(DoubleEscapedEntityOffenders(amp));
            Assert.Empty(DoubleEscapedEntityOffenders(deg));
            Assert.Equal(new[] { "Bad" }, DoubleEscapedEntityOffenders(bad));
            Assert.Equal(new[] { "Bad" }, DoubleEscapedEntityOffenders(amp + deg + bad));
        }

        private const string Q = "\"";
        private const string nl_literal = "\n";
        private static readonly string backslash = ((char)92).ToString();
        private static readonly string backslash_n = backslash + "n";
        private const string Amp = "&";
        /// <summary>The base resx and every shipped locale resx, by file name.</summary>
        public static IEnumerable<object[]> AllResxFiles() =>
            new[] { new object[] { "Strings.resx" } }
                .Concat(Locales.Select(l => new object[] { "Strings." + l + ".resx" }));

        /// <summary>Walks up from the test binary to the resx folder, the way
        /// every other source-reading test in this suite does.</summary>
        private static string ResxDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null
                && !Directory.Exists(Path.Combine(dir.FullName, "PadForge.App")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "PadForge.App", "Resources", "Strings");
        }

    }
}
