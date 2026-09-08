using System;
using System.Collections.Concurrent;
using System.Globalization;
using PadForge.Engine.Data;

namespace PadForge.Engine
{
    /// <summary>Continuous rumble from a mapped virtual stick axis.</summary>
    public static class SteeringAngleRumble
    {
        public static long Pack(Gamepad state)
            => unchecked((long)((ulong)(ushort)state.ThumbLX
                | ((ulong)(ushort)state.ThumbLY << 16)
                | ((ulong)(ushort)state.ThumbRX << 32)
                | ((ulong)(ushort)state.ThumbRY << 48)));

        public static short Axis(long frame, int axis)
            => axis is >= 0 and <= 3 ? unchecked((short)(frame >> (axis * 16))) : (short)0;

        public static void Compute(long frame, PadSetting ps, out ushort left, out ushort right)
        {
            left = right = 0;
            if (ps?.SteeringAngleRumbleEnabled != "1") return;
            short value = Axis(frame, Parse(ps.SteeringAngleRumbleAxis, 0));
            int strength = Math.Clamp(Parse(ps.SteeringAngleRumbleStrength, 50), 0, 100);
            double deadzone = Math.Clamp(Parse(ps.SteeringAngleRumbleDeadzone, 2), 0, 25) / 100.0;
            double travel = value < 0 ? -(double)value / 32768.0 : value / 32767.0;
            double level = Math.Max(0, (travel - deadzone) / (1 - deadzone));
            ushort motor = (ushort)Math.Round(level * strength / 100.0 * ushort.MaxValue);
            if (value < 0) left = motor;
            else right = motor;
        }

        /// <summary>Max-combines the cue without modifying shared input. Scratch
        /// can alias raw only when the caller owns that raw object.</summary>
        public static Vibration Merge(Vibration raw, PadSetting ps, long frame, Vibration scratch)
        {
            if (raw == null || scratch == null) return raw;
            Compute(frame, ps, out ushort left, out ushort right);
            if (left == 0 && right == 0) return raw;
            scratch.LeftMotorSpeed = Math.Max(raw.LeftMotorSpeed, left);
            scratch.RightMotorSpeed = Math.Max(raw.RightMotorSpeed, right);
            scratch.LeftTriggerMotorSpeed = raw.LeftTriggerMotorSpeed;
            scratch.RightTriggerMotorSpeed = raw.RightTriggerMotorSpeed;
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

        private static readonly ConcurrentDictionary<string, int> s_numbers = new(StringComparer.Ordinal);

        private static int Parse(string text, int fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            if (s_numbers.TryGetValue(text, out int number)) return number;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return fallback;
            if (s_numbers.Count < 4096) s_numbers.TryAdd(text, number);
            return number;
        }
    }
}
