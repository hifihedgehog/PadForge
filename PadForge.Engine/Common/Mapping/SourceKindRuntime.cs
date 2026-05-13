using System;
using System.Collections.Generic;
using PadForge.Engine.Data;

namespace PadForge.Engine.Common.Mapping
{
    /// <summary>
    /// Per-VC runtime state for stateful source kinds (Incremental).
    /// Lives on the polling-thread side of Step 3; cleared on profile
    /// switch and on app restart.
    ///
    /// <para>
    /// Stateless source kinds (Direct, InvertOnHold) do not allocate
    /// state here — their per-frame value is a pure function of the
    /// inputs and the modifier descriptor.
    /// </para>
    /// </summary>
    public sealed class SourceKindRuntime
    {
        // Keyed by (slotIndex, target, sourceIndex). MappingRow is a DTO
        // with no stable identity, so we key by row position. On row
        // reorder the user's Incremental accumulator survives because
        // Target+sourceIndex is what most users edit incrementally; on
        // wholesale row removal the state lingers harmlessly until the
        // dictionary is cleared (profile switch / engine stop).
        private readonly Dictionary<(int slot, string target, int srcIdx), double> _incrementalAccum
            = new();

        // Per-(slot, target, sourceIndex) gyro-integrated stick accumulator
        // in normalized stick units [-1..+1]. Separate from
        // _incrementalAccum because the integration step is gyro-rate × dt ×
        // sensitivity, not the unit-range/sec ramp Incremental uses, and the
        // valid range is fixed bipolar instead of user-configurable.
        private readonly Dictionary<(int slot, string target, int srcIdx), double> _gyroIntegratedAccum
            = new();

        /// <summary>Drops all state. Called on profile switch and engine
        /// stop. Cruise control snaps to neutral on next read; gyro-stick
        /// accumulators snap to center.</summary>
        public void Clear()
        {
            _incrementalAccum.Clear();
            _gyroIntegratedAccum.Clear();
        }

        /// <summary>Zeroes the gyro-stick accumulator for this row only.
        /// Wired to a future "Recenter Gyro" macro/key. Slot-scoped: a
        /// recenter on slot 0 leaves slot 1's accumulator alone.</summary>
        public void RecenterGyroIntegrated(int slotIndex, string target, int sourceIndex)
        {
            var key = (slotIndex, target ?? "", sourceIndex);
            if (_gyroIntegratedAccum.ContainsKey(key))
                _gyroIntegratedAccum[key] = 0;
        }

        /// <summary>Zeroes every gyro-stick accumulator on the given slot.
        /// Cheaper authoring path for "recenter all sticks" intent without
        /// having to enumerate target / sourceIndex pairs.</summary>
        public void RecenterAllGyroIntegrated(int slotIndex)
        {
            // Two-pass to avoid mutating the dict while iterating.
            List<(int, string, int)> keys = null;
            foreach (var k in _gyroIntegratedAccum.Keys)
            {
                if (k.slot != slotIndex) continue;
                (keys ??= new()).Add(k);
            }
            if (keys != null)
                foreach (var k in keys)
                    _gyroIntegratedAccum[k] = 0;
        }

        /// <summary>
        /// Integrates a gyro source's calibrated angular rate over time
        /// into a virtual stick deflection, clamped to [-1..+1]. Use only
        /// for stick targets — mouse/scroll targets want the rate directly,
        /// not the integral.
        ///
        /// <para>Caller passes the already-bias-subtracted rate (rad/s);
        /// runtime applies <see cref="MappingSource.GyroSensitivity"/> and
        /// dt here so the integration step is identical regardless of
        /// where the bias subtraction happened. No decay / auto-recenter
        /// — physical gyro reports angular velocity, so the accumulator's
        /// natural rest is "wherever the user last twisted to." The user
        /// recenters via opposite-direction motion or a (forthcoming)
        /// Recenter Gyro macro.</para>
        /// </summary>
        public double TickGyroIntegrated(
            int slotIndex,
            string target,
            int sourceIndex,
            double calibratedRateRadPerSec,
            double sensitivity,
            double frameDeltaSeconds)
        {
            var key = (slotIndex, target ?? "", sourceIndex);
            _gyroIntegratedAccum.TryGetValue(key, out double v);

            if (sensitivity <= 0) sensitivity = 1.0;
            if (frameDeltaSeconds < 0) frameDeltaSeconds = 0;

            // GyroScale (~1 / (500°/s in rad)) maps a 500°/s twist to a
            // full ±1 of "unit deflection per second." Sensitivity scales
            // around that; dt turns it into a per-frame increment.
            const double GyroScale = 1.0 / (500.0 * Math.PI / 180.0);
            v += calibratedRateRadPerSec * GyroScale * sensitivity * frameDeltaSeconds;
            if (v < -1) v = -1;
            else if (v > 1) v = 1;

            _gyroIntegratedAccum[key] = v;
            return v;
        }

