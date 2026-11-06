using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using PadForge.Services;

namespace PadForge.Views
{
    /// <summary>
    /// What the Screen Overlay shows (#560), drawn to the bounds it is given, in
    /// the overlay's window and in its dialog's preview. A picture scales up or
    /// down to touch the bounds with its proportions kept, centered, with its
    /// transparency kept and nothing drawn around it. Text is the keyboard
    /// screens' <see cref="PadForge.Common.Input.KeyboardScreens.KeyboardScreenRenderer.RenderText"/>:
    /// white on a black field, as large as fits, on as many lines as the words
    /// need. A color fills the bounds.
    /// </summary>
    public sealed class ScreenOverlayContentView : FrameworkElement
    {
        private static readonly Typeface TextFace = new(new FontFamily("Segoe UI"), FontStyles.Normal,
            FontWeights.Bold, FontStretches.Normal);

        private ScreenOverlayContent _content = ScreenOverlayContent.Nothing;

        public ScreenOverlayContentView()
        {
            IsHitTestVisible = false;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        }

        internal ScreenOverlayContent Shown => _content;

        /// <summary>Draws again only when what it shows changed: the service
        /// hands the same content over four times a second.</summary>
        internal void Show(ScreenOverlayContent content)
        {
            content ??= ScreenOverlayContent.Nothing;
            if (content == _content) return;
            _content = content;
            InvalidateVisual();
        }

        /// <summary>How large a picture's pixel draws: the size that fits the bounds
        /// with its proportions kept, times the zoom.</summary>
        internal static double PictureScale(int pictureWidth, int pictureHeight, int zoomPercent, Size bounds)
        {
            if (pictureWidth <= 0 || pictureHeight <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return 0;
            return Math.Min(bounds.Width / pictureWidth, bounds.Height / pictureHeight) * Math.Max(100, zoomPercent) / 100.0;
        }

        /// <summary>Where a picture lands in the bounds. At 100 percent it is as large
        /// as fits, centered. Above that, a direction it overflows starts at the pan,
        /// held to the picture's edges, and a direction it still fits stays
        /// centered, RenderWindow's rule for a keyboard screen.</summary>
        internal static Rect FitPicture(int pictureWidth, int pictureHeight, Size bounds, int zoomPercent = 100, int panX = 0, int panY = 0)
        {
            double scale = PictureScale(pictureWidth, pictureHeight, zoomPercent, bounds);
            double width = pictureWidth * scale, height = pictureHeight * scale;
            double x = width > bounds.Width ? -Math.Min(Math.Max(0, panX) * scale, width - bounds.Width) : (bounds.Width - width) / 2;
            double y = height > bounds.Height ? -Math.Min(Math.Max(0, panY) * scale, height - bounds.Height) : (bounds.Height - height) / 2;
            return new Rect(x, y, width, height);
        }

        /// <summary>How far a zoomed picture can pan, in the picture's pixels: what
        /// the window cannot hold of it, in each direction.</summary>
        internal static (int X, int Y) PanLimits(int pictureWidth, int pictureHeight, int zoomPercent, Size bounds)
        {
            double scale = PictureScale(pictureWidth, pictureHeight, zoomPercent, bounds);
            if (scale <= 0) return (0, 0);
            return ((int)Math.Ceiling(Math.Max(0, pictureWidth - bounds.Width / scale)),
                (int)Math.Ceiling(Math.Max(0, pictureHeight - bounds.Height / scale)));
        }

        /// <summary>The text at the largest size that fits inside a margin of a
        /// twentieth, RenderText's margin. RenderText counts down one size at a
        /// time on a screen of a few hundred pixels. This window can fill a
        /// display, so the size is found by halving.</summary>
        internal static (FormattedText Text, Point At) FitText(string text, Size bounds, double pixelsPerDip)
        {
            double margin = Math.Max(4, bounds.Width / 20);
            double maxWidth = Math.Max(1, bounds.Width - 2 * margin), maxHeight = Math.Max(1, bounds.Height - 2 * margin);
            FormattedText Sized(double size) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                TextFace, size, Brushes.White, pixelsPerDip)
            {
                MaxTextWidth = maxWidth,
                TextAlignment = TextAlignment.Center,
            };

            var fitted = Sized(1);
            double low = 1, high = Math.Max(1, maxHeight);
            while (high - low > 0.5)
            {
                double size = (low + high) / 2;
                var candidate = Sized(size);
                if (candidate.MinWidth <= maxWidth && candidate.Height <= maxHeight)
                {
                    fitted = candidate;
                    low = size;
                }
                else high = size;
            }
            return (fitted, new Point(margin, margin + Math.Max(0, (maxHeight - fitted.Height) / 2)));
        }

        protected override void OnRender(DrawingContext dc)
        {
            var bounds = RenderSize;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            var whole = new Rect(bounds);
            if (_content.Fill is Color fill)
                dc.DrawRectangle(new SolidColorBrush(fill), null, whole);
            else if (_content.Picture is { } picture)
            {
                // A zoomed picture runs past the bounds.
                dc.PushClip(new RectangleGeometry(whole));
                dc.DrawImage(picture, FitPicture(picture.PixelWidth, picture.PixelHeight, bounds, _content.Zoom, _content.PanX, _content.PanY));
                dc.Pop();
            }
            else if (_content.Text is { } text)
            {
                dc.DrawRectangle(Brushes.Black, null, whole);
                if (text.Trim().Length > 0)
                {
                    var (fitted, at) = FitText(text.Trim(), bounds, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(fitted, at);
                }
            }
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateVisual();

        /// <summary>The dialog's preview reads as its name, as the Image beside it
        /// does on the keyboard screens' dialog.</summary>
        protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    }
}
