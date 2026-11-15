using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>
    /// The cells of one row of the mappings list, and of its head: a column
    /// for each device, the same width in every row, so the rows read as a
    /// grid with no table behind them.
    ///
    /// <para>A child marked with <see cref="FixedWidthProperty"/> keeps
    /// that width. The others share what is left equally, and none goes
    /// under <see cref="MinColumnWidth"/>. When they would, the panel is
    /// wider than its room: it draws from <see cref="Offset"/> on and clips
    /// the rest, so one scroll bar under the list moves every row's cells
    /// together while what stands beside the panel stays.</para>
    ///
    /// <para>A collapsed child takes no column. Every row holds the same
    /// children in the same order, so the columns line up.</para>
    /// </summary>
    public sealed class ListCellsPanel : Panel
    {
        public static readonly DependencyProperty FixedWidthProperty = DependencyProperty.RegisterAttached(
            "FixedWidth", typeof(double), typeof(ListCellsPanel),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
            nameof(MinColumnWidth), typeof(double), typeof(ListCellsPanel),
            new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
            nameof(Offset), typeof(double), typeof(ListCellsPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static double GetFixedWidth(UIElement element) => (double)element.GetValue(FixedWidthProperty);

        public static void SetFixedWidth(UIElement element, double value) => element.SetValue(FixedWidthProperty, value);

        /// <summary>The least width of a column that shares the room.</summary>
        public double MinColumnWidth
        {
            get => (double)GetValue(MinColumnWidthProperty);
            set => SetValue(MinColumnWidthProperty, value);
        }

        /// <summary>How far the cells are scrolled sideways.</summary>
        public double Offset
        {
            get => (double)GetValue(OffsetProperty);
            set => SetValue(OffsetProperty, value);
        }

        public ListCellsPanel()
        {
            ClipToBounds = true;
        }

        /// <summary>The columns' widths for a room: a fixed column keeps
        /// its own, and the others share the rest equally, at their least
        /// width or more. With no bound each sharing column is at its
        /// least.</summary>
        internal static double[] Widths(IReadOnlyList<double> fixedWidths, double room, double least)
        {
            var widths = new double[fixedWidths.Count];
            double taken = 0;
            int sharing = 0;
            for (int i = 0; i < fixedWidths.Count; i++)
            {
                if (double.IsNaN(fixedWidths[i])) sharing++;
                else
                {
                    widths[i] = Math.Max(0, fixedWidths[i]);
                    taken += widths[i];
                }
            }
            if (sharing == 0) return widths;
            double each = double.IsInfinity(room) || double.IsNaN(room) ? least : Math.Max(least, (room - taken) / sharing);
            for (int i = 0; i < fixedWidths.Count; i++)
                if (double.IsNaN(fixedWidths[i])) widths[i] = each;
            return widths;
        }

        private List<UIElement> Shown()
        {
            var shown = new List<UIElement>();
            foreach (UIElement child in InternalChildren)
                if (child != null && child.Visibility != Visibility.Collapsed) shown.Add(child);
            return shown;
        }

        private static double[] FixedOf(List<UIElement> parts)
        {
            var fixedWidths = new double[parts.Count];
            for (int i = 0; i < parts.Count; i++) fixedWidths[i] = GetFixedWidth(parts[i]);
            return fixedWidths;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var parts = Shown();
            if (parts.Count == 0) return new Size(0, 0);
            var widths = Widths(FixedOf(parts), availableSize.Width, MinColumnWidth);
            double height = 0, across = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                parts[i].Measure(new Size(widths[i], double.PositiveInfinity));
                height = Math.Max(height, parts[i].DesiredSize.Height);
                across += widths[i];
            }
            // The panel asks for its room and no more: what does not fit is
            // scrolled, never pushed onto what stands after the panel.
            return new Size(double.IsInfinity(availableSize.Width) ? across : availableSize.Width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var parts = Shown();
            if (parts.Count == 0) return finalSize;
            var widths = Widths(FixedOf(parts), finalSize.Width, MinColumnWidth);
            double across = 0;
            foreach (double w in widths) across += w;
            double offset = Math.Clamp(Offset, 0, Math.Max(0, across - finalSize.Width));
            double x = -offset;
            for (int i = 0; i < parts.Count; i++)
            {
                parts[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
                x += widths[i];
            }
            return finalSize;
        }

        protected override Geometry GetLayoutClip(Size layoutSlotSize)
            => new RectangleGeometry(new Rect(RenderSize));
    }
}
