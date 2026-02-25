using System;
using System.Runtime.InteropServices;

namespace SDL3
{
    /// <summary>
    /// Minimal SDL3 P/Invoke declarations for joystick and gamepad support.
    /// Only the functions actually used by PadForge are declared here.
    ///
    /// Migrated from SDL2: key API changes include device-index enumeration
    /// replaced by instance-ID enumeration, GameController renamed to Gamepad,
    /// SDL_bool replaced with C bool, and consistent Verb-Noun function naming.
    /// </summary>
    public static class SDL
    {
        private const string lib = "SDL3";

        // ─────────────────────────────────────────────
        //  Init flags
        // ─────────────────────────────────────────────

        public const uint SDL_INIT_VIDEO = 0x00000020;     // Required for keyboard/mouse
        public const uint SDL_INIT_JOYSTICK = 0x00000200;
        public const uint SDL_INIT_HAPTIC = 0x00001000;
        public const uint SDL_INIT_GAMEPAD = 0x00002000; // was SDL_INIT_GAMECONTROLLER

        // ─────────────────────────────────────────────
        //  Hat constants (unchanged from SDL2)
        // ─────────────────────────────────────────────

        public const byte SDL_HAT_CENTERED = 0x00;
        public const byte SDL_HAT_UP = 0x01;
        public const byte SDL_HAT_RIGHT = 0x02;
        public const byte SDL_HAT_DOWN = 0x04;
        public const byte SDL_HAT_LEFT = 0x08;
        public const byte SDL_HAT_RIGHTUP = SDL_HAT_RIGHT | SDL_HAT_UP;
        public const byte SDL_HAT_RIGHTDOWN = SDL_HAT_RIGHT | SDL_HAT_DOWN;
        public const byte SDL_HAT_LEFTUP = SDL_HAT_LEFT | SDL_HAT_UP;
        public const byte SDL_HAT_LEFTDOWN = SDL_HAT_LEFT | SDL_HAT_DOWN;

        // ─────────────────────────────────────────────
        //  Hint strings
        // ─────────────────────────────────────────────

        public const string SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS = "SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS";
        public const string SDL_HINT_JOYSTICK_RAWINPUT = "SDL_JOYSTICK_RAWINPUT";
        public const string SDL_HINT_JOYSTICK_XINPUT = "SDL_JOYSTICK_XINPUT"; // was SDL_HINT_XINPUT_ENABLED
        public const string SDL_HINT_JOYSTICK_HIDAPI_SWITCH2 = "SDL_JOYSTICK_HIDAPI_SWITCH2";

        // ─────────────────────────────────────────────
        //  Property constants
        // ─────────────────────────────────────────────

        public const string SDL_PROP_JOYSTICK_CAP_RUMBLE_BOOLEAN = "SDL.joystick.cap.rumble";

        // ─────────────────────────────────────────────
        //  Enums
        // ─────────────────────────────────────────────

        public enum SDL_JoystickType : int
        {
            SDL_JOYSTICK_TYPE_UNKNOWN = 0,
            SDL_JOYSTICK_TYPE_GAMEPAD = 1, // was SDL_JOYSTICK_TYPE_GAMECONTROLLER
            SDL_JOYSTICK_TYPE_WHEEL = 2,
            SDL_JOYSTICK_TYPE_ARCADE_STICK = 3,
            SDL_JOYSTICK_TYPE_FLIGHT_STICK = 4,
            SDL_JOYSTICK_TYPE_DANCE_PAD = 5,
            SDL_JOYSTICK_TYPE_GUITAR = 6,
            SDL_JOYSTICK_TYPE_DRUM_KIT = 7,
            SDL_JOYSTICK_TYPE_ARCADE_PAD = 8,
            SDL_JOYSTICK_TYPE_THROTTLE = 9,
            SDL_JOYSTICK_TYPE_COUNT = 10
        }

