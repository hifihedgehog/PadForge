using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace PadForge.Common
{
    /// <summary>
    /// Monotonic cubic spline LUT for sensitivity curves.
    /// Supports both old format (single number "-50") and new format ("0,0;0.5,0.2;1,1").
    /// Thread-safe cache: LUT built once per unique curve string.
    /// </summary>
    internal static class CurveLut
    {
        private static readonly ConcurrentDictionary<string, double[]> _cache = new();
        private const int LutSize = 256;

        /// <summary>
        /// Gets (or builds and caches) a LUT for the given curve string.
        /// Returns null for linear (no curve needed).
        /// </summary>
        public static double[] GetOrBuild(string curveString)
        {
            if (string.IsNullOrEmpty(curveString) || curveString == "0")
                return null;

            return _cache.GetOrAdd(curveString, Build);
        }

        /// <summary>O(1) LUT lookup with linear interpolation between entries.</summary>
        public static double Lookup(double[] lut, double input)
        {
            if (lut == null) return input;
            double pos = Math.Clamp(input, 0, 1) * (lut.Length - 1);
            int idx = (int)pos;
            if (idx >= lut.Length - 1) return lut[lut.Length - 1];
            double frac = pos - idx;
            return lut[idx] + frac * (lut[idx + 1] - lut[idx]);
        }

        /// <summary>Parse control points from serialized string.</summary>
        public static List<(double X, double Y)> Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return new List<(double, double)> { (0, 0), (1, 1) };

            // Backward compat: single number → old power curve
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double oldCurve))
                return ConvertPowerCurve(oldCurve);

            var points = new List<(double X, double Y)>();
            foreach (var pair in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(',');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                {
                    points.Add((Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
                }
            }

            if (points.Count < 2)
                return new List<(double, double)> { (0, 0), (1, 1) };

            points.Sort((a, b) => a.X.CompareTo(b.X));
            if (points[0].X > 0.001) points.Insert(0, (0, 0));
            if (points[^1].X < 0.999) points.Add((1, 1));
            return points;
        }

        /// <summary>Serialize control points to string.</summary>
        public static string Serialize(IReadOnlyList<(double X, double Y)> points)
        {
            if (points == null || points.Count < 2) return "0,0;1,1";
            var ic = CultureInfo.InvariantCulture;
            var parts = new string[points.Count];
            for (int i = 0; i < points.Count; i++)
                parts[i] = $"{points[i].X.ToString("F3", ic)},{points[i].Y.ToString("F3", ic)}";
            return string.Join(";", parts);
        }

        /// <summary>Check if a curve string represents linear (no curve).</summary>
        public static bool IsLinear(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "0") return true;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                && Math.Abs(v) < 0.5) return true;
            var pts = Parse(s);
            if (pts.Count == 2 && Math.Abs(pts[0].X) < 0.001 && Math.Abs(pts[0].Y) < 0.001
                && Math.Abs(pts[1].X - 1) < 0.001 && Math.Abs(pts[1].Y - 1) < 0.001) return true;
            return false;
        }

        /// <summary>Normalizes a curve string by round-tripping through Parse→Serialize.
        /// Converts old-format values (e.g. "0") to canonical form (e.g. "0,0;1,1").</summary>
        public static string Normalize(string curveString)
        {
            if (string.IsNullOrEmpty(curveString)) return "0,0;1,1";
            var points = Parse(curveString);
            return Serialize(points);
        }

        /// <summary>Returns the preset name matching a curve string, or "Custom" if none match.</summary>
        public static string MatchPreset(string curveString)
        {
            if (string.IsNullOrEmpty(curveString)) return Presets[0].Name; // Linear
            var normalized = Normalize(curveString);
            foreach (var (name, serialized) in Presets)
                if (normalized == Normalize(serialized)) return name;
            return "Custom";
        }

        /// <summary>Named presets for the UI.</summary>
        public static readonly (string Name, string Serialized)[] Presets =
        {
            ("Linear",     "0,0;1,1"),
            ("Aggressive", "0,0;0.5,0.2;1,1"),
            ("Smooth",     "0,0;0.5,0.8;1,1"),
            ("Instant",    "0,0;0.1,0.9;1,1"),
            ("S-Curve",    "0,0;0.3,0.1;0.7,0.9;1,1"),
            ("Delay",      "0,0;0.8,0.2;1,1"),
        };

        // ── Private ──

        private static double[] Build(string s)
        {
            var points = Parse(s);
            return BuildSplineLut(points);
        }

        private static List<(double X, double Y)> ConvertPowerCurve(double curve)
        {
            if (Math.Abs(curve) < 0.5)
                return new List<(double, double)> { (0, 0), (1, 1) };

            double exp = Math.Pow(4.0, -curve / 100.0);
            return new List<(double, double)>
            {
                (0, 0),
                (0.25, Math.Pow(0.25, exp)),
                (0.5, Math.Pow(0.5, exp)),
                (0.75, Math.Pow(0.75, exp)),
                (1, 1)
            };
        }

        /// <summary>Fritsch-Carlson monotonic cubic spline → LUT.</summary>
        private static double[] BuildSplineLut(List<(double X, double Y)> points)
        {
            var lut = new double[LutSize];
            int n = points.Count;

            if (n < 2)
            {
                for (int i = 0; i < LutSize; i++)
                    lut[i] = (double)i / (LutSize - 1);
                return lut;
            }

            // Slopes between consecutive points
            var dk = new double[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                double dx = points[i + 1].X - points[i].X;
                dk[i] = dx > 1e-12 ? (points[i + 1].Y - points[i].Y) / dx : 0;
            }

            // Tangents (Fritsch-Carlson)
            var m = new double[n];
            m[0] = dk[0];
            m[n - 1] = dk[n - 2];
            for (int i = 1; i < n - 1; i++)
            {
                if (dk[i - 1] * dk[i] <= 0)
                    m[i] = 0;
                else
                    m[i] = (dk[i - 1] + dk[i]) / 2.0;
            }

            // Monotonicity correction
            for (int i = 0; i < n - 1; i++)
            {
                if (Math.Abs(dk[i]) < 1e-12)
                {
                    m[i] = 0;
                    m[i + 1] = 0;
                }
                else
                {
                    double a = m[i] / dk[i];
                    double b = m[i + 1] / dk[i];
                    double s = a * a + b * b;
                    if (s > 9)
                    {
                        double t = 3.0 / Math.Sqrt(s);
                        m[i] = t * a * dk[i];
                        m[i + 1] = t * b * dk[i];
                    }
                }
            }

            // Build LUT via cubic Hermite
            for (int i = 0; i < LutSize; i++)
            {
                double x = Math.Clamp((double)i / (LutSize - 1), 0, 1);

                // Find segment
                int seg = 0;
                for (int j = 0; j < n - 1; j++)
                    if (x >= points[j].X) seg = j;
                if (seg >= n - 1) seg = n - 2;

                double segDx = points[seg + 1].X - points[seg].X;
                if (segDx < 1e-12)
                {
                    lut[i] = points[seg].Y;
                    continue;
                }

                double t = (x - points[seg].X) / segDx;
                double t2 = t * t, t3 = t2 * t;
                lut[i] = Math.Clamp(
                    (2 * t3 - 3 * t2 + 1) * points[seg].Y +
                    (t3 - 2 * t2 + t) * segDx * m[seg] +
                    (-2 * t3 + 3 * t2) * points[seg + 1].Y +
                    (t3 - t2) * segDx * m[seg + 1],
                    0, 1);
            }

            return lut;
        }
    }
}
