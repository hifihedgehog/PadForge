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
    /// Phase 1a (this commit): the type exists and round-trips through XML.
    /// Phase 1b: populated from legacy <see cref="PadSetting"/> on load.
    /// Phase 1c: read by Step 3 in place of <see cref="PadSetting"/>'s
    /// per-field mapping descriptors.
    /// </para>
    /// </summary>
    public class MappingSet
    {
        /// <summary>Mapping rows. Each row has a <see cref="MappingRow.Target"/>
        /// and <see cref="MappingRow.LayerMask"/>; a single Target can have
        /// multiple rows when more than one layer is configured (e.g. Base
        /// plus Shift).</summary>
        [XmlElement("Row")]
        public List<MappingRow> Rows { get; set; } = new();

        /// <summary>Shift activator configuration, if any. <c>null</c> = no
        /// shift layer configured. The Shift-layer recipe lands as a
        /// downstream commit; Phase 1a reserves this slot now so the schema
        /// is forward-compatible.</summary>
        [XmlElement(IsNullable = true)]
        public ShiftActivator ShiftButton { get; set; }
    }
}
