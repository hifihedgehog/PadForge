using System.Xml.Serialization;

namespace PadForge.Engine.Mouse
{
    /// <summary>
    /// Per-(slot, device) mouse-gesture settings (issue #200). Hold the
    /// chosen gesture button on a mouse, flick, and the recognizer classifies
    /// net displacement at release into one of five one-shot gestures
    /// (Left / Right / Up / Down, or Click when below the flick threshold).
    /// Twin of <see cref="Touchpad.TouchpadGestureSettings"/> in shape:
    /// every field is an XmlAttribute with its default in the initializer,
    /// feature toggles default OFF (opt-in), Clone() is member-by-member,
    /// and Default() is a fresh instance.
    /// </summary>
    public sealed class MouseGestureSettings
    {
        /// <summary>Master enable. Off by default like every gesture category;
        /// the picker entries and macro triggers stay visible but inert.</summary>
        [XmlAttribute] public bool Enabled { get; set; }

        /// <summary>Which mouse button arms the recognizer while held.
        /// Raw button index into the mouse state: 0 Left, 1 Middle, 2 Right,
        /// 3 X1, 4 X2 (SdlMouseWrapper order). Defaults to X1: gestures want
        /// an otherwise-unmapped side button, since v1 does not suppress the
        /// button's own click while gesturing.</summary>
        [XmlAttribute] public int GestureButton { get; set; } = 3;

        /// <summary>Net displacement (raw mouse counts, dominant axis) the
        /// flick must reach by release to classify as a direction. Below it,
        /// the release fires Click. 150 counts is a deliberate small wrist
        /// flick at typical sensor resolution.</summary>
        [XmlAttribute] public int FlickThresholdCounts { get; set; } = 150;

        /// <summary>How long a fired gesture stays asserted in the fired set
        /// so slower consumers (30 Hz recorder, UI) catch the pulse. Same
        /// default as the touchpad lane.</summary>
        [XmlAttribute] public int CooldownMs { get; set; } = 100;

        public MouseGestureSettings Clone() => new MouseGestureSettings
        {
            Enabled = Enabled,
            GestureButton = GestureButton,
            FlickThresholdCounts = FlickThresholdCounts,
            CooldownMs = CooldownMs,
        };

        public static MouseGestureSettings Default() => new MouseGestureSettings();
    }
}
