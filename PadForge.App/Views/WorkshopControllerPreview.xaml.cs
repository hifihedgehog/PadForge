using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PadForge.Models2D;

namespace PadForge.Views
{
    /// <summary>Draws the real pad for a community config with each bound
    /// input CALLED OUT beside the button it belongs to.
    ///
    /// <para>The art is the repo's own Gamepad-Asset-Pack pipeline
    /// (<c>2DModels/</c> PNGs positioned by the generated
    /// <c>ControllerOverlayLayout</c>), never hand-drawn shapes. That
    /// pipeline already carries the join this needs: every
    /// <c>OverlayElement</c> knows its <c>TargetName</c> AND its pixel rect,
    /// and the translator's manifest reports the same target namespace, so
    /// a binding can be anchored to the exact button that serves it.</para>
    ///
    /// <para>Layout: the body sits in the middle, callouts go in gutters on
    /// the side each element is nearest, and a leader line runs from the
    /// label to the element edge. Labels are de-overlapped by walking them
    /// down their gutter, so a pad with a dozen bindings stays readable
    /// instead of stacking text on itself.</para></summary>
    public partial class WorkshopControllerPreview : UserControl
    {
        // Art-space units. The whole canvas is scaled by the Viewbox, so
        // these are chosen against the pack's ~1543x956 bodies.
        private const double Gutter = 430;
        private const double LabelFont = 30;
        private const double RowPitch = 52;

        private static readonly Brush Ember = Frozen(0xE8, 0x7A, 0x2E);
        private static readonly Brush LabelText = Frozen(0xEC, 0xEF, 0xF3);
        private static readonly Brush Leader = Frozen(0x6E, 0x7A, 0x88);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        public WorkshopControllerPreview() => InitializeComponent();

        /// <summary>One callout: the art element that serves it and the
        /// text describing what drives it.</summary>
        public readonly record struct Callout(string ArtTarget, string Label);

        public void Render(string controllerTag, IEnumerable<Callout> callouts)
        {
            ModelCanvas.Children.Clear();
            var wanted = (callouts ?? Enumerable.Empty<Callout>())
                .Where(c => !string.IsNullOrWhiteSpace(c.ArtTarget))
                .GroupBy(c => c.ArtTarget, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                              g => string.Join(", ", g.Select(c => c.Label).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct()),
                              StringComparer.OrdinalIgnoreCase);

            var (bodyW, bodyH, basePath, overlays, folder) = LayoutFor(controllerTag);
            double canvasW = bodyW + Gutter * 2;
            ModelCanvas.Width = canvasW;
            ModelCanvas.Height = bodyH;

            var body = Load(basePath, Gutter, 0, bodyW, bodyH);
            if (body != null)
            {
                body.Opacity = 0.5;
                Panel.SetZIndex(body, 1);
                ModelCanvas.Children.Add(body);
            }

            // Elements that actually have a binding, split by which gutter
            // they are nearest and ordered top-down so the leaders do not
            // cross each other.
            var hits = overlays
                .Where(o => wanted.ContainsKey(o.TargetName))
                .GroupBy(o => o.TargetName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(o => o.Y + o.Height / 2)
                .ToList();

            foreach (var ov in hits)
            {
                var lit = LoadTinted($"2DModels/{folder}/{ov.ImageFile}",
                                     Gutter + ov.X, ov.Y, ov.Width, ov.Height);
                if (lit == null) continue;
                Panel.SetZIndex(lit, 5);
                ModelCanvas.Children.Add(lit);
            }

            double leftNext = 0, rightNext = 0;
            foreach (var ov in hits)
            {
                double cx = Gutter + ov.X + ov.Width / 2;
                double cy = ov.Y + ov.Height / 2;
                bool left = cx < Gutter + bodyW / 2;

                // Walk the label down its gutter until it clears the last
                // one on that side, then keep it inside the canvas.
                double y = left ? Math.Max(cy, leftNext) : Math.Max(cy, rightNext);
                y = Math.Min(y, bodyH - RowPitch * 0.6);
                if (left) leftNext = y + RowPitch; else rightNext = y + RowPitch;

                var text = new TextBlock
                {
                    Text = wanted[ov.TargetName],
                    FontSize = LabelFont,
                    Foreground = LabelText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = Gutter - 40,
                    TextAlignment = left ? TextAlignment.Right : TextAlignment.Left,
                };
                text.Measure(new Size(Gutter - 40, double.PositiveInfinity));
                double tw = Math.Min(text.DesiredSize.Width, Gutter - 40);

                double tx = left ? Gutter - 34 - tw : Gutter + bodyW + 34;
                Canvas.SetLeft(text, tx);
                Canvas.SetTop(text, y - LabelFont * 0.72);
                Panel.SetZIndex(text, 20);
                ModelCanvas.Children.Add(text);

                // Leader: label edge to the element edge, with a small
                // ember tick at the button so the eye lands on the part.
                double lx = left ? Gutter - 26 : Gutter + bodyW + 26;
                double ex = left ? Gutter + ov.X : Gutter + ov.X + ov.Width;
                var poly = new System.Windows.Shapes.Polyline
                {
                    Stroke = Leader,
                    StrokeThickness = 2.5,
                    Points = new PointCollection { new(lx, y), new((lx + ex) / 2, y), new(ex, cy) },
                    IsHitTestVisible = false,
                };
                Panel.SetZIndex(poly, 15);
                ModelCanvas.Children.Add(poly);

                var tick = new System.Windows.Shapes.Ellipse
                {
                    Width = 12, Height = 12, Fill = Ember, IsHitTestVisible = false,
                };
                Canvas.SetLeft(tick, ex - 6);
                Canvas.SetTop(tick, cy - 6);
                Panel.SetZIndex(tick, 16);
                ModelCanvas.Children.Add(tick);
            }

            // Nothing anchored means nothing worth drawing.
            Visibility = hits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Draws a pack element in EMBER instead of its own paint.
        ///
        /// <para>The pack's *_Active and *_Click PNGs are authored in the
        /// vendor's own highlight colour, which is blue. Dropping an ember
        /// glow behind a blue sprite still reads as blue. So the sprite is
        /// used as an ALPHA MASK over a solid ember fill, which is the
        /// technique this repo already uses for the lightbar overlays: the
        /// shape comes from the art, the colour comes from us, and the
        /// preview stays on-brand without touching the assets.</para></summary>
        private static FrameworkElement LoadTinted(string resourcePath, double x, double y, double w, double h)
        {
            var bmp = EmbeddedBitmaps.Load(resourcePath);
            if (bmp == null) return null;
            var mask = new ImageBrush(bmp) { Stretch = Stretch.Fill };
            mask.Freeze();
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Fill = Ember,
                OpacityMask = mask,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0xE8, 0x7A, 0x2E),
                    BlurRadius = 26,
                    ShadowDepth = 0,
                    Opacity = 0.8,
                },
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            return rect;
        }

