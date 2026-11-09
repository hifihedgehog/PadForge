using PadForge.Engine.Common.Mapping;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>The reads a Steam dpad or four_buttons import should take, from
    /// steamclient64.dll's numbers: the dpad's engage trigger is the dead zone
    /// (default 10000 of 32767) with 1.25 percent of hold-back each way, a
    /// default span of 67.5 degrees each side (overlap_region 4000) with a
    /// 2.7-degree margin, layout 0 45 degrees with 1.8, and the four_buttons
    /// cells sit 20000 out with an 18000 radius and 2 percent of hold-back.</summary>
    internal static class SteamPadExpect
    {
        private const double Range = 32767.0;

        public static double Press(int deadzone = 10000) => (deadzone + Range * 0.0125) / Range * 100;
        public static double Release(int deadzone = 10000) => (deadzone - Range * 0.0125) / Range * 100;

        /// <summary>The direction's bearing: east 0, north 90, west 180,
        /// south 270.</summary>
        public static string Wedge(double bearing, BoundedGate gate, string pair, int deadzone = 10000,
            int layout = 1, int overlap = 4000)
        {
            double width = layout == 0 ? 90 : 90 + 180000.0 / overlap;
            return BoundedReads.Wedge(bearing, width / 2, width / 50, Press(deadzone), Release(deadzone), gate, pair);
        }

        public static double CellPress(int size = 18000) => (size - Range * 0.02) / Range * 100;
        public static double CellRelease(int size = 18000) => (size + Range * 0.02) / Range * 100;

        /// <summary>A cell's center in percent, +Y down: north (0, -d), south
        /// (0, d), east (d, 0), west (-d, 0).</summary>
        public static string Disc(double x, double y, BoundedGate gate, string pair, int size = 18000)
            => BoundedReads.Disc(x, y, CellPress(size), CellRelease(size), gate, pair);

        public static double Dist(int dist = 20000) => dist / Range * 100;

        /// <summary>The dpad's edge: edge_binding_radius (default 25000) with
        /// a 10 percent hold-back and the 1/120 s debounce, outer or (inverted)
        /// inner, which lets go with the dpad.</summary>
        public static string Edge(string pair, double radius = 25000, bool inverted = false,
            BoundedGate gate = BoundedGate.None, int deadzone = 10000)
        {
            double h = Range * 0.10;
            const double debounce = 1000.0 / 120.0;
            return inverted
                ? BoundedReads.Ring((radius - h) / Range * 100, (radius + h) / Range * 100, true, debounce, gate, pair,
                    Press(deadzone), Release(deadzone))
                : BoundedReads.Ring((radius + h) / Range * 100, (radius - h) / Range * 100, false, debounce, gate, pair);
        }

        /// <summary>The joystick modes' edge: edge_binding_radius (default
        /// 25000) with a 3 percent hold-back and no debounce.</summary>
        public static string JoystickEdge(string pair, double radius = 25000, bool inverted = false)
        {
            double h = Range * 0.03;
            return inverted
                ? BoundedReads.Ring((radius - h) / Range * 100, (radius + h) / Range * 100, true, 0, BoundedGate.None, pair)
                : BoundedReads.Ring((radius + h) / Range * 100, (radius - h) / Range * 100, false, 0, BoundedGate.None, pair);
        }

        /// <summary>The flick stick's edge: 1000 + 31000 * (command radius -
        /// 20) / 80 with a 10 percent hold-back and no debounce.</summary>
        public static string FlickEdge(string pair, int commandRadius = 80, bool inverted = false)
        {
            double t = 1000 + 31000 * System.Math.Clamp((commandRadius - 20) / 80.0, 0, 1);
            double h = Range * 0.10;
            return inverted
                ? BoundedReads.Ring((t - h) / Range * 100, (t + h) / Range * 100, true, 0, BoundedGate.None, pair)
                : BoundedReads.Ring((t + h) / Range * 100, (t - h) / Range * 100, false, 0, BoundedGate.None, pair);
        }
    }
}
