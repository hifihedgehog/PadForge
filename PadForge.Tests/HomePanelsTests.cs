using System;
using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The panel Home is laid out with (#574). TileGridPanel puts the
    /// controller cards and the extras' tiles in rows of equal columns that
    /// end flush with the page.
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

        /// <summary>A tile that takes a whole row ends the row before it,
        /// stands alone, and the tile after it starts a new row.</summary>
        [Theory]
        [InlineData(new[] { false, false, false, false, false }, 2, new[] { 2, 2, 1 })]
        [InlineData(new[] { false, true, false, false, false }, 2, new[] { 1, 1, 2, 1 })]
        [InlineData(new[] { true, true, false }, 3, new[] { 1, 1, 1 })]
        [InlineData(new[] { false, false, true }, 2, new[] { 2, 1 })]
        [InlineData(new[] { false, false, false, true }, 2, new[] { 2, 1, 1 })]
        [InlineData(new[] { false, false, false }, 1, new[] { 1, 1, 1 })]
        [InlineData(new[] { false, false, false }, 0, new[] { 1, 1, 1 })]
        [InlineData(new bool[0], 3, new int[0])]
        public void ATileThatTakesAWholeRow_StandsAlone(bool[] fullRow, int columns, int[] rows)
            => Assert.Equal(rows, TileGridPanel.Rows(fullRow, columns));

        /// <summary>Laid out, as the options under the picture's bar are: a
        /// whole-row tile is the panel's width whatever the columns, the
        /// row cut short before it fills when rows fill, and a collapsed
        /// one takes no row. With one column every tile is a row, which is
        /// the same options in the list.</summary>
        [Fact]
        public void AWholeRowTile_IsThePanelsWidth_AndTheRowBeforeItEndsThere() => RunSta(() =>
        {
            var panel = new TileGridPanel { MinItemWidth = 380, Gap = 40, RowGap = 0, FillRows = true };
            var a = new Border { Height = 44 };
            var b = new Border { Height = 44 };
            var c = new Border { Height = 44 };
            var wide = new Border { Height = 100 };
            var d = new Border { Height = 44 };
            TileGridPanel.SetFullRow(wide, true);
            foreach (var tile in new[] { a, b, c, wide, d }) panel.Children.Add(tile);

            // 800 holds two columns of 380 with 40 between them.
            panel.Measure(new Size(800, double.PositiveInfinity));
            Assert.Equal(new Size(800, 44 + 44 + 100 + 44), panel.DesiredSize);
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Rect(0, 0, 380, 44), LayoutInformation.GetLayoutSlot(a));
            Assert.Equal(new Rect(420, 0, 380, 44), LayoutInformation.GetLayoutSlot(b));
            Assert.Equal(new Rect(0, 44, 800, 44), LayoutInformation.GetLayoutSlot(c));
            Assert.Equal(new Rect(0, 88, 800, 100), LayoutInformation.GetLayoutSlot(wide));
            Assert.Equal(new Rect(0, 188, 800, 44), LayoutInformation.GetLayoutSlot(d));

            // Rows that do not fill: a tile keeps its column, and the whole
            // row is still the panel's width.
            panel.FillRows = false;
            panel.Measure(new Size(800, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Rect(0, 44, 380, 44), LayoutInformation.GetLayoutSlot(c));
            Assert.Equal(new Rect(0, 88, 800, 100), LayoutInformation.GetLayoutSlot(wide));

            // One column: every tile is a row.
            panel.MaxColumns = 1;
            panel.Measure(new Size(800, double.PositiveInfinity));
            Assert.Equal(new Size(800, 44 * 4 + 100), panel.DesiredSize);
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Rect(0, 44, 800, 44), LayoutInformation.GetLayoutSlot(b));
            Assert.Equal(new Rect(0, 132, 800, 100), LayoutInformation.GetLayoutSlot(wide));
            Assert.Equal(new Rect(0, 232, 800, 44), LayoutInformation.GetLayoutSlot(d));

            // A collapsed whole-row tile takes no row: the tiles around it
            // close up.
            panel.MaxColumns = int.MaxValue;
            panel.FillRows = true;
            wide.Visibility = Visibility.Collapsed;
            panel.Measure(new Size(800, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));
            Assert.Equal(new Size(800, 88), panel.DesiredSize);
            Assert.Equal(new Rect(0, 44, 380, 44), LayoutInformation.GetLayoutSlot(c));
            Assert.Equal(new Rect(420, 44, 380, 44), LayoutInformation.GetLayoutSlot(d));

            // With no width to go by a whole row is one column wide.
            Assert.Equal(380, TileGridPanel.FullRowWidth(double.PositiveInfinity, 380));
            Assert.Equal(380, TileGridPanel.FullRowWidth(double.NaN, 380));
            Assert.Equal(800, TileGridPanel.FullRowWidth(800, 380));
        });

        // ── a balanced set, and the tile that takes what is left ───────

        /// <summary>A short, fixed set is shared out evenly between its
        /// rows: as few columns as still need no more rows than the columns
        /// that fit would. A set that fits one row has a column each.</summary>
        [Theory]
        // Four tiles where three columns fit stand two and two.
        [InlineData(4, 3, 2)]
        [InlineData(4, 4, 4)]
        [InlineData(4, 2, 2)]
        [InlineData(4, 1, 1)]
        [InlineData(5, 4, 3)]
        [InlineData(6, 4, 3)]
        [InlineData(7, 4, 4)]
        [InlineData(3, 5, 3)]
        // Nothing to share out: the columns that fit.
        [InlineData(0, 3, 3)]
        public void ABalancedSet_UsesAsFewColumnsAsNeedNoMoreRows(int count, int columns, int expected)
            => Assert.Equal(expected, TileGridPanel.BalancedColumns(count, columns));

        [Fact]
        public void ABalancedGrid_SharesTheWidthAmongItsFewerColumns()
        {
            // 764 holds three columns of 230 with 12 between them. Four
            // tiles then stand two and two, each half the width.
            var (columns, item) = TileGridPanel.ColumnsFor(764, 230, 12, int.MaxValue, 4, balance: true);
            Assert.Equal(2, columns);
            Assert.Equal((764 - 12) / 2.0, item, 6);
            // Unbalanced, the columns that fit.
            (columns, item) = TileGridPanel.ColumnsFor(764, 230, 12, int.MaxValue, 4, balance: false);
            Assert.Equal(3, columns);
            Assert.Equal((764 - 24) / 3.0, item, 6);
            // Wide enough for all four: one row, as without it.
            (columns, item) = TileGridPanel.ColumnsFor(1104, 230, 12, int.MaxValue, 4, balance: true);
            Assert.Equal(4, columns);
            Assert.Equal((1104 - 36) / 4.0, item, 6);
            // No width to go by: the narrowest width, as without it.
            (columns, item) = TileGridPanel.ColumnsFor(double.PositiveInfinity, 230, 12, 4, 4, balance: true);
            Assert.Equal(4, columns);
            Assert.Equal(230, item);
        }

        [Theory]
        // The last of two tiles in a row of four columns takes three of them.
        [InlineData(1348.0, 4, 2, 325.0, 1007.0)]
        // Alone on its row it takes them all.
        [InlineData(1348.0, 4, 1, 325.0, 1348.0)]
        // A full row leaves it its column.
        [InlineData(1348.0, 4, 4, 325.0, 325.0)]
        // No width to go by: its column.
        [InlineData(double.PositiveInfinity, 4, 1, 300.0, 300.0)]
        [InlineData(double.NaN, 4, 1, 300.0, 300.0)]
        public void ATileThatTakesTheRest_RunsThroughTheColumnsLeft(double width, int columns, int inRow, double itemWidth, double expected)
            => Assert.Equal(expected, TileGridPanel.RestWidth(width, 16, columns, inRow, itemWidth), 6);

        /// <summary>Laid out, as the Profiles page's cards and drop tile
        /// are. The tile is the last of its row and runs on to the row's
        /// end, so no column stands empty. In a row with cards it is as
        /// tall as they are, and alone on a line it is as tall as itself.
        /// A tile that is not the last of its row keeps its column.</summary>
        [Fact]
        public void TheLastTile_TakesWhatIsLeftOfItsRow() => RunSta(() =>
        {
            var panel = new TileGridPanel { MinItemWidth = 300, Gap = 16 };
            var a = new Border { Height = 167 };
            var b = new Border { Height = 167 };
            var drop = new Border { MinHeight = 107 };
            TileGridPanel.SetTakesRest(drop, true);
            Assert.True(TileGridPanel.GetTakesRest(drop));
            foreach (var tile in new[] { a, b, drop }) panel.Children.Add(tile);

            void Lay(double width)
            {
                panel.Measure(new Size(width, double.PositiveInfinity));
                panel.Arrange(new Rect(panel.DesiredSize));
            }
            void Is(FrameworkElement tile, double x, double y, double width, double height)
            {
                var slot = LayoutInformation.GetLayoutSlot(tile);
                Assert.Equal(x, slot.X, 6);
                Assert.Equal(y, slot.Y, 6);
                Assert.Equal(width, slot.Width, 6);
                Assert.Equal(height, slot.Height, 6);
            }

            // 1104 holds three columns: two cards and the tile in the third.
            double column = (1104 - 32) / 3.0;
            Lay(1104);
            Is(a, 0, 0, column, 167);
            Is(b, column + 16, 0, column, 167);
            Is(drop, 2 * (column + 16), 0, column, 167);

            // One card: the tile runs through the two columns left.
            b.Visibility = Visibility.Collapsed;
            Lay(1104);
            Is(drop, column + 16, 0, 2 * column + 16, 167);
            b.Visibility = Visibility.Visible;

            // 764 holds two columns: the cards fill their line, and the tile
            // is a band under them, the panel's width and its own height.
            Lay(764);
            Is(b, 390, 0, 374, 167);
            Is(drop, 0, 183, 764, 107);
            Assert.Equal(new Size(764, 167 + 16 + 107), panel.DesiredSize);

            // Rows that share their width already leave nothing over.
            panel.FillRows = true;
            Lay(764);
            Is(drop, 0, 183, 764, 107);
            b.Visibility = Visibility.Collapsed;
            Lay(764);
            Is(a, 0, 0, 374, 167);
            Is(drop, 390, 0, 374, 167);
            panel.FillRows = false;
            b.Visibility = Visibility.Visible;

            // Not the last of its row: its column, like any tile.
            TileGridPanel.SetTakesRest(drop, false);
            TileGridPanel.SetTakesRest(a, true);
            Lay(1104);
            Is(a, 0, 0, column, 167);
            Is(drop, 2 * (column + 16), 0, column, 167);
        });

        /// <summary>A window's resize measures and arranges its root at the
        /// new size and only then runs the layout pass, as HwndSource does
        /// on WM_SIZE. When that arrange reaches the grid while the measure
        /// for its new tiles still waits in the layout queue, WPF measures
        /// the grid inside its own Arrange and does not tell the grid's
        /// parent. The list kept the height of its old tiles, and under the
        /// picture the picked control's blocks had no room (#574,
        /// 2026-11-12). The grid tells its parent itself.</summary>
        [Fact]
        public void TheGrid_TellsItsListOfNewTiles_WhenAResizeArrangesItFirst() => RunSta(() =>
        {
            var host = new FrameworkElementFactory(typeof(TileGridPanel));
            host.SetValue(TileGridPanel.MinItemWidthProperty, 400.0);
            var tile = new FrameworkElementFactory(typeof(Border));
            tile.SetValue(FrameworkElement.HeightProperty, 40.0);
            var tiles = new ObservableCollection<string> { "A" };
            var list = new ItemsControl
            {
                ItemsSource = tiles,
                ItemsPanel = new ItemsPanelTemplate(host),
                ItemTemplate = new DataTemplate { VisualTree = tile },
            };
            // A page that scrolls: the list is measured with no bound on its
            // height and arranged at least as tall as the view, so a taller
            // window arranges it anew and measures it as before.
            var page = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = list };
            void Resize(double height)
            {
                page.Measure(new Size(400, height));
                page.Arrange(new Rect(0, 0, 400, height));
                page.UpdateLayout();
            }
            Resize(300);
            Assert.Equal(40, list.DesiredSize.Height);

            // Two tiles come, and the window grows before the layout pass.
            tiles.Add("B");
            tiles.Add("C");
            Resize(500);
            var grid = (TileGridPanel)VisualTreeHelper.GetParent(list.ItemContainerGenerator.ContainerFromIndex(0));
            // One column of 400: three rows of 40 with the gap between them.
            Assert.Equal(40 * 3 + 16 * 2, grid.DesiredSize.Height);
            Assert.Equal(grid.DesiredSize.Height, list.DesiredSize.Height);
        });
    }
}
