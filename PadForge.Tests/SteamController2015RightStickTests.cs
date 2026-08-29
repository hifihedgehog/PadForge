using System;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The 2015 Steam Controller's right trackpad IS its right stick, in
    /// SDL's own words, and the model had nothing that said so.
    ///
    /// <para>A translucent stick stands on the pad's face and leans with the
    /// axes the pad already drives. It is scenery: in no click or button
    /// map, so hovering still finds the pad's four direction quarters and
    /// its click underneath.</para>
    /// </summary>
    public class SteamController2015RightStickTests
    {
        [Fact]
        public void TheRightPadCarriesAStickThatLeansWithIt()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);

            Assert.NotNull(m.RightThumbRing);
            Assert.NotEqual(new Vector3D(0, 0, 0), m.JoystickRotationPointCenterRightMillimeter);

        }

        /// <summary>A stick head is BOTH: directions around the rim and the
        /// stick BUTTON in the middle. The click registration is what gives
        /// it that middle, because TryResolveQuadrantHit hands the center to
        /// the click when a quadrant surface is also in the ClickMap and
        /// takes directions edge to edge when it is not.
        ///
        /// <para>The button is the PAD CLICK, because pressing this pad IS
        /// the right stick button on this controller in SDL's mapping and
        /// the wire carries no separate RightThumbButton. Head and pad
        /// answer as one control, which is what the hardware does.</para></summary>
        [Fact]
        public void TheHeadCarriesTheStickButtonToo()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);

            Assert.True(m.ClickMap.TryGetValue(m.RightThumbRing, out var target),
                "the stick head is in no click map, so its middle resolves a direction and the "
                + "stick button cannot be reached on it at all");
            Assert.Equal("RightTouchpadClick", target);

            // And it lights with the pad, since they are one control.
            Assert.Contains(m.RightThumbRing, m.ButtonMap["RightTouchpadClick"]);
            Assert.Contains(m.TouchpadRight, m.ButtonMap["RightTouchpadClick"]);

            // The wire has no separate right stick button to bind instead.
            Assert.True(Models2D.NintendoPreviewMap.IndexOf(
                "steam-controller", "RightThumbButton") < 0);
        }

        /// <summary>The head carries the four directions by quadrant, which
        /// is what a stick's ring does on every other model here. Without it
        /// the doughnut is inert: the pad's quarters answer a hover out at
        /// the rim while the head in the middle answers nothing, and the
        /// head is where a hand reaches for a stick.</summary>
        [Fact]
        public void TheHeadCarriesTheFourDirections()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);

            Assert.True(m.QuadrantMap.TryGetValue(m.RightThumbRing, out var targets),
                "the stick head is in no quadrant map, so hovering it resolves nothing");
            Assert.Equal(
                new[] { "RightThumbAxisYNeg", "RightThumbAxisY",
                        "RightThumbAxisXNeg", "RightThumbAxisX" },
                targets);
        }

        /// <summary>The doughnut IS the head's rim, so it leans with the
        /// stick. An earlier pass built it as a collar lying on the pad,
        /// which is not what any stick in this tree looks like: measured on
        /// the DualSense, the Xbox Series and the Switch 2 Pro, the ring
        /// mesh is the FRONTMOST slab and the click mesh is the stem and
        /// base cone behind it.</summary>
        [Fact]
        public void TheDoughnutIsPartOfTheHeadAndLeansWithIt()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            var pad = m.TouchpadSurface1;

            // One body: the ring is not a separate group left behind on the
            // pad while the stem leans.
            Assert.Single(m.RightThumbRing.Children);

            // And it is a ring: the widest part of the head sits BEHIND its
            // face, the way a dish with a raised rim measures.
            var mesh = Assert.IsType<MeshGeometry3D>(
                Assert.IsType<GeometryModel3D>(m.RightThumbRing.Children[0]).Geometry);

            double faceH = double.MinValue, widest = 0, widestH = 0;
            var center = pad.Center;
            foreach (var p in mesh.Positions)
            {
                var off = p - center;
                double h = Vector3D.DotProduct(off, pad.Normal);
                double r = (off - pad.Normal * h).Length;
                faceH = Math.Max(faceH, h);
                if (r > widest) { widest = r; widestH = h; }
            }
            Assert.True(widestH < faceH,
                $"the head is widest at its face ({widestH:F1} mm out of {faceH:F1}), so it has "
                + "no rim standing around the dish");
        }

        /// <summary>See-through, or it hides the pad's quarters and the
        /// finger dot that rides the same face.</summary>
        [Fact]
        public void TheStickIsTranslucent()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            var geo = Assert.IsType<GeometryModel3D>(m.RightThumbRing.Children[0]);
            var brush = Assert.IsType<SolidColorBrush>(
                Assert.IsType<DiffuseMaterial>(geo.Material).Brush);
            Assert.True(brush.Color.A < 0xC0,
                $"the ghost stick is {brush.Color.A}/255 opaque, which covers the pad under it");
            Assert.NotNull(geo.BackMaterial);
        }

        /// <summary>It stands ON the pad and stays on it at full deflection.
        /// The preview turns a stick about its pivot by the model's max
        /// angle, so walking that arc is what proves the pivot and the size
        /// agree with the pad they were measured from.</summary>
        [Fact]
        public void TheStickStaysOnThePadThroughItsWholeTravel()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            var pad = m.TouchpadSurface1;
            Assert.False(pad.IsEmpty);

            var pivot = m.JoystickRotationPointCenterRightMillimeter;
            double reach = pad.ExtentU / 2;

            foreach (var (ax, ay) in new[]
                     { (0.0, 0.0), (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0),
                       (0.7, 0.7), (-0.7, -0.7) })
            {
                var g = new Transform3DGroup();
                g.Children.Add(new RotateTransform3D(
                    new AxisAngleRotation3D(new Vector3D(0, 0, 1), m.JoystickMaxAngleDeg * ax),
                    new Point3D(pivot.X, pivot.Y, pivot.Z)));
                g.Children.Add(new RotateTransform3D(
                    new AxisAngleRotation3D(new Vector3D(1, 0, 0), -m.JoystickMaxAngleDeg * ay),
                    new Point3D(pivot.X, pivot.Y, pivot.Z)));

                var b = m.RightThumbRing.Bounds;
                var center = g.Transform(new Point3D(b.X + b.SizeX / 2,
                                                     b.Y + b.SizeY / 2,
                                                     b.Z + b.SizeZ / 2));

                var off = center - pad.Center;
                double u = Vector3D.DotProduct(off, pad.AxisU);
                double v = Vector3D.DotProduct(off, pad.AxisV);
                double away = Math.Sqrt(u * u + v * v);

                Assert.True(away < reach,
                    $"at ({ax:F1}, {ay:F1}) the stick's center is {away:F1} mm from the pad's, "
                    + $"which is outside its {reach:F1} mm half-width");
            }
        }

        /// <summary>The pivot sits behind the CAP the way the family's do:
        /// 17 mm on the DualSense, 19 on the Switch 2 Pro, 20 on the Xbox
        /// Series, 22 on the Xbox 360. Measured from the cap and not from
        /// the pad, because that is where those four are measured from and
        /// this stick stands proud of its pad.</summary>
        [Fact]
        public void ThePivotSitsBehindTheCapLikeEveryOtherStick()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            var pad = m.TouchpadSurface1;
            var pivot = m.JoystickRotationPointCenterRightMillimeter;

            double capFace = double.MinValue;
            foreach (var child in m.RightThumbRing.Children)
            {
                if (child is not GeometryModel3D geo || geo.Geometry is not MeshGeometry3D mesh)
                    continue;
                foreach (var p in mesh.Positions)
                    capFace = Math.Max(capFace, Vector3D.DotProduct((Vector3D)p, pad.Normal));
            }
            Assert.True(capFace > double.MinValue);

            double depth = capFace - Vector3D.DotProduct(pivot, pad.Normal);
            Assert.InRange(depth, 15.0, 24.0);
        }
    }
}
