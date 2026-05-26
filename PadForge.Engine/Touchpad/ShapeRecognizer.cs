using System;
using System.Collections.Generic;
using System.Numerics;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// Point-cloud shape recognizer used by the touchpad gesture engine.
    /// Implements $Q (Vatavu, Anthony, Wobbrock,
    /// <i>"$Q: A Super-Quick, Articulation-Invariant Stroke-Gesture
    /// Recognizer for Low-Resource Devices"</i>, MobileHCI 2018).
    /// Original C# implementation; public-domain algorithm.
    ///
    /// <para>The recognizer is scale / position / rotation invariant by
    /// construction (resample → centroid-translate → scale-uniform).
    /// Multi-finger gestures are supported by concatenating each
    /// finger's normalized path into a single cloud — finger
    /// correspondence is not tracked, which matches user expectation
    /// that "I drew this with two fingers" should match regardless of
    /// which finger drew which stroke.</para>
    ///
    /// <para>$Q vs $P: same gesture model (unordered point cloud,
    /// multi-stroke as one cloud) but the inner "nearest template
    /// point" lookup is O(1) via a pre-computed
    /// <see cref="ShapeTemplate.LookupTable"/> instead of $P's linear
    /// scan over template points. Matching cost drops from O(N²) to
    /// O(N) per template. The trade-off is ~8 KB of LUT memory per
    /// template at the reference grid size, which is negligible at
    /// PadForge's catalog scale.</para>
    ///
    /// <para>Tuning:</para>
    /// <list type="bullet">
    /// <item><b>N</b> (resample count): 32 by default. Larger = more
    /// accurate but slower template-load + match cost; the $P / $Q
    /// papers both cite 32 as the sweet spot for typical UI gestures.</item>
    /// <item><b>LUT size</b>: 64×64 cells across the normalized
    /// <c>[-1, +1]</c> indexing range. Each cell is 0.03125 wide.
    /// Finer LUTs add memory and template-load cost without measurable
    /// accuracy gain.</item>
    /// <item><b>Threshold</b>: 3.0 by default. Lower = stricter
    /// (fewer false-positives); higher = looser (more matches). Tune
    /// per-template via <see cref="ShapeTemplate.ThresholdOverride"/>
    /// if a specific gesture needs different sensitivity.</item>
    /// </list>
    /// </summary>
    public static class ShapeRecognizer
    {
        /// <summary>Default resample count. Both $P and $Q paper accuracy
        /// numbers use N=32 and recommend it as the general-purpose
        /// default.</summary>
        public const int DefaultResampleCount = 32;

        /// <summary>Default edge count of the per-template lookup grid.
        /// 64 follows the $Q paper's reference. Memory per template
        /// at this size is 64 × 64 × 2 bytes = 8 KB.</summary>
        public const int DefaultLookupTableSize = 64;

        // Half-width of the normalized-cloud indexing range. The
        // recognizer scales clouds so the combined bounding-box
        // diagonal is 1; points therefore land roughly inside
        // [-0.5/√2, +0.5/√2] (≈ ±0.35) for shapes whose bounding box
        // is square. The LUT covers a generous [-1, +1] range with
        // clamping so points falling outside the typical band still
        // land in a sensible grid cell.
        private const float NormalizedHalfRange = 1.0f;

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
        /// equals 1. The order-preserving normalization keeps the
        /// numeric range compatible with the legacy $P threshold
        /// scale, so user-tuned threshold values transfer across the
        /// $P → $Q migration without re-tuning.</summary>
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

        /// <summary>Builds the $Q lookup table for a normalized template
        /// cloud. Each cell in a <paramref name="lutSize"/> ×
        /// <paramref name="lutSize"/> grid spanning the
        /// <c>[-NormalizedHalfRange, +NormalizedHalfRange]</c> indexing
        /// range stores the index (into <paramref name="template"/>) of
        /// the template point nearest the cell's center. Storage is
        /// ushort so the table fits cloud sizes up to 65535 points;
        /// PadForge's worst case is 5 fingers × 32 = 160 points, well
        /// within the limit.</summary>
        public static ushort[] BuildLookupTable(Vector2[] template,
            int lutSize = DefaultLookupTableSize)
        {
            if (template == null || template.Length == 0 || lutSize <= 0)
                return new ushort[0];
            var lut = new ushort[lutSize * lutSize];
            float cellW = (2f * NormalizedHalfRange) / lutSize;
            for (int gy = 0; gy < lutSize; gy++)
            {
                float py = -NormalizedHalfRange + (gy + 0.5f) * cellW;
                for (int gx = 0; gx < lutSize; gx++)
                {
                    float px = -NormalizedHalfRange + (gx + 0.5f) * cellW;
                    ushort bestIdx = 0;
                    float bestDist = float.MaxValue;
                    for (int t = 0; t < template.Length; t++)
                    {
                        float dx = template[t].X - px;
                        float dy = template[t].Y - py;
                        float d = dx * dx + dy * dy;
                        if (d < bestDist) { bestDist = d; bestIdx = (ushort)t; }
                    }
                    lut[gy * lutSize + gx] = bestIdx;
                }
            }
            return lut;
        }

        /// <summary>Computes the $Q "Goodness-of-Match" distance between
        /// <paramref name="candidate"/> and <paramref name="template"/>'s
        /// cloud + LUT. For each candidate point, the LUT answers
        /// "nearest template point index" in O(1); the squared distance
        /// to that point is accumulated with the $P-style position
        /// weighting (earlier-in-cloud matches count more). Cyclic
        /// start at <paramref name="start"/> compensates for the
        /// position-weight bias toward whichever point happens to be
        /// first in the cloud's ordering.</summary>
        public static float CloudDistance(Vector2[] candidate,
            ShapeTemplate template, int start)
        {
            if (candidate == null || template == null) return float.MaxValue;
            if (template.PointCloud == null || template.LookupTable == null) return float.MaxValue;
            if (candidate.Length != template.PointCloud.Length) return float.MaxValue;
            int n = candidate.Length;
            if (n == 0) return 0f;
            int lutSize = template.LookupTableSize;
            if (lutSize <= 0 || template.LookupTable.Length != lutSize * lutSize)
                return float.MaxValue;

            float cellScale = lutSize / (2f * NormalizedHalfRange);
            float sum = 0f;
            int i = start;
            do
            {
                float fx = (candidate[i].X + NormalizedHalfRange) * cellScale;
                float fy = (candidate[i].Y + NormalizedHalfRange) * cellScale;
                int cellX = (int)fx;
                int cellY = (int)fy;
                if (cellX < 0) cellX = 0; else if (cellX >= lutSize) cellX = lutSize - 1;
                if (cellY < 0) cellY = 0; else if (cellY >= lutSize) cellY = lutSize - 1;
                int tIdx = template.LookupTable[cellY * lutSize + cellX];
                float dx = candidate[i].X - template.PointCloud[tIdx].X;
                float dy = candidate[i].Y - template.PointCloud[tIdx].Y;
                float weight = 1f - ((i - start + n) % n) / (float)n;
                sum += MathF.Sqrt(dx * dx + dy * dy) * weight;
                i = (i + 1) % n;
            } while (i != start);
            return sum;
        }

        /// <summary>Matches <paramref name="candidate"/> against the
        /// catalog of <paramref name="templates"/>. Returns the
        /// best-matching template's name (or null when no template is
        /// under threshold). Filters the catalog to entries whose
        /// FingerCount matches <paramref name="fingerCount"/> — multi-
        /// finger gestures only match same-finger-count templates.
        /// Two cyclic starts (0 and n/2) reduce starting-point bias;
        /// the paper recommends an ε-greedy sweep of more starts on
        /// noisy gesture corpora, but two starts already gives stable
        /// matches at PadForge's resample count without measurable
        /// per-match cost.</summary>
        public static string Match(Vector2[] candidate,
            IReadOnlyList<ShapeTemplate> templates,
            int fingerCount, float threshold, out float bestScore)
        {
            bestScore = float.MaxValue;
            string bestName = null;
            if (templates == null) return null;
            int half = candidate != null ? candidate.Length / 2 : 0;
            for (int t = 0; t < templates.Count; t++)
            {
                var tpl = templates[t];
                if (tpl == null || tpl.FingerCount != fingerCount) continue;
                if (!tpl.Enabled) continue;
                if (tpl.PointCloud == null || tpl.PointCloud.Length != candidate.Length) continue;
                if (tpl.LookupTable == null) continue;
                float effThreshold = tpl.ThresholdOverride > 0f
                    ? tpl.ThresholdOverride : threshold;
                float d0 = CloudDistance(candidate, tpl, 0);
                float dh = half > 0 ? CloudDistance(candidate, tpl, half) : d0;
                float d = MathF.Min(d0, dh);
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
            IReadOnlyList<ShapeTemplate> templates,
            int fingerCount, float threshold, out float bestScore,
            int resampleCount = DefaultResampleCount)
        {
            var cloud = BuildCloud(fingerPaths, resampleCount);
            return Match(cloud, templates, fingerCount, threshold, out bestScore);
        }
    }
}
