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
                // Steering kinds (v3.4 #94): read a whole 2D stick (X = Descriptor,
                // Y = ParamYDescriptor) or gravity, and project to one virtual-stick
                // channel. The row's target picks the channel; the Kind picks the math.
                case "WindingStick":
                {
                    if (runtime == null) return 0f;
                    double v = runtime.TickWindingStick(slotIndex, target, sourceIndex, src, state, frameDeltaSeconds);
                    return src.Invert ? -(float)v : (float)v;
                }
                case "AngleToAxisX":
                {
                    if (runtime == null) return 0f;
                    double v = runtime.TickAngleToAxis(slotIndex, target, sourceIndex, src, state, isX: true);
                    return src.Invert ? -(float)v : (float)v;
                }
                case "AngleToAxisY":
                {
                    if (runtime == null) return 0f;
                    double v = runtime.TickAngleToAxis(slotIndex, target, sourceIndex, src, state, isX: false);
                    return src.Invert ? -(float)v : (float)v;
                }
                case "MotionLeanX":
                {
                    if (runtime == null) return 0f;
                    double v = runtime.TickMotionLean(slotIndex, target, sourceIndex, src, state, src.DeviceGuid);
                    return src.Invert ? -(float)v : (float)v;
                }
                default:
                {
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
                    float v = SourceCoercion.EvaluateForBipolarAxisTarget(state, src, slotIndex, relativeTouchpad);
                    // Per-axis-frame sign correction — see ShouldFlipForAxisFrame.
                    // This is a SHARED seam (sticks, extended axes, KBM mouse,
                    // and the touchpad-output passthrough all reach it), so the
                    // sign rules MUST stay keyed on (source, target) there.
                    if (ShouldFlipForAxisFrame(src, target))
                        v = -v;
                    return v;
                }
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

        /// <summary>
        /// Sign correction for one (source, absolute-axis target) pairing, applied
        /// AFTER <see cref="SourceCoercion.EvaluateForBipolarAxisTarget"/>.
        ///
        /// <para>The bipolar coercion returns each source in its OWN natural frame,
        /// which is not always the frame the destination axis expects. Two confirmed
        /// mismatches live here. Both were verified against the JoyShockMapper
        /// reference AND by on-device testing — DO NOT collapse, widen, or "simplify"
        /// either branch without re-testing on hardware. Getting a sign wrong here is
        /// a silent, ship-breaking regression: the control still moves, just the wrong
        /// way, and nothing downstream flags it.</para>
        ///
        /// <list type="number">
        /// <item><b>Gyro yaw / roll / horizontal → stick.</b> The gyro reports a
        ///   right-hand-rule angular RATE; a leftward twist lands on +X. A stick is a
        ///   position, so it must deflect TOWARD the twist (twist left → stick left).
        ///   Flip. <b>Pitch is excluded</b> — nose-up already reads as stick-down,
        ///   matching a flight stick's pull-back and JSM's
        ///   <c>processGyroStick</c>, which emits <c>setStick(gyroStickX, -gyroStickY)</c>
        ///   (JoyShock.cpp). Flipping pitch would invert that and is wrong.</item>
        /// <item><b>Touchpad finger Y → stick Y.</b> The absolute touchpad reader keeps
        ///   SDL's "raw_y = 0 at the TOP" convention (correct for touchpad→touchpad
        ///   passthrough, commit 02b4315), so finger-at-top reads −1. A stick expects
        ///   up = +1, so a touchpad mapped as a joystick reads upside-down on Y. Flip,
        ///   but ONLY for the stick Y targets — the passthrough targets
        ///   (<c>TouchpadY1/2</c>) and the KBM mouse path keep SDL / aim conventions
        ///   and must NOT be flipped. X is already correct (finger-left → stick-left).</item>
        /// </list>
        ///
        /// Anything not matched here passes through unchanged.
        /// </summary>
        private static bool ShouldFlipForAxisFrame(MappingSource src, string target)
        {
            if (src == null || string.IsNullOrEmpty(target)) return false;
            string desc = src.Descriptor ?? "";

            // (1) Gyro → absolute stick / extended axis, horizontal family only.
            if (!IsRelativeMotionTarget(target) && SourceCoercion.IsGyroDescriptor(desc))
                return !desc.Trim().Equals("Gyro Pitch", StringComparison.OrdinalIgnoreCase);

            // (2) Touchpad finger Y → stick Y axis (not passthrough TouchpadY*, not mouse).
            if ((target == "LeftThumbAxisY" || target == "RightThumbAxisY")
                && desc.StartsWith("Touchpad ", StringComparison.Ordinal)
                && desc.TrimEnd().EndsWith(" Y", StringComparison.Ordinal))
                return true;

            return false;
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
