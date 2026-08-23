using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PadForge.Common.Input;
using PadForge.ViewModels;

namespace PadForge.Controls
{
    /// <summary>The graphic EQ (#347): a log-frequency response curve with one
    /// draggable handle per band.
    ///
    /// <para>Drawn in <see cref="OnRender"/> rather than built from a visual
    /// tree of shapes. The curve is a few hundred points and the handles move
    /// continuously during a drag, so churning Ellipse and Path children would
    /// cost far more than drawing does, and this keeps the whole control one
    /// file with no template to keep in sync.</para>
    ///
    /// <para>Drag moves frequency and gain together, which is the gesture
    /// people expect from an EQ. The mouse wheel over a handle changes Q,
    /// because width is the third dimension and there is nowhere else to put
    /// it without a second gesture. Both write straight through to the band,
    /// which re-encodes into the device config, so the curve edits the saved
    /// setting rather than a copy of it.</para></summary>
    public class EqCurveControl : FrameworkElement
    {
        private const double MinHz = 20.0;
        private const double MaxHz = 20000.0;
        private const double MaxDb = 24.0;
        // The chain's own rate, so the drawn curve is computed at the rate the
        // filters are compiled at. A local copy is how the editor's clamp
        // drifted away from the engine's.
        private const int Rate = EqBand.MirrorSampleRate;
        private const double HandleR = 6.0;

        public static readonly DependencyProperty BandsProperty =
            DependencyProperty.Register(nameof(Bands), typeof(ObservableCollection<EqBandVm>),
                typeof(EqCurveControl),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender, OnBandsChanged));

        public ObservableCollection<EqBandVm> Bands
        {
            get => (ObservableCollection<EqBandVm>)GetValue(BandsProperty);
            set => SetValue(BandsProperty, value);
        }

