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
        /// <c>"Incremental"</c>, <c>"InvertOnHold"</c>. Forward-compatible:
        /// unknown values treated as Direct.</summary>
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
    }
}
