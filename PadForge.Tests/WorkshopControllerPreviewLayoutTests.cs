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
            { SteamController2Layout.Overlays, "STEAMCONTROLLER2" },
        };

        /// <summary>Base canvas per folder, so the bounds check reads the
        /// layout it was handed instead of choosing between two.</summary>
        private static (int W, int H) BaseSize(string folder) => folder switch
        {
            "STEAMDECK" => (SteamDeckLayout.BaseWidth, SteamDeckLayout.BaseHeight),
            "STEAMCONTROLLER" => (SteamControllerLayout.BaseWidth, SteamControllerLayout.BaseHeight),
            "STEAMCONTROLLER2" => (SteamController2Layout.BaseWidth, SteamController2Layout.BaseHeight),
            _ => (0, 0),
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
            var (w, h) = BaseSize(folder);
            Assert.True(w > 0 && h > 0, $"no base size registered for {folder}");
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

        /// <summary>The Steam Controller carries its d-pad and its right
        /// stick ON ITS TRACKPADS, because that is where the hardware puts
        /// them: SDL reads the left pad as the hat and the right pad as the
        /// right stick's axes (SDL_hidapi_steam.c). The 3D preview has stood
        /// a synthesized stick on that pad since the model was built, and
        /// the 2D view carried neither control at all, so the pad reached
        /// the mapping page with nothing to hover, flash or bind for eight
        /// of its inputs.
        ///
        /// <para>The wedges and the stand-in stick are ANCHORED to the pads
        /// they ride, which is what this pins: a wedge that drifted off the
        /// left pad, or a stick that landed outside the right one, would
        /// bind a control the user cannot see.</para></summary>
        [Fact]
        public void SteamControllerRidesItsPadsAsDPadAndRightStick()
        {
            var by = SteamControllerLayout.Overlays.ToDictionary(o => o.TargetName);
            Assert.Contains("LeftThumbRing", by.Keys);
            Assert.Contains("LeftGrip", by.Keys);
            Assert.Contains("RightGrip", by.Keys);

            var left = by["LeftTouchpad"];
            foreach (var d in new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" })
            {
                var w = by[d];
                Assert.Equal(left.X, w.X);
                Assert.Equal(left.Y, w.Y);
                Assert.Equal(left.Width, w.Width);
                Assert.Equal(left.Height, w.Height);
            }

            var right = by["RightTouchpad"];
            foreach (var s in new[] { "RightThumbRing", "RightThumbButton" })
            {
                var g = by[s];
                Assert.InRange(g.X, right.X, right.X + right.Width - g.Width);
                Assert.InRange(g.Y, right.Y, right.Y + right.Height - g.Height);
            }
            Assert.Equal(OverlayElementType.StickRing, by["RightThumbRing"].ElementType);
            Assert.Equal(OverlayElementType.StickClick, by["RightThumbButton"].ElementType);
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
        /// physically pressed and labeled with what it produces. Anchoring
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
        // The 2026 pad now has its own body. It used to borrow the 2015
        // one, which was a stick and a D-pad short of the real device.
        [InlineData("controller_triton", "STEAMCONTROLLER2")]
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
