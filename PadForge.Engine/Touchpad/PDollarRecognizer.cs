using System;
using System.Collections.Generic;
using System.Numerics;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// $P (P-recognizer) shape matcher for touchpad gestures. Algorithm
    /// from Vatavu / Anthony / Wobbrock, "Gestures as point clouds:
    /// a $P recognizer for user interface prototypes" (ICMI 2012).
    /// Public-domain algorithm; this is an original C# implementation.
    ///
    /// <para>The recognizer is scale / position / rotation invariant by
    /// construction (resample → scale-to-unit → translate-to-centroid →
    /// point-cloud match). Multi-finger gestures are supported by
    /// concatenating each finger's normalized path into a single cloud
    /// — finger correspondence is not tracked, which matches user
    /// expectation that "I drew this with 2 fingers" should match
    /// regardless of which finger drew which stroke.</para>
    ///
    /// <para>Tuning:</para>
    /// <list type="bullet">
    /// <item><b>N</b> (resample count): 32 by default. Larger = more
    /// accurate but slower; the $P paper cites 32 as the sweet spot
    /// for typical UI gestures.</item>
    /// <item><b>Threshold</b>: 2.5 by default. Lower = stricter
    /// (fewer false-positives); higher = looser (more matches). Tune
    /// per-template if a specific gesture needs different sensitivity.</item>
    /// </list>
    /// </summary>
    public static class PDollarRecognizer
    {
        /// <summary>Default resample count. The paper's published
        /// accuracy numbers use N=32 and recommend it as the
        /// general-purpose default.</summary>
        public const int DefaultResampleCount = 32;

        /// <summary>Resamples <paramref name="raw"/> to exactly
        /// <paramref name="n"/> points spaced equally along the path
        /// length. Output replaces the input shape's arbitrary timing
        /// (fast / slow draw doesn't matter) with arc-length parameterized
        /// samples so subsequent cloud-distance comparisons aren't
        /// biased by where the user paused.</summary>
        public static Vector2[] Resample(IReadOnlyList<Vector2> raw, int n)
        {
            if (raw == null || raw.Count == 0 || n <= 0) return new Vector2[0];
            if (raw.Count == 1)
            {
                var r = new Vector2[n];
                for (int i = 0; i < n; i++) r[i] = raw[0];
                return r;
            }

            float totalLen = 0f;
            for (int i = 1; i < raw.Count; i++)
                totalLen += (raw[i] - raw[i - 1]).Length();
            if (totalLen <= 0f)
            {
                var r = new Vector2[n];
                for (int i = 0; i < n; i++) r[i] = raw[0];
                return r;
            }

            float step = totalLen / (n - 1);
            var output = new Vector2[n];
            output[0] = raw[0];
            float distSoFar = 0f;
            int outIdx = 1;
            for (int i = 1; i < raw.Count && outIdx < n; i++)
            {
                Vector2 a = raw[i - 1];
                Vector2 b = raw[i];
                float segLen = (b - a).Length();
                if (segLen <= 0f) continue;
                while (distSoFar + segLen >= step * outIdx && outIdx < n)
                {
                    float t = (step * outIdx - distSoFar) / segLen;
                    output[outIdx] = Vector2.Lerp(a, b, t);
                    outIdx++;
                }
                distSoFar += segLen;
            }
            // Floating-point drift can leave the last slot unset.
            for (int i = outIdx; i < n; i++)
                output[i] = raw[raw.Count - 1];
            return output;
        }

        /// <summary>Translates the centroid of the point set to the
        /// origin, then scales uniformly so the bounding-box diagonal
        /// equals 1. Order-preserving; the per-point ordering still
        /// matters for $P's greedy nearest-neighbor matching (it's a
        /// "point cloud" but with index hints).</summary>
        public static Vector2[] NormalizeCloud(Vector2[] pts)
        {
            if (pts == null || pts.Length == 0) return new Vector2[0];
            float cx = 0, cy = 0;
            for (int i = 0; i < pts.Length; i++) { cx += pts[i].X; cy += pts[i].Y; }
            cx /= pts.Length; cy /= pts.Length;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            var centered = new Vector2[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                var p = new Vector2(pts[i].X - cx, pts[i].Y - cy);
                centered[i] = p;
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            float diag = MathF.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
            if (diag <= 0f) return centered;
            var output = new Vector2[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                output[i] = centered[i] / diag;
            return output;
        }

        /// <summary>Builds the multi-finger point cloud for matching:
        /// resamples each finger's path to <paramref name="perFinger"/>
        /// points, then concatenates the resampled clouds and normalizes
        /// the combined cloud. Total cloud size =
        /// <c>fingerCount × perFinger</c>.</summary>
        public static Vector2[] BuildCloud(IReadOnlyList<IReadOnlyList<Vector2>> fingers, int perFinger)
        {
            if (fingers == null || fingers.Count == 0) return new Vector2[0];
            int total = fingers.Count * perFinger;
            var combined = new Vector2[total];
            int o = 0;
            for (int f = 0; f < fingers.Count; f++)
            {
                var rs = Resample(fingers[f], perFinger);
                for (int i = 0; i < rs.Length && o < total; i++, o++)
                    combined[o] = rs[i];
            }
            return NormalizeCloud(combined);
        }

        /// <summary>Computes the $P "Goodness-of-Match" distance between
        /// two normalized clouds of equal length. For each point in
        /// <paramref name="candidate"/>, finds the nearest still-
        /// unmatched point in <paramref name="template"/> (greedy NN);
        /// weights closer matches higher (start of cloud counts more).
        /// Returns 0 for identical clouds; higher = more dissimilar.
        /// Symmetric in expectation; the paper minimizes over both
        /// orderings, but a single greedy pass is sufficient for the
        /// resolutions PadForge uses (N=32 × 1-3 fingers).</summary>
        public static float CloudDistance(Vector2[] candidate, Vector2[] template, int start)
        {
            if (candidate == null || template == null) return float.MaxValue;
            if (candidate.Length != template.Length) return float.MaxValue;
            int n = candidate.Length;
            if (n == 0) return 0f;

            bool[] matched = new bool[n];
            float sum = 0f;
            int i = start;
            do
            {
                int bestIdx = -1;
                float bestDist = float.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (matched[j]) continue;
                    float d = (candidate[i] - template[j]).LengthSquared();
                    if (d < bestDist) { bestDist = d; bestIdx = j; }
                }
                if (bestIdx < 0) break;
                matched[bestIdx] = true;
                // Weight: earlier-in-cloud matches count more (per $P).
                float weight = 1f - ((i - start + n) % n) / (float)n;
                sum += MathF.Sqrt(bestDist) * weight;
                i = (i + 1) % n;
            } while (i != start);
            return sum;
        }

        /// <summary>Matches <paramref name="candidate"/> against the
        /// catalog of <paramref name="templates"/> using $P. Returns
        /// the best-matching template's name (or null when no template
        /// is under threshold). Filters the catalog to entries whose
        /// FingerCount matches <paramref name="fingerCount"/> — multi-
        /// finger gestures only match same-finger-count templates.</summary>
        public static string Match(Vector2[] candidate,
            IReadOnlyList<PDollarTemplate> templates,
            int fingerCount, float threshold, out float bestScore)
        {
            bestScore = float.MaxValue;
            string bestName = null;
            if (templates == null) return null;
            for (int t = 0; t < templates.Count; t++)
            {
                var tpl = templates[t];
                if (tpl == null || tpl.FingerCount != fingerCount) continue;
                if (!tpl.Enabled) continue;
                if (tpl.PointCloud == null || tpl.PointCloud.Length != candidate.Length) continue;
                float effThreshold = tpl.ThresholdOverride > 0f
                    ? tpl.ThresholdOverride : threshold;
                float d = CloudDistance(candidate, tpl.PointCloud, 0);
                if (d < bestScore)
                {
                    bestScore = d;
                    if (d <= effThreshold) bestName = tpl.Name;
                }
            }
            return bestName;
        }

        /// <summary>Convenience wrapper: builds the candidate cloud from
        /// the live <paramref name="fingerPaths"/> and matches against
        /// <paramref name="templates"/>. Resample count defaults to
        /// <see cref="DefaultResampleCount"/>.</summary>
        public static string MatchByFingerCount(
            IReadOnlyList<IReadOnlyList<Vector2>> fingerPaths,
            IReadOnlyList<PDollarTemplate> templates,
            int fingerCount, float threshold, out float bestScore,
            int resampleCount = DefaultResampleCount)
        {
            var cloud = BuildCloud(fingerPaths, resampleCount);
            return Match(cloud, templates, fingerCount, threshold, out bestScore);
        }
    }

    /// <summary>
    /// Pre-normalized shape template for the $P recognizer. In-box
    /// templates (Circle / Square / Triangle / Z / Checkmark) are
    /// constructed at app startup from procedural point generators;
    /// custom user templates are constructed at gesture-save time from
    /// the recorded sample(s) after multi-sample averaging.
    /// </summary>
    public sealed class PDollarTemplate
    {
        /// <summary>Gesture name (e.g. <c>Circle</c>, <c>CircleCCW</c>,
        /// <c>Custom_ZoomToFit</c>). Used as the suffix in the
        /// <c>Touchpad N {Name}</c> descriptor that the source-coercion
        /// layer reads.</summary>
        public string Name;

        /// <summary>Number of fingers this template represents. Single-
        /// stroke single-finger = 1; two-finger pinch-out-circle = 2;
        /// up to 5 for the PTP-max scenarios.</summary>
        public int FingerCount;

        /// <summary>Normalized point cloud, FingerCount × ResampleCount
        /// points long. Pre-built (resampled + scaled + centroid-
        /// translated) so the matcher's hot path skips the prep work.</summary>
        public Vector2[] PointCloud;

        /// <summary>Per-template threshold override. 0 = use the
        /// caller's default. Custom gestures with strict / loose feel
        /// can dial this independently of the global default.</summary>
        public float ThresholdOverride;

        /// <summary>Per-template enable toggle. The Touchpad tab UI
        /// surfaces this so users can disable individual gestures
        /// without deleting them.</summary>
        public bool Enabled = true;

        /// <summary>True for user-recorded templates, false for in-box
        /// shapes. Drives the per-finger-count gating in
        /// <see cref="GestureRecognizer.MaybeFireShape"/> — custom
        /// templates always evaluate; in-box shapes only when the
        /// per-pad EnableShapeGestures toggle is on.</summary>
        public bool IsCustom;

        /// <summary>Single-finger angle signature for the angular-margin
        /// recognizer that runs alongside $P on single-finger shapes.
        /// Null for multi-finger templates (angular-margin doesn't
        /// naturally extend to a permutation-invariant cloud across
        /// strokes the user can't be relied on to draw in the same
        /// order each time). Built once at template-load time.</summary>
        public double[] AngularSignature;

        /// <summary>Closed-shape flag — angular-margin matching tries
        /// every cyclic starting-point so a user beginning the trace
        /// from any corner / vertex matches the same template. Open
        /// shapes leave this false. Carried over to
        /// <see cref="AngularTemplate.IsClosed"/> during single-finger
        /// matching.</summary>
        public bool AngularIsClosed;

        /// <summary>Direction-agnostic flag — angular-margin matching
        /// also tries the reverse-direction signature so a shape drawn
        /// CW or CCW both match. Carried over to
        /// <see cref="AngularTemplate.IsDirectionAgnostic"/> during
        /// single-finger matching. Stays false on Circle CW / CCW
        /// (intentionally separate directional gestures).</summary>
        public bool AngularIsDirectionAgnostic;
    }
}
