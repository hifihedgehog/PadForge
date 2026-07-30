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

        /// <summary>Which mouse buttons arm the recognizer while held, as a
        /// bitmask over the raw button indices: bit 0 Left, bit 1 Middle,
        /// bit 2 Right, bit 3 X1, bit 4 X2 (SdlMouseWrapper order), plus
        /// bit 5 = the Custom activation (discussion #216), armed by the
        /// recorded cross-device input below instead of a mouse button.
        /// One, some, or all buttons can be gesture buttons. Each selected
        /// button runs its own independent session: it accumulates
        /// displacement while that button is held, classifies at that
        /// button's release, and carries its own cooldown; simultaneous
        /// sessions coexist. Defaults to X1 only: gestures want an
        /// otherwise-unmapped side button, since v1 does not suppress the
        /// button's own click while gesturing.</summary>
        [XmlAttribute] public int GestureButtons { get; set; } = 1 << 3;

        /// <summary>Cross-device descriptor that arms the Custom gesture
        /// session (discussion #216) while held: a keyboard key, a gamepad
        /// button, or an axis past the button threshold (a wheel pedal).
        /// Recorded through the same freeform recorder the Aim Engage
        /// button uses. Empty = the Custom session never arms, even with
        /// its bit selected; the empty-descriptor pass-through convention
        /// of the engage family does NOT apply here (an unconfigured
        /// gesture button must stay inert, not always-armed).</summary>
        [XmlAttribute] public string CustomEngageButton { get; set; } = "";

        /// <summary>Device GUID owning <see cref="CustomEngageButton"/>,
        /// same cross-device pair shape as
        /// <c>PadSetting.GyroAimEngageDeviceGuid</c>.</summary>
        [XmlAttribute] public string CustomEngageDeviceGuid { get; set; } = "";

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
            GestureButtons = GestureButtons,
            CustomEngageButton = CustomEngageButton,
            CustomEngageDeviceGuid = CustomEngageDeviceGuid,
            FlickThresholdCounts = FlickThresholdCounts,
            CooldownMs = CooldownMs,
        };

        public static MouseGestureSettings Default() => new MouseGestureSettings();
    }
}
