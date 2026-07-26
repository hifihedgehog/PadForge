using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The community-config manifest rendered the translator's own
    /// vocabulary as two columns of monospace text ("Gamepad Paddle2 ->
    /// ButtonA"), which is a diff report rather than a preview: nothing
    /// tells a reader WHERE on the controller a binding lives.
    ///
    /// <para>These cover the two pure halves of the fix: the source text a
    /// human reads, and the badge that makes a row scannable without
    /// reading it at all.</para></summary>
    public class WorkshopManifestBadgeTests
    {
        // ── Friendly source text ─────────────────────────────────────

        [Theory]
        [InlineData("Gamepad Paddle2", "Paddle 2")]
        [InlineData("Gamepad Paddle1", "Paddle 1")]
        [InlineData("Gamepad LeftStickX", "Left Stick X")]
        [InlineData("Gamepad RightTrigger", "Right Trigger")]
        [InlineData("Gamepad A", "A")]
        public void FriendlySource_DropsNoiseAndSpacesWords(string raw, string expected)
            => Assert.Equal(expected, WorkshopBrowseDialog.FriendlySource(raw));

        [Fact]
        public void FriendlySource_LeavesUnknownTextAlone()
        {
            Assert.Equal("", WorkshopBrowseDialog.FriendlySource(""));
            Assert.Null(WorkshopBrowseDialog.FriendlySource(null));
        }

        // ── Badges ───────────────────────────────────────────────────

        [Theory]
        [InlineData("Gamepad A", "A")]
        [InlineData("Gamepad B", "B")]
        [InlineData("Gamepad LeftTrigger", "LT")]
        [InlineData("Gamepad RightTrigger", "RT")]
        [InlineData("Gamepad LeftBumper", "LB")]
        [InlineData("Gamepad RightBumper", "RB")]
        [InlineData("Gamepad LeftStickX", "LS")]
        [InlineData("Gamepad RightStickY", "RS")]
        [InlineData("Gamepad DPadUp", "D-Pad")]
        [InlineData("Gamepad Paddle2", "P2")]
        public void Badge_GlyphForKnownInputs(string source, string glyph)
            => Assert.Equal(glyph, WorkshopBrowseDialog.InputBadge(source).Glyph);

        /// <summary>Face buttons and sticks are round, everything else is a
        /// pill. Shape carries the class alongside the tint, so the list
        /// stays readable for anyone who cannot separate the hues.</summary>
        [Theory]
        [InlineData("Gamepad A", true)]
        [InlineData("Gamepad LeftStickX", true)]
        [InlineData("Gamepad LeftTrigger", false)]
        [InlineData("Gamepad DPadUp", false)]
        public void Badge_ShapeCarriesTheClass(string source, bool round)
            => Assert.Equal(round, WorkshopBrowseDialog.InputBadge(source).Round);

        /// <summary>Input classes are tinted apart, so a reader can find
        /// every trigger without reading a word.</summary>
        [Fact]
        public void Badge_ClassesAreTintedApart()
        {
            var face = WorkshopBrowseDialog.InputBadge("Gamepad A").Tint;
            var trigger = WorkshopBrowseDialog.InputBadge("Gamepad LeftTrigger").Tint;
            var bumper = WorkshopBrowseDialog.InputBadge("Gamepad LeftBumper").Tint;
            var stick = WorkshopBrowseDialog.InputBadge("Gamepad LeftStickX").Tint;

            Assert.NotEqual(face, trigger);
            Assert.NotEqual(face, bumper);
            Assert.NotEqual(trigger, bumper);
            Assert.NotEqual(stick, face);
        }

        /// <summary>An unrecognised source gets NO badge rather than an
        /// invented one. Guessing a position on the controller would be
        /// worse than the plain text it replaces.</summary>
        [Theory]
        [InlineData("Some Future Input")]
        [InlineData("")]
        [InlineData(null)]
        public void Badge_UnknownSourceGetsNoGlyph(string source)
            => Assert.Equal("", WorkshopBrowseDialog.InputBadge(source).Glyph);
    }
}
