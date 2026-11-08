using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>A DS4Windows stick's settings, as ProfileDTO reads them:
    /// the dead zones on DS4Windows' 0..127 scale, the rest in percent.
    /// The axial fields hold the X axis's, the *Y fields the Y axis's.</summary>
    internal sealed class Ds4StickSettings
    {
        public bool Axial;
        public double Dead, Anti, MaxZone = 100, MaxOutput = 100, VerticalScale = 100, Sens = 1, Rotation;
        public bool MaxOutputForce;
        public double DeadY, AntiY, MaxZoneY = 100, MaxOutputY = 100;
        public string Curve = "linear";

        /// <summary>The settings as a DS4Windows profile body for the left
        /// stick.</summary>
        public string Xml()
        {
            string N(double v) => v.ToString(CultureInfo.InvariantCulture);
            string body = $"<LSDeadZone>{N(Dead)}</LSDeadZone><LSAntiDeadZone>{N(Anti)}</LSAntiDeadZone>"
                + $"<LSMaxZone>{N(MaxZone)}</LSMaxZone><LSMaxOutput>{N(MaxOutput)}</LSMaxOutput>"
                + $"<LSMaxOutputForce>{MaxOutputForce}</LSMaxOutputForce>"
                + $"<LSVerticalScale>{N(VerticalScale)}</LSVerticalScale><LSRotation>{N(Rotation)}</LSRotation>"
                + $"<LSOutputCurveMode>{Curve}</LSOutputCurveMode>"
                + $"<Sensitivity>{N(Sens)}|1|1|1|1|1</Sensitivity>";
            if (Axial)
            {
                body += "<LSDeadZoneType>Axial</LSDeadZoneType><LSAxialDeadOptions>"
                    + $"<DeadZoneX>{N(Dead)}</DeadZoneX><DeadZoneY>{N(DeadY)}</DeadZoneY>"
                    + $"<MaxZoneX>{N(MaxZone)}</MaxZoneX><MaxZoneY>{N(MaxZoneY)}</MaxZoneY>"
                    + $"<AntiDeadZoneX>{N(Anti)}</AntiDeadZoneX><AntiDeadZoneY>{N(AntiY)}</AntiDeadZoneY>"
                    + $"<MaxOutputX>{N(MaxOutput)}</MaxOutputX><MaxOutputY>{N(MaxOutputY)}</MaxOutputY>"
                    + "</LSAxialDeadOptions>";
            }
            else
            {
                body += "<LSDeadZoneType>Radial</LSDeadZoneType>";
            }
            return body;
        }
    }

    /// <summary>A DS4Windows stick two ways: as Mapping.cs computes it, and
    /// as PadForge plays the imported profile. The second runs the real fold
    /// onto the device's stick card, each row source after the editor's save
    /// round trip, the rows read and combined, and the card through
    /// InputManager.ApplyPadSettingTuning, the function Step 3 calls. Callers
    /// sit in the SettingsManagerStatics collection, since the fold reads
    /// SettingsManager.SlotMappingSets.
    ///
    /// <para>Both work on the stick as the rows read it, X right and Y
    /// down, each axis on -1..1, which is the frame DS4Windows rotates and
    /// shapes in (LX - 128, LY - 128). Mapping.cs's byte rounding and its
    /// 127 against 128 sides are left out.</para></summary>
    internal static class Ds4StickChain
    {
        /// <summary>Mapping.cs on a push (x, y): the rotation
        /// (DS4State.rotateLSCoordinates, 282-288), then for the Radial type
        /// the dead zone, max zone, vertical scale, max output and anti-dead
        /// zone off the push's angle (Mapping.cs 1141-1240), the sensitivity
        /// (1721-1730) and the curve on the processed length (1771-1888).
        /// For the Axial type each axis alone (1280-1370) and each axis's
        /// curve, with no sensitivity.</summary>
        public static (double X, double Y) Ds4(double x, double y, Ds4StickSettings p)
        {
            if (p.Rotation != 0)
            {
                double r = p.Rotation * Math.PI / 180.0;
                double rx = Math.Clamp(x * Math.Cos(r) - y * Math.Sin(r), -1.0, 1.0);
                double ry = Math.Clamp(x * Math.Sin(r) + y * Math.Cos(r), -1.0, 1.0);
                x = rx;
                y = ry;
            }
            if (p.Axial)
            {
                double ax = AxialAxis(x, p.Dead, p.Anti, p.MaxZone, p.MaxOutput);
                double ay = AxialAxis(y, p.DeadY, p.AntiY, p.MaxZoneY, p.MaxOutputY);
                return (Math.Sign(ax) * Curve(p.Curve, Math.Abs(ax)), Math.Sign(ay) * Curve(p.Curve, Math.Abs(ay)));
            }

            double outX = x, outY = y;
            double d = Math.Clamp(p.Dead, 0, 127) / 127.0;
            bool interpret = p.Anti > 0 || p.MaxZone != 100 || p.MaxOutput != 100 || p.MaxOutputForce || p.VerticalScale != 100;
            if (d > 0 || interpret)
            {
                double rho = Math.Sqrt(x * x + y * y);
                if (d > 0 && rho <= d)
                {
                    outX = outY = 0.0;
                }
                else
                {
                    double angle = Math.Atan2(y, x);
                    double c = Math.Abs(Math.Cos(angle)), n = Math.Abs(Math.Sin(angle));
                    double m = Math.Clamp(p.MaxZone, 0, 100) / 100.0;
                    double tx, ty;
                    if (d > 0)
                    {
                        tx = (Math.Min(Math.Abs(x), m) - d * c) / (m - d * c);
                        ty = (Math.Min(Math.Abs(y), m) - d * n) / (m - d * n);
                    }
                    else
                    {
                        tx = Math.Min(Math.Abs(x), m) / m;
                        ty = Math.Min(Math.Abs(y), m) / m;
                    }
                    if (p.VerticalScale != 100)
                        ty = Math.Min(Math.Max(ty * p.VerticalScale / 100.0, 0.0), 1.0);
                    if (p.MaxOutput != 100 || p.MaxOutputForce)
                    {
                        double cap = Math.Clamp(p.MaxOutput, 0, 100) / 100.0;
                        tx = Math.Min(Math.Max(tx, 0.0), Math.Min(c * cap / 0.99, 1.0));
                        ty = Math.Min(Math.Max(ty, 0.0), Math.Min(n * cap / 0.99, 1.0));
                    }
                    double a = p.Anti > 0 ? Math.Min(p.Anti, 100) * 0.01 : 0.0;
                    outX = tx > 0.0 ? Math.Sign(x) * ((1.0 - a * c) * tx + a * c) : 0.0;
                    outY = ty > 0.0 ? Math.Sign(y) * ((1.0 - a * n) * ty + a * n) : 0.0;
                }
            }
            if (p.Sens != 1.0)
            {
                outX = Math.Clamp(p.Sens * outX, -1.0, 1.0);
                outY = Math.Clamp(p.Sens * outY, -1.0, 1.0);
            }
            if (p.Curve != "linear" && (outX != 0 || outY != 0))
            {
                // capX is the push's share of X at its angle, raised to the
                // axis itself past the unit circle, so the ratio is the
                // processed length, capped at 1.
                double angle = Math.Atan2(outY, outX);
                double capX = Math.Max(Math.Abs(Math.Cos(angle)), Math.Abs(outX));
                double capY = Math.Max(Math.Abs(Math.Sin(angle)), Math.Abs(outY));
                double ratioX = capX > 0 ? Math.Abs(outX) / capX : 0;
                double ratioY = capY > 0 ? Math.Abs(outY) / capY : 0;
                outX = Math.Sign(outX) * Curve(p.Curve, ratioX) * capX;
                outY = Math.Sign(outY) * Curve(p.Curve, ratioY) * capY;
            }
            return (outX, outY);
        }

        /// <summary>One axis of the Axial type (Mapping.cs 1280-1330).</summary>
        private static double AxialAxis(double v, double deadRaw, double antiRaw, double maxZone, double maxOutput)
        {
            if (!(deadRaw > 0 || antiRaw > 0 || maxZone != 100 || maxOutput != 100)) return v;
            double dead = Math.Clamp(deadRaw, 0, 127) / 127.0;
            if (deadRaw > 0 && Math.Abs(v) <= dead) return 0.0;
            double m = Math.Clamp(maxZone, 0, 100) / 100.0;
            double o = (Math.Min(Math.Abs(v), m) - dead) / (m - dead);
            if (maxOutput != 100)
                o = Math.Min(Math.Max(o, 0.0), Math.Min(Math.Clamp(maxOutput, 0, 100) / 100.0 / 0.99, 1.0));
            double a = antiRaw > 0 ? Math.Min(antiRaw, 100) * 0.01 : 0.0;
            return o > 0.0 ? Math.Sign(v) * ((1.0 - a) * o + a) : 0.0;
        }

        /// <summary>Mapping.cs's stick curves on a ratio of 0..1 (1801-1863).</summary>
        public static double Curve(string mode, double r) => mode switch
        {
            "enhanced-precision" => r <= 0.4 ? 0.8 * r : r <= 0.75 ? r - 0.08 : r * 1.32 - 0.32,
            "quadratic" => r * r,
            "cubic" => r * r * r,
            "easeout-quad" => -r * (r - 2.0),
            "easeout-cubic" => (r - 1.0) * (r - 1.0) * (r - 1.0) + 1.0,
            _ => r,
        };

        private static readonly MethodInfo Tuning = typeof(InputManager)
            .GetMethod("ApplyPadSettingTuning", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>PadForge's output for the imported left or right stick,
        /// as a function of the push. The profile itself is left
        /// untouched.</summary>
        public static Func<double, double, (double X, double Y)> Played(ProfileData profile, bool left)
            => Played(profile.SlotMappingSets, left);

        /// <summary>The same for a slot's mapping sets, the first of which
        /// lands on the device, read through the rows of
        /// <paramref name="layer"/>.</summary>
        public static Func<double, double, (double X, double Y)> Played(MappingSet[] imported, bool left, string layer = "Base")
        {
            var sets = Clone(imported);
            var saved = SettingsManager.SlotMappingSets;
            var ps = new PadSetting();
            try
            {
                SettingsManager.SlotMappingSets = sets;
                WorkshopTuningApplier.ApplyToAssignedDevice(0, ps, Guid.NewGuid().ToString());
            }
            finally
            {
                SettingsManager.SlotMappingSets = saved;
            }
            string side = left ? "Left" : "Right";
            var rowX = sets[0].Rows.Single(r => r.Target == side + "ThumbAxisX" && r.LayerMask == layer);
            var rowY = sets[0].Rows.Single(r => r.Target == side + "ThumbAxisY" && r.LayerMask == layer);
            var srcX = Saved(rowX);
            var srcY = Saved(rowY);
            int ix = left ? 0 : 3, iy = left ? 1 : 4;
            return (x, y) =>
            {
                var state = new CustomInputState();
                Array.Fill(state.Axis, 32768);
                state.Axis[ix] = 32768 + (int)Math.Round(Math.Clamp(x, -1.0, 1.0) * 32767.0);
                state.Axis[iy] = 32768 + (int)Math.Round(Math.Clamp(y, -1.0, 1.0) * 32767.0);
                float vx = Math.Clamp(CombineHelper.CombineAxis(rowX.CombineMode,
                    srcX.Select(s => SourceCoercion.EvaluateForBipolarAxisTarget(state, s)).ToList()), -1f, 1f);
                float vy = Math.Clamp(CombineHelper.CombineAxis(rowY.CombineMode,
                    srcY.Select(s => SourceCoercion.EvaluateForBipolarAxisTarget(state, s)).ToList()), -1f, 1f);
                // InputManager.WriteBipolarAxisTarget: Y down on the rows is
                // minus on the pad.
                short sx = (short)Math.Clamp((int)(vx * 32767f), -32768, 32767);
                short sy = (short)Math.Clamp((int)(-vy * 32767f), -32768, 32767);
                var gp = new Gamepad();
                if (left) { gp.ThumbLX = sx; gp.ThumbLY = sy; }
                else { gp.ThumbRX = sx; gp.ThumbRY = sy; }
                object[] args = { gp, ps, -1 };
                Tuning.Invoke(null, args);
                gp = (Gamepad)args[0];
                return left ? (gp.ThumbLX / 32767.0, -gp.ThumbLY / 32767.0)
                            : (gp.ThumbRX / 32767.0, -gp.ThumbRY / 32767.0);
            };
        }

        private static readonly MethodInfo Capture = typeof(SettingsService)
            .GetMethod("CaptureCurveRangeParams", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo Restamp = typeof(SettingsService)
            .GetMethod("ApplyCurveRangeParamsToRow", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>A row's sources after the editor's save: each source
        /// through its row item (MappingSourceItem.FromDomain, ToDomain),
        /// then the stamps the editor has no card for put back by
        /// SettingsService's own capture and re-stamp, as the save does.</summary>
        public static System.Collections.Generic.List<MappingSource> Saved(MappingRow row)
        {
            object preserved = Capture.Invoke(null, new object[] { row });
            var rebuilt = new MappingRow { Target = row.Target, LayerMask = row.LayerMask, CombineMode = row.CombineMode };
            rebuilt.Sources.AddRange(row.Sources.Select(s => MappingSourceItem.FromDomain(s).ToDomain()));
            Restamp.Invoke(null, new[] { rebuilt, preserved });
            return rebuilt.Sources;
        }

        private static MappingSet[] Clone(MappingSet[] sets)
        {
            var serializer = new XmlSerializer(typeof(MappingSet[]));
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            serializer.Serialize(writer, sets);
            using var reader = new StringReader(writer.ToString());
            return (MappingSet[])serializer.Deserialize(reader);
        }
    }
}
