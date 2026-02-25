using System;

namespace PadForge.Engine
{
    /// <summary>
    /// Common interface for all SDL-based input device wrappers (joystick/gamepad,
    /// keyboard, mouse). Allows the input pipeline (Steps 2-5) to read state from
    /// any device type uniformly via <see cref="GetCurrentState"/>.
    /// </summary>
    public interface ISdlInputDevice : IDisposable
    {
        uint SdlInstanceId { get; }
        string Name { get; }
        int NumAxes { get; }
        int NumButtons { get; }
        int RawButtonCount { get; }
        int NumHats { get; }
        bool HasRumble { get; }
        bool HasHaptic { get; }
        bool HasGyro { get; }
        bool HasAccel { get; }
        HapticEffectStrategy HapticStrategy { get; }
        IntPtr HapticHandle { get; }
        uint HapticFeatures { get; }
        bool IsAttached { get; }
        ushort VendorId { get; }
        ushort ProductId { get; }
        Guid InstanceGuid { get; }
        Guid ProductGuid { get; }
        string DevicePath { get; }

        CustomInputState GetCurrentState();
        DeviceObjectItem[] GetDeviceObjects();
        int GetInputDeviceType();

        bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue);
        bool StopRumble();
    }
}
