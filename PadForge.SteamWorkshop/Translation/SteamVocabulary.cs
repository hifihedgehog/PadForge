using System;
using System.Text;

namespace PadForge.SteamWorkshop.Translation
{
    /// <summary>
    /// <para>Steam's config format is written in wire tokens: SCREAMING_SNAKE
    /// for verbs and keys (<c>SET_LED</c>, <c>LEFT_CONTROL</c>), lower_snake
    /// for slots and inputs (<c>right_joystick</c>, <c>dpad_east</c>). That
    /// vocabulary is correct in the file and wrong in front of a person, and
    /// the translator emits plenty of strings a person reads: macro names and
    /// shift-layer names written into the imported PROFILE, plus the report
    /// lines the browse dialog renders.</para>
    /// <para>This is the one place that converts. Slot tokens go through
    /// <see cref="PhysicalSlotResolver.SlotDisplayName(string)"/> and key
    /// tokens through <see cref="SteamInputVkTable.KeyDisplayName"/>, both of
    /// which name the thing the way the rest of the app names it. Everything
    /// else falls back to <see cref="SpellToken"/>, which only fixes the
    /// casing and separators and never guesses at meaning.</para>
    /// </summary>
    public static class SteamVocabulary
    {
        /// <summary>A Steam token as words: "LEFT_CONTROL" becomes "Left
        /// Control", "dpad_east" becomes "Dpad East", "KEYPAD_5" becomes
        /// "Keypad 5". The underscore already marks the word boundaries, so
        /// this only restores the casing. A single character (a letter or
        /// digit key) and an already-mixed-case word keep their shape.</summary>
        public static string SpellToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return token;
            var words = token.Trim().Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder(token.Length + 2);
            foreach (var w in words)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w.Length == 1 || w.ToUpperInvariant() != w
                    ? w
                    : char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant());
            }
            return sb.ToString();
        }


        /// <summary><para>A Steam controller_action verb as a phrase a person
        /// reads: <c>MOUSE_POSITION</c> becomes "Warp cursor",
        /// <c>add_layer</c> becomes "Turn on a shift layer". Null when the
        /// verb is unknown, so the caller can fall back to plain spelling
        /// rather than print a confident wrong name.</para>
        /// <para>Lives HERE rather than in the browse dialog because the
        /// translator needs it too. A menu cell with no author label falls
        /// back to its binding's first parameter, and that parameter is the
        /// raw verb, so an imported radial menu showed cells reading
        /// "MOUSE_POSITION" and "add_layer" in the Menus editor. The dialog
        /// had a humanizer for exactly these tokens and the translator could
        /// not reach it, which made this class's own claim to be "the one
        /// place that converts" false.</para>
        /// <para>Each phrase is a string-table key, read through
        /// <paramref name="nameFormat"/> (the app passes its lookup) or, with
        /// none, its English in <see cref="TranslationNames.English"/>.</para></summary>
        public static string CommandLabel(string token, Func<string, string> nameFormat = null)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            if (!CommandKeys.TryGetValue(token.Trim(), out var key)) return null;
            string text = nameFormat?.Invoke(key);
            return string.IsNullOrEmpty(text) || text == key ? TranslationNames.English[key] : text;
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> CommandKeys =
            new(StringComparer.OrdinalIgnoreCase)
            {
            ["SET_LED"] = "Workshop_Command_SetLed",
            ["CAMERA_RESET"] = "Workshop_Command_CameraReset",
            ["MOUSE_POSITION"] = "Workshop_Command_MousePosition",
            ["MOUSE_DELTA"] = "Workshop_Command_MouseDelta",
            ["SCREENSHOT"] = "Workshop_Command_Screenshot",
            ["SHOW_KEYBOARD"] = "Workshop_Command_ShowKeyboard",
            ["CHANGE_PRESET"] = "Workshop_Command_ChangePreset",
            ["ADD_LAYER"] = "Workshop_Command_AddLayer",
            ["REMOVE_LAYER"] = "Workshop_Command_RemoveLayer",
            ["HOLD_LAYER"] = "Workshop_Command_HoldLayer",
            ["CHANGE_PLAYER_NUMBER"] = "Workshop_Command_ChangePlayerNumber",
            ["EMPTY_BINDING"] = "Workshop_Command_Unbound",
            ["EMPTY_SUB_COMMAND"] = "Workshop_Command_Unbound",
            ["TOGGLE_LIZARD"] = "Workshop_Command_ToggleDesktopMode",
            ["TOGGLE_LIZARD_MODE"] = "Workshop_Command_ToggleDesktopMode",
            ["SYSTEM_KEY_0"] = "Workshop_Command_SystemKey",
            ["SYSTEM_KEY_1"] = "Workshop_Command_SystemKey",
            ["TOGGLE_MAGNIFIER"] = "Workshop_Command_ToggleMagnifier",
            ["BRIGHTNESS_UP"] = "Workshop_Command_BrightnessUp",
            ["BRIGHTNESS_DOWN"] = "Workshop_Command_BrightnessDown",
            ["CONTROLLER_POWEROFF"] = "Workshop_Command_PowerOff",
            ["OPEN_CONFIGURATOR"] = "Workshop_Command_OpenConfigurator",
            ["OPEN_QUICKMENU"] = "Workshop_Command_OpenQuickMenu",
            ["QUIT_APPLICATION"] = "Workshop_Command_QuitGame",
            ["TOGGLE_HAPTICS"] = "Workshop_Command_ToggleHaptics",
            ["TOGGLE_HUD"] = "Workshop_Command_ToggleHud",
            ["TOGGLE_RUMBLE"] = "Workshop_Command_ToggleRumble",
            ["FORCE_GUIDE_UP"] = "Workshop_Command_ForceGuide",
            ["DOTS_PER_360_CALIBRATION_SPIN"] = "Workshop_Command_GyroCalibrationSpin",
            ["TURN_TO_FACE_DIRECTION"] = "Workshop_Command_TurnToFace",
            };

        /// <summary><para>Names the control a macro hangs off, for the
        /// parenthetical in the macro's saved name ("Set LED (Left Trigger)").
        /// </para>
        /// <para>Prefers the RESOLVED descriptor over the Steam input token,
        /// and that preference is the whole point rather than a nicety.
        /// Steam's input names are POSITIONAL and mean different hardware on
        /// different slots: <c>dpad_north</c> is the Y button on a
        /// button_diamond, the up direction on a dpad, an up-swipe wedge on a
        /// trackpad, and a forward lean on a stick. Spelling the token would
        /// therefore print "Dpad North" on a macro bound to the Y button.
        /// The descriptor already carries what the resolver decided, so it is
        /// right in every context; the token is only a fallback for a source
        /// that resolved to nothing.</para></summary>
        public static string MemberLabel(string descriptor, string inputToken)
        {
            // A Steam dpad direction or four_buttons cell read names its input
            // and the way it points ("LeftStick Up", "Touchpad 0 Right Down"),
            // a trigger read its pull ("LeftTrigger Soft Pull").
            var read = PadForge.Engine.Common.Mapping.BoundedReads.Parse(descriptor);
            if (read != null && read.Kind == PadForge.Engine.Common.Mapping.BoundedReadKind.Ring)
                return PairLabel(read.Inner) + (read.InnerEdge ? " Inner Edge" : " Edge");
            if (read != null && read.Kind is PadForge.Engine.Common.Mapping.BoundedReadKind.Wedge
                    or PadForge.Engine.Common.Mapping.BoundedReadKind.Disc
                    or PadForge.Engine.Common.Mapping.BoundedReadKind.Hold)
                return PairLabel(read.Inner) + " " + Way(read);
            if (read != null && read.Kind is PadForge.Engine.Common.Mapping.BoundedReadKind.Level
                    or PadForge.Engine.Common.Mapping.BoundedReadKind.Pull
                    or PadForge.Engine.Common.Mapping.BoundedReadKind.Press)
            {
                string label = PairLabel(read.Inner);
                if (read.ComponentAxis == 2) return label + " Edge";
                if (read.ComponentAxis >= 0) return label + " " + Way(read);
                string pull = read.Kind == PadForge.Engine.Common.Mapping.BoundedReadKind.Level
                    ? (read.PressRadius < 50 ? "Soft Pull" : "Full Pull")
                    : read.IsFull ? "Full Pull"
                    : read.Kind == PadForge.Engine.Common.Mapping.BoundedReadKind.Press ? "Soft Press" : "Soft Pull";
                return label + " " + pull;
            }
            if (!string.IsNullOrWhiteSpace(descriptor))
            {
                // "Gamepad ButtonY" reads as "ButtonY" here: the macro name
                // already sits inside PadForge, so the qualifier is noise.
                const string prefix = "Gamepad ";
                return descriptor.StartsWith(prefix, StringComparison.Ordinal)
                    ? descriptor.Substring(prefix.Length)
                    : descriptor;
            }
            return SpellToken(inputToken);
        }

        private static string PairLabel(string pair)
        {
            if (pair.StartsWith("Gamepad ", StringComparison.Ordinal) && pair.EndsWith("Ring", StringComparison.Ordinal))
                return pair.Substring(8, pair.Length - 12);
            if (pair.StartsWith("Gamepad ", StringComparison.Ordinal)) return pair.Substring(8);
            if (pair.StartsWith("Gyro Tilt ", StringComparison.Ordinal)) return "Gyro Tilt";
            string[] p = pair.Split(' ');
            return p.Length >= 5 && p[0] == "Touchpad"
                ? "Touchpad " + p[1] + (p.Length == 6 ? " " + p[5] : "")
                : pair;
        }

        private static string Way(PadForge.Engine.Common.Mapping.BoundedRead read)
        {
            double x, y;
            switch (read.Kind)
            {
                case PadForge.Engine.Common.Mapping.BoundedReadKind.Wedge:
                    double r = read.Center * Math.PI / 180.0;
                    x = Math.Cos(r);
                    y = -Math.Sin(r);
                    break;
                case PadForge.Engine.Common.Mapping.BoundedReadKind.Disc:
                    x = read.CenterX;
                    y = read.CenterY;
                    break;
                default:
                    x = read.ComponentAxis == 0 ? read.ComponentSign : 0;
                    y = read.ComponentAxis == 1 ? read.ComponentSign : 0;
                    break;
            }
            return Math.Abs(x) >= Math.Abs(y) ? (x >= 0 ? "Right" : "Left") : (y < 0 ? "Up" : "Down");
        }
    }
}
