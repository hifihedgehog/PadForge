using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Configures one shift-layer activator on a <see cref="MappingSet"/>.
    /// A MappingSet can carry many activators (one per layer); each names
    /// the layer it engages via <see cref="LayerMask"/>.
    ///
    /// <para>
    /// Shift activator runtime state (<see cref="Mode"/> == <c>"Toggle"</c>'s
    /// engaged flag, the previous-frame button-down latch, the engaged-
    /// activator stack for last-engaged-wins resolution) lives on the per-VC
    /// <c>InputManager</c>, not on this DTO. Resets on app launch, on
    /// profile switch, and on slot-index compaction.
    /// </para>
    ///
    /// <para>
    /// v2/v3 extensions (<see cref="Kind"/>, <see cref="ChordSecondDescriptor"/>,
    /// <see cref="AxisThreshold"/>, <see cref="JumpToLayer"/>, <see cref="DelayMs"/>,
    /// <see cref="PostponeMapping"/>, <see cref="Color"/>) are serialized as
    /// optional attributes; v1 configs round-trip cleanly through them.
    /// </para>
    /// </summary>
    public class ShiftActivator
    {
        /// <summary>Device that owns the activator input. Cross-device
        /// activation is allowed (the activator can live on a different
        /// physical device than the sources it gates).</summary>
        [XmlAttribute] public string DeviceGuid { get; set; } = "";

        /// <summary>Input descriptor of the activator. Usually a button-class
        /// descriptor (<c>"Button N"</c>); axis-as-activator (Kind=Axis) reads
        /// from this descriptor with <see cref="AxisThreshold"/> applied.</summary>
        [XmlAttribute] public string Descriptor { get; set; } = "";

        /// <summary>Activation mode. <c>"Hold"</c> (default): layer is active
        /// while the input is engaged. <c>"Toggle"</c>: each press flips
        /// engagement. <c>"Custom"</c> (v2): press triggers a transition to
        /// <see cref="JumpToLayer"/>; layer stays until another transition
        /// fires. <c>"Cycle"</c> (v3): press advances through an ordered list
        /// of layers. <c>"Sticky"</c> (v3): one-shot — engages on press,
        /// auto-releases after the next input fires on the engaged layer.
        /// Runtime state does not persist across app restart.</summary>
        [XmlAttribute] public string Mode { get; set; } = "Hold";

        /// <summary>
        /// Layer that this activator engages, e.g. <c>"PitStop"</c>,
        /// <c>"Shift1"</c>. <see cref="MappingRow.LayerMask"/> values are
        /// matched against this. Defaults to <c>"Shift"</c> for back-compat
        /// with the singular-activator schema.
        /// </summary>
        [XmlAttribute] public string LayerMask { get; set; } = "Shift";

        /// <summary>
        /// Human-friendly display name shown on the layer tab and in dialogs.
        /// Defaults to the same value as <see cref="LayerMask"/> on creation
        /// but can be edited independently (e.g. LayerMask="Shift1",
        /// LayerName="Pit Stop").
        /// </summary>
        [XmlAttribute] public string LayerName { get; set; } = "";

        /// <summary>
        /// When <c>true</c>, targets the layer doesn't explicitly map fall
        /// through to the Base row (overlay-with-fallthrough). When
        /// <c>false</c> (default), the layer REPLACES Base entirely — every
        /// target without a row on this layer outputs zero/false. The
        /// per-row <see cref="MappingRow.NoInherit"/> flag selectively
        /// blocks individual fallthrough targets while this is true.
        /// </summary>
        [XmlAttribute] public bool InheritUnmapped { get; set; } = false;

        // ── v2 fields (declared in v1 schema for forward-compat) ──

        /// <summary>v2 Custom mode: layer to jump to on press. Empty
        /// disables. Used only when <see cref="Mode"/> == <c>"Custom"</c>.</summary>
        [XmlAttribute] public string JumpToLayer { get; set; } = "";

        /// <summary>v2 debounce: milliseconds the activator must remain
        /// engaged before the layer change fires. <c>0</c> = instant.</summary>
        [XmlAttribute] public int DelayMs { get; set; } = 0;

        /// <summary>v7 double-press gate (translator v25, Steam's
        /// Double_Press activator hosting layer verbs / preset jumps /
        /// mode shifts). When &gt; 0, the activator's input only counts as
        /// engaged during the SECOND press of a press-release-press pair
        /// whose presses land within this many milliseconds (Steam's
        /// double_tap_time; Valve's shipped default 442). Every mode sees
        /// the gated read: Hold holds from the second press until its
        /// release, Toggle / Custom / Cycle fire on the second press's
        /// rising edge, and single presses engage nothing. <c>0</c>
        /// (default) = plain read, no gate.</summary>
        [XmlAttribute] public int DoublePressMs { get; set; } = 0;

        /// <summary><para>Fire on Release: the edge modes (Toggle / Custom /
        /// Cycle / Sticky) fire when the input is let GO instead of when it
        /// goes down. Matches Steam's release-hosted activators, where any
        /// verb can wait for the lift.</para>
        /// <para><see cref="DelayMs"/> gates the press that ARMS the release,
        /// so a delay-gated activator reads "long-press, then let go".
        /// <see cref="DoublePressMs"/> composes upstream of the edge read, so
        /// the fire lands on the second press's release. Ignored by Hold
        /// (level-driven, already defined by both edges) and Passive (no
        /// button).</para></summary>
        [XmlAttribute] public bool FireOnRelease { get; set; }

        /// <summary>v6 release linger (translator v22, Steam's activator
        /// <c>delay_end</c> on a layer switch: "wait for this period of
        /// time after the button has been released before deactivating").
        /// Hold mode keeps the layer engaged this many milliseconds past
        /// the input's release; a re-press inside the window continues the
        /// engagement seamlessly, so the pending disengage is cancelled by
        /// the press (the audit-#2 M6 cancel-on-re-press shape, applied to
        /// the layer machinery instead of a macro pair). Other modes
        /// ignore it: their disengage is press-driven (Toggle / Cycle /
        /// Custom), never release-driven, so a release delay has no edge
        /// to move. <c>0</c> = instant disengage.</summary>
        [XmlAttribute] public int ReleaseDelayMs { get; set; } = 0;

        /// <summary>v2 postpone-the-mapping: when true, the activator's
        /// own row (if any) still fires alongside the layer change. When
        /// false (default), the activator input is consumed by the layer
        /// transition only.</summary>
        [XmlAttribute] public bool PostponeMapping { get; set; } = false;

        /// <summary>v2 per-layer color shown in tabs and the v3 overlay
        /// window. <c>"#AARRGGBB"</c> hex string; empty = no color.</summary>
        [XmlAttribute] public string Color { get; set; } = "";

        /// <summary>v2 activator kind. <c>"Button"</c> (default) reads the
        /// <see cref="Descriptor"/> as a button-class input. <c>"Chord"</c>:
        /// engages when both <see cref="Descriptor"/> AND
        /// <see cref="ChordSecondDescriptor"/> are held (cross-device chords
        /// supported via <see cref="ChordSecondDeviceGuid"/>). <c>"Axis"</c>:
        /// engages when the axis at <see cref="Descriptor"/> crosses
        /// <see cref="AxisThreshold"/>.</summary>
        [XmlAttribute] public string Kind { get; set; } = "Button";

        /// <summary>v2 chord: second device of a cross-device chord. Same
        /// shape as <see cref="DeviceGuid"/>.</summary>
        [XmlAttribute] public string ChordSecondDeviceGuid { get; set; } = "";

        /// <summary>v2 chord: second input descriptor of the chord.</summary>
        [XmlAttribute] public string ChordSecondDescriptor { get; set; } = "";

        /// <summary>v2 axis activator: threshold past which the axis engages
        /// the layer. Normalized [-1, 1] absolute value; default 0.5 means
        /// |axis| &gt;= 0.5 engages.</summary>
        [XmlAttribute] public double AxisThreshold { get; set; } = 0.5;

        /// <summary>v5 axis activator half selector (translator v15 swipe
        /// flicks). When <c>false</c> (default) the Axis kind keeps its
        /// legacy direction-blind |axis| &gt;= threshold test. When
        /// <c>true</c> only ONE signed direction engages, with
        /// <see cref="AxisInvert"/> picking which, so a stick wedge or a
        /// signed gyro rate can drive a layer without its opposite
        /// direction also firing.</summary>
        [XmlAttribute] public bool AxisHalf { get; set; } = false;

        /// <summary>v5 half selector for <see cref="AxisHalf"/>: false =
        /// the positive direction (axis &gt;= threshold), true = the
        /// negative direction (axis &lt;= -threshold). Same
        /// half-selection contract as <c>MappingSource.HalfAxis</c> +
        /// <c>Invert</c>.</summary>
        [XmlAttribute] public bool AxisInvert { get; set; } = false;

        // ── v3 fields ──

        /// <summary>v3 Cycle mode: pipe-separated ordered list of layer
        /// masks to cycle through (e.g. <c>"Shift1|Shift2|Shift3"</c>).
        /// The Next button (<see cref="Descriptor"/>) steps the cursor forward,
        /// the Previous button (<see cref="CyclePrevDescriptor"/>) backward.
        /// Stepping past an end follows <see cref="CycleWrap"/>; whether Base is
        /// a stop follows <see cref="CycleIncludeBase"/>. Used only when
        /// <see cref="Mode"/> == <c>"Cycle"</c>.</summary>
        [XmlAttribute] public string CycleLayers { get; set; } = "";

        /// <summary>v3 Cycle Previous button (#119). One Cycle activator holds
        /// the whole queue plus both buttons: <see cref="Descriptor"/> /
        /// <see cref="DeviceGuid"/> is the Next button, and these two are the
        /// Previous button. Cross-device is allowed (read via the prev device's
        /// state, like a chord's second half). Empty = no Previous button.</summary>
        [XmlAttribute] public string CyclePrevDeviceGuid { get; set; } = "";

        /// <summary>v3 Cycle Previous button input descriptor (#119). See
        /// <see cref="CyclePrevDeviceGuid"/>.</summary>
        [XmlAttribute] public string CyclePrevDescriptor { get; set; } = "";

        /// <summary>v3 Cycle wrap (#119). When <c>true</c> (default), stepping
        /// past the last position returns to the first (and past the first to the
        /// last). When <c>false</c>, the cursor clamps at the ends.</summary>
        [XmlAttribute] public bool CycleWrap { get; set; } = true;

        /// <summary>v3 Cycle include-Base (#119). When <c>false</c> (default), the
        /// cursor walks the queued layers only: Base is the resting state before
        /// the first press, but never a stop in the rotation (a weapon cursor stays
        /// on a weapon). When <c>true</c>, the unshifted Base is also a stop in the
        /// ring, so cycling can land back on it. A separate activator can always
        /// return to Base regardless of this flag.</summary>
        [XmlAttribute] public bool CycleIncludeBase { get; set; } = false;

        /// <summary>v3 personalization: emoji or single-grapheme string
        /// shown on the engaged-layer flyout. Empty falls back to the
        /// universal Shift glyph <c>⇧</c>.</summary>
        [XmlAttribute] public string Icon { get; set; } = "";

        // ── v8 fields (translator v26) ──

        /// <summary>Gate companion for the Axis kind: a button-class
        /// descriptor that must ALSO be held for the axis wedge to engage.
        /// This is the activator-lane twin of the row side's
        /// <c>MappingSource.GateDescriptor</c> (v18): a trackpad D-pad
        /// wedge is an axis half PLUS its contact / windowed-click gate,
        /// and without the gate leg a layer verb hosted on such a wedge
        /// had no faithful activator shape. Read against the activator's
        /// own device. Empty (default) = no gate. Ignored by the Button /
        /// Chord kinds (Button reads one bool; Chord already has its
        /// second leg).</summary>
        [XmlAttribute] public string GateDescriptor { get; set; } = "";

        // ── v4 fields (#206) ──

        /// <summary>v4 auto-cancel (#206): Toggle mode only. While the
        /// layer is engaged, if none of the layer's own mapped inputs
        /// (sources on rows whose LayerMask is this layer, Base
        /// fallthrough excluded) is active for this many milliseconds,
        /// the toggle disengages by itself. <c>0</c> (default) = never.
        /// The timer starts at engage and re-arms on every layer-input
        /// activity. Deliberately not offered for Latch: auto-unlatching
        /// was flagged as surprising by the requester.</summary>
        [XmlAttribute] public int AutoCancelMs { get; set; } = 0;

        /// <summary>Full-field copy. Every field is a string / value type
        /// (strings are immutable), so a memberwise clone is a complete deep
        /// copy. Copy sites must use this instead of hand-listing fields:
        /// the app's CopyShiftActivators hand-list dropped
        /// <see cref="AxisHalf"/> / <see cref="AxisInvert"/> (the v15 swipe
        /// direction stamps), making imported flick activators
        /// direction-blind after a profile switch or Copy From Slot.</summary>
        public ShiftActivator Clone() => (ShiftActivator)MemberwiseClone();
    }
}
