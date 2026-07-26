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
    /// <summary>Draws the real controller for a community config and lights
    /// up the inputs the config actually binds.
    ///
    /// <para>The art is the repo's own Gamepad-Asset-Pack pipeline
    /// (<c>2DModels/</c> PNGs positioned by the generated
    /// <see cref="ControllerOverlayLayout"/>), NOT hand-drawn shapes and
    /// not invented glyphs. That pipeline already carries the one join this
    /// preview needs: every <c>OverlayElement</c> knows its
    /// <c>TargetName</c>, and the translator's manifest reports the same
    /// target namespace ("ButtonA", "LeftTrigger", "DPadUp"), so a bound
    /// target can be placed on the correct pixel of the correct pad without
    /// any new mapping table.</para>
    ///
    /// <para>Bound elements render at full strength over the base body.
    /// Unbound ones stay hidden, so the silhouette reads as "this is what
    /// the config touches" at a glance rather than as a parts diagram.</para></summary>
    public partial class WorkshopControllerPreview : UserControl
    {
        public WorkshopControllerPreview()
        {
            InitializeComponent();
        }

        /// <summary>Clears the canvas and draws <paramref name="controller"/>
        /// with every element in <paramref name="boundTargets"/> lit.
        /// Unknown controller types fall back to the Xbox One body, which is
        /// the shape most community configs are authored against.</summary>
        public void Render(string controllerTag, IEnumerable<string> boundTargets)
        {
            ModelCanvas.Children.Clear();
            var bound = new HashSet<string>(
                boundTargets ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            var (baseW, baseH, basePath, overlays, folder) = LayoutFor(controllerTag);
            ModelCanvas.Width = baseW;
            ModelCanvas.Height = baseH;

            var body = Load(basePath, 0, 0, baseW, baseH);
            if (body != null)
            {
                // The unbound body sits back so the lit elements carry the eye.
                body.Opacity = 0.55;
                Panel.SetZIndex(body, 1);
                ModelCanvas.Children.Add(body);
            }

            int lit = 0;
            foreach (var ov in overlays)
            {
                if (!bound.Contains(ov.TargetName)) continue;
                var img = Load($"2DModels/{folder}/{ov.ImageFile}", ov.X, ov.Y, ov.Width, ov.Height);
                if (img == null) continue;
                img.IsHitTestVisible = false;
                img.Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0xE8, 0x7A, 0x2E),
                    BlurRadius = 18,
                    ShadowDepth = 0,
                    Opacity = 0.75,
                };
                Panel.SetZIndex(img, 5);
                ModelCanvas.Children.Add(img);
                lit++;
            }

            // Nothing to light means nothing worth showing: the dossier
            // hides the whole preview rather than presenting a dimmed pad
            // that looks like a rendering failure.
            Visibility = lit == 0 ? Visibility.Collapsed : Visibility.Visible;
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

        /// <summary>Picks the pad body from the config's Steam controller
        /// tag. The tags are the same namespace the browse chips use.</summary>
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
