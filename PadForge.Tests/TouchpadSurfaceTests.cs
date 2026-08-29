using System;
using System.Windows.Media.Media3D;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The 3D preview's finger spheres need a surface to ride, and every
    /// Valve pad has TWO.
    ///
    /// <para>BuildTouchpadFingerVisuals returns early on a model with no
    /// Touchpad group, so a pad that never set one showed no touch point at
    /// all. Only the DS4 and the DualSense set it, which is why the Steam
    /// Deck, the 2015 Steam Controller and the 2026 Steam Controller drew
    /// nothing while their 2D dots worked.</para>
    /// </summary>
    public class TouchpadSurfaceTests
    {
        public static TheoryData<string> TwoPadModels => new()
            { "SteamDeck", "SteamController", "SteamController2" };

        [Theory]
        [MemberData(nameof(TwoPadModels))]
        public void ATwoPadModelGivesEachFingerItsOwnSurface(string family)
        {
            using var m = ControllerModelBase.Create(family, null, false);

            Assert.NotNull(m.Touchpad);
            Assert.NotNull(m.TouchpadRight);
            Assert.NotSame(m.Touchpad, m.TouchpadRight);

            var a0 = m.TouchpadArea0;
            var a1 = m.TouchpadArea1;
            Assert.False(a0.IsEmpty);
            Assert.False(a1.IsEmpty);

            // Mirror images across the body's centerline, same size. The
            // tolerance is in MILLIMETRES rather than decimal places: the
            // two meshes agree to a thousandth and 36.8505 against 36.8496
            // still rounds apart at one place.
            Assert.True(Math.Abs(a0.SizeX - a1.SizeX) < 0.05,
                $"{family}: the two pads measure {a0.SizeX:F3} and {a1.SizeX:F3} across");
            Assert.True(Math.Abs(a0.SizeZ - a1.SizeZ) < 0.05,
                $"{family}: the two pads measure {a0.SizeZ:F3} and {a1.SizeZ:F3} tall");
            double c0 = a0.X + a0.SizeX / 2, c1 = a1.X + a1.SizeX / 2;
            Assert.True(c0 < 0 && c1 > 0,
                $"{family}: finger 0 must ride the LEFT pad and finger 1 the right, "
                + $"and their centers are at {c0:F1} and {c1:F1}");
            Assert.True(Math.Abs(Math.Abs(c0) - Math.Abs(c1)) < 0.05,
                $"{family}: the pads sit at {c0:F2} and {c1:F2}, which is not a mirror");
        }

        /// <summary>The 2015 pad is four direction quarters around a center
        /// disc, and the disc is 16.9 mm against a 42 mm pad. Riding the disc
        /// alone would pen the dot into the middle 40% of the pad.</summary>
        [Fact]
        public void TheSteamController2015SurfaceSpansTheWholePadNotItsCenter()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);

            double disc = m.Touchpad.Bounds.SizeX;
            double pad = m.TouchpadArea0.SizeX;
            Assert.True(pad > disc * 2,
                $"the touch area is {pad:F1} mm across a {disc:F1} mm center disc, so it is "
                + "still the disc rather than the pad");
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
            Assert.Equal(m.TouchpadArea0, m.TouchpadArea1);
        }

        /// <summary>The touch area is proud of the SHELL AT THE PAD, which
        /// is what keeps the finger dot outside the controller. Comparing
        /// against the body's global front does not work: the grips and the
        /// sticks reach further forward than any pad, so a pad legitimately
        /// sits behind that. Cast a ray down the pad's own axis
        /// instead.</summary>
        [Theory]
        [MemberData(nameof(TwoPadModels))]
        public void TheTouchAreaIsProudOfTheShellUnderIt(string family)
        {
            using var m = ControllerModelBase.Create(family, null, false);
            foreach (var (area, side) in new[]
                     { (m.TouchpadArea0, "left"), (m.TouchpadArea1, "right") })
            {
                double ax = area.X + area.SizeX / 2, az = area.Z + area.SizeZ / 2;
                double shell = NearestSurfaceY(m.MainBody, ax, az);
                Assert.True(double.IsNaN(shell) || area.Y <= shell + 0.5,
                    $"{family}: the {side} touch area starts {area.Y - shell:F1} mm behind the "
                    + "shell directly under it, so the finger dot would sit inside the pad");
            }
        }

        /// <summary>The frontmost Y of a group's geometry directly over
        /// (x, z), or NaN when nothing covers that point. -Y is out of the
        /// controller's face, so the smallest Y is the nearest surface.
        /// Point-in-triangle, because a hole is a property of the TRIANGLES:
        /// the triangle over the Steam Deck's stick axis spans 17 mm with
        /// every corner outside the well.</summary>
        private static double NearestSurfaceY(Model3DGroup group, double x, double z)
        {
            double best = double.NaN;
            foreach (var child in group.Children)
            {
                if (child is not GeometryModel3D geo || geo.Geometry is not MeshGeometry3D mesh)
                    continue;
                var p = mesh.Positions;
                var idx = mesh.TriangleIndices;
                for (int i = 0; i + 2 < idx.Count; i += 3)
                {
                    Point3D a = p[idx[i]], b = p[idx[i + 1]], c = p[idx[i + 2]];
                    double det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
                    if (Math.Abs(det) < 1e-12) continue;
                    double w0 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / det;
                    double w1 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / det;
                    double w2 = 1 - w0 - w1;
                    if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                    double y = w0 * a.Y + w1 * b.Y + w2 * c.Y;
                    if (double.IsNaN(best) || y < best) best = y;
                }
            }
            return best;
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
