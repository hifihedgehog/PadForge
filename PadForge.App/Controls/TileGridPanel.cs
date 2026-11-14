using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>
    /// Lays tiles out in rows of equal columns (#574). As many columns as fit
    /// at <see cref="MinItemWidth"/> with <see cref="Gap"/> between them, each
    /// column taking an equal share of the width, so the tiles of a row end
    /// flush with the panel's edge and a wider window gains columns, not
    /// gaps. Every tile of a row is as tall as the row's tallest. A collapsed
    /// tile takes no cell. With <see cref="FillRows"/> a row's tiles share
    /// the whole width, however few they are. A tile marked
    /// <see cref="FullRowProperty"/> has a row to itself at the panel's
    /// whole width. A row's last tile marked <see cref="TakesRestProperty"/>
    /// runs on through the columns the row has left. With
    /// <see cref="Balance"/> a set takes as few columns as still need no
    /// more rows, each column then wider.
    /// </summary>
    public sealed class TileGridPanel : Panel
    {
        public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
            nameof(MinItemWidth), typeof(double), typeof(TileGridPanel),
            new FrameworkPropertyMetadata(210.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The narrowest a tile gets before a column is dropped.</summary>
        public double MinItemWidth
        {
            get => (double)GetValue(MinItemWidthProperty);
            set => SetValue(MinItemWidthProperty, value);
        }

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(TileGridPanel),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The space between tiles, across and down.</summary>
        public double Gap
        {
            get => (double)GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
            nameof(RowGap), typeof(double), typeof(TileGridPanel),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The space between rows, when it is not the space
        /// between columns. Unset, rows stand <see cref="Gap"/> apart.</summary>
        public double RowGap
        {
            get => (double)GetValue(RowGapProperty);
            set => SetValue(RowGapProperty, value);
        }

        /// <summary>The space between rows: <see cref="RowGap"/> when it
        /// is set, else <see cref="Gap"/>.</summary>
        internal static double RowGapFor(double rowGap, double gap) => double.IsNaN(rowGap) ? gap : rowGap;

        public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
            nameof(MaxColumns), typeof(int), typeof(TileGridPanel),
            new FrameworkPropertyMetadata(int.MaxValue, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The most columns a row holds, however wide the panel.</summary>
        public int MaxColumns
        {
            get => (int)GetValue(MaxColumnsProperty);
            set => SetValue(MaxColumnsProperty, value);
        }

        /// <summary>How many columns a width holds, and how wide each is.
        /// With no width to go by (an unbounded measure) the tiles take
        /// their narrowest width in one row.</summary>
        internal static (int Columns, double ItemWidth) Columns(double width, double minItemWidth, double gap, int maxColumns)
        {
            if (!(minItemWidth > 0)) minItemWidth = 1;
            if (double.IsInfinity(width) || double.IsNaN(width))
                return (Math.Max(1, maxColumns), minItemWidth);
            int columns = (int)Math.Floor((width + gap) / (minItemWidth + gap));
            columns = Math.Max(1, Math.Min(columns, Math.Max(1, maxColumns)));
            double item = (width - gap * (columns - 1)) / columns;
            return (columns, Math.Max(0, item));
        }

        public static readonly DependencyProperty FillRowsProperty = DependencyProperty.Register(
            nameof(FillRows), typeof(bool), typeof(TileGridPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Whether the tiles of a row share the panel's whole
        /// width. A row with fewer tiles than the columns that fit then has
        /// wider tiles and no empty column: two groups of settings stand
        /// side by side at half the width each, and one alone takes all of
        /// it. Unset, a tile keeps its column's width and the columns after
        /// the last tile stay empty, as a row of cards wants.</summary>
        public bool FillRows
        {
            get => (bool)GetValue(FillRowsProperty);
            set => SetValue(FillRowsProperty, value);
        }

        /// <summary>The width of each tile in a row that holds
        /// <paramref name="inRow"/> tiles: the column's width, or an equal
        /// share of the whole width when rows fill and there is a width to
        /// share.</summary>
        internal static double RowItemWidth(double width, double gap, int inRow, double itemWidth, bool fill)
        {
            if (!fill || inRow < 1 || double.IsInfinity(width) || double.IsNaN(width)) return itemWidth;
            return Math.Max(0, (width - gap * (inRow - 1)) / inRow);
        }

        public static readonly DependencyProperty FullRowProperty = DependencyProperty.RegisterAttached(
            "FullRow", typeof(bool), typeof(TileGridPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static bool GetFullRow(UIElement element) => (bool)element.GetValue(FullRowProperty);

        public static void SetFullRow(UIElement element, bool value) => element.SetValue(FullRowProperty, value);

        /// <summary>How many tiles each row holds: tiles are taken in order,
        /// <paramref name="columns"/> to a row. A tile that takes a whole
        /// row ends the row before it, stands alone, and the tile after it
        /// starts a new row.</summary>
        internal static System.Collections.Generic.List<int> Rows(System.Collections.Generic.IReadOnlyList<bool> fullRow, int columns)
        {
            var rows = new System.Collections.Generic.List<int>();
            if (columns < 1) columns = 1;
            int inRow = 0;
            for (int i = 0; i < fullRow.Count; i++)
            {
                if (fullRow[i])
                {
                    if (inRow > 0) rows.Add(inRow);
                    rows.Add(1);
                    inRow = 0;
                    continue;
                }
                inRow++;
                if (inRow == columns)
                {
                    rows.Add(inRow);
                    inRow = 0;
                }
            }
            if (inRow > 0) rows.Add(inRow);
            return rows;
        }

        public static readonly DependencyProperty BalanceProperty = DependencyProperty.Register(
            nameof(Balance), typeof(bool), typeof(TileGridPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Whether a set takes as few columns as still need no more
        /// rows than the columns that fit, each column then wider: four tiles
        /// where three columns fit stand two and two, and not three and one,
        /// and two tiles where three fit take two columns of half the width.
        /// For a short, fixed set, with <see cref="FillRows"/>. Unset, every
        /// row but the last is full.</summary>
        public bool Balance
        {
            get => (bool)GetValue(BalanceProperty);
            set => SetValue(BalanceProperty, value);
        }

        /// <summary>The columns a balanced set uses: as few as still need
        /// no more rows than <paramref name="columns"/> would.</summary>
        internal static int BalancedColumns(int count, int columns)
        {
            if (count < 1 || columns < 1) return Math.Max(1, columns);
            int rows = (count + columns - 1) / columns;
            return (count + rows - 1) / rows;
        }

        /// <summary>The columns and the column width in use: what fits, or
        /// with <paramref name="balance"/> the fewer columns that share
        /// <paramref name="count"/> tiles out evenly, each then wider.</summary>
        internal static (int Columns, double ItemWidth) ColumnsFor(double width, double minItemWidth, double gap, int maxColumns, int count, bool balance)
        {
            var (columns, itemWidth) = Columns(width, minItemWidth, gap, maxColumns);
            if (!balance || double.IsInfinity(width) || double.IsNaN(width)) return (columns, itemWidth);
            int even = BalancedColumns(count, columns);
            return even == columns ? (columns, itemWidth) : (even, Math.Max(0, (width - gap * (even - 1)) / even));
        }

        public static readonly DependencyProperty TakesRestProperty = DependencyProperty.RegisterAttached(
            "TakesRest", typeof(bool), typeof(TileGridPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static bool GetTakesRest(UIElement element) => (bool)element.GetValue(TakesRestProperty);

        public static void SetTakesRest(UIElement element, bool value) => element.SetValue(TakesRestProperty, value);

        /// <summary>The width of a row's last tile when it takes what is
        /// left of the row: its own column and every column after it, with
        /// the gaps between them. A full row leaves it its column.</summary>
        internal static double RestWidth(double width, double gap, int columns, int inRow, double itemWidth)
        {
            if (double.IsInfinity(width) || double.IsNaN(width) || inRow >= columns) return itemWidth;
            int spans = columns - inRow + 1;
            return spans * itemWidth + (spans - 1) * gap;
        }

        /// <summary>The width of a tile that takes a whole row: the panel's
        /// width, or one column's when there is no width to go by.</summary>
        internal static double FullRowWidth(double width, double itemWidth)
            => double.IsInfinity(width) || double.IsNaN(width) ? itemWidth : Math.Max(0, width);

        private static bool[] FullRows(System.Collections.Generic.List<UIElement> shown)
        {
            var full = new bool[shown.Count];
            for (int i = 0; i < shown.Count; i++) full[i] = GetFullRow(shown[i]);
            return full;
        }

        /// <summary>The tiles that show, in order.</summary>
        private System.Collections.Generic.List<UIElement> Shown()
        {
            var shown = new System.Collections.Generic.List<UIElement>(InternalChildren.Count);
            foreach (UIElement child in InternalChildren)
                if (child != null && child.Visibility != Visibility.Collapsed)
                    shown.Add(child);
            return shown;
        }

        /// <summary>The width of the last tile of a row: the row's tile
        /// width, or what is left of the row for a tile that takes it. A
        /// row whose tiles share the whole width has nothing left.</summary>
        private double LastWidth(UIElement last, bool isFull, double width, int columns, int inRow, double itemWidth, double tileWidth)
            => !isFull && !FillRows && GetTakesRest(last) ? RestWidth(width, Gap, columns, inRow, itemWidth) : tileWidth;

        protected override Size MeasureOverride(Size availableSize)
        {
            var shown = Shown();
            var (columns, itemWidth) = ColumnsFor(availableSize.Width, MinItemWidth, Gap, MaxColumns, shown.Count, Balance);
            double down = RowGapFor(RowGap, Gap);
            bool[] full = FullRows(shown);
            double total = 0;
            int start = 0, across = 0;
            foreach (int inRow in Rows(full, columns))
            {
                double tileWidth = full[start]
                    ? FullRowWidth(availableSize.Width, itemWidth)
                    : RowItemWidth(availableSize.Width, Gap, inRow, itemWidth, FillRows);
                double rowHeight = 0;
                for (int i = start; i < start + inRow; i++)
                {
                    double own = i == start + inRow - 1
                        ? LastWidth(shown[i], full[start], availableSize.Width, columns, inRow, itemWidth, tileWidth)
                        : tileWidth;
                    shown[i].Measure(new Size(own, double.PositiveInfinity));
                    rowHeight = Math.Max(rowHeight, shown[i].DesiredSize.Height);
                }
                total += rowHeight + (start > 0 ? down : 0);
                across = Math.Max(across, inRow);
                start += inRow;
            }
            double width = double.IsInfinity(availableSize.Width)
                ? across * itemWidth + Math.Max(0, across - 1) * Gap
                : availableSize.Width;
            var size = new Size(shown.Count == 0 ? 0 : width, shown.Count == 0 ? 0 : total);
            if (size != _measured)
            {
                _measured = size;
                // The parent is asked to measure again. WPF asks it after a
                // measure that changes the grid's size, with one exception:
                // UIElement.Arrange measures an element whose measure is out
                // of date before it arranges it, and does not tell the
                // parent. A window's resize measures and arranges its root
                // before the layout pass runs (HwndSource on WM_SIZE). When
                // that arrange reached the grid while the measure for its new
                // tiles still waited in the layout queue, the ItemsPresenter
                // above it kept the old height, and the picked control's
                // blocks under the picture had no room (#574, 2026-11-12).
                // While the parent is measuring the grid the call does
                // nothing. After a measure run from the layout queue it
                // repeats the call WPF makes itself.
                (VisualTreeHelper.GetParent(this) as UIElement)?.InvalidateMeasure();
            }
            return size;
        }

        // The size the last measure came to.
        private Size _measured;

        protected override Size ArrangeOverride(Size finalSize)
        {
            var shown = Shown();
            var (columns, itemWidth) = ColumnsFor(finalSize.Width, MinItemWidth, Gap, MaxColumns, shown.Count, Balance);
            double down = RowGapFor(RowGap, Gap);
            bool[] full = FullRows(shown);
            double y = 0;
            int start = 0;
            foreach (int inRow in Rows(full, columns))
            {
                double tileWidth = full[start]
                    ? FullRowWidth(finalSize.Width, itemWidth)
                    : RowItemWidth(finalSize.Width, Gap, inRow, itemWidth, FillRows);
                // A row is as tall as its tallest tile.
                double height = 0;
                for (int i = start; i < start + inRow; i++)
                    height = Math.Max(height, shown[i].DesiredSize.Height);
                for (int i = 0; i < inRow; i++)
                {
                    double own = i == inRow - 1
                        ? LastWidth(shown[start + i], full[start], finalSize.Width, columns, inRow, itemWidth, tileWidth)
                        : tileWidth;
                    shown[start + i].Arrange(new Rect(i * (tileWidth + Gap), y, own, height));
                }
                y += height + down;
                start += inRow;
            }
            return finalSize;
        }
    }
}