        public enum SDL_PowerState : int
        {
            SDL_POWERSTATE_ERROR = -1,
            SDL_POWERSTATE_UNKNOWN = 0,
            SDL_POWERSTATE_ON_BATTERY = 1,
            SDL_POWERSTATE_NO_BATTERY = 2,
            SDL_POWERSTATE_CHARGING = 3,
            SDL_POWERSTATE_CHARGED = 4
        }

        // ─────────────────────────────────────────────
        //  Structs
        // ─────────────────────────────────────────────

        /// <summary>
        /// 16-byte GUID structure used by SDL for device identification.
        /// Renamed from SDL_JoystickGUID in SDL2 to SDL_GUID in SDL3.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct SDL_GUID
        {
            public byte data0;
            public byte data1;
            public byte data2;
            public byte data3;
            public byte data4;
            public byte data5;
            public byte data6;
            public byte data7;
            public byte data8;
            public byte data9;
            public byte data10;
            public byte data11;
            public byte data12;
            public byte data13;
            public byte data14;
            public byte data15;

            /// <summary>
            /// Converts the SDL GUID to a .NET <see cref="System.Guid"/>.
            /// </summary>
            public Guid ToGuid()
            {
                return new Guid(
                    (int)(data0 | (data1 << 8) | (data2 << 16) | (data3 << 24)),
                    (short)(data4 | (data5 << 8)),
                    (short)(data6 | (data7 << 8)),
                    data8, data9, data10, data11,
                    data12, data13, data14, data15);
            }

            /// <summary>
            /// Converts the raw 16 bytes to a byte array.
            /// </summary>
            public byte[] ToByteArray()
            {
                return new byte[]
                {
                    data0, data1, data2, data3,
                    data4, data5, data6, data7,
                    data8, data9, data10, data11,
                    data12, data13, data14, data15
                };
            }
        }

        // ─────────────────────────────────────────────
        //  Core lifecycle
        // ─────────────────────────────────────────────

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_Init")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_Init(uint flags);

        /// <summary>
        /// Initializes SDL subsystems. Returns true on success.
        /// SDL3 change: returns bool instead of int.
        /// </summary>
        public static bool SDL_Init(uint flags) => _SDL_Init(flags);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_Quit();

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetError")]
        private static extern IntPtr _SDL_GetError();

        public static string SDL_GetError()
        {
            return Marshal.PtrToStringUTF8(_SDL_GetError()) ?? string.Empty;
        }

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetHint")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_SetHint(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        public static bool SDL_SetHint(string name, string value) => _SDL_SetHint(name, value);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_free(IntPtr mem);

        // ─────────────────────────────────────────────
        //  Joystick enumeration (by instance ID)
        //
        //  SDL3 replaces SDL_NumJoysticks() + device-index-based
        //  queries with SDL_GetJoysticks() returning an array of
        //  SDL_JoystickID (uint) instance IDs.
        // ─────────────────────────────────────────────

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetJoysticks")]
        private static extern IntPtr _SDL_GetJoysticks(out int count);

        /// <summary>
        /// Returns an array of instance IDs for all connected joysticks.
        /// The caller does NOT need to free the array — this wrapper handles it.
        /// </summary>
        public static uint[] SDL_GetJoysticks()
        {
            IntPtr ptr = _SDL_GetJoysticks(out int count);
            if (ptr == IntPtr.Zero || count <= 0)
                return Array.Empty<uint>();

            try
            {
                var ids = new uint[count];
                for (int i = 0; i < count; i++)
                    ids[i] = unchecked((uint)Marshal.ReadInt32(ptr, i * 4));
                return ids;
            }
            finally
            {
                SDL_free(ptr);
            }
        }

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern SDL_GUID SDL_GetJoystickGUIDForID(uint instance_id);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ushort SDL_GetJoystickVendorForID(uint instance_id);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ushort SDL_GetJoystickProductForID(uint instance_id);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ushort SDL_GetJoystickProductVersionForID(uint instance_id);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern SDL_JoystickType SDL_GetJoystickTypeForID(uint instance_id);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetJoystickNameForID")]
        private static extern IntPtr _SDL_GetJoystickNameForID(uint instance_id);

