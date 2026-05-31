using System;
using System.Collections.Generic;
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

        /// <summary>Whether the device exposes per-trigger ("impulse") rumble motors
        /// (Xbox One / Elite / Series).</summary>
        public bool HasRumbleTriggers { get; private set; }

        /// <summary>SDL haptic device handle. Non-zero when haptic FFB is available (and rumble is not).</summary>
        public IntPtr Haptic { get; private set; } = IntPtr.Zero;

        /// <summary>Haptic handle exposed via ISdlInputDevice interface.</summary>
        public IntPtr HapticHandle => Haptic;

        /// <summary>Bitmask of supported haptic features (SDL_HAPTIC_* flags).</summary>
        public uint HapticFeatures { get; private set; }

        /// <summary>Number of haptic axes (1 for wheels, 2+ for joysticks).</summary>
        public int NumHapticAxes { get; private set; }

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

        /// <summary>
        /// Raw joystick button indices that are already consumed by the gamepad mapping.
        /// These are excluded from the extra raw button passthrough to avoid double-reporting.
        /// </summary>
        private HashSet<int> _mappedRawButtonIndices;

        /// <summary>
        /// Sparse list of button positions that this device actually exposes.
        /// For SDL3-recognized gamepads: 0-10 always, plus 11-21 only when
        /// <c>SDL_GamepadHasButton</c> reports the corresponding extended
        /// button (Misc1, paddles, Touchpad, Misc2-6) is present, plus any
        /// raw passthrough indices ≥22 that aren't already consumed by the
        /// gamepad mapping.
        /// For raw joystick devices: 0..NumButtons-1.
        /// Computed once at <see cref="Open"/> time and used by the Devices
        /// preview to avoid showing button slots the device doesn't have.
        /// </summary>
        public int[] SupportedButtonIndices { get; private set; } = Array.Empty<int>();

        /// <summary>Native SDL_Gamepad pointer when opened as a Gamepad
        /// (i.e. <see cref="GameController"/> is non-zero).  Returns
        /// IntPtr.Zero for devices opened as raw joysticks only.  Used by
        /// the DualSense passthrough dispatcher.</summary>
        public IntPtr GamepadHandle => GameController;

        /// <summary>Whether the device has a gyroscope sensor.</summary>
        public bool HasGyro { get; private set; }

        /// <summary>Whether the device has an accelerometer sensor.</summary>
        public bool HasAccel { get; private set; }

        /// <summary>Whether the device has a touchpad (DS4/DualSense/Steam Deck).</summary>
        public bool HasTouchpad { get; private set; }

        /// <summary>Number of touchpad surfaces SDL reports for this device
        /// (Steam Controller 2026 / Steam Deck = 2; DualSense / DS4 = 1).
        /// Sourced from the per-pad finger-count scratch sized at open time.
        /// Persisted onto UserDevice so the mapping picker keeps both pads
        /// even when the device is offline.</summary>
        public int NumTouchpads => _padFingerCounts?.Length ?? (HasTouchpad ? 1 : 0);

        /// <summary>Per-pad finger counts from SDL_GetNumGamepadTouchpadFingers,
        /// captured at open time. Persisted onto UserDevice so the picker offers
        /// only the fingers each pad actually supports, even when offline.</summary>
        public int[] TouchpadFingerCounts => _padFingerCounts ?? System.Array.Empty<int>();

        /// <summary>Human-readable device name.</summary>
        public string Name { get; private set; } = string.Empty;

        /// <summary>USB Vendor ID.</summary>
        public ushort VendorId { get; private set; }

        /// <summary>USB Product ID.</summary>
        public ushort ProductId { get; private set; }

        /// <summary>Device file system path (may be empty on some platforms).</summary>
        public string DevicePath { get; private set; } = string.Empty;

        /// <summary>SDL joystick type classification.</summary>
        public SDL_JoystickType JoystickType { get; private set; } = SDL_JoystickType.SDL_JOYSTICK_TYPE_UNKNOWN;

        /// <summary>Device serial number (e.g. Bluetooth MAC address). May be empty.</summary>
        public string SerialNumber { get; private set; } = string.Empty;

        /// <summary>SDL joystick GUID string (32 hex chars) used for gamecontrollerdb matching.</summary>
        public string SdlGuid { get; private set; } = string.Empty;

        /// <summary>
        /// Deterministic instance GUID for this device, derived from VID+PID+Serial
        /// (when serial is available) or device path. Used to match saved settings
        /// to physical devices.
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
            JoystickType = SDL_GetJoystickType(Joystick);
            DevicePath = SDL_GetJoystickPath(Joystick);
            SerialNumber = SDL_GetJoystickSerial(Joystick) ?? string.Empty;
            SdlGuid = GetJoystickGUIDString(Joystick);

            // Always capture the raw joystick button count before any gamepad override.
            RawButtonCount = SDL_GetNumJoystickButtons(Joystick);

            // When opened as a Gamepad, report the standardized layout counts
            // so that GetDeviceObjects() and the UI reflect the remapped layout
            // instead of the raw HID descriptor. This matches GetGamepadState().
            if (GameController != IntPtr.Zero)
            {
                NumAxes = 6;     // LX, LY, LT, RX, RY, RT
                NumButtons = 22; // 22 standardized slots, SDL3 canonical order:
                                 //   0-10  std XInput (A/B/X/Y/LB/RB/Back/Start/LS/RS/Guide)
                                 //   11    Misc1
                                 //   12-15 RPaddle1 / LPaddle1 / RPaddle2 / LPaddle2
                                 //   16    Touchpad click (SDL_GAMEPAD_BUTTON_TOUCHPAD)
                                 //   17-21 Misc2-Misc6
                NumHats = 1;     // D-pad synthesized from gamepad buttons

                // Parse the gamepad mapping to find which raw button indices are
                // already consumed. These will be excluded from the extra raw button
                // passthrough to avoid double-reporting (e.g., DS3 b11→RB, b12→Guide).
                _mappedRawButtonIndices = ParseMappedButtonIndices(GameController);
            }
            else
            {
                NumAxes = SDL_GetNumJoystickAxes(Joystick);
                NumButtons = RawButtonCount;
                NumHats = SDL_GetNumJoystickHats(Joystick);
                _mappedRawButtonIndices = null;
            }

            SupportedButtonIndices = ComputeSupportedButtonIndices();

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
            // Trigger rumble: SDL property OR'd with a hardware fact —
            // every Microsoft Xbox One+ controller has impulse-trigger
            // motors regardless of what SDL's current backend reports.
            // PadForge writes those triggers via XboxImpulseHidWriter
            // (raw HID) so we don't depend on SDL to expose the
            // capability. The UI tab is gated on HasRumbleTriggers,
            // so this also unblocks the tab whenever the controller is
            // connected.
            HasRumbleTriggers =
                (props != 0 && SDL_GetBooleanProperty(props, SDL_PROP_JOYSTICK_CAP_TRIGGER_RUMBLE_BOOLEAN, false))
                || XboxControllerIdentity.IsImpulseTriggerDevice(VendorId, ProductId);

            // Detect and enable motion sensors (gyro / accelerometer).
            if (GameController != IntPtr.Zero)
            {
                HasGyro = SDL_GamepadHasSensor(GameController, SDL_SENSOR_GYRO);
                HasAccel = SDL_GamepadHasSensor(GameController, SDL_SENSOR_ACCEL);
                if (HasGyro) SDL_SetGamepadSensorEnabled(GameController, SDL_SENSOR_GYRO, true);
                if (HasAccel) SDL_SetGamepadSensorEnabled(GameController, SDL_SENSOR_ACCEL, true);

                int numPads = SDL_GetNumGamepadTouchpads(GameController);
                HasTouchpad = numPads > 0;
                if (HasTouchpad)
                {
                    // Per-pad scratch for contact-ID synthesis. SDL3 doesn't
                    // expose HID contact IDs via SDL_GetGamepadTouchpadFinger,
                    // so we synthesize them: a per-pad monotonic counter
                    // increments each time a finger slot transitions from
                    // up to down. The currently-assigned ID for each slot
                    // is held across polling ticks (the CustomInputState is
                    // re-allocated each tick) and written into the snapshot
                    // each frame.
                    _padFingerCounts = new int[numPads];
                    _padContactIdNext = new int[numPads];
                    _padCurrentContactIds = new int[numPads][];
                    for (int p = 0; p < numPads; p++)
                    {
                        int nf = SDL_GetNumGamepadTouchpadFingers(GameController, p);
                        if (nf <= 0) nf = 1;
                        _padFingerCounts[p] = nf;
                        _padCurrentContactIds[p] = new int[nf];
                        for (int f = 0; f < nf; f++) _padCurrentContactIds[p][f] = -1;
                    }
                }
            }

            // Always try the haptic API for force feedback devices (joysticks,
            // wheels, etc.). Some report HasRumble=true via SDL properties but
            // only actually work through the haptic effect system. The routing
            // in ForceFeedbackState prefers HasHaptic when available.
            OpenHaptic();

            // Build stable GUIDs for settings matching.
            ProductGuid = BuildProductGuid(VendorId, ProductId);
            InstanceGuid = BuildInstanceGuid(DevicePath, VendorId, ProductId, instanceId, SerialNumber, SdlGuid);

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
            {
                return;
            }

            uint features = SDL_GetHapticFeatures(h);

            if (features == 0)
            {
                SDL_CloseHaptic(h);
                return;
            }

            // For devices that already have simple rumble (gamepads), skip haptic
            // unless they lack LeftRight support — simple rumble via SDL_RumbleJoystick
            // is more reliable for gamepads.
            if (HasRumble && (features & SDL_HAPTIC_LEFTRIGHT) != 0)
            {
                // Gamepad with LeftRight haptic — simple rumble works fine, skip haptic.
                SDL_CloseHaptic(h);
                return;
            }

            Haptic = h;
            HapticFeatures = features;
            NumHapticAxes = SDL_GetNumHapticAxes(h);

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
        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (Joystick == IntPtr.Zero)
                return null;

            // When the device is opened as a Gamepad, use the gamepad API to read
            // through SDL's built-in mapping layer (gamecontrollerdb). This remaps
            // DualSense, DualShock, Switch Pro, etc. to the standardized Xbox layout
            // so the same auto-mapping works for all recognized controllers.
            // ForceRaw bypasses this for devices whose SDL mapping is wrong
            // (e.g., DsHidMini DS3 in SDF mode).
            if (!forceRaw && GameController != IntPtr.Zero)
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

            // Suppress Guide when Back+Start are both pressed — Windows/XInput
            // synthesizes a Guide button press from this combo when the app has focus.
            if (state.Buttons[6] && state.Buttons[7] && state.Buttons[10])
                state.Buttons[10] = false;

            // --- Extended gamepad buttons (positions 11-21) ---
            // Indices 11-21 follow SDL3's SDL_GamepadButton enum order
            // (skipping the four DPad slots, which PadForge synthesizes
            // into POV[0] above). SDL_GetGamepadButton returns false on
            // devices that lack a given button, so this is harmless on a
            // plain Xbox 360 / DualShock 4.
            state.Buttons[11] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_MISC1);
            state.Buttons[12] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_RIGHT_PADDLE1);
            state.Buttons[13] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_LEFT_PADDLE1);
            state.Buttons[14] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_RIGHT_PADDLE2);
            state.Buttons[15] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_LEFT_PADDLE2);

            // Buttons[16] is reserved for SDL_GAMEPAD_BUTTON_TOUCHPAD; written
            // by the touchpad section below when HasTouchpad is true. Stays
            // false on non-touchpad devices.

            state.Buttons[17] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_MISC2);
            state.Buttons[18] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_MISC3);
            state.Buttons[19] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_MISC4);
            state.Buttons[20] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_MISC5);
            state.Buttons[21] = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_MISC6);

            // --- Extra raw buttons ---
            // Append raw joystick buttons beyond the 22 standardized gamepad
            // positions (0-10 PadForge standard + 11-21 SDL extended). This
            // exposes native device buttons that aren't part of any SDL
            // gamepad button enum for use as macro triggers. Skip indices
            // already consumed by the gamepad mapping to avoid double-
            // reporting.
            int rawCount = RawButtonCount;
            for (int i = 22; i < rawCount && i < CustomInputState.MaxButtons; i++)
            {
                if (_mappedRawButtonIndices != null && _mappedRawButtonIndices.Contains(i))
                    continue;
                state.Buttons[i] = SDL_GetJoystickButton(Joystick, i);
            }

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

            // --- Touchpad (DS4/DualSense/Steam Deck/Steam Controller/Triton) ---
            if (HasTouchpad && _padFingerCounts != null)
            {
                int numPads = _padFingerCounts.Length;
                state.Touchpads = new TouchpadInputState[numPads];
                for (int p = 0; p < numPads; p++)
                {
                    int nf = _padFingerCounts[p];
                    var tp = new TouchpadInputState(nf);
                    var currIds = _padCurrentContactIds[p];
                    for (int f = 0; f < nf; f++)
                    {
                        if (SDL_GetGamepadTouchpadFinger(GameController, p, f,
                                out bool fDown, out float fx, out float fy, out float fp))
                        {
                            tp.FingerX[f] = fx;
                            tp.FingerY[f] = fy;
                            tp.FingerPressure[f] = fp;
                            tp.FingerDown[f] = fDown;

                            // Contact-ID synthesis. SDL3 doesn't surface HID
                            // contact IDs; we infer them from slot up/down
                            // transitions. On a rising edge, allocate a
                            // new ID from the per-pad counter; on a falling
                            // edge, clear to -1. Steady state holds the
                            // previously-assigned ID.
                            if (fDown && currIds[f] < 0)
                                currIds[f] = _padContactIdNext[p]++;
                            else if (!fDown && currIds[f] >= 0)
                                currIds[f] = -1;
                            tp.FingerContactId[f] = currIds[f];
                        }
                    }
                    // Per-pad click. Pad 0 reads SDL_GAMEPAD_BUTTON_TOUCHPAD
                    // (state.Buttons[16] in PadForge's layout). Multi-touchpad
                    // devices like the Triton (Steam Controller 2026) expose
                    // additional pad clicks through MISC2..MISC6 per the
                    // touchpad-click-as-button recipe; map them here too.
                    if (p == 0)
                        tp.Clicked = SDL_GetGamepadButton(GameController,
                            SDL_GAMEPAD_BUTTON_TOUCHPAD);
                    else if (p == 1 && state.Buttons.Length > 17)
                        tp.Clicked = state.Buttons[17]; // MISC2 — second pad click
                    state.Touchpads[p] = tp;
                }

                // Buttons[16] = primary touchpad click — kept current with
                // the gamepad button so existing readers (Touchpad 0 Click
                // descriptor, virtual-DualSense output) continue to work
                // unchanged.
                state.Buttons[16] = SDL_GetGamepadButton(GameController,
                    SDL_GAMEPAD_BUTTON_TOUCHPAD);
            }

            // --- Battery (refresh ~once per 5s; battery doesn't change at poll rate) ---
            long now = System.Environment.TickCount64;
            if (GameController != IntPtr.Zero && now - _lastBatteryReadTick > 5000)
            {
                _lastBatteryReadTick = now;
                int powerState = SDL_GetGamepadPowerInfo(GameController, out int percent);
                _cachedBatteryPercent = percent;
                _cachedBatteryCharging = powerState == SDL_POWERSTATE_CHARGING
                                      || powerState == SDL_POWERSTATE_CHARGED;
            }
            state.BatteryPercent = _cachedBatteryPercent;
            state.BatteryCharging = _cachedBatteryCharging;

            return state;
        }

        private long _lastBatteryReadTick;
        private int _cachedBatteryPercent = -1;
        private bool _cachedBatteryCharging;

        // Per-touchpad scratch — sized once at OpenInternal time when
        // HasTouchpad is true. _padFingerCounts is the per-pad finger
        // slot count from SDL_GetNumGamepadTouchpadFingers; the contact-
        // ID counter and per-slot current ID survive across polling ticks
        // even though the CustomInputState gets re-allocated each tick.
        private int[] _padFingerCounts;
        private int[] _padContactIdNext;
        private int[][] _padCurrentContactIds;

        /// <summary>
        /// Parses the SDL gamepad mapping string to find which raw button indices (bN)
        /// are consumed by the mapping. Returns a set of those indices.
        /// </summary>
        private static HashSet<int> ParseMappedButtonIndices(IntPtr gameController)
        {
            var indices = new HashSet<int>();
            string mapping = GetGamepadMapping(gameController);
            if (mapping == null) return indices;

            // Mapping format: "GUID,name,a:b2,b:b1,...,platform:Windows,"
            // We need to find all "bN" values (button bindings).
            foreach (var segment in mapping.Split(','))
            {
                int colonIdx = segment.IndexOf(':');
                if (colonIdx < 0) continue;
                string value = segment.Substring(colonIdx + 1);
                if (value.Length > 1 && value[0] == 'b' && int.TryParse(value.Substring(1), out int btnIdx))
                    indices.Add(btnIdx);
            }
            return indices;
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
            // Prefer RawButtonCount so devices with more raw HID buttons than
            // the 22 standardized gamepad slots (e.g., flight sticks, fight
            // sticks, force-raw DS3 via DsHidMini) populate every native
            // button. NumButtons (22) is the standardized-range cap and is
            // only the fallback when RawButtonCount is unavailable.
            int btnCount = Math.Min(
                RawButtonCount > 0 ? RawButtonCount : NumButtons,
                state.Buttons.Length);
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
        /// Builds a deterministic instance GUID for a physical device.
        /// Priority: VID+PID+Serial (stable across reboots for BT devices),
        /// then device path, then VID+PID+SDL instance ID as last resort.
        /// </summary>
        public static Guid BuildInstanceGuid(string devicePath, ushort vid, ushort pid, uint instanceId, string serial = null, string sdlGuid = null)
        {
            string identifier;
            bool isXInputPath = devicePath != null
                && devicePath.StartsWith("XInput#", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(serial))
            {
                // Best: serial number (e.g. BT MAC) is stable across reboots/re-pairs.
                identifier = $"serial:{vid:X4}:{pid:X4}:{serial}";
            }
            else if (isXInputPath)
            {
                // SDL's "XInput#N" path is slot-dependent and SDL's GUID is
                // derived from a name that includes the slot number, so
                // neither is a stable identifier across slot reshuffles.
                // Look up the physical PnP instance for this VID/PID
                // (filtering out HIDMaestro-parented virtuals) — that path
                // contains the BT MAC or USB hub/port address and is
                // completely slot-independent.
                //
                // PadForge's SDL3 fork filters HIDMaestro virtual XInput slots
                // out of enumeration, so any XInput#N that reaches here is a
                // real controller. Resolve to the physical HID child's stable
                // PnP path (BT MAC / USB hub+port) so the identity survives
                // xinputhid slot reshuffles.
                //
                // Multi-match disambiguation: with two same-model controllers
                // (e.g. two Xbox Series BT pads paired at once), FindAll
                // returns both candidates in a deterministic order. We pick
                // by parsing the slot index out of the SDL XInput#N path so
                // each slot consistently maps to one of the two physicals,
                // and append :slot{slot} to the identifier so the two never
                // hash to the same GUID. Each PadForge entry binds to a slot;
                // when xinputhid reshuffles which physical sits in which slot
                // (reconnect, repair, USB hub event), per-pad settings follow
                // the slot — that's just XInput's behavior for same-model
                // duplicates, since the API doesn't expose a per-physical
                // signal for them.
                IReadOnlyList<string> candidates;
                try { candidates = StableXInputInstance.FindAll(vid, pid); }
                catch { candidates = Array.Empty<string>(); }

                if (candidates.Count > 0)
                {
                    int slot = ParseXInputSlot(devicePath);
                    int idx = candidates.Count == 1
                        ? 0
                        : (slot >= 0 ? Math.Min(slot, candidates.Count - 1) : 0);
                    string physicalPath = candidates[idx];

                    identifier = candidates.Count > 1
                        ? $"pnp:{physicalPath}:slot{(slot >= 0 ? slot : idx)}"
                        : $"pnp:{physicalPath}";
                }
                else if (!string.IsNullOrEmpty(sdlGuid) && !sdlGuid.All(c => c == '0'))
                {
                    identifier = $"sdlguid:{sdlGuid}";
                }
                else
                {
                    identifier = $"{devicePath}:{vid:X4}:{pid:X4}";
                }
            }
            else if (!string.IsNullOrEmpty(devicePath))
            {
                // Include VID/PID so different hardware sharing the same
                // path (e.g. two different Xbox controllers in XInput#0)
                // gets distinct identity.
                identifier = $"{devicePath}:{vid:X4}:{pid:X4}";
            }
            else
            {
                // Last resort: session-specific SDL instance ID.
                identifier = $"sdl:{vid:X4}:{pid:X4}:{instanceId}";
            }

            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(identifier));
                return new Guid(hash);
            }
        }

        /// <summary>
        /// Extracts the slot index from an SDL XInput-shaped device path
        /// (e.g. "XInput#0" → 0, "XInput#3" → 3). Returns -1 when the path
        /// doesn't match the expected shape. Used by
        /// <see cref="BuildInstanceGuid"/> to map SDL's slot numbering onto
        /// the lexicographically-sorted physical-instance list when more
        /// than one same-model controller is present.
        /// </summary>
        private static int ParseXInputSlot(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return -1;
            const string prefix = "XInput#";
            if (!devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return -1;

            int start = prefix.Length;
            int end = start;
            while (end < devicePath.Length && devicePath[end] >= '0' && devicePath[end] <= '9')
                end++;
            if (end == start) return -1;

            return int.TryParse(devicePath.AsSpan(start, end - start), out int slot) ? slot : -1;
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
            int btnCount = Math.Max(NumButtons, RawButtonCount);
            int totalObjects = NumAxes + NumHats + btnCount;
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

            bool isGamepad = GameController != IntPtr.Zero;

            // --- Axes ---
            for (int i = 0; i < NumAxes; i++)
            {
                var item = new DeviceObjectItem();
                item.InputIndex = i;

                if (i < standardAxisGuids.Length)
                {
                    item.ObjectTypeGuid = standardAxisGuids[i];
                    item.Name = isGamepad ? GetGamepadAxisName(i) : GetStandardAxisName(i);
                }
                else
                {
                    item.ObjectTypeGuid = ObjectGuid.Slider;
                    item.Name = $"Slider {i - standardAxisGuids.Length}";
                }

                item.ObjectType = DeviceObjectTypeFlags.AbsoluteAxis;
                item.Offset = i * 4; // Simulated offset for identification.
                items[index++] = item;
            }

            // --- Hats ---
            for (int i = 0; i < NumHats; i++)
            {
                var item = new DeviceObjectItem();
                item.InputIndex = i;
                item.ObjectTypeGuid = ObjectGuid.PovController;
                item.Name = isGamepad ? "D-Pad"
                    : NumHats == 1 ? "POV" : $"POV {i}";
                item.ObjectType = DeviceObjectTypeFlags.PointOfViewController;
                item.Offset = (NumAxes + i) * 4;
                items[index++] = item;
            }

            // --- Buttons ---
            // For SDL3-recognized gamepads, skip positions 11-21 the device
            // doesn't physically have (asked via SDL_GamepadHasButton) so
            // an Xbox 360 doesn't show "Misc 1" / "Right Paddle 1" in the
            // dropdown. Positions 0-10 are always present on any recognized
            // gamepad. Raw joystick devices (isGamepad=false) keep the flat
            // "Button N" enumeration unchanged.
            int finalCount = 0;
            for (int i = 0; i < btnCount; i++)
            {
                bool include = true;
                if (isGamepad && i >= 11 && i <= 21)
                {
                    int sdlButton = GamepadButtonForPosition(i);
                    include = sdlButton >= 0 && SDL_GamepadHasButton(GameController, sdlButton);
                }
                if (!include) continue;

                var item = new DeviceObjectItem();
                item.InputIndex = i;
                item.ObjectTypeGuid = ObjectGuid.Button;
                item.Name = (isGamepad && i < NumButtons) ? GetGamepadButtonName(i) : $"Button {i}";
                item.ObjectType = DeviceObjectTypeFlags.PushButton;
                item.Offset = (NumAxes + NumHats + i) * 4;
                items[index++] = item;
                finalCount++;
            }

            // Trim if we skipped any — caller iterates Length, can't have nulls.
            int totalIncluded = NumAxes + NumHats + finalCount;
            if (totalIncluded < items.Length)
            {
                var trimmed = new DeviceObjectItem[totalIncluded];
                Array.Copy(items, trimmed, totalIncluded);
                return trimmed;
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
                        // Route raw HID product strings through the
                        // sanitizer so embedded nulls / control chars /
                        // garbage bytes from cheap USB descriptors don't
                        // crash XmlSerializer at save time (issue #53).
                        return DeviceNameSanitizer.Clean(
                            Encoding.Unicode.GetString(buffer));
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
        /// Returns a human-readable name for standard axis indices 0–5 (raw joystick devices).
        /// Uses DirectInput-style naming matching joy.cpl.
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

        /// <summary>
        /// Returns a gamepad-friendly axis name for indices 0–5.
        /// Order matches PadForge internal mapping: LX, LY, LT, RX, RY, RT.
        /// </summary>
        private static string GetGamepadAxisName(int axisIndex)
        {
            return axisIndex switch
            {
                0 => "Left Stick X",
                1 => "Left Stick Y",
                2 => "Left Trigger",
                3 => "Right Stick X",
                4 => "Right Stick Y",
                5 => "Right Trigger",
                _ => $"Axis {axisIndex}"
            };
        }

        /// <summary>
        /// Returns a gamepad-friendly button name for the standardized gamepad
        /// button positions (0-10 standard + 11-21 extended). The extended
        /// positions are read via the SDL gamepad API in
        /// <see cref="GetGamepadState"/>; SDL returns false for buttons the
        /// device doesn't have, so callers should also gate inclusion in
        /// device-object enumeration on <see cref="SDL_GamepadHasButton"/>.
        /// </summary>
        private static string GetGamepadButtonName(int buttonIndex)
        {
            return buttonIndex switch
            {
                0 => "A",
                1 => "B",
                2 => "X",
                3 => "Y",
                4 => "Left Shoulder",
                5 => "Right Shoulder",
                6 => "Back",
                7 => "Start",
                8 => "Left Stick Button",
                9 => "Right Stick Button",
                10 => "Guide",
                11 => "Misc 1",
                12 => "Right Paddle 1",
                13 => "Left Paddle 1",
                14 => "Right Paddle 2",
                15 => "Left Paddle 2",
                16 => "Touchpad Click",
                17 => "Misc 2",
                18 => "Misc 3",
                19 => "Misc 4",
                20 => "Misc 5",
                21 => "Misc 6",
                _ => $"Button {buttonIndex}"
            };
        }

        /// <summary>
        /// Maps a PadForge button position (0-21) to the SDL gamepad button
        /// enum it reads from. Used by <see cref="GetDeviceObjects"/> to skip
        /// positions whose backing button isn't physically present on the
        /// device (so an Xbox 360 doesn't show "Misc 1" / "Right Paddle 1"
        /// in the dropdown). Positions 11-21 follow SDL's
        /// <c>SDL_GamepadButton</c> enum order (Misc1, paddles R1/L1/R2/L2,
        /// Touchpad, Misc2-Misc6), skipping the four DPad slots that
        /// PadForge synthesizes into POV[0].
        /// </summary>
        /// <summary>
        /// Builds the sparse list of button positions this device exposes,
        /// used to populate <see cref="SupportedButtonIndices"/>. For SDL3
        /// gamepads, positions 11-21 are gated on <c>SDL_GamepadHasButton</c>
        /// so an Xbox 360 (no paddles, no Misc, no touchpad) reports just
        /// 0-10, while a DualSense Edge reports 0-10 plus its actual
        /// paddle / Mute / touchpad slots. Raw passthrough indices ≥22 are
        /// included only when not already consumed by the gamepad mapping
        /// (matches <see cref="GetGamepadState"/>'s passthrough loop).
        /// Non-gamepad devices get a dense 0..NumButtons-1 list.
        /// </summary>
        private int[] ComputeSupportedButtonIndices()
        {
            int max = Math.Min(NumButtons, CustomInputState.MaxButtons);
            var list = new System.Collections.Generic.List<int>(max);

            if (GameController != IntPtr.Zero)
            {
                for (int i = 0; i < 11 && i < max; i++)
                    list.Add(i);

                for (int i = 11; i <= 21 && i < max; i++)
                {
                    int sdlButton = GamepadButtonForPosition(i);
                    if (sdlButton >= 0 && SDL_GamepadHasButton(GameController, sdlButton))
                        list.Add(i);
                }

                int rawCount = Math.Min(RawButtonCount, CustomInputState.MaxButtons);
                for (int i = 22; i < rawCount; i++)
                {
                    if (_mappedRawButtonIndices != null && _mappedRawButtonIndices.Contains(i))
                        continue;
                    list.Add(i);
                }
            }
            else
            {
                for (int i = 0; i < max; i++)
                    list.Add(i);
            }

            return list.ToArray();
        }

        private static int GamepadButtonForPosition(int position)
        {
            return position switch
            {
                0 => SDL_GAMEPAD_BUTTON_SOUTH,
                1 => SDL_GAMEPAD_BUTTON_EAST,
                2 => SDL_GAMEPAD_BUTTON_WEST,
                3 => SDL_GAMEPAD_BUTTON_NORTH,
                4 => SDL_GAMEPAD_BUTTON_LEFT_SHOULDER,
                5 => SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER,
                6 => SDL_GAMEPAD_BUTTON_BACK,
                7 => SDL_GAMEPAD_BUTTON_START,
                8 => SDL_GAMEPAD_BUTTON_LEFT_STICK,
                9 => SDL_GAMEPAD_BUTTON_RIGHT_STICK,
                10 => SDL_GAMEPAD_BUTTON_GUIDE,
                11 => SDL_GAMEPAD_BUTTON_MISC1,
                12 => SDL_GAMEPAD_BUTTON_RIGHT_PADDLE1,
                13 => SDL_GAMEPAD_BUTTON_LEFT_PADDLE1,
                14 => SDL_GAMEPAD_BUTTON_RIGHT_PADDLE2,
                15 => SDL_GAMEPAD_BUTTON_LEFT_PADDLE2,
                16 => SDL_GAMEPAD_BUTTON_TOUCHPAD,
                17 => SDL_GAMEPAD_BUTTON_MISC2,
                18 => SDL_GAMEPAD_BUTTON_MISC3,
                19 => SDL_GAMEPAD_BUTTON_MISC4,
                20 => SDL_GAMEPAD_BUTTON_MISC5,
                21 => SDL_GAMEPAD_BUTTON_MISC6,
                _ => -1
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
    /// Strategy for translating dual-motor rumble values into SDL haptic effects.
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
