using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PadForge.Common
{
    /// <summary>
    /// Attached behavior that creates a ticker/marquee effect on a TextBlock
    /// when its text exceeds the available width. Text scrolls left to reveal
    /// the overflow, pauses, then scrolls back.
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
            if (d is TextBlock tb)
            {
                if ((bool)e.NewValue)
                {
                    tb.TextWrapping = TextWrapping.NoWrap;
                    tb.Loaded += OnTextBlockLoaded;
                    tb.SizeChanged += OnTextBlockSizeChanged;

                    // If already loaded, evaluate immediately.
                    if (tb.IsLoaded)
                        EvaluateMarquee(tb);
                }
                else
                {
                    tb.Loaded -= OnTextBlockLoaded;
                    tb.SizeChanged -= OnTextBlockSizeChanged;
                    StopMarquee(tb);
                }
            }
        }

        private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock tb)
                EvaluateMarquee(tb);
        }

        private static void OnTextBlockSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is TextBlock tb)
                EvaluateMarquee(tb);
        }

        private static void EvaluateMarquee(TextBlock tb)
        {
            // Find the clipping parent (Border or Panel with ClipToBounds).
            var parent = VisualTreeHelper.GetParent(tb) as FrameworkElement;
            if (parent == null || parent.ActualWidth <= 0)
                return;

            // Measure unconstrained text width.
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double textWidth = tb.DesiredSize.Width;
            double containerWidth = parent.ActualWidth;

            if (textWidth <= containerWidth)
            {
                StopMarquee(tb);
                return;
            }

            double overflow = textWidth - containerWidth;

            var transform = tb.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                tb.RenderTransform = transform;
            }

            // Speed: ~40px/sec, with 1.5s pause at each end.
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

        private static void StopMarquee(TextBlock tb)
        {
            if (tb.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.X = 0;
            }
        }
    }
}
