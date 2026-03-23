using System;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Engine
{
    /// <summary>
    /// Virtual input device representing a browser-connected gamepad.
    /// State is written by the WebSocket receive thread and read by the polling thread.
    /// Implements <see cref="ISdlInputDevice"/> so it flows through the standard
    /// 6-step input pipeline alongside physical devices.
    /// </summary>
    public class WebControllerDevice : ISdlInputDevice
    {
        // Standard gamepad layout: 6 axes, 11 buttons, 1 POV.
        private const int NumGamepadAxes = 6;
        private const int NumGamepadButtons = 11;
        private const int NumGamepadPovs = 1;

        // Distinctive VID/PID to avoid ViGEm filter false positives.
        private const ushort WebVendorId = 0xBEEF;
        private const ushort WebProductId = 0xCA7E;

        /// <summary>Fixed ProductGuid shared by all web controller instances.</summary>
        public static readonly Guid WebProductGuid =
            new Guid("BEBC0000-0000-0000-0000-CAFEFACE0001");

        // Axis names matching the standard gamepad layout (LX, LY, LT, RX, RY, RT).
        private static readonly Guid[] AxisGuids =
        {
            ObjectGuid.XAxis,  ObjectGuid.YAxis,  ObjectGuid.ZAxis,
            ObjectGuid.RxAxis, ObjectGuid.RyAxis, ObjectGuid.RzAxis
        };

        private static readonly string[] AxisNames =
            { "Left X", "Left Y", "Left Trigger", "Right X", "Right Y", "Right Trigger" };

        private static readonly string[] ButtonNames =
            { "A", "B", "X", "Y", "Left Shoulder", "Right Shoulder", "Back", "Start", "Left Stick Button", "Right Stick Button", "Guide" };

        // Thread-safe state: written by WebSocket thread, read by polling thread.
        private volatile CustomInputState _currentState = new CustomInputState();
        private readonly object _stateLock = new object();
        private volatile bool _connected;

        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => NumGamepadAxes;
        public int NumButtons => NumGamepadButtons;
        public int RawButtonCount => NumGamepadButtons;
        public int NumHats => NumGamepadPovs;
        public bool HasRumble => true;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public ushort VendorId => WebVendorId;
        public ushort ProductId => WebProductId;
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid => WebProductGuid;

        public bool IsAttached => _connected;

        /// <summary>
        /// Fired on the polling thread when SetRumble is called.
        /// Parameters: (lowFrequency, highFrequency) in 0-65535 range.
        /// </summary>
        public event Action<ushort, ushort> RumbleRequested;

        /// <summary>
        /// Creates a new web controller device.
        /// </summary>
        /// <param name="clientId">Unique client identifier (from browser localStorage).</param>
        /// <param name="displayName">Human-readable name (e.g. "Web Controller 1").</param>
        public WebControllerDevice(string clientId, string displayName)
        {
            Name = displayName;
            DevicePath = $"web://{clientId}";
            InstanceGuid = BuildGuid(clientId);
            SdlInstanceId = (uint)clientId.GetHashCode();

            // Center stick axes at midpoint, triggers at 0 (full off).
            var state = new CustomInputState();
            for (int i = 0; i < NumGamepadAxes; i++)
                state.Axis[i] = (i == 2 || i == 5) ? 0 : 32767;
            Volatile.Write(ref _currentState, state);
        }

        /// <summary>Updates an axis value. Called from WebSocket receive thread.</summary>
        /// <param name="code">Axis index (0=LX, 1=LY, 2=LT, 3=RX, 4=RY, 5=RT).</param>
        /// <param name="value">Unsigned value 0-65535 (center = 32767).</param>
        public void UpdateAxis(int code, int value)
        {
            if (code < 0 || code >= NumGamepadAxes) return;
            lock (_stateLock)
            {
                var s = _currentState.Clone();
                s.Axis[code] = value;
                _currentState = s;
            }
        }

        /// <summary>Updates a button state. Called from WebSocket receive thread.</summary>
        /// <param name="code">Button index (0=A, 1=B, ..., 10=Guide).</param>
        /// <param name="pressed">True if pressed.</param>
        public void UpdateButton(int code, bool pressed)
        {
            if (code < 0 || code >= NumGamepadButtons) return;
            lock (_stateLock)
            {
                var s = _currentState.Clone();
                s.Buttons[code] = pressed;
                _currentState = s;
            }
        }

        /// <summary>Updates POV hat value. Called from WebSocket receive thread.</summary>
        /// <param name="value">Centidegrees (0=N, 9000=E, etc.) or -1 for centered.</param>
        public void UpdatePov(int value)
        {
            lock (_stateLock)
            {
                var s = _currentState.Clone();
                s.Povs[0] = value;
                _currentState = s;
            }
        }

        /// <summary>Updates a touchpad finger's position and contact state.</summary>
        public void UpdateTouchpadFinger(int finger, float x, float y, bool down)
        {
            lock (_stateLock)
            {
                var s = _currentState.Clone();
                int offset = finger * 3;
                if (offset + 2 < s.TouchpadFingers.Length)
                {
                    s.TouchpadFingers[offset] = x;
                    s.TouchpadFingers[offset + 1] = y;
                    s.TouchpadFingers[offset + 2] = down ? 1f : 0f;
                }
                if (finger < s.TouchpadDown.Length)
                    s.TouchpadDown[finger] = down;
                _currentState = s;
            }
        }

        /// <summary>Sets the touchpad click state (momentary).</summary>
        public void UpdateTouchpadClick(bool clicked)
        {
            lock (_stateLock)
            {
                var s = _currentState.Clone();
                s.TouchpadClick = clicked;
                _currentState = s;
            }
        }

        /// <summary>Sets the connection state.</summary>
        public void SetConnected(bool connected) => _connected = connected;

        public CustomInputState GetCurrentState(bool forceRaw = false) => _currentState.Clone();

        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[NumGamepadAxes + NumGamepadButtons + NumGamepadPovs];
            int idx = 0;

            // Axes.
            for (int i = 0; i < NumGamepadAxes; i++)
            {
                items[idx++] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = AxisGuids[i],
                    Name = AxisNames[i],
                    ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                    Offset = i * 4,
                    Aspect = ObjectAspect.Position
                };
            }

            // Buttons.
            for (int i = 0; i < NumGamepadButtons; i++)
            {
                items[idx++] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = ButtonNames[i],
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (NumGamepadAxes + i) * 4,
                    Aspect = ObjectAspect.Position
                };
            }

            // POV.
            items[idx++] = new DeviceObjectItem
            {
                InputIndex = 0,
                ObjectTypeGuid = ObjectGuid.PovController,
                Name = "D-Pad",
                ObjectType = DeviceObjectTypeFlags.PointOfViewController,
                Offset = (NumGamepadAxes + NumGamepadButtons) * 4,
                Aspect = ObjectAspect.Position
            };

            return items;
        }

        public int GetInputDeviceType() => InputDeviceType.Gamepad;

        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue)
        {
            RumbleRequested?.Invoke(low, high);
            return true;
        }

        public bool StopRumble()
        {
            RumbleRequested?.Invoke(0, 0);
            return true;
        }

        public void Dispose()
        {
            _connected = false;
            GC.SuppressFinalize(this);
        }

        private static Guid BuildGuid(string identifier)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(identifier));
            return new Guid(hash);
        }
    }
}
