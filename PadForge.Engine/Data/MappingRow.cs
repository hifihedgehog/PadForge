using System.Collections.Generic;
using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// One Xbox-360-output row in a virtual controller's mapping table.
    /// Carries N <see cref="MappingSource"/> entries combined via
    /// <see cref="CombineMode"/>.
    ///
    /// <para>
    /// Targets use the same names as today's <see cref="PadSetting"/>
    /// mapping fields: <c>"ButtonA"</c>, <c>"LeftThumbAxisX"</c>,
    /// <c>"LeftTrigger"</c>, <c>"DPadUp"</c>, etc. The Step 3 engine
    /// dispatches by <see cref="Target"/> name to the right Gamepad field.
    /// </para>
    ///
    /// <para>
    /// <see cref="LayerMask"/> is used by the Shift-layer recipe (Issue
    /// #61-C). <c>"Base"</c> rows are always live; non-Base rows override
    /// matching <see cref="Target"/>s while their layer is active. Old
    /// configs without <see cref="LayerMask"/> deserialize as Base.
    /// </para>
    /// </summary>
    public class MappingRow
    {
        /// <summary>Output target name. Must match a known
        /// <see cref="PadSetting"/> mapping field (e.g. <c>"ButtonA"</c>,
        /// <c>"LeftThumbAxisX"</c>, <c>"LeftTrigger"</c>, <c>"DPadUp"</c>),
        /// or one of the slot-scoped non-PadSetting targets
        /// (<c>"MotionGyro"</c>, <c>"MotionAccel"</c> — bound to a device's
        /// bundled motion source via <c>"Motion Gyro"</c> / <c>"Motion Accel"</c>
        /// source descriptors; the engine reads three axes directly from the
        /// source device's state instead of coercing through the per-source
        /// scalar pipeline).</summary>
        [XmlAttribute] public string Target { get; set; } = "";

        /// <summary>Layer this row belongs to. <c>"Base"</c> (default) is
        /// always live. Non-Base values (e.g. <c>"Shift"</c>) only fire when
        /// their layer is active. Forward-compatible: schema accepts
        /// <c>"Shift1"</c> / <c>"Shift2"</c> / etc. so adding more layers
        /// later is a UI change rather than a schema migration.</summary>
        [XmlAttribute] public string LayerMask { get; set; } = "Base";

        /// <summary>How the per-source values combine into the row's
        /// final output. Empty string = the per-target-type default
        /// (Max-abs for axes, OR for buttons). Other named modes:
        /// <c>"MaxAbs"</c>, <c>"Sum"</c>, <c>"Average"</c> for axes;
        /// <c>"OR"</c>, <c>"AND"</c>, <c>"XOR"</c> for buttons. The
        /// special value <c>"Custom"</c> evaluates
        /// <see cref="CombineExpression"/>.</summary>
        [XmlAttribute] public string CombineMode { get; set; } = "";

        /// <summary>Custom combine expression. Only meaningful when
        /// <see cref="CombineMode"/> == <c>"Custom"</c>. Variables
        /// <c>a..z</c> bind to the first 26 sources in row order;
        /// <c>s[i]</c> indexes the source list. Sandboxed: no I/O, no
        /// state, no user-defined functions. Compiled to AST at config
        /// load and cached on the row at runtime.</summary>
        [XmlAttribute] public string CombineExpression { get; set; } = "";

        /// <summary>Keeps the first two Custom arguments separate when their
        /// device ids and inversion would otherwise encode a bipolar pair.
        /// Ignored by other combine modes.</summary>
        [XmlAttribute] public bool SuppressBipolarPair { get; set; }

        /// <summary>
        /// Shift-layer "do not inherit" flag. When true on a non-Base row,
        /// the row suppresses Base fallthrough for this target on this
        /// layer even when the row has zero sources (an explicit "this
        /// target is OFF on this layer"). False (default) preserves the
        /// engine's overlay-with-fallthrough behavior — if the layer has
        /// no row for the target, the Base row fires.
        /// Engine: <c>ApplyMappingSetToGamepad</c> consults this when
        /// building the active-layer shift-covered targets set.
        /// </summary>
        [XmlAttribute] public bool NoInherit { get; set; } = false;

        /// <summary>Stick-trim combine (#155): deflection below this
        /// percentage of the trim axis's range is ignored, so steering
        /// wobble on the same stick never nudges the held level. Only
        /// meaningful when <see cref="CombineMode"/> == <c>"StickTrim"</c>.</summary>
        [XmlAttribute] public int TrimDeadzone { get; set; } = 25;

        /// <summary>Stick-trim combine (#155): full-deflection adjustment
        /// speed, in percent of the trigger range per second. 100 sweeps
        /// the whole range in one second.</summary>
        [XmlAttribute] public int TrimRate { get; set; } = 100;

        /// <summary>Stick-trim combine (#155): when true (default),
        /// releasing the gate resets the stored level to 100%, so the
        /// next press starts full. When false, the level persists across
        /// releases until trimmed again.</summary>
        [XmlAttribute] public bool TrimResetOnRelease { get; set; } = true;

        /// <summary>Motion Pitch, Yaw and Roll rows (#475): how deflection
        /// becomes motion. Empty (the default) is Speed, a turn at a speed
        /// that stops where it is when released. <c>"Angle"</c> leans the
        /// controller to an angle and levels it on release, pitch and roll
        /// only: yaw has no Angle response and reads as Speed. A string so
        /// the vocabulary can grow append-only.</summary>
        [XmlAttribute] public string MotionResponse { get; set; } = "";

        /// <summary>Speed response: degrees per second at full deflection.</summary>
        [XmlAttribute] public int MotionSpeed { get; set; } = DefaultMotionSpeed;

        /// <summary>Speed response: degrees per second just past the
        /// deadzone, where the turn starts.</summary>
        [XmlAttribute] public int MotionMinSpeed { get; set; } = 0;

        /// <summary>Angle response: degrees of lean at full deflection.</summary>
        [XmlAttribute] public int MotionAngle { get; set; } = DefaultMotionAngle;

        /// <summary>Percent of full deflection read as rest.</summary>
        [XmlAttribute] public int MotionDeadzone { get; set; } = DefaultMotionDeadzone;

        /// <summary>The Angle response's stored value.</summary>
        public const string MotionResponseAngle = "Angle";

        /// <summary>eden's analog motion binding turns one revolution per
        /// second at full deflection (input_poller.cpp, rev/s units).</summary>
        public const int DefaultMotionSpeed = 360;

        /// <summary>Dolphin's Tilt angle default (Tilt.cpp), its value for
        /// the Wii Remote.</summary>
        public const int DefaultMotionAngle = 85;

        /// <summary>eden's analog motion binding and cemu-no-gyro both
        /// read 0.2 of the stick as rest.</summary>
        public const int DefaultMotionDeadzone = 20;

        /// <summary>The Speed range's top, the model's rate cap.</summary>
        public const int MaxMotionSpeed = 1600;

        /// <summary>The Angle range's top.</summary>
        public const int MaxMotionAngle = 90;

        /// <summary>The Deadzone range's top, percent.</summary>
        public const int MaxMotionDeadzone = 90;

        /// <summary>Sources combined to produce this row's output.</summary>
        [XmlElement("Source")]
        public List<MappingSource> Sources { get; set; } = new();

        /// <summary>Copies this row's settings to <paramref name="target"/>:
        /// everything but the target, the layer, the sources and the
        /// bipolar-pair flag, which each copy lane sets for itself. Every lane
        /// that copies a row to a row goes through here (the profile clone,
        /// Copy, Paste, Copy From, a device's copy and paste, the layer
        /// copies), so a setting added later reaches all of them.</summary>
        public void CopySettingsTo(MappingRow target)
        {
            target.CombineMode = CombineMode ?? "";
            target.CombineExpression = CombineExpression ?? "";
            target.NoInherit = NoInherit;
            target.TrimDeadzone = TrimDeadzone;
            target.TrimRate = TrimRate;
            target.TrimResetOnRelease = TrimResetOnRelease;
            target.MotionResponse = MotionResponse ?? "";
            target.MotionSpeed = MotionSpeed;
            target.MotionMinSpeed = MotionMinSpeed;
            target.MotionAngle = MotionAngle;
            target.MotionDeadzone = MotionDeadzone;
        }
    }
}
