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
    /// tile takes no cell.
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

        protected override Size MeasureOverride(Size availableSize)
        {
            var (columns, itemWidth) = Columns(availableSize.Width, MinItemWidth, Gap, MaxColumns);
            double total = 0, rowHeight = 0;
            int inRow = 0, shown = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                child.Measure(new Size(itemWidth, double.PositiveInfinity));
                if (inRow == columns)
                {
                    total += rowHeight + Gap;
                    rowHeight = 0;
                    inRow = 0;
                }
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                inRow++;
                shown++;
            }
            total += rowHeight;
            double width = double.IsInfinity(availableSize.Width)
                ? Math.Min(shown, columns) * itemWidth + Math.Max(0, Math.Min(shown, columns) - 1) * Gap
                : availableSize.Width;
            return new Size(shown == 0 ? 0 : width, shown == 0 ? 0 : total);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (columns, itemWidth) = Columns(finalSize.Width, MinItemWidth, Gap, MaxColumns);
            // A row is as tall as its tallest tile, so the heights are read
            // a row at a time before anything is placed.
            var row = new System.Collections.Generic.List<UIElement>(columns);
            double y = 0;
            void Place()
            {
                if (row.Count == 0) return;
                double height = 0;
                foreach (var tile in row) height = Math.Max(height, tile.DesiredSize.Height);
                for (int i = 0; i < row.Count; i++)
                    row[i].Arrange(new Rect(i * (itemWidth + Gap), y, itemWidth, height));
                y += height + Gap;
                row.Clear();
            }
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                if (row.Count == columns) Place();
                row.Add(child);
            }
            Place();
            return finalSize;
        }
    }
}
