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

        /// <summary>
        /// True when the device exposes per-trigger ("impulse") rumble motors.
        /// Xbox One / Elite / Series controllers report true; Xbox 360,
        /// DualSense, DS4, generic non-Xbox HID gamepads report false.
        /// Driven by <c>SDL_PROP_JOYSTICK_CAP_TRIGGER_RUMBLE_BOOLEAN</c>.
        /// </summary>
        bool HasRumbleTriggers { get; }

        bool HasHaptic { get; }
        bool HasGyro { get; }
        bool HasAccel { get; }
        bool HasTouchpad { get; }
        /// <summary>Number of distinct touchpad surfaces the device exposes
        /// (Steam Controller 2026 / Steam Deck = 2; DualSense / DS4 = 1).
        /// Default mirrors <see cref="HasTouchpad"/> (0 or 1); the SDL wrapper
        /// overrides with the real per-device count so multi-pad devices keep
        /// both surfaces mappable even while offline.</summary>
        int NumTouchpads => HasTouchpad ? 1 : 0;
        /// <summary>Per-touchpad finger (simultaneous-contact) count, as SDL
        /// enumerates it via SDL_GetNumGamepadTouchpadFingers. Index aligns with
        /// the touchpad index. Steam Controller 2026 pads report 1; DualSense 2.
        /// Default is empty; the SDL wrapper overrides with real per-pad counts
        /// so the mapping picker only offers fingers the device actually has.</summary>
        int[] TouchpadFingerCounts => System.Array.Empty<int>();
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
