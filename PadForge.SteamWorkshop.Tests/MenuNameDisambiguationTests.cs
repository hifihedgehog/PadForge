using System.Linq;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;
using Xunit;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Steam needs one group PER PRESET to offer the same menu in two action
    /// sets, and a group name is unique only within its preset. PadForge's
    /// Menus list is flat, so RCT3 Weno V0.1, which names both of its radial
    /// groups "Menu", produced two entries reading "Menu" with nothing to tell
    /// them apart and no way to know which layer each drove.
    ///
    /// <para>They are NOT merged. The two menus are separate objects the user
    /// can edit apart, and a preset switch is meant to bring its own menu. The
    /// name says which layer instead.</para>
    /// </summary>
    public class MenuNameDisambiguationTests
    {
        private static Translation.TranslatedProfile Translate(long id)
        {
            var root = VdfParser.Parse(TestFixtures.Read(id));
            var cfg = Model.SteamInputConfig.FromVdf(root);
            return new ConfigTranslator().Translate(cfg, new TranslationOptions { FileId = id });
        }

        [Fact]
        public void TwoSameNamedMenusBecomeDistinguishable()
        {
            var names = Translate(3456927474).Menus.Select(m => m.Name).ToList();
            Assert.Equal(2, names.Count);
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.Contains("Menu", names);
            Assert.Contains("Menu (Menu save)", names);
        }

        [Fact]
        public void TheBaseLayersMenuIsNotQualified()
        {
            // Tagging the profile's own menu "(Default)" is noise on every
            // config rather than a distinction.
            var names = Translate(3456927474).Menus.Select(m => m.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("(Default)"));
        }

        [Theory]
        [InlineData(3456927474L)]
        [InlineData(3353173512L)]
        [InlineData(3451446931L)]
        [InlineData(789818086L)]
        public void NoConfigProducesTwoMenusWithTheSameName(long id)
        {
            var names = Translate(id).Menus.Select(m => m.Name).ToList();
            var dupes = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dupes.Count == 0,
                "menus indistinguishable in the list: " + string.Join(", ", dupes));
        }

        [Fact]
        public void PositiveControl_TheseConfigsReallyHaveMultipleMenus()
        {
            // Without this the sweep passes on configs with one menu each,
            // where uniqueness is free.
            foreach (var id in new[] { 3456927474L, 3353173512L })
                Assert.True(Translate(id).Menus.Count >= 2, id + " has fewer than 2 menus");
        }
    }
}
