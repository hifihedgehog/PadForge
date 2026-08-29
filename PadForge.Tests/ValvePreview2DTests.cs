using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Models2D;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The 2D preview of the three Valve pads.
    ///
    /// <para>Every case here is a defect the owner reported on 2026-08-29,
    /// in one sitting: triggers, pads and rear paddles that showed no hover
    /// highlight; a 2026 layout with no bumpers, triggers or grips at all; a
    /// 2015 layout with no d-pad and no right stick; and touch dots that drew
    /// themselves off the pad they happened on. They share one root, which is
    /// that a control the front art does not draw was left out of the layout
    /// instead of being drawn some other way.</para></summary>
    public class ValvePreview2DTests
    {
        private static readonly (string Model, string Folder, OverlayElement[] Overlays)[] Valve =
        {
            ("STEAMDECK", "STEAMDECK", SteamDeckLayout.Overlays),
            ("STEAMCONTROLLER", "STEAMCONTROLLER", SteamControllerLayout.Overlays),
            ("STEAMCONTROLLER2", "STEAMCONTROLLER2", SteamController2Layout.Overlays),
        };

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        private static (double R, double G, double B) MeanColor(string resourcePath)
        {
            var bmp = EmbeddedBitmaps.Load(resourcePath);
            Assert.NotNull(bmp);
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            double b = 0, g = 0, r = 0;
            long n = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (px[i * 4 + 3] <= 128) continue;
                b += px[i * 4]; g += px[i * 4 + 1]; r += px[i * 4 + 2];
                n++;
            }
            Assert.True(n > 0, resourcePath + " has no opaque pixels");
            return (r / n, g / n, b / n);
        }

        /// <summary>The pack press blue, measured off SD_Face_Button.png and
        /// SC2_ButtonA.png, which agree exactly.</summary>
        private static void AssertPressBlue(string resourcePath)
        {
            var mean = MeanColor(resourcePath);
            Assert.True(mean.B > 200 && mean.G > 170 && mean.R < 90,
                resourcePath + " means RGB " + mean.R.ToString("F0") + ","
                + mean.G.ToString("F0") + "," + mean.B.ToString("F0")
                + ", which is not the pack press blue");
        }

        /// <summary>Every pad names its click by the layout own convention,
        /// because that naming is what the view hover and click-to-record
        /// resolve through. A pad whose click cannot be found by name showed
        /// no highlight at all and bound nothing: the one-pad highlight the
        /// old code reached for is built only for the packs that ship
        /// single-touchpad art, so all three Valve pads fell through to an
        /// overlay image their layout never gave them.</summary>
        [Theory]
        [InlineData("STEAMDECK")]
        [InlineData("STEAMCONTROLLER")]
        [InlineData("STEAMCONTROLLER2")]
        public void EveryValvePadNamesItsClick(string model)
        {
            var overlays = Valve.Single(v => v.Model == model).Overlays;
            var pads = overlays.Where(o => o.ElementType == OverlayElementType.Touchpad).ToList();
            Assert.Equal(2, pads.Count);
            foreach (var pad in pads)
            {
                string want = pad.TargetName + "Click";
                var click = Assert.Single(overlays, o => o.TargetName == want);
                Assert.Equal(OverlayElementType.Button, click.ElementType);
                Assert.False(string.IsNullOrEmpty(click.ImageFile),
                    want + " has no art, so a hover on the pad would show nothing");
            }
        }

        /// <summary>A pad touch area is measured from its click art, and a
        /// coordinate off the pad is pulled onto it. The layout entry is a
        /// bounding box; the 2015 pads are discs and the 2026 pads are canted
        /// rounded squares, so a touch near a box corner drew its dot on the
        /// body next to the pad.</summary>
        [Theory]
        [InlineData("STEAMCONTROLLER", "SC_LeftTrackpad_Click.png")]
        [InlineData("STEAMCONTROLLER", "SC_RightTrackpad_Click.png")]
        [InlineData("STEAMCONTROLLER2", "SC2_LeftTouchpadClick.png")]
        [InlineData("STEAMCONTROLLER2", "SC2_RightTouchpadClick.png")]
        [InlineData("STEAMDECK", "SD_Touchpad_Click.png")]
        public void APadPullsAnOffPadTouchOntoItself(string folder, string file)
        {
            var area = PadTouchArea.Measure(
                EmbeddedBitmaps.Load("2DModels/" + folder + "/" + file));
            Assert.NotNull(area);
            Assert.True(area.Contains(0.5, 0.5), "the middle of a pad is on the pad");

            foreach (var corner in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0) })
            {
                var clamped = area.Clamp(corner.Item1, corner.Item2);
                Assert.True(area.Contains(clamped.X, clamped.Y),
                    "a corner clamped to a point that is still off the pad");
                Assert.InRange(clamped.X, 0.0, 1.0);
                Assert.InRange(clamped.Y, 0.0, 1.0);
            }
        }

        /// <summary>A coordinate already on the pad is left exactly where it
        /// was. A clamp that moved every touch would make the dot lag the
        /// finger everywhere, not just at the rim.</summary>
        [Fact]
        public void APadLeavesAnOnPadTouchAlone()
        {
            var area = PadTouchArea.Measure(
                EmbeddedBitmaps.Load("2DModels/STEAMCONTROLLER/SC_LeftTrackpad_Click.png"));
            Assert.NotNull(area);
            for (double u = 0.35; u <= 0.65; u += 0.1)
                for (double v = 0.35; v <= 0.65; v += 0.1)
                {
                    var clamped = area.Clamp(u, v);
                    Assert.Equal(u, clamped.X, 9);
                    Assert.Equal(v, clamped.Y, 9);
                }
        }

        /// <summary>The 2026 pads are CANTED, and the measured area says so on
        /// its own: its two top corners sit at visibly different heights,
        /// which no bounding box and no circle can express. This is what a
        /// rotation constant would have had to be hand-tuned to match.</summary>
        [Fact]
        public void The2026PadAreaIsCanted()
        {
            var area = PadTouchArea.Measure(
                EmbeddedBitmaps.Load("2DModels/STEAMCONTROLLER2/SC2_LeftTouchpadClick.png"));
            Assert.NotNull(area);

            double TopEdge(double u)
            {
                for (int y = 0; y < area.Height; y++)
                    if (area.Contains(u, y / (double)(area.Height - 1)))
                        return y / (double)(area.Height - 1);
                return 1.0;
            }
            double left = TopEdge(0.2), right = TopEdge(0.8);
            Assert.True(Math.Abs(left - right) > 0.02,
                "the 2026 pad top edge is level, so the area is not canted");
        }

        /// <summary>The Deck triggers light in the pack blue. The pack ships
        /// one trigger PNG per side and it is the REST silhouette in body
        /// gray, so a Deck trigger hovered or pulled filled gray while every
        /// other control on the same layout lit blue.</summary>
        [Theory]
        [InlineData("SD_L2-Active.png")]
        [InlineData("SD_R2-Active.png")]
        public void TheDeckTriggersLightInThePackBlue(string file)
            => AssertPressBlue("2DModels/STEAMDECK/" + file);

        /// <summary>The Deck rear paddle tiles light in the same blue. They
        /// were generated white, which is a color no other control on any
        /// layout uses.</summary>
        [Fact]
        public void TheDeckPaddleTileLightsInThePackBlue()
            => AssertPressBlue("2DModels/STEAMDECK/SD_CompactTile.png");

        /// <summary>Every Valve layout press sprite is the same blue, so no
        /// control on any of the three reads as a different kind of thing.
        /// Rest silhouettes and printed markings are excluded: those are body
        /// gray and neutral white on purpose.</summary>
        [Theory]
        [InlineData("STEAMDECK")]
        [InlineData("STEAMCONTROLLER")]
        [InlineData("STEAMCONTROLLER2")]
        public void EveryValvePressSpriteIsThePackBlue(string model)
        {
            var entry = Valve.Single(v => v.Model == model);
            foreach (var ov in entry.Overlays)
            {
                if (ov.ElementType == OverlayElementType.TriggerBase
                    || ov.ElementType == OverlayElementType.StickRing
                    || ov.ElementType == OverlayElementType.Decal)
                    continue;
                if (string.IsNullOrEmpty(ov.ImageFile)) continue;
                AssertPressBlue("2DModels/" + entry.Folder + "/" + ov.ImageFile);
            }
        }

        /// <summary>The 2026 carries its bumpers, its analog triggers and its
        /// four rear grips. A front elevation shows none of them, and the flow
        /// that builds this layout from Valve own drawing left all eight out,
        /// so they could be bound on the 3D model and nowhere else. They ride
        /// side tiles, the same answer the Deck rear paddles already
        /// use.</summary>
        [Theory]
        [InlineData("LeftShoulder")]
        [InlineData("RightShoulder")]
        [InlineData("LeftTrigger")]
        [InlineData("RightTrigger")]
        [InlineData("Paddle1")]
        [InlineData("Paddle2")]
        [InlineData("Paddle3")]
        [InlineData("Paddle4")]
        public void The2026CarriesTheControlsItsDrawingCannotShow(string target)
        {
            var ov = Assert.Single(SteamController2Layout.Overlays, o => o.TargetName == target);
            Assert.False(string.IsNullOrEmpty(ov.ImageFile));
            // Beside the body, never on it: a tile drawn over the shell would
            // claim touches from whatever it covered.
            bool leftColumn = ov.X + ov.Width <= 200;
            bool rightColumn = ov.X >= SteamController2Layout.BaseWidth - 200;
            Assert.True(leftColumn || rightColumn,
                target + " is on the body, not in a side column");
        }

        /// <summary>A decal is a printed marking, so it never carries a hit
        /// path: the view gives it no hit rectangle and the control it
        /// explains keeps the whole area.</summary>
        [Fact]
        public void ADecalIsNeverAHitTarget()
        {
            foreach (var entry in Valve)
                foreach (var ov in entry.Overlays)
                    if (ov.ElementType == OverlayElementType.Decal)
                        Assert.Null(ov.HitPath);
        }

        /// <summary>The 2015 prints what its front view cannot draw: the
        /// boundaries of the four wedges cut out of the left pad, and the
        /// outline of each rear grip. Without them a pad whose controls only
        /// appear while pressed reads as having none, which is how the grips
        /// looked on the web controller.</summary>
        [Theory]
        [InlineData("LeftPadZones")]
        [InlineData("LeftGripZone")]
        [InlineData("RightGripZone")]
        public void The2015PrintsItsUnseenControls(string target)
        {
            var ov = Assert.Single(SteamControllerLayout.Overlays, o => o.TargetName == target);
            Assert.Equal(OverlayElementType.Decal, ov.ElementType);
            Assert.False(string.IsNullOrEmpty(ov.ImageFile));
        }

        /// <summary>On the web controller the 2015 wedges and stand-in stick
        /// are DRAWINGS, so they get no touch zone of their own. The client
        /// builds one unified d-pad zone from every DPad overlay, and these
        /// four span the entire left pad, so a zone would be laid over the pad
        /// surface and swallow every drag on it.</summary>
        [Theory]
        [InlineData("DPadUp")]
        [InlineData("DPadDown")]
        [InlineData("DPadLeft")]
        [InlineData("DPadRight")]
        [InlineData("RightThumbRing")]
        [InlineData("RightThumbButton")]
        [InlineData("LeftTouchpadClick")]
        [InlineData("RightTouchpadClick")]
        public void TheWebLayoutYieldsThe2015DrawingsToItsPadSurfaces(string target)
        {
            string web = RepoText("PadForge.App", "Services", "WebControllerServer.cs");
            int at = web.IndexOf("[\"steamcontroller\"] = new()", StringComparison.Ordinal);
            Assert.True(at > 0);
            int end = web.IndexOf("};", at, StringComparison.Ordinal);
            string block = web.Substring(at, end - at);
            Assert.Contains("[\"" + target + "\"] = (\"none\", 0)", block);
        }

        /// <summary>The web client renders a decal and never binds one.</summary>
        [Fact]
        public void TheWebClientRendersDecalsWithoutBindingThem()
        {
            string js = RepoText("PadForge.App", "WebAssets", "js", "controller_client.js");
            Assert.Contains("ov.type === \"decal\"", js);
            string css = RepoText("PadForge.App", "WebAssets", "css", "controller.css");
            Assert.Contains("img.overlay.decal", css);
        }
    }
}
