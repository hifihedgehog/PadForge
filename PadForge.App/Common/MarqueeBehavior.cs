using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PadForge.Common
{
    /// <summary>
    /// Attached behavior that creates a ticker/marquee effect on any FrameworkElement
    /// when its content exceeds the available width. The element scrolls left to reveal
    /// the overflow, pauses, then scrolls back.
    ///
    /// Usage: Place the element inside a horizontal StackPanel inside a
    /// Border with ClipToBounds="True". The StackPanel gives the element
    /// infinite width (so ActualWidth = true content width). The Border clips.
    /// </summary>
    public static class MarqueeBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(MarqueeBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);
        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement fe)
            {
                if (fe is TextBlock tb)
                    tb.TextWrapping = TextWrapping.NoWrap;

                if ((bool)e.NewValue)
                {
                    fe.Loaded += OnElementLoaded;
                    fe.SizeChanged += OnElementSizeChanged;

                    if (fe.IsLoaded)
                        EvaluateMarquee(fe);
                }
                else
                {
                    fe.Loaded -= OnElementLoaded;
                    fe.SizeChanged -= OnElementSizeChanged;
                    StopMarquee(fe);
                }
            }
        }

        private static void OnElementLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
                EvaluateMarquee(fe);
        }

        private static void OnElementSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement fe)
                EvaluateMarquee(fe);
        }

        // Edge fade applied to the clip ancestor ONLY while the marquee
        // scrolls (user report 2026-07-06: a static mask on the card dimmed
        // the edges of fully-legible text like "No device mapped"). One
        // frozen brush, reference-compared on clear so an ancestor's own
        // mask is never clobbered.
        private static readonly LinearGradientBrush _edgeFadeMask = CreateEdgeFadeMask();

        private static LinearGradientBrush CreateEdgeFadeMask()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.05));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.95));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            brush.Freeze();
            return brush;
        }

        private static void SetEdgeFade(FrameworkElement clipAncestor, bool on)
        {
            if (clipAncestor == null) return;
            if (on)
            {
                if (clipAncestor.OpacityMask == null)
                    clipAncestor.OpacityMask = _edgeFadeMask;
            }
            else if (ReferenceEquals(clipAncestor.OpacityMask, _edgeFadeMask))
            {
                clipAncestor.OpacityMask = null;
            }
        }

        private static void EvaluateMarquee(FrameworkElement fe)
        {
            // Walk up the visual tree to find the first ancestor with ClipToBounds.
            // That ancestor's width is the visible container width.
            double containerWidth = 0;
            FrameworkElement clipAncestor = null;
            DependencyObject current = VisualTreeHelper.GetParent(fe);
            while (current != null)
            {
                if (current is UIElement uie && uie.ClipToBounds &&
                    current is FrameworkElement ancestor && ancestor.ActualWidth > 0)
                {
                    containerWidth = ancestor.ActualWidth;
                    clipAncestor = ancestor;
                    break;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            if (containerWidth <= 0)
                return;

            // The element must be inside a horizontal StackPanel (or similar
            // unconstrained panel) so that ActualWidth reflects the full content width.
            double contentWidth = fe.ActualWidth;
            if (contentWidth <= 0 || contentWidth <= containerWidth)
            {
                StopMarquee(fe);
                SetEdgeFade(clipAncestor, false);
                return;
            }

            SetEdgeFade(clipAncestor, true);

            double overflow = contentWidth - containerWidth;

            var transform = fe.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                fe.RenderTransform = transform;
            }

            // A scrolling marquee moves text at 60fps, and translating an
            // uncached TextBlock re-rasterizes its glyph run at each new
            // subpixel offset (profiled: per-frame CHwRasterizer work). A
            // BitmapCache survives transform changes without invalidating, so
            // the glyphs raster once and the scroll becomes a texture
            // composite. Text anti-aliasing inside a cache is grayscale
            // rather than ClearType, which is the standard trade for moving
            // text.
            if (fe.CacheMode == null)
                fe.CacheMode = new System.Windows.Media.BitmapCache();

            // Speed: ~40px/sec, with 2s pause at each end.
            double durationSeconds = overflow / 40.0;

            var animation = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever
            };

            // Hold at 0 (start) for 2s.
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));

            // Scroll left over duration.
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 + durationSeconds))));

            // Hold at end for 2s.
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4 + durationSeconds))));

            // Scroll back over duration.
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4 + durationSeconds * 2))));

            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private static void StopMarquee(FrameworkElement fe)
        {
            if (fe.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.X = 0;
            }
        }
    }
}
