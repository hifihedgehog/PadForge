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

        /// <summary>Drops all state. Called on profile switch and engine
        /// stop. Cruise control snaps to neutral on next read.</summary>
        public void Clear()
        {
            _incrementalAccum.Clear();
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
