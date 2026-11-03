using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>A keyboard OpenRGB lays out as a grid of its own (#557):
    /// the grid, each cell an index into the LEDs or -1, the LED count and,
    /// where OpenRGB names them, the LEDs' names.</summary>
    internal sealed record MatrixLayout(string Source, int Rows, int Cols, int Count, short[] Grid, string[] Names);

    /// <summary>
    /// The grids of OpenRGB's Cherry, EVision and Das Keyboard families,
    /// generated into MatrixKeyboardLayouts.g.cs by
    /// tools/gen_openrgb_matrix_layouts.py, and the lights each one has.
    /// </summary>
    internal static partial class MatrixKeyboardLayouts
    {
        /// <summary>
        /// The lights of a layout, each at the address of its LED index and
        /// placed at its cell of the grid. A LED OpenRGB names after a key is
        /// lit by that key's usage (<see cref="OpenRgbKeys"/>), and one it
        /// names otherwise carries the label OpenRgbKeys gives it. A LED in
        /// the grid that OpenRGB does not name, as on the Cherry and the
        /// first EVision keyboards, whose LEDs are "Keyboard LED n"
        /// (RGBController_CherryKeyboard.cpp:333-341,
        /// RGBController_EVisionKeyboard.cpp:323-331), is a key no source
        /// names yet, which Map Key Lights can name. A LED OpenRGB marks
        /// unused, or one outside the grid with no name, is a light of no
        /// key.
        /// </summary>
        public static IReadOnlyList<LightInfo> Lights(MatrixLayout layout)
        {
            var cellOf = new int[layout.Count];
            for (int i = 0; i < cellOf.Length; i++) cellOf[i] = -1;
            for (int cell = 0; cell < layout.Grid.Length; cell++)
                if (layout.Grid[cell] >= 0 && cellOf[layout.Grid[cell]] < 0) cellOf[layout.Grid[cell]] = cell;
            var lights = new List<LightInfo>(layout.Count);
            for (int i = 0; i < layout.Count; i++)
            {
                string name = layout.Names?[i];
                LightInfo light;
                if (name == null)
                    light = cellOf[i] >= 0
                        ? new LightInfo(i, LightKind.Key, 0, LightKeySource.None)
                        : new LightInfo(i, LightKind.Other, 0, LightKeySource.None);
                else if (OpenRgbKeys.TryGet(name, out ushort usage, out var label, out int number) && usage != 0)
                    light = new LightInfo(i, LightKind.Key, usage, LightKeySource.Generated);
                else
                    light = new LightInfo(i, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
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
