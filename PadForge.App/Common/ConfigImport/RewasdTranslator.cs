using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Common.ConfigImport
{
    /// <summary>
    /// Turns a reWASD <c>.rewasd</c> file (JSON, schema 3) into a profile. The
    /// vocabulary comes from the published schema and from what the public
    /// sample configs write, not from reWASD itself: a file holds masks (the
    /// inputs), mappings (an activator on a mask and what it sends), shifts
    /// (the layers) and per-device tuning.
    ///
    /// <para>Three facts in the samples set the shape. A button keeps its
    /// native function unless a separate mapping says <c>unmap</c>, so a
    /// mapped button is not an unmapped one. A plain output under
    /// <c>hold_until_release</c> is held, and several of them are a chord. A
    /// file always lists a virtual gamepad, so whether a pad slot exists comes
    /// from what the file uses it for.</para>
    /// </summary>
    internal sealed class RewasdTranslator
    {
        private const int DefaultLowZone = 30;
        private const int DefaultMedZone = 66;
        private const int DefaultHighZone = 85;

        // XInput's own digital threshold, 30 of 255.
        private const int TriggerPressPercent = 12;

        private sealed class StickTuning
        {
            public double SensX = 1.0;
            public double SensY = 1.0;
            public double RotationDeg;
            public double Inner;
            public double CurveExponent;
            public int Low = DefaultLowZone;
            public int Med = DefaultMedZone;
            public int High = DefaultHighZone;
        }

        private readonly JsonElement _root;
        private readonly ImportBuilder _b;
        private readonly Dictionary<int, JsonElement> _masks = new();
        private readonly Dictionary<int, JsonElement> _commands = new();
        private readonly Dictionary<int, string> _deviceClass = new();
        private readonly Dictionary<int, string> _shiftType = new();
        private readonly Dictionary<(int Shift, int Mask), HashSet<string>> _activatorTypes = new();
        private readonly HashSet<(int Shift, string Control)> _engagedBy = new();
        private readonly Dictionary<(int Layer, string Stick), StickTuning> _tuning = new();
        private readonly Dictionary<(int Layer, string Trigger), MappingSource> _triggerTuning = new();
        private readonly Dictionary<string, string> _radialHost = new(StringComparer.Ordinal);
        private readonly Dictionary<(int Shift, string Control), MappingSource> _stickSources = new();
        private readonly Dictionary<int, List<int>> _masksOfSector = new();
        private readonly Dictionary<int, List<(int Order, JsonElement Mapping)>> _mappingsOfMask = new();
        private bool _hasPhysicalPad;
        private bool _twoPads;
        private int _keyboardUnmaps;
        private int _mouseUnmaps;
        private int _firstVirtualId = 1;
        private bool _noted;

        private RewasdTranslator(JsonElement root)
        {
            _root = root;
            _b = new ImportBuilder();
        }

        public static ForeignImportResult Translate(JsonElement root, string path)
        {
            if (root.ValueKind != JsonValueKind.Object || SchemaMajor(root) != 3)
                return ForeignImportResult.Refuse(ForeignConfigKind.Rewasd, ImportReasons.RewasdOtherVersion);
            if (TooManyEntries(root))
                return ForeignImportResult.Refuse(ForeignConfigKind.Rewasd, ImportReasons.FileTooLarge);

            var t = new RewasdTranslator(root);
            t.ReadTables();
            t.ReadDevices();
            t.ReadShifts();
            t.ReadDeviceTuning();
            t.BuildPassThrough();
            t.ReadHardwareMappings();
            t.ReadMaskMappings();
            t.ReadRadialMenus();
            t.ReadLed();
            t.ReportLeftovers();

            t._b.Report.ConfigTitle = t.ProfileName(path);
            t._b.Report.SchemaVersion = 3;
            var profile = t._b.Finish(t.ProfileName(path), t.Executables());
            return new ForeignImportResult
            {
                Kind = ForeignConfigKind.Rewasd,
                Profile = profile,
                Report = t._b.Report,
                ReadsKeyboard = t._b.ReadsKeyboard,
                ReadsMouse = t._b.ReadsMouse,
                ReadsController = t._b.ReadsController,
            };
        }

        // The largest public sample has 193 masks, 129 mappings and 3 shifts.
        // These leave room for any real file and refuse one built to spin the
        // tables' loops, which look one list up in another.
        private const int MaxTableEntries = 5000;
        private const int MaxShifts = 64;

        private static bool TooManyEntries(JsonElement root)
        {
            int Count(string name) => Has(root, name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
            return Count("shifts") > MaxShifts
                || Count("masks") > MaxTableEntries
                || Count("mappings") > MaxTableEntries
                || Count("commands") > MaxTableEntries
                || Count("radialMenuCircles") > MaxTableEntries
                || Count("radialMenuSectors") > MaxTableEntries;
        }

        // ── Reading JSON ────────────────────────────────────────────────

        private static bool Has(JsonElement e, string name, out JsonElement value)
        {
            value = default;
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value)
                && value.ValueKind != JsonValueKind.Null;
        }

        private static string Str(JsonElement e, string name)
            => Has(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement e, string name, int fallback = 0)
        {
            if (!Has(e, name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d))
                return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
            if (v.ValueKind == JsonValueKind.String
                && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                return i;
            return fallback;
        }

        private static bool Flag(JsonElement e, string name)
            => Has(e, name, out var v) && v.ValueKind == JsonValueKind.True;

        private static IEnumerable<JsonElement> Arr(JsonElement e, string name)
        {
            if (!Has(e, name, out var v) || v.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in v.EnumerateArray()) yield return item;
        }

        private static IEnumerable<string> Names(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) yield break;
            foreach (var p in e.EnumerateObject()) yield return p.Name;
        }

        private static bool TryNumber(JsonElement v, out int value)
        {
            value = 0;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d)) return false;
            value = (int)Math.Clamp(d, int.MinValue, int.MaxValue);
            return true;
        }

        private static int SchemaMajor(JsonElement root)
        {
            if (!Has(root, "schemaVersion", out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return (int)d;
            if (v.ValueKind != JsonValueKind.String) return 0;
            string s = v.GetString() ?? "";
            int dot = s.IndexOf('.');
            return int.TryParse(dot >= 0 ? s[..dot] : s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : 0;
        }

        private string ProfileName(string path)
        {
            if (Has(_root, "config", out var cfg))
            {
                string app = Str(cfg, "appName");
                if (!string.IsNullOrWhiteSpace(app)) return app.Trim();
                string comment = Str(cfg, "comment");
                if (!string.IsNullOrWhiteSpace(comment)) return comment.Trim();
            }
            // reWASD stores Profiles\{Game}\Controller\{Config}.rewasd, so the
            // game's name is two folders up.
            string stem = Path.GetFileNameWithoutExtension(path ?? "");
            string dir = Path.GetDirectoryName(path ?? "");
            if (!string.IsNullOrEmpty(dir)
                && string.Equals(Path.GetFileName(dir), "Controller", StringComparison.OrdinalIgnoreCase))
            {
                string game = Path.GetFileName(Path.GetDirectoryName(dir));
                if (!string.IsNullOrEmpty(game)) return game + " - " + stem;
            }
            return stem;
        }

        private string Executables()
        {
            if (!Has(_root, "config", out var cfg)) return string.Empty;
            var names = Arr(cfg, "processNames")
                .Where(n => n.ValueKind == JsonValueKind.String)
                .Select(n => n.GetString())
                .Where(n => !string.IsNullOrWhiteSpace(n));
            return string.Join("|", names);
        }

        // ── Tables ──────────────────────────────────────────────────────

        private void ReadTables()
        {
            foreach (var m in Arr(_root, "masks")) _masks[Int(m, "id")] = m;
            foreach (var c in Arr(_root, "commands")) _commands[Int(c, "id")] = c;
            foreach (var s in Arr(_root, "shifts")) _shiftType[Int(s, "id")] = Str(s, "type") ?? "default";

            foreach (var mapping in Arr(_root, "mappings"))
            {
                if (!Has(mapping, "condition", out var c) || !Has(c, "mask", out var mask)) continue;
                var key = (Int(c, "shiftId"), Int(mask, "id"));
                if (!_activatorTypes.TryGetValue(key, out var types))
                    _activatorTypes[key] = types = new HashSet<string>(StringComparer.Ordinal);
                if (Has(mask, "activator", out var a)) types.Add(Str(a, "type") ?? "single");
            }
        }

        private void ReadDevices()
        {
            if (!Has(_root, "devices", out var devices)) return;

            int pads = 0;
            bool converts = false;
            foreach (var h in Arr(devices, "hardware"))
            {
                string name = Str(h, "name") ?? "";
                _deviceClass[Int(h, "id")] = name;
                if (name == "gamepad")
                {
                    pads++;
                    converts |= Flag(h, "convertToVirtual");
                    if (Flag(h, "hidePhysicalGamepad"))
                        _b.Partial(ImportReasons.ControllerSetting, Strings.Instance.Import_Line_Controller, "", "", "hidePhysicalGamepad");
                }
            }
            _hasPhysicalPad = pads > 0;
            _twoPads = pads > 1;

            bool usesPad = converts || FileUsesVirtualPad();
            bool keyboardMouseOnly = false;
            if (Has(devices, "virtual", out var virt))
            {
                bool first = true;
                foreach (var g in Arr(virt, "gamepads"))
                {
                    int id = Int(g, "id", 1);
                    string type = Str(g, "type") ?? "360";
                    if (first)
                    {
                        _firstVirtualId = id;
                        keyboardMouseOnly = type == "keyboardandmouse";
                    }
                    first = false;
                    if (usesPad && type != "keyboardandmouse")
                    {
                        var (vcType, profile) = VirtualFor(type);
                        _b.DeclarePad(id, vcType, profile);
                    }
                    if (Has(g, "external", out _) || Has(g, "udp", out _))
                        _b.Skipped(ImportReasons.RemoteTarget, type, "");
                    if (Has(g, "default", out var def))
                    {
                        foreach (var st in Arr(def, "sticks"))
                        {
                            string stick = Str(st, "name") ?? "";
                            var props = Names(st).ToList();
                            bool Shakes(string n) => n.StartsWith("jitter", StringComparison.Ordinal)
                                                     || n.StartsWith("shaking", StringComparison.Ordinal);
                            if (props.Any(Shakes))
                                _b.Skipped(ImportReasons.JitterShaking, stick, "");
                            var left = props.Where(n => n != "name" && !Shakes(n)).ToList();
                            if (left.Count > 0)
                                _b.Partial(ImportReasons.ControllerSetting, stick, "", "", string.Join(", ", left));
                        }
                    }
                }
            }
            if (usesPad && !_b.HasPad && !keyboardMouseOnly)
                _b.DeclarePad(1, VirtualControllerType.Xbox, "xbox-360-wired");

            if (_twoPads)
                _b.Partial(ImportReasons.SharedControllers, Strings.Instance.Import_Line_Controller, "", "");
        }

        /// <summary>The slot's type and HIDMaestro profile. The profile ids
        /// are the shipped presets closest to what reWASD emulates.</summary>
        private static (VirtualControllerType, string) VirtualFor(string type) => type switch
        {
            "xboxonebt" => (VirtualControllerType.Xbox, "xbox-one-s-bt"),
            "ds4" => (VirtualControllerType.PlayStation, "dualshock-4-v2"),
            "ds3" => (VirtualControllerType.PlayStation, "dualshock-3"),
            "nintendoswitchpro" => (VirtualControllerType.Nintendo, "switch-pro"),
            _ => (VirtualControllerType.Xbox, "xbox-360-wired"),
        };

        /// <summary>reWASD's file lists a virtual gamepad either way, so its
        /// presence says nothing. It is in use when a mapping sends to it, or
        /// when a mapping unmaps or moves a physical pad's button, which only
        /// a virtual pad can hide from a game.</summary>
        private bool FileUsesVirtualPad()
        {
            foreach (var mapping in Arr(_root, "mappings"))
            {
                foreach (var step in Arr(mapping, "macros"))
                    if (Has(step, "gamepad", out _)) return true;
                if (Has(mapping, "condition", out var c) && Has(c, "hardware", out var hw)
                    && ClassOf(Int(hw, "deviceId"), Str(hw, "description")) == "gamepad")
                    return true;
            }
            return false;
        }

        private string ClassOf(int deviceId, string description = null)
        {
            if (_deviceClass.TryGetValue(deviceId, out var c) && !string.IsNullOrEmpty(c)) return c;
            if (description != null)
            {
                if (description.StartsWith("DIK_", StringComparison.Ordinal)) return "keyboard";
                if (description.StartsWith("MOUSE", StringComparison.OrdinalIgnoreCase)
                    || description.StartsWith("Mouse", StringComparison.Ordinal)) return "mouse";
            }
            return deviceId switch
            {
                11 or 21 => "keyboard",
                22 or 23 or 33 => "mouse",
                _ => "gamepad",
            };
        }

        private static string LayerOf(int shift) => shift <= 0 ? ImportBuilder.BaseLayer : "Shift" + shift;

        private string PathOf(int shift, string label)
            => shift <= 0 ? label : _b.LayerName(LayerOf(shift)) + " / " + label;

        // ── Inputs ──────────────────────────────────────────────────────

        private ImportInput ResolveInput(int deviceId, int buttonId, string description, int shift = 0)
        {
            return ClassOf(deviceId, description) switch
            {
                "keyboard" => KeyboardInput(buttonId),
                "mouse" => MouseInput(buttonId),
                _ => GamepadInput(buttonId, shift),
            };
        }

        private ImportInput KeyboardInput(int scanCode)
        {
            if (!DirectInputKeys.TryGetVirtualKey(scanCode, out byte vk)) return null;
            _b.ReadsKeyboard = true;
            // Numpad Enter reads apart from Enter in the keyboard's state,
            // at the index RawInputListener keeps it under.
            int index = scanCode == 0x9C ? 0x88 : vk;
            return new ImportInput
            {
                Control = "kb:" + index,
                Label = "Keyboard " + ImportOutput.KeyName(vk),
                Descriptor = "Button " + index,
                DeviceGuid = ImportBuilder.AllKeyboardsGuid,
                IsKeyboard = true,
            };
        }

        private ImportInput MouseInput(int id)
        {
            ImportInput Button(int raw, string label)
            {
                _b.ReadsMouse = true;
                return new ImportInput
                {
                    Control = "ms:" + id,
                    Label = label,
                    Descriptor = "Button " + raw,
                    DeviceGuid = ImportBuilder.AllMiceGuid,
                    IsMouse = true,
                };
            }
            ImportInput Axis(int axis, bool lower, string label)
            {
                _b.ReadsMouse = true;
                return new ImportInput
                {
                    Control = "ms:" + id,
                    Label = label,
                    Descriptor = "Axis " + axis,
                    DeviceGuid = ImportBuilder.AllMiceGuid,
                    HalfAxis = true,
                    Invert = lower,
                    IsMouse = true,
                    IsAnalog = true,
                };
            }
            // The mouse's state order is Left, Middle, Right, X1, X2. reWASD's
            // ids run Left, Right, Middle, X1, X2: the samples put shoot on 1,
            // aim on 2 and melee on 3.
            return id switch
            {
                1 => Button(0, "Mouse Left"),
                2 => Button(2, "Mouse Right"),
                3 => Button(1, "Mouse Middle"),
                4 => Button(3, "Mouse X1"),
                5 => Button(4, "Mouse X2"),
                64 => Axis(1, true, "Mouse Up"),
                65 => Axis(1, false, "Mouse Down"),
                66 or 143 => Axis(0, true, "Mouse Left"),
                67 or 144 => Axis(0, false, "Mouse Right"),
                171 => Axis(2, true, "Wheel Up"),
                172 => Axis(2, false, "Wheel Down"),
                _ => null,
            };
        }

        private StickTuning TuningOf(int shift, string stick)
        {
            if (_tuning.TryGetValue((shift, stick), out var t)) return t;
            if (shift != 0 && _tuning.TryGetValue((0, stick), out t)) return t;
            return new StickTuning();
        }

        private ImportInput GamepadInput(int id, int shift = 0)
        {
            ImportInput Btn(string descriptor, string label) => new()
            {
                Control = "gp:" + id,
                Label = label,
                Descriptor = descriptor,
            };
            ImportInput Dir(string axis, bool lower, string stick, string label) => new()
            {
                Control = "gp:" + id,
                Label = label,
                Descriptor = axis,
                HalfAxis = true,
                Invert = lower,
                DeadZone = TuningOf(shift, stick).Low,
                IsAnalog = true,
            };
            ImportInput Trig(string descriptor, int zone, string label) => new()
            {
                Control = "gp:" + id,
                Label = label,
                Descriptor = descriptor,
                DeadZone = zone,
                IsAnalog = true,
            };
            ImportInput Ring(string descriptor, int zone, string label) => new()
            {
                Control = "gp:" + id,
                Label = label,
                Descriptor = descriptor,
                DeadZone = zone,
                IsAnalog = true,
            };
            ImportInput HighDir(string axis, bool lower, string stick, string label)
            {
                var d = Dir(axis, lower, stick, label);
                d.DeadZone = TuningOf(shift, stick).High;
                return d;
            }
            ImportInput Half(string descriptor, bool lower, string label) => new()
            {
                Control = "gp:" + id,
                Label = label,
                Descriptor = descriptor,
                HalfAxis = true,
                Invert = lower,
                IsAnalog = true,
            };

            var l = TuningOf(shift, "left");
            var r = TuningOf(shift, "right");
            var lt = TuningOf(shift, "trigger:left");
            var rt = TuningOf(shift, "trigger:right");
            return id switch
            {
                1 => Btn("Gamepad ButtonA", "A"),
                2 => Btn("Gamepad ButtonB", "B"),
                3 => Btn("Gamepad ButtonX", "X"),
                4 => Btn("Gamepad ButtonY", "Y"),
                5 => Btn("Gamepad LeftShoulder", "LB"),
                6 => Btn("Gamepad RightShoulder", "RB"),
                7 => Btn("Gamepad ButtonBack", "Back"),
                8 => Btn("Gamepad ButtonStart", "Start"),
                9 => Btn("Gamepad LeftStick", "Left stick click"),
                10 => Btn("Gamepad RightStick", "Right stick click"),
                11 => Btn("Gamepad ButtonGuide", "Guide"),
                // SDL's MISC1 is the Share, Capture or Mic button, whichever
                // the controller has.
                12 or 13 => Btn("Button 11", "Share"),
                // SDL's paddle order: right 1, left 1, right 2, left 2.
                14 or 29 => Btn("Gamepad Paddle2", "Left upper paddle"),
                15 or 30 => Btn("Gamepad Paddle1", "Right upper paddle"),
                // SDL's DualSense Edge mapping: paddle3 right function, paddle4
                // left function.
                16 => Btn("Gamepad Paddle4", "Left function button"),
                17 => Btn("Gamepad Paddle3", "Right function button"),
                31 => Btn("Gamepad Paddle4", "Left lower paddle"),
                32 => Btn("Gamepad Paddle3", "Right lower paddle"),
                33 => Btn("Gamepad DPadUp", "D-pad Up"),
                34 => Btn("Gamepad DPadDown", "D-pad Down"),
                35 => Btn("Gamepad DPadLeft", "D-pad Left"),
                36 => Btn("Gamepad DPadRight", "D-pad Right"),
                37 => Ring("Gamepad LeftStickRing", l.Low, "Left stick low zone"),
                38 => Ring("Gamepad LeftStickRing", l.Med, "Left stick medium zone"),
                39 => Ring("Gamepad LeftStickRing", l.High, "Left stick high zone"),
                40 => Dir("Gamepad LeftStickY", true, "left", "Left stick Up"),
                41 => Dir("Gamepad LeftStickY", false, "left", "Left stick Down"),
                42 => Dir("Gamepad LeftStickX", true, "left", "Left stick Left"),
                43 => Dir("Gamepad LeftStickX", false, "left", "Left stick Right"),
                44 => Ring("Gamepad RightStickRing", r.Low, "Right stick low zone"),
                45 => Ring("Gamepad RightStickRing", r.Med, "Right stick medium zone"),
                46 => Ring("Gamepad RightStickRing", r.High, "Right stick high zone"),
                47 => Dir("Gamepad RightStickY", true, "right", "Right stick Up"),
                48 => Dir("Gamepad RightStickY", false, "right", "Right stick Down"),
                49 => Dir("Gamepad RightStickX", true, "right", "Right stick Left"),
                50 => Dir("Gamepad RightStickX", false, "right", "Right stick Right"),
                51 => Trig("Gamepad LeftTrigger", TriggerPressPercent, "Left trigger"),
                52 => Trig("Gamepad LeftTrigger", lt.Low, "Left trigger low zone"),
                53 => Trig("Gamepad LeftTrigger", lt.Med, "Left trigger medium zone"),
                54 => Trig("Gamepad LeftTrigger", lt.High, "Left trigger high zone"),
                55 => Trig("Gamepad RightTrigger", TriggerPressPercent, "Right trigger"),
                56 => Trig("Gamepad RightTrigger", rt.Low, "Right trigger low zone"),
                57 => Trig("Gamepad RightTrigger", rt.Med, "Right trigger medium zone"),
                58 => Trig("Gamepad RightTrigger", rt.High, "Right trigger high zone"),
                68 => Half("Gyro Pitch", false, "Gyro Up"),
                69 => Half("Gyro Pitch", true, "Gyro Down"),
                70 => Half("Gyro Yaw", false, "Gyro Left"),
                71 => Half("Gyro Yaw", true, "Gyro Right"),
                91 => Btn("Touchpad 0 DPadUp", "Left touchpad Up"),
                92 => Btn("Touchpad 0 DPadDown", "Left touchpad Down"),
                93 => Btn("Touchpad 0 DPadLeft", "Left touchpad Left"),
                94 => Btn("Touchpad 0 DPadRight", "Left touchpad Right"),
                99 => Btn("Touchpad 0 Click", "Left touchpad click"),
                100 => Btn("Touchpad 1 DPadUp", "Right touchpad Up"),
                101 => Btn("Touchpad 1 DPadDown", "Right touchpad Down"),
                102 => Btn("Touchpad 1 DPadLeft", "Right touchpad Left"),
                103 => Btn("Touchpad 1 DPadRight", "Right touchpad Right"),
                108 => Btn("Touchpad 1 Click", "Right touchpad click"),
                119 => HighDir("Gamepad LeftStickX", true, "left", "Left stick far Left"),
                120 => HighDir("Gamepad LeftStickX", false, "left", "Left stick far Right"),
                131 => HighDir("Gamepad RightStickX", true, "right", "Right stick far Left"),
                132 => HighDir("Gamepad RightStickX", false, "right", "Right stick far Right"),
                163 => Btn("Touchpad 0 Tap", "Left touchpad tap"),
                _ => null,
            };
        }

        /// <summary>A mask's inputs. Null when one cannot be read, which names
        /// it in <paramref name="unknown"/>, because a mask missing a leg
        /// would fire too easily.</summary>
        private List<ImportInput> InputsOf(JsonElement set, int shift, out string unknown)
        {
            unknown = null;
            var inputs = new List<ImportInput>();
            foreach (var item in set.EnumerateArray())
            {
                string description = Str(item, "description");
                var input = ResolveInput(Int(item, "deviceId"), Int(item, "buttonId"), description, shift);
                if (input == null)
                {
                    unknown = description ?? Int(item, "buttonId").ToString(CultureInfo.InvariantCulture);
                    return null;
                }
                inputs.Add(input);
            }
            return inputs;
        }

        private static string ControlOf(IReadOnlyList<ImportInput> inputs)
            => string.Join("+", inputs.Select(i => i.Control).OrderBy(c => c, StringComparer.Ordinal));

        private static string LabelOf(IReadOnlyList<ImportInput> inputs)
            => string.Join(" + ", inputs.Select(i => i.Label));

        // ── Layers ──────────────────────────────────────────────────────

        private void ReadShifts()
        {
            foreach (var shift in Arr(_root, "shifts"))
            {
                int id = Int(shift, "id");
                string type = Str(shift, "type") ?? "default";
                string name = Str(shift, "description");
                string mask = LayerOf(id);
                _b.AddLayer(mask, string.IsNullOrWhiteSpace(name)
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_LayerName_Format, id)
                    : name.Trim());

                if (type.StartsWith("unlock", StringComparison.Ordinal))
                    _b.Partial(ImportReasons.UnlockLayer, _b.LayerName(mask), type, mask);

                // A layer can name its own input: held for "default", flipped
                // for "toggle".
                if (Has(shift, "set", out var set) && set.ValueKind == JsonValueKind.Array)
                {
                    var inputs = InputsOf(set, 0, out string unknown);
                    if (inputs == null)
                    {
                        _b.Skipped(ImportReasons.UnknownInput, _b.LayerName(mask), unknown, unknown);
                    }
                    else if (inputs.Count > 0)
                    {
                        var act = ImportBuilder.ActivatorFor(inputs, type == "toggle" ? "Toggle" : "Hold", mask);
                        _b.AddActivator(act);
                        _engagedBy.Add((id, ControlOf(inputs)));
                        _b.Clean(_b.LayerName(mask), LabelOf(inputs), act.Mode);
                    }
                }

                // Do Not Inherit: the control acts as unmapped on this layer
                // unless the layer maps it.
                int blocked = 0;
                foreach (var maskId in Arr(shift, "unheritableMasks"))
                {
                    if (!TryNumber(maskId, out int unheritable) || !_masks.TryGetValue(unheritable, out var m)) continue;
                    if (!Has(m, "set", out var mset) || mset.ValueKind != JsonValueKind.Array) continue;
                    var inputs = InputsOf(mset, id, out _);
                    if (inputs == null) continue;
                    _b.Remove(mask, ControlOf(inputs));
                    blocked++;
                }
                if (blocked > 0)
                    _b.Clean(_b.LayerName(mask), string.Format(CultureInfo.InvariantCulture, "{0} x Do Not Inherit", blocked), "NoInherit");
            }

            // Which layer each Hold or Toggle jump engages, so the mapping
            // that jumps back on release is known as its other half.
            foreach (var mapping in Arr(_root, "mappings"))
            {
                if (!Has(mapping, "jumpToLayer", out var jump)) continue;
                if (!Has(mapping, "condition", out var c) || !Has(c, "mask", out var maskRef)) continue;
                if (!Has(maskRef, "activator", out var a)) continue;
                string mode = Str(a, "mode") ?? "";
                if (mode != "hold_until_release" && mode != "toggle") continue;
                if (!_masks.TryGetValue(Int(maskRef, "id"), out var m)) continue;
                if (!Has(m, "set", out var set) || set.ValueKind != JsonValueKind.Array) continue;
                var inputs = InputsOf(set, 0, out _);
                if (inputs != null) _engagedBy.Add((Int(jump, "layer"), ControlOf(inputs)));
            }
        }

        // ── Pass-through ────────────────────────────────────────────────

        private static readonly (int Id, string Descriptor, string Target)[] PadIdentity =
        {
            (1, "Gamepad ButtonA", "ButtonA"),
            (2, "Gamepad ButtonB", "ButtonB"),
            (3, "Gamepad ButtonX", "ButtonX"),
            (4, "Gamepad ButtonY", "ButtonY"),
            (5, "Gamepad LeftShoulder", "LeftShoulder"),
            (6, "Gamepad RightShoulder", "RightShoulder"),
            (7, "Gamepad ButtonBack", "ButtonBack"),
            (8, "Gamepad ButtonStart", "ButtonStart"),
            (9, "Gamepad LeftStick", "LeftThumbButton"),
            (10, "Gamepad RightStick", "RightThumbButton"),
            (11, "Gamepad ButtonGuide", "ButtonGuide"),
            (33, "Gamepad DPadUp", "DPadUp"),
            (34, "Gamepad DPadDown", "DPadDown"),
            (35, "Gamepad DPadLeft", "DPadLeft"),
            (36, "Gamepad DPadRight", "DPadRight"),
        };

        /// <summary>A physical pad keeps its native function on the virtual
        /// one until a mapping unmaps it, so the file's silence becomes rows.
        /// A file that names no physical pad, keyboard to gamepad, gets
        /// none.</summary>
        private void BuildPassThrough()
        {
            if (!_b.HasPad || !_hasPhysicalPad) return;

            foreach (var (id, descriptor, target) in PadIdentity)
            {
                _b.Bind(ImportBuilder.BaseLayer, "gp:" + id, target,
                    new MappingSource { Descriptor = descriptor }, _firstVirtualId);
            }
            BindTrigger(0, "left");
            BindTrigger(0, "right");
            BindStick(0, "left");
            BindStick(0, "right");

            // Touchpad and motion pass through on the slots whose report
            // carries them, as the default PlayStation mapping does.
            var type = _b.PadTypeOf(_firstVirtualId);
            if (type == VirtualControllerType.PlayStation && DeclaredVirtualType() == "ds4")
            {
                void Tp(string target, string descriptor)
                    => _b.Bind(ImportBuilder.BaseLayer, "gp:tp:" + target, target,
                        new MappingSource { Descriptor = descriptor }, _firstVirtualId);
                Tp("TouchpadX1", "Touchpad 0 Finger 0 X");
                Tp("TouchpadY1", "Touchpad 0 Finger 0 Y");
                Tp("TouchpadContact1", "Touchpad 0 Finger 0 Down");
                Tp("TouchpadX2", "Touchpad 0 Finger 1 X");
                Tp("TouchpadY2", "Touchpad 0 Finger 1 Y");
                Tp("TouchpadContact2", "Touchpad 0 Finger 1 Down");
                _b.Bind(ImportBuilder.BaseLayer, "gp:99", "TouchpadClick",
                    new MappingSource { Descriptor = "Touchpad 0 Click" }, _firstVirtualId);
            }
            if (type is VirtualControllerType.PlayStation or VirtualControllerType.Nintendo)
            {
                _b.Bind(ImportBuilder.BaseLayer, "gp:motion:gyro", "MotionGyro",
                    new MappingSource { Descriptor = "Motion Gyro" }, _firstVirtualId);
                _b.Bind(ImportBuilder.BaseLayer, "gp:motion:accel", "MotionAccel",
                    new MappingSource { Descriptor = "Motion Accel" }, _firstVirtualId);
            }

            _b.Clean(Strings.Instance.Import_Line_Controller, Strings.Instance.Import_Line_Native, "");

            // Per-layer tuning: a layer that retunes a stick or trigger gets
            // that axis's own rows while it is on.
            foreach (var ((layer, stick), _) in _tuning.Where(kv => kv.Key.Layer != 0 && kv.Key.Stick is "left" or "right").ToList())
                BindStick(layer, stick);
            foreach (var ((layer, trigger), _) in _triggerTuning.Where(kv => kv.Key.Layer != 0).ToList())
                BindTrigger(layer, trigger);
        }

        private string DeclaredVirtualType()
        {
            if (Has(_root, "devices", out var d) && Has(d, "virtual", out var v))
                foreach (var g in Arr(v, "gamepads"))
                    return Str(g, "type") ?? "";
            return "";
        }

        private void BindTrigger(int shift, string side)
        {
            string descriptor = side == "left" ? "Gamepad LeftTrigger" : "Gamepad RightTrigger";
            string target = side == "left" ? "LeftTrigger" : "RightTrigger";
            var src = _triggerTuning.TryGetValue((shift, side), out var tuned) ? tuned.Clone()
                : _triggerTuning.TryGetValue((0, side), out tuned) ? tuned.Clone()
                : new MappingSource();
            src.Descriptor = descriptor;
            string layer = LayerOf(shift);
            string control = side == "left" ? "gp:51" : "gp:55";
            if (shift != 0) _b.Remove(layer, control);
            _b.Bind(layer, control, target, src, _firstVirtualId);
        }

        /// <summary>A stick's two rows with its tuning on the sources. A
        /// rotation is row math, the Workshop mouse channel's AddLeg: each
        /// output row sums both pad axes with the trigonometric coefficients
        /// in the source's sensitivity, the sign in Invert. Near-zero legs
        /// drop, so a 90-degree turn stays two clean rows.</summary>
        private void BindStick(int shift, string stick)
        {
            string prefix = stick == "left" ? "Left" : "Right";
            string gx = "Gamepad " + prefix + "StickX";
            string gy = "Gamepad " + prefix + "StickY";
            string tx = prefix + "ThumbAxisX";
            string ty = prefix + "ThumbAxisY";
            string cx = "gp:stick:" + stick + ":x";
            string cy = "gp:stick:" + stick + ":y";
            var t = TuningOf(shift, stick);
            string layer = LayerOf(shift);

            MappingSource Make(string descriptor, double coeff, double sens)
            {
                var src = new MappingSource { Descriptor = descriptor };
                double scale = Math.Abs(coeff) * sens;
                if (scale != 1.0) src.Sensitivity = scale;
                if (coeff < 0) src.Invert = true;
                if (t.Inner > 0)
                {
                    src.ParamStickDeadZoneShape = 2;
                    src.ParamStickDeadZoneInner = t.Inner;
                }
                if (t.CurveExponent > 0) src.ParamCurveExponent = t.CurveExponent;
                return src;
            }

            if (shift != 0)
            {
                _b.Remove(layer, cx);
                _b.Remove(layer, cy);
            }

            if (t.RotationDeg != 0)
            {
                double r = t.RotationDeg * Math.PI / 180.0;
                double cos = Math.Cos(r), sin = Math.Sin(r);
                void Leg(string control, string target, string descriptor, double coeff, double sens)
                {
                    if (Math.Abs(coeff) < 0.001) return;
                    _b.Bind(layer, control, target, Make(descriptor, coeff, sens), _firstVirtualId);
                }
                Leg(cx, tx, gx, cos, t.SensX);
                Leg(cx, tx, gy, -sin, t.SensX);
                Leg(cy, ty, gx, sin, t.SensY);
                Leg(cy, ty, gy, cos, t.SensY);
                _b.SumRow(tx);
                _b.SumRow(ty);
            }
            else
            {
                var sx = Make(gx, 1.0, t.SensX);
                var sy = Make(gy, 1.0, t.SensY);
                _stickSources[(shift, cx)] = sx;
                _stickSources[(shift, cy)] = sy;
                _b.Bind(layer, cx, tx, sx, _firstVirtualId);
                _b.Bind(layer, cy, ty, sy, _firstVirtualId);
            }
        }

        // ── Hardware mappings: unmap and native remaps ──────────────────

        private void ReadHardwareMappings()
        {
            var halves = new Dictionary<(int Shift, string Stick, string Axis), (bool Lower, bool Upper)>();

            foreach (var mapping in Arr(_root, "mappings"))
            {
                if (!Has(mapping, "condition", out var c) || !Has(c, "hardware", out var from)) continue;
                if (!Has(mapping, "hardware", out var to)) continue;

                int shift = Int(c, "shiftId");
                string layer = LayerOf(shift);
                int device = Int(from, "deviceId");
                string description = Str(from, "description");
                string cls = ClassOf(device, description);
                bool unmap = Flag(to, "unmap");

                if (cls != "gamepad")
                {
                    if (unmap)
                    {
                        if (cls == "keyboard") _keyboardUnmaps++;
                        else _mouseUnmaps++;
                        continue;
                    }
                    if (Has(from, "stick", out _))
                    {
                        LowerStickHardware(from, to, false, shift);
                        continue;
                    }
                    var input = Has(from, "buttonId", out _)
                        ? ResolveInput(device, Int(from, "buttonId"), description, shift)
                        : null;
                    var output = Has(to, "buttonId", out _)
                        ? (cls == "keyboard" ? KeyOutput(Int(to, "buttonId")) : MouseOutput(Int(to, "buttonId")))
                        : null;
                    if (input == null || output == null)
                    {
                        _b.Skipped(ImportReasons.UnknownInput, PathOf(shift, description ?? cls), description ?? cls, description ?? cls);
                        continue;
                    }
                    _b.Bind(layer, input, output);
                    _b.Clean(PathOf(shift, input.Label), output.Label, output.Target);
                    continue;
                }

                if (Has(from, "stick", out _))
                {
                    LowerStickHardware(from, to, unmap, shift);
                    continue;
                }
                if (Has(from, "trigger", out _))
                {
                    string side = Str(from, "trigger");
                    string control = side == "left" ? "gp:51" : "gp:55";
                    string label = side == "left" ? "Left trigger" : "Right trigger";
                    _b.Remove(layer, control);
                    if (!unmap && Has(to, "trigger", out _))
                    {
                        string target = Str(to, "trigger") == "left" ? "LeftTrigger" : "RightTrigger";
                        _b.Bind(layer, control, target, new MappingSource
                        {
                            Descriptor = side == "left" ? "Gamepad LeftTrigger" : "Gamepad RightTrigger",
                        }, _firstVirtualId);
                    }
                    _b.Clean(PathOf(shift, label), unmap ? "Unmapped" : Str(to, "trigger") ?? "", "");
                    continue;
                }

                int id = Int(from, "buttonId");
                if (id is >= 40 and <= 43 or >= 47 and <= 50)
                {
                    string stick = id <= 43 ? "left" : "right";
                    int dir = id <= 43 ? id - 40 : id - 47;
                    string axis = dir <= 1 ? "y" : "x";
                    bool lower = dir is 0 or 2;
                    halves.TryGetValue((shift, stick, axis), out var state);
                    halves[(shift, stick, axis)] = lower ? (true, state.Upper) : (state.Lower, true);
                    continue;
                }

                var padInput = GamepadInput(id, shift);
                if (padInput == null)
                {
                    _b.Skipped(ImportReasons.UnknownInput, PathOf(shift, description ?? id.ToString(CultureInfo.InvariantCulture)),
                        description ?? "", description ?? id.ToString(CultureInfo.InvariantCulture));
                    continue;
                }
                _b.Remove(layer, padInput.Control);
                if (unmap)
                {
                    _b.Clean(PathOf(shift, padInput.Label), "Unmapped", "");
                    continue;
                }
                var padOutput = Has(to, "buttonId", out _) ? PadOutput(Int(to, "buttonId"), _firstVirtualId, out string missing) : null;
                if (padOutput == null)
                {
                    _b.Skipped(ImportReasons.NoSuchPadButton, PathOf(shift, padInput.Label), Str(to, "description") ?? "", Str(to, "description") ?? "");
                    continue;
                }
                _b.Bind(layer, padInput, padOutput);
                _b.Clean(PathOf(shift, padInput.Label), padOutput.Label, padOutput.Target);
            }

            foreach (var ((shift, stick, axis), state) in halves)
                ApplyHalfUnmap(shift, stick, axis, state.Lower, state.Upper);

            if (_keyboardUnmaps > 0)
                _b.Partial(ImportReasons.ConsumeKeys, "Keyboard",
                    string.Format(CultureInfo.InvariantCulture, "{0} x Unmap", _keyboardUnmaps), "",
                    Strings.Instance.Devices_AllKeyboardsMerged);
            if (_mouseUnmaps > 0)
                _b.Partial(ImportReasons.ConsumeKeys, "Mouse",
                    string.Format(CultureInfo.InvariantCulture, "{0} x Unmap", _mouseUnmaps), "",
                    Strings.Instance.Devices_AllMiceMerged);
        }

        /// <summary>A stick direction unmapped: the axis keeps its other half,
        /// read through a half-axis source that stays positive-or-negative as
        /// that half was.</summary>
        private void ApplyHalfUnmap(int shift, string stick, string axis, bool lower, bool upper)
        {
            string prefix = stick == "left" ? "Left" : "Right";
            string control = "gp:stick:" + stick + ":" + axis;
            string layer = LayerOf(shift);
            _b.Remove(layer, control);
            string label = prefix + " stick " + axis.ToUpperInvariant();
            if (lower && upper)
            {
                _b.Clean(PathOf(shift, label), "Unmapped", "");
                return;
            }
            if (TuningOf(shift, stick).RotationDeg != 0)
                _b.Partial(ImportReasons.ControllerSetting, PathOf(shift, label), "", "", "rotation");
            var src = _stickSources.TryGetValue((shift, control), out var whole) || _stickSources.TryGetValue((0, control), out whole)
                ? whole.Clone()
                : new MappingSource { Descriptor = "Gamepad " + prefix + "Stick" + axis.ToUpperInvariant() };
            src.HalfAxis = true;
            src.Invert = upper;
            ImportBuilder.SetOutputDirection(src, upper);
            _b.Bind(layer, control, prefix + "ThumbAxis" + axis.ToUpperInvariant(), src, _firstVirtualId);
            _b.Clean(PathOf(shift, label), lower ? "Unmapped up or left" : "Unmapped down or right", "");
        }

        private void LowerStickHardware(JsonElement from, JsonElement to, bool unmap, int shift)
        {
            string stick = Str(from, "stick") ?? "";
            string axis = Str(from, "axis") ?? "x";
            string layer = LayerOf(shift);
            string label = stick + " " + axis;

            if (stick == "mouse" && unmap)
            {
                _mouseUnmaps++;
                return;
            }
            if (stick == "additional")
            {
                _b.Skipped(ImportReasons.ThirdStick, PathOf(shift, label), label);
                return;
            }

            string control = "gp:stick:" + stick + ":" + axis;
            _b.Remove(layer, control);
            if (unmap)
            {
                _b.Clean(PathOf(shift, label), "Unmapped", "");
                return;
            }
            if (!Has(to, "stick", out _))
            {
                _b.Skipped(ImportReasons.UnknownOutput, PathOf(shift, label), label, label);
                return;
            }

            MappingSource src;
            switch (stick)
            {
                case "gyro":
                    src = new MappingSource { Descriptor = axis == "x" ? "Gyro Yaw" : "Gyro Pitch" };
                    break;
                case "mouse":
                    _b.ReadsMouse = true;
                    src = new MappingSource { Descriptor = axis == "x" ? "Axis 0" : "Axis 1", DeviceGuid = ImportBuilder.AllMiceGuid };
                    break;
                default:
                    src = new MappingSource
                    {
                        Descriptor = "Gamepad " + (stick == "left" ? "Left" : "Right") + "Stick" + axis.ToUpperInvariant(),
                    };
                    break;
            }
            src.Invert = Flag(to, "inverted");

            string toStick = Str(to, "stick") ?? "";
            string toAxis = (Str(to, "axis") ?? "x").ToUpperInvariant();
            if (toStick == "mouse")
            {
                _b.Bind(layer, control, "KbmMouse" + toAxis, src, 0);
            }
            else if (toStick is "left" or "right")
            {
                _b.Bind(layer, control, (toStick == "left" ? "Left" : "Right") + "ThumbAxis" + toAxis, src, _firstVirtualId);
            }
            else
            {
                _b.Skipped(ImportReasons.ThirdStick, PathOf(shift, label), toStick);
                return;
            }
            _b.Clean(PathOf(shift, label), toStick + " " + toAxis, "");
        }

        // ── Outputs ─────────────────────────────────────────────────────

        private static ImportOutput KeyOutput(int scanCode)
            => DirectInputKeys.TryGetVirtualKey(scanCode, out byte vk) ? ImportOutput.Key(vk) : null;

        private static ImportOutput MouseOutput(int id) => id switch
        {
            1 => ImportOutput.Mouse(0),
            2 => ImportOutput.Mouse(1),
            3 => ImportOutput.Mouse(2),
            4 => ImportOutput.Mouse(3),
            5 => ImportOutput.Mouse(4),
            _ => null,
        };

        private ImportOutput PadOutput(int id, int padId, out string missing)
        {
            missing = null;
            ImportOutput o = id switch
            {
                1 => ImportOutput.Pad("ButtonA"),
                2 => ImportOutput.Pad("ButtonB"),
                3 => ImportOutput.Pad("ButtonX"),
                4 => ImportOutput.Pad("ButtonY"),
                5 => ImportOutput.Pad("LeftShoulder"),
                6 => ImportOutput.Pad("RightShoulder"),
                7 => ImportOutput.Pad("ButtonBack"),
                8 => ImportOutput.Pad("ButtonStart"),
                9 => ImportOutput.Pad("LeftThumbButton"),
                10 => ImportOutput.Pad("RightThumbButton"),
                11 => ImportOutput.Pad("ButtonGuide"),
                33 => ImportOutput.Pad("DPadUp"),
                34 => ImportOutput.Pad("DPadDown"),
                35 => ImportOutput.Pad("DPadLeft"),
                36 => ImportOutput.Pad("DPadRight"),
                40 => ImportOutput.Stick("LeftThumbAxisY", true),
                41 => ImportOutput.Stick("LeftThumbAxisY", false),
                42 => ImportOutput.Stick("LeftThumbAxisX", true),
                43 => ImportOutput.Stick("LeftThumbAxisX", false),
                47 => ImportOutput.Stick("RightThumbAxisY", true),
                48 => ImportOutput.Stick("RightThumbAxisY", false),
                49 => ImportOutput.Stick("RightThumbAxisX", true),
                50 => ImportOutput.Stick("RightThumbAxisX", false),
                51 => ImportOutput.Trigger("LeftTrigger"),
                55 => ImportOutput.Trigger("RightTrigger"),
                99 => ImportOutput.Pad("TouchpadClick"),
                _ => null,
            };
            if (o == null)
            {
                missing = id.ToString(CultureInfo.InvariantCulture);
                return null;
            }
            o.PadId = padId;
            return o;
        }

        // ── Macros: one mapping's steps ─────────────────────────────────

        private enum StepKind { Output, Raw }

        private sealed class Step
        {
            public StepKind Kind;
            public ImportOutput Output;
            public string Action;
            public bool Pulse;
            public ActionData Raw;
        }

        /// <summary>Reads a mapping's steps, reporting the ones that cannot
        /// come across. Radial menu navigation returns nothing: PadForge's
        /// menus read their host directly.</summary>
        private List<Step> ParseSteps(JsonElement mapping, int shift, string path, IReadOnlyList<ImportInput> inputs, out bool notes)
        {
            notes = false;
            var steps = new List<Step>();
            foreach (var s in Arr(mapping, "macros"))
            {
                if (Has(s, "keyboard", out var k))
                {
                    var o = KeyOutput(Int(k, "buttonId"));
                    if (o == null)
                    {
                        string d = Str(k, "description") ?? Int(k, "buttonId").ToString(CultureInfo.InvariantCulture);
                        _b.Skipped(ImportReasons.UnknownOutput, path, d, d);
                        continue;
                    }
                    steps.Add(new Step { Output = o, Action = Str(k, "action"), Pulse = Flag(k, "pulsation") });
                }
                else if (Has(s, "mouse", out var m))
                {
                    ImportOutput o = null;
                    if (Has(m, "buttonId", out _)) o = MouseOutput(Int(m, "buttonId"));
                    else if (Has(m, "direction", out _))
                    {
                        string dir = Str(m, "direction");
                        o = ImportOutput.MouseMove(dir is "left" or "right", dir is "left" or "up");
                        if (Has(m, "value", out _) || Flag(m, "flick"))
                        {
                            _b.Partial(ImportReasons.MoveSpeed, path, o.Label, o.Target);
                            notes = true;
                        }
                    }
                    else if (Has(m, "wheel", out _))
                    {
                        string dir = Str(m, "wheel");
                        o = ImportOutput.MouseWheel(dir is "left" or "right", dir is "left" or "up");
                    }
                    if (o == null)
                    {
                        _b.Skipped(ImportReasons.UnknownOutput, path, "Mouse", "Mouse");
                        continue;
                    }
                    steps.Add(new Step { Output = o, Action = Str(m, "action") });
                }
                else if (Has(s, "gamepad", out var g))
                {
                    int id = Int(g, "buttonId");
                    var o = PadOutput(id, Int(g, "id", _firstVirtualId), out _);
                    if (o == null)
                    {
                        string d = Str(g, "description") ?? id.ToString(CultureInfo.InvariantCulture);
                        _b.Skipped(ImportReasons.NoSuchPadButton, path, d, d);
                        continue;
                    }
                    if (Has(g, "value", out _) && o.Kind is ImportOutputKind.PadStick or ImportOutputKind.PadTrigger)
                    {
                        int value = Int(g, "value", 32767);
                        if (value is > 0 and < 32767) o.Scale = value / 32767.0;
                        if (Has(g, "absolute", out var abs) && abs.ValueKind == JsonValueKind.False)
                        {
                            _b.Partial(ImportReasons.StickAdded, path, o.Label, o.Target);
                            notes = true;
                        }
                    }
                    steps.Add(new Step { Output = o, Action = Str(g, "action"), Pulse = Flag(g, "pulsation") });
                }
                else if (Has(s, "pause", out var p))
                {
                    steps.Add(new Step
                    {
                        Kind = StepKind.Raw,
                        Raw = new ActionData { Type = MacroActionType.Delay, DurationMs = Math.Max(1, Int(p, "value", 100)) },
                    });
                }
                else if (Has(s, "break", out _))
                {
                    steps.Add(new Step { Kind = StepKind.Raw, Raw = new ActionData { Type = MacroActionType.ComboBreak } });
                }
                else if (Has(s, "rumble", out var r))
                {
                    foreach (var a in RumbleActions(r))
                        steps.Add(new Step { Kind = StepKind.Raw, Raw = a });
                }
                else if (Has(s, "command", out var cmd))
                {
                    _noted = false;
                    var action = CommandAction(Int(cmd, "id"), shift, path, inputs);
                    notes |= _noted;
                    if (action != null) steps.Add(new Step { Kind = StepKind.Raw, Raw = action });
                }
                else
                {
                    string d = Names(s).FirstOrDefault() ?? "";
                    _b.Skipped(ImportReasons.UnknownOutput, path, d, d);
                }
            }
            return steps;
        }

        private static IEnumerable<ActionData> RumbleActions(JsonElement r)
        {
            int duration = Math.Max(1, Int(r, "duration", 100));
            if (!Has(r, "amplitude", out var amp)) yield break;
            if (Has(amp, "motor", out var motor))
            {
                yield return new ActionData
                {
                    Type = MacroActionType.Rumble,
                    RumbleHoldMode = MacroRumbleHoldMode.Reactive,
                    RumbleStrengthLeft = Math.Clamp(Int(motor, "left"), 0, 100),
                    RumbleStrengthRight = Math.Clamp(Int(motor, "right"), 0, 100),
                    RumbleHoldMs = duration,
                    RumbleFadeMs = 0,
                };
            }
            if (Has(amp, "trigger", out var trigger))
            {
                yield return new ActionData
                {
                    Type = MacroActionType.RumbleTrigger,
                    RumbleHoldMode = MacroRumbleHoldMode.Reactive,
                    RumbleStrengthLeft = Math.Clamp(Int(trigger, "left"), 0, 100),
                    RumbleStrengthRight = Math.Clamp(Int(trigger, "right"), 0, 100),
                    RumbleHoldMs = duration,
                    RumbleFadeMs = 0,
                };
            }
        }

        /// <summary>A reWASD command as a macro action. The ones that would
        /// start a program or power off the PC never become one: nothing named
        /// in a shared config runs. The numbers are the command types the
        /// public samples and reWASD's schema dump use.</summary>
        private ActionData CommandAction(int id, int shift, string path, IReadOnlyList<ImportInput> inputs)
        {
            if (!_commands.TryGetValue(id, out var cmd))
            {
                _b.Skipped(ImportReasons.UnknownCommand, path, id.ToString(CultureInfo.InvariantCulture), id.ToString(CultureInfo.InvariantCulture));
                _noted = true;
                return null;
            }
            int type = Int(cmd, "type");
            string name = Str(cmd, "description") ?? Str(cmd, "name") ?? type.ToString(CultureInfo.InvariantCulture);

            ActionData Gyro(MacroSetGyroEngagedMode mode) => new() { Type = MacroActionType.SetGyroEngaged, SetGyroEngagedMode = mode };

            ActionData Chord(params byte[] vks)
            {
                _b.Partial(ImportReasons.LoweredToKeys, path, name, "", string.Join("+", vks.Select(ImportOutput.KeyName)));
                _noted = true;
                return new ActionData
                {
                    Type = MacroActionType.KeyPress,
                    KeyString = string.Concat(vks.Select(v => "{" + (VirtualKey)v + "}")),
                };
            }

            switch (type)
            {
                case 4:
                case 6:
                    return Gyro(MacroSetGyroEngagedMode.Toggle);
                case 15:
                    return Gyro(MacroSetGyroEngagedMode.On);
                case 16:
                    return Gyro(MacroSetGyroEngagedMode.Off);
                case 5:
                case 18:
                    return new ActionData { Type = MacroActionType.GyroRecenter };
                case 11:
                    return Chord(0x5B, 0x2C);   // Win + Print Screen
                case 12:
                    return Chord(0xA4, 0x73);   // Alt + F4
                case 7:
                case 8:
                case 9:
                case 13:
                case 17:
                    _b.Skipped(ImportReasons.SystemCommand, path, name, name);
                    _noted = true;
                    return null;
                case >= 101 and <= 110:
                case 120:
                    _b.Skipped(ImportReasons.Crosshair, path, name);
                    _noted = true;
                    return null;
                case >= 130 and <= 137:
                    // Radial navigation. PadForge's menu reads its host itself,
                    // and the first direction names which host that is.
                    if (inputs.Count > 0 && _shiftType.TryGetValue(shift, out var st) && st == "radialMenu")
                    {
                        if (type is 134 or 135 or 136 or 137) NoteRadialHost(shift, inputs[0]);
                        _noted = true;
                    }
                    return null;
                case >= 1000 and <= 1999:
                    string target = Str(cmd, "name") ?? name;
                    if (Has(cmd, "params", out var ps) && ps.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in ps.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.String)
                            {
                                target = p.Value.GetString();
                                break;
                            }
                    }
                    _b.Skipped(ImportReasons.ProgramNotRun, path, target, target);
                    _noted = true;
                    return null;
                default:
                    _b.Skipped(ImportReasons.UnknownCommand, path, name, name);
                    _noted = true;
                    return null;
            }
        }

        private void NoteRadialHost(int shift, ImportInput input)
        {
            string host = input.Descriptor switch
            {
                "Gamepad LeftStickX" or "Gamepad LeftStickY" => "Gamepad LeftStick",
                "Gamepad RightStickX" or "Gamepad RightStickY" => "Gamepad RightStick",
                _ => null,
            };
            string layer = LayerOf(shift);
            if (host != null && (!_radialHost.TryGetValue(layer, out var existing) || existing == "Gamepad RightStick"))
                _radialHost[layer] = host;
        }

        // ── Mask mappings ───────────────────────────────────────────────

        private void ReadMaskMappings()
        {
            foreach (var mapping in Arr(_root, "mappings"))
            {
                if (!Has(mapping, "condition", out var c) || !Has(c, "mask", out var maskRef)) continue;

                int maskId = Int(maskRef, "id");
                int shift = Int(c, "shiftId");
                string layer = LayerOf(shift);
                Has(maskRef, "activator", out var activator);
                string type = Str(activator, "type") ?? "single";
                string mode = Str(activator, "mode") ?? "hold_until_release";

                if (!_masks.TryGetValue(maskId, out var mask))
                {
                    string missing = maskId.ToString(CultureInfo.InvariantCulture);
                    _b.Skipped(ImportReasons.UnknownInput, PathOf(shift, missing), "", missing);
                    continue;
                }
                if (Has(mask, "radialMenuSet", out _)) continue; // ReadRadialMenus
                if (Has(mask, "crosshairSet", out _))
                {
                    _b.Skipped(ImportReasons.Crosshair, PathOf(shift, Str(mask, "description") ?? "Crosshair"), "");
                    continue;
                }
                if (!Has(mask, "set", out var set) || set.ValueKind != JsonValueKind.Array)
                    continue;

                var inputs = InputsOf(set, shift, out string unknown);
                if (inputs == null)
                {
                    _b.Skipped(ImportReasons.UnknownInput, PathOf(shift, unknown), "", unknown);
                    continue;
                }
                if (inputs.Count == 0) continue;

                ApplyZone(inputs, type, shift);
                string path = PathOf(shift, LabelOf(inputs));
                string description = Str(mapping, "description");

                if (Has(mapping, "jumpToLayer", out var jump))
                    LowerJump(jump, shift, maskId, inputs, type, mode, activator, path, description);
                if (Has(mapping, "mouseRegion", out var region))
                    LowerRegion(region, layer, inputs, path, description);
                if (Has(mapping, "macros", out _))
                    LowerMacros(mapping, shift, maskId, inputs, type, mode, activator, path, description);
            }
        }

        /// <summary>A zone activator reads the input past that zone's edge. A
        /// file's zones are edges, so an input in the low zone also reads in
        /// medium and high, which the report says.</summary>
        private void ApplyZone(List<ImportInput> inputs, string type, int shift)
        {
            if (type is not ("zoneLow" or "zoneMed" or "zoneHigh")) return;
            foreach (var input in inputs.Where(i => i.IsAnalog))
            {
                string side = input.Descriptor.Contains("Left", StringComparison.Ordinal) ? "left" : "right";
                var t = TuningOf(shift, input.Descriptor.EndsWith("Trigger", StringComparison.Ordinal) ? "trigger:" + side : side);
                input.DeadZone = type == "zoneLow" ? t.Low : type == "zoneMed" ? t.Med : t.High;
            }
        }

        private static string ParamName(JsonElement activator, params string[] names)
        {
            if (Has(activator, "params", out var ps))
                foreach (var n in names)
                    if (Has(ps, n, out _)) return n;
            return null;
        }

        private static int ParamInt(JsonElement activator, string name, int fallback)
            => Has(activator, "params", out var ps) ? Int(ps, name, fallback) : fallback;

        private (MacroTriggerMode Mode, int HoldMs, int DoubleMs, bool Deferred) TriggerFor(
            string type, int shift, int maskId, JsonElement activator)
        {
            // reWASD's documented default long press.
            int hold = ParamInt(activator, "longwaittime", 300);
            int dbl = ParamInt(activator, "doublewaittime", 442);
            int tpl = ParamInt(activator, "triplewaittime", dbl);
            _activatorTypes.TryGetValue((shift, maskId), out var siblings);
            siblings ??= new HashSet<string>();
            switch (type)
            {
                case "long": return (MacroTriggerMode.HoldForMs, hold, dbl, false);
                case "double": return (MacroTriggerMode.DoublePress, hold, dbl, false);
                case "triple": return (MacroTriggerMode.TriplePress, hold, tpl, false);
                case "release": return (MacroTriggerMode.OnRelease, hold, dbl, false);
                case "start": return (MacroTriggerMode.OnPress, hold, dbl, false);
                case "single":
                    if (siblings.Contains("double") || siblings.Contains("triple"))
                        return (MacroTriggerMode.SinglePress, hold, dbl, true);
                    if (siblings.Contains("long"))
                    {
                        // ShortPress shares its threshold with the long press
                        // beside it, so read that sibling's time.
                        return (MacroTriggerMode.ShortPress, SiblingLongHold(shift, maskId, hold), dbl, true);
                    }
                    return (MacroTriggerMode.OnPress, hold, dbl, false);
                default:
                    return (MacroTriggerMode.OnPress, hold, dbl, false);
            }
        }

        private int SiblingLongHold(int shift, int maskId, int fallback)
        {
            foreach (var mapping in Arr(_root, "mappings"))
            {
                if (!Has(mapping, "condition", out var c) || !Has(c, "mask", out var m)) continue;
                if (Int(c, "shiftId") != shift || Int(m, "id") != maskId) continue;
                if (Has(m, "activator", out var a) && Str(a, "type") == "long")
                    return ParamInt(a, "longwaittime", fallback);
            }
            return fallback;
        }

        private void LowerMacros(JsonElement mapping, int shift, int maskId, List<ImportInput> inputs,
            string type, string mode, JsonElement activator, string path, string description)
        {
            var steps = ParseSteps(mapping, shift, path, inputs, out bool notes);
            if (steps.Count == 0) return;

            // An expert activator's wait before it fires: a row has nowhere to
            // hold it, so a wait makes this a macro.
            int firesAfter = ParamInt(activator, "delay", 0);
            if (firesAfter > 0)
            {
                steps.Insert(0, new Step
                {
                    Kind = StepKind.Raw,
                    Raw = new ActionData { Type = MacroActionType.Delay, DurationMs = firesAfter },
                });
            }

            string layer = LayerOf(shift);
            string control = ControlOf(inputs);
            string label = !string.IsNullOrWhiteSpace(description)
                ? description.Trim()
                : string.Join(", ", steps.Where(s => s.Kind == StepKind.Output).Select(s => s.Output.Label));
            if (string.IsNullOrEmpty(label)) label = LabelOf(inputs);
            int padId = steps.Where(s => s.Kind == StepKind.Output && !s.Output.IsKbm).Select(s => s.Output.PadId).FirstOrDefault();
            if (padId == 0) padId = _firstVirtualId;
            bool zone = type.StartsWith("zone", StringComparison.Ordinal);

            var (trigger, holdMs, doubleMs, deferred) = TriggerFor(type, shift, maskId, activator);
            bool plain = steps.All(s => s.Kind == StepKind.Output && s.Action == null && !s.Pulse);
            bool expertMacro = Has(activator, "params", out var ps) && Flag(ps, "macro");

            if (zone && inputs.Any(i => i.IsAnalog))
                _b.Partial(ImportReasons.ZoneOutward, path, label, "");

            ImportMacro Macro(MacroTriggerMode m, MacroRepeatMode repeat, IReadOnlyList<ActionData> actions) => new()
            {
                Name = label,
                Control = control,
                Layer = layer,
                Inputs = inputs,
                Mode = m,
                HoldMs = holdMs,
                DoublePressMs = doubleMs,
                Repeat = repeat,
                PadId = padId,
                Actions = actions,
            };

            // Held outputs on an ordinary press are rows, when the mask is a
            // shape a row can read: gates read the same device as the source.
            bool heldMode = mode == "hold_until_release";
            if (heldMode && plain && !deferred && !expertMacro
                && (type is "single" or "start" || zone) && CanRow(inputs))
            {
                var rows = steps.Select(s => s.Output).Where(o => o.HasRow).ToList();
                var gates = inputs.Skip(1).ToList();
                foreach (var o in rows) _b.Bind(layer, inputs[0], o, gates);

                // What a row cannot carry takes the hold pair.
                var macroKeys = steps.Select(s => s.Output).Where(o => !o.HasRow).ToList();
                if (macroKeys.Count > 0)
                {
                    var held = macroKeys.Select(o => o.Down()).ToArray();
                    _b.AddHoldPair(Macro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, held), ImportBuilder.ReleaseOf(held));
                }
                if (!zone) _b.Clean(path, label, string.Join(", ", steps.Select(s => s.Output.Target)));
                return;
            }

            // A macro's button action writes the Xbox button word, which has no
            // touchpad click. A row reaches it, and this was not a row.
            foreach (var noBit in steps.Where(s => s.Kind == StepKind.Output
                         && s.Output.Kind == ImportOutputKind.PadButton && s.Output.PadBit == 0).ToList())
            {
                _b.Skipped(ImportReasons.NoSuchPadButton, path, noBit.Output.Label, noBit.Output.Label);
                steps.Remove(noBit);
            }
            if (steps.Count == 0) return;

            var tapSteps = TapActions(steps);

            if (heldMode)
            {
                if (deferred)
                {
                    // A Single or Short press fires after the button has
                    // already been judged, often after it is released, so it
                    // can only tap. The hold pair would latch with no release
                    // left to clear it.
                    _b.AddMacro(Macro(trigger, MacroRepeatMode.Once, tapSteps));
                    if (plain) _b.Partial(ImportReasons.HeldAsTap, path, label, trigger.ToString());
                    else _b.Clean(path, label, trigger.ToString());
                    return;
                }
                LowerHeldSequence(steps, Macro(trigger, MacroRepeatMode.UntilRelease, null));
                _b.Clean(path, label, trigger.ToString());
                return;
            }

            switch (mode)
            {
                case "repeat":
                    var r = Macro(trigger == MacroTriggerMode.OnPress ? MacroTriggerMode.WhileHeld : trigger,
                        MacroRepeatMode.UntilRelease, tapSteps);
                    _b.AddMacro(r);
                    _b.Clean(path, label, "Repeat");
                    return;
                case "turbo":
                    var t = Macro(trigger == MacroTriggerMode.OnPress ? MacroTriggerMode.Turbo : trigger,
                        MacroRepeatMode.UntilRelease, tapSteps);
                    _b.AddMacro(t);
                    _b.Clean(path, label, "Turbo");
                    return;
                case "toggle":
                    LowerToggle(steps, tapSteps, Macro(trigger, MacroRepeatMode.Once, null), type, path, label);
                    return;
                default:
                    _b.AddMacro(Macro(trigger, MacroRepeatMode.Once, tapSteps));
                    _b.Clean(path, label, trigger.ToString());
                    return;
            }
        }

        private static bool CanRow(IReadOnlyList<ImportInput> inputs)
        {
            if (inputs.Count > 3) return false;
            if (inputs.Count == 1) return true;
            // A gate is a plain read of the source's own device.
            var first = inputs[0];
            return inputs.All(i => !i.IsAnalog && !i.HalfAxis
                && string.Equals(i.DeviceGuid ?? "", first.DeviceGuid ?? "", StringComparison.OrdinalIgnoreCase)
                && i.IsKeyboard == first.IsKeyboard && i.IsMouse == first.IsMouse);
        }

        /// <summary>The steps run once: a plain output taps, an explicit down
        /// or up sets or clears the latch.</summary>
        private static List<ActionData> TapActions(List<Step> steps)
        {
            var actions = new List<ActionData>();
            foreach (var s in steps)
            {
                if (s.Kind == StepKind.Raw)
                {
                    actions.Add(s.Raw);
                    continue;
                }
                switch (s.Action)
                {
                    case "down":
                    case "single_down":
                        actions.Add(s.Output.Down() ?? s.Output.Tap());
                        break;
                    case "up":
                        var up = s.Output.Up();
                        if (up != null) actions.Add(up);
                        break;
                    default:
                        actions.Add(s.Output.Tap());
                        break;
                }
            }
            return actions;
        }

        /// <summary>A sequence held until release: plain outputs and downs set
        /// their latches, an explicit up clears one, and whatever the sequence
        /// can have left set clears on release.</summary>
        private void LowerHeldSequence(List<Step> steps, ImportMacro press)
        {
            var down = new List<ActionData>();
            foreach (var s in steps)
            {
                if (s.Kind == StepKind.Raw)
                {
                    down.Add(s.Raw);
                    continue;
                }
                if (s.Action == "up")
                {
                    var up = s.Output.Up();
                    if (up != null) down.Add(up);
                    continue;
                }
                // A pulsed output repeats on its own while the macro runs.
                var held = (s.Pulse ? s.Output.Pulse(100) : null) ?? s.Output.Down() ?? s.Output.Tap();
                down.Add(held);
            }
            press.Actions = down;
            _b.AddHoldPair(press, ImportBuilder.ReleaseOf(down));
        }

        /// <summary>A toggle: each fire flips the outputs' latches. Plain
        /// outputs flip directly. A sequence with waits runs repeatedly while
        /// latched, the Toggle trigger's own contract. On a Double, Triple or
        /// Long press the same gesture stops it, where reWASD stops on one
        /// click.</summary>
        private void LowerToggle(List<Step> steps, List<ActionData> tapSteps, ImportMacro macro, string type, string path, string label)
        {
            bool flips = steps.All(s => s.Kind == StepKind.Output && s.Action == null && s.Output.Flip() != null);
            if (flips)
            {
                macro.Actions = steps.Select(s => s.Output.Flip()).ToArray();
                macro.Repeat = MacroRepeatMode.Once;
                _b.AddMacro(macro);
            }
            else
            {
                if (type == "single") macro.Mode = MacroTriggerMode.Toggle;
                macro.Repeat = MacroRepeatMode.UntilRelease;
                macro.Actions = tapSteps;
                _b.AddMacro(macro);
            }
            if (type is "double" or "triple" or "long")
                _b.Partial(ImportReasons.ToggleSameGesture, path, label, "Toggle");
            else
                _b.Clean(path, label, "Toggle");
        }

        // ── Jumps ───────────────────────────────────────────────────────

        private void LowerJump(JsonElement jump, int hostShift, int maskId, List<ImportInput> inputs, string type, string mode,
            JsonElement activator, string path, string description)
        {
            int toShift = Int(jump, "layer");
            string target = LayerOf(toShift);
            string host = LayerOf(hostShift);
            string control = ControlOf(inputs);
            string label = string.IsNullOrWhiteSpace(description)
                ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_Line_ToLayer_Format,
                    toShift == 0 ? Strings.Instance.Pad_Shift_BaseTabLabel : _b.LayerName(target))
                : description.Trim();

            // The file's own way of writing a Hold: a jump out on press and,
            // on the layer, a jump back on release. The activator is both.
            if (toShift == 0 && hostShift != 0 && _engagedBy.Contains((hostShift, control)))
            {
                _b.Clean(path, label, "Hold");
                return;
            }

            bool held = mode == "hold_until_release";
            bool toggled = mode == "toggle";
            if (toShift != 0 && (held || toggled) && type is "single" or "start" or "long" or "double" or "release")
            {
                var act = ImportBuilder.ActivatorFor(inputs, held ? "Hold" : "Toggle", target);
                act.DelayMs = Int(jump, "delay");
                act.PostponeMapping = Flag(jump, "postpone");
                if (hostShift != 0) act.HostLayerMask = host;
                // The gesture rides the activator: a long press is its delay,
                // a double press its window, a release its fire edge.
                if (type == "long") act.DelayMs = ParamInt(activator, "longwaittime", 300);
                if (type == "double") act.DoublePressMs = ParamInt(activator, "doublewaittime", 442);
                if (type == "release") act.FireOnRelease = true;
                _b.AddActivator(act);
                _b.Clean(path, label, act.Mode);
                return;
            }

            // Every other jump is one shot, on its own gesture.
            var (trigger, holdMs, doubleMs, _) = TriggerFor(type, hostShift, maskId, activator);
            _b.AddMacro(new ImportMacro
            {
                Name = label,
                Control = control,
                Layer = host,
                Inputs = inputs,
                Mode = trigger,
                HoldMs = holdMs,
                DoublePressMs = doubleMs,
                PadId = _firstVirtualId,
                Actions = new[]
                {
                    new ActionData { Type = MacroActionType.SwitchLayer, SwitchLayerMask = target },
                },
            });
            _b.Clean(path, label, "SwitchLayer");
        }

        // ── Mouse region ────────────────────────────────────────────────

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private void LowerRegion(JsonElement region, string layer, List<ImportInput> inputs, string path, string description)
        {
            int width = Math.Max(0, Int(region, "x2") - Int(region, "x1"));
            int height = Math.Max(0, Int(region, "y2") - Int(region, "y1"));
            int insetX = Math.Max(0, (GetSystemMetrics(0) - width) / 2);
            int insetY = Math.Max(0, (GetSystemMetrics(1) - height) / 2);
            ActionData Clamp(MacroLatchDirection d) => new()
            {
                Type = MacroActionType.MouseLimitRegion,
                CursorClampMode = CursorClampMode.XAndY,
                CursorClampInsetX = insetX,
                CursorClampInsetY = insetY,
                LatchDirection = d,
            };
            string label = string.IsNullOrWhiteSpace(description) ? "Mouse region" : description.Trim();
            _b.AddHoldPair(new ImportMacro
            {
                Name = label,
                Control = ControlOf(inputs),
                Layer = layer,
                Inputs = inputs,
                Mode = MacroTriggerMode.OnPress,
                PadId = _firstVirtualId,
                Actions = new[] { Clamp(MacroLatchDirection.On) },
            }, new[] { Clamp(MacroLatchDirection.Off) });
            _b.Partial(ImportReasons.RegionCentered, path, label, "MouseLimitRegion");
        }

        // ── Radial menus ────────────────────────────────────────────────

        /// <summary>Each top circle becomes one radial menu on the layer its
        /// sectors' mappings sit on, opened by whatever jumps to that layer.
        /// A sector with a sub-menu has no ring of its own in PadForge, so the
        /// sub-menu's sectors join the ring as more slots and every action
        /// stays reachable.</summary>
        private void ReadRadialMenus()
        {
            var circles = Arr(_root, "radialMenuCircles").ToList();
            if (circles.Count == 0) return;
            var circleById = new Dictionary<int, JsonElement>();
            foreach (var c in circles) circleById.TryAdd(Int(c, "id"), c);
            IndexRadialMappings();
            var sectors = new Dictionary<int, JsonElement>();
            foreach (var s in Arr(_root, "radialMenuSectors")) sectors[Int(s, "id")] = s;

            int menuId = 1;
            foreach (var circle in circles.Where(c => !Has(c, "parentSectorId", out _)))
            {
                int circleId = Int(circle, "id");
                bool flattened = false;
                var leaves = new List<int>();
                // A file can point a sector at its own circle, and a recursion
                // nothing bounds would take the process with it.
                var visited = new HashSet<int> { circleId };
                void Walk(JsonElement c, int depth)
                {
                    foreach (var s in Arr(c, "sectors"))
                    {
                        if (!TryNumber(s, out int sectorId)) continue;
                        if (sectors.TryGetValue(sectorId, out var sector) && Has(sector, "childCircleId", out _))
                        {
                            int child = Int(sector, "childCircleId");
                            if (circleById.TryGetValue(child, out var childCircle))
                            {
                                if (depth < 8 && visited.Add(child))
                                {
                                    flattened = true;
                                    Walk(childCircle, depth + 1);
                                }
                                continue;
                            }
                        }
                        leaves.Add(sectorId);
                    }
                }
                Walk(circle, 0);
                if (leaves.Count == 0) continue;

                int shift = RadialShiftOf(leaves);
                string layer = LayerOf(shift);
                var menu = new MenuDefinitionEntry
                {
                    DeviceGuid = "",
                    MenuId = menuId,
                    Name = _b.LayerName(layer),
                    Kind = MenuKind.Radial,
                    HostDescriptor = _radialHost.TryGetValue(layer, out var host) ? host : "Gamepad RightStick",
                    LayerMask = layer,
                    // Touch Release commits when the stick re-centers and when
                    // the hosting layer ends, reWASD's "leave the Shift".
                    FireType = MenuFireType.TouchRelease,
                    CellCount = leaves.Count,
                    HasCenter = false,
                    ShowLabels = true,
                    LayerHoldsOpen = false,
                };

                for (int i = 0; i < leaves.Count; i++)
                {
                    sectors.TryGetValue(leaves[i], out var sector);
                    string name = sector.ValueKind == JsonValueKind.Object ? Str(sector, "description") : null;
                    menu.Items.Add(new MenuItemDefinition
                    {
                        Index = i + 1,
                        Label = string.IsNullOrWhiteSpace(name)
                            ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_MenuItem_Format, i + 1)
                            : name.Trim(),
                    });
                    LowerSector(circleId, leaves[i], menuId, i + 1, shift);
                }
                _b.AddMenu(menu);
                _b.Partial(flattened ? ImportReasons.RadialFlattened : ImportReasons.RadialMenu,
                    _b.LayerName(layer), menu.Name, "Menu " + menuId);
                menuId++;
            }
        }

        /// <summary>Which masks name each sector and which mappings read each
        /// mask, in file order, so a file's menus cost one pass over its
        /// tables.</summary>
        private void IndexRadialMappings()
        {
            foreach (var kv in _masks)
            {
                foreach (var r in Arr(kv.Value, "radialMenuSet"))
                {
                    int sector = Int(r, "sectorId");
                    if (!_masksOfSector.TryGetValue(sector, out var list))
                        _masksOfSector[sector] = list = new List<int>();
                    if (!list.Contains(kv.Key)) list.Add(kv.Key);
                }
            }
            int order = 0;
            foreach (var mapping in Arr(_root, "mappings"))
            {
                order++;
                if (!Has(mapping, "condition", out var c) || !Has(c, "mask", out var m)) continue;
                int mask = Int(m, "id");
                if (!_mappingsOfMask.TryGetValue(mask, out var list))
                    _mappingsOfMask[mask] = list = new List<(int, JsonElement)>();
                list.Add((order, mapping));
            }
        }

        private int RadialShiftOf(List<int> sectorIds)
        {
            int firstOrder = int.MaxValue;
            int shift = 0;
            foreach (int sector in sectorIds)
            {
                if (!_masksOfSector.TryGetValue(sector, out var masks)) continue;
                foreach (int mask in masks)
                {
                    if (!_mappingsOfMask.TryGetValue(mask, out var mappings)) continue;
                    var (order, mapping) = mappings[0];
                    if (order < firstOrder && Has(mapping, "condition", out var c))
                    {
                        firstOrder = order;
                        shift = Int(c, "shiftId");
                    }
                }
            }
            if (firstOrder != int.MaxValue) return shift;
            foreach (var kv in _shiftType)
                if (kv.Value == "radialMenu") return kv.Key;
            return 0;
        }

        /// <summary>A sector's mappings read the menu item's fire. They live
        /// on Base: the fire lands as the layer ends, and an item only fires
        /// while its menu is up anyway.</summary>
        private void LowerSector(int circleId, int sectorId, int menuId, int slot, int shift)
        {
            if (!_masksOfSector.TryGetValue(sectorId, out var masks)) return;
            foreach (int mask in masks)
            {
                if (!_mappingsOfMask.TryGetValue(mask, out var mappings)) continue;
                var input = new ImportInput
                {
                    Control = "menu:" + menuId + ":" + slot,
                    Label = "Menu " + menuId + " item " + slot,
                    Descriptor = "Menu " + menuId + " Item " + slot,
                };
                foreach (var (_, mapping) in mappings)
                {
                    if (!Has(mapping, "condition", out var c) || !Has(c, "mask", out var m)) continue;
                    Has(m, "activator", out var a);
                    var inputs = new List<ImportInput> { input };
                    string path = PathOf(shift, input.Label);
                    LowerMacros(mapping, 0, mask, inputs, Str(a, "type") ?? "single",
                        Str(a, "mode") ?? "hold_until_release", a, path, Str(mapping, "description"));
                }
            }
        }

        // ── Tuning ──────────────────────────────────────────────────────

        private void ReadDeviceTuning()
        {
            if (!Has(_root, "devices", out var devices)) return;
            foreach (var h in Arr(devices, "hardware"))
            {
                if (Str(h, "name") != "gamepad")
                {
                    // A mouse's own speed and scroll, a keyboard's analog keys.
                    if (Has(h, "default", out var own))
                        foreach (var key in Arr(own, "analogButtons"))
                            _b.Skipped(ImportReasons.AnalogActuation, Str(key, "name") ?? "", "");
                    if (Has(h, "mouse", out _) || (Has(h, "default", out var scope) && (Has(scope, "stick", out _) || Has(scope, "scrolls", out _))))
                        _b.Partial(ImportReasons.ControllerSetting, "Mouse", "", "", "mouse");
                    continue;
                }
                if (Has(h, "default", out var def)) ReadTuningScope(def, 0);
                foreach (var scope in Arr(h, "shifts")) ReadTuningScope(scope, Int(scope, "shiftId"));
            }
        }

        private void ReadTuningScope(JsonElement scope, int shift)
        {
            string Scoped(string label) => PathOf(shift, label);
            foreach (var st in Arr(scope, "sticks"))
            {
                string name = Str(st, "name") ?? "";
                if (name is not ("left" or "right"))
                {
                    if (Names(st).Any(n => n != "name"))
                        _b.Partial(ImportReasons.ControllerSetting, Scoped(name), "", "", name);
                    continue;
                }
                if (Has(st, "mouse", out _))
                    _b.Partial(ImportReasons.MoveSpeed, Scoped(StickLabel(name)), "", "");
                NoteLeftovers(st, Scoped(StickLabel(name)), "name", "sensitivity", "rotation", "zones", "curves", "mouse");
                var t = new StickTuning();
                if (Has(st, "sensitivity", out var sens))
                {
                    // Shunt's compiler reads the same field as a fraction of
                    // 32767 and floors it at a tenth.
                    t.SensX = Math.Clamp(Int(sens, "x", 32767) / 32767.0, 0.1, 1.0);
                    t.SensY = Math.Clamp(Int(sens, "y", 32767) / 32767.0, 0.1, 1.0);
                }
                t.RotationDeg = Int(st, "rotation");
                foreach (var z in Arr(st, "zones"))
                {
                    int value = Int(z, "x", Int(z, "y"));
                    int percent = (int)Math.Clamp(Math.Round(value * 100.0 / 32767.0), 1, 99);
                    switch (Str(z, "name"))
                    {
                        case "low":
                            t.Low = percent;
                            t.Inner = percent / 100.0;
                            break;
                        case "med": t.Med = percent; break;
                        case "high": t.High = percent; break;
                    }
                }
                if (Has(st, "curves", out var curves) && Has(curves, "points", out var points))
                {
                    double e = FitExponent(points, out int count);
                    if (e > 0)
                    {
                        t.CurveExponent = e;
                        _b.Partial(ImportReasons.CurveFitted, Scoped(StickLabel(name)), "Curve",
                            e.ToString("0.##", CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture));
                    }
                }
                _tuning[(shift, name)] = t;
                if (Has(st, "sensitivity", out _) || t.RotationDeg != 0 || t.Inner > 0)
                    _b.Clean(Scoped(StickLabel(name)), "Tuning", "");
            }

            foreach (var tr in Arr(scope, "triggers"))
            {
                string name = Str(tr, "name") ?? "";
                if (name is not ("left" or "right")) continue;
                NoteLeftovers(tr, Scoped(name == "left" ? "Left trigger" : "Right trigger"), "name", "zones");
                var src = new MappingSource();
                var zones = new StickTuning();
                foreach (var z in Arr(tr, "zones"))
                {
                    int value = Int(z, "x", Int(z, "y"));
                    int percent = (int)Math.Clamp(Math.Round(value * 100.0 / 32767.0), 1, 99);
                    switch (Str(z, "name"))
                    {
                        case "low":
                            zones.Low = percent;
                            src.ParamStickDeadZoneShape = 1;
                            src.ParamStickDeadZoneInner = Math.Clamp(value / 32767.0, 0.0, 0.99);
                            break;
                        case "med":
                            zones.Med = percent;
                            break;
                        case "high":
                            zones.High = percent;
                            src.ParamRangeOuter = Math.Clamp(value / 32767.0, 0.01, 1.0);
                            break;
                    }
                }
                _triggerTuning[(shift, name)] = src;
                _tuning[(shift, "trigger:" + name)] = zones;
                _b.Clean(Scoped(name == "left" ? "Left trigger" : "Right trigger"), "Tuning", "");
            }

            foreach (var key in Arr(scope, "analogButtons"))
                _b.Skipped(ImportReasons.AnalogActuation, Scoped(Str(key, "name") ?? ""), "");

            // A touchpad's mode and glide are what nearly every file writes
            // without anyone having set them.
            foreach (var pad in Arr(scope, "touchpads"))
                NoteLeftovers(pad, Scoped("Touchpad"), "id", "mode", "trackballFriction");
            if (Arr(scope, "vibration").Any())
                _b.Partial(ImportReasons.ControllerSetting, Scoped("Vibration"), "", "", "vibration");
        }

        private static string StickLabel(string name) => name == "left" ? "Left stick" : "Right stick";

        /// <summary>One line naming whatever an element holds past what the
        /// importer reads, so nothing a file set goes unmentioned.</summary>
        private void NoteLeftovers(JsonElement e, string path, params string[] read)
        {
            var left = Names(e).Where(n => !read.Contains(n)).ToList();
            if (left.Count > 0)
                _b.Partial(ImportReasons.ControllerSetting, path, "", "", string.Join(", ", left));
        }

        /// <summary>The exponent nearest a curve's points, least squares in log
        /// space. PadForge's cards carry the fold from an exponent, so the
        /// curve's shape survives and its exact points do not, which the
        /// report says.</summary>
        private static double FitExponent(JsonElement points, out int count)
        {
            count = 0;
            if (!Has(points, "horizontal", out var list) && !Has(points, "vertical", out list)) return 0;
            if (list.ValueKind != JsonValueKind.Array) return 0;
            var pts = list.EnumerateArray()
                .Select(p => (Travel: (double)Int(p, "travel"), Value: (double)Int(p, "value")))
                .Where(p => p.Travel > 0 && p.Value > 0)
                .OrderBy(p => p.Travel)
                .ToList();
            if (pts.Count == 0) return 0;
            double maxTravel = pts[^1].Travel;
            double maxValue = Math.Max(pts[^1].Value, 1.0);
            double num = 0, den = 0;
            foreach (var (travel, value) in pts)
            {
                double x = travel / maxTravel;
                double y = value / maxValue;
                if (x >= 1.0 || y >= 1.0) continue;
                double lx = Math.Log(x), ly = Math.Log(y);
                num += lx * ly;
                den += lx * lx;
                count++;
            }
            return den > 0 ? Math.Clamp(num / den, 0.2, 8.0) : 0;
        }

        // ── Lightbar ────────────────────────────────────────────────────

        /// <summary>The file's lightbar, one color or mode per layer. Base
        /// writes first and a layer's macro runs after it in the same frame, so
        /// the layer's color is the one a frame ends on.</summary>
        private void ReadLed()
        {
            if (!Has(_root, "config", out var cfg) || !Has(cfg, "led", out var led)) return;
            if (Has(led, "default", out var def)) LowerLed(def, 0);
            foreach (var s in Arr(led, "shifts")) LowerLed(s, Int(s, "shiftId"));
        }

        private void LowerLed(JsonElement led, int shift)
        {
            string mode = Str(led, "mode") ?? "solid";
            string layer = LayerOf(shift);
            string path = PathOf(shift, "Lightbar");
            ActionData color = null;
            foreach (var c in Arr(led, "colors"))
            {
                if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 3) continue;
                static byte Channel(JsonElement v) => TryNumber(v, out int n) ? (byte)Math.Clamp(n, 0, 255) : (byte)0;
                color = ImportBuilder.ProfileColor(Channel(c[0]), Channel(c[1]), Channel(c[2]));
                break;
            }

            ActionData action;
            switch (mode)
            {
                case "rainbow":
                    action = new ActionData { Type = MacroActionType.LightbarModeSet, LightbarTargetMode = LightbarMode.Rainbow };
                    break;
                case "breath":
                    action = new ActionData { Type = MacroActionType.LightbarModeSet, LightbarTargetMode = LightbarMode.Breathing };
                    break;
                default:
                    action = color;
                    break;
            }
            if (action == null) return;

            _b.AddMacro(new ImportMacro
            {
                Name = "Lightbar",
                Control = "lightbar",
                RunsBesideLayers = true,
                Layer = layer,
                Mode = MacroTriggerMode.Always,
                PadId = _firstVirtualId,
                Actions = new[] { action },
            });

            if (mode is "solid" or "rainbow")
                _b.Clean(path, mode, action.Type.ToString());
            else
                _b.Partial(ImportReasons.LedShownAs, path, mode, action.Type.ToString(), mode);
        }

        // ── What the file has and a profile does not ────────────────────

        private void ReportLeftovers()
        {
            if (Arr(_root, "crosshairs").Any())
                _b.Skipped(ImportReasons.Crosshair, "Crosshairs", "");

            // Every public sample writes 1, and no public source says how
            // another value changes a file's times.
            if (Has(_root, "config", out var cfg) && Has(cfg, "minTimeUnit", out var unit) && TryNumber(unit, out int ms) && ms != 1)
                _b.Partial(ImportReasons.ControllerSetting, "Macros", "", "", "minTimeUnit");
        }
    }
}
