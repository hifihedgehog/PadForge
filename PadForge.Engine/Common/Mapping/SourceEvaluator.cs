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
    /// Toggle: latches the Direct read of the same input through
    /// <see cref="SourceKindRuntime.TickToggle"/>. Each press flips it.
    /// RapidTrigger: the Direct read of the same input, released and
    /// pressed again by small moves past the deadzone through
    /// <see cref="SourceKindRuntime.TickRapidTrigger"/>.
    /// A "Mouse Travel" input (#600) is read through
    /// <see cref="SourceKindRuntime.TickMouseTravel"/> on every lane, where
    /// those three kinds take their Direct read.
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

        /// <summary>True for a source of the travel family (#600):
        /// "Mouse Travel X" or "Y", "Wheel Travel", "Wheel Travel Sideways",
        /// or an axis or slider marked as moving relatively, read by a kind
        /// that reads its own input. Such a source keeps a wheel's position
        /// in the slot's runtime and reads its gate as its Steer
        /// input.</summary>
        public static bool IsMouseTravel(MappingSource source)
        {
            if (source == null) return false;
            // Every travel descriptor starts with M or W, so any other
            // source not marked relative answers at once, before the kind's
            // string tests.
            if (!source.ParamAxisRelative
                && (source.Descriptor is not { Length: > 0 } d || (d[0] != 'M' && d[0] != 'W')))
                return false;
            return IsDescriptorKind(source.Kind)
               && (SourceCoercion.IsMouseTravelDescriptor(source.Descriptor)
                   || (source.ParamAxisRelative && SourceCoercion.TryParseRelativeAxis(source.Descriptor, out _, out _)));
        }

        /// <summary>The mouse families that steer by their own settings
        /// (#600), apart from travel.</summary>
        public enum MouseSteerFamily { None, Speed, Position }

        /// <summary>Which of Mouse Speed and Mouse Position a source is,
        /// when it steers by its own settings (#600), and whether it reads
        /// Y. Mouse Speed is a mouse's Axis 0 or 1. Either steers by its own
        /// settings once one of them leaves its default: Mouse Speed's
        /// Sensitivity, Smoothing or Back to Center, Mouse Position's
        /// Smoothing or its anchor under a Steer input, or a Steer input
        /// under While Held and Stay or Toggle. At the defaults, and with a
        /// Steer input under While Held and Center, a source reads as it
        /// always has, its gate reading nothing while open, which is that
        /// mode.</summary>
        public static MouseSteerFamily MouseSteerOf(MappingSource src, string evaluatedDeviceGuid, out bool y)
        {
            y = false;
            // The four descriptors are 6 or 16 characters long: any other
            // answers None before the kind's string tests.
            if (src?.Descriptor is not { Length: 6 or 16 }) return MouseSteerFamily.None;
            if (!IsDescriptorKind(src.Kind) || src.ParamAxisRelative) return MouseSteerFamily.None;
            string d = src.Descriptor;
            MouseSteerFamily family;
            if (d == "Mouse Position X" || d == "Mouse Position Y")
            {
                family = MouseSteerFamily.Position;
                y = d[d.Length - 1] == 'Y';
            }
            else if (d == "Axis 0" || d == "Axis 1")
            {
                family = MouseSteerFamily.Speed;
                y = d == "Axis 1";
            }
            else return MouseSteerFamily.None;

            bool waits = !string.IsNullOrEmpty(src.GateDescriptor) || !string.IsNullOrEmpty(src.Gate2Descriptor);
            string steer = MappingSource.EffectiveMouseSteer(src.ParamTravelSteer, null, waits, holdsPosition: false);
            bool own = steer is MappingSource.TravelSteerHeldStay or MappingSource.TravelSteerToggle
                || MappingSource.EffectiveTravelSmoothingMs(src.ParamTravelSmoothingMs) > 0;
            if (family == MouseSteerFamily.Speed)
                own |= MappingSource.EffectiveMouseSpeedSensitivity(src.ParamMouseSpeedSensitivity) != MappingSource.DefaultMouseSpeedSensitivity
                    || MappingSource.EffectiveMouseBackToCenter(src.ParamMouseBackToCenter) > 0;
            else
                own |= src.ParamMousePositionAnchor && waits;
            if (!own) return MouseSteerFamily.None;
            // Asked last: an Axis 0 or 1 at its defaults costs no lookup.
            if (family == MouseSteerFamily.Speed)
            {
                var isMouse = SourceCoercion.IsMouseDeviceProvider;
                if (isMouse == null || !isMouse(SourceCoercion.EffectiveDeviceGuid(src, evaluatedDeviceGuid)))
                    return MouseSteerFamily.None;
            }
            return family;
        }

        /// <summary>True for a source that reads its gate as its Steer
        /// input: the travel family, and Mouse Speed or Mouse Position
        /// steering by its own settings.</summary>
        public static bool ReadsOwnGate(MappingSource src, string evaluatedDeviceGuid)
            => IsMouseTravel(src) || MouseSteerOf(src, evaluatedDeviceGuid, out _) != MouseSteerFamily.None;

        /// <summary>Advances a travel source and answers its position from
        /// -1 to 1 (SourceKindRuntime.TickMouseTravel). The source's gate is
        /// its Steer input, which only decides whether the mouse moves the
        /// wheel: let go, the wheel stays where it is or comes back, so the
        /// gate is read here and handed to the wheel, where any other source
        /// reads nothing while its gate is open. A read with no runtime (a
        /// preview) has no wheel and answers center. <paramref name="forceSign"/>
        /// and <paramref name="forceFolds"/> are the sign From the Game's Force
        /// works in (SourceKindRuntime.TickMouseTravel), given by the lane that
        /// writes an axis and null on the others. With no runtime the app's
        /// wheel for a reader that keeps none answers
        /// (SourceCoercion.TravelPositionProvider), center where it takes no
        /// read.</summary>
        private static double MouseTravel(CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds, string evaluatedDeviceGuid,
            string layer, double? forceSign = null, bool forceFolds = false)
            => runtime == null ? SourceCoercion.TravelPosition(state, src, slotIndex, evaluatedDeviceGuid) ?? 0
                : runtime.TickMouseTravel(slotIndex, target, sourceIndex, src, state, frameDeltaSeconds,
                    SteerHeld(state, src, slotIndex, evaluatedDeviceGuid),
                    SourceCoercion.EffectiveDeviceGuid(src, evaluatedDeviceGuid),
                    forceSign, forceFolds, layer);

        /// <summary>A travel-family source's position from -1 to 1 on
        /// <paramref name="runtime"/> under <paramref name="target"/>, its
        /// Steer input read as a row's is: the app's wheel for a reader that
        /// keeps none (SourceCoercion.TravelPositionProvider).</summary>
        public static double ReadTravel(CustomInputState state, MappingSource src, int slotIndex, string target,
            SourceKindRuntime runtime, double frameDeltaSeconds, string evaluatedDeviceGuid)
        {
            if (src == null || runtime == null || !IsMouseTravel(src)) return 0;
            using var placement = SourceCoercion.ReadingDevice(src, evaluatedDeviceGuid);
            return MouseTravel(state, src, slotIndex, target, 0, runtime, frameDeltaSeconds, evaluatedDeviceGuid, null);
        }

        /// <summary>Advances a Mouse Speed or Mouse Position source that
        /// steers by its own settings and answers its stick from -1 to 1.
        /// A read with no runtime answers center.</summary>
        private static double MouseSteer(MouseSteerFamily family, bool y, CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds, string evaluatedDeviceGuid, string layer)
        {
            if (runtime == null) return 0;
            bool held = SteerHeld(state, src, slotIndex, evaluatedDeviceGuid);
            return family == MouseSteerFamily.Speed
                ? runtime.TickMouseSpeed(slotIndex, target, sourceIndex, src, state, frameDeltaSeconds, held, y, layer)
                : runtime.TickMousePosition(slotIndex, target, sourceIndex, src, frameDeltaSeconds, held, y, layer: layer);
        }

        /// <summary>A mouse source's own answer from -1 to 1 before the row
        /// reads it, for the travel family and for Mouse Speed and Mouse
        /// Position steering by their own settings, or null for any other
        /// source. <paramref name="family"/> says which, None for the travel
        /// family. Mouse Speed's answer is the speed before the source's
        /// Acceleration, which each lane takes where the plain read of an
        /// axis takes it.</summary>
        private static double? MousePosition(CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds, string evaluatedDeviceGuid,
            string layer, out MouseSteerFamily family, double? forceSign = null, bool forceFolds = false)
        {
            family = MouseSteerFamily.None;
            if (IsMouseTravel(src))
                return MouseTravel(state, src, slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid,
                    layer, forceSign, forceFolds);
            family = MouseSteerOf(src, evaluatedDeviceGuid, out bool y);
            if (family == MouseSteerFamily.None) return null;
            return MouseSteer(family, y, state, src, slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid, layer);
        }

        /// <summary>The Direct read of a source as a button: a mouse
        /// source's own answer past the deadzone, the input's own read for
        /// any other. Mouse Speed and Mouse Position keep the shape the
        /// plain read of their input gives a button.</summary>
        private static bool DirectButton(CustomInputState state, MappingSource src, int thresholdPercent,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds, string evaluatedDeviceGuid, string layer)
        {
            if (MousePosition(state, src, slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid,
                    layer, out var family) is not double v)
                return SourceCoercion.EvaluateForButtonTarget(state, src, thresholdPercent, slotIndex, evaluatedDeviceGuid);
            return family switch
            {
                MouseSteerFamily.Speed => SourceCoercion.MouseSpeedAsButton(v, src, thresholdPercent),
                MouseSteerFamily.Position => SourceCoercion.MousePositionAsButton(v, src, thresholdPercent),
                _ => SourceCoercion.MouseTravelAsButton(v, src, thresholdPercent),
            };
        }

        /// <summary>The Direct read of a source as a trigger's pull, by the
        /// rule of <see cref="DirectButton"/>.</summary>
        private static float DirectTrigger(CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds, string evaluatedDeviceGuid, string layer)
        {
            if (MousePosition(state, src, slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid,
                    layer, out var family) is not double v)
                return SourceCoercion.EvaluateForTriggerTarget(state, src, slotIndex, evaluatedDeviceGuid);
            return family switch
            {
                MouseSteerFamily.Speed => SourceCoercion.MouseSpeedAsTrigger(v, src, state),
                MouseSteerFamily.Position => SourceCoercion.MousePositionAsTrigger(v, src),
                _ => SourceCoercion.MouseTravelAsTrigger(v, src),
            };
        }

        /// <summary>The Rapid Trigger distance as a fraction of full travel.</summary>
        private static double RapidTriggerDistance(MappingSource src)
            => MappingSource.EffectiveRapidTriggerDistance(src.ParamRapidTriggerDistance) / 100.0;

        /// <summary>How far a trigger or axis read must travel to count as a
        /// Toggle press, as a fraction of full scale: the source's own
        /// activation threshold, 50 percent unless the row sets another. The
        /// button lane reaches the same rule through SourceCoercion.</summary>
        private static float TogglePressLevel(MappingSource src)
            => SourceCoercion.EffectiveThresholdPercent(src, 50) / 100f;

        /// <summary>A mouse source's Steer input (#600), its gate, read on the
        /// device the source names for it
        /// (<see cref="MappingSource.ParamTravelSteerDeviceGuid"/>) as an Up or
        /// Down key reads the controller it names
        /// (<see cref="SourceKindRuntime.ReadKey"/>): a keyboard's key, another
        /// pad's button, or any device of the slot. A source that names none
        /// reads it on its own device, as every gate is read
        /// (<see cref="GateHeld"/>). The second gate an import writes is read
        /// there too.</summary>
        /// <para>The lanes read the gate of a source that does not steer by
        /// its own settings through this too, so a Mouse Speed or Mouse
        /// Position source at its defaults reads its Steer input on the
        /// device the line picked for it.</para>
        private static bool SteerHeld(CustomInputState state, MappingSource src,
            int slotIndex, string evaluatedDeviceGuid, int thresholdPercent = 50)
        {
            string device = src.ParamTravelSteerDeviceGuid;
            if (device == null) return GateHeld(state, src, slotIndex, evaluatedDeviceGuid, thresholdPercent);
            string gate = src.GateDescriptor;
            if (!string.IsNullOrEmpty(gate)
                && !SourceKindRuntime.ReadKey(state, gate, device, src, evaluatedDeviceGuid, slotIndex))
                return false;
            return Gate2Held(state, src, slotIndex, evaluatedDeviceGuid, thresholdPercent);
        }

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
            return Gate2Held(state, src, slotIndex, evaluatedDeviceGuid, thresholdPercent);
        }

        /// <summary>The second leg of <see cref="GateHeld"/>.</summary>
        private static bool Gate2Held(CustomInputState state, MappingSource src,
            int slotIndex, string evaluatedDeviceGuid, int thresholdPercent)
        {
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
            // A mouse source that steers reads its gate as its Steer input (MousePosition).
            if (!ReadsOwnGate(src, evaluatedDeviceGuid)
                && !SteerHeld(state, src, slotIndex, evaluatedDeviceGuid, globalThresholdPercent)) return false;

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
                    bool pressed = DirectButton(state, src, globalThresholdPercent,
                        slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid, layer);
                    return runtime.TickToggle(slotIndex, target, sourceIndex, pressed, 1.0, layer) != 0;
                }
                case "RapidTrigger":
                {
                    // The Direct read decides the zone, so the first press lands
                    // where a Direct row presses, and the trigger lane's pull of
                    // the same input is the travel inside it. With no runtime
                    // (a preview) or an input with no depth, the Direct read.
                    // A mouse source's depth is its own answer, as its zone
                    // is, so its wheel and its Steer input decide both.
                    bool past = DirectButton(state, src, globalThresholdPercent,
                        slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid, layer);
                    if (runtime == null || !SourceCoercion.IsRapidTriggerSource(src.Descriptor))
                        return past;
                    float depth = ReadsOwnGate(src, evaluatedDeviceGuid)
                        ? DirectTrigger(state, src, slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid, layer)
                        : SourceCoercion.EvaluateForTriggerTarget(state, src, slotIndex, evaluatedDeviceGuid);
                    return runtime.TickRapidTrigger(slotIndex, target, sourceIndex,
                        past, depth, RapidTriggerDistance(src), layer);
                }
                case "Ramped":
                    // A ramped axis envelope has no defensible boolean reading; a
                    // button target gets nothing (issue #111). Picking a threshold
                    // here would surprise anyone who set up the row.
                    return false;
                default: // Direct
                    return DirectButton(state, src, globalThresholdPercent,
                        slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid, layer);
            }
        }

        /// <param name="rowSign">The sign the caller puts on the answer
        /// before it reaches the target: -1 for a row whose Invert on Hold is
        /// held, times -1 for a stick's Y, which the gamepad writer negates.
        /// From the Game's Force on a travel source works in it.</param>
        public static float EvaluateForBipolarAxisTarget(
            CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds,
            string evaluatedDeviceGuid = null, string layer = null, double rowSign = 1.0)
        {
            if (src == null || IsUnmappedDirect(src)) return 0f;
            using var placement = SourceCoercion.ReadingDevice(src, evaluatedDeviceGuid);
            // A mouse source that steers reads its gate as its Steer input (MousePosition).
            if (!ReadsOwnGate(src, evaluatedDeviceGuid) && !SteerHeld(state, src, slotIndex, evaluatedDeviceGuid)) return 0f;

            // The Motion Pitch, Yaw and Roll rows (#475) read a source that
            // rests at zero one way: read centered, a released trigger sits
            // at full deflection and turns the controller with nothing
            // touched, the shape #443 fixed for activators. A half the user
            // picked keeps its own read. So does a mouse source that steers
            // (ReadsOwnGate), whose answer is its wheel or its own speed or
            // position, whatever the input rests at.
            bool oneWay = !src.HalfAxis && MappingSetMigrator.IsMotionAxisTarget(target)
                && !ReadsOwnGate(src, evaluatedDeviceGuid)
                && (SourceCoercion.SourceRestsAtZeroProvider?.Invoke(src.Descriptor,
                    SourceCoercion.EffectiveDeviceGuid(src, evaluatedDeviceGuid)) ?? false);

            string kind = src.Kind ?? "Direct";
            // Rapid Trigger acts on presses (#482). On a stick an analog input
            // is proportional movement, so the row reads it as Direct, the
            // Motion rows' one-way read included.
            if (kind == "RapidTrigger") kind = "Direct";
            if (kind != "Toggle")
                return EvaluateBipolarKind(kind, state, src, slotIndex, target, sourceIndex,
                    runtime, frameDeltaSeconds, evaluatedDeviceGuid, oneWay, layer, rowSign);

            // Toggle latches the Direct read of the same input at full
            // scale, in the direction the latching press pointed.
            if (runtime == null) return 0f;
            float direct = EvaluateBipolarKind("Direct", state, src, slotIndex, target, sourceIndex,
                runtime, frameDeltaSeconds, evaluatedDeviceGuid, oneWay, layer, rowSign);
            return (float)runtime.TickToggle(slotIndex, target, sourceIndex,
                Math.Abs(direct) >= TogglePressLevel(src), direct < 0 ? -1.0 : 1.0, layer);
        }

        private static float EvaluateBipolarKind(string kind,
            CustomInputState state, MappingSource src,
            int slotIndex, string target, int sourceIndex,
            SourceKindRuntime runtime, double frameDeltaSeconds,
            string evaluatedDeviceGuid, bool oneWay, string layer, double rowSign)
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
            // The travel family (#600) is an input too, and its wheel
            // is the runtime's (IsMouseTravel), and so is a Mouse Speed or
            // Mouse Position source steering by its own settings. An empty
            // kind reads as Direct there, so it does here.
            else if ((kind == "Direct" || kind.Length == 0)
                     && (IsMouseTravel(src) || MouseSteerOf(src, evaluatedDeviceGuid, out _) != MouseSteerFamily.None))
                kind = "MouseTravel";

            switch (kind)
            {
                case "MouseTravel":
                {
                    // Invert, Half and Flip Output are read with the wheel.
                    // The sign from the wheel to the axis this row writes is
                    // the one From the Game's Force works in.
                    double forceSign = SourceCoercion.MouseTravelBipolarSign(src, out bool forceFolds) * rowSign;
                    double v = MousePosition(state, src, slotIndex, target, sourceIndex, runtime, frameDeltaSeconds,
                        evaluatedDeviceGuid, layer, out var family, forceSign, forceFolds) ?? 0;
                    // Mouse Speed is an axis, whose read takes the source's
                    // Acceleration before its curve (SourceCoercion's axis
                    // tail), so it does here too.
                    if (family == MouseSteerFamily.Speed) v = SourceCoercion.ApplyPerSourceAccel(src, (float)v);
                    return SourceCoercion.MouseTravelAsBipolar(v, src);
                }
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
        /// branch did (finger-up drove the stick DOWN), and it was removed. Touchpad finger-Y
        /// rides the Y negate to finger-up → stick-up, and KBM mouse / scroll keep their own
        /// aim convention.</para>
        ///
        /// <para><b>Gyro pitch on a stick Y target goes DOWN for the nose up.</b> A rate
        /// pitch is positive with the nose up, and the Y negate writes it as the stick
        /// pulled back. That is the opposite of the gyro mouse lane, which moves the cursor
        /// up, and of JoyShockMapper's gyro stick: its pitch is camera space, down positive
        /// (main.cpp <c>gyroY -= inGyroX</c>), and <c>processGyroStick</c> writes
        /// <c>setStick(gyroStickX, -gyroStickY)</c>, so the nose up pushes the stick up.
        /// Saved and exported profiles carry this sign, so it stays. A producer that writes
        /// a camera-style gyro-to-stick row (an importer, a starter profile) sets Invert on
        /// the pitch source to look up for the nose up. Pitch must not be flipped here
        /// without the hardware re-test named above.</para>
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
            // A mouse source that steers reads its gate as its Steer input (MousePosition).
            if (!ReadsOwnGate(src, evaluatedDeviceGuid) && !SteerHeld(state, src, slotIndex, evaluatedDeviceGuid)) return 0f;

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
                    float pull = DirectTrigger(state, src,
                        slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid, layer);
                    return (float)runtime.TickToggle(slotIndex, target, sourceIndex,
                        pull >= TogglePressLevel(src), 1.0, layer);
                }
                case "RapidTrigger":
                {
                    // A full pull while pressed and rest while released, the
                    // level Toggle holds on a trigger row. The zone opens at the
                    // source's own deadzone, 50 percent unless the row sets
                    // another, the default Toggle's trigger lane uses. With no
                    // runtime (the Triggers group preview) it reads as its first
                    // press, and an input with no depth reads as Direct. A
                    // mouse source's zone and depth are its own answer, so
                    // its wheel and its Steer input decide both.
                    if (!SourceCoercion.IsRapidTriggerSource(src.Descriptor))
                        return DirectTrigger(state, src,
                            slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid, layer);
                    bool own = ReadsOwnGate(src, evaluatedDeviceGuid);
                    bool past = own
                        ? DirectButton(state, src, 50, slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid, layer)
                        : SourceCoercion.EvaluateForButtonTarget(state, src, 50, slotIndex, evaluatedDeviceGuid);
                    if (runtime == null) return past ? 1f : 0f;
                    float depth = own
                        ? DirectTrigger(state, src, slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid, layer)
                        : SourceCoercion.EvaluateForTriggerTarget(state, src, slotIndex, evaluatedDeviceGuid);
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
                default:
                    return DirectTrigger(state, src,
                        slotIndex, target, sourceIndex, runtime, frameDeltaSeconds, evaluatedDeviceGuid, layer);
            }
        }
    }
}
