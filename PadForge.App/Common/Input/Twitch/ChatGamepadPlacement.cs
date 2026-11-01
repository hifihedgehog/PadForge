using System.Globalization;
using PadForge.Engine.Common;

namespace PadForge.Common.Input.Twitch
{
    /// <summary>
    /// Where the Twitch Chat row's usual words sit in SDL's gamepad layout
    /// (issue #555), so assigning the row to a slot maps "!a" to A, "!lt" to
    /// the left trigger and "!up" to D-pad up, through the default mapping's
    /// placement path, the one Bliss-Box ports use. A usual word the streamer
    /// removed binds nothing, and a word the streamer added is mapped by
    /// hand.
    /// </summary>
    internal static class ChatGamepadPlacement
    {
        // Each button word's position, GamepadObjectNames.Button's order.
        private static readonly (string Word, int Position)[] s_buttons =
        {
            ("a", 0), ("b", 1), ("x", 2), ("y", 3), ("lb", 4), ("rb", 5),
            ("select", 6), ("start", 7), ("ls", 8), ("rs", 9),
        };

        // The triggers, pressed all the way, GamepadObjectNames.Axis's order.
        private static readonly (string Word, int Position)[] s_triggers = { ("lt", 2), ("rt", 5) };

        // GamepadPlacement.DPadButton's order.
        private static readonly string[] s_dpad = { "up", "down", "left", "right" };

        /// <summary>The placement for the words the list holds now.</summary>
        public static GamepadPlacement For()
        {
            var buttons = new string[GamepadPlacement.ButtonPositions];
            var axes = new string[GamepadPlacement.AxisPositions];
            var dpad = new string[s_dpad.Length];
            foreach (var (word, position) in s_buttons) buttons[position] = Source(word);
            foreach (var (word, position) in s_triggers) axes[position] = Source(word);
            for (int i = 0; i < s_dpad.Length; i++) dpad[i] = Source(s_dpad[i]);
            return new GamepadPlacement(buttons, axes, dpad: false, pressureAxes: null, dpadButtons: dpad);
        }

        private static string Source(string word)
        {
            int button = ChatCommandRegistry.ButtonFor(word);
            return button > 0 ? "Button " + button.ToString(CultureInfo.InvariantCulture) : null;
        }
    }
}
