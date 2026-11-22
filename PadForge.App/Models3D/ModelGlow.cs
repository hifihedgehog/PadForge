using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PadForge.Models3D
{
    /// <summary>How a 3D controller lights what is held on it: a pressed
    /// button takes the accent, and a tilted stick or a pulled trigger
    /// grades toward it by how far it is held (adapted from Handheld
    /// Companion). The controller's page and a Home card's model light
    /// their parts through these, so the two look alike.</summary>
    internal static class ModelGlow
    {
        /// <summary>How far a stick is tilted, as a part of full tilt on
        /// either axis, before its cap glows. Real sticks rarely report
        /// exactly zero, and a drifting stick would keep its cap tinted at
        /// rest (the owner saw the right cap off hue while the left was
        /// fine, from this glow alone). Only the glow waits for it, never
        /// the mapping.</summary>
        internal const float StickGlowsFrom = 0.05f;

        /// <summary>How far a trigger is pulled, from 0 to 1, before it
        /// glows, so sensor noise does not keep it lit at rest.</summary>
        internal const float TriggerGlowsFrom = 0.03f;

        // ─────────────────────────────────────────────
        //  Gradient color interpolation (from HC)
        // ─────────────────────────────────────────────

        // Per-element retained highlight material (keyed weakly on the
        // GeometryModel3D so rebuilt models collect): the per-frame
        // DiffuseMaterial + SolidColorBrush pair was allocated for every
        // deflected stick / pulled trigger every dirty frame. The brush we
        // own is never frozen, so mutating its Color is the supported
        // dependent-animation shape.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GeometryModel3D, DiffuseMaterial>
            s_highlightMaterials = new();

        // Textured defaults (ImageBrush atlases) cannot express the lerp
        // as a solid color: BrushColor's fallback made any deflection show
        // the FULL accent. Overlay a per-owner translucent accent layer on
        // top of the texture instead, alpha scaled by the factor, so the
        // glow grades with stick/trigger intensity while the texture stays
        // visible underneath.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GeometryModel3D, MaterialGroup>
            s_texturedHighlightGroups = new();

        // Per-geometry rest material for graded groups: riders inside a
        // ring/trigger group carry their own decal material, so restoring
        // the group default would repaint them wrongly.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GeometryModel3D, Material>
            s_riderDefaults = new();

        internal static Material GradientHighlight(GeometryModel3D owner,
            Material defaultMaterial, Material highlightMaterial, float factor,
            bool riderDecal = false)
        {
            factor = Math.Clamp(factor, 0f, 1f);
            if ((defaultMaterial as DiffuseMaterial)?.Brush is ImageBrush || defaultMaterial is MaterialGroup)
            {
                var grp = s_texturedHighlightGroups.GetValue(owner, _ =>
                {
                    // Two layers: the art, and a LIT accent overlay. Lit
                    // diffuse keeps the shading so shape and direction
                    // read. The overlay must render EXACTLY like a plain
                    // accent button (accent-colored brush under the same
                    // rig): for the white-masked rider brush that means
                    // AmbientColor = accent too. AmbientColor filters the
                    // RAW brush independently of Color, so left white the
                    // ambient term is gray (milky wash) and forced black
                    // it vanishes (dim, harsh shading). Accent-colored,
                    // every lighting term is accent-proportional: soft
                    // fill, exact hue, shaded. Rider overlays stay masked
                    // by the rider's alpha through a WHITE twin of the
                    // rider brush, so dark art (the XS carbon knurl)
                    // glows at full accent strength too.
                    var mg = new MaterialGroup();
                    mg.Children.Add(defaultMaterial);
                    mg.Children.Add(riderDecal && (defaultMaterial as DiffuseMaterial)?.Brush is ImageBrush rb
                        ? new DiffuseMaterial(WhiteMaskBrush(rb))
                        : new DiffuseMaterial(new SolidColorBrush()));
                    return mg;
                });
                if (!ReferenceEquals(grp.Children[0], defaultMaterial))
                    grp.Children[0] = defaultMaterial;
                var accent = BrushColor((highlightMaterial as DiffuseMaterial)?.Brush);
                var overlay = (DiffuseMaterial)grp.Children[1];
                var tint = Color.FromArgb((byte)(factor * 255), accent.R, accent.G, accent.B);
                if (overlay.Brush is SolidColorBrush solid)
                {
                    // accent-colored brush: its ambient term is already
                    // accent-tinted, same as a plain accent button
                    solid.Color = tint;
                }
                else
                {
                    overlay.Color = tint;          // diffuse term, alpha included
                    overlay.AmbientColor = tint;   // ambient term matches the hue
                }
                return grp;
            }
            // Cast-proof (#175 regression fix): a themed material may carry a
            // gradient brush, so the lerp starts from its first stop instead
            // of crashing the render loop with an invalid cast.
            var startColor = BrushColor((defaultMaterial as DiffuseMaterial)?.Brush);
            var endColor = BrushColor((highlightMaterial as DiffuseMaterial)?.Brush);

            byte a = (byte)(startColor.A * (1 - factor) + endColor.A * factor);
            byte r = (byte)(startColor.R * (1 - factor) + endColor.R * factor);
            byte g = (byte)(startColor.G * (1 - factor) + endColor.G * factor);
            byte b = (byte)(startColor.B * (1 - factor) + endColor.B * factor);

            var mat = s_highlightMaterials.GetValue(owner,
                _ => new DiffuseMaterial(new SolidColorBrush()));
            ((SolidColorBrush)mat.Brush).Color = Color.FromArgb(a, r, g, b);
            return mat;
        }

        internal static Color BrushColor(Brush brush) => brush switch
        {
            SolidColorBrush s => s.Color,
            GradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color,
            _ => PadForge.Common.EmberTheme.Accent,
        };

        // White-masked twins of rider atlas brushes (RGB forced white,
        // alpha preserved, viewport settings cloned): the rider glow
        // overlay multiplies its Color by these, giving full-strength
        // accent wherever art exists regardless of the art's darkness.
        // Keyed on the source brush so riders sharing one atlas share
        // one 16 MB conversion.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageBrush, ImageBrush>
            s_whiteMaskBrushes = new();

        private static ImageBrush WhiteMaskBrush(ImageBrush rb)
        {
            return s_whiteMaskBrushes.GetValue(rb, src =>
            {
                if (src.ImageSource is not System.Windows.Media.Imaging.BitmapSource bmp)
                    return src;
                var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    bmp, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
                var px = new byte[h * stride];
                conv.CopyPixels(px, stride, 0);
                for (int i = 0; i < px.Length; i += 4)
                    px[i] = px[i + 1] = px[i + 2] = 255;
                var mask = System.Windows.Media.Imaging.BitmapSource.Create(
                    w, h, conv.DpiX, conv.DpiY, PixelFormats.Bgra32, null, px, stride);
                mask.Freeze();
                var brush = src.Clone();
                brush.ImageSource = mask;
                brush.Freeze();
                return brush;
            });
        }

        // Highlight for a group that may carry rider decals. A COVERING
        // rider (the Xbox guide emblem) gets its own texels tinted accent
        // through DiffuseMaterial.Color while the host keeps its default
        // material, so only the glyph art glows (owner-specified look).
        // Non-covering riders (trigger labels, knurl bands) hide for the
        // duration and the host takes the solid accent like any other
        // element. Returns true when a covering rider carried the
        // highlight (callers must then leave the host material alone).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GeometryModel3D, DiffuseMaterial>
            s_riderTints = new();

        /// <summary>A geometry's rest material, kept the first time it is
        /// asked for: a rider carries its own decal material, which the
        /// group's default would paint over.</summary>
        internal static Material RiderDefault(GeometryModel3D geometry)
            => s_riderDefaults.GetValue(geometry, g => g.Material);

        /// <summary>A geometry's rest material, when one was kept.</summary>
        internal static bool TryRiderDefault(GeometryModel3D geometry, out Material rest)
            => s_riderDefaults.TryGetValue(geometry, out rest);

        /// <summary>Lights a pressed control's group. <paramref name="wholeGlow"/>
        /// is a stick's cap ring or a bumper, which glow whole.</summary>
        internal static bool SetGroupHighlight(Model3DGroup group, GeometryModel3D primary, Material hlMat,
            bool wholeGlow, ISet<GeometryModel3D> riders, ISet<GeometryModel3D> covering)
        {
            // Thumb rings glow as a WHOLE at full strength: host takes the
            // accent and the knurl riders tint through their own texels
            // (the graded-glow shape at factor 1), so the cap texture
            // glows just like the rest of the stick (owner ruling).
            // Bumpers take the same shape, so their L1/R1 lettering
            // glows on press exactly as L2/R2's does under the trigger's
            // graded path. Hiding the rider instead (the default branch
            // below) left the label gray while the button lit up.
            if (wholeGlow)
            {
                foreach (var child in group.Children)
                {
                    if (child is not GeometryModel3D g2) continue;
                    g2.Material = GradientHighlight(g2,
                        RiderDefault(g2), hlMat, 1f,
                        riders.Contains(g2));
                }
                return true;
            }
            bool covered = false;
            foreach (var child in group.Children)
            {
                if (child is not GeometryModel3D g2 || ReferenceEquals(g2, primary)) continue;
                if (!riders.Contains(g2)) continue;
                var rest = RiderDefault(g2);
                if (covering.Contains(g2)
                    && (rest as DiffuseMaterial)?.Brush is ImageBrush rb)
                {
                    // Lit accent through the art's own texels with the
                    // ambient term ACCENT-COLORED (white ambient = milky
                    // wash, black ambient = dim with harsh shading): the
                    // white art renders like a plain accent button, the
                    // dark logo cutout stays dark, and the lighting keeps
                    // the button's shape readable.
                    var accent = BrushColor((hlMat as DiffuseMaterial)?.Brush);
                    var tint = s_riderTints.GetValue(g2, _ => new DiffuseMaterial(rb));
                    tint.Color = Color.FromArgb(255, accent.R, accent.G, accent.B);
                    tint.AmbientColor = tint.Color;
                    g2.Material = tint;
                    g2.BackMaterial = tint;
                    covered = true;
                }
                else
                {
                    g2.Material = null;
                    g2.BackMaterial = null;
                }
            }
            if (!covered)
            {
                primary.Material = hlMat;
                primary.BackMaterial = hlMat;
            }
            return covered;
        }

        /// <summary>Puts a group's riders back to their rest materials.</summary>
        internal static void RestoreRiders(Model3DGroup group, GeometryModel3D primary, ISet<GeometryModel3D> riders)
        {
            foreach (var child in group.Children)
            {
                if (child is not GeometryModel3D g2 || ReferenceEquals(g2, primary)) continue;
                if (!riders.Contains(g2)) continue;
                if (s_riderDefaults.TryGetValue(g2, out var rest))
                {
                    g2.Material = rest;
                    g2.BackMaterial = rest;
                }
            }
        }
    }
}
