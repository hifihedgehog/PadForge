using System;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Models2D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The three Valve profiles that were held back for want of artwork are
    /// now offered, and these tests are the reason it is safe to offer them.
    ///
    /// <para>The hold was never about code. HIDMaestro.Core has carried
    /// steam-deck-composite, steam-controller-composite and
    /// steam-controller-2 since 1.7.0.0, and all three worked; what was
    /// missing was a body to draw them as. Selecting one produced a working
    /// device rendered as an Xbox 360 pad, so the catalog hid them.</para>
    ///
    /// <para>What replaced the hold: the Steam Deck mesh from Handheld
    /// Companion, and both Steam Controller meshes converted from Valve's
    /// own published CAD, plus a 2026 two-dimensional set built from Valve's
    /// reference drawing. These tests assert the whole chain, from a profile
    /// id to files that exist on disk, so a future rename cannot quietly put
    /// an Xbox body back under a Valve profile.</para>
    /// </summary>
    public class ValveProfileArtworkTests
    {
        public static readonly string[] Released =
        {
            "steam-deck-composite",
            "steam-controller-composite",
            "steam-controller-2",
        };

        private static string AppDir()
        {
            var d = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && d != null; i++)
            {
                var c = Path.Combine(d, "PadForge.App");
                if (Directory.Exists(Path.Combine(c, "2DModels"))) return c;
                d = Path.GetDirectoryName(d);
            }
            return null;
        }

        /// <summary>Nothing is withheld any more. The gate stays in the code
        /// as a mechanism, but it must be empty, because an id left in it
        /// would silently vanish from every picker.</summary>
        [Fact]
        public void NoProfileIsWithheld()
        {
            foreach (var id in Released)
                Assert.False(HMaestroProfileCatalog.IsWithheldProfile(id));

            Assert.All(HMaestroProfileCatalog.AllProfiles,
                p => Assert.False(HMaestroProfileCatalog.IsWithheldProfile(p.Id)));
        }

        /// <summary>The released profiles reach the buckets a user picks
        /// from. Absent from AllProfiles they would still resolve by id and
        /// still be unreachable, which is what the hold did.</summary>
        [Theory]
        [InlineData("steam-deck-composite")]
        [InlineData("steam-controller-composite")]
        [InlineData("steam-controller-2")]
        [InlineData("steam-controller")]
        [InlineData("steam-deck")]
        public void ValveProfilesAreOffered(string id)
        {
            Assert.Contains(HMaestroProfileCatalog.AllProfiles, p => p.Id == id);
            Assert.Contains(HMaestroProfileCatalog.ExtendedProfiles, p => p.Id == id);
        }

        /// <summary>Each Valve profile resolves to its OWN art, never to a
        /// fallback. The fallback is an Xbox 360 pad, so a resolver that
        /// stopped matching would fail silently in the one way that matters:
        /// the app would still draw a controller, just the wrong one.</summary>
        [Theory]
        [InlineData("steam-controller", "STEAMCONTROLLER", "SteamController")]
        [InlineData("steam-controller-composite", "STEAMCONTROLLER", "SteamController")]
        [InlineData("steam-controller-2", "STEAMCONTROLLER2", "SteamController2")]
        [InlineData("steam-deck", "STEAMDECK", "SteamDeck")]
        [InlineData("steam-deck-composite", "STEAMDECK", "SteamDeck")]
        public void EachValveProfileResolvesToItsOwnArt(string id, string want2D, string want3D)
        {
            var (name2D, name3D) = HMaestroProfileCatalog.ResolveAssetFolders(
                id, VirtualControllerType.Xbox);
            Assert.Equal(want2D, name2D);
            Assert.Equal(want3D, name3D);
        }

        /// <summary>The 2015 pad and the 2026 pad must NOT share art. They
        /// differ by a whole stick, a D-pad and the shape of the trackpads,
        /// so one drawn as the other is wrong in a way a user would see.
        /// This is the same split SWITCH2PRO needed against SWITCHPRO.</summary>
        [Fact]
        public void TheTwoSteamControllerGenerationsDoNotShareArt()
        {
            var older = HMaestroProfileCatalog.ResolveAssetFolders(
                "steam-controller", VirtualControllerType.Xbox);
            var newer = HMaestroProfileCatalog.ResolveAssetFolders(
                "steam-controller-2", VirtualControllerType.Xbox);
            Assert.NotEqual(older.Name2D, newer.Name2D);
            Assert.NotEqual(older.Name3D, newer.Name3D);
        }

        /// <summary>Every folder a Valve profile resolves to actually exists
        /// and holds meshes. A resolver pointing at an empty directory
        /// throws on the first LoadModel, which is a crash rather than a
        /// wrong picture.</summary>
        [Theory]
        [InlineData("SteamController")]
        [InlineData("SteamController2")]
        [InlineData("SteamDeck")]
        public void EveryResolved3DFolderShipsMeshes(string folder)
        {
            var app = AppDir();
            Assert.NotNull(app);
            var dir = Path.Combine(app, "3DModels", folder);
            Assert.True(Directory.Exists(dir), $"missing 3D folder {folder}");
            Assert.True(File.Exists(Path.Combine(dir, "MainBody.obj")),
                $"{folder} has no MainBody.obj, which ControllerModelBase requires");
        }

        /// <summary>The 2026 layout covers the controls the pad actually
        /// has. It is the only Valve layout built from a CAD drawing rather
        /// than from pack art, so a silhouette the classifier failed to name
        /// would show up here as a missing target rather than as a control
        /// the user cannot bind.</summary>
        [Theory]
        [InlineData("ButtonA")]
        [InlineData("ButtonB")]
        [InlineData("ButtonX")]
        [InlineData("ButtonY")]
        [InlineData("DPadUp")]
        [InlineData("DPadDown")]
        [InlineData("DPadLeft")]
        [InlineData("DPadRight")]
        [InlineData("ButtonBack")]
        [InlineData("ButtonStart")]
        [InlineData("ButtonGuide")]
        [InlineData("ButtonQuickAccess")]
        [InlineData("LeftThumbRing")]
        [InlineData("RightThumbRing")]
        [InlineData("LeftThumbButton")]
        [InlineData("RightThumbButton")]
        [InlineData("LeftTouchpad")]
        [InlineData("RightTouchpad")]
        [InlineData("LeftTouchpadClick")]
        [InlineData("RightTouchpadClick")]
        public void The2026LayoutCarriesTheControl(string target)
        {
            Assert.Contains(SteamController2Layout.Overlays, o => o.TargetName == target);
        }

        /// <summary>The 2026 pad has TWO sticks. The generation before it
        /// had one, and the layout was built by classifying shapes in a
        /// drawing, so a classifier that paired them wrongly would leave
        /// this pad one stick short exactly like its predecessor.</summary>
        [Fact]
        public void The2026LayoutHasBothSticks()
        {
            Assert.Equal(2, SteamController2Layout.Overlays
                .Count(o => o.ElementType == OverlayElementType.StickRing));
            Assert.Equal(2, SteamController2Layout.Overlays
                .Count(o => o.ElementType == OverlayElementType.StickClick));
            Assert.Single(SteamControllerLayout.Overlays,
                o => o.ElementType == OverlayElementType.StickRing);
        }
    }
}
