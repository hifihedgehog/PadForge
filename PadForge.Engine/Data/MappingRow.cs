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
        /// <c>"LeftThumbAxisX"</c>, <c>"LeftTrigger"</c>, <c>"DPadUp"</c>).
        /// </summary>
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

        /// <summary>Sources combined to produce this row's output.</summary>
        [XmlElement("Source")]
        public List<MappingSource> Sources { get; set; } = new();
    }
}
