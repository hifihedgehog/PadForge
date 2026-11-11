using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The two panels Home is laid out with (#574). TileGridPanel puts the
    /// controller cards and the extras' tiles in rows of equal columns that
    /// end flush with the page. LineOrStackPanel keeps a card's state and
    /// its device counts on one line while they fit and stacks them when
    /// they do not, so a long state or a long translation is never cut off.
    /// </summary>
    public class HomePanelsTests
    {
        /// <summary>WPF elements need an STA thread.</summary>
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(30000), "the panel test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        // ── the tile grid ──────────────────────────────────────────────

        [Theory]
        // As many columns as fit at the narrowest width, with the gap between them.
        [InlineData(252.0, 1, 252.0)]
        [InlineData(519.0, 1, 519.0)]
        [InlineData(520.0, 2, 252.0)]
        [InlineData(788.0, 3, 252.0)]
        // The columns share what is left, so a row ends at the panel's edge.
        [InlineData(600.0, 2, 292.0)]
        [InlineData(1393.0, 5, 265.8)]
        // Narrower than one tile: still one column, as wide as there is room for.
        [InlineData(200.0, 1, 200.0)]
        [InlineData(0.0, 1, 0.0)]
        public void TheGrid_HoldsAsManyColumnsAsFit_AndSharesTheWidth(double width, int columns, double itemWidth)
        {
            var (count, item) = TileGridPanel.Columns(width, 252, 16, int.MaxValue);
            Assert.Equal(columns, count);
            Assert.Equal(itemWidth, item, 3);
            // Every column and every gap, and nothing left over.
            if (width >= 252)
                Assert.Equal(width, count * item + (count - 1) * 16, 6);
        }

        [Fact]
        public void TheGrid_StopsAtItsMostColumns_AndTakesTheNarrowestWidthWithNoWidthToGoBy()
        {
            var (count, item) = TileGridPanel.Columns(2000, 252, 16, 3);
            Assert.Equal(3, count);
            Assert.Equal((2000 - 2 * 16) / 3.0, item, 6);

            // An unbounded measure: one row of tiles at their narrowest.
            (count, item) = TileGridPanel.Columns(double.PositiveInfinity, 252, 16, 4);
            Assert.Equal(4, count);
            Assert.Equal(252, item);
            (count, item) = TileGridPanel.Columns(double.NaN, 252, 16, 4);
            Assert.Equal(4, count);
            Assert.Equal(252, item);

            // A width that is no width asks for no division by nothing.
            (count, item) = TileGridPanel.Columns(500, 0, 16, int.MaxValue);
            Assert.True(count >= 1 && item >= 0);
        }

        [Theory]
        // A row that fills shares the whole width among its tiles.
        [InlineData(1088.0, 2, 260.0, true, 536.0)]
        [InlineData(1088.0, 1, 260.0, true, 1088.0)]
        [InlineData(1088.0, 4, 260.0, true, 260.0)]
        // One that does not keeps the column's width.
        [InlineData(1088.0, 2, 260.0, false, 260.0)]
        // With no width to share, the tiles keep their narrowest width.
        [InlineData(double.PositiveInfinity, 2, 252.0, true, 252.0)]
        [InlineData(double.NaN, 2, 252.0, true, 252.0)]
        public void ARowThatFills_SharesTheWholeWidthAmongItsTiles(double width, int inRow, double itemWidth, bool fill, double expected)
            => Assert.Equal(expected, TileGridPanel.RowItemWidth(width, 16, inRow, itemWidth, fill), 6);

        /// <summary>Laid out: the tiles of a row are as tall as its tallest,
        /// rows are a gap apart, and a collapsed tile takes no cell, so the
        /// tiles after it close up.</summary>
        [Fact]
        public void TheGrid_MakesARowAsTallAsItsTallestTile_AndSkipsACollapsedOne() => RunSta(() =>
        {
            var panel = new TileGridPanel { MinItemWidth = 252, Gap = 16 };
            var a = new Border { Height = 40 };
            var hidden = new Border { Height = 300, Visibility = Visibility.Collapsed };
            var b = new Border { Height = 60 };
            var c = new Border { Height = 50 };
            foreach (var tile in new[] { a, hidden, b, c }) panel.Children.Add(tile);

            // 536 holds two columns of 260 with the gap between them.
            panel.Measure(new Size(536, double.PositiveInfinity));
            Assert.Equal(new Size(536, 60 + 16 + 50), panel.DesiredSize);
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Rect(0, 0, 260, 60), LayoutInformation.GetLayoutSlot(a));
            Assert.Equal(new Rect(276, 0, 260, 60), LayoutInformation.GetLayoutSlot(b));
            Assert.Equal(new Rect(0, 76, 260, 50), LayoutInformation.GetLayoutSlot(c));

            // Rows can stand another distance apart than columns do.
            Assert.Equal(16, TileGridPanel.RowGapFor(double.NaN, 16));
            Assert.Equal(24, TileGridPanel.RowGapFor(24, 16));
            panel.RowGap = 24;
            panel.Measure(new Size(536, double.PositiveInfinity));
            Assert.Equal(new Size(536, 60 + 24 + 50), panel.DesiredSize);
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Rect(276, 0, 260, 60), LayoutInformation.GetLayoutSlot(b));
            Assert.Equal(new Rect(0, 84, 260, 50), LayoutInformation.GetLayoutSlot(c));

            // Rows that fill: the two tiles of the first row are as they
            // were, and the one tile of the second takes the whole width.
            panel.FillRows = true;
            panel.Measure(new Size(536, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Rect(0, 0, 260, 60), LayoutInformation.GetLayoutSlot(a));
            Assert.Equal(new Rect(276, 0, 260, 60), LayoutInformation.GetLayoutSlot(b));
            Assert.Equal(new Rect(0, 84, 536, 50), LayoutInformation.GetLayoutSlot(c));
            // Two tiles in a width that fits four columns: half each, and no
            // empty column.
            c.Visibility = Visibility.Collapsed;
            panel.Measure(new Size(1088, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Rect(0, 0, 536, 60), LayoutInformation.GetLayoutSlot(a));
            Assert.Equal(new Rect(552, 0, 536, 60), LayoutInformation.GetLayoutSlot(b));
            // Without it each keeps its column's 260.
            panel.FillRows = false;
            panel.Measure(new Size(1088, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Rect(276, 0, 260, 60), LayoutInformation.GetLayoutSlot(b));

            // With nothing to show the panel takes no room.
            var empty = new TileGridPanel();
            empty.Children.Add(new Border { Height = 80, Visibility = Visibility.Collapsed });
            empty.Measure(new Size(536, double.PositiveInfinity));
            Assert.Equal(new Size(0, 0), empty.DesiredSize);
        });

        // ── one line or a stack ────────────────────────────────────────

        [Theory]
        [InlineData(100.0, 120.0, true)]
        [InlineData(120.0, 120.0, true)]
        [InlineData(120.5, 120.0, false)]
        [InlineData(1000.0, double.PositiveInfinity, true)]
        [InlineData(1000.0, double.NaN, true)]
        public void ChildrenFitALine_WhileTheyNeedNoMoreThanThereIs(double needed, double available, bool fits)
        {
            Assert.Equal(fits, LineOrStackPanel.Fits(needed, available));
        }

        /// <summary>With room the children stand side by side, the separator
        /// between them. Without it they stand one under the other at the
        /// panel's full width, and the separator takes no room.</summary>
        [Fact]
        public void TheChildren_ShareALineWhileTheyFit_AndStackWithoutTheSeparatorWhenTheyDoNot() => RunSta(() =>
        {
            var panel = new LineOrStackPanel();
            var state = new Border { Width = 100, Height = 20 };
            var dot = new Border { Width = 10, Height = 20 };
            LineOrStackPanel.SetOnLineOnly(dot, true);
            var counts = new Border { Width = 120, Height = 16 };
            foreach (var child in new[] { state, dot, counts }) panel.Children.Add(child);
            Assert.True(LineOrStackPanel.GetOnLineOnly(dot));
            Assert.False(LineOrStackPanel.GetOnLineOnly(state));

            // 230 across is exactly what the three need.
            panel.Measure(new Size(230, double.PositiveInfinity));
            Assert.True(panel.IsOnLine);
            Assert.Equal(new Size(230, 20), panel.DesiredSize);
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Rect(0, 0, 100, 20), LayoutInformation.GetLayoutSlot(state));
            Assert.Equal(new Rect(100, 0, 10, 20), LayoutInformation.GetLayoutSlot(dot));
            Assert.Equal(new Rect(110, 0, 120, 20), LayoutInformation.GetLayoutSlot(counts));

            // One less and they stack: the state, then the counts under it.
            panel.Measure(new Size(229, double.PositiveInfinity));
            Assert.False(panel.IsOnLine);
            Assert.Equal(new Size(120, 36), panel.DesiredSize);
            panel.Arrange(new Rect(0, 0, 229, 36));
            Assert.Equal(new Rect(0, 0, 229, 20), LayoutInformation.GetLayoutSlot(state));
            Assert.Equal(new Rect(0, 20, 229, 16), LayoutInformation.GetLayoutSlot(counts));
            Assert.Equal(new Rect(0, 0, 0, 0), LayoutInformation.GetLayoutSlot(dot));

            // Wide again, and they are back on one line.
            panel.Measure(new Size(400, double.PositiveInfinity));
            Assert.True(panel.IsOnLine);
            Assert.Equal(new Size(230, 20), panel.DesiredSize);

            // A collapsed child is left out either way.
            counts.Visibility = Visibility.Collapsed;
            panel.Measure(new Size(105, double.PositiveInfinity));
            Assert.False(panel.IsOnLine);
            Assert.Equal(new Size(100, 20), panel.DesiredSize);
        });

        /// <summary>A text that is too long even on a line of its own trims
        /// there: stacked, each child is measured at the panel's width.</summary>
        [Fact]
        public void AStackedText_IsMeasuredAtThePanelsWidth_SoItTrims() => RunSta(() =>
        {
            var panel = new LineOrStackPanel();
            var state = new TextBlock { Text = new string('W', 80), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var counts = new TextBlock { Text = "3 mapped, 0 connected", FontSize = 12 };
            panel.Children.Add(state);
            panel.Children.Add(counts);

            panel.Measure(new Size(200, double.PositiveInfinity));
            Assert.False(panel.IsOnLine);
            Assert.True(state.DesiredSize.Width <= 200, "the long text was measured wider than the panel");
            Assert.True(panel.DesiredSize.Width <= 200);
            Assert.Equal(state.DesiredSize.Height + counts.DesiredSize.Height, panel.DesiredSize.Height, 6);
        });
    }
}
