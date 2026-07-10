using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PadForge.Views
{
    /// <summary>
    /// Photoshop-style HSV color picker — Saturation × Value square plus
    /// a vertical Hue strip. Drives three byte dependency properties
    /// (R, G, B) for two-way binding against
    /// <see cref="ViewModels.DeviceSlotConfig"/>'s lightbar fields.
    ///
    /// <para>Cursor positions follow incoming RGB binding changes (so
    /// HEX entry / slider drags update the picker visually) and outgoing
    /// drag actions on the canvas write the corresponding RGB values
    /// back through the bindings.</para>
    /// </summary>
    public partial class ColorPickerControl : UserControl
    {
        public static readonly DependencyProperty RedProperty = DependencyProperty.Register(
            nameof(Red), typeof(byte), typeof(ColorPickerControl),
            new FrameworkPropertyMetadata((byte)0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRgbChanged));
        public static readonly DependencyProperty GreenProperty = DependencyProperty.Register(
            nameof(Green), typeof(byte), typeof(ColorPickerControl),
            new FrameworkPropertyMetadata((byte)0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRgbChanged));
        public static readonly DependencyProperty BlueProperty = DependencyProperty.Register(
            nameof(Blue), typeof(byte), typeof(ColorPickerControl),
            new FrameworkPropertyMetadata((byte)255,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRgbChanged));

        public byte Red
        {
            get => (byte)GetValue(RedProperty);
            set => SetValue(RedProperty, value);
        }
        public byte Green
        {
            get => (byte)GetValue(GreenProperty);
            set => SetValue(GreenProperty, value);
        }
        public byte Blue
        {
            get => (byte)GetValue(BlueProperty);
            set => SetValue(BlueProperty, value);
        }

        // Cached HSV state to avoid jitter from repeated RGB→HSV→RGB
        // round-trips. Hue isn't recoverable from RGB when saturation is
        // 0, so retaining it here lets the user slide value to 0 and
        // back without losing their hue selection.
        private double _hue;        // 0..360
        private double _saturation; // 0..1
        private double _value;      // 0..1

        // Set true while we're writing the RGB DPs from inside an HSV
        // mouse handler so OnRgbChanged doesn't recompute HSV (which
        // would round-trip and re-perturb the cursor).
        private bool _suppressRgbChanged;

        // Flag to track active drag state on each canvas. Mouse capture
        // would also work but separates state more clearly here.
        private bool _draggingSv;
        private bool _draggingHue;

        public ColorPickerControl()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                RecomputeHsvFromRgb();
                RefreshHueLayer();
                RefreshCursors();
            };
        }

        private static void OnRgbChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ColorPickerControl c && !c._suppressRgbChanged)
            {
                c.RecomputeHsvFromRgb();
                c.RefreshHueLayer();
                c.RefreshCursors();
            }
        }

        // ────────────────────────────────────────────────
        //  Saturation × Value canvas
        // ────────────────────────────────────────────────

        private void SvCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = true;
            SvCanvas.CaptureMouse();
            UpdateSvFromMouse(e.GetPosition(SvCanvas));
        }

        private void SvCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingSv)
                UpdateSvFromMouse(e.GetPosition(SvCanvas));
        }

        private void SvCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingSv = false;
            SvCanvas.ReleaseMouseCapture();
        }

        private void UpdateSvFromMouse(Point p)
        {
            double w = SvCanvas.ActualWidth > 0 ? SvCanvas.ActualWidth : 200;
            double h = SvCanvas.ActualHeight > 0 ? SvCanvas.ActualHeight : 200;
            _saturation = Math.Clamp(p.X / w, 0, 1);
            _value = Math.Clamp(1 - p.Y / h, 0, 1);
            WriteRgbFromHsv();
            RefreshCursors();
        }

        // ────────────────────────────────────────────────
        //  Hue strip
        // ────────────────────────────────────────────────

        private void HueCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = true;
            HueCanvas.CaptureMouse();
            UpdateHueFromMouse(e.GetPosition(HueCanvas));
        }

        private void HueCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_draggingHue)
                UpdateHueFromMouse(e.GetPosition(HueCanvas));
        }

        private void HueCanvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = false;
            HueCanvas.ReleaseMouseCapture();
        }

        private void UpdateHueFromMouse(Point p)
        {
            double h = HueCanvas.ActualHeight > 0 ? HueCanvas.ActualHeight : 200;
            _hue = Math.Clamp(p.Y / h, 0, 1) * 360;
            WriteRgbFromHsv();
            RefreshHueLayer();
            RefreshCursors();
        }

        // ────────────────────────────────────────────────
        //  HSV ↔ RGB
        // ────────────────────────────────────────────────

        private void WriteRgbFromHsv()
        {
            HsvToRgb(_hue, _saturation, _value, out byte r, out byte g, out byte b);
            _suppressRgbChanged = true;
            try
            {
                Red = r;
                Green = g;
                Blue = b;
            }
            finally
            {
                _suppressRgbChanged = false;
            }
        }

        private void RecomputeHsvFromRgb()
        {
            RgbToHsv(Red, Green, Blue, out double h, out double s, out double v);
            // Preserve hue when saturation collapses to 0 — the math
            // returns hue=0 but the user-set hue should stick so the
            // strip cursor doesn't snap to red on dark colors.
            if (s > 0) _hue = h;
            _saturation = s;
            _value = v;
        }

        // ────────────────────────────────────────────────
        //  Visual refresh
        // ────────────────────────────────────────────────

        /// <summary>Paints the Saturation×Value square's underlying solid
        /// hue layer to the current hue. The white-overlay and black-
        /// overlay layers (defined in XAML) tint it into the gradient.</summary>
        private void RefreshHueLayer()
        {
            if (SvHueLayer == null) return;
            HsvToRgb(_hue, 1, 1, out byte r, out byte g, out byte b);
            SvHueLayer.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        /// <summary>Repositions the SV cursor and Hue cursor to match
        /// the cached HSV state.</summary>
        private void RefreshCursors()
        {
            if (SvCanvas == null || SvCursor == null || HueCursor == null) return;
            double w = SvCanvas.ActualWidth > 0 ? SvCanvas.ActualWidth : 200;
            double h = SvCanvas.ActualHeight > 0 ? SvCanvas.ActualHeight : 200;
            double cx = _saturation * w - SvCursor.Width / 2;
            double cy = (1 - _value) * h - SvCursor.Height / 2;
            Canvas.SetLeft(SvCursor, cx);
            Canvas.SetTop(SvCursor, cy);

            double hh = HueCanvas.ActualHeight > 0 ? HueCanvas.ActualHeight : 200;
            double hy = (_hue / 360.0) * hh - HueCursor.Height / 2;
            Canvas.SetTop(HueCursor, hy);
        }

        // ────────────────────────────────────────────────
        //  Color math
        // ────────────────────────────────────────────────

        private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Clamp(s, 0, 1);
            v = Math.Clamp(v, 0, 1);

            double c = v * s;
            double hPrime = h / 60.0;
            double x = c * (1 - Math.Abs(hPrime % 2 - 1));
            double r1, g1, b1;
            if      (hPrime < 1) { r1 = c; g1 = x; b1 = 0; }
            else if (hPrime < 2) { r1 = x; g1 = c; b1 = 0; }
            else if (hPrime < 3) { r1 = 0; g1 = c; b1 = x; }
            else if (hPrime < 4) { r1 = 0; g1 = x; b1 = c; }
            else if (hPrime < 5) { r1 = x; g1 = 0; b1 = c; }
            else                 { r1 = c; g1 = 0; b1 = x; }

            double m = v - c;
            r = (byte)Math.Round((r1 + m) * 255);
            g = (byte)Math.Round((g1 + m) * 255);
            b = (byte)Math.Round((b1 + m) * 255);
        }

        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
            double max = Math.Max(rf, Math.Max(gf, bf));
            double min = Math.Min(rf, Math.Min(gf, bf));
            double delta = max - min;

            v = max;
            s = max == 0 ? 0 : delta / max;
            if (delta == 0)
            {
                h = 0;
                return;
            }
            if (max == rf)      h = 60 * (((gf - bf) / delta) % 6);
            else if (max == gf) h = 60 * (((bf - rf) / delta) + 2);
            else                h = 60 * (((rf - gf) / delta) + 4);
            if (h < 0) h += 360;
        }
    }
}
