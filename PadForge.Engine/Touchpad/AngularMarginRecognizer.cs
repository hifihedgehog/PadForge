using System;
using System.Collections.Generic;
using System.Numerics;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// Angular-margin shape recognizer for single-finger gestures.
    /// Resamples a candidate path to a fixed number of evenly-spaced
    /// arc-length points, computes the angle of each segment between
    /// adjacent points, and compares the angle sequence against a
    /// reference template's angle sequence — direction-preserving
    /// match that handles shapes with distinct corners (Square,
    /// Triangle, Z, Checkmark) noticeably better than point-cloud
    /// distance ($P) because it actively rewards consistent stroke
    /// direction at each interpolated point.
    ///
    /// <para>Algorithm follows GestureSign's PointPatternAnalyzer
    /// (BSD-style permissively-licensed reference); the implementation
    /// here is original C# re-derived from the same description rather
    /// than a literal port. Pairs with <see cref="PDollarRecognizer"/>:
    /// the gesture engine runs both against the candidate path on a
    /// single-finger shape and keeps the higher-confidence match.</para>
    ///
    /// <para>Multi-finger gestures stay with $P only — angular-margin
    /// doesn't naturally generalize to a multi-stroke point cloud
    /// without per-finger correspondence the user can't be relied on
    /// to provide between samples.</para>
    /// </summary>
    public static class AngularMarginRecognizer
    {
        /// <summary>Resampled point count for the angular comparison.
        /// 64 segments → 65 points, gives 64 inter-point angles to
        /// compare. Higher = finer-grained discrimination (slower).
        /// Lower = coarser, more forgiving of small wobbles.</summary>
        public const int DefaultSegments = 64;

        /// <summary>Match returns a score in [0..1] where 1 = perfect
        /// alignment, 0 = perpendicular / inverted at every segment.
        /// A reasonable accept threshold for in-box shapes (Circle /
        /// Square / Triangle / etc.) is ≥ 0.55; user-recorded
        /// gestures vary more and may need 0.45-0.5.</summary>
        public const float DefaultAcceptScore = 0.55f;

        /// <summary>Returns the segment-angle array a recognizer
        /// template needs. Resamples <paramref name="rawPath"/> to
        /// <paramref name="segments"/>+1 evenly-spaced arc-length
        /// points, then computes the angle (radians, -π..π) of each
        /// segment. Returns null when the input is too short to
        /// resample (need ≥ 2 distinct points with non-zero total
        /// length).</summary>
        public static double[] BuildAngleSignature(IReadOnlyList<Vector2> rawPath, int segments = DefaultSegments)
        {
            if (rawPath == null || rawPath.Count < 2 || segments < 4) return null;

            var resampled = ResampleArcLength(rawPath, segments + 1);
            if (resampled == null) return null;

            var angles = new double[segments];
            for (int i = 0; i < segments; i++)
            {
                Vector2 a = resampled[i];
                Vector2 b = resampled[i + 1];
                angles[i] = Math.Atan2(b.Y - a.Y, b.X - a.X);
            }
            return angles;
        }

        /// <summary>Score the candidate against a template by mean
        /// angular delta across all segment indices. Returns a score
        /// in [0..1] (1 = identical direction at every segment). Both
        /// inputs must be the same length — typically both produced
        /// by <see cref="BuildAngleSignature"/> with the same
        /// <c>segments</c> value.</summary>
        public static float Score(double[] candidate, double[] template)
        {
            return ScoreShifted(candidate, template, 0);
        }

        /// <summary>Score the candidate against the template treating
        /// the candidate as if its starting point is <paramref name="shift"/>
        /// segments ahead. Used by the closed-shape match path so a
        /// user drawing a square starting from any corner matches the
        /// same template. Shift wraps modulo <c>candidate.Length</c>.</summary>
        public static float ScoreShifted(double[] candidate, double[] template, int shift)
        {
            if (candidate == null || template == null) return 0f;
            if (candidate.Length == 0 || candidate.Length != template.Length) return 0f;

            int n = candidate.Length;
            int s = ((shift % n) + n) % n;
            double sumDelta = 0;
            for (int i = 0; i < n; i++)
                sumDelta += AngularDelta(candidate[(i + s) % n], template[i]);

            double meanDelta = sumDelta / n;
            return (float)(1.0 - meanDelta / Math.PI);
        }

        /// <summary>Best score across every cyclic starting-point of
        /// the candidate. Use for closed shapes (Square, Triangle,
        /// Circle, X) so the user can start drawing from any corner /
        /// vertex / edge and still match. O(N²) in the segment count
        /// (~4 ms at N=64 for one template; negligible at recognition
        /// rate).</summary>
        public static float BestRotationalScore(double[] candidate, double[] template)
        {
            if (candidate == null || template == null) return 0f;
            if (candidate.Length == 0 || candidate.Length != template.Length) return 0f;
            float best = 0f;
            int n = candidate.Length;
            for (int s = 0; s < n; s++)
            {
                float sc = ScoreShifted(candidate, template, s);
                if (sc > best) best = sc;
            }
            return best;
        }

        /// <summary>Returns the angle signature of the candidate as if
        /// the original path were traversed in the opposite direction:
        /// the angle array is reversed AND each angle is rotated by π
        /// (a→b becomes b→a, which is the same vector negated). Use
        /// for direction-agnostic shapes (Square, Triangle — any
        /// shape that looks the same drawn CW or CCW).</summary>
        public static double[] Reversed(double[] angles)
        {
            if (angles == null) return null;
            int n = angles.Length;
            var rev = new double[n];
            for (int i = 0; i < n; i++)
                rev[i] = WrapAngle(angles[n - 1 - i] + Math.PI);
            return rev;
        }

        private static double WrapAngle(double a)
        {
            // Wrap into (-π, π] so subsequent AngularDelta comparisons
            // stay in their expected range.
            const double TwoPi = 2 * Math.PI;
            a %= TwoPi;
            if (a > Math.PI) a -= TwoPi;
            else if (a <= -Math.PI) a += TwoPi;
            return a;
        }

        /// <summary>Walk a catalog of angle-signature templates and
        /// return the name of the best-scoring template plus its
        /// confidence in [0..1]. Returns (null, 0) on empty catalog
        /// or invalid candidate. Pass the same <paramref name="segments"/>
        /// the templates were built with.
        ///
        /// <para>Per-template flags control match permissiveness:
        /// <see cref="AngularTemplate.IsClosed"/> tries all cyclic
        /// candidate starting-points (so a Square drawn starting from
        /// any corner matches); <see cref="AngularTemplate.IsDirectionAgnostic"/>
        /// also tries the reverse-direction signature (so a Square
        /// drawn CW and CCW both match). Open / directional shapes
        /// (Z, Checkmark, Circle CW vs CCW) leave these false so
        /// start orientation + direction stay part of their identity.</para></summary>
        public static (string Name, float Score) Match(
            IReadOnlyList<Vector2> rawPath,
            IReadOnlyList<AngularTemplate> templates,
            int segments = DefaultSegments)
        {
            var candAngles = BuildAngleSignature(rawPath, segments);
            if (candAngles == null || templates == null || templates.Count == 0)
                return (null, 0f);

            string bestName = null;
            float bestScore = 0f;
            foreach (var t in templates)
            {
                if (t == null || !t.Enabled) continue;
                if (t.Angles == null || t.Angles.Length != candAngles.Length) continue;

                float s = t.IsClosed
                    ? BestRotationalScore(candAngles, t.Angles)
                    : Score(candAngles, t.Angles);

                if (t.IsDirectionAgnostic)
                {
                    var revCand = Reversed(candAngles);
                    float rs = t.IsClosed
                        ? BestRotationalScore(revCand, t.Angles)
                        : Score(revCand, t.Angles);
                    if (rs > s) s = rs;
                }

                if (s > bestScore)
                {
                    bestScore = s;
                    bestName = t.Name;
                }
            }
            return (bestName, bestScore);
        }

        // ─── Internals ──────────────────────────────────────

        private static Vector2[] ResampleArcLength(IReadOnlyList<Vector2> path, int n)
        {
            double total = 0;
            for (int i = 1; i < path.Count; i++)
                total += (path[i] - path[i - 1]).Length();
            if (total <= 0) return null;

            double step = total / (n - 1);
            var output = new Vector2[n];
            output[0] = path[0];

            int outIdx = 1;
            double walked = 0;
            for (int i = 1; i < path.Count && outIdx < n; i++)
            {
                Vector2 a = path[i - 1];
                Vector2 b = path[i];
                double segLen = (b - a).Length();
                while (outIdx < n && walked + segLen >= step * outIdx - 1e-9)
                {
                    double remaining = step * outIdx - walked;
                    double t = segLen > 1e-9 ? remaining / segLen : 1.0;
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;
                    output[outIdx] = Vector2.Lerp(a, b, (float)t);
                    outIdx++;
                }
                walked += segLen;
            }
            // Pad any tail with the last input point (rounding cushion).
            for (int i = outIdx; i < n; i++)
                output[i] = path[path.Count - 1];
            return output;
        }

        private static double AngularDelta(double a, double b)
        {
            double d = Math.Abs(a - b);
            if (d > Math.PI) d = 2 * Math.PI - d;
            return d;
        }
    }

    /// <summary>Single-finger angle-signature template used by
    /// <see cref="AngularMarginRecognizer.Match"/>. Built once at
    /// template-load time and reused per recognition pass.</summary>
    public sealed class AngularTemplate
    {
        /// <summary>Display name returned in a positive match
        /// (e.g. "Square", "Custom_ZoomToFit").</summary>
        public string Name;

        /// <summary>Per-segment angle (radians) of the resampled
        /// reference path. Length = <c>AngularMarginRecognizer.DefaultSegments</c>.</summary>
        public double[] Angles;

        /// <summary>Per-template gate. Disabled templates skip the
        /// score loop — same shape as <see cref="PDollarTemplate.Enabled"/>.</summary>
        public bool Enabled = true;

        /// <summary>Custom user gestures flag this true so the
        /// engine can apply per-pad enable toggles independently of
        /// in-box shape toggles.</summary>
        public bool IsCustom;

        /// <summary>Closed shapes (Square, Triangle, Circle) match
        /// against the candidate at every cyclic starting-point so a
        /// user beginning the trace from any corner / vertex / edge
        /// matches the same template. Open shapes (Z, Checkmark) stay
        /// at start-anchored matching because their first segment
        /// orientation is part of their identity.</summary>
        public bool IsClosed;

        /// <summary>True when the shape reads the same drawn either
        /// direction (Square, Triangle, X look identical CW or CCW).
        /// Circle CW vs Circle CCW are intentionally separate
        /// templates, so this stays false for both — they're each
        /// directional even though they're closed.</summary>
        public bool IsDirectionAgnostic;
    }
}
