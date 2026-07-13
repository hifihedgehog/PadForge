using System;
using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    public class SteamInputConfigTests
    {
        private static SteamInputConfig ParseFixture(long fileId) =>
            SteamInputConfig.FromVdf(VdfParser.Parse(TestFixtures.Read(fileId)));

        [Fact]
        public void Rejects_version_below_three_with_pre2017_message()
        {
            var root = VdfParser.Parse("\"controller_mappings\" { \"version\" \"2\" \"title\" \"old\" }");
            var ex = Assert.Throws<SteamInputConfigException>(() => SteamInputConfig.FromVdf(root));
            Assert.Contains("version 2", ex.Message);
            Assert.Contains("pre-2017", ex.Message);
            Assert.Contains("version 3 only", ex.Message);
        }

        [Fact]
        public void Rejects_missing_version()
        {
            var root = VdfParser.Parse("\"controller_mappings\" { \"title\" \"no version\" }");
            var ex = Assert.Throws<SteamInputConfigException>(() => SteamInputConfig.FromVdf(root));
            Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Rejects_document_without_controller_mappings()
        {
            var root = VdfParser.Parse("\"something_else\" { \"version\" \"3\" }");
            Assert.Throws<SteamInputConfigException>(() => SteamInputConfig.FromVdf(root));
        }

        [Fact]
        public void Accepts_being_handed_the_mappings_node_directly()
        {
            var root = VdfParser.Parse(TestFixtures.Read(TestFixtures.SkyrimDs4));
            var viaRoot = SteamInputConfig.FromVdf(root);
            var viaMappings = SteamInputConfig.FromVdf(root["controller_mappings"]);
            Assert.Equal(viaRoot.Title, viaMappings.Title);
            Assert.Equal(viaRoot.Groups.Count, viaMappings.Groups.Count);
        }

        [Fact]
        public void Parses_skyrim_ds4_header_groups_presets_and_bindings()
        {
            var config = ParseFixture(TestFixtures.SkyrimDs4);

            Assert.Equal(3, config.Version);
            Assert.Equal("Dualshock 4 Skyrim", config.Title);
            Assert.Equal("controller_ps4", config.ControllerType);
            Assert.Equal("76561198001901205", config.CreatorSteamId);
            Assert.True(config.Groups.Count >= 20);
            Assert.NotEmpty(config.Presets);

            // Localization is captured per language.
            Assert.True(config.Localization.ContainsKey("english"));
            Assert.Equal("Gamepad", config.Localization["english"]["title"]);

            // The "Default" preset activates the button_diamond and switch groups.
            var preset = config.Presets.First(p => p.Id == 0);
            Assert.Equal("Default", preset.Name);
            Assert.Equal("button_diamond active", preset.GroupSourceBindings[0]);
            Assert.Equal("switch active", preset.GroupSourceBindings[7]);

            // Group 0 (four_buttons) binds button_a to xinput_button A via Full_Press.
            var group0 = config.Groups.First(g => g.Id == 0);
            Assert.Equal("four_buttons", group0.Mode);
            var buttonA = group0.Inputs["button_a"];
            var activator = buttonA.Activators.Single();
            Assert.Equal("Full_Press", activator.Type);
            var binding = activator.Bindings.Single();
            Assert.Equal("xinput_button", binding.Type);
            Assert.Equal("A", binding.Param);
        }

        [Fact]
        public void Parses_skyrim_kbm_fixture()
        {
            var config = ParseFixture(TestFixtures.SkyrimKbm);
            Assert.Equal(3, config.Version);
            Assert.StartsWith("SkyrimSE Perfected", config.Title);
            Assert.NotEmpty(config.Groups);
        }

        [Fact]
        public void Parses_homm3_deck_fixture_with_keypress_bindings()
        {
            var config = ParseFixture(TestFixtures.Homm3Deck);
            Assert.Equal(3, config.Version);
            Assert.Equal("HoMM3 HotA Deckified", config.Title);
            Assert.NotEmpty(config.Groups);

            var hasKeyPress = config.Groups
                .SelectMany(g => g.Inputs.Values)
                .SelectMany(i => i.Activators)
                .SelectMany(a => a.Bindings)
                .Any(b => b.Type == "key_press");
            Assert.True(hasKeyPress, "expected at least one key_press binding");
        }

        [Fact]
        public void Captures_reference_group_target_id()
        {
            // Factorio uses reference-mode groups with a referenced_mode setting.
            var config = ParseFixture(3353173512);
            var reference = config.Groups.FirstOrDefault(g => g.Mode == "reference");
            Assert.NotNull(reference);
            Assert.True(reference.ReferencedGroupId.HasValue);
        }

        [Fact]
        public void Exposes_disabled_activator_types_list()
        {
            var config = ParseFixture(TestFixtures.GabeGeneration);
            var anyInput = config.Groups.SelectMany(g => g.Inputs.Values).First();
            // The property is always present (empty when nothing is disabled).
            Assert.NotNull(anyInput.DisabledActivatorTypes);
        }

        [Theory]
        [InlineData("xinput_button A", "xinput_button", "A", null)]
        [InlineData("mouse_wheel SCROLL_UP, scroll up", "mouse_wheel", "SCROLL_UP", "scroll up")]
        [InlineData("controller_action CHANGE_PRESET 2 1 1, Army Command Action Set",
            "controller_action", "CHANGE_PRESET 2 1 1", "Army Command Action Set")]
        [InlineData("key_press F5, Quicksave, ghost_030_inv_0100.png #000000 #ad0000",
            "key_press", "F5", "Quicksave")]
        public void Binding_parse_splits_type_param_and_action(string raw, string type, string param, string action)
        {
            var binding = SteamInputBinding.Parse(raw);
            Assert.Equal(type, binding.Type);
            Assert.Equal(param, binding.Param);
            Assert.Equal(action, binding.ActionName);
            Assert.Equal(raw, binding.Raw);
        }

        [Fact]
        public void Every_committed_fixture_parses_to_a_version_three_config()
        {
            var count = 0;
            foreach (var path in TestFixtures.AllVdfPaths())
            {
                var root = VdfParser.Parse(System.IO.File.ReadAllText(path));
                var config = SteamInputConfig.FromVdf(root);
                Assert.Equal(3, config.Version);
                Assert.NotEmpty(config.Groups);
                count++;
            }
            Assert.Equal(22, count);
        }
    }
}
