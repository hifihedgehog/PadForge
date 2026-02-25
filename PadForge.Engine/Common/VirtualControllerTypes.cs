namespace PadForge.Engine
{
    /// <summary>
    /// Type of virtual controller to create via ViGEmBus.
    /// </summary>
    public enum VirtualControllerType
    {
        Xbox360 = 0,
        DualShock4 = 1
    }

    /// <summary>
    /// Abstraction over ViGEm virtual controller operations.
    /// Concrete implementations (Xbox360VirtualController, DS4VirtualController)
    /// live in the App assembly where the ViGEm NuGet reference exists.
    /// </summary>
    public interface IVirtualController
    {
        VirtualControllerType Type { get; }
        bool IsConnected { get; }
        void Connect();
        void Disconnect();
        void SubmitGamepadState(Gamepad gp);
        void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates);
    }
}
