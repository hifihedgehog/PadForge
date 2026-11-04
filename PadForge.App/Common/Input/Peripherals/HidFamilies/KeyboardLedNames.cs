using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>
    /// The lights of the OpenRGB keyboard families whose tables name each
    /// LED with OpenRGB's key names (#557): the tables in
    /// KeyboardLedNames.g.cs, which tools/gen_keyboard_led_names.py reads
    /// from OpenRGB, and the layouts KeyboardLayoutManager builds.
    /// </summary>
    internal static partial class KeyboardLedNames
    {
        /// <summary>The light an OpenRGB LED name makes at an address: a key
        /// from a generated table where OpenRGB's key names give the name a
        /// keyboard-page usage (<see cref="OpenRgbKeys"/>), else an Other
        /// light with the label the name has, if any. Null for OpenRGB's
        /// empty name, KEY_EN_UNUSED (RGBControllerKeyNames.cpp:15), which
        /// marks a place with no LED.</summary>
        public static LightInfo? Named(int address, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            OpenRgbKeys.TryGet(name, out ushort usage, out var label, out int number);
            return usage != 0
                ? new LightInfo(address, LightKind.Key, usage, LightKeySource.Generated)
                : new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
        }

        /// <summary>The lights of a name table, each at the address
        /// <paramref name="address"/> gives its index, the empty names left
        /// out.</summary>
        public static List<LightInfo> FromNames(IReadOnlyList<string> names, Func<int, int> address, int count = -1)
        {
            int n = count < 0 ? names.Count : Math.Min(count, names.Count);
            var lights = new List<LightInfo>(n);
            for (int i = 0; i < n; i++)
                if (Named(address(i), names[i]) is { } light) lights.Add(light);
            return lights;
        }

        /// <summary>The lights of a layout KeyboardLayoutManager built, each
        /// at its LED value (the address the family's frames use), with the
        /// place it has in the layout. A light the name of which
        /// <paramref name="isEdge"/> takes is an edge light.</summary>
        public static List<LightInfo> FromLayout(KeyboardLayoutManager layout, Func<string, bool> isEdge = null)
        {
            var lights = new List<LightInfo>(layout.GetKeyCount());
            var made = layout.Lights((_, key) => (int)key.Value);
            for (int i = 0; i < made.Count; i++)
            {
                var light = made[i];
                string name = layout.GetKeyNameAt(i);
                if (isEdge != null && light.Kind == LightKind.Other && isEdge(name))
                    light = light with { Kind = LightKind.Edge, Label = LightLabel.None, LabelNumber = 0 };
                lights.Add(light);
            }
            return lights;
        }

        /// <summary>The colors of <paramref name="lights"/> by address, in an
        /// array of <paramref name="size"/> where each light's address picks
        /// its slot. A slot no light has stays black, and an address outside
        /// the array is left out.</summary>
        public static int[] ByAddress(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors, int size)
        {
            var byAddress = new int[size];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int address = lights[i].Address;
                if ((uint)address < (uint)size) byAddress[address] = colors[i] & 0xFFFFFF;
            }
            return byAddress;
        }

        /// <summary>The model of each registration name a family has, made
        /// once, so every collection of one product answers with one
        /// model.</summary>
        internal sealed class ModelCache
        {
            private readonly Dictionary<string, HidLightingModel> _models = new(StringComparer.Ordinal);
            private readonly object _gate = new();

            public HidLightingModel Get(string key, Func<HidLightingModel> make)
            {
                lock (_gate)
                {
                    if (!_models.TryGetValue(key, out var model)) _models[key] = model = make();
                    return model;
                }
            }
        }
    }
}
