using System.Collections.Generic;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Control category for positional translation between layouts.
    /// </summary>
    public enum ControlCategory { Button, Axis, AxisNeg, DPad }

    /// <summary>
    /// A canonical position within a control category (e.g., "3rd button", "1st axis negative").
    /// </summary>
    public record MappingSlot(ControlCategory Category, int Position);

    /// <summary>
    /// Translates mapping property names between different virtual controller layouts
    /// (Xbox / PlayStation, Extended custom, MIDI, KB+M) using positional equivalence.
    /// </summary>
    public static class MappingTranslation
    {
        /// <summary>
        /// Maps a layout-specific property name to its canonical position.
        /// </summary>
        public static MappingSlot GetPosition(string propertyName, VirtualControllerType type, bool isExtended)
        {
            if (type is VirtualControllerType.Extended or VirtualControllerType.Nintendo && isExtended)
                return GetRawSurfacePosition(propertyName);
            if (type == VirtualControllerType.Midi)
                return GetMidiPosition(propertyName);
            if (type == VirtualControllerType.KeyboardMouse)
                return GetKbmPosition(propertyName);
            // Xbox / PlayStation / Extended gamepad preset
            return GetGamepadPosition(propertyName);
        }

        /// <summary>
        /// Maps a canonical position to a layout-specific property name.
        /// </summary>
        public static string GetPropertyName(MappingSlot slot, VirtualControllerType type, bool isExtended)
        {
            if (type is VirtualControllerType.Extended or VirtualControllerType.Nintendo && isExtended)
                return GetRawSurfacePropertyName(slot);
            if (type == VirtualControllerType.Midi)
                return GetMidiPropertyName(slot);
            if (type == VirtualControllerType.KeyboardMouse)
                return GetKbmPropertyName(slot);
            return GetGamepadPropertyName(slot);
        }

        // ─────────────────────────────────────────────
        //  Gamepad (Xbox / PlayStation / Extended gamepad preset)
        // ─────────────────────────────────────────────

        private static readonly Dictionary<string, MappingSlot> _gamepadMap = new()
        {
            // Buttons
            ["ButtonA"]           = new(ControlCategory.Button, 0),
            ["ButtonB"]           = new(ControlCategory.Button, 1),
            ["ButtonX"]           = new(ControlCategory.Button, 2),
            ["ButtonY"]           = new(ControlCategory.Button, 3),
            ["LeftShoulder"]      = new(ControlCategory.Button, 4),
            ["RightShoulder"]     = new(ControlCategory.Button, 5),
            ["ButtonBack"]        = new(ControlCategory.Button, 6),
            ["ButtonStart"]       = new(ControlCategory.Button, 7),
            ["LeftThumbButton"]   = new(ControlCategory.Button, 8),
            ["RightThumbButton"]  = new(ControlCategory.Button, 9),
            ["ButtonGuide"]       = new(ControlCategory.Button, 10),
            ["ButtonShare"]       = new(ControlCategory.Button, 11),
            ["ButtonMute"]        = new(ControlCategory.Button, 12),
            ["LeftPaddle"]        = new(ControlCategory.Button, 13),
            ["RightPaddle"]       = new(ControlCategory.Button, 14),
            ["LeftFunction"]      = new(ControlCategory.Button, 15),
            ["RightFunction"]     = new(ControlCategory.Button, 16),
            // Axes
            ["LeftThumbAxisX"]    = new(ControlCategory.Axis, 0),
            ["LeftThumbAxisY"]    = new(ControlCategory.Axis, 1),
            ["LeftTrigger"]       = new(ControlCategory.Axis, 2),
            ["RightThumbAxisX"]   = new(ControlCategory.Axis, 3),
            ["RightThumbAxisY"]   = new(ControlCategory.Axis, 4),
            ["RightTrigger"]      = new(ControlCategory.Axis, 5),
            // Axis negatives
            ["LeftThumbAxisXNeg"]  = new(ControlCategory.AxisNeg, 0),
            ["LeftThumbAxisYNeg"]  = new(ControlCategory.AxisNeg, 1),
            ["RightThumbAxisXNeg"] = new(ControlCategory.AxisNeg, 3),
            ["RightThumbAxisYNeg"] = new(ControlCategory.AxisNeg, 4),
            // D-Pad
            ["DPadUp"]    = new(ControlCategory.DPad, 0),
            ["DPadDown"]  = new(ControlCategory.DPad, 1),
            ["DPadLeft"]  = new(ControlCategory.DPad, 2),
            ["DPadRight"] = new(ControlCategory.DPad, 3),
        };

        private static readonly Dictionary<MappingSlot, string> _gamepadReverse;

        static MappingTranslation()
        {
            _gamepadReverse = new Dictionary<MappingSlot, string>();
            foreach (var kvp in _gamepadMap)
                _gamepadReverse[kvp.Value] = kvp.Key;
        }

        private static MappingSlot GetGamepadPosition(string name)
            => _gamepadMap.TryGetValue(name, out var slot) ? slot : null;

        private static string GetGamepadPropertyName(MappingSlot slot)
            => _gamepadReverse.TryGetValue(slot, out var name) ? name : null;

        // ─────────────────────────────────────────────
        //  Extended Custom
        // ─────────────────────────────────────────────

        private static MappingSlot GetRawSurfacePosition(string name)
        {
            if (name == null) return null;

            const int BtnPrefixLen = 6;   // "RawBtn"
            const int AxisPrefixLen = 7;  // "RawAxis"
            const int NegSuffixLen = 3;   // "Neg"

            if (name.StartsWith("RawBtn") && int.TryParse(name.AsSpan(BtnPrefixLen), out int btnIdx))
                return new(ControlCategory.Button, btnIdx);

            if (name.StartsWith("RawAxis") && name.EndsWith("Neg"))
            {
                if (int.TryParse(name.AsSpan(AxisPrefixLen, name.Length - AxisPrefixLen - NegSuffixLen), out int axNegIdx))
                    return new(ControlCategory.AxisNeg, axNegIdx);
            }

            if (name.StartsWith("RawAxis") && int.TryParse(name.AsSpan(AxisPrefixLen), out int axIdx))
                return new(ControlCategory.Axis, axIdx);

            // RawPov0Up, RawPov0Down PadSetting keys, etc. — only POV 0 maps to D-Pad
            if (name.StartsWith("RawPov0"))
            {
                if (name.EndsWith("Up")) return new(ControlCategory.DPad, 0);
                if (name.EndsWith("Down")) return new(ControlCategory.DPad, 1);
                if (name.EndsWith("Left")) return new(ControlCategory.DPad, 2);
                if (name.EndsWith("Right")) return new(ControlCategory.DPad, 3);
            }

            return null;
        }

        private static string GetRawSurfacePropertyName(MappingSlot slot) => slot.Category switch
        {
            ControlCategory.Button  => $"RawBtn{slot.Position}",
            ControlCategory.Axis    => $"RawAxis{slot.Position}",
            ControlCategory.AxisNeg => $"RawAxis{slot.Position}Neg",
            ControlCategory.DPad    => slot.Position switch
            {
                0 => "RawPov0Up",
                1 => "RawPov0Down",
                2 => "RawPov0Left",
                3 => "RawPov0Right",
                _ => null
            },
            _ => null
        };

        // ─────────────────────────────────────────────
        //  MIDI
        // ─────────────────────────────────────────────

        private static MappingSlot GetMidiPosition(string name)
        {
            if (name == null) return null;

            if (name.StartsWith("MidiNote") && int.TryParse(name.AsSpan(8), out int noteIdx))
                return new(ControlCategory.Button, noteIdx);

            if (name.StartsWith("MidiCC") && name.EndsWith("Neg"))
            {
                if (int.TryParse(name.AsSpan(6, name.Length - 9), out int ccNegIdx))
                    return new(ControlCategory.AxisNeg, ccNegIdx);
            }

            if (name.StartsWith("MidiCC") && int.TryParse(name.AsSpan(6), out int ccIdx))
                return new(ControlCategory.Axis, ccIdx);

            return null; // MIDI has no D-Pad
        }

        private static string GetMidiPropertyName(MappingSlot slot) => slot.Category switch
        {
            ControlCategory.Button  => $"MidiNote{slot.Position}",
            ControlCategory.Axis    => $"MidiCC{slot.Position}",
            ControlCategory.AxisNeg => $"MidiCC{slot.Position}Neg",
            ControlCategory.DPad    => null, // MIDI has no D-Pad
            _ => null
        };

        // ─────────────────────────────────────────────
        //  Keyboard + Mouse
        // ─────────────────────────────────────────────

        // KBM button positions: first 5 are mouse buttons, then keyboard keys.
        // KBM axis positions: MouseX=0, MouseY=1, Scroll=2.

        private static readonly string[] _kbmButtonKeys =
        {
            "KbmMBtn0",  // 0: LMB
            "KbmMBtn1",  // 1: RMB
            "KbmMBtn2",  // 2: MMB
            "KbmMBtn3",  // 3: X1
            "KbmMBtn4",  // 4: X2
            "KbmKey20",  // 5: Space
            "KbmKey45",  // 6: E
            "KbmKey52",  // 7: R
            "KbmKey47",  // 8: G
            "KbmKey51",  // 9: Q
            "KbmKey43",  // 10: C
        };

        private static readonly Dictionary<string, MappingSlot> _kbmAxisMap = new()
        {
            ["KbmMouseX"]     = new(ControlCategory.Axis, 0),
            ["KbmMouseY"]     = new(ControlCategory.Axis, 1),
            ["KbmScroll"]     = new(ControlCategory.Axis, 2),
            ["KbmScrollH"]    = new(ControlCategory.Axis, 3),
            ["KbmMouseXNeg"]  = new(ControlCategory.AxisNeg, 0),
            ["KbmMouseYNeg"]  = new(ControlCategory.AxisNeg, 1),
            ["KbmScrollNeg"]  = new(ControlCategory.AxisNeg, 2),
            ["KbmScrollHNeg"] = new(ControlCategory.AxisNeg, 3),
        };

        private static readonly string[] _kbmDPadKeys =
        {
            "KbmKey26",  // 0: Up arrow
            "KbmKey28",  // 1: Down arrow
            "KbmKey25",  // 2: Left arrow
            "KbmKey27",  // 3: Right arrow
        };

        private static MappingSlot GetKbmPosition(string name)
        {
            if (name == null) return null;

            // Check axis map first
            if (_kbmAxisMap.TryGetValue(name, out var axisSlot))
                return axisSlot;

            // Check D-Pad keys
            for (int i = 0; i < _kbmDPadKeys.Length; i++)
                if (name == _kbmDPadKeys[i]) return new(ControlCategory.DPad, i);

            // Check button keys (mouse buttons + keyboard)
            for (int i = 0; i < _kbmButtonKeys.Length; i++)
                if (name == _kbmButtonKeys[i]) return new(ControlCategory.Button, i);

            // Generic KbmMBtn outside the table, assigned by its own index.
            // KbmKey has no equivalent arm on purpose: its position IS the
            // table index (GetKbmPropertyName maps a Button position straight
            // back through _kbmButtonKeys), so an off-table KbmKey has no
            // position that round-trips and returning one would translate it
            // into whichever key happens to sit at that index. The comment
            // used to promise KbmKey handling that was never here.
            if (name.StartsWith("KbmMBtn") && int.TryParse(name.AsSpan(7), out int mbIdx))
                return new(ControlCategory.Button, mbIdx);

            return null;
        }

        private static string GetKbmPropertyName(MappingSlot slot) => slot.Category switch
        {
            ControlCategory.Button => slot.Position < _kbmButtonKeys.Length
                ? _kbmButtonKeys[slot.Position] : null,
            ControlCategory.Axis => slot.Position switch
            {
                0 => "KbmMouseX",
                1 => "KbmMouseY",
                2 => "KbmScroll",
                3 => "KbmScrollH",
                _ => null
            },
            ControlCategory.AxisNeg => slot.Position switch
            {
                0 => "KbmMouseXNeg",
                1 => "KbmMouseYNeg",
                2 => "KbmScrollNeg",
                3 => "KbmScrollHNeg",
                _ => null
            },
            ControlCategory.DPad => slot.Position < _kbmDPadKeys.Length
                ? _kbmDPadKeys[slot.Position] : null,
            _ => null
        };

        // ─────────────────────────────────────────────
        //  Layout detection helper
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns true if the source and target use the same mapping layout
        /// (direct property copy is sufficient, no translation needed).
        /// </summary>
        public static bool IsSameLayout(
            VirtualControllerType srcType, bool srcIsExtended,
            VirtualControllerType tgtType, bool tgtIsExtended)
        {
            // Xbox and PlayStation share the same gamepad property names.
            var srcLayout = GetLayoutKind(srcType, srcIsExtended);
            var tgtLayout = GetLayoutKind(tgtType, tgtIsExtended);
            return srcLayout == tgtLayout;
        }

        private enum LayoutKind { Gamepad, Extended, Midi, Kbm }

        private static LayoutKind GetLayoutKind(VirtualControllerType type, bool isExtended)
        {
            if (type is VirtualControllerType.Extended or VirtualControllerType.Nintendo && isExtended)
                return LayoutKind.Extended;
            if (type == VirtualControllerType.Midi)
                return LayoutKind.Midi;
            if (type == VirtualControllerType.KeyboardMouse)
                return LayoutKind.Kbm;
            return LayoutKind.Gamepad; // Xbox, PlayStation, Extended gamepad preset
        }

        /// <summary>
        /// Returns a short display label for the layout type (e.g., "Xbox", "Extended Custom", "MIDI", "KB+M").
        /// </summary>
        public static string GetLayoutLabel(VirtualControllerType type, bool isExtended)
        {
            return GetLayoutKind(type, isExtended) switch
            {
                LayoutKind.Extended => type == VirtualControllerType.Nintendo
                    ? "Nintendo" : "Extended",
                LayoutKind.Midi       => "MIDI",
                LayoutKind.Kbm        => "KB+M",
                _ => type switch
                {
                    VirtualControllerType.Xbox    => "Xbox",
                    VirtualControllerType.PlayStation => "PlayStation",
                    VirtualControllerType.Extended       => "Extended (Gamepad)",
                    _ => type.ToString()
                }
            };
        }
    }
}
