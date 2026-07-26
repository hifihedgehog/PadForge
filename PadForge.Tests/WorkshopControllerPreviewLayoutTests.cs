using System;
using System.IO;
using System.Linq;
using PadForge.Models2D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The community-profile preview draws the pad the config was
    /// authored on. Valve's own hardware is most of what the Steam workshop
    /// carries, and every Steam tag used to fall through to an Xbox body.
    ///
    /// <para>These lock the two things that silently rot: a tag routing to
    /// the wrong body, and a layout entry naming a sprite that is not
    /// shipped (which renders as nothing at all, with no error).</para></summary>
    public class WorkshopControllerPreviewLayoutTests
    {
        private static string ModelsDir()
        {
            var d = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && d != null; i++)
            {
                var c = Path.Combine(d, "PadForge.App", "2DModels");
                if (Directory.Exists(c)) return c;
                d = Path.GetDirectoryName(d);
            }
            return null;
        }

        public static TheoryData<OverlayElement[], string> SteamLayouts() => new()
        {
            { SteamDeckLayout.Overlays, "STEAMDECK" },
            { SteamControllerLayout.Overlays, "STEAMCONTROLLER" },
        };

        /// <summary>Every named sprite must exist on disk. A layout entry
        /// pointing at a missing PNG draws nothing and reports nothing.</summary>
        [Theory]
        [MemberData(nameof(SteamLayouts))]
        public void EveryNamedSpriteIsShipped(OverlayElement[] overlays, string folder)
        {
            var dir = ModelsDir();
            Assert.NotNull(dir);
            var missing = overlays
                .Select(o => o.ImageFile)
                .Where(f => !string.IsNullOrEmpty(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => !File.Exists(Path.Combine(dir, folder, f)))
                .ToList();
            Assert.Empty(missing);
        }

        /// <summary>Every element sits inside the body it is drawn on.
        /// An out-of-bounds rect puts a callout tick in dead space.</summary>
        [Theory]
        [MemberData(nameof(SteamLayouts))]
        public void EveryElementIsInsideTheBody(OverlayElement[] overlays, string folder)
        {
            int w = folder == "STEAMDECK" ? SteamDeckLayout.BaseWidth : SteamControllerLayout.BaseWidth;
            int h = folder == "STEAMDECK" ? SteamDeckLayout.BaseHeight : SteamControllerLayout.BaseHeight;
            var bad = overlays
                .Where(o => o.X < -10 || o.Y < -10 || o.X + o.Width > w + 10 || o.Y + o.Height > h + 10)
                .Select(o => o.TargetName)
                .ToList();
            Assert.Empty(bad);
        }

        /// <summary>The Deck carries the full Xbox-style control set, so a
        /// config binding any of these has somewhere to point.</summary>
        [Theory]
        [InlineData("ButtonA")]
        [InlineData("ButtonB")]
        [InlineData("ButtonX")]
        [InlineData("ButtonY")]
        [InlineData("DPadUp")]
        [InlineData("DPadDown")]
        [InlineData("DPadLeft")]
        [InlineData("DPadRight")]
        [InlineData("LeftTrigger")]
        [InlineData("RightTrigger")]
        [InlineData("LeftShoulder")]
        [InlineData("RightShoulder")]
        [InlineData("LeftThumbRing")]
        [InlineData("RightThumbRing")]
        [InlineData("ButtonBack")]
        [InlineData("ButtonStart")]
        [InlineData("ButtonGuide")]
        [InlineData("LeftTouchpad")]
        [InlineData("RightTouchpad")]
        [InlineData("Paddle1")]
        [InlineData("Paddle2")]
        [InlineData("Paddle3")]
        [InlineData("Paddle4")]
        public void DeckCoversTheControlSet(string target)
            => Assert.Contains(SteamDeckLayout.Overlays,
                               o => string.Equals(o.TargetName, target, StringComparison.Ordinal));

        /// <summary>The Steam Controller has ONE stick and NO d-pad (the
        /// left trackpad serves that role). Emitting a right stick or a
        /// d-pad would anchor callouts to parts the pad does not have.</summary>
        [Fact]
        public void SteamControllerHasNoRightStickAndNoDPad()
        {
            var names = SteamControllerLayout.Overlays.Select(o => o.TargetName).ToList();
            Assert.Contains("LeftThumbRing", names);
            Assert.DoesNotContain(names, n => n.StartsWith("RightThumb", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.StartsWith("DPad", StringComparison.Ordinal));
            Assert.Contains("LeftGrip", names);
            Assert.Contains("RightGrip", names);
        }

        /// <summary>Valve tags must reach Valve bodies. Before this, every
        /// one of them fell through to the Xbox One S shape.</summary>
        /// <summary>The Deck's four rear paddles have no front-facing
        /// position, so they are drawn on the Compact overlay's dedicated
        /// labeled tiles. The plain VSCView overlay parks them OFF canvas
        /// (x=-149 and x=1879 on an 1860-wide body), which is why the
        /// layout is built on the Compact/Alternative pair instead.</summary>
        [Fact]
        public void DeckPaddlesAreOnTheBodyNotParkedOffCanvas()
        {
            var paddles = SteamDeckLayout.Overlays
                .Where(o => o.TargetName.StartsWith("Paddle", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(4, paddles.Count);
            Assert.All(paddles, p =>
            {
                Assert.InRange(p.X, 0, SteamDeckLayout.BaseWidth - p.Width);
                Assert.InRange(p.Y, 0, SteamDeckLayout.BaseHeight - p.Height);
            });
        }

        /// <summary>THE DIRECTION. The preview draws the pad the config was
        /// authored ON, so a binding is anchored to the control the author
        /// physically pressed and labelled with what it produces. Anchoring
        /// on the target drew the VIRTUAL pad's geometry on the source
        /// device's body: a Deck touchpad bound to the d-pad lit the Deck's
        /// own d-pad and called it "Touchpad 0".</summary>
        [Theory]
        [InlineData("Touchpad 0 DPadUp", "LeftTouchpadClick")]
        [InlineData("Touchpad 0 Click", "LeftTouchpadClick")]
        [InlineData("Touchpad 1 Finger 0 X", "RightTouchpadClick")]
        [InlineData("Gamepad Paddle1", "Paddle1")]
        [InlineData("Gamepad Paddle4", "Paddle4")]
        [InlineData("Gamepad LeftStick", "LeftThumbRing")]
        [InlineData("Gamepad LeftStickRing", "LeftThumbRing")]
        [InlineData("Gamepad RightStick", "RightThumbRing")]
        [InlineData("LeftThumbButton", "LeftThumbButton")]
        [InlineData("Gamepad ButtonA", "ButtonA")]
        [InlineData("Gamepad DPadUp", "DPadUp")]
        [InlineData("LeftTrigger", "LeftTrigger")]
        public void SourceAnchorsOntoItsOwnControl(string source, string expected)
            => Assert.Equal(expected, PadForge.Views.WorkshopBrowseDialog.ArtAnchorFor(source));

        /// <summary>A DualSense or DS4 has ONE touchpad, named without a
        /// side, so a sided anchor must fold onto it instead of vanishing.</summary>
        [Fact]
        public void SidedTouchpadFoldsOntoASingleTouchpadBody()
        {
            var w = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["LeftTouchpadClick"] = "D-Pad Up" };
            PadForge.Views.WorkshopControllerPreview.FoldTouchpadAnchors(w, DualSenseLayout.Overlays);
            Assert.False(w.ContainsKey("LeftTouchpadClick"));
            Assert.Equal("D-Pad Up", w["TouchpadClick"]);
        }

        /// <summary>...and must NOT fold on a body that really has two.</summary>
        [Fact]
        public void SidedTouchpadStaysPutOnATwoPadBody()
        {
            var w = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["LeftTouchpadClick"] = "D-Pad Up" };
            PadForge.Views.WorkshopControllerPreview.FoldTouchpadAnchors(w, SteamDeckLayout.Overlays);
            Assert.Equal("D-Pad Up", w["LeftTouchpadClick"]);
        }

        [Theory]
        [InlineData("controller_neptune", "STEAMDECK")]
        [InlineData("controller_steamcontroller_gordon", "STEAMCONTROLLER")]
        [InlineData("controller_steamcontroller", "STEAMCONTROLLER")]
        [InlineData("controller_triton", "STEAMCONTROLLER")]
        [InlineData("controller_ps5_edge", "DualSense")]
        [InlineData("controller_ps4", "DS4")]
        [InlineData("controller_switch_pro", "SWITCHPRO")]
        [InlineData("controller_xbox360", "XBOX360")]
        [InlineData("controller_xboxelite", "XBOXSERIES")]
        [InlineData("controller_xboxone", "XBOXONE")]
        [InlineData(null, "XBOXONE")]
        public void TagRoutesToItsOwnBody(string tag, string expectedFolder)
            => Assert.Equal(expectedFolder, PadForge.Views.WorkshopControllerPreview.FolderForTag(tag));
    }
}
