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
    }
}
