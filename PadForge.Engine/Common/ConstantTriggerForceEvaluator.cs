using System;
using System.Globalization;
using PadForge.Engine.Data;

namespace PadForge.Engine
{
    /// <summary>
    /// Trigger-motor analogue of <see cref="ConstantForceEvaluator"/>:
    /// when game-driven trigger motor speeds are silent AND the device's
    /// PadSetting has constant-trigger-force enabled, fills the
    /// <c>LeftTriggerMotorSpeed</c> and <c>RightTriggerMotorSpeed</c>
    /// fields of <paramref name="scratch"/> from
    /// <see cref="PadSetting.ConstantTriggerForceLeft"/> and
    /// <see cref="PadSetting.ConstantTriggerForceRight"/>. Otherwise
    /// returns <paramref name="raw"/> unchanged. Override-with-resume
    /// semantics: any non-zero game trigger pauses the constant force;
    /// the next silent tick brings it back.
    /// </summary>
    public static class ConstantTriggerForceEvaluator
    {
        public static Vibration Resolve(Vibration raw, PadSetting ps, Vibration scratch)
        {
            if (raw == null || ps == null || scratch == null) return raw;

            bool gameTrigger = raw.LeftTriggerMotorSpeed != 0
                            || raw.RightTriggerMotorSpeed != 0;
            if (gameTrigger) return raw;

            if (!IsEnabled(ps.ConstantTriggerForceEnabled)) return raw;

            double left = ParseNorm(ps.ConstantTriggerForceLeft);
            double right = ParseNorm(ps.ConstantTriggerForceRight);
            if (left <= 0.0 && right <= 0.0) return raw;

            ushort leftMotor = (ushort)Math.Clamp((int)Math.Round(left * 65535.0), 0, 65535);
            ushort rightMotor = (ushort)Math.Clamp((int)Math.Round(right * 65535.0), 0, 65535);

            // Copy raw main-motor / directional fields so the constant
            // trigger force composes with any constant main-motor force
            // that already populated scratch upstream.
            scratch.LeftMotorSpeed = raw.LeftMotorSpeed;
            scratch.RightMotorSpeed = raw.RightMotorSpeed;
            scratch.LeftTriggerMotorSpeed = leftMotor;
            scratch.RightTriggerMotorSpeed = rightMotor;
            scratch.HasDirectionalData = raw.HasDirectionalData;
            scratch.HasConditionData = raw.HasConditionData;
            scratch.EffectType = raw.EffectType;
            scratch.SignedMagnitude = raw.SignedMagnitude;
            scratch.Direction = raw.Direction;
            scratch.Period = raw.Period;
            scratch.DeviceGain = raw.DeviceGain;
            scratch.ConditionAxisCount = raw.ConditionAxisCount;
            scratch.ConditionAxes = raw.ConditionAxes;
            return scratch;
        }

        private static bool IsEnabled(string s)
            => s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);

        private static double ParseNorm(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0.0;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return 0.0;
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0.0;
            return Math.Clamp(v, 0.0, 1.0);
        }
    }
}
