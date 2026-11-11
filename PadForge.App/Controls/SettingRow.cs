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
    /// slider takes all the room left as well. Grow is the slider's: a
    /// child marked to fill still stops at its MaxWidth.</para>
    ///
    /// <para>What follows the child that gives way is the row's value: a
    /// number, and its unit when it has one. With <see cref="ValueWidth"/>
    /// set, a value narrower than that width sits at the right of it, so
    /// the numbers of a card's rows end in one column and their sliders at
    /// one line, whatever each number's length.</para>
    /// </summary>
    public sealed class SettingRow : Panel
    {
        public static readonly DependencyProperty GrowProperty = DependencyProperty.Register(
            nameof(Grow), typeof(bool), typeof(SettingRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ValueWidthProperty = DependencyProperty.Register(
            nameof(ValueWidth), typeof(double), typeof(SettingRow),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty FillsProperty = DependencyProperty.RegisterAttached(
            "Fills", typeof(bool), typeof(SettingRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        /// <summary>Whether the slider takes all the room left in the row.</summary>
        public bool Grow
        {
            get => (bool)GetValue(GrowProperty);
            set => SetValue(GrowProperty, value);
        }

        /// <summary>The least width of the row's value, what follows the
        /// child that gives way. Zero keeps the value against that child.</summary>
        public double ValueWidth
        {
            get => (double)GetValue(ValueWidthProperty);
            set => SetValue(ValueWidthProperty, value);
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

        /// <summary>The room left empty between the child that gives way
        /// and the value after it: what the value lacks of the row's value
        /// width. A row with nothing after that child leaves none, so a
        /// slider with no number runs to the reset.</summary>
        internal static double ValuePad(double valueWidth, double value)
            => value > 0 && valueWidth > value ? valueWidth - value : 0;

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

        /// <summary>Whether the child that gives way takes all the room. A
        /// child that fills does when it has no MaxWidth to stop at, and
        /// stops at its MaxWidth in a row that grows too: a picker 260
        /// wide stays 260 in a wide row and narrows in a narrow one. A
        /// slider does when the row grows it.</summary>
        private bool TakesAll(UIElement flex)
            => GetFills(flex) ? double.IsInfinity(((FrameworkElement)flex).MaxWidth) : Grow;

        /// <summary>What the child that gives way asked for at the last
        /// measure, kept for the arrange that follows it.</summary>
        private double _wanted;

        protected override Size MeasureOverride(Size availableSize)
        {
            UIElement flex = FindFlex();
            double fixedWidth = 0, height = 0, value = 0;
            bool afterFlex = false;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null) continue;
                if (child == flex)
                {
                    afterFlex = true;
                    continue;
                }
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                if (child.Visibility == Visibility.Collapsed) continue;
                fixedWidth += child.DesiredSize.Width;
                if (afterFlex && !IsTrailing(child)) value += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }
            double flexWidth = 0;
            if (flex != null)
            {
                fixedWidth += ValuePad(ValueWidth, value);
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
            double leading = 0, value = 0;
            bool afterFlex = false;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                if (child == flex)
                {
                    afterFlex = true;
                }
                else if (IsTrailing(child))
                {
                    double w = child.DesiredSize.Width;
                    right -= w;
                    child.Arrange(new Rect(Math.Max(0, right), 0, w, finalSize.Height));
                }
                else
                {
                    leading += child.DesiredSize.Width;
                    if (afterFlex) value += child.DesiredSize.Width;
                }
            }
            double pad = flex != null ? ValuePad(ValueWidth, value) : 0;
            double flexWidth = flex != null ? FlexWidth(_wanted, right - leading - pad, TakesAll(flex)) : 0;

            double left = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed || IsTrailing(child)) continue;
                double w = child == flex ? flexWidth : child.DesiredSize.Width;
                child.Arrange(new Rect(left, 0, w, finalSize.Height));
                left += w;
                // The value stands at the right of its column.
                if (child == flex) left += pad;
            }
            return finalSize;
        }
    }
}
