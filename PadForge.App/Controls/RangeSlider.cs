using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PadForge.Controls
{
    [TemplatePart(Name = "PART_Track", Type = typeof(FrameworkElement))]
    [TemplatePart(Name = "PART_LowerThumb", Type = typeof(Thumb))]
    [TemplatePart(Name = "PART_UpperThumb", Type = typeof(Thumb))]
    [TemplatePart(Name = "PART_RangeHighlight", Type = typeof(FrameworkElement))]
    public class RangeSlider : Control
    {
        static RangeSlider()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(RangeSlider),
                new FrameworkPropertyMetadata(typeof(RangeSlider)));
        }

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(0.0, OnValueChanged));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(100.0, OnValueChanged));

        public static readonly DependencyProperty LowerValueProperty =
            DependencyProperty.Register(nameof(LowerValue), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(0.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnValueChanged, CoerceLowerValue));

        public static readonly DependencyProperty UpperValueProperty =
            DependencyProperty.Register(nameof(UpperValue), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(100.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnValueChanged, CoerceUpperValue));

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public double LowerValue
        {
            get => (double)GetValue(LowerValueProperty);
            set => SetValue(LowerValueProperty, value);
        }

        public double UpperValue
        {
            get => (double)GetValue(UpperValueProperty);
            set => SetValue(UpperValueProperty, value);
        }

        private Thumb _lowerThumb;
        private Thumb _upperThumb;
        private FrameworkElement _track;
        private FrameworkElement _rangeHighlight;

        private static object CoerceLowerValue(DependencyObject d, object value)
        {
            var slider = (RangeSlider)d;
            double v = (double)value;
            v = Math.Max(v, slider.Minimum);
            v = Math.Min(v, slider.UpperValue);
            return Math.Round(v);
        }

        private static object CoerceUpperValue(DependencyObject d, object value)
        {
            var slider = (RangeSlider)d;
            double v = (double)value;
            v = Math.Max(v, slider.LowerValue);
            v = Math.Min(v, slider.Maximum);
            return Math.Round(v);
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var slider = (RangeSlider)d;
            slider.CoerceValue(LowerValueProperty);
            slider.CoerceValue(UpperValueProperty);
            slider.UpdateVisuals();
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_lowerThumb != null) _lowerThumb.DragDelta -= OnLowerThumbDragDelta;
            if (_upperThumb != null) _upperThumb.DragDelta -= OnUpperThumbDragDelta;

            _track = GetTemplateChild("PART_Track") as FrameworkElement;
            _lowerThumb = GetTemplateChild("PART_LowerThumb") as Thumb;
            _upperThumb = GetTemplateChild("PART_UpperThumb") as Thumb;
            _rangeHighlight = GetTemplateChild("PART_RangeHighlight") as FrameworkElement;

            if (_lowerThumb != null) _lowerThumb.DragDelta += OnLowerThumbDragDelta;
            if (_upperThumb != null) _upperThumb.DragDelta += OnUpperThumbDragDelta;
            if (_track != null) _track.SizeChanged += (_, _) => UpdateVisuals();

            UpdateVisuals();
        }

        private void OnLowerThumbDragDelta(object sender, DragDeltaEventArgs e)
        {
            double usable = GetUsableWidth();
            if (usable <= 0) return;

            double delta = e.HorizontalChange / usable * (Maximum - Minimum);
            LowerValue = Math.Round(Math.Clamp(LowerValue + delta, Minimum, UpperValue));
        }

        private void OnUpperThumbDragDelta(object sender, DragDeltaEventArgs e)
        {
            double usable = GetUsableWidth();
            if (usable <= 0) return;

            double delta = e.HorizontalChange / usable * (Maximum - Minimum);
            UpperValue = Math.Round(Math.Clamp(UpperValue + delta, LowerValue, Maximum));
        }

        private double GetUsableWidth()
        {
            if (_track == null) return 0;
            double thumbWidth = _lowerThumb?.ActualWidth ?? 14;
            return _track.ActualWidth - thumbWidth;
        }

        private void UpdateVisuals()
        {
            if (_track == null) return;

            double trackWidth = _track.ActualWidth;
            double thumbWidth = _lowerThumb?.ActualWidth ?? 14;
            double thumbHalf = thumbWidth / 2;
            double usable = trackWidth - thumbWidth;
            double range = Maximum - Minimum;

            if (usable <= 0 || range <= 0) return;

            double lowerFrac = (LowerValue - Minimum) / range;
            double upperFrac = (UpperValue - Minimum) / range;

            double lowerLeft = lowerFrac * usable;
            double upperLeft = upperFrac * usable;

            if (_lowerThumb != null)
                Canvas.SetLeft(_lowerThumb, lowerLeft);
            if (_upperThumb != null)
                Canvas.SetLeft(_upperThumb, upperLeft);

            if (_rangeHighlight != null)
            {
                Canvas.SetLeft(_rangeHighlight, lowerLeft + thumbHalf);
                _rangeHighlight.Width = Math.Max(0, upperLeft - lowerLeft);
            }
        }
    }
}
