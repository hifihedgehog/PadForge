using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Type of virtual controller to create.
    /// </summary>
    public enum VirtualControllerType
    {
        Xbox360 = 0,
        DualShock4 = 1,
        VJoy = 2,
        Midi = 3
    }

    /// <summary>
    /// Abstraction over ViGEm virtual controller operations.
    /// Concrete implementations (Xbox360VirtualController, DS4VirtualController)
    /// live in the App assembly where the ViGEm NuGet reference exists.
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
