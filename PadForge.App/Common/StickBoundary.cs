using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace PadForge.Common
{
    /// <summary>
    /// Stick boundary calibration and circular reshaping (issue #174).
    ///
    /// A physical stick's reachable region at full deflection is not a circle:
    /// the gate and sensor geometry make it an irregular rounded square that
    /// under-reaches on the diagonals and differs per unit. This class captures
    /// the reachable boundary as N radii at evenly spaced angles (a live rim
    /// sweep grows the map), then radially warps live input by dividing its
    /// distance by the measured radius at its angle, so the physical boundary
    /// maps onto a clean unit circle. No clipping: the full physical sweep is
    /// preserved and remapped, and every direction reaches full at the rim.
    ///
    /// The geometry (ray-to-segment intersection, polygon-edge interpolation,
    /// radial division) is the same model Dolphin's ReshapableInput/StickGate
    /// uses. That math is unprotectable geometry; the C# here is original and
    /// derived from first principles. The persistence format (samples scaled by
    /// 100, space-separated) matches Dolphin's so the map stays human-readable
    /// in PadForge.xml. Empty map = feature off = identity, zero runtime cost.
    /// </summary>
    internal static class StickBoundary
    {
        // One sector per degree. Dolphin uses 32; measured against RePad's
        // ~360-sector "Perfect Circle" this captures the gate's fine structure
        // and draws a smooth boundary, while the runtime cost stays a single
        // LUT lookup regardless of sample count. The math never hardcodes this,
        // it reads data.Length, so an old 32-sample map still loads and warps.
        public const int SampleCount = 360;
        private const double Tau = Math.PI * 2.0;
        private const double Eps = 1e-5;

        // Short-axis normalization matches ApplyCenterOffset's 32768 scale, so
        // the warp round-trips signed-short input cleanly.
        private const double ShortScale = 32768.0;

        // ────────────────────────────────────────────────
        //  Geometry
        // ────────────────────────────────────────────────

        /// <summary>Intersects a ray from the origin (unit direction rx,ry)
        /// with the segment (p1x,p1y)->(p2x,p2y). Returns true with the distance
        /// along the ray to the crossing, or false when the segment is parallel
        /// to the ray, the crossing lies off the segment, or it is behind the
        /// origin. Derived from the 2x2 solve of t*R = A + s*(B-A); the 1e-5
        /// slop lets a shared endpoint between adjacent segments still register.</summary>
        private static bool TryRaySegment(double rx, double ry,
            double p1x, double p1y, double p2x, double p2y, out double dist)
        {
            dist = 0.0;
            double dx = p2x - p1x, dy = p2y - p1y;
            double det = dx * ry - dy * rx;          // parallel when ~0
            if (Math.Abs(det) < Eps) return false;
            double s = (rx * p1y - ry * p1x) / det;  // segment parameter
            if (s < -Eps || s > 1.0 + Eps) return false;
            double t = (dx * p1y - dy * p1x) / det;  // ray distance (unit ray)
            if (t < 0.0) return false;
            dist = t;
            return true;
        }

        /// <summary>The calibrated radius at an arbitrary angle, by polygon-EDGE
        /// interpolation (NOT a radial lerp of the two neighbor radii): the two
        /// bracketing samples are reconstructed as 2D points, and the query ray
        /// is intersected with the straight chord between them. Falls back to the
        /// lower neighbor's radius when the chord is degenerate.</summary>
        private static double RadiusAtAngleExact(double[] data, double angle)
        {
            int n = data.Length;
            double s = angle / Tau * n;
            int i1 = ((int)Math.Floor(s)) % n;
            if (i1 < 0) i1 += n;
            int i2 = (i1 + 1) % n;
            double a1 = i1 * Tau / n, a2 = i2 * Tau / n;
            double p1x = Math.Cos(a1) * data[i1], p1y = Math.Sin(a1) * data[i1];
            double p2x = Math.Cos(a2) * data[i2], p2y = Math.Sin(a2) * data[i2];
            if (TryRaySegment(Math.Cos(angle), Math.Sin(angle), p1x, p1y, p2x, p2y, out double r))
                return r;
            return data[i1];
        }

        // ────────────────────────────────────────────────
        //  Capture (the live sweep)
        // ────────────────────────────────────────────────

        /// <summary>A fresh, empty sample map (all radii zero).</summary>
        public static double[] NewMap() => new double[SampleCount];

        /// <summary>Grows the map from one inter-frame segment of the sweep. For
        /// each sample ray, if the segment prev->cur crosses it, the sample rises
        /// to the farther reach. Max-only, so the map only ever grows, and using
        /// the segment (not the lone point) fills the rays a fast sweep skips
        /// between two frames. Points are in [-1,1] stick space.</summary>
        public static void UpdateFromSegment(double[] data,
            double prevX, double prevY, double curX, double curY)
        {
            if (data == null) return;
            int n = data.Length;
            for (int i = 0; i < n; i++)
            {
                double a = i * Tau / n;
                if (TryRaySegment(Math.Cos(a), Math.Sin(a), prevX, prevY, curX, curY, out double hit)
                    && hit > data[i])
                    data[i] = hit;
            }
        }

        /// <summary>Fills any sample left below <paramref name="floor"/> (a
        /// sector the sweep never reached) by circular linear interpolation
        /// between the nearest filled neighbors, so an incomplete sweep does not
        /// leave inward spikes. Returns the count of samples that WERE filled by
        /// the sweep; the caller treats a low count as a failed calibration.</summary>
        public static int BackfillGaps(double[] data, double floor = 0.2)
        {
            int n = data.Length, filled = 0;
            for (int i = 0; i < n; i++) if (data[i] >= floor) filled++;
            if (filled == 0 || filled == n) return filled;

            for (int i = 0; i < n; i++)
            {
                if (data[i] >= floor) continue;
                // nearest filled neighbor forward and backward (circular)
                int fwd = 1; while (fwd < n && data[(i + fwd) % n] < floor) fwd++;
                int bwd = 1; while (bwd < n && data[((i - bwd) % n + n) % n] < floor) bwd++;
                double vf = data[(i + fwd) % n], vb = data[((i - bwd) % n + n) % n];
                double t = (double)bwd / (bwd + fwd);
                data[i] = vb + (vf - vb) * t;
            }
            return filled;
        }

        /// <summary>Count of sectors the sweep has not yet reached (radius still
        /// below <paramref name="floor"/>), for the coverage-driven "N sectors
        /// left" readout. Zero means the rim is fully mapped.</summary>
        public static int RemainingSectors(double[] data, double floor = 0.2)
        {
            if (data == null) return 0;
            int rem = 0;
            for (int i = 0; i < data.Length; i++) if (data[i] < floor) rem++;
            return rem;
        }

        /// <summary>Circularity of a map as a percentage: 100 for a perfect
        /// circle, lower the more the boundary deviates from its mean radius.
        /// Shown as the "how squircular is this stick" readout.</summary>
        public static double Circularity(double[] data)
        {
            if (data == null || data.Length == 0) return 0.0;
            double mean = 0.0;
            for (int i = 0; i < data.Length; i++) mean += data[i];
            mean /= data.Length;
            if (mean < Eps) return 0.0;
            double mad = 0.0;
            for (int i = 0; i < data.Length; i++) mad += Math.Abs(data[i] - mean);
            mad /= data.Length;
            return Math.Clamp(100.0 * (1.0 - mad / mean), 0.0, 100.0);
        }

        // ────────────────────────────────────────────────
        //  Persistence
        // ────────────────────────────────────────────────

        /// <summary>Serializes a map to space-separated integers scaled by 100
        /// (Dolphin's format), or "" for a null/empty/all-zero map.</summary>
        public static string Serialize(double[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            bool any = false;
            for (int i = 0; i < data.Length; i++) if (data[i] > Eps) { any = true; break; }
            if (!any) return string.Empty;
            var sb = new StringBuilder(data.Length * 4);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(((int)Math.Round(data[i] * 100.0)).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Parses a serialized map, or null when empty/malformed.</summary>
        public static double[] Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            var data = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    return null;
                data[i] = v / 100.0;
            }
            return data;
        }

        /// <summary>Boundary polygon vertices in the 200x200 radar's plot space
        /// (center 100,100, 100% ring radius 100), for the card overlay. Empty
        /// when the map is off. Y is screen-down, matching the radar's dots.</summary>
        public static System.Windows.Media.PointCollection PlotPolygon(double[] data, double size = 200.0)
        {
            var pts = new System.Windows.Media.PointCollection();
            if (data == null) return pts;
            double c = size / 2.0, scale = size / 2.0;
            for (int i = 0; i < data.Length; i++)
            {
                double a = i * Tau / data.Length;
                pts.Add(new System.Windows.Point(
                    c + Math.Cos(a) * data[i] * scale,
                    c - Math.Sin(a) * data[i] * scale));
            }
            return pts;
        }

        // ────────────────────────────────────────────────
        //  Runtime LUT + warp (mirrors CurveLut's cache shape)
        // ────────────────────────────────────────────────

        private const int LutSize = 512; // power of two >= SampleCount, so index math can mask
        private static readonly ConcurrentDictionary<string, double[]> _cache = new();

        /// <summary>Returns the cached radius LUT (<see cref="LutSize"/> buckets)
        /// for a boundary string, or null when the feature is off (empty string).
        /// Built once per distinct string via the polygon-edge interpolation, then
        /// every hot-path lookup is O(1) with no dictionary touch and no
        /// allocation, the same shape as <see cref="CurveLut.GetOrBuild"/>.</summary>
        public static double[] GetOrBuild(string boundary)
        {
            if (string.IsNullOrWhiteSpace(boundary)) return null;
            return _cache.GetOrAdd(boundary, BuildLut);
        }

        private static double[] BuildLut(string boundary)
        {
            var data = Parse(boundary);
            if (data == null) return null;
            var lut = new double[LutSize];
            for (int b = 0; b < LutSize; b++)
                lut[b] = RadiusAtAngleExact(data, b * Tau / LutSize);
            return lut;
        }

        private static double RadiusAtAngle(double[] lut, double angle)
        {
            // normalize to [0, Tau)
            angle -= Tau * Math.Floor(angle / Tau);
            double f = angle / Tau * LutSize;
            int i0 = (int)f;
            double frac = f - i0;
            i0 &= LutSize - 1;
            int i1 = (i0 + 1) & (LutSize - 1);
            return lut[i0] + (lut[i1] - lut[i0]) * frac;
        }

        /// <summary>The warp core in unit stick space [-1,1], XInput frame
        /// (y positive = up, the frame the map is captured in). Divides the
        /// distance by the measured radius at the angle so the boundary maps to
        /// the unit circle; past the calibrated rim reads as full (1.0), never
        /// clipped inward. No-op when lut is null or at dead center. Shared by
        /// the runtime short-axis <see cref="Reshape"/> and the Sticks-tab
        /// preview (which flips Y into this frame around the call).</summary>
        public static void ReshapeUnit(ref double x, ref double y, double[] lut)
        {
            if (lut == null) return;
            double r = Math.Sqrt(x * x + y * y);
            if (r < 1e-6) return; // at center
            double angle = Math.Atan2(y, x);
            if (angle < 0) angle += Tau;
            double radius = RadiusAtAngle(lut, angle);
            if (radius < 1e-6) return; // degenerate sample, identity
            double warped = r / radius;
            if (warped > 1.0) warped = 1.0; // past the calibrated rim reads as full
            x = Math.Cos(angle) * warped;
            y = Math.Sin(angle) * warped;
        }

        /// <summary>Radially warps one stick's signed-short position so the
        /// measured boundary maps to the unit circle. No-op when lut is null.
        /// Runs between the center-offset and dead-zone stages, so the dead-zone
        /// and curve chain downstream sees circle-true values at every angle.</summary>
        public static void Reshape(ref short sx, ref short sy, double[] lut)
        {
            if (lut == null) return;
            double x = sx / ShortScale, y = sy / ShortScale;
            ReshapeUnit(ref x, ref y, lut);
            sx = (short)Math.Clamp(x * ShortScale, short.MinValue, short.MaxValue);
            sy = (short)Math.Clamp(y * ShortScale, short.MinValue, short.MaxValue);
        }
    }
}
