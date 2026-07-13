using System.Collections.Generic;
using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Per-virtual-controller mapping table. Replaces the per-(VC, Device)
    /// mapping fields on <see cref="PadSetting"/>. Cross-device sources
    /// live naturally inside a single <see cref="MappingRow"/>.
    ///
    /// <para>
    /// Shift-layer authoring (Issue #61 Phase 6) lives entirely inside
    /// this object: <see cref="ShiftActivators"/> declares the activator
    /// configuration for every layer on this slot, and <see cref="Rows"/>
    /// carries every layer's rows tagged by <see cref="MappingRow.LayerMask"/>.
    /// This guarantees shift state is per-profile by construction — the
    /// whole MappingSet is what <see cref="ProfileData.SlotMappingSets"/>
    /// stores per slot.
    /// </para>
    /// </summary>
    public class MappingSet
    {
        /// <summary>Mapping rows. Each row has a <see cref="MappingRow.Target"/>
        /// and <see cref="MappingRow.LayerMask"/>; a single Target can have
        /// multiple rows when more than one layer is configured.</summary>
        [XmlElement("Row")]
        public List<MappingRow> Rows { get; set; } = new();

        /// <summary>
        /// Shift activators authored for this slot. Each activator names
        /// the layer it engages via <see cref="ShiftActivator.LayerMask"/>.
        /// Empty list = no shift layers; only Base rows fire. Multi-activator
        /// resolution uses last-engaged-wins (the most recently engaged
        /// activator's layer is active).
        /// </summary>
        [XmlElement("ShiftActivator")]
        public List<ShiftActivator> ShiftActivators { get; set; } = new();

        // ── Base layer flyout appearance (#119) ──
        // Base has no activator, so its display name / dot color / glyph for
        // the engaged-layer flyout and the Base tab live here. Empty falls
        // back to the localized "Base" label, a gray dot, and the Shift glyph.

        /// <summary>Display name shown on the Base tab and the Base flyout.
        /// Empty falls back to the localized "Base" label.</summary>
        [XmlAttribute] public string BaseLayerName { get; set; } = "";

        /// <summary>Base flyout / tab dot color, <c>"#RRGGBB"</c>. Empty = gray.</summary>
        [XmlAttribute] public string BaseColor { get; set; } = "";

        /// <summary>Base flyout glyph (emoji or single grapheme). Empty falls
        /// back to the universal Shift glyph.</summary>
        [XmlAttribute] public string BaseIcon { get; set; } = "";

        /// <summary>
        /// An authoritative set owns its slot's mappings completely: the
        /// legacy-automap merge must not add rows or inject sources into it.
        /// Stamped true on Steam Workshop imports, whose rows spell out every
        /// binding (including automap-identical ones) explicitly, so a
        /// device's auto-mapped legacy descriptors would double every input.
        /// Old XML has no attribute and deserializes false, keeping every
        /// hand-authored set on the normal merge path.
        /// </summary>
        [XmlAttribute] public bool Authoritative { get; set; } = false;
    }
}
