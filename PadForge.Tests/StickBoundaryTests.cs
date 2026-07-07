using System;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Stick boundary calibration + circular reshaping (#174). Pins the two
    /// load-bearing properties: a point ON the measured boundary at any angle
    /// warps to full deflection (the "every direction reaches the rim" promise),
    /// and an uncalibrated map is a byte-identical no-op. Also the serialize
    /// round-trip and the degenerate-map guards (no NaN, no divide-by-zero).
    /// </summary>
    public class StickBoundaryTests
    {
        private const double ShortScale = 32768.0;

        private static double Magnitude(short sx, short sy)
            => Math.Sqrt((sx / ShortScale) * (sx / ShortScale) + (sy / ShortScale) * (sy / ShortScale));

        /// <summary>Places a point exactly on a boundary map's rim at sample i,
        /// warps it, and returns the resulting magnitude.</summary>
        private static double WarpRimPoint(double[] data, double[] lut, int i)
        {
            double a = i * (Math.PI * 2.0) / data.Length;
            short sx = (short)Math.Clamp(Math.Cos(a) * data[i] * ShortScale, short.MinValue, short.MaxValue);
            short sy = (short)Math.Clamp(Math.Sin(a) * data[i] * ShortScale, short.MinValue, short.MaxValue);
            StickBoundary.Reshape(ref sx, ref sy, lut);
            return Magnitude(sx, sy);
        }

        [Fact]
        public void RoundBoundary_RimMapsToFull_HalfMapsToHalf()
        {
            // A circle of radius 0.7: dividing by 0.7 must send the rim to 1.0
            // and a half-deflection to 0.5, at every angle, exactly.
            var data = StickBoundary.NewMap();
            for (int i = 0; i < data.Length; i++) data[i] = 0.7;
            var lut = StickBoundary.GetOrBuild(StickBoundary.Serialize(data));
            Assert.NotNull(lut);

            for (int i = 0; i < data.Length; i += 30)
            {
                double rim = WarpRimPoint(data, lut, i);
                Assert.InRange(rim, 0.96, 1.001); // clamped at full, minus short quantization

                double a = i * (Math.PI * 2.0) / data.Length;
                short hx = (short)(Math.Cos(a) * 0.35 * ShortScale);
                short hy = (short)(Math.Sin(a) * 0.35 * ShortScale);
                StickBoundary.Reshape(ref hx, ref hy, lut);
                Assert.InRange(Magnitude(hx, hy), 0.48, 0.52); // half of the rim
            }
        }

        // A superellipse (n=4) squircle: the physically-real stick shape, convex,
        // cardinals ~1.0 and diagonals bulging farther (~1.19). This is what a
        // rounded-square gate actually produces (the user's own map measured
        // cardinals 1.0 / diagonals 1.11), unlike a concave pincushion which a
        // gate cannot form.
        private static double Superellipse(double angle)
        {
            double c = Math.Abs(Math.Cos(angle)), s = Math.Abs(Math.Sin(angle));
            return 0.85 / Math.Pow(c * c * c * c + s * s * s * s, 0.25);
        }

        [Fact]
        public void SquircleBoundary_EveryDirectionReachesFull()
        {
            // The point of #174: a point on the measured squircle rim, at any
            // angle, must warp to full 1.0 so every direction reaches the rim.
            var data = StickBoundary.NewMap();
            for (int i = 0; i < data.Length; i++)
                data[i] = Superellipse(i * (Math.PI * 2.0) / data.Length);
            var lut = StickBoundary.GetOrBuild(StickBoundary.Serialize(data));

            double worst = 1.0;
            for (int i = 0; i < data.Length; i++)
                worst = Math.Min(worst, WarpRimPoint(data, lut, i));
            Assert.InRange(worst, 0.95, 1.001);
        }

        [Fact]
        public void EmptyMap_IsIdentity()
        {
            Assert.Null(StickBoundary.GetOrBuild(""));
            Assert.Null(StickBoundary.GetOrBuild("   "));
            Assert.Null(StickBoundary.GetOrBuild(null));

            short sx = 12345, sy = -6789;
            StickBoundary.Reshape(ref sx, ref sy, null); // no LUT = no-op
            Assert.Equal(12345, sx);
            Assert.Equal(-6789, sy);
        }

        [Fact]
        public void DegenerateMaps_DoNotDivideByZeroOrNaN()
        {
            // All-zero serializes to empty (feature off).
            var zero = StickBoundary.NewMap();
            Assert.Equal(string.Empty, StickBoundary.Serialize(zero));

            // A single spike among zeros must not produce NaN on any angle.
            var spike = StickBoundary.NewMap();
            spike[10] = 1.0;
            var lut = StickBoundary.GetOrBuild(StickBoundary.Serialize(spike));
            for (int i = 0; i < 360; i += 3)
            {
                double a = i * Math.PI / 180.0;
                short sx = (short)(Math.Cos(a) * 0.5 * ShortScale);
                short sy = (short)(Math.Sin(a) * 0.5 * ShortScale);
                StickBoundary.Reshape(ref sx, ref sy, lut);
                Assert.False(double.IsNaN(Magnitude(sx, sy)));
            }
        }

        [Fact]
        public void Serialize_Parse_RoundTrips()
        {
            var data = StickBoundary.NewMap();
            for (int i = 0; i < data.Length; i++)
                data[i] = 0.5 + 0.3 * Math.Sin(i * 0.1);
            string s = StickBoundary.Serialize(data);
            var back = StickBoundary.Parse(s);

            Assert.NotNull(back);
            Assert.Equal(data.Length, back.Length);
            for (int i = 0; i < data.Length; i++)
                Assert.Equal(data[i], back[i], 2); // scaled-by-100 precision
        }

        [Fact]
        public void Coverage_RemainingAndBackfill()
        {
            var data = StickBoundary.NewMap();
            Assert.Equal(data.Length, StickBoundary.RemainingSectors(data));

            // Fill every other sector; backfill must close the gaps and report
            // the count actually swept.
            for (int i = 0; i < data.Length; i += 2) data[i] = 0.8;
            Assert.Equal(data.Length / 2, StickBoundary.RemainingSectors(data));

            int filled = StickBoundary.BackfillGaps(data);
            Assert.Equal(data.Length / 2, filled);
            Assert.Equal(0, StickBoundary.RemainingSectors(data)); // no gaps left
        }

        [Fact]
        public void ReshapeUnit_RimMapsToUnit_AndPreviewCoreIsFrameConsistent()
        {
            // ReshapeUnit is the shared warp core the Sticks-tab preview calls
            // (after flipping Y into the XInput frame). A rim point in unit space
            // must land on the unit circle at every angle; a half point at half.
            var data = StickBoundary.NewMap();
            for (int i = 0; i < data.Length; i++) data[i] = 0.8;
            var lut = StickBoundary.GetOrBuild(StickBoundary.Serialize(data));

            for (int deg = 0; deg < 360; deg += 15)
            {
                double a = deg * Math.PI / 180.0;
                double rx = Math.Cos(a) * 0.8, ry = Math.Sin(a) * 0.8;
                StickBoundary.ReshapeUnit(ref rx, ref ry, lut);
                Assert.InRange(Math.Sqrt(rx * rx + ry * ry), 0.999, 1.001);

                double hx = Math.Cos(a) * 0.4, hy = Math.Sin(a) * 0.4;
                StickBoundary.ReshapeUnit(ref hx, ref hy, lut);
                Assert.InRange(Math.Sqrt(hx * hx + hy * hy), 0.499, 0.501);
            }
        }

        [Fact]
        public void Reshape_ShortPath_AgreesWithUnitCore()
        {
            // The signed-short Reshape must equal the unit core round-tripped
            // through the 32768 scale, within short quantization, so the runtime
            // (short) and preview (unit) never diverge on the same map.
            var data = StickBoundary.NewMap();
            for (int i = 0; i < data.Length; i++)
            {
                double a = i * (Math.PI * 2.0) / data.Length;
                data[i] = 0.75 + 0.15 * Math.Cos(2 * a);
            }
            var lut = StickBoundary.GetOrBuild(StickBoundary.Serialize(data));

            for (int deg = 0; deg < 360; deg += 11)
            {
                double a = deg * Math.PI / 180.0;
                double ux = Math.Cos(a) * 0.6, uy = Math.Sin(a) * 0.6;
                StickBoundary.ReshapeUnit(ref ux, ref uy, lut);

                short sx = (short)(Math.Cos(a) * 0.6 * ShortScale);
                short sy = (short)(Math.Sin(a) * 0.6 * ShortScale);
                StickBoundary.Reshape(ref sx, ref sy, lut);

                Assert.InRange(sx / ShortScale - ux, -0.002, 0.002);
                Assert.InRange(sy / ShortScale - uy, -0.002, 0.002);
            }
        }

        [Fact]
        public void UpdateFromSegment_GrowsMapAlongSweep()
        {
            // Walking a chord that skims the rim between two frames must raise
            // every sample the segment crosses to that reach (the fast-sweep fill
            // that a lone-point sampler would miss).
            var data = StickBoundary.NewMap();
            int steps = 720;
            double prevX = 0.9, prevY = 0.0;
            for (int k = 1; k <= steps; k++)
            {
                double a = k * (Math.PI * 2.0) / steps;
                double x = Math.Cos(a) * 0.9, y = Math.Sin(a) * 0.9;
                StickBoundary.UpdateFromSegment(data, prevX, prevY, x, y);
                prevX = x; prevY = y;
            }
            Assert.Equal(0, StickBoundary.RemainingSectors(data));
            for (int i = 0; i < data.Length; i++)
                Assert.InRange(data[i], 0.88, 0.92); // full rim mapped at ~0.9
        }

        [Fact]
        public void Lut_TracksPolygonEdges_AtOffSampleAngles()
        {
            // Off-sample fidelity: a point on the exact polygon EDGE (the chord
            // midpoint between two adjacent samples) lies on the measured
            // boundary, so the LUT-driven warp must send it to ~full. This is
            // the angle range the bucket interpolation covers, distinct from the
            // on-sample check the squircle test already pins.
            var data = StickBoundary.NewMap();
            for (int i = 0; i < data.Length; i++)
            {
                double a = i * (Math.PI * 2.0) / data.Length;
                double c = Math.Abs(Math.Cos(a)), s = Math.Abs(Math.Sin(a));
                data[i] = 0.85 / Math.Pow(c * c * c * c + s * s * s * s, 0.25); // superellipse n=4
                if (data[i] > 1.0) data[i] = 1.0;
            }
            var lut = StickBoundary.GetOrBuild(StickBoundary.Serialize(data));

            double worst = 0.0;
            int n = data.Length;
            for (int i = 0; i < n; i++)
            {
                double a1 = i * (Math.PI * 2.0) / n, a2 = (i + 1) * (Math.PI * 2.0) / n;
                double p1x = Math.Cos(a1) * data[i], p1y = Math.Sin(a1) * data[i];
                double p2x = Math.Cos(a2) * data[(i + 1) % n], p2y = Math.Sin(a2) * data[(i + 1) % n];
                double mx = 0.5 * (p1x + p2x), my = 0.5 * (p1y + p2y); // on the edge
                StickBoundary.ReshapeUnit(ref mx, ref my, lut);
                worst = Math.Max(worst, Math.Abs(Math.Sqrt(mx * mx + my * my) - 1.0));
            }
            // At 1-degree samples the chord midpoint sits a hair inside the LUT's
            // straight interpolation; 1% bounds the whole rim.
            Assert.True(worst < 0.01, $"LUT edge error {worst:F5} exceeds 1%");
        }

        [Fact]
        public void Convexify_RemovesInwardWedge_KeepsConvexShape()
        {
            // A near-circular boundary (~1.0) with a deep inward notch, the
            // exact capture artifact seen on hardware: sectors dive to 0.46
            // then snap back to 1.10. Convexify must bridge the notch to the
            // surrounding rim and leave the rest essentially untouched.
            var data = StickBoundary.NewMap();
            for (int i = 0; i < data.Length; i++) data[i] = 1.0;
            for (int k = 0; k < 8; k++) data[20 + k] = 0.9 - 0.06 * k; // 0.90..0.48 notch

            var fixedMap = StickBoundary.Convexify(data);

            // The notch sectors are pulled back out to near the rim.
            for (int k = 0; k < 8; k++)
                Assert.InRange(fixedMap[20 + k], 0.95, 1.02);
            // The untouched rim stays ~1.0 (convex hull of a circle is the circle).
            for (int i = 100; i < 340; i += 20)
                Assert.InRange(fixedMap[i], 0.98, 1.02);
        }

        [Fact]
        public void Convexify_PreservesConvexSquircle_AndIsIdempotent()
        {
            // A smooth convex superellipse must survive convexify within chord
            // error (the hull re-samples 1-degree edges), and re-running is a
            // near-no-op. This guards convexify against distorting a real gate.
            var data = StickBoundary.NewMap();
            for (int i = 0; i < data.Length; i++)
                data[i] = Superellipse(i * (Math.PI * 2.0) / data.Length);
            var once = StickBoundary.Convexify(data);
            var twice = StickBoundary.Convexify(once);
            for (int i = 0; i < data.Length; i++)
            {
                Assert.InRange(once[i] - data[i], -0.01, 0.01); // shape preserved
                Assert.InRange(twice[i] - once[i], -0.005, 0.005); // stable
            }
        }

        [Fact]
        public void Circularity_PerfectCircleIs100_SquircleIsLower()
        {
            var circle = StickBoundary.NewMap();
            for (int i = 0; i < circle.Length; i++) circle[i] = 0.8;
            Assert.InRange(StickBoundary.Circularity(circle), 99.9, 100.0);

            var squircle = StickBoundary.NewMap();
            for (int i = 0; i < squircle.Length; i++)
            {
                double a = i * (Math.PI * 2.0) / squircle.Length;
                squircle[i] = 0.7 + 0.2 * Math.Abs(Math.Cos(2 * a));
            }
            Assert.InRange(StickBoundary.Circularity(squircle), 80.0, 97.0);
        }
    }
}
