using System;
using System.Collections.Generic;
using System.Globalization;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>One of a controller's rows beside the place an SDL row gives
    /// it (#600 item 45): the target, the device's input on it now, the
    /// field SDL's row has for the target and what it binds, and whether the
    /// two differ.</summary>
    internal sealed record SdlPlaceLine(string Target, string Input, string Field, string RowSays, bool Differs);

    /// <summary>What SDL's format cannot hold of a controller's mapping
    /// (#600 item 46).</summary>
    [Flags]
    internal enum SdlLeftOut
    {
        None = 0,
        Macros = 1,
        Curves = 2,
        DeadZones = 4,
        KeyboardOutputs = 8,
        /// <summary>Turbo, toggles, combinations and the other kinds of a
        /// row's source beyond a direct read.</summary>
        Actions = 16,
    }

    /// <summary>
    /// SDL's gamepad layout against PadForge's (#600 items 45, 46 and 48), as
    /// plain logic. PadForge numbers the places of SDL's gamepad layout as
    /// its default mapping does (SettingsManager.CreateDefaultPadSetting,
    /// GamepadObjectNames): buttons 0 to 21, axes 0 to 5 and the hat's four
    /// directions. Each has one field of SDL's row.
    /// </summary>
    internal static class ControllerDatabaseLayout
    {
        /// <summary>SDL's field for each of PadForge's button places
        /// (GamepadObjectNames.Button), paddles in SDL's own order
        /// (SDL_PrivateGenerateAutomaticGamepadMapping: paddle1 is the right
        /// upper, paddle2 the left upper).</summary>
        internal static readonly string[] ButtonPlaceFields =
        {
            "a", "b", "x", "y", "leftshoulder", "rightshoulder", "back", "start", "leftstick", "rightstick", "guide",
            "misc1", "paddle1", "paddle2", "paddle3", "paddle4", "touchpad", "misc2", "misc3", "misc4", "misc5", "misc6",
        };

        /// <summary>SDL's field for each of PadForge's axis places
        /// (GamepadObjectNames.Axis).</summary>
        internal static readonly string[] AxisPlaceFields = { "leftx", "lefty", "lefttrigger", "rightx", "righty", "righttrigger" };

        private static readonly string[] HatDirections = { "Up", "Right", "Down", "Left" };
        private static readonly string[] HatFields = { "dpup", "dpright", "dpdown", "dpleft" };
        private static readonly int[] HatMasks = { 1, 2, 4, 8 };

        /// <summary>The D-pad's fields in the order of GamepadPlacement's
        /// D-pad buttons.</summary>
        private static readonly string[] DPadButtonFields = { "dpup", "dpdown", "dpleft", "dpright" };

        /// <summary>The field SDL's row has for each of a controller's
        /// targets (MappingSetMigrator's target names).</summary>
        private static readonly Dictionary<string, string> TargetFields = new(StringComparer.Ordinal)
        {
            ["ButtonA"] = "a", ["ButtonB"] = "b", ["ButtonX"] = "x", ["ButtonY"] = "y",
            ["LeftShoulder"] = "leftshoulder", ["RightShoulder"] = "rightshoulder",
            ["ButtonBack"] = "back", ["ButtonStart"] = "start", ["ButtonGuide"] = "guide",
            ["ButtonShare"] = "misc1", ["ButtonMute"] = "misc1",
            ["RightPaddle"] = "paddle1", ["LeftPaddle"] = "paddle2", ["RightFunction"] = "paddle3", ["LeftFunction"] = "paddle4",
            ["LeftThumbButton"] = "leftstick", ["RightThumbButton"] = "rightstick",
            ["DPadUp"] = "dpup", ["DPadDown"] = "dpdown", ["DPadLeft"] = "dpleft", ["DPadRight"] = "dpright",
            ["LeftTrigger"] = "lefttrigger", ["RightTrigger"] = "righttrigger",
            ["LeftThumbAxisX"] = "leftx", ["LeftThumbAxisY"] = "lefty", ["RightThumbAxisX"] = "rightx", ["RightThumbAxisY"] = "righty",
        };

        /// <summary>SDL's field for a target, or null for a target with no
        /// place in SDL's layout.</summary>
        internal static string FieldOfTarget(string target) =>
            target != null && TargetFields.TryGetValue(target, out string field) ? field : null;

        /// <summary>The targets in the order a controller's page lists them,
        /// for a stable table.</summary>
        internal static int TargetOrder(string target)
        {
            int i = 0;
            foreach (string key in TargetFields.Keys)
            {
                if (string.Equals(key, target, StringComparison.Ordinal)) return i;
                i++;
            }
            return int.MaxValue;
        }

        /// <summary>The field a descriptor names on a device SDL reads as a
        /// gamepad, where "Button 0" is SDL's south button and "Axis 2" its
        /// left trigger. Null for anything else.</summary>
        internal static string FieldOfGamepadInput(string descriptor)
        {
            if (TryIndex(descriptor, "Button ", out int b)) return b < ButtonPlaceFields.Length ? ButtonPlaceFields[b] : null;
            if (TryIndex(descriptor, "Axis ", out int a)) return a < AxisPlaceFields.Length ? AxisPlaceFields[a] : null;
            if (TryHat(descriptor, out int hat, out int mask) && hat == 0)
                for (int i = 0; i < HatMasks.Length; i++)
                    if (HatMasks[i] == mask) return HatFields[i];
            return null;
        }

        /// <summary>The input of SDL's row form ("b3", "a1~", "+a2", "h0.4")
        /// that a raw joystick's source reads, or null when it reads none
        /// SDL's form can name.</summary>
        internal static string InputOfRawSource(MappingSource source)
        {
            if (source == null) return null;
            string d = source.Descriptor ?? string.Empty;
            if (TryIndex(d, "Button ", out int b)) return "b" + b.ToString(CultureInfo.InvariantCulture);
            int axis = -1;
            if (TryIndex(d, "Axis ", out int a)) axis = a;
            else if (TryIndex(d, "Slider ", out int s)) axis = s + CustomInputState.MaxAxis;
            if (axis >= 0)
            {
                string n = "a" + axis.ToString(CultureInfo.InvariantCulture);
                if (source.HalfAxis) return (source.Invert ? "-" : "+") + n;
                return source.Invert ? n + "~" : n;
            }
            if (TryHat(d, out int hat, out int mask)) return "h" + hat.ToString(CultureInfo.InvariantCulture) + "." + mask.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        private static bool TryIndex(string descriptor, string prefix, out int index)
        {
            index = -1;
            return descriptor != null && descriptor.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(descriptor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index);
        }

        /// <summary>"POV 0 Up" as hat 0 and SDL's mask 1.</summary>
        private static bool TryHat(string descriptor, out int hat, out int mask)
        {
            hat = mask = 0;
            if (descriptor == null || !descriptor.StartsWith("POV ", StringComparison.Ordinal)) return false;
            string[] parts = descriptor.Split(' ');
            if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out hat)) return false;
            int i = Array.IndexOf(HatDirections, parts[2]);
            if (i < 0) return false;
            mask = HatMasks[i];
            return true;
        }

        /// <summary>A device's source of one row: its first direct source,
        /// or its first of any kind.</summary>
        internal static MappingSource SourceOf(MappingRow row, string deviceGuid)
        {
            MappingSource any = null;
            foreach (var s in row?.Sources ?? new List<MappingSource>())
            {
                if (s == null || !string.Equals(s.DeviceGuid, deviceGuid, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsDirect(s)) return s;
                any ??= s;
            }
            return any;
        }

        private static bool IsDirect(MappingSource s) => string.IsNullOrEmpty(s.Kind) || string.Equals(s.Kind, "Direct", StringComparison.Ordinal);

        /// <summary>
        /// Each base-layer row of a controller that reads the device, beside
        /// the place SDL's row gives the row's target (#600 item 45). On a
        /// device SDL reads as a gamepad, the row's input is a place of SDL's
        /// layout, and it differs when that place is not the target's. On a
        /// device read raw, the row's input is a joystick input, and it
        /// differs from the one SDL's row binds. A target with no place in
        /// SDL's layout is left out.
        /// </summary>
        /// <param name="describe">Names a descriptor as the page names it.</param>
        internal static List<SdlPlaceLine> Places(MappingSet set, string deviceGuid, bool readAsGamepad, SdlMappingRow row,
            Func<string, string> describe)
        {
            var lines = new List<(int Order, SdlPlaceLine Line)>();
            foreach (var r in set?.Rows ?? new List<MappingRow>())
            {
                if (r == null || !string.Equals(r.LayerMask ?? "Base", "Base", StringComparison.Ordinal)) continue;
                string field = FieldOfTarget(r.Target);
                if (field == null) continue;
                var source = SourceOf(r, deviceGuid);
                if (source == null) continue;
                string bound = row?.InputOf(field);
                string says = bound == null ? null : field + ": " + bound;
                bool differs;
                if (readAsGamepad)
                    differs = !string.Equals(FieldOfGamepadInput(source.Descriptor), field, StringComparison.Ordinal)
                              || !IsDirect(source);
                else
                    differs = bound != null && !string.Equals(InputOfRawSource(source), bound, StringComparison.Ordinal);
                lines.Add((TargetOrder(r.Target), new SdlPlaceLine(r.Target, describe?.Invoke(source.Descriptor) ?? source.Descriptor,
                    field, says, differs)));
            }
            lines.Sort((x, y) => x.Order.CompareTo(y.Order));
            var result = new List<SdlPlaceLine>(lines.Count);
            foreach (var l in lines) result.Add(l.Line);
            return result;
        }

        /// <summary>
        /// The fields of an SDL row no row of the device's controllers reads
        /// (#600 item 45): on a device SDL reads as a gamepad, a field whose
        /// place no row reads. Empty for a device on no controller.
        /// </summary>
        internal static List<string> FieldsNotUsed(SdlMappingRow row, IEnumerable<MappingSet> sets, string deviceGuid)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            bool any = false;
            foreach (var set in sets ?? Array.Empty<MappingSet>())
                foreach (var r in set?.Rows ?? new List<MappingRow>())
                    foreach (var s in r?.Sources ?? new List<MappingSource>())
                    {
                        if (s == null || !string.Equals(s.DeviceGuid, deviceGuid, StringComparison.OrdinalIgnoreCase)) continue;
                        any = true;
                        if (FieldOfGamepadInput(s.Descriptor) is { } f) used.Add(f);
                    }
            var unused = new List<string>();
            if (row == null || !any) return unused;
            foreach (var b in row.Bindings)
                if (!used.Contains(b.Name) && !unused.Contains(b.Name)) unused.Add(b.Name);
            return unused;
        }

        /// <summary>
        /// The bindings a raw joystick's rows make (#600 item 46): for each
        /// target with a place in SDL's layout, the joystick input of the
        /// device's first direct source. A stick's two directions on one
        /// axis read as the axis. What SDL's form cannot hold is said in
        /// <paramref name="leftOut"/>.
        /// </summary>
        internal static List<SdlBinding> BindingsFromRows(MappingSet set, string deviceGuid, out SdlLeftOut leftOut)
        {
            leftOut = SdlLeftOut.None;
            var bindings = new List<SdlBinding>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in set?.Rows ?? new List<MappingRow>())
            {
                if (r == null) continue;
                bool mine = false;
                foreach (var s in r.Sources ?? new List<MappingSource>())
                    if (s != null && string.Equals(s.DeviceGuid, deviceGuid, StringComparison.OrdinalIgnoreCase)) { mine = true; break; }
                if (!mine) continue;
                if (!string.Equals(r.LayerMask ?? "Base", "Base", StringComparison.Ordinal)) { leftOut |= SdlLeftOut.Actions; continue; }
                string field = FieldOfTarget(r.Target);
                if (field == null)
                {
                    if (IsKeyboardOrMouseTarget(r.Target)) leftOut |= SdlLeftOut.KeyboardOutputs;
                    else leftOut |= SdlLeftOut.Actions;
                    continue;
                }
                var source = SourceOf(r, deviceGuid);
                if (source == null) continue;
                if (!IsDirect(source)) { leftOut |= SdlLeftOut.Actions; continue; }
                if (!string.IsNullOrEmpty(r.CombineExpression) || !string.IsNullOrEmpty(r.CombineMode)) leftOut |= SdlLeftOut.Actions;
                string input = InputOfRawSource(source);
                if (input == null || !taken.Add(field)) continue;
                bindings.Add(new SdlBinding(field, input));
            }
            return bindings;
        }

        private static bool IsKeyboardOrMouseTarget(string target) =>
            target != null && (target.StartsWith("Kbm", StringComparison.Ordinal) || target.StartsWith("Key", StringComparison.Ordinal)
                               || target.StartsWith("Mouse", StringComparison.Ordinal));

        /// <summary>What of a device's tuning SDL's form cannot hold: dead
        /// zones and curves that are not at their defaults.</summary>
        internal static SdlLeftOut TuningLeftOut(PadSetting ps)
        {
            if (ps == null) return SdlLeftOut.None;
            var left = SdlLeftOut.None;
            foreach (string v in new[]
                     {
                         ps.LeftThumbDeadZoneX, ps.LeftThumbDeadZoneY, ps.RightThumbDeadZoneX, ps.RightThumbDeadZoneY,
                         ps.LeftTriggerDeadZone, ps.RightTriggerDeadZone, ps.LeftThumbAntiDeadZone, ps.RightThumbAntiDeadZone,
                         ps.LeftThumbAntiDeadZoneX, ps.LeftThumbAntiDeadZoneY, ps.RightThumbAntiDeadZoneX, ps.RightThumbAntiDeadZoneY,
                         ps.LeftTriggerAntiDeadZone, ps.RightTriggerAntiDeadZone,
                     })
                if (IsSet(v)) left |= SdlLeftOut.DeadZones;
            foreach (string v in new[]
                     {
                         ps.LeftThumbLinear, ps.RightThumbLinear, ps.LeftThumbSensitivityCurveX, ps.LeftThumbSensitivityCurveY,
                         ps.RightThumbSensitivityCurveX, ps.RightThumbSensitivityCurveY, ps.LeftTriggerSensitivityCurve,
                         ps.RightTriggerSensitivityCurve,
                     })
                if (IsSet(v)) left |= SdlLeftOut.Curves;
            return left;
        }

        private static bool IsSet(string value) =>
            !string.IsNullOrWhiteSpace(value) && !(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d == 0);

        // ─────────────────────────────────────────────
        //  A guessed layout (#600 item 48)
        // ─────────────────────────────────────────────

        /// <summary>
        /// A layout guessed for a joystick no database knows, from the inputs
        /// it has, in SDL's row form. The order is the one most DirectInput
        /// pads report: the left stick on X and Y, the right stick on Z and
        /// Rz, or on Rx and Ry with the triggers on Z and Rz when the device
        /// has all six axes, the first hat as the D-pad, and the face buttons
        /// first. For vendor 054C the guess follows the community's DualShock 4
        /// and DualSense rows, which number the face buttons square, cross,
        /// circle, triangle and put Select and Start at 8 and 9. Its PS3 and
        /// PlayStation Classic rows number them triangle, circle, cross,
        /// square, so a guess for those pads is wrong until the user fixes it.
        /// A button the device lacks is left out, so a short pad never maps
        /// a dead input. Every guess is labeled one where it shows.
        /// </summary>
        internal static List<SdlBinding> Guess(int axes, int buttons, int hats, ushort vendorId)
        {
            var list = new List<SdlBinding>();
            void Button(string field, int index)
            {
                if (index < buttons) list.Add(new SdlBinding(field, "b" + index.ToString(CultureInfo.InvariantCulture)));
            }
            void Axis(string field, int index)
            {
                if (index < axes) list.Add(new SdlBinding(field, "a" + index.ToString(CultureInfo.InvariantCulture)));
            }

            if (vendorId == 0x054C)
            {
                Button("x", 0); Button("a", 1); Button("b", 2); Button("y", 3);
                Button("leftshoulder", 4); Button("rightshoulder", 5);
                Button("back", 8); Button("start", 9); Button("leftstick", 10); Button("rightstick", 11); Button("guide", 12);
                // Sony's DirectInput report: L2 and R2 as buttons 6 and 7,
                // their pressure on Rx and Ry, the right stick on Z and Rz.
                Axis("leftx", 0); Axis("lefty", 1); Axis("rightx", 2); Axis("righty", 5);
                if (axes >= 6) { Axis("lefttrigger", 3); Axis("righttrigger", 4); }
                else { Button("lefttrigger", 6); Button("righttrigger", 7); }
            }
            else
            {
                Button("a", 0); Button("b", 1); Button("x", 2); Button("y", 3);
                Button("leftshoulder", 4); Button("rightshoulder", 5);
                if (axes >= 6)
                {
                    // Six axes: the triggers have axes of their own.
                    Button("back", 6); Button("start", 7); Button("leftstick", 8); Button("rightstick", 9); Button("guide", 10);
                    Axis("leftx", 0); Axis("lefty", 1); Axis("lefttrigger", 2); Axis("rightx", 3); Axis("righty", 4); Axis("righttrigger", 5);
                }
                else
                {
                    // Four axes or fewer: the triggers are buttons 6 and 7, as
                    // on most pads with twelve buttons.
                    Button("lefttrigger", 6); Button("righttrigger", 7);
                    Button("back", 8); Button("start", 9); Button("leftstick", 10); Button("rightstick", 11); Button("guide", 12);
                    Axis("leftx", 0); Axis("lefty", 1); Axis("rightx", 2); Axis("righty", 3);
                }
            }
            if (hats > 0)
            {
                list.Add(new SdlBinding("dpup", "h0.1"));
                list.Add(new SdlBinding("dpright", "h0.2"));
                list.Add(new SdlBinding("dpdown", "h0.4"));
                list.Add(new SdlBinding("dpleft", "h0.8"));
            }
            return list;
        }

        // ─────────────────────────────────────────────
        //  A row as a placement, for a device SDL does not read
        // ─────────────────────────────────────────────

        /// <summary>
        /// A row's bindings as the places of SDL's layout on a device that
        /// SDL does not read, such as a plugin's (#600 item 47): each field
        /// takes the device's "Button N" or "Axis N", and the first hat is
        /// the D-pad when the row binds its four directions there. Otherwise
        /// each D-pad direction on a plain button is the placement's D-pad
        /// button for it, as the Twitch Chat row's are (#555). A half axis, a
        /// turned axis and a direction on a hat that is not the whole D-pad
        /// have no such plain form, and their fields are left out and named
        /// in <paramref name="leftOut"/>.
        /// </summary>
        internal static GamepadPlacement PlacementOf(SdlMappingRow row, out List<string> leftOut)
        {
            leftOut = new List<string>();
            if (row == null) return null;
            var buttons = new string[GamepadPlacement.ButtonPositions];
            var axes = new string[GamepadPlacement.AxisPositions];
            bool hatPad = true;
            for (int i = 0; i < HatFields.Length; i++)
                if (!string.Equals(row.InputOf(HatFields[i]), "h0." + HatMasks[i].ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    hatPad = false;
            string[] dpadButtons = null;
            foreach (var b in row.Bindings)
            {
                if (Array.IndexOf(HatFields, b.Field) >= 0)
                {
                    if (hatPad) continue;
                    string pressed = PlainDescriptor(b.Input);
                    if (pressed == null || !pressed.StartsWith("Button ", StringComparison.Ordinal))
                    {
                        leftOut.Add(b.Field);
                        continue;
                    }
                    // GamepadPlacement's D-pad buttons are up, down, left, right.
                    (dpadButtons ??= new string[4])[Array.IndexOf(DPadButtonFields, b.Field)] = pressed;
                    continue;
                }
                string plain = PlainDescriptor(b.Input);
                int button = Array.IndexOf(ButtonPlaceFields, b.Field);
                int axis = Array.IndexOf(AxisPlaceFields, b.Field);
                if (plain == null || (axis >= 0 && !plain.StartsWith("Axis ", StringComparison.Ordinal) && !plain.StartsWith("Button ", StringComparison.Ordinal))
                    || (button >= 0 && !plain.StartsWith("Button ", StringComparison.Ordinal)))
                {
                    leftOut.Add(b.Field);
                    continue;
                }
                if (button >= 0) buttons[button] = plain;
                else if (axis >= 0) axes[axis] = plain;
                else leftOut.Add(b.Field);
            }
            return new GamepadPlacement(buttons, axes, hatPad, null, dpadButtons);
        }

        /// <summary>"b3" as "Button 3" and "a2" as "Axis 2". Null for a hat,
        /// a half axis or a turned axis.</summary>
        private static string PlainDescriptor(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            if (input[0] == 'b') return "Button " + input.Substring(1);
            if (input[0] == 'a' && !input.EndsWith("~", StringComparison.Ordinal)) return "Axis " + input.Substring(1);
            return null;
        }
    }
}
