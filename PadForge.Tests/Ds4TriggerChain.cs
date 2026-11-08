using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>A DS4Windows trigger two ways: as Mapping.cs computes it, and
    /// as PadForge plays the imported profile. The second runs the real fold
    /// onto the device's Triggers card, the row's source after the editor's
    /// save round trip, the row read, and the card in
    /// InputManager.ApplyTriggerDeadZone. Callers sit in the
    /// SettingsManagerStatics collection, since the fold reads
    /// SettingsManager.SlotMappingSets.</summary>
    internal static class Ds4TriggerChain
    {
        /// <summary>Mapping.cs on a pull of 0..1: the dead zone (0..255) and
        /// max zone, the max output and the anti-dead zone (1615-1663), the
        /// sensitivity clamped to full (1743-1749), then the curve, which
        /// leaves a released trigger at 0 (2045-2080).</summary>
        public static double Ds4(double t, double dead, double anti, double maxZone, double maxOut, double sens, Func<double, double> curve)
        {
            double l2 = t * 255.0;
            double o = t;
            if (dead > 0 || anti > 0 || maxZone != 100 || maxOut != 100)
            {
                double maxValue = 255.0 * maxZone / 100.0;
                double v;
                if (dead > 0) v = l2 > dead ? (Math.Clamp(l2, 0.0, maxValue) - dead) / (maxValue - dead) : 0.0;
                else v = Math.Clamp(l2, 0.0, maxValue) / maxValue;
                if (maxOut != 100) v = Math.Min(Math.Max(v, 0.0), maxOut / 100.0);
                double a = anti > 0 ? anti * 0.01 : 0.0;
                o = v > 0.0 ? (1.0 - a) * v + a : 0.0;
            }
            o = Math.Min(1.0, o * sens);
            return o > 0.0 && curve != null ? curve(o) : o;
        }

        /// <summary>PadForge's output for the imported row of
        /// <paramref name="target"/> (LeftTrigger or RightTrigger), as a
        /// function of the pull. The profile itself is left untouched.</summary>
        public static Func<double, double> Played(ProfileData profile, string target)
        {
            var sets = Clone(profile.SlotMappingSets);
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
            var row = sets[0].Rows.Single(r => r.Target == target && r.LayerMask == "Base");
            var src = MappingSourceItem.FromDomain(row.Sources[0]).ToDomain();
            bool left = target == "LeftTrigger";
            double dz = Num(left ? ps.LeftTriggerDeadZone : ps.RightTriggerDeadZone, 0);
            double adz = Num(left ? ps.LeftTriggerAntiDeadZone : ps.RightTriggerAntiDeadZone, 0);
            double max = Num(left ? ps.LeftTriggerMaxRange : ps.RightTriggerMaxRange, 100);
            double[] lut = global::PadForge.Common.CurveLut.GetOrBuild(left ? ps.LeftTriggerSensitivityCurve : ps.RightTriggerSensitivityCurve);
            int axis = left ? 2 : 5;
            return t =>
            {
                var state = new CustomInputState();
                state.Axis[axis] = (int)Math.Round(t * 65535.0);
                float v = SourceCoercion.EvaluateForTriggerTarget(state, src);
                ushort pulled = (ushort)Math.Round(Math.Clamp(v, 0f, 1f) * 65535.0);
                return InputManager.ApplyTriggerDeadZone(pulled, dz, adz, max, lut) / 65535.0;
            };
        }

        private static MappingSet[] Clone(MappingSet[] sets)
        {
            var serializer = new XmlSerializer(typeof(MappingSet[]));
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            serializer.Serialize(writer, sets);
            using var reader = new StringReader(writer.ToString());
            return (MappingSet[])serializer.Deserialize(reader);
        }

        private static double Num(string text, double fallback)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }
}
