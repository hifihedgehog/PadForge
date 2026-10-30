using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PadForge.Controls
{
    /// <summary>
    /// One line of text that trims with an ellipsis at rest and, while
    /// <see cref="Active"/>, shows a text too long for its space by scrolling:
    /// a pause, a slide to the end, a pause and the slide back (owner,
    /// 2026-10-30, for the slot cards' Preset name, discussion #543). The
    /// whole text is always the tooltip.
    ///
    /// <para>Common.MarqueeBehavior scrolls for as long as its content
    /// overflows, which suits a device roster that changes on its own. A name
    /// the user reads on demand stays still until the pointer asks for it.
    /// The speed and the edge fade are MarqueeBehavior's, so the two read as
    /// one motion.</para>
    ///
    /// <para>The moving copy sits in a Canvas, which measures it unconstrained
    /// and clips it at its own bounds. A trimmed TextBlock would only slide
    /// its ellipsis. Reduced motion (SystemParameters.ClientAreaAnimation)
    /// keeps the text trimmed, and the tooltip carries it.</para>
    /// </summary>
    public sealed class MarqueeLabel : Grid
    {
        // A hover asked for the name, so the first pause is short. The end
        // pause leaves time to read the tail.
        private const double StartPauseSeconds = 0.6;
        private const double EndPauseSeconds = 1.5;

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(MarqueeLabel),
            new PropertyMetadata(string.Empty, OnTextChanged));

        public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
            nameof(Active), typeof(bool), typeof(MarqueeLabel),
            new PropertyMetadata(false, OnActiveChanged));

        private readonly TextBlock _rest;
        private readonly Canvas _track;
        private readonly TextBlock _run;
        private readonly TranslateTransform _shift = new TranslateTransform();

        public MarqueeLabel()
        {
            _rest = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            _run = new TextBlock { RenderTransform = _shift };
            _track = new Canvas { ClipToBounds = true, Visibility = Visibility.Hidden };
            _track.Children.Add(_run);
            Children.Add(_rest);
            Children.Add(_track);
            SizeChanged += (_, _) => { if (Active) Restart(); };
            Loaded += (_, _) => { if (Active) Restart(); };
            Unloaded += (_, _) => Stop();
        }

        /// <summary>The line shown, trimmed at rest.</summary>
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>While true, a text longer than the space scrolls. Bound
        /// to the hover of the card that holds the label.</summary>
        public bool Active
        {
            get => (bool)GetValue(ActiveProperty);
            set => SetValue(ActiveProperty, value);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var label = (MarqueeLabel)d;
            string text = e.NewValue as string ?? string.Empty;
            label._rest.Text = text;
            label._run.Text = text;
            label.ToolTip = text.Length > 0 ? text : null;
            if (label.Active) label.Restart();
        }

        private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var label = (MarqueeLabel)d;
            if ((bool)e.NewValue) label.Restart();
            else label.Stop();
        }

        private void Restart()
        {
            Stop();
            if (!SystemParameters.ClientAreaAnimation || !IsLoaded || ActualWidth <= 0) return;

            _run.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double overflow = _run.DesiredSize.Width - ActualWidth;
            if (overflow < 1) return;

            Canvas.SetTop(_run, Math.Max(0, (ActualHeight - _run.DesiredSize.Height) / 2));
            _rest.Visibility = Visibility.Hidden;
            _track.Visibility = Visibility.Visible;
            Common.MarqueeBehavior.SetEdgeFade(_track, true);
            // Moving text in a BitmapCache composites as a texture instead of
            // re-rasterizing at every subpixel offset (MarqueeBehavior's
            // profiled reason).
            if (_run.CacheMode == null)
                _run.CacheMode = new BitmapCache();

            double travel = overflow / Common.MarqueeBehavior.PixelsPerSecond;
            double atEnd = StartPauseSeconds + travel;
            double leaveEnd = atEnd + EndPauseSeconds;
            var slide = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            slide.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            slide.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(StartPauseSeconds))));
            slide.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(atEnd))));
            slide.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(leaveEnd))));
            slide.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(leaveEnd + travel))));
            _shift.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        private void Stop()
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _shift.X = 0;
            Common.MarqueeBehavior.SetEdgeFade(_track, false);
            _track.Visibility = Visibility.Hidden;
            _rest.Visibility = Visibility.Visible;
        }
    }
}
