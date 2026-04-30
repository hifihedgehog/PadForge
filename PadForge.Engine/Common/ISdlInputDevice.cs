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

        /// <summary>
        /// Sparse list of button positions this device actually exposes.
        /// Implementations that don't gate buttons can return a dense
        /// 0..NumButtons-1 array. Used by the Devices preview to skip
        /// positions the device doesn't physically have (e.g. paddles on
        /// a controller that doesn't have any).
        /// </summary>
        int[] SupportedButtonIndices { get; }

        /// <summary>
        /// Native SDL_Gamepad pointer for this device, or
        /// <see cref="IntPtr.Zero"/> if the device wasn't opened as a
        /// Gamepad (raw joystick, keyboard, mouse, web controller, etc).
        /// Used by the DualSense passthrough dispatcher to call
        /// <c>SDL_SendGamepadEffect</c> on the assigned physical
        /// DualSense / DualSense Edge.
        /// </summary>
        IntPtr GamepadHandle { get; }
        bool HasRumble { get; }
        bool HasHaptic { get; }
        bool HasGyro { get; }
        bool HasAccel { get; }
        bool HasTouchpad { get; }
        HapticEffectStrategy HapticStrategy { get; }
        IntPtr HapticHandle { get; }
        uint HapticFeatures { get; }
        int NumHapticAxes { get; }
        bool IsAttached { get; }
        ushort VendorId { get; }
        ushort ProductId { get; }
        Guid InstanceGuid { get; }
        Guid ProductGuid { get; }
        string DevicePath { get; }
        string SerialNumber { get; }
        string SdlGuid { get; }

        CustomInputState GetCurrentState(bool forceRaw = false);
        DeviceObjectItem[] GetDeviceObjects();
        int GetInputDeviceType();

        bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue);
        bool StopRumble();
    }
}