        /// <summary>
        /// Updates the Incremental accumulator for this source and returns
        /// the per-frame contribution (already in the source kind's
        /// configured range — unipolar [ParamMin, ParamMax]).
        /// </summary>
        public double TickIncremental(
            int slotIndex,
            string target,
            int sourceIndex,
            MappingSource src,
            CustomInputState state,
            double frameDeltaSeconds)
        {
            if (src == null || state == null) return 0;
            var key = (slotIndex, target ?? "", sourceIndex);
            _incrementalAccum.TryGetValue(key, out double current);

            // Clamp to declared range (handles user re-narrowing the range
            // mid-session).
            if (current < src.ParamMin) current = src.ParamMin;
            if (current > src.ParamMax) current = src.ParamMax;

            bool up = ReadButtonLikeBool(state, src.ParamUp);
            bool down = ReadButtonLikeBool(state, src.ParamDown);

            double rate = src.ParamRate;
            if (rate < 0) rate = 0;

            double range = src.ParamMax - src.ParamMin;
            if (range <= 0) range = 1.0;

            // Step in units-of-output-range per second; e.g. rate=0.5
            // sweeps the full range in 2 s.
            double step = rate * range * frameDeltaSeconds;

            if (up && !down)
            {
                current += step;
                if (current > src.ParamMax) current = src.ParamMax;
            }
            else if (down && !up)
            {
                current -= step;
                if (current < src.ParamMin) current = src.ParamMin;
            }
            else if (!up && !down && !src.ParamSticky)
            {
                // Non-sticky: snap to ParamMin when neither held.
                current = src.ParamMin;
            }
            // else: held opposite or both held / both released sticky →
            // hold last value.

            _incrementalAccum[key] = current;
            return current;
        }

        // Reads a button-like descriptor (Button N or POV N Dir) from a
        // CustomInputState. No deadzone handling here — Incremental's up
        // and down inputs are bool intent buttons; analog inputs aren't a
        // sensible up/down trigger for an accumulator.
        private static bool ReadButtonLikeBool(CustomInputState state, string descriptor)
        {
            if (state == null || string.IsNullOrWhiteSpace(descriptor)) return false;
            string s = descriptor.Trim();

            if (s.StartsWith("Button ", StringComparison.Ordinal))
            {
                if (int.TryParse(s.Substring(7), out int idx) &&
                    idx >= 0 && idx < state.Buttons.Length)
                    return state.Buttons[idx];
                return false;
            }

            if (s.StartsWith("POV ", StringComparison.Ordinal))
            {
                var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[1], out int povIdx) &&
                    povIdx >= 0 && povIdx < state.Povs.Length)
                {
                    int v = state.Povs[povIdx];
                    if (v < 0) return false;
                    int n = ((v % 36000) + 36000) % 36000;
                    return parts[2].ToLowerInvariant() switch
                    {
                        "up"    => n >= 31500 || n <= 4500,
                        "right" => n >= 4500 && n <= 13500,
                        "down"  => n >= 13500 && n <= 22500,
                        "left"  => n >= 22500 && n <= 31500,
                        _       => false,
                    };
                }
                return false;
            }

            return false;
        }
    }
}
