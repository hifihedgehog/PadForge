using System;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>What a part of an input's tag line does when the line is
    /// short of room.</summary>
    public enum ListTagPart
    {
        /// <summary>Keeps its own width: the level's number.</summary>
        Fixed,

        /// <summary>The input's name. It gives way last, and trims.</summary>
        Name,

        /// <summary>The level's bar. It gives way first, and goes when it
        /// would be under its least width.</summary>
        Bar,

        /// <summary>The source's mode in a word. It gives way after the
        /// bar and before the name, trims, and goes when it would be under
        /// its least width.</summary>
        Mode,
    }

    /// <summary>
    /// One input's line in a cell of the mappings list: its name on a tag,
    /// an axis's live bar, the level as a number, and the source's mode.
    ///
    /// <para>The line's parts stood at fixed widths, and a column a little
    /// narrower than a long name, the bar and a four-character number ran
    /// the number past the column's edge, where it was cut ("100" with half
    /// a percent sign). The number now always keeps its width. The bar
    /// takes what is left, up to <see cref="BarWidth"/>, and goes when it
    /// would be under <see cref="BarLeast"/>. The mode's word trims next,
    /// and goes under <see cref="ModeLeast"/>. Only then does the name
    /// trim, down to <see cref="NameLeast"/>.</para>
    /// </summary>
    public sealed class ListTagPanel : Panel
    {
        public static readonly DependencyProperty PartProperty = DependencyProperty.RegisterAttached(
            "Part", typeof(ListTagPart), typeof(ListTagPanel),
            new FrameworkPropertyMetadata(ListTagPart.Fixed, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

        public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(
            nameof(BarWidth), typeof(double), typeof(ListTagPanel),
            new FrameworkPropertyMetadata(84.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty BarLeastProperty = DependencyProperty.Register(
            nameof(BarLeast), typeof(double), typeof(ListTagPanel),
            new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ModeLeastProperty = DependencyProperty.Register(
            nameof(ModeLeast), typeof(double), typeof(ListTagPanel),
            new FrameworkPropertyMetadata(32.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty NameLeastProperty = DependencyProperty.Register(
            nameof(NameLeast), typeof(double), typeof(ListTagPanel),
            new FrameworkPropertyMetadata(56.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static ListTagPart GetPart(UIElement element) => (ListTagPart)element.GetValue(PartProperty);

        public static void SetPart(UIElement element, ListTagPart value) => element.SetValue(PartProperty, value);

        /// <summary>The bar's width where the line has room for it.</summary>
        public double BarWidth
        {
            get => (double)GetValue(BarWidthProperty);
            set => SetValue(BarWidthProperty, value);
        }

        /// <summary>The least width the bar is drawn at. With less room the
        /// bar is not drawn, and the number alone says the level.</summary>
        public double BarLeast
        {
            get => (double)GetValue(BarLeastProperty);
            set => SetValue(BarLeastProperty, value);
        }

        /// <summary>The least width the mode's word trims to. With less
        /// room it is not shown. The open row names the mode in full.</summary>
        public double ModeLeast
        {
            get => (double)GetValue(ModeLeastProperty);
            set => SetValue(ModeLeastProperty, value);
        }

        /// <summary>The least width the name's tag trims to.</summary>
        public double NameLeast
        {
            get => (double)GetValue(NameLeastProperty);
            set => SetValue(NameLeastProperty, value);
        }

        /// <summary>What a line's parts ask for: the name's width, the bar's
        /// full width and its least (0 with no bar), the mode's width and
        /// its least (0 with no mode), the name's least, and what the parts
        /// that never give way take together.</summary>
        internal readonly record struct Wants(double Name, double Bar, double BarLeast, double Mode, double ModeLeast,
            double NameLeast, double Fixed);

        /// <summary>The widths the name, the bar and the mode take in a
        /// room. With room for all, each has its own. Short of that the bar
        /// takes what the others leave, and under its least it takes
        /// nothing. Then the mode takes what the name leaves, and under its
        /// least it takes nothing. Then the name has what the fixed parts
        /// leave, and never less than its least or its own width, whichever
        /// is smaller. With no bound, each has its own.</summary>
        internal static (double Name, double Bar, double Mode) Share(double room, Wants wants)
        {
            if (double.IsInfinity(room) || double.IsNaN(room)) return (wants.Name, wants.Bar, wants.Mode);
            double left = Math.Max(0, room - wants.Fixed);
            if (left >= wants.Name + wants.Mode + wants.Bar) return (wants.Name, wants.Bar, wants.Mode);
            if (wants.Bar > 0 && left >= wants.Name + wants.Mode + wants.BarLeast)
                return (wants.Name, left - wants.Name - wants.Mode, wants.Mode);
            if (left >= wants.Name + wants.Mode) return (wants.Name, 0, wants.Mode);
            if (wants.Mode > 0 && left >= wants.Name + wants.ModeLeast) return (wants.Name, 0, left - wants.Name);
            return (Math.Min(wants.Name, Math.Max(Math.Min(wants.Name, wants.NameLeast), left)), 0, 0);
        }

        // What the parts asked for at the last measure, for the arrange.
        private Wants _wants;

        private static double MarginAcross(UIElement child)
            => child is FrameworkElement element ? element.Margin.Left + element.Margin.Right : 0;

        protected override Size MeasureOverride(Size availableSize)
        {
            var unbound = new Size(double.PositiveInfinity, double.PositiveInfinity);
            UIElement name = null, bar = null, mode = null;
            double height = 0, nameWant = 0, barMost = 0, barLeast = 0, modeWant = 0, modeLeast = 0, fixedWant = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                switch (GetPart(child))
                {
                    case ListTagPart.Name when name == null:
                        name = child;
                        child.Measure(unbound);
                        nameWant = child.DesiredSize.Width;
                        height = Math.Max(height, child.DesiredSize.Height);
                        break;
                    case ListTagPart.Bar when bar == null:
                        bar = child;
                        barMost = BarWidth + MarginAcross(child);
                        barLeast = BarLeast + MarginAcross(child);
                        break;
                    case ListTagPart.Mode when mode == null:
                        mode = child;
                        child.Measure(unbound);
                        modeWant = child.DesiredSize.Width;
                        modeLeast = Math.Min(modeWant, ModeLeast + MarginAcross(child));
                        height = Math.Max(height, child.DesiredSize.Height);
                        break;
                    default:
                        child.Measure(unbound);
                        fixedWant += child.DesiredSize.Width;
                        height = Math.Max(height, child.DesiredSize.Height);
                        break;
                }
            }

            _wants = new Wants(nameWant, barMost, barLeast, modeWant, modeLeast, NameLeast, fixedWant);
            var (nameWidth, barWidth, modeWidth) = Share(availableSize.Width, _wants);
            // A part that gets less than it asked for is measured again at
            // the width it gets, so its text trims.
            if (name != null && nameWidth < nameWant)
            {
                name.Measure(new Size(nameWidth, double.PositiveInfinity));
                height = Math.Max(height, name.DesiredSize.Height);
            }
            if (mode != null && modeWidth < modeWant)
                mode.Measure(new Size(modeWidth, double.PositiveInfinity));
            if (bar != null)
            {
                bar.Measure(new Size(barWidth, double.PositiveInfinity));
                height = Math.Max(height, bar.DesiredSize.Height);
            }
            return new Size(fixedWant + nameWidth + barWidth + modeWidth, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (nameWidth, barWidth, modeWidth) = Share(finalSize.Width, _wants);
            bool nameSeen = false, barSeen = false, modeSeen = false;
            double x = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                double width;
                var part = GetPart(child);
                if (part == ListTagPart.Name && !nameSeen)
                {
                    nameSeen = true;
                    width = nameWidth;
                }
                else if (part == ListTagPart.Bar && !barSeen)
                {
                    barSeen = true;
                    width = barWidth;
                }
                else if (part == ListTagPart.Mode && !modeSeen)
                {
                    modeSeen = true;
                    width = modeWidth;
                }
                else width = child.DesiredSize.Width;
                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }
            return finalSize;
        }
    }
}
