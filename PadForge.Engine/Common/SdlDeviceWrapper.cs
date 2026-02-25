using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SDL3;
using static SDL3.SDL;

namespace PadForge.Engine
{
    /// <summary>
    /// Wraps an SDL joystick (and optionally its Gamepad overlay) to provide
    /// unified device access: open/close, state polling, rumble, GUID construction,
    /// and device object enumeration.
    ///
    /// Each physical device is represented by one <see cref="SdlDeviceWrapper"/> instance
    /// that is opened by <see cref="Open(uint)"/> and released by <see cref="Dispose"/>.
    /// </summary>
    public class SdlDeviceWrapper : ISdlInputDevice
    {
        // ─────────────────────────────────────────────
        //  Properties
        // ─────────────────────────────────────────────

        /// <summary>Raw SDL joystick handle. Always valid when the device is open.</summary>
        public IntPtr Joystick { get; private set; } = IntPtr.Zero;

        /// <summary>SDL Gamepad handle. May be IntPtr.Zero if the device is not recognized as a gamepad.</summary>
        public IntPtr GameController { get; private set; } = IntPtr.Zero;

        /// <summary>SDL instance ID (unique per device connection session). 0 = invalid.</summary>
        public uint SdlInstanceId { get; private set; }

        /// <summary>Number of axes reported by SDL.</summary>
        public int NumAxes { get; private set; }

        /// <summary>Number of buttons reported by SDL.</summary>
        public int NumButtons { get; private set; }

        /// <summary>Number of hat switches reported by SDL.</summary>
        public int NumHats { get; private set; }

        /// <summary>Whether the device supports rumble vibration.</summary>
        public bool HasRumble { get; private set; }

        /// <summary>SDL haptic device handle. Non-zero when haptic FFB is available (and rumble is not).</summary>
        public IntPtr Haptic { get; private set; } = IntPtr.Zero;

        /// <summary>Haptic handle exposed via ISdlInputDevice interface.</summary>
        public IntPtr HapticHandle => Haptic;

        /// <summary>Bitmask of supported haptic features (SDL_HAPTIC_* flags).</summary>
        public uint HapticFeatures { get; private set; }

        /// <summary>True if the device has a haptic FFB handle open.</summary>
        public bool HasHaptic => Haptic != IntPtr.Zero;

        /// <summary>Best haptic strategy for this device (chosen at open time).</summary>
        public HapticEffectStrategy HapticStrategy { get; private set; } = HapticEffectStrategy.None;

        /// <summary>
        /// Total number of raw joystick buttons as reported by SDL (before gamepad remapping).
        /// For gamepad devices this may be higher than <see cref="NumButtons"/> (11), exposing
        /// extra native buttons like DualSense touchpad click or mic button.
        /// </summary>
        public int RawButtonCount { get; private set; }

        /// <summary>Whether the device has a gyroscope sensor.</summary>
        public bool HasGyro { get; private set; }

        /// <summary>Whether the device has an accelerometer sensor.</summary>
        public bool HasAccel { get; private set; }

        /// <summary>Human-readable device name.</summary>
        public string Name { get; private set; } = string.Empty;

        /// <summary>USB Vendor ID.</summary>
        public ushort VendorId { get; private set; }

        /// <summary>USB Product ID.</summary>
        public ushort ProductId { get; private set; }

        /// <summary>USB Product Version.</summary>
        public ushort ProductVersion { get; private set; }

        /// <summary>Device file system path (may be empty on some platforms).</summary>
        public string DevicePath { get; private set; } = string.Empty;

        /// <summary>SDL joystick type classification.</summary>
        public SDL_JoystickType JoystickType { get; private set; } = SDL_JoystickType.SDL_JOYSTICK_TYPE_UNKNOWN;

        /// <summary>
        /// Deterministic instance GUID for this device, derived from its device path
        /// (or a fallback identifier). Used to match saved settings to physical devices.
        /// </summary>
        public Guid InstanceGuid { get; private set; } = Guid.Empty;

