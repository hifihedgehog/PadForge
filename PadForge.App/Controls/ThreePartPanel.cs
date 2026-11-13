using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// Three parts that share a card by the room it has (#574, the
    /// world-class pass): a lead (a stick's settings), a middle (its
    /// curve) and an end (its live view).
    ///
    /// <para>Wide, they stand on one line, as the Sticks board draws them:
    /// the lead, the middle, the end. The lead grows a little, up to its
    /// <c>MaxWidth</c>, and the middle takes the rest up to its own.
    /// Narrower than the three need, the end stands beside the other two
    /// and the middle goes under the lead, at the full width of their
    /// column, so a chart is never squeezed between its neighbors. Narrower
    /// than that, the three stand one under the other.</para>
    ///
    /// <para>The lead's and the middle's least widths are their
    /// <see cref="FlexLinePanel.BasisProperty"/>. The end keeps the width
    /// it asks for. A FlexLinePanel cannot do this: it wraps the last part
    /// alone under the first, which left a 326 wide view under a column of
    /// settings and the rest of the card empty (seen at 1280,
    /// 2026-11-13).</para>
    /// </summary>
    public sealed class ThreePartPanel : Panel
    {
        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(ThreePartPanel),
            new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty LineGapProperty = DependencyProperty.Register(
            nameof(LineGap), typeof(double), typeof(ThreePartPanel),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The space between two parts side by side.</summary>
        public double Gap
        {
            get => (double)GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        /// <summary>The space between two parts one under the other.</summary>
        public double LineGap
        {
            get => (double)GetValue(LineGapProperty);
            set => SetValue(LineGapProperty, value);
        }

        /// <summary>How the three stand.</summary>
        internal enum Form
        {
            /// <summary>Lead, middle, end on one line.</summary>
            OneLine,

            /// <summary>The lead over the middle, and the end beside both.</summary>
            EndBeside,

            /// <summary>One under the other.</summary>
            Stacked,
        }

        /// <summary>The form a width allows: one line while the lead's and
        /// the middle's least widths and the end fit with the gaps, the end
        /// beside the other two while the wider of those two and the end
        /// fit, and a stack below that. With no bound, one line.</summary>
        internal static Form FormFor(double width, double leadBasis, double middleBasis, double end, double gap)
        {
            if (double.IsInfinity(width) || double.IsNaN(width)) return Form.OneLine;
            // A hair of slack, so a sum that is the width but for the
            // rounding of its parts still fits.
            if (leadBasis + gap + middleBasis + gap + end <= width + 0.01) return Form.OneLine;
            if (Math.Max(leadBasis, middleBasis) + gap + end <= width + 0.01) return Form.EndBeside;
            return Form.Stacked;
        }

        /// <summary>The lead's and the middle's widths in a form. On one
        /// line the lead takes a third of what the line has left over the
        /// two least widths, up to its most, and the middle the rest, up to
        /// its own. Beside the end, each takes the column the end leaves,
        /// up to its most. Stacked, each takes the width, up to its most.</summary>
        internal static (double Lead, double Middle) Widths(Form form, double width, double leadBasis, double leadMost,
            double middleBasis, double middleMost, double end, double gap)
        {
            if (double.IsInfinity(width) || double.IsNaN(width)) return (leadBasis, middleBasis);
            switch (form)
            {
                case Form.OneLine:
                {
                    double room = width - end - 2 * gap;
                    double over = Math.Max(0, room - leadBasis - middleBasis);
                    double lead = Math.Min(Math.Max(leadMost, leadBasis), leadBasis + over / 3);
                    double middle = Math.Min(Math.Max(middleMost, middleBasis), room - lead);
                    return (lead, Math.Max(0, middle));
                }
                case Form.EndBeside:
                {
                    double column = Math.Max(0, width - end - gap);
                    return (Math.Min(leadMost, column), Math.Min(middleMost, column));
                }
                default:
                    return (Math.Min(leadMost, Math.Max(0, width)), Math.Min(middleMost, Math.Max(0, width)));
            }
        }

        private List<UIElement> Shown()
        {
            var shown = new List<UIElement>();
            foreach (UIElement child in InternalChildren)
                if (child != null && child.Visibility != Visibility.Collapsed) shown.Add(child);
            return shown;
        }

        private static double Basis(UIElement part)
        {
            double basis = FlexLinePanel.GetBasis(part);
            return double.IsNaN(basis) ? 0 : Math.Max(0, basis);
        }

        private static double Most(UIElement part)
            => part is FrameworkElement element && !double.IsInfinity(element.MaxWidth)
                ? element.MaxWidth + element.Margin.Left + element.Margin.Right
                : double.PositiveInfinity;

        // The plan of the last measure, which the arrange lays out by.
        private Form _form;
        private double _lead, _middle, _end;

        protected override Size MeasureOverride(Size availableSize)
        {
            var parts = Shown();
            if (parts.Count != 3)
            {
                // Fewer or more than three: a plain stack, each at the width
                // up to its most.
                double tall = 0, wide = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    parts[i].Measure(new Size(Math.Min(availableSize.Width, Most(parts[i])), double.PositiveInfinity));
                    tall += parts[i].DesiredSize.Height + (i > 0 ? LineGap : 0);
                    wide = Math.Max(wide, parts[i].DesiredSize.Width);
                }
                return new Size(double.IsInfinity(availableSize.Width) ? wide : availableSize.Width, tall);
            }

            UIElement lead = parts[0], middle = parts[1], end = parts[2];
            end.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _end = end.DesiredSize.Width;
            double width = availableSize.Width;
            _form = FormFor(width, Basis(lead), Basis(middle), _end, Gap);
            (_lead, _middle) = Widths(_form, width, Basis(lead), Most(lead), Basis(middle), Most(middle), _end, Gap);
            lead.Measure(new Size(_lead, double.PositiveInfinity));
            middle.Measure(new Size(_middle, double.PositiveInfinity));

            double height = _form switch
            {
                Form.OneLine => Math.Max(lead.DesiredSize.Height, Math.Max(middle.DesiredSize.Height, end.DesiredSize.Height)),
                Form.EndBeside => Math.Max(lead.DesiredSize.Height + LineGap + middle.DesiredSize.Height, end.DesiredSize.Height),
                _ => lead.DesiredSize.Height + LineGap + middle.DesiredSize.Height + LineGap + end.DesiredSize.Height,
            };
            double across = _form switch
            {
                Form.OneLine => _lead + Gap + _middle + Gap + _end,
                Form.EndBeside => Math.Max(_lead, _middle) + Gap + _end,
                _ => Math.Max(Math.Max(_lead, _middle), _end),
            };
            return new Size(double.IsInfinity(width) ? across : width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var parts = Shown();
            if (parts.Count != 3)
            {
                double y = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0) y += LineGap;
                    parts[i].Arrange(new Rect(0, y, Math.Min(finalSize.Width, Most(parts[i])), parts[i].DesiredSize.Height));
                    y += parts[i].DesiredSize.Height;
                }
                return finalSize;
            }

            UIElement lead = parts[0], middle = parts[1], end = parts[2];
            double leadHeight = lead.DesiredSize.Height, middleHeight = middle.DesiredSize.Height, endHeight = end.DesiredSize.Height;
            switch (_form)
            {
                case Form.OneLine:
                    lead.Arrange(new Rect(0, 0, _lead, leadHeight));
                    middle.Arrange(new Rect(_lead + Gap, 0, _middle, middleHeight));
                    end.Arrange(new Rect(_lead + Gap + _middle + Gap, 0, _end, endHeight));
                    break;
                case Form.EndBeside:
                    lead.Arrange(new Rect(0, 0, _lead, leadHeight));
                    middle.Arrange(new Rect(0, leadHeight + LineGap, _middle, middleHeight));
                    // The end stands against the line's end, as the card's
                    // Reset All does over it.
                    end.Arrange(new Rect(Math.Max(0, finalSize.Width - _end), 0, _end, endHeight));
                    break;
                default:
                    lead.Arrange(new Rect(0, 0, _lead, leadHeight));
                    middle.Arrange(new Rect(0, leadHeight + LineGap, _middle, middleHeight));
                    end.Arrange(new Rect(0, leadHeight + LineGap + middleHeight + LineGap, _end, endHeight));
                    break;
            }
            return finalSize;
        }
    }
}
