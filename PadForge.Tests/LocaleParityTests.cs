using System.Collections;
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
    }
}
