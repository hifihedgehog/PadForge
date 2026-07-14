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
    /// v1 ships <c>Direct</c> only. <c>Incremental</c> (B2) and
    /// <c>InvertOnHold</c> (B3) follow in Commit 4 of the multi-source
    /// recipe. The schema reserves the <c>Param*</c> bag now so adding
    /// kinds later is XML-additive.
    /// </para>
    /// </summary>
    public class MappingSource
    {
        /// <summary>Source kind discriminator. <c>"Direct"</c> (default),
        /// <c>"Incremental"</c>, <c>"InvertOnHold"</c>, <c>"Ramped"</c>, plus the
        /// steering kinds. Forward-compatible: unknown values treated as Direct.</summary>
        [XmlAttribute] public string Kind { get; set; } = "Direct";

        /// <summary>Device this source reads from. Empty string means "first
        /// available device on the VC." Resolved per frame in Step 3.</summary>
        [XmlAttribute] public string DeviceGuid { get; set; } = "";

        /// <summary>Input descriptor in the existing format used by the Step 3
        /// mapping engine: <c>"Button N"</c>, <c>"Axis N"</c>, <c>"IHAxis N"</c>,
        /// <c>"POV N Dir"</c>, <c>"Slider N"</c>, or <c>""</c> (unmapped).
        /// For <c>InvertOnHold</c> kind, this is the inner source's input.
        /// Ignored for <c>Incremental</c>.</summary>
        [XmlAttribute] public string Descriptor { get; set; } = "";

        /// <summary>When <c>true</c>, flip the per-source value sign before
        /// combine. For button-class sources mapped to bipolar axis targets
        /// this is what produces the "negative direction" half of a paddle
        /// pair (pressed → -1 instead of +1).</summary>
        [XmlAttribute] public bool Invert { get; set; }

        /// <summary>When <c>true</c>, treat a bipolar axis source as
        /// half-axis (only the positive or negative half maps to the
        /// 0..+1 output range, depending on <see cref="Invert"/>).</summary>
        [XmlAttribute] public bool HalfAxis { get; set; }

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

        /// <summary>Incremental.up — descriptor of the button that ramps the
        /// accumulator upward while held. Only read when <c>Kind == "Incremental"</c>.</summary>
        [XmlAttribute] public string ParamUp { get; set; } = "";

        /// <summary>Incremental.down — descriptor of the button that ramps the
        /// accumulator downward while held.</summary>
        [XmlAttribute] public string ParamDown { get; set; } = "";

        /// <summary>Incremental rate in units-per-second (full output range
        /// is 1.0 unit, so 0.5 means full sweep takes 2 s).</summary>
        [XmlAttribute] public double ParamRate { get; set; } = 0.5;

        /// <summary>Incremental sticky behavior. <c>true</c> = value holds
        /// when neither up nor down is held (cruise control). <c>false</c> =
        /// snaps back to <see cref="ParamMin"/> when neither held (manual ramp).</summary>
        [XmlAttribute] public bool ParamSticky { get; set; } = true;

        /// <summary>Incremental clamp lower bound.</summary>
        [XmlAttribute] public double ParamMin { get; set; } = 0;

        /// <summary>Incremental clamp upper bound.</summary>
        [XmlAttribute] public double ParamMax { get; set; } = 1;

        /// <summary>InvertOnHold modifier — descriptor of the button that
        /// inverts the inner source while held. Only read when
        /// <c>Kind == "InvertOnHold"</c>.</summary>
        [XmlAttribute] public string ParamModifier { get; set; } = "";

        // ─── Ramped axis envelope (v3.5 #111) ───
        // A keyboard-to-axis time-based ramp. The positive-direction key (ParamUp)
        // and negative-direction key (ParamDown) drive a bipolar accumulator in
        // [-1, +1] that attacks toward the held side over ParamAttackTime and
        // releases back toward zero over ParamReleaseTime. Math ported from the
        // KBM2Gamepad / FreePIE throttle logic referenced in the issue.

        /// <summary>Ramped attack time in seconds: how long the axis takes to travel
        /// 0 to ±1 while the matching-direction key is held. 0 = instant. Only read
        /// when <c>Kind == "Ramped"</c>.</summary>
        [XmlAttribute] public double ParamAttackTime { get; set; } = 0.30;

        /// <summary>Ramped release time in seconds: how long the axis takes to travel
        /// ±1 back to 0 after release (and the base rate when reversing). 0 = instant.</summary>
        [XmlAttribute] public double ParamReleaseTime { get; set; } = 0.30;

        /// <summary>Ramped autocenter. <c>true</c> = releasing both keys ramps the
        /// axis back toward zero at the release rate. <c>false</c> = the axis holds
        /// its last value (cruise). The reverse speed-up is gated on this being on.</summary>
        [XmlAttribute] public bool ParamAutocenter { get; set; } = true;

        /// <summary>Ramped reverse multiplier. Applied to the toward-zero step while
        /// the opposite-direction key is held and the axis is still on the original
        /// side, so a direction switch returns to zero faster before attacking the new
        /// side. 1.0 disables the speed-up. Gated on <see cref="ParamAutocenter"/>.</summary>
        [XmlAttribute] public double ParamReverseMultiplier { get; set; } = 4.0;

        /// <summary>v3.2 per-source gyro sensitivity multiplier. Applied
        /// to the calibrated gyro rate during bipolar / unipolar coercion
        /// so users tune sensitivity at the row they're authoring instead
        /// of having to walk over to the Sticks tab's Left Thumb settings.
        /// Default 1.0 = the engine's GyroScale (500°/s → ±1 deflection).
        /// Values &gt; 1.0 are more sensitive (smaller rotations produce
        /// larger output); values &lt; 1.0 are less sensitive. Only
        /// affects sources whose descriptor starts with "Gyro ".</summary>
        [XmlAttribute] public double GyroSensitivity { get; set; } = 1.0;

        /// <summary>v3.5 per-source mouse-cursor sensitivity multiplier (issue
        /// #107). Applied to the normalized absolute cursor offset during bipolar
        /// / unipolar / bool coercion so each "Mouse Position X" / "Mouse Position
        /// Y" row tunes its own reach. Default 1.0 = full stick deflection at 10%
        /// of screen width from center. Values &gt; 1.0 reach full deflection with
        /// less cursor travel; values &lt; 1.0 need more. Only affects sources
        /// whose descriptor starts with "Mouse Position ".</summary>
        [XmlAttribute] public double MouseCursorSensitivity { get; set; } = 1.0;

        /// <summary>Per-source Wii IR-pointer sensitivity multiplier (issue #146).
        /// Applied to the normalized IR pointer offset during bipolar / unipolar /
        /// bool coercion so each "IR Pointer X" / "IR Pointer Y" row tunes its own
        /// reach. Default 1.0 = full stick deflection at the edge of the camera's
        /// field of view. Values &gt; 1.0 reach full deflection with less aim
        /// travel; values &lt; 1.0 need more. Only affects sources whose descriptor
        /// starts with "IR Pointer ".</summary>
        [XmlAttribute] public double IrPointerSensitivity { get; set; } = 1.0;

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
        [XmlAttribute] public double Sensitivity { get; set; } = 1.0;

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
        [XmlAttribute] public double ParamStickDeadzone { get; set; } = 0.15;

        // Winding (Kind = "WindingStick"). JSM WIND_STICK_RANGE / WIND_STICK_POWER /
        // UNWIND_RATE (post-d10b51a values).
        [XmlAttribute] public double ParamWindRangeDeg   { get; set; } = 900;
        [XmlAttribute] public double ParamWindPower      { get; set; } = 1;
        [XmlAttribute] public double ParamWindUnwindRate { get; set; } = 1800;

        // Angle-to-Axis (Kind = "AngleToAxisX" | "AngleToAxisY"). JSM
        // ANGLE_TO_AXIS_DEADZONE_INNER / _OUTER (degrees from the on-axis / max).
        [XmlAttribute] public double ParamAngleInnerDz   { get; set; } = 0;
        [XmlAttribute] public double ParamAngleOuterDz   { get; set; } = 10;

        // Motion-Lean (Kind = "MotionLeanX"). JSM MOTION_DEADZONE_INNER / _OUTER
        // (degrees of tilt) and CONTROLLER_ORIENTATION (Forward/Left/Right/Backward).
        [XmlAttribute] public double ParamMotionInnerDz  { get; set; } = 15;
        [XmlAttribute] public double ParamMotionOuterDz  { get; set; } = 135;
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
        [XmlAttribute] public double ParamFlickTime { get; set; } = 0.1;

        /// <summary>Mouse counts emitted per full 360° camera turn (Steam
        /// "Dots Per 360°"; the wild corpus stores it in the group's
        /// "sensitivity" key). JSM expresses the same scale as
        /// REAL_WORLD_CALIBRATION counts-per-degree (default 40,
        /// main.cpp:2190) → 40 × 360 = 14400 here.</summary>
        [XmlAttribute] public double ParamFlickCountsPer360 { get; set; } = 14400;

        /// <summary>Stick deflection (raw magnitude, 0..1) at which a flick
        /// engages. JSM checks length ≥ 1.0 on a deadzone-remapped read
        /// (JoyShock.cpp:864-869) whose outer deadzone defaults to 0.1
        /// (main.cpp:2334), i.e. raw ≥ 0.9; the remap is folded into this
        /// knob directly. JSM's 0.9× release hysteresis is applied on top
        /// while flicking (JoyShock.cpp:865-868).</summary>
        [XmlAttribute] public double ParamFlickThreshold { get; set; } = 0.9;

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
        [XmlAttribute] public double ParamFlickSnapStrength { get; set; } = 1.0;

        /// <summary>Forward angle deadzone in degrees: a flick whose angle is
        /// within this of dead-ahead reads as 0° (JSM FLICK_DEADZONE_ANGLE,
        /// main.cpp:2322 default 0; Steam "Front Angle Deadzone").</summary>
        [XmlAttribute] public double ParamFlickDeadzoneAngle { get; set; } = 0;

        /// <summary>Rotation smoothing threshold in radians-per-tick.
        /// Negative (default) = JSM's automatic tiered window (full
        /// smoothing at 0.02 rad/tick, none above 0.04, JoyShock.cpp:927-933);
        /// 0 disables smoothing; positive overrides the lower threshold
        /// (upper = 2×). JSM ROTATE_SMOOTH_OVERRIDE, main.cpp:2414
        /// default -1.</summary>
        [XmlAttribute] public double ParamFlickSmooth { get; set; } = -1;

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
        [XmlAttribute] public double ParamPointerCenter { get; set; } = 0.5;

        /// <summary>Region extent along this source's screen axis as a
        /// fraction of the full axis (1.0 = the whole screen, 0.1 = a
        /// minimap-sized band). The tuned pad position scales by this
        /// around the region center, then clamps to the screen.</summary>
        [XmlAttribute] public double ParamPointerExtent { get; set; } = 1.0;

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
        /// can create quirky behaviours on transition").</summary>
        [XmlAttribute] public bool ParamFlickOnEngage { get; set; } = false;

        /// <summary>Full-field copy. Every field is a string / value type (strings are
        /// immutable), so a memberwise clone is a complete deep copy. Use this at clone
        /// sites instead of hand-listing fields, so a new Param* can't silently drop on
        /// profile switch / slot reassign (the bug that dropped the steering params and,
        /// before that, GyroSensitivity / NoInherit).</summary>
        public MappingSource Clone() => (MappingSource)MemberwiseClone();
    }
}
