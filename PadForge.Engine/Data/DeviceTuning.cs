using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Per-(VC × Device) tuning data: stick deadzones, sensitivity curves,
    /// trigger ranges, FFB gains, audio rumble settings — everything on
    /// today's <see cref="PadSetting"/> except the per-field mapping
    /// descriptors (which move to <see cref="MappingSet"/>).
    ///
    /// <para>
    /// Phase 1a (this commit): placeholder type with identity fields. The
    /// 80+ tuning fields are mirrored over from <see cref="PadSetting"/> in
    /// Commit 1b's migration pass and consumed by Step 3 / FFB / audio
    /// dispatchers in Commit 1c. Until then, the live values still come
    /// from <see cref="PadSetting"/>.
    /// </para>
    /// </summary>
    public class DeviceTuning
    {
        /// <summary>Device this tuning row applies to. Matches the device's
        /// <c>InstanceGuid</c> in <see cref="UserDevice"/>.</summary>
        [XmlAttribute] public string DeviceGuid { get; set; } = "";
    }
}
