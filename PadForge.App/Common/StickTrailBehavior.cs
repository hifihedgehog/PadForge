using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadForge.ViewModels;

namespace PadForge.Common
{
    /// <summary>Ember artifact stick trail (#175): 46 fading dots per signal,
    /// cold for the raw stick position and ember for the forged output,
    /// drawn on a dedicated overlay canvas inside the 200x200 stick plot.
    /// Pooled ellipses repositioned on a 33ms timer; the timer only runs
    /// while the canvas is visible, so the tab pays nothing when hidden.</summary>
    public static class StickTrailBehavior
    {
        private const int TrailLength = 46;
        private const double PlotSize = 200;
        private const double DotSize = 2.8;

        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(StickTrailBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);
        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

        private sealed class TrailState
        {
            public DispatcherTimer Timer;
            public Ellipse[] RawDots;
            public Ellipse[] OutDots;
            public double[] Xs;
            public double[] Ys;
            public double[] Fxs;
            public double[] Fys;
            public int Head;
            public int Count;
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Canvas canvas || !(bool)e.NewValue) return;

            var state = new TrailState
            {
                RawDots = new Ellipse[TrailLength],
                OutDots = new Ellipse[TrailLength],
                Xs = new double[TrailLength],
                Ys = new double[TrailLength],
                Fxs = new double[TrailLength],
                Fys = new double[TrailLength],
            };

            var cold = new SolidColorBrush(Color.FromRgb(0x58, 0xB6, 0xE4));
            var ember = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x3C));
            cold.Freeze();
            ember.Freeze();
            for (int i = 0; i < TrailLength; i++)
            {
                state.RawDots[i] = new Ellipse { Width = DotSize, Height = DotSize, Fill = cold, Opacity = 0, IsHitTestVisible = false };
                state.OutDots[i] = new Ellipse { Width = DotSize, Height = DotSize, Fill = ember, Opacity = 0, IsHitTestVisible = false };
                canvas.Children.Add(state.RawDots[i]);
                canvas.Children.Add(state.OutDots[i]);
            }

            state.Timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            state.Timer.Tick += (_, _) => Step(canvas, state);
            canvas.IsVisibleChanged += (_, args) =>
            {
                if ((bool)args.NewValue) state.Timer.Start();
                else state.Timer.Stop();
            };
            if (canvas.IsVisible) state.Timer.Start();
            canvas.Unloaded += (_, _) => state.Timer.Stop();
        }

        private static void Step(Canvas canvas, TrailState s)
        {
            if (canvas.DataContext is not StickConfigItem stick) return;

            // Ring buffer of normalized positions (0..1 plot space).
            // Distance-gated (artifact look): a new dot is laid down only
            // after the stick travels a fixed arc length, so the trail reads
            // as an evenly spaced dotted line at any hand speed instead of
            // clumping when slow and gapping when fast.
            int prev = (s.Head - 1 + TrailLength) % TrailLength;
            double dx = stick.RawPosX - s.Xs[prev];
            double dy = stick.RawPosY - s.Ys[prev];
            double fdx = stick.LiveX - s.Fxs[prev];
            double fdy = stick.LiveY - s.Fys[prev];
            const double spacing = 0.018;
            if (s.Count > 0 &&
                dx * dx + dy * dy < spacing * spacing &&
                fdx * fdx + fdy * fdy < spacing * spacing)
                return;
            s.Xs[s.Head] = stick.RawPosX;
            s.Ys[s.Head] = stick.RawPosY;
            s.Fxs[s.Head] = stick.LiveX;
            s.Fys[s.Head] = stick.LiveY;
            s.Head = (s.Head + 1) % TrailLength;
            if (s.Count < TrailLength) s.Count++;

            for (int i = 0; i < TrailLength; i++)
            {
                if (i >= s.Count)
                {
                    s.RawDots[i].Opacity = 0;
                    s.OutDots[i].Opacity = 0;
                    continue;
                }
                // Oldest sample gets the faintest dot; newest approaches 0.35.
                int idx = (s.Head - s.Count + i + TrailLength * 2) % TrailLength;
                double alpha = (i + 1) / (double)s.Count * 0.45;

                var raw = s.RawDots[i];
                raw.Opacity = alpha;
                Canvas.SetLeft(raw, s.Xs[idx] * PlotSize - DotSize / 2);
                Canvas.SetTop(raw, s.Ys[idx] * PlotSize - DotSize / 2);

                var outd = s.OutDots[i];
                outd.Opacity = alpha;
                Canvas.SetLeft(outd, s.Fxs[idx] * PlotSize - DotSize / 2);
                Canvas.SetTop(outd, s.Fys[idx] * PlotSize - DotSize / 2);
            }
        }
    }
}
