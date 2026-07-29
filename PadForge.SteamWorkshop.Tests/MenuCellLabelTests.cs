using System.Linq;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;
using Xunit;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// A menu cell with no author label falls back to its binding's first
    /// parameter, and for a controller_action cell that parameter is the raw
    /// Steam verb. So an imported radial menu showed cells reading
    /// MOUSE_POSITION and add_layer in the Menus editor.
    ///
    /// <para>The browse dialog had a humanizer for exactly those tokens and
    /// the translator could not reach it, which made SteamVocabulary's own
    /// claim to be "the one place that converts" false. The table lives there
    /// now and both call it.</para>
    /// </summary>
    public class MenuCellLabelTests
    {
        [Theory]
        [InlineData("MOUSE_POSITION", "Warp cursor")]
        [InlineData("add_layer", "Turn on a shift layer")]
        [InlineData("remove_layer", "Turn off a shift layer")]
        [InlineData("hold_layer", "Hold a shift layer")]
        [InlineData("change_preset", "Switch shift layer")]
        [InlineData("SET_LED", "Set light color")]
        [InlineData("empty_binding", "Unbound")]
        public void CommandLabel_NamesTheVerb(string token, string expected)
        {
            Assert.Equal(expected, SteamVocabulary.CommandLabel(token));
        }

        [Fact]
        public void CommandLabel_IsCaseInsensitive_BecauseSteamMixesBothShapes()
        {
            // Steam writes ADD_LAYER in some places and add_layer in others,
            // and the corpus carries both.
            Assert.Equal(SteamVocabulary.CommandLabel("ADD_LAYER"),
                         SteamVocabulary.CommandLabel("add_layer"));
        }

        [Fact]
        public void CommandLabel_ReturnsNullForAnUnknownVerb()
        {
            // Null rather than a guess, so the caller falls back to plain
            // spelling instead of printing a confident wrong name.
            Assert.Null(SteamVocabulary.CommandLabel("SOME_FUTURE_VERB"));
            Assert.Null(SteamVocabulary.CommandLabel(""));
            Assert.Null(SteamVocabulary.CommandLabel(null));
        }

        [Fact]
        public void KeyAndButtonParametersKeepTheirOwnShape()
        {
            // A cell bound to a key falls back to the key itself, which is
            // what Steam shows when no label is set. Spelling must not turn
            // "F5" into "F5 " or lowercase it.
            Assert.Equal("F5", SteamVocabulary.SpellToken("F5"));
            Assert.Equal("A", SteamVocabulary.SpellToken("A"));
        }

        [Fact]
        public void RealConfigLabelsAreWordsNotTokens()
        {
            // The reported case: RCT3's radial menu. Every cell label must
            // read as words, and none may still carry an underscore-joined
            // wire token.
            var root = VdfParser.Parse(TestFixtures.Read(3456927474));
            var cfg = Model.SteamInputConfig.FromVdf(root);
            var p = new ConfigTranslator().Translate(cfg, new TranslationOptions { FileId = 3456927474 });

            var labels = p.Menus.SelectMany(m => m.Items)
                .Select(i => i.Label)
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();
            Assert.NotEmpty(labels);

            var raw = labels.Where(l => l.Contains('_')).ToList();
            Assert.True(raw.Count == 0,
                "menu cell labels still carrying wire tokens: " + string.Join(", ", raw));

            // And the specific two from the report.
            Assert.Contains("Turn on a shift layer", labels);
            Assert.Contains("Warp cursor", labels);
        }

        [Fact]
        public void PositiveControl_TheConfigReallyHasControllerActionCells()
        {
            // Without this the sweep above passes on a config whose cells all
            // carried author labels, proving nothing about the fallback.
            var vdf = TestFixtures.Read(3456927474);
            Assert.Contains("controller_action MOUSE_POSITION", vdf);
            Assert.Contains("controller_action add_layer", vdf);
        }
    }
}
