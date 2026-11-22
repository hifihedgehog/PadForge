using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
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
        // The callouts, in the frame's own units. Their words are 12, the
        // least text PadForge draws, one line each, at any size the pad is
        // drawn at. They were drawn inside the scaled art at 30 of its
        // pixels, which the 200-tall frame brought to about 5.
        private const double CalloutFont = 12;
        // The lines' pitch where the frame has room for it.
        private const double CalloutPitch = 16;
        // The level run of a leader, from beside its words to the pad.
        private const double LeaderRun = 14;
        private const double TickSize = 5;

        private static Brush Ember => PadForge.Common.Themes.ThemeLook.CalloutBrush;
        private static readonly Brush Leader = Frozen(0x6E, 0x7A, 0x88);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        /// <summary>The controls with a binding and what each does, in the
        /// order the pad draws them.</summary>
        private readonly List<(OverlayElement Element, string Text)> _callouts = new();

        public WorkshopControllerPreview()
        {
            InitializeComponent();
            // The callouts follow the pad: a new size of the frame or of the
            // pad lays them out again.
            CalloutCanvas.SizeChanged += (s, e) => LayoutCallouts();
            ArtBox.SizeChanged += (s, e) => LayoutCallouts();
        }

        /// <summary>One callout: the art element it is DRAWN ON (the
        /// physical control the config binds) and the text describing what
        /// that control does.</summary>
        public readonly record struct Callout(string ArtAnchor, string Label);

        public void Render(string controllerTag, IEnumerable<Callout> callouts)
        {
            ModelCanvas.Children.Clear();
            _callouts.Clear();
            var wanted = (callouts ?? Enumerable.Empty<Callout>())
                .Where(c => !string.IsNullOrWhiteSpace(c.ArtAnchor))
                .GroupBy(c => c.ArtAnchor, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key,
                              g => string.Join(", ", g.Select(c => c.Label).Where(l => !string.IsNullOrWhiteSpace(l)).Distinct()),
                              StringComparer.OrdinalIgnoreCase);

            var (bodyW, bodyH, basePath, overlays, folder) = LayoutFor(controllerTag);
            FoldTouchpadAnchors(wanted, overlays);
            ModelCanvas.Width = bodyW;
            ModelCanvas.Height = bodyH;

            var body = Load(basePath, 0, 0, bodyW, bodyH);
            if (body != null)
            {
                body.Opacity = 0.5;
                Panel.SetZIndex(body, 1);
                ModelCanvas.Children.Add(body);
            }

            // Elements that actually have a binding, ordered top-down so the
            // leaders on each side do not cross each other.
            var hits = overlays
                .Where(o => wanted.ContainsKey(o.TargetName))
                .GroupBy(o => o.TargetName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(o => o.Y + o.Height / 2)
                .ToList();

            foreach (var ov in hits)
            {
                _callouts.Add((ov, wanted[ov.TargetName]));
                var lit = LoadTinted($"2DModels/{folder}/{ov.ImageFile}", ov.X, ov.Y, ov.Width, ov.Height);
                if (lit == null) continue;
                Panel.SetZIndex(lit, 5);
                ModelCanvas.Children.Add(lit);
            }

            // Nothing anchored means nothing worth drawing.
            Visibility = hits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            // A pad of another size is laid out before its callouts can be.
            LayoutCallouts();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(LayoutCallouts));
        }

        /// <summary>Draws each callout beside the pad, on the side its
        /// control is nearest: its words at 12, and a leader from beside the
        /// words to the control's edge, with a small ember tick there so the
        /// eye lands on the part. Each line starts level with its control
        /// and walks down to clear the one above it, and a column too long
        /// for the frame is pulled up to end inside it. Words too long for
        /// the room beside the pad end in an ellipsis.</summary>
        internal void LayoutCallouts()
        {
            CalloutCanvas.Children.Clear();
            double width = CalloutCanvas.ActualWidth, height = CalloutCanvas.ActualHeight;
            if (_callouts.Count == 0 || !(width > 0) || !(height > 0) || !(ArtBox.ActualWidth > 0)) return;

            GeneralTransform toCallouts = ModelCanvas.TransformToVisual(CalloutCanvas);
            Rect pad = toCallouts.TransformBounds(new Rect(0, 0, ModelCanvas.Width, ModelCanvas.Height));
            double middle = pad.Left + pad.Width / 2;
            var places = _callouts
                .Select(c => (c.Text, At: toCallouts.TransformBounds(new Rect(c.Element.X, c.Element.Y, c.Element.Width, c.Element.Height))))
                .ToList();

            foreach (bool left in new[] { true, false })
            {
                var side = places
                    .Where(p => (p.At.Left + p.At.Width / 2 < middle) == left)
                    .OrderBy(p => p.At.Top + p.At.Height / 2)
                    .ToList();
                if (side.Count == 0) continue;

                // A side with more callouts than the frame holds at the
                // usual pitch sets its lines closer, down to the words' own
                // size: a Valve pad can call out 15 controls on one side.
                double pitch = Math.Max(CalloutFont, Math.Min(CalloutPitch, height / side.Count));
                double half = pitch / 2;
                var rows = new double[side.Count];
                for (int i = 0; i < side.Count; i++)
                    rows[i] = Math.Max(side[i].At.Top + side[i].At.Height / 2, i == 0 ? half : rows[i - 1] + pitch);
                for (int i = side.Count - 1; i >= 0; i--)
                    rows[i] = Math.Max(half, Math.Min(rows[i], i == side.Count - 1 ? height - half : rows[i + 1] - pitch));

                double edge = left ? pad.Left : pad.Right;
                double room = (left ? edge : width - edge) - LeaderRun;
                if (room <= 0) continue;

                for (int i = 0; i < side.Count; i++)
                {
                    var text = PadForge.Common.TextFaces.Body(new TextBlock
                    {
                        Text = side[i].Text,
                        FontSize = CalloutFont,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = room,
                        TextAlignment = left ? TextAlignment.Right : TextAlignment.Left,
                    });
                    text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                    text.Measure(new Size(room, double.PositiveInfinity));
                    double tw = Math.Min(text.DesiredSize.Width, room);
                    Canvas.SetLeft(text, left ? edge - LeaderRun - tw : edge + LeaderRun);
                    Canvas.SetTop(text, rows[i] - text.DesiredSize.Height / 2);
                    CalloutCanvas.Children.Add(text);

                    Rect at = side[i].At;
                    double cy = at.Top + at.Height / 2;
                    double ex = left ? at.Left : at.Right;
                    CalloutCanvas.Children.Add(new System.Windows.Shapes.Polyline
                    {
                        Stroke = Leader,
                        StrokeThickness = 1,
                        Points = new PointCollection
                        {
                            new(left ? edge - LeaderRun + 4 : edge + LeaderRun - 4, rows[i]),
                            new(edge, rows[i]),
                            new(ex, cy),
                        },
                        IsHitTestVisible = false,
                    });
                    var tick = new System.Windows.Shapes.Ellipse
                    {
                        Width = TickSize, Height = TickSize, Fill = Ember, IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(tick, ex - TickSize / 2);
                    Canvas.SetTop(tick, cy - TickSize / 2);
                    CalloutCanvas.Children.Add(tick);
                }
            }
        }

        /// <summary>Draws a pack element in EMBER instead of its own paint.
        ///
        /// <para>The pack's *_Active and *_Click PNGs are authored in the
        /// vendor's own highlight color, which is blue. Dropping an ember
        /// glow behind a blue sprite still reads as blue. So the sprite is
        /// used as an ALPHA MASK over a solid ember fill, which is the
        /// technique this repo already uses for the lightbar overlays: the
        /// shape comes from the art, the color comes from us, and the
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
                    Color = PadForge.Common.Themes.ThemeLook.Callout,
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
            var img = new PadForge.Controls.DecorativeImage { Source = bmp, Width = w, Height = h, Stretch = Stretch.Fill };
            Canvas.SetLeft(img, x);
            Canvas.SetTop(img, y);
            return img;
        }

        /// <summary>Steam names touchpads by index, so a source resolves to
        /// a SIDED anchor. Valve's own pads have two, but a DualSense or DS4
        /// body has exactly one, named without a side. Fold the sided anchor
        /// onto it rather than dropping the callout.</summary>
        internal static void FoldTouchpadAnchors(
            IDictionary<string, string> wanted, OverlayElement[] overlays)
        {
            var have = new HashSet<string>(overlays.Select(o => o.TargetName),
                                           StringComparer.OrdinalIgnoreCase);
            foreach (var key in wanted.Keys.ToList())
            {
                if (have.Contains(key)) continue;
                if (!key.StartsWith("LeftTouchpad", StringComparison.OrdinalIgnoreCase)
                    && !key.StartsWith("RightTouchpad", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var alt in new[] { "TouchpadClick", "Touchpad" })
                {
                    if (!have.Contains(alt)) continue;
                    wanted[alt] = wanted.TryGetValue(alt, out var prior) && prior != wanted[key]
                        ? prior + ", " + wanted[key]
                        : wanted[key];
                    wanted.Remove(key);
                    break;
                }
            }
        }

        /// <summary>Which art folder a controller tag resolves to. Exists so
        /// the tag-to-body routing is testable without a UI thread.</summary>
        internal static string FolderForTag(string tag) => LayoutFor(tag).Folder;

        private static (int W, int H, string BasePath, OverlayElement[] Ov, string Folder)
            LayoutFor(string tag)
        {
            string t = tag ?? "";
            bool Is(string frag) => t.Contains(frag, StringComparison.OrdinalIgnoreCase);

            // Every body this repo ships, matched most-specific first. The
            // Steam tags are the same namespace the browse chips use.
            if (Is("ps5")) // DualSense and DualSense Edge
                return (DualSenseLayout.BaseWidth, DualSenseLayout.BaseHeight,
                        DualSenseLayout.BasePath, DualSenseLayout.Overlays, "DualSense");
            if (Is("ps4"))
                return (DS4Layout.BaseWidth, DS4Layout.BaseHeight,
                        DS4Layout.BasePath, DS4Layout.Overlays, "DS4");
            // controller_ps3 is a live tag. Thousands of configs carry it
            // (8,566 for GTA V alone, queried 2026-10-01), and the serial
            // kv-tag on one of them, 1597302226, reads 54c-268: the
            // DualShock 3's own VID and PID.
            if (Is("ps3"))
                return (DualShock3Layout.BaseWidth, DualShock3Layout.BaseHeight,
                        DualShock3Layout.BasePath, DualShock3Layout.Overlays, "DS3");
            if (Is("switch"))
                return (SwitchProLayout.BaseWidth, SwitchProLayout.BaseHeight,
                        SwitchProLayout.BasePath, SwitchProLayout.Overlays, "SWITCHPRO");
            // Valve's own hardware, which is most of what the Steam
            // workshop actually carries. "neptune" is the Deck.
            if (Is("neptune"))
                return (SteamDeckLayout.BaseWidth, SteamDeckLayout.BaseHeight,
                        SteamDeckLayout.BasePath, SteamDeckLayout.Overlays, "STEAMDECK");
            // "gordon" is the 2015 pad, "triton" the 2026 one. They get
            // separate bodies: the 2026 unit carries two sticks, a real
            // D-pad and square trackpads, so the older render would show
            // the wrong controls in the wrong places.
            if (Is("triton"))
                return (SteamController2Layout.BaseWidth, SteamController2Layout.BaseHeight,
                        SteamController2Layout.BasePath, SteamController2Layout.Overlays, "STEAMCONTROLLER2");
            if (Is("steamcontroller"))
                return (SteamControllerLayout.BaseWidth, SteamControllerLayout.BaseHeight,
                        SteamControllerLayout.BasePath, SteamControllerLayout.Overlays, "STEAMCONTROLLER");
            if (Is("xbox360"))
                return (Xbox360Layout.BaseWidth, Xbox360Layout.BaseHeight,
                        Xbox360Layout.BasePath, Xbox360Layout.Overlays, "XBOX360");
            // The Elite and the Series pad are the current-generation body,
            // which we ship and which was previously going unused while
            // every one of these fell back to the Xbox One S shape.
            if (Is("xboxelite") || Is("xboxseries") || Is("xbsx"))
                return (XboxSeriesXLayout.BaseWidth, XboxSeriesXLayout.BaseHeight,
                        XboxSeriesXLayout.BasePath, XboxSeriesXLayout.Overlays, "XBOXSERIES");
            return (XboxOneSLayout.BaseWidth, XboxOneSLayout.BaseHeight,
                    XboxOneSLayout.BasePath, XboxOneSLayout.Overlays, "XBOXONE");
        }
    }
}