        public static string SDL_GetJoystickNameForID(uint instance_id)
        {
            return Marshal.PtrToStringUTF8(_SDL_GetJoystickNameForID(instance_id)) ?? string.Empty;
        }

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetJoystickPathForID")]
        private static extern IntPtr _SDL_GetJoystickPathForID(uint instance_id);

        public static string SDL_GetJoystickPathForID(uint instance_id)
        {
            IntPtr ptr = _SDL_GetJoystickPathForID(instance_id);
            return ptr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(ptr) ?? string.Empty) : string.Empty;
        }

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_IsGamepad")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_IsGamepad(uint instance_id);

        /// <summary>
        /// Returns true if the joystick is recognized as a gamepad.
        /// SDL3 change: renamed from SDL_IsGameController, takes instance ID.
        /// </summary>
        public static bool SDL_IsGamepad(uint instance_id) => _SDL_IsGamepad(instance_id);

        // ─────────────────────────────────────────────
        //  Joystick instance (opened device)
        // ─────────────────────────────────────────────

        /// <summary>Opens a joystick by instance ID. SDL3 change: takes instance ID instead of device index.</summary>
        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_OpenJoystick(uint instance_id);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_CloseJoystick(IntPtr joystick);

        /// <summary>
        /// Returns the instance ID of an opened joystick.
        /// SDL3 change: returns uint (0 = error), was int (negative = error).
        /// </summary>
        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetJoystickID(IntPtr joystick);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_JoystickConnected")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_JoystickConnected(IntPtr joystick);

        /// <summary>Returns true if the joystick is still connected.</summary>
        public static bool SDL_JoystickConnected(IntPtr joystick) => _SDL_JoystickConnected(joystick);

        // ─────────────────────────────────────────────
        //  Gamepad (was GameController)
        // ─────────────────────────────────────────────

        /// <summary>Opens a gamepad by instance ID.</summary>
        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_OpenGamepad(uint instance_id);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_CloseGamepad(IntPtr gamepad);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_GetGamepadJoystick(IntPtr gamepad);

        // ─────────────────────────────────────────────
        //  Gamepad state polling (standardized layout)
        //
        //  SDL_GetGamepadAxis / SDL_GetGamepadButton read through SDL's
        //  built-in gamecontrollerdb mapping layer. Any recognized device
        //  (DualSense, DualShock, Switch Pro, etc.) is remapped to the
        //  standardized Xbox-like layout automatically.
        //
        //  Axis enum (SDL_GamepadAxis):
        //    LEFTX=0, LEFTY=1, RIGHTX=2, RIGHTY=3,
        //    LEFT_TRIGGER=4, RIGHT_TRIGGER=5
        //
        //  Button enum (SDL_GamepadButton):
        //    SOUTH/A=0, EAST/B=1, WEST/X=2, NORTH/Y=3,
        //    BACK=4, GUIDE=5, START=6,
        //    LEFT_STICK=7, RIGHT_STICK=8,
        //    LEFT_SHOULDER=9, RIGHT_SHOULDER=10,
        //    DPAD_UP=11, DPAD_DOWN=12, DPAD_LEFT=13, DPAD_RIGHT=14,
        //    MISC1=15, RIGHT_PADDLE1=16, LEFT_PADDLE1=17,
        //    RIGHT_PADDLE2=18, LEFT_PADDLE2=19, TOUCHPAD=20
        // ─────────────────────────────────────────────

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetGamepadButton")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_GetGamepadButton(IntPtr gamepad, int button);

        public static bool SDL_GetGamepadButton(IntPtr gamepad, int button) =>
            _SDL_GetGamepadButton(gamepad, button);

