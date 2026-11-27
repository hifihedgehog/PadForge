using System.Collections.Generic;
using System.Windows.Input;
using PadForge.Common;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The curve editor's mouse and Shift-arrow edits on a point list, with
    /// no window. A move between neighbors that leave no room used to throw
    /// from Math.Clamp (lower bound above the upper), and a left-half
    /// double-click on a stick chart used to land on the center line.
    /// </summary>
    public class CurveEditorPointEditTests
    {
        private static List<(double X, double Y)> Points(params (double X, double Y)[] p) => new(p);

        [Fact]
        public void ADrag_BetweenNeighborsAHundredthApart_KeepsTheInputAndMovesTheOutput()
        {
            var points = Points((0, 0), (0.50, 0.2), (0.505, 0.4), (0.51, 0.6), (1, 1));
            CurveEditor.DragPoint(points, 2, 0.9, 0.7);
            Assert.Equal((0.505, 0.7), points[2]);
        }

        [Fact]
        public void ADrag_BetweenNeighborsTwoHundredthsApart_DoesNotThrow()
        {
            // 0.03 - 0.01 is just under 0.02 in doubles.
            var points = Points((0, 0), (0.01, 0.1), (0.02, 0.2), (0.03, 0.3), (1, 1));
            CurveEditor.DragPoint(points, 2, 0.5, 0.25);
            Assert.Equal(0.02, points[2].X, 9);
            Assert.Equal(0.25, points[2].Y, 9);
        }

        [Fact]
        public void ADrag_WithRoom_StaysAHundredthShortOfEachNeighbor()
        {
            var points = Points((0, 0), (0.5, 0.5), (1, 1));
            CurveEditor.DragPoint(points, 1, -2, 0.5);
            Assert.Equal(0.01, points[1].X, 9);
            CurveEditor.DragPoint(points, 1, 2, 1.5);
            Assert.Equal((0.99, 1.0), points[1]);
        }

        [Theory]
        [InlineData(Key.Left)]
        [InlineData(Key.Right)]
        public void ShiftArrows_BetweenNeighborsAHundredthApart_KeepThePoint(Key key)
        {
            var points = Points((0, 0), (0.50, 0.2), (0.505, 0.4), (0.51, 0.6), (1, 1));
            int index = 2;
            Assert.Equal(CurveEditor.KeyResult.Selected,
                CurveEditor.ApplyKey(points, ref index, ModifierKeys.Shift, key, x => x));
            Assert.Equal((0.505, 0.4), points[2]);
        }

        [Fact]
        public void ALeftHalfDoubleClick_OnAStickChart_LandsAtItsOwnInput()
        {
            // The left half draws the curve mirrored through the center, so a
            // click at (-0.5, -0.3) is the point (0.5, 0.3).
            var points = Points((0, 0), (1, 1));
            int at = CurveEditor.AddPoint(points, -0.5, -0.3, isSigned: true);
            Assert.Equal(1, at);
            Assert.Equal(0.5, points[1].X, 9);
            Assert.Equal(0.3, points[1].Y, 9);
        }

        [Fact]
        public void ADoubleClick_OnAnExistingInput_DoesNotStackTwoPoints()
        {
            var points = Points((0, 0), (0.5, 0.5), (1, 1));
            int at = CurveEditor.AddPoint(points, 0.5, 0.2, isSigned: false);
            Assert.Equal(4, points.Count);
            Assert.NotEqual(points[at - 1].X, points[at].X);
            Assert.NotEqual(points[at + 1].X, points[at].X);
        }

        [Fact]
        public void ADoubleClick_WithNoRoomBetweenNeighbors_AddsNothing()
        {
            var points = Points((0, 0), (0.015, 0.1), (1, 1));
            Assert.Equal(-1, CurveEditor.AddPoint(points, 0.008, 0.05, isSigned: false));
            Assert.Equal(3, points.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ADoubleClick_AtInputZero_AddsAPointAHundredthIn(bool isSigned)
        {
            // The first pixel column of an unsigned chart, and a stick
            // chart's center column, are input 0 exactly.
            var points = Points((0, 0), (1, 1));
            Assert.Equal(1, CurveEditor.AddPoint(points, 0, 0.3, isSigned));
            Assert.Equal(0.01, points[1].X, 9);
            Assert.Equal(0.3, points[1].Y, 9);
        }

        [Fact]
        public void ADoubleClick_PastTheLastInput_AddsAPointAHundredthShortOfIt()
        {
            // A saved curve can end at input 0.999, left of the chart's last column.
            var points = CurveLut.Parse("0,0;0.5,0.5;0.999,1");
            Assert.Equal(2, CurveEditor.AddPoint(points, 1, 0.8, isSigned: false));
            Assert.Equal(0.989, points[2].X, 9);
            Assert.Equal(4, points.Count);
        }

        [Fact]
        public void TheRecordedReplay_TwoLeftHalfDoubleClicks_GiveTwoPointsThatDrag()
        {
            // Pixel clicks from the 382 by 150 plot of the recorded replay.
            double w = 382, h = 150;
            (double, double) Curve(double px, double py) => ((px - w / 2) / (w / 2), (h / 2 - py) / (h / 2));
            var points = Points((0, 0), (1, 1));
            var (x1, y1) = Curve(w * 0.20, h * 0.15);
            var (x2, y2) = Curve(w * 0.30, h * 0.35);
            CurveEditor.AddPoint(points, x1, y1, isSigned: true);
            CurveEditor.AddPoint(points, x2, y2, isSigned: true);
            Assert.Equal(4, points.Count);
            Assert.Equal(0.4, points[1].X, 9);
            Assert.Equal(0.6, points[2].X, 9);

            var (dx, dy) = Curve(w * 0.60, h * 0.30);
            CurveEditor.DragPoint(points, 1, dx, dy);
            CurveEditor.DragPoint(points, 2, dx, dy);
            Assert.True(points[0].X < points[1].X && points[1].X < points[2].X && points[2].X < points[3].X);
        }

        [Fact]
        public void TheRecordedReplay_TwoPointsSavedAtOneInput_DragWithoutThrowing()
        {
            // What two left-half double-clicks saved before: both at 0.01.
            var points = CurveLut.Parse("0,0;0.010,0.300;0.010,0.700;1,1");
            Assert.Equal(4, points.Count);
            CurveEditor.DragPoint(points, 1, 0.2, 0.5);
            Assert.Equal(0.01, points[1].X, 9);
            Assert.Equal(0.5, points[1].Y, 9);
            CurveEditor.DragPoint(points, 2, 0.2, 0.5);
            Assert.Equal((0.2, 0.5), points[2]);
        }
    }

    /// <summary>Two saved points at one input are a step in the curve. The
    /// table stays finite and the output jumps at that input.</summary>
    public class CurveLutStackedPointTests
    {
        [Fact]
        public void TwoPointsAtOneInput_BuildAFiniteTable()
        {
            const string curve = "0,0;0.500,0.200;0.500,0.800;1,1";
            Assert.Equal(4, CurveLut.Parse(curve).Count);
            double[] lut = CurveLut.GetOrBuild(curve);
            foreach (double v in lut)
                Assert.True(double.IsFinite(v) && v >= 0 && v <= 1);
            Assert.True(CurveLut.Lookup(lut, 0.45) < 0.25);
            Assert.True(CurveLut.Lookup(lut, 0.55) > 0.75);
        }
    }
}
