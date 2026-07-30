using System;

namespace PadForge.Engine.Menus
{
    /// <summary>
    /// Pure selection math for radial / grid menus. The radial convention
    /// is the SHIPPED #88 radial-zone convention (GestureRecognizer.
    /// DetectRadialZones): 0 degrees = up, angles increase clockwise, N
    /// equal wedges of 360/N degrees, wedge k centered at k * 360/N. The
    /// same wedge shape is sc-controller's proven radial menu
    /// (scc/osd/radial_menu.py: angle = atan2(x, y) from up, item hit when
    /// |degdiff(item.a, angle)| &lt; 180/n with item.a = 360/n * index).
    /// Inputs are in the SDL frame both sticks and touchpads use: +X
    /// right, +Y down.
    /// </summary>
    public static class MenuSelectionMath
    {
        /// <summary>Ring slot for a deflection vector, or the center.
        /// Returns 0 for the center (inside <paramref name="deadzone"/>,
        /// only when <paramref name="hasCenter"/>), -1 for "nothing
        /// hovered" (center without a center item, or no ring slots), and
        /// 1..<paramref name="ringSlots"/> for ring cells clockwise from
        /// the top. Steam's radial serialization is exactly this indexing:
        /// touch_menu_button_0 is the center button
        /// ("ControllerBinding_RadialMenuButton0" = "Radial Menu Center
        /// Button"), 1..N are the ring.</summary>
        public static int RadialIndexFromVector(double dx, double dy, int ringSlots,
            bool hasCenter, double deadzone)
        {
            double mag = Math.Sqrt(dx * dx + dy * dy);
            if (mag < deadzone || ringSlots <= 0)
                return hasCenter ? 0 : -1;

            // Verbatim shipped zone math (GestureRecognizer.DetectRadialZones):
            // +Y is down, atan2(dy,dx) is CW-from-+X, +PI/2 anchors zone 0 up.
            double ang = Math.Atan2(dy, dx) + Math.PI / 2.0;
            if (ang < 0) ang += 2.0 * Math.PI;
            double zoneWidth = 2.0 * Math.PI / ringSlots;
            int zone = (int)Math.Floor((ang + zoneWidth / 2.0) / zoneWidth) % ringSlots;
            return zone + 1;
        }

        /// <summary>Grid shape for a cell count: near-square, wider than
        /// tall, matching Steam's rectangular counts (2 = 2x1, 4 = 2x2,
        /// 9 = 3x3, 12 = 4x3, 16 = 4x4). Steam's hex arrangements
        /// (5 / 7 / 13) are rendered rectangular here (3x2 / 3x3 / 4x4
        /// with trailing empty cells), a named approximation.</summary>
        public static (int Columns, int Rows) GridShape(int cellCount)
        {
            if (cellCount <= 0) return (0, 0);
            int cols = (int)Math.Ceiling(Math.Sqrt(cellCount));
            int rows = (int)Math.Ceiling(cellCount / (double)cols);
            return (cols, rows);
        }

        /// <summary>Cell index for a normalized position (0..1, top-left
        /// origin) on a <paramref name="cellCount"/> grid. Positions past
        /// the last (partial) row clamp to the last cell so the whole
        /// surface always selects something, and out-of-range counts
        /// return -1.</summary>
        public static int GridIndexFromPosition(double nx, double ny, int cellCount)
        {
            var (cols, rows) = GridShape(cellCount);
            if (cols <= 0) return -1;
            int col = (int)Math.Floor(Clamp01(nx) * cols);
            if (col >= cols) col = cols - 1;
            int row = (int)Math.Floor(Clamp01(ny) * rows);
            if (row >= rows) row = rows - 1;
            int idx = row * cols + col;
            return idx >= cellCount ? cellCount - 1 : idx;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
