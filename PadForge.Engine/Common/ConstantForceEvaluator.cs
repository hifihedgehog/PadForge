using System;
using System.Globalization;
using PadForge.Engine.Data;

namespace PadForge.Engine
{
    /// <summary>
    /// Resolves the user-configured per-device "constant force" override
    /// against the live game-driven Vibration. When the game is silent
    /// (no scalar rumble, no directional FFB, no condition data) and the
    /// device's PadSetting has constant force enabled, synthesizes a
    /// Vibration carrying the user's X/Y as polar magnitude+direction
    /// (consumed by the haptic path) plus L/R motor magnitudes (consumed
    /// by the rumble fallback and the Sony dispatcher's per-device rumble
    /// pump). Game force always wins per slot when nonzero; on the next
    /// game-zero tick the constant force resumes.
    ///
    /// <para>One scratch <see cref="Vibration"/> per call site avoids
    /// allocating per tick; pass the same instance back in. The scratch
    /// is only mutated when the override fires — the caller can use the
    /// returned reference unconditionally.</para>
    /// </summary>
    public static class ConstantForceEvaluator
    {
        /// <summary>
        /// Returns either <paramref name="raw"/> unchanged (game force
        /// non-zero, or constant force disabled, or X/Y both zero) or
        /// <paramref name="scratch"/> mutated with the synthesized
        /// constant force.
        /// </summary>
        public static Vibration Resolve(Vibration raw, PadSetting ps, Vibration scratch)
        {
            if (raw == null || ps == null || scratch == null) return raw;

            bool gameForce = raw.LeftMotorSpeed != 0
                          || raw.RightMotorSpeed != 0
                          || raw.HasDirectionalData
                          || raw.HasConditionData;
            if (gameForce) return raw;

            if (!IsEnabled(ps.ConstantForceEnabled)) return raw;

            double x = ParseNorm(ps.ConstantForceX);
            double y = ParseNorm(ps.ConstantForceY);
            if (x == 0.0 && y == 0.0) return raw;

            double mag = Math.Sqrt(x * x + y * y);
            if (mag > 1.0) mag = 1.0;
            if (mag < 0.001) return raw;

            // HID polar convention: 0 = North (Y+ in UI), CW positive
            // through 32767. UI Y+ is up, so engine Y maps to -y here.
            // atan2(x, -y) lands 0=N, π/2=E, π=S, 3π/2=W. Wrap to [0, 2π).
            double dirRad = Math.Atan2(x, -y);
            if (dirRad < 0) dirRad += 2.0 * Math.PI;
            ushort direction = (ushort)Math.Clamp(
                (int)Math.Round(dirRad / (2.0 * Math.PI) * 32767.0),
                0, 32767);
            short signedMag = (short)Math.Clamp(
                (int)Math.Round(mag * 10000.0),
                0, 10000);

            // Rumble fallback motor mapping. Two-motor controllers carry
            // no real spatial direction; map quadrant intensity so the
            // user gets a sense of where the force is pointing on devices
            // that lack haptic FFB. |X| pushes the right (light/high-freq)
            // motor; |Y| pushes the left (heavy/low-freq) motor; each
            // axis also lifts the opposite motor by half so diagonals
            // engage both.
            double xAbs = Math.Abs(x);
            double yAbs = Math.Abs(y);
            double leftMag = Math.Min(1.0, yAbs + xAbs * 0.5);
            double rightMag = Math.Min(1.0, xAbs + yAbs * 0.5);
            ushort leftMotor = (ushort)Math.Clamp(
                (int)Math.Round(leftMag * 65535.0), 0, 65535);
            ushort rightMotor = (ushort)Math.Clamp(
                (int)Math.Round(rightMag * 65535.0), 0, 65535);

            scratch.LeftMotorSpeed = leftMotor;
            scratch.RightMotorSpeed = rightMotor;
            scratch.HasDirectionalData = true;
            scratch.HasConditionData = false;
            scratch.EffectType = FfbEffectTypes.Const;
            scratch.SignedMagnitude = signedMag;
            scratch.Direction = direction;
            scratch.Period = 0;
            scratch.DeviceGain = 255;
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
            return Math.Clamp(v, -1.0, 1.0);
        }
    }
}
