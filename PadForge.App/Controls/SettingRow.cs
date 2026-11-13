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
    /// <para>A row lays its parts out within its <see cref="ReachProperty"/>,
    /// 660 unless a page sets another, however wide the row itself is: a
    /// row ran the width of its card with its reset at the far end, and on
    /// a wide window a reset stood up to 900 from the slider it resets
    /// (seen 2026-11-13). Within the reach the reset is beside the value,
    /// and the resets of a card stand in one column. A row whose parts need
    /// more than the reach has what they need.</para>
    ///
    /// <para>What follows the child that gives way is the row's value: a
    /// number, and its unit when it has one. With <see cref="ValueWidth"/>
    /// set, a value narrower than that width sits at the right of it, so
    /// the numbers of a card's rows end in one column and their sliders at
    /// one line, whatever each number's length.</para>
    ///
    /// <para>With <see cref="EndsProperty"/> set, on the row or on anything
    /// that holds it, the row is the Settings board's: its first child, the
    /// label, takes the room the row has left and wraps in it, every other
    /// child stands at the row's end in the order written, and the reset
    /// comes last, <see cref="EndGap"/> after them. A child marked
    /// <see cref="AsideProperty"/>, a line on what the setting reports,
    /// stands before the controls it was written after, so the switches of
    /// a card stay in one column whatever each row reports. The child
    /// marked to fill is the one that gives way first when the row is
    /// narrow, down to <see cref="FillLeast"/>, and then the label wraps.
    /// The reach does not apply: the page holds its cards to a column.</para>
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

        /// <summary>The most width a row lays its parts out in. Set on a
        /// page or a card, every row inside reads it. Infinity lets a row
        /// run its whole width.</summary>
        public static readonly DependencyProperty ReachProperty = DependencyProperty.RegisterAttached(
            "Reach", typeof(double), typeof(SettingRow),
            new FrameworkPropertyMetadata(DefaultReach, FrameworkPropertyMetadataOptions.Inherits
                | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        /// <summary>A label of 230, a slider of 260, a value with its unit,
        /// a Test button and a reset, which take 649, with a little to
        /// spare.</summary>
        internal const double DefaultReach = 660;

        /// <summary>Whether a row's controls stand at its end, after a label
        /// that takes the room. Set on a page or a card, every row inside
        /// reads it.</summary>
        public static readonly DependencyProperty EndsProperty = DependencyProperty.RegisterAttached(
            "Ends", typeof(bool), typeof(SettingRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits
                | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public static bool GetEnds(DependencyObject element) => (bool)element.GetValue(EndsProperty);

        public static void SetEnds(DependencyObject element, bool value) => element.SetValue(EndsProperty, value);

        /// <summary>A child of a row whose controls stand at its end that
        /// stands before those controls: what the setting reports, beside
        /// its switch and not after it.</summary>
        public static readonly DependencyProperty AsideProperty = DependencyProperty.RegisterAttached(
            "Aside", typeof(bool), typeof(SettingRow),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure
                | FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static bool GetAside(UIElement element) => (bool)element.GetValue(AsideProperty);

        public static void SetAside(UIElement element, bool value) => element.SetValue(AsideProperty, value);

        /// <summary>The room between a row's last control and its reset
        /// where the controls stand at the row's end: the board's 12.</summary>
        internal const double EndGap = 12;

        /// <summary>The least the child that gives way keeps before the
        /// label wraps, unless it asks for less: enough for a few words of
        /// a line that wraps.</summary>
        internal const double FillLeast = 96;

        /// <summary>How a row whose controls stand at its end shares its
        /// width between the label and the child that gives way. The label
        /// has all the room the other parts leave. A row too narrow for
        /// the label on one line and that child at the width it asks takes
        /// what it lacks off the child first, down to its least, and the
        /// label wraps in what is left. A row with no bound hands both
        /// what they ask.</summary>
        internal static (double Lead, double Fill) EndsWidths(double width, double leadWanted, double fixedWidth,
            double fillWanted, double fillLeast)
        {
            if (double.IsInfinity(width) || double.IsNaN(width)) return (leadWanted, fillWanted);
            double room = Math.Max(0, width - fixedWidth);
            double fill = leadWanted + fillWanted <= room
                ? fillWanted
                : Math.Max(Math.Min(fillLeast, fillWanted), room - leadWanted);
            fill = Math.Min(fill, room);
            return (room - fill, fill);
        }

        public static double GetReach(DependencyObject element) => (double)element.GetValue(ReachProperty);

        public static void SetReach(DependencyObject element, double value) => element.SetValue(ReachProperty, value);

        /// <summary>The room a row of this width lays its parts out in: the
        /// width, or the reach when the row is wider than that, or what its
        /// parts need when that is more than the reach. Never more than the
        /// width.</summary>
        internal static double RoomFor(double width, double reach, double needed)
        {
            if (double.IsInfinity(width) || double.IsNaN(width)) return width;
            if (double.IsNaN(reach) || double.IsInfinity(reach) || reach <= 0 || width <= reach) return width;
            return Math.Min(width, Math.Max(reach, needed));
        }

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

        /// <summary>What the row's parts need at the least, from the last
        /// measure: every other part, and the child that gives way at its
        /// own least when it takes all the room and at what it asks for
        /// when it does not.</summary>
        private double _needed;

        private double LeastOf(UIElement flex)
        {
            if (flex is not FrameworkElement element) return 0;
            return TakesAll(flex) ? element.MinWidth + element.Margin.Left + element.Margin.Right : _wanted;
        }

        // What the last measure of a row whose controls stand at its end
        // came to, kept for the arrange that follows it.
        private double _leadWanted, _endsFixed, _fillWanted, _fillLeast;

        /// <summary>The parts of a row whose controls stand at its end: the
        /// label, the child that gives way, and whether it has a control
        /// and a trailing part, between which the gap falls.</summary>
        private (UIElement Lead, UIElement Fill, bool Control, bool Trailing) EndsParts()
        {
            UIElement lead = null, fill = null;
            bool control = false, trailing = false;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                if (IsTrailing(child)) trailing = true;
                else if (lead == null) lead = child;
                else
                {
                    control = true;
                    if (fill == null && GetFills(child)) fill = child;
                }
            }
            return (lead, fill, control, trailing);
        }

        private Size MeasureEnds(Size availableSize)
        {
            var (lead, fill, control, trailing) = EndsParts();
            var all = new Size(double.PositiveInfinity, availableSize.Height);
            double fixedWidth = control && trailing ? EndGap : 0, height = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child == lead || child == fill) continue;
                child.Measure(all);
                if (child.Visibility == Visibility.Collapsed) continue;
                fixedWidth += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }

            double leadWanted = 0;
            if (lead != null)
            {
                lead.Measure(all);
                leadWanted = lead.DesiredSize.Width;
            }
            double fillWanted = 0, fillLeast = 0;
            if (fill is FrameworkElement part)
            {
                // What it asks for: its MaxWidth when it has one, a field
                // that should be that wide, and else what its content
                // takes, a picker as wide as its longest choice or a line
                // of text on one line.
                double margin = part.Margin.Left + part.Margin.Right;
                if (double.IsInfinity(part.MaxWidth))
                {
                    part.Measure(all);
                    fillWanted = part.DesiredSize.Width;
                }
                else
                    fillWanted = part.MaxWidth + margin;
                fillLeast = Math.Max(part.MinWidth + margin, Math.Min(FillLeast, fillWanted));
            }
            _leadWanted = leadWanted;
            _endsFixed = fixedWidth;
            _fillWanted = fillWanted;
            _fillLeast = fillLeast;

            var (leadWidth, fillWidth) = EndsWidths(availableSize.Width, leadWanted, fixedWidth, fillWanted, fillLeast);
            if (fill != null)
            {
                fill.Measure(new Size(fillWidth, availableSize.Height));
                height = Math.Max(height, fill.DesiredSize.Height);
            }
            if (lead != null)
            {
                lead.Measure(new Size(leadWidth, availableSize.Height));
                height = Math.Max(height, lead.DesiredSize.Height);
            }
            double width = double.IsInfinity(availableSize.Width) ? leadWanted + fixedWidth + fillWanted : availableSize.Width;
            return new Size(width, height);
        }

        private Size ArrangeEnds(Size finalSize)
        {
            var (lead, fill, control, trailing) = EndsParts();
            double fillWidth = EndsWidths(finalSize.Width, _leadWanted, _endsFixed, _fillWanted, _fillLeast).Fill;

            // The trailing parts from the row's end, the first outermost.
            double right = finalSize.Width;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed || !IsTrailing(child)) continue;
                double w = child.DesiredSize.Width;
                right -= w;
                child.Arrange(new Rect(Math.Max(0, right), 0, w, finalSize.Height));
            }
            if (control && trailing) right -= EndGap;

            // The controls end there: what stands aside first, then the
            // rest, each in the order written.
            double Width(UIElement child) => child == fill ? fillWidth : child.DesiredSize.Width;
            bool Stands(UIElement child) => child != null && child != lead && child.Visibility != Visibility.Collapsed && !IsTrailing(child);
            double total = 0;
            foreach (UIElement child in InternalChildren)
                if (Stands(child)) total += Width(child);
            double x = Math.Max(0, right - total);
            lead?.Arrange(new Rect(0, 0, x, finalSize.Height));
            foreach (bool aside in new[] { true, false })
            {
                foreach (UIElement child in InternalChildren)
                {
                    if (!Stands(child) || GetAside(child) != aside) continue;
                    double w = Width(child);
                    child.Arrange(new Rect(x, 0, w, finalSize.Height));
                    x += w;
                }
            }
            return finalSize;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (GetEnds(this)) return MeasureEnds(availableSize);
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
            _needed = fixedWidth;
            if (flex != null)
            {
                fixedWidth += ValuePad(ValueWidth, value);
                _wanted = Wanted((FrameworkElement)flex, availableSize.Height);
                _needed = fixedWidth + LeastOf(flex);
                double room = RoomFor(availableSize.Width, GetReach(this), _needed);
                flexWidth = FlexWidth(_wanted, room - fixedWidth, TakesAll(flex));
                flex.Measure(new Size(flexWidth, availableSize.Height));
                height = Math.Max(height, flex.DesiredSize.Height);
            }
            double width = fixedWidth + flexWidth;
            if (!double.IsInfinity(availableSize.Width)) width = Math.Min(width, availableSize.Width);
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (GetEnds(this)) return ArrangeEnds(finalSize);
            UIElement flex = FindFlex();
            // The parts stand within the row's reach: the reset is at its
            // end, beside the value, not at the far end of a wide card.
            double right = RoomFor(finalSize.Width, GetReach(this), _needed);
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
