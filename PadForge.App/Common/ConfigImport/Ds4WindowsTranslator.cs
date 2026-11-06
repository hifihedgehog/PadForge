using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Common.ConfigImport
{
    /// <summary>
    /// Turns a DS4Windows profile (XML, root <c>DS4Windows</c>) into a profile.
    /// The element names, the macro number codes and the shift trigger indices
    /// are the ones in Schmaldeo's fork at <c>ProfileDTO.cs</c>,
    /// <c>Mapping.cs</c> and <c>ScpUtil.cs</c>. Forks keep the root and add
    /// elements, so an element this does not read is ignored, which is how a
    /// fork's profile still imports.
    ///
    /// <para>A profile lists only what differs from the default, where every
    /// control sends its own output (<c>defaultButtonMapping</c>). So the rows
    /// start as that default and each listed control replaces its own.</para>
    /// </summary>
    internal sealed class Ds4WindowsTranslator
    {
        private const int PadId = 1;

        private readonly XElement _root;
        private readonly ImportBuilder _b;
        private bool _ds4Output;
        private bool _noVirtualPad;
        private string _gyroMode = "Controls";
        private string _touchMode = "Mouse";
        private bool _flickLeft;
        private bool _flickRight;

        private readonly Dictionary<(string Layer, string Axis), (bool Lower, bool Upper)> _axisHalves = new();
        private readonly Dictionary<string, MappingSource> _axisSources = new(StringComparer.Ordinal);

        private Ds4WindowsTranslator(XElement root)
        {
            _root = root;
            _b = new ImportBuilder();
        }

        public static ForeignImportResult Translate(XDocument doc, string path)
        {
            var t = new Ds4WindowsTranslator(doc.Root);
            t.ReadOutput();
            t.BuildDefaultRows();
            t.ReadControlBlock(t.Element("Control"), isShift: false);
            t.ReadControlBlock(t.Element("ShiftControl"), isShift: true);
            t.ReadTwoStage("L2");
            t.ReadTwoStage("R2");
            t.ReadGyro();
            t.ReadTouchpad();
            t.ReadLightbar();
            t.ReadSafetyAndLeftovers();
            t.ReadSpecialActions(path);
            if (t._noVirtualPad) t._b.DropPads();

            string name = Path.GetFileNameWithoutExtension(path ?? "");
            t._b.Report.ConfigTitle = name;
            t._b.Report.SchemaVersion = t.IntAttr(doc.Root, "config_version");
            var profile = t._b.Finish(name, ExecutablesFor(name, path));
            return new ForeignImportResult
            {
                Kind = ForeignConfigKind.Ds4Windows,
                Profile = profile,
                Report = t._b.Report,
                ReadsKeyboard = t._b.ReadsKeyboard,
                ReadsMouse = t._b.ReadsMouse,
                ReadsController = t._b.ReadsController,
            };
        }

        // ── Reading XML ─────────────────────────────────────────────────

        private XElement Element(string name) => _root.Element(name);

        private string Text(string name) => _root.Element(name)?.Value?.Trim() ?? "";

        private static string Text(XElement parent, string name) => parent?.Element(name)?.Value?.Trim() ?? "";

        private int IntAttr(XElement e, string name)
            => int.TryParse(e?.Attribute(name)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

        private double Num(string name, double fallback) => Num(_root, name, fallback);

        private static double Num(XElement parent, string name, double fallback)
            => double.TryParse(Text(parent, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

        private bool IsTrue(string name) => bool.TryParse(Text(name), out bool v) && v;

        private static bool IsTrue(XElement parent, string name, bool fallback)
            => bool.TryParse(Text(parent, name), out bool v) ? v : fallback;

        // ── The output device and the default rows ──────────────────────

        private void ReadOutput()
        {
            _ds4Output = string.Equals(Text("OutputContDevice"), "DS4", StringComparison.OrdinalIgnoreCase);
            _noVirtualPad = IsTrue("DinputOnly");

            // ProfileDTO's defaults, with the two older elements that outrank
            // the mode when a profile has them.
            string usesSaForMouse = Text("UseSAforMouse");
            string gyro = Text("GyroOutputMode");
            _gyroMode = usesSaForMouse.Length > 0
                ? (IsTrue("UseSAforMouse") ? "Mouse" : "Controls")
                : gyro.Length > 0 ? gyro : "Controls";
            string usesTpForControls = Text("UseTPforControls");
            string touch = Text("TouchpadOutputMode");
            _touchMode = usesTpForControls.Length > 0
                ? (IsTrue("UseTPforControls") ? "Controls" : "Mouse")
                : touch.Length > 0 ? touch : "Mouse";

            if (_ds4Output)
                _b.DeclarePad(PadId, VirtualControllerType.PlayStation, "dualshock-4-v2");
            else
                _b.DeclarePad(PadId, VirtualControllerType.Xbox, "xbox-360-wired");
        }

        /// <summary>DS4Windows' <c>defaultButtonMapping</c>: each control
        /// sends its own output, the two sticks and triggers analog.</summary>
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
            ("Share", "Gamepad ButtonBack", "ButtonBack"),
            ("Options", "Gamepad ButtonStart", "ButtonStart"),
            ("PS", "Gamepad ButtonGuide", "ButtonGuide"),
            ("DpadUp", "Gamepad DPadUp", "DPadUp"),
            ("DpadDown", "Gamepad DPadDown", "DPadDown"),
            ("DpadLeft", "Gamepad DPadLeft", "DPadLeft"),
            ("DpadRight", "Gamepad DPadRight", "DPadRight"),
        };

        private void BuildDefaultRows()
        {
            foreach (var (control, descriptor, target) in DefaultButtons)
                _b.Bind(ImportBuilder.BaseLayer, "ds:" + control, target, new MappingSource { Descriptor = descriptor }, PadId);

            _b.Bind(ImportBuilder.BaseLayer, "ds:L2", "LeftTrigger", TriggerSource("L2", 2), PadId);
            _b.Bind(ImportBuilder.BaseLayer, "ds:R2", "RightTrigger", TriggerSource("R2", 3), PadId);

            _flickLeft = string.Equals(Text("LSOutputMode"), "FlickStick", StringComparison.OrdinalIgnoreCase);
            _flickRight = string.Equals(Text("RSOutputMode"), "FlickStick", StringComparison.OrdinalIgnoreCase);
            BindStick("LS", "Left", _flickLeft);
            BindStick("RS", "Right", _flickRight);
            if (_noVirtualPad)
                _b.Clean("DinputOnly", "True", "");
            else
                _b.Clean(Strings.Instance.Import_Line_Controller, Strings.Instance.Import_Line_Native, "");
        }

        /// <summary>ProfileDTO writes the sensitivities LS, RS, L2, R2, SX, SZ
        /// and clamps what it reads to 0.1 through 5.</summary>
        private double SensitivityPart(int index)
        {
            var parts = Text("Sensitivity").Split('|');
            return index < parts.Length
                   && double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? Math.Clamp(v, 0.1, 5.0)
                : 1.0;
        }

        /// <summary>DS4Windows' curve names as the exponent of the same shape.
        /// Quadratic and cubic are exact. The easing and precision curves, and
        /// a custom curve's bezier sampled at three points, come to the
        /// nearest exponent, which the report says.</summary>
        private double CurveExponent(string prefix, string label)
        {
            string mode = Text(prefix + "OutputCurveMode");
            switch (mode.ToLowerInvariant())
            {
                case "":
                case "linear":
                    return 0;
                case "quadratic":
                    return 2.0;
                case "cubic":
                    return 3.0;
                case "enhanced-precision":
                    _b.Partial(ImportReasons.CurveApproximated, label, mode, "", mode);
                    return 1.2;
                case "easeout-quad":
                    _b.Partial(ImportReasons.CurveApproximated, label, mode, "", mode);
                    return 0.6;
                case "easeout-cubic":
                    _b.Partial(ImportReasons.CurveApproximated, label, mode, "", mode);
                    return 0.45;
                case "custom":
                    double e = FitBezier(Text(prefix + "OutputCurveCustom"));
                    _b.Partial(ImportReasons.CurveApproximated, label, mode, "", mode);
                    return e;
                default:
                    _b.Partial(ImportReasons.CurveApproximated, label, mode, "", mode);
                    return 0;
            }
        }

        /// <summary>The exponent nearest a cubic bezier from (0,0) to (1,1),
        /// written <c>x1, y1, x2, y2</c>.</summary>
        internal static double FitBezier(string definition)
        {
            var parts = (definition ?? "").Split(',');
            if (parts.Length < 4) return 0;
            var values = new double[4];
            for (int i = 0; i < 4; i++)
            {
                string token = parts[i].Trim().TrimEnd('%');
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return 0;
            }
            double num = 0, den = 0;
            foreach (double t in new[] { 0.25, 0.5, 0.75 })
            {
                double u = 1 - t;
                double x = 3 * u * u * t * values[0] + 3 * u * t * t * values[2] + t * t * t;
                double y = 3 * u * u * t * values[1] + 3 * u * t * t * values[3] + t * t * t;
                if (x <= 0 || y <= 0 || x >= 1 || y >= 1) continue;
                double lx = Math.Log(x), ly = Math.Log(y);
                num += lx * ly;
                den += lx * lx;
            }
            return den > 0 ? Math.Clamp(num / den, 0.2, 8.0) : 0;
        }

        private MappingSource TriggerSource(string prefix, int sensIndex)
        {
            string label = prefix + " tuning";
            var src = new MappingSource { Descriptor = prefix == "L2" ? "Gamepad LeftTrigger" : "Gamepad RightTrigger" };
            // A trigger's dead zone has no prefix of its own. ProfileDTO calls
            // it LeftTriggerMiddle and RightTriggerMiddle, 0 through 255.
            double dead = Num(prefix == "L2" ? "LeftTriggerMiddle" : "RightTriggerMiddle", 0);
            if (dead > 0)
            {
                // A trigger's dead zone only folds onto its card with a shape
                // stamped (StarterProfileCatalog.BrakeTrigger).
                src.ParamStickDeadZoneShape = 1;
                src.ParamStickDeadZoneInner = Math.Clamp(dead / 255.0, 0.0, 0.99);
            }
            double anti = Num(prefix + "AntiDeadZone", 0);
            if (anti > 0) src.ParamAntiDeadzone = Math.Clamp(anti / 100.0, 0.0, 0.99);
            double max = Num(prefix + "MaxZone", 100);
            if (max > 0 && max < 100) src.ParamRangeOuter = max / 100.0;
            double sens = SensitivityPart(sensIndex);
            if (sens != 1.0) src.Sensitivity = sens;
            double exponent = CurveExponent(prefix, label);
            if (exponent > 0) src.ParamCurveExponent = exponent;
            double output = Num(prefix + "MaxOutput", 100);
            if (output is > 0 and < 100)
            {
                src.Sensitivity *= output / 100.0;
                _b.Partial(ImportReasons.MaxOutput, label, prefix + "MaxOutput", "", output.ToString("0.#", CultureInfo.InvariantCulture));
            }
            return src;
        }

        /// <summary>A stick's two rows. Its dead zone, anti dead zone, range,
        /// curve and rotation ride the sources and fold into the device's
        /// cards at assignment, as a Workshop import's do.</summary>
        private void BindStick(string prefix, string side, bool flick)
        {
            string gx = "Gamepad " + side + "StickX";
            string gy = "Gamepad " + side + "StickY";
            string tx = side + "ThumbAxisX";
            string ty = side + "ThumbAxisY";
            string label = prefix + " tuning";
            string layer = ImportBuilder.BaseLayer;

            if (flick)
            {
                var settings = Element(prefix + "OutputSettings")?.Element("FlickStickSettings");
                var src = new MappingSource { Descriptor = "Flick Stick " + side };
                // ProcessFlickStick multiplies the flick's degrees by the
                // calibration, so a full turn is 360 of them.
                double calibration = Num(settings, "RealWorldCalibration", 5.3);
                if (calibration > 0) src.ParamFlickCountsPer360 = 360.0 * calibration;
                double threshold = Num(settings, "FlickThreshold", 0);
                if (threshold > 0) src.ParamFlickThreshold = threshold;
                double time = Num(settings, "FlickTime", 0);
                if (time > 0) src.ParamFlickTime = time;
                _b.Bind(layer, "ds:" + prefix + ":flick", "KbmMouseX", src, 0);
                _b.Clean(label, "Flick Stick", "KbmMouseX");
                return;
            }

            bool axial = string.Equals(Text(prefix + "DeadZoneType"), "Axial", StringComparison.OrdinalIgnoreCase);
            var opts = Element(prefix + "AxialDeadOptions");
            double exponent = CurveExponent(prefix, label);
            double rotation = Num(prefix + "Rotation", 0);
            double output = Num(prefix + "MaxOutput", 100);
            double sens = SensitivityPart(prefix == "LS" ? 0 : 1) * (output is > 0 and < 100 ? output / 100.0 : 1.0);
            double verticalScale = Num(prefix + "VerticalScale", 100) / 100.0;

            MappingSource Make(string descriptor, double coeff, bool isX)
            {
                var src = new MappingSource { Descriptor = descriptor };
                double dead = axial
                    ? Num(opts, isX ? "DeadZoneX" : "DeadZoneY", 0)
                    : Num(prefix + "DeadZone", 0);
                double anti = axial
                    ? Num(opts, isX ? "AntiDeadZoneX" : "AntiDeadZoneY", 0)
                    : Num(prefix + "AntiDeadZone", 0);
                double max = axial
                    ? Num(opts, isX ? "MaxZoneX" : "MaxZoneY", 100)
                    : Num(prefix + "MaxZone", 100);
                if (dead > 0)
                {
                    src.ParamStickDeadZoneShape = axial ? 1 : 2;
                    src.ParamStickDeadZoneInner = Math.Clamp(dead / 127.0, 0.0, 0.99);
                }
                if (anti > 0) src.ParamAntiDeadzone = Math.Clamp(anti / 100.0, 0.0, 0.99);
                if (max is > 0 and < 100) src.ParamRangeOuter = max / 100.0;
                if (exponent > 0) src.ParamCurveExponent = exponent;
                double scale = Math.Abs(coeff) * sens * (isX ? 1.0 : verticalScale);
                if (scale != 1.0) src.Sensitivity = scale;
                if (coeff < 0) src.Invert = true;
                return src;
            }

            string cx = "ds:" + prefix + "X";
            string cy = "ds:" + prefix + "Y";
            if (rotation != 0)
            {
                // rotateLSCoordinates: x' = x cos - y sin, y' = x sin + y cos.
                double r = rotation * Math.PI / 180.0;
                double cos = Math.Cos(r), sin = Math.Sin(r);
                void Leg(string control, string target, string descriptor, double coeff, bool isX)
                {
                    if (Math.Abs(coeff) >= 0.001) _b.Bind(layer, control, target, Make(descriptor, coeff, isX), PadId);
                }
                Leg(cx, tx, gx, cos, true);
                Leg(cx, tx, gy, -sin, true);
                Leg(cy, ty, gx, sin, false);
                Leg(cy, ty, gy, cos, false);
                _b.SumRow(tx);
                _b.SumRow(ty);
            }
            else
            {
                _axisSources[cx] = Make(gx, 1.0, true);
                _axisSources[cy] = Make(gy, 1.0, false);
                _b.Bind(layer, cx, tx, _axisSources[cx], PadId);
                _b.Bind(layer, cy, ty, _axisSources[cy], PadId);
            }

            if (output is > 0 and < 100)
                _b.Partial(ImportReasons.MaxOutput, label, prefix + "MaxOutput", "", output.ToString("0.#", CultureInfo.InvariantCulture));
        }

        // ── Control names ───────────────────────────────────────────────

        private ImportInput ControlInput(string name)
        {
            ImportInput Btn(string descriptor) => new() { Control = "ds:" + name, Label = name, Descriptor = descriptor };
            // Mapping.cs reads a stick direction as pressed past 55 of 127.
            const int StickPress = 43;
            ImportInput Half(string descriptor, bool lower, int threshold = 0) => new()
            {
                Control = "ds:" + name,
                Label = name,
                Descriptor = descriptor,
                HalfAxis = true,
                Invert = lower,
                DeadZone = threshold,
                IsAnalog = true,
            };
            ImportInput Pull(string descriptor, int threshold) => new()
            {
                Control = "ds:" + name,
                Label = name,
                Descriptor = descriptor,
                DeadZone = threshold,
                IsAnalog = true,
            };
            ImportInput Ring(string prefix, string descriptor)
            {
                var i = Pull(descriptor, (int)Math.Clamp(Num(prefix + "OuterBindDead", 75), 1, 99));
                i.Invert = IsTrue(prefix + "OuterBindInvert");
                return i;
            }

            switch (name)
            {
                case "Cross": return Btn("Gamepad ButtonA");
                case "Circle": return Btn("Gamepad ButtonB");
                case "Square": return Btn("Gamepad ButtonX");
                case "Triangle": return Btn("Gamepad ButtonY");
                case "L1": return Btn("Gamepad LeftShoulder");
                case "R1": return Btn("Gamepad RightShoulder");
                case "L2": return Pull("Gamepad LeftTrigger", TriggerThreshold("L2"));
                case "R2": return Pull("Gamepad RightTrigger", TriggerThreshold("R2"));
                case "L2FullPull": return Pull("Gamepad LeftTrigger", 99);
                case "R2FullPull": return Pull("Gamepad RightTrigger", 99);
                case "L3": return Btn("Gamepad LeftStick");
                case "R3": return Btn("Gamepad RightStick");
                case "Share": return Btn("Gamepad ButtonBack");
                case "Options": return Btn("Gamepad ButtonStart");
                case "PS": return Btn("Gamepad ButtonGuide");
                case "DpadUp": return Btn("Gamepad DPadUp");
                case "DpadDown": return Btn("Gamepad DPadDown");
                case "DpadLeft": return Btn("Gamepad DPadLeft");
                case "DpadRight": return Btn("Gamepad DPadRight");
                // SDL's MISC1 is the microphone or capture button, and its
                // DualSense Edge mapping is paddle1 right back, paddle2 left
                // back, paddle3 right function, paddle4 left function.
                case "Mute":
                case "Capture":
                    return Btn("Button 11");
                case "BRP": case "SideR": return Btn("Gamepad Paddle1");
                case "BLP": case "SideL": return Btn("Gamepad Paddle2");
                case "FnR": return Btn("Gamepad Paddle3");
                case "FnL": return Btn("Gamepad Paddle4");
                case "LXNeg": return Half("Gamepad LeftStickX", true, StickPress);
                case "LXPos": return Half("Gamepad LeftStickX", false, StickPress);
                case "LYNeg": return Half("Gamepad LeftStickY", true, StickPress);
                case "LYPos": return Half("Gamepad LeftStickY", false, StickPress);
                case "RXNeg": return Half("Gamepad RightStickX", true, StickPress);
                case "RXPos": return Half("Gamepad RightStickX", false, StickPress);
                case "RYNeg": return Half("Gamepad RightStickY", true, StickPress);
                case "RYPos": return Half("Gamepad RightStickY", false, StickPress);
                case "LSOuter": return Ring("LS", "Gamepad LeftStickRing");
                case "RSOuter": return Ring("RS", "Gamepad RightStickRing");
                // Sustained tilt, as DS4Windows reads the accelerometer. Its
                // special actions name these Tilt Left, Tilt Right, Tilt Down
                // and Tilt Up. Lean Y reads positive with the nose up.
                case "GyroXPos": return Half("Gyro Lean X", true, LeanThreshold(4));
                case "GyroXNeg": return Half("Gyro Lean X", false, LeanThreshold(4));
                case "GyroZPos": return Half("Gyro Lean Y", true, LeanThreshold(5));
                case "GyroZNeg": return Half("Gyro Lean Y", false, LeanThreshold(5));
                case "GyroSwipeLeft": return Half(SwipeAxisX(), false, SwipeThreshold("DeadZoneX"));
                case "GyroSwipeRight": return Half(SwipeAxisX(), true, SwipeThreshold("DeadZoneX"));
                case "GyroSwipeUp": return Half("Gyro Pitch", false, SwipeThreshold("DeadZoneY"));
                case "GyroSwipeDown": return Half("Gyro Pitch", true, SwipeThreshold("DeadZoneY"));
                case "TouchLeft": return Btn("Touchpad 0 Click Left");
                case "TouchRight": return Btn("Touchpad 0 Click Right");
                // Mouse.cs sets these regions on the click, where PadForge's two
                // spots read the touch, so the click gates them.
                case "TouchUpper":
                {
                    var top = Btn("Touchpad 0 TouchTop");
                    top.Gate = "Touchpad 0 Click";
                    return top;
                }
                case "TouchMulti":
                {
                    var multi = Btn("Touchpad 0 TouchMulti");
                    multi.Gate = "Touchpad 0 Click";
                    return multi;
                }
                // DS4StateFieldMapping reads these true on the one frame a touch
                // begins or ends.
                case "TouchStarted":
                {
                    var began = Btn("Touchpad 0 Finger 0 Down");
                    began.EdgeOnly = MacroTriggerMode.OnPress;
                    return began;
                }
                case "TouchEnded":
                {
                    var ended = Btn("Touchpad 0 Finger 0 Down");
                    ended.EdgeOnly = MacroTriggerMode.OnRelease;
                    return ended;
                }
                case "SwipeUp": return Btn("Touchpad 0 SwipeUp");
                case "SwipeDown": return Btn("Touchpad 0 SwipeDown");
                case "SwipeLeft": return Btn("Touchpad 0 SwipeLeft");
                case "SwipeRight": return Btn("Touchpad 0 SwipeRight");
                default: return null;
            }
        }

        /// <summary>Mapping.cs calls a tilt pressed when the accelerometer,
        /// 128 to a g, scaled by the file's sensitivity, reads past 67. PadForge's
        /// lean reads 90 degrees as full scale.</summary>
        private int LeanThreshold(int sensitivityIndex)
        {
            double g = 67.0 / 128.0 / SensitivityPart(sensitivityIndex);
            if (g >= 1.0) return 99;
            return (int)Math.Clamp(Math.Round(Math.Asin(g) * 180.0 / Math.PI / 90.0 * 100.0), 1, 99);
        }

        private string SwipeAxisX()
            => string.Equals(Text(Element("GyroSwipeSettings"), "XAxis"), "Roll", StringComparison.OrdinalIgnoreCase)
                ? "Gyro Roll"
                : "Gyro Yaw";

        /// <summary>Mouse.cs swipes past a speed in degrees a second, 80 unless
        /// the file says. A gyro read as a button takes its dead zone as a
        /// percentage of 90 degrees a second (SourceCoercion).</summary>
        private int SwipeThreshold(string element)
        {
            double degrees = Math.Abs(Num(Element("GyroSwipeSettings"), element, 80));
            return (int)Math.Clamp(Math.Round(degrees / 90.0 * 100.0), 1, 100);
        }

        /// <summary>Mapping.cs reads a tilt only while the gyro is set to
        /// Controls, and Mouse.cs sets a gyro swipe only under DirectionalSwipe.
        /// Under any other mode a binding on one never fires.</summary>
        private bool GyroControlIsOff(string name) => name switch
        {
            "GyroXPos" or "GyroXNeg" or "GyroZPos" or "GyroZNeg" => _gyroMode != "Controls",
            "GyroSwipeLeft" or "GyroSwipeRight" or "GyroSwipeUp" or "GyroSwipeDown" => _gyroMode != "DirectionalSwipe",
            _ => false,
        };

        private int TriggerThreshold(string prefix)
        {
            // Mapping.cs presses a trigger past 100 of 255. A hair trigger reads
            // the soft pull's first movement.
            string mode = Text(prefix + "TwoStageMode");
            return string.Equals(mode, "HairTrigger", StringComparison.OrdinalIgnoreCase) ? 2 : 39;
        }

        /// <summary>A Button value, by either of the two name lists DS4Windows
        /// writes depending on the output device (<c>xboxDefaultNames</c>,
        /// <c>ds4DefaultNames</c>).</summary>
        private ImportOutput ButtonOutput(string name, out bool unbound)
        {
            unbound = name is "Unbound" or "Unassigned" or "";
            ImportOutput o = name switch
            {
                "Left X-Axis-" => ImportOutput.Stick("LeftThumbAxisX", true),
                "Left X-Axis+" => ImportOutput.Stick("LeftThumbAxisX", false),
                "Left Y-Axis-" => ImportOutput.Stick("LeftThumbAxisY", true),
                "Left Y-Axis+" => ImportOutput.Stick("LeftThumbAxisY", false),
                "Right X-Axis-" => ImportOutput.Stick("RightThumbAxisX", true),
                "Right X-Axis+" => ImportOutput.Stick("RightThumbAxisX", false),
                "Right Y-Axis-" => ImportOutput.Stick("RightThumbAxisY", true),
                "Right Y-Axis+" => ImportOutput.Stick("RightThumbAxisY", false),
                "Left Bumper" or "L1" => ImportOutput.Pad("LeftShoulder"),
                "Right Bumper" or "R1" => ImportOutput.Pad("RightShoulder"),
                "Left Trigger" or "L2" => ImportOutput.Trigger("LeftTrigger"),
                "Right Trigger" or "R2" => ImportOutput.Trigger("RightTrigger"),
                "Left Stick" or "L3" => ImportOutput.Pad("LeftThumbButton"),
                "Right Stick" or "R3" => ImportOutput.Pad("RightThumbButton"),
                "A Button" or "Cross" => ImportOutput.Pad("ButtonA"),
                "B Button" or "Circle" => ImportOutput.Pad("ButtonB"),
                "X Button" or "Square" => ImportOutput.Pad("ButtonX"),
                "Y Button" or "Triangle" => ImportOutput.Pad("ButtonY"),
                "Up Button" or "Dpad Up" => ImportOutput.Pad("DPadUp"),
                "Down Button" or "Dpad Down" => ImportOutput.Pad("DPadDown"),
                "Left Button" or "Dpad Left" => ImportOutput.Pad("DPadLeft"),
                "Right Button" or "Dpad Right" => ImportOutput.Pad("DPadRight"),
                "Guide" or "PS" => ImportOutput.Pad("ButtonGuide"),
                "Back" or "Share" => ImportOutput.Pad("ButtonBack"),
                "Start" or "Options" => ImportOutput.Pad("ButtonStart"),
                "Touchpad Click" => _ds4Output ? ImportOutput.Pad("TouchpadClick") : null,
                "Left Mouse Button" => ImportOutput.Mouse(0),
                "Right Mouse Button" => ImportOutput.Mouse(1),
                "Middle Mouse Button" => ImportOutput.Mouse(2),
                "4th Mouse Button" => ImportOutput.Mouse(3),
                "5th Mouse Button" => ImportOutput.Mouse(4),
                "Mouse Wheel Up" => ImportOutput.MouseWheel(false, true),
                "Mouse Wheel Down" => ImportOutput.MouseWheel(false, false),
                "Mouse Up" or "Abs Mouse Up" => ImportOutput.MouseMove(false, true),
                "Mouse Down" or "Abs Mouse Down" => ImportOutput.MouseMove(false, false),
                "Mouse Left" or "Abs Mouse Left" => ImportOutput.MouseMove(true, true),
                "Mouse Right" or "Abs Mouse Right" => ImportOutput.MouseMove(true, false),
                _ => null,
            };
            return o;
        }

        // ── Control and ShiftControl ────────────────────────────────────

        private sealed class ControlSpec
        {
            public string Name;
            public string Button;
            public string Key;
            public string Macro;
            public string Extras;
            public string KeyType;
            public string Lightbar;
            public int Trigger;
        }

        private void ReadControlBlock(XElement block, bool isShift)
        {
            if (block == null) return;
            var specs = new Dictionary<string, ControlSpec>(StringComparer.Ordinal);
            ControlSpec Spec(XElement item)
            {
                if (!specs.TryGetValue(item.Name.LocalName, out var s))
                    specs[item.Name.LocalName] = s = new ControlSpec { Name = item.Name.LocalName };
                int trigger = IntAttr(item, "Trigger");
                if (trigger > 0) s.Trigger = trigger;
                return s;
            }

            foreach (var group in block.Elements())
            {
                foreach (var item in group.Elements())
                {
                    var spec = Spec(item);
                    string value = item.Value?.Trim() ?? "";
                    switch (group.Name.LocalName)
                    {
                        case "Button": spec.Button = value; break;
                        case "Key": spec.Key = value; break;
                        case "Macro": spec.Macro = value; break;
                        case "Extras": spec.Extras = value; break;
                        case "KeyType": spec.KeyType = value; break;
                        case "LightbarMacro": spec.Lightbar = value; break;
                    }
                }
            }

            foreach (var spec in specs.Values)
            {
                if (isShift && spec.Trigger <= 0) continue;
                string layer = isShift ? EnsureShiftLayer(spec.Trigger) : ImportBuilder.BaseLayer;
                if (layer == null)
                {
                    _b.Skipped(ImportReasons.UnknownInput, spec.Name,
                        spec.Trigger.ToString(CultureInfo.InvariantCulture),
                        spec.Trigger.ToString(CultureInfo.InvariantCulture));
                    continue;
                }
                LowerControl(spec, layer);
            }
        }

        /// <summary>Mapping.cs <c>shiftTriggerMapping</c>: the index a
        /// <c>Trigger</c> attribute holds, in order.</summary>
        private static readonly string[] ShiftTriggers =
        {
            null, "Cross", "Circle", "Square", "Triangle", "Options", "Share",
            "DpadUp", "DpadDown", "DpadLeft", "DpadRight", "PS", "L1", "R1", "L2", "R2", "L3", "R3",
            "TouchLeft", "TouchUpper", "TouchMulti", "TouchRight",
            "GyroZNeg", "GyroZPos", "GyroXPos", "GyroXNeg",
            null, // one finger on the touchpad, handled below
            "Mute", "FnL", "FnR", "BLP", "BRP", "Capture", "SideL", "SideR",
        };

        private readonly HashSet<int> _shiftLayers = new();

        /// <summary>One layer per trigger: held, inheriting, and the trigger
        /// keeps its own mapping, because DS4Windows does not consume it.</summary>
        private string EnsureShiftLayer(int trigger)
        {
            if (trigger <= 0 || trigger >= ShiftTriggers.Length) return null;
            string mask = "Shift" + trigger;
            if (!_shiftLayers.Add(trigger)) return mask;

            ImportInput input;
            string name;
            if (trigger == 26)
            {
                name = "Touch";
                input = new ImportInput { Control = "ds:Touch", Label = name, Descriptor = "Touchpad 0 Finger 0 Down" };
            }
            else
            {
                name = ShiftTriggers[trigger];
                input = ControlInput(name);
            }
            if (input == null)
            {
                _shiftLayers.Remove(trigger);
                return null;
            }
            string layerName = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_ShiftLayerName_Format, name);
            _b.AddLayer(mask, layerName);
            if (GyroControlIsOff(name)) return mask;
            var act = ImportBuilder.ActivatorFor(new[] { input }, "Hold", mask);
            act.PostponeMapping = true;
            _b.AddActivator(act);
            _b.Clean(layerName, name, "Hold");
            return mask;
        }

        private void LowerControl(ControlSpec spec, string layer)
        {
            if (GyroControlIsOff(spec.Name)) return;
            var input = ControlInput(spec.Name);
            string path = layer == ImportBuilder.BaseLayer ? spec.Name : _b.LayerName(layer) + " / " + spec.Name;
            if (input == null)
            {
                _b.Skipped(ImportReasons.UnknownInput, path, spec.Name, spec.Name);
                return;
            }

            bool toggle = (spec.KeyType ?? "").Contains("Toggle", StringComparison.Ordinal);
            bool unboundFlag = (spec.KeyType ?? "").Contains("Unbound", StringComparison.Ordinal);

            if (!string.IsNullOrEmpty(spec.Macro))
            {
                ReplaceDefault(layer, spec.Name, input);
                LowerMacro(input, layer, spec.Macro, spec.KeyType, path);
            }
            else if (!string.IsNullOrEmpty(spec.Key))
            {
                ReplaceDefault(layer, spec.Name, input);
                if (unboundFlag)
                    _b.Clean(path, "Unbound", "");
                else
                    LowerKey(input, layer, spec.Key, toggle, path);
            }
            else if (spec.Button != null)
            {
                ReplaceDefault(layer, spec.Name, input);
                var output = ButtonOutput(spec.Button, out bool unbound);
                if (unbound)
                {
                    _b.Clean(path, "Unbound", "");
                }
                else if (output == null)
                {
                    _b.Skipped(ImportReasons.NoSuchPadButton, path, spec.Button, spec.Button);
                }
                else if (input.EdgeOnly != null)
                {
                    LowerEdgeTap(input, layer, output, path);
                }
                else
                {
                    _b.Bind(layer, input, output);
                    _b.Clean(path, output.Label, output.Target);
                }
            }

            if (!string.IsNullOrEmpty(spec.Extras))
            {
                // Mapping.cs: an active lightbar macro takes precedence over
                // the Extras color.
                bool lightbarMacro = (spec.Lightbar ?? "").StartsWith("True", StringComparison.OrdinalIgnoreCase);
                LowerExtras(input, layer, spec.Extras, path, lightbarMacro);
            }
            if (!string.IsNullOrEmpty(spec.Lightbar)) LowerLightbarMacro(input, layer, spec.Lightbar, path);
        }

        /// <summary>The control stops sending its default output. One half of
        /// a stick keeps the other half: the axis row narrows to a half-axis
        /// read of what is left.</summary>
        private void ReplaceDefault(string layer, string name, ImportInput input)
        {
            string axis = name switch
            {
                "LXNeg" or "LXPos" => "LSX",
                "LYNeg" or "LYPos" => "LSY",
                "RXNeg" or "RXPos" => "RSX",
                "RYNeg" or "RYPos" => "RSY",
                _ => null,
            };
            if (axis == null)
            {
                _b.Remove(layer, input.Control);
                if (layer == ImportBuilder.BaseLayer) _replacedRegions.Add(name);
                return;
            }

            bool lower = name.EndsWith("Neg", StringComparison.Ordinal);
            _axisHalves.TryGetValue((layer, axis), out var state);
            state = lower ? (true, state.Upper) : (state.Lower, true);
            _axisHalves[(layer, axis)] = state;

            string control = "ds:" + axis;
            _b.Remove(layer, control);
            if (state.Lower && state.Upper) return;

            bool left = axis[0] == 'L';
            bool isX = axis[2] == 'X';
            string target = (left ? "Left" : "Right") + "ThumbAxis" + (isX ? "X" : "Y");
            var src = _axisSources.TryGetValue(control, out var whole)
                ? whole.Clone()
                : new MappingSource { Descriptor = "Gamepad " + (left ? "Left" : "Right") + "Stick" + (isX ? "X" : "Y") };
            src.HalfAxis = true;
            src.Invert = state.Upper;
            ImportBuilder.SetOutputDirection(src, state.Upper);
            _b.Bind(layer, control, target, src, PadId);
        }

        private void LowerKey(ImportInput input, string layer, string keyText, bool toggle, string path)
        {
            if (!int.TryParse(keyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vk) || vk is <= 0 or > 255)
            {
                _b.Skipped(ImportReasons.UnknownOutput, path, keyText, keyText);
                return;
            }
            var output = ImportOutput.Key((byte)vk);
            if (input.EdgeOnly != null && !toggle)
            {
                LowerEdgeTap(input, layer, output, path);
            }
            else if (toggle)
            {
                _b.AddMacro(new ImportMacro
                {
                    Name = output.Label,
                    Control = input.Control,
                    Layer = layer,
                    Inputs = new[] { input },
                    Mode = input.EdgeOnly ?? MacroTriggerMode.OnPress,
                    PadId = PadId,
                    Actions = new[] { output.Flip() },
                });
                _b.Clean(path, output.Label, "ToggleKey");
            }
            else if (output.HasRow)
            {
                _b.Bind(layer, input, output);
                _b.Clean(path, output.Label, output.Target);
            }
            else
            {
                _b.AddHoldPair(new ImportMacro
                {
                    Name = output.Label,
                    Control = input.Control,
                    Layer = layer,
                    Inputs = new[] { input },
                    Mode = MacroTriggerMode.OnPress,
                    PadId = PadId,
                    Actions = new[] { output.Down() },
                }, new[] { output.Up() });
                _b.Clean(path, output.Label, "ToggleKey");
            }
        }

        private void LowerEdgeTap(ImportInput input, string layer, ImportOutput output, string path)
        {
            _b.AddMacro(new ImportMacro
            {
                Name = output.Label,
                Control = input.Control,
                Layer = layer,
                Inputs = new[] { input },
                Mode = input.EdgeOnly ?? MacroTriggerMode.OnPress,
                PadId = PadId,
                Actions = new[] { output.Tap() },
            });
            _b.Clean(path, output.Label, input.EdgeOnly.ToString());
        }

        // ── Macros ──────────────────────────────────────────────────────

        /// <summary>MacroParser's code table for the mouse and gamepad events.
        /// Each code reads down on its first appearance and up on the next.</summary>
        private ImportOutput MacroCodeOutput(int code) => code switch
        {
            256 => ImportOutput.Mouse(0),
            257 => ImportOutput.Mouse(1),
            258 => ImportOutput.Mouse(2),
            259 => ImportOutput.Mouse(3),
            260 => ImportOutput.Mouse(4),
            261 => ImportOutput.Pad("ButtonA"),
            262 => ImportOutput.Pad("ButtonB"),
            263 => ImportOutput.Pad("ButtonX"),
            264 => ImportOutput.Pad("ButtonY"),
            265 => ImportOutput.Pad("ButtonStart"),
            266 => ImportOutput.Pad("ButtonBack"),
            267 => ImportOutput.Pad("DPadUp"),
            268 => ImportOutput.Pad("DPadDown"),
            269 => ImportOutput.Pad("DPadLeft"),
            270 => ImportOutput.Pad("DPadRight"),
            271 => ImportOutput.Pad("ButtonGuide"),
            272 => ImportOutput.Pad("LeftShoulder"),
            273 => ImportOutput.Pad("RightShoulder"),
            274 => ImportOutput.Trigger("LeftTrigger"),
            275 => ImportOutput.Trigger("RightTrigger"),
            276 => ImportOutput.Pad("LeftThumbButton"),
            277 => ImportOutput.Pad("RightThumbButton"),
            278 => ImportOutput.Stick("LeftThumbAxisX", false),
            279 => ImportOutput.Stick("LeftThumbAxisX", true),
            280 => ImportOutput.Stick("LeftThumbAxisY", false),
            281 => ImportOutput.Stick("LeftThumbAxisY", true),
            282 => ImportOutput.Stick("RightThumbAxisX", false),
            283 => ImportOutput.Stick("RightThumbAxisX", true),
            284 => ImportOutput.Stick("RightThumbAxisY", false),
            285 => ImportOutput.Stick("RightThumbAxisY", true),
            // 286, the touchpad click: a macro's button action writes the Xbox
            // button word, which has none.
            _ => null,
        };

        /// <summary>Decodes a macro by PlayMacroCodeValue's rules: at or past
        /// a billion is a lightbar color, at or past a million a rumble, at or
        /// past 300 a wait of the value less 300, and anything lower a key or
        /// button that alternates down and up. A press with no wait before its
        /// release is one tap. What the macro leaves held is released at its
        /// end and a color it set is reset, as DS4Windows does, unless a special
        /// action says to keep them.</summary>
        internal List<ActionData> DecodeMacro(string text, string path, bool keepState = false)
        {
            var codes = new List<int>();
            foreach (var part in (text ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    codes.Add(v);

            // 287 and 288 are touch events a virtual controller has no use for.
            // 289 to 299 fall through DS4Windows' table as a wait below zero,
            // which public profiles do hold. None of them is a step.
            codes.RemoveAll(c => c is >= 287 and < 300);

            var actions = new List<ActionData>();
            var held = new List<int>();
            bool coloredBar = false;
            for (int i = 0; i < codes.Count; i++)
            {
                int code = codes[i];
                if (code >= 1000000000)
                {
                    if (code == 1000000000)
                    {
                        actions.Add(new ActionData { Type = MacroActionType.LightbarColorClear });
                    }
                    else
                    {
                        coloredBar = true;
                        int rgb = code - 1000000000;
                        actions.Add(LightbarHold(
                            (byte)Math.Clamp(rgb / 1000000, 0, 255),
                            (byte)Math.Clamp(rgb / 1000 % 1000, 0, 255),
                            (byte)Math.Clamp(rgb % 1000, 0, 255)));
                    }
                }
                else if (code >= 1000000)
                {
                    int rumble = code - 1000000;
                    actions.Add(rumble == 0
                        ? new ActionData { Type = MacroActionType.RumbleStop }
                        : new ActionData
                        {
                            Type = MacroActionType.Rumble,
                            RumbleHoldMode = MacroRumbleHoldMode.Sticky,
                            RumbleStrengthLeft = Math.Clamp((rumble / 1000) * 100 / 255, 0, 100),
                            RumbleStrengthRight = Math.Clamp((rumble % 1000) * 100 / 255, 0, 100),
                        });
                }
                else if (code >= 300)
                {
                    int ms = code - 300;
                    if (ms > 0) actions.Add(new ActionData { Type = MacroActionType.Delay, DurationMs = ms });
                }
                else
                {
                    var output = code <= 255 ? ImportOutput.Key((byte)code) : MacroCodeOutput(code);
                    if (output == null)
                    {
                        _b.Skipped(ImportReasons.UnknownOutput, path, code.ToString(CultureInfo.InvariantCulture), code.ToString(CultureInfo.InvariantCulture));
                        continue;
                    }
                    if (held.Remove(code))
                    {
                        actions.Add(output.Up());
                    }
                    else if (i + 1 < codes.Count && codes[i + 1] == code)
                    {
                        actions.Add(output.Tap());
                        i++;
                    }
                    else
                    {
                        held.Add(code);
                        actions.Add(output.Down());
                    }
                }
            }
            if (keepState) return actions;
            foreach (int code in held.AsEnumerable().Reverse())
            {
                var output = code <= 255 ? ImportOutput.Key((byte)code) : MacroCodeOutput(code);
                if (output != null) actions.Add(output.Up());
            }
            if (coloredBar) actions.Add(new ActionData { Type = MacroActionType.LightbarColorClear });
            return actions;
        }

        private static ActionData LightbarHold(byte r, byte g, byte b) => new()
        {
            Type = MacroActionType.LightbarColor,
            LightbarHoldMode = MacroLightbarHoldMode.Sticky,
            LightbarColorSource = MacroLightbarColorSource.Fixed,
            LightbarR = r,
            LightbarG = g,
            LightbarB = b,
        };

        /// <summary>A plain macro plays once per press. HoldMacro and
        /// RepeatMacro both come round again while the button is held:
        /// PlayMacroTask resets its done flag at the end when either is set.</summary>
        private void LowerMacro(ImportInput input, string layer, string text, string keyType, string path)
        {
            var actions = DecodeMacro(text, path);
            if (actions.Count == 0) return;
            bool repeats = (keyType ?? "").Contains("HoldMacro", StringComparison.Ordinal)
                        || (keyType ?? "").Contains("RepeatMacro", StringComparison.Ordinal);
            _b.AddMacro(new ImportMacro
            {
                Name = input.Label,
                Control = input.Control,
                Layer = layer,
                Inputs = new[] { input },
                Mode = input.EdgeOnly ?? (repeats ? MacroTriggerMode.WhileHeld : MacroTriggerMode.OnPress),
                Repeat = repeats && input.EdgeOnly == null ? MacroRepeatMode.UntilRelease : MacroRepeatMode.Once,
                RepeatDelayMs = repeats ? 50 : 100,
                PadId = PadId,
                Actions = actions,
            });
            _b.Clean(path, "Macro", repeats ? "UntilRelease" : "Once");
        }

        /// <summary>Extras: heavy, light, lightbar on, red, green, blue, flash,
        /// mouse sensitivity on, mouse sensitivity. They run while the control
        /// is held, beside whatever else it does.</summary>
        private void LowerExtras(ImportInput input, string layer, string text, string path, bool lightbarMacro)
        {
            var parts = text.Split(',').Select(p =>
                int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0).ToArray();
            int At(int i) => i < parts.Length ? parts[i] : 0;

            var down = new List<ActionData>();
            var up = new List<ActionData>();
            if (At(0) > 0 || At(1) > 0)
            {
                down.Add(new ActionData
                {
                    Type = MacroActionType.Rumble,
                    RumbleHoldMode = MacroRumbleHoldMode.Sticky,
                    RumbleStrengthLeft = Math.Clamp(At(0) * 100 / 255, 0, 100),
                    RumbleStrengthRight = Math.Clamp(At(1) * 100 / 255, 0, 100),
                });
                up.Add(new ActionData { Type = MacroActionType.RumbleStop });
            }
            if (At(2) == 1 && !lightbarMacro)
            {
                down.Add(LightbarHold((byte)Math.Clamp(At(3), 0, 255), (byte)Math.Clamp(At(4), 0, 255), (byte)Math.Clamp(At(5), 0, 255)));
                up.Add(new ActionData { Type = MacroActionType.LightbarColorClear });
                if (At(6) != 0) _b.Partial(ImportReasons.ControllerSetting, path, "Extras", "", "Flash");
            }
            if (At(7) == 1)
                _b.Skipped(ImportReasons.ControllerSetting, path, "Extras", "Mouse sensitivity");
            if (down.Count == 0) return;

            _b.AddHoldPair(new ImportMacro
            {
                Name = input.Label + " extras",
                // Extras run beside the control's mapping, so they do not count
                // as the control's own binding on a layer.
                Control = null,
                Layer = layer,
                Inputs = new[] { input },
                Mode = MacroTriggerMode.OnPress,
                PadId = PadId,
                Actions = down,
            }, up);
            _b.Clean(path, "Extras", "Rumble, lightbar");
        }

        /// <summary><c>True/255,255,255:100;255,0,0:50;/Press/False</c>: active,
        /// colors with their times, trigger, cancel-current
        /// (LightbarMacro.Compile).</summary>
        private void LowerLightbarMacro(ImportInput input, string layer, string text, string path)
        {
            var parts = text.Split('/');
            if (parts.Length < 3 || !string.Equals(parts[0], "True", StringComparison.OrdinalIgnoreCase)) return;

            var actions = new List<ActionData>();
            foreach (var element in parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = element.Split(':');
                if (pair.Length != 2) continue;
                var rgb = pair[0].Split(',');
                if (rgb.Length != 3) continue;
                byte Part(int i) => byte.TryParse(rgb[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte v) ? v : (byte)0;
                actions.Add(LightbarHold(Part(0), Part(1), Part(2)));
                if (int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) && ms > 0)
                    actions.Add(new ActionData { Type = MacroActionType.Delay, DurationMs = ms });
            }
            if (actions.Count == 0) return;
            actions.Add(new ActionData { Type = MacroActionType.LightbarColorClear });

            _b.AddMacro(new ImportMacro
            {
                Name = input.Label + " lightbar",
                Control = null,
                Layer = layer,
                Inputs = new[] { input },
                Mode = string.Equals(parts[2], "Release", StringComparison.OrdinalIgnoreCase)
                    ? MacroTriggerMode.OnRelease
                    : MacroTriggerMode.OnPress,
                PadId = PadId,
                Actions = actions,
            });
            _b.Clean(path, "Lightbar macro", "LightbarColor");
        }

        // ── Triggers ────────────────────────────────────────────────────

        private void ReadTwoStage(string prefix)
        {
            string mode = Text(prefix + "TwoStageMode");
            if (mode is "HipFire" or "HipFireExclusiveButtons")
            {
                string time = Num(prefix + "HipFireTime", 100).ToString("0", CultureInfo.InvariantCulture);
                _b.Partial(ImportReasons.HipFire, prefix, mode, "", time);
            }
            if (mode is "ExclusiveButtons" or "HipFireExclusiveButtons")
                _b.Partial(ImportReasons.ControllerSetting, prefix, mode, "", mode);

            string effect = Text(prefix + "TriggerEffect");
            if (effect.Length == 0 || string.Equals(effect, "None", StringComparison.OrdinalIgnoreCase)) return;

            int? catalog = effect switch
            {
                "Rigid" => TriggerEffectCatalog.ResistanceMode,
                "FullClick" => TriggerEffectCatalog.WeaponMode,
                "Pulse" => TriggerEffectCatalog.VibrationMode,
                _ => null,
            };
            if (catalog == null)
            {
                _b.Skipped(ImportReasons.ControllerSetting, prefix, effect, effect);
                return;
            }
            _b.AddMacro(new ImportMacro
            {
                Name = prefix + " " + effect,
                Mode = MacroTriggerMode.Always,
                PadId = PadId,
                Actions = new[]
                {
                    // Held a moment past each write, so it ends by itself when
                    // the profile switches away, and written four times a second
                    // rather than every frame, since each write encodes a block.
                    new ActionData
                    {
                        Type = MacroActionType.TriggerEffect,
                        TriggerEffectSide = prefix == "L2" ? MacroTriggerEffectSide.Left : MacroTriggerEffectSide.Right,
                        TriggerEffectMode = catalog.Value,
                        TriggerEffectParameters = TriggerEffectCatalog.FormatValues(TriggerEffectCatalog.Defaults(catalog.Value)),
                        TriggerEffectHold = MacroTriggerEffectHold.Duration,
                        TriggerEffectMs = 500,
                    },
                    new ActionData { Type = MacroActionType.Delay, DurationMs = 250 },
                },
            });
            _b.Clean(prefix, effect, "TriggerEffect");
        }

        // ── Gyro ────────────────────────────────────────────────────────

        /// <summary>Mouse.cs <c>getDS4ControlsByName(int)</c>: the index a gyro
        /// trigger list holds, -1 meaning always on.</summary>
        private static readonly string[] GyroTriggers =
        {
            "Cross", "Circle", "Square", "Triangle", "L1", "L2", "R1", "R2",
            "DpadUp", "DpadDown", "DpadLeft", "DpadRight", "L3", "R3",
            null, null, // one and two fingers
            "Options", "Share", "PS", null, "Mute", "SideL", "SideR", "FnL", "FnR", "BLP", "BRP",
        };

        private string GyroTriggerDescriptor(int index)
        {
            if (index == 14) return "Touchpad 0 Finger 0 Down";
            if (index == 15) return "Touchpad 0 Finger 1 Down";
            if (index == 19) return "Touchpad 0 Click";
            if (index < 0 || index >= GyroTriggers.Length || GyroTriggers[index] == null) return null;
            return ControlInput(GyroTriggers[index])?.Descriptor;
        }

        private void ApplyGyroEngage(string triggers, bool turns, bool toggle)
        {
            var list = (triggers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)
                .Where(v => v >= 0)
                .ToList();
            if (list.Count == 0) return;
            string first = GyroTriggerDescriptor(list[0]);
            if (first == null) return;
            _b.GyroEngageDescriptor = first;
            _b.GyroEngageInvert = !turns;
            _b.GyroEngageToggle = toggle;
            if (list.Count > 1)
                _b.Partial(ImportReasons.ControllerSetting, "Gyro", "Triggers", first, "Gyro triggers");
        }

        private void ReadGyro()
        {
            string mode = _gyroMode;
            int invert = (int)Num("GyroInvert", 0);
            double sensitivity = Num("GyroSensitivity", 100) / 100.0;
            double vertical = Num("GyroSensVerticalScale", 100) / 100.0;
            string yaw = (int)Num("GyroMouseHAxis", 0) == 1 ? "Gyro Roll" : "Gyro Yaw";

            switch (mode)
            {
                case "Mouse":
                {
                    ApplyGyroEngage(Text("SATriggers"), IsTrue(_root, "GyroTriggerTurns", true), IsTrue("GyroMouseToggle"));
                    _b.Bind(ImportBuilder.BaseLayer, "ds:gyro:x", "KbmMouseX",
                        new MappingSource { Descriptor = yaw, GyroSensitivity = sensitivity, Invert = (invert & 2) != 0 }, 0);
                    _b.Bind(ImportBuilder.BaseLayer, "ds:gyro:y", "KbmMouseY",
                        new MappingSource { Descriptor = "Gyro Pitch", GyroSensitivity = sensitivity * vertical, Invert = (invert & 1) != 0 }, 0);
                    _b.Clean("Gyro", "Mouse", "KbmMouseX, KbmMouseY");
                    break;
                }
                case "MouseJoystick":
                {
                    string stick = Text("GyroMouseStickOutputStick");
                    string side = stick.Contains("Left", StringComparison.OrdinalIgnoreCase) ? "Left" : "Right";
                    // Mouse.cs reads these bits X first, the reverse of the mouse and
                    // touchpad modes' 2 for X and 1 for Y.
                    int stickInvert = (int)Num("GyroMouseStickInvert", 0);
                    double stickVertical = Num("GyroMouseStickVerticalScale", 100) / 100.0;
                    ApplyGyroEngage(Text("GyroMouseStickTriggers"), IsTrue(_root, "GyroMouseStickTriggerTurns", true), IsTrue("GyroMouseStickToggle"));
                    string axes = Text("GyroMouseStickOutputStickAxes");
                    string stickYaw = (int)Num("GyroMouseStickHAxis", 0) == 1 ? "Gyro Roll" : "Gyro Yaw";
                    if (!string.Equals(axes, "YOnly", StringComparison.OrdinalIgnoreCase))
                        _b.Bind(ImportBuilder.BaseLayer, "ds:gyro:x", side + "ThumbAxisX",
                            new MappingSource { Descriptor = stickYaw, Invert = (stickInvert & 1) != 0 }, PadId);
                    if (!string.Equals(axes, "XOnly", StringComparison.OrdinalIgnoreCase))
                        _b.Bind(ImportBuilder.BaseLayer, "ds:gyro:y", side + "ThumbAxisY",
                            new MappingSource { Descriptor = "Gyro Pitch", GyroSensitivity = stickVertical, Invert = (stickInvert & 2) != 0 }, PadId);
                    _b.Clean("Gyro", "Mouse joystick", side + "ThumbAxis");
                    break;
                }
                case "Passthru":
                {
                    if (_ds4Output)
                    {
                        _b.Bind(ImportBuilder.BaseLayer, "ds:motion:gyro", "MotionGyro", new MappingSource { Descriptor = "Motion Gyro" }, PadId);
                        _b.Bind(ImportBuilder.BaseLayer, "ds:motion:accel", "MotionAccel", new MappingSource { Descriptor = "Motion Accel" }, PadId);
                        _b.Clean("Gyro", "Passthru", "MotionGyro, MotionAccel");
                    }
                    break;
                }
                case "Controls":
                {
                    var settings = Element("GyroControlsSettings");
                    ApplyGyroEngage(Text(settings, "Triggers"), IsTrue(settings, "TriggerTurns", true), IsTrue(settings, "Toggle", false));
                    break;
                }
                case "DirectionalSwipe":
                {
                    var settings = Element("GyroSwipeSettings");
                    ApplyGyroEngage(Text(settings, "Triggers"), IsTrue(settings, "TriggerTurns", true), false);
                    if (Num(settings, "DelayTime", 0) > 0)
                        _b.Partial(ImportReasons.ControllerSetting, "Gyro", "", "", "DelayTime");
                    break;
                }
            }
        }

        // ── Touchpad ────────────────────────────────────────────────────

        private void ReadTouchpad()
        {
            string mode = _touchMode;
            if (_ds4Output && mode != "Passthru" && IsTrue("TouchpadClickPassthru"))
            {
                _b.Bind(ImportBuilder.BaseLayer, "ds:tp:click", "TouchpadClick",
                    new MappingSource { Descriptor = "Touchpad 0 Click" }, PadId);
                _b.Clean("Touchpad", "Click passthrough", "TouchpadClick");
            }
            double sens = Num("touchSensitivity", 100) / 100.0;
            int invert = (int)Num("TouchpadInvert", 0);
            const string Base = ImportBuilder.BaseLayer;

            switch (mode)
            {
                case "Passthru":
                    if (_ds4Output)
                    {
                        void Row(string target, string descriptor)
                            => _b.Bind(Base, "ds:tp:" + target, target, new MappingSource { Descriptor = descriptor }, PadId);
                        Row("TouchpadX1", "Touchpad 0 Finger 0 X");
                        Row("TouchpadY1", "Touchpad 0 Finger 0 Y");
                        Row("TouchpadContact1", "Touchpad 0 Finger 0 Down");
                        Row("TouchpadX2", "Touchpad 0 Finger 1 X");
                        Row("TouchpadY2", "Touchpad 0 Finger 1 Y");
                        Row("TouchpadContact2", "Touchpad 0 Finger 1 Down");
                        Row("TouchpadClick", "Touchpad 0 Click");
                        _b.Clean("Touchpad", "Passthru", "TouchpadX1, TouchpadClick");
                    }
                    break;

                case "Mouse":
                    _b.Bind(Base, "ds:tp:x", "KbmMouseX", new MappingSource
                    {
                        Descriptor = "Touchpad 0 Finger 0 X",
                        Sensitivity = sens,
                        Invert = (invert & 2) != 0,
                    }, 0);
                    _b.Bind(Base, "ds:tp:y", "KbmMouseY", new MappingSource
                    {
                        Descriptor = "Touchpad 0 Finger 0 Y",
                        Sensitivity = sens,
                        Invert = (invert & 1) != 0,
                    }, 0);
                    // Mouse mode's click regions, the rest of defaultButtonMapping.
                    BindRegionDefault("TouchLeft", ImportOutput.Mouse(0));
                    BindRegionDefault("TouchUpper", ImportOutput.Mouse(2));
                    BindRegionDefault("TouchMulti", ImportOutput.Mouse(1));
                    BindRegionDefault("TouchRight", ImportOutput.Mouse(IsTrue("lowerRCOn") ? 1 : 0));
                    _b.Clean("Touchpad", "Mouse", "KbmMouseX, KbmMouseY");
                    if (Num("scrollSensitivity", 0) > 0 || IsTrue("doubleTap") || Num("tapSensitivity", 0) > 0)
                        _b.Partial(ImportReasons.TouchpadShape, "Touchpad", "Scroll and tap", "");
                    break;

                case "AbsoluteMouse":
                    _b.Bind(Base, "ds:tp:x", "KbmMouseX", new MappingSource { Descriptor = "Touchpad 0 Pointer X" }, 0);
                    _b.Bind(Base, "ds:tp:y", "KbmMouseY", new MappingSource { Descriptor = "Touchpad 0 Pointer Y" }, 0);
                    _b.Clean("Touchpad", "Absolute mouse", "KbmMouseX, KbmMouseY");
                    if (Element("AbsMouseRegionSettings") != null && Text(Element("AbsMouseRegionSettings"), "AbsWidth") is { Length: > 0 } w && w != "1")
                        _b.Partial(ImportReasons.TouchpadShape, "Touchpad", "Absolute region", "");
                    break;

                case "MouseJoystick":
                {
                    var stickSettings = Element("TouchpadMouseStick");
                    string side = Text(stickSettings, "OutputStick").Contains("Left", StringComparison.OrdinalIgnoreCase) ? "Left" : "Right";
                    _b.Bind(Base, "ds:tp:x", side + "ThumbAxisX", new MappingSource { Descriptor = "Touchpad 0 StickX" }, PadId);
                    _b.Bind(Base, "ds:tp:y", side + "ThumbAxisY", new MappingSource { Descriptor = "Touchpad 0 StickY" }, PadId);
                    _b.Clean("Touchpad", "Mouse joystick", side + "ThumbAxis");
                    break;
                }
            }
        }

        private void BindRegionDefault(string name, ImportOutput output)
        {
            var input = ControlInput(name);
            if (input == null || _b.HasBinding(ImportBuilder.BaseLayer, input.Control)) return;
            if (_replacedRegions.Contains(name)) return;
            _b.Bind(ImportBuilder.BaseLayer, input, output);
        }

        private readonly HashSet<string> _replacedRegions = new(StringComparer.Ordinal);

        // ── Lightbar ────────────────────────────────────────────────────

        private void ReadLightbar()
        {
            string mode = Text("LightbarMode");
            if (string.Equals(mode, "Passthru", StringComparison.OrdinalIgnoreCase)) return;

            ActionData action;
            if (Num("Rainbow", 0) > 0)
            {
                action = new ActionData { Type = MacroActionType.LightbarModeSet, LightbarTargetMode = LightbarMode.Rainbow };
            }
            else
            {
                var parts = Text("Color").Split(',');
                if (parts.Length != 3) return;
                byte Part(int i) => byte.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte v) ? v : (byte)0;
                action = ImportBuilder.ProfileColor(Part(0), Part(1), Part(2));
            }

            _b.AddMacro(new ImportMacro
            {
                Name = "Lightbar",
                Control = "lightbar",
                RunsBesideLayers = true,
                Mode = MacroTriggerMode.Always,
                PadId = PadId,
                Actions = new[] { action },
            });
            if (IsTrue("ledAsBatteryIndicator"))
                _b.Partial(ImportReasons.LedShownAs, "Lightbar", "Battery", action.Type.ToString(), "Battery");
            else
                _b.Clean("Lightbar", "Color", action.Type.ToString());
        }

        // ── What must not run, and what has no place in a profile ───────

        private void ReadSafetyAndLeftovers()
        {
            string program = Text("LaunchProgram");
            if (program.Length > 0) _b.Skipped(ImportReasons.ProgramNotRun, "LaunchProgram", program, program);

            string wheel = Text("SASteeringWheelEmulationAxis");
            if (wheel.Length > 0 && !string.Equals(wheel, "None", StringComparison.OrdinalIgnoreCase))
                _b.Skipped(ImportReasons.SteeringWheel, "SASteeringWheelEmulationAxis", wheel);

            void Note(string element, Func<string, bool> changed)
            {
                string value = Text(element);
                if (value.Length > 0 && changed(value))
                    _b.Skipped(ImportReasons.ControllerSetting, element, value, element);
            }
            Note("RumbleBoost", v => v != "100");
            Note("RumbleAutostopTime", v => v != "0");
            Note("idleDisconnectTimeout", v => v != "0");
            Note("StartTouchpadOff", v => v == "True");
            Note("MouseAcceleration", v => v == "True");
            Note("ButtonMouseSensitivity", v => v != "25");
            Note("LSFuzz", v => v != "0");
            Note("RSFuzz", v => v != "0");
            Note("LSSquareStick", v => v == "True");
            Note("RSSquareStick", v => v == "True");
            Note("LSAntiSnapback", v => v == "True");
            Note("RSAntiSnapback", v => v == "True");
            Note("TouchpadButtonMode", v => v != "Click");
            Note("TouchDisInvTriggers", v => v != "-1");
            Note("touchToggle", v => v == "False");
            Note("TrackballMode", v => v == "True");
            Note("TouchRelMouseRotation", v => v != "0");
            Note("TouchRelMouseMinThreshold", v => v != "1");
            Note("ButtonMouseVerticalScale", v => v != "100");
            Note("MaxSatRainbow", v => v != "100");
            Note("flashBatteryAt", v => v != "0");
            Note("ChargingType", v => v != "0");
            foreach (string delta in new[] { "LSDeltaAccelSettings", "RSDeltaAccelSettings" })
            {
                if (IsTrue(Element(delta), "Enabled", false))
                    _b.Skipped(ImportReasons.ControllerSetting, delta, "Enabled", delta);
            }
            if (_gyroMode == "Mouse")
            {
                Note("GyroMouseDeadZone", v => v != "10");
                Note("GyroMouseMinThreshold", v => v != "1");
                Note("SATriggerCond", v => v != "and");
            }
            if (_gyroMode == "MouseJoystick")
            {
                Note("GyroMouseStickDeadZone", v => v != "30");
                Note("GyroMouseStickMaxZone", v => v != "830");
                Note("GyroMouseStickAntiDeadX", v => v != "0.4");
                Note("GyroMouseStickAntiDeadY", v => v != "0.4");
                Note("GyroMouseStickMaxOutputEnabled", v => v == "True");
                Note("GyroMouseStickTriggerCond", v => v != "and");
            }
        }

        // ── Special actions and auto profiles ───────────────────────────

        /// <summary>Actions.xml sits one folder above Profiles. The profile
        /// names the ones it uses in ProfileActions, joined by "/".</summary>
        private void ReadSpecialActions(string path)
        {
            var wanted = Text("ProfileActions").Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return;

            var doc = ForeignConfigReader.TryReadSibling(path, "Actions.xml");
            var actions = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in doc?.Root?.Elements("Action") ?? Enumerable.Empty<XElement>())
            {
                string actionName = a.Attribute("Name")?.Value;
                if (actionName != null) actions.TryAdd(actionName, a);
            }
            foreach (string name in wanted)
            {
                if (!actions.TryGetValue(name, out var action))
                {
                    _b.Skipped(ImportReasons.ActionNotFound, "Special action / " + name, name, name);
                    continue;
                }
                LowerAction(action, name);
            }
        }

        /// <summary>Special actions name their triggers in words, the table in
        /// SpecialAction.getDS4ControlsByName. Any other spelling is the control
        /// name itself.</summary>
        private static string ActionControlName(string name) => name switch
        {
            "Up" => "DpadUp",
            "Down" => "DpadDown",
            "Left" => "DpadLeft",
            "Right" => "DpadRight",
            "Function Left" => "FnL",
            "Function Right" => "FnR",
            "Bottom Left Paddle" => "BLP",
            "Bottom Right Paddle" => "BRP",
            "Left Stick Left" => "LXNeg",
            "Left Stick Right" => "LXPos",
            "Left Stick Up" => "LYNeg",
            "Left Stick Down" => "LYPos",
            "Right Stick Left" => "RXNeg",
            "Right Stick Right" => "RXPos",
            "Right Stick Up" => "RYNeg",
            "Right Stick Down" => "RYPos",
            "L2 Full Pull" => "L2FullPull",
            "R2 Full Pull" => "R2FullPull",
            "Left Touch" => "TouchLeft",
            "Right Touch" => "TouchRight",
            "Upper Touch" => "TouchUpper",
            "Multitouch" => "TouchMulti",
            "Swipe Up" => "SwipeUp",
            "Swipe Down" => "SwipeDown",
            "Swipe Left" => "SwipeLeft",
            "Swipe Right" => "SwipeRight",
            "Tilt Up" => "GyroZNeg",
            "Tilt Down" => "GyroZPos",
            "Tilt Left" => "GyroXPos",
            "Tilt Right" => "GyroXNeg",
            "Touch Started" => "TouchStarted",
            "Touch Ended" => "TouchEnded",
            _ => name,
        };

        private void LowerAction(XElement action, string name)
        {
            string type = Text(action, "Type");
            string details = Text(action, "Details");
            string extras = Text(action, "Extras");
            string path = "Special action / " + name;

            var inputs = new List<ImportInput>();
            foreach (var control in Text(action, "Trigger").Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (GyroControlIsOff(ActionControlName(control.Trim()))) return;
                var input = ControlInput(ActionControlName(control.Trim()));
                if (input == null)
                {
                    _b.Skipped(ImportReasons.UnknownInput, path, control, control);
                    return;
                }
                inputs.Add(input);
            }
            if (inputs.Count == 0)
            {
                _b.Skipped(ImportReasons.UnknownInput, path, name, name);
                return;
            }

            switch (type)
            {
                case "Program":
                    _b.Skipped(ImportReasons.ProgramNotRun, path, details, details);
                    return;
                case "Profile":
                    _b.Skipped(ImportReasons.ProfileSwitch, path, details, details);
                    return;
                case "Macro":
                {
                    bool keep = extras.Contains("KeepKeyState", StringComparison.Ordinal);
                    var steps = DecodeMacro(details, path, keep);
                    if (steps.Count == 0) return;
                    bool repeat = extras.Contains("Repeat", StringComparison.Ordinal);
                    bool onRelease = extras.Contains("RunOnRelease", StringComparison.Ordinal);
                    _b.AddMacro(new ImportMacro
                    {
                        Name = name,
                        Inputs = inputs,
                        Mode = onRelease || inputs.Any(i => i.EdgeOnly == MacroTriggerMode.OnRelease) ? MacroTriggerMode.OnRelease
                            : repeat ? MacroTriggerMode.WhileHeld
                            : MacroTriggerMode.OnPress,
                        Repeat = repeat ? MacroRepeatMode.UntilRelease : MacroRepeatMode.Once,
                        RepeatDelayMs = repeat ? 50 : 100,
                        KeepsWhatItHolds = keep,
                        PadId = PadId,
                        Actions = steps,
                    });
                    _b.Clean(path, "Macro", onRelease ? "OnRelease" : "OnPress");
                    return;
                }
                case "Key":
                {
                    string first = details.Split(' ')[0];
                    if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vk) || vk is <= 0 or > 255)
                    {
                        _b.Skipped(ImportReasons.UnknownOutput, path, details, details);
                        return;
                    }
                    var output = ImportOutput.Key((byte)vk);
                    string style = extras.Split('\n')[0].Trim();
                    bool flips = style is "Press" or "Release";
                    var moment = inputs.FirstOrDefault(i => i.EdgeOnly != null)?.EdgeOnly;
                    if (moment != null && !flips)
                    {
                        _b.AddMacro(new ImportMacro
                        {
                            Name = name,
                            Inputs = inputs,
                            Mode = moment.Value,
                            PadId = PadId,
                            Actions = new[] { output.Tap() },
                        });
                    }
                    else if (flips)
                    {
                        _b.AddMacro(new ImportMacro
                        {
                            Name = name,
                            Inputs = inputs,
                            Mode = style == "Release" ? MacroTriggerMode.OnRelease : MacroTriggerMode.OnPress,
                            PadId = PadId,
                            Actions = new[] { output.Flip() },
                        });
                    }
                    else
                    {
                        _b.AddHoldPair(new ImportMacro
                        {
                            Name = name,
                            Inputs = inputs,
                            Mode = MacroTriggerMode.OnPress,
                            PadId = PadId,
                            Actions = new[] { output.Down() },
                        }, new[] { output.Up() });
                    }
                    _b.Clean(path, output.Label, "ToggleKey");
                    return;
                }
                case "DisconnectBT":
                {
                    double hold = double.TryParse(details, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : 0;
                    _b.AddMacro(new ImportMacro
                    {
                        Name = name,
                        Inputs = inputs,
                        Mode = hold > 0 ? MacroTriggerMode.HoldForMs : MacroTriggerMode.OnPress,
                        HoldMs = (int)(hold * 1000),
                        PadId = PadId,
                        Actions = new[]
                        {
                            new ActionData { Type = MacroActionType.DisconnectController, DisconnectTarget = MacroDisconnectTarget.TriggeringDevice },
                        },
                    });
                    _b.Clean(path, "Disconnect", "DisconnectController");
                    return;
                }
                case "MultiAction":
                    LowerMultiAction(inputs[0], name, path, details.Split(','));
                    return;
                case "XboxGameDVR":
                    LowerMultiAction(inputs[0], name, path, ExpandGameDvr(details));
                    return;
                case "GyroCalibration":
                    _b.AddMacro(new ImportMacro
                    {
                        Name = name,
                        Inputs = inputs,
                        PadId = PadId,
                        Actions = new[] { new ActionData { Type = MacroActionType.GyroRecenter } },
                    });
                    _b.Clean(path, "Gyro calibration", "GyroRecenter");
                    return;
                case "SASteeringWheelEmulationCalibrate":
                    _b.Skipped(ImportReasons.SteeringWheel, path, type);
                    return;
                default:
                    _b.Skipped(ImportReasons.ControllerSetting, path, type, type);
                    return;
            }
        }

        /// <summary>SpecialAction's constructor: a Game DVR action names one of
        /// five shortcuts for each gesture, the fourth a key of the file's own.</summary>
        private static string[] ExpandGameDvr(string details)
        {
            var parts = details.Split(',');
            string Macro(int i)
            {
                if (i >= parts.Length || !int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type))
                    return "";
                return type switch
                {
                    0 => "91/71/71/91",
                    1 => "91/164/82/82/164/91",
                    2 => "91/164/44/44/164/91",
                    3 when parts.Length > 3 => parts[3] + "/" + parts[3],
                    4 => "91/164/71/71/164/91",
                    _ => "",
                };
            }
            return new[] { Macro(0), Macro(1), Macro(2) };
        }

        /// <summary>A tap, a hold and a double tap on the action's first
        /// trigger, the one Mapping.cs reads. It calls a press a tap if it lets
        /// go within 150 ms, a hold past 500 ms, and a double tap if the next
        /// press lands within 150 ms of that release. PadForge's window runs
        /// from press to press, so it spans the two 150s.</summary>
        private void LowerMultiAction(ImportInput input, string name, string path, IReadOnlyList<string> macros)
        {
            const int HoldMs = 500;
            const int WindowMs = 300;
            string At(int i) => i < macros.Count ? macros[i] : "";
            var tap = DecodeMacro(At(0), path);
            var hold = DecodeMacro(At(1), path);
            var twice = DecodeMacro(At(2), path);

            void Add(string gesture, MacroTriggerMode mode, List<ActionData> actions)
            {
                if (actions.Count == 0) return;
                _b.AddMacro(new ImportMacro
                {
                    Name = name + " (" + gesture + ")",
                    Inputs = new[] { input },
                    Mode = mode,
                    HoldMs = HoldMs,
                    DoublePressMs = WindowMs,
                    PadId = PadId,
                    Actions = actions,
                });
                _b.Clean(path, gesture, mode.ToString());
            }
            Add("tap", twice.Count > 0 ? MacroTriggerMode.SinglePress
                : hold.Count > 0 ? MacroTriggerMode.ShortPress
                : MacroTriggerMode.OnPress, tap);
            Add("hold", MacroTriggerMode.HoldForMs, hold);
            Add("double tap", MacroTriggerMode.DoublePress, twice);
        }

        /// <summary>Auto Profiles.xml, one folder above Profiles: each program
        /// names the profile each controller takes, so the programs that name
        /// this one are its executables.</summary>
        private static string ExecutablesFor(string profileName, string path)
        {
            var doc = ForeignConfigReader.TryReadSibling(path, "Auto Profiles.xml");
            if (doc?.Root == null) return string.Empty;
            var names = new List<string>();
            foreach (var program in doc.Root.Elements("Program"))
            {
                string exe = program.Attribute("path")?.Value;
                if (string.IsNullOrWhiteSpace(exe)) continue;
                bool uses = false;
                for (int i = 1; i <= 8; i++)
                {
                    if (string.Equals(program.Element("Controller" + i)?.Value?.Trim(), profileName, StringComparison.OrdinalIgnoreCase))
                        uses = true;
                }
                if (uses) names.Add(exe);
            }
            return string.Join("|", names);
        }
    }
}
