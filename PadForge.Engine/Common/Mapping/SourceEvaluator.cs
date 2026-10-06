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
    /// Toggle: latches the Direct read of the same input through
    /// <see cref="SourceKindRuntime.TickToggle"/>. Each press flips it.
    /// RapidTrigger: the Direct read of the same input, released and
    /// pressed again by small moves past the deadzone through
    /// <see cref="SourceKindRuntime.TickRapidTrigger"/>.
    /// </para>
    /// </summary>
    public static class SourceEvaluator
    {
        /// <summary>A blank Direct source occupies a position but reads no input.
        /// So does a blank Toggle or Rapid Trigger, which act on that same read.</summary>
        public static bool IsUnmappedDirect(MappingSource source)
            => source != null && string.IsNullOrEmpty(source.Descriptor)
                && IsDescriptorKind(source.Kind);

        /// <summary>True for the kinds that read the source's own descriptor
        /// as a plain input: Direct (also a null or empty kind), Toggle (#461),
        /// which latches that same read, and Rapid Trigger (#482), which
        /// releases and presses it again by travel. The other kinds read their
        /// own parameter keys, or modify the row instead.</summary>
        public static bool IsDescriptorKind(string kind)
            => string.IsNullOrEmpty(kind)
            || string.Equals(kind, "Direct", StringComparison.Ordinal)
            || string.Equals(kind, "Toggle", StringComparison.Ordinal)
            || string.Equals(kind, "RapidTrigger", StringComparison.Ordinal);

        /// <summary>True for the Toggle kind (#461). Its read advances a
        /// latch, so a caller walking the slot's devices must read them
        /// all rather than stop at the first one that answers.</summary>
        public static bool IsToggleKind(MappingSource source)
            => source != null && string.Equals(source.Kind, "Toggle", StringComparison.Ordinal);

        /// <summary>True for the Rapid Trigger kind (#482).</summary>
        public static bool IsRapidTriggerKind(MappingSource source)
            => source != null && string.Equals(source.Kind, "RapidTrigger", StringComparison.Ordinal);

        /// <summary>True when a read of the source advances per-frame state that
        /// every device on the slot must feed: Toggle's latch and Rapid
        /// Trigger's zone. A caller walking the slot's devices for an
        /// any-device source reads them all rather than stop at the first one
        /// that answers.</summary>
        public static bool ReadsEveryDevice(MappingSource source)
            => IsToggleKind(source) || IsRapidTriggerKind(source);

        /// <summary>True for the kinds read through an Up and a Down key,
        /// Incremental and Ramp. Each key reads the controller it names
        /// (SourceKindRuntime.ReadKey), so nothing they read comes from the
        /// device the source is evaluated on.</summary>
        public static bool UsesUpDownKeys(MappingSource source)
            => source != null && source.Kind is "Incremental" or "Ramped";

        /// <summary>The Rapid Trigger distance as a fraction of full travel.</summary>
        private static double RapidTriggerDistance(MappingSource src)
            => MappingSource.EffectiveRapidTriggerDistance(src.ParamRapidTriggerDistance) / 100.0;

        /// <summary>How far a trigger or axis read must travel to count as a
        /// Toggle press, as a fraction of full scale: the source's own
        /// activation threshold, 50 percent unless the row sets another. The
        /// button lane reaches the same rule through SourceCoercion.</summary>
        private static float TogglePressLevel(MappingSource src)
            => SourceCoercion.EffectiveThresholdPercent(src, 50) / 100f;

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
                    // The gate keeps a Gamepad name as written: the read
                    // resolves it for the device it reads, and a Bliss-Box
                    // port read raw places it its own way (#469).
                    cached = new MappingSource
                    {
                        Kind = "Direct",
                        Descriptor = gate.Trim(),
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
                        Descriptor = gate2.Trim(),
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
            string evaluatedDeviceGuid = null, string layer = null)
        {
            if (src == null || IsUnmappedDirect(src)) return false;
            using var placement = SourceCoercion.ReadingDevice(src, evaluatedDeviceGuid);
            if (!GateHeld(state, src, slotIndex, evaluatedDeviceGuid, globalThresholdPercent)) return false;

            switch (src.Kind ?? "Direct")
            {
                case "Incremental":
                {
                    if (runtime == null) return false;
                    double v = runtime.TickIncremental(slotIndex, target, sourceIndex,
                        src, state, frameDeltaSeconds, evaluatedDeviceGuid);
                    bool result = v > 0.5;
                    return src.Invert ? !result : result;
                }
                case "Toggle":
                {
                    // The Direct read of the same input, latched: each
                    // press flips the button, which holds between presses.
                    if (runtime == null) return false;
                    bool pressed = SourceCoercion.EvaluateForButtonTarget(state, src,
                        globalThresholdPercent, slotIndex, evaluatedDeviceGuid);
                    return runtime.TickToggle(slotIndex, target, sourceIndex, pressed, 1.0, layer) != 0;
                }
                case "RapidTrigger":
                {
                    // The Direct read decides the zone, so the first press lands
                    // where a Direct row presses, and the trigger lane's pull of
                    // the same input is the travel inside it. With no runtime
                    // (a preview) or an input with no depth, the Direct read.
                    bool past = SourceCoercion.EvaluateForButtonTarget(state, src,
                        globalThresholdPercent, slotIndex, evaluatedDeviceGuid);
                    if (runtime == null || !SourceCoercion.IsRapidTriggerSource(src.Descriptor))
                        return past;
                    float depth = SourceCoercion.EvaluateForTriggerTarget(state, src,
                        slotIndex, evaluatedDeviceGuid);
                    return runtime.TickRapidTrigger(slotIndex, target, sourceIndex,
                        past, depth, RapidTriggerDistance(src), layer);
                }
                case "Ramped":
                    // A ramped axis envelope has no defensible boolean reading; a
                    // button target gets nothing (issue #111). Picking a threshold
                    // here would surprise anyone who set up the row.
                    return false;
                case "InvertOnHold":
                {
                    bool modifier = SourceKindRuntime.ReadKey(state, src.ParamModifier,
                        src.ParamModifierDeviceGuid, src, evaluatedDeviceGuid, slotIndex,
                        anyDeviceSpansSlot: false);
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
            string evaluatedDeviceGuid = null, string layer = null)
        {
            if (src == null || IsUnmappedDirect(src)) return 0f;
            using var placement = SourceCoercion.ReadingDevice(src, evaluatedDeviceGuid);
            if (!GateHeld(state, src, slotIndex, evaluatedDeviceGuid)) return 0f;

            // The Motion Pitch, Yaw and Roll rows (#475) read a source that
            // rests at zero one way: read centered, a released trigger sits
            // at full deflection and turns the controller with nothing
            // touched, the shape #443 fixed for activators. A half the user
            // picked keeps its own read.
            bool oneWay = !src.HalfAxis && MappingSetMigrator.IsMotionAxisTarget(target)
                && (SourceCoercion.SourceRestsAtZeroProvider?.Invoke(src.Descriptor,
                    SourceCoercion.EffectiveDeviceGuid(src, evaluatedDeviceGuid)) ?? false);

            string kind = src.Kind ?? "Direct";
            // Rapid Trigger acts on presses (#482). On a stick an analog input
            // is proportional movement, so the row reads it as Direct, the
            // Motion rows' one-way read included.
            if (kind == "RapidTrigger") kind = "Direct";
            if (kind != "Toggle")
                return EvaluateBipolarKind(kind, state, src, slotIndex, target, sourceIndex,
                    runtime, frameDeltaSeconds, evaluatedDeviceGuid, oneWay);

            // Toggle latches the Direct read of the same input at full
            // scale, in the direction the latching press pointed.
            if (runtime == null) return 0f;
            float direct = EvaluateBipolarKind("Direct", state, src, slotIndex, target, sourceIndex,
                runtime, frameDeltaSeconds, evaluatedDeviceGuid, oneWay);
            return (float)runtime.TickToggle(slotIndex, target, sourceIndex,
                Math.Abs(direct) >= TogglePressLevel(src), direct < 0 ? -1.0 : 1.0, layer);
        }

        private static float EvaluateBipolarKind(string kind,
            CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds,
            string evaluatedDeviceGuid, bool oneWay = false)
        {
            if (oneWay && kind == "Direct")
            {
                // The trigger lane's 0..1 pull, which spends Invert as 1 - v
                // on these sources. Here Invert picks the direction instead.
                float v = SourceCoercion.EvaluateForTriggerTarget(state, src, slotIndex, evaluatedDeviceGuid);
                float pull = src.Invert ? 1f - v : v;
                return src.Invert ? -pull : pull;
            }

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
            if (kind == "Direct" && SourceCoercion.IsMotionLeanDescriptor(src.Descriptor))
                kind = "MotionLeanX";
            else if (kind == "Direct" && SourceCoercion.IsMotionLeanAuxDescriptor(src.Descriptor))
                kind = "MotionLeanAuxX";
            else if (kind == "Direct" && SourceCoercion.IsMotionShakeDescriptor(src.Descriptor))
                kind = "MotionShake";
            else if (kind == "Direct" && SourceCoercion.IsMotionShakeAuxDescriptor(src.Descriptor))
                kind = "MotionShakeAux";

            switch (kind)
            {
                case "Incremental":
                {
                    if (runtime == null) return 0f;
                    double v = runtime.TickIncremental(slotIndex, target, sourceIndex,
                        src, state, frameDeltaSeconds, evaluatedDeviceGuid);
                    if (v < -1) v = -1;
                    if (v > 1) v = 1;
                    return src.Invert ? -(float)v : (float)v;
                }
                case "Ramped":
                {
                    if (runtime == null) return 0f;
                    double v = runtime.TickRamped(slotIndex, target, sourceIndex,
                        src, state, frameDeltaSeconds, evaluatedDeviceGuid);
                    if (v < -1) v = -1;
                    if (v > 1) v = 1;
                    return src.Invert ? -(float)v : (float)v;
                }
                case "InvertOnHold":
                {
                    bool modifier = SourceKindRuntime.ReadKey(state, src.ParamModifier,
                        src.ParamModifierDeviceGuid, src, evaluatedDeviceGuid, slotIndex,
                        anyDeviceSpansSlot: false);
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
                case "MotionShake":
                case "MotionShakeAux":
                {
                    // Shake envelope as an axis (#364): unsigned 0..1, no
                    // runtime state (the App computes the envelope beside
                    // the gravity EMA and the provider hands it over).
                    // Invert has nothing to point at on an envelope.
                    return SourceCoercion.ReadShakeEnvelope(src,
                        SourceCoercion.EffectiveDeviceGuid(src, evaluatedDeviceGuid),
                        kind == "MotionShakeAux");
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
                || target == "KbmScroll"
                // The horizontal wheel is a rate output exactly like its
                // vertical twin. Omitting it made a touchpad source on
                // KbmScrollH read ABSOLUTE pad position, so resting a finger
                // anywhere off center scrolled sideways continuously instead
                // of scrolling by the per-frame delta.
                || target == "KbmScrollH";
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
            if (SourceCoercion.IsGravityTiltFamilyDescriptor(desc)) return false;
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
            string evaluatedDeviceGuid = null, string layer = null)
        {
            if (src == null || IsUnmappedDirect(src)) return 0f;
            using var placement = SourceCoercion.ReadingDevice(src, evaluatedDeviceGuid);
            if (!GateHeld(state, src, slotIndex, evaluatedDeviceGuid)) return 0f;

            switch (src.Kind ?? "Direct")
            {
                case "Incremental":
                {
                    if (runtime == null) return 0f;
                    double v = runtime.TickIncremental(slotIndex, target, sourceIndex,
                        src, state, frameDeltaSeconds, evaluatedDeviceGuid);
                    if (v < 0) v = 0;
                    if (v > 1) v = 1;
                    return src.Invert ? 1f - (float)v : (float)v;
                }
                case "Toggle":
                {
                    // The Direct pull of the same input, latched at full.
                    if (runtime == null) return 0f;
                    float pull = SourceCoercion.EvaluateForTriggerTarget(state, src,
                        slotIndex, evaluatedDeviceGuid);
                    return (float)runtime.TickToggle(slotIndex, target, sourceIndex,
                        pull >= TogglePressLevel(src), 1.0, layer);
                }
                case "RapidTrigger":
                {
                    // A full pull while pressed and rest while released, the
                    // level Toggle holds on a trigger row. The zone opens at the
                    // source's own deadzone, 50 percent unless the row sets
                    // another, the default Toggle's trigger lane uses. With no
                    // runtime (the Triggers tab preview) it reads as its first
                    // press, and an input with no depth reads as Direct.
                    if (!SourceCoercion.IsRapidTriggerSource(src.Descriptor))
                        return SourceCoercion.EvaluateForTriggerTarget(state, src, slotIndex, evaluatedDeviceGuid);
                    bool past = SourceCoercion.EvaluateForButtonTarget(state, src,
                        50, slotIndex, evaluatedDeviceGuid);
                    if (runtime == null) return past ? 1f : 0f;
                    float depth = SourceCoercion.EvaluateForTriggerTarget(state, src,
                        slotIndex, evaluatedDeviceGuid);
                    return runtime.TickRapidTrigger(slotIndex, target, sourceIndex,
                        past, depth, RapidTriggerDistance(src), layer) ? 1f : 0f;
                }
                case "Ramped":
                {
                    // A trigger has no negative side: fold the bipolar envelope to
                    // [0, 1] so the positive-direction key drives the trigger and the
                    // negative-direction key reads as released (issue #111).
                    if (runtime == null) return 0f;
                    double v = runtime.TickRamped(slotIndex, target, sourceIndex,
                        src, state, frameDeltaSeconds, evaluatedDeviceGuid);
                    if (v < 0) v = 0;
                    if (v > 1) v = 1;
                    return (float)v;
                }
                case "InvertOnHold":
                {
                    bool modifier = SourceKindRuntime.ReadKey(state, src.ParamModifier,
                        src.ParamModifierDeviceGuid, src, evaluatedDeviceGuid, slotIndex,
                        anyDeviceSpansSlot: false);
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
    }
}
