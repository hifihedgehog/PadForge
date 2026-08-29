using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Each control's mesh holds that control and nothing else.
    ///
    /// <para>The 2026 Steam Controller shipped breaking this twice over, and
    /// both showed up as a highlight going to the wrong place. Its trackpad
    /// meshes carried fragments sitting on the REAR PADDLES, 737 vertices
    /// over R4 and R5 and 169 over L4 and L5, so hovering a trackpad lit
    /// part of the back. Its shell carried the bottom paddles' engraved
    /// lettering, 1707 triangles wholly inside R5 and L5, so those glyphs
    /// stayed shell-colored while the paddle lit.</para>
    ///
    /// <para>Only the first is expressible as a cheap invariant, and it is
    /// the one here. Bounding-box containment cannot state the second: a
    /// paddle's well wall wraps its cap and lands inside its box, reaching
    /// within 0.7 mm of the cap's outer face, so no box or depth rule
    /// separates a well from lettering. The check for THAT is a connected
    /// component scan, which tools/steam_controller_2026_pads.py performs
    /// and which belongs in a mesh audit rather than in every test
    /// run.</para>
    /// </summary>
    public class MeshOwnershipTests
    {
        public static TheoryData<string> ValveModels => new()
            { "SteamController2", "SteamDeck", "SteamController" };

        /// <summary>A trackpad's mesh is on the FRONT. Anything of it past
        /// the body's own mid-depth belongs to something on the back, and on
        /// the 2026 that was the rear paddles.
        ///
        /// <para>Mid-depth comes from MainBody rather than from y = 0: the
        /// origin is not the mid-plane on every model. The Steam Deck's body
        /// runs -4.5 to 37.6 mm, so its front face sits near zero and a pad
        /// 1 mm behind it is still on the front.</para></summary>
        [Theory]
        [MemberData(nameof(ValveModels))]
        public void ATrackpadMeshStaysOnTheFront(string family)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            var body = m.MainBody.Bounds;
            double mid = body.Y + body.SizeY / 2;

            foreach (var (pad, side) in new[]
                     { (m.Touchpad, "left"), (m.TouchpadRight, "right") })
            {
                if (pad == null) continue;
                var b = pad.Bounds;
                Assert.True(b.Y + b.SizeY <= mid,
                    $"{family}: the {side} trackpad's mesh reaches {b.Y + b.SizeY - mid:F1} mm "
                    + "past the body's mid-depth, so it carries geometry from the back");
            }
        }

        private static bool Inside(Rect3D b, Point3D p)
            => p.X >= b.X && p.X <= b.X + b.SizeX
            && p.Y >= b.Y && p.Y <= b.Y + b.SizeY
            && p.Z >= b.Z && p.Z <= b.Z + b.SizeZ;

        private static List<(Point3D, Point3D, Point3D)> Triangles(Model3DGroup g)
        {
            var tris = new List<(Point3D, Point3D, Point3D)>();
            if (g == null) return tris;
            foreach (var child in g.Children)
            {
                if (child is not GeometryModel3D geo || geo.Geometry is not MeshGeometry3D mesh)
                    continue;
                var p = mesh.Positions;
                var idx = mesh.TriangleIndices;
                for (int i = 0; i + 2 < idx.Count; i += 3)
                    tris.Add((p[idx[i]], p[idx[i + 1]], p[idx[i + 2]]));
            }
            return tris;
        }
    }
}
