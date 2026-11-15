using System;
using System.Linq;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Where a column of the picture's legend puts its chips
    /// (<see cref="LegendColumn"/>). A chip stands beside its control, chips
    /// never overlap, and the column keeps clear of the buttons in the
    /// picture's corners. A picked control's bar leaves the picture about
    /// 320 tall in a window 1040 tall, and eleven chips at the usual gap
    /// ran under Reset View there (seen 2026-11-15): the gap closes up
    /// first.
    /// </summary>
    public class LegendColumnTests
    {
        private const double Chip = LegendColumn.ChipHeight, Gap = LegendColumn.Gap, Edge = LegendColumn.EdgeMargin;

        /// <summary>The layout before the column knew of the corners: down
        /// from the edge's margin at the usual gap, then up from the foot.</summary>
        private static double[] Before(double[] anchors, double height)
        {
            var y = new double[anchors.Length];
            double previousBottom = Edge - Gap;
            for (int i = 0; i < y.Length; i++)
            {
                y[i] = Math.Max(anchors[i] - Chip / 2, previousBottom + Gap);
                previousBottom = y[i] + Chip;
            }
            if (y[^1] + Chip > height - Edge)
            {
                y[^1] = height - Edge - Chip;
                for (int i = y.Length - 2; i >= 0; i--) y[i] = Math.Min(y[i], y[i + 1] - Chip - Gap);
                y[0] = Math.Max(y[0], Edge);
            }
            return y;
        }

        private static void AssertClose(double[] expected, double[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], 6);
        }

        [Fact]
        public void TheNumbersAreTheLegendsOwn()
        {
            Assert.Equal((20.0, 6.0, 2.0, 8.0), (LegendColumn.ChipHeight, LegendColumn.Gap, LegendColumn.LeastGap, LegendColumn.EdgeMargin));
            Assert.Empty(LegendColumn.Tops(Array.Empty<double>(), 400, 44));
            Assert.Empty(LegendColumn.Tops(null, 400, 44));
        }

        /// <summary>In a tall picture a chip stands beside its control, a
        /// chip is pushed down by the one over it, and a column that would
        /// run past the foot is pushed up from the foot.</summary>
        [Fact]
        public void InATallPictureAChipStandsBesideItsControl()
        {
            AssertClose(new[] { 90.0, 190, 290 }, LegendColumn.Tops(new[] { 100.0, 200, 300 }, 600, 44));
            AssertClose(new[] { 90.0, 116, 142 }, LegendColumn.Tops(new[] { 100.0, 105, 110 }, 600, 44));
            AssertClose(new[] { 520.0, 546, 572 }, LegendColumn.Tops(new[] { 570.0, 580, 590 }, 600, 44));
            // One chip, low and high.
            AssertClose(new[] { 572.0 }, LegendColumn.Tops(new[] { 599.0 }, 600, 44));
            AssertClose(new[] { 290.0 }, LegendColumn.Tops(new[] { 300.0 }, 600, 44));

            // With no control up in a corner's band, the column stands
            // where it stood before it knew of the corners.
            foreach (var anchors in new[]
                     {
                         new[] { 60.0, 61, 62, 300, 301, 580, 590 },
                         new[] { 200.0, 210, 220, 230, 240, 250, 260, 270, 280, 290, 300 },
                         new[] { 70.0, 500 },
                     })
                AssertClose(Before(anchors, 600), LegendColumn.Tops(anchors, 600, 44));
        }

        /// <summary>A control drawn up in a corner's band has its chip a
        /// gap under the buttons. With no buttons the chip stands at the
        /// edge's margin.</summary>
        [Fact]
        public void AColumnKeepsClearOfTheCornersButtons()
        {
            AssertClose(new[] { 50.0, 76 }, LegendColumn.Tops(new[] { 20.0, 30 }, 600, 44));
            AssertClose(new[] { 50.0 }, LegendColumn.Tops(new[] { 5.0 }, 600, 44));
            AssertClose(new[] { 10.0, 36 }, LegendColumn.Tops(new[] { 20.0, 30 }, 600, 0));
            AssertClose(new[] { 8.0, 34 }, LegendColumn.Tops(new[] { 5.0, 6 }, 600, 0));
            // Buttons that end inside the edge's margin change nothing.
            AssertClose(new[] { 8.0, 34 }, LegendColumn.Tops(new[] { 5.0, 6 }, 600, 1));
        }

        /// <summary>The window the defect was seen in: 1040 tall, a
        /// control picked, the picture 322 tall, eleven chips at the
        /// right. The gap closes up and the column stands between the
        /// buttons and the foot. At the usual gap it started 34 down,
        /// under buttons that end 44 down.</summary>
        [Fact]
        public void InAShortPictureTheGapClosesUp_BeforeAChipGoesUnderAButton()
        {
            var anchors = Enumerable.Range(0, 11).Select(i => 120.0 + i * 8).ToArray();
            var tops = LegendColumn.Tops(anchors, 322, 44);
            Assert.Equal(50, tops[0], 6);
            Assert.Equal(294, tops[10], 6);
            for (int i = 1; i < tops.Length; i++) Assert.Equal(24.4, tops[i] - tops[i - 1], 6);
            // Before: the first chip 34 down.
            Assert.Equal(34, Before(anchors, 322)[0], 6);
            Assert.Equal(34, LegendColumn.Tops(anchors, 322, 0)[0], 6);

            // Ten chips have room under the buttons at the usual gap.
            var ten = LegendColumn.Tops(anchors.Take(10).ToArray(), 322, 44);
            Assert.Equal(60, ten[0], 6);
            Assert.Equal(294, ten[9], 6);
            for (int i = 1; i < ten.Length; i++) Assert.Equal(26, ten[i] - ten[i - 1], 6);

            // The gap never closes past the least one while the column
            // stands under the buttons: 11 chips need 240 there.
            var tight = LegendColumn.Tops(anchors, 298, 44);
            Assert.Equal(50, tight[0], 6);
            Assert.Equal(22, tight[1] - tight[0], 6);
            Assert.Equal(270, tight[10], 6);
        }

        /// <summary>A picture too short for the column under the buttons,
        /// even at the least gap: the column takes the corner's room too,
        /// with the widest gap that fits.</summary>
        [Fact]
        public void InAPictureTooShortEvenForThat_TheColumnTakesTheCornersRoom()
        {
            var anchors = Enumerable.Range(0, 11).Select(i => 120.0 + i * 8).ToArray();
            var tops = LegendColumn.Tops(anchors, 290, 44);
            Assert.Equal(8, tops[0], 6);
            Assert.Equal(262, tops[10], 6);
            for (int i = 1; i < tops.Length; i++) Assert.Equal(25.4, tops[i] - tops[i - 1], 6);

            // Room for the usual gap from the edge: 11 chips need 280.
            var roomy = LegendColumn.Tops(anchors, 297, 44);
            Assert.Equal(26, roomy[1] - roomy[0], 6);
            Assert.True(roomy[0] >= 8);
            Assert.Equal(269, roomy[10], 6);

            // The least picture, 200 tall, has no room for eleven chips at
            // any gap. The first chip keeps to the edge's margin and the
            // last to the foot, as before.
            var least = LegendColumn.Tops(anchors, 200, 44);
            Assert.Equal(8, least[0], 6);
            Assert.Equal(172, least[10], 6);
        }
    }
}
