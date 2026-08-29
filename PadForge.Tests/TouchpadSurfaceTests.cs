using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The 3D preview's finger dot has to land ON a pad and INSIDE it.
    ///
    /// <para>Two defects, both from describing a pad with an axis-aligned
    /// box. BuildTouchpadFingerVisuals returns early on a model with no
    /// Touchpad group, and only the DS4 and the DualSense ever set one, so
    /// no Steam controller drew a dot at all. Then the first fix mapped the
    /// dot across the pad group's world-space bounds, and every Valve pad is
    /// canted: the 2026's face is 15 degrees off the controller's front and
    /// the 2015's 19. A point on the box sits beside the pad rather than on
    /// it, and the box's corners reach past the outline.</para>
    ///
    /// <para>TouchSurface fits the face itself, so these check the fit and
    /// then check the result the only way that settles it: cast a ray back
    /// at the pad from where the dot would go, and require it to hit.</para>
    /// </summary>
    public class TouchpadSurfaceTests
    {
        public static TheoryData<string> TwoPadModels => new()
            { "SteamDeck", "SteamController", "SteamController2" };

        public static TheoryData<string> EveryTouchModel => new()
            { "SteamDeck", "SteamController", "SteamController2", "DualSense", "DS4" };

        [Theory]
        [MemberData(nameof(TwoPadModels))]
        public void ATwoPadModelGivesEachFingerItsOwnSurface(string family)
        {
            using var m = ControllerModelBase.Create(family, null, false);

            Assert.NotNull(m.Touchpad);
            Assert.NotNull(m.TouchpadRight);
            Assert.NotSame(m.Touchpad, m.TouchpadRight);

            var s0 = m.TouchpadSurface0;
            var s1 = m.TouchpadSurface1;
            Assert.False(s0.IsEmpty);
            Assert.False(s1.IsEmpty);

            // Mirror images across the body's centerline. The tolerance is in
            // MILLIMETRES: two mirrored meshes agree to a thousandth, and
            // 36.8505 against 36.8496 still rounds apart at one place.
            Assert.True(Math.Abs(s0.ExtentU - s1.ExtentU) < 0.6,
                $"{family}: the pads measure {s0.ExtentU:F2} and {s1.ExtentU:F2} across");
            Assert.True(Math.Abs(s0.ExtentV - s1.ExtentV) < 0.6,
                $"{family}: the pads measure {s0.ExtentV:F2} and {s1.ExtentV:F2} up");
            Assert.True(s0.Center.X < 0 && s1.Center.X > 0,
                $"{family}: finger 0 must ride the LEFT pad and finger 1 the right, and their "
                + $"centers are at {s0.Center.X:F1} and {s1.Center.X:F1}");
        }

        /// <summary>The axes are built from the normal, never fitted, so
        /// they cannot come out arbitrary: U points to the controller's
        /// right and V to its top, which is what makes a touch report's
        /// x = 0 the pad's left edge on every model.</summary>
        [Theory]
        [MemberData(nameof(EveryTouchModel))]
        public void TheSurfaceAxesPointRightAndUp(string family)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            foreach (var (s, side) in new[]
                     { (m.TouchpadSurface0, "left"), (m.TouchpadSurface1, "right") })
            {
                Assert.False(s.IsEmpty);
                Assert.True(s.AxisU.X > 0.5, $"{family}: the {side} pad's U axis is not rightward");
                Assert.True(s.AxisV.Z > 0.5, $"{family}: the {side} pad's V axis is not upward");
                Assert.True(s.Normal.Y < -0.5,
                    $"{family}: the {side} pad's normal points into the controller");
                Assert.Equal(1.0, s.Normal.Length, 3);
            }
        }

        /// <summary>THE PROPERTY: a dot placed anywhere on the pad lands on
        /// the pad. Cast a ray back down the surface normal from where the
        /// preview would put it, and require a hit on the pad's own
        /// geometry. A point off the outline misses entirely, which is the
        /// "beyond" half, and a point on the wrong plane hits at the wrong
        /// distance or not at all, which is the "not on" half.</summary>
        [Theory]
        [MemberData(nameof(EveryTouchModel))]
        public void EveryPointOnAPadLandsOnThePad(string family)
        {
            using var m = ControllerModelBase.Create(family, null, false);

            // Cast against the SAME parts the surface was fitted from. The
            // 2015's pad is its four quarters plus a center disc, and a dot
            // at a fifth of the way in is over a quarter, not the disc.
            Check(m.TouchpadSurface0, m.TouchParts0, "left");
            Check(m.TouchpadSurface1, m.TouchParts1, "right");

            void Check(ControllerModelBase.TouchSurface s, Model3DGroup[] parts, string side)
            {
                Assert.False(s.IsEmpty);
                var tris = Triangles(parts);
                Assert.NotEmpty(tris);

                // The interior, plus the four edge midpoints. Not the corners:
                // a pad is a rounded square, so its bounding rectangle's
                // corners are off the surface by design, and a real touch
                // report never reaches them.
                var probes = new List<(double u, double v)>();
                foreach (double u in new[] { 0.2, 0.4, 0.5, 0.6, 0.8 })
                    foreach (double v in new[] { 0.2, 0.4, 0.5, 0.6, 0.8 })
                        probes.Add((u, v));
                probes.Add((0.05, 0.5)); probes.Add((0.95, 0.5));
                probes.Add((0.5, 0.05)); probes.Add((0.5, 0.95));

                foreach (var (u, v) in probes)
                {
                    var at = s.At(u, v, 1.5);
                    double hit = RayDown(tris, at, -s.Normal);
                    Assert.True(!double.IsNaN(hit),
                        $"{family}: the {side} pad has no surface under ({u:F2}, {v:F2}), so the "
                        + "dot would float beyond the pad");
                    Assert.True(hit < 6.0,
                        $"{family}: the {side} pad's surface at ({u:F2}, {v:F2}) is {hit:F1} mm "
                        + "from the dot, so the dot is not sitting on it");
                }
            }
        }

        private static List<(Point3D A, Point3D B, Point3D C)> Triangles(Model3DGroup[] parts)
        {
            var tris = new List<(Point3D, Point3D, Point3D)>();
            foreach (var g in parts)
            {
                if (g == null) continue;
                foreach (var child in g.Children)
                {
                    if (child is not GeometryModel3D geo || geo.Geometry is not MeshGeometry3D mesh)
                        continue;
                    var p = mesh.Positions;
                    var idx = mesh.TriangleIndices;
                    for (int i = 0; i + 2 < idx.Count; i += 3)
                        tris.Add((p[idx[i]], p[idx[i + 1]], p[idx[i + 2]]));
                }
            }
            return tris;
        }

        /// <summary>Distance from origin to the nearest triangle along dir,
        /// or NaN when the ray misses everything. Moller-Trumbore, two
        /// sided, because a pad's winding is not ours to assume.</summary>
        private static double RayDown(
            List<(Point3D A, Point3D B, Point3D C)> tris, Point3D origin, Vector3D dir)
        {
            double best = double.NaN;
            foreach (var (a, b, c) in tris)
            {
                Vector3D e1 = b - a, e2 = c - a;
                var h = Vector3D.CrossProduct(dir, e2);
                double det = Vector3D.DotProduct(e1, h);
                if (Math.Abs(det) < 1e-12) continue;
                double inv = 1.0 / det;
                var s = origin - a;
                double u = inv * Vector3D.DotProduct(s, h);
                if (u < -1e-9 || u > 1 + 1e-9) continue;
                var q = Vector3D.CrossProduct(s, e1);
                double v = inv * Vector3D.DotProduct(dir, q);
                if (v < -1e-9 || u + v > 1 + 1e-9) continue;
                double d = inv * Vector3D.DotProduct(e2, q);
                if (d <= 1e-6) continue;
                if (double.IsNaN(best) || d < best) best = d;
            }
            return best;
        }

        /// <summary>The 2015 pad is four direction quarters around a center
        /// disc, and the disc is 16.9 mm against a 42 mm pad. Fitting the
        /// disc alone would pen the dot into the middle 40%.</summary>
        [Fact]
        public void TheSteamController2015SurfaceSpansTheWholePadNotItsCenter()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            double disc = m.Touchpad.Bounds.SizeX;
            double pad = m.TouchpadSurface0.ExtentU;
            Assert.True(pad > disc * 2,
                $"the touch face is {pad:F1} mm across a {disc:F1} mm center disc, so it is still "
                + "the disc rather than the pad");
        }

        /// <summary>A one-pad model is unchanged: both fingers ride the one
        /// surface, which is what a DualSense reports.</summary>
        [Theory]
        [InlineData("DualSense")]
        [InlineData("DS4")]
        public void AOnePadModelPutsBothFingersOnTheSameSurface(string family)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            Assert.NotNull(m.Touchpad);
            Assert.Null(m.TouchpadRight);
            Assert.Equal(m.TouchpadSurface0.Center, m.TouchpadSurface1.Center);
            Assert.Equal(m.TouchpadSurface0.ExtentU, m.TouchpadSurface1.ExtentU, 3);
        }

        /// <summary>The 2D layout carries the same pair, and by the same
        /// names. The dots there were gated on the ONE-PAD click sprite, so
        /// no Steam controller drew a touch point in either preview even
        /// though the layout had both pads all along.</summary>
        public static TheoryData<PadForge.Models2D.OverlayElement[]> TwoPadLayouts => new()
        {
            PadForge.Models2D.SteamDeckLayout.Overlays,
            PadForge.Models2D.SteamControllerLayout.Overlays,
            PadForge.Models2D.SteamController2Layout.Overlays,
        };

        [Theory]
        [MemberData(nameof(TwoPadLayouts))]
        public void TheTwoDLayoutDeclaresBothPads(PadForge.Models2D.OverlayElement[] overlays)
        {
            bool left = false, right = false;
            foreach (var ov in overlays)
            {
                if (ov.ElementType != PadForge.Models2D.OverlayElementType.Touchpad) continue;
                if (ov.TargetName == "LeftTouchpad") left = true;
                if (ov.TargetName == "RightTouchpad") right = true;
            }
            Assert.True(left, "a two-pad layout has no LeftTouchpad overlay for finger 0");
            Assert.True(right, "a two-pad layout has no RightTouchpad overlay for finger 1");
        }
    }
}
