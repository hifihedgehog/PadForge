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

        // reWASD's untouched axis range, which it never writes
        // (reWASDCommon Constants.DefaultStickSensitivity).
        private const int DefaultAxisRange = 13573;

        private sealed class StickTuning
        {
            // The axis ranges that size the direction zones, and whether a
            // corner is a diagonal's alone ("Use 8 digital directions").
            public int RangeX = DefaultAxisRange;
            public int RangeY = DefaultAxisRange;
            public bool EightWay;
            public double RotationDeg;
            public double CurveExponent;
            public int Low = DefaultLowZone;
            public int Med = DefaultMedZone;
            public int High = DefaultHighZone;

            // A zone's height when it is an ellipse, 0 when it is a circle.
            public int LowY;
            public int MedY;
            public int HighY;

            /// <summary>Zone 0 (low), 1 (medium) or 2 (high), across and up
            /// and down.</summary>
            public (int X, int Y) Edge(int zone) => zone switch
            {
                0 => (Low, LowY > 0 ? LowY : Low),
                1 => (Med, MedY > 0 ? MedY : Med),
                _ => (High, HighY > 0 ? HighY : High),
            };
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
        // Hardware remaps replace the native key as an unmap does, so they
        // ask for Consume Mapped Inputs too.
        private int _keyboardRemaps;
        private int _mouseRemaps;
        private int _mouseMoves;
        private readonly List<(JsonElement Pad, int Id)> _virtualPads = new();
        private int _firstVirtualId = 1;
        private bool _noted;

        /// <summary>Per layer, whether the gyro is in Tilt mode (the gyro
        /// entry's "tilt"). A layer with no gyro entry takes Base's.</summary>
        private readonly Dictionary<int, bool> _gyroTilt = new();

        /// <summary>Set when a Gyro mode left, right or lean input, or a gyro
        /// stick source's X, is read, so the report says which rotation it
        /// reads.</summary>
        private bool _gyroAxesRead;

        /// <summary>Set when a touchpad diagonal is read.</summary>
        private bool _touchDiagonalRead;

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
            t.ReadShiftActivators();
            t.ReadVirtualSticks();
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
                Runs = t._b.Runs,
                Starts = t._b.Starts,
            };
        }

        // The largest public sample has 193 masks, 129 mappings and 3 shifts.
        // These leave room for any real file and refuse one built to spin the
        // tables' loops, which look one list up in another.
        private const int MaxTableEntries = 5000;
        private const int MaxShifts = 64;

        // The schema's set holds one to four inputs (definitions/set,
        // maxItems 4), and a longer one would become one macro reading every
        // input on every poll.
        private const int MaxSetInputs = 4;

        private static bool TooManyEntries(JsonElement root)
        {
            static int Length(JsonElement e, string name) => Has(e, name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
            int Count(string name) => Length(root, name);
            if (Count("shifts") > MaxShifts
                || Count("masks") > MaxTableEntries
                || Count("mappings") > MaxTableEntries
                || Count("commands") > MaxTableEntries
                || Count("radialMenuCircles") > MaxTableEntries
                || Count("radialMenuSectors") > MaxTableEntries)
                return true;
            return Arr(root, "masks").Any(m => Length(m, "set") > MaxSetInputs)
                || Arr(root, "shifts").Any(s => Length(s, "set") > MaxSetInputs
                                                || Length(s, "unheritableMasks") > MaxTableEntries);
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
            FindPresetAndUnusedLayers();
            foreach (var s in Arr(_root, "shifts"))
                if (!_shiftAs.ContainsKey(Int(s, "id"))) _shiftType[Int(s, "id")] = Str(s, "type") ?? "default";

            foreach (var mapping in Arr(_root, "mappings"))
            {
                if (!Has(mapping, "condition", out var c) || !Has(c, "mask", out var mask)) continue;
                var key = (ShiftIdOf(c), Int(mask, "id"));
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
                    // PadForge hides a device only once its user confirms it on
                    // the Devices page, never from a file.
                    if (Flag(h, "hidePhysicalGamepad"))
                        _b.Partial(ImportReasons.HideController, Strings.Instance.Import_Line_Controller, "", "");
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
                    // reWASD's Use for UDP server sends the virtual pad's
                    // motion to emulators over cemuhook (help.rewasd.com,
                    // Servers), which is PadForge's DSU motion server.
                    if (Flag(g, "udp"))
                    {
                        _b.EnableDsu = true;
                        _b.Clean(type, "UDP server", "DSU motion server");
                    }
                    // An external device carries the pad through an ESP32 or
                    // Bluetooth adapter to a console, a phone or another PC.
                    if (Flag(g, "external"))
                        _b.Partial(ImportReasons.RemoteTarget, type, "", "");
                    // The pad's report rate and its virtual USB hub have no
                    // published unit or counterpart, so a file that sets them
                    // says so. Normal is the schema's first value.
                    string performance = Str(g, "performance");
                    if (!string.IsNullOrEmpty(performance) && performance != "normal")
                        _b.Partial(ImportReasons.ControllerSetting, type, "", "", "performance");
                    if (Flag(g, "virtualUsbHub"))
                        _b.Partial(ImportReasons.ControllerSetting, type, "", "", "virtualUsbHub");
                    _virtualPads.Add((g, id));
                }
            }
            if (usesPad && !_b.HasPad && !keyboardMouseOnly)
                _b.DeclarePad(1, VirtualControllerType.Xbox, "xbox-360-wired");

            if (_twoPads && MappingsTellPadsApart())
                _b.Partial(ImportReasons.SharedControllers, Strings.Instance.Import_Line_Controller, "", "");
        }

        /// <summary>True when the file's inputs name more than one physical
        /// gamepad, which a slot's device-free sources cannot keep apart. Two
        /// listed controllers that share every mapping lose nothing.</summary>
        private bool MappingsTellPadsApart()
        {
            var named = new HashSet<int>();
            void Note(JsonElement e)
            {
                int device = Int(e, "deviceId");
                if (ClassOf(device, Str(e, "description")) == "gamepad") named.Add(device);
            }
            foreach (var m in Arr(_root, "masks"))
                foreach (var item in Arr(m, "set")) Note(item);
            foreach (var mapping in Arr(_root, "mappings"))
                if (Has(mapping, "condition", out var c) && Has(c, "hardware", out var hw)) Note(hw);
            return named.Count > 1;
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

        /// <summary>Shift ids read as another: a preset's one layer reads as
        /// Base, and a layer reWASD never opens reads as -1, which nothing
        /// imports.</summary>
        private readonly Dictionary<int, int> _shiftAs = new();

        private int ShiftIdOf(JsonElement e)
        {
            int id = Int(e, "shiftId");
            return _shiftAs.TryGetValue(id, out int s) ? s : id;
        }

        private void FindPresetAndUnusedLayers()
        {
            var shifts = Arr(_root, "shifts").ToList();
            var jumped = new HashSet<int>();
            var mapped = new HashSet<int>();
            foreach (var m in Arr(_root, "mappings"))
            {
                if (Has(m, "jumpToLayer", out var j)) jumped.Add(Int(j, "layer"));
                if (Has(m, "condition", out var c)) mapped.Add(Int(c, "shiftId"));
            }
            bool Opens(JsonElement s) => jumped.Contains(Int(s, "id"))
                || (Has(s, "set", out var set) && set.ValueKind == JsonValueKind.Array && set.GetArrayLength() > 0);

            // reWASD's own presets hold one layer that a user applies to a
            // layer of a config: the mappings, or the list of what not to
            // inherit, sit on it, Base holds nothing and nothing opens it.
            // Read on its own, it is the config.
            if (shifts.Count == 1 && !mapped.Contains(0) && (Str(shifts[0], "type") ?? "default") == "default" && !Opens(shifts[0])
                && (mapped.Contains(Int(shifts[0], "id")) || Arr(shifts[0], "unheritableMasks").Any()))
                _shiftAs[Int(shifts[0], "id")] = 0;

            // Every config carries a Radial Menu shift, and only a jump to it
            // opens it (reWASD's help, Radial menu). With none it never runs.
            foreach (var s in shifts)
                if (Str(s, "type") == "radialMenu" && !Opens(s) && !_shiftAs.ContainsKey(Int(s, "id")))
                    _shiftAs[Int(s, "id")] = -1;
        }

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
            // Numpad Enter comes back at 0x88, the index RawInputListener
            // keeps it under apart from Enter, which is also the index the
            // output side sends it from.
            if (!DirectInputKeys.TryGetVirtualKey(scanCode, out byte vk)) return null;
            _b.ReadsKeyboard = true;
            return new ImportInput
            {
                Control = "kb:" + vk,
                Label = "Keyboard " + ImportOutput.KeyName(vk),
                Descriptor = "Button " + vk,
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
                // Windows reports a notch up as +120 (RawInputListener), and
                // the mouse's Axis 2 sits above center for it, the upper half.
                171 => Axis(2, false, "Wheel Up"),
                172 => Axis(2, true, "Wheel Down"),
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
            // A stick direction is reWASD's own test (jlserv.exe, opcode 0xE1,
            // 0x14007b880): past the zone's edge, Up and Down fire while |x|
            // times the vertical axis range is under |y| times 32768, and Left
            // and Right while |y| times the horizontal range is under |x|
            // times 32768. That region is a sector of the ring, and a zone's
            // edge is an ellipse, x across and y up and down. reWASD turns the
            // sector counterclockwise for a positive rotation (StickCrossView,
            // OverlayMenuVME.CalcAngle), so the push that reads as a direction
            // lies the rotation back. A straight stick reads the half the
            // direction lies in, so an analog output takes that axis raw, the
            // way reWASD passes a stick to a stick ("Virtual controller
            // deflection doesn't influence the stick-to-stick mappings",
            // forum thread 223048). A turned sector can cross into the other
            // half, so a turned stick reads the ring. A banded direction stops
            // at the next zone's edge.
            ImportInput Directional(string stick, string axis, bool lower, double bearing, double reach, int zone, bool banded, string label)
            {
                var t = TuningOf(shift, stick);
                string ring = "Gamepad " + (stick == "left" ? "Left" : "Right") + "StickRing";
                var d = t.RotationDeg == 0
                    ? new ImportInput { Control = "gp:" + id, Label = label, Descriptor = axis, HalfAxis = true, Invert = lower, IsAnalog = true }
                    : Ring(ring, 0, label);
                d.DeadZone = 1;
                double center = Math.Round(((bearing - t.RotationDeg) % 360.0 + 360.0) % 360.0, 2);
                d.Band = ZoneSector(t, ring, center, Math.Round(Math.Max(reach, 0.0), 2), zone, banded);
                return d;
            }
            // Up, Down, Left or Right past the low zone. Bearings run
            // counterclockwise from right.
            ImportInput Dir(string axis, bool lower, string stick, string label)
            {
                bool vertical = axis.EndsWith("Y", StringComparison.Ordinal);
                double bearing = vertical ? (lower ? 90 : 270) : (lower ? 180 : 0);
                return Directional(stick, axis, lower, bearing, CardinalReach(TuningOf(shift, stick), vertical), 0, false, label);
            }
            // A zone of the ring with no direction. A circle reads the ring
            // past the zone's edge, banded below the next one. An ellipse reads
            // through a sector that turns all the way round.
            ImportInput RingZone(string stick, int zone, string label)
            {
                var t = TuningOf(shift, stick);
                string ring = "Gamepad " + (stick == "left" ? "Left" : "Right") + "StickRing";
                var (ex, ey) = t.Edge(zone);
                var (nx, ny) = zone < 2 ? t.Edge(zone + 1) : (0, 0);
                if (ey == ex && ny == nx)
                    return Ring(ring, ex, label).Below(nx);
                var d = Ring(ring, 1, label);
                d.Band = ZoneSector(t, ring, 0, 180, zone, zone < 2);
                return d;
            }
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
            // A direction inside one zone: Low, Med or High by index 0 to 3
            // Up, Down, Left, Right, reWASD's order for ids 109 to 132. It
            // reads past the zone's edge and short of the next one, and the
            // high zone is open upward.
            ImportInput ZoneDir(int k, string stick)
            {
                int zone = k / 4, dir = k % 4;
                string prefix = stick == "left" ? "Left" : "Right";
                var t = TuningOf(shift, stick);
                bool vertical = dir <= 1, lower = dir is 0 or 2;
                double bearing = vertical ? (lower ? 90 : 270) : (lower ? 180 : 0);
                return Directional(stick, "Gamepad " + prefix + "Stick" + (vertical ? "Y" : "X"), lower, bearing,
                    CardinalReach(t, vertical), zone, true,
                    prefix + " stick " + (zone == 0 ? "low" : zone == 1 ? "medium" : "high") + " zone "
                    + (dir switch { 0 => "Up", 1 => "Down", 2 => "Left", _ => "Right" }));
            }
            // A diagonal is the corner where both of reWASD's tests pass,
            // past the low zone. Measured from the horizontal axis toward the
            // vertical one, it runs from 90 less the vertical reach to the
            // horizontal reach: 22.5 to 67.5 degrees at the default ranges.
            ImportInput Diagonal(double bearing, string stick, string label)
            {
                string prefix = stick == "left" ? "Left" : "Right";
                var t = TuningOf(shift, stick);
                double horizontal = Reach(t.RangeX), vertical = Reach(t.RangeY);
                double mid = (90.0 - vertical + horizontal) / 2.0, half = (horizontal + vertical - 90.0) / 2.0;
                double center = bearing switch { 45 => mid, 135 => 180 - mid, 225 => 180 + mid, _ => 360 - mid };
                return Directional(stick, "Gamepad " + prefix + "StickY", bearing is > 0 and < 180, center, half, 0, false, label);
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
            // reWASD's help (Gyroscope): "By default in reWASD, in Gyro mode,
            // yaw stands for Lean mappings, while the roll is used to look
            // around." PadForge's Gyro Roll is SDL's roll negated
            // (SourceCoercion.ReadTunedGyroRate), so its upper half is a roll
            // to the right, and Gyro Yaw's upper half turns the nose left.
            ImportInput Look(ImportInput input)
            {
                _gyroAxesRead = true;
                return input;
            }
            // Tilt mode reads how far the gamepad is tilted (reWASD's help,
            // Gyroscope), PadForge's gravity lean: Gyro Lean X is positive
            // with the right edge down, Gyro Lean Y with the nose up.
            bool tilt = GyroTilt(shift);
            // A touchpad diagonal is two of the touchpad d-pad's directions at
            // once, which it reports only in its 8-Way mode
            // (GestureRecognizer.ComputeJoystickDPad).
            ImportInput TouchDiagonal(int pad, string vertical, string horizontal, string label)
            {
                _touchDiagonalRead = true;
                var input = Btn("Touchpad " + pad + " DPad" + vertical, label);
                input.Gate = "Touchpad " + pad + " DPad" + horizontal;
                return input;
            }

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
                37 => RingZone("left", 0, "Left stick low zone"),
                38 => RingZone("left", 1, "Left stick medium zone"),
                39 => RingZone("left", 2, "Left stick high zone"),
                40 => Dir("Gamepad LeftStickY", true, "left", "Left stick Up"),
                41 => Dir("Gamepad LeftStickY", false, "left", "Left stick Down"),
                42 => Dir("Gamepad LeftStickX", true, "left", "Left stick Left"),
                43 => Dir("Gamepad LeftStickX", false, "left", "Left stick Right"),
                44 => RingZone("right", 0, "Right stick low zone"),
                45 => RingZone("right", 1, "Right stick medium zone"),
                46 => RingZone("right", 2, "Right stick high zone"),
                47 => Dir("Gamepad RightStickY", true, "right", "Right stick Up"),
                48 => Dir("Gamepad RightStickY", false, "right", "Right stick Down"),
                49 => Dir("Gamepad RightStickX", true, "right", "Right stick Left"),
                50 => Dir("Gamepad RightStickX", false, "right", "Right stick Right"),
                51 => Trig("Gamepad LeftTrigger", TriggerPressPercent, "Left trigger"),
                52 => Trig("Gamepad LeftTrigger", lt.Low, "Left trigger low zone").Below(lt.Med),
                53 => Trig("Gamepad LeftTrigger", lt.Med, "Left trigger medium zone").Below(lt.High),
                54 => Trig("Gamepad LeftTrigger", lt.High, "Left trigger high zone"),
                55 => Trig("Gamepad RightTrigger", TriggerPressPercent, "Right trigger"),
                56 => Trig("Gamepad RightTrigger", rt.Low, "Right trigger low zone").Below(rt.Med),
                57 => Trig("Gamepad RightTrigger", rt.Med, "Right trigger medium zone").Below(rt.High),
                58 => Trig("Gamepad RightTrigger", rt.High, "Right trigger high zone"),
                68 => tilt ? Half("Gyro Lean Y", false, "Tilt Up") : Half("Gyro Pitch", false, "Gyro Up"),
                69 => tilt ? Half("Gyro Lean Y", true, "Tilt Down") : Half("Gyro Pitch", true, "Gyro Down"),
                70 => tilt ? Half("Gyro Lean X", true, "Tilt Left") : Look(Half("Gyro Roll", true, "Gyro Left")),
                71 => tilt ? Half("Gyro Lean X", false, "Tilt Right") : Look(Half("Gyro Roll", false, "Gyro Right")),
                72 when !tilt => Look(Half("Gyro Yaw", false, "Gyro Lean Left")),
                73 when !tilt => Look(Half("Gyro Yaw", true, "Gyro Lean Right")),
                91 => Btn("Touchpad 0 DPadUp", "Left touchpad Up"),
                92 => Btn("Touchpad 0 DPadDown", "Left touchpad Down"),
                93 => Btn("Touchpad 0 DPadLeft", "Left touchpad Left"),
                94 => Btn("Touchpad 0 DPadRight", "Left touchpad Right"),
                95 => TouchDiagonal(0, "Up", "Left", "Left touchpad Up-Left"),
                96 => TouchDiagonal(0, "Up", "Right", "Left touchpad Up-Right"),
                97 => TouchDiagonal(0, "Down", "Left", "Left touchpad Down-Left"),
                98 => TouchDiagonal(0, "Down", "Right", "Left touchpad Down-Right"),
                99 => Btn("Touchpad 0 Click", "Left touchpad click"),
                100 => Btn("Touchpad 1 DPadUp", "Right touchpad Up"),
                101 => Btn("Touchpad 1 DPadDown", "Right touchpad Down"),
                102 => Btn("Touchpad 1 DPadLeft", "Right touchpad Left"),
                103 => Btn("Touchpad 1 DPadRight", "Right touchpad Right"),
                104 => TouchDiagonal(1, "Up", "Left", "Right touchpad Up-Left"),
                105 => TouchDiagonal(1, "Up", "Right", "Right touchpad Up-Right"),
                106 => TouchDiagonal(1, "Down", "Left", "Right touchpad Down-Left"),
                107 => TouchDiagonal(1, "Down", "Right", "Right touchpad Down-Right"),
                108 => Btn("Touchpad 1 Click", "Right touchpad click"),
                >= 109 and <= 120 => ZoneDir(id - 109, "left"),
                >= 121 and <= 132 => ZoneDir(id - 121, "right"),
                163 => Btn("Touchpad 0 Tap", "Left touchpad tap"),
                175 => Diagonal(135, "left", "Left stick Up-Left"),
                176 => Diagonal(45, "left", "Left stick Up-Right"),
                177 => Diagonal(225, "left", "Left stick Down-Left"),
                178 => Diagonal(315, "left", "Left stick Down-Right"),
                179 => Diagonal(135, "right", "Right stick Up-Left"),
                180 => Diagonal(45, "right", "Right stick Up-Right"),
                181 => Diagonal(225, "right", "Right stick Down-Left"),
                182 => Diagonal(315, "right", "Right stick Down-Right"),
                _ => null,
            };
        }

        /// <summary>A mask's inputs. Null when one cannot be read, which names
        /// it in <paramref name="unknown"/>, because a mask missing a leg
        /// would fire too easily.</summary>
        private List<ImportInput> InputsOf(JsonElement set, int shift, out string unknown)
            => InputsOf(set, shift, out unknown, out _);

        /// <summary>The same, with the reason key for the input that could
        /// not be read.</summary>
        private List<ImportInput> InputsOf(JsonElement set, int shift, out string unknown, out string reason)
        {
            unknown = null;
            reason = ImportReasons.UnknownInput;
            var inputs = new List<ImportInput>();
            foreach (var item in set.EnumerateArray())
            {
                string description = Str(item, "description");
                int device = Int(item, "deviceId"), button = Int(item, "buttonId");
                var input = ResolveInput(device, button, description, shift);
                if (input == null)
                {
                    unknown = description ?? button.ToString(CultureInfo.InvariantCulture);
                    if (ClassOf(device, description) == "gamepad") reason = GamepadSkipReason(button, shift);
                    return null;
                }
                inputs.Add(input);
            }
            return inputs;
        }

        /// <summary>Why a gamepad input PadForge cannot read is left out.
        /// The ids are reWASD's GamepadButton names (shunt's enum dump).</summary>
        private string GamepadSkipReason(int id, int shift) => id switch
        {
            // BTN_THUMBSTICK_3_*: a third stick.
            >= 213 and <= 235 => ImportReasons.ThirdStick,
            // BTN_GYRO_TILT_LOW, MED, HIGH and their directions and leans.
            >= 74 and <= 76 or >= 145 and <= 162 => ImportReasons.GyroZone,
            72 or 73 when GyroTilt(shift) => ImportReasons.TiltLean,
            _ => ImportReasons.UnknownInput,
        };

        /// <summary>True when an input of a set carries the schema's
        /// "extended" field, which no public sample writes and no source
        /// explains, so the import names it.</summary>
        private static bool SetHasExtended(JsonElement set)
            => set.ValueKind == JsonValueKind.Array && set.EnumerateArray().Any(i => Has(i, "extended", out _));

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
                if (_shiftAs.ContainsKey(id)) continue;
                string type = Str(shift, "type") ?? "default";
                string name = Str(shift, "description");
                string mask = LayerOf(id);
                _b.AddLayer(mask, string.IsNullOrWhiteSpace(name)
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_LayerName_Format, id)
                    : name.Trim());

                // An unlock layer hands the keyboard, the mouse or both back to
                // Windows while it is on.
                if (type.StartsWith("unlock", StringComparison.Ordinal))
                {
                    bool keyboard = type is "unlock" or "unlockKeyboardOnly";
                    bool mouse = type is "unlock" or "unlockMouseOnly";
                    _b.Unlock(mask, keyboard, mouse);
                    if (keyboard)
                        _b.Clean(_b.LayerName(mask), type, Strings.Instance.Devices_AllKeyboardsMerged);
                    if (mouse)
                        _b.Clean(_b.LayerName(mask), type, Strings.Instance.Devices_AllMiceMerged);
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

        /// <summary>A layer can name its own input: held for "default",
        /// flipped for "toggle". Read after the device tuning, so a stick
        /// direction or zone engages at the file's own zone edge.</summary>
        private void ReadShiftActivators()
        {
            foreach (var shift in Arr(_root, "shifts"))
            {
                int id = Int(shift, "id");
                if (_shiftAs.ContainsKey(id)) continue;
                if (!Has(shift, "set", out var set) || set.ValueKind != JsonValueKind.Array) continue;
                string type = Str(shift, "type") ?? "default";
                string mask = LayerOf(id);
                var inputs = InputsOf(set, 0, out string unknown, out string reason);
                if (inputs == null)
                {
                    _b.Skipped(reason, _b.LayerName(mask), unknown, unknown);
                }
                else if (inputs.Count > 0)
                {
                    var act = ImportBuilder.ActivatorFor(inputs, type == "toggle" ? "Toggle" : "Hold", mask);
                    _b.AddActivator(act);
                    _engagedBy.Add((id, ControlOf(inputs)));
                    _b.Clean(_b.LayerName(mask), LabelOf(inputs), act.Mode);
                    if (SetHasExtended(set))
                        _b.Partial(ImportReasons.ControllerSetting, _b.LayerName(mask), "", "", "extended");
                }
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

            MappingSource Make(string descriptor, double coeff)
            {
                var src = new MappingSource { Descriptor = descriptor };
                double scale = Math.Abs(coeff);
                if (scale != 1.0) src.Sensitivity = scale;
                if (coeff < 0) src.Invert = true;
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
                // reWASD turns a stick counterclockwise for a positive
                // rotation. Its service's rotate instruction computes
                // x cos - y sin and x sin + y cos on a stick whose Y points up.
                // These rows' Y points down, so the same turn takes the
                // negative angle here.
                double r = -t.RotationDeg * Math.PI / 180.0;
                double cos = Math.Cos(r), sin = Math.Sin(r);
                void Leg(string control, string target, string descriptor, double coeff)
                {
                    if (Math.Abs(coeff) < 0.001) return;
                    _b.Bind(layer, control, target, Make(descriptor, coeff), _firstVirtualId);
                }
                Leg(cx, tx, gx, cos);
                Leg(cx, tx, gy, -sin);
                Leg(cy, ty, gx, sin);
                Leg(cy, ty, gy, cos);
                _b.SumRow(tx);
                _b.SumRow(ty);
            }
            else
            {
                var sx = Make(gx, 1.0);
                var sy = Make(gy, 1.0);
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

                int shift = ShiftIdOf(c);
                if (shift < 0) continue;
                string layer = LayerOf(shift);
                int device = Int(from, "deviceId");
                string description = Str(from, "description");
                string cls = ClassOf(device, description);
                bool unmap = Flag(to, "unmap");

                if (cls != "gamepad")
                {
                    if (unmap)
                    {
                        // Mouse movement has no key or button for the hook to
                        // hold back.
                        if (Has(from, "stick", out _))
                        {
                            _mouseMoves++;
                            continue;
                        }
                        if (cls == "keyboard") _keyboardUnmaps++;
                        else _mouseUnmaps++;
                        var blocked = Has(from, "buttonId", out _)
                            ? ResolveInput(device, Int(from, "buttonId"), description, shift)
                            : null;
                        if (blocked != null) _b.Block(blocked, "Block " + blocked.Label, layer);
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
                    _b.Bind(layer, input, output, replacesNative: true);
                    _b.Clean(PathOf(shift, input.Label), output.Label, output.Target);
                    if (cls == "keyboard") _keyboardRemaps++;
                    else _mouseRemaps++;
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
                    _b.Skipped(GamepadSkipReason(id, shift), PathOf(shift, description ?? id.ToString(CultureInfo.InvariantCulture)),
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

            if (_keyboardUnmaps + _keyboardRemaps > 0)
            {
                _b.AdviseConsume(ImportBuilder.AllKeyboardsGuid);
                _b.Partial(ImportReasons.ConsumeKeys, "Keyboard", HardwareCount(_keyboardUnmaps, _keyboardRemaps), "",
                    Strings.Instance.Devices_AllKeyboardsMerged);
            }
            if (_mouseUnmaps + _mouseRemaps > 0)
            {
                _b.AdviseConsume(ImportBuilder.AllMiceGuid);
                _b.Partial(ImportReasons.ConsumeKeys, "Mouse", HardwareCount(_mouseUnmaps, _mouseRemaps), "",
                    Strings.Instance.Devices_AllMiceMerged);
            }
            if (_mouseMoves > 0)
                _b.Partial(ImportReasons.MouseMovement, "Mouse",
                    string.Format(CultureInfo.InvariantCulture, "{0} x Unmap", _mouseMoves), "");
        }

        /// <summary>The binding column of the Consume Mapped Inputs line:
        /// "3 x Unmap", "2 x Remap" or both.</summary>
        private static string HardwareCount(int unmaps, int remaps)
        {
            var parts = new List<string>(2);
            if (unmaps > 0) parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} x Unmap", unmaps));
            if (remaps > 0) parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} x Remap", remaps));
            return string.Join(", ", parts);
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
                _mouseMoves++;
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
                {
                    // Gyro mode looks left and right by roll, reWASD's
                    // default, and Tilt mode reads the tilt angle (its help,
                    // Gyroscope).
                    bool tilt = GyroTilt(shift);
                    if (!tilt && axis == "x") _gyroAxesRead = true;
                    src = new MappingSource
                    {
                        Descriptor = tilt ? (axis == "x" ? "Gyro Lean X" : "Gyro Lean Y")
                                          : (axis == "x" ? "Gyro Roll" : "Gyro Pitch"),
                    };
                    break;
                }
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
            // reWASD's help (Gyroscope, Advanced settings, both modes): "By
            // default, you need to move the gamepad's 'nose' up to look up".
            // Gyro Pitch and Gyro Lean Y are positive with the nose up, and
            // the engine writes a stick's Y negated, so on a stick the row
            // would push down. Invert turns it up, and the file's own
            // "inverted" flips it back. The mouse lane already moves the
            // cursor up for the nose up.
            if (stick == "gyro" && axis == "y" && toStick is "left" or "right")
                src.Invert = !src.Invert;
            // The same help: "and to roll your gamepad to the right to look
            // right". PadForge's Gyro Roll is SDL's roll negated
            // (SourceCoercion.ReadTunedGyroRate), positive for a roll to the
            // right. A stick X row turns that left (the axis-frame flip in
            // SourceEvaluator.ShouldFlipForAxisFrame), and so does the mouse
            // lane (negated into screen space, KeyboardMouseVirtualController).
            // Invert turns it right. Tilt mode's Gyro Lean X already pushes
            // right for the right edge down.
            if (stick == "gyro" && axis == "x" && src.Descriptor == "Gyro Roll"
                && toStick is "left" or "right" or "mouse")
                src.Invert = !src.Invert;
            if (toStick == "mouse")
            {
                _b.Bind(layer, control, "KbmMouse" + toAxis, src, 0);
            }
            else if (toStick is "left" or "right")
            {
                _b.Bind(layer, control, (toStick == "left" ? "Left" : "Right") + "ThumbAxis" + toAxis, src, _firstVirtualId);
            }
            else if (toStick == "gyro")
            {
                // A PlayStation or Nintendo slot turns its virtual motion from
                // any stick through the Motion Yaw and Motion Pitch rows
                // (#475): pushed right turns right, pushed up raises the nose
                // (MotionRowsModel). An Xbox controller has no motion.
                if (_b.PadTypeOf(_firstVirtualId) is not (VirtualControllerType.PlayStation or VirtualControllerType.Nintendo))
                {
                    _b.Skipped(ImportReasons.MotionOutput, PathOf(shift, label), toStick);
                    return;
                }
                _b.Bind(layer, control, toAxis == "X" ? MappingSetMigrator.MotionYawTarget : MappingSetMigrator.MotionPitchTarget,
                    src, _firstVirtualId);
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
                // A Switch Pro has the Capture button a row can reach. The
                // other virtual types here have nothing at this id.
                12 when DeclaredVirtualType() == "nintendoswitchpro" => ImportOutput.Pad("ButtonShare"),
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

            /// <summary>What a command step is called, for the preview.</summary>
            public string Label = "";
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
                        o.Relative = Has(g, "absolute", out var abs) && abs.ValueKind == JsonValueKind.False;
                    }
                    // The schema's relative stick form carries a compensation
                    // switch no public source gives a meaning, so it is named.
                    if (Flag(g, "compensation"))
                    {
                        _b.Partial(ImportReasons.ControllerSetting, path, o.Label, o.Target, "compensation");
                        notes = true;
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
                    // A rumble step's "macro" switch has no published meaning.
                    if (Flag(r, "macro"))
                    {
                        _b.Partial(ImportReasons.ControllerSetting, path, "Rumble", "", "macro");
                        notes = true;
                    }
                }
                else if (Has(s, "command", out var cmd))
                {
                    _noted = false;
                    var action = CommandAction(Int(cmd, "id"), shift, path, inputs);
                    notes |= _noted;
                    if (action != null)
                        steps.Add(new Step { Kind = StepKind.Raw, Raw = action, Label = CommandName(Int(cmd, "id")) });
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

        private string CommandName(int id)
            => _commands.TryGetValue(id, out var cmd) ? Str(cmd, "description") ?? Str(cmd, "name") ?? "" : "";

        /// <summary>A reWASD command as a macro action. Program launches and
        /// power commands come across, and the preview names each before the
        /// user accepts the file. The numbers are the command types the public
        /// samples and reWASD's schema dump use.</summary>
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

            ActionData Chord(params byte[] vks) => new()
            {
                Type = MacroActionType.KeyPress,
                KeyString = string.Concat(vks.Select(v => "{" + (VirtualKey)v + "}")),
            };

            ActionData Shortcut(SwitchProfileMode mode) => new()
            {
                Type = MacroActionType.ProfileShortcut,
                ProfileShortcutMode = mode,
            };

            // RewasdUserCommand's numbers, the power commands as Windows' own.
            switch (type)
            {
                case 1:
                    return Shortcut(SwitchProfileMode.ToggleVCsDisabled);
                case 2:
                    return new ActionData { Type = MacroActionType.DisconnectController, DisconnectTarget = MacroDisconnectTarget.SlotDevices };
                case 3:
                    return Shortcut(SwitchProfileMode.ToggleWindow);
                case 7:
                    return _b.Run(name, "shutdown.exe", "/l");
                case 8:
                    return _b.Run(name, "shutdown.exe", "/s /t 0");
                case 9:
                    return _b.Run(name, "powershell.exe",
                        "-NoProfile -WindowStyle Hidden -Command \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Application]::SetSuspendState('Suspend', $false, $false)\"");
                case 13:
                    return _b.Run(name, "shutdown.exe", "/h");
                case 17:
                    return _b.Run(name, "shutdown.exe", "/r /t 0");
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
                case >= 101 and <= 110:
                case 120:
                    // PadForge runs eight Screen Overlays, and reWASD's ninth
                    // and tenth crosshairs have none of their own: folded
                    // onto the eighth, one button would hide another's
                    // picture.
                    if (type != 120 && type - 100 > PadForge.Common.Input.KeyboardScreens.KeyboardScreenInfo.MaxOverlays)
                    {
                        _b.Skipped(ImportReasons.NinthCrosshair, path, name);
                        _noted = true;
                        return null;
                    }
                    // The button works: it shows a Screen Overlay, and the
                    // picture is the user's to give it.
                    _b.Partial(ImportReasons.Crosshair, path, name, "ToggleScreenOverlay");
                    _noted = true;
                    return new ActionData
                    {
                        Type = MacroActionType.ToggleScreenOverlay,
                        ScreenOverlayNumber = type == 120 ? 0 : type - 100,
                    };
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
                {
                    Has(cmd, "params", out var ps);
                    string first = Names(ps).Select(n => Str(ps, n)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    string program = Str(ps, "path") ?? first ?? Str(cmd, "name") ?? name;
                    string args = Str(ps, "args") ?? Str(ps, "arguments") ?? "";
                    string folder = Str(ps, "workdir") ?? Str(ps, "working_directory") ?? "";
                    return _b.Run(program, program, args, folder);
                }
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
                int shift = ShiftIdOf(c);
                if (shift < 0) continue;
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

                var inputs = InputsOf(set, shift, out string unknown, out string reason);
                if (inputs == null)
                {
                    _b.Skipped(reason, PathOf(shift, unknown), "", unknown);
                    continue;
                }
                if (inputs.Count == 0) continue;

                ApplyZone(inputs, type, shift);
                string path = PathOf(shift, LabelOf(inputs));
                string description = Str(mapping, "description");
                if (SetHasExtended(set))
                    _b.Partial(ImportReasons.ControllerSetting, path, "", "", "extended");

                if (Has(mapping, "jumpToLayer", out var jump))
                    LowerJump(jump, shift, maskId, inputs, type, mode, activator, path, description);
                if (Has(mapping, "mouseRegion", out var region))
                    LowerRegion(region, layer, inputs, path, description);
                if (Has(mapping, "macros", out _))
                    LowerMacros(mapping, shift, maskId, inputs, type, mode, activator, path, description);
            }
        }

        /// <summary>A zone activator reads the input inside that zone. A file's
        /// zones are edges, and a zone's mapping is held while the input is
        /// inside it and released when it leaves, in the words of reWASD's
        /// administrator (forum threads 228291 and 232253).</summary>
        private void ApplyZone(List<ImportInput> inputs, string type, int shift)
        {
            if (type is not ("zoneLow" or "zoneMed" or "zoneHigh")) return;
            foreach (var input in inputs.Where(i => i.IsAnalog))
            {
                string side = input.Descriptor.Contains("Left", StringComparison.Ordinal) ? "left" : "right";
                var t = TuningOf(shift, input.Descriptor.EndsWith("Trigger", StringComparison.Ordinal) ? "trigger:" + side : side);
                int zone = type == "zoneLow" ? 0 : type == "zoneMed" ? 1 : 2;
                // A stick direction, or an elliptic ring zone, keeps its
                // sector, moved to this zone's edges.
                if (PadForge.Engine.Common.Mapping.BoundedReads.Parse(input.Band) is { Kind: PadForge.Engine.Common.Mapping.BoundedReadKind.Sector } sector)
                {
                    input.Band = ZoneSector(t, sector.Inner, sector.Center, sector.HalfWidth, zone, true);
                    continue;
                }
                input.DeadZone = zone == 0 ? t.Low : zone == 1 ? t.Med : t.High;
                input.Below(zone == 0 ? t.Med : zone == 1 ? t.High : 0);
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
                if (ShiftIdOf(c) != shift || Int(m, "id") != maskId) continue;
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
                : string.Join(", ", steps.Select(s => s.Kind == StepKind.Output ? s.Output.Label : s.Label).Where(l => !string.IsNullOrEmpty(l)));
            if (string.IsNullOrEmpty(label)) label = LabelOf(inputs);
            int padId = steps.Where(s => s.Kind == StepKind.Output && !s.Output.IsKbm).Select(s => s.Output.PadId).FirstOrDefault();
            if (padId == 0) padId = _firstVirtualId;
            bool zone = type.StartsWith("zone", StringComparison.Ordinal);

            var (trigger, holdMs, doubleMs, deferred) = TriggerFor(type, shift, maskId, activator);
            bool plain = steps.All(s => s.Kind == StepKind.Output && s.Action == null && !s.Pulse);
            bool expertMacro = Has(activator, "params", out var ps) && Flag(ps, "macro");

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
                var added = macroKeys.Where(o => o.Relative).Select(o => o.Add()).ToArray();
                if (added.Length > 0)
                {
                    var adding = Macro(MacroTriggerMode.OnPress, MacroRepeatMode.UntilRelease, added);
                    adding.RepeatDelayMs = 0;
                    _b.AddMacro(adding);
                }
                var held = macroKeys.Where(o => !o.Relative).Select(o => o.Down()).ToArray();
                if (held.Length > 0)
                    _b.AddHoldPair(Macro(MacroTriggerMode.OnPress, MacroRepeatMode.Once, held), ImportBuilder.ReleaseOf(held));
                _b.Clean(path, label, string.Join(", ", steps.Select(s => s.Output.Target)));
                return;
            }

            // A macro's button action writes the Xbox button word, which has no
            // touchpad click. A row reaches it, and this was not a row.
            foreach (var noBit in steps.Where(s => s.Kind == StepKind.Output
                         && s.Output.Kind == ImportOutputKind.PadButton && s.Output.PadBit == 0).ToList())
            {
                _b.Skipped(ImportReasons.MacroCannotPress, path, noBit.Output.Label, noBit.Output.Label);
                steps.Remove(noBit);
            }
            if (steps.Count == 0) return;

            var tapSteps = TapActions(steps);

            if (heldMode)
            {
                // A Single or Short press beside another gesture fires once it
                // is judged. If the button is still down it holds until the
                // release, and if it is already up its pass is one tap.
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
                        actions.Add(s.Output.Relative ? s.Output.Add(50) : s.Output.Tap());
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
                // A pulsed output repeats on its own while the macro runs. A
                // relative one adds to the stick while its action is current,
                // so the sequence has to come round again each frame.
                if (s.Output.Relative)
                {
                    down.Add(s.Output.Add());
                    press.RepeatDelayMs = 0;
                    continue;
                }
                var held = (s.Pulse ? s.Output.Pulse(100) : null) ?? s.Output.Down() ?? s.Output.Tap();
                down.Add(held);
            }
            // A deferred press can fire with the button already up: its pass
            // is then one tap, and this is how long that tap holds. Still
            // down, it holds until the release.
            if (press.Mode is MacroTriggerMode.SinglePress or MacroTriggerMode.ShortPress)
                down.Add(new ActionData { Type = MacroActionType.Delay, DurationMs = 50 });
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
            if (flips && type is "double" or "triple" or "long")
            {
                // reWASD's help: a toggle on these stops after one click. The
                // click's macro runs ahead of the gesture's, so a gesture that
                // turns it on is not cleared in the same frame.
                var on = steps.Select(s => s.Output.Down()).ToArray();
                _b.AddMacro(new ImportMacro
                {
                    Name = macro.Name,
                    Control = macro.Control,
                    Layer = macro.Layer,
                    Inputs = macro.Inputs,
                    Mode = MacroTriggerMode.OnPress,
                    PadId = macro.PadId,
                    Actions = ImportBuilder.ReleaseOf(on),
                });
                macro.Actions = on;
                macro.Repeat = MacroRepeatMode.Once;
                _b.AddMacro(macro);
                _b.Clean(path, label, "Toggle");
                return;
            }
            if (flips)
            {
                macro.Actions = steps.Select(s => s.Output.Flip()).ToArray();
                macro.Repeat = MacroRepeatMode.Once;
                _b.AddMacro(macro);
            }
            else
            {
                // reWASD's help again: a sequence toggled on single loops until
                // the next single press, and on a gesture it stops after one
                // click.
                bool gesture = type is "double" or "triple" or "long";
                if (type == "single") macro.Mode = MacroTriggerMode.Toggle;
                macro.Repeat = gesture ? MacroRepeatMode.UntilNextPress : MacroRepeatMode.UntilRelease;
                macro.Actions = tapSteps;
                _b.AddMacro(macro);
            }
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
            int x1 = Int(region, "x1"), x2 = Int(region, "x2"), y1 = Int(region, "y1"), y2 = Int(region, "y2");
            int screenWidth = GetSystemMetrics(0), screenHeight = GetSystemMetrics(1);
            int insetX = Math.Max(0, (screenWidth - Math.Max(0, x2 - x1)) / 2);
            int insetY = Math.Max(0, (screenHeight - Math.Max(0, y2 - y1)) / 2);
            int offsetX = (x1 + x2 - screenWidth) / 2;
            int offsetY = (y1 + y2 - screenHeight) / 2;
            ActionData Clamp(MacroLatchDirection d) => new()
            {
                Type = MacroActionType.MouseLimitRegion,
                CursorClampMode = CursorClampMode.XAndY,
                CursorClampInsetX = insetX,
                CursorClampInsetY = insetY,
                CursorClampOffsetX = offsetX,
                CursorClampOffsetY = offsetY,
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
            _b.Clean(path, label, "MouseLimitRegion");
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
                if (shift < 0) continue;
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
                if (flattened)
                    _b.Partial(ImportReasons.RadialFlattened, _b.LayerName(layer), menu.Name, "Menu " + menuId);
                else if (FileNamesRadialApply())
                    _b.Partial(ImportReasons.RadialMenu, _b.LayerName(layer), menu.Name, "Menu " + menuId);
                else
                    _b.Clean(_b.LayerName(layer), menu.Name, "Menu " + menuId);
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

        /// <summary>True when a mapping presses reWASD's Apply command. With
        /// none, a menu commits the way PadForge's does.</summary>
        private bool FileNamesRadialApply()
            => _commands.Values.Any(c => Int(c, "type") == 132)
               && Arr(_root, "mappings").Any(m => Arr(m, "macros").Any(s => Has(s, "command", out var cmd)
                   && _commands.TryGetValue(Int(cmd, "id"), out var c) && Int(c, "type") == 132));

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
                        shift = ShiftIdOf(c);
                    }
                }
            }
            if (firstOrder != int.MaxValue) return shift;
            foreach (var kv in _shiftType)
                if (kv.Value == "radialMenu") return kv.Key;
            // Only an unused radial layer is left, and it is not imported.
            return _shiftAs.ContainsValue(-1) ? -1 : 0;
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
                    // A mouse's own speed and scroll, a keyboard's analog keys,
                    // which the schema keeps as actuationButtons.
                    var keyScopes = Arr(h, "shifts").ToList();
                    if (Has(h, "default", out var own)) keyScopes.Insert(0, own);
                    foreach (var keyScope in keyScopes)
                        foreach (var key in Arr(keyScope, "actuationButtons").Concat(Arr(keyScope, "analogButtons")))
                            _b.Skipped(ImportReasons.AnalogActuation, KeyLabel(key), "");
                    if (Has(h, "mouse", out _) || (Has(h, "default", out var scope) && (Has(scope, "stick", out _) || Has(scope, "scrolls", out _))))
                        _b.Partial(ImportReasons.ControllerSetting, "Mouse", "", "", "mouse");
                    continue;
                }
                if (Has(h, "default", out var def)) ReadTuningScope(def, 0);
                foreach (var scope in Arr(h, "shifts"))
                {
                    int shift = ShiftIdOf(scope);
                    if (shift >= 0) ReadTuningScope(scope, shift);
                }
            }
        }

        private void ReadTuningScope(JsonElement scope, int shift)
        {
            string Scoped(string label) => PathOf(shift, label);
            foreach (var st in Arr(scope, "sticks"))
            {
                string name = Str(st, "name") ?? "";
                if (name == "gyro")
                {
                    // The gyro's own entry. "tilt" puts it in Tilt mode, which
                    // reads the tilt angle in place of the rotation.
                    _gyroTilt[shift] = Flag(st, "tilt");
                    NoteLeftovers(st, Scoped("Gyro"), "name", "tilt");
                    continue;
                }
                if (name is not ("left" or "right"))
                {
                    if (Names(st).Any(n => n != "name"))
                        _b.Partial(ImportReasons.ControllerSetting, Scoped(name), "", "", name);
                    continue;
                }
                if (Has(st, "mouse", out _))
                    _b.Partial(ImportReasons.MoveSpeed, Scoped(StickLabel(name)), "", "");
                NoteLeftovers(st, Scoped(StickLabel(name)), "name", "sensitivity", "rotation", "zones", "curves", "mouse", "diagonalZones");
                var t = new StickTuning();
                // "sensitivity" is reWASD's Horizontal and Vertical axis range,
                // which sizes the direction zones keys mapped to the stick read
                // (GamepadInput's Directional). It never reaches the game's
                // stick (reWASD's administrator on its forum, thread 222207).
                if (Has(st, "sensitivity", out var range))
                {
                    t.RangeX = AxisRange(range, "x");
                    t.RangeY = AxisRange(range, "y");
                }
                t.EightWay = Flag(st, "diagonalZones");
                // reWASD's loader (ConfigJSON_3_0) takes -90 to 90 degrees and
                // leaves the stick straight for anything else.
                int rotation = Int(st, "rotation");
                t.RotationDeg = rotation is >= -90 and <= 90 ? rotation : 0;
                foreach (var z in Arr(st, "zones"))
                {
                    // Where zone mappings fire. reWASD's help: zones change
                    // nothing the game reads, and its dead zone reaches the game
                    // only with "Affect gamepad behavior for games", which a
                    // stick in this schema cannot set. A zone is an ellipse, x
                    // across and y up and down, which reWASD's saver writes only
                    // when it differs from x. Its loader (ConfigJSON_3_0) keeps
                    // the default across when x is missing and takes x for y
                    // when y is missing.
                    int zone = Str(z, "name") switch { "low" => 0, "med" => 1, "high" => 2, _ => -1 };
                    if (zone < 0) continue;
                    var (zx, _) = t.Edge(zone);
                    if (Has(z, "x", out _)) zx = ZonePercent(Int(z, "x"));
                    int zy = Has(z, "y", out _) ? ZonePercent(Int(z, "y")) : zx;
                    int height = zy == zx ? 0 : zy;
                    switch (zone)
                    {
                        case 0: t.Low = zx; t.LowY = height; break;
                        case 1: t.Med = zx; t.MedY = height; break;
                        default: t.High = zx; t.HighY = height; break;
                    }
                }
                if (Has(st, "curves", out var curves) && Has(curves, "points", out var points))
                {
                    string prefix = name == "left" ? "Left" : "Right";
                    string across = CurvePoints(points, "horizontal"), up = CurvePoints(points, "vertical") ?? across;
                    across ??= up;
                    if (across != null && shift == 0)
                    {
                        // The file's own points, on the device's curve cards.
                        _b.Curves[prefix + "ThumbAxisX"] = across;
                        _b.Curves[prefix + "ThumbAxisY"] = up;
                        _b.Clean(Scoped(StickLabel(name)), "Curve", "");
                    }
                    else if (across != null)
                    {
                        // A device has one curve card, and this one belongs to
                        // a layer, so it rides the layer's rows as a power curve.
                        t.CurveExponent = FitExponent(points, out _);
                        _b.Partial(ImportReasons.ControllerSetting, Scoped(StickLabel(name)), "Curve", "", "curves");
                    }
                }
                _tuning[(shift, name)] = t;
                // BindStick turns the game's stick as reWASD's rotate
                // instruction does, and the zones place zone mappings.
                if (t.RotationDeg != 0 || Arr(st, "zones").Any())
                    _b.Clean(Scoped(StickLabel(name)), "Tuning", "");
            }

            foreach (var tr in Arr(scope, "triggers"))
            {
                string name = Str(tr, "name") ?? "";
                if (name is not ("left" or "right")) continue;
                NoteLeftovers(tr, Scoped(name == "left" ? "Left trigger" : "Right trigger"), "name", "zones", "affectHardware");
                // reWASD's help: "Affect gamepad behavior for games" puts the
                // dead zone on the trigger the game reads, and changes nothing
                // else. Without it the zones only place zone mappings.
                bool affectsGame = Flag(tr, "affectHardware");
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
                            if (affectsGame)
                            {
                                src.ParamStickDeadZoneShape = 1;
                                src.ParamStickDeadZoneInner = Math.Clamp(value / 32767.0, 0.0, 0.99);
                            }
                            break;
                        case "med":
                            zones.Med = percent;
                            break;
                        case "high":
                            zones.High = percent;
                            break;
                    }
                }
                _triggerTuning[(shift, name)] = src;
                _tuning[(shift, "trigger:" + name)] = zones;
                _b.Clean(Scoped(name == "left" ? "Left trigger" : "Right trigger"), "Tuning", "");
            }

            foreach (var key in Arr(scope, "analogButtons"))
                _b.Skipped(ImportReasons.AnalogActuation, Scoped(KeyLabel(key)), "");

            // A touchpad's mode and glide are what nearly every file writes
            // without anyone having set them.
            foreach (var pad in Arr(scope, "touchpads"))
                NoteLeftovers(pad, Scoped("Touchpad"), "id", "mode", "trackballFriction");
            if (Arr(scope, "vibration").Any())
                _b.Partial(ImportReasons.ControllerSetting, Scoped("Vibration"), "", "", "vibration");
        }

        /// <summary>A stick's axis range on one axis, as reWASD's loader
        /// (ConfigJSON_3_0) takes it: 1 to 32768, else the default. reWASD
        /// 9.1 and 9.3 wrote 1 on sticks nobody touched (the samples Miner
        /// Warfare, 9.1.2, and TATE - Rotate -90 Landscape 180, 9.3.0), which
        /// reWASD now reads as zones each the width of a half.</summary>
        private static int AxisRange(JsonElement range, string axis)
        {
            int value = Int(range, axis, DefaultAxisRange);
            return value is >= 1 and <= 32768 ? value : DefaultAxisRange;
        }

        /// <summary>A zone value, 0 to 32767, as the percent the reads take.</summary>
        private static int ZonePercent(int value) => (int)Math.Clamp(Math.Round(value * 100.0 / 32767.0), 1, 99);

        /// <summary>A stick's sector past a zone's edge, an ellipse when the
        /// zone has its own height, and short of the next zone's edge when
        /// banded and that edge lies inside full travel.</summary>
        private static string ZoneSector(StickTuning t, string ring, double center, double halfWidth, int zone, bool banded)
        {
            var (ex, ey) = t.Edge(zone);
            var (nx, ny) = banded && zone < 2 ? t.Edge(zone + 1) : (0, 0);
            if (nx <= ex || nx >= 100) nx = ny = 0;
            return PadForge.Engine.Common.Mapping.BoundedReads.Sector(center, halfWidth, ex, ey, nx, ny, ring);
        }

        /// <summary>How far a direction reaches from its axis, in degrees: the
        /// angle whose tangent is 32768 over the axis range, 67.5 at the
        /// default.</summary>
        private static double Reach(int range) => Math.Atan(32768.0 / range) * 180.0 / Math.PI;

        /// <summary>How far Up and Down, or Left and Right, reach. With "Use 8
        /// digital directions" a corner belongs to its diagonal, so a
        /// direction stops where the other axis's test begins.</summary>
        private static double CardinalReach(StickTuning t, bool vertical)
        {
            double own = Reach(vertical ? t.RangeY : t.RangeX);
            return t.EightWay ? Math.Min(own, 90.0 - Reach(vertical ? t.RangeX : t.RangeY)) : own;
        }

        private static string StickLabel(string name) => name == "left" ? "Left stick" : "Right stick";

        /// <summary>Whether the gyro is in Tilt mode on a layer.</summary>
        private bool GyroTilt(int shift)
            => _gyroTilt.TryGetValue(shift, out bool tilt) ? tilt : shift != 0 && _gyroTilt.TryGetValue(0, out tilt) && tilt;

        /// <summary>An analog key's name: the file's description, else its
        /// id. The schema has no name field.</summary>
        private static string KeyLabel(JsonElement key)
            => Str(key, "description") ?? Int(key, "buttonId").ToString(CultureInfo.InvariantCulture);

        /// <summary>A virtual stick's own settings, per layer.</summary>
        private void ReadVirtualSticks()
        {
            foreach (var (pad, id) in _virtualPads)
            {
                if (Has(pad, "default", out var def)) ReadVirtualScope(def, 0, id);
                foreach (var scope in Arr(pad, "shifts"))
                {
                    int shift = ShiftIdOf(scope);
                    if (shift >= 0) ReadVirtualScope(scope, shift, id);
                }
            }
        }

        /// <summary>Initial and maximum deflection are the range a virtual
        /// stick's output moves in, in raw axis units of 32767: the public
        /// samples write 6038 to 32767, and a layer that tops the left stick
        /// at 17918 is a walk. The Y fields follow X unless the file sets
        /// them apart, reWASD's "same settings for X and Y". A layer's entry
        /// stands whole, the fields it leaves out at reWASD's defaults.</summary>
        private void ReadVirtualScope(JsonElement scope, int shift, int padId)
        {
            string layer = LayerOf(shift);
            if (!_b.HasLayer(layer)) return;
            foreach (var st in Arr(scope, "sticks"))
            {
                string stick = Str(st, "name") ?? "";
                bool shaped = stick is "left" or "right";
                string path = PathOf(shift, shaped ? StickLabel(stick) : stick);
                var props = Names(st).ToList();
                bool Shakes(string n) => n.StartsWith("jitter", StringComparison.Ordinal)
                                         || n.StartsWith("shaking", StringComparison.Ordinal);
                bool Range(string n) => shaped && n is "initialDeflection" or "initialDeflectionY" or "maxDeflection" or "maxDeflectionY";
                if (props.Any(Shakes))
                    _b.Skipped(ImportReasons.JitterShaking, path, "");
                if (props.Any(Range))
                {
                    int ix = Int(st, "initialDeflection"), iy = Int(st, "initialDeflectionY", ix);
                    int mx = Int(st, "maxDeflection", 32767), my = Int(st, "maxDeflectionY", mx);
                    string prefix = stick == "left" ? "Left" : "Right";
                    Shape(prefix + "ThumbAxisX", ix, mx);
                    Shape(prefix + "ThumbAxisY", iy, my);
                    _b.Clean(path, "Deflection range", "AxisScale");
                }
                var left = props.Where(n => n != "name" && !Shakes(n) && !Range(n)).ToList();
                if (left.Count > 0)
                    _b.Partial(ImportReasons.ControllerSetting, path, "", "", string.Join(", ", left));
            }

            void Shape(string target, int initial, int max)
            {
                max = Math.Clamp(max, 1, 32767);
                initial = Math.Clamp(initial, 0, max);
                // The source's anti-deadzone floors the shaped value, and the
                // Scale Axis multiplies it, so the floor is initial / max for
                // the output to start at initial and top out at max.
                _b.StickShape(layer, target, Math.Min(0.99, initial / (double)max), max / 32767.0, padId);
            }
        }

        /// <summary>One line naming whatever an element holds past what the
        /// importer reads, so nothing a file set goes unmentioned.</summary>
        private void NoteLeftovers(JsonElement e, string path, params string[] read)
        {
            var left = Names(e).Where(n => !read.Contains(n)).ToList();
            if (left.Count > 0)
                _b.Partial(ImportReasons.ControllerSetting, path, "", "", string.Join(", ", left));
        }

        /// <summary>A curve point's travel or value as a fraction of the
        /// stick's full scale. Both sit on the 0-32767 scale the same files'
        /// zones use: the public samples end their curves at travels of
        /// 10125, 19970, 23626 and 24697, so the last point is not full
        /// travel.</summary>
        private static double CurveUnit(double raw) => Math.Min(1.0, raw / 32767.0);

        /// <summary>A file's curve points in the card's format, 0 to 1 on both
        /// sides, from the center out. A curve that ends short of full travel
        /// holds its last value from there to the end. Null when it names too
        /// few.</summary>
        private static string CurvePoints(JsonElement points, string side)
        {
            if (!Has(points, side, out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var pts = list.EnumerateArray()
                .Select(p => (Travel: (double)Int(p, "travel"), Value: (double)Int(p, "value")))
                .Where(p => p.Travel >= 0 && p.Value >= 0)
                .OrderBy(p => p.Travel)
                .ToList();
            if (pts.Count < 2 || pts[^1].Travel <= 0 || pts[^1].Value <= 0) return null;
            static string Text(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            var parts = new List<string>(pts.Count + 2);
            if (pts[0].Travel > 0) parts.Add("0,0");
            foreach (var (travel, value) in pts)
                parts.Add(Text(CurveUnit(travel)) + "," + Text(CurveUnit(value)));
            if (CurveUnit(pts[^1].Travel) < 1.0)
                parts.Add("1," + Text(CurveUnit(pts[^1].Value)));
            return string.Join(";", parts);
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
            double num = 0, den = 0;
            foreach (var (travel, value) in pts)
            {
                double x = CurveUnit(travel);
                double y = CurveUnit(value);
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
            foreach (var s in Arr(led, "shifts"))
            {
                int shift = ShiftIdOf(s);
                if (shift >= 0) LowerLed(s, shift);
            }
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

            var (r, g, b) = color == null ? ((byte)255, (byte)255, (byte)255) : (color.LightbarR, color.LightbarG, color.LightbarB);
            var pattern = new List<ActionData>();
            switch (mode)
            {
                case "rainbow":
                    pattern = ImportBuilder.RainbowPattern(6000);
                    break;
                case "breath":
                    // Up through three steps, then a long fade back down.
                    pattern.AddRange(ImportBuilder.LightStep((byte)(r / 4), (byte)(g / 4), (byte)(b / 4), 700, 0, 500));
                    pattern.AddRange(ImportBuilder.LightStep((byte)(r / 2), (byte)(g / 2), (byte)(b / 2), 700, 0, 500));
                    pattern.AddRange(ImportBuilder.LightStep(r, g, b, 200, 1500, 1700));
                    break;
                case "heartbeat":
                    pattern.AddRange(ImportBuilder.LightStep(r, g, b, 60, 140, 200));
                    pattern.AddRange(ImportBuilder.LightStep(r, g, b, 60, 240, 900));
                    break;
                case "stroboscope":
                    pattern.AddRange(ImportBuilder.LightStep(r, g, b, 150, 0, 100));
                    pattern.AddRange(ImportBuilder.LightStep(0, 0, 0, 150, 0, 100));
                    break;
                case "shimmer":
                    pattern.AddRange(ImportBuilder.LightStep(r, g, b, 250, 0, 150));
                    pattern.AddRange(ImportBuilder.LightStep((byte)(r * 3 / 4), (byte)(g * 3 / 4), (byte)(b * 3 / 4), 250, 0, 150));
                    break;
                default:
                    if (color != null) pattern.Add(color);
                    break;
            }
            if (pattern.Count == 0) return;

            _b.AddMacro(new ImportMacro
            {
                Name = "Lightbar",
                Control = "lightbar",
                RunsBesideLayers = pattern.Count == 1,
                Layer = layer,
                Mode = MacroTriggerMode.Always,
                PadId = _firstVirtualId,
                Actions = pattern,
            });
            _b.Clean(path, mode, "LightbarColor");
        }

        // ── What the file has and a profile does not ────────────────────

        private void ReportLeftovers()
        {
            // reWASD's Preferences pick which rotation steers, and a file does
            // not record the choice (reWASD's help, Gyroscope).
            if (_gyroAxesRead)
                _b.Partial(ImportReasons.GyroAxes, "Gyro", "", "");
            if (_touchDiagonalRead)
                _b.Partial(ImportReasons.TouchpadDiagonal, "Touchpad", "", "");
            if (Arr(_root, "crosshairs").Any())
                _b.Skipped(ImportReasons.Crosshair, "Crosshairs", "");

            // Every public sample writes 1, and no public source says how
            // another value changes a file's times.
            if (Has(_root, "config", out var cfg) && Has(cfg, "minTimeUnit", out var unit) && TryNumber(unit, out int ms) && ms != 1)
                _b.Partial(ImportReasons.ControllerSetting, "Macros", "", "", "minTimeUnit");
        }
    }
}
