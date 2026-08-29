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

            // Scenery. A click or button entry would steal the pad's hover.
            Assert.False(m.ClickMap.ContainsKey(m.RightThumbRing));
            foreach (var kv in m.ButtonMap)
                Assert.DoesNotContain(m.RightThumbRing, kv.Value);
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

        /// <summary>The pivot sits behind the pad the way the family's do:
        /// 17 mm on the DualSense, 19 on the Switch 2 Pro, 20 on the Xbox
        /// Series, 22 on the Xbox 360.</summary>
        [Fact]
        public void ThePivotSitsBehindThePadLikeEveryOtherStick()
        {
            using var m = ControllerModelBase.Create("SteamController", null, false);
            var pad = m.TouchpadSurface1;
            var pivot = m.JoystickRotationPointCenterRightMillimeter;

            double depth = Vector3D.DotProduct(
                pad.Center - (Point3D)pivot, pad.Normal);
            Assert.InRange(depth, 12.0, 26.0);
        }
    }
}
