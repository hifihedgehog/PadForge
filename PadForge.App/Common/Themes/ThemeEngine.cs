using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace PadForge.Common.Themes
{
    /// <summary>What a theme gives the engine (#591): its base and its
    /// four colors. A color left out is PadForge's own for that base.</summary>
    internal sealed class ThemeSeeds
    {
        internal bool Dark = true;
        internal Color? Background, Accent, Secondary, Attention;
    }

    /// <summary>The few colors a theme's gallery tile is drawn in.</summary>
    internal sealed record ThemeSwatch(Color Ground, Color Card, Color Line, Color Accent, Color Secondary, Color Attention, Color Text);

    /// <summary>
    /// Works out a whole look from a theme's four colors (#591). Every
    /// brush PadForge already has keeps its place in its family (accent,
    /// secondary, attention or ground) and takes the theme's hue and
    /// chroma. Bright tones move with the theme color's lightness. Tinted
    /// grounds, surfaces, lines and marks move with the ground's lightness.
    /// Text keeps its lightness and is then moved until it reads.
    ///
    /// The engine reads the stock look's values, so it runs right after
    /// <see cref="EmberTheme.ApplyAccent"/> and never over its own output:
    /// <see cref="Apply"/> hands back the step that puts the stock values
    /// back. System, Light and Dark never run it.
    /// </summary>
    internal sealed class ThemeEngine
    {
        private enum Family { None, Accent, Secondary, Attention, Steel }

        // The stock look's own seeds on each base: what every stock brush is measured against.
        internal static readonly Color StockAccent = Color.FromRgb(0xFF, 0x6B, 0x2C);
        internal static Color StockSecondary(bool dark) => dark ? Color.FromRgb(0x7C, 0xC7, 0xEA) : Color.FromRgb(0x1E, 0x6E, 0x9F);
        internal static Color StockAttention(bool dark) => dark ? Color.FromRgb(0xF2, 0xB8, 0x4B) : Color.FromRgb(0x8F, 0x62, 0x00);
        internal static Color StockGround(bool dark) => dark ? Color.FromRgb(0x0B, 0x10, 0x18) : Color.FromRgb(0xFA, 0xFA, 0xFA);
        private static readonly Color StockCardDark = Color.FromRgb(0x12, 0x19, 0x25);

        /// <summary>The contrast a theme's accent holds on a card, and its colored text.</summary>
        internal const double MarkContrast = 3.0, TextContrast = 4.5;

        /// <summary>What white reads at on a dark theme's page and card,
        /// and black on a light theme's, at the least: the contrast the
        /// first text step holds. A ground a file sets past that is
        /// brought in, since no text color could read on it.</summary>
        internal const double GroundTextContrast = 7.0;

        private readonly Lch _refAccent, _refSecondary, _refAttention, _refGround;
        private readonly double _refGroundLuma;
        private readonly Lch _accent, _secondary, _attention, _ground;
        private readonly double _groundLuma;
        private readonly bool _dark;
        /// <summary>The two grounds text is read on: a card and the page.</summary>
        private readonly Color _card, _page;
        private readonly Color? _background;

        internal ThemeEngine(ThemeSeeds seeds)
        {
            _dark = seeds.Dark;
            Color refGround = StockGround(_dark);
            _refAccent = Lch.From(StockAccent);
            _refSecondary = Lch.From(StockSecondary(_dark));
            _refAttention = Lch.From(StockAttention(_dark));
            _refGround = Lch.From(refGround);
            _refGroundLuma = ThemeColor.Luma(refGround);
            // The ground: the file's, held to its base. A dark base keeps a
            // ground that white reads on, on the page and on the card it
            // makes, and a light base one that black reads on. A ground past
            // that is taken darker or lighter at its own hue and chroma
            // until it is. The starters' grounds are all inside.
            Color ground = seeds.Background ?? refGround;
            Lch asked = Lch.From(ground);
            double lightness = asked.L;
            for (int step = 0; ; step++)
            {
                _ground = Lch.From(ground);
                _groundLuma = ThemeColor.Luma(ground);
                _background = seeds.Background.HasValue ? ground : null;
                // The card over the theme's ground, and the page itself.
                _card = _dark ? Steel(StockCardDark)
                    : Color.FromRgb((byte)Math.Round(0.7 * 255 + 0.3 * ground.R), (byte)Math.Round(0.7 * 255 + 0.3 * ground.G), (byte)Math.Round(0.7 * 255 + 0.3 * ground.B));
                _page = _dark ? Steel(StockGround(true)) : ground;
                Color ink = _dark ? Colors.White : Colors.Black;
                bool reads = ThemeColor.Contrast(ink, _card) >= GroundTextContrast && ThemeColor.Contrast(ink, _page) >= GroundTextContrast;
                if (reads || !seeds.Background.HasValue || step >= 400) break;
                lightness = Math.Clamp(lightness + (_dark ? -0.0025 : 0.0025), 0, 1);
                ground = new Lch(lightness, asked.C, asked.H).ToColor();
            }
            // A mark has to read where it is drawn.
            _accent = Lch.From(Held(seeds.Accent ?? StockAccent, MarkContrast));
            _secondary = Lch.From(seeds.Secondary ?? StockSecondary(_dark));
            _attention = Lch.From(seeds.Attention ?? StockAttention(_dark));
        }

        /// <summary>A color moved until it reads at the ratio on a card
        /// and on the page. Both are dark or both are light, so the second
        /// step moves the way the first did and undoes none of it.</summary>
        private Color Held(Color color, double ratio) => ThemeColor.Reads(ThemeColor.Reads(color, _card, ratio), _page, ratio);

        /// <summary>The card's fill under this theme.</summary>
        internal Color Card => _card;

        /// <summary>The accent as the theme draws it, after it was moved to read on a card.</summary>
        internal Color Accent => _accent.ToColor();

        /// <summary>The page's ground under this theme.</summary>
        internal Color Ground => _page;

        internal ThemeSwatch Swatch()
            => new ThemeSwatch(Ground, _card,
                Map("SteelLineBrush", _dark ? Color.FromRgb(0x2A, 0x35, 0x46) : Color.FromRgb(0xC9, 0xCD, 0xD6)), Accent,
                Map("ColdBrush", StockSecondary(_dark)), Map("WaitBrush", StockAttention(_dark)),
                Map("TextFillColorSecondaryBrush", _dark ? Color.FromRgb(0xA9, 0xB4, 0xC4) : Color.FromRgb(0x44, 0x53, 0x6B)));

        /// <summary>The stock look's own tile colors on a base.</summary>
        internal static ThemeSwatch StockSwatch(bool dark)
            => dark
                ? new ThemeSwatch(StockGround(true), StockCardDark, Color.FromRgb(0x2A, 0x35, 0x46), StockAccent, StockSecondary(true), StockAttention(true), Color.FromRgb(0xA9, 0xB4, 0xC4))
                : new ThemeSwatch(StockGround(false), Colors.White, Color.FromRgb(0xC9, 0xCD, 0xD6), StockAccent, StockSecondary(false), StockAttention(false), Color.FromRgb(0x44, 0x53, 0x6B));

        // The page's three ground keys. A light base sets these alone and
        // the stock light look's translucent cards tint themselves over them.
        private static readonly string[] PageKeys =
        {
            "ApplicationBackgroundBrush", "SolidBackgroundFillColorBaseBrush", "NavigationViewContentBackground",
        };

        /// <summary>
        /// Sets the theme's brushes and colors over the application's own
        /// resources, where a page's by-name reference finds them first, and
        /// returns the step that takes them off again. It reads every key the
        /// application's own dictionaries hold: the ones EmberTheme set, and
        /// PadForge's tokens and styles' keys in the merged dictionaries after
        /// the two WPF UI ones.
        /// </summary>
        internal Action Apply(Application app)
        {
            var res = app.Resources;
            var keys = new HashSet<object>();
            foreach (object key in res.Keys) keys.Add(key);
            for (int i = 2; i < res.MergedDictionaries.Count; i++) Collect(res.MergedDictionaries[i], keys);
            var before = new Dictionary<object, object>();
            var added = new List<object>();

            void Set(object key, object themed)
            {
                if (IsOwn(res, key)) before[key] = res[key];
                else added.Add(key);
                res[key] = themed;
            }

            if (!_dark && _background.HasValue)
                foreach (string page in PageKeys)
                {
                    Set(page, Frozen(new SolidColorBrush(_background.Value)));
                    keys.Remove(page);
                }
            foreach (object key in keys)
            {
                if (key is not string name) continue;
                object themed = Transform(name, app.TryFindResource(key));
                if (themed != null) Set(key, themed);
            }
            return () =>
            {
                foreach (var pair in before) res[pair.Key] = pair.Value;
                foreach (object key in added) res.Remove(key);
            };
        }

        private static bool IsOwn(ResourceDictionary res, object key)
        {
            foreach (object own in res.Keys) if (Equals(own, key)) return true;
            return false;
        }

        private static void Collect(ResourceDictionary dictionary, HashSet<object> keys)
        {
            foreach (object key in dictionary.Keys) keys.Add(key);
            foreach (var merged in dictionary.MergedDictionaries) Collect(merged, keys);
        }

        private object Transform(string key, object value)
        {
            switch (value)
            {
                case SolidColorBrush solid:
                {
                    Color mapped = Map(key, solid.Color);
                    if (mapped == solid.Color) return null;
                    return Frozen(new SolidColorBrush(mapped) { Opacity = solid.Opacity });
                }
                case Color color:
                {
                    Color mapped = Map(key, color);
                    return mapped == color ? null : mapped;
                }
                case GradientBrush gradient:
                {
                    var clone = gradient.Clone();
                    bool changed = false;
                    foreach (var stop in clone.GradientStops)
                    {
                        Color mapped = Map(key, stop.Color);
                        if (mapped != stop.Color) { stop.Color = mapped; changed = true; }
                    }
                    return changed ? Frozen(clone) : null;
                }
                default: return null;
            }
        }

        private static T Frozen<T>(T brush) where T : Freezable
        {
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        // A key that starts with one of these belongs to the accent, whatever its color.
        private static readonly string[] AccentKeys = { "Ember", "Danger", "LinkHover", "Accent", "SystemAccent", "SliderThumb", "SystemFillColorAttention", "TitleWarmth" };

        // Colored text and marks: each reads on a card at the text contrast.
        private static readonly HashSet<string> TextKeys = new(StringComparer.Ordinal)
        {
            "EmberTextBrush", "EmberHotBrush", "DangerInkBrush", "LinkHoverBrush", "ColdBrush", "InputChipTextBrush",
            "WaitBrush", "WaitTextBrush", "WaitMutedBrush", "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush",
            "AccentTextFillColorTertiaryBrush",
        };

        // The steel text steps and the contrast each holds on a card.
        private static readonly Dictionary<string, double> SteelText = new(StringComparer.Ordinal)
        {
            ["TextFillColorPrimaryBrush"] = 7.0, ["TextFillColorSecondaryBrush"] = 5.5, ["TextFillColorTertiaryBrush"] = 4.5,
            ["SubRowTextBrush"] = 7.0, ["TextFillColorDisabledBrush"] = 2.6, ["QuietGlyphBrush"] = 3.0, ["SelectedTextBrush"] = 7.0,
        };

        private static Family FamilyOf(string key, Lch c)
        {
            if (key != null)
            {
                if (AccentKeys.Any(p => key.StartsWith(p, StringComparison.Ordinal))) return Family.Accent;
                if (key.StartsWith("Cold", StringComparison.Ordinal) || key.StartsWith("InputChip", StringComparison.Ordinal)) return Family.Secondary;
                if (key.StartsWith("Wait", StringComparison.Ordinal)) return Family.Attention;
            }
            if (c.C < 0.012) return Family.Steel;
            if (c.H >= 15 && c.H < 68 && c.C >= 0.03) return Family.Accent;
            if (c.H >= 68 && c.H < 115 && c.C >= 0.03) return Family.Attention;
            if (c.H >= 215 && c.H < 250 && c.C >= 0.055) return Family.Secondary;
            if (c.H >= 215 && c.H < 300) return Family.Steel;
            return Family.None;
        }

        /// <summary>The stock look's color under a key, as this theme draws it.</summary>
        internal Color Map(string key, Color color)
        {
            if (color.A == 0 && color.R == 0 && color.G == 0 && color.B == 0) return color;
            var c = Lch.From(color);
            Color mapped;
            switch (FamilyOf(key, c))
            {
                case Family.Accent:
                    mapped = Tinted(c, color, _refAccent, _accent,
                        key == "EmberInkBrush" || (key != null && key.StartsWith("AccentButtonForeground", StringComparison.Ordinal)));
                    break;
                case Family.Secondary: mapped = Tinted(c, color, _refSecondary, _secondary, false); break;
                case Family.Attention: mapped = Tinted(c, color, _refAttention, _attention, false); break;
                case Family.Steel: mapped = Steel(color); break;
                default: return color;
            }
            if (key != null && color.A == 255)
            {
                if (TextKeys.Contains(key)) mapped = Held(mapped, TextContrast);
                if (SteelText.TryGetValue(key, out double ratio)) mapped = Held(mapped, ratio);
            }
            return Color.FromArgb(color.A, mapped.R, mapped.G, mapped.B);
        }

        /// <summary>A color of a seed's family: its hue turned with the
        /// seed, its chroma scaled with the seed's, and its lightness
        /// moved with the seed when it is a bright tone, or with the
        /// ground when it is a tinted ground.</summary>
        private Color Tinted(Lch c, Color color, Lch reference, Lch seed, bool ink)
        {
            double hue = seed.H + (c.H - reference.H);
            double chroma = Math.Min(c.C * (reference.C > 0.001 ? seed.C / reference.C : 1), Math.Max(seed.C * 1.3, 0.02));
            bool bright = _dark ? c.L >= 0.45 : c.L <= 0.80;
            if (ink)
            {
                // Words on the accent's fill: the dark ink when it reads there, else white.
                Color fill = seed.ToColor();
                Color darkInk = ThemeColor.WithLuma(ThemeColor.Luma(color), Math.Min(chroma, 0.03), hue);
                return ThemeColor.Contrast(darkInk, fill) >= TextContrast ? darkInk : Colors.White;
            }
            if (bright)
                return new Lch(Math.Clamp(c.L + (seed.L - reference.L), 0.04, 0.985), chroma, hue).ToColor();
            return ThemeColor.WithLuma(Math.Clamp(ThemeColor.Luma(color) + (_groundLuma - _refGroundLuma), 0, 1), chroma, hue);
        }

        /// <summary>A step of the ground: surfaces, lines and marks move
        /// with the ground, and text keeps its own lightness.</summary>
        private Color Steel(Color color)
        {
            var c = Lch.From(color);
            double luma = ThemeColor.Luma(color);
            bool moves = _dark ? luma <= 0.50 : luma >= 0.50;
            double target = moves ? Math.Clamp(luma + (_groundLuma - _refGroundLuma), 0, 1) : luma;
            if (!_dark)
            {
                // On a light ground a surface takes the ground's own tint,
                // and text a little of it.
                if (_ground.C < 0.004) return moves ? ThemeColor.WithLuma(target, 0, 0) : color;
                return ThemeColor.WithLuma(target, moves ? _ground.C : Math.Min(Math.Max(c.C, 0.02), 0.04), _ground.H);
            }
            double scale = _refGround.C > 0.001 ? _ground.C / _refGround.C : 1;
            // Light text takes little of a vivid ground's color, or it turns that color.
            double chroma = moves ? Math.Min(c.C * scale, Math.Max(_ground.C * 1.35, 0.0)) : Math.Min(c.C * Math.Sqrt(scale), 0.045);
            if (moves && luma >= 0.30) chroma = Math.Min(chroma, 0.06);
            if (_ground.C < 0.004) chroma = 0;
            double hue = _ground.H + (c.H - _refGround.H);
            return ThemeColor.WithLuma(target, chroma, hue);
        }
    }
}
