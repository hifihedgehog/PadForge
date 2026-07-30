using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The community-profiles filter rendered Steam's raw tags
    /// ("controller_ps5_edge", "controller_steamcontroller_gordon") because
    /// Steam returns the TAG ITSELF as a controller tag's display_name, and
    /// the chip builder preferred display_name whenever it was non-empty.
    ///
    /// <para>Prettifying the tag instead is not a fix either: the tag
    /// bodies are Valve's internal codenames, so it yields "Ps5 Edge" and
    /// "Steamcontroller Gordon". The names have to be mapped.</para>
    ///
    /// <para>The codenames were resolved rather than guessed, because they
    /// are user-visible: neptune is the Steam Deck, triton is the
    /// Steam Controller (2026) (28DE:1304), gordon is the Steam Controller
    /// (2015). Both generations are named by YEAR rather than by ordinal,
    /// matching the branding and the rest of PadForge.</para></summary>
    public class ControllerTagLabelTests
    {
        [Theory]
        [InlineData("controller_ps5_edge", "DualSense Edge")]
        [InlineData("controller_ps5", "DualSense")]
        [InlineData("controller_ps4", "DualShock 4")]
        [InlineData("controller_xboxone", "Xbox One")]
        [InlineData("controller_xbox360", "Xbox 360")]
        [InlineData("controller_xboxelite", "Xbox Elite")]
        [InlineData("controller_switch_pro", "Switch Pro")]
        [InlineData("controller_generic", "Generic")]
        public void RetailNames(string tag, string expected)
            => Assert.Equal(expected, WorkshopBrowseDialog.ControllerTagLabel(tag, null));

        /// <summary>The three codename tags, which are the ones a
        /// prettifier cannot possibly get right.</summary>
        [Theory]
        [InlineData("controller_neptune", "Steam Deck")]
        [InlineData("controller_triton", "Steam Controller (2026)")]
        [InlineData("controller_steamcontroller_gordon", "Steam Controller (2015)")]
        public void CodenamesResolveToRetailNames(string tag, string expected)
            => Assert.Equal(expected, WorkshopBrowseDialog.ControllerTagLabel(tag, null));

        /// <summary>THE BUG. Steam echoes the tag back as display_name, so
        /// a mapped tag must ignore it rather than render it verbatim.</summary>
        [Fact]
        public void EchoedDisplayNameIsIgnored()
        {
            Assert.Equal("DualSense Edge",
                WorkshopBrowseDialog.ControllerTagLabel("controller_ps5_edge", "controller_ps5_edge"));
            Assert.Equal("Steam Controller (2015)",
                WorkshopBrowseDialog.ControllerTagLabel(
                    "controller_steamcontroller_gordon", "controller_steamcontroller_gordon"));
        }

        /// <summary>A genuinely different display_name from Steam still
        /// wins for a tag we have not mapped, so a controller released
        /// after this build gets Steam's name rather than ours.</summary>
        [Fact]
        public void UnmappedTagPrefersARealDisplayName()
            => Assert.Equal("Cool New Pad",
                WorkshopBrowseDialog.ControllerTagLabel("controller_future_thing", "Cool New Pad"));

        /// <summary>And with no display_name at all it degrades to the
        /// prettified tag instead of showing the raw string.</summary>
        [Fact]
        public void UnmappedTagWithoutDisplayNameFallsBackToPrettified()
        {
            var label = WorkshopBrowseDialog.ControllerTagLabel("controller_future_thing", null);
            Assert.Equal("Future Thing", label);
            Assert.DoesNotContain("controller_", label);
        }
    }
}
