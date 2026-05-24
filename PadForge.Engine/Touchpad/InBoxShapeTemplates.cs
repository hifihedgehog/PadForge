using System;
using System.Collections.Generic;
using System.Numerics;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// Procedural builders for the in-box shape templates that ship
    /// with PadForge. Run once at app startup to populate the in-box
    /// template catalog; user-recorded custom gestures join the same
    /// catalog at profile-load time.
    ///
    /// <para>Each shape is described as a single-finger continuous path
    /// in normalized touchpad space (0..1). Templates are resampled +
    /// normalized once via <see cref="PDollarRecognizer.BuildCloud"/>
    /// using the default N=32 so they're ready to match without re-
    /// processing per evaluation.</para>
    /// </summary>
    public static class InBoxShapeTemplates
    {
        /// <summary>Builds the full in-box shape catalog. Returns a
        /// list of <see cref="PDollarTemplate"/> ready for the
        /// gesture engine. Each entry's <c>IsCustom = false</c> so the
        /// <c>EnableShapeGestures</c> per-pad toggle gates them all
        /// uniformly.</summary>
        public static List<PDollarTemplate> Build()
        {
            var list = new List<PDollarTemplate>();
            Add(list, "Circle",     BuildCircle(true));
            Add(list, "CircleCCW",  BuildCircle(false));
            Add(list, "Square",     BuildSquare());
            Add(list, "Triangle",   BuildTriangle());
            Add(list, "Z",          BuildZ());
            Add(list, "Checkmark",  BuildCheckmark());
            Add(list, "X",          BuildXShape());
            return list;
        }

        private static void Add(List<PDollarTemplate> list, string name, List<Vector2> path)
        {
            var cloud = PDollarRecognizer.BuildCloud(new[] { path }, PDollarRecognizer.DefaultResampleCount);
            // Single-finger shapes also get an angular-margin signature
            // so the recognizer can run both algorithms and keep the
            // higher-confidence match. $P handles "rough cloud of points"
            // well; angular-margin handles "consistent stroke direction
            // at every corner" well — they're complementary on shapes
            // like Square / Z / Triangle / Checkmark.
            var angles = AngularMarginRecognizer.BuildAngleSignature(path);
            list.Add(new PDollarTemplate
            {
                Name = name,
                FingerCount = 1,
                PointCloud = cloud,
                Enabled = true,
                IsCustom = false,
                AngularSignature = angles,
            });
        }

        // ─── Procedural shape generators ─────────────────────────────

        private static List<Vector2> BuildCircle(bool clockwise)
        {
            const int steps = 64;
            var pts = new List<Vector2>(steps + 1);
            // Start at +X (right side of pad); go CW (touchpad space:
            // Y grows downward, so CW in screen coords = mathematically
            // CCW). Caller passes clockwise=true for the natural "you
            // drew a circle going clockwise on the pad" gesture.
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps * 2f * MathF.PI;
                float ang = clockwise ? t : -t;
                pts.Add(new Vector2(0.5f + 0.4f * MathF.Cos(ang),
                                    0.5f + 0.4f * MathF.Sin(ang)));
            }
            return pts;
        }

        private static List<Vector2> BuildSquare()
        {
            // 4 corners with light edge sampling.
            const int edgeSamples = 8;
            var pts = new List<Vector2>(edgeSamples * 4 + 1);
            Vector2 a = new(0.2f, 0.2f), b = new(0.8f, 0.2f),
                    c = new(0.8f, 0.8f), d = new(0.2f, 0.8f);
            AppendLerp(pts, a, b, edgeSamples);
            AppendLerp(pts, b, c, edgeSamples);
            AppendLerp(pts, c, d, edgeSamples);
            AppendLerp(pts, d, a, edgeSamples);
            return pts;
        }

        private static List<Vector2> BuildTriangle()
        {
            const int edgeSamples = 10;
            var pts = new List<Vector2>(edgeSamples * 3 + 1);
            // Equilateral, point up.
            Vector2 a = new(0.5f, 0.15f),
                    b = new(0.85f, 0.8f),
                    c = new(0.15f, 0.8f);
            AppendLerp(pts, a, b, edgeSamples);
            AppendLerp(pts, b, c, edgeSamples);
            AppendLerp(pts, c, a, edgeSamples);
            return pts;
        }

        private static List<Vector2> BuildZ()
        {
            // Top stroke L→R, diagonal R-top→L-bottom, bottom stroke L→R.
            const int edgeSamples = 12;
            var pts = new List<Vector2>(edgeSamples * 3 + 1);
            AppendLerp(pts, new(0.2f, 0.2f), new(0.8f, 0.2f), edgeSamples);
            AppendLerp(pts, new(0.8f, 0.2f), new(0.2f, 0.8f), edgeSamples);
            AppendLerp(pts, new(0.2f, 0.8f), new(0.8f, 0.8f), edgeSamples);
            return pts;
        }

        private static List<Vector2> BuildCheckmark()
        {
            // Short down-right stroke, then longer up-right stroke.
            const int edgeSamples = 12;
            var pts = new List<Vector2>(edgeSamples * 2 + 1);
            AppendLerp(pts, new(0.25f, 0.5f), new(0.4f, 0.75f), edgeSamples);
            AppendLerp(pts, new(0.4f, 0.75f), new(0.8f, 0.25f), edgeSamples);
            return pts;
        }

        private static List<Vector2> BuildXShape()
        {
            // TL→BR diagonal, then BL→TR diagonal. $P doesn't model the
            // pen-up between strokes — the algorithm just sees the union
            // of points, which is fine for a "what shape did the user
            // draw" recognizer since X looks the same as a cloud
            // regardless of which diagonal came first.
            const int edgeSamples = 12;
            var pts = new List<Vector2>(edgeSamples * 2 + 1);
            AppendLerp(pts, new(0.2f, 0.2f), new(0.8f, 0.8f), edgeSamples);
            AppendLerp(pts, new(0.2f, 0.8f), new(0.8f, 0.2f), edgeSamples);
            return pts;
        }

        private static void AppendLerp(List<Vector2> dst, Vector2 a, Vector2 b, int n)
        {
            int start = dst.Count > 0 ? 1 : 0; // skip the duplicate of last endpoint
            for (int i = start; i <= n; i++)
            {
                float t = i / (float)n;
                dst.Add(Vector2.Lerp(a, b, t));
            }
        }

        /// <summary>Enumerates the names of every in-box shape. Used by
        /// the InputChoice picker to surface available descriptors.</summary>
        public static IReadOnlyList<string> Names => _names;
        private static readonly string[] _names =
        {
            "Circle", "CircleCCW", "Square", "Triangle", "Z", "Checkmark", "X"
        };
    }
}
