using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// One physical input feeding a <see cref="MappingRow"/>. A row can carry
    /// multiple sources combined via <see cref="MappingRow.CombineMode"/>.
    ///
    /// <para>
    /// Source kinds determine HOW a source is evaluated to produce its
    /// per-frame float. Combine modes determine how N already-evaluated
    /// sources merge. Source kinds can carry runtime state across frames;
    /// combine modes can't.
    /// </para>
    ///
    /// <para>
    /// <c>Direct</c> (the default), <c>Toggle</c> and <c>RapidTrigger</c>
    /// read <see cref="Descriptor"/>. <c>Incremental</c> and <c>Ramped</c>
    /// read their Up and Down keys (<see cref="ParamUp"/>,
    /// <see cref="ParamDown"/>). On a two-way axis row the steering kinds
    /// read a stick and the motion kinds read gravity or the shake
    /// envelope. <c>InvertOnHold</c> is a row modifier: Step 3 reads its
    /// <see cref="ParamModifier"/> key and never reads it as a source. Each
    /// kind reads only its own subset of the <c>Param*</c> fields, so adding
    /// a kind is XML-additive.
    /// </para>
    /// </summary>
    public class MappingSource
    {
        /// <summary>Source kind discriminator. <c>"Direct"</c> (default),
        /// <c>"Toggle"</c> (#461, the Direct read latched by each press),
        /// <c>"RapidTrigger"</c> (#482, the Direct read, released and pressed
        /// again by small moves while the input stays past the deadzone),
        /// <c>"Incremental"</c>, <c>"InvertOnHold"</c>, <c>"Ramped"</c>, plus the
        /// steering kinds. Forward-compatible: unknown values treated as Direct.</summary>
        [XmlAttribute] public string Kind { get; set; } = "Direct";

        /// <summary>Device this source reads from. Empty string means "first
        /// available device on the VC." Resolved per frame in Step 3.</summary>
        [XmlAttribute] public string DeviceGuid { get; set; } = "";

        /// <summary>Input descriptor in the existing format used by the Step 3
        /// mapping engine: <c>"Button N"</c>, <c>"Axis N"</c>, <c>"IHAxis N"</c>,
        /// <c>"POV N Dir"</c>, <c>"Slider N"</c>, or <c>""</c> (unmapped).
        /// <c>Direct</c> reads it, and so do <c>Toggle</c> and
        /// <c>RapidTrigger</c>, which act on that same read, and a kind this
        /// build does not know, which is read as Direct. On a two-way axis
        /// row <c>WindingStick</c>, <c>AngleToAxisX</c> and
        /// <c>AngleToAxisY</c> read it as their stick's X axis, with
        /// <see cref="ParamYDescriptor"/> for Y, and the motion kinds
        /// (<c>MotionLeanX</c>, <c>MotionLeanAuxX</c>, <c>MotionShake</c>,
        /// <c>MotionShakeAux</c>) read gravity or the shake envelope and
        /// leave it unread. On a button or trigger row all seven fall to the
        /// Direct read. Three kinds take no input from it on any row and keep
        /// whatever an earlier kind left in it: <c>Incremental</c> and
        /// <c>Ramped</c> read their Up and Down keys, and
        /// <c>InvertOnHold</c> is a row modifier, which Step 3 skips as a
        /// source and reads by its <see cref="ParamModifier"/> key alone. A
        /// change back to a kind that reads the Descriptor finds it
        /// there.</summary>
        [XmlAttribute] public string Descriptor { get; set; } = "";

        /// <summary>The controller the kept <see cref="Descriptor"/> was
        /// picked on, for a source whose kind leaves the Descriptor unread
        /// and which sits on another device: an Incremental or Ramp source
        /// goes to the controller its keys share, and an Invert on Hold
        /// source to the one its modifier names
        /// (MappingSourceItem.FollowKeysDevice), and each keeps the
        /// Descriptor of the kind it had before. Back in a kind that reads
        /// the Descriptor, the source returns here, where the input was
        /// picked, and not to the keys' controller, where nobody picked it.
        /// Empty for an input picked under any controller. Null, the usual
        /// case, when the Descriptor reads <see cref="DeviceGuid"/>: no
        /// initializer, so the serializer leaves a null out, a file saved
        /// before this field loads as null, and a build without it skips
        /// the attribute. It moves with the keys wherever a copy moves them
        /// (<see cref="RetargetKeyDevices"/>).</summary>
        [XmlAttribute] public string DescriptorDeviceGuid { get; set; }

        /// <summary>When <c>true</c>, flip the per-source value sign before
        /// combine. For button-class sources mapped to bipolar axis targets
        /// this is what produces the "negative direction" half of a paddle
        /// pair (pressed → -1 instead of +1).</summary>
        [XmlAttribute] public bool Invert { get; set; }

        /// <summary>When <c>true</c>, treat a bipolar axis source as
        /// half-axis (only the positive or negative half maps to the
        /// 0..+1 output range, depending on <see cref="Invert"/>).</summary>
        [XmlAttribute] public bool HalfAxis { get; set; }

        /// <summary>Output-side sign flip for sources whose
        /// <see cref="Invert"/> is already spoken for.
        ///
        /// <para>On a half-axis read of a centered <c>"Axis N"</c> (or a
        /// Mouse Motion source), <see cref="Invert"/> is consumed INSIDE the
        /// read as the half SELECTOR (lower half instead of upper), so the
        /// evaluators must not also negate with it or the flag would
        /// double-apply. That left such a source unable to express "select
        /// this half AND flip the result": a caller that needed the flip
        /// could only get it by overwriting Invert, which silently destroyed
        /// the half selection. This carries the flip separately.</para>
        ///
        /// <para>Applies ONLY where Invert is consumed as a selector. Everywhere
        /// else Invert already IS the output flip and this stays false, so old
        /// XML (which never carries the attribute) keeps its exact
        /// behavior.</para></summary>
        [XmlAttribute] public bool InvertOutput { get; set; }

        /// <summary>When <c>true</c> AND <see cref="HalfAxis"/> is also
        /// <c>true</c>, the axis-to-button check fires on absolute
        /// deflection past the deadzone — i.e. either side of center
        /// counts. <see cref="Invert"/> has no effect in this mode since
        /// mirroring around center already covers both directions.
        /// Ignored when HalfAxis is off (a full-range read has no center
        /// to mirror across).</summary>
        [XmlAttribute] public bool Bidirectional { get; set; }

        /// <summary>Per-source axis-to-button activation deadzone (0–100%).
        /// Used when a non-button source feeds a button or POV-direction
        /// target.</summary>
        [XmlAttribute] public int DeadZone { get; set; } = 50;

        // ─── Kind-specific parameters (only the relevant subset is read per Kind) ───

        /// <summary>The Up key of an Incremental or Ramp source: the input that
        /// moves the value up while held.</summary>
        [XmlAttribute] public string ParamUp { get; set; } = "";

        /// <summary>The Down key of an Incremental or Ramp source: the input
        /// that moves the value down while held.</summary>
        [XmlAttribute] public string ParamDown { get; set; } = "";

        /// <summary>The controller <see cref="ParamUp"/> reads: the device the
        /// key was picked or recorded from, or empty for any controller on the
        /// slot. Null for a key saved before keys carried a controller, which
        /// reads where every key used to, on this source's
        /// <see cref="DeviceGuid"/> (SourceKindRuntime.KeyDevice). No
        /// initializer, so the serializer leaves a null out and an absent
        /// attribute loads as null.</summary>
        [XmlAttribute] public string ParamUpDeviceGuid { get; set; }

        /// <summary>The controller <see cref="ParamDown"/> reads, by the rules
        /// of <see cref="ParamUpDeviceGuid"/>.</summary>
        [XmlAttribute] public string ParamDownDeviceGuid { get; set; }

        /// <summary>Incremental rate in units-per-second (full output range
        /// is 1.0 unit, so 0.5 means full sweep takes 2 s).</summary>
        [XmlAttribute] public double ParamRate { get => _paramRate; set => _paramRate = Finite(value, _paramRate); }
        private double _paramRate = 0.5;

        /// <summary>Incremental sticky behavior. <c>true</c> = value holds
        /// when neither up nor down is held (cruise control). <c>false</c> =
        /// snaps back to <see cref="ParamMin"/> when neither held (manual ramp).</summary>
        [XmlAttribute] public bool ParamSticky { get; set; } = true;

        /// <summary>Incremental clamp lower bound.</summary>
        [XmlAttribute] public double ParamMin { get => _paramMin; set => _paramMin = Finite(value, _paramMin); }
        private double _paramMin = 0;

        /// <summary>Incremental clamp upper bound.</summary>
        [XmlAttribute] public double ParamMax { get => _paramMax; set => _paramMax = Finite(value, _paramMax); }
        private double _paramMax = 1;

        /// <summary>The modifier key of an Invert on Hold source: the input
        /// that inverts the row while held. Only read when
        /// <c>Kind == "InvertOnHold"</c>.</summary>
        [XmlAttribute] public string ParamModifier { get; set; } = "";

        /// <summary>The controller <see cref="ParamModifier"/> reads, by the
        /// rules of <see cref="ParamUpDeviceGuid"/>.</summary>
        [XmlAttribute] public string ParamModifierDeviceGuid { get; set; }

        // ─── Ramped axis envelope (v3.5 #111) ───
        // A keyboard-to-axis time-based ramp. The positive-direction key (ParamUp)
        // and negative-direction key (ParamDown) drive a bipolar accumulator in
        // [-1, +1] that attacks toward the held side over ParamAttackTime and
        // releases back toward zero over ParamReleaseTime. The ramp behaves
        // like the KBM2Gamepad and FreePIE throttles the issue referenced. The
        // math here is PadForge's own.

        /// <summary>Ramped attack time in seconds: how long the axis takes to travel
        /// 0 to ±1 while the matching-direction key is held. 0 = instant. Only read
        /// when <c>Kind == "Ramped"</c>.</summary>
        [XmlAttribute] public double ParamAttackTime { get => _paramAttackTime; set => _paramAttackTime = Finite(value, _paramAttackTime); }
        private double _paramAttackTime = 0.30;

        /// <summary>Ramped release time in seconds: how long the axis takes to travel
        /// ±1 back to 0 after release (and the base rate when reversing). 0 = instant.</summary>
        [XmlAttribute] public double ParamReleaseTime { get => _paramReleaseTime; set => _paramReleaseTime = Finite(value, _paramReleaseTime); }
        private double _paramReleaseTime = 0.30;

        /// <summary>Ramped autocenter. <c>true</c> = releasing both keys ramps the
        /// axis back toward zero at the release rate. <c>false</c> = the axis holds
        /// its last value (cruise). The reverse speed-up is gated on this being on.</summary>
        [XmlAttribute] public bool ParamAutocenter { get; set; } = true;

        /// <summary>Ramped reverse multiplier. Applied to the toward-zero step while
        /// the opposite-direction key is held and the axis is still on the original
        /// side, so a direction switch returns to zero faster before attacking the new
        /// side. 1.0 disables the speed-up. Gated on <see cref="ParamAutocenter"/>.</summary>
        [XmlAttribute] public double ParamReverseMultiplier { get => _paramReverseMultiplier; set => _paramReverseMultiplier = Finite(value, _paramReverseMultiplier); }
        private double _paramReverseMultiplier = 4.0;

        // ─── Rapid Trigger (#482) ───

        /// <summary>Rapid Trigger distance in percent of full travel: how far
        /// the input must rise from its deepest point to release, or push back
        /// down from its shallowest point to press again, while it stays past
        /// the <see cref="DeadZone"/> (the actuation point). 1 to
        /// <see cref="MaxRapidTriggerDistance"/>, read through
        /// <see cref="EffectiveRapidTriggerDistance"/>. Only read when
        /// <c>Kind == "RapidTrigger"</c>.</summary>
        [XmlAttribute] public int ParamRapidTriggerDistance { get; set; } = DefaultRapidTriggerDistance;

        /// <summary>minipad-firmware's default, a tenth of the travel
        /// (<c>rapidTriggerUpSensitivity = TRAVEL_DISTANCE_IN_0_01MM / 10</c>,
        /// 0.40 mm of its 4.00 mm switch).</summary>
        public const int DefaultRapidTriggerDistance = 10;

        /// <summary>The longest Rapid Trigger distance. Past half the travel
        /// the input could no longer release and press again inside the zone
        /// a mid-travel deadzone leaves.</summary>
        public const int MaxRapidTriggerDistance = 50;

        /// <summary>The distance the engine and the editors read for a stored
        /// value: 1 to <see cref="MaxRapidTriggerDistance"/> percent. A value
        /// below 1 (a hand-edited profile) reads as the default, since at 0
        /// the input would release and press again on sensor noise.</summary>
        public static int EffectiveRapidTriggerDistance(int stored)
            => stored < 1 ? DefaultRapidTriggerDistance : System.Math.Min(stored, MaxRapidTriggerDistance);

        /// <summary>v3.2 per-source gyro sensitivity multiplier. Applied
        /// to the calibrated gyro rate during bipolar / unipolar coercion
        /// so users tune sensitivity at the row they're authoring instead
        /// of having to walk over to the Sticks group's Left Thumb settings.
        /// Default 1.0 = the engine's GyroScale (500°/s → ±1 deflection).
        /// Values &gt; 1.0 are more sensitive (smaller rotations produce
        /// larger output); values &lt; 1.0 are less sensitive. Only
        /// affects sources whose descriptor starts with "Gyro ".</summary>
        [XmlAttribute] public double GyroSensitivity { get => _gyroSensitivity; set => _gyroSensitivity = Finite(value, _gyroSensitivity); }
        private double _gyroSensitivity = 1.0;

        /// <summary>v3.5 per-source mouse-cursor sensitivity multiplier (issue
        /// #107). Applied to the normalized absolute cursor offset during bipolar
        /// / unipolar / bool coercion so each "Mouse Position X" / "Mouse Position
        /// Y" row tunes its own reach. Default 1.0 = full stick deflection at 10%
        /// of screen width from center. Values &gt; 1.0 reach full deflection with
        /// less cursor travel; values &lt; 1.0 need more. Only affects sources
        /// whose descriptor starts with "Mouse Position ".</summary>
        [XmlAttribute] public double MouseCursorSensitivity { get => _mouseCursorSensitivity; set => _mouseCursorSensitivity = Finite(value, _mouseCursorSensitivity); }
        private double _mouseCursorSensitivity = 1.0;

        /// <summary>Per-source Wii IR-pointer sensitivity multiplier (issue #146).
        /// Applied to the normalized IR pointer offset during bipolar / unipolar /
        /// bool coercion so each "IR Pointer X" / "IR Pointer Y" row tunes its own
        /// reach. Default 1.0 = full stick deflection at the edge of the camera's
        /// field of view. Values &gt; 1.0 reach full deflection with less aim
        /// travel; values &lt; 1.0 need more. Only affects sources whose descriptor
        /// starts with "IR Pointer ".</summary>
        [XmlAttribute] public double IrPointerSensitivity { get => _irPointerSensitivity; set => _irPointerSensitivity = Finite(value, _irPointerSensitivity); }
        private double _irPointerSensitivity = 1.0;

        /// <summary>Generic per-source sensitivity multiplier (issue #9).
        /// Applied to the coerced value of a plain analog source (an
        /// <c>"Axis N"</c> / <c>"Slider N"</c> read, including the abstract
        /// Gamepad sticks and triggers that canonicalize to one) during
        /// bipolar / trigger coercion, then re-clamped to the target range.
        /// Default 1.0 = unchanged. Values &gt; 1.0 reach full deflection
        /// with less travel; values &lt; 1.0 need more. The specialized
        /// families (gyro / mouse cursor / IR pointer) carry their own
        /// sensitivity above and are unaffected by this knob, so a source
        /// only ever exposes one sensitivity slider. Rides every copy leg the
        /// specialized sensitivities ride (memberwise <see cref="Clone"/> plus
        /// the hand-listed VM / push / reload mirrors).</summary>
        [XmlAttribute] public double Sensitivity { get => _sensitivity; set => _sensitivity = Finite(value, _sensitivity); }
        private double _sensitivity = 1.0;

        /// <summary>
        /// v3.3 per-source Do-not-inherit slot. Reserved in the schema for
        /// future per-source / per-zone fall-through suppression
        /// (e.g. "don't inherit the negative half of LeftThumbAxisX from
        /// Base"). The current evaluator uses
        /// <see cref="MappingRow.NoInherit"/> at row granularity; this
        /// field is XML-additive so a future engine pass can light up
        /// per-source semantics without a schema migration.
        /// </summary>
        [XmlAttribute] public bool NoInherit { get; set; } = false;

        // ─── Steering source kinds (v3.4 #94) ───
        // The steering modes are 2D: they read a whole physical stick, not one
        // axis. The row's X axis is in <see cref="Descriptor"/>; the paired Y axis
        // is here. Math sourced from JoyShockMapper src/JoyShock.cpp:1179-1323 and
        // src/main.cpp:887-987 (Electronicks fork bb69784) — read then written
        // original, the geometry only.

        /// <summary>Steering source companion axis. <see cref="Descriptor"/> reads
        /// the stick's X axis; this reads its Y axis (e.g. "Axis 1"). Only used by
        /// the steering kinds (WindingStick / AngleToAxisX / AngleToAxisY).</summary>
        [XmlAttribute] public string ParamYDescriptor { get; set; } = "";

        /// <summary>Radial inner deadzone (0..1) applied to the 2D stick before the
        /// steering math, since the MappingSet raw-axis read applies none. Below it
        /// the stick reads as centered (no wind accrues, no angle output).</summary>
        [XmlAttribute] public double ParamStickDeadzone { get => _paramStickDeadzone; set => _paramStickDeadzone = Finite(value, _paramStickDeadzone); }
        private double _paramStickDeadzone = 0.15;

        // Winding (Kind = "WindingStick"). JSM WIND_STICK_RANGE / WIND_STICK_POWER /
        // UNWIND_RATE (post-d10b51a values).
        [XmlAttribute] public double ParamWindRangeDeg   { get => _paramWindRangeDeg; set => _paramWindRangeDeg = Finite(value, _paramWindRangeDeg); }
        private double _paramWindRangeDeg = 900;
        [XmlAttribute] public double ParamWindPower      { get => _paramWindPower; set => _paramWindPower = Finite(value, _paramWindPower); }
        private double _paramWindPower = 1;
        [XmlAttribute] public double ParamWindUnwindRate { get => _paramWindUnwindRate; set => _paramWindUnwindRate = Finite(value, _paramWindUnwindRate); }
        private double _paramWindUnwindRate = 1800;

        // Angle-to-Axis (Kind = "AngleToAxisX" | "AngleToAxisY"). JSM
        // ANGLE_TO_AXIS_DEADZONE_INNER / _OUTER (degrees from the on-axis / max).
        [XmlAttribute] public double ParamAngleInnerDz   { get => _paramAngleInnerDz; set => _paramAngleInnerDz = Finite(value, _paramAngleInnerDz); }
        private double _paramAngleInnerDz = 0;
        [XmlAttribute] public double ParamAngleOuterDz   { get => _paramAngleOuterDz; set => _paramAngleOuterDz = Finite(value, _paramAngleOuterDz); }
        private double _paramAngleOuterDz = 10;

        // Gravity-tilt envelope ("Gyro Tilt X/Y", #292): degrees of tilt
        // for full deflection (default via GyroTiltDefaultRangeDeg = 25,
        // the modal Steam-corpus deflection max) and a subtract-style
        // inner deadzone. Stamped from the Gyro group's Tilt card per
        // (slot, device), the Motion Steering push pattern. 0 = unset.
        [XmlAttribute] public double ParamTiltRangeDeg   { get => _paramTiltRangeDeg; set => _paramTiltRangeDeg = Finite(value, _paramTiltRangeDeg); }
        private double _paramTiltRangeDeg = 0;
        [XmlAttribute] public double ParamTiltInnerDz    { get => _paramTiltInnerDz; set => _paramTiltInnerDz = Finite(value, _paramTiltInnerDz); }
        private double _paramTiltInnerDz = 0;

        // Motion-Lean (Kind = "MotionLeanX"). JSM MOTION_DEADZONE_INNER / _OUTER
        // (degrees of tilt) and CONTROLLER_ORIENTATION (Forward/Left/Right/Backward).
        [XmlAttribute] public double ParamMotionInnerDz  { get => _paramMotionInnerDz; set => _paramMotionInnerDz = Finite(value, _paramMotionInnerDz); }
        private double _paramMotionInnerDz = 15;
        [XmlAttribute] public double ParamMotionOuterDz  { get => _paramMotionOuterDz; set => _paramMotionOuterDz = Finite(value, _paramMotionOuterDz); }
        private double _paramMotionOuterDz = 135;
        [XmlAttribute] public string ParamControllerOrientation { get; set; } = "Forward";

        // ─── Flick stick (#225, descriptor family "Flick Stick Right"/"Flick
        // Stick Left" on a KbmMouseX row). Math ported from JoyShockMapper
        // JoyShock.cpp handleFlickStick (:852-1017), read then written
        // original. Defaults mirror JSM's registered settings (main.cpp)
        // except where named. Tuning is per-source so a Workshop import's
        // per-group values survive materialization; the pad-page card
        // re-stamps these on save (ApplyFlickStickParamsToRow, the Motion
        // Steering push pattern). ───

        /// <summary>Flick easing duration in seconds for a full 180° flick
        /// (shorter turns complete in the same time; JSM FLICK_TIME,
        /// main.cpp:2262 default 0.1).</summary>
        [XmlAttribute] public double ParamFlickTime { get => _paramFlickTime; set => _paramFlickTime = Finite(value, _paramFlickTime); }
        private double _paramFlickTime = 0.1;

        /// <summary>Mouse counts emitted per full 360° camera turn (Steam
        /// "Dots Per 360°"; the wild corpus stores it in the group's
        /// "sensitivity" key). JSM expresses the same scale as
        /// REAL_WORLD_CALIBRATION counts-per-degree (default 40,
        /// main.cpp:2190) → 40 × 360 = 14400 here.</summary>
        [XmlAttribute] public double ParamFlickCountsPer360 { get => _paramFlickCountsPer360; set => _paramFlickCountsPer360 = Finite(value, _paramFlickCountsPer360); }
        private double _paramFlickCountsPer360 = 14400;

        /// <summary>Stick deflection (raw magnitude, 0..1) at which a flick
        /// engages. JSM checks length ≥ 1.0 on a deadzone-remapped read
        /// (JoyShock.cpp:864-869) whose outer deadzone defaults to 0.1
        /// (main.cpp:2334), i.e. raw ≥ 0.9; the remap is folded into this
        /// knob directly. JSM's 0.9× release hysteresis is applied on top
        /// while flicking (JoyShock.cpp:865-868).</summary>
        [XmlAttribute] public double ParamFlickThreshold { get => _paramFlickThreshold; set => _paramFlickThreshold = Finite(value, _paramFlickThreshold); }
        private double _paramFlickThreshold = 0.9;

        /// <summary>Flick snap mode: "None" (default), "Forward", "Half",
        /// "Four", "Sixths", "Eight". JSM FLICK_SNAP_MODE ships
        /// NONE/FOUR/EIGHT (JoyShockMapper.h:300-306, default NONE
        /// main.cpp:2144) with a 180° snap interval as the switch fallback
        /// (JoyShock.cpp:883); "Half"/"Sixths"/"Forward" complete Steam's
        /// snap vocabulary (NoSnap/ForwardOnly/Half/Quarter/Sixths/Eighths)
        /// through the same round-to-interval math. String, not a
        /// serialized enum, so the value set stays append-only.</summary>
        [XmlAttribute] public string ParamFlickSnapMode { get; set; } = "None";

        /// <summary>Snap lerp strength 0..1 (JSM FLICK_SNAP_STRENGTH,
        /// main.cpp:2420 default 1.0 = full snap).</summary>
        [XmlAttribute] public double ParamFlickSnapStrength { get => _paramFlickSnapStrength; set => _paramFlickSnapStrength = Finite(value, _paramFlickSnapStrength); }
        private double _paramFlickSnapStrength = 1.0;

        /// <summary>Forward angle deadzone in degrees: a flick whose angle is
        /// within this of dead-ahead reads as 0° (JSM FLICK_DEADZONE_ANGLE,
        /// main.cpp:2322 default 0; Steam "Front Angle Deadzone").</summary>
        [XmlAttribute] public double ParamFlickDeadzoneAngle { get => _paramFlickDeadzoneAngle; set => _paramFlickDeadzoneAngle = Finite(value, _paramFlickDeadzoneAngle); }
        private double _paramFlickDeadzoneAngle = 0;

        /// <summary>Rotation smoothing threshold in radians-per-tick.
        /// Negative (default) = JSM's automatic tiered window (full
        /// smoothing at 0.02 rad/tick, none above 0.04, JoyShock.cpp:927-933);
        /// 0 disables smoothing; positive overrides the lower threshold
        /// (upper = 2×). JSM ROTATE_SMOOTH_OVERRIDE, main.cpp:2414
        /// default -1.</summary>
        [XmlAttribute] public double ParamFlickSmooth { get => _paramFlickSmooth; set => _paramFlickSmooth = Finite(value, _paramFlickSmooth); }
        private double _paramFlickSmooth = -1;

        /// <summary>Constant rotation added to the flick angle, in degrees
        /// (positive = clockwise / rightward). Steam's flickstick
        /// "rotation" group setting: the whole input map rotates, so the
        /// offset lands on the flick angle before snapping; rim-rotation
        /// deltas are invariant under a constant offset. Default 0.</summary>
        [XmlAttribute] public double ParamFlickRotationOffsetDeg { get => _paramFlickRotationOffsetDeg; set => _paramFlickRotationOffsetDeg = Finite(value, _paramFlickRotationOffsetDeg); }
        private double _paramFlickRotationOffsetDeg = 0;

        // ─── Absolute touchpad pointer region window (#9 B-15, descriptor
        // family "Touchpad N Pointer X/Y[ Left|Right]"). The translator's
        // channel for Steam's mouse_region geometry: position_x/position_y
        // (region center as percent of screen; shipped configurator ids
        // PositionXMouse / PositionYMouse) and scale x
        // sensitivity_horiz/vert_scale (region size per axis; ids
        // ScaleMouseRegion / Horizontal-/VerticalSensitivityMouseRegion).
        // Each source drives ONE axis, so one center + one extent per
        // source. Defaults are the identity full-screen map. ───

        /// <summary>Region center along this source's screen axis,
        /// normalized 0..1 (0.5 = screen center). Only read by the
        /// "Touchpad N Pointer ..." family.</summary>
        [XmlAttribute] public double ParamPointerCenter { get => _paramPointerCenter; set => _paramPointerCenter = Finite(value, _paramPointerCenter); }
        private double _paramPointerCenter = 0.5;

        /// <summary>Region extent along this source's screen axis as a
        /// fraction of the full axis (1.0 = the whole screen, 0.1 = a
        /// minimap-sized band). The tuned pad position scales by this
        /// around the region center, then clamps to the screen.</summary>
        [XmlAttribute] public double ParamPointerExtent { get => _paramPointerExtent; set => _paramPointerExtent = Finite(value, _paramPointerExtent); }
        private double _paramPointerExtent = 1.0;

        // ─── Response curve / outer range (translator v11). The Workshop
        // translator's per-source lane for Steam's stick response-curve
        // cluster: curve_exponent / custom_curve_exponent land on
        // ParamCurveExponent, deadzone_outer_radius on ParamRangeOuter.
        // Applied ONLY in the generic bipolar tail of
        // SourceCoercion.ReadAsBipolar, after Sensitivity, outer range
        // first and then the exponent, sign carried via magnitude math.
        // Defaults are 0 = off, so existing XML and every hand-authored
        // source keep exact pass-through behavior. ───

        /// <summary>Sign-preserving output shaping: magnitude maps
        /// |x| to |x|^e, sign carried through unchanged. 0 (default) and
        /// 1 mean off. The engine's named curve presets correspond to
        /// e = 0.5 (Relaxed), 1.5 (Wide), 2 (Aggressive), 2.5 (ExtraWide),
        /// the same shapes as SourceCoercion.ApplyOutputCurve.</summary>
        [XmlAttribute] public double ParamCurveExponent { get => _paramCurveExponent; set => _paramCurveExponent = Finite(value, _paramCurveExponent); }
        private double _paramCurveExponent;

        /// <summary>Outer range as a 0..1 fraction of full deflection.
        /// The output magnitude rescales v = min(1, |v| / outer), so full
        /// deflection is reached AT this radius (Steam's
        /// deadzone_outer_radius / 32767). 0 (default) = off.</summary>
        [XmlAttribute] public double ParamRangeOuter { get => _paramRangeOuter; set => _paramRangeOuter = Finite(value, _paramRangeOuter); }
        private double _paramRangeOuter;

        /// <summary>Output anti-deadzone floor as a 0..1 fraction (v18,
        /// Steam's anti_deadzone / 32767): a non-zero shaped magnitude is
        /// remapped to floor + (1 - floor) * mag, so the smallest real
        /// input starts at the floor. Applied inside the same
        /// curve/range shaping seam, after the exponent. 0 = off.</summary>
        [XmlAttribute] public double ParamAntiDeadzone { get => _paramAntiDeadzone; set => _paramAntiDeadzone = Finite(value, _paramAntiDeadzone); }
        private double _paramAntiDeadzone;

        /// <summary>Stick-read deadzone geometry (translator v25, Steam's
        /// deadzone_shape on the stick-hosted analog pair modes): 0
        /// (default) = off, the untouched read. 1 = axial:
        /// <see cref="ParamStickDeadZoneInner"/> and ParamRangeOuter
        /// rescale this axis by its own magnitude (Steam Cross / Square,
        /// the per-axis check). 2 = radial: the rescale factor is
        /// computed from the stick PAIR magnitude (this axis and its
        /// companion, Axis 0/1 or 3/4), so the dead region is a circle
        /// and diagonals shape correctly (Steam Circle). Applied inside
        /// the generic bipolar Axis read before sensitivity; when set,
        /// ParamRangeOuter is consumed here instead of the per-axis
        /// shaping tail. Retires the v19 "no per-source companion-axis
        /// channel" residual for every stick-hosted pair emitter (mouse
        /// lanes since v25, thumb pairs since the round-six completion).
        /// CODING TRAP: this 1 = axial / 2 = radial coding is per-source
        /// only. The slot-level DeadZoneShape enum codes Axial = 0 /
        /// Radial = 1 / ScaledRadial = 2, so the value 1 means opposite
        /// things in the two channels; never copy one into the
        /// other.</summary>
        [XmlAttribute] public int ParamStickDeadZoneShape { get; set; }

        /// <summary>Inner radius for <see cref="ParamStickDeadZoneShape"/>
        /// as a 0..1 fraction of full deflection (Steam's
        /// deadzone_inner_radius / 32767). Its own field on purpose:
        /// <see cref="DeadZone"/> defaults to 50 as the BUTTON coercion's
        /// "engine default threshold" sentinel, so reading it as an analog
        /// inner radius would put a silent 50 percent hole in every
        /// unauthored source. 0 (default) = no inner deadzone.</summary>
        [XmlAttribute] public double ParamStickDeadZoneInner { get => _paramStickDeadZoneInner; set => _paramStickDeadZoneInner = Finite(value, _paramStickDeadZoneInner); }
        private double _paramStickDeadZoneInner;

        /// <summary>Rate-dependent gain (v18, Steam's acceleration on the
        /// mouse modes): the touchpad delta scales by 1 + accel * |v|,
        /// the ApplyGyroAcceleration formula on the cursor lane.
        /// 0 = off.</summary>
        [XmlAttribute] public double ParamAccel { get => _paramAccel; set => _paramAccel = Finite(value, _paramAccel); }
        private double _paramAccel;

        /// <summary>Optional per-source AND companion (v18): the source
        /// contributes only while this second descriptor reads true on
        /// the same device, evaluated through the chord-second-leg
        /// button read. Carries Steam's requires_click D-pad gate and
        /// the single-pad click-on-half gates without spending the
        /// row-level combine, so multi-source rows keep per-source
        /// gates. Empty = ungated.</summary>
        [XmlAttribute] public string GateDescriptor { get; set; } = "";

        /// <summary>Second AND companion (v26): a chord partner on a host
        /// whose primary gate slot is already spent (a single-pad trackpad
        /// D-pad wedge gated on its half's contact, chorded with another
        /// button). Both gates must hold. Empty = no second gate.</summary>
        [XmlAttribute] public string Gate2Descriptor { get; set; } = "";

        /// <summary>Arm behavior when evaluation (re)starts with the stick
        /// already past the threshold (the shift-layer engage case, #225).
        /// <c>false</c> (default): arm at the current angle and track
        /// rotation from there, firing no flick. <c>true</c>: fire the
        /// flick immediately, JSM's behavior (is_flicking starts false, so
        /// a deflected stick is a "new flick!", JoyShock.cpp:873-876) and
        /// Steam's "Allow Flick on Awake" ON. The default diverges from
        /// JSM deliberately: PadForge's layer host means the same stick is
        /// usually live on the Base layer at engage time, and JSM itself
        /// suppresses mode-shift transition artifacts on its return path
        /// (Stick.h:84-87 ignore_stick_mode, "Modeshifting the stick mode
        /// can create quirky behaviors on transition").</summary>
        [XmlAttribute] public bool ParamFlickOnEngage { get; set; } = false;

        // ─── Mouse Travel (#597, descriptor family "Mouse Travel X" /
        // "Mouse Travel Y"). A mouse's raw counts are added up and held as
        // a position from -1 to 1, the way a hand holds a wheel
        // (SourceKindRuntime.TickMouseTravel). The input that Steer waits
        // on is the source's own gate (GateDescriptor). Each field below
        // is left out of the file while it holds its default, so a source
        // that reads another input saves as it did before the family
        // existed, and a file from before it loads with the defaults. ───

        public const double DefaultTravelCounts = 2000, MinTravelCounts = 100, MaxTravelCounts = 50000;
        public const double DefaultTravelReturnTime = 0.75, MinTravelReturnTime = 0.05, MaxTravelReturnTime = 5.0;
        public const double MaxTravelSmoothingMs = 100;
        public const double DefaultTravelLockAtSpeed = 100, MinTravelLockAtSpeed = 20, MaxTravelLockAtSpeed = 100;

        /// <summary>The values of <see cref="ParamTravelCentering"/>.</summary>
        public const string TravelCenteringOff = "Off", TravelCenteringSpring = "Spring",
            TravelCenteringTelemetry = "Telemetry", TravelCenteringGameForce = "GameForce";

        /// <summary>The values of <see cref="ParamTravelSteer"/>.</summary>
        public const string TravelSteerAlways = "Always", TravelSteerHeld = "Held", TravelSteerToggle = "Toggle";

        /// <summary>Travel to Full Lock: the counts a mouse moves from center
        /// to full lock, <see cref="MinTravelCounts"/> to
        /// <see cref="MaxTravelCounts"/>, read through
        /// <see cref="EffectiveTravelCounts"/>.</summary>
        [XmlAttribute] public double ParamTravelCounts { get => _paramTravelCounts; set => _paramTravelCounts = Finite(value, _paramTravelCounts); }
        private double _paramTravelCounts = DefaultTravelCounts;
        public bool ShouldSerializeParamTravelCounts() => _paramTravelCounts != DefaultTravelCounts;

        /// <summary>Centering: what brings the wheel back. "Off" holds it
        /// where the hand left it, "Spring" returns it over
        /// <see cref="ParamTravelReturnTime"/>, "Telemetry" returns it as a
        /// car's front tires would at the game's speed and grip, and
        /// "GameForce" lets the force a game sends the slot's virtual wheel
        /// turn it. A string and no serialized enum, so the set only grows.
        /// A value this build does not know reads as Off
        /// (<see cref="EffectiveTravelCentering"/>).</summary>
        [XmlAttribute] public string ParamTravelCentering { get; set; } = TravelCenteringOff;
        public bool ShouldSerializeParamTravelCentering()
            => !string.IsNullOrEmpty(ParamTravelCentering) && ParamTravelCentering != TravelCenteringOff;

        /// <summary>Return Time in seconds: how long Spring takes to bring a
        /// let-go wheel 95% of the way back, and Telemetry at 100 km/h on a
        /// dry road. <see cref="MinTravelReturnTime"/> to
        /// <see cref="MaxTravelReturnTime"/>, read through
        /// <see cref="EffectiveTravelReturnTime"/>.</summary>
        [XmlAttribute] public double ParamTravelReturnTime { get => _paramTravelReturnTime; set => _paramTravelReturnTime = Finite(value, _paramTravelReturnTime); }
        private double _paramTravelReturnTime = DefaultTravelReturnTime;
        public bool ShouldSerializeParamTravelReturnTime() => _paramTravelReturnTime != DefaultTravelReturnTime;

        /// <summary>Steer: when the mouse turns the wheel. "Always", "Held"
        /// (only while the gate's input is held) or "Toggle" (each press of
        /// the gate's input turns steering on or off). With no gate picked
        /// the mouse always steers. A gate beside "Always" holds the mouse
        /// as it does beside "Held", the way a gate holds any source, and
        /// the editor shows such a source as While Held and keeps no gate
        /// under Always. A value this build does not know reads as Always
        /// (<see cref="EffectiveTravelSteer"/>).</summary>
        [XmlAttribute] public string ParamTravelSteer { get; set; } = TravelSteerAlways;
        public bool ShouldSerializeParamTravelSteer()
            => !string.IsNullOrEmpty(ParamTravelSteer) && ParamTravelSteer != TravelSteerAlways;

        /// <summary>Smoothing in milliseconds, 0 to
        /// <see cref="MaxTravelSmoothingMs"/>: the time constant the output
        /// follows the wheel with, for a mouse that reports unevenly. 0 is
        /// off.</summary>
        [XmlAttribute] public double ParamTravelSmoothingMs { get => _paramTravelSmoothingMs; set => _paramTravelSmoothingMs = Finite(value, _paramTravelSmoothingMs); }
        private double _paramTravelSmoothingMs;
        public bool ShouldSerializeParamTravelSmoothingMs() => _paramTravelSmoothingMs != 0;

        /// <summary>Lock at Speed in percent: how much of the lock is left
        /// at 200 km/h, <see cref="MinTravelLockAtSpeed"/> to
        /// <see cref="MaxTravelLockAtSpeed"/>. 100
        /// leaves the lock alone at every speed.</summary>
        [XmlAttribute] public double ParamTravelLockAtSpeed { get => _paramTravelLockAtSpeed; set => _paramTravelLockAtSpeed = Finite(value, _paramTravelLockAtSpeed); }
        private double _paramTravelLockAtSpeed = DefaultTravelLockAtSpeed;
        public bool ShouldSerializeParamTravelLockAtSpeed() => _paramTravelLockAtSpeed != DefaultTravelLockAtSpeed;

        /// <summary>The counts to full lock the engine and the editors read
        /// for a stored value. A value at or under 0 (a hand-edited profile)
        /// reads as the default, since no travel reaches a lock at 0.</summary>
        public static double EffectiveTravelCounts(double stored)
            => stored > 0 ? System.Math.Clamp(stored, MinTravelCounts, MaxTravelCounts) : DefaultTravelCounts;

        /// <summary>The Return Time the engine and the editors read for a
        /// stored value, by the rule of <see cref="EffectiveTravelCounts"/>.</summary>
        public static double EffectiveTravelReturnTime(double stored)
            => stored > 0 ? System.Math.Clamp(stored, MinTravelReturnTime, MaxTravelReturnTime) : DefaultTravelReturnTime;

        /// <summary>The Smoothing the engine and the editors read for a
        /// stored value.</summary>
        public static double EffectiveTravelSmoothingMs(double stored)
            => System.Math.Clamp(stored, 0, MaxTravelSmoothingMs);

        /// <summary>The Lock at Speed the engine and the editors read for a
        /// stored value. A value at or under 0 reads as 100, which leaves
        /// the lock alone.</summary>
        public static double EffectiveTravelLockAtSpeed(double stored)
            => stored > 0 ? System.Math.Clamp(stored, MinTravelLockAtSpeed, MaxTravelLockAtSpeed) : DefaultTravelLockAtSpeed;

        /// <summary>The Centering the engine and the editors read for a
        /// stored value: one of the four, Off for anything else.</summary>
        public static string EffectiveTravelCentering(string stored)
            => stored is TravelCenteringSpring or TravelCenteringTelemetry or TravelCenteringGameForce
                ? stored : TravelCenteringOff;

        /// <summary>The Steer the engine and the editors read for a stored
        /// value: one of the three, Always for anything else.</summary>
        public static string EffectiveTravelSteer(string stored)
            => stored is TravelSteerHeld or TravelSteerToggle ? stored : TravelSteerAlways;

        // ─── Runtime "Menu {id} Item {k}" parse cache (not serialized:
        // XmlSerializer ignores non-public members). The per-tick readers
        // re-split the descriptor per row per tick on the poll thread;
        // caching the parse here makes the hot read a reference compare
        // (SourceCoercion.TryParseMenuItemCached). Poll thread is the only
        // reader and writer. The UI thread can swap Descriptor mid-tick,
        // so the key is compared by reference and a one-tick stale parse
        // is accepted. MemberwiseClone copying the fields stays valid:
        // the clone shares the same Descriptor reference. ───
        internal string MenuParseKey;
        internal int MenuParseMenuId = -1;
        internal int MenuParseItemIndex = -1;

        // ─── Runtime gate-source cache (v18, not serialized): the
        // synthetic Direct source the evaluators read GateDescriptor
        // through, rebuilt only when the descriptor reference changes
        // (same reference-compare contract as the menu parse cache). ───
        internal MappingSource GateSourceCache;
        internal string GateSourceCacheKey;
        internal MappingSource Gate2SourceCache;
        internal string Gate2SourceCacheKey;

        /// <summary>Every double here keeps its value when handed NaN or
        /// infinity, from the settings file, a view model or a copy alike, so
        /// a hand-edited "NaN" never reaches the engine. Every lane assigns
        /// through these setters (XmlSerializer, ToDomain, deep clones, the
        /// steering stamp), and a fresh source keeps its default.</summary>
        private static double Finite(double value, double current)
            => double.IsFinite(value) ? value : current;

        /// <summary>Moves each key's controller through
        /// <paramref name="retarget"/>, an old device guid to its new one, or
        /// to null where there is none, and drops a key that has none. A key
        /// that reads any controller, or carries none of its own, stays as it
        /// is. True when every key the kind reads was dropped, so the source
        /// reads nothing any more. The controller of a kept input
        /// (<see cref="DescriptorDeviceGuid"/>) moves the same way, and has
        /// no part in that answer.</summary>
        public bool RetargetKeyDevices(System.Func<string, string> retarget)
        {
            bool upDown = Kind is "Incremental" or "Ramped";
            bool modifier = Kind == "InvertOnHold";
            int read = (upDown && !string.IsNullOrEmpty(ParamUp) ? 1 : 0)
                + (upDown && !string.IsNullOrEmpty(ParamDown) ? 1 : 0)
                + (modifier && !string.IsNullOrEmpty(ParamModifier) ? 1 : 0);
            (ParamUp, ParamUpDeviceGuid, bool upGone) = RetargetKey(ParamUp, ParamUpDeviceGuid, retarget);
            (ParamDown, ParamDownDeviceGuid, bool downGone) = RetargetKey(ParamDown, ParamDownDeviceGuid, retarget);
            (ParamModifier, ParamModifierDeviceGuid, bool modifierGone) =
                RetargetKey(ParamModifier, ParamModifierDeviceGuid, retarget);
            int dropped = (upDown && upGone ? 1 : 0) + (upDown && downGone ? 1 : 0)
                + (modifier && modifierGone ? 1 : 0);
            RetargetDescriptorDevice(retarget, keptOnly: upDown || modifier);
            return read > 0 && dropped == read;
        }

        /// <summary>Moves <see cref="DescriptorDeviceGuid"/> through
        /// <paramref name="retarget"/>. Where its controller has no
        /// counterpart the kept input goes with it, as a key does: left
        /// behind, a later return to a kind that reads the Descriptor read
        /// it on the keys' controller, where nobody picked it. A kind that
        /// reads its Descriptor (<paramref name="keptOnly"/> false) reads it
        /// on the source's own device, so a value there is no memory, and
        /// it is dropped alone.</summary>
        private void RetargetDescriptorDevice(System.Func<string, string> retarget, bool keptOnly)
        {
            if (string.IsNullOrEmpty(DescriptorDeviceGuid)) return;
            string to = keptOnly ? retarget(DescriptorDeviceGuid) : null;
            DescriptorDeviceGuid = to;
            if (to == null && keptOnly) Descriptor = "";
        }

        private static (string key, string device, bool dropped) RetargetKey(
            string key, string device, System.Func<string, string> retarget)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(device)) return (key, device, false);
            string to = retarget(device);
            return to == null ? ("", null, true) : (key, to, false);
        }

        /// <summary>Full-field copy. Every serialized field is a string / value type
        /// (strings are immutable), so a memberwise clone is a complete deep copy.
        /// Use this at clone sites instead of hand-listing fields, so a new Param*
        /// can't silently drop on profile switch / slot reassign (the bug that
        /// dropped the steering params and, before that, GyroSensitivity /
        /// NoInherit). Runtime caches do NOT travel: the gate-source cache holds
        /// a synthetic source built with the ORIGINAL's device guid, so a clone
        /// that is retargeted to another device would keep evaluating its gate
        /// against the stale device. The menu parse cache
        /// stays: it is keyed by Descriptor reference, which the clone shares.</summary>
        public MappingSource Clone()
        {
            var copy = (MappingSource)MemberwiseClone();
            copy.GateSourceCache = null;
            copy.GateSourceCacheKey = null;
            copy.Gate2SourceCache = null;
            copy.Gate2SourceCacheKey = null;
            return copy;
        }
    }
}
