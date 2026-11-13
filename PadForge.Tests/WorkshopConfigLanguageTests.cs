using System;
using System.Globalization;
using System.Linq;
using PadForge.Common;
using PadForge.Views;

namespace PadForge.Tests
{
    /// <summary>
    /// A Workshop config names its presets through its own localization
    /// table, one block per Steam language. The browser reads the block for
    /// the interface language, then the other variety of that language where
    /// Steam keeps two, then English (#505). The map named only the first ten
    /// interface languages: Russian or Polish read English, Traditional
    /// Chinese read Simplified, and Portugal's Portuguese read Brazil's.
    /// </summary>
    [Collection("CultureSwitching")]
    public class WorkshopConfigLanguageTests
    {
        /// <summary>The "API language code" column of Steamworks' Supported
        /// Languages page (partner.steamgames.com/doc/store/localization/languages).</summary>
        private static readonly string[] SteamLanguages =
        {
            "arabic", "bulgarian", "schinese", "tchinese", "czech", "danish", "dutch", "english",
            "finnish", "french", "german", "greek", "hungarian", "indonesian", "italian", "japanese",
            "koreana", "malay", "norwegian", "polish", "portuguese", "brazilian", "romanian", "russian",
            "spanish", "latam", "swedish", "thai", "turkish", "ukrainian", "vietnamese",
        };

        [Theory]
        [InlineData("zh-HK", "tchinese,schinese")]
        [InlineData("zh-Hant", "tchinese,schinese")]
        [InlineData("zh-Hans", "schinese,tchinese")]
        [InlineData("pt-PT", "portuguese,brazilian")]
        [InlineData("pt-BR", "brazilian,portuguese")]
        [InlineData("es", "spanish,latam")]
        [InlineData("ru", "russian")]
        [InlineData("pl", "polish")]
        [InlineData("no", "norwegian")]
        [InlineData("ko", "koreana")]
        [InlineData("de", "german")]
        [InlineData("en-US", "")]
        [InlineData("en-GB", "")]
        [InlineData("sw", "")]
        public void PresetNamesAreReadInTheInterfaceLanguage(string tag, string names)
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(tag);
                var expected = (names.Length == 0 ? Array.Empty<string>() : names.Split(',')).Append("english");
                Assert.Equal(expected, CommunityConfigsPanel.PreferredConfigLanguages());
            }
            finally
            {
                CultureInfo.CurrentUICulture = before;
            }
        }

        /// <summary>Each entry is a row of the language table and names only
        /// languages Steam lists, and every Steam language but English is
        /// reached: the interface is translated into all of them.</summary>
        [Fact]
        public void EverySteamLanguageIsReachedFromItsRow()
        {
            foreach (var entry in CommunityConfigsPanel.SteamLanguageNames)
            {
                Assert.True(LanguageCatalog.Find(entry.Key)?.Tag == entry.Key, entry.Key + " is not a row of the language table");
                Assert.All(entry.Value, name => Assert.Contains(name, SteamLanguages));
            }
            var reached = CommunityConfigsPanel.SteamLanguageNames.Values.SelectMany(v => v).ToHashSet();
            Assert.All(SteamLanguages.Where(n => n != "english"), name => Assert.Contains(name, reached));
        }
    }
}
