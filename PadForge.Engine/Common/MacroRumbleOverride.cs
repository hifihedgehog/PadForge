using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Per-slot ephemeral rumble override driven by the
    /// <c>MacroActionType.Rumble</c> action. Mirrors the lightbar
    /// macro override on <c>DeviceSlotConfig</c>: Reactive holds
    /// run at full strength across the hold window then fade linearly
    /// to zero across the fade window; Sticky holds at full strength
    /// until <see cref="Clear"/> runs (driven by
    /// <c>MacroActionType.RumbleStop</c>).
    ///
    /// <para>One instance per pad slot lives on <c>InputManager</c>.
    /// The macro evaluator writes to it via <see cref="FireReactive"/>
    /// or <see cref="FireSticky"/>; the FFB pipeline reads
    /// <see cref="ComputeMotors"/> at the same three injection points
    /// the constant-force evaluator uses (Step 2 ApplyForceFeedback,
    /// InputService's Sony dispatcher pump, and ComputeFinalVibrationStates
    /// for the FFB-tab activity meter), and combines with the game's
    /// raw rumble via <c>max()</c> so user-driven feedback layers on
    /// top of game force instead of suppressing it.</para>
    /// </summary>
    public sealed class MacroRumbleOverride
    {
        public enum HoldMode
        {
            /// <summary>One-shot pulse with full-strength hold + decay fade.</summary>
            Reactive = 0,
            /// <summary>Held at full strength until <see cref="Clear"/> runs.</summary>
            Sticky = 1
        }

        private DateTime _startUtc = DateTime.MinValue;
        private DateTime _holdEndUtc = DateTime.MinValue;
        private DateTime _expiresAtUtc = DateTime.MinValue;
        private HoldMode _mode = HoldMode.Reactive;
        private byte _strengthLeftPct;
        private byte _strengthRightPct;

        /// <summary>True while the override is producing non-zero
        /// motor output. Reactive holds expire at <c>_expiresAtUtc</c>;
        /// Sticky holds remain active until <see cref="Clear"/>.</summary>
        public bool IsActive
        {
            get
            {
                if (_mode == HoldMode.Sticky)
                    return _expiresAtUtc == DateTime.MaxValue;
                return DateTime.UtcNow < _expiresAtUtc;
            }
        }

        /// <summary>Fires a Reactive (decay-fade) pulse. <paramref name="leftPct"/>
        /// and <paramref name="rightPct"/> are 0..100; <paramref name="holdMs"/> is
        /// the full-strength duration; <paramref name="fadeMs"/> is the linear
        /// fade-out duration. Latches over any previous pulse.</summary>
        public void FireReactive(byte leftPct, byte rightPct, int holdMs, int fadeMs)
        {
            if (leftPct == 0 && rightPct == 0)
            {
                Clear();
                return;
            }
            if (holdMs < 0) holdMs = 0;
            if (fadeMs < 0) fadeMs = 0;
            _strengthLeftPct = leftPct > 100 ? (byte)100 : leftPct;
            _strengthRightPct = rightPct > 100 ? (byte)100 : rightPct;
            _mode = HoldMode.Reactive;
            _startUtc = DateTime.UtcNow;
            _holdEndUtc = _startUtc.AddMilliseconds(holdMs);
            _expiresAtUtc = _holdEndUtc.AddMilliseconds(fadeMs);
        }

        /// <summary>Latches a Sticky hold at full strength until
        /// <see cref="Clear"/>. Re-firing replaces the previous strength
        /// values without disturbing the active flag.</summary>
        public void FireSticky(byte leftPct, byte rightPct)
        {
            if (leftPct == 0 && rightPct == 0)
            {
                Clear();
                return;
            }
            _strengthLeftPct = leftPct > 100 ? (byte)100 : leftPct;
            _strengthRightPct = rightPct > 100 ? (byte)100 : rightPct;
            _mode = HoldMode.Sticky;
            _startUtc = DateTime.UtcNow;
            _holdEndUtc = DateTime.MaxValue;
            _expiresAtUtc = DateTime.MaxValue;
        }

        /// <summary>Releases an active Sticky hold (or short-circuits a
        /// Reactive fade-tail). Idempotent — safe to call when no
        /// override is active.</summary>
        public void Clear()
        {
            _startUtc = DateTime.MinValue;
            _holdEndUtc = DateTime.MinValue;
            _expiresAtUtc = DateTime.MinValue;
            _strengthLeftPct = 0;
            _strengthRightPct = 0;
        }

        /// <summary>Layers <paramref name="ovr"/>'s current motor values
        /// onto <paramref name="raw"/> via <c>max()</c>, returning either
        /// <paramref name="raw"/> unchanged (override inactive or
        /// producing no motor output this tick) or
        /// <paramref name="scratch"/> mutated with the merged values.
        /// Directional and condition FFB fields pass through from
        /// <paramref name="raw"/> unchanged — macro rumble is scalar-only,
        /// so haptic devices still see whatever directional info the
        /// game is driving with the macro rumble's motor magnitudes
        /// available as a fallback for non-haptic devices.
        ///
        /// <para>Used at the three FFB injection points (Step 2
        /// ApplyForceFeedback, InputService's Sony dispatcher rumble
        /// pump, and ComputeFinalVibrationStates for the meter) so the
        /// macro rumble layers identically across all rumble-bearing
        /// surfaces.</para>
        /// </summary>
        public static Vibration Merge(Vibration raw, MacroRumbleOverride ovr, Vibration scratch)
        {
            if (raw == null || ovr == null || scratch == null) return raw;
            if (!ovr.IsActive) return raw;

            ovr.ComputeMotors(out ushort macroL, out ushort macroR);
            if (macroL == 0 && macroR == 0) return raw;

            scratch.LeftMotorSpeed = Math.Max(raw.LeftMotorSpeed, macroL);
            scratch.RightMotorSpeed = Math.Max(raw.RightMotorSpeed, macroR);
            // Sibling of ConstantForceEvaluator.Resolve: every other field is
            // copied from raw below, and these two were the only omissions, so
            // an active macro rumble override silently zeroed the game's
            // impulse-trigger motors. A macro that drives the main motors makes
            // no claim on the triggers.
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

        /// <summary>Computes the current motor values (0..65535) for this
        /// override. Returns (0, 0) when inactive. Reactive holds return
        /// full strength up to <c>_holdEndUtc</c> then ramp linearly to 0
        /// across <c>[_holdEndUtc, _expiresAtUtc]</c>. Sticky holds always
        /// return full strength.</summary>
        public void ComputeMotors(out ushort leftMotor, out ushort rightMotor)
        {
            if (!IsActive)
            {
                leftMotor = 0;
                rightMotor = 0;
                return;
            }

            float intensity;
            if (_mode == HoldMode.Sticky)
            {
                intensity = 1f;
            }
            else
            {
                DateTime now = DateTime.UtcNow;
                if (now <= _holdEndUtc)
                {
                    intensity = 1f;
                }
                else
                {
                    double fadeMs = (_expiresAtUtc - _holdEndUtc).TotalMilliseconds;
                    if (fadeMs <= 0)
                    {
                        intensity = 0f;
                    }
                    else
                    {
                        double fadeElapsed = (now - _holdEndUtc).TotalMilliseconds;
                        intensity = (float)Math.Clamp(1.0 - fadeElapsed / fadeMs, 0.0, 1.0);
                    }
                }
            }

            // Convert pct (0..100) → ushort motor value (0..65535) via
            // the same /100 * 65535 scaling the rumble pipeline uses.
            // Multiply by intensity for the fade window, then clamp.
            int left = (int)Math.Round(_strengthLeftPct / 100.0 * 65535.0 * intensity);
            int right = (int)Math.Round(_strengthRightPct / 100.0 * 65535.0 * intensity);
            leftMotor = (ushort)Math.Clamp(left, 0, 65535);
            rightMotor = (ushort)Math.Clamp(right, 0, 65535);
        }
    }
}
