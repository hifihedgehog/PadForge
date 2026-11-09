using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
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
                Runs = t._b.Runs,
                Starts = t._b.Starts,
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
        /// joined by "|" in the culture DS4Windows runs in, so a decimal comma
        /// is its own, and an older file joins them by ",". It reads a part
        /// in that culture and clamps it to 0.1 through 5 for the sticks, 0.1
        /// through 10 for the triggers and 0 through 5 for the tilt axes, and
        /// a part that does not read is 1.</summary>
        private double SensitivityPart(int index)
        {
            string text = Text("Sensitivity");
            var parts = text.Split('|');
            if (parts.Length == 1) parts = text.Split(',');
            if (index >= parts.Length) return 1.0;
            string part = parts[index].Trim();
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                && !double.TryParse(part.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return 1.0;
            if (!double.IsFinite(v)) return 1.0;
            return index switch
            {
                2 or 3 => Math.Clamp(v, 0.1, 10.0),
                4 or 5 => Math.Clamp(v, 0.0, 5.0),
                _ => Math.Clamp(v, 0.1, 5.0),
            };
        }

        /// <summary>A stick, trigger or tilt axis's named output curve on
        /// 0..1, Mapping.cs's own formula, or null for linear. The sticks'
        /// enhanced precision is 0.8, 0.08 and 1.32. The triggers' and the
        /// tilt axes' is 0.55, 0.18 and 1.72 (SetCurveAndDeadzone's L2, R2,
        /// SX and SZ blocks).</summary>
        private Func<double, double> CurveOf(string prefix, bool stick)
            => Text(prefix + "OutputCurveMode").ToLowerInvariant() switch
            {
                "quadratic" => x => x * x,
                "cubic" => x => x * x * x,
                "enhanced-precision" => stick
                    ? x => x <= 0.4 ? 0.8 * x : x <= 0.75 ? x - 0.08 : x * 1.32 - 0.32
                    : x => x <= 0.4 ? 0.55 * x : x <= 0.75 ? x - 0.18 : x * 1.72 - 0.72,
                "easeout-quad" => x => -x * (x - 2.0),
                "easeout-cubic" => x => (x - 1.0) * (x - 1.0) * (x - 1.0) + 1.0,
                "custom" => BezierCurve(Text(prefix + "OutputCurveCustom")),
                _ => null,
            };

        /// <summary>A cubic bezier from (0,0) to (1,1), written <c>x1, y1, x2,
        /// y2</c>, as y at a given x. Null when the text does not read.</summary>
        internal static Func<double, double> BezierCurve(string definition)
        {
            var parts = (definition ?? "").Split(',');
            if (parts.Length < 4) return null;
            var v = new double[4];
            for (int i = 0; i < 4; i++)
            {
                string token = parts[i].Trim().TrimEnd('%');
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
                if (!double.IsFinite(v[i])) return null;
            }
            double Axis(double a, double b, double t)
            {
                double u = 1 - t;
                return 3 * u * u * t * a + 3 * u * t * t * b + t * t * t;
            }
            return x =>
            {
                double lo = 0, hi = 1;
                for (int i = 0; i < 40; i++)
                {
                    double mid = (lo + hi) / 2;
                    if (Axis(v[0], v[2], mid) < x) lo = mid;
                    else hi = mid;
                }
                return Axis(v[1], v[3], (lo + hi) / 2);
            };
        }

        /// <summary>A trigger's row, with its tuning on the device's Triggers
        /// card. Mapping.cs takes the dead zone and max zone, the max output
        /// and the anti-dead zone first (1615-1663), then the sensitivity,
        /// clamped to full (1743-1749), then the curve (2045-2080). PadForge
        /// reads a row's sensitivity before the card, so the sensitivity
        /// rides the card too: the anti-dead zone grows by it, the range
        /// narrows by what is left of it, and a curve takes what neither can
        /// hold. The row itself reads the plain pull.</summary>
        private MappingSource TriggerSource(string prefix, int sensIndex)
        {
            var src = new MappingSource { Descriptor = prefix == "L2" ? "Gamepad LeftTrigger" : "Gamepad RightTrigger" };
            // A trigger's dead zone has no prefix of its own. ProfileDTO calls
            // it LeftTriggerMiddle and RightTriggerMiddle, 0 through 255.
            var z = TriggerZones(prefix);
            if (z.Dead > 0)
            {
                // A trigger's dead zone only folds onto its card with a shape
                // stamped (StarterProfileCatalog.BrakeTrigger).
                src.ParamStickDeadZoneShape = 1;
                src.ParamStickDeadZoneInner = Math.Min(z.Dead, 0.99);
            }
            var (anti, range, curve) = TriggerCard(z);
            if (anti > 0) src.ParamAntiDeadzone = anti;
            if (range < 1) src.ParamRangeOuter = range;
            if (curve != null) _b.Curves[prefix == "L2" ? "LeftTrigger" : "RightTrigger"] = ImportBuilder.CurvePoints(curve);
            string stage = StageRead(prefix, false, TriggerPullPercent(prefix));
            if (stage.Length > 0) src.GateDescriptor = stage;
            return src;
        }

        /// <summary>The Triggers card that plays Mapping.cs's trigger:
        /// its anti-dead zone, its range as a fraction, and its curve on the
        /// pull past the dead zone, null for a straight line.
        ///
        /// <para>With the dead zone d, max zone m, max output c, anti-dead
        /// zone a and sensitivity s, Mapping.cs gives curve(min(1, s(a + (1 -
        /// a) min(o, c)))) for o = (min(t, m) - d) / (m - d). The card gives
        /// A + (1 - A) L(p) for p = min(1, (t - d) / (r - d)). With A = sa and
        /// k = s(1 - a) / (1 - sa), a range r = d + (m - d) / k makes p =
        /// min(1, ko) for k of 1 or more, and L(p) = min(p, kc) then matches
        /// the straight line exactly. Under 1 the range stays m and L scales
        /// by k. A curve takes A into L, since the card lifts after its curve
        /// and Mapping.cs before. A trigger that reads the same past its dead
        /// zone at every pull (sa of 1 or more, a max zone at or under the
        /// dead zone, a max output of 0, a dead zone of 255) gets a flat
        /// curve at that value.</para></summary>
        private static (double Anti, double Range, Func<double, double> Curve) TriggerCard((double Dead, double Anti, double Max, double Cap, double Sens, Func<double, double> Curve) z)
        {
            double Shaped(double o) => o > 0.0 && z.Curve != null ? z.Curve(o) : o;
            var (lift, range, line, flat) = ZoneLine(z.Dead, z.Anti, z.Max, z.Cap, z.Sens, 1.0, z.Curve);
            if (flat is double full) return (0.0, 1.0, _ => full);
            if (z.Curve == null) return (lift, range, line);
            if (line == null && lift <= 0.0) return (0.0, range, z.Curve);
            return (0.0, range, x => Shaped(lift + (1.0 - lift) * (line != null ? line(x) : x)));
        }

        /// <summary>The straight-line part of a trigger or stick axis that
        /// Mapping.cs shapes as curve(min(1, s(a + (1 - a) min(g o, c)))) for
        /// the travel o past the dead zone d, scaled to the max zone m: the
        /// lift s a, the card's range as a fraction of the travel, and the
        /// line on the card's remapped travel p, null for p itself, with
        /// curve(lift + (1 - lift) line(p)) giving Mapping.cs's value. The
        /// gain g is a radial stick's vertical scale on Y, which Mapping.cs
        /// applies and clamps to full before the max output c (1200-1203).
        /// Flat holds the value when every travel past the dead zone reads
        /// the same: a max output or gain of 0, a dead zone at full, a max
        /// zone at the dead zone (0 / 0, which Mapping.cs leaves at rest),
        /// a max zone under it (full at once), or a lift of full.</summary>
        private static (double Lift, double Range, Func<double, double> Line, double? Flat) ZoneLine(
            double d, double a, double m, double c, double s, double g, Func<double, double> curve)
        {
            double Shaped(double o) => o > 0.0 && curve != null ? curve(o) : o;
            if (c <= 0.0 || g <= 0.0 || d >= 1.0 || Math.Abs(m - d) < 1e-9) return (0.0, 1.0, null, 0.0);
            if (m < d) return (0.0, 1.0, null, Shaped(Math.Min(1.0, s * ((1.0 - a) * Math.Min(g, c) + a))));
            double lift = s * a;
            if (lift >= 1.0) return (0.0, 1.0, null, Shaped(1.0));
            // s (1 - a) min(g o, c) = (1 - lift) k min(o, c / g).
            double k = s * (1.0 - a) * g / (1.0 - lift);
            double capO = c / g;
            if (k >= 1.0)
            {
                double top = Math.Min(1.0, k * capO);
                return (lift, d + (m - d) / k, top < 1.0 ? x => Math.Min(x, top) : null, null);
            }
            return (lift, m, x => k * Math.Min(x, capO), null);
        }

        /// <summary>What ProfileDTO gives a Radial stick's dead zone and
        /// anti-dead zone a file leaves out: StickDeadZoneInfo.DEFAULT_DEADZONE
        /// and DEFAULT_ANTIDEADZONE (ProfileDTO.cs:453-477), which MapTo copies
        /// on (2047-2050).</summary>
        private static class StickDeadZoneInfo
        {
            public const double DeadZone = 10;
            public const double AntiDeadZone = 20;
        }

        /// <summary>A trigger's anti-dead zone a file leaves out: L2AntiDeadZone
        /// and R2AntiDeadZone are auto-properties with no initializer
        /// (ProfileDTO.cs:383-393).</summary>
        private const double TriggerAntiDeadZone = 0;

        /// <summary>One stick axis's zones as ProfileDTO reads them (the
        /// Radial type's shared values, or the Axial type's for that axis).
        /// An AxialDeadOptions child the file leaves out reads 0, its max zone
        /// and max output included: those fields have no initializer
        /// (ProfileDTO.cs:2699-2760), the DTO builds the options when it is
        /// made (1445-1447), and MapTo copies them in whole (2069-2079). All
        /// as fractions: the dead zone of 127, the anti-dead zone, which
        /// Mapping.cs applies only above 0, the max zone, the max output
        /// widened by 0.99 and capped at full (1204-1216, 1304-1309), 1 when
        /// Mapping.cs skips it, the sensitivity and the vertical scale, which
        /// only the Radial type applies, and the curve.</summary>
        private (bool Axial, double Dead, double Anti, double Max, double Cap, double Sens, double Gain, Func<double, double> Curve) StickZones(string prefix, bool isX)
        {
            bool axial = string.Equals(Text(prefix + "DeadZoneType"), "Axial", StringComparison.OrdinalIgnoreCase);
            var opts = Element(prefix + "AxialDeadOptions");
            double dead = axial ? Num(opts, isX ? "DeadZoneX" : "DeadZoneY", 0) : Num(prefix + "DeadZone", StickDeadZoneInfo.DeadZone);
            double anti = axial ? Num(opts, isX ? "AntiDeadZoneX" : "AntiDeadZoneY", 0) : Num(prefix + "AntiDeadZone", StickDeadZoneInfo.AntiDeadZone);
            double max = axial ? Num(opts, isX ? "MaxZoneX" : "MaxZoneY", 0) : Num(prefix + "MaxZone", 100);
            double output = axial ? Num(opts, isX ? "MaxOutputX" : "MaxOutputY", 0) : Num(prefix + "MaxOutput", 100);
            bool capped = output != 100 || (!axial && IsTrue(prefix + "MaxOutputForce"));
            double vertical = axial || isX ? 100 : Num(prefix + "VerticalScale", 100);
            return (axial,
                Math.Clamp(dead, 0.0, 127.0) / 127.0,
                Math.Clamp(anti, 0.0, 100.0) / 100.0,
                Math.Clamp(max, 0.0, 100.0) / 100.0,
                capped ? Math.Min(Math.Clamp(output, 0.0, 100.0) / 100.0 / 0.99, 1.0) : 1.0,
                axial ? 1.0 : SensitivityPart(prefix == "LS" ? 0 : 1),
                Math.Clamp(vertical, 0.0, 400.0) / 100.0,
                CurveOf(prefix, stick: true));
        }

        /// <summary>The stick card that plays one axis of Mapping.cs's stick
        /// on a straight push: its anti-dead zone, its range as a fraction,
        /// and its curve, null for a straight line. The card runs the curve
        /// before its anti-dead zone and Mapping.cs the anti-dead zone, the
        /// sensitivity and then the curve, so with a curve the anti-dead zone
        /// takes the curve's first value past the dead zone, and the curve
        /// the rest of the climb to full: A + (1 - A) L(p) is Mapping.cs's
        /// value. Keeping the jump in the anti-dead zone keeps it on the
        /// pair's length, as the card floors it, where a jump in the curve
        /// would lift each axis apart.</summary>
        private static (double Anti, double Range, Func<double, double> Curve) StickCard((bool Axial, double Dead, double Anti, double Max, double Cap, double Sens, double Gain, Func<double, double> Curve) z)
        {
            double Shaped(double o) => o > 0.0 && z.Curve != null ? z.Curve(o) : o;
            var (lift, range, line, flat) = ZoneLine(z.Dead, z.Anti, z.Max, z.Cap, z.Sens, z.Gain, z.Curve);
            if (flat is double full) return (0.0, 1.0, _ => full);
            if (z.Curve == null) return (lift, range, line);
            double first = Shaped(lift);
            if (first >= 1.0) return (0.0, 1.0, _ => 1.0);
            return (first, range, x => (Shaped(lift + (1.0 - lift) * (line != null ? line(x) : x)) - first) / (1.0 - first));
        }

        /// <summary>True when Mapping.cs works out a Radial stick's dead
        /// zone, anti-dead zone, max output or curve from the angle of the
        /// push, which the stick card cannot follow off the axes. The dead
        /// zone comes off each axis by that axis's share of it, over that
        /// axis's own span (tempLsXDead, 1181-1189), where the card's scaled
        /// radial dead zone takes the whole length (ComputeRadial). The
        /// anti-dead zone and the max output scale by the same share
        /// (1204-1223), where the card floors the pair's length and caps
        /// nothing. The curve runs on the processed length (tempRatioX over
        /// capX, 1771-1863), where the card curves each axis apart. A max
        /// output of 0 rests the stick on both, and an Axial stick shapes
        /// each axis alone on both.</summary>
        private bool StickBendsByAngle(string prefix, out string settings)
        {
            var named = new List<string>();
            if (!string.Equals(Text(prefix + "DeadZoneType"), "Axial", StringComparison.OrdinalIgnoreCase))
            {
                double output = Num(prefix + "MaxOutput", 100);
                if (Num(prefix + "DeadZone", StickDeadZoneInfo.DeadZone) > 0) named.Add(prefix + "DeadZone");
                if (Num(prefix + "AntiDeadZone", StickDeadZoneInfo.AntiDeadZone) > 0) named.Add(prefix + "AntiDeadZone");
                // The cap binds off the axes once the length reaches it
                // inside the unit circle: a max zone m over a gain g gives
                // the axis its full value at m / g of the push, against a
                // cap of the output over 0.99 at that axis's share.
                double reach = Math.Clamp(Num(prefix + "MaxZone", 100), 0.0, 100.0) / 100.0;
                double vertical = Math.Clamp(Num(prefix + "VerticalScale", 100), 0.0, 400.0) / 100.0;
                if (vertical > 1.0) reach /= vertical;
                if (output > 0 && (output != 100 || IsTrue(prefix + "MaxOutputForce"))
                    && reach * Math.Clamp(output, 0.0, 100.0) / 100.0 / 0.99 < 1.0)
                    named.Add(prefix + "MaxOutput");
                if (CurveOf(prefix, stick: true) != null) named.Add(prefix + "OutputCurveMode");
            }
            settings = string.Join(", ", named);
            return named.Count > 0;
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
                // A setting the file leaves out reads 0: FlickStickSettings'
                // properties have no initializer, the DTO builds them when it
                // is made, and MapTo copies them on (ProfileDTO.cs:3033-3070,
                // 1456, 2309-2316). ProcessFlickStick multiplies the flick's
                // degrees by the calibration, so a full turn is 360 of them,
                // and a calibration of 0 turns nothing (Mapping.cs:3327-3334).
                double calibration = Num(settings, "RealWorldCalibration", 0);
                if (calibration <= 0)
                {
                    _b.Clean(label, "Flick Stick", "");
                    return;
                }
                src.ParamFlickCountsPer360 = 360.0 * calibration;
                double threshold = Num(settings, "FlickThreshold", 0);
                if (threshold > 0) src.ParamFlickThreshold = threshold;
                // A threshold of 0 holds the stick past it even at rest
                // (Mapping.cs:3359-3361), which the flick row's own threshold,
                // above 0, cannot.
                else _b.Partial(ImportReasons.ControllerSetting, label, "FlickThreshold", "", "FlickThreshold");
                // A flick time of 0 spends the flick at once (3398-3399). The
                // row holds a time above 0, so it takes a millisecond.
                double time = Num(settings, "FlickTime", 0);
                src.ParamFlickTime = time > 0 ? time : 0.001;
                _b.Bind(layer, "ds:" + prefix + ":flick", "KbmMouseX", src, 0);
                _b.Clean(label, "Flick Stick", "KbmMouseX");
                return;
            }

            double rotation = Num(prefix + "Rotation", 0);
            // The zones, the sensitivity, the vertical scale, the max output
            // and the curve all ride the device's stick card, which runs
            // after the rows. Mapping.cs applies the sensitivity and the
            // vertical scale after the dead zone, so a row that scaled the
            // raw stick would move the dead zone's edge.
            var zx = StickZones(prefix, true);
            var zy = StickZones(prefix, false);
            var cardX = StickCard(zx);
            var cardY = StickCard(zy);
            if (cardX.Curve != null) _b.Curves[tx] = ImportBuilder.CurvePoints(cardX.Curve);
            if (cardY.Curve != null) _b.Curves[ty] = ImportBuilder.CurvePoints(cardY.Curve);
            if (StickBendsByAngle(prefix, out string bent))
                _b.Partial(ImportReasons.StickByAngle, label, bent, "", bent);

            // A row carries only the rotation's coefficient and sign.
            MappingSource Make(string descriptor, double coeff, bool isX)
            {
                var src = new MappingSource { Descriptor = descriptor };
                var z = isX ? zx : zy;
                var (anti, range, _) = isX ? cardX : cardY;
                // An Axial stick's card takes the Axial shape even without a
                // dead zone, since the card's radial shapes floor the pair's
                // length, and Mapping.cs floors each axis alone (1312-1320).
                if (z.Dead > 0 || zx.Axial)
                {
                    src.ParamStickDeadZoneShape = zx.Axial ? 1 : 2;
                    src.ParamStickDeadZoneInner = Math.Min(z.Dead, 0.99);
                }
                if (anti > 0) src.ParamAntiDeadzone = Math.Min(anti, 0.99);
                if (range < 1) src.ParamRangeOuter = range;
                double scale = Math.Abs(coeff);
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
        }

        // ── Control names ───────────────────────────────────────────────

        /// <param name="specialAction">True for a special action's trigger,
        /// which Mapping.cs reads through getBoolSpecialActionMapping. It
        /// presses a tilt at a different point, and every other control at
        /// the same one.</param>
        private ImportInput ControlInput(string name, bool specialAction = false)
        {
            ImportInput Btn(string descriptor) => new() { Control = "ds:" + name, Label = name, Descriptor = descriptor };
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
                var i = Pull(descriptor, RingPressPercent(prefix, asKey: false));
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
                case "L2": return Pull("Gamepad LeftTrigger", TriggerPressPercent("L2"));
                case "R2": return Pull("Gamepad RightTrigger", TriggerPressPercent("R2"));
                case "L2FullPull": return Pull("Gamepad LeftTrigger", BoundedReads.FullPullPercent);
                case "R2FullPull": return Pull("Gamepad RightTrigger", BoundedReads.FullPullPercent);
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
                case "LXNeg": return Half("Gamepad LeftStickX", true, StickPressPercent("LS", true, false));
                case "LXPos": return Half("Gamepad LeftStickX", false, StickPressPercent("LS", true, true));
                case "LYNeg": return Half("Gamepad LeftStickY", true, StickPressPercent("LS", false, false));
                case "LYPos": return Half("Gamepad LeftStickY", false, StickPressPercent("LS", false, true));
                case "RXNeg": return Half("Gamepad RightStickX", true, StickPressPercent("RS", true, false));
                case "RXPos": return Half("Gamepad RightStickX", false, StickPressPercent("RS", true, true));
                case "RYNeg": return Half("Gamepad RightStickY", true, StickPressPercent("RS", false, false));
                case "RYPos": return Half("Gamepad RightStickY", false, StickPressPercent("RS", false, true));
                case "LSOuter": return Ring("LS", "Gamepad LeftStickRing");
                case "RSOuter": return Ring("RS", "Gamepad RightStickRing");
                // Sustained tilt, as DS4Windows reads the accelerometer. Its
                // special actions name these Tilt Left, Tilt Right, Tilt Down
                // and Tilt Up. Lean Y reads positive with the nose up.
                case "GyroXPos": return Half("Gyro Lean X", true, LeanThreshold(true, specialAction));
                case "GyroXNeg": return Half("Gyro Lean X", false, LeanThreshold(true, specialAction));
                case "GyroZPos": return Half("Gyro Lean Y", true, LeanThreshold(false, specialAction));
                case "GyroZNeg": return Half("Gyro Lean Y", false, LeanThreshold(false, specialAction));
                // SixDirectionalSwipe swipes right on a positive angVelYaw or
                // angVelRoll, minus SDL's yaw or roll. A Gyro Yaw read is SDL's
                // yaw, so right is its lower half. A Gyro Roll read is minus
                // SDL's roll (ReadTunedGyroRate), so right is its upper half.
                case "GyroSwipeLeft": return Half(SwipeAxisX(), SwipeAxisX() == "Gyro Roll", SwipeThreshold("DeadZoneX"));
                case "GyroSwipeRight": return Half(SwipeAxisX(), SwipeAxisX() != "Gyro Roll", SwipeThreshold("DeadZoneX"));
                case "GyroSwipeUp": return Half("Gyro Pitch", false, SwipeThreshold("DeadZoneY"));
                case "GyroSwipeDown": return Half("Gyro Pitch", true, SwipeThreshold("DeadZoneY"));
                // Mouse.cs TouchButtonCheckProcess sets these regions on the
                // click, or on the touch alone in Touch button mode.
                case "TouchLeft": return Btn(RegionsByTouch ? "Touchpad 0 Finger 0 Down Left" : "Touchpad 0 Click Left");
                case "TouchRight": return Btn(RegionsByTouch ? "Touchpad 0 Finger 0 Down Right" : "Touchpad 0 Click Right");
                // PadForge's two spots read the touch, so the click gates them
                // unless the regions answer the touch.
                case "TouchUpper":
                {
                    var top = Btn("Touchpad 0 TouchTop");
                    if (!RegionsByTouch) top.Gate = "Touchpad 0 Click";
                    return top;
                }
                case "TouchMulti":
                {
                    var multi = Btn("Touchpad 0 TouchMulti");
                    if (!RegionsByTouch) multi.Gate = "Touchpad 0 Click";
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

        /// <summary>Where a tilt presses, as a percent of PadForge's lean,
        /// which reads 90 degrees as full scale. Mapping.cs turns the
        /// accelerometer, 128 counts to a g, into an output of 0..128: zero
        /// up to SXDeadZone (a fraction of 128, 0.25 by default), then the
        /// travel to SXMaxZone scaled by the sensitivity and lifted by the
        /// anti-dead zone, each step cut to a whole count, then the SX curve.
        /// A binding, a key and a shift trigger read that output past 0
        /// (GetBoolMapping, GetBoolActionMapping), a special action past 67
        /// (getBoolSpecialActionMapping). SZ is the same for the forward
        /// tilt. 100 when no tilt reaches it.</summary>
        private int LeanThreshold(bool isX, bool specialAction)
        {
            string axis = isX ? "SX" : "SZ";
            double deadZone = Num(axis + "DeadZone", 0.25);
            double maxZone = Math.Clamp(Num(axis + "MaxZone", 100) / 100.0, 0.0, 1.0);
            double anti = Math.Clamp(Num(axis + "AntiDeadZone", 0) / 100.0, 0.0, 1.0);
            double sens = SensitivityPart(isX ? 4 : 5);
            var curve = CurveOf(axis, stick: false);
            int dead = (int)(128.0 * deadZone);
            int need = specialAction ? 68 : 1;
            for (int counts = 0; counts <= 128; counts++)
            {
                int output;
                if (dead > 0 || maxZone < 1.0 || anti > 0.0)
                {
                    int maxValue = (int)(maxZone * 128.0);
                    if (counts > dead)
                    {
                        double ratio = counts < maxValue ? (counts - dead) / (double)(maxValue - dead) : 1.0;
                        output = (int)Math.Min(128.0, sens * 128.0 * ((1.0 - anti) * ratio + anti));
                    }
                    else
                    {
                        output = 0;
                    }
                }
                else
                {
                    output = (int)Math.Min(128.0, sens * 128.0 * (counts / 128.0));
                }
                if (curve != null && output > 0) output = (int)(curve(output / 128.0) * 128.0);
                if (output >= need)
                    return (int)Math.Clamp(Math.Round(Math.Asin(counts / 128.0) * 180.0 / Math.PI / 90.0 * 100.0), 1.0, 99.0);
            }
            return 100;
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

        // ── Press points on the processed state ─────────────────────────
        //
        // ControlService hands MapCustom the state SetCurveAndDeadzone made
        // (rotation, dead zone, max zone, anti-dead zone, vertical scale,
        // sensitivity, curve), and GetBoolMapping presses a pad button, a
        // macro or a shift trigger on that state: a stick direction past 55,
        // a trigger and the outer ring past 100 of 255. A row reads the raw
        // input, so each press point is the raw travel where the processed
        // value first passes DS4Windows' own. A rotated stick turns the axis
        // GetBoolMapping reads, which no single raw axis can, so a pad
        // button's direction reads the unrotated axis.

        /// <summary>The raw travel, in percent, past which
        /// <paramref name="processed"/> passes <paramref name="threshold"/>.
        /// The processed value never falls as the travel grows. 100 when it
        /// never passes, so a full push is the least that presses.</summary>
        private static int RawPastProcessed(Func<double, double> processed, double threshold)
        {
            if (processed(1.0) <= threshold) return 100;
            double lo = 0.0, hi = 1.0;
            for (int i = 0; i < 40; i++)
            {
                double mid = (lo + hi) / 2.0;
                if (processed(mid) > threshold) hi = mid;
                else lo = mid;
            }
            return (int)Math.Clamp(Math.Round(hi * 100.0), 1.0, 100.0);
        }

        /// <summary>One stick axis pushed alone, on 0..1, through
        /// SetCurveAndDeadzone: the dead zone and max zone (radial, or that
        /// axis's axial values), the vertical scale on a radial Y, the max
        /// output, the anti-dead zone and then, unless
        /// <paramref name="zonesOnly"/>, the sensitivity and the curve. The
        /// outer ring reads the stick before those last two.</summary>
        private Func<double, double> StickProcessed(string prefix, bool isX, bool zonesOnly = false)
        {
            bool axial = string.Equals(Text(prefix + "DeadZoneType"), "Axial", StringComparison.OrdinalIgnoreCase);
            var opts = Element(prefix + "AxialDeadOptions");
            double dead = (axial ? Num(opts, isX ? "DeadZoneX" : "DeadZoneY", 0) : Num(prefix + "DeadZone", StickDeadZoneInfo.DeadZone)) / 127.0;
            double anti = (axial ? Num(opts, isX ? "AntiDeadZoneX" : "AntiDeadZoneY", 0) : Num(prefix + "AntiDeadZone", StickDeadZoneInfo.AntiDeadZone)) / 100.0;
            double max = (axial ? Num(opts, isX ? "MaxZoneX" : "MaxZoneY", 0) : Num(prefix + "MaxZone", 100)) / 100.0;
            double maxOut = (axial ? Num(opts, isX ? "MaxOutputX" : "MaxOutputY", 0) : Num(prefix + "MaxOutput", 100)) / 100.0;
            double vertical = !axial && !isX ? Num(prefix + "VerticalScale", 100) / 100.0 : 1.0;
            // The sensitivity applies on the Radial type only (Mapping.cs,
            // "Only apply deprecated Sensitivity modifier for Radial DZ").
            double sens = axial ? 1.0 : SensitivityPart(prefix == "LS" ? 0 : 1);
            var curve = CurveOf(prefix, stick: true);
            return r =>
            {
                if (r <= dead) return 0.0;
                double span = max - dead;
                double o = span > 0.0 ? (Math.Min(r, max) - dead) / span : 1.0;
                o = Math.Clamp(o * vertical, 0.0, 1.0);
                // Mapping.cs widens the max output by dividing it by 0.99.
                if (maxOut is >= 0.0 and < 1.0) o = Math.Min(o, Math.Min(maxOut / 0.99, 1.0));
                if (o <= 0.0) return 0.0;
                o = (1.0 - anti) * o + anti;
                if (zonesOnly) return o;
                o = Math.Min(1.0, o * sens);
                return curve != null ? curve(o) : o;
            };
        }

        /// <summary>A trigger on 0..1 through its dead zone, max zone, max
        /// output, anti-dead zone, sensitivity and curve.</summary>
        private Func<double, double> TriggerProcessed(string prefix)
        {
            var z = TriggerZones(prefix);
            return t =>
            {
                double o;
                if (z.Dead > 0.0) o = t > z.Dead ? PastDeadZone(z, t) : 0.0;
                else o = z.Max > 0.0 ? Math.Min(t, z.Max) / z.Max : 0.0;
                o = Math.Min(o, z.Cap);
                if (o <= 0.0) return 0.0;
                o = (1.0 - z.Anti) * o + z.Anti;
                o = Math.Min(1.0, o * z.Sens);
                return z.Curve != null ? z.Curve(o) : o;
            };
        }

        /// <summary>A trigger's zones as ProfileDTO reads them: the dead zone
        /// a byte, the max zone and max output clamped to 0..100, the
        /// anti-dead zone as given, which Mapping.cs applies only above 0
        /// (above 100 it overflows the byte, read here as full), all as
        /// fractions, with the sensitivity and the curve.</summary>
        private (double Dead, double Anti, double Max, double Cap, double Sens, Func<double, double> Curve) TriggerZones(string prefix)
            => (Math.Clamp(Num(prefix == "L2" ? "LeftTriggerMiddle" : "RightTriggerMiddle", 0), 0.0, 255.0) / 255.0,
                Math.Clamp(Num(prefix + "AntiDeadZone", TriggerAntiDeadZone), 0.0, 100.0) / 100.0,
                Math.Clamp(Num(prefix + "MaxZone", 100), 0.0, 100.0) / 100.0,
                Math.Clamp(Num(prefix + "MaxOutput", 100), 0.0, 100.0) / 100.0,
                SensitivityPart(prefix == "L2" ? 2 : 3),
                CurveOf(prefix, stick: false));

        /// <summary>Mapping.cs's (current - dead) / (maxValue - dead) for a
        /// pull past the dead zone, current clamped to the max zone. A max
        /// zone under the dead zone divides two negatives and reads full, and
        /// one equal to it reads 0 / 0, which leaves the trigger at 0.</summary>
        private static double PastDeadZone((double Dead, double Anti, double Max, double Cap, double Sens, Func<double, double> Curve) z, double t)
            => Math.Abs(z.Max - z.Dead) < 1e-9 ? 0.0
               : z.Max < z.Dead ? 1.0
               : (Math.Min(t, z.Max) - z.Dead) / (z.Max - z.Dead);

        /// <summary>Where a pad button, macro or shift trigger reads a stick
        /// direction as pressed: the processed axis past 55 of 127 on the
        /// positive side, of 128 on the negative.</summary>
        private int StickPressPercent(string prefix, bool isX, bool positive)
            => RawPastProcessed(StickProcessed(prefix, isX), 55.0 / (positive ? 127.0 : 128.0));

        /// <summary>Where a pad button, macro or shift trigger reads a
        /// trigger as pressed: the processed trigger past 100 of 255.</summary>
        private int TriggerPressPercent(string prefix)
            => RawPastProcessed(TriggerProcessed(prefix), 100.0 / 255.0);

        /// <summary>The outer ring's radius. Mapping.cs scales the band past
        /// the outer bind dead zone to 0..255 (or the band inside it, when
        /// inverted), and a pad button reads it past 100, a key past 0. The
        /// ring takes the stick's zones, not its sensitivity or curve.</summary>
        private int RingPressPercent(string prefix, bool asKey)
        {
            double edge = Math.Clamp(Num(prefix + "OuterBindDead", 75), 0, 100) / 100.0;
            double part = asKey ? 0.0 : 100.0 / 255.0;
            double radius = IsTrue(prefix + "OuterBindInvert")
                ? edge * (1.0 - part)
                : edge + (1.0 - edge) * part;
            return Math.Clamp(RawPastProcessed(StickProcessed(prefix, true, zonesOnly: true), radius), 1, 99);
        }

        /// <summary>The percent a trigger's value leaves 0 at: past its dead
        /// zone. A key and a mouse button read it (GetBoolActionMapping), and
        /// so do the two-stage modes.</summary>
        private int TriggerPullPercent(string prefix)
        {
            double dead = Num(prefix == "L2" ? "LeftTriggerMiddle" : "RightTriggerMiddle", 0);
            return (int)Math.Clamp(Math.Ceiling(dead * 100.0 / 255.0), 1.0, 98.0);
        }

        /// <summary>The read a trigger stage takes on top of its own, in the
        /// modes where one stage holds the other back. Empty in Disabled and
        /// Normal, where each fires on its own (ProcessTwoStageTrigger).
        /// <paramref name="press"/> is the percent this stage's output reads
        /// as pressed.</summary>
        private string StageRead(string prefix, bool full, int press)
        {
            string input = prefix == "L2" ? "Gamepad LeftTrigger" : "Gamepad RightTrigger";
            int pull = TriggerPullPercent(prefix);
            int Wait() => (int)Math.Clamp(Num(prefix + "HipFireTime", 100) + Num("DebouncingMs", 0), 0.0, 60000.0);
            switch (Text(prefix + "TwoStageMode"))
            {
                case "ExclusiveButtons":
                    return full ? "" : BoundedReads.Between(Math.Max(pull, press), BoundedReads.FullPullPercent, false, false, input);
                case "HairTrigger":
                    return BoundedReads.Stage(TwoStageMode.HairTrigger, full, 0, pull, press, input);
                case "HipFire":
                    return BoundedReads.Stage(TwoStageMode.HipFire, full, Wait(), pull, press, input);
                case "HipFireExclusiveButtons":
                    return BoundedReads.Stage(TwoStageMode.HipFireExclusive, full, Wait(), pull, press, input);
                default:
                    return "";
            }
        }

        /// <summary>A stick direction, past its dead zone within 67.5 degrees
        /// of that way, and a trigger, past its dead zone. That is how a key
        /// and a mouse button read a control (GetBoolActionMapping). A pad
        /// button and a macro read past a fixed press (GetBoolMapping), which
        /// is what <see cref="ControlInput"/> gives.</summary>
        private void ReadAsKeyOrClick(ImportInput input, string name)
        {
            void Sector(double center, string stick, bool isX)
            {
                string prefix = stick == "Left" ? "LS" : "RS";
                bool axial = string.Equals(Text(prefix + "DeadZoneType"), "Axial", StringComparison.OrdinalIgnoreCase);
                double dead = axial
                    ? Num(Element(prefix + "AxialDeadOptions"), isX ? "DeadZoneX" : "DeadZoneY", 0)
                    : Num(prefix + "DeadZone", StickDeadZoneInfo.DeadZone);
                // The read underneath only has to agree on the side.
                input.DeadZone = 1;
                // MapCustom reads the angle off the rotated stick, and
                // rotateLSCoordinates turns the stick clockwise by the
                // rotation in this counterclockwise frame, so the push that
                // reads as this direction lies the rotation further round.
                double rotation = Num(prefix + "Rotation", 0);
                center = ((center + rotation) % 360.0 + 360.0) % 360.0;
                if (rotation != 0)
                {
                    // A turned sector reaches past the half of the stick the
                    // straight direction reads, so the ring reads under it.
                    input.Descriptor = "Gamepad " + stick + "StickRing";
                    input.HalfAxis = false;
                    input.Invert = false;
                }
                input.Band = BoundedReads.Sector(center, Math.Round(KeySectorHalfWidth(prefix, isX), 2),
                    (int)Math.Clamp(Math.Ceiling(dead * 100.0 / 127.0), 1.0, 99.0), "Gamepad " + stick + "StickRing");
            }
            switch (name)
            {
                case "L2": input.DeadZone = TriggerPullPercent("L2"); break;
                case "R2": input.DeadZone = TriggerPullPercent("R2"); break;
                case "LXNeg": Sector(180, "Left", true); break;
                case "LXPos": Sector(0, "Left", true); break;
                case "LYNeg": Sector(90, "Left", false); break;
                case "LYPos": Sector(270, "Left", false); break;
                case "RXNeg": Sector(180, "Right", true); break;
                case "RXPos": Sector(0, "Right", true); break;
                case "RYNeg": Sector(90, "Right", false); break;
                case "RYPos": Sector(270, "Right", false); break;
                case "LSOuter": input.DeadZone = RingPressPercent("LS", asKey: true); break;
                case "RSOuter": input.DeadZone = RingPressPercent("RS", asKey: true); break;
            }
        }

        /// <summary>True when SetCurveAndDeadzone turns a push of the stick
        /// by an angle that depends on how far it goes, so no sector on the
        /// raw ring holds the region a key reads. MapCustom reads a key's
        /// angle off the processed stick (calculateStickAngles). On the Axial
        /// type every axis is shaped alone (Mapping.cs:1280-1330, 1890-1894):
        /// a dead zone, an anti-dead zone, a max zone or a max output under
        /// 100, or a curve, bends it. On the Radial type the curve runs on the
        /// length and keeps the angle (tempRatio over capX, 1771-1888), and a
        /// dead zone or a vertical scale under 100 turns a push by an angle its
        /// length does not change. An anti-dead zone, a max zone under 100,
        /// a max output under 100 beside a dead zone, a vertical scale over
        /// 100 and a sensitivity over 1 each clamp or lift one axis apart
        /// from the other, which bends it (1141-1240, 1721-1730).</summary>
        private bool KeyAngleBends(string prefix)
        {
            if (string.Equals(Text(prefix + "DeadZoneType"), "Axial", StringComparison.OrdinalIgnoreCase))
            {
                var opts = Element(prefix + "AxialDeadOptions");
                foreach (string axis in new[] { "X", "Y" })
                {
                    if (Num(opts, "DeadZone" + axis, 0) > 0 || Num(opts, "AntiDeadZone" + axis, 0) > 0
                        || Num(opts, "MaxZone" + axis, 0) < 100 || Num(opts, "MaxOutput" + axis, 0) < 100)
                        return true;
                }
                return CurveOf(prefix, stick: true) != null;
            }
            double dead = Num(prefix + "DeadZone", StickDeadZoneInfo.DeadZone);
            return Num(prefix + "AntiDeadZone", StickDeadZoneInfo.AntiDeadZone) > 0
                || Num(prefix + "MaxZone", 100) < 100
                || (Num(prefix + "MaxOutput", 100) < 100 && dead > 0)
                || Num(prefix + "VerticalScale", 100) > 100
                || SensitivityPart(prefix == "LS" ? 0 : 1) > 1.0;
        }

        /// <summary>The half-width, in degrees on the raw ring, of a key's
        /// sector. Mapping.cs reads a direction 67.5 degrees each side of its
        /// axis (GetBoolActionMapping). On the Radial type a dead zone d
        /// (of 127) moves a push at raw angle r to tan r x (1 - d cos r) /
        /// (1 - d sin r), and a vertical scale v multiplies that by v, so the
        /// raw edge is where that reaches 67.5 degrees from the X axis, or
        /// 22.5 for an up or down key.</summary>
        private double KeySectorHalfWidth(string prefix, bool xDirection)
        {
            if (string.Equals(Text(prefix + "DeadZoneType"), "Axial", StringComparison.OrdinalIgnoreCase)) return 67.5;
            double d = Math.Clamp(Num(prefix + "DeadZone", StickDeadZoneInfo.DeadZone) / 127.0, 0.0, 0.99);
            double v = Math.Clamp(Num(prefix + "VerticalScale", 100) / 100.0, 0.01, 1.0);
            if (d <= 0.0 && v >= 1.0) return 67.5;
            double Processed(double r)
                => Math.Atan2(v * Math.Sin(r) / (1.0 - d * Math.Sin(r)), Math.Cos(r) / (1.0 - d * Math.Cos(r))) * 180.0 / Math.PI;
            double target = xDirection ? 67.5 : 22.5;
            double lo = 0.0, hi = Math.PI / 2.0;
            for (int i = 0; i < 60; i++)
            {
                double mid = (lo + hi) / 2.0;
                if (Processed(mid) < target) lo = mid;
                else hi = mid;
            }
            double edge = lo * 180.0 / Math.PI;
            return xDirection ? edge : 90.0 - edge;
        }

        /// <summary>The report line for a key, click or Extras binding.
        /// Partial when <paramref name="control"/> is a stick direction whose
        /// angle the stick's zones or curve bend unevenly
        /// (<see cref="KeyAngleBends"/>).</summary>
        private void KeyLine(string path, string binding, string emitted, string control)
        {
            string prefix = control switch
            {
                "LXNeg" or "LXPos" or "LYNeg" or "LYPos" => "LS",
                "RXNeg" or "RXPos" or "RYNeg" or "RYPos" => "RS",
                _ => null,
            };
            if (prefix != null && KeyAngleBends(prefix))
                _b.Partial(ImportReasons.StickBeforeCurve, path, binding, emitted, control);
            else
                _b.Clean(path, binding, emitted);
        }

        /// <summary>A soft or full pull binding under its trigger's two-stage
        /// mode. Nothing else on the controller reads a stage.</summary>
        private void ApplyStage(ImportInput input, string name)
        {
            string read = name switch
            {
                "L2" => StageRead("L2", false, input.DeadZone),
                "R2" => StageRead("R2", false, input.DeadZone),
                "L2FullPull" => StageRead("L2", true, BoundedReads.FullPullPercent),
                "R2FullPull" => StageRead("R2", true, BoundedReads.FullPullPercent),
                _ => "",
            };
            if (read.Length > 0) input.Band = read;
        }

        /// <summary>Whether a full pull binding does anything: Mapping.cs
        /// passes it over while its trigger's two-stage mode is Disabled, the
        /// default.</summary>
        private bool FullPullIsOff(string name)
        {
            if (name is not ("L2FullPull" or "R2FullPull")) return false;
            string mode = Text(name.Substring(0, 2) + "TwoStageMode");
            return mode.Length == 0 || string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase);
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
                // DS4Windows' absolute outputs place the pointer where the
                // stick points (GetAbsMouseMapping). PadForge's absolute
                // pointer takes pointer sources only, so these move it, and
                // the report line says so.
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
            if (GyroControlIsOff(spec.Name) || FullPullIsOff(spec.Name)) return;
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
                LowerMacro(Staged(spec.Name, false), layer, spec.Macro, spec.KeyType, path);
            }
            else if (!string.IsNullOrEmpty(spec.Key))
            {
                ReplaceDefault(layer, spec.Name, input);
                if (unboundFlag)
                    _b.Clean(path, "Unbound", "");
                else
                    LowerKey(Staged(spec.Name, true), layer, spec.Key, toggle, path);
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
                else
                {
                    var read = Staged(spec.Name, output.Kind is ImportOutputKind.MouseButton or ImportOutputKind.Wheel);
                    if (read.EdgeOnly != null && output.Kind == ImportOutputKind.PadButton && output.PadBit == 0)
                    {
                        // A moment rides a macro, and a macro's button word
                        // has no bit for the touchpad click a row can press.
                        _b.Skipped(ImportReasons.MacroCannotPress, path, spec.Button, output.Label);
                    }
                    else if (read.EdgeOnly != null)
                    {
                        LowerEdgeTap(read, layer, output, path);
                    }
                    else
                    {
                        _b.Bind(layer, read, output);
                        if (spec.Button.StartsWith("Abs Mouse ", StringComparison.Ordinal))
                            _b.Partial(ImportReasons.AbsoluteMouse, path, spec.Button, output.Target);
                        else
                        {
                            if (output.Kind is ImportOutputKind.MouseButton or ImportOutputKind.Wheel)
                                KeyLine(path, output.Label, output.Target, spec.Name);
                            else
                                Line(path, output.Label, output.Target, spec.Name);
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(spec.Extras))
            {
                // Mapping.cs: an active lightbar macro takes precedence over
                // the Extras color.
                bool lightbarMacro = (spec.Lightbar ?? "").StartsWith("True", StringComparison.OrdinalIgnoreCase);
                LowerExtras(Staged(spec.Name, true), layer, spec.Extras, path, lightbarMacro);
            }
            // Mapping.cs runs a lightbar macro only for the controls in its
            // BoolDS4Controls, read from the raw state.
            if (!string.IsNullOrEmpty(spec.Lightbar) && LightbarMacroControls.Contains(spec.Name))
                LowerLightbarMacro(ControlInput(spec.Name), layer, spec.Lightbar, path);
        }

        /// <summary>Mapping.BoolDS4Controls: the controls a lightbar macro
        /// runs for.</summary>
        private static readonly HashSet<string> LightbarMacroControls = new(StringComparer.Ordinal)
        {
            "Square", "Triangle", "Circle", "Cross", "DpadUp", "DpadDown", "DpadLeft", "DpadRight",
            "L1", "L3", "R1", "R3", "Share", "Options", "Mute", "TouchRight", "TouchLeft", "Capture",
            "SideL", "SideR", "FnL", "FnR", "BLP", "BRP",
        };

        /// <summary>True for a direction of a rotated stick. GetBoolMapping
        /// and getBoolSpecialActionMapping read it off the rotated axis, the
        /// stick turned by LSRotation or RSRotation, which mixes both raw
        /// axes, and a row or a macro entry here reads one raw axis.</summary>
        private bool ReadsBeforeRotation(string control) => control switch
        {
            "LXNeg" or "LXPos" or "LYNeg" or "LYPos" => Num("LSRotation", 0) != 0,
            "RXNeg" or "RXPos" or "RYNeg" or "RYPos" => Num("RSRotation", 0) != 0,
            _ => false,
        };

        /// <summary>The report line for a binding that came across. Partial
        /// when <paramref name="control"/> is a direction of a rotated stick
        /// read the GetBoolMapping way, which reads here before the rotation.
        /// A key's or a click's sector turns with the stick and passes
        /// null.</summary>
        private void Line(string path, string binding, string emitted, string control)
        {
            if (control != null && ReadsBeforeRotation(control))
                _b.Partial(ImportReasons.StickBeforeRotation, path, binding, emitted, control);
            else
                _b.Clean(path, binding, emitted);
        }

        /// <summary>The control as one kind of binding reads it. A key, a
        /// mouse button and Extras read it as GetBoolActionMapping does, a
        /// pad button and a macro as GetBoolMapping does, and a trigger's
        /// two-stage mode holds either back.</summary>
        private ImportInput Staged(string name, bool asKeyOrClick)
        {
            var input = ControlInput(name);
            if (asKeyOrClick) ReadAsKeyOrClick(input, name);
            ApplyStage(input, name);
            return input;
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
                KeyLine(path, output.Label, "ToggleKey", input.Label);
            }
            else if (output.HasRow)
            {
                _b.Bind(layer, input, output);
                KeyLine(path, output.Label, output.Target, input.Label);
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
                KeyLine(path, output.Label, "ToggleKey", input.Label);
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
            // 286, the touchpad click, is DecodeMacro's own case: a macro's
            // button action writes the Xbox button word, which has none.
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
                    if (code == 286)
                    {
                        // A DualShock 4 slot has the touchpad click and a
                        // mapping row presses it. An Xbox slot has none.
                        _b.Skipped(_ds4Output ? ImportReasons.MacroCannotPress : ImportReasons.NoSuchPadButton,
                            path, "286", "Touchpad click");
                        continue;
                    }
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
            Line(path, "Macro", repeats ? "UntilRelease" : "Once", input.Label);
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
            KeyLine(path, "Extras", "Rumble, lightbar", input.Label);
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

        /// <summary>DS4Sixaxis GYRO_RES_IN_DEG_SEC: the raw counts DS4Windows'
        /// gyro zones are written in, per degree per second.</summary>
        private const double GyroCountsPerDegree = 16;

        /// <summary>PadForge's gyro reaches full deflection at 500 degrees per
        /// second (SourceCoercion.GyroScale).</summary>
        private const double GyroFullScaleDegrees = 500;

        private static string GyroDegrees(double counts)
            => (Math.Max(0, counts) / GyroCountsPerDegree).ToString("0.###", CultureInfo.InvariantCulture);

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
                    // MouseCursor.cs moves right on gyroYawFull or gyroRollFull,
                    // minus SDL's yaw or roll, with one sign for both. A Gyro
                    // Roll read is minus SDL's roll against a Gyro Yaw read's
                    // plus SDL's yaw (ReadTunedGyroRate), so roll takes the
                    // other direction.
                    _b.Bind(ImportBuilder.BaseLayer, "ds:gyro:x", "KbmMouseX",
                        new MappingSource { Descriptor = yaw, GyroSensitivity = sensitivity, Invert = ((invert & 2) != 0) != (yaw == "Gyro Roll") }, 0);
                    _b.Bind(ImportBuilder.BaseLayer, "ds:gyro:y", "KbmMouseY",
                        new MappingSource { Descriptor = "Gyro Pitch", GyroSensitivity = sensitivity * vertical, Invert = (invert & 1) != 0 }, 0);
                    // MouseCursor.cs subtracts its dead zone from the raw counts.
                    _b.GyroDeadZone = GyroDegrees(Num("GyroMouseDeadZone", 10));
                    _b.Clean("Gyro", "Mouse", "KbmMouseX, KbmMouseY");
                    break;
                }
                case "MouseJoystick":
                {
                    // A stick or axes the file leaves out read None: both are
                    // auto-properties with no initializer, which MapTo copies
                    // (ProfileDTO.cs:1018-1028, 2229-2230), and None writes no
                    // stick (Mapping.cs:3036).
                    string side = OutputStickSide(Text("GyroMouseStickOutputStick"), "");
                    var (stickX, stickY) = OutputStickAxes(Text("GyroMouseStickOutputStickAxes"), "");
                    // Mouse.cs reads these bits X first, the reverse of the mouse and
                    // touchpad modes' 2 for X and 1 for Y.
                    int stickInvert = (int)Num("GyroMouseStickInvert", 0);
                    double stickVertical = Num("GyroMouseStickVerticalScale", 100) / 100.0;
                    ApplyGyroEngage(Text("GyroMouseStickTriggers"), IsTrue(_root, "GyroMouseStickTriggerTurns", true), IsTrue("GyroMouseStickToggle"));
                    string stickYaw = (int)Num("GyroMouseStickHAxis", 0) == 1 ? "Gyro Roll" : "Gyro Yaw";
                    // Mouse.cs SixMouseStick: past the dead zone the stick is
                    // full at the max zone, both in raw counts, and the
                    // anti-dead zone floors any movement. PadForge's gyro is
                    // full at 500 degrees per second, so the outer range is
                    // the max zone over that.
                    double outer = Math.Clamp(Math.Max(1, Num("GyroMouseStickMaxZone", 830)) / GyroCountsPerDegree / GyroFullScaleDegrees, 0.001, 0.99);
                    double antiX = Math.Clamp(Num("GyroMouseStickAntiDeadX", 0.4), 0.0, 0.99);
                    double antiY = Math.Clamp(Num("GyroMouseStickAntiDeadY", 0.4), 0.0, 0.99);
                    _b.GyroDeadZone = GyroDegrees(Num("GyroMouseStickDeadZone", 30));
                    if (side != null && stickX)
                        _b.Bind(ImportBuilder.BaseLayer, "ds:gyro:x", side + "ThumbAxisX",
                            // SixMouseStick pushes right on gyroYawFull or
                            // gyroRollFull with one sign, and a Gyro Roll read
                            // runs against a Gyro Yaw read, as on the mouse.
                            new MappingSource { Descriptor = stickYaw, Invert = ((stickInvert & 1) != 0) != (stickYaw == "Gyro Roll"), ParamRangeOuter = outer, ParamAntiDeadzone = antiX }, PadId);
                    if (side != null && stickY)
                        _b.Bind(ImportBuilder.BaseLayer, "ds:gyro:y", side + "ThumbAxisY",
                            // SixMouseStick pushes the stick up as the nose
                            // comes up (deltaY = -gyroPitchFull toward LY 0).
                            // A Gyro Pitch read is plus SDL's pitch, nose up,
                            // and a stick Y row's positive value is down, so
                            // the row turns it unless bit 2 turns it back.
                            new MappingSource { Descriptor = "Gyro Pitch", GyroSensitivity = stickVertical, Invert = (stickInvert & 2) == 0, ParamRangeOuter = outer, ParamAntiDeadzone = antiY }, PadId);
                    _b.Clean("Gyro", "Mouse joystick", side != null && (stickX || stickY) ? side + "ThumbAxis" : "");
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

        /// <summary>TouchpadButtonMode Touch: the touchpad's click regions fire
        /// on a touch, no click needed.</summary>
        private bool RegionsByTouch => string.Equals(Text("TouchpadButtonMode"), "Touch", StringComparison.OrdinalIgnoreCase);

        /// <summary>One past DS4Windows' shift triggers, so these layers never
        /// meet a shift layer's mask.</summary>
        private string TouchLayerMask(int offset) => "Shift" + (ShiftTriggers.Length + offset);

        /// <summary>ControlService.CheckForTouchToggle: a touch while PS is
        /// held turns touchpad movement off and on, in every mode but
        /// Controls, and StartTouchpadOff starts it off. A toggle layer on
        /// that chord carries both: the movement stops on it, or lives only
        /// on it when the touchpad starts off.</summary>
        private void ApplyTouchToggle()
        {
            if (_touchMode is not ("Mouse" or "MouseJoystick" or "AbsoluteMouse")) return;
            bool toggles = IsTrue(_root, "touchToggle", true);
            bool startsOff = IsTrue("StartTouchpadOff");
            var controls = new[] { "ds:tp:x", "ds:tp:y" };
            if (!toggles)
            {
                // Off from the start with no way to turn it on.
                if (startsOff)
                {
                    foreach (string c in controls) _b.Remove(ImportBuilder.BaseLayer, c);
                    _b.Clean("Touchpad", "Starts off", "");
                }
                return;
            }
            var ps = ControlInput("PS");
            if (ps == null) return;
            var touch = new ImportInput { Control = "ds:Touch", Label = "Touch", Descriptor = "Touchpad 0 Finger 0 Down" };
            string mask = TouchLayerMask(0);
            string name = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_ShiftLayerName_Format, "PS + Touch");
            _b.AddLayer(mask, name);
            _b.AddActivator(ImportBuilder.ActivatorFor(new[] { ps, touch }, "Toggle", mask));
            foreach (string c in controls)
            {
                if (startsOff) _b.MoveBindings(c, ImportBuilder.BaseLayer, mask);
                else _b.Remove(mask, c);
            }
            _b.Clean(name, "PS + Touch", startsOff ? "Toggle, starts off" : "Toggle");
        }

        /// <summary>Mouse.cs touchesMoved: while every TouchDisInvTriggers
        /// control is held, the touchpad's inversion is off. A hold layer on
        /// that chord carries the movement uninverted. With no inversion set
        /// it changes nothing.</summary>
        private void ApplyTouchDisInvert(int invert)
        {
            var list = (Text("TouchDisInvTriggers") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)
                .Where(v => v >= 0)
                .ToList();
            if (list.Count == 0 || invert == 0 || _touchMode != "Mouse" || IsTrue("StartTouchpadOff")) return;
            var inputs = new List<ImportInput>();
            foreach (int index in list)
            {
                string descriptor = GyroTriggerDescriptor(index);
                if (descriptor == null) { inputs = null; break; }
                inputs.Add(new ImportInput { Control = "ds:disinv:" + index, Label = descriptor, Descriptor = descriptor });
            }
            if (inputs == null || inputs.Count > 3)
            {
                _b.Partial(ImportReasons.ControllerSetting, "Touchpad", "", "", "TouchDisInvTriggers");
                return;
            }
            string mask = TouchLayerMask(1);
            string name = string.Format(CultureInfo.CurrentCulture, Strings.Instance.Import_ShiftLayerName_Format, string.Join(" + ", inputs.Select(i => i.Label)));
            _b.AddLayer(mask, name);
            _b.AddActivator(ImportBuilder.ActivatorFor(inputs, "Hold", mask));
            foreach (string c in new[] { "ds:tp:x", "ds:tp:y" })
                _b.CopyBindings(c, ImportBuilder.BaseLayer, mask, s => s.Invert = false);
            _b.Clean(name, "Touchpad inversion off", "Hold");
        }

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
                    // Mouse.cs rolls the trackball in Mouse mode only.
                    if (IsTrue("TrackballMode"))
                    {
                        _b.TouchpadGlide = TrackballGlide(Num("TrackballFriction", 10))
                            .ToString("0.#####", CultureInfo.InvariantCulture);
                        _b.Clean("TrackballMode", "True", "Touchpad momentum");
                    }
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
                    // TouchpadMouseStickSerializer starts at RightStick and XY
                    // (ProfileDTO.cs:3200-3210), as BackingStore does when the
                    // element is absent (MapTo skips it, 2369).
                    var stickSettings = Element("TouchpadMouseStick");
                    string side = OutputStickSide(Text(stickSettings, "OutputStick"), "RightStick");
                    var (stickX, stickY) = OutputStickAxes(Text(stickSettings, "OutputStickAxes"), "XY");
                    if (side != null && stickX)
                        _b.Bind(Base, "ds:tp:x", side + "ThumbAxisX", new MappingSource { Descriptor = "Touchpad 0 StickX" }, PadId);
                    if (side != null && stickY)
                        _b.Bind(Base, "ds:tp:y", side + "ThumbAxisY", new MappingSource { Descriptor = "Touchpad 0 StickY" }, PadId);
                    _b.Clean("Touchpad", "Mouse joystick", side != null && (stickX || stickY) ? side + "ThumbAxis" : "");
                    break;
                }
            }
            ApplyTouchToggle();
            ApplyTouchDisInvert(invert);
        }

        /// <summary>A gyro or touchpad stick's output, GyroMouseStickInfo's and
        /// TouchMouseStickInfo's OutputStick (ProfilePropGroups.cs:339-344):
        /// "Left" or "Right", or null for None, which writes no stick.
        /// <paramref name="absent"/> is what the loader gives a file that
        /// leaves it out.</summary>
        private static string OutputStickSide(string value, string absent)
        {
            string v = value.Length > 0 ? value : absent;
            if (v.Equals("LeftStick", StringComparison.OrdinalIgnoreCase)) return "Left";
            if (v.Equals("RightStick", StringComparison.OrdinalIgnoreCase)) return "Right";
            return null;
        }

        /// <summary>Which axes a gyro or touchpad stick writes, OutputStickAxes
        /// XY, X, Y or None (ProfilePropGroups.cs:346-352, OutputHorizontal
        /// and OutputVertical 514-524).</summary>
        private static (bool X, bool Y) OutputStickAxes(string value, string absent)
        {
            string v = (value.Length > 0 ? value : absent).ToUpperInvariant();
            return (v is "XY" or "X", v is "XY" or "Y");
        }

        /// <summary>The touchpad momentum glide that coasts as DS4Windows'
        /// trackball does. Mouse.cs slows the ball by TRACKBALL_RADIUS x
        /// friction / TRACKBALL_INERTIA a second, with TRACKBALL_INERTIA
        /// 2 x TRACKBALL_MASS x TRACKBALL_RADIUS^2 / 5, in units of one touch
        /// count per TRACKBALL_SCALE (4 ms), over a pad 1920 counts wide
        /// (DS4Touchpad.RESOLUTION_X_MAX). PadForge's glide g slows the ball
        /// by 44.8 x (1 - g) / 0.20 pad widths a second
        /// (SourceCoercion.TouchFrictionAtMinGlide) and reads g from 0.80 to
        /// 1.00.</summary>
        internal static double TrackballGlide(double friction)
        {
            const double Radius = 0.0245, Mass = 45.0, Scale = 0.004, PadCounts = 1920.0;
            const double PadForgeFullFriction = 44.8 / 0.20;
            double inertia = 2.0 * Mass * Radius * Radius / 5.0;
            double padWidthsPerSecondSquared = Radius * Math.Max(0.0, friction) / inertia / Scale / PadCounts;
            return Math.Clamp(1.0 - padWidthsPerSecondSquared / PadForgeFullFriction, 0.80, 1.00);
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

            // The Rainbow element is the seconds one trip around takes.
            // DS4LightBar.updateLightBar tests it before the battery light.
            double rainbow = Num("Rainbow", 0);
            IReadOnlyList<ActionData> actions;
            string shown;
            var color = LedColor();
            if (rainbow > 0)
            {
                // MaxSatRainbow scales every channel (HuetoRGB's C).
                double brightness = Math.Clamp(Num("MaxSatRainbow", 100), 0, 100) / 100.0;
                actions = ImportBuilder.RainbowPattern((int)Math.Clamp(rainbow * 1000, 360, 60000), brightness);
                shown = "Rainbow";
                // Under the battery light, DS4Windows dims the rainbow as the
                // battery drains, and a pattern has no battery to read.
                if (IsTrue("ledAsBatteryIndicator"))
                    _b.Partial(ImportReasons.ControllerSetting, "Lightbar", "Rainbow", "", "ledAsBatteryIndicator");
            }
            else if (IsTrue("ledAsBatteryIndicator"))
            {
                // getTransitionedColor runs from LowColor at empty to Color at
                // full, the device's Battery light from Low to High.
                actions = new[] { new ActionData { Type = MacroActionType.LightbarModeSet, LightbarTargetMode = LightbarMode.Battery } };
                var low = LowLedColor();
                var high = color;
                _b.BatteryColors = string.Create(CultureInfo.InvariantCulture,
                    $"{low.Item1},{low.Item2},{low.Item3};{high.Item1},{high.Item2},{high.Item3}");
                shown = "Battery";
            }
            else
            {
                actions = new[] { ImportBuilder.ProfileColor(color.Item1, color.Item2, color.Item3) };
                shown = "Color";
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
            _b.Clean("Lightbar", shown, "LightbarColor");
        }

        /// <summary>The lightbar color as ProfileDTO reads it: a DS4Color that
        /// starts black (ProfileDTO.cs:98), set from Color when it holds three
        /// parts, else from the older Red, Green and Blue, where
        /// PostProcessXml parses Red for every channel it finds
        /// (2569-2597).</summary>
        private (byte, byte, byte) LedColor()
        {
            if (Text("Color").Length > 0) return ColorOf("Color") ?? ((byte)0, (byte)0, (byte)0);
            byte red = Byte(Text("Red"));
            return (Text("Red").Length > 0 ? red : (byte)0,
                Text("Green").Length > 0 ? red : (byte)0,
                Text("Blue").Length > 0 ? red : (byte)0);
        }

        /// <summary>The battery light's empty color as ProfileDTO reads it:
        /// black (224), set from LowColor, else from LowRed, LowGreen and
        /// LowBlue (2599-2622).</summary>
        private (byte, byte, byte) LowLedColor()
        {
            if (Text("LowColor").Length > 0) return ColorOf("LowColor") ?? ((byte)0, (byte)0, (byte)0);
            return (Byte(Text("LowRed")), Byte(Text("LowGreen")), Byte(Text("LowBlue")));
        }

        private static byte Byte(string text)
            => byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte v) ? v : (byte)0;

        private (byte, byte, byte)? ColorOf(string element)
        {
            var parts = Text(element).Split(',');
            if (parts.Length != 3) return null;
            byte Part(int i) => byte.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte v) ? v : (byte)0;
            return (Part(0), Part(1), Part(2));
        }

        // ── A program the profile starts, and what has no place in a profile ──

        private void ReadSafetyAndLeftovers()
        {
            string program = Text("LaunchProgram");
            if (program.Length > 0)
            {
                // Started when the profile loads: an Always macro that runs it
                // and then waits for as long as a wait can last.
                _b.AddMacro(new ImportMacro
                {
                    Name = "LaunchProgram",
                    // Every layer, so a layer that stops inheriting takes no
                    // copy of it and the program starts once.
                    Layer = "",
                    Mode = MacroTriggerMode.Always,
                    PadId = PadId,
                    Actions = new[]
                    {
                        _b.Run(program, program, onLoad: true),
                        new ActionData { Type = MacroActionType.Delay, DurationMs = int.MaxValue },
                    },
                });
                _b.Clean("LaunchProgram", program, "RunProgram");
            }

            ReadSteeringWheel();

            string boost = Text("RumbleBoost");
            if (boost.Length > 0 && boost != "100")
            {
                if (int.TryParse(boost, NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent) && percent is >= 0 and < 100)
                {
                    _b.RumblePercent = percent.ToString(CultureInfo.InvariantCulture);
                    _b.Clean("RumbleBoost", boost, "Motor strength");
                }
                else
                {
                    _b.Skipped(ImportReasons.ControllerSetting, "RumbleBoost", boost, "RumbleBoost");
                }
            }

            string idle = Text("idleDisconnectTimeout");
            if (idle.Length > 0 && idle != "0")
            {
                if (int.TryParse(idle, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds > 0)
                {
                    _b.IdleDisconnectSeconds = seconds.ToString(CultureInfo.InvariantCulture);
                    _b.Clean("idleDisconnectTimeout", idle, "Idle disconnect");
                }
                else
                {
                    _b.Skipped(ImportReasons.ControllerSetting, "idleDisconnectTimeout", idle, "idleDisconnectTimeout");
                }
            }

            void Note(string element, Func<string, bool> changed, string reason = ImportReasons.ControllerSetting)
            {
                string value = Text(element);
                if (value.Length > 0 && changed(value))
                    _b.Skipped(reason, element, value, element);
            }
            // PadForge has nothing these could land on: no rumble auto-stop,
            // no start-with-the-touchpad-off, no button-to-mouse speed, no
            // stick fuzz, square gate or anti-snapback, no low-battery flash
            // and no charging light.
            const string None = ImportReasons.NoSuchSetting;
            Note("RumbleAutostopTime", v => v != "0", None);
            // The movement modes carry it as the touch toggle's layer.
            Note("StartTouchpadOff", v => v == "True" && _touchMode is not ("Mouse" or "MouseJoystick" or "AbsoluteMouse"));
            Note("MouseAcceleration", v => v == "True", None);
            Note("ButtonMouseSensitivity", v => v != "25", None);
            Note("LSFuzz", v => v != "0", None);
            Note("RSFuzz", v => v != "0", None);
            Note("LSSquareStick", v => v == "True", None);
            Note("RSSquareStick", v => v == "True", None);
            Note("LSAntiSnapback", v => v == "True", None);
            Note("RSAntiSnapback", v => v == "True", None);
            Note("TouchpadButtonMode", v => v == "Release");
            Note("TouchRelMouseRotation", v => v != "0");
            Note("TouchRelMouseMinThreshold", v => v != "1");
            Note("ButtonMouseVerticalScale", v => v != "100", None);
            Note("flashBatteryAt", v => v != "0", None);
            Note("ChargingType", v => v != "0", None);
            foreach (string delta in new[] { "LSDeltaAccelSettings", "RSDeltaAccelSettings" })
            {
                if (IsTrue(Element(delta), "Enabled", false))
                    _b.Skipped(None, delta, "Enabled", delta);
            }
            if (_gyroMode == "Mouse")
            {
                Note("GyroMouseMinThreshold", v => v != "1");
                Note("SATriggerCond", v => v != "and");
            }
            if (_gyroMode == "MouseJoystick")
            {
                Note("GyroMouseStickMaxOutputEnabled", v => v == "True");
                Note("GyroMouseStickTriggerCond", v => v != "and");
            }
        }

        /// <summary>The wheel reads the controller's lean, which PadForge calls
        /// Motion Lean. Mapping.cs writes the wheel over the axis it names. A
        /// full lock past half a turn each way is beyond what a lean can read.</summary>
        private void ReadSteeringWheel()
        {
            string wheel = Text("SASteeringWheelEmulationAxis");
            if (wheel.Length == 0 || string.Equals(wheel, "None", StringComparison.OrdinalIgnoreCase)) return;

            double range = Num("SASteeringWheelEmulationRange", 360);
            MappingSource Lean() => new()
            {
                Descriptor = Engine.Common.Mapping.SourceCoercion.MotionLeanDescriptor,
                ParamMotionInnerDz = 0,
                ParamMotionOuterDz = Math.Clamp(180.0 - range / 2.0, 0.0, 179.0),
            };
            void Axis(string control, string target)
            {
                _b.Remove(ImportBuilder.BaseLayer, control);
                _b.Bind(ImportBuilder.BaseLayer, control, target, Lean(), PadId);
            }
            switch (wheel)
            {
                case "LX": Axis("ds:LSX", "LeftThumbAxisX"); break;
                case "LY": Axis("ds:LSY", "LeftThumbAxisY"); break;
                case "RX": Axis("ds:RSX", "RightThumbAxisX"); break;
                case "RY": Axis("ds:RSY", "RightThumbAxisY"); break;
                case "L2R2":
                {
                    var left = Lean();
                    left.HalfAxis = true;
                    left.Invert = true;
                    var right = Lean();
                    right.HalfAxis = true;
                    _b.Remove(ImportBuilder.BaseLayer, "ds:L2");
                    _b.Remove(ImportBuilder.BaseLayer, "ds:R2");
                    _b.Bind(ImportBuilder.BaseLayer, "ds:L2", "LeftTrigger", left, PadId);
                    _b.Bind(ImportBuilder.BaseLayer, "ds:R2", "RightTrigger", right, PadId);
                    break;
                }
                default:
                    _b.Skipped(ImportReasons.SteeringWheel, "SASteeringWheelEmulationAxis", wheel);
                    return;
            }
            _b.Clean("SASteeringWheelEmulationAxis", wheel, "Motion Lean");
            if (range > 360)
                _b.Partial(ImportReasons.ControllerSetting, "SASteeringWheelEmulationRange", range.ToString("0", CultureInfo.InvariantCulture), "", "SASteeringWheelEmulationRange");
            if (Num("SASteeringWheelFuzz", 0) > 0)
                _b.Partial(ImportReasons.NoSuchSetting, "SASteeringWheelFuzz", "", "", "SASteeringWheelFuzz");
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
            // Actions.xml starts out holding this one, so every DS4Windows the
            // profile reaches has it, whether or not a file came along.
            actions.TryAdd("Disconnect Controller", XElement.Parse("<Action Name=\"Disconnect Controller\"><Trigger>PS/Options</Trigger><Type>DisconnectBT</Type><Details>0</Details></Action>"));
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
            string unrotated = null;
            foreach (var control in Text(action, "Trigger").Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ReadsBeforeRotation(ActionControlName(control.Trim()))) unrotated ??= ActionControlName(control.Trim());
                if (GyroControlIsOff(ActionControlName(control.Trim()))) return;
                var input = ControlInput(ActionControlName(control.Trim()), specialAction: true);
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
                {
                    // Mapping.cs takes $hidden out of the arguments and runs
                    // the program windowless. Run Program starts it as Windows
                    // would.
                    string args = Text(action, "Arguements").Replace("$hidden", "", StringComparison.Ordinal).Trim();
                    double hold = Num(action, "Delay", 0);
                    _b.AddMacro(new ImportMacro
                    {
                        Name = name,
                        Inputs = inputs,
                        Mode = hold > 0 ? MacroTriggerMode.HoldForMs : MacroTriggerMode.OnPress,
                        HoldMs = (int)(hold * 1000),
                        PadId = PadId,
                        Actions = new[] { _b.Run(details, details, args) },
                    });
                    Line(path, details, "RunProgram", unrotated);
                    return;
                }
                case "Profile":
                {
                    _b.AddMacro(new ImportMacro
                    {
                        Name = name,
                        Inputs = inputs,
                        PadId = PadId,
                        Actions = new[]
                        {
                            new ActionData
                            {
                                Type = MacroActionType.ProfileShortcut,
                                ProfileShortcutMode = SwitchProfileMode.Specific,
                                ProfileShortcutName = details,
                            },
                        },
                    });
                    // Going back runs while the other profile is loaded, so it
                    // takes a Profile Shortcut, which reads its buttons from
                    // any profile.
                    if (Text(action, "UnloadTrigger").Length > 0)
                        _b.Partial(ImportReasons.UnloadTrigger, path, details, "ProfileShortcut", details);
                    else
                        Line(path, details, "ProfileShortcut", unrotated);
                    return;
                }
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
                    Line(path, "Macro", onRelease ? "OnRelease" : "OnPress", unrotated);
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
                    string style = Text(action, "UnloadStyle");
                    var moment = inputs.FirstOrDefault(i => i.EdgeOnly != null)?.EdgeOnly;
                    var unload = new List<ImportInput>();
                    foreach (var control in Text(action, "UnloadTrigger").Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var input = ControlInput(ActionControlName(control.Trim()), specialAction: true);
                        if (input != null) unload.Add(input);
                    }
                    if (unload.Count > 0)
                    {
                        // Mapping.cs presses the key when the action fires and
                        // lets it go when the unload trigger is pressed, or
                        // released under the Release style.
                        _b.AddMacro(new ImportMacro
                        {
                            Name = name,
                            Inputs = inputs,
                            PadId = PadId,
                            Actions = new[] { output.Down() },
                        });
                        _b.AddMacro(new ImportMacro
                        {
                            Name = name + " (release)",
                            Inputs = unload,
                            Mode = style == "Release" ? MacroTriggerMode.OnRelease : MacroTriggerMode.OnPress,
                            PadId = PadId,
                            Actions = new[] { output.Up() },
                        });
                    }
                    else if (moment != null)
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
                    Line(path, output.Label, "ToggleKey", unrotated);
                    return;
                }
                case "DisconnectBT":
                {
                    double hold = double.TryParse(details, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : Num(action, "Delay", 0);
                    _b.AddMacro(new ImportMacro
                    {
                        Name = name,
                        Inputs = inputs,
                        Mode = hold > 0 ? MacroTriggerMode.HoldForMs : MacroTriggerMode.OnPress,
                        HoldMs = (int)(hold * 1000),
                        PadId = PadId,
                        Actions = new[]
                        {
                            // The trigger names no device, so what it would
                            // call its own is whatever feeds the slot.
                            new ActionData { Type = MacroActionType.DisconnectController, DisconnectTarget = MacroDisconnectTarget.SlotDevices },
                        },
                    });
                    Line(path, "Disconnect", "DisconnectController", unrotated);
                    return;
                }
                case "MultiAction":
                    LowerMultiAction(inputs[0], name, path, details.Split(','), unrotated);
                    return;
                case "XboxGameDVR":
                    LowerMultiAction(inputs[0], name, path, ExpandGameDvr(details), unrotated);
                    return;
                case "GyroCalibration":
                    _b.AddMacro(new ImportMacro
                    {
                        Name = name,
                        Inputs = inputs,
                        PadId = PadId,
                        Actions = new[] { new ActionData { Type = MacroActionType.GyroRecenter } },
                    });
                    Line(path, "Gyro calibration", "GyroRecenter", unrotated);
                    return;
                case "SASteeringWheelEmulationCalibrate":
                    // Recenter re-captures the lean's neutral too.
                    _b.AddMacro(new ImportMacro
                    {
                        Name = name,
                        Inputs = inputs,
                        PadId = PadId,
                        Actions = new[] { new ActionData { Type = MacroActionType.GyroRecenter } },
                    });
                    Line(path, "Wheel calibration", "GyroRecenter", unrotated);
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
        private void LowerMultiAction(ImportInput input, string name, string path, IReadOnlyList<string> macros, string unrotated)
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
                Line(path, gesture, mode.ToString(), unrotated);
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
