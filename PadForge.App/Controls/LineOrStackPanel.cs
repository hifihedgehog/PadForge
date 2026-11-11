using System;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// Puts its children on one line while they fit, and one under another
    /// when they do not (#574). Home's controller card uses it for the state
    /// and the device counts: a short state shares the line with the counts,
    /// and a long one, or a long translation, moves the counts to a line of
    /// their own where nothing is cut off. A child marked
    /// <see cref="OnLineOnlyProperty"/> is a separator. It shows on the
    /// line and takes no room once the children are stacked.
    /// </summary>
    public sealed class LineOrStackPanel : Panel
    {
        public static readonly DependencyProperty OnLineOnlyProperty = DependencyProperty.RegisterAttached(
            "OnLineOnly", typeof(bool), typeof(LineOrStackPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static bool GetOnLineOnly(UIElement element) => (bool)element.GetValue(OnLineOnlyProperty);

        public static void SetOnLineOnly(UIElement element, bool value) => element.SetValue(OnLineOnlyProperty, value);

        /// <summary>Whether the children were last measured onto one line.</summary>
        internal bool IsOnLine { get; private set; } = true;

        /// <summary>Whether children that need <paramref name="needed"/>
        /// across fit a width. An unbounded width holds anything.</summary>
        internal static bool Fits(double needed, double available)
            => double.IsInfinity(available) || double.IsNaN(available) || needed <= available;

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = 0, height = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                width += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }
            IsOnLine = Fits(width, availableSize.Width);
            if (IsOnLine) return new Size(width, height);

            // Stacked: each child gets the whole width, so a text that is
            // still too long trims on its own line.
            width = 0;
            height = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                if (GetOnLineOnly(child))
                {
                    child.Measure(new Size(0, 0));
                    continue;
                }
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                width = Math.Max(width, child.DesiredSize.Width);
                height += child.DesiredSize.Height;
            }
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double along = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                if (IsOnLine)
                {
                    child.Arrange(new Rect(along, 0, child.DesiredSize.Width, finalSize.Height));
                    along += child.DesiredSize.Width;
                }
                else if (GetOnLineOnly(child))
                {
                    child.Arrange(new Rect(0, 0, 0, 0));
                }
                else
                {
                    child.Arrange(new Rect(0, along, finalSize.Width, child.DesiredSize.Height));
                    along += child.DesiredSize.Height;
                }
            }
            return finalSize;
        }
    }
}
