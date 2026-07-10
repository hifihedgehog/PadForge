using System.Xml.Serialization;

namespace PadForge.Engine.Mouse
{
    /// <summary>
    /// One (device, settings) pair inside <c>PadSetting.MouseGestureSettings</c>
    /// (issue #200). Twin of <see cref="Touchpad.TouchpadSettingsEntry"/>,
    /// minus the pad index: a mouse has exactly one motion surface.
    /// </summary>
    public sealed class MouseGestureSettingsEntry
    {
        [XmlAttribute] public string DeviceGuid { get; set; } = "";

        public MouseGestureSettings Settings { get; set; } = MouseGestureSettings.Default();
    }
}
