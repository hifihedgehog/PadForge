using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>
    /// One card that lists parts which fold to a line each (#574). The
    /// parts are stacked top to bottom with a hairline between them, and
    /// the card's fill, edge and rounded corners are drawn by the panel
    /// itself, which clips the parts to those corners so a part's fill
    /// under the pointer never shows past them.
    ///
    /// <para>A part that is collapsed takes no row and no hairline, so the
    /// card is as tall as the parts that show. With no part showing the
    /// card takes no room at all, its gap included, which a Border around
    /// the same parts could not do: an empty Border still draws its edge
    /// and keeps its margin.</para>
    /// </summary>
    public sealed class FoldList : Panel
    {
        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(FoldList),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
            nameof(Line), typeof(Brush), typeof(FoldList),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CornerProperty = DependencyProperty.Register(
            nameof(Corner), typeof(double), typeof(FoldList),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(FoldList),
            new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The card's fill.</summary>
        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        /// <summary>The card's edge and the hairline between two parts.</summary>
        public Brush Line
        {
            get => (Brush)GetValue(LineProperty);
            set => SetValue(LineProperty, value);
        }

        /// <summary>The radius of the card's corners.</summary>
        public double Corner
        {
            get => (double)GetValue(CornerProperty);
            set => SetValue(CornerProperty, value);
        }

        /// <summary>The room under the card, kept only while a part shows.</summary>
        public double Gap
        {
            get => (double)GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        /// <summary>The thickness of the edge and of a hairline.</summary>
        internal const double Rule = 1.0;

        /// <summary>The height a card takes for parts of the given
        /// heights: the parts, a hairline between each two, the edge over
        /// and under them and the gap. Nothing for no parts.</summary>
        internal static double HeightFor(double[] partHeights, double gap)
        {
            if (partHeights == null || partHeights.Length == 0) return 0;
            double height = 2 * Rule + gap + (partHeights.Length - 1) * Rule;
            foreach (double h in partHeights) height += h;
            return height;
        }

        private int VisibleCount()
        {
            int n = 0;
            foreach (UIElement child in InternalChildren)
                if (child != null && child.Visibility != Visibility.Collapsed) n++;
            return n;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double inner = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Math.Max(0, availableSize.Width - 2 * Rule);
            double width = 0;
            var heights = new System.Collections.Generic.List<double>();
            foreach (UIElement child in InternalChildren)
            {
                if (child == null) continue;
                child.Measure(new Size(inner, double.PositiveInfinity));
                if (child.Visibility == Visibility.Collapsed) continue;
                width = Math.Max(width, child.DesiredSize.Width);
                heights.Add(child.DesiredSize.Height);
            }
            if (heights.Count == 0) return new Size(0, 0);
            return new Size(width + 2 * Rule, HeightFor(heights.ToArray(), Gap));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double inner = Math.Max(0, finalSize.Width - 2 * Rule);
            double y = Rule;
            bool first = true;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null) continue;
                if (child.Visibility == Visibility.Collapsed)
                {
                    child.Arrange(new Rect(0, 0, 0, 0));
                    continue;
                }
                if (!first) y += Rule;
                first = false;
                child.Arrange(new Rect(Rule, y, inner, child.DesiredSize.Height));
                y += child.DesiredSize.Height;
            }
            // A hairline sits where two parts meet, so a part that grew or
            // shrank moves the lines under it.
            InvalidateVisual();
            return finalSize;
        }

        /// <summary>The card's own rectangle: the panel less the gap under it.</summary>
        private Rect CardRect(Size size) => new(0, 0, size.Width, Math.Max(0, size.Height - Gap));

        protected override Geometry GetLayoutClip(Size layoutSlotSize)
        {
            if (VisibleCount() == 0) return base.GetLayoutClip(layoutSlotSize);
            Rect card = CardRect(RenderSize);
            if (card.Width <= 0 || card.Height <= 0) return base.GetLayoutClip(layoutSlotSize);
            var clip = new RectangleGeometry(card, Corner, Corner);
            clip.Freeze();
            return clip;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (VisibleCount() == 0) return;
            Rect card = CardRect(RenderSize);
            if (card.Width <= 0 || card.Height <= 0) return;

            Brush line = Line;
            // The edge is a pen drawn half its thickness inside the card,
            // on whole device pixels.
            var edges = new GuidelineSet(
                new[] { card.Left, card.Right },
                new[] { card.Top, card.Bottom });
            dc.PushGuidelineSet(edges);
            var edge = new Rect(card.Left + Rule / 2, card.Top + Rule / 2,
                Math.Max(0, card.Width - Rule), Math.Max(0, card.Height - Rule));
            double radius = Math.Max(0, Corner - Rule / 2);
            dc.DrawRoundedRectangle(Fill, line != null ? new Pen(line, Rule) : null, edge, radius, radius);
            dc.Pop();
            if (line == null) return;

            bool first = true;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                if (first) { first = false; continue; }
                // The hairline is the row just over this part.
                double top = VisualTreeHelper.GetOffset(child).Y - Rule;
                var rule = new Rect(Rule, top, Math.Max(0, card.Width - 2 * Rule), Rule);
                dc.PushGuidelineSet(new GuidelineSet(null, new[] { rule.Top, rule.Bottom }));
                dc.DrawRectangle(line, null, rule);
                dc.Pop();
            }
        }
    }
}
