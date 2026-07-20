using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadForge.ViewModels;

namespace PadForge.Common
{
    /// <summary>Ember artifact peak-hold (#175 competitor item 3): a short
    /// ember tick on the radar rim at the angle of the largest OUT
    /// deflection recorded, plus a mono percent readout written into the
    /// TextBlock handed over via the Readout attached property (the static
    /// "PEAK" caption stays in XAML; this writes only the "n%" value).
    /// A new peak, or a stick holding the peak, relights the tick; once
    /// the stick backs off, the tick decays over ~3s and the peak then
    /// re-arms at the current deflection. The decay is gated on
    /// SystemParameters.ClientAreaAnimation: with OS animations off the
    /// tick holds the last peak statically (peak tracking and readout stay
    /// live either way). The peak resets when the canvas DataContext swaps
    /// to another StickConfigItem, i.e. when the stick config changes.
    /// Sibling of StickTrailBehavior, sharing its plot conventions
    /// (200x200 plot, normalized 0..1 positions, y-down) and its 60Hz
    /// visible-only sampler lifecycle.</summary>
    public static class StickPeakHoldBehavior
    {
        private const double Center = 100;    // plot center, px
        private const double TickInner = 92;  // tick foot radius, px
        private const double TickOuter = 99;  // tick head, just inside the rim ring
        private const double DecayMs = 3000;  // fade span once the peak goes stale
        private const double MinPeak = 0.02;  // ignore centered-stick noise

        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(StickPeakHoldBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);
        public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

        /// <summary>The value TextBlock the behavior writes "n%" into.</summary>
        public static readonly DependencyProperty ReadoutProperty =
            DependencyProperty.RegisterAttached("Readout", typeof(TextBlock), typeof(StickPeakHoldBehavior),
                new PropertyMetadata(null));

        public static void SetReadout(DependencyObject obj, TextBlock value) => obj.SetValue(ReadoutProperty, value);
        public static TextBlock GetReadout(DependencyObject obj) => (TextBlock)obj.GetValue(ReadoutProperty);

        private sealed class PeakState
        {
            public DispatcherTimer Timer;
            public Line Tick;
            public double PeakMag;   // normalized 0..1 OUT deflection
            public double AgeMs;
            public bool HasPeak;
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Canvas canvas || !(bool)e.NewValue) return;

            var ember = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x3C)); // trail OUT ember
            ember.Freeze();
            var state = new PeakState
            {
                Tick = new Line
                {
                    Stroke = ember,
                    StrokeThickness = 2,
                    Opacity = 0,
                    IsHitTestVisible = false,
                },
            };
            canvas.Children.Add(state.Tick);

            // Reduced motion gates only the decay: without OS animations the
            // tick stays a static last-peak marker instead of fading.
            bool animate = SystemParameters.ClientAreaAnimation;

            state.Timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            state.Timer.Tick += (_, _) => Step(canvas, state, animate);
            canvas.IsVisibleChanged += (_, args) =>
            {
                if ((bool)args.NewValue) state.Timer.Start();
                else state.Timer.Stop();
            };
            if (canvas.IsVisible) state.Timer.Start();
            canvas.Unloaded += (_, _) => state.Timer.Stop();
            // Stick config change = template rebind: forget the old stick's peak.
            canvas.DataContextChanged += (_, _) => Reset(canvas, state);
        }

        private static void Step(Canvas canvas, PeakState s, bool animate)
        {
            if (canvas.DataContext is not StickConfigItem stick) return;

            // OUT deflection in plot space (LiveX/LiveY are 0..1, center 0.5).
            double dx = stick.LiveX - 0.5, dy = stick.LiveY - 0.5;
            double mag = Math.Min(1.0, 2.0 * Math.Sqrt(dx * dx + dy * dy));

            if (mag >= s.PeakMag && mag > MinPeak)
            {
                // New peak, or the stick is holding the peak: (re)light the tick.
                s.PeakMag = mag;
                s.AgeMs = 0;
                s.HasPeak = true;
                double angle = Math.Atan2(dy, dx); // y-down frame matches the plot
                s.Tick.X1 = Center + TickInner * Math.Cos(angle);
                s.Tick.Y1 = Center + TickInner * Math.Sin(angle);
                s.Tick.X2 = Center + TickOuter * Math.Cos(angle);
                s.Tick.Y2 = Center + TickOuter * Math.Sin(angle);
                s.Tick.Opacity = 1;
                WriteReadout(canvas, s.PeakMag);
                return;
            }

            if (!s.HasPeak || !animate) return; // static last-peak without animations

            s.AgeMs += 16;
            double alpha = 1.0 - s.AgeMs / DecayMs;
            if (alpha > 0)
            {
                s.Tick.Opacity = alpha;
                return;
            }

            // Fully decayed: re-arm at the current deflection so the next
            // sample registers a fresh (possibly lower) peak.
            s.HasPeak = false;
            s.PeakMag = 0;
            s.Tick.Opacity = 0;
            WriteReadout(canvas, mag);
        }

        private static void Reset(Canvas canvas, PeakState s)
        {
            s.HasPeak = false;
            s.PeakMag = 0;
            s.AgeMs = 0;
            s.Tick.Opacity = 0;
            WriteReadout(canvas, 0);
        }

        // 0..100 percent strings, built once: the readout re-formatted
        // the same value at 60 Hz while a stick held its peak.
        private static readonly string[] s_pctStrings = BuildPctStrings();
        private static string[] BuildPctStrings()
        {
            var arr = new string[101];
            for (int i = 0; i <= 100; i++)
                arr[i] = i.ToString(CultureInfo.InvariantCulture) + "%";
            return arr;
        }

        private static void WriteReadout(Canvas canvas, double mag)
        {
            if (GetReadout(canvas) is TextBlock tb)
            {
                int pct = Math.Clamp((int)Math.Round(mag * 100), 0, 100);
                tb.Text = s_pctStrings[pct];
            }
        }
    }
}
