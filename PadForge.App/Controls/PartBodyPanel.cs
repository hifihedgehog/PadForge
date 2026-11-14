using System;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// The body of a part card (the sections pass of #574): the settings
    /// most people change, then the part's live view, what PadForge reads
    /// from it right now. A tuning card reads left to right, set it and
    /// watch it, so in a card at least <see cref="SideBySideWidth"/> wide
    /// the live view stands beside the settings, which take at most
    /// <see cref="SettingsWidth"/>, and fills the rest. In a narrower card,
    /// two of them to a row of the page, the live view stands under the
    /// settings across the card's width. The first child is the settings
    /// and the second the live view. With no live view, or a collapsed
    /// one, the settings have the whole width.
    ///
    /// <para>A body whose <see cref="SideBySideWidth"/> is NaN stands the
    /// two side by side whenever both fit: the settings at
    /// <see cref="SettingsWidth"/> and the live view at the width it asks
    /// for. A stick's card is one: its live view is an instrument of a
    /// fixed size, and its settings column is sized for the language.</para>
    /// </summary>
    public sealed class PartBodyPanel : Panel
    {
        public static readonly DependencyProperty SideBySideWidthProperty = DependencyProperty.Register(
            nameof(SideBySideWidth), typeof(double), typeof(PartBodyPanel),
            new FrameworkPropertyMetadata(900.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty SettingsWidthProperty = DependencyProperty.Register(
            nameof(SettingsWidth), typeof(double), typeof(PartBodyPanel),
            new FrameworkPropertyMetadata(560.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(PartBodyPanel),
            new FrameworkPropertyMetadata(22.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty StackGapProperty = DependencyProperty.Register(
            nameof(StackGap), typeof(double), typeof(PartBodyPanel),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The least width at which the live view stands beside
        /// the settings. NaN: whenever both fit at their own widths.</summary>
        public double SideBySideWidth
        {
            get => (double)GetValue(SideBySideWidthProperty);
            set => SetValue(SideBySideWidthProperty, value);
        }

        /// <summary>The most the settings take while the live view stands
        /// beside them.</summary>
        public double SettingsWidth
        {
            get => (double)GetValue(SettingsWidthProperty);
            set => SetValue(SettingsWidthProperty, value);
        }

        /// <summary>The room between the settings and the live view beside them.</summary>
        public double Gap
        {
            get => (double)GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        /// <summary>The room between the settings and the live view under them.</summary>
        public double StackGap
        {
            get => (double)GetValue(StackGapProperty);
            set => SetValue(StackGapProperty, value);
        }

        /// <summary>Whether the parts were last measured side by side.</summary>
        internal bool IsSideBySide { get; private set; }

        /// <summary>The width of the settings and of the live view for a body
        /// of the given width: side by side when the body is at least the
        /// least width for it, else each the whole width. A body with no
        /// bound stacks.</summary>
        internal static (bool SideBySide, double Settings, double Live) Widths(double width, double sideBySideWidth,
            double settingsWidth, double gap)
        {
            if (double.IsInfinity(width) || double.IsNaN(width) || width < sideBySideWidth)
                return (false, width, width);
            double settings = Math.Min(settingsWidth, Math.Max(0, width - gap));
            return (true, settings, Math.Max(0, width - gap - settings));
        }

        /// <summary>The least width for the two side by side: the one set,
        /// or with none set (NaN) the settings' width, the gap and the
        /// width the live view asks for.</summary>
        internal static double LeastSideBySide(double sideBySideWidth, double settingsWidth, double gap, double liveAsks)
            => double.IsNaN(sideBySideWidth) ? settingsWidth + gap + liveAsks : sideBySideWidth;

        // The least width the last measure decided by, which the arrange
        // decides by too.
        private double _least = double.PositiveInfinity;

        private (UIElement Settings, UIElement Live) Parts()
        {
            UIElement settings = null, live = null;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                if (settings == null) settings = child;
                else if (live == null) live = child;
            }
            return (settings, live);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var (settings, live) = Parts();
            foreach (UIElement child in InternalChildren)
                if (child != null && child != settings && child != live) child.Measure(new Size(0, 0));
            if (settings == null) return new Size(0, 0);
            if (live == null)
            {
                IsSideBySide = false;
                settings.Measure(availableSize);
                return settings.DesiredSize;
            }
            double liveAsks = 0;
            if (double.IsNaN(SideBySideWidth))
            {
                live.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                liveAsks = live.DesiredSize.Width;
            }
            _least = LeastSideBySide(SideBySideWidth, SettingsWidth, Gap, liveAsks);
            var (side, settingsWidth, liveWidth) = Widths(availableSize.Width, _least, SettingsWidth, Gap);
            IsSideBySide = side;
            settings.Measure(new Size(settingsWidth, double.PositiveInfinity));
            live.Measure(new Size(liveWidth, double.PositiveInfinity));
            double width = double.IsInfinity(availableSize.Width)
                ? Math.Max(settings.DesiredSize.Width, live.DesiredSize.Width)
                : availableSize.Width;
            double height = side
                ? Math.Max(settings.DesiredSize.Height, live.DesiredSize.Height)
                : settings.DesiredSize.Height + StackGap + live.DesiredSize.Height;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (settings, live) = Parts();
            if (settings == null) return finalSize;
            if (live == null)
            {
                settings.Arrange(new Rect(finalSize));
                return finalSize;
            }
            var (side, settingsWidth, liveWidth) = Widths(finalSize.Width, _least, SettingsWidth, Gap);
            if (side)
            {
                settings.Arrange(new Rect(0, 0, settingsWidth, finalSize.Height));
                live.Arrange(new Rect(settingsWidth + Gap, 0, liveWidth, live.DesiredSize.Height));
            }
            else
            {
                double top = settings.DesiredSize.Height;
                settings.Arrange(new Rect(0, 0, finalSize.Width, top));
                live.Arrange(new Rect(0, top + StackGap, finalSize.Width, live.DesiredSize.Height));
            }
            return finalSize;
        }
    }
}
