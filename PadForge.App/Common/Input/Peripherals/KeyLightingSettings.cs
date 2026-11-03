using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>What a layer's lights show when the layer gives them no
    /// color of their own (#557). Append-only, saved by name.</summary>
    public enum KeyBackgroundRule
    {
        /// <summary>The Lighting tab's mode, the Base layer's rule until it
        /// is given another.</summary>
        Same = 0,
        /// <summary>Those lights off.</summary>
        Off = 1,
        /// <summary>Those lights in <see cref="KeyLightingLayer.BackgroundRgb"/>.</summary>
        Color = 2,
        /// <summary>Those lights as the Base layer shows them, a shift
        /// layer's rule until it is given another: the Mappings tab's
        /// inherit rule, light by light. The Base layer never has it.</summary>
        BaseLayer = 3,
    }

    /// <summary>One layer's light colors (#557): its own colors, and what
    /// its other lights show (<see cref="Background"/>).</summary>
    internal sealed class KeyLightingLayer
    {
        public string Mask { get; init; } = KeyLightingSettings.BaseMask;

        public bool IsBase => Mask == KeyLightingSettings.BaseMask;

        private readonly KeyBackgroundRule? _background;

        /// <summary>What the lights this layer gives no color show. Until
        /// set, the rule the layer starts with
        /// (<see cref="DefaultRule"/>). The Base layer has no Base layer
        /// to show, so it reads <see cref="KeyBackgroundRule.BaseLayer"/>
        /// as the Lighting tab's mode.</summary>
        public KeyBackgroundRule Background
        {
            get
            {
                var rule = _background ?? DefaultRule(Mask);
                return rule == KeyBackgroundRule.BaseLayer && IsBase ? KeyBackgroundRule.Same : rule;
            }
            init => _background = value;
        }

        public int BackgroundRgb { get; init; }
        public IReadOnlyDictionary<ushort, int> Keys { get; init; } = new Dictionary<ushort, int>();
        public IReadOnlyDictionary<string, int> Groups { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public IReadOnlyDictionary<int, int> Lights { get; init; } = new Dictionary<int, int>();

        /// <summary>The rule a layer starts with: the Lighting tab's mode on
        /// the Base layer, the Base layer's colors on a shift layer, the
        /// way a key a shift layer leaves alone keeps doing what it does on
        /// Base.</summary>
        public static KeyBackgroundRule DefaultRule(string mask)
            => mask == KeyLightingSettings.BaseMask ? KeyBackgroundRule.Same : KeyBackgroundRule.BaseLayer;

        /// <summary>True when the layer changes nothing: no colors of its
        /// own and the rule it starts with. Such a layer is not saved.</summary>
        public bool IsEmpty => Background == DefaultRule(Mask)
            && Keys.Count == 0 && Groups.Count == 0 && Lights.Count == 0;

        /// <summary>The frame this layer asks for: its colors, its
        /// background unless it keeps the Lighting tab's mode, and, for a
        /// layer that shows the Base layer under its own colors,
        /// <paramref name="baseFrame"/> beneath them.</summary>
        public KeyFrame ToFrame(KeyFrame baseFrame = null) => new(Keys, Groups, Lights,
            Background switch
            {
                KeyBackgroundRule.Off => 0,
                KeyBackgroundRule.Color => BackgroundRgb & 0xFFFFFF,
                _ => null,
            },
            Background == KeyBackgroundRule.BaseLayer ? baseFrame : null);
    }

    /// <summary>
    /// A device's light colors on one controller, per shift layer (#557),
    /// as DeviceSlotConfig saves them: one line of text, so it rides the
    /// settings file, profiles and the clipboard the way the equalizer
    /// bands do. Made whole and never changed, so the dispatcher can read
    /// it on its own thread while the tab edits a copy.
    ///
    /// <para>The text: layers split by "|", a layer as its shift layer's
    /// LayerMask, "&gt;", then its items split by ",". An item is
    /// "bg=same", "bg=off", "bg=base" or "bg=RRGGBB" for a rule other than
    /// the one the layer starts with (<see cref="KeyLightingLayer.DefaultRule"/>),
    /// "kUU=RRGGBB" for the key with HID usage 0xUU, "gNAME=RRGGBB" for a
    /// group (<see cref="LightGroups"/>) and "lN=RRGGBB" for the device's
    /// light number N. For example
    /// "Base&gt;k1A=FF0000,gedge=0000FF|Shift1&gt;bg=off,k06=FFFFFF". A part
    /// the reader does not know is dropped, and the writer puts everything
    /// in one order, so equal settings have equal text.</para>
    /// </summary>
    internal sealed class KeyLightingSettings
    {
        public const string BaseMask = "Base";

        public static readonly KeyLightingSettings Empty = new(new Dictionary<string, KeyLightingLayer>(StringComparer.Ordinal));

        public IReadOnlyDictionary<string, KeyLightingLayer> Layers { get; }

        public bool IsEmpty => Layers.Values.All(l => l.IsEmpty);

        public KeyLightingSettings(IReadOnlyDictionary<string, KeyLightingLayer> layers)
        {
            var copy = new Dictionary<string, KeyLightingLayer>(StringComparer.Ordinal);
            if (layers != null)
                foreach (var pair in layers)
                {
                    if (!ValidMask(pair.Key) || pair.Value == null) continue;
                    var layer = pair.Value.Mask == pair.Key ? pair.Value : Rename(pair.Value, pair.Key);
                    if (!layer.IsEmpty) copy[pair.Key] = layer;
                }
            Layers = copy;
        }

        private static KeyLightingLayer Rename(KeyLightingLayer layer, string mask) => new()
        {
            Mask = mask,
            Background = layer.Background,
            BackgroundRgb = layer.BackgroundRgb,
            Keys = layer.Keys,
            Groups = layer.Groups,
            Lights = layer.Lights,
        };

        /// <summary>A layer mask the text can hold: not empty, and none of
        /// the characters the text splits on.</summary>
        public static bool ValidMask(string mask)
            => !string.IsNullOrWhiteSpace(mask) && mask.IndexOfAny(new[] { '|', '>', ',', '=' }) < 0;

        /// <summary>The layer saved for <paramref name="mask"/>, or null.</summary>
        public KeyLightingLayer LayerOf(string mask)
            => mask != null && Layers.TryGetValue(mask, out var layer) ? layer : null;

        /// <summary>The layer saved for <paramref name="mask"/>, or the
        /// empty one it starts as.</summary>
        public KeyLightingLayer LayerOrDefault(string mask)
            => LayerOf(mask) ?? new KeyLightingLayer { Mask = mask };

        /// <summary>
        /// The frame for the shift layer engaged now
        /// (InputManager.GetEngagedLayerMask). A shift layer shows its own
        /// colors, and its other lights show what its rule says: the Base
        /// layer's colors and background until it is given another rule,
        /// the way an unmapped row on a shift layer falls through to Base
        /// on the Mappings tab. A shift layer with nothing saved shows the
        /// Base layer. Empty when the device has no colors at all.
        /// </summary>
        public KeyFrame FrameFor(string engagedMask)
        {
            var baseFrame = LayerOf(BaseMask)?.ToFrame() ?? KeyFrame.Empty;
            if (engagedMask == null || engagedMask == BaseMask) return baseFrame;
            var layer = LayerOf(engagedMask);
            return layer == null ? baseFrame : layer.ToFrame(baseFrame);
        }

        /// <summary>These settings with <paramref name="layer"/> in place of
        /// the one of its mask, or without it when it is empty.</summary>
        public KeyLightingSettings With(KeyLightingLayer layer)
        {
            if (layer == null || !ValidMask(layer.Mask)) return this;
            var layers = new Dictionary<string, KeyLightingLayer>(Layers, StringComparer.Ordinal);
            if (layer.IsEmpty) layers.Remove(layer.Mask);
            else layers[layer.Mask] = layer;
            return new KeyLightingSettings(layers);
        }

        /// <summary>These settings with no single-light colors, for a copy
        /// onto another device, whose light numbers mean other lights.</summary>
        public KeyLightingSettings WithoutLights()
        {
            var layers = new Dictionary<string, KeyLightingLayer>(StringComparer.Ordinal);
            foreach (var pair in Layers)
            {
                var l = pair.Value;
                layers[pair.Key] = new KeyLightingLayer
                {
                    Mask = l.Mask,
                    Background = l.Background,
                    BackgroundRgb = l.BackgroundRgb,
                    Keys = l.Keys,
                    Groups = l.Groups,
                };
            }
            return new KeyLightingSettings(layers);
        }

        // ─────────────────────────────────────────────
        //  Text
        // ─────────────────────────────────────────────

        public static KeyLightingSettings Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Empty;
            var layers = new Dictionary<string, KeyLightingLayer>(StringComparer.Ordinal);
            foreach (var part in text.Split('|'))
            {
                int arrow = part.IndexOf('>');
                if (arrow <= 0) continue;
                string mask = part.Substring(0, arrow).Trim();
                if (!ValidMask(mask) || layers.ContainsKey(mask)) continue;
                var background = KeyLightingLayer.DefaultRule(mask);
                int backgroundRgb = 0;
                var keys = new Dictionary<ushort, int>();
                var groups = new Dictionary<string, int>(StringComparer.Ordinal);
                var lights = new Dictionary<int, int>();
                foreach (var raw in part.Substring(arrow + 1).Split(','))
                {
                    string item = raw.Trim();
                    if (item.Length == 0) continue;
                    int eq = item.IndexOf('=');
                    if (eq <= 0) continue;
                    string name = item.Substring(0, eq);
                    string value = item.Substring(eq + 1);
                    if (name == "bg")
                    {
                        if (value == "same") background = KeyBackgroundRule.Same;
                        else if (value == "off") background = KeyBackgroundRule.Off;
                        else if (value == "base") background = KeyBackgroundRule.BaseLayer;
                        else if (TryColor(value, out int bg))
                        {
                            background = KeyBackgroundRule.Color;
                            backgroundRgb = bg;
                        }
                        continue;
                    }
                    if (!TryColor(value, out int rgb)) continue;
                    switch (name[0])
                    {
                        case 'k':
                            if (ushort.TryParse(name.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort usage)
                                && usage != 0)
                                keys[usage] = rgb;
                            break;
                        case 'g':
                            string group = name.Substring(1);
                            if (LightGroups.IsValid(group)) groups[group] = rgb;
                            break;
                        case 'l':
                            if (int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int address)
                                && address >= 0)
                                lights[address] = rgb;
                            break;
                    }
                }
                layers[mask] = new KeyLightingLayer
                {
                    Mask = mask,
                    Background = background,
                    BackgroundRgb = backgroundRgb,
                    Keys = keys,
                    Groups = groups,
                    Lights = lights,
                };
            }
            return layers.Count == 0 ? Empty : new KeyLightingSettings(layers);
        }

        private static bool TryColor(string value, out int rgb)
        {
            rgb = 0;
            return value.Length == 6
                && int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
        }

        public string Format()
        {
            var sb = new StringBuilder();
            foreach (var mask in Layers.Keys.OrderBy(m => m == BaseMask ? 0 : 1).ThenBy(m => m, StringComparer.Ordinal))
            {
                var layer = Layers[mask];
                if (sb.Length > 0) sb.Append('|');
                sb.Append(mask).Append('>');
                var items = new List<string>();
                if (layer.Background != KeyLightingLayer.DefaultRule(mask))
                    items.Add("bg=" + layer.Background switch
                    {
                        KeyBackgroundRule.Off => "off",
                        KeyBackgroundRule.Color => Hex(layer.BackgroundRgb),
                        KeyBackgroundRule.BaseLayer => "base",
                        _ => "same",
                    });
                foreach (var pair in layer.Keys.OrderBy(p => p.Key))
                    items.Add("k" + pair.Key.ToString("X2", CultureInfo.InvariantCulture) + "=" + Hex(pair.Value));
                foreach (var pair in layer.Groups.OrderBy(p => p.Key, StringComparer.Ordinal))
                    items.Add("g" + pair.Key + "=" + Hex(pair.Value));
                foreach (var pair in layer.Lights.OrderBy(p => p.Key))
                    items.Add("l" + pair.Key.ToString(CultureInfo.InvariantCulture) + "=" + Hex(pair.Value));
                sb.Append(string.Join(",", items));
            }
            return sb.ToString();
        }

        private static string Hex(int rgb) => (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    }
}
