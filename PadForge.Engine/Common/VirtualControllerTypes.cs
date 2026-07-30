using System;
using System.Xml.Serialization;

namespace PadForge.Engine
{
    /// <summary>
    /// Top-level category for a virtual controller. The actual device identity
    /// (Xbox 360 Wired, DualSense, Logitech G920, etc.) is selected within each
    /// category via a per-slot preset config or, for Extended, a custom HID
    /// descriptor. Numeric values and on-disk names are preserved from v2
    /// (Xbox360→Microsoft→Xbox, DualShock4→Sony→PlayStation, VJoy→Extended)
    /// via XmlEnum so existing settings files load.
    /// </summary>
    public enum VirtualControllerType
    {
        /// <summary>Xbox family — Xbox 360, Xbox One, Xbox Series, Elite, Adaptive.</summary>
        // XmlEnum preserves the on-disk name "Microsoft" so v2/early-v3
        // PadForge.xml files deserialize correctly. The in-code identifier
        // is Xbox to match the Xbox/PlayStation/Extended family naming
        // shown in the UI.
        [XmlEnum("Microsoft")]
        Xbox = 0,
        /// <summary>PlayStation category — DualShock 3/4, DualSense, DualSense Edge, PS Move.</summary>
        // XmlEnum preserves the on-disk name "Sony" so v2/early-v3 PadForge.xml
        // files deserialize correctly. The in-code identifier is PlayStation
        // to match the Xbox/PlayStation/Extended family naming shown in the UI.
        [XmlEnum("Sony")]
        PlayStation = 1,
        /// <summary>Extended category — any of the 220+ remaining HIDMaestro profiles
        /// (Logitech, Thrustmaster, Fanatec, Hori, 8BitDo, etc.) plus user-defined
        /// custom HID descriptors.</summary>
        Extended = 2,
        /// <summary>MIDI controller (Windows MIDI Services).</summary>
        Midi = 3,
        /// <summary>Keyboard + Mouse output (built-in, no driver).</summary>
        KeyboardMouse = 4,
        /// <summary>Nintendo category. Switch Pro Controller for now.
        /// Console-family face like Xbox / PlayStation (own bucket, icon,
        /// fixed catalog profile) riding the Extended raw-HID data path
        /// (profile-driven layout, raw button indices, Nintendo lettering).
        /// No Customize surface: the slot always deploys the catalog
        /// profile as-is. Appended after KeyboardMouse; numeric values are
        /// persisted, never reorder.</summary>
        Nintendo = 5
    }

    /// <summary>
    /// The six user-facing VC type groups in fixed visual order.
    /// Each group is independent: operations on one MUST NOT affect any
    /// other. The group order matches the sidebar / dashboard rendering
    /// order and is not user-reorderable.
    /// </summary>
    public static class VirtualControllerGroups
    {
        public static readonly VirtualControllerType[] InOrder = new[]
        {
            VirtualControllerType.Xbox,
            VirtualControllerType.PlayStation,
            VirtualControllerType.Nintendo,
            VirtualControllerType.Extended,
            VirtualControllerType.KeyboardMouse,
            VirtualControllerType.Midi,
        };
    }

    /// <summary>
    /// Abstraction over a virtual controller. The single concrete
    /// implementation in v3 is HMaestroVirtualController, plus
    /// MidiVirtualController and KeyboardMouseVirtualController for the
    /// non-HID output types.
    /// </summary>
    public interface IVirtualController : IDisposable
    {
        VirtualControllerType Type { get; }
        bool IsConnected { get; }

        /// <summary>
        /// The pad slot index this VC currently occupies. Updated by SwapSlotData
        /// so feedback callbacks write to the correct VibrationStates element
        /// after a slot reorder.
        /// </summary>
        int FeedbackPadIndex { get; set; }

        void Connect();
        void Disconnect();
        void SubmitGamepadState(Gamepad gp);
        void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates);
    }
}
