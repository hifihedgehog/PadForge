using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using PadForge.Models2D;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Contracts the 2026-08-06 audit round established. Each test names the
    /// gap it closes, because every one of them was a control the engine
    /// drove end to end while no preview surface could show it.
    /// </summary>
    public class MutePreviewAndAuditFixTests
    {
        private static string RepoRoot([CallerFilePath] string me = null)
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(me), ".."));

        // ── The mic-mute preview leg ────────────────────────────────────

        /// <summary>Both DualSense 2D sets draw the mute press. The button
        /// reached the wire, the grid and the packer while
        /// SetOverlayVisible("ButtonMute") addressed an overlay no layout
        /// declared, so pressing it lit nothing.</summary>
        [Theory]
        [InlineData("DualSense")]
        [InlineData("DUALSENSEEDGE")]
        public void MuteOverlay_ExistsInBothDualSenseLayouts(string folder)
        {
            var overlays = folder == "DualSense"
                ? DualSenseLayout.Overlays : DualSenseEdgeLayout.Overlays;
            var mute = Assert.Single(overlays, o => o.TargetName == "ButtonMute");
            Assert.Equal(OverlayElementType.Button, mute.ElementType);
            Assert.False(string.IsNullOrEmpty(mute.ImageFile));
            Assert.True(File.Exists(Path.Combine(
                RepoRoot(), "PadForge.App", "2DModels", folder, mute.ImageFile)),
                $"{folder}/{mute.ImageFile} is missing");
        }

        /// <summary>The Edge's mute sits at the plain pad's position plus the
        /// gutter margin, like every other shared control in that set.</summary>
        [Fact]
        public void EdgeMute_IsTheMarginShiftedPlainMute()
        {
            int margin = (DualSenseEdgeLayout.BaseWidth - DualSenseLayout.BaseWidth) / 2;
            var plain = DualSenseLayout.Overlays.First(o => o.TargetName == "ButtonMute");
            var edge = DualSenseEdgeLayout.Overlays.First(o => o.TargetName == "ButtonMute");
            Assert.Equal(plain.X + margin, edge.X);
            Assert.Equal(plain.Y, edge.Y);
            Assert.Equal(plain.Width, edge.Width);
            Assert.Equal(plain.Height, edge.Height);
        }

        /// <summary>Every DualSense-family 3D model registers the mute mesh.
        /// It is the translucent capsule welded into the clear-plastic part,
        /// so it had no group of its own to hover, click or light.</summary>
        [Theory]
        [InlineData("White")]
        [InlineData("Midnight")]
        [InlineData("CosmicRed")]
        public void MuteMesh_IsRegisteredOnEveryDualSenseColorway(string appearance)
        {
            using var model = new ControllerModelDualSense(appearance);
            AssertMuteRegistered(model);
        }

        [Fact]
        public void MuteMesh_IsRegisteredOnTheEdge()
        {
            using var model = new ControllerModelDualSenseEdge();
            AssertMuteRegistered(model);
        }

        private static void AssertMuteRegistered(ControllerModelBase model)
        {
            Assert.True(model.ButtonMap.ContainsKey("ButtonMute"), "ButtonMute unregistered");
            var group = Assert.Single(model.ButtonMap["ButtonMute"]);
            Assert.Equal("ButtonMute", model.ClickMap[group]);          // click-to-record
            Assert.True(model.HighlightMaterials.ContainsKey(group));   // press highlight
            Assert.True(model.DefaultMaterials.ContainsKey(group));     // rest material
            // Between the sticks, below the PS logo: (0, -17.4, -8.0).
            var b = group.Bounds;
            Assert.InRange(b.X + b.SizeX / 2, -2.0, 2.0);
            Assert.InRange(b.Z + b.SizeZ / 2, -12.0, -4.0);
        }

        /// <summary>The DS4 has no mute button and must not grow one.</summary>
        [Fact]
        public void Ds4_HasNoMuteMesh()
        {
            using var model = new ControllerModelDS4("JetBlack");
            Assert.False(model.ButtonMap.ContainsKey("ButtonMute"));
        }

        // ── The Edge's touchpad preview ─────────────────────────────────

        /// <summary>Every 2D folder that declares a Touchpad element resolves
        /// a click sprite that exists. The gate used to name the two folders
        /// that had touchpads when it was written, so the Edge lost its
        /// finger dots and click highlight the day it got its own folder,
        /// and its sprite path interpolated the folder name into a file that
        /// was never there.</summary>
        [Theory]
        [InlineData("DS4")]
        [InlineData("DualSense")]
        [InlineData("DUALSENSEEDGE")]
        public void TouchpadFolders_ResolveASpriteThatExists(string folder)
        {
            string sprite = Views.ControllerModel2DView.TouchpadClickSprite(folder);
            Assert.False(string.IsNullOrEmpty(sprite), $"{folder} resolves no click sprite");
            Assert.True(File.Exists(Path.Combine(
                RepoRoot(), "PadForge.App", "2DModels", folder, sprite)),
                $"{folder}/{sprite} is missing");

            var overlays = folder switch
            {
                "DS4" => DS4Layout.Overlays,
                "DualSense" => DualSenseLayout.Overlays,
                _ => DualSenseEdgeLayout.Overlays,
            };
            Assert.Contains(overlays, o => o.ElementType == OverlayElementType.Touchpad);
        }

        [Theory]
        [InlineData("XBOXSERIES")]
        [InlineData("SWITCHPRO")]
        [InlineData("XBOX360")]
        public void NonTouchpadFolders_ResolveNoSprite(string folder)
            => Assert.Null(Views.ControllerModel2DView.TouchpadClickSprite(folder));

        // ── Persona mic reference level ─────────────────────────────────

        /// <summary>The mic feature unit's unity point is the persona's own
        /// declared maximum, not a constant. The DualSense declares +48 dB
        /// (volumeMaxRaw 12288) and the DualShock 4 declares +24 dB (6144),
        /// so a shared 48 left the DS4 virtual mic 24 dB down at its own
        /// maximum and effectively silent at its default.</summary>
        [Theory]
        [InlineData("dualsense-composite", 48.0)]
        [InlineData("dualsense-edge", 48.0)]
        [InlineData("dualsense-edge-bt", 48.0)]
        [InlineData("dualshock-4-v2-composite", 24.0)]
        [InlineData("dualshock-4-v2", 24.0)]
        [InlineData(null, 48.0)]
        public void MicUnityDb_FollowsThePersona(string profileId, double expected)
            => Assert.Equal(expected,
                Common.Input.AudioPassthroughService.MicUnityDb(profileId));

        /// <summary>At its own maximum every persona's mic must reach unity.
        /// This is the assertion the constant failed: 10^((24-48)/20) = 0.063.</summary>
        [Theory]
        [InlineData("dualshock-4-v2-composite", 24.0)]
        [InlineData("dualsense-composite", 48.0)]
        public void MicGain_ReachesUnityAtThePersonaMaximum(string profileId, double maxDb)
        {
            double unity = Common.Input.AudioPassthroughService.MicUnityDb(profileId);
            double gain = Math.Min(1.0, Math.Pow(10.0, (maxDb - unity) / 20.0));
            Assert.Equal(1.0, gain, 6);
        }
    }
}
