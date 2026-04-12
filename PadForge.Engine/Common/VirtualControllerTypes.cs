using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Top-level category for a virtual controller. The actual device identity
    /// (Xbox 360 Wired, DualSense, Logitech G920, etc.) is selected within each
    /// category via a per-slot preset config or, for Extended, a custom HID
    /// descriptor. Numeric values are preserved from v2 (Xbox360→Microsoft,
    /// DualShock4→Sony, VJoy→Extended) so existing settings files load.
    /// </summary>
    public enum VirtualControllerType
    {
        /// <summary>Microsoft category — Xbox 360, Xbox One, Xbox Series, Elite, Adaptive.</summary>
        Microsoft = 0,
        /// <summary>Sony category — DualShock 3/4, DualSense, DualSense Edge, PS Move.</summary>
        Sony = 1,
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
