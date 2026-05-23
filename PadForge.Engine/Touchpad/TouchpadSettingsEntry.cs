using System.Xml.Serialization;

namespace PadForge.Engine.Touchpad
{
    /// <summary>
    /// XML-serializable per-(device, touchpad-index) gesture settings
    /// entry. Lives under a <see cref="PadForge.Engine.Data.PadSetting"/>'s
    /// touchpad-settings collection; the runtime engine reads them via
    /// <see cref="PadForge.Common.Input.InputManager.TouchpadGestureSettingsProvider"/>
    /// keyed by <c>(DeviceGuid, TouchpadIndex)</c>.
    /// </summary>
    public sealed class TouchpadSettingsEntry
    {
        /// <summary>Device this entry's settings apply to (instance
        /// GUID string). Pair with <see cref="TouchpadIndex"/> to
        /// disambiguate multi-pad devices.</summary>
        [XmlAttribute] public string DeviceGuid { get; set; } = "";

        /// <summary>Touchpad index within the device. 0 for single-pad
        /// devices; 0..N-1 for multi-pad devices like the Steam
        /// Controller original (3 pads) or Steam Deck (2 pads).</summary>
        [XmlAttribute] public int TouchpadIndex { get; set; }

        /// <summary>The actual settings bundle. Round-trips its own
        /// XmlAttribute-tagged fields as nested attributes; default
        /// values come from <see cref="TouchpadGestureSettings.Default"/>
        /// when an entry is loaded with missing properties (XML
        /// migrator policy: forward-compatible by default).</summary>
        public TouchpadGestureSettings Settings { get; set; } = TouchpadGestureSettings.Default();
    }
}
