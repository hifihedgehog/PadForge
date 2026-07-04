using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Views.Controls
{
    /// <summary>
    /// Visualizes one trigger's adaptive-trigger effect profile across
    /// the full pull range (left = released, right = fully pressed).
    /// The shape mirrors what the firmware will actually do given the
    /// current Mode + Start/End/Strength/Frequency values:
    ///
    ///   Off                     — empty track
    ///   Feedback                — solid rectangle from start onward
    ///   Weapon                  — solid rectangle inside [start, end]
    ///   Vibration               — sine wave from start onward
    ///   MultiPosFeedback        — alternating bumps across [start, end]
    ///   Slope                   — triangular ramp from start to end,
    ///                             held at peak past end
    ///   MultiPosVibration       — alternating sine bursts across
    ///                             [start, end]
    ///
    /// Strength scales the vertical fill height; Frequency controls
    /// the cycle count of the sine wave overlays.
    /// </summary>
    public partial class TriggerEffectGraph : UserControl
    {
        // Ember gradient for effect fills (#175 iteration 53): the flat
        // accent slab read washed. Horizontal deep-ember -> ember -> hot,
        // 0.85 opacity, frozen (never animated, safe to share).
        private static readonly LinearGradientBrush EmberFillBrush = CreateEmberFillBrush();

        private static LinearGradientBrush CreateEmberFillBrush()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                Opacity = 0.85
            };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xC4, 0x3D, 0x0C), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x6B, 0x2C), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xA2, 0x4D), 1.0));
            brush.Freeze();
            return brush;
        }

        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.Register(nameof(Mode), typeof(AdaptiveTriggerMode), typeof(TriggerEffectGraph),
                new PropertyMetadata(AdaptiveTriggerMode.Off, OnAnyChanged));

        public static readonly DependencyProperty StartPositionProperty =
            DependencyProperty.Register(nameof(StartPosition), typeof(byte), typeof(TriggerEffectGraph),
                new PropertyMetadata((byte)0, OnAnyChanged));

        public static readonly DependencyProperty EndPositionProperty =
            DependencyProperty.Register(nameof(EndPosition), typeof(byte), typeof(TriggerEffectGraph),
                new PropertyMetadata((byte)255, OnAnyChanged));

        public static readonly DependencyProperty StrengthProperty =
            DependencyProperty.Register(nameof(Strength), typeof(byte), typeof(TriggerEffectGraph),
                new PropertyMetadata((byte)0, OnAnyChanged));

        public static readonly DependencyProperty FrequencyProperty =
            DependencyProperty.Register(nameof(Frequency), typeof(byte), typeof(TriggerEffectGraph),
                new PropertyMetadata((byte)0, OnAnyChanged));

        public AdaptiveTriggerMode Mode
        {
            get => (AdaptiveTriggerMode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        public byte StartPosition
        {
            get => (byte)GetValue(StartPositionProperty);
            set => SetValue(StartPositionProperty, value);
        }

        public byte EndPosition
        {
            get => (byte)GetValue(EndPositionProperty);
            set => SetValue(EndPositionProperty, value);
        }

        public byte Strength
        {
            get => (byte)GetValue(StrengthProperty);
            set => SetValue(StrengthProperty, value);
        }

        public byte Frequency
        {
            get => (byte)GetValue(FrequencyProperty);
            set => SetValue(FrequencyProperty, value);
        }

        public TriggerEffectGraph()
        {
            InitializeComponent();
            Loaded += (_, _) => Redraw();
            SizeChanged += (_, _) => Redraw();
        }

        private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((TriggerEffectGraph)d).Redraw();

        private void Redraw()
        {
            GraphCanvas.Children.Clear();
            ModeLabel.Text = ModeDisplayName(Mode);

            double w = GraphCanvas.ActualWidth;
            double h = GraphCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Reserve a few pixels at the bottom for the mode label band.
            double labelBand = 14;
            double plotH = Math.Max(1, h - labelBand);

            // Baseline track (the trigger throw).
            var track = new Rectangle
            {
                Width = w,
                Height = 2,
                RadiusX = 1, RadiusY = 1
            };
            track.SetResourceReference(Shape.FillProperty, "ControlStrokeColorSecondaryBrush");
            Canvas.SetLeft(track, 0);
            Canvas.SetTop(track, plotH - 1);
            GraphCanvas.Children.Add(track);

            // Vertical zone ticks at the 10 firmware zone boundaries —
            // gives the user a sense of granularity (each zone is one
            // bump on the multi-position presets).
            for (int i = 1; i < 10; i++)
            {
                double x = w * i / 10.0;
                var tick = new Line
                {
                    X1 = x, Y1 = plotH - 4,
                    X2 = x, Y2 = plotH,
                    StrokeThickness = 0.5,
                    Opacity = 0.4
                };
                tick.SetResourceReference(Shape.StrokeProperty, "ControlStrokeColorSecondaryBrush");
                GraphCanvas.Children.Add(tick);
            }

            if (Mode == AdaptiveTriggerMode.Off || Strength == 0)
                return;

            // Vibration modes also encode as Off when frequency is 0
            // (firmware autoTrigger with freq byte 0 produces no buzz).
            // Match that in the preview so the user doesn't see an
            // animated wave for an inert trigger.
            if ((Mode == AdaptiveTriggerMode.Vibration ||
                 Mode == AdaptiveTriggerMode.MultiplePositionVibration)
                && Frequency == 0)
                return;

            // Map byte position [0, 255] → x in [0, w]. Strength byte
            // [0, 255] → height fraction of plotH.
            double startX = StartPosition / 255.0 * w;
            double endX = EndPosition / 255.0 * w;
            if (endX < startX) (startX, endX) = (endX, startX);
            double strH = Math.Max(2, Strength / 255.0 * (plotH - 6));

            switch (Mode)
            {
                case AdaptiveTriggerMode.Feedback:
                    DrawFilledRect(startX, w, plotH, strH);
                    break;

                case AdaptiveTriggerMode.Weapon:
                    // Soft zone start..end with a sharp peak at the end
                    // (the click point) — no resistance past the click.
                    DrawFilledRect(startX, endX, plotH, strH);
                    DrawClickMarker(endX, plotH, strH);
                    break;

                case AdaptiveTriggerMode.Vibration:
                    DrawVibrationWave(startX, w, plotH, strH);
                    break;

                case AdaptiveTriggerMode.MultiplePositionFeedback:
                    DrawAlternatingBumps(startX, endX, plotH, strH);
                    break;

                case AdaptiveTriggerMode.SlopeFeedback:
                    DrawSlopeRamp(startX, endX, w, plotH, strH);
                    break;

                case AdaptiveTriggerMode.MultiplePositionVibration:
                    DrawPulsingVibration(startX, endX, plotH, strH);
                    break;
            }
        }

        // ────────────────────────────────────────────────
        //  Shape helpers
        // ────────────────────────────────────────────────

        private void DrawFilledRect(double x0, double x1, double plotH, double strH)
        {
            if (x1 <= x0) return;
            var rect = new Rectangle
            {
                Width = x1 - x0,
                Height = strH,
                RadiusX = 2, RadiusY = 2
            };
            rect.Fill = EmberFillBrush;
            Canvas.SetLeft(rect, x0);
            Canvas.SetTop(rect, plotH - strH);
            GraphCanvas.Children.Add(rect);
        }

        private void DrawClickMarker(double x, double plotH, double strH)
        {
            // Vertical accent line at the click point — taller than the
            // soft-zone fill so the user can read "this is where it
            // breaks."
            double topY = plotH - strH - 4;
            var line = new Line
            {
                X1 = x, Y1 = topY,
                X2 = x, Y2 = plotH,
                StrokeThickness = 2
            };
            line.SetResourceReference(Shape.StrokeProperty, "AccentFillColorSecondaryBrush");
            GraphCanvas.Children.Add(line);
        }

        private void DrawAlternatingBumps(double x0, double x1, double plotH, double strH)
        {
            if (x1 <= x0) return;
            // 10 zones across the full track, but only zones inside
            // [start, end] participate, and only every other one is
            // active (matching the synth's alternating encoding).
            double w = GraphCanvas.ActualWidth;
            int startZone = ClampZone(StartPosition);
            int endZone = ClampZone(EndPosition);
            if (endZone < startZone) (startZone, endZone) = (endZone, startZone);

            for (int i = startZone; i <= endZone; i++)
            {
                if (((i - startZone) & 1) != 0) continue;
                double zx0 = w * i / 10.0;
                double zx1 = w * (i + 1) / 10.0;
                DrawFilledRect(zx0 + 1, zx1 - 1, plotH, strH);
            }
        }

        private void DrawSlopeRamp(double x0, double x1, double w, double plotH, double strH)
        {
            if (x1 <= x0) x1 = Math.Min(w, x0 + 1);
            // Ramp from baseline at startX to peak at endX, hold at peak
            // from endX to right edge.
            var poly = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(x0, plotH),
                    new Point(x1, plotH - strH),
                    new Point(w, plotH - strH),
                    new Point(w, plotH)
                }
            };
            poly.Fill = EmberFillBrush;
            GraphCanvas.Children.Add(poly);
        }

        private void DrawVibrationWave(double x0, double x1, double plotH, double strH)
        {
            // Continuous sine wave — fixed cycle count (frequency byte
            // controls firmware buzz rate, not the visual cycle count;
            // we still scale a little with frequency so the user can
            // see freq changes register).
            int cycles = Math.Max(4, Math.Min(20, 4 + Frequency / 4));
            DrawWave(x0, x1, plotH, strH, cycles, dashed: false);
        }

        private void DrawPulsingVibration(double x0, double x1, double plotH, double strH)
        {
            // Multi-position vibration: alternating zones across
            // [start, end] each containing a short sine burst. Visually
            // distinct from continuous Vibration.
            double w = GraphCanvas.ActualWidth;
            int startZone = ClampZone(StartPosition);
            int endZone = ClampZone(EndPosition);
            if (endZone < startZone) (startZone, endZone) = (endZone, startZone);

            for (int i = startZone; i <= endZone; i++)
            {
                if (((i - startZone) & 1) != 0) continue;
                double zx0 = w * i / 10.0;
                double zx1 = w * (i + 1) / 10.0;
                int zoneCycles = Math.Max(2, Math.Min(6, 2 + Frequency / 8));
                DrawWave(zx0 + 1, zx1 - 1, plotH, strH, zoneCycles, dashed: false);
            }
        }

        private void DrawWave(double x0, double x1, double plotH, double strH, int cycles, bool dashed)
        {
            if (x1 <= x0) return;
            const int samplesPerCycle = 12;
            int n = Math.Max(2, cycles * samplesPerCycle);
            var pts = new PointCollection();
            double midY = plotH - strH / 2.0;
            double amp = strH / 2.0;
            for (int s = 0; s <= n; s++)
            {
                double t = (double)s / n;
                double x = x0 + t * (x1 - x0);
                double y = midY - amp * Math.Sin(t * cycles * 2 * Math.PI);
                pts.Add(new Point(x, y));
            }
            var poly = new Polyline
            {
                Points = pts,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };
            if (dashed)
                poly.StrokeDashArray = new DoubleCollection { 3, 2 };
            poly.SetResourceReference(Shape.StrokeProperty, "AccentFillColorSecondaryBrush");
            GraphCanvas.Children.Add(poly);
        }

        private static int ClampZone(byte position) => Math.Clamp(position * 10 / 256, 0, 9);

        private static string ModeDisplayName(AdaptiveTriggerMode mode)
        {
            var s = Strings.Instance;
            return mode switch
            {
                AdaptiveTriggerMode.Off => s.Pad_AT_Off,
                AdaptiveTriggerMode.Feedback => s.Pad_AT_Feedback,
                AdaptiveTriggerMode.Weapon => s.Pad_AT_Weapon,
                AdaptiveTriggerMode.Vibration => s.Pad_AT_Vibration,
                AdaptiveTriggerMode.MultiplePositionFeedback => s.Pad_AT_MultiPosFeedback,
                AdaptiveTriggerMode.SlopeFeedback => s.Pad_AT_Slope,
                AdaptiveTriggerMode.MultiplePositionVibration => s.Pad_AT_MultiPosVibration,
                _ => mode.ToString()
            };
        }
    }
}
