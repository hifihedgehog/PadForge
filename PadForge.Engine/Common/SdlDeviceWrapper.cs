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
            NumAxes = SDL_GetNumJoystickAxes(Joystick);
            NumButtons = SDL_GetNumJoystickButtons(Joystick);
            NumHats = SDL_GetNumJoystickHats(Joystick);
            Name = SDL_GetJoystickName(Joystick);
            VendorId = SDL_GetJoystickVendor(Joystick);
            ProductId = SDL_GetJoystickProduct(Joystick);
            ProductVersion = SDL_GetJoystickProductVersion(Joystick);
            JoystickType = SDL_GetJoystickType(Joystick);
            DevicePath = SDL_GetJoystickPath(Joystick);

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

            // Build stable GUIDs for settings matching.
            ProductGuid = BuildProductGuid(VendorId, ProductId);
            InstanceGuid = BuildInstanceGuid(DevicePath, VendorId, ProductId, instanceId);

            return true;
        }

        /// <summary>
        /// Internal close that releases SDL handles without setting _disposed.
        /// </summary>
        private void CloseInternal()
        {
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
}
