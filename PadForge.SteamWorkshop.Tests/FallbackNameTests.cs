using System.IO;
using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;
using Xunit;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// The translator has no string table. Every word it writes into a stored
    /// name (an unnamed preset, menu or config, a macro's verb) comes from the
    /// options' lookup, which the app fills from PadForge's string table, and
    /// a set title is read in the options' languages. A run with no lookup
    /// takes the English of <see cref="TranslationNames"/>.
    /// </summary>
    public class FallbackNameTests
    {
        private static string Vdf(string secondPresetName) =>
            "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"#Missing\"\n"
            + "\t\"actions\"\n\t{\n\t\t\"Set_Two\"\n\t\t{\n\t\t\t\"title\"\t\"#T\"\n\t\t}\n\t}\n"
            + "\t\"localization\"\n\t{\n"
            + "\t\t\"english\"\n\t\t{\n\t\t\t\"T\"\t\"Two\"\n\t\t}\n"
            + "\t\t\"german\"\n\t\t{\n\t\t\t\"T\"\t\"Zwei\"\n\t\t}\n\t}\n"
            + Group(1, "radial_menu") + Group(2, "touch_menu")
            + Preset(0, "Default", (1, "right_joystick active"))
            + Preset(1, secondPresetName, (2, "right_trackpad active"))
            + "}\n";

        private static string Group(int id, string mode, string binding = null)
        {
            var sb = new System.Text.StringBuilder($"\t\"group\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"mode\"\t\"{mode}\"\n");
            sb.Append("\t\t\"inputs\"\n\t\t{\n");
            for (int i = 1; i <= 2; i++)
            {
                sb.Append($"\t\t\t\"touch_menu_button_{i}\"\n\t\t\t{{\n\t\t\t\t\"activators\"\n\t\t\t\t{{\n");
                sb.Append("\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n");
                sb.Append($"\t\t\t\t\t\t\t\"binding\"\t\"{binding ?? "key_press " + i}\"\n");
                sb.Append("\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n");
            }
            sb.Append("\t\t}\n\t}\n");
            return sb.ToString();
        }

        private static string Preset(int id, string name, params (int GroupId, string Binding)[] entries)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"\t\"preset\"\n\t{{\n\t\t\"id\"\t\"{id}\"\n\t\t\"name\"\t\"{name}\"\n");
            sb.Append("\t\t\"group_source_bindings\"\n\t\t{\n");
            foreach (var e in entries)
                sb.Append($"\t\t\t\"{e.GroupId}\"\t\"{e.Binding}\"\n");
            sb.Append("\t\t}\n\t}\n");
            return sb.ToString();
        }

        /// <summary>A lookup that marks every format it hands out, so a name
        /// built from it is told apart from one built from English.</summary>
        private static string Marked(string key) => "«" + TranslationNames.English[key] + "»";

        private static TranslatedProfile Translate(string vdf, TranslationOptions options)
            => new ConfigTranslator().Translate(SteamInputConfig.FromVdf(VdfParser.Parse(vdf)), options);

        [Fact]
        public void UnnamedPresetsMenusAndConfigsTakeTheLookupsNames()
        {
            var p = Translate(Vdf(""), new TranslationOptions { FileId = 5, NameFormat = Marked });
            Assert.Equal(new[] { "«Radial Menu 1»", "«Touch Menu 2» («Preset 1»)" },
                p.Menus.OrderBy(m => m.MenuId).Select(m => m.Name));
            Assert.Equal("«Workshop Profile»", p.Name);
        }

        [Fact]
        public void WithoutALookupTheNamesAreEnglish()
        {
            var p = Translate(Vdf(""), new TranslationOptions { FileId = 5 });
            Assert.Equal(new[] { "Radial Menu 1", "Touch Menu 2 (Preset 1)" },
                p.Menus.OrderBy(m => m.MenuId).Select(m => m.Name));
            Assert.Equal("Workshop Profile", p.Name);
        }

        [Fact]
        public void ALookupThatMissesTakesTheEnglish()
        {
            // The app's lookup answers a missing key with the key itself.
            var p = Translate(Vdf(""), new TranslationOptions { FileId = 5, NameFormat = key => key });
            Assert.Equal("Workshop Profile", p.Name);
        }

        [Fact]
        public void ASetTitleIsReadInThePreferredLanguages()
        {
            Assert.Equal("Touch Menu 2 (Zwei)", Translate(Vdf("Set_Two"), new TranslationOptions
            {
                FileId = 5,
                PreferredLanguages = new[] { "german" },
            }).Menus.Single(m => m.MenuId == 2).Name);
            Assert.Equal("Touch Menu 2 (Two)", Translate(Vdf("Set_Two"), new TranslationOptions { FileId = 5 })
                .Menus.Single(m => m.MenuId == 2).Name);
        }

        [Fact]
        public void TheChipsNameIsTheRunsName()
        {
            var config = SteamInputConfig.FromVdf(VdfParser.Parse(Vdf("")));
            var options = new TranslationOptions { NameFormat = Marked };
            Assert.Equal("«Preset 1»", ConfigTranslator.PresetDisplayName(config, config.Presets.Single(x => x.Id == 1), options));
            Assert.Equal("Default", ConfigTranslator.PresetDisplayName(config, config.Presets.Single(x => x.Id == 0), options));
        }

        /// <summary>A menu cell the author left unlabeled names its Steam
        /// command through the lookup.</summary>
        [Fact]
        public void AnUnlabeledCommandCellTakesTheLookupsPhrase()
        {
            string vdf = "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"x\"\n"
                + Group(1, "radial_menu", "controller_action SCREENSHOT")
                + Preset(0, "Default", (1, "right_joystick active"))
                + "}\n";
            var menu = Assert.Single(Translate(vdf, new TranslationOptions { FileId = 5, NameFormat = Marked }).Menus);
            Assert.All(menu.Items, item => Assert.Equal("«Screenshot»", item.Label));
            Assert.All(Assert.Single(Translate(vdf, new TranslationOptions { FileId = 5 }).Menus).Items,
                item => Assert.Equal("Screenshot", item.Label));
            Assert.Equal("«Warp cursor»", SteamVocabulary.CommandLabel("mouse_position", Marked));
            Assert.Equal("Warp cursor", SteamVocabulary.CommandLabel("mouse_position", key => key));
        }

        /// <summary>Across the whole fixture corpus, every macro the
        /// translator names and every mode-shift layer it names takes its
        /// words from the lookup.</summary>
        [Fact]
        public void EveryStoredMacroAndModeShiftNameComesFromTheLookup()
        {
            int macros = 0, shifts = 0;
            foreach (var path in TestFixtures.AllVdfPaths())
            {
                var config = SteamInputConfig.FromVdf(VdfParser.Parse(File.ReadAllText(path)));
                var p = new ConfigTranslator().Translate(config, new TranslationOptions { FileId = 9, NameFormat = Marked });
                foreach (var m in p.Macros)
                {
                    macros++;
                    Assert.True(m.Name.Contains('«'), $"{Path.GetFileName(path)}: macro \"{m.Name}\" is not from the lookup");
                }
                foreach (var a in p.XboxMappingSet.ShiftActivators.Concat(p.KbmMappingSet.ShiftActivators)
                             .Where(a => a.LayerMask != null && a.LayerMask.Contains("_MS_")))
                {
                    shifts++;
                    Assert.True(a.LayerName.Contains('«'),
                        $"{Path.GetFileName(path)}: mode shift layer \"{a.LayerName}\" is not from the lookup");
                }
            }
            Assert.True(macros > 50, "the corpus named only " + macros + " macros");
            Assert.True(shifts > 5, "the corpus named only " + shifts + " mode shifts");
        }
    }
}
