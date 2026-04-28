using System;
using System.Xml.Serialization;

namespace PadForge.Engine
{
    /// <summary>
    /// Top-level category for a virtual controller. The actual device identity
    /// (Xbox 360 Wired, DualSense, Logitech G920, etc.) is selected within each
    /// category via a per-slot preset config or, for Extended, a custom HID
    /// descriptor. Numeric values are preserved from v2 (Xbox360→Microsoft,
    /// DualShock4→PlayStation, VJoy→Extended, kept for v2→v3 migration) so
    /// existing settings files load.
    /// </summary>
    public enum VirtualControllerType
    {
        /// <summary>Xbox family — Xbox 360, Xbox One, Xbox Series, Elite, Adaptive.
        /// In-code identifier kept as <c>Microsoft</c> for v2 PadForge.xml back-compat.</summary>
        Microsoft = 0,
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
        KeyboardMouse = 4
    }

    /// <summary>
    /// The five user-facing VC type groups in fixed visual order.
    /// Each group is independent: operations on one MUST NOT affect any
    /// other. The group order matches the sidebar / dashboard rendering
    /// order and is not user-reorderable.
    /// </summary>
    public static class VirtualControllerGroups
    {
        public static readonly VirtualControllerType[] InOrder = new[]
        {
            VirtualControllerType.Microsoft,
            VirtualControllerType.PlayStation,
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
