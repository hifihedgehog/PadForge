using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Common.Crosshairs;
using PadForge.Services;

namespace PadForge.Controls
{
    /// <summary>
    /// Draws one crosshair (#585) at its size in the display's pixels on a
    /// display of any scale, so a 2-pixel arm is 2 pixels at 150% as at
    /// 100%. The crosshair's window puts its center on
    /// <see cref="Origin"/>. The card's preview (<see cref="Fit"/>) centers it
    /// and shrinks one too large for the card until all of it shows.
    ///
    /// <para>The outline is drawn first and the fill over it, as
    /// Crosshair-Overlay draws its larger outline quads before the crosshair
    /// (Drawing.cpp, STYLE_DEFAULT_OUTLINED falling through to
    /// STYLE_DEFAULT). The opacity applies to the crosshair as one piece, as
    /// the CS:GO crosshair generator sets one opacity on the crosshair's
    /// group (javascript.js updateCrosshair, group.setOpacity). Smooth Edges
    /// off draws every edge on whole pixels and scales a picture by its
    /// nearest pixels.</para>
    /// </summary>
    public sealed class CrosshairView : FrameworkElement
    {
        public static readonly DependencyProperty LookProperty = DependencyProperty.Register(
            nameof(Look), typeof(CrosshairData), typeof(CrosshairView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((CrosshairView)d).Build()));

        public static readonly DependencyProperty FitProperty = DependencyProperty.Register(
            nameof(Fit), typeof(bool), typeof(CrosshairView),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The crosshair as its settings draw it. A new copy for each
        /// change, so the view builds the shape again.</summary>
        public CrosshairData Look
        {
            get => (CrosshairData)GetValue(LookProperty);
            set => SetValue(LookProperty, value);
        }

        /// <summary>Centered in the view and shrunk to fit, for the card's
        /// preview. Off, the center is <see cref="Origin"/>.</summary>
        public bool Fit
        {
            get => (bool)GetValue(FitProperty);
            set => SetValue(FitProperty, value);
        }

        private Point _origin;

        /// <summary>Where the crosshair's center goes, in pixels from the
        /// view's corner.</summary>
        internal Point Origin
        {
            get => _origin;
            set
            {
                if (_origin == value) return;
                _origin = value;
                InvalidateVisual();
            }
        }

        /// <summary>The shape the look draws, or null with no look.</summary>
        internal CrosshairShape Shape { get; private set; }

        /// <summary>The picture of an Image crosshair, or null when it has none
        /// or its file cannot be read.</summary>
        internal BitmapSource Picture { get; private set; }

        private Brush _fill;
        private Pen _outline;

        /// <summary>Builds the look again, reading an Image crosshair's
        /// picture again through <see cref="CrosshairPictures"/>: a file
        /// that could not be read before, or was written again since, is
        /// drawn as it is now.</summary>
        internal void Reload()
        {
            Build();
            InvalidateVisual();
        }

        private void Build()
        {
            var look = Look;
            if (look == null)
            {
                Shape = null;
                Picture = null;
                return;
            }
            Shape = CrosshairShape.Of(look);
            Picture = look.Style == CrosshairStyle.Image ? CrosshairPictures.Load(look.ImagePath) : null;
            _fill = Frozen(new SolidColorBrush(Color.FromRgb(look.ColorR, look.ColorG, look.ColorB)));
            _outline = Shape.OutlinePenWidth > 0
                ? CrosshairShape.OutlinePen(Shape.OutlinePenWidth, Frozen(new SolidColorBrush(Color.FromRgb(look.OutlineR, look.OutlineG, look.OutlineB))))
                : null;
            RenderOptions.SetEdgeMode(this, look.SmoothEdges ? EdgeMode.Unspecified : EdgeMode.Aliased);
            RenderOptions.SetBitmapScalingMode(this, look.SmoothEdges ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        }

        private static Brush Frozen(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var look = Look;
            var shape = Shape;
            if (look == null || shape == null || (shape.IsImage && Picture == null)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            Point origin = _origin;
            double fit = 1;
            if (Fit)
            {
                double width = ActualWidth * dpi.DpiScaleX, height = ActualHeight * dpi.DpiScaleY;
                origin = new Point(Math.Round(width / 2), Math.Round(height / 2));
                fit = FitScale(shape.Bounds, width, height);
            }
            // Drawn in the display's pixels from here on.
            dc.PushTransform(new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY));
            dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
            dc.PushTransform(new ScaleTransform(fit, fit));
            dc.PushOpacity(look.Opacity);
            if (shape.IsImage)
            {
                dc.PushTransform(new RotateTransform(shape.Rotation));
                dc.DrawImage(Picture, shape.ImageRect);
                dc.Pop();
            }
            else
            {
                if (_outline != null) dc.DrawGeometry(null, _outline, shape.Body);
                dc.DrawGeometry(_fill, null, shape.Body);
            }
            dc.Pop();
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        /// <summary>The scale that fits a crosshair centered in a width and a
        /// height, both in pixels: 1 when it fits as it is, never larger.
        /// Measured from the center each way, so the center stays in the
        /// middle.</summary>
        internal static double FitScale(Rect bounds, double width, double height)
        {
            if (bounds.IsEmpty || width <= 0 || height <= 0) return 1;
            double reachX = Math.Max(-bounds.Left, bounds.Right), reachY = Math.Max(-bounds.Top, bounds.Bottom);
            double fit = 1;
            if (reachX > 0) fit = Math.Min(fit, width / 2 / reachX);
            if (reachY > 0) fit = Math.Min(fit, height / 2 / reachY);
            return fit;
        }
    }
}
