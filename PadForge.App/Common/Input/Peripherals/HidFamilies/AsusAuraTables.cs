using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One layout of an ASUS Aura keyboard line, a layout_info of
    /// AsusAuraKeyboardLayouts.h:56-63: the region key it is stored under
    /// (the ASUS_TUF_K7_LAYOUT_ enum, lines 19-46), its matrix of LED
    /// indexes, rows by columns with NA for no LED, and its LEDs as OpenRGB's
    /// name and the LED value the per-key frame carries.</summary>
    internal sealed record AsusKeyboardLayout(int Region, uint[] Matrix, int Size, int Rows, int Cols,
                                              (string Name, byte Id)[] Leds);

    /// <summary>One std::map&lt;int,layout_info&gt; of AsusAuraKeyboardLayouts.h:
    /// a keyboard line's layouts by region.</summary>
    internal sealed record AsusKeyboardLayoutSet(string Name, string Source, AsusKeyboardLayout[] Layouts)
    {
        /// <summary>The layout stored under <paramref name="region"/>, or
        /// null where the map has none.</summary>
        public AsusKeyboardLayout Find(int region)
        {
            foreach (var layout in Layouts)
                if (layout.Region == region) return layout;
            return null;
        }
    }

    /// <summary>One mouse_type of aura_mouse_devices
    /// (AsusAuraMouseDevices.h:86-97): speed and brightness bounds, whether
    /// it is wireless, its version protocol, whether it has a mode of its own
    /// for per-LED color (direct), its zones and its mode list, in which the
    /// place of a mode is the number the device takes for it.</summary>
    internal sealed record AsusMouseDevice(int ProductId, byte SpeedMin, byte SpeedMax, byte BrightnessMin,
                                           byte BrightnessMax, bool Wireless, int VersionProtocol, bool Direct,
                                           byte[] Zones, byte[] Modes);

    /// <summary>One led_map of aura_mouse_led_maps
    /// (AsusAuraMouseDevices.h:699-703): the LED count of the direct frame
    /// and each zone's LEDs in it.</summary>
    internal sealed record AsusMouseLedMap(int ProductId, int LedCount, (byte Zone, byte[] Leds)[] Zones)
    {
        /// <summary>The LEDs of <paramref name="zone"/>, or none: std::map's
        /// operator[] on a missing key gives an empty list
        /// (AsusAuraMouseController.cpp:231).</summary>
        public byte[] LedsOf(int zone)
        {
            foreach (var (z, leds) in Zones)
                if (z == zone) return leds;
            return Array.Empty<byte>();
        }
    }

    /// <summary>The tables of OpenRGB's ASUS Aura keyboard, mouse and
    /// Cerberus controllers (#557), generated into AsusAuraTables.g.cs by
    /// tools/gen_asus_aura_tables.py at <see cref="OpenRgbCommit"/>.</summary>
    internal static partial class AsusAuraTables
    {
        /// <summary>The region keys OpenRGB falls back to
        /// (ASUS_TUF_K7_LAYOUT_UK and _US, AsusAuraKeyboardLayouts.h:24 and
        /// 37).</summary>
        internal const int RegionUk = 4;
        internal const int RegionUs = 17;

        /// <summary>A keyboard line's map by OpenRGB's name for it, or
        /// null.</summary>
        public static AsusKeyboardLayoutSet KeyboardSet(string name)
        {
            foreach (var set in KeyboardSets)
                if (set.Name == name) return set;
            return null;
        }

        /// <summary>The mouse_type of a product ID, or null where
        /// aura_mouse_devices has none.</summary>
        public static AsusMouseDevice Mouse(int productId)
        {
            foreach (var mouse in Mice)
                if (mouse.ProductId == productId) return mouse;
            return null;
        }

        /// <summary>The led_map of a product ID, or null where
        /// aura_mouse_led_maps has none.</summary>
        public static AsusMouseLedMap MouseLedMap(int productId)
        {
            foreach (var map in MouseLedMaps)
                if (map.ProductId == productId) return map;
            return null;
        }

        /// <summary>Each LED's cell in a matrix, rows by columns, as
        /// (row, column) by LED index, or (-1, -1) for an LED no cell
        /// holds.</summary>
        public static (int Row, int Col)[] Cells(uint[] matrix, int rows, int cols, int leds)
        {
            var cells = new (int, int)[leds];
            Array.Fill(cells, (-1, -1));
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    uint index = matrix[r * cols + c];
                    if (index < (uint)leds && cells[index].Item1 < 0) cells[index] = (r, c);
                }
            return cells;
        }

        /// <summary>The light of an LED by OpenRGB's name for it: a key by
        /// its HID usage where <see cref="OpenRgbKeys"/> knows the name, a
        /// logo for "Logo" and "Logo n", an edge light for the underglow, a
        /// zone for "Keyboard LED n", the TUF K1 and K5's five zones
        /// (AsusAuraKeyboardLayouts.h:3732-3736), and for any other name a
        /// key no source named, with the label its name has.
        /// <paramref name="legendUsage"/> is the key of a national legend
        /// OpenRgbKeys does not hold, 0 for none.</summary>
        public static LightInfo LightOf(string name, int address, ushort legendUsage = 0)
        {
            if (OpenRgbKeys.TryGet(name, out ushort usage, out var label, out int number) && usage != 0)
                return new LightInfo(address, LightKind.Key, usage, LightKeySource.Generated);
            if (label != LightLabel.None)
                return new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
            if (name != null)
            {
                if (name == "Logo" || name.StartsWith("Logo ", StringComparison.Ordinal))
                    return new LightInfo(address, LightKind.Logo, 0, LightKeySource.None);
                if (name.StartsWith("Underglow", StringComparison.Ordinal))
                    return new LightInfo(address, LightKind.Edge, 0, LightKeySource.None);
                const string zone = "Keyboard LED ";
                if (name.StartsWith(zone, StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(zone.Length), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int n))
                    return new LightInfo(address, LightKind.Zone, 0, LightKeySource.None) { Zone = n };
            }
            return legendUsage != 0
                ? new LightInfo(address, LightKind.Key, legendUsage, LightKeySource.Generated)
                : new LightInfo(address, LightKind.Key, 0, LightKeySource.None);
        }
    }
}
