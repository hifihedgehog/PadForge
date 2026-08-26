using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Models3D;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The route from a slot's profile to a model in the viewport: the
    /// profile picks a mesh family, and the model built from it has to
    /// arrive with geometry the camera can frame.
    ///
    /// <para>EnsureModel swallows any failure into Debug.WriteLine, so a
    /// profile whose model does not build leaves the preview blank with no
    /// other symptom at all. This walks the same route it does.</para>
    /// </summary>
    public class ControllerModelViewportTests
    {
        private readonly ITestOutputHelper _out;
        public ControllerModelViewportTests(ITestOutputHelper o) => _out = o;

        public static IEnumerable<object[]> Profiles() => new[]
        {
            new object[] { "xbox-360", VirtualControllerType.Xbox, "XBOX360" },
            new object[] { "xbox-series-composite", VirtualControllerType.Xbox, "XboxSeries" },
            new object[] { "dualshock-4-v2-composite", VirtualControllerType.PlayStation, "DS4" },
            new object[] { "dualsense-composite", VirtualControllerType.PlayStation, "DualSense" },
            new object[] { "dualsense-edge-composite", VirtualControllerType.PlayStation, "DualSenseEdge" },
            new object[] { "switch-pro", VirtualControllerType.Nintendo, "Switch2Pro" },
            new object[] { "switch2-pro", VirtualControllerType.Nintendo, "Switch2Pro" },
            new object[] { "steam-deck-composite", VirtualControllerType.Extended, "SteamDeck" },
            new object[] { "steam-controller", VirtualControllerType.Extended, "SteamController" },
            new object[] { "steam-controller-composite", VirtualControllerType.Extended, "SteamController" },
            new object[] { "steam-controller-2", VirtualControllerType.Extended, "SteamController2" },
        };

        /// <summary>The profile picks the mesh family the viewport builds.</summary>
        [Theory]
        [MemberData(nameof(Profiles))]
        public void ProfileRoutesToItsMeshFamily(string profileId, VirtualControllerType type, string wantFamily)
        {
            var (_, needed) = HMaestroProfileCatalog.ResolveAssetFolders(profileId, type);
            Assert.Equal(wantFamily, needed);
        }

        /// <summary>And the model that family names builds, carries geometry,
        /// and has a scale the camera can frame. A blank preview is what the
        /// user sees when any of the three fails.</summary>
        [Theory]
        [MemberData(nameof(Profiles))]
        public void EveryProfileBuildsAModelWithGeometry(string profileId, VirtualControllerType type, string wantFamily)
        {
            var (_, needed) = HMaestroProfileCatalog.ResolveAssetFolders(profileId, type);
            bool extra = needed == "Switch2Pro"
                ? PadForge.Models2D.NintendoPreviewMap.IndexOf(profileId, "ButtonC") >= 0
                : needed == "XboxSeries" && profileId.StartsWith("xbox-series-", StringComparison.OrdinalIgnoreCase);

            using var m = ControllerModelBase.Create(needed, null, extra);
            Assert.Equal(needed, m.ModelFamily);

            var bounds = m.model3DGroup.Bounds;
            Assert.False(bounds.IsEmpty, $"{profileId}: the model has no bounds, so nothing would draw");
            Assert.True(bounds.SizeX > 1 && bounds.SizeY > 1 && bounds.SizeZ > 1,
                $"{profileId}: degenerate bounds {bounds}");
            Assert.True(m.ModelScale > 0.1 && m.ModelScale < 10,
                $"{profileId}: scale {m.ModelScale} would put the model off camera");

            int geo = m.model3DGroup.Children.OfType<Model3DGroup>()
                .Sum(g => g.Children.OfType<GeometryModel3D>().Count());
            Assert.True(geo > 0, $"{profileId}: no geometry in the scene");
            _out.WriteLine($"{profileId} -> {needed}: {geo} meshes, "
                + $"{bounds.SizeX:F1} x {bounds.SizeY:F1} x {bounds.SizeZ:F1} mm, scale {m.ModelScale:F3}");
        }
    }
}
