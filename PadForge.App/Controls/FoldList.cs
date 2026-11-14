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
    ///
    /// <para>As a <see cref="IsTray"/> the list draws no card of its own:
    /// it is the foot of the card that holds it, where that part's optional
    /// features stand as lines (the sections pass of #574). It draws its
    /// fill across the card's width with a hairline over it and one
    /// between two lines, no edge at its sides or under it, and rounds its
    /// lower corners to the card's inner corners.</para>
    /// </summary>
    public sealed class FoldList : Panel
    {
        public static readonly DependencyProperty IsTrayProperty = DependencyProperty.Register(
            nameof(IsTray), typeof(bool), typeof(FoldList),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure
                | FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Whether the list is the foot of the card it stands in
        /// rather than a card of its own.</summary>
        public bool IsTray
        {
            get => (bool)GetValue(IsTrayProperty);
            set => SetValue(IsTrayProperty, value);
        }

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
        internal static double HeightFor(double[] partHeights, double gap, bool tray = false)
        {
            if (partHeights == null || partHeights.Length == 0) return 0;
            // A tray has the hairline over it and none under it: the card it
            // stands in draws its own edge there.
            double height = (tray ? Rule : 2 * Rule) + gap + (partHeights.Length - 1) * Rule;
            foreach (double h in partHeights) height += h;
            return height;
        }

        /// <summary>A tray's outline: square at the top, where the card's
        /// rows go on above it, and its lower corners rounded by the given
        /// radius, the card's inner corners.</summary>
        internal static Geometry TrayShape(Rect area, double corner)
        {
            double r = Math.Max(0, Math.Min(corner, Math.Min(area.Width, area.Height) / 2));
            var shape = new StreamGeometry();
            using (StreamGeometryContext g = shape.Open())
            {
                g.BeginFigure(area.TopLeft, true, true);
                g.LineTo(area.TopRight, false, false);
                g.LineTo(new Point(area.Right, area.Bottom - r), false, false);
                g.ArcTo(new Point(area.Right - r, area.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                g.LineTo(new Point(area.Left + r, area.Bottom), false, false);
                g.ArcTo(new Point(area.Left, area.Bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            }
            shape.Freeze();
            return shape;
        }

        private int VisibleCount()
        {
            int n = 0;
            foreach (UIElement child in InternalChildren)
                if (child != null && child.Visibility != Visibility.Collapsed) n++;
            return n;
        }

        /// <summary>The room at each side of a part: the card's edge, or none
        /// in a tray, which runs the full width of the card it stands in.</summary>
        private double Side => IsTray ? 0 : Rule;

        protected override Size MeasureOverride(Size availableSize)
        {
            double side = Side;
            double inner = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Math.Max(0, availableSize.Width - 2 * side);
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
            return new Size(width + 2 * side, HeightFor(heights.ToArray(), Gap, IsTray));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double side = Side;
            double inner = Math.Max(0, finalSize.Width - 2 * side);
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
                child.Arrange(new Rect(side, y, inner, child.DesiredSize.Height));
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
            if (IsTray) return TrayShape(card, Corner);
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
            if (IsTray)
            {
                RenderTray(dc, card, line);
                return;
            }
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

        /// <summary>A tray: its fill, the hairline over it across the card's
        /// width, and a hairline between two lines, each on whole device
        /// pixels.</summary>
        private void RenderTray(DrawingContext dc, Rect card, Brush line)
        {
            dc.DrawGeometry(Fill, null, TrayShape(card, Corner));
            if (line == null) return;
            void Hairline(double top)
            {
                var rule = new Rect(card.Left, top, card.Width, Rule);
                dc.PushGuidelineSet(new GuidelineSet(null, new[] { rule.Top, rule.Bottom }));
                dc.DrawRectangle(line, null, rule);
                dc.Pop();
            }
            Hairline(card.Top);
            bool first = true;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                if (first) { first = false; continue; }
                Hairline(VisualTreeHelper.GetOffset(child).Y - Rule);
            }
        }
    }
}
