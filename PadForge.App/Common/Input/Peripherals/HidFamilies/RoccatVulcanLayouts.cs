using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>One LED of a Roccat Vulcan layout (led_value,
    /// RoccatVulcanKeyboardLayouts.h:30-34): OpenRGB's name for it and its
    /// value in the keyboard's frames. <see cref="Under"/> is the US key a
    /// Japanese legend sits on, which tools/gen_roccat_vulcan_layouts.py
    /// reads from the US grid of the same table.</summary>
    internal sealed record RoccatVulcanLed(string Name, byte Id)
    {
        public string Under { get; init; }
    }

    /// <summary>One region of one of OpenRGB's Roccat Vulcan tables
    /// (layout_info, RoccatVulcanKeyboardLayouts.h:36-43): the grid, each
    /// cell an index into <see cref="Leds"/> or -1, and the LEDs in
    /// OpenRGB's order.</summary>
    internal sealed record RoccatVulcanLayout(string Table, int Region, int Rows, int Cols, short[] Grid,
                                              string Source, RoccatVulcanLed[] Leds);

    /// <summary>
    /// The layouts of the Roccat Vulcan keyboards (#557), generated from
    /// OpenRGB into RoccatVulcanLayouts.g.cs, and the lights each one has.
    /// OpenRGB picks the table by product ID and the region by the layout
    /// byte the keyboard reports (RGBController_RoccatVulcanKeyboard.cpp:106-163):
    /// German, UK and French boards take the UK entry, a Japanese board the
    /// Japanese entry, every other value the US entry.
    /// </summary>
    internal static partial class RoccatVulcanLayouts
    {
        /// <summary>The table OpenRGB gives a product
        /// (RGBController_RoccatVulcanKeyboard.cpp:110-144).</summary>
        public static string TableOf(ushort vendorId, ushort productId)
        {
            if (vendorId == RoccatVulcanLighting.TurtleBeachVendorId)
                return productId switch
                {
                    RoccatVulcanLighting.TurtleBeachVulcanIITkl => "RoccatVulcanTKLLayouts",
                    RoccatVulcanLighting.TurtleBeachVulcanIITklPro => "TurtleBeachVulcanIITKLProLayouts",
                    RoccatVulcanLighting.TurtleBeachVulcanII => "RoccatVulcanIILayouts",
                    _ => "RoccatVulcan120AimoLayouts",
                };
            return productId switch
            {
                RoccatVulcanLighting.Vulcan100Aimo or RoccatVulcanLighting.Vulcan120Aimo => "RoccatVulcan120AimoLayouts",
                RoccatVulcanLighting.VulcanTkl or RoccatVulcanLighting.VulcanTklPro => "RoccatVulcanTKLLayouts",
                RoccatVulcanLighting.VulcanPro or RoccatVulcanLighting.Pyro => "RoccatPyroLayouts",
                RoccatVulcanLighting.VulcanII => "RoccatVulcanIILayouts",
                RoccatVulcanLighting.VulcanIIMax => "RoccatVulcanIIMaxLayouts",
                RoccatVulcanLighting.VulcanIIMini => "RoccatVulcanIIMiniLayouts",
                RoccatVulcanLighting.Magma or RoccatVulcanLighting.MagmaMini => "RoccatMagmaLayouts",
                _ => "RoccatVulcan120AimoLayouts",
            };
        }

        /// <summary>The region entry OpenRGB looks up for a reported layout
        /// byte (RGBController_RoccatVulcanKeyboard.cpp:148-163).</summary>
        public static int RegionOf(int reported) => reported switch
        {
            LayoutDe or LayoutUk or LayoutFr => LayoutUk,
            LayoutJp => LayoutJp,
            _ => LayoutUs,
        };

        /// <summary>
        /// The entry a keyboard of <paramref name="table"/> lights by, for
        /// the layout byte it reported. Two tables hold a UK entry only (the
        /// Vulcan II and the Turtle Beach Vulcan II TKL Pro). For a US or
        /// Japanese board OpenRGB's std::map lookup then makes an empty entry
        /// and the keyboard gets no LEDs at all (line 172 with operator[]),
        /// so here such a board takes the table's own entry, whose LED
        /// values are the keyboard's whatever the legends say.
        /// </summary>
        public static RoccatVulcanLayout Find(string table, int reported)
        {
            int region = RegionOf(reported);
            RoccatVulcanLayout first = null;
            foreach (var layout in All)
            {
                if (layout.Table != table) continue;
                if (layout.Region == region) return layout;
                first ??= layout;
            }
            return first;
        }

        /// <summary>The keys a Vulcan II Max lights a second time, with a LED
        /// of their own under the key (RGBController_RoccatVulcanKeyboard.cpp:166-170),
        /// are named "Key: F1 LED 2" and the like: the key's own name and
        /// this.</summary>
        internal const string SecondLed = " LED 2";

        /// <summary>The palm rest's LEDs of the Vulcan II Max.</summary>
        internal const string PalmRest = "Palm Rest LED ";

        /// <summary>
        /// The lights of a layout in OpenRGB's order, each at the address of
        /// its LED value, placed at its cell of the grid. A key OpenRGB names
        /// is lit by its usage (<see cref="OpenRgbKeys"/>), a Japanese legend
        /// by the US key it sits on, and a key's second LED by the key's
        /// usage too, since it sits under that key. The palm rest is edge
        /// lighting. The Magma's five LEDs are its zones (OpenRGB's comment:
        /// it "doesn't have per-led lighting", RoccatVulcanKeyboardController.cpp:117-125),
        /// numbered from the left. An unused LED is a light of no key.
        /// </summary>
        public static IReadOnlyList<LightInfo> Lights(RoccatVulcanLayout layout)
        {
            var cellOf = new int[layout.Leds.Length];
            Array.Fill(cellOf, -1);
            for (int cell = 0; cell < layout.Grid.Length; cell++)
                if (layout.Grid[cell] >= 0 && cellOf[layout.Grid[cell]] < 0) cellOf[layout.Grid[cell]] = cell;
            bool magma = layout.Table == "RoccatMagmaLayouts";
            var lights = new List<LightInfo>(layout.Leds.Length);
            for (int i = 0; i < layout.Leds.Length; i++)
            {
                var led = layout.Leds[i];
                string name = led.Under ?? led.Name;
                if (name.StartsWith("Key: ", StringComparison.Ordinal) && name.EndsWith(SecondLed, StringComparison.Ordinal))
                    name = name[..^SecondLed.Length];
                LightInfo light;
                if (magma)
                    light = new LightInfo(led.Id, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
                else if (name.StartsWith(PalmRest, StringComparison.Ordinal))
                    light = new LightInfo(led.Id, LightKind.Edge, 0, LightKeySource.None);
                else if (OpenRgbKeys.TryGet(name, out ushort usage, out var label, out int number) && usage != 0)
                    light = new LightInfo(led.Id, LightKind.Key, usage, LightKeySource.Generated);
                else if (name.Length == 0)
                    light = new LightInfo(led.Id, LightKind.Other, 0, LightKeySource.None);
                else
                    light = new LightInfo(led.Id, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
                if (cellOf[i] >= 0)
                    light = light with
                    {
                        X = cellOf[i] % layout.Cols, Y = cellOf[i] / layout.Cols, Width = 1, Height = 1, HasPosition = true,
                    };
                lights.Add(light);
            }
            return lights;
        }
    }
}
