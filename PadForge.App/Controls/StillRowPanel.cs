using System;
using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>The row of controller pictures on a profile's card (the
    /// approved "keep the frame" canvas). The pictures stand side by side
    /// at one size: the full size while they all fit the room, smaller
    /// while they still fit at the least size, and past that only as many
    /// as fit. The card writes the count of the rest beside the row. The
    /// row never wraps, so every profile's card is as tall as the next.
    ///
    /// <para>The room is what the card gives the row less
    /// <see cref="Reserve"/>, which the row leaves for what stands beside
    /// it: the profile's name and the count. The row tells its pictures
    /// how tall to be through <see cref="StillHeightProperty"/>, and tells
    /// the list it stands in how many it left out through
    /// <see cref="HiddenCountProperty"/>.</para></summary>
    public sealed class StillRowPanel : Panel
    {
        public static readonly DependencyProperty FullProperty = DependencyProperty.Register(
            nameof(Full), typeof(double), typeof(StillRowPanel),
            new FrameworkPropertyMetadata(186.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty LeastProperty = DependencyProperty.Register(
            nameof(Least), typeof(double), typeof(StillRowPanel),
            new FrameworkPropertyMetadata(96.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(StillRowPanel),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty ReserveProperty = DependencyProperty.Register(
            nameof(Reserve), typeof(double), typeof(StillRowPanel),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>A picture's height for its width: the canvas draws one
        /// 186 wide and 126 tall.</summary>
        public const double Shape = 126.0 / 186.0;

        /// <summary>How tall a picture in the row is. The row sets it on
        /// itself and its pictures take it.</summary>
        public static readonly DependencyProperty StillHeightProperty = DependencyProperty.RegisterAttached(
            "StillHeight", typeof(double), typeof(StillRowPanel),
            new FrameworkPropertyMetadata(186.0 * Shape, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>How many pictures the row left out, on the list the
        /// row stands in.</summary>
        public static readonly DependencyProperty HiddenCountProperty = DependencyProperty.RegisterAttached(
            "HiddenCount", typeof(int), typeof(StillRowPanel), new FrameworkPropertyMetadata(0));

        public static double GetStillHeight(DependencyObject element) => (double)element.GetValue(StillHeightProperty);
        public static void SetStillHeight(DependencyObject element, double value) => element.SetValue(StillHeightProperty, value);
        public static int GetHiddenCount(DependencyObject element) => (int)element.GetValue(HiddenCountProperty);
        public static void SetHiddenCount(DependencyObject element, int value) => element.SetValue(HiddenCountProperty, value);

        /// <summary>A picture's width while they all fit.</summary>
        public double Full
        {
            get => (double)GetValue(FullProperty);
            set => SetValue(FullProperty, value);
        }

        /// <summary>The least width a picture is drawn at.</summary>
        public double Least
        {
            get => (double)GetValue(LeastProperty);
            set => SetValue(LeastProperty, value);
        }

        /// <summary>The room between two pictures.</summary>
        public double Gap
        {
            get => (double)GetValue(GapProperty);
            set => SetValue(GapProperty, value);
        }

        /// <summary>The width the row leaves of what it is given.</summary>
        public double Reserve
        {
            get => (double)GetValue(ReserveProperty);
            set => SetValue(ReserveProperty, value);
        }

        /// <summary>How many of <paramref name="count"/> pictures a room
        /// shows, and how wide each is. All of them at the full width
        /// while they fit, all of them narrower down to the least width,
        /// and past that as many as fit at the least width, sharing the
        /// room. A room too narrow for one shows none. A room with no
        /// limit shows all at the full width.</summary>
        internal static (int Shown, double Width) Plan(double room, int count, double full, double least, double gap)
        {
            if (count <= 0) return (0, 0);
            if (double.IsInfinity(room) || double.IsNaN(room)) return (count, full);
            room = Math.Max(0, room);
            double each = (room - gap * (count - 1)) / count;
            if (each >= least) return (count, Math.Min(full, each));
            int shown = Math.Min(count, (int)Math.Floor((room + gap) / (least + gap)));
            if (shown <= 0) return (0, 0);
            return (shown, Math.Min(full, (room - gap * (shown - 1)) / shown));
        }

        private (int Shown, double Width) _plan;

        protected override Size MeasureOverride(Size availableSize)
        {
            int count = InternalChildren.Count;
            _plan = Plan(availableSize.Width - Reserve, count, Full, Least, Gap);
            SetStillHeight(this, Math.Round(_plan.Width * Shape));
            if (ItemsControl.GetItemsOwner(this) is { } owner && GetHiddenCount(owner) != count - _plan.Shown)
                SetHiddenCount(owner, count - _plan.Shown);

            double height = 0;
            for (int i = 0; i < count; i++)
            {
                UIElement child = InternalChildren[i];
                // A picture left out is out of the row altogether: it is
                // not drawn, and neither the pointer, the keyboard nor a
                // screen reader finds it. The row's pictures are its own to
                // show and hide, since the list makes them for it.
                var seen = i < _plan.Shown ? Visibility.Visible : Visibility.Collapsed;
                if (child.Visibility != seen) child.Visibility = seen;
                if (i >= _plan.Shown) continue;
                child.Measure(new Size(_plan.Width, double.PositiveInfinity));
                height = Math.Max(height, child.DesiredSize.Height);
            }
            double width = _plan.Shown == 0 ? 0 : _plan.Shown * _plan.Width + Gap * (_plan.Shown - 1);
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                UIElement child = InternalChildren[i];
                if (i >= _plan.Shown) continue;
                child.Arrange(new Rect(x, 0, _plan.Width, finalSize.Height));
                x += _plan.Width + Gap;
            }
            return finalSize;
        }
    }
}
