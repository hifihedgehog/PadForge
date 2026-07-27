using PadForge.Engine;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Locks the audit fixes that are reachable without a UI thread.
    /// Each of these was a shipped defect found by the 2026-07-26 whole-codebase
    /// audit; the tests exist so the contract cannot silently regress.</summary>
    public class AuditFixTests
    {
        // ── Finding #9: decorated source fed to the art anchor ──────────────
        //
        // SourceAndTarget returns the identifier PLUS the translator's
        // half-axis/invert parenthetical PLUS a " · activator" annotation.
        // ArtAnchorFor and FriendlySource both key on the BARE stem, so a
        // decorated string matched no overlay and fell through to the raw
        // identifier in the label.

        [Theory]
        [InlineData("Gamepad ButtonA", "Gamepad ButtonA", "")]
        [InlineData("Gamepad ButtonA · Long Press", "Gamepad ButtonA", " · Long Press")]
        [InlineData("Touchpad 0 DPadUp · Double Tap", "Touchpad 0 DPadUp", " · Double Tap")]
        [InlineData("Gamepad LeftStick (half)", "Gamepad LeftStick", " (half)")]
        [InlineData("Gamepad LeftStick (half) · Long Press", "Gamepad LeftStick", " (half) · Long Press")]
        public void DecorationIsSplitFromTheBareStem(string source, string bare, string decoration)
        {
            var (b, d) = WorkshopBrowseDialog.SplitSourceDecoration(source);
            Assert.Equal(bare, b);
            Assert.Equal(decoration, d);
        }

        /// <summary>THE DEFECT ITSELF: a decorated source must still anchor to
        /// the same art element as its undecorated twin. Before the fix the
        /// activator annotation rode into ArtAnchorFor and the binding lit
        /// nothing at all.</summary>
        [Theory]
        [InlineData("Gamepad ButtonA · Long Press", "ButtonA")]
        [InlineData("Touchpad 0 Click · Double Tap", "LeftTouchpadClick")]
        [InlineData("Touchpad 1 Finger 0 X · Long Press", "RightTouchpadClick")]
        [InlineData("Gamepad Paddle1 · Long Press", "Paddle1")]
        [InlineData("Gamepad LeftStick (half)", "LeftThumbRing")]
        public void DecoratedSourceStillAnchorsToItsControl(string decorated, string expectedAnchor)
        {
            var (bare, _) = WorkshopBrowseDialog.SplitSourceDecoration(decorated);
            Assert.Equal(expectedAnchor, WorkshopBrowseDialog.ArtAnchorFor(bare));
        }

        /// <summary>And the decoration must survive into the label, or the fix
        /// would trade a dead anchor for a lost annotation.</summary>
        [Fact]
        public void DecorationSurvivesIntoTheLabel()
        {
            var (bare, deco) = WorkshopBrowseDialog.SplitSourceDecoration("Gamepad ButtonA · Long Press");
            Assert.Equal("A · Long Press", WorkshopBrowseDialog.FriendlySource(bare) + deco);
        }

        // ── Finding #16: RawHidState.Clear skipped HardwareAxes ─────────────

        /// <summary>HardwareAxes is the pre-tuning mirror of Axes. Clear() left
        /// it carrying the last stick sample, which the Pad page reads back as
        /// live input on a device that has gone away.</summary>
        [Fact]
        public void ClearNeutralizesEveryArrayIncludingHardwareAxes()
        {
            var s = new RawHidState
            {
                Axes = new short[] { 16000, -16000 },
                HardwareAxes = new short[] { 32000, -32000 },
                Buttons = new uint[] { 0xFFFFFFFF },
                Povs = new[] { 90 },
            };

            s.Clear();

            Assert.All(s.Axes, a => Assert.Equal((short)0, a));
            Assert.All(s.HardwareAxes, a => Assert.Equal((short)0, a));
            Assert.All(s.Buttons, b => Assert.Equal(0u, b));
            Assert.All(s.Povs, p => Assert.Equal(-1, p));
        }

        [Fact]
        public void ClearToleratesNullArrays()
        {
            var s = new RawHidState();
            s.Clear();
        }

        // ── Finding #22: EmbeddedBitmaps had no cache ───────────────────────

        /// <summary>Every result is frozen, so sharing is safe, and the mouse
        /// preview re-decoded ~10 MB of Bgra32 on each theme rebuild without
        /// this. Identity equality is the assertion: a second call must not
        /// decode again.</summary>
        [Fact]
        public void RepeatedLoadsReturnTheSameFrozenInstance()
        {
            var a = EmbeddedBitmaps.Load(MouseArt.Dir + MouseArt.Line);
            var b = EmbeddedBitmaps.Load(MouseArt.Dir + MouseArt.Line);
            Assert.NotNull(a);
            Assert.True(a.IsFrozen);
            Assert.Same(a, b);
        }

        /// <summary>A miss must stay a miss without re-probing every frame.</summary>
        [Fact]
        public void MissesAreCachedToo()
        {
            var a = EmbeddedBitmaps.Load("2DModels/NOPE/does_not_exist.png");
            var b = EmbeddedBitmaps.Load("2DModels/NOPE/does_not_exist.png");
            Assert.Null(a);
            Assert.Null(b);
        }
    }
}
