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

        /// <summary>A preset chip and the menu the import writes name a set
        /// alike: in the interface language, and from the string table when
        /// the author left the set or the menu unnamed. The translation ran in
        /// English while the chips read the interface language, and both wrote
        /// "Preset N" in English.</summary>
        [Fact]
        public void TheChipsAndTheImportNameASetAlike()
        {
            static string Group(int id, string mode) =>
                $"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n\t\t\"inputs\"\n\t\t{{\n"
                + "\t\t\t\"touch_menu_button_1\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n"
                + "\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\t\"binding\"\t\"key_press 1\"\n\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n"
                + "\t\t}\n\t}\n";
            static string Preset(int id, string name, int group, string host) =>
                $"\t\"preset\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"name\"\t\"{name}\"\n\t\t\"group_source_bindings\"\n\t\t{{\n"
                + $"\t\t\t\"{group}\"\t\"{host} active\"\n\t\t}}\n\t}}\n";
            string vdf = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"x\"\n"
                + "\t\"actions\"\n\t{\n\t\t\"Set_Two\"\n\t\t{\n\t\t\t\"title\"\t\"#T\"\n\t\t}\n\t}\n"
                + "\t\"localization\"\n\t{\n\t\t\"english\"\n\t\t{\n\t\t\t\"T\"\t\"Two\"\n\t\t}\n"
                + "\t\t\"german\"\n\t\t{\n\t\t\t\"T\"\t\"Zwei\"\n\t\t}\n\t}\n"
                + Group(1, "radial_menu") + Group(2, "touch_menu") + Group(3, "touch_menu")
                + Preset(0, "Default", 1, "right_joystick") + Preset(1, "Set_Two", 2, "right_trackpad")
                + Preset(2, "", 3, "left_trackpad")
                + "}\n";
            var config = PadForge.SteamWorkshop.Model.SteamInputConfig.FromVdf(PadForge.SteamWorkshop.Vdf.VdfParser.Parse(vdf));

            var before = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
                var options = CommunityConfigsPanel.WorkshopTranslationOptions(5, null);
                var menus = new PadForge.SteamWorkshop.Translation.ConfigTranslator().Translate(config, options)
                    .Menus.OrderBy(m => m.MenuId).Select(m => m.Name).ToList();
                string Label(int id) => CommunityConfigsPanel.PresetLabel(config, config.Presets.Single(p => p.Id == id));
                var strings = PadForge.Resources.Strings.Strings.Instance;

                Assert.Equal("Zwei", Label(1));
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, strings.Import_UnnamedPreset_Format, 2), Label(2));
                Assert.Equal(new[]
                {
                    string.Format(CultureInfo.CurrentCulture, strings.Import_UnnamedRadialMenu_Format, 1),
                    string.Format(CultureInfo.CurrentCulture, strings.Import_UnnamedTouchMenu_Format, 2) + " (" + Label(1) + ")",
                    string.Format(CultureInfo.CurrentCulture, strings.Import_UnnamedTouchMenu_Format, 3) + " (" + Label(2) + ")",
                }, menus);
            }
            finally
            {
                CultureInfo.CurrentUICulture = before;
            }
        }

        /// <summary>The Workshop translator's English for each name it stores
        /// is the string table's English for the same key, so a run without
        /// the app's lookup names things as the app does in English, and every
        /// key the translator asks for is in the table.</summary>
        [Fact]
        public void TheTranslatorsEnglishNamesAreTheStringTables()
        {
            foreach (var kv in PadForge.SteamWorkshop.Translation.TranslationNames.English)
                Assert.Equal(kv.Value, PadForge.Resources.Strings.Strings.ResourceManager.GetString(kv.Key, CultureInfo.InvariantCulture));
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
