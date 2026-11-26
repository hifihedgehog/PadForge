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

        /// <summary>Total raw joystick axis count before the gamepad layout caps
        /// <see cref="NumAxes"/> to 6. Defaults to <see cref="NumAxes"/> for
        /// devices (keyboard, mouse, MIDI, remote peer, …) that have no separate
        /// raw-axis notion; only SdlDeviceWrapper overrides it (issue #193).</summary>
        int RawAxisCount => NumAxes;

        /// <summary>True when the device carries raw joystick axes beyond the
        /// standard six that should surface as generic "Axis N" mapping sources
        /// (issue #193). Only SdlDeviceWrapper sets it; everything else has
        /// none.</summary>
        bool HasExtraGenericAxes => false;

        /// <summary>True when the device's reader writes the sideways wheel
        /// (CustomInputState.MouseRawWheelH) on a mouse row, so Wheel Travel
        /// Sideways can move (#600). A reader that hands one wheel only, a
        /// touchpad's mouse or a TouchSense mouse, says false.</summary>
        bool ReadsSidewaysWheel => true;

        int NumHats { get; }

        /// <summary>
        /// Sparse list of button positions this device actually exposes.
        /// Used by the Devices preview to skip positions the device doesn't
        /// physically have (e.g. paddles on a controller that doesn't have
        /// any).
        ///
        /// <para>Three states, and they are not interchangeable. NULL means
        /// the implementation does not gate buttons, so every index below the
        /// count is real. A NON-EMPTY array means exactly those positions. An
        /// EMPTY array means the device has NO buttons, which is a different
        /// claim from making no claim. Implementations that do not gate must
        /// return null or a dense 0..NumButtons-1 array, never an empty one.
        /// </para>
        /// </summary>
        int[] SupportedButtonIndices { get; }

        /// <summary>The axis positions this device actually has, sparse. Same
        /// three states as <see cref="SupportedButtonIndices"/>: null means
        /// dense, a non-empty array means exactly those, and an empty array
        /// means the device has no axes at all. Exists because NumAxes is the
        /// standardized 6-slot gamepad space rather than a count of the pad's
        /// physical axes.</summary>
        int[] SupportedAxisIndices => null;

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
        /// <summary>Whether the device exposes an auxiliary (left-side)
        /// accelerometer (issue #199): the Wii Nunchuk's own sensor on a
        /// Nunchuk-attached remote, or the left half of a combined Joy-Con
        /// pair. SDL_SENSOR_ACCEL_L. Default false; only the SDL wrapper
        /// (live query) and the Remote Link peer mirror override.</summary>
        bool HasAccelAux => false;
        /// <summary>Whether the device exposes an auxiliary (left-side)
        /// gyroscope (issue #252): the left half of a combined Joy-Con pair,
        /// gen 1 or gen 2. SDL_SENSOR_GYRO_L. Unlike
        /// <see cref="HasAccelAux"/> this is never a Nunchuk, which has no
        /// gyro. Default false; only the SDL wrapper (live query) and the
        /// Remote Link peer mirror override.</summary>
        bool HasGyroAux => false;
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
        /// <summary>Capacitive sensor channels as a bitmask over the
        /// SDL_GAMEPAD_CAPSENSE_* indices. 0 by default: only the SDL wrapper
        /// reads them.</summary>
        int CapSenseChannelMask => 0;
        /// <summary>Whether the pad carries voice phrases, as another PC that
        /// owns it answers (Remote Link). Null by default: the device's own
        /// record decides (UserDevice.HasVoicePhrases).</summary>
        bool? OwnerHasVoicePhrases => null;
        bool? TouchpadPressureSupported => null;
        bool? TouchpadClickSupported => null;
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

        /// <summary>Sets the impulse-trigger motors through the gamepad
        /// handle. Only the SDL wrapper has a gamepad handle, so every other
        /// device refuses, as the direct SDL call this replaced did.</summary>
        bool SetTriggerRumble(ushort left, ushort right, uint durationMs = uint.MaxValue)
            => GamepadHandle != IntPtr.Zero
               && SDL3.SDL.SDL_RumbleGamepadTriggers(GamepadHandle, left, right, durationMs);

        /// <summary>Keeps a rumble level the device holds running past a cap
        /// in the layer below it. Called on every poll. Only the SDL wrapper's
        /// rumble has such a cap, so by default there is nothing to do.</summary>
        void RenewHeldRumble() { }
    }
}
