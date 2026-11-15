using System;
using System.Windows;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>
    /// A thin bar for a live level, drawn in one pass: the mappings list
    /// holds one for every axis in view and they move thirty times a
    /// second, so a templated control for each was more than a level needs.
    ///
    /// <para><see cref="Level"/> is where the value stands in its range, 0
    /// to 1. A plain bar fills from its start. A centered bar
    /// (<see cref="IsCentered"/>) has a tick in the middle, where the value
    /// rests, and fills from the tick toward the value, either way.</para>
    /// </summary>
    public sealed class LevelBar : FrameworkElement
    {
        public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
            nameof(Level), typeof(double), typeof(LevelBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsCenteredProperty = DependencyProperty.Register(
            nameof(IsCentered), typeof(bool), typeof(LevelBar),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
            nameof(Track), typeof(Brush), typeof(LevelBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(LevelBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TickProperty = DependencyProperty.Register(
            nameof(Tick), typeof(Brush), typeof(LevelBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The track's height. The tick stands over it.</summary>
        public const double TrackHeight = 4;

        public double Level
        {
            get => (double)GetValue(LevelProperty);
            set => SetValue(LevelProperty, value);
        }

        public bool IsCentered
        {
            get => (bool)GetValue(IsCenteredProperty);
            set => SetValue(IsCenteredProperty, value);
        }

        public Brush Track
        {
            get => (Brush)GetValue(TrackProperty);
            set => SetValue(TrackProperty, value);
        }

        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public Brush Tick
        {
            get => (Brush)GetValue(TickProperty);
            set => SetValue(TickProperty, value);
        }

        /// <summary>The part of the bar that is filled, as a start and a
        /// width: from the start to the level, or from the middle to the
        /// level on a centered bar. A level that is no number fills
        /// nothing.</summary>
        internal static (double Start, double Width) FillOf(double level, bool centered, double width)
        {
            if (double.IsNaN(level) || width <= 0) return (0, 0);
            level = Math.Clamp(level, 0, 1);
            if (!centered) return (0, level * width);
            double middle = width / 2, at = level * width;
            return at >= middle ? (middle, at - middle) : (at, middle - at);
        }

        protected override Size MeasureOverride(Size availableSize)
            => new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 10);

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth, height = ActualHeight;
            if (width <= 0 || height <= 0) return;
            double top = (height - TrackHeight) / 2;
            if (Track != null)
                dc.DrawRoundedRectangle(Track, null, new Rect(0, top, width, TrackHeight), TrackHeight / 2, TrackHeight / 2);
            var (start, fill) = FillOf(Level, IsCentered, width);
            if (Fill != null && fill > 0.25)
                dc.DrawRoundedRectangle(Fill, null, new Rect(start, top, fill, TrackHeight), TrackHeight / 2, TrackHeight / 2);
            if (IsCentered && Tick != null)
                dc.DrawRectangle(Tick, null, new Rect(Math.Round(width / 2) - 0.5, 0, 1, height));
        }
    }
}
