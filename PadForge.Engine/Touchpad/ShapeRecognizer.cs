// Portions of this file are a C# re-derivation of the $Q Super-Quick
// Recognizer JavaScript reference implementation
// (https://depts.washington.edu/acelab/proj/dollar/qdollar.js).
//
// Copyright (c) 2018-2019, Nathan Magrofuoco, Jacob O. Wobbrock,
// Radu-Daniel Vatavu, and Lisa Anthony. All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//   * Redistributions of source code must retain the above copyright
//     notice, this list of conditions and the following disclaimer.
//   * Redistributions in binary form must reproduce the above
//     copyright notice, this list of conditions and the following
//     disclaimer in the documentation and/or other materials provided
//     with the distribution.
//   * Neither the names of the University Stefan cel Mare of Suceava,
//     University of Washington, nor University of Florida, nor the
//     names of its contributors may be used to endorse or promote
//     products derived from this software without specific prior
//     written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
// FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
// Radu-Daniel Vatavu OR Lisa Anthony OR Jacob O. Wobbrock BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT
// OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR
// BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
// WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE
// OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE,
// EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

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
    /// Faithful port of the BSD-licensed canonical JavaScript reference
    /// implementation at depts.washington.edu/acelab/proj/dollar/qdollar.js
    /// (Nathan Magrofuoco / Vatavu / Anthony / Wobbrock, 2018-2019).
    ///
    /// <para>The recognizer is scale / position / rotation invariant by
    /// construction (resample → scale-to-largest-dimension → translate
    /// centroid to origin). Multi-finger gestures are supported by
    /// concatenating each finger's normalized path into a single cloud —
    /// finger correspondence is not tracked, which matches user
    /// expectation that "I drew this with two fingers" should match
    /// regardless of which finger drew which stroke.</para>
    ///
    /// <para>$Q vs $P: same gesture model and same per-template
    /// matched[]-tracked inner loop in CloudDistance (each template
    /// point can only be matched to one candidate point — this is what
    /// prevents a degenerate candidate from latching all its points
    /// onto a few template points and falsely matching). The speedup
    /// comes from the per-template lookup table feeding ComputeLowerBound:
    /// for each candidate starting-position, an LUT-derived lower bound
    /// on the cloud distance is precomputed, and any starting position
    /// whose lower bound exceeds the best-so-far is skipped before
    /// CloudDistance ever runs. Matching cost drops from O(N²) on every
    /// start to O(N²) on a pruned subset of starts — paper reports
    /// ~142× speedup on low-resource hardware; on a desktop CPU at
    /// PadForge's template count the win is measured in microseconds.</para>
    ///
    /// <para>Tuning:</para>
    /// <list type="bullet">
    /// <item><b>N</b> (resample count): 32 by default. The $P / $Q
    /// papers both cite 32 as the sweet spot for typical UI gestures.</item>
    /// <item><b>LUT size</b>: 64×64 cells. Memory per template ≈ 8 KB.</item>
    /// <item><b>Threshold</b>: 3.0 by default. Lower = stricter. The
    /// canonical $Q sum is roughly
    /// <c>Σᵢ weightᵢ · ‖candidateᵢ − templateπ(i)‖²</c> with weights
    /// <c>N, N-1, …, 1</c>; for clouds normalized to
    /// <c>[−0.5, +0.5]²</c> the score ranges from <c>~0.05</c> on a
    /// near-perfect match to <c>~10+</c> on dissimilar shapes.</item>
    /// </list>
    /// </summary>
    public static class ShapeRecognizer
    {
        /// <summary>Default resample count. Both $P and $Q paper accuracy
        /// numbers use N=32 and recommend it as the general-purpose
        /// default.</summary>
        public const int DefaultResampleCount = 32;

        /// <summary>Default edge count of the per-template lookup grid.
        /// 64 follows the $Q paper's reference implementation. Memory
        /// per template at this size is 64 × 64 × 2 bytes = 8 KB.</summary>
        public const int DefaultLookupTableSize = 64;

        // Integer-coordinate range used by the LUT scaling factor —
        // matches the canonical JS reference's MaxIntCoord = 1024.
        // Points are mapped to integer space [0, MaxIntCoord-1] before
        // being divided by LUTScaleFactor to land in [0, LUTSize-1].
        private const int MaxIntCoord = 1024;
        private const float LutScaleFactor = MaxIntCoord / (float)DefaultLookupTableSize;

        // ─────────────────────────────────────────────────────────
        //  Normalization pipeline (matches the canonical JS reference)
        //
        //  1. Resample to N points by arc length.
        //  2. Scale so larger of (width, height) becomes 1 (uniform
        //     scale, preserves aspect ratio).
        //  3. Translate centroid to origin.
        //
        //  After step 3, the larger bounding-box dimension spans
        //  [-0.5, +0.5] and the smaller spans the same range scaled
        //  by aspect. A perfectly horizontal line ends up at Y ≈ 0
        //  across X ∈ [-0.5, +0.5]; a perfectly square shape ends
        //  up filling [-0.5, +0.5]² evenly.
        // ─────────────────────────────────────────────────────────

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
            for (int i = outIdx; i < n; i++)
                output[i] = raw[raw.Count - 1];
            return output;
        }

        /// <summary>Normalizes a resampled cloud per the canonical $Q
        /// pipeline: scale so the larger of the bounding-box dimensions
        /// becomes 1 (uniform scale, aspect-ratio preserving), then
        /// translate the centroid to origin. Points land roughly in
        /// <c>[-0.5, +0.5]²</c> for square shapes; a horizontal line
        /// lands at <c>Y ≈ 0, X ∈ [-0.5, +0.5]</c>.</summary>
        public static Vector2[] NormalizeCloud(Vector2[] pts)
        {
            if (pts == null || pts.Length == 0) return new Vector2[0];

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < pts.Length; i++)
            {
                if (pts[i].X < minX) minX = pts[i].X;
                if (pts[i].X > maxX) maxX = pts[i].X;
                if (pts[i].Y < minY) minY = pts[i].Y;
                if (pts[i].Y > maxY) maxY = pts[i].Y;
            }
            float size = MathF.Max(maxX - minX, maxY - minY);
            if (size <= 0f) return new Vector2[pts.Length];

            // Scale to [0, 1] in the larger dimension, then translate
            // centroid to origin so subsequent matching is centered.
            var scaled = new Vector2[pts.Length];
            float cx = 0f, cy = 0f;
            for (int i = 0; i < pts.Length; i++)
            {
                float qx = (pts[i].X - minX) / size;
                float qy = (pts[i].Y - minY) / size;
                scaled[i] = new Vector2(qx, qy);
                cx += qx; cy += qy;
            }
            cx /= pts.Length; cy /= pts.Length;
            var output = new Vector2[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                output[i] = new Vector2(scaled[i].X - cx, scaled[i].Y - cy);
            return output;
        }

        /// <summary>Builds the multi-finger point cloud for matching:
        /// resamples each finger's path to <paramref name="perFinger"/>
        /// points, concatenates, then normalizes the combined cloud.</summary>
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

        // ─────────────────────────────────────────────────────────
        //  Lookup table (canonical $Q)
        //
        //  For each cell (gx, gy) of a LutSize × LutSize grid covering
        //  the integer coordinate range [0, MaxIntCoord-1], store the
        //  index of the template point whose own integer coords map
        //  to the closest grid cell. Used by ComputeLowerBound to
        //  estimate "how close could the candidate be to the template
        //  at this starting position?" in O(N) per start, so
        //  CloudMatch can skip starting positions whose lower bound
        //  already exceeds the best-so-far distance.
        // ─────────────────────────────────────────────────────────

        /// <summary>Builds the $Q lookup table for a normalized template
        /// cloud. Each grid cell stores the index of the cloud point
        /// whose integer-grid coordinate is closest to that cell. Used
        /// solely by <see cref="ComputeLowerBound"/> for the cloud-
        /// match pruning pass.</summary>
        public static ushort[] BuildLookupTable(Vector2[] cloud,
            int lutSize = DefaultLookupTableSize)
        {
            if (cloud == null || cloud.Length == 0 || lutSize <= 0)
                return new ushort[0];

            // Pre-compute every cloud point's grid coordinate so the
            // per-cell loop doesn't redo the arithmetic.
            var gx = new int[cloud.Length];
            var gy = new int[cloud.Length];
            for (int i = 0; i < cloud.Length; i++)
            {
                gx[i] = (int)MathF.Round(((cloud[i].X + 1f) * 0.5f * (MaxIntCoord - 1)) / LutScaleFactor);
                gy[i] = (int)MathF.Round(((cloud[i].Y + 1f) * 0.5f * (MaxIntCoord - 1)) / LutScaleFactor);
                if (gx[i] < 0) gx[i] = 0; else if (gx[i] >= lutSize) gx[i] = lutSize - 1;
                if (gy[i] < 0) gy[i] = 0; else if (gy[i] >= lutSize) gy[i] = lutSize - 1;
            }

            var lut = new ushort[lutSize * lutSize];
            for (int x = 0; x < lutSize; x++)
            {
                for (int y = 0; y < lutSize; y++)
                {
                    int best = 0;
                    int bestSq = int.MaxValue;
                    for (int i = 0; i < cloud.Length; i++)
                    {
                        int dx = gx[i] - x;
                        int dy = gy[i] - y;
                        int d = dx * dx + dy * dy;
                        if (d < bestSq) { bestSq = d; best = i; }
                    }
                    lut[x * lutSize + y] = (ushort)best;
                }
            }
            return lut;
        }

        // ─────────────────────────────────────────────────────────
        //  CloudDistance — canonical $Q inner loop
        //
        //  Same shape as $P's: walk candidate points starting at
        //  `start`, greedily match each to the nearest unmatched
        //  template point, accumulate weight·d² with weights running
        //  from n down to 1. The matched[] array (here: unmatched
        //  index list) is what prevents a degenerate candidate from
        //  latching all its points onto a few template points — drop
        //  this and the recognizer false-positives on line-vs-2D-shape.
        //
        //  Early termination via `minSoFar`: as soon as the running
        //  sum exceeds the best distance seen across other starts /
        //  directions / templates, return early. The LUT-derived
        //  lower bounds in CloudMatch make most starts terminate
        //  during this loop.
        // ─────────────────────────────────────────────────────────

        /// <summary>$Q cloud-distance: weighted greedy-nearest-unmatched
        /// match starting at <paramref name="start"/>. Returns the
        /// accumulated weight·distance² sum, with early abandonment
        /// at <paramref name="minSoFar"/>.</summary>
        public static float CloudDistance(Vector2[] pts1, Vector2[] pts2,
            int start, float minSoFar)
        {
            int n = pts1.Length;
            if (n == 0 || pts2 == null || pts2.Length != n) return float.MaxValue;

            // Working list of unmatched indices into pts2.
            var unmatched = new int[n];
            for (int j = 0; j < n; j++) unmatched[j] = j;
            int remaining = n;

            int i = start;
            int weight = n;
            float sum = 0f;
            do
            {
                int bestSlot = -1;
                float bestD = float.MaxValue;
                for (int k = 0; k < remaining; k++)
                {
                    int idx = unmatched[k];
                    float dx = pts1[i].X - pts2[idx].X;
                    float dy = pts1[i].Y - pts2[idx].Y;
                    float d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; bestSlot = k; }
                }
                // Remove the matched template index by swap-with-last
                // so the next iteration's scan stays tight.
                unmatched[bestSlot] = unmatched[remaining - 1];
                remaining--;

                sum += weight * bestD;
                if (sum >= minSoFar) return sum; // early abandon
                weight--;
                i = (i + 1) % n;
            } while (i != start);
            return sum;
        }

        // ─────────────────────────────────────────────────────────
        //  ComputeLowerBound — LUT-driven pruning
        //
        //  For each candidate starting-position (stepped at sqrt(n)),
        //  compute a lower-bound estimate of CloudDistance using the
        //  template's LUT. Each lower-bound value is the sum, over
        //  the candidate points starting from that position, of
        //  weight·d² where d is the distance from the candidate
        //  point to the LUT-nearest template point. The LUT lookup
        //  is O(1) per candidate point, so this whole pass is O(N)
        //  per template and provides the early-skip signal that
        //  makes $Q faster than $P.
        // ─────────────────────────────────────────────────────────

        /// <summary>Computes per-starting-position lower bounds on
        /// the cloud distance, used by <see cref="CloudMatch"/> to
        /// skip starting positions that can't possibly improve on
        /// the best-so-far.</summary>
        private static float[] ComputeLowerBound(Vector2[] pts1, Vector2[] pts2,
            int step, ushort[] lut, int lutSize)
        {
            int n = pts1.Length;
            int slots = n / step + 1;
            var lb = new float[slots];
            var sat = new float[n]; // summed-area table of LUT-nearest squared distances

            // Pre-compute SAT and lb[0]: a full pass at start=0.
            lb[0] = 0f;
            for (int i = 0; i < n; i++)
            {
                int gx = (int)MathF.Round(((pts1[i].X + 1f) * 0.5f * (MaxIntCoord - 1)) / LutScaleFactor);
                int gy = (int)MathF.Round(((pts1[i].Y + 1f) * 0.5f * (MaxIntCoord - 1)) / LutScaleFactor);
                if (gx < 0) gx = 0; else if (gx >= lutSize) gx = lutSize - 1;
                if (gy < 0) gy = 0; else if (gy >= lutSize) gy = lutSize - 1;
                int idx = lut[gx * lutSize + gy];
                float dx = pts1[i].X - pts2[idx].X;
                float dy = pts1[i].Y - pts2[idx].Y;
                float d = dx * dx + dy * dy;
                sat[i] = (i == 0) ? d : sat[i - 1] + d;
                lb[0] += (n - i) * d;
            }

            // lb[j] for j = step, 2*step, ... — closed-form derivation
            // from the canonical reference: the weighted sum from a
            // shifted starting position equals lb[0] + i*sat[n-1] - n*sat[i-1].
            int slot = 1;
            for (int i = step; i < n; i += step, slot++)
                lb[slot] = lb[0] + i * sat[n - 1] - n * sat[i - 1];

            return lb;
        }

        // ─────────────────────────────────────────────────────────
        //  CloudMatch — public per-template match entry point
        //
        //  Tries cyclic starting positions stepped at sqrt(n), in
        //  BOTH directions (candidate→template and template→candidate),
        //  returns the minimum distance. The matched[]-tracked
        //  CloudDistance plus the LUT-driven lower-bound pruning is
        //  what produces the canonical $Q's correct behavior on the
        //  full corpus AND its speed.
        // ─────────────────────────────────────────────────────────

        /// <summary>Matches the candidate against one template's
        /// cloud + LUT pair. Returns the best (lowest) cloud distance
        /// found across the cyclic-start sweep in either direction.
        /// Caller supplies the candidate's LUT pre-built (once per
        /// <see cref="Match"/> call) so it isn't rebuilt per template.</summary>
        public static float CloudMatch(Vector2[] candidate,
            ushort[] candidateLut, ShapeTemplate template, float minSoFar)
        {
            if (candidate == null || template == null) return float.MaxValue;
            if (template.PointCloud == null || template.LookupTable == null) return float.MaxValue;
            if (candidate.Length != template.PointCloud.Length) return float.MaxValue;
            int n = candidate.Length;
            if (n == 0) return 0f;
            int lutSize = template.LookupTableSize;
            if (lutSize <= 0 || template.LookupTable.Length != lutSize * lutSize)
                return float.MaxValue;
            if (candidateLut == null || candidateLut.Length != lutSize * lutSize)
                return float.MaxValue;

            int step = (int)MathF.Floor(MathF.Sqrt(n));
            if (step < 1) step = 1;

            var lb1 = ComputeLowerBound(candidate, template.PointCloud, step,
                template.LookupTable, lutSize);
            var lb2 = ComputeLowerBound(template.PointCloud, candidate, step,
                candidateLut, lutSize);

            float best = minSoFar;
            int j = 0;
            for (int i = 0; i < n; i += step, j++)
            {
                if (lb1[j] < best)
                {
                    float d1 = CloudDistance(candidate, template.PointCloud, i, best);
                    if (d1 < best) best = d1;
                }
                if (lb2[j] < best)
                {
                    float d2 = CloudDistance(template.PointCloud, candidate, i, best);
                    if (d2 < best) best = d2;
                }
            }
            return best;
        }

        /// <summary>Matches <paramref name="candidate"/> against the
        /// catalog of <paramref name="templates"/>. Returns the
        /// best-matching template's name (or null when no template's
        /// distance lands under the threshold). Filters the catalog
        /// to entries whose FingerCount matches
        /// <paramref name="fingerCount"/> — multi-finger gestures only
        /// match same-finger-count templates.</summary>
        public static string Match(Vector2[] candidate,
            IReadOnlyList<ShapeTemplate> templates,
            int fingerCount, float threshold, out float bestScore)
        {
            bestScore = float.MaxValue;
            if (templates == null || candidate == null || candidate.Length == 0)
                return null;

            // Build the candidate's LUT once for the whole catalog
            // walk. CloudMatch needs it for the reverse-direction
            // lower bound, but the candidate doesn't change across
            // templates so rebuilding per template was redundant.
            var candidateLut = BuildLookupTable(candidate, DefaultLookupTableSize);

            // Track two things separately: the overall minimum
            // distance (passed as minSoFar to each CloudMatch for
            // early-abandon pruning), and the lowest threshold-passing
            // distance (whose template name we return). Per-template
            // ThresholdOverride means the threshold check has to
            // happen per template, not at the return site.
            string bestName = null;
            float bestValidScore = float.MaxValue;
            for (int t = 0; t < templates.Count; t++)
            {
                var tpl = templates[t];
                if (tpl == null || tpl.FingerCount != fingerCount) continue;
                if (!tpl.Enabled) continue;
                if (tpl.PointCloud == null || tpl.PointCloud.Length != candidate.Length) continue;
                if (tpl.LookupTable == null) continue;
                if (tpl.LookupTableSize != DefaultLookupTableSize) continue;
                float effThreshold = tpl.ThresholdOverride > 0f
                    ? tpl.ThresholdOverride : threshold;
                float d = CloudMatch(candidate, candidateLut, tpl, bestScore);
                if (d < bestScore) bestScore = d;
                if (d <= effThreshold && d < bestValidScore)
                {
                    bestValidScore = d;
                    bestName = tpl.Name;
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
