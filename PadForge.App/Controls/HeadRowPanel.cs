using System;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// A card's head row in two parts (#574): what leads, at the row's
    /// start, and what trails, at its end. While both fit they share one
    /// line: the trailing part takes the width it asks for and the leading
    /// part the rest, so a leading part that wraps does so in what is left.
    /// When the leading part would get less than
    /// <see cref="LeadingMinWidth"/> the parts stand one under the other,
    /// each with the row's whole width, so a narrow window cuts nothing off.
    /// The first child leads and the second trails. A row with one child
    /// gives it the whole width.
    /// </summary>
    public sealed class HeadRowPanel : Panel
    {
        public static readonly DependencyProperty LeadingMinWidthProperty = DependencyProperty.Register(
            nameof(LeadingMinWidth), typeof(double), typeof(HeadRowPanel),
            new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The least width the leading part shares a line at.</summary>
        public double LeadingMinWidth
        {
            get => (double)GetValue(LeadingMinWidthProperty);
            set => SetValue(LeadingMinWidthProperty, value);
        }

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(HeadRowPanel),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The space between the parts when they are stacked.</summary>
        public double Gap
        {
            get => (double)GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        /// <summary>Whether the parts were last measured onto one line.</summary>
        internal bool IsOnLine { get; private set; } = true;

        /// <summary>Whether the parts share a line: the row has no bound,
        /// or what the trailing part leaves is at least the leading part's
        /// least width.</summary>
        internal static bool SharesLine(double width, double trailing, double leadingMin)
            => double.IsInfinity(width) || double.IsNaN(width) || width - trailing >= leadingMin;

        private (UIElement Leading, UIElement Trailing) Parts()
        {
            UIElement leading = null, trailing = null;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                if (leading == null) leading = child;
                else if (trailing == null) trailing = child;
            }
            return (leading, trailing);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var (leading, trailing) = Parts();
            if (leading == null) return new Size(0, 0);
            if (trailing == null)
            {
                IsOnLine = true;
                leading.Measure(availableSize);
                return leading.DesiredSize;
            }

            trailing.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            double tail = trailing.DesiredSize.Width;
            IsOnLine = SharesLine(availableSize.Width, tail, LeadingMinWidth);
            if (IsOnLine)
            {
                double room = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - tail);
                leading.Measure(new Size(room, availableSize.Height));
                double width = double.IsInfinity(availableSize.Width) ? leading.DesiredSize.Width + tail : availableSize.Width;
                return new Size(width, Math.Max(leading.DesiredSize.Height, trailing.DesiredSize.Height));
            }

            // Stacked: each part has the whole width, so a part that wraps
            // does so on its own lines.
            leading.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            trailing.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            return new Size(availableSize.Width, leading.DesiredSize.Height + Gap + trailing.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (leading, trailing) = Parts();
            if (leading == null) return finalSize;
            if (trailing == null)
            {
                leading.Arrange(new Rect(finalSize));
                return finalSize;
            }
            if (IsOnLine)
            {
                double tail = Math.Min(trailing.DesiredSize.Width, finalSize.Width);
                leading.Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - tail), finalSize.Height));
                trailing.Arrange(new Rect(finalSize.Width - tail, 0, tail, finalSize.Height));
                return finalSize;
            }
            double lead = leading.DesiredSize.Height;
            leading.Arrange(new Rect(0, 0, finalSize.Width, lead));
            trailing.Arrange(new Rect(0, lead + Gap, finalSize.Width, trailing.DesiredSize.Height));
            return finalSize;
        }
    }
}
