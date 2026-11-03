using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The lights of each Chroma REST category and the custom effect that
    /// colors them one by one (#557). The grids, the LED counts and the key
    /// at each keyboard cell come from the Chroma SDK's types header
    /// (RzChromaSDKTypes.h at <see cref="SampleCommit"/>, generated into
    /// SdkKeyTables.g.cs). The JSON forms are the REST documentation's
    /// (assets.razerzone.com/dev_portal/REST/html: "Create effects for
    /// Keyboards", "for Mice", "for Headsets", "for Keypads", "for Chroma
    /// Linked devices") and Colore's (Serialization/*CustomConverter.cs),
    /// which agree: {"effect": name, "param": colors}, each color an
    /// integer in BGR.
    ///
    /// <para>The keyboard takes CHROMA_CUSTOM_KEY: a 6 by 22 "color" canvas
    /// painted cell by cell where the cell sits, and a 6 by 22 "key" array
    /// whose cells carry 0x01000000 with the color of the key at that cell
    /// in the generic layout. The REST page: "Specify a bit mask of
    /// 0x01000000 (16777216) with the color value for each keys that has an
    /// effect", and Razer's Chroma FAQ: "With CHROMA_CUSTOM_KEY, the
    /// developer is guaranteed that effect appears the same for all keyboard
    /// layouts", where a CHROMA_CUSTOM canvas lands wherever each model puts
    /// the cell (Colore CustomKeyboardEffect.cs:205-219 sets the same flag).
    /// The canvas carries every light's color too, at the cell the header
    /// gives it, as RGB.NET paints its canvas (Generic/LedMappings.cs), so a
    /// cell a key effect does not reach still shows its own color and every
    /// other cell shows the background.</para>
    ///
    /// <para>The mouse takes CHROMA_CUSTOM2, a 9 by 7 grid whose cells the
    /// RZLED2 enum names (the header's own note: "Row = HIBYTE(RZLED2_...),
    /// Column = LOBYTE(...)"). The headset, mousepad and Chroma Link take
    /// CHROMA_CUSTOM with one color per LED, and the keypad CHROMA_CUSTOM
    /// with a 4 by 5 grid. WyvrnChroma's ChromaGrid.cs gives the same sizes
    /// for each.</para>
    /// </summary>
    internal static partial class ChromaLayouts
    {
        /// <summary>The flag on a key cell that names a key effect
        /// (RzChromaSDKTypes.h:383, the REST keyboard page).</summary>
        internal const int KeyFlag = 0x01000000;

        // Built on first use: a static initializer here could run before the
        // generated part's tables, since C# orders initializers across
        // partial files in no set way.
        private static readonly Lazy<Dictionary<string, LightMap>> s_maps = new(BuildMaps);

        /// <summary>The lights of a category, or null for one the session
        /// does not know.</summary>
        internal static LightMap MapOf(string category)
            => category != null && s_maps.Value.TryGetValue(category, out var map) ? map : null;

        /// <summary>Every category with its lights, for the worker to
        /// publish.</summary>
        internal static IEnumerable<KeyValuePair<string, LightMap>> Maps => s_maps.Value;

        private static Dictionary<string, LightMap> BuildMaps() => new(StringComparer.Ordinal)
        {
            ["keyboard"] = new LightMap(KeyboardLights(), "Chroma SDK RzChromaSDKTypes.h, RZKEY grid"),
            ["mouse"] = new LightMap(MouseLights(), "Chroma SDK RzChromaSDKTypes.h, RZLED2 grid"),
            ["headset"] = new LightMap(Zones(HeadsetLeds), "Chroma SDK RzChromaSDKTypes.h, headset LEDs"),
            ["mousepad"] = new LightMap(Zones(MousepadLeds), "Chroma SDK RzChromaSDKTypes.h, mousepad LEDs"),
            ["keypad"] = new LightMap(Zones(KeypadRows * KeypadColumns), "Chroma SDK RzChromaSDKTypes.h, keypad grid"),
            ["chromalink"] = new LightMap(Zones(ChromaLinkLeds), "Chroma SDK RzChromaSDKTypes.h, Chroma Link LEDs"),
        };

        /// <summary>A keyboard cell's index in the grid, row by row.</summary>
        internal static int KeyboardCell(ushort code) => (code >> 8) * KeyboardColumns + (code & 0xFF);

        /// <summary>A mouse cell's index in the grid, row by row.</summary>
        internal static int MouseCell(ushort code) => (code >> 8) * MouseColumns + (code & 0xFF);

        /// <summary>
        /// Every keyboard light, in cell order, its cell as its address: a
        /// key by its usage, the macro keys M1 to M5 by number, Fn by its
        /// label and RZLED_LOGO as the logo. The macro keys carry the G-key
        /// label with their number, as Aurora names them G1 to G5
        /// (Modules/Razer/RazerLayoutMap.cs:35-119).
        /// </summary>
        private static IReadOnlyList<LightInfo> KeyboardLights()
        {
            var lights = new List<LightInfo>(KeyboardKeys.Length + 1);
            foreach (var key in KeyboardKeys)
            {
                int cell = KeyboardCell(key.Code);
                if (key.Usage != 0)
                    lights.Add(new LightInfo(cell, LightKind.Key, key.Usage, LightKeySource.Vendor));
                else if (key.Name == "RZKEY_FN")
                    lights.Add(new LightInfo(cell, LightKind.Other, 0, LightKeySource.Vendor) { Label = LightLabel.Fn });
                else if (key.Name.StartsWith("RZKEY_MACRO", StringComparison.Ordinal))
                    lights.Add(new LightInfo(cell, LightKind.Other, 0, LightKeySource.Vendor)
                    {
                        Label = LightLabel.GKey,
                        LabelNumber = key.Name[^1] - '0',
                    });
                else
                    throw new InvalidOperationException("RZKEY without a light: " + key.Name);
            }
            lights.Add(new LightInfo(KeyboardCell(KeyboardLogo), LightKind.Logo, 0, LightKeySource.None));
            lights.Add(new LightInfo(KeyboardCell(PreviousTrack), LightKind.Other, 0, LightKeySource.Vendor)
            {
                Label = LightLabel.Previous,
            });
            lights.Add(new LightInfo(KeyboardCell(PlayPause), LightKind.Other, 0, LightKeySource.Vendor)
            {
                Label = LightLabel.PlayPause,
            });
            lights.Sort((a, b) => a.Address.CompareTo(b.Address));
            return lights;
        }

        /// <summary>The two media keys the header leaves unnamed, in the
        /// cells RGB.NET and Aurora both give them: previous track at row 0,
        /// column 18, and play at column 19 (RGB.NET.Devices.Razer
        /// Generic/LedMappings.cs:43-44, a row and a column in on its 8 by 24
        /// grid, and Aurora Modules/Razer/RazerLayoutMap.cs:27-28). The two
        /// cells after them hold the logo and the yen key in the header, where
        /// RGB.NET has next track and mute and Aurora next track at
        /// column 21, so they keep the header's lights.</summary>
        internal const ushort PreviousTrack = 0x0012;
        internal const ushort PlayPause = 0x0013;

        /// <summary>
        /// Every mouse light, in cell order: the scroll wheel zone 1 and the
        /// backlight zone 2, the logo, and the side and bottom strips as edge
        /// lights (RZLED2, RzChromaSDKTypes.h:491-515).
        /// </summary>
        private static IReadOnlyList<LightInfo> MouseLights()
        {
            var lights = new List<LightInfo>(MouseLeds.Length);
            foreach (var led in MouseLeds)
            {
                int cell = MouseCell(led.Code);
                lights.Add(led.Name switch
                {
                    "RZLED2_SCROLLWHEEL" => new LightInfo(cell, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 },
                    "RZLED2_BACKLIGHT" => new LightInfo(cell, LightKind.Zone, 0, LightKeySource.None) { Zone = 2 },
                    "RZLED2_LOGO" => new LightInfo(cell, LightKind.Logo, 0, LightKeySource.None),
                    _ => new LightInfo(cell, LightKind.Edge, 0, LightKeySource.None),
                });
            }
            lights.Sort((a, b) => a.Address.CompareTo(b.Address));
            return lights;
        }

        /// <summary><paramref name="count"/> zones numbered from 1, each
        /// addressed by its index in the effect's array.</summary>
        private static IReadOnlyList<LightInfo> Zones(int count)
        {
            var lights = new LightInfo[count];
            for (int i = 0; i < count; i++)
                lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
            return lights;
        }

        /// <summary>0x00RRGGBB to the Chroma BGR integer,
        /// R + (G &lt;&lt; 8) + (B &lt;&lt; 16).</summary>
        internal static int ToBgr(int rgb)
            => ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16);

        /// <summary>CHROMA_STATIC in one color, the body the worker sends a
        /// category with no light colors.</summary>
        internal static string StaticBody(int rgb)
            => "{\"effect\":\"CHROMA_STATIC\",\"param\":{\"color\":"
               + ToBgr(rgb).ToString(CultureInfo.InvariantCulture) + "}}";

        /// <summary>
        /// The effect that shows <paramref name="rgb"/> with
        /// <paramref name="keys"/> on a category: CHROMA_STATIC when there
        /// are no light colors or the category has a single light, else the
        /// category's custom effect with each light's color
        /// (<see cref="LightRenderer.Render"/>) and the background on every
        /// cell no light holds.
        /// </summary>
        internal static string EffectBody(string category, int rgb, KeyFrame keys)
        {
            var map = MapOf(category);
            if (map == null || !map.PerLight || keys == null || keys.IsEmpty) return StaticBody(rgb);
            var colors = LightRenderer.Render(map, rgb, keys);
            int fill = LightRenderer.Fill(rgb, keys);
            return category switch
            {
                "keyboard" => KeyboardBody(map, colors, fill),
                "mouse" => GridBody("CHROMA_CUSTOM2", map, colors, fill, MouseRows, MouseColumns),
                "keypad" => GridBody("CHROMA_CUSTOM", map, colors, fill, KeypadRows, KeypadColumns),
                _ => ListBody(colors),
            };
        }

        /// <summary>The cells the RZKEY enum names, the ones a key effect
        /// can address.</summary>
        private static readonly Lazy<HashSet<int>> s_keyCells = new(() =>
        {
            var cells = new HashSet<int>();
            foreach (var key in KeyboardKeys) cells.Add(KeyboardCell(key.Code));
            return cells;
        });

        /// <summary>CHROMA_CUSTOM_KEY: the canvas with each light's color at
        /// its cell and the background elsewhere, and the key array with the
        /// flag and the color at the cell of every RZKEY key. The logo and
        /// the two media keys are no RZKEY code, so they ride on the canvas
        /// alone.</summary>
        private static string KeyboardBody(LightMap map, int[] colors, int fill)
        {
            var canvas = Canvas(map, colors, fill, KeyboardRows * KeyboardColumns);
            var key = new int[KeyboardRows * KeyboardColumns];
            var keyCells = s_keyCells.Value;
            for (int i = 0; i < colors.Length; i++)
            {
                int cell = map.Lights[i].Address;
                if (keyCells.Contains(cell)) key[cell] = KeyFlag | ToBgr(colors[i]);
            }
            var sb = new StringBuilder(4096);
            sb.Append("{\"effect\":\"CHROMA_CUSTOM_KEY\",\"param\":{\"color\":");
            AppendGrid(sb, canvas, KeyboardRows, KeyboardColumns);
            sb.Append(",\"key\":");
            AppendGrid(sb, key, KeyboardRows, KeyboardColumns);
            sb.Append("}}");
            return sb.ToString();
        }

        private static string GridBody(string effect, LightMap map, int[] colors, int fill, int rows, int columns)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"effect\":\"").Append(effect).Append("\",\"param\":");
            AppendGrid(sb, Canvas(map, colors, fill, rows * columns), rows, columns);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>CHROMA_CUSTOM for a category whose lights are its whole
        /// array, in order.</summary>
        private static string ListBody(int[] colors)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"effect\":\"CHROMA_CUSTOM\",\"param\":[");
            for (int i = 0; i < colors.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ToBgr(colors[i]).ToString(CultureInfo.InvariantCulture));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>Each cell's BGR color: its light's, or the background
        /// for a cell no light holds.</summary>
        private static int[] Canvas(LightMap map, int[] colors, int fill, int cells)
        {
            var canvas = new int[cells];
            Array.Fill(canvas, ToBgr(fill));
            for (int i = 0; i < colors.Length; i++)
                canvas[map.Lights[i].Address] = ToBgr(colors[i]);
            return canvas;
        }

        private static void AppendGrid(StringBuilder sb, int[] cells, int rows, int columns)
        {
            sb.Append('[');
            for (int r = 0; r < rows; r++)
            {
                if (r > 0) sb.Append(',');
                sb.Append('[');
                for (int c = 0; c < columns; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(cells[r * columns + c].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(']');
            }
            sb.Append(']');
        }
    }
}
