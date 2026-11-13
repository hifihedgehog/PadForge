using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PadForge.Controls
{
    /// <summary>The light around something that is live (#574): a soft halo
    /// drawn behind its one child and outside the child's rounded edge. The
    /// engine while it runs, a forging controller, the picked pill and an
    /// extra that is on all stand in one.
    ///
    /// The halo is a picture of its own under the child, so the child keeps
    /// its own crisp text. A bitmap effect on the child would redraw the
    /// child as a picture at every change. The light is brightest at the
    /// child's edge and gone at its reach, with a smooth fall between the
    /// two. Nothing is drawn inside the child's edge, so a child with a
    /// clear fill is not tinted, and the light takes no click.
    ///
    /// A drawing's soft shadow of blur B and opacity A, with no offset, is a
    /// halo with a reach of 1.25 B and a strength of A / 2: a blurred edge
    /// is half as dark at the edge itself as the shadow's own opacity. A
    /// drawing's hard ring of one pixel is <see cref="Rim"/>.
    ///
    /// It comes on and goes off over a fifth of a second, and with
    /// <see cref="Breathes"/> it then rises and falls slowly for as long as
    /// it is lit. Both are a change of the picture's opacity, which Windows
    /// plays without asking for the picture again. With Windows' animations
    /// off it changes at once and holds still.</summary>
    public sealed class Halo : Decorator
    {
        /// <summary>The number of straight pieces the light's fall is drawn
        /// with, from the child's edge to the reach.</summary>
        internal const int Steps = 8;

        /// <summary>How long the light takes to come on or go off.</summary>
        internal static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(200);

        /// <summary>How far a breath lets the light fall, and how long one
        /// fall takes.</summary>
        internal const double BreathLow = 0.62;
        internal static readonly TimeSpan BreathTime = TimeSpan.FromSeconds(2.4);

        public static readonly DependencyProperty IsLitProperty = DependencyProperty.Register(
            nameof(IsLit), typeof(bool), typeof(Halo),
            new FrameworkPropertyMetadata(false, (d, _) => ((Halo)d).Retarget()));

        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color), typeof(Color), typeof(Halo),
            new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0x6B, 0x2C), (d, _) => ((Halo)d).Redraw()));

        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
            nameof(Radius), typeof(double), typeof(Halo),
            new FrameworkPropertyMetadata(28.0, (d, _) => ((Halo)d).Redraw()));

        public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
            nameof(Strength), typeof(double), typeof(Halo),
            new FrameworkPropertyMetadata(0.17, (d, _) => ((Halo)d).Redraw()));

        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
            nameof(CornerRadius), typeof(double), typeof(Halo),
            new FrameworkPropertyMetadata(12.0, (d, _) => ((Halo)d).Redraw()));

        public static readonly DependencyProperty RimProperty = DependencyProperty.Register(
            nameof(Rim), typeof(double), typeof(Halo),
            new FrameworkPropertyMetadata(0.0, (d, _) => ((Halo)d).Redraw()));

        public static readonly DependencyProperty BreathesProperty = DependencyProperty.Register(
            nameof(Breathes), typeof(bool), typeof(Halo),
            new FrameworkPropertyMetadata(false, (d, _) => ((Halo)d).Retarget()));

        /// <summary>Whether the halo shows.</summary>
        public bool IsLit
        {
            get => (bool)GetValue(IsLitProperty);
            set => SetValue(IsLitProperty, value);
        }

        /// <summary>The light's color: ember unless set.</summary>
        public Color Color
        {
            get => (Color)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        /// <summary>How far past the child's edge the light reaches.</summary>
        public double Radius
        {
            get => (double)GetValue(RadiusProperty);
            set => SetValue(RadiusProperty, value);
        }

        /// <summary>The light's opacity at the child's edge, from 0 to 1.</summary>
        public double Strength
        {
            get => (double)GetValue(StrengthProperty);
            set => SetValue(StrengthProperty, value);
        }

        /// <summary>The child's corner radius, which the halo follows.</summary>
        public double CornerRadius
        {
            get => (double)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        /// <summary>The opacity of a line one pixel wide that hugs the
        /// child's edge from outside, from 0 to 1. None unless set.</summary>
        public double Rim
        {
            get => (double)GetValue(RimProperty);
            set => SetValue(RimProperty, value);
        }

        /// <summary>Whether a lit halo rises and falls slowly. Only the
        /// engine's rings do.</summary>
        public bool Breathes
        {
            get => (bool)GetValue(BreathesProperty);
            set => SetValue(BreathesProperty, value);
        }

        /// <summary>How far the halo is on, from 0 to 1: the opacity of the
        /// light's picture, which the fade and the breath animate.</summary>
        internal double Level => _light.Opacity;

        /// <summary>Whether a fade or a breath is playing.</summary>
        internal bool IsMoving => _light.HasAnimatedProperties;

        /// <summary>Whether the breath is playing.</summary>
        internal bool IsBreathing { get; private set; }

        /// <summary>Mirrors Windows' animation setting. A test sets it.</summary>
        internal static Func<bool> MotionEnabled = () => SystemParameters.ClientAreaAnimation;

        private readonly Light _light;
        private int _run;

        public Halo()
        {
            _light = new Light(this) { IsHitTestVisible = false, Opacity = 0 };
            AddVisualChild(_light);
            Loaded += (_, _) => Retarget();
            // A breath runs only while the halo is in a window, so a page
            // that is put away keeps no clock alive. The light stays where
            // IsLit says, so the page comes back as it was left.
            Unloaded += (_, _) => Hold(IsLit ? 1.0 : 0.0);
        }

        // The light is drawn first, so it lies under the child.
        protected override int VisualChildrenCount => base.VisualChildrenCount + 1;

        protected override Visual GetVisualChild(int index)
            => index == 0 ? _light : base.GetVisualChild(index - 1);

        protected override Size MeasureOverride(Size constraint)
        {
            Size size = base.MeasureOverride(constraint);
            _light.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Size size = base.ArrangeOverride(arrangeSize);
            _light.Arrange(new Rect(size));
            return size;
        }

        private void Redraw() => _light.InvalidateVisual();

        /// <summary>Puts the light at a level and stops whatever moved it.</summary>
        private void Hold(double level)
        {
            _run++;
            IsBreathing = false;
            _light.Opacity = level;
            _light.BeginAnimation(OpacityProperty, null);
        }

        /// <summary>Moves the light to where IsLit says it belongs.</summary>
        private void Retarget()
        {
            double target = IsLit ? 1.0 : 0.0;
            if (!IsLoaded || !MotionEnabled())
            {
                Hold(target);
                return;
            }
            if (!_light.HasAnimatedProperties && _light.Opacity == target)
            {
                // Already there, as when a page comes back: no fade.
                if (IsLit && Breathes) Breathe();
                return;
            }
            int run = ++_run;
            IsBreathing = false;
            var fade = new DoubleAnimation(target, FadeTime)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            fade.Completed += (_, _) =>
            {
                if (run != _run) return;
                Hold(target);
                if (IsLit && Breathes && IsLoaded && MotionEnabled()) Breathe();
            };
            _light.BeginAnimation(OpacityProperty, fade);
        }

        private void Breathe()
        {
            _run++;
            IsBreathing = true;
            _light.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, BreathLow, BreathTime)
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }

        /// <summary>How bright the light is at a point between the child's
        /// edge (0) and the reach (1), as a part of its strength: the square
        /// of the distance left to the reach. That is the fall of a blurred
        /// shadow's outer half, steep at the edge and flat at the rim.</summary>
        internal static double Fall(double t)
        {
            t = Math.Clamp(t, 0, 1);
            return (1 - t) * (1 - t);
        }

        /// <summary>The stops of a brush that runs from full light to none.
        /// <paramref name="start"/> is where along the brush the child's
        /// edge lies: 0 for a side, and for a corner the part of the
        /// brush's radius that is inside the child's own rounding.</summary>
        internal static GradientStopCollection Stops(Color color, double strength, double start)
        {
            static Color At(Color c, double opacity)
                => Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), c.R, c.G, c.B);

            var stops = new GradientStopCollection(Steps + 2);
            if (start > 0) stops.Add(new GradientStop(At(color, strength), 0));
            for (int i = 0; i <= Steps; i++)
            {
                double t = i / (double)Steps;
                stops.Add(new GradientStop(At(color, strength * Fall(t)), start + (1 - start) * t));
            }
            stops.Freeze();
            return stops;
        }

        /// <summary>The light's picture: four sides and four corners, each
        /// one brush from the child's edge outward. It is as large as the
        /// halo and draws past its own edge.</summary>
        private sealed class Light : FrameworkElement
        {
            private readonly Halo _halo;

            internal Light(Halo halo) => _halo = halo;

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight, reach = Math.Max(0, _halo.Radius), strength = _halo.Strength;
                double rim = Math.Clamp(_halo.Rim, 0, 1);
                bool glows = reach > 0 && strength > 0;
                if (w <= 0 || h <= 0 || (!glows && rim <= 0)) return;
                double corner = Math.Min(Math.Max(0, _halo.CornerRadius), Math.Min(w, h) / 2);
                Color color = _halo.Color;

                // Everything but the child's own shape.
                var edge = new Rect(0, 0, w, h);
                var all = edge;
                all.Inflate(Math.Max(reach, 2), Math.Max(reach, 2));
                var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(all), new RectangleGeometry(edge, corner, corner));
                outside.Freeze();

                // The pieces meet on these lines, which are put on whole
                // pixels so no seam shows between two of them.
                var seams = new GuidelineSet(new[] { corner, w - corner }, new[] { corner, h - corner });
                seams.Freeze();

                dc.PushClip(outside);
                dc.PushGuidelineSet(seams);

                if (rim > 0)
                {
                    var line = new SolidColorBrush(Color.FromArgb((byte)Math.Round(rim * 255), color.R, color.G, color.B));
                    line.Freeze();
                    var pen = new Pen(line, 1);
                    pen.Freeze();
                    dc.DrawRoundedRectangle(null, pen, new Rect(-0.5, -0.5, w + 1, h + 1), corner + 0.5, corner + 0.5);
                }
                if (!glows)
                {
                    dc.Pop();
                    dc.Pop();
                    return;
                }

                var side = Stops(color, strength, 0);
                void Side(Rect piece, Point from, Point to)
                {
                    if (piece.Width <= 0 || piece.Height <= 0) return;
                    var brush = new LinearGradientBrush(side) { MappingMode = BrushMappingMode.Absolute, StartPoint = from, EndPoint = to };
                    brush.Freeze();
                    dc.DrawRectangle(brush, null, piece);
                }
                Side(new Rect(corner, -reach, w - 2 * corner, reach), new Point(0, 0), new Point(0, -reach));
                Side(new Rect(corner, h, w - 2 * corner, reach), new Point(0, h), new Point(0, h + reach));
                Side(new Rect(-reach, corner, reach, h - 2 * corner), new Point(0, 0), new Point(-reach, 0));
                Side(new Rect(w, corner, reach, h - 2 * corner), new Point(w, 0), new Point(w + reach, 0));

                double radius = corner + reach;
                var round = Stops(color, strength, corner / radius);
                void Corner(Rect piece, Point center)
                {
                    var brush = new RadialGradientBrush(round)
                    {
                        MappingMode = BrushMappingMode.Absolute,
                        Center = center,
                        GradientOrigin = center,
                        RadiusX = radius,
                        RadiusY = radius,
                    };
                    brush.Freeze();
                    dc.DrawRectangle(brush, null, piece);
                }
                Corner(new Rect(-reach, -reach, radius, radius), new Point(corner, corner));
                Corner(new Rect(w - corner, -reach, radius, radius), new Point(w - corner, corner));
                Corner(new Rect(-reach, h - corner, radius, radius), new Point(corner, h - corner));
                Corner(new Rect(w - corner, h - corner, radius, radius), new Point(w - corner, h - corner));

                dc.Pop();
                dc.Pop();
            }
        }
    }
}
