using System;
using System.Collections.Generic;
using System.Linq;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The light colors one controller asks of one device on top of its
    /// background color (#557): keys by HID usage, groups of lights by name
    /// (<see cref="LightGroups"/>), single lights by address, when set a
    /// background that replaces the claim's color, and, for a shift layer
    /// that shows the Base layer under its own colors, the Base layer's
    /// frame (<see cref="Under"/>). Colors are 0xRRGGBB.
    ///
    /// <para>Made whole and never changed, so a claim can carry one across
    /// threads and a backend can compare two by content. Keys and groups
    /// name nothing about a device, so one frame lights the W key on any
    /// brand. A single light's address is the device's own, and only the
    /// device it was set for has it.</para>
    /// </summary>
    internal sealed class KeyFrame : IEquatable<KeyFrame>
    {
        private static readonly IReadOnlyDictionary<ushort, int> NoKeys = new Dictionary<ushort, int>();
        private static readonly IReadOnlyDictionary<string, int> NoGroups = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<int, int> NoLights = new Dictionary<int, int>();

        /// <summary>No color on any light, and the claim's own background.</summary>
        public static readonly KeyFrame Empty = new(null, null, null, null);

        private readonly int _hash;

        public IReadOnlyDictionary<ushort, int> Keys { get; }
        public IReadOnlyDictionary<string, int> Groups { get; }
        public IReadOnlyDictionary<int, int> Lights { get; }

        /// <summary>The color every light without one of its own shows, or
        /// null for <see cref="Under"/>'s, and without that the claim's
        /// color. 0 turns those lights off.</summary>
        public int? Background { get; }

        /// <summary>The frame a light falls through to when this one gives
        /// it no color: a shift layer's Base layer. Null when there is
        /// none, and never an empty frame.</summary>
        public KeyFrame Under { get; }

        /// <summary>True when the frame changes nothing: no light color and
        /// the claim's own background.</summary>
        public bool IsEmpty => Keys.Count == 0 && Groups.Count == 0 && Lights.Count == 0 && Background == null
            && Under == null;

        /// <summary>True when this frame or one beneath it colors a single
        /// light by its address.</summary>
        public bool HasLights => Lights.Count > 0 || Under is { HasLights: true };

        private KeyFrame _withoutLights;

        /// <summary>The frame with no single light's color at any depth: what
        /// a device other than the one the lights were set on takes, since
        /// an address is only its own device's. The frame itself when it has
        /// none.</summary>
        public KeyFrame WithoutLights
            => !HasLights ? this
                : _withoutLights ??= new KeyFrame(Keys, Groups, null, Background, Under?.WithoutLights);

        public KeyFrame(IReadOnlyDictionary<ushort, int> keys, IReadOnlyDictionary<string, int> groups,
            IReadOnlyDictionary<int, int> lights, int? background, KeyFrame under = null)
        {
            Keys = Copy(keys, NoKeys);
            Groups = Copy(groups, NoGroups, StringComparer.Ordinal);
            Lights = Copy(lights, NoLights);
            Background = background.HasValue ? background.Value & 0xFFFFFF : null;
            Under = under is { IsEmpty: false } ? under : null;
            var hash = new HashCode();
            foreach (var pair in Keys.OrderBy(p => p.Key)) { hash.Add(pair.Key); hash.Add(pair.Value); }
            foreach (var pair in Groups.OrderBy(p => p.Key, StringComparer.Ordinal)) { hash.Add(pair.Key); hash.Add(pair.Value); }
            foreach (var pair in Lights.OrderBy(p => p.Key)) { hash.Add(pair.Key); hash.Add(pair.Value); }
            hash.Add(Background);
            hash.Add(Under?.GetHashCode() ?? 0);
            _hash = hash.ToHashCode();
        }

        private static IReadOnlyDictionary<TKey, int> Copy<TKey>(IReadOnlyDictionary<TKey, int> source,
            IReadOnlyDictionary<TKey, int> none, IEqualityComparer<TKey> comparer = null)
        {
            if (source == null || source.Count == 0) return none;
            var copy = new Dictionary<TKey, int>(source.Count, comparer);
            foreach (var pair in source) copy[pair.Key] = pair.Value & 0xFFFFFF;
            return copy;
        }

        public bool Equals(KeyFrame other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null || other._hash != _hash || other.Background != Background) return false;
            return Same(Keys, other.Keys) && Same(Groups, other.Groups) && Same(Lights, other.Lights)
                && (Under is null ? other.Under is null : Under.Equals(other.Under));
        }

        private static bool Same<TKey>(IReadOnlyDictionary<TKey, int> a, IReadOnlyDictionary<TKey, int> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var pair in a)
                if (!b.TryGetValue(pair.Key, out int value) || value != pair.Value) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is KeyFrame other && Equals(other);

        public override int GetHashCode() => _hash;

        /// <summary>Whether two frames would paint a device alike, with
        /// null standing for <see cref="Empty"/>.</summary>
        public static bool Same(KeyFrame a, KeyFrame b)
            => (a ?? Empty).Equals(b ?? Empty);
    }

    /// <summary>Turns a unit's lights, its background color and a key frame
    /// into one color per light (#557).</summary>
    internal static class LightRenderer
    {
        /// <summary>
        /// One 0xRRGGBB color for each light of <paramref name="map"/>, in
        /// its order. The most specific color wins: the light's own, then
        /// its key's, then its group's. A light the frame gives no color
        /// takes the color the frame beneath it gives
        /// (<see cref="KeyFrame.Under"/>), by the same order, and failing
        /// that the background: the first a frame sets, from the top, and
        /// <paramref name="background"/> otherwise. A key light whose key
        /// no source named takes the background, as a key no frame names
        /// does.
        /// </summary>
        public static int[] Render(LightMap map, int background, KeyFrame frame)
        {
            var lights = map?.Lights ?? Array.Empty<LightInfo>();
            var colors = new int[lights.Count];
            frame ??= KeyFrame.Empty;
            int fill = background;
            for (var f = frame; f != null; f = f.Under)
                if (f.Background is int set)
                {
                    fill = set;
                    break;
                }
            fill &= 0xFFFFFF;
            for (int i = 0; i < lights.Count; i++)
                colors[i] = ColorOf(frame, lights[i]) ?? fill;
            return colors;
        }

        /// <summary>The color the frames from <paramref name="frame"/> down
        /// give <paramref name="light"/> of its own, or null.</summary>
        public static int? ColorOf(KeyFrame frame, in LightInfo light)
        {
            for (var f = frame; f != null; f = f.Under)
            {
                if (f.Lights.TryGetValue(light.Address, out int color)
                    || (light.Kind == LightKind.Key && light.Usage != 0 && f.Keys.TryGetValue(light.Usage, out color))
                    || (light.Group is { } group && f.Groups.TryGetValue(group, out color)))
                    return color;
            }
            return null;
        }

        /// <summary>Whether two renders of one map paint the same.</summary>
        public static bool Same(int[] a, int[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            return a.AsSpan().SequenceEqual(b);
        }
    }
}
