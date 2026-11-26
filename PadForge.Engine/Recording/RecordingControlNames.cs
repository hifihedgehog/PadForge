using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PadForge.Engine.Recording
{
    /// <summary>How a device's controls are named in a recording: by the
    /// layout PadForge reads it with.</summary>
    public enum RecordingControlKind : byte
    {
        /// <summary>Axis0, Slider0, Hat0, Button0.</summary>
        Generic,
        /// <summary>Read through SDL's gamepad layout: LeftX, A, DPad.</summary>
        Gamepad,
        /// <summary>MouseX, MouseLeft.</summary>
        Mouse,
        /// <summary>A key by its Windows key code: KeyA, KeyEnter, Vk233.</summary>
        Keyboard,
    }

    /// <summary>
    /// The names a recording (#600) gives a device's controls and a virtual
    /// controller's fields. They are fixed English words that never follow
    /// the interface language or a device's own object names, so a file reads
    /// the same on every PC and a program reads it back exactly. A name says
    /// its index: LeftX and Axis0 are both axis 0, so a reader needs no
    /// device facts to turn a name back into its control.
    /// </summary>
    public static class RecordingControlNames
    {
        /// <summary>Axes 0 to 5 of a device read as a gamepad, in the order
        /// SdlDeviceWrapper.GetGamepadState fills them.</summary>
        public static readonly string[] GamepadAxes = { "LeftX", "LeftY", "LeftTrigger", "RightX", "RightY", "RightTrigger" };

        /// <summary>Buttons 0 to 21 of a device read as a gamepad: 0 to 10 as
        /// GetGamepadState orders them, 11 to 21 in SDL3's button order with
        /// the four D-pad buttons left out (they are hat 0).</summary>
        public static readonly string[] GamepadButtons =
        {
            "A", "B", "X", "Y", "LeftShoulder", "RightShoulder", "Back", "Start", "LeftStick", "RightStick", "Guide",
            "Misc1", "RightPaddle1", "LeftPaddle1", "RightPaddle2", "LeftPaddle2", "Touchpad",
            "Misc2", "Misc3", "Misc4", "Misc5", "Misc6",
        };

        /// <summary>A mouse's three axes, as SdlMouseWrapper fills them.</summary>
        public static readonly string[] MouseAxes = { "MouseX", "MouseY", "Wheel" };

        /// <summary>A mouse's five buttons, as SdlMouseWrapper fills them.</summary>
        public static readonly string[] MouseButtons = { "MouseLeft", "MouseMiddle", "MouseRight", "MouseX1", "MouseX2" };

        /// <summary>The bits of an XInput button word, low bit first. Bit 11
        /// is no XInput button, and a PlayStation controller's touchpad click
        /// in PadForge's own frame (Gamepad.TOUCHPAD).</summary>
        public static readonly string[] XInputButtons =
        {
            "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "Start", "Back", "LeftStick", "RightStick",
            "LeftShoulder", "RightShoulder", "Guide", "Touchpad", "A", "B", "X", "Y",
        };

        /// <summary>The key code a typed letter or number is written as when
        /// the user hides them (RecordingCapture.TypedKey).</summary>
        public const int TypedKeyCode = 0xE8;

        private static readonly string[] KeyNames = BuildKeyNames();
        private static readonly Dictionary<string, (RecordingField Field, int Index)> InputByName = BuildInputNames();

        private static string[] BuildKeyNames()
        {
            // A copy kept here, not read from the keyboard reader's own
            // table, so a change there never renames a key in a file.
            var names = new string[256];
            void Set(int code, string name) => names[code] = "Key" + name;
            Set(0x08, "Backspace"); Set(0x09, "Tab"); Set(0x0D, "Enter"); Set(0x10, "Shift"); Set(0x11, "Ctrl");
            Set(0x12, "Alt"); Set(0x13, "Pause"); Set(0x14, "CapsLock"); Set(0x1B, "Escape"); Set(0x20, "Space");
            Set(0x21, "PageUp"); Set(0x22, "PageDown"); Set(0x23, "End"); Set(0x24, "Home"); Set(0x25, "Left");
            Set(0x26, "Up"); Set(0x27, "Right"); Set(0x28, "Down"); Set(0x2C, "PrintScreen"); Set(0x2D, "Insert");
            Set(0x2E, "Delete");
            for (int i = 0; i < 10; i++) Set(0x30 + i, "Digit" + i.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < 26; i++) Set(0x41 + i, ((char)('A' + i)).ToString());
            Set(0x5B, "LeftWin"); Set(0x5C, "RightWin"); Set(0x5D, "Apps");
            for (int i = 0; i < 10; i++) Set(0x60 + i, "Numpad" + i.ToString(CultureInfo.InvariantCulture));
            Set(0x6A, "NumpadMultiply"); Set(0x6B, "NumpadAdd"); Set(0x6D, "NumpadSubtract"); Set(0x6E, "NumpadDecimal");
            Set(0x6F, "NumpadDivide");
            for (int i = 0; i < 24; i++) Set(0x70 + i, "F" + (i + 1).ToString(CultureInfo.InvariantCulture));
            Set(0x88, "NumpadEnter"); Set(0x90, "NumLock"); Set(0x91, "ScrollLock");
            Set(0xA0, "LeftShift"); Set(0xA1, "RightShift"); Set(0xA2, "LeftCtrl"); Set(0xA3, "RightCtrl");
            Set(0xA4, "LeftAlt"); Set(0xA5, "RightAlt");
            Set(0xBA, "Semicolon"); Set(0xBB, "Equals"); Set(0xBC, "Comma"); Set(0xBD, "Minus"); Set(0xBE, "Period");
            Set(0xBF, "Slash"); Set(0xC0, "Grave"); Set(0xDB, "LeftBracket"); Set(0xDC, "Backslash");
            Set(0xDD, "RightBracket"); Set(0xDE, "Quote");
            Set(TypedKeyCode, "Typed");
            for (int i = 0; i < names.Length; i++)
                names[i] ??= "Vk" + i.ToString(CultureInfo.InvariantCulture);
            return names;
        }

        private static Dictionary<string, (RecordingField, int)> BuildInputNames()
        {
            var names = new Dictionary<string, (RecordingField, int)>(StringComparer.Ordinal);
            for (int i = 0; i < GamepadAxes.Length; i++) names.Add(GamepadAxes[i], (RecordingField.Axis, i));
            for (int i = 0; i < GamepadButtons.Length; i++) names.Add(GamepadButtons[i], (RecordingField.Button, i));
            for (int i = 0; i < MouseAxes.Length; i++) names.Add(MouseAxes[i], (RecordingField.Axis, i));
            for (int i = 0; i < MouseButtons.Length; i++) names.Add(MouseButtons[i], (RecordingField.Button, i));
            for (int i = 0; i < KeyNames.Length; i++) names.Add(KeyNames[i], (RecordingField.Button, i));
            names.Add("DPad", (RecordingField.Hat, 0));
            return names;
        }

        /// <summary>An input control's name.</summary>
        public static string Input(RecordingControlKind kind, RecordingField field, int index)
        {
            switch (field)
            {
                case RecordingField.Axis:
                    if (kind == RecordingControlKind.Gamepad && (uint)index < GamepadAxes.Length) return GamepadAxes[index];
                    if (kind == RecordingControlKind.Mouse && (uint)index < MouseAxes.Length) return MouseAxes[index];
                    return "Axis" + index.ToString(CultureInfo.InvariantCulture);
                case RecordingField.Slider:
                    return "Slider" + index.ToString(CultureInfo.InvariantCulture);
                case RecordingField.Hat:
                    return kind == RecordingControlKind.Gamepad && index == 0 ? "DPad" : "Hat" + index.ToString(CultureInfo.InvariantCulture);
                case RecordingField.Button:
                    if (kind == RecordingControlKind.Gamepad && (uint)index < GamepadButtons.Length) return GamepadButtons[index];
                    if (kind == RecordingControlKind.Mouse && (uint)index < MouseButtons.Length) return MouseButtons[index];
                    if (kind == RecordingControlKind.Keyboard && (uint)index < KeyNames.Length) return KeyNames[index];
                    return "Button" + index.ToString(CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }

        /// <summary>The control an input name names: its field and index.
        /// False for a name that is none.</summary>
        public static bool TryInput(ReadOnlySpan<char> name, out RecordingField field, out int index)
        {
            field = RecordingField.None;
            index = 0;
            if (name.IsEmpty) return false;
            if (Numbered(name, "Axis", CustomInputState.MaxAxis, out index)) { field = RecordingField.Axis; return true; }
            if (Numbered(name, "Slider", CustomInputState.MaxSliders, out index)) { field = RecordingField.Slider; return true; }
            if (Numbered(name, "Hat", CustomInputState.MaxPovs, out index)) { field = RecordingField.Hat; return true; }
            if (Numbered(name, "Button", CustomInputState.MaxButtons, out index)) { field = RecordingField.Button; return true; }
            if (Numbered(name, "Vk", 256, out index)) { field = RecordingField.Button; return true; }
            if (!InputByName.TryGetValue(name.ToString(), out var known)) return false;
            field = known.Field;
            index = known.Index;
            return true;
        }

        private static bool Numbered(ReadOnlySpan<char> name, string stem, int count, out int index)
        {
            index = 0;
            if (name.Length <= stem.Length || !name.StartsWith(stem, StringComparison.Ordinal)) return false;
            var digits = name.Slice(stem.Length);
            if (digits.Length > 1 && digits[0] == '0') return false;
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index < count;
        }

        /// <summary>An XInput button word as its buttons' names joined by
        /// plus signs, or 0 for none.</summary>
        public static void AppendXInputButtons(StringBuilder sb, int word)
        {
            word &= 0xFFFF;
            if (word == 0) { sb.Append('0'); return; }
            bool first = true;
            for (int bit = 0; bit < 16; bit++)
            {
                if ((word & (1 << bit)) == 0) continue;
                if (!first) sb.Append('+');
                first = false;
                sb.Append(XInputButtons[bit]);
            }
        }

        /// <summary>Reads what <see cref="AppendXInputButtons"/> wrote, and
        /// a hex word an older file wrote.</summary>
        public static bool TryXInputButtons(ReadOnlySpan<char> text, out int word)
        {
            word = 0;
            if (text.IsEmpty) return false;
            if (text.SequenceEqual("0")) return true;
            if (text.StartsWith("0x"))
                return int.TryParse(text.Slice(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out word) && word <= 0xFFFF;
            while (!text.IsEmpty)
            {
                int plus = text.IndexOf('+');
                var name = plus < 0 ? text : text.Slice(0, plus);
                text = plus < 0 ? default : text.Slice(plus + 1);
                int bit = -1;
                for (int i = 0; i < XInputButtons.Length; i++)
                    if (name.SequenceEqual(XInputButtons[i])) { bit = i; break; }
                if (bit < 0) return false;
                word |= 1 << bit;
            }
            return true;
        }
    }
}
