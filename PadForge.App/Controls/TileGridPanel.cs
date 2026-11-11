using System;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// Lays tiles out in rows of equal columns (#574). As many columns as fit
    /// at <see cref="MinItemWidth"/> with <see cref="Gap"/> between them, each
    /// column taking an equal share of the width, so the tiles of a row end
    /// flush with the panel's edge and a wider window gains columns, not
    /// gaps. Every tile of a row is as tall as the row's tallest. A collapsed
    /// tile takes no cell. With <see cref="FillRows"/> a row's tiles share
    /// the whole width, however few they are.
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

        /// <summary>The tiles that show, in order.</summary>
        private System.Collections.Generic.List<UIElement> Shown()
        {
            var shown = new System.Collections.Generic.List<UIElement>(InternalChildren.Count);
            foreach (UIElement child in InternalChildren)
                if (child != null && child.Visibility != Visibility.Collapsed)
                    shown.Add(child);
            return shown;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var (columns, itemWidth) = Columns(availableSize.Width, MinItemWidth, Gap, MaxColumns);
            double down = RowGapFor(RowGap, Gap);
            var shown = Shown();
            double total = 0;
            for (int start = 0; start < shown.Count; start += columns)
            {
                int inRow = Math.Min(columns, shown.Count - start);
                double tileWidth = RowItemWidth(availableSize.Width, Gap, inRow, itemWidth, FillRows);
                double rowHeight = 0;
                for (int i = start; i < start + inRow; i++)
                {
                    shown[i].Measure(new Size(tileWidth, double.PositiveInfinity));
                    rowHeight = Math.Max(rowHeight, shown[i].DesiredSize.Height);
                }
                total += rowHeight + (start > 0 ? down : 0);
            }
            int across = Math.Min(shown.Count, columns);
            double width = double.IsInfinity(availableSize.Width)
                ? across * itemWidth + Math.Max(0, across - 1) * Gap
                : availableSize.Width;
            return new Size(shown.Count == 0 ? 0 : width, shown.Count == 0 ? 0 : total);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (columns, itemWidth) = Columns(finalSize.Width, MinItemWidth, Gap, MaxColumns);
            double down = RowGapFor(RowGap, Gap);
            var shown = Shown();
            double y = 0;
            for (int start = 0; start < shown.Count; start += columns)
            {
                int inRow = Math.Min(columns, shown.Count - start);
                double tileWidth = RowItemWidth(finalSize.Width, Gap, inRow, itemWidth, FillRows);
                // A row is as tall as its tallest tile.
                double height = 0;
                for (int i = start; i < start + inRow; i++)
                    height = Math.Max(height, shown[i].DesiredSize.Height);
                for (int i = 0; i < inRow; i++)
                    shown[start + i].Arrange(new Rect(i * (tileWidth + Gap), y, tileWidth, height));
                y += height + down;
            }
            return finalSize;
        }
    }
}
