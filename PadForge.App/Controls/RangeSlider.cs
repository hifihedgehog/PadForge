using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PadForge.Resources.Strings;

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
            // The slider itself takes no focus: each of its two thumbs is a
            // Tab stop that the arrow keys move (#574). While only the
            // pointer moved them, a trigger's range could be set by keyboard
            // in the boxes beside the slider alone, and those boxes went
            // under More.
            FocusableProperty.OverrideMetadata(typeof(RangeSlider), new FrameworkPropertyMetadata(false));
        }

        /// <summary>What an arrow key moves an end by, and what Page Up and
        /// Page Down move it by.</summary>
        internal const double SmallStep = 1, LargeStep = 10;

        /// <summary>The slider is no control of its own to UI Automation:
        /// its two thumbs are, each a slider with the row's name.</summary>
        protected override AutomationPeer OnCreateAutomationPeer() => new RangeSliderPeer(this);

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
            return Math.Round(v, 1);
        }

        private static object CoerceUpperValue(DependencyObject d, object value)
        {
            var slider = (RangeSlider)d;
            double v = (double)value;
            v = Math.Max(v, slider.LowerValue);
            v = Math.Min(v, slider.Maximum);
            return Math.Round(v, 1);
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var slider = (RangeSlider)d;
            slider.CoerceValue(LowerValueProperty);
            slider.CoerceValue(UpperValueProperty);
            slider.UpdateVisuals();
            // A screen reader on a thumb hears its new value.
            Thumb moved = e.Property == LowerValueProperty ? slider._lowerThumb
                        : e.Property == UpperValueProperty ? slider._upperThumb : null;
            if (moved != null && AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged))
                UIElementAutomationPeer.FromElement(moved)?.RaisePropertyChangedEvent(
                    RangeValuePatternIdentifiers.ValueProperty, (double)e.OldValue, (double)e.NewValue);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_lowerThumb != null)
            {
                _lowerThumb.DragDelta -= OnLowerThumbDragDelta;
                _lowerThumb.KeyDown -= OnThumbKeyDown;
            }
            if (_upperThumb != null)
            {
                _upperThumb.DragDelta -= OnUpperThumbDragDelta;
                _upperThumb.KeyDown -= OnThumbKeyDown;
            }

            _track = GetTemplateChild("PART_Track") as FrameworkElement;
            _lowerThumb = GetTemplateChild("PART_LowerThumb") as Thumb;
            _upperThumb = GetTemplateChild("PART_UpperThumb") as Thumb;
            _rangeHighlight = GetTemplateChild("PART_RangeHighlight") as FrameworkElement;

            if (_lowerThumb != null)
            {
                _lowerThumb.DragDelta += OnLowerThumbDragDelta;
                _lowerThumb.KeyDown += OnThumbKeyDown;
            }
            if (_upperThumb != null)
            {
                _upperThumb.DragDelta += OnUpperThumbDragDelta;
                _upperThumb.KeyDown += OnThumbKeyDown;
            }
            if (_track != null) _track.SizeChanged += (_, _) => UpdateVisuals();

            UpdateVisuals();
        }

        /// <summary>Whether a thumb is the one that sets the range's upper
        /// end.</summary>
        internal bool IsUpper(Thumb thumb) => ReferenceEquals(thumb, _upperThumb);

        /// <summary>The value of one end.</summary>
        internal double EndValue(bool upper) => upper ? UpperValue : LowerValue;

        /// <summary>The least and the most one end can be: the lower end
        /// stops at the upper one, and the upper at the lower.</summary>
        internal (double Least, double Most) EndBounds(bool upper)
            => upper ? (LowerValue, Maximum) : (Minimum, UpperValue);

        /// <summary>Sets one end, inside its bounds.</summary>
        internal void SetEnd(bool upper, double value)
        {
            var (least, most) = EndBounds(upper);
            value = Math.Round(Math.Clamp(value, least, most), 1);
            if (upper) UpperValue = value;
            else LowerValue = value;
        }

        /// <summary>Where a key takes an end from <paramref name="value"/>:
        /// an arrow one step, Page Up and Page Down a large step, Home and
        /// End to the end's own bounds. The slider reads left to right in
        /// every language, so Left and Down always lower it. NaN for a key
        /// that moves nothing.</summary>
        internal static double Nudged(double value, Key key, double least, double most)
            => key switch
            {
                Key.Left or Key.Down => Math.Max(least, value - SmallStep),
                Key.Right or Key.Up => Math.Min(most, value + SmallStep),
                Key.PageDown => Math.Max(least, value - LargeStep),
                Key.PageUp => Math.Min(most, value + LargeStep),
                Key.Home => least,
                Key.End => most,
                _ => double.NaN,
            };

        private void OnThumbKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not Thumb thumb) return;
            bool upper = IsUpper(thumb);
            var (least, most) = EndBounds(upper);
            double next = Nudged(EndValue(upper), e.Key, least, most);
            if (double.IsNaN(next)) return;
            SetEnd(upper, next);
            e.Handled = true;
        }

        private void OnLowerThumbDragDelta(object sender, DragDeltaEventArgs e)
        {
            double usable = GetUsableWidth();
            if (usable <= 0) return;

            double delta = e.HorizontalChange / usable * (Maximum - Minimum);
            LowerValue = Math.Round(Math.Clamp(LowerValue + delta, Minimum, UpperValue), 1);
        }

        private void OnUpperThumbDragDelta(object sender, DragDeltaEventArgs e)
        {
            double usable = GetUsableWidth();
            if (usable <= 0) return;

            double delta = e.HorizontalChange / usable * (Maximum - Minimum);
            UpperValue = Math.Round(Math.Clamp(UpperValue + delta, LowerValue, Maximum), 1);
        }

        private double GetUsableWidth()
        {
            if (_track == null) return 0;
            double thumbWidth = _lowerThumb?.ActualWidth ?? 20;
            return _track.ActualWidth - thumbWidth;
        }

        private void UpdateVisuals()
        {
            if (_track == null) return;

            double trackWidth = _track.ActualWidth;
            double thumbWidth = _lowerThumb?.ActualWidth ?? 20;
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

    /// <summary>A thumb of a <see cref="RangeSlider"/>: a Tab stop that reads
    /// to a screen reader as a slider, with the range's name and its own
    /// end's value.</summary>
    public sealed class RangeThumb : Thumb
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new RangeThumbPeer(this);
    }

    /// <summary>The range slider's peer: outside the control and content
    /// views itself, with its two thumbs under it.</summary>
    internal sealed class RangeSliderPeer : FrameworkElementAutomationPeer
    {
        public RangeSliderPeer(RangeSlider owner) : base(owner) { }

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }

    /// <summary>A range thumb's peer: a slider named as its range is (the
    /// lower end) or with the word between a range's two ends after that
    /// name (the upper end), as the boxes that type the two ends are named.
    /// Its value is its end's, between that end's own bounds.</summary>
    internal sealed class RangeThumbPeer : FrameworkElementAutomationPeer, IRangeValueProvider
    {
        public RangeThumbPeer(RangeThumb owner) : base(owner) { }

        private RangeThumb Thumb => (RangeThumb)Owner;

        private RangeSlider Slider => Thumb.TemplatedParent as RangeSlider;

        private bool Upper => Slider?.IsUpper(Thumb) == true;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override string GetClassNameCore() => nameof(RangeThumb);

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;

        protected override string GetNameCore()
        {
            string range = Slider == null ? null : AutomationProperties.GetName(Slider);
            if (string.IsNullOrEmpty(range)) return base.GetNameCore();
            return Upper ? range + ", " + Strings.Instance.Common_To : range;
        }

        public override object GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        public bool IsReadOnly => Slider == null || !Thumb.IsEnabled;

        public double Value => Slider?.EndValue(Upper) ?? 0;

        public double Minimum => Slider?.EndBounds(Upper).Least ?? 0;

        public double Maximum => Slider?.EndBounds(Upper).Most ?? 0;

        public double SmallChange => RangeSlider.SmallStep;

        public double LargeChange => RangeSlider.LargeStep;

        public void SetValue(double value)
        {
            if (IsReadOnly) throw new ElementNotEnabledException();
            Slider.SetEnd(Upper, value);
        }
    }
}
