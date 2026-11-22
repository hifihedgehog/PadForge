using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Common.Themes;

namespace PadForge.Controls
{
    /// <summary>
    /// A theme's picture behind the top of the page (#591). It lies under
    /// every page, at the page area's upper right, as wide as the area up
    /// to 1176 units, and keeps its shape, so a narrow window draws all of
    /// it smaller.
    ///
    /// Home shows the whole picture: its engine card goes translucent
    /// while a picture is up, and the picture shows through it. A page of
    /// solid cards shows the picture in its header strip only, faded out
    /// before the first card, so no part of a subject shows in the gap
    /// between two cards.
    ///
    /// It draws the bitmap itself, into a box it measures, so a picture's
    /// own pixel count and resolution do not change its size, and it takes
    /// no click. A picture keeps its shape: it is as tall as the box and
    /// stands at the box's right, and one wider than the box is cut at the
    /// box's left. A picture of the box's shape, as the starters', the
    /// builder's and a game look's are, fills it.
    ///
    /// A picture never mirrors: its logo is words. Under a right-to-left
    /// language it stays at the right as drawn, where the page's own words
    /// now begin, so it is drawn fainter there and the words stay in front.
    /// </summary>
    public sealed class ThemeBackdrop : FrameworkElement
    {
        /// <summary>The picture's size in units.</summary>
        internal const double PictureWidth = 1176, PictureHeight = 240;

        /// <summary>On a page of solid cards: how far down the picture is
        /// whole, and where it is gone.</summary>
        internal const double StripSolid = 66, StripEnd = 86;

        /// <summary>How much of the picture shows under a right-to-left language.</summary>
        internal const double RightToLeftOpacity = 0.6;

        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(BitmapSource), typeof(ThemeBackdrop),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsWholeProperty = DependencyProperty.Register(
            nameof(IsWhole), typeof(bool), typeof(ThemeBackdrop),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly Brush s_strip = MakeStrip();

        /// <summary>The picture, or null for none.</summary>
        public BitmapSource Source
        {
            get => (BitmapSource)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        /// <summary>Whether all of the picture shows (Home), or its header strip only.</summary>
        public bool IsWhole
        {
            get => (bool)GetValue(IsWholeProperty);
            set => SetValue(IsWholeProperty, value);
        }

        public ThemeBackdrop()
        {
            IsHitTestVisible = false;
            Focusable = false;
            // The whole width, with the picture drawn at its right: the
            // element is pinned left to right, so its own right is the
            // window's right whatever the language.
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Top;
            FlowDirection = FlowDirection.LeftToRight;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            // Loaded can come again with no Unloaded between.
            Loaded += (_, _) =>
            {
                ThemeManager.Applied -= OnLookApplied;
                ThemeManager.Applied += OnLookApplied;
                PadForge.Resources.Strings.Strings.CultureChanged -= OnCultureChanged;
                PadForge.Resources.Strings.Strings.CultureChanged += OnCultureChanged;
                Source = ThemeManager.Picture;
            };
            Unloaded += (_, _) =>
            {
                ThemeManager.Applied -= OnLookApplied;
                PadForge.Resources.Strings.Strings.CultureChanged -= OnCultureChanged;
            };
        }

        // Another language may read the other way. The window takes its
        // direction after the language changes, so the picture is drawn
        // again once that is done.
        private void OnCultureChanged() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        /// <summary>Whether the page around the picture reads right to left.</summary>
        private bool PageReadsRightToLeft
            => VisualTreeHelper.GetParent(this) is FrameworkElement around && around.FlowDirection == FlowDirection.RightToLeft;

        private void OnLookApplied(object sender, EventArgs e)
        {
            // A look is put on by the application's own thread. A backdrop
            // of another thread belongs to a window that closed without
            // unloading it: it lets go.
            if (!CheckAccess())
            {
                ThemeManager.Applied -= OnLookApplied;
                return;
            }
            Source = ThemeManager.Picture;
        }

        private static Brush MakeStrip()
        {
            // Mapped in units from the top, so a picture drawn smaller in a
            // narrow window still ends where the first card begins. Past its
            // end the brush holds its last stop, which is clear.
            var strip = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, StripEnd),
            };
            strip.GradientStops.Add(new GradientStop(Colors.Black, 0));
            strip.GradientStops.Add(new GradientStop(Colors.Black, StripSolid / StripEnd));
            strip.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            strip.Freeze();
            return strip;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Source == null) return default;
            double across = double.IsInfinity(availableSize.Width) ? PictureWidth : availableSize.Width;
            return new Size(across, Math.Min(across, PictureWidth) * PictureHeight / PictureWidth);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var picture = Source;
            if (picture == null || ActualWidth <= 0 || ActualHeight <= 0 || picture.PixelWidth <= 0 || picture.PixelHeight <= 0) return;
            // The box: the area's upper right, smaller in a narrow window.
            double width = Math.Min(ActualWidth, PictureWidth), height = width * PictureHeight / PictureWidth;
            var box = new Rect(ActualWidth - width, 0, width, height);
            double drawn = height * picture.PixelWidth / picture.PixelHeight;
            bool cut = drawn > width + 0.01;
            bool faint = PageReadsRightToLeft;
            if (faint) drawingContext.PushOpacity(RightToLeftOpacity);
            if (!IsWhole) drawingContext.PushOpacityMask(s_strip);
            if (cut) drawingContext.PushClip(new RectangleGeometry(box));
            drawingContext.DrawImage(picture, new Rect(ActualWidth - drawn, 0, drawn, height));
            if (cut) drawingContext.Pop();
            if (!IsWhole) drawingContext.Pop();
            if (faint) drawingContext.Pop();
        }
    }
}
