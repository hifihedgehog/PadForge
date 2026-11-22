using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Common.ConfigImport
{
    /// <summary>
    /// Turns a DSX (Paliverse) 3.1+ profile, the JSON in a <c>.dsx</c> file,
    /// into a profile. The sections and their keys are read from published
    /// profiles (winkywong1998/DSX-profile-share) and from the MIT project
    /// that writes them (RAIZ-Zzz, <c>dualsense/profile.py</c>). The trigger
    /// modes and their parameters are DSX's own Mod System (Apache-2.0,
    /// <c>Models.cs</c> and its README), which the #529 encoder plays.
    ///
    /// <para>What DSX does not publish stays a fact on its line: the numbers
    /// its Button Mapping stores for inputs and controller buttons, the units
    /// of its speeds, and modes no published profile shows. A mode is read by
    /// the words of its name, which are the words that key its settings
    /// (<c>strobe_speed</c> for a strobe light, <c>flick_stick</c> for flick
    /// stick).</para>
    ///
    /// <para>DSX's patch notes (BETA 010 and 011) say its passthrough switches
    /// override a profile's adaptive trigger and LED settings, and that in
    /// Native Mode with Send Data to Device off DSX writes no LEDs. Those
    /// cases leave the controller to the game here too.</para>
    /// </summary>
    internal sealed class DsxTranslator
    {
        private const int PadId = 1;

        // A file from anywhere must not take the app down.
        private const int MaxBindings = 512;
        private const int MaxPresets = 64;
        private const int MaxComboInputs = 8;
        private const int MaxOutputs = 16;
        private const int MaxColors = 64;

        private readonly JsonElement _root;
        private readonly ImportBuilder _b = new();

        private bool _noVirtualPad;
        private bool _playStation;
        private bool _virtualAudio;
        private bool _triggersToGame;
        private bool _ledsToGame;
        private bool _nativeNoData;
        private bool _sticksSwapped;
        private bool _motionBound;

        /// <summary>The stick output a motion mode drives, or null.</summary>
        private string _motionStick;

        /// <summary>The physical sticks a mode other than passthrough took.</summary>
        private readonly HashSet<string> _stickTaken = new(StringComparer.Ordinal);

        private DsxTranslator(JsonElement root) => _root = root;

        public static ForeignImportResult Translate(JsonElement root, string path)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return ForeignImportResult.Refuse(ForeignConfigKind.Dsx, ImportReasons.FileNotConfig);

            var t = new DsxTranslator(root);
            t.ReadVirtualController();
            t.ReadStickModes();
            t.ReadMotion();
            t.BuildDefaultRows();
            t.ReadTouchpad();
            t.ReadTriggers();
            t.ReadLights();
            t.ReadHaptics();
            t.ReadMicButton();
            t.ReadMappingLab();
            if (t._noVirtualPad) t._b.DropPads();

            string name = Str(root, "name");
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path ?? "");
            t._b.Report.ConfigTitle = name;
            var profile = t._b.Finish(name, "");
            return new ForeignImportResult
            {
                Kind = ForeignConfigKind.Dsx,
                Profile = profile,
                Report = t._b.Report,
                ReadsKeyboard = t._b.ReadsKeyboard,
                ReadsMouse = t._b.ReadsMouse,
                ReadsController = t._b.ReadsController,
                Runs = t._b.Runs,
                Starts = t._b.Starts,
            };
        }

        // ── Reading JSON ────────────────────────────────────────────────

        private JsonElement? Section(string name) => Child(_root, name);

        private static JsonElement? Child(JsonElement? parent, string name)
            => parent is JsonElement p && p.ValueKind == JsonValueKind.Object
               && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

        private static string Str(JsonElement? parent, string name)
            => parent is JsonElement p && p.ValueKind == JsonValueKind.Object
               && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "").Trim()
                : "";

        private static double Num(JsonElement? parent, string name, double fallback)
        {
            if (parent is not JsonElement p || p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v))
                return fallback;
            return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : fallback;
        }

        private static int Int(JsonElement? parent, string name, int fallback)
        {
            double d = Num(parent, name, fallback);
            return d >= int.MinValue && d <= int.MaxValue ? (int)Math.Round(d) : fallback;
        }

        private static bool Bool(JsonElement? parent, string name, bool fallback)
        {
            if (parent is not JsonElement p || p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v))
                return fallback;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            };
        }

        private static IEnumerable<JsonElement> Items(JsonElement? parent, string name, int cap)
        {
            if (parent is not JsonElement p || p.ValueKind != JsonValueKind.Object
                || !p.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
                yield break;
            int n = 0;
            foreach (var item in v.EnumerateArray())
            {
                if (n++ >= cap) yield break;
                yield return item;
            }
        }

        private static string[] Words(string mode)
            => (mode ?? "").Split(new[] { '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>True when one word of a DSX mode name starts with
        /// <paramref name="word"/>, any case.</summary>
        private static bool Word(string mode, string word)
            => Words(mode).Any(w => w.StartsWith(word, StringComparison.OrdinalIgnoreCase));

        private static bool Is(string mode, string name) => string.Equals(mode, name, StringComparison.OrdinalIgnoreCase);

        private static bool IsNone(string mode)
            => mode.Length == 0 || Is(mode, "NONE") || Is(mode, "OFF") || Is(mode, "DISABLED");

        private static string Percent(double fraction)
            => (fraction * 100).ToString("0.##", CultureInfo.InvariantCulture) + " %";

        private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ── The virtual controller ──────────────────────────────────────

        private void ReadVirtualController()
        {
            var general = Section("controller_general");
            string device = Str(general, "ActiveVirtualDevice");
            const string Label = "Virtual controller";

            if (Word(device, "NATIVE"))
            {
                // The real pad goes to the game, as in DS4Windows' DInput
                // only mode: no virtual controller.
                _noVirtualPad = true;
                _nativeNoData = !Bool(general, "SendOutputData", false);
                _b.Clean(Label, device, Strings.Instance.Import_Line_Native);
                return;
            }
            if (Word(device, "DUALSHOCK"))
            {
                string subtype = Str(general, "ActiveDualShockSubtype");
                string id = Words(subtype).Contains("2") ? "dualshock-4-v2" : "dualshock-4-v1";
                _b.DeclarePad(PadId, VirtualControllerType.PlayStation, id);
                _playStation = true;
                _ledsToGame = Bool(general, "UseDualShockLEDs", false);
                _b.Clean(Label, device + " " + subtype, id);
                RumbleSwitch(general, "UseDualShockRumble");
                return;
            }
            if (Word(device, "DUALSENSE"))
            {
                string subtype = Str(general, "ActiveDualSenseSubtypeV2");
                if (subtype.Length == 0) subtype = Str(general, "ActiveDualSenseSubtype");
                bool edge = Word(subtype, "EDGE");
                _virtualAudio = Word(subtype, "AUDIO");
                string id = (edge ? "dualsense-edge" : "dualsense") + (_virtualAudio ? "-composite" : "");
                _b.DeclarePad(PadId, VirtualControllerType.PlayStation, id);
                _playStation = true;
                _triggersToGame = Bool(general, "UseDualSenseTriggers", false);
                _ledsToGame = Bool(general, "UseDualSenseLEDs", false);
                _b.Clean(Label, device + " " + subtype, id);
                RumbleSwitch(general, "UseDualSenseRumble");
                return;
            }

            _b.DeclarePad(PadId, VirtualControllerType.Xbox, "xbox-360-wired");
            string xboxSubtype = Str(general, "ActiveXboxSubtype");
            if (!Word(device, "XBOX"))
                _b.Skipped(ImportReasons.DsxModeName, Label, device, device);
            else if (xboxSubtype.Length > 0 && !Is(xboxSubtype, "GAMEPAD"))
                _b.Skipped(ImportReasons.DsxModeName, Label, xboxSubtype, xboxSubtype);
            else
                _b.Clean(Label, device, "xbox-360-wired");
            RumbleSwitch(general, "UseXboxRumble");
        }

        /// <summary>The virtual controller's rumble passthrough: off, the
        /// game's rumble never reaches the pad.</summary>
        private void RumbleSwitch(JsonElement? general, string key)
        {
            if (Bool(general, key, true)) return;
            _b.RumblePercent = "0";
            _b.Clean("Rumble", key + " off", "0 %");
        }

        // ── Default rows and the zones ──────────────────────────────────

        /// <summary>With no binding, DSX sends each control to the virtual
        /// controller's own, the defaults DS4Windows' import builds too.</summary>
        private static readonly (string Control, string Descriptor, string Target)[] DefaultButtons =
        {
            ("Cross", "Gamepad ButtonA", "ButtonA"),
            ("Circle", "Gamepad ButtonB", "ButtonB"),
            ("Square", "Gamepad ButtonX", "ButtonX"),
            ("Triangle", "Gamepad ButtonY", "ButtonY"),
            ("L1", "Gamepad LeftShoulder", "LeftShoulder"),
            ("R1", "Gamepad RightShoulder", "RightShoulder"),
            ("L3", "Gamepad LeftStick", "LeftThumbButton"),
            ("R3", "Gamepad RightStick", "RightThumbButton"),
            ("Create", "Gamepad ButtonBack", "ButtonBack"),
            ("Options", "Gamepad ButtonStart", "ButtonStart"),
            ("PS", "Gamepad ButtonGuide", "ButtonGuide"),
            ("DpadUp", "Gamepad DPadUp", "DPadUp"),
            ("DpadDown", "Gamepad DPadDown", "DPadDown"),
            ("DpadLeft", "Gamepad DPadLeft", "DPadLeft"),
            ("DpadRight", "Gamepad DPadRight", "DPadRight"),
        };

        /// <summary>The control a default row reads under, apart from the
        /// control a binding on the same input reads under, so muting the
        /// input takes back the default row alone.</summary>
        private const string NativePrefix = "dsx:native:";

        private void BuildDefaultRows()
        {
            // Native Mode leaves the pad to the game, zones and all.
            if (_noVirtualPad) return;
            foreach (var (control, descriptor, target) in DefaultButtons)
                _b.Bind(ImportBuilder.BaseLayer, NativePrefix + control, target, new MappingSource { Descriptor = descriptor }, PadId);

            var zones = Section("controller_deadzone");
            _b.Bind(ImportBuilder.BaseLayer, NativePrefix + "L2", "LeftTrigger", TriggerSource(zones, "lt", "Gamepad LeftTrigger"), PadId);
            _b.Bind(ImportBuilder.BaseLayer, NativePrefix + "R2", "RightTrigger", TriggerSource(zones, "rt", "Gamepad RightTrigger"), PadId);

            var general = Section("controller_general");
            bool bothX = Bool(general, "IsBothAnalogXInverted", false);
            bool bothY = Bool(general, "IsBothAnalogYInverted", false);
            _sticksSwapped = Bool(Section("controller_sticks"), "joysticks_swapped", false);
            foreach (string side in new[] { "Left", "Right" })
            {
                if (_stickTaken.Contains(side)) continue;
                BindStick(side, zones,
                    Bool(general, "Is" + side + "AnalogXInverted", false) || bothX,
                    Bool(general, "Is" + side + "AnalogYInverted", false) || bothY);
            }
            if (_sticksSwapped)
                _b.Clean("Sticks", "Swap sticks", "LeftThumbAxis, RightThumbAxis");
            _b.Clean(Strings.Instance.Import_Line_Controller, Strings.Instance.Import_Line_Native, "");
        }

        /// <summary>A trigger's start and end in percent of its pull: the
        /// pull reads nothing before the start and full at the end.</summary>
        private MappingSource TriggerSource(JsonElement? zones, string prefix, string descriptor)
        {
            var src = new MappingSource { Descriptor = descriptor };
            string label = prefix == "lt" ? "Left trigger" : "Right trigger";
            double start = Math.Clamp(Num(zones, prefix + "_start", 0), 0, 99) / 100.0;
            double end = Math.Clamp(Num(zones, prefix + "_end", 100), 1, 100) / 100.0;
            if (start > 0)
            {
                // A trigger's dead zone folds onto its card with a shape
                // stamped, as DS4Windows' import does.
                src.ParamStickDeadZoneShape = 1;
                src.ParamStickDeadZoneInner = start;
            }
            if (end < 1 && end > start) src.ParamRangeOuter = end;
            if (Bool(zones, prefix + "_advanced_mode", false))
                _b.Partial(ImportReasons.DsxShaping, label, "Advanced", "", "Advanced");
            else if (start > 0 || end < 1)
                _b.Clean(label, Percent(start) + " - " + Percent(end), descriptor);
            return src;
        }

        /// <summary>A stick's zone as its two rows carry it: the dead zone's
        /// shape and size, the anti-dead zone, and where the push reads full.
        /// DSX's advanced zone names that point its saturation, DirectInput's
        /// term for it, and its range the share of the output the stick
        /// reaches, DirectInput's range.</summary>
        private (int Shape, double Inner, double Anti, double Outer, double Scale) StickZone(JsonElement? zones, string side)
        {
            string prefix = side + "Stick_";
            string label = side + " stick zone";
            string mode = Str(zones, prefix + "DeadzoneMode");
            if (Word(mode, "ADVANCED"))
            {
                double inner = Math.Clamp(Num(zones, prefix + "AdvancedDeadzone_v2", 0), 0, 99) / 100.0;
                double anti = Math.Clamp(Num(zones, prefix + "AdvancedAntiDeadzone_v2", 0), 0, 99) / 100.0;
                double outer = Math.Clamp(Num(zones, prefix + "AdvancedSaturation_v2", 1.0), 0.01, 1.0);
                double range = Num(zones, prefix + "AdvancedRange_v2", 100);
                double scale = Math.Clamp(range, 1, 100) / 100.0;
                // DSX publishes none of its advanced zone's arithmetic, its
                // shape included, so every advanced zone carries the line.
                var shaping = new List<string> { "Advanced" };
                if (range != 100) shaping.Add("Range");
                if (Num(zones, prefix + "AdvancedSensitivity_v2", 0) != 0) shaping.Add("Sensitivity");
                if (anti > 0 && Bool(zones, prefix + "AdvancedSmoothAntiDeadzone_v2", false)) shaping.Add("Smooth Anti-Deadzone");
                if (Bool(zones, prefix + "AdvancedSquareDiagonals_v2", false)) shaping.Add("Square Diagonals");
                string named = string.Join(", ", shaping);
                _b.Partial(ImportReasons.DsxShaping, label, named, side + "ThumbAxis", named);
                return (2, inner, anti, outer < 1 && outer > inner ? outer : 1.0, scale);
            }
            if (Word(mode, "PASSTHROUGH"))
                return (0, 0, 0, 1, 1);
            if (mode.Length > 0 && !Word(mode, "SIMPLE"))
            {
                _b.Skipped(ImportReasons.DsxModeName, label, mode, mode);
                return (0, 0, 0, 1, 1);
            }
            string shape = Str(zones, prefix + "SimpleDeadzoneMode");
            double dead = Math.Clamp(Num(zones, prefix + "SimpleDeadzone", 0), 0, 99) / 100.0;
            if (dead <= 0) return (0, 0, 0, 1, 1);
            if (shape.Length > 0 && !Word(shape, "RADIAL") && !Word(shape, "AXIAL"))
            {
                _b.Skipped(ImportReasons.DsxModeName, label, shape, shape);
                return (0, 0, 0, 1, 1);
            }
            int code = Word(shape, "AXIAL") ? 1 : 2;
            _b.Clean(label, (code == 2 ? "Radial " : "Axial ") + Percent(dead), side + "ThumbAxis");
            return (code, dead, 0, 1, 1);
        }

        /// <summary>The rows of a stick DSX passes through. The zone and the
        /// inversion belong to the physical stick, and with the sticks swapped
        /// it drives the other side's output.</summary>
        private void BindStick(string side, JsonElement? zones, bool invertX, bool invertY)
        {
            var zone = StickZone(zones, side);
            string output = _sticksSwapped ? (side == "Left" ? "Right" : "Left") : side;
            MappingSource Make(string axis, bool invert)
            {
                var src = new MappingSource { Descriptor = "Gamepad " + side + "Stick" + axis };
                if (zone.Shape > 0 && zone.Inner > 0)
                {
                    src.ParamStickDeadZoneShape = zone.Shape;
                    src.ParamStickDeadZoneInner = zone.Inner;
                }
                if (zone.Anti > 0) src.ParamAntiDeadzone = zone.Anti;
                if (zone.Outer < 1) src.ParamRangeOuter = zone.Outer;
                if (invert) src.Invert = true;
                return src;
            }
            _b.Bind(ImportBuilder.BaseLayer, NativePrefix + side + "StickX", output + "ThumbAxisX", Make("X", invertX), PadId);
            _b.Bind(ImportBuilder.BaseLayer, NativePrefix + side + "StickY", output + "ThumbAxisY", Make("Y", invertY), PadId);
            if (zone.Scale < 1 && output == _motionStick)
            {
                // A range scales the whole output, and the motion drives this
                // one too, where DSX's range shapes the stick alone.
                _b.Skipped(ImportReasons.ControllerSetting, side + " stick zone", "Range", "Range");
            }
            else if (zone.Scale < 1)
            {
                // The anti-dead zone rides each source as the shape's floor.
                _b.StickShape(ImportBuilder.BaseLayer, output + "ThumbAxisX", zone.Anti, zone.Scale, PadId);
                _b.StickShape(ImportBuilder.BaseLayer, output + "ThumbAxisY", zone.Anti, zone.Scale, PadId);
            }
        }

        // ── Stick modes ─────────────────────────────────────────────────

        private void ReadStickModes()
        {
            var sticks = Section("controller_sticks");
            ReadStickMode(sticks, "Left", "ls", "left_stick");
            ReadStickMode(sticks, "Right", "rs", "right_stick");
            string both = Str(sticks, "both_mode_updated");
            if (!IsNone(both))
                _b.Skipped(ImportReasons.DsxModeName, "Both sticks", both, both);
        }

        private static ImportInput StickHalf(string side, string axis, bool lower) => new()
        {
            Control = "dsx:" + side + "Stick" + axis + (lower ? "-" : "+"),
            Label = side + " stick " + (axis == "X" ? (lower ? "Left" : "Right") : (lower ? "Up" : "Down")),
            Descriptor = "Gamepad " + side + "Stick" + axis,
            HalfAxis = true,
            Invert = lower,
            IsAnalog = true,
        };

        private static ImportInput[] StickDirections(string side) => new[]
        {
            StickHalf(side, "Y", true), StickHalf(side, "Y", false), StickHalf(side, "X", true), StickHalf(side, "X", false),
        };

        private void ReadStickMode(JsonElement? sticks, string side, string pre, string longPre)
        {
            string mode = Str(sticks, pre + "_mode_updated");
            if (IsNone(mode)) return;
            string label = side + " stick";
            const string Base = ImportBuilder.BaseLayer;

            if (Word(mode, "FLICK"))
            {
                _stickTaken.Add(side);
                var src = new MappingSource { Descriptor = "Flick Stick " + side };
                // A share of the stick's push, PadForge's own measure.
                double threshold = Num(sticks, pre + "_flick_stick_threshold", 0);
                if (threshold > 0 && threshold < 1) src.ParamFlickThreshold = threshold;
                _b.Bind(Base, "dsx:" + side + ":flick", "KbmMouseX", src, 0);
                _b.ReadsMouse = true;
                _b.Clean(label, "Flick stick", "KbmMouseX");
                // The turn per flick and the flick's easing are DSX's distance
                // and duration, which it gives no unit.
                _b.Partial(ImportReasons.DsxUnit, label, "Flick distance and duration", "KbmMouseX");
                string profile = Str(sticks, pre + "_flick_stick_profile");
                if (profile.Length > 0)
                    _b.Partial(ImportReasons.DsxShaping, label, profile, "KbmMouseX", profile);
                Modifiers(label, sticks, pre + "_stick_to_flick_stick");
                return;
            }
            if (Word(mode, "MOUSE"))
            {
                _stickTaken.Add(side);
                _b.Bind(Base, "dsx:" + side + "StickX", "KbmMouseX", new MappingSource { Descriptor = "Gamepad " + side + "StickX" }, 0);
                _b.Bind(Base, "dsx:" + side + "StickY", "KbmMouseY", new MappingSource { Descriptor = "Gamepad " + side + "StickY" }, 0);
                _b.ReadsMouse = true;
                _b.Clean(label, "Stick to mouse", "KbmMouseX, KbmMouseY");
                _b.Partial(ImportReasons.DsxUnit, label, "Mouse speeds", "KbmMouseX, KbmMouseY");
                if (Bool(sticks, pre + "_stick_to_mouse_is_use_mmo_mode", false))
                {
                    // MMO mode holds a mouse button while the stick moves the
                    // pointer, as MMO cameras turn with it held.
                    var button = ImportOutput.Mouse(Bool(sticks, pre + "_stick_to_mouse_is_use_lmb_for_mmo_mode", true) ? 0 : 1);
                    foreach (var dir in StickDirections(side)) _b.Bind(Base, dir, button);
                    _b.Partial(ImportReasons.DsxUnit, label, "MMO mode", button.Target);
                }
                Modifiers(label, sticks, pre + "_stick_to_mouse");
                return;
            }
            if (Word(mode, "SCROLL"))
            {
                _stickTaken.Add(side);
                var dirs = StickDirections(side);
                _b.Bind(Base, dirs[0], ImportOutput.MouseWheel(false, true));
                _b.Bind(Base, dirs[1], ImportOutput.MouseWheel(false, false));
                _b.Bind(Base, dirs[2], ImportOutput.MouseWheel(true, true));
                _b.Bind(Base, dirs[3], ImportOutput.MouseWheel(true, false));
                _b.ReadsMouse = true;
                _b.Clean(label, "Stick to scroll", "KbmScroll, KbmScrollH");
                _b.Partial(ImportReasons.DsxUnit, label, "Scroll speeds", "KbmScroll, KbmScrollH");
                Modifiers(label, sticks, pre + "_stick_to_scroll");
                return;
            }
            if (Word(mode, "WASD") || Word(mode, "KEYBOARD") || Word(mode, "ARROW"))
            {
                _stickTaken.Add(side);
                string scheme = Str(sticks, pre + "_stick_to_wasd_control_scheme");
                if (scheme.Length == 0) scheme = Word(mode, "ARROW") ? "ARROWS" : Word(mode, "KEYBOARD") ? "CUSTOM" : "WASD";
                KeyScheme(label, scheme, StickDirections(side), sticks, longPre + "_to_keyboard_custom_");
                Zones(label, Child(sticks, longPre + "_to_keyboard_directional_zones"));
                return;
            }
            if (Word(mode, "CONTROLLER") || Word(mode, "BUTTON"))
            {
                _stickTaken.Add(side);
                string scheme = Str(sticks, pre + "_stick_to_controller_buttons_control_scheme");
                ButtonScheme(label, scheme, StickDirections(side), sticks, longPre + "_to_cb_custom_");
                Zones(label, Child(sticks, longPre + "_to_controller_buttons_directional_zones"));
                return;
            }
            _b.Skipped(ImportReasons.DsxModeName, label, mode, mode);
        }

        /// <summary>A mouse mode's precise and fast speed buttons, which DSX
        /// names by numbers it does not publish.</summary>
        private void Modifiers(string label, JsonElement? section, string prefix)
        {
            int precise = Int(section, prefix + "_precise_button", 0);
            int fast = Int(section, prefix + "_fast_button", 0);
            if (precise == 0 && fast == 0) return;
            string numbers = precise.ToString(CultureInfo.InvariantCulture) + ", " + fast.ToString(CultureInfo.InvariantCulture);
            _b.Skipped(ImportReasons.DsxNumber, label, "Precise and fast speed buttons", numbers);
        }

        /// <summary>DSX's direction zones, four of them 45 degrees each by
        /// default. PadForge presses a direction past the stick's threshold on
        /// that axis, so a diagonal presses two.</summary>
        private void Zones(string label, JsonElement? zones)
        {
            if (zones == null) return;
            string layout = Str(zones, "direction_layout");
            bool plain = (layout.Length == 0 || Is(layout, "FourDirections"))
                         && Num(zones, "rotation_degrees", 0) == 0
                         && !Bool(zones, "use_manual_diagonal_mappings", false);
            _b.Partial(ImportReasons.DsxShaping, label, plain ? "Direction zones" : "Direction zones: " + layout, "", "Direction zones");
        }

        /// <summary>Four directions to keys: WASD, the arrow keys, or the
        /// keys DSX's custom scheme names.</summary>
        private void KeyScheme(string label, string scheme, ImportInput[] dirs, JsonElement? section, string customPrefix)
        {
            if (Word(scheme, "WASD"))
            {
                BindDirections(dirs, new[] { 0x57, 0x53, 0x41, 0x44 });
                _b.Clean(label, "WASD", "W, S, A, D");
            }
            else if (Word(scheme, "ARROW"))
            {
                BindDirections(dirs, new[] { 0x26, 0x28, 0x25, 0x27 });
                _b.Clean(label, "Arrow keys", "Up, Down, Left, Right");
            }
            else if (Word(scheme, "CUSTOM"))
            {
                string[] names = { "up", "down", "left", "right" };
                for (int i = 0; i < 4; i++)
                    CustomKey(label + " " + names[i], dirs[i], Child(section, customPrefix + names[i] + "_direction"));
                return;
            }
            else
            {
                _b.Skipped(ImportReasons.DsxModeName, label, scheme, scheme);
                return;
            }
            // DSX's press threshold has no unit, so a direction presses at
            // PadForge's own, half the travel.
            _b.Partial(ImportReasons.DsxUnit, label, "Press threshold", "");
        }

        private void BindDirections(ImportInput[] dirs, int[] vks)
        {
            for (int i = 0; i < 4; i++) _b.Bind(ImportBuilder.BaseLayer, dirs[i], ImportOutput.Key((byte)vks[i]));
            _b.ReadsKeyboard = true;
        }

        /// <summary>Four directions to controller buttons: the D-pad, or the
        /// buttons DSX's custom scheme names by its own numbers.</summary>
        private void ButtonScheme(string label, string scheme, ImportInput[] dirs, JsonElement? section, string customPrefix)
        {
            if (scheme.Length == 0 || Word(scheme, "PAD"))
            {
                if (_noVirtualPad)
                {
                    // Native Mode has no virtual D-pad to press.
                    _b.Skipped(ImportReasons.ControllerSetting, label, scheme, scheme);
                    return;
                }
                string[] targets = { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" };
                for (int i = 0; i < 4; i++) _b.Bind(ImportBuilder.BaseLayer, dirs[i], ImportOutput.Pad(targets[i]));
                _b.Clean(label, "D-pad", "DPadUp, DPadDown, DPadLeft, DPadRight");
                _b.Partial(ImportReasons.DsxUnit, label, "Press threshold", "");
                return;
            }
            if (Word(scheme, "CUSTOM"))
            {
                var numbers = new[] { "up", "down", "left", "right" }
                    .Select(d => Int(section, customPrefix + d + "_direction", 0).ToString(CultureInfo.InvariantCulture));
                _b.Skipped(ImportReasons.DsxNumber, label, scheme, string.Join(", ", numbers));
                return;
            }
            _b.Skipped(ImportReasons.DsxModeName, label, scheme, scheme);
        }

        /// <summary>One direction of a custom key scheme. No published profile
        /// sets one, so the key is read as the WPF key name it is stored by,
        /// DSX being a WPF program, and the line says so.</summary>
        private void CustomKey(string label, ImportInput dir, JsonElement? binding)
        {
            if (binding == null) return;
            string type = Str(binding, "KeyboardMappingType");
            if (IsNone(type)) return;
            var keys = new List<byte>();
            string shown;
            if (Word(type, "TEXT"))
            {
                string text = Str(binding, "SimulatedText");
                if (text.Length == 0) return;
                _b.AddMacro(new ImportMacro
                {
                    Name = label,
                    Inputs = new[] { dir },
                    Mode = MacroTriggerMode.OnPress,
                    PadId = PadId,
                    Actions = new[] { new ActionData { Type = MacroActionType.TextBlock, TextContent = text } },
                });
                _b.ReadsKeyboard = true;
                _b.Partial(ImportReasons.DsxKeyName, label, type, "TextBlock", type);
                return;
            }
            if (Word(type, "MIX"))
            {
                shown = Str(binding, "MixMatchModifierKeys");
                if (!ModifierKeys(shown, keys)) { _b.Skipped(ImportReasons.DsxModeName, label, shown, shown); return; }
                for (int i = 1; i <= 3; i++)
                {
                    string name = Str(binding, "MixMatchHotKey" + i.ToString(CultureInfo.InvariantCulture));
                    if (IsNone(name)) continue;
                    shown += " + " + name;
                    if (KeyFromName(name) is not byte vk) { _b.Skipped(ImportReasons.DsxModeName, label, name, name); return; }
                    keys.Add(vk);
                }
            }
            else
            {
                string modifiers = Str(binding, "ModifierKey");
                string key = Str(binding, "HotKey");
                shown = modifiers + " + " + key;
                if (!ModifierKeys(modifiers, keys)) { _b.Skipped(ImportReasons.DsxModeName, label, modifiers, modifiers); return; }
                if (!IsNone(key))
                {
                    if (KeyFromName(key) is not byte vk) { _b.Skipped(ImportReasons.DsxModeName, label, key, key); return; }
                    keys.Add(vk);
                }
            }
            if (keys.Count == 0) return;
            BindHeld(label, new[] { dir }, keys.Select(ImportOutput.Key).ToList(), "dsx:" + label);
            _b.Partial(ImportReasons.DsxKeyName, label, shown, string.Join(" + ", keys.Select(ImportOutput.KeyName)), shown);
        }

        /// <summary>WPF ModifierKeys, the flags a hot key's modifiers are
        /// stored as: None, Alt, Control, Shift and Windows.</summary>
        private static bool ModifierKeys(string text, List<byte> keys)
        {
            if (IsNone(text)) return true;
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (part.ToLowerInvariant())
                {
                    case "none": break;
                    case "alt": keys.Add(0x12); break;
                    case "control": keys.Add(0x11); break;
                    case "shift": keys.Add(0x10); break;
                    case "windows": keys.Add(0x5B); break;
                    default: return false;
                }
            }
            return true;
        }

        /// <summary>A WPF key name as its Windows virtual-key code.</summary>
        private static byte? KeyFromName(string name)
        {
            if (!Enum.TryParse(name, ignoreCase: true, out System.Windows.Input.Key key) || key == System.Windows.Input.Key.None)
                return null;
            int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
            return vk is > 0 and < 256 ? (byte)vk : null;
        }

        /// <summary>Outputs held while the inputs are, the reWASD import's
        /// own lowering: rows for what a row carries, the rest a hold pair.</summary>
        private void BindHeld(string name, IReadOnlyList<ImportInput> inputs, IReadOnlyList<ImportOutput> outs, string control)
        {
            var gates = inputs.Skip(1).ToList();
            foreach (var o in outs.Where(o => o.HasRow)) _b.Bind(ImportBuilder.BaseLayer, inputs[0], o, gates);
            var held = outs.Where(o => !o.HasRow).Select(o => o.Down()).Where(a => a != null).ToArray();
            if (held.Length > 0)
                _b.AddHoldPair(new ImportMacro
                {
                    Name = name,
                    Control = control,
                    Inputs = inputs,
                    Mode = MacroTriggerMode.OnPress,
                    PadId = PadId,
                    Actions = held,
                }, ImportBuilder.ReleaseOf(held));
            MarkReads(outs);
        }

        private void MarkReads(IReadOnlyList<ImportOutput> outs)
        {
            if (outs.Any(o => o.Kind == ImportOutputKind.Key)) _b.ReadsKeyboard = true;
            if (outs.Any(o => o.Kind is ImportOutputKind.MouseButton or ImportOutputKind.MouseAxis or ImportOutputKind.Wheel)) _b.ReadsMouse = true;
        }

        // ── Motion ──────────────────────────────────────────────────────

        /// <summary>PadForge's gyro reaches full deflection at 500 degrees per
        /// second (SourceCoercion.GyroScale).</summary>
        private const double GyroFullScaleDegrees = 500;

        /// <summary>The DualSense's own motion axes: X is pitch, Y is yaw and
        /// Z is roll. DSX's six swap switches pair them, so they name these
        /// three, and a swap of one axis turns it around. A mode's own switch
        /// stands over the shared one when it is set.</summary>
        private static (string Horizontal, bool InvertH, string Vertical, bool InvertV, bool Roll) MotionAxes(JsonElement? motion, string prefix)
        {
            bool Flag(string name)
            {
                if (motion is JsonElement m && m.ValueKind == JsonValueKind.Object && m.TryGetProperty(prefix + name, out var v)
                    && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return v.GetBoolean();
                return Bool(motion, "motion_" + name, false);
            }
            string pitch = "Gyro Pitch", yaw = "Gyro Yaw", roll = "Gyro Roll";
            bool invPitch = Flag("is_swap_x_axis"), invYaw = Flag("is_swap_y_axis"), invRoll = Flag("is_swap_z_axis");
            if (Flag("is_swap_x_y_axis")) { (pitch, yaw) = (yaw, pitch); (invPitch, invYaw) = (invYaw, invPitch); }
            if (Flag("is_swap_x_z_axis")) { (pitch, roll) = (roll, pitch); (invPitch, invRoll) = (invRoll, invPitch); }
            if (Flag("is_swap_y_z_axis")) { (yaw, roll) = (roll, yaw); (invYaw, invRoll) = (invRoll, invYaw); }
            bool useX = Bool(motion, prefix + "is_use_x_axis", Bool(motion, "motion_is_use_x_axis", true));
            bool useY = Bool(motion, prefix + "is_use_y_axis", Bool(motion, "motion_is_use_y_axis", true));
            bool useZ = Bool(motion, prefix + "is_use_z_axis", Bool(motion, "motion_is_use_z_axis", false));
            return (useY ? yaw : null, invYaw, useX ? pitch : null, invPitch, useZ);
        }

        private void ReadMotion()
        {
            var motion = Section("controller_motion");
            string mode = Str(motion, "motion_mode");
            const string Label = "Motion";
            const string Base = ImportBuilder.BaseLayer;
            if (IsNone(mode) || Word(mode, "PASSTHROUGH"))
            {
                if (_playStation) BindMotionPassthrough();
                return;
            }

            if (Word(mode, "MOUSE"))
            {
                var axes = MotionAxes(motion, "motion_to_mouse_");
                if (axes.Horizontal != null)
                    _b.Bind(Base, "dsx:gyro:x", "KbmMouseX", new MappingSource { Descriptor = axes.Horizontal, Invert = axes.InvertH }, 0);
                if (axes.Vertical != null)
                    _b.Bind(Base, "dsx:gyro:y", "KbmMouseY", new MappingSource { Descriptor = axes.Vertical, Invert = axes.InvertV }, 0);
                _b.ReadsMouse = true;
                _b.Clean(Label, "Motion to mouse", "KbmMouseX, KbmMouseY");
                MotionLines(Label, axes.Roll, "KbmMouseX, KbmMouseY");
                Modifiers(Label, motion, "motion_to_mouse");
                if (_playStation && Bool(motion, "motion_to_mouse_is_use_passthrough", true)) BindMotionPassthrough();
                return;
            }
            if (Word(mode, "JOYSTICK") || Word(mode, "STICK"))
            {
                string side = Word(mode, "LEFT") ? "Left" : Word(mode, "RIGHT") ? "Right" : null;
                if (side == null)
                {
                    _b.Skipped(ImportReasons.DsxModeName, Label, mode, mode);
                    return;
                }
                if (_noVirtualPad)
                {
                    // Native Mode has no virtual stick for the motion to move.
                    _b.Skipped(ImportReasons.ControllerSetting, Label, mode, mode);
                    return;
                }
                _motionStick = side;
                string pre = "motion_to_" + side.ToLowerInvariant() + "_stick_";
                var axes = MotionAxes(motion, pre);
                // The stick reads full at DSX's rate, in the degrees per second
                // the setting's name gives.
                double full = Math.Clamp(Num(motion, pre + "max_output_speed_deg_s", 180), 1, 2000);
                double outer = Math.Clamp(full / GyroFullScaleDegrees, 0.002, 1.0);
                double floor = Math.Clamp(Num(motion, pre + "min_output_percent", 0), 0, 99) / 100.0;
                MappingSource Row(string descriptor, bool invert) => new()
                {
                    Descriptor = descriptor,
                    Invert = invert,
                    ParamRangeOuter = outer < 1 ? outer : 0,
                    ParamAntiDeadzone = floor,
                };
                if (axes.Horizontal != null)
                    _b.Bind(Base, "dsx:gyro:x", side + "ThumbAxisX", Row(axes.Horizontal, axes.InvertH), PadId);
                // A stick Y row's positive value is down and a pitch read is
                // nose up, so the row turns it.
                if (axes.Vertical != null)
                    _b.Bind(Base, "dsx:gyro:y", side + "ThumbAxisY", Row(axes.Vertical, !axes.InvertV), PadId);
                _b.Clean(Label, mode + " " + N(full) + " deg/s", side + "ThumbAxis");
                // A cap scales the stick's whole output, the real stick's
                // too, where DSX caps the motion's share alone.
                if (Num(motion, pre + "max_output_percent", 100) < 100)
                    _b.Skipped(ImportReasons.ControllerSetting, Label, "Maximum output", "Maximum output");
                MotionLines(Label, axes.Roll, side + "ThumbAxis");
                Modifiers(Label, motion, pre.TrimEnd('_'));
                if (_playStation && Bool(motion, pre + "is_use_passthrough", true)) BindMotionPassthrough();
                return;
            }
            if (Word(mode, "KEYBOARD") || Word(mode, "WASD"))
            {
                KeyScheme(Label, Str(motion, "motion_to_keyboard_control_scheme"), MotionDirections(motion, "motion_to_keyboard_"),
                    motion, "motion_to_keyboard_custom_");
                MotionLines(Label, false, "");
                if (_playStation && Bool(motion, "motion_to_keyboard_is_use_passthrough", true)) BindMotionPassthrough();
                return;
            }
            if (Word(mode, "CONTROLLER") || Word(mode, "BUTTON"))
            {
                ButtonScheme(Label, Str(motion, "motion_to_controller_buttons_control_scheme"),
                    MotionDirections(motion, "motion_to_controller_buttons_"), motion, "motion_to_cb_custom_");
                MotionLines(Label, false, "");
                if (_playStation && Bool(motion, "motion_to_controller_buttons_is_use_passthrough", true)) BindMotionPassthrough();
                return;
            }
            _b.Skipped(ImportReasons.DsxModeName, Label, mode, mode);
            if (_playStation) BindMotionPassthrough();
        }

        /// <summary>The gyro read as four directions, as the reWASD import
        /// reads its gyro directions: pitch for up and down, yaw for left and
        /// right. Pitch reads nose up as positive, so up is its upper half,
        /// and yaw a turn to the left, so left is its upper half.</summary>
        private static ImportInput[] MotionDirections(JsonElement? motion, string prefix)
        {
            var axes = MotionAxes(motion, prefix);
            string h = axes.Horizontal ?? "Gyro Yaw", v = axes.Vertical ?? "Gyro Pitch";
            static ImportInput Half(string descriptor, bool lower, string name) => new()
            {
                Control = "dsx:gyro:" + name,
                Label = "Gyro " + name,
                Descriptor = descriptor,
                HalfAxis = true,
                Invert = lower,
                IsAnalog = true,
            };
            return new[]
            {
                Half(v, axes.InvertV, "Up"), Half(v, !axes.InvertV, "Down"),
                Half(h, axes.InvertH, "Left"), Half(h, !axes.InvertH, "Right"),
            };
        }

        /// <summary>What every motion mode leaves open: the axes DSX names
        /// without saying which way they turn, its speeds and dead zones,
        /// which have no unit, and its roll, which PadForge does not add.</summary>
        private void MotionLines(string label, bool roll, string emitted)
        {
            _b.Partial(ImportReasons.DsxMotionAxes, label, "X, Y, Z", emitted);
            _b.Partial(ImportReasons.DsxUnit, label, "Motion speeds and dead zone", emitted);
            if (roll) _b.Partial(ImportReasons.DsxShaping, label, "Z axis", emitted, "Z axis");
        }

        /// <summary>A PlayStation virtual controller takes the pad's own motion,
        /// as DSX passes it through.</summary>
        private void BindMotionPassthrough()
        {
            if (_motionBound) return;
            _motionBound = true;
            _b.Bind(ImportBuilder.BaseLayer, "dsx:motion:gyro", "MotionGyro", new MappingSource { Descriptor = "Motion Gyro" }, PadId);
            _b.Bind(ImportBuilder.BaseLayer, "dsx:motion:accel", "MotionAccel", new MappingSource { Descriptor = "Motion Accel" }, PadId);
            _b.Clean("Motion", "Passthrough", "MotionGyro, MotionAccel");
        }

        // ── Touchpad ────────────────────────────────────────────────────

        private void ReadTouchpad()
        {
            var touch = Section("controller_touchpad");
            string mode = Str(touch, "TouchpadMode");
            const string Label = "Touchpad";
            const string Base = ImportBuilder.BaseLayer;

            bool toMouse = Word(mode, "MOUSE");
            bool passthrough = IsNone(mode) || Word(mode, "PASSTHROUGH")
                               || (toMouse && Bool(touch, "IsTouchpadToMousePassthroughEnabled", false));
            if (passthrough && _playStation)
            {
                void Row(string target, string descriptor)
                    => _b.Bind(Base, "dsx:tp:" + target, target, new MappingSource { Descriptor = descriptor }, PadId);
                Row("TouchpadX1", "Touchpad 0 Finger 0 X");
                Row("TouchpadY1", "Touchpad 0 Finger 0 Y");
                Row("TouchpadContact1", "Touchpad 0 Finger 0 Down");
                Row("TouchpadX2", "Touchpad 0 Finger 1 X");
                Row("TouchpadY2", "Touchpad 0 Finger 1 Y");
                Row("TouchpadContact2", "Touchpad 0 Finger 1 Down");
                Row("TouchpadClick", "Touchpad 0 Click");
                _b.Clean(Label, "Passthrough", "TouchpadX1, TouchpadClick");
            }

            if (toMouse)
            {
                _b.Bind(Base, "dsx:tp:x", "KbmMouseX", new MappingSource { Descriptor = "Touchpad 0 Finger 0 X" }, 0);
                _b.Bind(Base, "dsx:tp:y", "KbmMouseY", new MappingSource { Descriptor = "Touchpad 0 Finger 0 Y" }, 0);
                _b.ReadsMouse = true;
                if (Bool(touch, "IsTouchpadToMousePressToLeftClickEnabled", true))
                    _b.Bind(Base, new ImportInput { Control = "dsx:tp:click", Label = "Touchpad click", Descriptor = "Touchpad 0 Click" },
                        ImportOutput.Mouse(0));
                _b.Clean(Label, "Touchpad to mouse", "KbmMouseX, KbmMouseY");
                _b.Partial(ImportReasons.DsxUnit, Label, "Touchpad speeds", "KbmMouseX, KbmMouseY");
                int precise = Int(touch, "TouchpadToMousePreciseSensiButton", 0);
                int fast = Int(touch, "TouchpadToMouseFastSensiButton", 0);
                if (precise != 0 || fast != 0)
                    _b.Skipped(ImportReasons.DsxNumber, Label, "Precise and fast speed buttons",
                        precise.ToString(CultureInfo.InvariantCulture) + ", " + fast.ToString(CultureInfo.InvariantCulture));
                var gestures = new List<string>();
                if (Bool(touch, "IsTouchpadToMouseTapToLeftClickEnabled", false)) gestures.Add("Tap to left click");
                if (Bool(touch, "IsTouchpadToMouseTapToRightClickEnabled", false)) gestures.Add("Tap to right click");
                if (Bool(touch, "IsTouchpadToMouseScrollWithTwoFingersEnabled", false)) gestures.Add("Two-finger scroll");
                if (gestures.Count > 0)
                    _b.Partial(ImportReasons.TouchpadShape, Label, string.Join(", ", gestures), "");
            }
            else if (!passthrough)
            {
                _b.Skipped(ImportReasons.DsxModeName, Label, mode, mode);
            }

            // Touchpad Plus: nine touch zones, each with a mode of its own.
            foreach (string zone in new[] { "Entire", "LeftSide", "TopSide", "RightSide", "BottomSide", "TopLeft", "TopRight", "BottomLeft", "BottomRight" })
            {
                string zoneMode = Str(touch, "TouchpadPlus" + zone + "TouchpadMode");
                if (!IsNone(zoneMode)) _b.Skipped(ImportReasons.DsxModeName, Label, "Touchpad Plus " + zone, zoneMode);
            }
        }

        // ── Adaptive triggers ───────────────────────────────────────────

        /// <summary>A trigger setup as the mode the #529 encoder takes and its
        /// parameters, in the encoder's order: DSX's Mod System README gives
        /// FEEDBACK (start, strength), WEAPON (start, end, strength),
        /// VIBRATION (start, amplitude, frequency), SLOPE_FEEDBACK (start, end,
        /// two strengths) and the two ten-region modes, and Models.cs the
        /// rest. FEEDBACK, WEAPON and VIBRATION play as their twins 13, 16 and
        /// 17, which the encoder writes the same and the action's picker
        /// lists. Null for a name with no setup the profile keys.</summary>
        internal static (int Mode, int[] Values)? TriggerSetup(JsonElement? t, string side)
        {
            string type = Str(t, side + "_type");
            int V(string name, int fallback) => Int(t, side + "_" + name, fallback);
            int[] Run(string name, int count)
                => Enumerable.Range(1, count).Select(i => V(name + i.ToString(CultureInfo.InvariantCulture), 0)).ToArray();

            switch (type.ToUpperInvariant())
            {
                case "":
                case "OFF":
                case "NORMAL":
                    return (TriggerEffectCatalog.OffMode, Array.Empty<int>());
                case "FEEDBACK":
                    return (TriggerEffectCatalog.ResistanceMode, new[] { V("feedback_start", 1), V("feedback_resist", 8) });
                case "WEAPON":
                    return (TriggerEffectCatalog.WeaponMode, new[] { V("weapon_start", 2), V("weapon_end", 6), V("weapon_resist", 8) });
                case "VIBRATION":
                    return (TriggerEffectCatalog.VibrationMode, new[] { V("vib_start", 1), V("vib_amp", 8), V("vib_freq", 10) });
                case "SLOPE_FEEDBACK":
                    return (TriggerEffectCatalog.SlopeFeedbackMode,
                        new[] { V("slope_start", 1), V("slope_end", 9), V("slope_resist_start", 8), V("slope_resist_end", 1) });
                case "MULTIPLE_POSITION_FEEDBACK":
                    return (TriggerEffectCatalog.MultiplePositionFeedbackMode, Run("multifeedback_resist", 10));
                case "MULTIPLE_POSITION_VIBRATION":
                    return (TriggerEffectCatalog.MultiplePositionVibrationMode,
                        new[] { V("multifeedback_freq", 10) }.Concat(Run("multifeedback_amp", 10)).ToArray());
                case "BOW":
                    return (TriggerEffectCatalog.BowMode, new[] { V("bow_start", 1), V("bow_end", 8), V("bow_resist", 5), V("bow_snap", 8) });
                case "GALLOPING":
                    return (TriggerEffectCatalog.GallopingMode,
                        new[] { V("gallop_start", 1), V("gallop_end", 9), V("gallop_foot1", 3), V("gallop_foot2", 6), V("gallop_freq", 4) });
                case "MACHINE":
                    return (TriggerEffectCatalog.MachineMode,
                        new[] { V("machine_start", 1), V("machine_end", 9), V("machine_ampA", 2), V("machine_ampB", 7), V("machine_freq", 10), V("machine_period", 5) });
                case "CUSTOMTRIGGERVALUE":
                {
                    // CustomTriggerValueMode by name, then the seven forces.
                    if (!Enum.TryParse(Str(t, side + "_custom_mode"), ignoreCase: true, out CustomTriggerValueMode custom)
                        || !Enum.IsDefined(custom))
                        return null;
                    return (12, new[] { (int)custom }.Concat(Run("custom_force", 7)).ToArray());
                }
                // Vibration with fixed zones, the encoder's cases 8 and 19.
                case "VIBRATETRIGGER":
                case "VIBRATE_TRIGGER_10HZ":
                    return (TriggerEffectCatalog.VibrationMode, new[] { 0, 8, 10 });
                // DSX's named presets, which take no parameters.
                case "GAMECUBE": return (1, Array.Empty<int>());
                case "VERYSOFT": return (2, Array.Empty<int>());
                case "SOFT": return (3, Array.Empty<int>());
                case "HARD": return (4, Array.Empty<int>());
                case "VERYHARD": return (5, Array.Empty<int>());
                case "HARDEST": return (6, Array.Empty<int>());
                case "RIGID": return (7, Array.Empty<int>());
                case "CHOPPY": return (9, Array.Empty<int>());
                case "MEDIUM": return (10, Array.Empty<int>());
                case "VIBRATETRIGGERPULSE": return (11, Array.Empty<int>());
                default:
                    return null;
            }
        }

        /// <summary>DSX's Mod System CustomTriggerValueMode, by name.</summary>
        private enum CustomTriggerValueMode
        {
            OFF = 0, Rigid = 1, RigidA = 2, RigidB = 3, RigidAB = 4, Pulse = 5, PulseA = 6, PulseB = 7, PulseAB = 8,
            VibrateResistance = 9, VibrateResistanceA = 10, VibrateResistanceB = 11, VibrateResistanceAB = 12,
            VibratePulse = 13, VibratePulseA = 14, VibratePulseB = 15, VibratePulseAB = 16,
        }

        private static ActionData TriggerAction(int mode, int[] values, MacroTriggerEffectSide side, MacroTriggerEffectHold hold) => new()
        {
            Type = MacroActionType.TriggerEffect,
            TriggerEffectSide = side,
            TriggerEffectMode = mode,
            TriggerEffectParameters = TriggerEffectCatalog.FormatValues(values),
            TriggerEffectHold = hold,
            TriggerEffectMs = 500,
        };

        private void ReadTriggers()
        {
            var active = Section("controller_adaptive_triggers");
            if (active == null) return;
            foreach (var (side, effectSide, label) in new[]
                     {
                         ("lt", MacroTriggerEffectSide.Left, "Left trigger effect"),
                         ("rt", MacroTriggerEffectSide.Right, "Right trigger effect"),
                     })
            {
                string type = Str(active, side + "_type");
                var setup = TriggerSetup(active, side);
                if (setup == null)
                {
                    _b.Skipped(ImportReasons.DsxModeName, label, type, type);
                    continue;
                }
                if (setup.Value.Mode == TriggerEffectCatalog.OffMode) continue;
                if (_triggersToGame)
                {
                    _b.Partial(ImportReasons.DsxPassthrough, label, type, "", label);
                    continue;
                }
                if (_nativeNoData)
                {
                    _b.Partial(ImportReasons.DsxNoData, label, type, "", label);
                    continue;
                }
                if (TriggerEffectCatalog.Encode(setup.Value.Mode, setup.Value.Values) == null)
                {
                    // Outside the ranges DSX's own sliders allow.
                    string values = type + " " + TriggerEffectCatalog.FormatValues(setup.Value.Values);
                    _b.Skipped(ImportReasons.ControllerSetting, label, values, values);
                    continue;
                }
                _b.AddMacro(new ImportMacro
                {
                    Name = label,
                    Mode = MacroTriggerMode.Always,
                    PadId = PadId,
                    Actions = new[]
                    {
                        // Held a moment past each write, so it ends by itself
                        // when the profile switches away, and written four times
                        // a second, as DS4Windows' import does.
                        TriggerAction(setup.Value.Mode, setup.Value.Values, effectSide, MacroTriggerEffectHold.Duration),
                        new ActionData { Type = MacroActionType.Delay, DurationMs = 250 },
                    },
                });
                _b.Clean(label, type, "TriggerEffect");
                if (setup.Value.Mode == TriggerEffectCatalog.VibrationMode
                    && Bool(active, "Vibration_" + (side == "lt" ? "Left" : "Right") + "Trigger_DynamicRumbleSync", false))
                    _b.Skipped(ImportReasons.NoSuchSetting, label, "Dynamic Rumble Sync", "Dynamic Rumble Sync");
            }
            ReadTriggerPresets();
        }

        /// <summary>The saved presets beside the active one. Each becomes a
        /// macro the user can bind, as DSX switches to one from a shortcut.</summary>
        private void ReadTriggerPresets()
        {
            int activeIndex = Int(_root, "controller_adaptive_triggers_active_preset", 0);
            int index = 0;
            foreach (var preset in Items(_root, "controller_adaptive_triggers_preset_list", MaxPresets))
            {
                int i = index++;
                if (i == activeIndex || preset.ValueKind != JsonValueKind.Object) continue;
                string name = Str(preset, "preset_name");
                if (name.Length == 0) name = "Preset " + (i + 1).ToString(CultureInfo.InvariantCulture);
                if (!preset.TryGetProperty("lt_type", out _) && !preset.TryGetProperty("rt_type", out _))
                {
                    _b.Skipped(ImportReasons.ControllerSetting, "Trigger preset", name, name);
                    continue;
                }
                var actions = new List<ActionData>();
                foreach (var (side, effectSide) in new[] { ("lt", MacroTriggerEffectSide.Left), ("rt", MacroTriggerEffectSide.Right) })
                {
                    var setup = TriggerSetup(preset, side);
                    if (setup == null || TriggerEffectCatalog.Encode(setup.Value.Mode, setup.Value.Values) == null) continue;
                    actions.Add(setup.Value.Mode == TriggerEffectCatalog.OffMode
                        ? new ActionData { Type = MacroActionType.TriggerEffectStop, TriggerEffectSide = effectSide }
                        : TriggerAction(setup.Value.Mode, setup.Value.Values, effectSide, MacroTriggerEffectHold.UntilStopped));
                }
                if (actions.Count == 0) continue;
                _b.AddMacro(new ImportMacro { Name = "Triggers: " + name, PadId = PadId, Actions = actions, Unbound = true });
                _b.Partial(ImportReasons.DsxPreset, "Trigger preset", name, "TriggerEffect");
            }
        }

        // ── Lights ──────────────────────────────────────────────────────

        /// <summary>A DSX color, <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
        internal static (byte R, byte G, byte B)? Color(string text)
        {
            string hex = (text ?? "").Trim().TrimStart('#');
            if (hex.Length == 8) hex = hex[2..];
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                return null;
            return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        private static (byte R, byte G, byte B) Scale((byte R, byte G, byte B) c, double brightness)
            => ((byte)Math.Round(c.R * brightness), (byte)Math.Round(c.G * brightness), (byte)Math.Round(c.B * brightness));

        private void ReadLights()
        {
            var led = Section("controller_led");
            if (led == null) return;
            const string Label = "Lightbar";
            string mode = Str(led, "colormode");
            double brightness = Math.Clamp(Num(led, "brightness", 100), 0, 100) / 100.0;

            if (_ledsToGame) _b.Partial(ImportReasons.DsxPassthrough, Label, mode, "", Label);
            else if (_nativeNoData) _b.Partial(ImportReasons.DsxNoData, Label, mode, "", Label);
            else ReadLightbar(led, mode, brightness);

            foreach (var preset in Items(_root, "controller_led_preset_list", MaxPresets))
                if (preset.ValueKind == JsonValueKind.Object)
                    _b.Skipped(ImportReasons.ControllerSetting, "Lightbar preset", Str(preset, "preset_name"), Str(preset, "preset_name"));

            if (Bool(led, "battery_use_flash", false))
                _b.Skipped(ImportReasons.NoSuchSetting, Label, "Low battery flash", "Low battery flash");

            ReadPlayerLeds(led);
            ReadMicLed(led);
        }

        private void ReadLightbar(JsonElement? led, string mode, double brightness)
        {
            const string Label = "Lightbar";
            var actions = new List<ActionData>();

            if (Word(mode, "STATIC"))
            {
                var c = Color(Str(led, "static_color"));
                if (c != null)
                {
                    var (r, g, b) = Scale(c.Value, brightness);
                    actions.Add(ImportBuilder.ProfileColor(r, g, b));
                }
            }
            else if (Word(mode, "BREATH"))
            {
                var c = Color(Str(led, "breathing_color"));
                if (c != null)
                {
                    var (r, g, b) = Scale(c.Value, brightness);
                    // The reWASD import's breath: up through two steps, then a
                    // long fade back down.
                    actions.AddRange(ImportBuilder.LightStep((byte)(r / 4), (byte)(g / 4), (byte)(b / 4), 700, 0, 500));
                    actions.AddRange(ImportBuilder.LightStep((byte)(r / 2), (byte)(g / 2), (byte)(b / 2), 700, 0, 500));
                    actions.AddRange(ImportBuilder.LightStep(r, g, b, 200, 1500, 1700));
                    _b.Partial(ImportReasons.DsxUnit, Label, "Breathing speed", "LightbarColor");
                }
                if (Bool(led, "breathing_use_random_color", false))
                    _b.Skipped(ImportReasons.NoSuchSetting, Label, "Random color", "Random color");
            }
            else if (Word(mode, "STROB"))
            {
                var c = Color(Str(led, "strobe_color"));
                if (c != null)
                {
                    double strobe = Math.Clamp(Num(led, "strobe_brightness", 100), 0, 100) / 100.0;
                    var (r, g, b) = Scale(c.Value, brightness * strobe);
                    // The reWASD import's stroboscope.
                    actions.AddRange(ImportBuilder.LightStep(r, g, b, 150, 0, 100));
                    actions.AddRange(ImportBuilder.LightStep(0, 0, 0, 150, 0, 100));
                    _b.Partial(ImportReasons.DsxUnit, Label, "Strobe speed", "LightbarColor");
                }
                if (Bool(led, "strobe_use_random_color", false))
                    _b.Skipped(ImportReasons.NoSuchSetting, Label, "Random color", "Random color");
            }
            else if (Word(mode, "MULTI") || Word(mode, "RAINBOW") || Word(mode, "CYCLE"))
            {
                string multi = Str(led, "multicolor_mode");
                if (multi.Length == 0 || Word(multi, "HSL") || Word(multi, "RAINBOW"))
                {
                    // The reWASD import's own trip around the hues.
                    actions.AddRange(ImportBuilder.RainbowPattern(6000, brightness));
                }
                else if (Word(multi, "CUSTOM"))
                {
                    foreach (var item in Items(led, "multicolor_customcolor_color_list", MaxColors))
                    {
                        if (item.ValueKind != JsonValueKind.String || Color(item.GetString()) is not { } c) continue;
                        var (r, g, b) = Scale(c, brightness);
                        actions.AddRange(ImportBuilder.LightStep(r, g, b, 1100, 0, 1000));
                    }
                }
                else
                {
                    _b.Skipped(ImportReasons.DsxModeName, Label, multi, multi);
                    return;
                }
                _b.Partial(ImportReasons.DsxUnit, Label, "Color cycle speed", "LightbarColor");
            }
            else if (Word(mode, "BATTER"))
            {
                // PadForge's Battery light runs from one color at empty to one
                // at full: DSX's first and last.
                var list = Items(led, "battery_color_list", 16)
                    .Where(i => i.ValueKind == JsonValueKind.String)
                    .Select(i => Color(i.GetString()))
                    .Where(c => c != null)
                    .Select(c => c.Value)
                    .ToList();
                if (list.Count >= 2)
                    _b.BatteryColors = string.Create(CultureInfo.InvariantCulture,
                        $"{list[0].R},{list[0].G},{list[0].B};{list[^1].R},{list[^1].G},{list[^1].B}");
                actions.Add(new ActionData { Type = MacroActionType.LightbarModeSet, LightbarTargetMode = LightbarMode.Battery });
                if (list.Count > 2)
                    _b.Skipped(ImportReasons.NoSuchSetting, Label, "Middle battery colors", "Middle battery colors");
            }
            else if (IsNone(mode))
            {
                actions.Add(ImportBuilder.ProfileColor(0, 0, 0));
            }
            else
            {
                _b.Skipped(ImportReasons.DsxModeName, Label, mode, mode);
                return;
            }

            if (actions.Count == 0)
            {
                _b.Skipped(ImportReasons.ControllerSetting, Label, mode, mode);
                return;
            }
            _b.AddMacro(new ImportMacro
            {
                Name = "Lightbar",
                Control = "lightbar",
                RunsBesideLayers = true,
                Mode = MacroTriggerMode.Always,
                PadId = PadId,
                Actions = actions,
            });
            _b.Clean(Label, mode, "LightbarColor");
        }

        /// <summary>The DualSense's player LED patterns, LED 1 the bit 0
        /// (the DeviceSlotConfig.PlayerLedMode wire patterns).</summary>
        private static readonly (int Pattern, PlayerLedMode Mode)[] PlayerPatterns =
        {
            (0x00, PlayerLedMode.Off), (0x04, PlayerLedMode.Player1), (0x0A, PlayerLedMode.Player2),
            (0x15, PlayerLedMode.Player3), (0x1B, PlayerLedMode.Player4), (0x1F, PlayerLedMode.All),
        };

        private void ReadPlayerLeds(JsonElement? led)
        {
            const string Label = "Player LEDs";
            string mode = Str(led, "ds_player_mode");
            if (mode.Length > 0 && (_ledsToGame || _nativeNoData))
            {
                _b.Partial(_ledsToGame ? ImportReasons.DsxPassthrough : ImportReasons.DsxNoData, Label, mode, "", Label);
                return;
            }

            // The Mod System's PlayerLEDMode is One to Five and AllOff, and a
            // profile writes the same in capitals.
            bool individual = Word(mode, "INDIVIDUAL") || Word(mode, "CUSTOM") || Word(mode, "MANUAL");
            PlayerLedMode? target = mode.ToUpperInvariant() switch
            {
                "ONE" => PlayerLedMode.Player1,
                "TWO" => PlayerLedMode.Player2,
                "THREE" => PlayerLedMode.Player3,
                "FOUR" => PlayerLedMode.Player4,
                "FIVE" => PlayerLedMode.All,
                "ALL_OFF" or "ALLOFF" or "OFF" => PlayerLedMode.Off,
                _ => null,
            };
            if (target == null && Word(mode, "BATTER")) target = PlayerLedMode.Battery;
            if (target == null && individual)
            {
                int pattern = 0;
                for (int i = 1; i <= 5; i++)
                    if (Bool(led, "ds_player_individual_" + i.ToString(CultureInfo.InvariantCulture), false)) pattern |= 1 << (i - 1);
                foreach (var (p, m) in PlayerPatterns)
                    if (p == pattern) target = m;
                if (target == null)
                {
                    string lit = string.Join(", ", Enumerable.Range(1, 5).Where(i => (pattern & (1 << (i - 1))) != 0));
                    _b.Skipped(ImportReasons.NoSuchSetting, Label, mode + ": " + lit, "LEDs " + lit);
                }
            }
            if (target != null)
            {
                _b.PlayerLedMode = target.Value.ToString();
                _b.Clean(Label, mode, target.Value.ToString());
            }
            else if (mode.Length > 0 && !individual)
            {
                _b.Skipped(ImportReasons.DsxModeName, Label, mode, mode);
            }

            string bright = Str(led, "ds_util_brightness");
            if (Enum.TryParse(bright, ignoreCase: true, out PlayerLedBrightness level) && Enum.IsDefined(level)
                && !int.TryParse(bright, out _))
            {
                _b.PlayerLedBrightness = level.ToString();
                _b.Clean(Label, "Brightness " + bright, level.ToString());
            }
            else if (bright.Length > 0)
            {
                _b.Skipped(ImportReasons.DsxModeName, Label, bright, bright);
            }
        }

        private void ReadMicLed(JsonElement? led)
        {
            const string Label = "Mic LED";
            string mode = Str(led, "ds_mic_mode");
            if (mode.Length == 0) return;
            if (_ledsToGame || _nativeNoData)
            {
                _b.Partial(_ledsToGame ? ImportReasons.DsxPassthrough : ImportReasons.DsxNoData, Label, mode, "", Label);
                return;
            }
            // The Mod System's MicLEDMode is On, Pulse and Off. A profile can
            // also follow the PC microphone's mute.
            MicLedMode? target = Word(mode, "MUTE") ? MicLedMode.FollowDeviceMute
                : Word(mode, "PULSE") ? MicLedMode.Pulse
                : Is(mode, "ON") || Is(mode, "SOLID") ? MicLedMode.Solid
                : IsNone(mode) ? MicLedMode.Off
                : null;
            if (target == null)
            {
                _b.Skipped(ImportReasons.DsxModeName, Label, mode, mode);
                return;
            }
            _b.MicLedMode = target.Value.ToString();
            _b.Clean(Label, mode, target.Value.ToString());
        }

        // ── Haptics and audio ───────────────────────────────────────────

        private void ReadHaptics()
        {
            var haptics = Section("controller_haptics");
            var general = Section("controller_general");
            const string Label = "Haptics";

            // The haptics of a virtual DualSense with audio: Disabled, Haptics
            // to Rumble or Haptics Passthrough (DSX v3.1 BETA 1.44's notes).
            string personaMode = _virtualAudio ? Str(general, "DualSenseHapticsMode") : "";
            if (Word(personaMode, "RUMBLE") || Word(personaMode, "PASSTHROUGH"))
                _b.Partial(ImportReasons.DsxHaptics, Label, personaMode, Strings.Instance.Import_Line_Native);
            else if (personaMode.Length > 0 && IsNone(personaMode))
                _b.Skipped(ImportReasons.NoSuchSetting, Label, personaMode, "Game haptics off");
            else if (personaMode.Length > 0)
                _b.Skipped(ImportReasons.DsxModeName, Label, personaMode, personaMode);

            if (haptics == null)
            {
                // The older section, before DSX 3.2's haptics page.
                if (Section("controller_haptics_rumble") != null)
                    _b.Skipped(ImportReasons.ControllerSetting, Label, "controller_haptics_rumble", "controller_haptics_rumble");
                return;
            }

            var rumble = Child(haptics, "rumble");
            if (rumble != null)
            {
                if (!Bool(rumble, "enabled", true))
                {
                    _b.RumblePercent = "0";
                    _b.Clean(Label, "Rumble off", "0 %");
                }
                else
                {
                    int intensity = Int(rumble, "intensity", 8);
                    if (intensity != 8)
                        _b.Partial(ImportReasons.DsxUnit, Label, "Rumble intensity " + intensity.ToString(CultureInfo.InvariantCulture), "");
                    if (Bool(rumble, "enhanced_enabled", false))
                        _b.Skipped(ImportReasons.NoSuchSetting, Label, "Enhanced rumble", "Enhanced rumble");
                }
            }

            var feedback = Child(haptics, "game_feedback");
            if (feedback != null)
            {
                bool off = !Bool(feedback, "enabled", true)
                           || !Bool(feedback, "haptics_passthrough_enabled", true)
                           || !Bool(feedback, "speaker_fx_passthrough_enabled", true);
                bool low = Num(feedback, "haptics_passthrough_level", 100) < 100 || Num(feedback, "speaker_fx_passthrough_level", 100) < 100;
                if (off) _b.Skipped(ImportReasons.NoSuchSetting, Label, "Game feedback off", "Game feedback off");
                else if (low) _b.Skipped(ImportReasons.NoSuchSetting, Label, "Game feedback level", "Game feedback level");
            }

            var audio = Child(haptics, "audio");
            string source = Str(audio, "source");
            if (Bool(audio, "enabled", true) && !IsNone(source))
                _b.Skipped(ImportReasons.NoSuchSetting, Label, "Audio to haptics: " + source, "Audio to haptics");

            var output = Child(haptics, "output");
            if (output != null)
            {
                int speaker = Int(output, "speaker_volume", 100);
                if (speaker != 100)
                    _b.Skipped(ImportReasons.AppSetting, "Speaker volume", speaker.ToString(CultureInfo.InvariantCulture) + " %",
                        Strings.Instance.Pad_Audio_MasterVolume);
                int headset = Math.Clamp(Int(output, "headset_volume", 100), 0, 100);
                if (headset != 100)
                {
                    _b.HeadphoneVolume = headset.ToString(CultureInfo.InvariantCulture);
                    _b.Clean("Headset volume", headset.ToString(CultureInfo.InvariantCulture) + " %", "HeadphoneVolume");
                }
            }

            // The modes that play an input, the rumble or a sound file as a
            // haptic wave. The buttons the button mode plays on are DSX's own
            // numbers.
            if (Child(haptics, "modes") is JsonElement m)
            {
                foreach (var property in m.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object || !Bool(property.Value, "enabled", false)) continue;
                    string shown = property.Name.Replace('_', ' ');
                    if (shown.Length > 0) shown = char.ToUpperInvariant(shown[0]) + shown[1..];
                    if (property.Name.Equals("touchpad_to_haptics", StringComparison.OrdinalIgnoreCase))
                    {
                        // PadForge's own touchpad haptics, the swipe ticks.
                        _b.Partial(ImportReasons.TouchpadShape, Label, shown, "");
                        continue;
                    }
                    if (property.Name.Equals("button_to_haptics", StringComparison.OrdinalIgnoreCase))
                    {
                        var buttons = Items(property.Value, "button_haptics_enabled_buttons", 64)
                            .Where(b => b.ValueKind == JsonValueKind.String).Select(b => b.GetString());
                        _b.Skipped(ImportReasons.DsxNumber, Label, shown, string.Join(", ", buttons));
                        continue;
                    }
                    _b.Skipped(ImportReasons.NoSuchSetting, Label, shown, shown);
                }
            }

            foreach (var preset in Items(_root, "controller_haptics_preset_list", MaxPresets))
                if (preset.ValueKind == JsonValueKind.Object)
                    _b.Skipped(ImportReasons.ControllerSetting, "Haptics preset", Str(preset, "preset_name"), Str(preset, "preset_name"));
        }

        /// <summary>DSX's microphone button turns the PC's microphone on and
        /// off. PadForge has no action that mutes a microphone.</summary>
        private void ReadMicButton()
        {
            var general = Section("controller_general");
            if (!Bool(general, "ToggleMicrophoneMuteGeneralSettings", false)) return;
            string mode = Str(general, "ToggleMicrophoneMuteModeGeneralSettings");
            _b.Skipped(ImportReasons.NoSuchSetting, "Mic button", mode.Length > 0 ? mode : "PC microphone", "PC microphone mute");
        }

        // ── Button Mapping (the Mapping Lab) ────────────────────────────

        /// <summary>The binding's check value: FNV-1a over its inputs as
        /// little-endian 16-bit numbers, which every published binding
        /// matches.</summary>
        internal static uint ComboHash(IReadOnlyList<int> inputs)
        {
            uint h = 0x811C9DC5;
            foreach (int input in inputs)
            {
                h = (h ^ (byte)(input & 0xFF)) * 0x01000193;
                h = (h ^ (byte)((input >> 8) & 0xFF)) * 0x01000193;
            }
            return h;
        }

        /// <summary>The inputs DSX's published profiles show: 41 to 44 are the
        /// D-pad in the order up, down, left, right (a profile sends them as
        /// the arrow keys in that order). DSX publishes no other number.</summary>
        internal static ImportInput DsxInput(int number) => number switch
        {
            41 => new ImportInput { Control = "dsx:DpadUp", Label = "D-pad Up", Descriptor = "Gamepad DPadUp" },
            42 => new ImportInput { Control = "dsx:DpadDown", Label = "D-pad Down", Descriptor = "Gamepad DPadDown" },
            43 => new ImportInput { Control = "dsx:DpadLeft", Label = "D-pad Left", Descriptor = "Gamepad DPadLeft" },
            44 => new ImportInput { Control = "dsx:DpadRight", Label = "D-pad Right", Descriptor = "Gamepad DPadRight" },
            _ => null,
        };

        /// <summary>A keyboard or mouse output: 0x4000 plus the Windows
        /// virtual-key code. Null for anything else.</summary>
        internal static ImportOutput DsxOutput(int hash)
        {
            if ((hash & ~0x3FFF) != 0x4000) return null;
            int vk = hash & 0x3FFF;
            return vk switch
            {
                0x01 => ImportOutput.Mouse(0),
                0x02 => ImportOutput.Mouse(1),
                0x04 => ImportOutput.Mouse(2),
                0x05 => ImportOutput.Mouse(3),
                0x06 => ImportOutput.Mouse(4),
                > 0x06 and < 256 => ImportOutput.Key((byte)vk),
                _ => null,
            };
        }

        private const string MappingLabel = "Button Mapping";

        private void ReadMappingLab()
        {
            if (!_root.TryGetProperty("controller_mapping_lab", out var lab) || lab.ValueKind != JsonValueKind.Object) return;
            if (!Bool(Child(lab, "settings"), "actionSystemEnabled", true))
            {
                _b.Clean(MappingLabel, "Off", "");
                return;
            }
            foreach (var binding in Items(lab, "bindings", MaxBindings))
            {
                if (binding.ValueKind != JsonValueKind.Object) continue;
                var input = Child(binding, "input");
                var numbers = Items(input, "inputHashes", MaxComboInputs)
                    .Where(i => i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out _))
                    .Select(i => i.GetInt32())
                    .ToList();
                if (numbers.Count == 0)
                {
                    // DSX writes every binding's inputs as numbers.
                    _b.Skipped(ImportReasons.DsxEdited, MappingLabel, Str(binding, "id"));
                    continue;
                }
                string named = string.Join(" + ", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));

                if (!(input is JsonElement inp && inp.TryGetProperty("comboHash", out var hashValue)
                      && hashValue.ValueKind == JsonValueKind.Number && hashValue.TryGetUInt32(out uint combo)
                      && combo == ComboHash(numbers)))
                {
                    _b.Skipped(ImportReasons.DsxEdited, MappingLabel, named);
                    continue;
                }
                var inputs = numbers.Select(DsxInput).ToList();
                if (inputs.Any(i => i == null))
                {
                    // What it sends goes on the line, so the binding can be
                    // made again by hand on the right button.
                    bool any = false;
                    if (Child(binding, "activators") is JsonElement unknown)
                        foreach (var activator in unknown.EnumerateObject())
                        {
                            if (activator.Value.ValueKind != JsonValueKind.Object) continue;
                            _b.Skipped(ImportReasons.DsxNumber, MappingLabel, named + " " + activator.Name + ": " + Describe(activator.Value), named);
                            any = true;
                        }
                    if (!any) _b.Skipped(ImportReasons.DsxNumber, MappingLabel, named, named);
                    continue;
                }
                if (Child(binding, "activators") is not JsonElement acts) continue;
                foreach (var activator in acts.EnumerateObject())
                {
                    if (activator.Value.ValueKind != JsonValueKind.Object) continue;
                    LowerActivator(inputs, activator.Name, activator.Value, named);
                }
            }
        }

        /// <summary>What an activator sends, in words where DSX's numbers are
        /// known: a profile switch, keys and mouse buttons, and the number
        /// of anything else.</summary>
        private static string Describe(JsonElement act)
        {
            var output = Child(act, "output");
            string target = Str(Child(output, "details"), "profileSystemTarget");
            if (target.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
                return "Profile " + target["profile:".Length..];
            var hashes = Is(Str(output, "kind"), "advanced")
                ? Items(output, "outputs", MaxOutputs).Select(o => Int(o, "mappingHash", -1)).ToList()
                : new List<int> { Int(output, "mappingHash", -1) };
            return string.Join(" + ", hashes.Select(h => DsxOutput(h)?.Label ?? OutputNumber(h)));
        }

        private static string OutputNumber(int hash) => "output " + hash.ToString(CultureInfo.InvariantCulture);

        /// <summary>One activator of a binding, lowered as the reWASD import
        /// lowers the same words: Single and Start hold the outputs while
        /// the inputs are held, Release taps them, and turbo taps them over
        /// and over while held.</summary>
        private void LowerActivator(IReadOnlyList<ImportInput> inputs, string activator, JsonElement act, string named)
        {
            string binding = named + " " + activator;
            var output = Child(act, "output");
            string mode = Str(output, "mode");
            var settings = Child(act, "settings");
            bool release = Is(activator, "Release");
            if (!Is(activator, "Single") && !Is(activator, "Start") && !release)
            {
                _b.Skipped(ImportReasons.DsxModeName, MappingLabel, binding, activator);
                return;
            }
            if (mode.Length > 0 && !Is(mode, "single") && !Is(mode, "hold_until_release"))
            {
                _b.Skipped(ImportReasons.DsxModeName, MappingLabel, binding, mode);
                return;
            }
            var trigger = release ? MacroTriggerMode.OnRelease : MacroTriggerMode.OnPress;
            string macroName = MappingLabel + " " + string.Join(" + ", inputs.Select(i => i.Label));

            // A profile switch names its target, "profile:" and the name.
            string target = Str(Child(output, "details"), "profileSystemTarget");
            if (target.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
            {
                string profile = target["profile:".Length..].Trim();
                if (profile.Length == 0)
                {
                    _b.Skipped(ImportReasons.DsxEdited, MappingLabel, binding);
                    return;
                }
                _b.AddMacro(new ImportMacro
                {
                    Name = macroName,
                    Inputs = inputs,
                    Mode = trigger,
                    PadId = PadId,
                    Actions = new[]
                    {
                        new ActionData
                        {
                            Type = MacroActionType.ProfileShortcut,
                            ProfileShortcutMode = SwitchProfileMode.Specific,
                            ProfileShortcutName = profile,
                        },
                    },
                });
                Mute(inputs, act);
                _b.Partial(ImportReasons.DsxDpad, MappingLabel, binding, "ProfileShortcut " + profile);
                return;
            }

            var hashes = Is(Str(output, "kind"), "advanced")
                ? Items(output, "outputs", MaxOutputs).Select(o => (Hash: Int(o, "mappingHash", -1), Mode: Str(o, "mode"))).ToList()
                : new List<(int Hash, string Mode)> { (Int(output, "mappingHash", -1), "") };
            var outs = new List<ImportOutput>();
            foreach (var (hash, outMode) in hashes)
            {
                if (outMode.Length > 0 && !Is(outMode, "hold"))
                {
                    _b.Skipped(ImportReasons.DsxModeName, MappingLabel, binding, outMode);
                    return;
                }
                var o = DsxOutput(hash);
                if (o == null)
                {
                    _b.Skipped(ImportReasons.DsxNumber, MappingLabel, binding + ": " + OutputNumber(hash),
                        hash.ToString(CultureInfo.InvariantCulture));
                    return;
                }
                outs.Add(o);
            }
            if (outs.Count == 0) return;
            string emitted = string.Join(" + ", outs.Select(o => o.Label));
            // Before the binding's own rows, which read the same control.
            Mute(inputs, act);

            if (Bool(settings, "turboEnabled", false))
            {
                // The reWASD import's turbo: taps repeated while held.
                int delay = Math.Clamp(Int(settings, "turboDelayMaxMs", 100), 10, 5000);
                _b.AddMacro(new ImportMacro
                {
                    Name = macroName,
                    Inputs = inputs,
                    Mode = release ? MacroTriggerMode.OnRelease : MacroTriggerMode.Turbo,
                    Repeat = release ? MacroRepeatMode.Once : MacroRepeatMode.UntilRelease,
                    RepeatDelayMs = delay,
                    PadId = PadId,
                    Actions = outs.Select(o => o.Tap()).ToList(),
                });
                MarkReads(outs);
                _b.Partial(ImportReasons.DsxDpad, MappingLabel, binding, emitted);
                _b.Partial(ImportReasons.DsxTurbo, MappingLabel, binding, emitted, delay.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (release)
            {
                _b.AddMacro(new ImportMacro
                {
                    Name = macroName,
                    Inputs = inputs,
                    Mode = MacroTriggerMode.OnRelease,
                    PadId = PadId,
                    Actions = outs.Select(o => o.Tap()).ToList(),
                });
                MarkReads(outs);
            }
            else
            {
                BindHeld(macroName, inputs, outs, "dsx:map:" + named);
            }
            _b.Partial(ImportReasons.DsxDpad, MappingLabel, binding, emitted);
        }

        /// <summary>A muted input stops sending its own output: its default
        /// row goes, and the rows other bindings made on it stay.</summary>
        private void Mute(IReadOnlyList<ImportInput> inputs, JsonElement act)
        {
            if (!Bool(act, "muteInput", false)) return;
            foreach (var i in inputs)
                _b.Remove(ImportBuilder.BaseLayer, NativePrefix + i.Control["dsx:".Length..]);
        }
    }
}
