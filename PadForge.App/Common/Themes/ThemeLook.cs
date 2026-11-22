using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PadForge.Common.Themes
{
    /// <summary>
    /// The look's colors for code that draws (#591): a preview's glow, a
    /// held control's light, a chart's fill. Such code kept each color as
    /// a constant in a static field, where no theme could reach it. It
    /// reads them here, by the same tokens markup reads, and what it makes
    /// from them (a frozen glow, a frozen brush) is shared and made again
    /// when the look changes.
    /// </summary>
    internal static class ThemeLook
    {
        private static readonly object s_lock = new();
        private static readonly Dictionary<(double Blur, double Opacity), DropShadowEffect> s_glows = new();
        private static readonly Dictionary<string, Brush> s_brushes = new();
        private static readonly Dictionary<(string Key, double Blur, double Opacity), DropShadowEffect> s_toneGlows = new();
        private static int s_version;

        static ThemeLook()
        {
            ThemeTokens.Changed += (_, _) =>
            {
                lock (s_lock)
                {
                    s_glows.Clear();
                    s_toneGlows.Clear();
                    s_brushes.Clear();
                    s_version++;
                }
            };
        }

        /// <summary>A count that rises each time a look is put on. A view
        /// that keeps what it drew compares it to the count it drew at.</summary>
        internal static int Version
        {
            get { lock (s_lock) return s_version; }
        }

        /// <summary>The accent: ember under PadForge's own look.</summary>
        internal static Color Accent => ThemeTokens.Of("EmberColor", EmberTheme.Accent);

        /// <summary>The accent's deep, light, pale and flash tones.</summary>
        internal static Color AccentDeep => ThemeTokens.Of("EmberDeepColor", Color.FromRgb(0xC4, 0x3D, 0x0C));
        internal static Color AccentLight => ThemeTokens.Of("EmberLightColor", Color.FromRgb(0xFF, 0xA2, 0x4D));
        internal static Color AccentPale => ThemeTokens.Of("EmberPaleColor", Color.FromRgb(0xFF, 0xD2, 0xB8));
        internal static Color AccentFlash => ThemeTokens.Of("EmberFlashColor", Color.FromRgb(0xFF, 0xA5, 0x00));

        /// <summary>The tones code draws an output in, each by its token:
        /// a stick's output trail, the equalizer's curve and its handle
        /// under the pointer, the light of the curve editor's live dot, and
        /// a community config's lit control.</summary>
        internal static Color Trail => ThemeTokens.Of("EmberTrailColor", Color.FromRgb(0xFF, 0x8C, 0x3C));
        internal static Color Curve => ThemeTokens.Of("EmberCurveColor", Color.FromRgb(0xF2, 0x65, 0x2A));
        internal static Color CurveHot => ThemeTokens.Of("EmberCurveHotColor", Color.FromRgb(0xFF, 0x9D, 0x5D));
        internal static Color DotGlow => ThemeTokens.Of("EmberDotGlowColor", Color.FromRgb(0xFF, 0x82, 0x28));
        internal static Color Callout => ThemeTokens.Of("EmberCalloutColor", Color.FromRgb(0xE8, 0x7A, 0x2E));

        /// <summary>The raw input signal's color, of the secondary's
        /// family: the cold blue under PadForge's own look.</summary>
        internal static Color Input => ThemeTokens.Of("ColdInputColor", Color.FromRgb(0x58, 0xB6, 0xE4));

        // The brushes of those tones, each frozen and shared until the look changes.
        internal static Brush AccentBrush => Solid("Accent", () => Accent);
        internal static Brush AccentDeepBrush => Solid("AccentDeep", () => AccentDeep);
        internal static Brush AccentLightBrush => Solid("AccentLight", () => AccentLight);
        internal static Brush AccentFlashBrush => Solid("AccentFlash", () => AccentFlash);
        internal static Brush TrailBrush => Solid("Trail", () => Trail);
        internal static Brush CurveBrush => Solid("Curve", () => Curve);
        internal static Brush CurveHotBrush => Solid("CurveHot", () => CurveHot);
        internal static Brush CalloutBrush => Solid("Callout", () => Callout);
        internal static Brush InputBrush => Solid("Input", () => Input);

        /// <summary>A soft light in the raw input's color with no offset,
        /// frozen and shared until the look changes.</summary>
        internal static DropShadowEffect InputGlow(double blur, double opacity)
        {
            lock (s_lock)
            {
                if (!s_toneGlows.TryGetValue(("Input", blur, opacity), out var glow))
                {
                    glow = new DropShadowEffect { Color = Input, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };
                    glow.Freeze();
                    s_toneGlows[("Input", blur, opacity)] = glow;
                }
                return glow;
            }
        }

        /// <summary>A soft light in the accent with no offset, frozen and
        /// shared: the bloom a lit preview part carries. The same one comes
        /// back until the look changes, so a part can tell by reference
        /// whether it already wears it.</summary>
        internal static DropShadowEffect Glow(double blur, double opacity)
        {
            lock (s_lock)
            {
                if (!s_glows.TryGetValue((blur, opacity), out var glow))
                {
                    glow = new DropShadowEffect { Color = Accent, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };
                    glow.Freeze();
                    s_glows[(blur, opacity)] = glow;
                }
                return glow;
            }
        }

        /// <summary>A brush made from the look's colors, frozen and
        /// shared under a name, and made again when the look changes.</summary>
        internal static Brush Brush(string name, Func<Brush> make)
        {
            lock (s_lock)
            {
                if (!s_brushes.TryGetValue(name, out var brush))
                {
                    brush = make();
                    if (brush.CanFreeze) brush.Freeze();
                    s_brushes[name] = brush;
                }
                return brush;
            }
        }

        /// <summary>One of the accent's tones as a solid brush.</summary>
        internal static Brush Solid(string name, Func<Color> color) => Brush(name, () => new SolidColorBrush(color()));
    }
}
