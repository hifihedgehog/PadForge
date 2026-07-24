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

        /// <summary>Whether this is a bare Wii Remote whose IR camera PadForge
        /// surfaces as an "IR Pointer" source (issue #146).</summary>
        public bool HasIrCamera { get; private set; }

        /// <summary>Whether this is a Wii Balance Board (issue #146).</summary>
        public bool IsBalanceBoard { get; private set; }

        /// <summary>Whether this is a standalone right Joy-Con whose NIR camera
        /// PadForge surfaces as an "IR Brightness" cover/proximity source
        /// (issue #151).</summary>
        public bool HasJoyConIr { get; private set; }

        /// <summary>Whether this is a Joy-Con 2 (L or R) whose optical mouse
        /// sensor PadForge surfaces as "Mouse Motion X/Y" sources (issue #154).
        /// True when the fork's BLE Switch 2 driver posts the sensor counters
        /// on joystick axes 6/7 (SDL#8, raw axis count 8).</summary>
        public bool HasJoyCon2Mouse { get; private set; }

        /// <summary>Cycles the motion sensors off and back on. The SDL
        /// fork decides the right Joy-Con NIR camera's fate only at this
        /// enable edge (SDL_hidapi_switch.c SetSensorsEnabled: hint set ->
        /// EnableIRSensor, hint clear while active -> DisableIRSensor via
        /// the disable leg), so a RUNTIME flip of the IR hint needs one
        /// bounce to take effect without a reconnect (#248 audit round 2).
        /// Gyro/accel streams resume immediately; one bounce costs a few
        /// sensor frames.</summary>
        public void BounceMotionSensors()
        {
            if (GameController == IntPtr.Zero) return;
            if (HasGyro) SDL_SetGamepadSensorEnabled(GameController, SDL_SENSOR_GYRO, false);
            if (HasAccel) SDL_SetGamepadSensorEnabled(GameController, SDL_SENSOR_ACCEL, false);
            if (HasGyro) SDL_SetGamepadSensorEnabled(GameController, SDL_SENSOR_GYRO, true);
            if (HasAccel) SDL_SetGamepadSensorEnabled(GameController, SDL_SENSOR_ACCEL, true);
        }

        /// <summary>Whether this device has an NFC reader the SDL fork can
        /// drive (issue #241/#248, SDL#15): the classic Switch right
        /// Joy-Con (PID 0x2007), the Pro Controller (PID 0x2009), and the
        /// combined Joy-Con pair (synthetic PID 0x2008), whose right child
        /// carries the MCU. The reader is only powered when NFC is armed
        /// and the SDL_HINT_JOYSTICK_HIDAPI_SWITCH_NFC hint is set; this
        /// flag just says the hardware can. Read via
        /// SDL_GetGamepadNfcTagUid.</summary>
        public bool HasNfcReader { get; private set; }

        /// <summary>Total raw joystick axis count (before the gamepad layout pins
        /// <see cref="NumAxes"/> to 6). SDL's convention is that device-specific
        /// analog data beyond the six standard gamepad axes rides raw joystick axes
        /// 6+ (e.g. upstream's PS3 driver posts the DualShock 3 pressure buttons on
        /// axes 6-15). Captured at open, mirroring <see cref="RawButtonCount"/>.</summary>
        public int RawAxisCount { get; private set; }

        /// <summary>True when a gamepad-opened device carries raw joystick axes
        /// beyond the standard six that should be surfaced as generic "Axis N"
        /// mapping sources for the user to bind (issue #193). Excludes devices whose
        /// extra axes are already surfaced as dedicated sensor sources (Wii IR
        /// pointer, Joy-Con NIR / mouse): a raw IR-dot coordinate or wrapping mouse
        /// counter is not a usable, settling axis, and those readers already keep it
        /// out of the generic Axis[] array. So this is "expose the extra analog axes,
        /// except the ones already claimed as processed sensor sources".</summary>
        public bool HasExtraGenericAxes { get; private set; }

        /// <summary>Whether the device has an accelerometer sensor.</summary>
        public bool HasAccel { get; private set; }

        /// <summary>Whether the device exposes the auxiliary (left-side)
        /// accelerometer, SDL_SENSOR_ACCEL_L (issue #199): the Nunchuk on a
        /// Nunchuk-attached Wii Remote, the left half of a combined Joy-Con
        /// pair.</summary>
        public bool HasAccelAux { get; private set; }

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

        /// <summary>Per-channel capsense capability from SDL_GamepadHasCapSense
        /// (fork API, SDL_gamepad.h since 3.6.0), indexed by the
        /// SDL_GAMEPAD_CAPSENSE_* constants (left stick / right stick /
        /// left grip / right grip). Null when no channel exists, so the
        /// per-frame fill and the CustomInputState allocation stay free for
        /// the overwhelmingly common capsense-less device.</summary>
        private bool[] _capSenseChannels;

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

            // Always capture the raw joystick button/axis counts before any gamepad
            // override pins NumAxes/NumButtons to the standardized layout.
            RawButtonCount = SDL_GetNumJoystickButtons(Joystick);
            RawAxisCount = SDL_GetNumJoystickAxes(Joystick);

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

                // Auxiliary (left-side) accelerometer (issue #199): the Wii
                // Nunchuk on a Nunchuk-attached remote, or the left half of a
                // combined Joy-Con pair. Upstream SDL has delivered it as
                // SDL_SENSOR_ACCEL_L since 2022 (SDL_hidapi_wii.c registers it
                // alongside the remote-body accel; SDL_hidapi_switch.c on the
                // pair's left child); PadForge simply never read it.
                HasAccelAux = SDL_GamepadHasSensor(GameController, SDL_SENSOR_ACCEL_L);
                if (HasAccelAux) SDL_SetGamepadSensorEnabled(GameController, SDL_SENSOR_ACCEL_L, true);

                // Capacitive touch channels (fork API): stick-top touch on
                // Steam Controller-family devices, grip capsense on the SC
                // 2026. Probe once at open; only allocate the per-channel
                // capability map when at least one channel exists.
                for (int c = 0; c < SDL_GAMEPAD_CAPSENSE_COUNT; c++)
                {
                    if (SDL_GamepadHasCapSense(GameController, c))
                    {
                        _capSenseChannels ??= new bool[SDL_GAMEPAD_CAPSENSE_COUNT];
                        _capSenseChannels[c] = true;
                    }
                }

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

            // Wii Remote IR camera + Wii Balance Board (issue #146). Both are
            // Nintendo VID 0x057E, and the board enumerates as a Wii Remote
            // (PID 0x0306), so the board is told apart by its SDL name. Enabling the
            // accel sensor above also powers the IR camera in the SDL hidapi_wii
            // driver, which posts the two IR dots on dedicated joystick axes 6-9
            // (SDL#6 follow-up commit 41909fdc4e), working for a bare remote AND for
            // one with a Nunchuk or Classic Controller (report 0x37, Basic IR).
            bool isWiiVendor = VendorId == 0x057E;
            IsBalanceBoard = isWiiVendor && !string.IsNullOrEmpty(Name)
                && Name.IndexOf("Balance Board", StringComparison.OrdinalIgnoreCase) >= 0;
            // A camera-capable Wii Remote is the one the driver gives the four extra
            // IR axes (raw joystick axis count 10), regardless of extension. Wii U
            // Pro and Balance Board stay at 6 axes and are excluded. Reading the raw
            // joystick axis count (not NumAxes, which is pinned to 6 for a
            // gamepad-opened device) is the stable signal the SDL contract defines.
            HasIrCamera = isWiiVendor && !IsBalanceBoard
                && Joystick != IntPtr.Zero && SDL_GetNumJoystickAxes(Joystick) >= 10;

            // Right Joy-Con NIR camera (issue #151). The SDL fork's hidapi_switch
            // gives a STANDALONE right Joy-Con (VID 0x057E PID 0x2007, not a
            // combined pair) one extra joystick axis beyond the six gamepad axes
            // (raw axis count 7, SDL#7 commit a31980950a) and posts the MCU's
            // average-intensity byte there once the camera powers (hint + sensors
            // enabled). Same detection idiom as the Wii IR axes above: the raw
            // joystick axis count is the stable signal the SDL contract defines.
            HasJoyConIr = VendorId == 0x057E && ProductId == 0x2007
                && Joystick != IntPtr.Zero && SDL_GetNumJoystickAxes(Joystick) >= 7;

            // Joy-Con 2 optical mouse sensor (issue #154). The fork's BLE Switch 2
            // driver posts the sensor's absolute 16-bit counters on joystick axes
            // 6/7 for a Joy-Con 2 L (PID 0x2067) or R (PID 0x2066) when its mouse
            // hint is set (SDL#8, raw axis count 8). Same raw-naxes contract idiom
            // as the Wii IR axes and the Joy-Con NIR scalar above.
            HasJoyCon2Mouse = VendorId == 0x057E
                && (ProductId == 0x2066 || ProductId == 0x2067)
                && Joystick != IntPtr.Zero && SDL_GetNumJoystickAxes(Joystick) >= 8;

            // NFC reader (issue #241/#248, SDL#15). The NFC/IR MCU lives on
            // the classic Switch right Joy-Con (PID 0x2007) and Pro
            // Controller (PID 0x2009). The combined pair (synthetic PID
            // 0x2008, SDL_hidapijoystick.c:1090) contains a right Joy-Con,
            // and SDL propagates the combined joystick to every child
            // (SDL_hidapijoystick.c:784-787), so the right child posts the
            // tag UID onto the pair's joystick exactly like its GYRO_R.
            // Gated on GameController != 0 because the tag getter is a
            // gamepad-layer call. Switch 2 controllers are excluded: no
            // reference reads their NFC on PC and there is no working code
            // over any transport (verified 2026-07-24), so PadForge offers
            // no NFC affordance the fork cannot back.
            HasNfcReader = VendorId == 0x057E
                && (ProductId == 0x2007 || ProductId == 0x2008 || ProductId == 0x2009)
                && GameController != IntPtr.Zero;

            // Generic extra joystick axes (issue #193). A gamepad-opened device may
            // report raw joystick axes beyond the standard six that carry ordinary
            // analog inputs. Upstream SDL's own PS3 driver does exactly this: it
            // posts the DualShock 3's 10 button-pressure values on axes 6-15. A DS3
            // in DsHidMini SDF mode reaches us the same way. Surface those as generic
            // "Axis N" sources for the user to map. Exclude the sensor-camera devices
            // detected above: their extra axes are IR-dot / mouse-counter / brightness
            // data with their own dedicated sources, not usable raw axes, and a
            // never-settling sensor value in the generic Axis[] array would poison
            // the input-activity detectors (recorder / sticky-shift / idle).
            HasExtraGenericAxes = GameController != IntPtr.Zero
                && RawAxisCount > 6
                && !HasIrCamera && !HasJoyConIr && !HasJoyCon2Mouse;

            // Always try the haptic API for force feedback devices (joysticks,
            // wheels, etc.). Some report HasRumble=true via SDL properties but
            // only actually work through the haptic effect system. The routing
            // in ForceFeedbackState prefers HasHaptic when available.
            OpenHaptic();

            // Build stable GUIDs for settings matching.
            ProductGuid = BuildProductGuid(VendorId, ProductId);
            InstanceGuid = BuildInstanceGuid(DevicePath, VendorId, ProductId, instanceId, SerialNumber, SdlGuid);

            // Some bridged devices (the DS3 over BthPS3 / WinUSB) expose no SDL path
            // because they are virtual joysticks, but they DO connect over a real
            // interface. Surface that path for display + transport classification AFTER
            // the identity is fixed above, so the differing per-transport paths never
            // reach BuildInstanceGuid (the identity stays on the stable SDL GUID).
            if (string.IsNullOrEmpty(DevicePath))
            {
                var extPath = ExternalDevicePathProvider?.Invoke(SdlInstanceId);
                if (!string.IsNullOrEmpty(extPath)) DevicePath = extPath;
            }

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

            // Reset NFC pulse/edge state so a wrapper that is closed and
            // reopened (Open supports this) cannot inherit a stale press or
            // suppress the first equal UID from the prior connection (Codex
            // #11). Harmless when the app reconnect path makes a fresh
            // wrapper instead.
            _nfcPrevUid = null;
            if (_nfcPulseUntil != null) Array.Clear(_nfcPulseUntil, 0, _nfcPulseUntil.Length);

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

            // A physically detached handle still "reads" — SDL returns signed 0
            // for every axis, which the unsigned conversion turns into 32768
            // (center). For a wheel pedal mapped as an inverted trigger that
            // center becomes 65535-32768 = 32767 (~50% engaged) during the
            // disconnect-debounce window, and Step 3 stamps it into the slot
            // output. Detachment is permanent for this handle (a replug gets a
            // new SDL instance), so refuse the read; Step 2 treats null as a
            // failed read and marks the device offline, keeping the last GOOD
            // input state.
            if (!SDL_JoystickConnected(Joystick))
                return null;

            // When the device is opened as a Gamepad, use the gamepad API to read
            // through SDL's built-in mapping layer (gamecontrollerdb). This remaps
            // DualSense, DualShock, Switch Pro, etc. to the standardized Xbox layout
            // so the same auto-mapping works for all recognized controllers.
            // ForceRaw bypasses this for devices whose SDL mapping is wrong
            // (e.g., DsHidMini DS3 in SDF mode).
            CustomInputState state = (!forceRaw && GameController != IntPtr.Zero)
                ? GetGamepadState()
                : GetJoystickState();

            // Wii Remote IR pointer rides dedicated raw joystick axes 6-9 (SDL#6
            // follow-up 41909fdc4e), which the gamepad mapping does not surface, so
            // it is read joystick-direct here regardless of which path built the state.
            if (HasIrCamera && state != null)
                ReadIrPointer(state);

            // Right Joy-Con NIR camera scalar rides dedicated joystick axis 6
            // (SDL#7), also outside the gamepad mapping, so it is read
            // joystick-direct the same way.
            if (HasJoyConIr && state != null)
                ReadJoyConIr(state);

            // Joy-Con 2 optical mouse counters ride dedicated joystick axes 6/7
            // (SDL#8), read joystick-direct the same way.
            if (HasJoyCon2Mouse && state != null)
                ReadJoyCon2Mouse(state);

            // NFC tag reader (issue #241). Read gamepad-layer, gated on the
            // arming provider so the MCU stays off (and this stays a no-op)
            // until a slot arms an NFC trigger.
            if (HasNfcReader && state != null)
                ReadNfcTag(state);

            return state;
        }

        // ─── NFC tag reader (issue #241, SDL#15) ───
        // The Engine wrapper cannot see the App-side NfcTagRegistry, so the
        // App wires these providers at startup (the SourceCoercion provider
        // idiom). All null until wired, which keeps the Engine standalone.

        /// <summary>True when any slot has armed an NFC trigger, so the MCU
        /// should be powered and the tag getter polled. App-wired.</summary>
        public static Func<bool> NfcArmedProvider;
        /// <summary>Resolves a tag UID to its stable NfcTagRegistry button
        /// (0 = registered nowhere / use Any only, &gt;0 = that tag's button,
        /// -1 = unregistered). App-wired to NfcTagRegistry.ButtonForUid.</summary>
        public static Func<string, int> NfcTagButtonResolver;
        /// <summary>Highest registry button in use, so the NfcTag array spans
        /// every registered tag. App-wired to NfcTagRegistry.MaxButtonInUse.</summary>
        public static Func<int> NfcTagSpanProvider;
        /// <summary>Raised (rising edge only) when a controller reads a tag,
        /// so the registration flow can capture a controller-sourced UID the
        /// same way it captures a PC/SC reader's. App-wired.</summary>
        public static Action<string> NfcTagDetectedForRegistration;

        /// <summary>Tag-button hold, mirroring NfcReaderDevice.PulseMs: a
        /// held tag streams present from the getter, so the button stays
        /// pressed while the tag rests and releases this long after removal,
        /// smoothing any single-poll gap into a clean momentary edge.</summary>
        private const int NfcPulseMs = 175;
        private long[] _nfcPulseUntil;
        private string _nfcPrevUid;

        private void ReadNfcTag(CustomInputState state)
        {
            long now = Environment.TickCount64;
            var armed = NfcArmedProvider;
            if (armed == null || !armed())
            {
                // Not armed: FULLY reset the pulse and edge state, not just
                // the output array. A stale deadline left in _nfcPulseUntil
                // would otherwise resurrect a false press when NFC re-arms
                // (Codex #4). The array stays allocated (cleared) so the
                // codec still omits it and consumers read "no NFC".
                _nfcPrevUid = null;
                if (_nfcPulseUntil != null) Array.Clear(_nfcPulseUntil, 0, _nfcPulseUntil.Length);
                if (state.NfcTag != null) Array.Clear(state.NfcTag, 0, state.NfcTag.Length);
                return;
            }

            _nfcPulseUntil ??= new long[CustomInputState.MaxButtons];

            if (SDL_TryGetGamepadNfcTagUid(GameController, out string uid)
                && !string.IsNullOrEmpty(uid))
            {
                // Rising edge for registration, tied to the SAME pulse window
                // as the exposed button (Codex #5): raise only when the tag
                // is different or the Any-tag pulse had lapsed (a real
                // absence, not one dropped poll the fork's own 2 s debounce
                // would bridge). A resting tag never re-raises.
                bool anyPressed = _nfcPulseUntil[0] != 0 && now < _nfcPulseUntil[0];
                if (!anyPressed || !string.Equals(uid, _nfcPrevUid, StringComparison.Ordinal))
                {
                    try { NfcTagDetectedForRegistration?.Invoke(uid); } catch { }
                }
                _nfcPrevUid = uid;
                long until = now + NfcPulseMs;
                _nfcPulseUntil[0] = until; // Any NFC Tag
                int button = NfcTagButtonResolver?.Invoke(uid) ?? -1;
                if (button > 0 && button < _nfcPulseUntil.Length)
                    _nfcPulseUntil[button] = until;
            }
            // A single absent poll does NOT clear _nfcPrevUid: the pulse holds
            // the button through a dropped poll, so the edge tracker holds too.
            // It clears below once the Any pulse actually lapses.

            // Expire deadlines across the WHOLE pulse array (not just the
            // current span), so a span that shrank and later regrew cannot
            // inherit a stale deadline on a reused button (Codex #4).
            for (int b = 0; b < _nfcPulseUntil.Length; b++)
                if (_nfcPulseUntil[b] != 0 && now >= _nfcPulseUntil[b])
                    _nfcPulseUntil[b] = 0;

            // Size the tag array to span every registered button (1 = "Any"
            // plus the highest tag button) and write each button's state.
            int span = 1 + Math.Max(0, NfcTagSpanProvider?.Invoke() ?? 0);
            if (span > CustomInputState.MaxButtons) span = CustomInputState.MaxButtons;
            if (state.NfcTag == null || state.NfcTag.Length != span)
                state.NfcTag = new bool[span];
            for (int b = 0; b < span; b++)
                state.NfcTag[b] = _nfcPulseUntil[b] != 0;

            if (_nfcPulseUntil[0] == 0) _nfcPrevUid = null;
        }

        // Joy-Con 2 optical mouse sensor (issue #154). The fork's BLE Switch 2
        // driver posts the sensor's ABSOLUTE 16-bit X/Y counters on joystick
        // axes 6/7, bit-preserved as Sint16 (SDL#8; report 0x05 bytes 0x10-0x13
        // per switch2_controller_research hid_reports.md, cross-confirmed by
        // joycon2cpp GetRawOpticalMouse, joycon2mouse joycon.py:105-106, and
        // jc2mouse driver.py:71-74). PadForge derives per-poll deltas with
        // 16-bit wraparound, the jc2mouse delta_u16 idiom (driver.py:174-176):
        // the counter wraps at 0x10000, so the signed 16-bit difference is the
        // true motion for any delta under half the counter range. The first
        // read only primes the previous value (joycon2mouse's None guard), so
        // connect never emits a spurious jump from counter 0.
        // Poll-thread only: the poll loop is the sole GetCurrentState caller
        // (Remote Link ships the poll snapshot instead of reading the wrapper,
        // so a second caller can no longer split the motion or tear this
        // baseline pair), which is why these fields need no synchronization.
        private int _jc2MousePrevX, _jc2MousePrevY;
        private bool _jc2MouseHasPrev;

        private void ReadJoyCon2Mouse(CustomInputState state)
        {
            ushort curX = (ushort)SDL_GetJoystickAxis(Joystick, 6);
            ushort curY = (ushort)SDL_GetJoystickAxis(Joystick, 7);
            // All-zero counters = optical stream not active yet (jc2mouse's
            // _optical_active idiom, driver.py:594-595; ReadIrPointer carries
            // the same all-zero guard). Without this, the one-time priming
            // poll can capture (0,0) during the sensor's warm-up window and
            // the first real absolute counter then emits a spurious
            // one-frame jump of up to half the counter range. Re-priming on
            // every all-zero read keeps the baseline aligned with the first
            // ACTIVE report (jc2mouse resets its baselines on mouse-mode
            // entry for the same reason, driver.py:2501-2502). A live
            // counter pair crossing exactly (0,0) mid-stream costs one
            // dropped motion frame, not a jump.
            if (curX == 0 && curY == 0)
            {
                _jc2MouseHasPrev = false;
                return; // state fields stay 0 while the stream is inactive
            }
            if (!_jc2MouseHasPrev)
            {
                _jc2MousePrevX = curX;
                _jc2MousePrevY = curY;
                _jc2MouseHasPrev = true;
                return; // state fields stay 0 for the priming poll
            }
            state.JoyCon2MouseDX = (short)(curX - _jc2MousePrevX);
            state.JoyCon2MouseDY = (short)(curY - _jc2MousePrevY);
            _jc2MousePrevX = curX;
            _jc2MousePrevY = curY;
        }

        // The SDL fork posts the right Joy-Con MCU's average-intensity byte
        // (buf[53], 0-255) on dedicated joystick axis 6, scaled 0..32767
        // (SDL_hidapi_switch.c HandleMcuIRReport, SDL#7 commit a31980950a). The
        // axis reads 0 while the camera is off, so no sentinel handling is
        // needed: normalize the positive range to 0..1 and clamp any negative
        // (never posted by the fork) to 0.
        private void ReadJoyConIr(CustomInputState state)
        {
            short raw = SDL_GetJoystickAxis(Joystick, 6);
            state.JoyConIrIntensity = raw <= 0 ? 0f : raw / 32767f;
        }

        // The SDL hidapi_wii driver posts the two IR dots on DEDICATED joystick
        // axes 6-9 (SDL#6 follow-up 41909fdc4e), separate from the gamepad sticks so
        // an extension (Nunchuk/Classic) keeps axes 0-3: axis6 = dot0_x (0..1023),
        // axis7 = dot0_y (0..767), axis8 = dot1_x, axis9 = dot1_y, with -1 meaning
        // "dot not detected". The two dots are the two sensor-bar LEDs; their
        // midpoint is the aim point. Works for a bare remote (Extended IR, 0x33) and
        // one with an extension (Basic IR, 0x37). Both feed the same axes 6-9.
        private void ReadIrPointer(CustomInputState state)
        {
            var (x, y, detected) = ComputeIrAim(
                SDL_GetJoystickAxis(Joystick, 6),
                SDL_GetJoystickAxis(Joystick, 7),
                SDL_GetJoystickAxis(Joystick, 8),
                SDL_GetJoystickAxis(Joystick, 9));

            if (!detected)
            {
                state.Ir.Detected = false;
                return;
            }

            // Pointer-tab tuning (sensor-bar offset, smoothing) is applied at
            // the SLOT-scoped read (SourceCoercion.ReadTunedIrPointer), not
            // here: this wrapper is per-device and one remote can feed several
            // virtual controllers, each with its own Pointer-tab settings
            // (issue #146 follow-up). state.Ir carries the raw screen-aligned
            // aim only.
            state.Ir.X = x;
            state.Ir.Y = y;
            state.Ir.Detected = true;
        }

        /// <summary>Screen-aligned aim from the two raw IR dot slots. The aim
        /// exists ONLY when BOTH sensor-bar dots are visible: every proven
        /// pointer reference computes the aim as the midpoint of a dot PAIR
        /// and treats fewer than two dots as out of reach (Touchmote
        /// ScreenPositionCalculator.cs:89-160, foundMidpoint requires a
        /// Found i/j pair, !foundMidpoint returns OutOfReach; same scan in
        /// Ryochan7-lightgun :207-315 and Suegrini-4IR :183-291, and
        /// WiimoteLib-Trihy itself zeroes IRState.Midpoint unless sensors
        /// 0 AND 1 are both Found, Wiimote.cs:663-672). A
        /// single-dot fallback is NOT allowed: when one LED leaves the
        /// camera view the midpoint would snap to the surviving dot, half a
        /// dot-separation away, and a steady sweep would re-walk that span
        /// of the screen (the #203 bench "double walk"). Shared with the
        /// unit tests.</summary>
        internal static (float X, float Y, bool Detected) ComputeIrAim(
            short d0x, short d0y, short d1x, short d1y)
        {
            // Before the first IR report arrives, SDL axes read their default 0,
            // which is indistinguishable from "dot at pixel (0,0)" per axis. All
            // four at exactly 0 means both dots on the same pixel, which is
            // physically impossible for two sensor-bar LEDs, so treat it as
            // "no report yet" rather than yanking the pointer to a corner on
            // connect (seen on first hardware contact, 2026-07-01 log).
            if (d0x == 0 && d0y == 0 && d1x == 0 && d1y == 0)
                return (0f, 0f, false);

            if (d0x < 0 || d0y < 0 || d1x < 0 || d1y < 0)
                return (0f, 0f, false); // fewer than two dots: out of reach

            float sx = (d0x + d1x) * 0.5f;
            float sy = (d0y + d1y) * 0.5f;

            // Camera frame is 1024x768. Normalize the dot midpoint to the [-1..+1]
            // stick range. X is mirrored, Y is NOT. Confirmed against the proven
            // Wii-pointer references: Touchmote ScreenPositionCalculator.cs:173 does
            // `relativePosition.X = 1 - X` (mirror) and applies no Y inversion (only a
            // sensor-bar pixel offset, lines 162-171), and WiimoteLib Wiimote.cs:653/658
            // normalizes both axes as raw/1023.5 and raw/767.5 with no flip. All four
            // Touchmote variants agree. So screen-aligned aim is X mirrored, Y direct:
            // X = -1 left / +1 right, Y = -1 top / +1 bottom.
            float nx = sx / 1023.5f;
            float ny = sy / 767.5f;
            float x = (0.5f - nx) * 2f;   // mirrored
            float y = (ny - 0.5f) * 2f;   // not flipped
            return (Math.Clamp(x, -1f, 1f), Math.Clamp(y, -1f, 1f), true);
        }

        /// <summary>
        /// Reads input through SDL's gamepad mapping layer. Produces a standardized
        /// CustomInputState layout that matches CreateDefaultPadSetting:
        ///   Axes: [0]=LX, [1]=LY, [2]=LT, [3]=RX, [4]=RY, [5]=RT
        ///   Buttons: [0]=A, [1]=B, [2]=X, [3]=Y, [4]=LB, [5]=RB,
        ///            [6]=Back, [7]=Start, [8]=LS, [9]=RS, [10]=Guide
        ///   POV[0]: D-pad synthesized from gamepad D-pad buttons.
        /// </summary>
        // ── Pooled state buffers (perf audit 2026-07-20) ──
        // Two per-wrapper CustomInputState instances, alternated per read.
        // Retainer footprint (agents, this audit): the published instance
        // must stay intact for exactly one tick (ud.OldInputState's idle
        // compare) and no consumer holds it longer, so two buffers
        // suffice. ResetForReuse restores exact fresh-construction
        // semantics (reflection-guarded by CustomInputStateMirrorTests),
        // so decoders that rely on fresh-zero fields stay correct.
        // Cross-thread preview readers may observe mixed-adjacent-tick
        // values while a buffer is rewritten, the same acceptance
        // GyroCalibratorService and TouchpadOverlayDevice document.
        private PooledInputStatePair _statePool;

        private CustomInputState NextPooledState() => _statePool.Next();

        private CustomInputState GetGamepadState()
        {
            var state = NextPooledState();

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

            // --- Extra raw axes ---
            // Append raw joystick axes beyond the six standardized gamepad axes,
            // the same passthrough shape as the extra raw buttons below (issue
            // #193). SDL posts device-specific analog data on axes 6+ (e.g. the
            // DualShock 3's pressure buttons), which the gamepad mapping does not
            // surface. Gated to HasExtraGenericAxes so the sensor-camera devices
            // (Wii IR / Joy-Con), whose axes 6+ are dedicated non-settling sensor
            // data read into their own fields, are left out of the generic array.
            // Signed SDL range → unsigned 0..65535, same convention as the sticks.
            if (HasExtraGenericAxes)
            {
                int extraCount = Math.Min(RawAxisCount, CustomInputState.MaxAxis);
                for (int i = 6; i < extraCount; i++)
                    state.Axis[i] = (ushort)(SDL_GetJoystickAxis(Joystick, i) - short.MinValue);
            }

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
            // Extended positions 11-21, gated by the open-time presence
            // probe: reading an absent position returned constant false at
            // ~10 crossings per pad per tick. ResetForReuse zeroed the
            // buttons, so skipping absent positions is bit-identical.
            // Position 16 is reserved for SDL_GAMEPAD_BUTTON_TOUCHPAD;
            // written by the touchpad section below when HasTouchpad is
            // true, false otherwise.
            var extPresent = _extButtonPresent;
            for (int pos = 11; pos <= 21; pos++)
            {
                if (pos == 16) continue;
                if (extPresent != null && !extPresent[pos]) continue;
                state.Buttons[pos] = SDL_GetGamepadButton(GameController, GamepadButtonForPosition(pos));
            }

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
            if (HasAccelAux)
                SDL_GetGamepadSensorData(GameController, SDL_SENSOR_ACCEL_L, state.AccelAux, 3);

            // --- Capsense (stick-top / grip touch, fork API) ---
            if (_capSenseChannels != null)
            {
                if (state.CapSense == null || state.CapSense.Length != SDL_GAMEPAD_CAPSENSE_COUNT)
                    state.CapSense = new bool[SDL_GAMEPAD_CAPSENSE_COUNT];
                for (int c = 0; c < SDL_GAMEPAD_CAPSENSE_COUNT; c++)
                {
                    if (_capSenseChannels[c])
                        state.CapSense[c] = SDL_GetGamepadCapSense(GameController, c);
                }
            }

            // --- Touchpad (DS4/DualSense/Steam Deck/Steam Controller/Triton) ---
            if (HasTouchpad && _padFingerCounts != null)
            {
                int numPads = _padFingerCounts.Length;
                if (state.Touchpads == null || state.Touchpads.Length != numPads)
                    state.Touchpads = new TouchpadInputState[numPads];
                bool primaryClick = false;
                for (int p = 0; p < numPads; p++)
                {
                    int nf = _padFingerCounts[p];
                    var tp = state.Touchpads[p];
                    if (tp == null || tp.MaxFingers != nf)
                        tp = new TouchpadInputState(nf);
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
                    {
                        primaryClick = SDL_GetGamepadButton(GameController, SDL_GAMEPAD_BUTTON_TOUCHPAD);
                        tp.Clicked = primaryClick;
                    }
                    else if (p == 1 && state.Buttons.Length > 17)
                        tp.Clicked = state.Buttons[17]; // MISC2 — second pad click
                    state.Touchpads[p] = tp;
                }

                // Buttons[16] = primary touchpad click — kept current with
                // the gamepad button so existing readers (Touchpad 0 Click
                // descriptor, virtual-DualSense output) continue to work
                // unchanged.
                // Same value the p==0 branch just read; one SDL crossing
                // and both readers see one coherent click sample.
                state.Buttons[16] = primaryClick;
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

                // SDL has no battery channel for virtual joysticks (no power setter in
                // the virtual API), so devices bridged from a native transport publish
                // through this provider instead, keyed by SDL instance id. Only
                // consulted when SDL itself reports unknown.
                if (_cachedBatteryPercent < 0)
                {
                    var ext = ExternalPowerInfoProvider?.Invoke(SdlInstanceId);
                    if (ext.HasValue)
                    {
                        _cachedBatteryPercent = ext.Value.Percent;
                        _cachedBatteryCharging = ext.Value.Charging;
                    }
                }
            }
            state.BatteryPercent = _cachedBatteryPercent;
            state.BatteryCharging = _cachedBatteryCharging;

            return state;
        }

        /// <summary>Battery source for devices whose power state SDL cannot see
        /// (virtual joysticks bridged from a native transport, e.g. the Bluetooth
        /// DualShock 3). Keyed by SDL instance id; return null when the id isn't
        /// yours. Wired by the app layer.</summary>
        public static Func<uint, (int Percent, bool Charging)?> ExternalPowerInfoProvider { get; set; }

        /// <summary>Real interface path for a device whose SDL path is empty because it is
        /// a virtual joystick bridged from a native transport (the DS3 over BthPS3 / WinUSB).
        /// Keyed by SDL instance id; return null when the id isn't yours. Consulted only
        /// after the identity GUID is fixed, so it never affects device identity. Wired by
        /// the app layer.</summary>
        public static Func<uint, string> ExternalDevicePathProvider { get; set; }

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
            var state = NextPooledState();

            // --- Axes ---
            // Raw joystick mode reads every axis the device actually exposes, not
            // PadForge's presentation count. For a gamepad-opened device NumAxes is
            // clamped to the standardized 6, so a device carrying extra analog axes
            // (a DsHidMini GPJ DS3 node reports 8: axes 4-7 are the face-button
            // pressures) would drop everything past axis 5 in raw mode. Drive the
            // read off RawAxisCount (SDL's true SDL_GetNumJoystickAxes) whenever the
            // device has surfaced extra generic axes. Sensor devices (Wii / Joy-Con)
            // stay on NumAxes so their non-settling camera axes are not pulled into
            // Axis[] (their dedicated readers own those), and raw-opened joysticks
            // already have NumAxes == RawAxisCount so this is a no-op for them.
            // First MaxAxis axes go into Axis[], overflow goes into Sliders[].
            int effectiveAxes = HasExtraGenericAxes ? RawAxisCount : NumAxes;
            int axisCount = Math.Min(effectiveAxes, CustomInputState.MaxAxis + CustomInputState.MaxSliders);
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

        /// <summary>Steam Deck (Valve 0x28DE:0x1205) rumble headroom.
        /// Discussion #179 brackets a 16-bit wrap in the firmware's
        /// 0xEB rumble-emulation gain stage: intensity 83% (54,394)
        /// still rises, 84% (55,049) collapses to ~1%, so a downstream
        /// multiplier in (1.1905, 1.2048] wraps the product past
        /// 65,535. Neither our chain (fully clamped) nor SDL's Deck
        /// driver (verbatim Uint16 pass-through into
        /// MsgSimpleRumbleCmd, SDL_hidapi_steamdeck.c:429-447) nor the
        /// Linux hid-steam driver (verbatim magnitudes, same
        /// left_gain=2/right_gain=0 pair, hid-steam.c
        /// steam_haptic_rumble) scales the value, so the wrap lives
        /// past the wire. The ceiling is 54,394, the reporter's
        /// hardware-PROVEN rising point (the bracket leaves
        /// 54,395-55,049 untested, so anything higher could still
        /// wrap at full slider). Pre-scaling keeps the whole range
        /// monotonic: 100% maps to 54,394, which the firmware's ~1.2x
        /// stage lands near actual full strength. Hypothesis-under-test
        /// against closed firmware, derived from the reporter's
        /// empirical bracket.</summary>
        private const double DeckRumbleHeadroom = 54394.0 / 65535.0;

        private bool IsSteamDeck => VendorId == 0x28DE && ProductId == 0x1205;

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

            if (IsSteamDeck)
            {
                lowFreq = (ushort)(lowFreq * DeckRumbleHeadroom);
                highFreq = (ushort)(highFreq * DeckRumbleHeadroom);
            }

            return SDL_RumbleJoystick(Joystick, lowFreq, highFreq, durationMs);
        }

        /// <summary>
        /// Player-identity idle floor (#191): pushes the 0-based player
        /// index into SDL so drivers with player LEDs light them (Switch
        /// family subcommand 0x30, Wii's 4-LED map, Switch 2's bulk
        /// command). ALLOWLISTED to Nintendo (VID 0x057E): SDL's PS4/PS5
        /// drivers respond to this call by writing their own player
        /// lightbar defaults, which would fight PadForge's sole-writer
        /// Sony dispatcher, and every other Windows backend is a no-op
        /// stub (the Xbox 360 ring is owned by the OS XUSB driver with
        /// no public setter). Known limit: SDL's combined Joy-Con pair
        /// driver has an empty SetDevicePlayerIndex, so a paired set
        /// (synthetic PID 0x2008) is a silent no-op until the fork
        /// carries a forward-to-children patch. Singles, Pro, Switch 2,
        /// and Wii light correctly.
        ///
        /// The DualShock 3 (Sony 0x054C:0x0268) is also allowed, but ONLY on the USB
        /// SXS path: SDL's sixaxis driver lights LED 1-4 from the player index
        /// (SDL_hidapi_ps3.c HIDAPI_DriverPS3_UpdateLEDsSonySixaxis, effects[8 - idx]=1).
        /// The Bluetooth DS3 shares the same VID/PID but is an SDL *virtual* joystick
        /// whose LED is owned by Ds3DirectService.SetPlayerNumber, so it is excluded
        /// here to keep exactly one writer per transport. Not opened to other Sony PIDs:
        /// SDL's PS4/PS5 drivers would fight the sole-writer Sony dispatcher.
        /// </summary>
        public bool SetPlayerIndex(int playerIndex)
        {
            if (Joystick == IntPtr.Zero) return false;
            if (VendorId == 0x057E)
                return SDL_SetJoystickPlayerIndex(Joystick, playerIndex);
            if (VendorId == 0x054C && ProductId == 0x0268 &&
                !SDL_IsJoystickVirtual(SdlInstanceId))
                return SDL_SetJoystickPlayerIndex(Joystick, playerIndex);
            return false;
        }

        /// <summary>
        /// HOME button LED brightness for the Switch family (#226, the
        /// #209 Guide LED's Nintendo lane). Mechanism only: the family
        /// gate lives in SwitchHomeLedSetter, whose worker is the sole
        /// caller. SDL routes this to
        /// HIDAPI_DriverSwitch_SetJoystickLED, which scales max(r,g,b)
        /// onto 0-100 and holds subcommand 0x38's 4-bit intensity steady
        /// (SDL_hidapi_switch.c SetHomeLED), so an equal-RGB write
        /// carries plain brightness. The combined pair driver forwards
        /// to both children and the right Joy-Con acts
        /// (SDL_hidapi_combined.c). Devices without the home LED refuse
        /// inside SDL's own type check and return false. The subcommand
        /// ACK wait in the Switch driver blocks the caller ~30-100 ms
        /// while holding SDL's global joystick lock, so call this from a
        /// dedicated worker, never the poll or UI thread. A stale
        /// closed handle fails safely (SDL_joystick.c
        /// CHECK_JOYSTICK_MAGIC is an SDL_ObjectValid lookup, not a
        /// deref).
        /// </summary>
        public bool SetHomeLedBrightness(int percent)
        {
            if (Joystick == IntPtr.Zero) return false;
            byte v = HomeLedPercentToByte(percent);
            return SDL_SetJoystickLED(Joystick, v, v, v);
        }

        /// <summary>0-100 percent to the equal-RGB LED byte. Ceiling is
        /// deliberate: SDL recovers percent as (int)((v / 255.0f) *
        /// 100.0f) (SDL_hidapi_switch.c
        /// HIDAPI_DriverSwitch_SetJoystickLED), and v = ceil(p * 2.55)
        /// makes that round-trip exact for every p in 0..100 (v/2.55 sits
        /// in [p, p + 0.4), so the truncation lands on p), where plain
        /// rounding slips to p-1 on some values (99 to 98).</summary>
        internal static byte HomeLedPercentToByte(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            return (byte)Math.Ceiling(percent * 255 / 100.0);
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
            else if (!string.IsNullOrEmpty(sdlGuid) && !sdlGuid.All(c => c == '0'))
            {
                // No device path and no serial (an SDL virtual joystick like the DS3
                // bridge). SDL's joystick GUID is stable across reconnects (derived from
                // bus/vendor/product/name), whereas the instance id is session-specific
                // and shifts on every reattach, re-minting the identity and silently
                // dropping the slot mapping (the input stops passing through even though
                // the device previews live). Use the stable GUID.
                identifier = $"sdlguid:{sdlGuid}";
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
            // Extra generic axes (issue #193): raw joystick axes beyond the six
            // standardized gamepad axes, surfaced only for HasExtraGenericAxes
            // devices. Reserve their slots so the emit loop below can't overrun.
            int extraAxisCount = HasExtraGenericAxes
                ? Math.Max(0, Math.Min(RawAxisCount, CustomInputState.MaxAxis) - NumAxes)
                : 0;
            int totalObjects = NumAxes + extraAxisCount + NumHats + btnCount;
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
            // For SDL3-recognized gamepads, skip standard axis positions the
            // device doesn't physically have (asked via SDL_GamepadHasAxis),
            // mirroring the button gate below. DeviceObjects is the capability
            // list CreateDefaultPadSetting's HasAxis() trusts, so a stickless
            // gamepad must not advertise phantom Left/Right Stick axes (which
            // read as a dead center). Raw joystick devices (isGamepad=false)
            // keep the flat enumeration unchanged.
            for (int i = 0; i < NumAxes; i++)
            {
                if (isGamepad && i < standardAxisGuids.Length)
                {
                    int sdlAxis = GamepadAxisForPosition(i);
                    if (sdlAxis < 0 || !SDL_GamepadHasAxis(GameController, sdlAxis))
                        continue;
                }

                var item = new DeviceObjectItem();
                item.InputIndex = i;

                if (i < standardAxisGuids.Length)
                {
                    item.ObjectTypeGuid = standardAxisGuids[i];
                    item.Name = isGamepad ? GetGamepadAxisName(i) : GetStandardAxisName(i);
                }
                else if (i < CustomInputState.MaxAxis)
                {
                    // Raw-opened joystick axes 6..23 land in Axis[] (GetJoystickState
                    // stores axis i in Axis[i] for i<24), so surface them as the Axis
                    // family ("Axis N"). The old Slider label routed them to the
                    // "Slider N" descriptor, which reads Sliders[] (written in exactly
                    // one place, SdlDeviceWrapper.cs ~:922, only from raw axes 24+), so
                    // every axis past the standard six was DEAD on any device with
                    // fewer than 25 axes: flight sticks, wheels, HOTAS throttles. This
                    // is the raw-open counterpart to the gamepad extra-axis emit
                    // (issue #193). A non-Slider axis GUID keeps IsAxis true /
                    // IsSlider false. Only reached for isGamepad==false (a gamepad's
                    // NumAxes is 6, so its 6+ axes come from the #193 extra loop).
                    item.ObjectTypeGuid = ObjectGuid.ZAxis;
                    item.Name = $"Axis {i}";
                }
                else
                {
                    // True overflow: axes 24+ are the only ones GetJoystickState
                    // stores in Sliders[], so they keep the Slider family. Key
                    // InputIndex and the name on the Sliders[] STORAGE index
                    // (i - MaxAxis, 0..7), not the raw axis number: the
                    // "Slider N" descriptor reads state.Sliders[N], so a
                    // raw-axis-numbered descriptor ("Slider 24") fails the
                    // idx < MaxSliders guard and reads dead while the live
                    // value sits in Sliders[0..7]. The type gate (IsSlider vs
                    // IsAxis) keeps these from colliding with the axis
                    // objects that share low InputIndex values.
                    item.InputIndex = i - CustomInputState.MaxAxis;
                    item.ObjectTypeGuid = ObjectGuid.Slider;
                    item.Name = $"Slider {i - CustomInputState.MaxAxis}";
                }

                item.ObjectType = DeviceObjectTypeFlags.AbsoluteAxis;
                item.Offset = i * 4; // Simulated offset for identification.
                items[index++] = item;
            }

            // --- Extra generic axes (issue #193) ---
            // Raw joystick axes beyond the standardized six, emitted as the Axis
            // family (a non-Slider axis GUID: IsAxis stays true, IsSlider false) so
            // they round-trip through state.Axis[] via the "Axis N" descriptor
            // instead of the dead Slider path. A separate loop keeps the standard
            // axis loop and the hat/button Offset bases above unchanged; runs only
            // for HasExtraGenericAxes devices, where NumAxes is the gamepad 6.
            if (HasExtraGenericAxes)
            {
                int end = Math.Min(RawAxisCount, CustomInputState.MaxAxis);
                for (int i = NumAxes; i < end; i++)
                {
                    var item = new DeviceObjectItem();
                    item.InputIndex = i;
                    item.ObjectTypeGuid = ObjectGuid.ZAxis; // any non-Slider axis GUID; only IsSlider is derived from it
                    item.Name = $"Axis {i}";
                    item.ObjectType = DeviceObjectTypeFlags.AbsoluteAxis;
                    item.Offset = i * 4;
                    items[index++] = item;
                }
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

            // Trim if we skipped any axis or button. The caller iterates Length
            // and can't have nulls. index is the exact count actually written.
            if (index < items.Length)
            {
                var trimmed = new DeviceObjectItem[index];
                Array.Copy(items, trimmed, index);
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
        /// <summary>Open-time presence of extended positions 11-21 (from
        /// the SDL_GamepadHasButton probe); null before the probe runs.</summary>
        private bool[] _extButtonPresent;

        private int[] ComputeSupportedButtonIndices()
        {
            int max = Math.Min(NumButtons, CustomInputState.MaxButtons);
            var list = new System.Collections.Generic.List<int>(max);

            if (GameController != IntPtr.Zero)
            {
                for (int i = 0; i < 11 && i < max; i++)
                    list.Add(i);

                _extButtonPresent = new bool[22];
                for (int i = 11; i <= 21 && i < max; i++)
                {
                    int sdlButton = GamepadButtonForPosition(i);
                    if (sdlButton >= 0 && SDL_GamepadHasButton(GameController, sdlButton))
                    {
                        list.Add(i);
                        _extButtonPresent[i] = true;
                    }
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

        /// <summary>
        /// Maps a DeviceObjects axis position (LX/LY/LT/RX/RY/RT order, matching
        /// <see cref="GetGamepadAxisName"/> and the CustomInputState Axis[] layout)
        /// to the SDL gamepad axis enum, so axis-object enumeration can gate on
        /// <see cref="SDL_GamepadHasAxis"/> the way the button path gates on
        /// SDL_GamepadHasButton. Returns -1 for non-standard positions.
        /// </summary>
        private static int GamepadAxisForPosition(int position)
        {
            return position switch
            {
                0 => SDL_GAMEPAD_AXIS_LEFTX,
                1 => SDL_GAMEPAD_AXIS_LEFTY,
                2 => SDL_GAMEPAD_AXIS_LEFT_TRIGGER,
                3 => SDL_GAMEPAD_AXIS_RIGHTX,
                4 => SDL_GAMEPAD_AXIS_RIGHTY,
                5 => SDL_GAMEPAD_AXIS_RIGHT_TRIGGER,
                _ => -1
            };
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
