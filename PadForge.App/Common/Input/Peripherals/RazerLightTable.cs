using System.Collections.Concurrent;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>How a Razer product is painted light by light (#557).</summary>
    internal enum RazerLightPlan : byte
    {
        /// <summary>One report per LED, each in its own light's color, by
        /// the product's family. A light's address is its LED ID.</summary>
        PerLed,
        /// <summary>A frame of <see cref="RazerLightRow.Rows"/> by
        /// <see cref="RazerLightRow.Columns"/> cells with a color each. A
        /// light's address is its row times the columns plus its column.</summary>
        Frame,
    }

    /// <summary>The lights of one Razer product and how its frames reach
    /// them (#557), as tools/gen_razer_light_table.py reads them out of the
    /// references.</summary>
    internal sealed class RazerLightRow
    {
        public ushort ProductId { get; init; }
        public RazerLightPlan Plan { get; init; }

        /// <summary>The frame a <see cref="RazerLightPlan.Frame"/> product
        /// is painted with, 0 for one painted by LED.</summary>
        public byte Rows { get; init; }
        public byte Columns { get; init; }

        /// <summary>Every light but the keys of a keyboard or keypad zone,
        /// in the order OpenRGB lists a product's LEDs, or for a product
        /// painted by LED in the order of its LEDs.</summary>
        public LightInfo[] Lights { get; init; } = System.Array.Empty<LightInfo>();

        /// <summary>The recipe OpenRGB's KeyboardLayoutManager lays the
        /// product's keyboard or keypad zone out by (the layout its
        /// RazerDevices.cpp entry points to, built by
        /// <see cref="KeyboardLayouts.Build"/>), or null for a zone whose
        /// keys no source names.</summary>
        public string KeyboardRecipe { get; init; }

        /// <summary>The keyboard or keypad zone, where the product has one
        /// whose lights are keys: the index of its first LED among OpenRGB's
        /// LEDs, its rows and columns, and the columns of the frame OpenRGB
        /// numbers its LEDs on. 0 rows for none.</summary>
        public int KeyBase { get; init; }
        public byte KeyRows { get; init; }
        public byte KeyColumns { get; init; }
        public byte DeviceColumns { get; init; }

        public bool HasKeys => KeyRows > 0;

        /// <summary>The reference the lights were read from, for the log
        /// and the tab.</summary>
        public string Source { get; init; }
    }

    /// <summary>
    /// The light maps of the Razer products that have more than one light
    /// (#557). A product it does not list is painted one color. The rows are
    /// generated into RazerLightTable.g.cs.
    /// </summary>
    internal static partial class RazerLightTable
    {
        private static readonly Dictionary<ushort, RazerLightRow> s_byProductId;
        private static readonly ConcurrentDictionary<(ushort, KeyboardLayout), LightMap> s_maps = new();

        /// <summary>Indexes the rows once every field initializer has run:
        /// the rows are in the generated file, and C# sets no order between
        /// the initializers of two files of one partial class.</summary>
        static RazerLightTable() => s_byProductId = Index();

        /// <summary>Every row, in product ID order.</summary>
        internal static IReadOnlyList<RazerLightRow> Rows => s_rows;

        private static Dictionary<ushort, RazerLightRow> Index()
        {
            var index = new Dictionary<ushort, RazerLightRow>(s_rows.Length);
            foreach (var row in s_rows) index[row.ProductId] = row;
            return index;
        }

        internal static RazerLightRow Find(ushort productId)
            => s_byProductId.TryGetValue(productId, out var row) ? row : null;

        /// <summary>The model key Map Key Lights stores learned names
        /// under (<see cref="LightMap.Product"/>).</summary>
        internal static string ProductKey(ushort productId)
            => $"razer:{RazerDirectProtocol.VendorId:X4}:{productId:X4}";

        /// <summary>The light map of a product in a region, made once and
        /// shared by every unit of it, or null for a product painted one
        /// color. A keyboard whose region is not known yet is laid out ANSI,
        /// as OpenRGB lays out one whose region it does not know
        /// (RGBController_Razer.cpp:256-257).</summary>
        internal static LightMap Map(ushort productId, KeyboardLayout region = KeyboardLayout.AnsiQwerty)
        {
            var row = Find(productId);
            if (row == null) return null;
            if (row.KeyboardRecipe == null) region = KeyboardLayout.AnsiQwerty;
            return s_maps.GetOrAdd((productId, region), _ =>
            {
                var lights = row.HasKeys ? Compose(row, region) : row.Lights;
                string source = row.KeyboardRecipe != null
                    ? $"{row.Source}, {row.KeyboardRecipe} {region}"
                    : row.Source;
                return new LightMap(lights, source) { Product = ProductKey(productId) };
            });
        }

        /// <summary>
        /// The keys of a product's keyboard or keypad zone, then its other
        /// lights. OpenRGB makes an LED of each cell of the zone, rows by
        /// columns, named by the first key the layout puts there, and an LED
        /// whose cell no key holds has no name (RGBController_Razer.cpp:272-293
        /// and KeyboardLayoutManager.cpp:933-944). Each such key is a light
        /// here at the cell OpenRGB sends it to: its LED index, the zone's
        /// first LED plus its row times the zone's columns plus its column,
        /// on a frame the device's columns wide (RazerController.cpp:207-258),
        /// which is where it sits in the frame painted. Its key is the HID
        /// usage OpenRGB's key name stands for (<see cref="OpenRgbKeys"/>), or
        /// a label for a key with no usage. A zone with no layout makes every
        /// cell a key no source names, for Map Key Lights.
        /// </summary>
        internal static LightInfo[] Compose(RazerLightRow row, KeyboardLayout region)
        {
            var layout = row.KeyboardRecipe != null ? KeyboardLayouts.Build(row.KeyboardRecipe, region) : null;
            var lights = new List<LightInfo>(row.KeyRows * row.KeyColumns + row.Lights.Length);
            for (int keyRow = 0; keyRow < row.KeyRows; keyRow++)
            {
                for (int keyCol = 0; keyCol < row.KeyColumns; keyCol++)
                {
                    string name = layout?.GetKeyNameAt(keyRow, keyCol);
                    if (layout != null && string.IsNullOrEmpty(name)) continue;
                    int index = row.KeyBase + keyRow * row.KeyColumns + keyCol;
                    int frameRow = index / row.DeviceColumns, frameCol = index % row.DeviceColumns;
                    if (frameRow >= row.Rows || frameCol >= row.Columns) continue;
                    int address = frameRow * row.Columns + frameCol;
                    LightInfo light;
                    if (layout == null)
                        light = Key(address);
                    else
                    {
                        OpenRgbKeys.TryGet(name, out ushort usage, out var label, out int number);
                        // The names Razer's layouts give lights that are no
                        // key: "Logo", "Left Underglow 0" to "Right
                        // Underglow 8", and a few others such as "Trackpad"
                        // (RazerDevices.cpp, the layouts' edit lists).
                        light = usage != 0 ? new LightInfo(address, LightKind.Key, usage, LightKeySource.Generated)
                            : name == "Logo" ? Logo(address)
                            : name.Contains("Underglow", System.StringComparison.Ordinal) ? Edge(address)
                            : new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
                    }
                    lights.Add(At(light, frameRow, frameCol));
                }
            }
            lights.AddRange(row.Lights);
            return lights.ToArray();
        }

        /// <summary>A light placed at its cell, one cell wide and high.</summary>
        private static LightInfo At(LightInfo light, int row, int col)
            => light with { X = col, Y = row, Width = 1, Height = 1, HasPosition = true };

        private static LightInfo Zone(int address, int zone)
            => new(address, LightKind.Zone, 0, LightKeySource.None) { Zone = zone };

        private static LightInfo Logo(int address) => new(address, LightKind.Logo, 0, LightKeySource.None);

        private static LightInfo Edge(int address) => new(address, LightKind.Edge, 0, LightKeySource.None);

        /// <summary>A key whose usage no source names: a keypad key, which
        /// sends what its own keymap says. Map Key Lights can name it.</summary>
        private static LightInfo Key(int address) => new(address, LightKind.Key, 0, LightKeySource.None);
    }
}
