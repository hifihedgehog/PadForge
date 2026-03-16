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

        private static void EvaluateMarquee(FrameworkElement fe)
        {
            // Walk up the visual tree to find the first ancestor with ClipToBounds.
            // That ancestor's width is the visible container width.
            double containerWidth = 0;
            DependencyObject current = VisualTreeHelper.GetParent(fe);
            while (current != null)
            {
                if (current is UIElement uie && uie.ClipToBounds &&
                    current is FrameworkElement ancestor && ancestor.ActualWidth > 0)
                {
                    containerWidth = ancestor.ActualWidth;
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
                return;
            }

            double overflow = contentWidth - containerWidth;

            var transform = fe.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                fe.RenderTransform = transform;
            }

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