        /// <summary>
        /// Product GUID derived from VID/PID for device identification
        /// and settings matching.
        /// </summary>
        public Guid ProductGuid { get; private set; } = Guid.Empty;

        /// <summary>True if the device was recognized and opened as an SDL Gamepad.</summary>
        public bool IsGameController => GameController != IntPtr.Zero;

        /// <summary>True if the device handle is still valid and attached.</summary>
        public bool IsAttached
        {
            get
            {
                if (Joystick == IntPtr.Zero)
                    return false;
                return SDL_JoystickConnected(Joystick);
            }
        }

        private bool _disposed;

        // ─────────────────────────────────────────────
        //  Open / Close
        // ─────────────────────────────────────────────

        /// <summary>
        /// Opens the SDL device with the given instance ID.
        /// Attempts to open as a Gamepad first (if SDL recognizes it);
        /// falls back to raw Joystick mode. Populates all public properties.
        /// </summary>
        /// <param name="instanceId">SDL instance ID from SDL_GetJoysticks().</param>
        /// <returns>True if the device was opened successfully.</returns>
        public bool Open(uint instanceId)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SdlDeviceWrapper));

            // Close any previously opened device on this wrapper.
            CloseInternal();

            // Try Gamepad first for better mapping support.
            if (SDL_IsGamepad(instanceId))
            {
                GameController = SDL_OpenGamepad(instanceId);
                if (GameController != IntPtr.Zero)
                {
                    Joystick = SDL_GetGamepadJoystick(GameController);
                }
            }

            // Fall back to raw joystick if Gamepad failed or wasn't recognized.
            if (Joystick == IntPtr.Zero)
            {
                GameController = IntPtr.Zero;
                Joystick = SDL_OpenJoystick(instanceId);
            }

            if (Joystick == IntPtr.Zero)
                return false;

            // Populate properties from the opened joystick handle.
            SdlInstanceId = SDL_GetJoystickID(Joystick);
            Name = SDL_GetJoystickName(Joystick);
            VendorId = SDL_GetJoystickVendor(Joystick);
            ProductId = SDL_GetJoystickProduct(Joystick);
            ProductVersion = SDL_GetJoystickProductVersion(Joystick);
            JoystickType = SDL_GetJoystickType(Joystick);
            DevicePath = SDL_GetJoystickPath(Joystick);

            // Always capture the raw joystick button count before any gamepad override.
            RawButtonCount = SDL_GetNumJoystickButtons(Joystick);

            // When opened as a Gamepad, report the standardized layout counts
            // so that GetDeviceObjects() and the UI reflect the remapped layout
            // instead of the raw HID descriptor. This matches GetGamepadState().
            if (GameController != IntPtr.Zero)
            {
                NumAxes = 6;     // LX, LY, LT, RX, RY, RT
                NumButtons = 11; // A, B, X, Y, LB, RB, Back, Start, LS, RS, Guide
                NumHats = 1;     // D-pad synthesized from gamepad buttons
            }
            else
            {
                NumAxes = SDL_GetNumJoystickAxes(Joystick);
                NumButtons = RawButtonCount;
                NumHats = SDL_GetNumJoystickHats(Joystick);
            }

            // SDL3 may return a raw VID/PID string (e.g., "0x16c0/0x05e1") for devices
            // not in its internal database. Fall back to the Windows HID product string.
            if (IsRawVidPidName(Name))
            {
                string hidName = TryGetHidProductString(DevicePath);
                if (hidName != null)
                    Name = hidName;
            }

            // Check rumble support via properties system (replaces SDL_JoystickHasRumble).
            uint props = SDL_GetJoystickProperties(Joystick);
            HasRumble = props != 0 && SDL_GetBooleanProperty(props, SDL_PROP_JOYSTICK_CAP_RUMBLE_BOOLEAN, false);

            // Detect and enable motion sensors (gyro / accelerometer).
            if (GameController != IntPtr.Zero)
            {
                HasGyro = SDL_GamepadHasSensor(GameController, SDL_SENSOR_GYRO);
                HasAccel = SDL_GamepadHasSensor(GameController, SDL_SENSOR_ACCEL);
                if (HasGyro) SDL_SetGamepadSensorEnabled(GameController, SDL_SENSOR_GYRO, true);
                if (HasAccel) SDL_SetGamepadSensorEnabled(GameController, SDL_SENSOR_ACCEL, true);
            }

            // If the device doesn't support simple rumble, try the haptic API for
            // full force feedback (DirectInput FFB wheels, joysticks, etc.).
            if (!HasRumble)
                OpenHaptic();

            // Build stable GUIDs for settings matching.
            ProductGuid = BuildProductGuid(VendorId, ProductId);
            InstanceGuid = BuildInstanceGuid(DevicePath, VendorId, ProductId, instanceId);

            return true;
        }

        /// <summary>
        /// Internal close that releases SDL handles without setting _disposed.
        /// Haptic must be closed before the joystick it was opened from.
        /// </summary>
        private void CloseInternal()
        {
            // Close haptic first — it depends on the joystick handle.
            if (Haptic != IntPtr.Zero)
            {
                SDL_CloseHaptic(Haptic);
                Haptic = IntPtr.Zero;
                HapticFeatures = 0;
                HapticStrategy = HapticEffectStrategy.None;
            }

            if (GameController != IntPtr.Zero)
            {
                SDL_CloseGamepad(GameController);
                GameController = IntPtr.Zero;
                // CloseGamepad also closes the underlying joystick.
                Joystick = IntPtr.Zero;
            }
            else if (Joystick != IntPtr.Zero)
            {
                SDL_CloseJoystick(Joystick);
                Joystick = IntPtr.Zero;
            }

            SdlInstanceId = 0;
        }

        /// <summary>
        /// Attempts to open the SDL haptic subsystem from the current joystick handle.
        /// Queries supported features and picks the best effect strategy:
        /// LeftRight > Sine > Constant.
        /// </summary>
        private void OpenHaptic()
        {
            if (Joystick == IntPtr.Zero)
                return;

            IntPtr h = SDL_OpenHapticFromJoystick(Joystick);
            if (h == IntPtr.Zero)
                return;

            uint features = SDL_GetHapticFeatures(h);
            if (features == 0)
            {
                SDL_CloseHaptic(h);
                return;
            }

            Haptic = h;
            HapticFeatures = features;

            // Pick the best strategy for translating dual-motor rumble into haptic effects.
            if ((features & SDL_HAPTIC_LEFTRIGHT) != 0)
                HapticStrategy = HapticEffectStrategy.LeftRight;
            else if ((features & SDL_HAPTIC_SINE) != 0)
                HapticStrategy = HapticEffectStrategy.Sine;
            else if ((features & SDL_HAPTIC_CONSTANT) != 0)
                HapticStrategy = HapticEffectStrategy.Constant;
            else
            {
                // Device has haptic support but no usable effect types.
                SDL_CloseHaptic(h);
                Haptic = IntPtr.Zero;
                HapticFeatures = 0;
                return;
            }

            // Set gain to maximum if the device supports it.
            if ((features & SDL_HAPTIC_GAIN) != 0)
                SDL_SetHapticGain(h, 100);
        }

        // ─────────────────────────────────────────────
        //  State reading
        // ─────────────────────────────────────────────

        /// <summary>
        /// Reads the current input state of the device and returns it as a
        /// <see cref="CustomInputState"/>. Call <see cref="SDL_UpdateJoysticks"/>
        /// before calling this method (typically once per frame for all devices).
        ///
        /// SDL axes are signed (-32768 to 32767). This method converts them to
        /// unsigned (0 to 65535) by subtracting <see cref="short.MinValue"/>,
        /// matching the convention used by the mapping pipeline.
        ///
        /// SDL hats are bitmasks. This method converts them to centidegrees
        /// (-1 for centered), matching the DirectInput POV convention.
        /// </summary>
        /// <returns>A new <see cref="CustomInputState"/> snapshot, or null if the device is not attached.</returns>
        public CustomInputState GetCurrentState()
        {
            if (Joystick == IntPtr.Zero)
                return null;

            // When the device is opened as a Gamepad, use the gamepad API to read
            // through SDL's built-in mapping layer (gamecontrollerdb). This remaps
            // DualSense, DualShock, Switch Pro, etc. to the standardized Xbox layout
            // so the same auto-mapping works for all recognized controllers.
            if (GameController != IntPtr.Zero)
                return GetGamepadState();

            return GetJoystickState();
        }

        /// <summary>
        /// Reads input through SDL's gamepad mapping layer. Produces a standardized
        /// CustomInputState layout that matches CreateDefaultPadSetting:
        ///   Axes: [0]=LX, [1]=LY, [2]=LT, [3]=RX, [4]=RY, [5]=RT
        ///   Buttons: [0]=A, [1]=B, [2]=X, [3]=Y, [4]=LB, [5]=RB,
        ///            [6]=Back, [7]=Start, [8]=LS, [9]=RS, [10]=Guide
        ///   POV[0]: D-pad synthesized from gamepad D-pad buttons.
        /// </summary>
        private CustomInputState GetGamepadState()
        {
            var state = new CustomInputState();

            // --- Axes ---
            // Read standardized gamepad axes and reorder to match the auto-mapping layout:
            //   CustomInputState Axis[0..5] = LX, LY, LT, RX, RY, RT
            //   SDL gamepad axis enum       = LX(0), LY(1), RX(2), RY(3), LT(4), RT(5)

            // Stick axes: signed -32768..32767 → unsigned 0..65535
            short lx = SDL_GetGamepadAxis(GameController, SDL_GAMEPAD_AXIS_LEFTX);
            short ly = SDL_GetGamepadAxis(GameController, SDL_GAMEPAD_AXIS_LEFTY);
            short rx = SDL_GetGamepadAxis(GameController, SDL_GAMEPAD_AXIS_RIGHTX);
            short ry = SDL_GetGamepadAxis(GameController, SDL_GAMEPAD_AXIS_RIGHTY);

            state.Axis[0] = (ushort)(lx - short.MinValue);  // LX
            state.Axis[1] = (ushort)(ly - short.MinValue);  // LY
            state.Axis[3] = (ushort)(rx - short.MinValue);  // RX
            state.Axis[4] = (ushort)(ry - short.MinValue);  // RY

            // Trigger axes: gamepad API returns 0..32767 (0=released, 32767=full).
            // Scale to 0..65535 unsigned to match the convention used by the mapping pipeline.
            short lt = SDL_GetGamepadAxis(GameController, SDL_GAMEPAD_AXIS_LEFT_TRIGGER);
            short rt = SDL_GetGamepadAxis(GameController, SDL_GAMEPAD_AXIS_RIGHT_TRIGGER);
            state.Axis[2] = (int)(lt * 65535L / 32767);     // LT
            state.Axis[5] = (int)(rt * 65535L / 32767);     // RT

            // --- Buttons ---
            // Reorder from SDL gamepad button enum to the auto-mapping layout:
            //   [0]=A(South), [1]=B(East), [2]=X(West), [3]=Y(North),
            //   [4]=LB, [5]=RB, [6]=Back, [7]=Start, [8]=LS, [9]=RS, [10]=Guide
            state.Buttons[0] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_SOUTH);
            state.Buttons[1] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_EAST);
            state.Buttons[2] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_WEST);
            state.Buttons[3] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_NORTH);
            state.Buttons[4] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_LEFT_SHOULDER);
            state.Buttons[5] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER);
            state.Buttons[6] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_BACK);
            state.Buttons[7] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_START);
            state.Buttons[8] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_LEFT_STICK);
            state.Buttons[9] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_RIGHT_STICK);
            state.Buttons[10] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_GUIDE);

            // --- Extra raw buttons ---
            // Append raw joystick buttons beyond the 11 standard gamepad buttons.
            // This exposes native device buttons (e.g. DualSense touchpad, mic) that
            // aren't part of the Xbox gamepad mapping, for use as macro triggers.
            int rawCount = RawButtonCount;
            for (int i = 11; i < rawCount && i < CustomInputState.MaxButtons; i++)
                state.Buttons[i] = SDL_GetJoystickButton(Joystick, i);

            // --- D-pad → POV[0] ---
            // Synthesize a POV hat from the four D-pad buttons.
            bool up = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_DPAD_UP);
            bool down = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_DPAD_DOWN);
            bool left = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_DPAD_LEFT);
            bool right = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_DPAD_RIGHT);
            state.Povs[0] = DpadToCentidegrees(up, down, left, right);

            // --- Sensors (gyro / accelerometer) ---
            if (HasGyro)
                SDL_GetGamepadSensorData(GameController, SDL_SENSOR_GYRO, state.Gyro, 3);
            if (HasAccel)
                SDL_GetGamepadSensorData(GameController, SDL_SENSOR_ACCEL, state.Accel, 3);

            return state;
        }

        /// <summary>
        /// Reads raw joystick input (no gamepad remapping). Used for non-gamepad devices
        /// and for devices not recognized in SDL's gamecontrollerdb.
        /// </summary>
        private CustomInputState GetJoystickState()
        {
            var state = new CustomInputState();

            // --- Axes ---
            // First MaxAxis axes go into Axis[], overflow goes into Sliders[].
            int axisCount = Math.Min(NumAxes, CustomInputState.MaxAxis + CustomInputState.MaxSliders);
            for (int i = 0; i < axisCount; i++)
            {
                short raw = SDL_GetJoystickAxis(Joystick, i);
                // Convert signed SDL range to unsigned: -32768→0, 0→32768, 32767→65535
                int unsigned = (ushort)(raw - short.MinValue);

                if (i < CustomInputState.MaxAxis)
                {
                    state.Axis[i] = unsigned;
                }
                else
                {
                    int sliderIndex = i - CustomInputState.MaxAxis;
                    if (sliderIndex < CustomInputState.MaxSliders)
                        state.Sliders[sliderIndex] = unsigned;
                }
            }

            // --- Hats (POV) ---
            int hatCount = Math.Min(NumHats, state.Povs.Length);
            for (int i = 0; i < hatCount; i++)
            {
                byte hat = SDL_GetJoystickHat(Joystick, i);
                state.Povs[i] = HatToCentidegrees(hat);
            }

            // --- Buttons ---
            int btnCount = Math.Min(NumButtons, state.Buttons.Length);
            for (int i = 0; i < btnCount; i++)
            {
                state.Buttons[i] = SDL_GetJoystickButton(Joystick, i);
            }

            return state;
        }

        // ─────────────────────────────────────────────
        //  Rumble (SDL only)
        //
        //  Uses SDL_RumbleJoystick with a very long duration so the
        //  caller controls when rumble stops. Change-detection in
        //  ForceFeedbackState ensures we only call when values differ,
        //  avoiding the hardware restart gaps that occur with redundant calls.
        // ─────────────────────────────────────────────

        /// <summary>
        /// Sends rumble to the device via SDL_RumbleJoystick.
        /// </summary>
        /// <param name="lowFreq">Low-frequency (heavy) motor intensity (0–65535).</param>
        /// <param name="highFreq">High-frequency (light) motor intensity (0–65535).</param>
        /// <param name="durationMs">Rumble duration in milliseconds.</param>
        /// <returns>True if rumble was applied successfully.</returns>
        public bool SetRumble(ushort lowFreq, ushort highFreq, uint durationMs = uint.MaxValue)
        {
            if (!HasRumble || Joystick == IntPtr.Zero)
                return false;

            return SDL_RumbleJoystick(Joystick, lowFreq, highFreq, durationMs);
        }

        /// <summary>
        /// Stops all rumble on the device.
        /// </summary>
        public bool StopRumble()
        {
            return SetRumble(0, 0, 0);
        }

        // ─────────────────────────────────────────────
        //  GUID construction
        // ─────────────────────────────────────────────

        /// <summary>
        /// Builds a synthetic product GUID from VID and PID.
        /// Used for device identification and settings matching.
        ///
        /// Layout (16 bytes):
        ///   bytes[0..1] = VID (little-endian)
        ///   bytes[2..3] = PID (little-endian)
        ///   bytes[4..15] = 0x00
        ///
        /// NOTE: This does NOT include the "PIDVID" signature at bytes 10-15.
        /// The PIDVID signature is only present in real DirectInput product GUIDs
        /// for XInput-over-DirectInput wrapper devices. Since we use SDL (not raw
        /// DirectInput), we detect XInput devices via SDL hints and VID/PID checks.
        /// </summary>
        public static Guid BuildProductGuid(ushort vid, ushort pid)
        {
            byte[] bytes = new byte[16];

            // VID in little-endian at bytes 0-1.
            bytes[0] = (byte)(vid & 0xFF);
            bytes[1] = (byte)((vid >> 8) & 0xFF);

            // PID in little-endian at bytes 2-3.
            bytes[2] = (byte)(pid & 0xFF);
            bytes[3] = (byte)((pid >> 8) & 0xFF);

            // Remaining bytes are zero — no PIDVID signature.

            return new Guid(bytes);
        }

        /// <summary>
        /// Builds a product GUID in the classic PIDVID format that DirectInput uses
        /// for XInput-over-DirectInput wrapper devices. Only used when creating
        /// UserDevice records for native XInput controllers (slots 0–3).
        /// </summary>
        public static Guid BuildXInputProductGuid(ushort vid, ushort pid)
        {
            byte[] bytes = new byte[16];

            bytes[0] = (byte)(vid & 0xFF);
            bytes[1] = (byte)((vid >> 8) & 0xFF);
            bytes[2] = (byte)(pid & 0xFF);
            bytes[3] = (byte)((pid >> 8) & 0xFF);

            // ASCII "PIDVID" at bytes 10-15.
            bytes[10] = 0x50; // P
            bytes[11] = 0x49; // I
            bytes[12] = 0x44; // D
            bytes[13] = 0x56; // V
            bytes[14] = 0x49; // I
            bytes[15] = 0x44; // D

            return new Guid(bytes);
        }

        /// <summary>
        /// Builds a deterministic instance GUID from the device path (preferred)
        /// or a fallback identifier string. Uses MD5 to produce a stable 16-byte hash
        /// so the same physical device (same USB port path) always gets the same GUID,
        /// enabling settings persistence across sessions.
        /// </summary>
        /// <param name="devicePath">The file system device path (may be empty).</param>
        /// <param name="vid">USB Vendor ID.</param>
        /// <param name="pid">USB Product ID.</param>
        /// <param name="instanceId">SDL instance ID (used in fallback only).</param>
        /// <returns>A deterministic GUID for the device instance.</returns>
        public static Guid BuildInstanceGuid(string devicePath, ushort vid, ushort pid, uint instanceId)
        {
            string identifier;

            if (!string.IsNullOrEmpty(devicePath))
            {
                // Use the device path for a stable identifier.
                identifier = devicePath;
            }
            else
            {
                // Fallback: synthetic identifier from VID, PID, and instance ID.
                identifier = $"sdl:{vid:X4}:{pid:X4}:{instanceId}";
            }

            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(identifier));
                return new Guid(hash);
            }
        }

        // ─────────────────────────────────────────────
        //  Device objects enumeration
        // ─────────────────────────────────────────────

        /// <summary>
        /// Builds an array of <see cref="DeviceObjectItem"/> describing each axis,
        /// hat, and button on the device. This is the SDL equivalent of
        /// DirectInput's GetObjects() call.
        ///
        /// Axes 0–5 are assigned the standard type GUIDs (XAxis, YAxis, ZAxis,
        /// RxAxis, RyAxis, RzAxis). Remaining axes get Slider GUIDs.
        /// Hats get PovController GUIDs. Buttons get Button GUIDs.
        /// </summary>
        public DeviceObjectItem[] GetDeviceObjects()
        {
            int totalObjects = NumAxes + NumHats + NumButtons;
            var items = new DeviceObjectItem[totalObjects];
            int index = 0;

            // Well-known axis GUIDs for the first 6 axes (matching DirectInput convention).
            Guid[] standardAxisGuids = new Guid[]
            {
                ObjectGuid.XAxis,
                ObjectGuid.YAxis,
                ObjectGuid.ZAxis,
                ObjectGuid.RxAxis,
                ObjectGuid.RyAxis,
                ObjectGuid.RzAxis
            };

            // --- Axes ---
            for (int i = 0; i < NumAxes; i++)
            {
                var item = new DeviceObjectItem();
                item.InputIndex = i;

                if (i < standardAxisGuids.Length)
                {
                    item.ObjectTypeGuid = standardAxisGuids[i];
                    item.Name = GetStandardAxisName(i);
                }
                else
                {
                    item.ObjectTypeGuid = ObjectGuid.Slider;
                    item.Name = $"Slider {i - standardAxisGuids.Length}";
                }

                item.ObjectType = DeviceObjectTypeFlags.AbsoluteAxis;
                item.Offset = i * 4; // Simulated offset for identification.
                item.Aspect = ObjectAspect.Position;

                items[index++] = item;
            }

            // --- Hats ---
            for (int i = 0; i < NumHats; i++)
            {
                var item = new DeviceObjectItem();
                item.InputIndex = i;
                item.ObjectTypeGuid = ObjectGuid.PovController;
                item.Name = NumHats == 1 ? "Hat Switch" : $"Hat Switch {i}";
                item.ObjectType = DeviceObjectTypeFlags.PointOfViewController;
                item.Offset = (NumAxes + i) * 4;
                item.Aspect = ObjectAspect.Position;

                items[index++] = item;
            }

            // --- Buttons ---
            for (int i = 0; i < NumButtons; i++)
            {
                var item = new DeviceObjectItem();
                item.InputIndex = i;
                item.ObjectTypeGuid = ObjectGuid.Button;
                item.Name = $"Button {i}";
                item.ObjectType = DeviceObjectTypeFlags.PushButton;
                item.Offset = (NumAxes + NumHats + i) * 4;
                item.Aspect = ObjectAspect.Position;

                items[index++] = item;
            }

            return items;
        }

        /// <summary>
        /// Maps the SDL joystick type to an <see cref="InputDeviceType"/> constant
        /// for device classification in the settings and UI.
        /// </summary>
        public int GetInputDeviceType()
        {
            return JoystickType switch
            {
                SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD => InputDeviceType.Gamepad,
                SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL => InputDeviceType.Driving,
                SDL_JoystickType.SDL_JOYSTICK_TYPE_FLIGHT_STICK => InputDeviceType.Flight,
                SDL_JoystickType.SDL_JOYSTICK_TYPE_ARCADE_STICK => InputDeviceType.Joystick,
                SDL_JoystickType.SDL_JOYSTICK_TYPE_ARCADE_PAD => InputDeviceType.Gamepad,
                SDL_JoystickType.SDL_JOYSTICK_TYPE_DANCE_PAD => InputDeviceType.Supplemental,
                SDL_JoystickType.SDL_JOYSTICK_TYPE_GUITAR => InputDeviceType.Supplemental,
                SDL_JoystickType.SDL_JOYSTICK_TYPE_DRUM_KIT => InputDeviceType.Supplemental,
                SDL_JoystickType.SDL_JOYSTICK_TYPE_THROTTLE => InputDeviceType.Flight,
                _ => InputDeviceType.Joystick
            };
        }

        // ─────────────────────────────────────────────
        //  Hat conversion
        // ─────────────────────────────────────────────

        /// <summary>
        /// Converts an SDL hat bitmask to DirectInput-style centidegrees.
        /// -1 = centered (no direction pressed).
        /// 0 = up (north), 9000 = right (east), 18000 = down (south), 27000 = left (west).
        /// Diagonal directions are at 4500, 13500, 22500, 31500.
        /// </summary>
        /// <param name="hat">SDL hat bitmask value.</param>
        /// <returns>Angle in centidegrees (0–35900) or -1 for centered.</returns>
        public static int HatToCentidegrees(byte hat)
        {
            // Strip any extraneous bits.
            hat &= 0x0F;

            return hat switch
            {
                SDL_HAT_UP => 0,
                SDL_HAT_RIGHTUP => 4500,
                SDL_HAT_RIGHT => 9000,
                SDL_HAT_RIGHTDOWN => 13500,
                SDL_HAT_DOWN => 18000,
                SDL_HAT_LEFTDOWN => 22500,
                SDL_HAT_LEFT => 27000,
                SDL_HAT_LEFTUP => 31500,
                _ => -1  // SDL_HAT_CENTERED or any other value
            };
        }

        /// <summary>
        /// Converts four D-pad booleans to DirectInput-style centidegrees.
        /// Used by <see cref="GetGamepadState"/> to synthesize a POV hat
        /// from SDL gamepad D-pad buttons.
        /// </summary>
        public static int DpadToCentidegrees(bool up, bool down, bool left, bool right)
        {
            if (up && right) return 4500;
            if (right && down) return 13500;
            if (down && left) return 22500;
            if (left && up) return 31500;
            if (up) return 0;
            if (right) return 9000;
            if (down) return 18000;
            if (left) return 27000;
            return -1; // Centered
        }

        // ─────────────────────────────────────────────
        //  HID product string fallback
        //  SDL3 doesn't always return friendly device names — some devices
        //  get a raw "0xVVVV/0xPPPP" string. We query the Windows HID
        //  product string to recover the friendly name.
        // ─────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetProductString(
            IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        /// <summary>
        /// Checks if a device name looks like a raw VID/PID string (e.g., "0x16c0/0x05e1")
        /// that SDL3 returns for devices not in its internal database.
        /// </summary>
        private static bool IsRawVidPidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 11)
                return false;

            return name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && name.Contains('/');
        }

        /// <summary>
        /// Attempts to read the HID product string from a device path.
        /// Returns null if the path is invalid or the query fails.
        /// </summary>
        private static string TryGetHidProductString(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath))
                return null;

            try
            {
                IntPtr handle = CreateFile(
                    devicePath,
                    0,  // No access rights needed for HidD_GetProductString
                    3,  // FILE_SHARE_READ | FILE_SHARE_WRITE
                    IntPtr.Zero,
                    3,  // OPEN_EXISTING
                    0,
                    IntPtr.Zero);

                if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE)
                    return null;

                try
                {
                    byte[] buffer = new byte[512];
                    if (HidD_GetProductString(handle, buffer, (uint)buffer.Length))
                    {
                        string name = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
                        if (!string.IsNullOrWhiteSpace(name))
                            return name.Trim();
                    }
                    return null;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            catch
            {
                return null;
            }
        }

        // ─────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns a human-readable name for standard axis indices 0–5.
        /// </summary>
        private static string GetStandardAxisName(int axisIndex)
        {
            return axisIndex switch
            {
                0 => "X Axis",
                1 => "Y Axis",
                2 => "Z Axis",
                3 => "X Rotation",
                4 => "Y Rotation",
                5 => "Z Rotation",
                _ => $"Axis {axisIndex}"
            };
        }

        // ─────────────────────────────────────────────
        //  IDisposable
        // ─────────────────────────────────────────────

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            CloseInternal();
            _disposed = true;
        }

        ~SdlDeviceWrapper()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /// Strategy for translating ViGEm dual-motor rumble values into SDL haptic effects.
    /// Chosen at device open time based on the device's supported feature flags.
    /// </summary>
    public enum HapticEffectStrategy
    {
        None,
        LeftRight,
        Sine,
        Constant
    }
}
