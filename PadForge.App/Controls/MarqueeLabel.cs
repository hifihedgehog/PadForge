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
    /// <para>The copy slides 40 pixels a second, and its ends fade only while
    /// it slides: a mask kept at rest dimmed the ends of a line that fit (user
    /// report, 2026-07-06).</para>
    ///
    /// <para>The moving copy sits in a Canvas, which measures it unconstrained
    /// and clips it at its own bounds. A trimmed TextBlock would only slide
    /// its ellipsis. Reduced motion (SystemParameters.ClientAreaAnimation)
    /// keeps the text trimmed, and the tooltip carries it. So does the app
    /// standing behind another window (AmbientMotionProbe).</para>
    /// </summary>
    public sealed class MarqueeLabel : Grid
    {
        // A hover asked for the name, so the first pause is short. The end
        // pause leaves time to read the tail.
        private const double StartPauseSeconds = 0.6;
        private const double EndPauseSeconds = 1.5;
        private const double PixelsPerSecond = 40.0;

        private static readonly LinearGradientBrush EdgeFade = MakeEdgeFade();

        private static LinearGradientBrush MakeEdgeFade()
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.05));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.95));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            brush.Freeze();
            return brush;
        }

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(MarqueeLabel),
            new PropertyMetadata(string.Empty, OnTextChanged));

        public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
            nameof(Active), typeof(bool), typeof(MarqueeLabel),
            new PropertyMetadata(false, OnActiveChanged));

        /// <summary>Mirrors Windows' animation setting. A test sets it.</summary>
        internal static Func<bool> MotionEnabled = () => SystemParameters.ClientAreaAnimation;

        /// <summary>Whether the app is in front, where an endless slide may
        /// run: the ambient motion probe's answer. A test sets it.</summary>
        internal static Func<bool> AppInFront = () => Common.AmbientMotionProbe.Instance.IsAppActive;

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
            // Loaded can come again with no Unloaded between, so the probe
            // is let go before it is taken.
            Loaded += (_, _) =>
            {
                Common.AmbientMotionProbe.Instance.PropertyChanged -= OnAmbientMotionChanged;
                Common.AmbientMotionProbe.Instance.PropertyChanged += OnAmbientMotionChanged;
                if (Active) Restart();
            };
            Unloaded += (_, _) =>
            {
                Common.AmbientMotionProbe.Instance.PropertyChanged -= OnAmbientMotionChanged;
                Stop();
            };
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
            // The moving copy stands where its text starts, a Latin name at
            // the left edge and an Arabic or Hebrew one at the right, in a
            // window of either direction, so the slide shows the name from
            // its start toward its end.
            if (Common.TextDirection.FirstStrongDirection(text) is FlowDirection own)
                label._track.FlowDirection = own;
            else
                label._track.ClearValue(FlowDirectionProperty);
            label.ToolTip = text.Length > 0 ? text : null;
            if (label.Active) label.Restart();
        }

        private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var label = (MarqueeLabel)d;
            if ((bool)e.NewValue) label.Restart();
            else label.Stop();
        }

        private void OnAmbientMotionChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Common.AmbientMotionProbe.IsAppActive)) FollowApp();
        }

        /// <summary>The app came to the front or left it. The slide repeats
        /// forever, so it is ambient motion and costs its frames whether or
        /// not anyone can see it. A pointer left on the card while the user
        /// switches away by keyboard sends no mouse leave, so Active stays
        /// true: the slide stops while the app is behind another window,
        /// minimized or in the tray, and starts again when the app is
        /// back.</summary>
        private void FollowApp()
        {
            if (Active) Restart();
            else Stop();
        }

        private void Restart()
        {
            Stop();
            if (!MotionEnabled() || !AppInFront() || !IsLoaded || ActualWidth <= 0) return;

            _run.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double overflow = _run.DesiredSize.Width - ActualWidth;
            if (overflow < 1) return;

            Canvas.SetTop(_run, Math.Max(0, (ActualHeight - _run.DesiredSize.Height) / 2));
            _rest.Visibility = Visibility.Hidden;
            _track.Visibility = Visibility.Visible;
            _track.OpacityMask = EdgeFade;
            // Moving text in a BitmapCache composites as a texture instead of
            // re-rasterizing its glyphs at every subpixel offset, which was
            // profiled as rasterizer work on every frame.
            if (_run.CacheMode == null)
                _run.CacheMode = new BitmapCache();

            double travel = overflow / PixelsPerSecond;
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
            _track.OpacityMask = null;
            _track.Visibility = Visibility.Hidden;
            _rest.Visibility = Visibility.Visible;
        }
    }
}
