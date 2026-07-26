using System;
using System.Collections.Generic;
using System.Numerics;
using PadForge.Engine.Touchpad;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round twenty-one.
    ///
    /// <para>Continuing round twenty's measured sweep. The remaining
    /// zero-coverage Engine types were PrecisionTouchpadReader (46 KB),
    /// ShapeRecognizer (27 KB) and AngularMarginRecognizer (17 KB).</para>
    ///
    /// <para>PrecisionTouchpadReader is DELIBERATELY NOT COVERED HERE and
    /// is recorded as a residual instead. Its parse entry point takes
    /// IntPtr preparsed-data and calls HidP_GetUsageValue, so it cannot be
    /// driven from a test without synthesizing a Windows HID preparsed
    /// blob. Making it testable means refactoring pure logic out of a
    /// Win32-coupled method that currently has no tests to catch a
    /// mistake, on a file whose four historical bugs were only ever caught
    /// on real hardware with five fingers. Its four documented fixes are
    /// present and were verified by reading (tip-switch usage 0x42,
    /// multi-report frame assembly, per-slot contact-ID carry, and
    /// orphaned-frame reset) but they have no regression protection.</para>
    ///
    /// <para>NO DEFECT FOUND in either recognizer. Resample allocates
    /// exactly n and back-fills the tail, so the classic $1 off-by-one
    /// cannot occur; the loop invariant also keeps its interpolation
    /// parameter inside (0, 1]. NormalizeCloud matches its docstring
    /// exactly, including the claim that a horizontal line lands at Y = 0
    /// with X spanning [-0.5, +0.5]. These pin the contracts.</para></summary>
    public class AuditJuly26RoundTwentyOneTests
    {
        private static List<Vector2> Line(int count, float x0, float y0, float x1, float y1)
        {
            var pts = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                float t = count == 1 ? 0f : i / (float)(count - 1);
                pts.Add(new Vector2(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t));
            }
            return pts;
        }

        private static List<Vector2> Circle(int count, float radius = 1f, float startRad = 0f)
        {
            var pts = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                double a = startRad + (i / (double)count) * 2.0 * Math.PI;
                pts.Add(new Vector2((float)(Math.Cos(a) * radius), (float)(Math.Sin(a) * radius)));
            }
            return pts;
        }

        // ── Resample ─────────────────────────────────────────────────

        /// <summary>THE CONTRACT: exactly n points, always. The $1 family's
        /// best-known implementation bug is a resample loop that emits n-1
        /// because floating-point accumulation makes the final step land
        /// just short, and downstream cloud comparison then reads a
        /// default-valued last point as a real sample at the origin.</summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(32)]
        [InlineData(64)]
        [InlineData(257)]
        public void Resample_ReturnsExactlyNPoints(int n)
        {
            Assert.Equal(n, ShapeRecognizer.Resample(Line(7, 0, 0, 10, 0), n).Length);
            Assert.Equal(n, ShapeRecognizer.Resample(Circle(9), n).Length);
        }

        [Fact]
        public void Resample_KeepsTheEndpoints()
        {
            var r = ShapeRecognizer.Resample(Line(5, 0, 0, 10, 0), 16);
            Assert.Equal(0f, r[0].X, 3);
            Assert.Equal(10f, r[^1].X, 3);
        }

        /// <summary>Arc-length parameterization is the entire purpose: a
        /// path drawn with a pause in the middle must resample to the same
        /// evenly spaced points as one drawn at constant speed, or a slow
        /// draw scores differently from a fast one.</summary>
        [Fact]
        public void Resample_IsEvenlySpacedRegardlessOfInputDensity()
        {
            // Same straight line, but the input clusters 20 points in the
            // first half and 2 in the second, as a mid-stroke pause does.
            var lumpy = new List<Vector2>();
            for (int i = 0; i <= 20; i++) lumpy.Add(new Vector2(i * 0.25f, 0));
            lumpy.Add(new Vector2(7.5f, 0));
            lumpy.Add(new Vector2(10f, 0));

            var r = ShapeRecognizer.Resample(lumpy, 11);

            for (int i = 0; i < r.Length; i++)
                Assert.Equal(i, r[i].X, 2);   // 0,1,2,...,10
        }

        [Fact]
        public void Resample_DegenerateInputsDoNotThrow()
        {
            Assert.Empty(ShapeRecognizer.Resample(null, 16));
            Assert.Empty(ShapeRecognizer.Resample(new List<Vector2>(), 16));
            Assert.Empty(ShapeRecognizer.Resample(Line(4, 0, 0, 1, 0), 0));
            Assert.Empty(ShapeRecognizer.Resample(Line(4, 0, 0, 1, 0), -5));

            // A single point, and a path of identical points (zero length):
            // both must fill n copies rather than divide by zero.
            var single = ShapeRecognizer.Resample(new List<Vector2> { new Vector2(3, 4) }, 5);
            Assert.Equal(5, single.Length);
            Assert.All(single, p => Assert.Equal(new Vector2(3, 4), p));

            var stalled = ShapeRecognizer.Resample(Line(6, 2, 2, 2, 2), 5);
            Assert.Equal(5, stalled.Length);
            Assert.All(stalled, p => Assert.Equal(new Vector2(2, 2), p));
        }

        // ── NormalizeCloud ───────────────────────────────────────────

        /// <summary>Centroid lands on the origin.</summary>
        [Fact]
        public void NormalizeCloud_CentresTheCentroid()
        {
            var norm = ShapeRecognizer.NormalizeCloud(
                ShapeRecognizer.Resample(Circle(24, radius: 37f), 32));

            float cx = 0, cy = 0;
            foreach (var p in norm) { cx += p.X; cy += p.Y; }
            Assert.Equal(0f, cx / norm.Length, 4);
            Assert.Equal(0f, cy / norm.Length, 4);
        }

        /// <summary>Scale is UNIFORM, so aspect ratio survives. The larger
        /// bounding-box dimension becomes exactly 1 and the smaller stays
        /// proportional. A per-axis normalize would squash a wide oval into
        /// a circle and make the two indistinguishable.</summary>
        [Fact]
        public void NormalizeCloud_ScalesUniformlyAndPreservesAspect()
        {
            // 20 wide, 10 tall.
            var pts = new Vector2[] {
                new(0, 0), new(20, 0), new(20, 10), new(0, 10), new(10, 5),
            };
            var norm = ShapeRecognizer.NormalizeCloud(pts);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in norm)
            {
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
            }

            Assert.Equal(1f, maxX - minX, 4);       // larger dim -> exactly 1
            Assert.Equal(0.5f, maxY - minY, 4);     // aspect preserved, not stretched
        }

        /// <summary>The docstring's own worked example: a horizontal line
        /// lands at Y = 0 with X spanning [-0.5, +0.5].</summary>
        [Fact]
        public void NormalizeCloud_HorizontalLineMatchesTheDocumentedShape()
        {
            var norm = ShapeRecognizer.NormalizeCloud(
                ShapeRecognizer.Resample(Line(9, 5, 100, 45, 100), 16));

            foreach (var p in norm)
            {
                Assert.Equal(0f, p.Y, 4);
                Assert.InRange(p.X, -0.5001f, 0.5001f);
            }
        }

        [Fact]
        public void NormalizeCloud_DegenerateInputsDoNotThrow()
        {
            Assert.Empty(ShapeRecognizer.NormalizeCloud(null));
            Assert.Empty(ShapeRecognizer.NormalizeCloud(new Vector2[0]));
            // All-identical points have zero extent and must not divide by it.
            var flat = ShapeRecognizer.NormalizeCloud(new Vector2[] { new(4, 4), new(4, 4) });
            Assert.Equal(2, flat.Length);
        }

        /// <summary>Translation invariance falls out of centring, and it is
        /// what lets the same gesture score identically anywhere on the
        /// pad.</summary>
        [Fact]
        public void NormalizeCloud_IsTranslationInvariant()
        {
            var here = ShapeRecognizer.NormalizeCloud(
                ShapeRecognizer.Resample(Circle(16), 32));
            var there = ShapeRecognizer.NormalizeCloud(
                ShapeRecognizer.Resample(Circle(16), 32));
            for (int i = 0; i < here.Length; i++)
            {
                Assert.Equal(here[i].X, there[i].X, 4);
                Assert.Equal(here[i].Y, there[i].Y, 4);
            }
        }

        // ── BuildCloud ───────────────────────────────────────────────

        [Fact]
        public void BuildCloud_ConcatenatesEveryFinger()
        {
            var fingers = new List<IReadOnlyList<Vector2>>
            {
                Line(5, 0, 0, 10, 0),
                Line(5, 0, 5, 10, 5),
                Line(5, 0, 9, 10, 9),
            };
            Assert.Equal(3 * 8, ShapeRecognizer.BuildCloud(fingers, 8).Length);
        }

        [Fact]
        public void BuildCloud_DegenerateInputsDoNotThrow()
        {
            Assert.Empty(ShapeRecognizer.BuildCloud(null, 8));
            Assert.Empty(ShapeRecognizer.BuildCloud(new List<IReadOnlyList<Vector2>>(), 8));
        }

        // ── AngularMarginRecognizer ──────────────────────────────────

        [Theory]
        [InlineData(8)]
        [InlineData(32)]
        [InlineData(64)]
        public void BuildAngleSignature_ReturnsExactlyTheRequestedSegments(int segments)
        {
            var sig = AngularMarginRecognizer.BuildAngleSignature(Circle(40), segments);
            Assert.Equal(segments, sig.Length);
        }

        /// <summary>A signature scores perfectly against itself. Without
        /// this identity the accept threshold means nothing.</summary>
        [Fact]
        public void Score_OfASignatureAgainstItself_IsPerfect()
        {
            var sig = AngularMarginRecognizer.BuildAngleSignature(Circle(40), 32);
            Assert.Equal(1f, AngularMarginRecognizer.Score(sig, sig), 3);
        }

        /// <summary>THE POINT OF THE CLASS. The same circle started from a
        /// different angle must still match, because a user drawing a
        /// circle does not start at a canonical place on it. If rotational
        /// search regressed, every circle gesture drawn from an unusual
        /// starting point would silently stop being recognized.</summary>
        [Fact]
        public void BestRotationalScore_MatchesTheSameShapeStartedElsewhere()
        {
            var template = AngularMarginRecognizer.BuildAngleSignature(Circle(64), 32);
            var rotated = AngularMarginRecognizer.BuildAngleSignature(
                Circle(64, startRad: (float)(Math.PI / 2)), 32);

            float best = AngularMarginRecognizer.BestRotationalScore(rotated, template);
            float unshifted = AngularMarginRecognizer.Score(rotated, template);

            Assert.True(best >= unshifted,
                $"rotational search must never score below the unshifted comparison ({best} < {unshifted})");
            Assert.True(best > AngularMarginRecognizer.DefaultAcceptScore,
                $"a quarter-turn-rotated circle scored {best}, below the accept threshold");
        }

        /// <summary>Shift 0 is the unshifted comparison, by definition.</summary>
        [Fact]
        public void ScoreShifted_AtZero_EqualsPlainScore()
        {
            var a = AngularMarginRecognizer.BuildAngleSignature(Circle(40), 32);
            var b = AngularMarginRecognizer.BuildAngleSignature(Line(20, 0, 0, 10, 4), 32);
            Assert.Equal(AngularMarginRecognizer.Score(a, b),
                         AngularMarginRecognizer.ScoreShifted(a, b, 0), 4);
        }

        /// <summary>Reversal is an involution, so the backwards-stroke
        /// support cannot drift a signature on a round trip.</summary>
        [Fact]
        public void Reversed_IsItsOwnInverse()
        {
            var sig = AngularMarginRecognizer.BuildAngleSignature(Circle(40), 32);
            var back = AngularMarginRecognizer.Reversed(AngularMarginRecognizer.Reversed(sig));

            Assert.Equal(sig.Length, back.Length);
            for (int i = 0; i < sig.Length; i++)
                Assert.Equal(sig[i], back[i], 6);
        }

        /// <summary>Degenerate paths yield a NULL signature BY DESIGN, and
        /// this test originally asserted the opposite, which is how the
        /// convention came to light.
        ///
        /// <para>Null is the sentinel for "no signature could be built".
        /// Every consumer already honours it: ScoreShifted and
        /// BestRotationalScore both null-guard to 0, Reversed returns null
        /// for null, and TouchpadCustomGesture deliberately ASSIGNS null to
        /// ShapeTemplate.AngularSignature for the multi-finger case, so the
        /// field is nullable by contract rather than by accident.</para>
        ///
        /// <para>Worth knowing before tidying either class: the two
        /// neighbouring recognizers use OPPOSITE degenerate conventions.
        /// ShapeRecognizer.Resample and BuildCloud return an EMPTY ARRAY,
        /// AngularMarginRecognizer returns NULL. Both are internally
        /// consistent, so this pins them rather than proposing a
        /// merge.</para></summary>
        [Fact]
        public void AngularSignature_DegenerateInputsReturnTheNullSentinel()
        {
            Assert.Null(AngularMarginRecognizer.BuildAngleSignature(null, 32));
            Assert.Null(AngularMarginRecognizer.BuildAngleSignature(new List<Vector2>(), 32));
            // Fewer than two points cannot define a single direction.
            Assert.Null(AngularMarginRecognizer.BuildAngleSignature(
                new List<Vector2> { new(1, 1) }, 32));
            // Fewer than four segments is rejected outright.
            Assert.Null(AngularMarginRecognizer.BuildAngleSignature(Circle(40), 3));
        }

        /// <summary>And the sentinel must stay harmless downstream: a null
        /// signature scores 0 rather than throwing on the gesture path.</summary>
        [Fact]
        public void NullSignature_ScoresZeroEverywhere()
        {
            var good = AngularMarginRecognizer.BuildAngleSignature(Circle(40), 32);
            Assert.NotNull(good);

            Assert.Equal(0f, AngularMarginRecognizer.Score(null, good));
            Assert.Equal(0f, AngularMarginRecognizer.Score(good, null));
            Assert.Equal(0f, AngularMarginRecognizer.ScoreShifted(null, null, 3));
            Assert.Equal(0f, AngularMarginRecognizer.BestRotationalScore(null, good));
            Assert.Equal(0f, AngularMarginRecognizer.BestRotationalScore(good, null));
            Assert.Null(AngularMarginRecognizer.Reversed(null));

            // Mismatched lengths are also a 0 rather than an index crash.
            var shorter = AngularMarginRecognizer.BuildAngleSignature(Circle(40), 16);
            Assert.Equal(0f, AngularMarginRecognizer.BestRotationalScore(shorter, good));
        }
    }
}
