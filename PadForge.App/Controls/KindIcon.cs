using System;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>The icon of a kind of device. A kind with a glyph in Segoe
    /// MDL2 Assets (<see cref="Glyph"/>) draws it exactly as a GlyphText of
    /// the same size and color does. A kind no icon font draws, a wheel or a
    /// joystick, takes a line drawing instead (<see cref="Shape"/>, from
    /// Common.DeviceTypeGlyph.ShapeFor), which wins when it is set: a square
    /// of 17/18 of the font size with lines 1.5 in 17 and round ends, the way
    /// a card's head draws the steering wheel beside a glyph of 18
    /// (FoldCard in ControllerIcons.xaml). A screen reader passes over it, as
    /// it passes over a GlyphText.</summary>
    public sealed class KindIcon : Decorator
    {
        /// <summary>The drawing's side, in parts of the font size.</summary>
        internal const double ShapeSide = 17.0 / 18.0;

        /// <summary>The drawing's line, in parts of its side.</summary>
        internal const double ShapeLine = 1.5 / 17.0;

        public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
            nameof(Glyph), typeof(string), typeof(KindIcon),
            new PropertyMetadata(string.Empty, (d, e) => ((KindIcon)d)._glyph.Text = e.NewValue as string ?? string.Empty));

        public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
            nameof(Shape), typeof(Geometry), typeof(KindIcon),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                (d, e) => ((KindIcon)d)._glyph.Visibility = e.NewValue == null ? Visibility.Visible : Visibility.Collapsed));

        public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
            typeof(KindIcon), new FrameworkPropertyMetadata(SystemFonts.MessageFontSize,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure
                | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(KindIcon), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

        // The glyph, which takes this icon's size and color as a child takes
        // them from its parent.
        private readonly GlyphText _glyph = new()
        {
            FontFamily = IconFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        public KindIcon() => Child = _glyph;

        /// <summary>The kind's glyph in Segoe MDL2 Assets
        /// (Common.DeviceTypeGlyph.For). Empty for a kind with a
        /// drawing.</summary>
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        /// <summary>The kind's line drawing on a grid of 24
        /// (Common.DeviceTypeGlyph.ShapeFor), or null for a kind with a
        /// glyph.</summary>
        public Geometry Shape
        {
            get => (Geometry)GetValue(ShapeProperty);
            set => SetValue(ShapeProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            Size glyph = base.MeasureOverride(constraint);
            if (Shape == null) return glyph;
            double side = FontSize * ShapeSide;
            return new Size(side, side);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Shape is not Geometry shape || Foreground is not Brush ink) return;
            Rect bounds = shape.Bounds;
            double longest = Math.Max(bounds.Width, bounds.Height);
            if (bounds.IsEmpty || longest <= 0) return;
            // The drawing and its line fill the square, centered in the room
            // the icon was given, as a Path stretched Uniform inside it would.
            double side = FontSize * ShapeSide;
            double line = side * ShapeLine;
            double scale = (side - line) / longest;
            double x = (RenderSize.Width - bounds.Width * scale) / 2 - bounds.X * scale;
            double y = (RenderSize.Height - bounds.Height * scale) / 2 - bounds.Y * scale;
            var pen = new Pen(ink, line / scale)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
            dc.DrawGeometry(null, pen, shape);
            dc.Pop();
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new QuietPeer(this);

        private sealed class QuietPeer : FrameworkElementAutomationPeer
        {
            public QuietPeer(KindIcon owner) : base(owner) { }

            protected override bool IsControlElementCore() => false;

            protected override bool IsContentElementCore() => false;
        }
    }
}