        // Frozen once. OnRender runs on every mouse-move during a drag, and
        // the Atom x5-Z8350 perf floor is the reason these are not rebuilt
        // there: ten brushes and pens a frame is pure garbage for a drawing
        // that never changes colour.
        private static readonly Brush Bg = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)));
        private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xC8, 0xC8, 0xC8))), 1));
        private static readonly Pen ZeroPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xC8, 0xC8, 0xC8))), 1));
        private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xC8, 0xC8, 0xC8)));
        private static readonly Pen CurvePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x65, 0x2A))), 2));
        private static readonly Brush HandleFill = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x65, 0x2A)));
        private static readonly Brush HandleHot = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x9D, 0x5D)));
        private static readonly Pen HandleRing = Frozen(new Pen(Frozen(new SolidColorBrush(Colors.White)), 1.5));
        private static readonly Typeface Face = new("Segoe UI");
        private static readonly double[] GridHz = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };

        private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        // Reused across renders for the same reason: one list per frame of a
        // drag is one list too many.
        private readonly List<EqBand> _scratch = new();

        private EqBandVm _drag;
        private EqBandVm _hover;

        public EqCurveControl()
        {
            Focusable = true;
            MinHeight = 190;
            ClipToBounds = true;
        }

        private static void OnBandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (EqCurveControl)d;
            if (e.OldValue is ObservableCollection<EqBandVm> oldC)
            {
                oldC.CollectionChanged -= c.OnCollectionChanged;
                foreach (var b in oldC) b.PropertyChanged -= c.OnBandChanged;
            }
            if (e.NewValue is ObservableCollection<EqBandVm> newC)
            {
                newC.CollectionChanged += c.OnCollectionChanged;
                foreach (var b in newC) b.PropertyChanged += c.OnBandChanged;
            }
            c.InvalidateVisual();
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (EqBandVm b in e.OldItems) b.PropertyChanged -= OnBandChanged;
            if (e.NewItems != null) foreach (EqBandVm b in e.NewItems) b.PropertyChanged += OnBandChanged;
            InvalidateVisual();
        }

        private void OnBandChanged(object sender, PropertyChangedEventArgs e) => InvalidateVisual();

        // ── coordinate mapping ──────────────────────────────────────────────

        private double XOf(double hz)
        {
            double t = (Math.Log10(Math.Clamp(hz, MinHz, MaxHz)) - Math.Log10(MinHz))
                     / (Math.Log10(MaxHz) - Math.Log10(MinHz));
            return t * ActualWidth;
        }

        private double HzOf(double x)
        {
            double t = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1);
            return Math.Pow(10, Math.Log10(MinHz) + t * (Math.Log10(MaxHz) - Math.Log10(MinHz)));
        }

        private double YOf(double db) => (0.5 - Math.Clamp(db, -MaxDb, MaxDb) / (2 * MaxDb)) * ActualHeight;

        private double DbOf(double y) => (0.5 - Math.Clamp(y / Math.Max(1, ActualHeight), 0, 1)) * 2 * MaxDb;

        // ── rendering ───────────────────────────────────────────────────────

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 1 || h <= 1) return;

            dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));

            foreach (double hz in GridHz)
            {
                double x = Math.Round(XOf(hz)) + 0.5;
                dc.DrawLine(GridPen, new Point(x, 0), new Point(x, h));
                string t = hz >= 1000 ? (hz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k"
                                      : hz.ToString("0", CultureInfo.InvariantCulture);
                dc.DrawText(Text(t, LabelBrush, 9), new Point(Math.Min(x + 3, w - 22), h - 13));
            }
            for (double db = -MaxDb; db <= MaxDb; db += 12)
            {
                double y = Math.Round(YOf(db)) + 0.5;
                dc.DrawLine(Math.Abs(db) < 0.01 ? ZeroPen : GridPen, new Point(0, y), new Point(w, y));
                if (Math.Abs(db) > 0.01)
                    dc.DrawText(Text((db > 0 ? "+" : "") + db.ToString("0", CultureInfo.InvariantCulture), LabelBrush, 9),
                                new Point(3, y + 1));
            }

            var src = Bands;
            if (src == null || src.Count == 0) return;
            var bands = _scratch;
            bands.Clear();
            foreach (var v in src) bands.Add(v.ToBandPublic());

            // The response curve, one point per device pixel.
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool started = false;
                int steps = Math.Max(64, (int)w);
                for (int i = 0; i <= steps; i++)
                {
                    double x = i * w / steps;
                    double db = EqResponse.MagnitudeDb(bands, HzOf(x), Rate);
                    var p = new Point(x, YOf(db));
                    if (!started) { g.BeginFigure(p, false, false); started = true; }
                    else g.LineTo(p, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, CurvePen, geo);

            // One handle per band, hollow when disabled.
            foreach (var vm in src)
            {
                var c = new Point(XOf(vm.FrequencyHz), YOf(GainForHandle(vm)));
                bool isHot = ReferenceEquals(vm, _hover) || ReferenceEquals(vm, _drag);
                dc.DrawEllipse(vm.Enabled ? (isHot ? HandleHot : HandleFill) : null, HandleRing, c, HandleR, HandleR);
            }
        }

        /// <summary>Where the handle sits vertically. Gain for the types that
        /// have one; the pass and notch types have no gain, so their handle
        /// rides the curve itself where the user can still see and grab it.</summary>
        private double GainForHandle(EqBandVm vm)
        {
            if (vm.Type is EqBandType.Peaking or EqBandType.LowShelf or EqBandType.HighShelf)
                return vm.GainDb;
            var one = new List<EqBand> { vm.ToBandPublic() };
            return EqResponse.MagnitudeDb(one, vm.FrequencyHz, Rate);
        }

        private static FormattedText Text(string s, Brush b, double size) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Face, size, b, 1.25);

        // ── interaction ─────────────────────────────────────────────────────

        private EqBandVm HitTest(Point p)
        {
            EqBandVm best = null;
            double bestD = HandleR * 2.5;
            if (Bands == null) return null;
            foreach (var vm in Bands)
            {
                double dx = p.X - XOf(vm.FrequencyHz), dy = p.Y - YOf(GainForHandle(vm));
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < bestD) { bestD = d; best = vm; }
            }
            return best;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            _drag = HitTest(p);
            if (_drag != null)
            {
                CaptureMouse();
                Focus();
                e.Handled = true;
            }
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (_drag != null && IsMouseCaptured)
            {
                _drag.FrequencyHz = (float)HzOf(p.X);
                if (_drag.Type is EqBandType.Peaking or EqBandType.LowShelf or EqBandType.HighShelf)
                    _drag.GainDb = (float)DbOf(p.Y);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            var h = HitTest(p);
            if (!ReferenceEquals(h, _hover))
            {
                _hover = h;
                Cursor = h != null ? Cursors.SizeAll : Cursors.Arrow;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();
            _drag = null;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hover = null;
            Cursor = Cursors.Arrow;
            InvalidateVisual();
        }

        /// <summary>Wheel over a handle changes Q, which is the band's width.
        /// Multiplicative so the feel is even across the range: Q moves in
        /// perceptual steps rather than in absolute ones that are enormous at
        /// 0.1 and invisible at 10.</summary>
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var vm = HitTest(e.GetPosition(this));
            if (vm == null) return;
            double f = e.Delta > 0 ? 1.15 : 1 / 1.15;
            vm.Q = (float)Math.Clamp(vm.Q * f, 0.05, 20.0);
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
