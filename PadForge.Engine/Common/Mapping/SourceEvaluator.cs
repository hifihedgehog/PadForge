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
        /// <summary>Per-source AND gate (v18): when
        /// <see cref="MappingSource.GateDescriptor"/> is set, the source
        /// contributes only while that second descriptor reads true on the
        /// same device state, evaluated through the exact button-like read
        /// the chord second leg uses (a synthetic Direct source into
        /// <see cref="SourceCoercion.EvaluateForButtonTarget"/>). The
        /// synthetic source is cached on the owning source and rebuilt
        /// only when the descriptor reference changes (the menu parse
        /// cache contract), so the hot path is a reference compare. The
        /// gate descriptor is canonicalized at cache-build time: a
        /// "Gamepad ..." alias gate otherwise paid the alias Substring on
        /// every tick, and the canonical form short-circuits the per-read
        /// CanonicalDescriptor to a reference pass-through. An axis-natured
        /// gate thresholds at <paramref name="thresholdPercent"/>: the
        /// button lane forwards its caller's global threshold, the axis /
        /// trigger lanes keep the 50 percent default (they carry no caller
        /// threshold of their own).</summary>
        private static bool GateHeld(CustomInputState state, MappingSource src,
            int slotIndex, string evaluatedDeviceGuid, int thresholdPercent = 50)
        {
            string gate = src.GateDescriptor;
            if (!string.IsNullOrEmpty(gate))
            {
                var cached = src.GateSourceCache;
                if (cached == null || !ReferenceEquals(src.GateSourceCacheKey, gate))
                {
                    cached = new MappingSource
                    {
                        Kind = "Direct",
                        Descriptor = SourceCoercion.CanonicalDescriptor(gate),
                        DeviceGuid = src.DeviceGuid,
                    };
                    src.GateSourceCache = cached;
                    src.GateSourceCacheKey = gate;
                }
                if (!SourceCoercion.EvaluateForButtonTarget(state, cached, thresholdPercent,
                        slotIndex, evaluatedDeviceGuid))
                    return false;
            }
            // Second AND companion (v26): a chord partner beside a spent
            // primary gate (the single-pad wedge chord). Same synthetic-
            // source cache contract as the first leg.
            string gate2 = src.Gate2Descriptor;
            if (!string.IsNullOrEmpty(gate2))
            {
                var cached2 = src.Gate2SourceCache;
                if (cached2 == null || !ReferenceEquals(src.Gate2SourceCacheKey, gate2))
                {
                    cached2 = new MappingSource
                    {
                        Kind = "Direct",
                        Descriptor = SourceCoercion.CanonicalDescriptor(gate2),
                        DeviceGuid = src.DeviceGuid,
                    };
                    src.Gate2SourceCache = cached2;
                    src.Gate2SourceCacheKey = gate2;
                }
                if (!SourceCoercion.EvaluateForButtonTarget(state, cached2, thresholdPercent,
                        slotIndex, evaluatedDeviceGuid))
                    return false;
            }
            return true;
        }

        public static bool EvaluateForButtonTarget(
            CustomInputState state, MappingSource src,
            int globalThresholdPercent,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds,
            string evaluatedDeviceGuid = null)
        {
            if (src == null) return false;
            if (!GateHeld(state, src, slotIndex, evaluatedDeviceGuid, globalThresholdPercent)) return false;

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
                case "Ramped":
                    // A ramped axis envelope has no defensible boolean reading; a
                    // button target gets nothing (issue #111). Picking a threshold
                    // here would surprise anyone who set up the row.
                    return false;
                case "InvertOnHold":
                {
                    bool modifier = ReadButtonLikeBool(state, src.ParamModifier);
                    var inner = CloneAsDirect(src, invertOverride: src.Invert ^ modifier);
                    return SourceCoercion.EvaluateForButtonTarget(state, inner, globalThresholdPercent, slotIndex, evaluatedDeviceGuid);
                }
                default: // Direct
                    return SourceCoercion.EvaluateForButtonTarget(state, src, globalThresholdPercent, slotIndex, evaluatedDeviceGuid);
            }
        }

        public static float EvaluateForBipolarAxisTarget(
            CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds,
            string evaluatedDeviceGuid = null)
        {
            if (src == null) return 0f;
            if (!GateHeld(state, src, slotIndex, evaluatedDeviceGuid)) return 0f;

            // Touchpad source readings differ between relative-motion
            // targets (KBM mouse / scroll consume per-frame deltas) and
            // absolute-position targets (touchpad-output passthrough,
            // stick axes, extended axes — all want raw pad position).
            // SourceCoercion has both readers; the flag picks which one
            // it uses for touchpad descriptors.
            bool relativeTouchpad = IsRelativeMotionTarget(target);

            // "Motion Lean" is a first-class INPUT descriptor (picked from the
            // input dropdown like "Gyro Roll"), not a steering mode stamped onto
            // a target. A plain Direct source carrying it routes into the same
            // lean math as Kind="MotionLeanX"; the row's target is whatever axis
            // the user mapped it to, and nothing overrides the stick's own input.
            string kind = src.Kind ?? "Direct";
            if (kind == "Direct" && SourceCoercion.IsMotionLeanDescriptor(src.Descriptor))
                kind = "MotionLeanX";
            else if (kind == "Direct" && SourceCoercion.IsMotionLeanAuxDescriptor(src.Descriptor))
                kind = "MotionLeanAuxX";

            switch (kind)
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
                case "Ramped":
                {
                    if (runtime == null) return 0f;
                    double v = runtime.TickRamped(slotIndex, target, sourceIndex,
                        src, state, frameDeltaSeconds);
                    if (v < -1) v = -1;
                    if (v > 1) v = 1;
                    return src.Invert ? -(float)v : (float)v;
                }
                case "InvertOnHold":
                {
                    bool modifier = ReadButtonLikeBool(state, src.ParamModifier);
                    var inner = CloneAsDirect(src, invertOverride: src.Invert ^ modifier);
                    return SourceCoercion.EvaluateForBipolarAxisTarget(state, inner, slotIndex, relativeTouchpad, evaluatedDeviceGuid);
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
                    double v = runtime.TickMotionLean(slotIndex, target, sourceIndex, src, state,
                        SourceCoercion.EffectiveDeviceGuid(src, evaluatedDeviceGuid));
                    return src.Invert ? -(float)v : (float)v;
                }
                case "MotionLeanAuxX":
                {
                    // "Motion Lean L" (#199): the same tilt math over the
                    // auxiliary (Nunchuk / left Joy-Con) gravity twin.
                    if (runtime == null) return 0f;
                    double v = runtime.TickMotionLean(slotIndex, target, sourceIndex, src, state,
                        SourceCoercion.EffectiveDeviceGuid(src, evaluatedDeviceGuid), aux: true);
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
                    float v = SourceCoercion.EvaluateForBipolarAxisTarget(state, src, slotIndex, relativeTouchpad, evaluatedDeviceGuid);
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
        /// which is not always the frame the destination axis expects. This helper
        /// owns exactly ONE correction. DO NOT widen it without re-testing on
        /// hardware: a sign wrong here is a silent, ship-breaking regression — the
        /// control still moves, just the wrong way, and nothing downstream flags it.</para>
        ///
        /// <para><b>Gyro horizontal family (yaw / roll / horizontal, NOT pitch) → stick X.</b>
        /// The gyro reports a right-hand-rule angular RATE; a leftward twist lands on
        /// +X. A stick is a position, so it must deflect TOWARD the twist (twist left →
        /// stick left). Flip.</para>
        ///
        /// <para><b>Why X only, and why nothing else.</b>
        /// <c>InputManager.WriteBipolarAxisTarget</c> already encodes the engine's
        /// stick sign convention: it negates Y for EVERY source (the SDL "+Y down → -axis"
        /// rule, <c>gp.ThumbLY = -value</c>) and leaves X alone. So a correction belongs
        /// here only where that downstream step does nothing — the X axis. Flipping a Y
        /// target here would double-negate it. That is exactly what an earlier touchpad
        /// branch did (finger-up drove the stick DOWN); it was removed. Everything else is
        /// already correct downstream: gyro pitch rides the Y negate to nose-up → stick-down
        /// (the flight-stick pull-back, JSM <c>processGyroStick</c> emits
        /// <c>setStick(gyroStickX, -gyroStickY)</c>); touchpad finger-Y rides it to
        /// finger-up → stick-up; KBM mouse / scroll keep their own aim convention. None of
        /// them may be flipped here.</para>
        /// </summary>
        private static bool ShouldFlipForAxisFrame(MappingSource src, string target)
        {
            if (src == null) return false;
            if (target != "LeftThumbAxisX" && target != "RightThumbAxisX") return false;
            string desc = src.Descriptor ?? "";
            // The gravity-lean pair (v26) is a POSITION already authored in
            // the stick sign frame (Lean X positive = tilt right = stick
            // right), not a right-hand-rule rate, so the rate correction
            // must not touch it.
            if (SourceCoercion.IsGyroLeanDescriptor(desc)) return false;
            // Pitch is the one rate axis already in the stick frame, and the
            // aux family (#252) spells it "Gyro L Pitch", so the exclusion
            // matches the AXIS rather than the exact primary descriptor. A
            // plain string compare against "Gyro Pitch" would have flipped
            // the aux pitch while leaving the primary correct.
            return SourceCoercion.IsGyroDescriptor(desc)
                && !SourceCoercion.IsGyroPitchAxisDescriptor(desc);
        }

        public static float EvaluateForTriggerTarget(
            CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds,
            string evaluatedDeviceGuid = null)
        {
            if (src == null) return 0f;
            if (!GateHeld(state, src, slotIndex, evaluatedDeviceGuid)) return 0f;

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
                case "Ramped":
                {
                    // A trigger has no negative side: fold the bipolar envelope to
                    // [0, 1] so the positive-direction key drives the trigger and the
                    // negative-direction key reads as released (issue #111).
                    if (runtime == null) return 0f;
                    double v = runtime.TickRamped(slotIndex, target, sourceIndex,
                        src, state, frameDeltaSeconds);
                    if (v < 0) v = 0;
                    if (v > 1) v = 1;
                    return (float)v;
                }
                case "InvertOnHold":
                {
                    bool modifier = ReadButtonLikeBool(state, src.ParamModifier);
                    var inner = CloneAsDirect(src, invertOverride: src.Invert ^ modifier);
                    return SourceCoercion.EvaluateForTriggerTarget(state, inner, slotIndex, evaluatedDeviceGuid);
                }
                default:
                    return SourceCoercion.EvaluateForTriggerTarget(state, src, slotIndex, evaluatedDeviceGuid);
            }
        }

        // Builds a copy of <paramref name="src"/> with Kind forced to Direct and
        // the specified Invert. Lets InvertOnHold reuse SourceCoercion's coercion
        // table without mutating the original. Uses the full memberwise
        // MappingSource.Clone so no per-source field (the DeadZone, the gyro /
        // mouse / IR sensitivities, and the #9 generic Sensitivity) silently drops
        // for an axis inner source. Kind = Direct means the copied Param* fields
        // are never read.
        private static MappingSource CloneAsDirect(MappingSource src, bool invertOverride)
        {
            var clone = src.Clone();
            clone.Kind = "Direct";
            clone.Invert = invertOverride;
            return clone;
        }

        // Mirrors SourceKindRuntime's button-like reader so the
        // InvertOnHold modifier-button check stays consistent with
        // Incremental's up/down inputs.
        private static bool ReadButtonLikeBool(CustomInputState state, string descriptor)
        {
            if (state == null || string.IsNullOrWhiteSpace(descriptor)) return false;
            // Fold "Gamepad ButtonA" / "Gamepad DPadUp" aliases (#9) to
            // their canonical "Button N" / "POV 0 Dir" form, mirroring
            // SourceKindRuntime's reader.
            string s = SourceCoercion.CanonicalDescriptor(descriptor);

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

            // Not Button/POV: hardware-bool families (capsense, NFC tag,
            // touchpad contact) are pickable as Invert-on-Hold modifiers
            // too. Same fallback as SourceKindRuntime's reader (#248 audit).
            return SourceCoercion.ReadHardwareBoolDescriptor(state, s);
        }
    }
}
