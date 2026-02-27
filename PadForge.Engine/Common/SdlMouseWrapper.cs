using System;
using System.Security.Cryptography;
using System.Text;
using static SDL3.SDL;

namespace PadForge.Engine
{
    /// <summary>
    /// Wraps a mouse device for unified input via <see cref="ISdlInputDevice"/>.
    /// State is read from Raw Input (per-device) via <see cref="RawInputListener"/>.
    /// </summary>
    public class SdlMouseWrapper : ISdlInputDevice
    {
        private uint _sdlId;
        private IntPtr _rawInputHandle;
        private bool _disposed;
        private bool _isRawInputDevice;

        private const int MouseButtons = 5;
        private const int MouseAxes = 2;
        private const int AxisCenter = 32767;
        private const float MotionScale = 256f;

        public uint SdlInstanceId => _sdlId;
        public string Name { get; private set; } = "Mouse";
        public int NumAxes => MouseAxes;
        public int NumButtons => MouseButtons;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public bool HasRumble => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public ushort VendorId { get; private set; }
        public ushort ProductId { get; private set; }
        public string DevicePath { get; private set; } = string.Empty;
        public string SerialNumber => string.Empty;
        public Guid InstanceGuid { get; private set; }
        public Guid ProductGuid { get; private set; }

        /// <summary>The Raw Input device handle for per-device state reading.</summary>
        public IntPtr RawInputHandle => _rawInputHandle;

        public bool IsAttached
        {
            get
            {
                if (_isRawInputDevice)
                {
                    var devices = RawInputListener.EnumerateMice();
                    for (int i = 0; i < devices.Length; i++)
                    {
                        if (devices[i].Handle == _rawInputHandle)
                            return true;
                    }
                    return false;
                }

                var ids = SDL_GetMice();
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] == _sdlId)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Opens the mouse from a Raw Input device enumeration result.
        /// </summary>
        public bool Open(RawInputListener.DeviceInfo deviceInfo)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SdlMouseWrapper));

            _isRawInputDevice = true;
            _rawInputHandle = deviceInfo.Handle;
            Name = deviceInfo.Name;
            DevicePath = deviceInfo.DevicePath;

            InstanceGuid = BuildGuid(deviceInfo.DevicePath);
            ProductGuid = BuildGuid("Mouse");
            VendorId = deviceInfo.VendorId;
            ProductId = deviceInfo.ProductId;

            _sdlId = (uint)deviceInfo.DevicePath.GetHashCode();

            return true;
        }

        /// <summary>Pre-allocated buffer for mouse button reads.</summary>
        private readonly bool[] _mouseButtonBuffer = new bool[5];

        public CustomInputState GetCurrentState()
        {
            var state = new CustomInputState();

            RawInputListener.ConsumeMouseDelta(_rawInputHandle, out int dx, out int dy);
            state.Axis[0] = Math.Clamp(AxisCenter + (int)(dx * MotionScale), 0, 65535);
            state.Axis[1] = Math.Clamp(AxisCenter + (int)(dy * MotionScale), 0, 65535);

            RawInputListener.GetMouseButtons(_rawInputHandle, _mouseButtonBuffer);
            state.Buttons[0] = _mouseButtonBuffer[0]; // Left
            state.Buttons[1] = _mouseButtonBuffer[1]; // Middle
            state.Buttons[2] = _mouseButtonBuffer[2]; // Right
            state.Buttons[3] = _mouseButtonBuffer[3]; // X1
            state.Buttons[4] = _mouseButtonBuffer[4]; // X2

            return state;
        }

        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[MouseAxes + MouseButtons];
            int index = 0;

            items[index++] = new DeviceObjectItem
            {
                InputIndex = 0,
                ObjectTypeGuid = ObjectGuid.XAxis,
                Name = "X Motion",
                ObjectType = DeviceObjectTypeFlags.RelativeAxis,
                Offset = 0,
                Aspect = ObjectAspect.Position
            };
            items[index++] = new DeviceObjectItem
            {
                InputIndex = 1,
                ObjectTypeGuid = ObjectGuid.YAxis,
                Name = "Y Motion",
                ObjectType = DeviceObjectTypeFlags.RelativeAxis,
                Offset = 4,
                Aspect = ObjectAspect.Position
            };

            string[] buttonNames = { "Left Click", "Middle Click", "Right Click", "X1", "X2" };
            for (int i = 0; i < MouseButtons; i++)
            {
                items[index++] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = buttonNames[i],
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (MouseAxes + i) * 4,
                    Aspect = ObjectAspect.Position
                };
            }

            return items;
        }

        public int GetInputDeviceType() => InputDeviceType.Mouse;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        private static Guid BuildGuid(string identifier)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(identifier));
            return new Guid(hash);
        }

        public void Dispose()
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
