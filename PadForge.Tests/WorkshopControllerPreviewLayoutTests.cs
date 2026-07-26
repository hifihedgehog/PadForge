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
