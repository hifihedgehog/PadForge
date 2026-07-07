using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Console-style player identity defaults (#191, from discussion
    /// #126). The virtual controller's 1-based display number picks a
    /// Sony player color and a DualSense player-pip pattern, shown as
    /// the IDLE FLOOR under the existing lighting precedence: a game's
    /// passthrough writes, macro overrides, and any configured Lightbar
    /// or Player LED mode all continue to win. The floor only replaces
    /// what an untouched pad used to show (extinguished pips on the
    /// DualSense, a black-painted lightbar on the DualShock 4).
    ///
    /// Tables are byte-for-byte the shipping implementations'. Colors:
    /// Linux hid-playstation.c player_colors ("Use same player colors
    /// as PlayStation 4") and SDL_hidapi_ps5.c SetLedsForPlayerIndex,
    /// which extend the canonical four with orange / teal / white for
    /// players 5-7. Pips: SDL_hidapi_ps5.c SetLightsForPlayerIndex and
    /// hid-playstation.c dualsense_set_player_leds (centered patterns),
    /// which dualsense-tester's PlayerLedControl matches. Numbers past
    /// 7 wrap, mirroring SDL's modulo behavior.
    /// </summary>
    internal static class PlayerIdentityDefaults
    {
        private static readonly (byte R, byte G, byte B)[] Colors =
        {
            (0x00, 0x00, 0x40), // P1 blue
            (0x40, 0x00, 0x00), // P2 red
            (0x00, 0x40, 0x00), // P3 green
            (0x20, 0x00, 0x20), // P4 pink
            (0x20, 0x10, 0x00), // P5 orange
            (0x00, 0x10, 0x10), // P6 teal
            (0x10, 0x10, 0x10), // P7 white
        };

        private static readonly byte[] Pips =
            { 0x04, 0x0A, 0x15, 0x1B, 0x1F, 0x11, 0x0E };

        /// <summary>Sony player color for a 1-based virtual controller
        /// number. Numbers past 7 wrap like SDL's player index does.</summary>
        public static (byte R, byte G, byte B) ColorFor(int playerNumber)
            => Colors[Wrap(playerNumber)];

        /// <summary>DualSense player-pip bits for a 1-based virtual
        /// controller number, WITHOUT the no-fade flag (the caller ORs
        /// the DS5 synthesizer's PlayerIndicatorNoFade).</summary>
        public static byte PipsFor(int playerNumber)
            => Pips[Wrap(playerNumber)];

        private static int Wrap(int playerNumber)
            => playerNumber <= 0 ? 0 : (playerNumber - 1) % 7;
    }
}
