using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// A line of parts that wraps (#574), as the bar under the controller's
    /// picture is laid out: a heading, the picked control's sources and
    /// its value side by side while the card is wide, one under the other
    /// when it is not.
    ///
    /// <para>Each part has a basis, the width it takes before anything is
    /// shared out: <see cref="BasisProperty"/> when it is set, the width
    /// the part asks for when it is not. Parts fill a line while their
    /// bases fit with <see cref="Gap"/> between them, and a part that does
    /// not fit starts the next line, <see cref="LineGap"/> under the last.
    /// What a line has left over goes to its parts by their
    /// <see cref="GrowProperty"/> shares, so a part with a large share
    /// takes nearly all of it. A part alone on a line that is narrower
    /// than its basis gets the line's width.</para>
    ///
    /// <para>Every part of a line is as tall as the line's tallest and
    /// stands at its top by its own alignment. A collapsed part takes no
    /// place.</para>
    ///
    /// <para>A part that keeps the width it asked for is measured once,
    /// with no bound. Measured again at that width, which layout rounding
    /// may have taken part of a pixel off, a text that fits was cut short
    /// with an ellipsis and a name box lost the edge of its last letter
    /// (seen 2026-11-12).</para>
    ///
    /// <para>A row of a setting is such a line too: its label, its
    /// control and its reset. <see cref="MinLineHeight"/> keeps every row
    /// the same height whatever its control's. With
    /// <see cref="EndsApart"/> the parts marked
    /// <see cref="AtEndProperty"/> stand against the line's end, so a
    /// row's reset is at the row's end and what the row has left over
    /// lies between it and the control.</para>
    /// </summary>
    public sealed class FlexLinePanel : Panel
    {
        public static readonly DependencyProperty BasisProperty = DependencyProperty.RegisterAttached(
            "Basis", typeof(double), typeof(FlexLinePanel),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static readonly DependencyProperty GrowProperty = DependencyProperty.RegisterAttached(
            "Grow", typeof(double), typeof(FlexLinePanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(FlexLinePanel),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty LineGapProperty = DependencyProperty.Register(
            nameof(LineGap), typeof(double), typeof(FlexLinePanel),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty AtEndProperty = DependencyProperty.RegisterAttached(
            "AtEnd", typeof(bool), typeof(FlexLinePanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static readonly DependencyProperty EndsApartProperty = DependencyProperty.Register(
            nameof(EndsApart), typeof(bool), typeof(FlexLinePanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

        public static readonly DependencyProperty MinLineHeightProperty = DependencyProperty.Register(
            nameof(MinLineHeight), typeof(double), typeof(FlexLinePanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static double GetBasis(UIElement element) => (double)element.GetValue(BasisProperty);

        public static void SetBasis(UIElement element, double value) => element.SetValue(BasisProperty, value);

        public static double GetGrow(UIElement element) => (double)element.GetValue(GrowProperty);

        public static void SetGrow(UIElement element, double value) => element.SetValue(GrowProperty, value);

        public static bool GetAtEnd(UIElement element) => (bool)element.GetValue(AtEndProperty);

        public static void SetAtEnd(UIElement element, bool value) => element.SetValue(AtEndProperty, value);

        /// <summary>Whether the parts marked <see cref="AtEndProperty"/>
        /// stand against the line's end. Unset, they stand where the line
        /// puts them, after the part before them.</summary>
        public bool EndsApart
        {
            get => (bool)GetValue(EndsApartProperty);
            set => SetValue(EndsApartProperty, value);
        }

        /// <summary>The least height of a line.</summary>
        public double MinLineHeight
        {
            get => (double)GetValue(MinLineHeightProperty);
            set => SetValue(MinLineHeightProperty, value);
        }

        /// <summary>The space between the parts of a line.</summary>
        public double Gap
        {
            get => (double)GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        /// <summary>The space between lines.</summary>
        public double LineGap
        {
            get => (double)GetValue(LineGapProperty);
            set => SetValue(LineGapProperty, value);
        }

        /// <summary>How many parts each line holds: parts are taken in
        /// order while their bases fit the width with the gap between
        /// them. A line holds one part at least, so a part wider than the
        /// width stands alone. With no bound, one line holds them all.</summary>
        internal static List<int> Lines(IReadOnlyList<double> bases, double width, double gap)
        {
            var lines = new List<int>();
            if (bases.Count == 0) return lines;
            if (double.IsInfinity(width) || double.IsNaN(width))
            {
                lines.Add(bases.Count);
                return lines;
            }
            int count = 0;
            double used = 0;
            for (int i = 0; i < bases.Count; i++)
            {
                double with = count == 0 ? bases[i] : used + gap + bases[i];
                // A hair of slack, so a sum that is the width but for the
                // rounding of its parts still fits.
                if (count > 0 && with > width + 0.01)
                {
                    lines.Add(count);
                    count = 0;
                    with = bases[i];
                }
                used = with;
                count++;
            }
            lines.Add(count);
            return lines;
        }

        /// <summary>The widths of one line's parts: their bases, with what
        /// the line has left over shared out by their grow shares. A line
        /// narrower than its bases gives its one part the line's width.
        /// With no bound each part keeps its basis.</summary>
        internal static double[] Share(IReadOnlyList<double> bases, IReadOnlyList<double> grows, double width, double gap)
        {
            var widths = new double[bases.Count];
            double sum = 0, shares = 0;
            for (int i = 0; i < bases.Count; i++)
            {
                widths[i] = bases[i];
                sum += bases[i];
                shares += Math.Max(0, grows[i]);
            }
            if (double.IsInfinity(width) || double.IsNaN(width) || bases.Count == 0) return widths;
            double left = width - sum - gap * (bases.Count - 1);
            if (left < 0)
            {
                // Only a line of one part can be over its width.
                if (bases.Count == 1) widths[0] = Math.Max(0, width);
                return widths;
            }
            if (shares <= 0) return widths;
            for (int i = 0; i < bases.Count; i++)
                widths[i] += left * Math.Max(0, grows[i]) / shares;
            return widths;
        }

        /// <summary>Where each part of a line starts: from the line's
        /// start, with the gap between them. With
        /// <paramref name="endsApart"/> the end parts that close the line
        /// stand against its end, the last one flush with it, when the
        /// line has room left over. An end part with another part after it
        /// stays where the line puts it, and so does every part of a line
        /// with no bound.</summary>
        internal static double[] Starts(IReadOnlyList<double> widths, IReadOnlyList<bool> atEnd, double width, double gap, bool endsApart)
        {
            var starts = new double[widths.Count];
            double x = 0;
            for (int i = 0; i < widths.Count; i++)
            {
                starts[i] = x;
                x += widths[i] + gap;
            }
            if (!endsApart || double.IsInfinity(width) || double.IsNaN(width)) return starts;
            double end = width;
            for (int i = widths.Count - 1; i >= 0 && atEnd[i]; i--)
            {
                double start = end - widths[i];
                if (start <= starts[i]) break;
                starts[i] = start;
                end = start - gap;
            }
            return starts;
        }

        private List<UIElement> Shown()
        {
            var shown = new List<UIElement>();
            foreach (UIElement child in InternalChildren)
            {
                if (child != null && child.Visibility != Visibility.Collapsed) shown.Add(child);
            }
            return shown;
        }

        /// <summary>The parts' bases. A part with none set is measured with
        /// no bound first, and its basis is the width it asks for.</summary>
        private static (double[] Bases, double[] Grows) Plan(List<UIElement> parts)
        {
            var bases = new double[parts.Count];
            var grows = new double[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                double basis = GetBasis(parts[i]);
                if (double.IsNaN(basis))
                {
                    parts[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    basis = parts[i].DesiredSize.Width;
                }
                bases[i] = Math.Max(0, basis);
                grows[i] = GetGrow(parts[i]);
            }
            return (bases, grows);
        }

        // The plan of the last measure, which the arrange lays out by: a
        // part with no basis was measured with no bound for it, and has
        // been measured again since at the width it got.
        private double[] _bases = Array.Empty<double>();
        private double[] _grows = Array.Empty<double>();

        protected override Size MeasureOverride(Size availableSize)
        {
            var parts = Shown();
            if (parts.Count == 0)
            {
                _bases = _grows = Array.Empty<double>();
                return new Size(0, 0);
            }
            var (bases, grows) = Plan(parts);
            _bases = bases;
            _grows = grows;
            double height = 0, widest = 0;
            int first = 0;
            var lines = Lines(bases, availableSize.Width, Gap);
            for (int line = 0; line < lines.Count; line++)
            {
                int count = lines[line];
                var widths = Share(new ArraySegment<double>(bases, first, count), new ArraySegment<double>(grows, first, count),
                    availableSize.Width, Gap);
                double tallest = 0, across = Gap * (count - 1);
                for (int i = 0; i < count; i++)
                {
                    var part = parts[first + i];
                    bool keepsItsOwnWidth = double.IsNaN(GetBasis(part)) && widths[i] == bases[first + i];
                    if (!keepsItsOwnWidth) part.Measure(new Size(widths[i], double.PositiveInfinity));
                    tallest = Math.Max(tallest, parts[first + i].DesiredSize.Height);
                    across += widths[i];
                }
                height += Math.Max(tallest, MinLineHeight) + (line > 0 ? LineGap : 0);
                widest = Math.Max(widest, across);
                first += count;
            }
            return new Size(double.IsInfinity(availableSize.Width) ? widest : availableSize.Width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var parts = Shown();
            if (parts.Count == 0) return finalSize;
            // The measure's plan, unless the parts have changed since.
            double[] bases = _bases, grows = _grows;
            if (bases.Length != parts.Count) (bases, grows) = Plan(parts);
            double y = 0;
            int first = 0;
            var lines = Lines(bases, finalSize.Width, Gap);
            for (int line = 0; line < lines.Count; line++)
            {
                int count = lines[line];
                var widths = Share(new ArraySegment<double>(bases, first, count), new ArraySegment<double>(grows, first, count),
                    finalSize.Width, Gap);
                double tallest = MinLineHeight;
                var atEnd = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    tallest = Math.Max(tallest, parts[first + i].DesiredSize.Height);
                    atEnd[i] = GetAtEnd(parts[first + i]);
                }
                if (line > 0) y += LineGap;
                double[] starts = Starts(widths, atEnd, finalSize.Width, Gap, EndsApart);
                for (int i = 0; i < count; i++)
                    parts[first + i].Arrange(new Rect(starts[i], y, widths[i], tallest));
                y += tallest;
                first += count;
            }
            return finalSize;
        }
    }
}
