using System.Xml.Serialization;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Configures the physical button that engages a Shift layer on a
    /// <see cref="MappingSet"/>. <c>null</c> on a MappingSet means no
    /// shift layer is configured — Base rows always fire and any non-Base
    /// rows are dormant.
    ///
    /// <para>
    /// Shift activator runtime state (<c>Toggle</c> mode's engaged
    /// flag, the previous-frame button-down latch) lives on the per-VC
    /// <c>InputManager</c>, not on this DTO. Resets on app launch and on
    /// profile switch.
    /// </para>
    /// </summary>
    public class ShiftActivator
    {
        /// <summary>Device that owns the shift button. Cross-device shift
        /// is allowed (the activator can live on a different physical
        /// device than the sources it gates).</summary>
        [XmlAttribute] public string DeviceGuid { get; set; } = "";

        /// <summary>Input descriptor of the shift button. Must be a
        /// button-class descriptor (<c>"Button N"</c>); axis-as-shift is
        /// out of scope in v1.</summary>
        [XmlAttribute] public string Descriptor { get; set; } = "";

        /// <summary>Activation mode. <c>"Hold"</c> (default): shift is
        /// active while the button is held down. <c>"Toggle"</c>: each
        /// press flips engagement. Toggle state does not persist across
        /// app restart.</summary>
        [XmlAttribute] public string Mode { get; set; } = "Hold";
    }
}
