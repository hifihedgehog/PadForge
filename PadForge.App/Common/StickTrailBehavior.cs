using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadForge.ViewModels;

namespace PadForge.Common
{
    /// <summary>Ember artifact stick trail (#175): a smooth connected dotted
    /// line tracing the recent stick path, cold for the raw position and
    /// ember for the forged output. Dots are emitted by arc-length
    /// resampling: a new dot appears exactly every SPACING units of travel,
    /// interpolated along the path, so the trail is single-file and evenly
    /// spaced at any hand speed and immune to sensor jitter. Dots age out
    /// over about a second, so the trail drains when the stick rests. The
    /// 60Hz sampler only runs while the canvas is visible.</summary>
    public static class StickTrailBehavior
    {
        private const int MaxDots = 46;
        private const double PlotSize = 200;
        private const double DotSize = 3.0;
        private const double Spacing = 0.02;      // normalized units between dots (~4px)
        private const double FadeMs = 1100;       // age at which a dot fully dissolves

        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(StickTrailBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);
        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

        private sealed class Signal
        {
            public Ellipse[] Dots;
            public double[] Xs;
            public double[] Ys;
            public double[] AgeMs;
            public int Head;
            public int Count;
            public double LastX;
            public double LastY;
            public bool HasLast;
        }

        private sealed class TrailState
        {
            public DispatcherTimer Timer;
            public Signal Raw;
            public Signal Out;
        }

        private static Signal MakeSignal(Canvas canvas, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var sig = new Signal
            {
                Dots = new Ellipse[MaxDots],
                Xs = new double[MaxDots],
                Ys = new double[MaxDots],
                AgeMs = new double[MaxDots],
            };
            for (int i = 0; i < MaxDots; i++)
            {
                sig.Dots[i] = new Ellipse
                {
                    Width = DotSize,
                    Height = DotSize,
                    Fill = brush,
                    Opacity = 0,
                    IsHitTestVisible = false,
                };
                canvas.Children.Add(sig.Dots[i]);
            }
            return sig;
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Canvas canvas || !(bool)e.NewValue) return;

            // Reduced motion (#175): when the OS animation preference is off,
            // never start the 60Hz trail sampler. Trail dots stay off; the
            // live position dots are bound in XAML and keep working.
            if (!SystemParameters.ClientAreaAnimation) return;

            var state = new TrailState
            {
                Raw = MakeSignal(canvas, Color.FromRgb(0x58, 0xB6, 0xE4)),
                Out = MakeSignal(canvas, Color.FromRgb(0xFF, 0x8C, 0x3C)),
            };

            state.Timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            state.Timer.Tick += (_, _) =>
            {
                if (canvas.DataContext is not StickConfigItem stick) return;
                StepSignal(state.Raw, stick.RawPosX, stick.RawPosY);
                StepSignal(state.Out, stick.LiveX, stick.LiveY);
            };
            canvas.IsVisibleChanged += (_, args) =>
            {
                if ((bool)args.NewValue) state.Timer.Start();
                else state.Timer.Stop();
            };
            if (canvas.IsVisible) state.Timer.Start();
            canvas.Unloaded += (_, _) => state.Timer.Stop();
        }

        private static void StepSignal(Signal s, double x, double y)
        {
            if (!s.HasLast)
            {
                s.LastX = x;
                s.LastY = y;
                s.HasLast = true;
            }

            // Arc-length resampling: emit dots at exact SPACING intervals
            // along the segment from the last emitted point to the current
            // position, so the trail reads as one connected dotted line.
            double dx = x - s.LastX, dy = y - s.LastY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            int guard = 0;
            while (dist >= Spacing && guard++ < MaxDots)
            {
                double t = Spacing / dist;
                s.LastX += dx * t;
                s.LastY += dy * t;
                Emit(s, s.LastX, s.LastY);
                dx = x - s.LastX;
                dy = y - s.LastY;
                dist = Math.Sqrt(dx * dx + dy * dy);
            }

            // Age and render every dot; old dots dissolve so a resting
            // stick's trail drains away instead of lingering.
            for (int i = 0; i < s.Count; i++)
            {
                int idx = (s.Head - s.Count + i + MaxDots * 2) % MaxDots;
                s.AgeMs[idx] += 16;
                double rampAlpha = (i + 1) / (double)s.Count * 0.5;
                double ageAlpha = Math.Max(0, 1.0 - s.AgeMs[idx] / FadeMs);
                var dot = s.Dots[idx];
                dot.Opacity = rampAlpha * ageAlpha;
                Canvas.SetLeft(dot, s.Xs[idx] * PlotSize - DotSize / 2);
                Canvas.SetTop(dot, s.Ys[idx] * PlotSize - DotSize / 2);
            }
            // Drop fully dissolved dots off the tail.
            while (s.Count > 0)
            {
                int tail = (s.Head - s.Count + MaxDots) % MaxDots;
                if (s.AgeMs[tail] < FadeMs) break;
                s.Dots[tail].Opacity = 0;
                s.Count--;
            }
        }

        private static void Emit(Signal s, double x, double y)
        {
            s.Xs[s.Head] = x;
            s.Ys[s.Head] = y;
            s.AgeMs[s.Head] = 0;
            s.Head = (s.Head + 1) % MaxDots;
            if (s.Count < MaxDots) s.Count++;
        }
    }
}
