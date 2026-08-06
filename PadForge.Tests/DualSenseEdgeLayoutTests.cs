using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Models2D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The DualSense Edge's own 2D asset set. Its four extras (the rear back
    /// buttons and the front Fn pair) are floating tiles in a side gutter,
    /// the Steam Deck L4/L5/R4/R5 treatment the Switch 2 Pro's GL/GR already
    /// follow, because none of the four has a front position this render can
    /// site: the back pair is on the rear, and the Fn pair's front position
    /// is not derivable (the Edge mesh has no Fn parts, and the only
    /// reference drawing that carries them is a different projection whose
    /// fit puts them off the body).
    ///
    /// The set is SEPARATE from the plain DualSense's for the same reason
    /// SWITCH2PRO is separate from SWITCHPRO: a pad must never render
    /// controls it has no wire for.
    /// </summary>
    public class DualSenseEdgeLayoutTests
    {
        private static readonly string[] EdgeExtras =
            { "LeftPaddle", "RightPaddle", "LeftFunction", "RightFunction" };

        [Fact]
        public void EdgeLayout_CarriesAllFourExtras()
        {
            var targets = DualSenseEdgeLayout.Overlays.Select(o => o.TargetName).ToList();
            foreach (var e in EdgeExtras)
                Assert.Single(targets, t => t == e);
        }

        /// <summary>The plain DualSense must not gain them. This is the
        /// assertion that fails if the two sets are ever merged.</summary>
        [Fact]
        public void PlainDualSenseLayout_CarriesNoneOfThem()
        {
            var targets = DualSenseLayout.Overlays.Select(o => o.TargetName).ToList();
            foreach (var e in EdgeExtras)
                Assert.DoesNotContain(e, targets);
        }

        /// <summary>Edge profiles resolve to the Edge 2D set; every other
        /// DualSense profile keeps the plain one. Both share the Edge 3D
        /// mesh, which is unchanged.</summary>
        [Theory]
        [InlineData("dualsense-edge", "DUALSENSEEDGE", "DualSenseEdge")]
        [InlineData("dualsense-edge-composite", "DUALSENSEEDGE", "DualSenseEdge")]
        [InlineData("dualsense", "DualSense", "DualSense")]
        [InlineData("dualsense-composite", "DualSense", "DualSense")]
        [InlineData("dualshock-4-v2", "DS4", "DS4")]
        public void AssetFolders_RouteEdgeToItsOwnSet(string profileId, string want2D, string want3D)
        {
            var (name2D, name3D) = HMaestroProfileCatalog.ResolveAssetFolders(
                profileId, VirtualControllerType.PlayStation);
            Assert.Equal(want2D, name2D);
            Assert.Equal(want3D, name3D);
        }

        /// <summary>The gutter is real: the Edge base is exactly the pack
        /// render plus a margin each side, and every body control shifted by
        /// that margin rather than being re-fitted against a stretched
        /// scale. Comparing a shared control proves the shift is uniform.</summary>
        [Fact]
        public void EdgeBase_IsThePackRenderPlusOneMarginEachSide()
        {
            const int bodyW = 1467;
            Assert.Equal(DualSenseLayout.BaseWidth, bodyW);
            Assert.Equal(DualSenseLayout.BaseHeight, DualSenseEdgeLayout.BaseHeight);

            int margin = (DualSenseEdgeLayout.BaseWidth - bodyW) / 2;
            Assert.True(margin > 0, "the Edge base carries no gutter");
            Assert.Equal(DualSenseEdgeLayout.BaseWidth, bodyW + 2 * margin);

            foreach (var shared in new[] { "ButtonA", "ButtonY", "DPadUp", "LeftThumbRing", "Touchpad" })
            {
                var plain = DualSenseLayout.Overlays.First(o => o.TargetName == shared);
                var edge = DualSenseEdgeLayout.Overlays.First(o => o.TargetName == shared);
                Assert.Equal(plain.X + margin, edge.X);
                Assert.Equal(plain.Y, edge.Y);
                Assert.Equal(plain.Width, edge.Width);
                Assert.Equal(plain.Height, edge.Height);
            }
        }

        /// <summary>The tiles sit in the gutter, clear of the body, two per
        /// side. A tile overlapping the pad would read as a real control.</summary>
        [Fact]
        public void Tiles_SitInTheGutterTwoPerSide()
        {
            int margin = (DualSenseEdgeLayout.BaseWidth - 1467) / 2;
            var tiles = DualSenseEdgeLayout.Overlays
                .Where(o => EdgeExtras.Contains(o.TargetName)).ToList();
            Assert.Equal(4, tiles.Count);

            var left = tiles.Where(t => t.X < margin).ToList();
            var right = tiles.Where(t => t.X >= DualSenseEdgeLayout.BaseWidth - margin).ToList();
            Assert.Equal(2, left.Count);
            Assert.Equal(2, right.Count);

            foreach (var t in left)
                Assert.True(t.X + t.Width <= margin,
                    $"{t.TargetName} overlaps the body (ends at {t.X + t.Width}, gutter is {margin})");
            foreach (var t in right)
                Assert.True(t.X >= DualSenseEdgeLayout.BaseWidth - margin,
                    $"{t.TargetName} overlaps the body");

            // Back above Fn on each side, matching the mapping grid's rows.
            Assert.True(tiles.First(t => t.TargetName == "LeftPaddle").Y
                      < tiles.First(t => t.TargetName == "LeftFunction").Y);
            Assert.True(tiles.First(t => t.TargetName == "RightPaddle").Y
                      < tiles.First(t => t.TargetName == "RightFunction").Y);
        }

        /// <summary>Every Edge overlay stays inside its canvas.</summary>
        [Fact]
        public void EveryOverlay_IsInBounds()
        {
            foreach (var o in DualSenseEdgeLayout.Overlays)
            {
                Assert.True(o.X >= 0 && o.Y >= 0,
                    $"{o.TargetName} at ({o.X},{o.Y})");
                Assert.True(o.X + o.Width <= DualSenseEdgeLayout.BaseWidth,
                    $"{o.TargetName} runs past the right edge");
                Assert.True(o.Y + o.Height <= DualSenseEdgeLayout.BaseHeight,
                    $"{o.TargetName} runs past the bottom edge");
            }
        }
    }
}
