using System;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// One setting's row (#574): its label, its control, its value, then
    /// whatever belongs at the row's far right (its reset, a Test button).
    /// Children are placed in order from the left, each at the width it
    /// asks for, and a child docked right (<c>DockPanel.Dock="Right"</c>,
    /// which the reset styles set) is placed from the right edge, the first
    /// of them outermost.
    ///
    /// <para>One child gives way when the row is narrower than its
    /// children. A child marked <see cref="FillsProperty"/> is that child
    /// and takes all the room the row has left, up to its <c>MaxWidth</c>
    /// when it has one, so a line of text beside a switch wraps inside the
    /// row and a pair of meters widens with it. With none marked it is the
    /// first slider that has no width of its own: it takes its
    /// <c>MaxWidth</c> while the row has room and what is left when it has
    /// not, so a narrow window narrows the slider and keeps the reset in
    /// view. A DockPanel hands each child its full width in turn, and the
    /// last one, the reset, got none. With <see cref="Grow"/> set the
    /// slider takes all the room left as well.</para>
    /// </summary>
    public sealed class SettingRow : Panel
    {
        public static readonly DependencyProperty GrowProperty = DependencyProperty.Register(
            nameof(Grow), typeof(bool), typeof(SettingRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FillsProperty = DependencyProperty.RegisterAttached(
            "Fills", typeof(bool), typeof(SettingRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        /// <summary>Whether the slider takes all the room left in the row.</summary>
        public bool Grow
        {
            get => (bool)GetValue(GrowProperty);
            set => SetValue(GrowProperty, value);
        }

        public static bool GetFills(UIElement element) => (bool)element.GetValue(FillsProperty);

        public static void SetFills(UIElement element, bool value) => element.SetValue(FillsProperty, value);

        private static bool IsTrailing(UIElement child) => DockPanel.GetDock(child) == Dock.Right;

        private static bool IsFreeSlider(UIElement child)
            => child is Slider slider && double.IsNaN(slider.Width);

        /// <summary>The width the child that gives way takes: all the room
        /// when it grows, and its wanted width or the room, whichever is
        /// less, when it does not. A row with no bound hands it what it
        /// wants.</summary>
        internal static double FlexWidth(double wanted, double room, bool grow)
        {
            if (double.IsInfinity(room)) return wanted;
            room = Math.Max(0, room);
            return grow ? room : Math.Min(wanted, room);
        }

        /// <summary>What the child asks for: its MaxWidth with its margin,
        /// or what it measures at when it has none.</summary>
        private static double Wanted(FrameworkElement flex, double height)
        {
            double margin = flex.Margin.Left + flex.Margin.Right;
            if (!double.IsInfinity(flex.MaxWidth)) return flex.MaxWidth + margin;
            flex.Measure(new Size(double.PositiveInfinity, height));
            return flex.DesiredSize.Width;
        }

        /// <summary>The child that gives way: one marked to fill, else the
        /// first slider with no width of its own. A child docked right
        /// never is.</summary>
        private UIElement FindFlex()
        {
            UIElement slider = null;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed || IsTrailing(child)) continue;
                if (GetFills(child)) return child;
                if (slider == null && IsFreeSlider(child)) slider = child;
            }
            return slider;
        }

        /// <summary>Whether the child that gives way takes all the room:
        /// the row grows its slider, or the child fills and has no
        /// MaxWidth to stop at.</summary>
        private bool TakesAll(UIElement flex)
            => Grow || (GetFills(flex) && double.IsInfinity(((FrameworkElement)flex).MaxWidth));

        /// <summary>What the child that gives way asked for at the last
        /// measure, kept for the arrange that follows it.</summary>
        private double _wanted;

        protected override Size MeasureOverride(Size availableSize)
        {
            UIElement flex = FindFlex();
            double fixedWidth = 0, height = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child == flex) continue;
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                if (child.Visibility == Visibility.Collapsed) continue;
                fixedWidth += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }
            double flexWidth = 0;
            if (flex != null)
            {
                _wanted = Wanted((FrameworkElement)flex, availableSize.Height);
                flexWidth = FlexWidth(_wanted, availableSize.Width - fixedWidth, TakesAll(flex));
                flex.Measure(new Size(flexWidth, availableSize.Height));
                height = Math.Max(height, flex.DesiredSize.Height);
            }
            double width = fixedWidth + flexWidth;
            if (!double.IsInfinity(availableSize.Width)) width = Math.Min(width, availableSize.Width);
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            UIElement flex = FindFlex();
            double right = finalSize.Width;
            double leading = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                if (IsTrailing(child))
                {
                    double w = child.DesiredSize.Width;
                    right -= w;
                    child.Arrange(new Rect(Math.Max(0, right), 0, w, finalSize.Height));
                }
                else if (child != flex)
                {
                    leading += child.DesiredSize.Width;
                }
            }
            double flexWidth = flex != null ? FlexWidth(_wanted, right - leading, TakesAll(flex)) : 0;

            double left = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed || IsTrailing(child)) continue;
                double w = child == flex ? flexWidth : child.DesiredSize.Width;
                child.Arrange(new Rect(left, 0, w, finalSize.Height));
                left += w;
            }
            return finalSize;
        }
    }
}