        private static Image Load(string resourcePath, double x, double y, double w, double h)
        {
            // EmbeddedBitmaps, never pack:// URIs: those crash on .NET 10
            // single-file publish (the #175 lightbar lesson).
            var bmp = EmbeddedBitmaps.Load(resourcePath);
            if (bmp == null) return null;
            var img = new Image { Source = bmp, Width = w, Height = h, Stretch = Stretch.Fill };
            Canvas.SetLeft(img, x);
            Canvas.SetTop(img, y);
            return img;
        }

        private static (int W, int H, string BasePath, OverlayElement[] Ov, string Folder)
            LayoutFor(string tag)
        {
            string t = tag ?? "";
            if (t.Contains("ps5", StringComparison.OrdinalIgnoreCase))
                return (DualSenseLayout.BaseWidth, DualSenseLayout.BaseHeight,
                        DualSenseLayout.BasePath, DualSenseLayout.Overlays, "DualSense");
            if (t.Contains("ps4", StringComparison.OrdinalIgnoreCase))
                return (DS4Layout.BaseWidth, DS4Layout.BaseHeight,
                        DS4Layout.BasePath, DS4Layout.Overlays, "DS4");
            if (t.Contains("switch", StringComparison.OrdinalIgnoreCase))
                return (SwitchProLayout.BaseWidth, SwitchProLayout.BaseHeight,
                        SwitchProLayout.BasePath, SwitchProLayout.Overlays, "SWITCHPRO");
            if (t.Contains("xbox360", StringComparison.OrdinalIgnoreCase))
                return (Xbox360Layout.BaseWidth, Xbox360Layout.BaseHeight,
                        Xbox360Layout.BasePath, Xbox360Layout.Overlays, "XBOX360");
            return (XboxOneSLayout.BaseWidth, XboxOneSLayout.BaseHeight,
                    XboxOneSLayout.BasePath, XboxOneSLayout.Overlays, "XBOXONE");
        }
    }
}