        // Gamepad axis indices (SDL_GamepadAxis enum).
        public const int SDL_GAMEPAD_AXIS_LEFTX = 0;
        public const int SDL_GAMEPAD_AXIS_LEFTY = 1;
        public const int SDL_GAMEPAD_AXIS_RIGHTX = 2;
        public const int SDL_GAMEPAD_AXIS_RIGHTY = 3;
        public const int SDL_GAMEPAD_AXIS_LEFT_TRIGGER = 4;
        public const int SDL_GAMEPAD_AXIS_RIGHT_TRIGGER = 5;
        public const int SDL_GAMEPAD_AXIS_COUNT = 6;

        // Gamepad button indices (SDL_GamepadButton enum).
        public const int SDL_GAMEPAD_BUTTON_SOUTH = 0;   // A
        public const int SDL_GAMEPAD_BUTTON_EAST = 1;    // B
        public const int SDL_GAMEPAD_BUTTON_WEST = 2;    // X
        public const int SDL_GAMEPAD_BUTTON_NORTH = 3;   // Y
        public const int SDL_GAMEPAD_BUTTON_BACK = 4;
        public const int SDL_GAMEPAD_BUTTON_GUIDE = 5;
        public const int SDL_GAMEPAD_BUTTON_START = 6;
        public const int SDL_GAMEPAD_BUTTON_LEFT_STICK = 7;
        public const int SDL_GAMEPAD_BUTTON_RIGHT_STICK = 8;
        public const int SDL_GAMEPAD_BUTTON_LEFT_SHOULDER = 9;
        public const int SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER = 10;
        public const int SDL_GAMEPAD_BUTTON_DPAD_UP = 11;
        public const int SDL_GAMEPAD_BUTTON_DPAD_DOWN = 12;
        public const int SDL_GAMEPAD_BUTTON_DPAD_LEFT = 13;
        public const int SDL_GAMEPAD_BUTTON_DPAD_RIGHT = 14;
        public const int SDL_GAMEPAD_BUTTON_COUNT = 21;

        // ─────────────────────────────────────────────
        //  Joystick state polling
        // ─────────────────────────────────────────────

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_UpdateJoysticks();

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern short SDL_GetJoystickAxis(IntPtr joystick, int axis);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetJoystickButton")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_GetJoystickButton(IntPtr joystick, int button);

        /// <summary>
        /// Returns true if the button is pressed.
        /// SDL3 change: returns bool instead of byte.
        /// </summary>
        public static bool SDL_GetJoystickButton(IntPtr joystick, int button) =>
            _SDL_GetJoystickButton(joystick, button);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern byte SDL_GetJoystickHat(IntPtr joystick, int hat);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetNumJoystickAxes(IntPtr joystick);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetNumJoystickButtons(IntPtr joystick);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetNumJoystickHats(IntPtr joystick);

        // ─────────────────────────────────────────────
        //  Joystick properties (from opened instance)
        // ─────────────────────────────────────────────

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetJoystickName")]
        private static extern IntPtr _SDL_GetJoystickName(IntPtr joystick);

        public static string SDL_GetJoystickName(IntPtr joystick)
        {
            return Marshal.PtrToStringUTF8(_SDL_GetJoystickName(joystick)) ?? string.Empty;
        }

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ushort SDL_GetJoystickVendor(IntPtr joystick);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ushort SDL_GetJoystickProduct(IntPtr joystick);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern ushort SDL_GetJoystickProductVersion(IntPtr joystick);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern SDL_JoystickType SDL_GetJoystickType(IntPtr joystick);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetJoystickPath")]
        private static extern IntPtr _SDL_GetJoystickPath(IntPtr joystick);

