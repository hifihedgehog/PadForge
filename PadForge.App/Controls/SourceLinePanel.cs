using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>Which part of a source's line a child is.</summary>
    public enum SourceLinePart
    {
        /// <summary>No part: the child is laid out as a line under the
        /// source, as <see cref="Under"/> is.</summary>
        None,
        /// <summary>The letter a Custom formula knows the source by.</summary>
        Letter,
        Device,
        Mode,
        /// <summary>The input with Record and Clear beside it. It takes
        /// what the other columns leave.</summary>
        Source,
        Invert,
        Half,
        Either,
        Remove,
        /// <summary>A line under the source that starts under Mode: the
        /// settings of the source's mode.</summary>
        Under,
    }

    /// <summary>
    /// One source of a mapping row as a line, and the head over the lines:
    /// Device, Mode, Source, Invert, Half, Bidirectional and Remove, each
    /// in a column of a fixed width but Source, which takes the rest. Every
    /// line of every open row uses the same columns, so the sources read as
    /// a table and a first source looks like any other.
    ///
    /// <para>A switch that is not drawn gives its column to Source: a mode
    /// that reads keys shows them there and has no switches.</para>
    ///
    /// <para>In a narrow host (<see cref="NarrowProperty"/>, which the host
    /// sets and every line inherits) the three switches leave the line and
    /// stand under it, from Mode's column on, each with its name. If Source
    /// would still be under its least width, it takes a line of its own
    /// before them, so its picker never shrinks to a stub.</para>
    ///
    /// <para>With <see cref="LettersProperty"/> set, a column for the
    /// formula's letters opens before Device.</para>
    /// </summary>
    public sealed class SourceLinePanel : Panel
    {
        public const double LetterWidth = 30;
        public const double DeviceWidth = 262;
        public const double NarrowDeviceWidth = 230;
        public const double ModeWidth = 208;
        public const double InvertWidth = 92;
        public const double HalfWidth = 92;
        public const double EitherWidth = 112;
        public const double RemoveWidth = 84;

        /// <summary>The least width of Source on the line. Under it Source
        /// takes a line of its own.</summary>
        public const double SourceLeast = 240;

        /// <summary>The least height of a source's line.</summary>
        public const double LineHeight = 46;

        public static readonly DependencyProperty LineMinProperty = DependencyProperty.Register(
            nameof(LineMin), typeof(double), typeof(SourceLinePanel),
            new FrameworkPropertyMetadata(LineHeight, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>The least height of this panel's line: a source's by
        /// default, and less for the head over the sources.</summary>
        public double LineMin
        {
            get => (double)GetValue(LineMinProperty);
            set => SetValue(LineMinProperty, value);
        }

        /// <summary>The space between the parts that stand under the line.</summary>
        public const double UnderGap = 24;

        /// <summary>The width under which a host is narrow: the columns at
        /// their widths with Source at its least.</summary>
        public static double NarrowBelow(bool letters)
            => (letters ? LetterWidth : 0) + DeviceWidth + ModeWidth + SourceLeast
               + InvertWidth + HalfWidth + EitherWidth + RemoveWidth;

        public static readonly DependencyProperty PartProperty = DependencyProperty.RegisterAttached(
            "Part", typeof(SourceLinePart), typeof(SourceLinePanel),
            new FrameworkPropertyMetadata(SourceLinePart.None, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static readonly DependencyProperty NarrowProperty = DependencyProperty.RegisterAttached(
            "Narrow", typeof(bool), typeof(SourceLinePanel),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty LettersProperty = DependencyProperty.RegisterAttached(
            "Letters", typeof(bool), typeof(SourceLinePanel),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static SourceLinePart GetPart(UIElement element) => (SourceLinePart)element.GetValue(PartProperty);

        public static void SetPart(UIElement element, SourceLinePart value) => element.SetValue(PartProperty, value);

        public static bool GetNarrow(DependencyObject element) => (bool)element.GetValue(NarrowProperty);

        public static void SetNarrow(DependencyObject element, bool value) => element.SetValue(NarrowProperty, value);

        public static bool GetLetters(DependencyObject element) => (bool)element.GetValue(LettersProperty);

        public static void SetLetters(DependencyObject element, bool value) => element.SetValue(LettersProperty, value);

        /// <summary>Where a line's parts stand, for a width.</summary>
        internal readonly struct Plan
        {
            public Plan(double letter, double device, double mode, double sourceStart, double sourceWidth, bool sourceOnLine,
                double invertStart, double halfStart, double eitherStart, double removeStart, bool switchesOnLine, double underStart)
            {
                Letter = letter;
                Device = device;
                Mode = mode;
                SourceStart = sourceStart;
                SourceWidth = sourceWidth;
                SourceOnLine = sourceOnLine;
                InvertStart = invertStart;
                HalfStart = halfStart;
                EitherStart = eitherStart;
                RemoveStart = removeStart;
                SwitchesOnLine = switchesOnLine;
                UnderStart = underStart;
            }

            public double Letter { get; }
            public double Device { get; }
            public double Mode { get; }
            public double SourceStart { get; }
            public double SourceWidth { get; }
            public bool SourceOnLine { get; }
            public double InvertStart { get; }
            public double HalfStart { get; }
            public double EitherStart { get; }
            public double RemoveStart { get; }
            public bool SwitchesOnLine { get; }
            public double UnderStart { get; }
        }

        /// <summary>The plan of a line: the columns from the start, Remove
        /// against the end, and Source between them. A switch that is not
        /// drawn leaves its column to Source. In a narrow host the switches
        /// are off the line, and Source is too when it would be under its
        /// least width.</summary>
        internal static Plan PlanFor(double width, bool narrow, bool letters, bool invert, bool half, bool either)
        {
            double letter = letters ? LetterWidth : 0;
            double device = narrow ? NarrowDeviceWidth : DeviceWidth;
            double under = letter + device;
            if (double.IsInfinity(width) || double.IsNaN(width))
                width = NarrowBelow(letters);
            double removeStart = Math.Max(under + ModeWidth, width - RemoveWidth);
            if (narrow)
            {
                double room = removeStart - under - ModeWidth;
                bool onLine = room >= SourceLeast;
                return new Plan(letter, device, ModeWidth, onLine ? under + ModeWidth : under,
                    onLine ? room : Math.Max(0, width - under), onLine, 0, 0, 0, removeStart, false, under);
            }
            double eitherStart = removeStart - (either ? EitherWidth : 0);
            double halfStart = eitherStart - (half ? HalfWidth : 0);
            double invertStart = halfStart - (invert ? InvertWidth : 0);
            double sourceStart = under + ModeWidth;
            return new Plan(letter, device, ModeWidth, sourceStart, Math.Max(0, invertStart - sourceStart), true,
                invertStart, halfStart, eitherStart, removeStart, true, under);
        }

        private readonly Dictionary<SourceLinePart, UIElement> _parts = new();
        private readonly List<UIElement> _unders = new();

        private void Sort()
        {
            _parts.Clear();
            _unders.Clear();
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                var part = GetPart(child);
                if (part is SourceLinePart.None or SourceLinePart.Under) _unders.Add(child);
                else _parts[part] = child;
            }
        }

        private bool Has(SourceLinePart part) => _parts.ContainsKey(part);

        private Plan PlanOf(double width)
            => PlanFor(width, GetNarrow(this), GetLetters(this), Has(SourceLinePart.Invert), Has(SourceLinePart.Half),
                Has(SourceLinePart.Either));

        private double Measure(SourceLinePart part, double width)
        {
            if (!_parts.TryGetValue(part, out var child)) return 0;
            child.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
            return child.DesiredSize.Height;
        }

        private static readonly SourceLinePart[] Switches =
            { SourceLinePart.Invert, SourceLinePart.Half, SourceLinePart.Either };

        protected override Size MeasureOverride(Size availableSize)
        {
            Sort();
            double width = double.IsInfinity(availableSize.Width) ? NarrowBelow(GetLetters(this)) : availableSize.Width;
            var plan = PlanOf(width);
            double line = 0;
            line = Math.Max(line, Measure(SourceLinePart.Letter, plan.Letter));
            line = Math.Max(line, Measure(SourceLinePart.Device, plan.Device));
            line = Math.Max(line, Measure(SourceLinePart.Mode, plan.Mode));
            line = Math.Max(line, Measure(SourceLinePart.Remove, RemoveWidth));
            double height = 0;
            double sourceHeight = Measure(SourceLinePart.Source, plan.SourceWidth);
            if (plan.SourceOnLine) line = Math.Max(line, sourceHeight);
            if (plan.SwitchesOnLine)
            {
                line = Math.Max(line, Measure(SourceLinePart.Invert, InvertWidth));
                line = Math.Max(line, Measure(SourceLinePart.Half, HalfWidth));
                line = Math.Max(line, Measure(SourceLinePart.Either, EitherWidth));
            }
            if (_parts.Count > 0) height += Math.Max(line, LineMin);
            if (!plan.SourceOnLine && Has(SourceLinePart.Source)) height += Math.Max(sourceHeight, LineMin);
            if (!plan.SwitchesOnLine)
            {
                double tallest = 0;
                foreach (var part in Switches)
                {
                    if (!_parts.TryGetValue(part, out var child)) continue;
                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    tallest = Math.Max(tallest, child.DesiredSize.Height);
                }
                if (tallest > 0) height += Math.Max(tallest, 40);
            }
            double underWidth = Math.Max(0, width - plan.UnderStart);
            foreach (var under in _unders)
            {
                under.Measure(new Size(underWidth, double.PositiveInfinity));
                height += under.DesiredSize.Height;
            }
            return new Size(width, height);
        }

        private void Place(SourceLinePart part, double x, double y, double width, double height)
        {
            if (_parts.TryGetValue(part, out var child))
                child.Arrange(new Rect(x, y, Math.Max(0, width), height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            Sort();
            var plan = PlanOf(finalSize.Width);
            double line = LineMin;
            foreach (var pair in _parts)
            {
                bool onLine = pair.Key switch
                {
                    SourceLinePart.Source => plan.SourceOnLine,
                    SourceLinePart.Invert or SourceLinePart.Half or SourceLinePart.Either => plan.SwitchesOnLine,
                    _ => true,
                };
                if (onLine) line = Math.Max(line, pair.Value.DesiredSize.Height);
            }
            double y = 0;
            if (_parts.Count > 0)
            {
                Place(SourceLinePart.Letter, 0, 0, plan.Letter, line);
                Place(SourceLinePart.Device, plan.Letter, 0, plan.Device, line);
                Place(SourceLinePart.Mode, plan.UnderStart, 0, plan.Mode, line);
                if (plan.SourceOnLine) Place(SourceLinePart.Source, plan.SourceStart, 0, plan.SourceWidth, line);
                if (plan.SwitchesOnLine)
                {
                    Place(SourceLinePart.Invert, plan.InvertStart, 0, InvertWidth, line);
                    Place(SourceLinePart.Half, plan.HalfStart, 0, HalfWidth, line);
                    Place(SourceLinePart.Either, plan.EitherStart, 0, EitherWidth, line);
                }
                Place(SourceLinePart.Remove, plan.RemoveStart, 0, Math.Max(0, finalSize.Width - plan.RemoveStart), line);
                y = line;
            }
            if (!plan.SourceOnLine && _parts.TryGetValue(SourceLinePart.Source, out var source))
            {
                double h = Math.Max(source.DesiredSize.Height, LineMin);
                source.Arrange(new Rect(plan.SourceStart, y, plan.SourceWidth, h));
                y += h;
            }
            if (!plan.SwitchesOnLine)
            {
                double tallest = 0;
                foreach (var part in Switches)
                    if (_parts.TryGetValue(part, out var child)) tallest = Math.Max(tallest, child.DesiredSize.Height);
                if (tallest > 0)
                {
                    double h = Math.Max(tallest, 40), x = plan.UnderStart;
                    foreach (var part in Switches)
                    {
                        if (!_parts.TryGetValue(part, out var child)) continue;
                        child.Arrange(new Rect(x, y, child.DesiredSize.Width, h));
                        x += child.DesiredSize.Width + UnderGap;
                    }
                    y += h;
                }
            }
            double underWidth = Math.Max(0, finalSize.Width - plan.UnderStart);
            foreach (var under in _unders)
            {
                under.Arrange(new Rect(plan.UnderStart, y, underWidth, under.DesiredSize.Height));
                y += under.DesiredSize.Height;
            }
            return finalSize;
        }
    }

    /// <summary>
    /// The box an open row's sources stand in. It tells the lines inside
    /// whether it is narrow (<see cref="SourceLinePanel.NarrowProperty"/>),
    /// from its own width: once its size is known, never while it is being
    /// measured, so the lines are laid out again with the answer.
    /// </summary>
    public sealed class SourceBox : Border
    {
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (!sizeInfo.WidthChanged) return;
            double inner = sizeInfo.NewSize.Width - Padding.Left - Padding.Right
                - BorderThickness.Left - BorderThickness.Right;
            bool narrow = inner > 0 && inner < SourceLinePanel.NarrowBelow(SourceLinePanel.GetLetters(this));
            if (SourceLinePanel.GetNarrow(this) != narrow) SourceLinePanel.SetNarrow(this, narrow);
        }
    }
}
