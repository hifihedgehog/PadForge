using System;
using PadForge.Engine.Data;

namespace PadForge.Engine.Common.Mapping
{
    /// <summary>
    /// Top-level per-source evaluator that dispatches by
    /// <see cref="MappingSource.Kind"/>. The combine layer in
    /// <c>InputManager.Step3.MappingSetEval</c> calls these methods
    /// per source per row per frame.
    ///
    /// <para>
    /// Direct: delegates to <see cref="SourceCoercion"/>.
    /// Incremental: ticks <see cref="SourceKindRuntime"/> and clamps
    /// the accumulator into the target's natural range.
    /// InvertOnHold: reads the inner descriptor via SourceCoercion with
    /// <see cref="MappingSource.Invert"/> XOR'd with the modifier
    /// button's current state.
    /// </para>
    /// </summary>
    public static class SourceEvaluator
    {
        public static bool EvaluateForButtonTarget(
            CustomInputState state, MappingSource src,
            int globalThresholdPercent,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds)
        {
            if (src == null) return false;

            switch (src.Kind ?? "Direct")
            {
                case "Incremental":
                {
                    if (runtime == null) return false;
                    double v = runtime.TickIncremental(slotIndex, target, sourceIndex,
                        src, state, frameDeltaSeconds);
                    bool result = v > 0.5;
                    return src.Invert ? !result : result;
                }
                case "InvertOnHold":
                {
                    bool modifier = ReadButtonLikeBool(state, src.ParamModifier);
                    var inner = CloneAsDirect(src, invertOverride: src.Invert ^ modifier);
                    return SourceCoercion.EvaluateForButtonTarget(state, inner, globalThresholdPercent, slotIndex);
                }
                default: // Direct
                    return SourceCoercion.EvaluateForButtonTarget(state, src, globalThresholdPercent, slotIndex);
            }
        }

        public static float EvaluateForBipolarAxisTarget(
            CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds)
        {
            if (src == null) return 0f;

            // Touchpad source readings differ between relative-motion
            // targets (KBM mouse / scroll consume per-frame deltas) and
            // absolute-position targets (touchpad-output passthrough,
            // stick axes, extended axes — all want raw pad position).
            // SourceCoercion has both readers; the flag picks which one
            // it uses for touchpad descriptors.
            bool relativeTouchpad = IsRelativeMotionTarget(target);

            switch (src.Kind ?? "Direct")
            {
                case "Incremental":
                {
                    if (runtime == null) return 0f;
                    double v = runtime.TickIncremental(slotIndex, target, sourceIndex,
                        src, state, frameDeltaSeconds);
                    if (v < -1) v = -1;
                    if (v > 1) v = 1;
                    return src.Invert ? -(float)v : (float)v;
                }
                case "InvertOnHold":
                {
                    bool modifier = ReadButtonLikeBool(state, src.ParamModifier);
                    var inner = CloneAsDirect(src, invertOverride: src.Invert ^ modifier);
                    return SourceCoercion.EvaluateForBipolarAxisTarget(state, inner, slotIndex, relativeTouchpad);
                }
                default:
                    // Gyro → virtual stick is rate-direct, same as gyro →
                    // mouse / scroll: instantaneous angular rate (post-tuning)
                    // maps to stick deflection magnitude. Stop tilting and
                    // the stick recenters; the camera ends up rotated by
                    // the integral of stick deflection over time, which the
                    // game's own stick-to-camera curve handles. The earlier
                    // "integrate angular rate into stick position" path
                    // produced sustained deflection that read as
                    // "hold the controller tilted to keep turning" — the
                    // opposite of how gyro is supposed to feel (JSM
                    // MOUSE_JOYSTICK, Steam Input gyro→stick, Splatoon).
                    return SourceCoercion.EvaluateForBipolarAxisTarget(state, src, slotIndex, relativeTouchpad);
            }
        }

        /// <summary>True when the target name names a relative-motion
        /// channel — the only ones in PadForge are KBM mouse X/Y and
        /// scroll. Stick axes, touchpad output passthrough, and
        /// extended-config axes are absolute-position; for them a
        /// touchpad source should read raw pad position, not deltas.</summary>
        internal static bool IsRelativeMotionTarget(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            return target == "KbmMouseX"
                || target == "KbmMouseY"
                || target == "KbmScroll";
        }

        public static float EvaluateForTriggerTarget(
            CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds)
        {
            if (src == null) return 0f;

            switch (src.Kind ?? "Direct")
            {
                case "Incremental":
                {
                    if (runtime == null) return 0f;
                    double v = runtime.TickIncremental(slotIndex, target, sourceIndex,
                        src, state, frameDeltaSeconds);
                    if (v < 0) v = 0;
                    if (v > 1) v = 1;
                    return src.Invert ? 1f - (float)v : (float)v;
                }
                case "InvertOnHold":
                {
                    bool modifier = ReadButtonLikeBool(state, src.ParamModifier);
                    var inner = CloneAsDirect(src, invertOverride: src.Invert ^ modifier);
                    return SourceCoercion.EvaluateForTriggerTarget(state, inner, slotIndex);
                }
                default:
                    return SourceCoercion.EvaluateForTriggerTarget(state, src, slotIndex);
            }
        }

        // Builds a shallow copy of <paramref name="src"/> with Kind forced
        // to Direct and the specified Invert. Lets InvertOnHold reuse
        // SourceCoercion's coercion table without mutating the original.
        private static MappingSource CloneAsDirect(MappingSource src, bool invertOverride)
            => new MappingSource
            {
                Kind = "Direct",
                DeviceGuid = src.DeviceGuid,
                Descriptor = src.Descriptor,
                Invert = invertOverride,
                HalfAxis = src.HalfAxis,
                Bidirectional = src.Bidirectional,
                DeadZone = src.DeadZone,
            };

        // Mirrors SourceKindRuntime's button-like reader so the
        // InvertOnHold modifier-button check stays consistent with
        // Incremental's up/down inputs.
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