        public static string SDL_GetJoystickPath(IntPtr joystick)
        {
            IntPtr ptr = _SDL_GetJoystickPath(joystick);
            return ptr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(ptr) ?? string.Empty) : string.Empty;
        }

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern SDL_GUID SDL_GetJoystickGUID(IntPtr joystick);

        // ─────────────────────────────────────────────
        //  Properties system (for capability queries)
        // ─────────────────────────────────────────────

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetJoystickProperties(IntPtr joystick);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetBooleanProperty")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_GetBooleanProperty(
            uint props,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.U1)] bool default_value);

        public static bool SDL_GetBooleanProperty(uint props, string name, bool defaultValue) =>
            _SDL_GetBooleanProperty(props, name, defaultValue);

        // ─────────────────────────────────────────────
        //  Power info (replaces SDL_JoystickCurrentPowerLevel)
        // ─────────────────────────────────────────────

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern SDL_PowerState SDL_GetJoystickPowerInfo(IntPtr joystick, out int percent);

        // ─────────────────────────────────────────────
        //  Gamepad sensors (gyro / accelerometer)
        // ─────────────────────────────────────────────

        // SDL_SensorType enum values
        public const int SDL_SENSOR_ACCEL = 1;
        public const int SDL_SENSOR_GYRO = 2;
        public const int SDL_SENSOR_ACCEL_L = 3;
        public const int SDL_SENSOR_GYRO_L = 4;
        public const int SDL_SENSOR_ACCEL_R = 5;
        public const int SDL_SENSOR_GYRO_R = 6;

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GamepadHasSensor")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_GamepadHasSensor(IntPtr gamepad, int type);

        /// <summary>Returns true if the gamepad has the specified sensor type.</summary>
        public static bool SDL_GamepadHasSensor(IntPtr gamepad, int type) =>
            _SDL_GamepadHasSensor(gamepad, type);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetGamepadSensorEnabled")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_SetGamepadSensorEnabled(IntPtr gamepad, int type,
            [MarshalAs(UnmanagedType.U1)] bool enabled);

        /// <summary>Enables or disables data reporting for the specified sensor.</summary>
        public static bool SDL_SetGamepadSensorEnabled(IntPtr gamepad, int type, bool enabled) =>
            _SDL_SetGamepadSensorEnabled(gamepad, type, enabled);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetGamepadSensorData")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_GetGamepadSensorData(IntPtr gamepad, int type,
            [Out] float[] data, int num_values);

        /// <summary>
        /// Reads sensor data. For gyro: 3 floats (X, Y, Z) in radians/second.
        /// For accel: 3 floats (X, Y, Z) in m/s².
        /// </summary>
        public static bool SDL_GetGamepadSensorData(IntPtr gamepad, int type, float[] data, int num_values) =>
            _SDL_GetGamepadSensorData(gamepad, type, data, num_values);

        // ─────────────────────────────────────────────
        //  Rumble / haptics
        // ─────────────────────────────────────────────

        /// <summary>
        /// Rumble a joystick for a specified duration.
        /// SDL3 change: renamed from SDL_JoystickRumble, returns bool.
        /// </summary>
        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_RumbleJoystick")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_RumbleJoystick(
            IntPtr joystick,
            ushort low_frequency_rumble,
            ushort high_frequency_rumble,
            uint duration_ms);

        public static bool SDL_RumbleJoystick(IntPtr joystick,
            ushort low_frequency_rumble, ushort high_frequency_rumble, uint duration_ms) =>
            _SDL_RumbleJoystick(joystick, low_frequency_rumble, high_frequency_rumble, duration_ms);

        // ─────────────────────────────────────────────
        //  Haptic (force feedback) — constants
        // ─────────────────────────────────────────────

        public const uint SDL_HAPTIC_CONSTANT     = 1u << 0;
        public const uint SDL_HAPTIC_SINE         = 1u << 1;
        public const uint SDL_HAPTIC_SQUARE       = 1u << 2;
        public const uint SDL_HAPTIC_TRIANGLE     = 1u << 3;
        public const uint SDL_HAPTIC_SAWTOOTHUP   = 1u << 4;
        public const uint SDL_HAPTIC_SAWTOOTHDOWN = 1u << 5;
        public const uint SDL_HAPTIC_RAMP         = 1u << 6;
        public const uint SDL_HAPTIC_SPRING       = 1u << 7;
        public const uint SDL_HAPTIC_DAMPER        = 1u << 8;
        public const uint SDL_HAPTIC_INERTIA      = 1u << 9;
        public const uint SDL_HAPTIC_FRICTION     = 1u << 10;
        public const uint SDL_HAPTIC_LEFTRIGHT    = 1u << 11;
        public const uint SDL_HAPTIC_CUSTOM       = 1u << 15;
        public const uint SDL_HAPTIC_GAIN         = 1u << 16;
        public const uint SDL_HAPTIC_AUTOCENTER   = 1u << 17;

        public const uint SDL_HAPTIC_INFINITY = 0xFFFFFFFFu;

        // Direction types (SDL_HapticDirectionType = Uint8)
        public const byte SDL_HAPTIC_POLAR         = 0;
        public const byte SDL_HAPTIC_CARTESIAN     = 1;
        public const byte SDL_HAPTIC_SPHERICAL     = 2;
        public const byte SDL_HAPTIC_STEERING_AXIS = 3;

        // ─────────────────────────────────────────────
        //  Haptic structs
        // ─────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        public struct SDL_HapticDirection
        {
            public byte type;       // SDL_HapticDirectionType (Uint8)
            private byte _pad1;
            private byte _pad2;
            private byte _pad3;
            public int dir0;        // dir[3] as individual fields (Sint32)
            public int dir1;
            public int dir2;
        } // 16 bytes

        [StructLayout(LayoutKind.Sequential)]
        public struct SDL_HapticLeftRight
        {
            public ushort type;              // SDL_HAPTIC_LEFTRIGHT
            private ushort _pad;
            public uint length;              // Duration in ms
            public ushort large_magnitude;   // 0–65535
            public ushort small_magnitude;   // 0–65535
        } // 12 bytes

        [StructLayout(LayoutKind.Sequential)]
        public struct SDL_HapticConstant
        {
            public ushort type;              // SDL_HAPTIC_CONSTANT
            private ushort _pad;
            public SDL_HapticDirection direction;
            public uint length;              // Duration in ms
            public ushort delay;
            public ushort button;
            public ushort interval;
            public short level;              // -32768 to 32767
            public ushort attack_length;
            public ushort attack_level;
            public ushort fade_length;
            public ushort fade_level;
        } // 40 bytes

        [StructLayout(LayoutKind.Sequential)]
        public struct SDL_HapticPeriodic
        {
            public ushort type;              // SDL_HAPTIC_SINE, etc.
            private ushort _pad;
            public SDL_HapticDirection direction;
            public uint length;              // Duration in ms
            public ushort delay;
            public ushort button;
            public ushort interval;
            public ushort period;            // Period in ms
            public short magnitude;          // Peak value -32767 to 32767
            public short offset;             // Mean value
            public ushort phase;             // Phase shift 0–35999 (hundredths of degrees)
            public ushort attack_length;
            public ushort attack_level;
            public ushort fade_length;
            public ushort fade_level;
        } // 44 bytes

        /// <summary>
        /// SDL_HapticEffect union. Uses explicit layout to overlay all effect types.
        /// Size = largest member (SDL_HapticCondition at 68 bytes on x64).
        /// We use 72 bytes for safety margin across compilers/platforms.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 72)]
        public struct SDL_HapticEffect
        {
            [FieldOffset(0)] public ushort type;
            [FieldOffset(0)] public SDL_HapticLeftRight leftright;
            [FieldOffset(0)] public SDL_HapticConstant constant;
            [FieldOffset(0)] public SDL_HapticPeriodic periodic;
        }

        // ─────────────────────────────────────────────
        //  Haptic functions
        // ─────────────────────────────────────────────

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_OpenHapticFromJoystick(IntPtr joystick);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_CloseHaptic(IntPtr haptic);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetHapticFeatures(IntPtr haptic);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_CreateHapticEffect(IntPtr haptic, ref SDL_HapticEffect effect);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_UpdateHapticEffect")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_UpdateHapticEffect(IntPtr haptic, int effect, ref SDL_HapticEffect data);

        public static bool SDL_UpdateHapticEffect(IntPtr haptic, int effect, ref SDL_HapticEffect data) =>
            _SDL_UpdateHapticEffect(haptic, effect, ref data);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_RunHapticEffect")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_RunHapticEffect(IntPtr haptic, int effect, uint iterations);

        public static bool SDL_RunHapticEffect(IntPtr haptic, int effect, uint iterations) =>
            _SDL_RunHapticEffect(haptic, effect, iterations);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_StopHapticEffect")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_StopHapticEffect(IntPtr haptic, int effect);

        public static bool SDL_StopHapticEffect(IntPtr haptic, int effect) =>
            _SDL_StopHapticEffect(haptic, effect);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_DestroyHapticEffect(IntPtr haptic, int effect);

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_SetHapticGain")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool _SDL_SetHapticGain(IntPtr haptic, int gain);

        public static bool SDL_SetHapticGain(IntPtr haptic, int gain) =>
            _SDL_SetHapticGain(haptic, gain);

        // ─────────────────────────────────────────────
        //  Keyboard enumeration and state
        // ─────────────────────────────────────────────

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetKeyboards")]
        private static extern IntPtr _SDL_GetKeyboards(out int count);

        /// <summary>
        /// Returns an array of instance IDs for all connected keyboards.
        /// </summary>
        public static uint[] SDL_GetKeyboards()
        {
            IntPtr ptr = _SDL_GetKeyboards(out int count);
            if (ptr == IntPtr.Zero || count <= 0)
                return Array.Empty<uint>();

            try
            {
                var ids = new uint[count];
                for (int i = 0; i < count; i++)
                    ids[i] = unchecked((uint)Marshal.ReadInt32(ptr, i * 4));
                return ids;
            }
            finally
            {
                SDL_free(ptr);
            }
        }

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetKeyboardNameForID")]
        private static extern IntPtr _SDL_GetKeyboardNameForID(uint instance_id);

        public static string SDL_GetKeyboardNameForID(uint instance_id)
        {
            return Marshal.PtrToStringUTF8(_SDL_GetKeyboardNameForID(instance_id)) ?? "Keyboard";
        }

        /// <summary>
        /// Returns a pointer to an array of booleans (one per SDL_Scancode) representing key states.
        /// The pointer is owned by SDL and valid until the next SDL_PumpEvents/SDL_PollEvent.
        /// </summary>
        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_GetKeyboardState(out int numkeys);

        // ─────────────────────────────────────────────
        //  Mouse enumeration and state
        // ─────────────────────────────────────────────

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetMice")]
        private static extern IntPtr _SDL_GetMice(out int count);

        /// <summary>
        /// Returns an array of instance IDs for all connected mice.
        /// </summary>
        public static uint[] SDL_GetMice()
        {
            IntPtr ptr = _SDL_GetMice(out int count);
            if (ptr == IntPtr.Zero || count <= 0)
                return Array.Empty<uint>();

            try
            {
                var ids = new uint[count];
                for (int i = 0; i < count; i++)
                    ids[i] = unchecked((uint)Marshal.ReadInt32(ptr, i * 4));
                return ids;
            }
            finally
            {
                SDL_free(ptr);
            }
        }

        [DllImport(lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_GetMouseNameForID")]
        private static extern IntPtr _SDL_GetMouseNameForID(uint instance_id);

        public static string SDL_GetMouseNameForID(uint instance_id)
        {
            return Marshal.PtrToStringUTF8(_SDL_GetMouseNameForID(instance_id)) ?? "Mouse";
        }

        /// <summary>
        /// Returns the current mouse button state and absolute position.
        /// Button mask: bit 0 = left, bit 1 = middle, bit 2 = right, bit 3 = X1, bit 4 = X2.
        /// </summary>
        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetMouseState(out float x, out float y);

        /// <summary>
        /// Returns mouse relative motion since the last call.
        /// </summary>
        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetRelativeMouseState(out float x, out float y);

        // SDL mouse button masks
        public const uint SDL_BUTTON_LMASK = 1u << 0;
        public const uint SDL_BUTTON_MMASK = 1u << 1;
        public const uint SDL_BUTTON_RMASK = 1u << 2;
        public const uint SDL_BUTTON_X1MASK = 1u << 3;
        public const uint SDL_BUTTON_X2MASK = 1u << 4;

        // ─────────────────────────────────────────────
        //  SDL Scancode constants (common keys)
        //  Full enum: SDL_Scancode in SDL3 headers.
        //  We define only the subset needed for button naming.
        // ─────────────────────────────────────────────

        public static readonly string[] ScancodeName = BuildScancodeNames();

        private static string[] BuildScancodeNames()
        {
            var names = new string[512];
            for (int i = 0; i < names.Length; i++)
                names[i] = $"Key {i}";

            // Letters
            for (int i = 0; i < 26; i++)
                names[4 + i] = ((char)('A' + i)).ToString();

            // Numbers
            for (int i = 0; i < 10; i++)
                names[30 + i] = i.ToString();

            // Common keys
            names[40] = "Return";
            names[41] = "Escape";
            names[42] = "Backspace";
            names[43] = "Tab";
            names[44] = "Space";
            names[45] = "Minus";
            names[46] = "Equals";
            names[47] = "LeftBracket";
            names[48] = "RightBracket";
            names[49] = "Backslash";
            names[51] = "Semicolon";
            names[52] = "Apostrophe";
            names[53] = "Grave";
            names[54] = "Comma";
            names[55] = "Period";
            names[56] = "Slash";
            names[57] = "CapsLock";

            // F keys
            for (int i = 0; i < 12; i++)
                names[58 + i] = $"F{i + 1}";

            names[70] = "PrintScreen";
            names[71] = "ScrollLock";
            names[72] = "Pause";
            names[73] = "Insert";
            names[74] = "Home";
            names[75] = "PageUp";
            names[76] = "Delete";
            names[77] = "End";
            names[78] = "PageDown";
            names[79] = "Right";
            names[80] = "Left";
            names[81] = "Down";
            names[82] = "Up";
            names[83] = "NumLock";

            // Keypad
            names[84] = "KP Divide";
            names[85] = "KP Multiply";
            names[86] = "KP Minus";
            names[87] = "KP Plus";
            names[88] = "KP Enter";
            for (int i = 0; i < 10; i++)
                names[89 + i] = $"KP {(i + 1) % 10}";
            names[99] = "KP Period";

            // Modifiers
            names[224] = "LCtrl";
            names[225] = "LShift";
            names[226] = "LAlt";
            names[227] = "LGui";
            names[228] = "RCtrl";
            names[229] = "RShift";
            names[230] = "RAlt";
            names[231] = "RGui";

            return names;
        }

        // ─────────────────────────────────────────────
        //  Version
        // ─────────────────────────────────────────────

        /// <summary>
        /// Gets the version of the linked SDL3 library.
        /// SDL3 change: returns a packed int (major*1000000 + minor*1000 + patch)
        /// instead of filling an SDL_version struct.
        /// </summary>
        [DllImport(lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_GetVersion();

        /// <summary>
        /// Convenience: returns the linked SDL version as a (major, minor, patch) tuple.
        /// </summary>
        public static (int major, int minor, int patch) SDL_Linked_Version()
        {
            int v = SDL_GetVersion();
            return (v / 1000000, (v / 1000) % 1000, v % 1000);
        }
    }
}
