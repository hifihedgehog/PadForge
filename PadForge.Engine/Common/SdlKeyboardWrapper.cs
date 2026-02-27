using System;
using System.Security.Cryptography;
using System.Text;
using static SDL3.SDL;

namespace PadForge.Engine
{
    /// <summary>
    /// Wraps a keyboard device for unified input via <see cref="ISdlInputDevice"/>.
    /// State is read from Raw Input (per-device) via <see cref="RawInputListener"/>.
    /// Enumeration uses either SDL or Raw Input depending on how the device was opened.
    /// </summary>
    public class SdlKeyboardWrapper : ISdlInputDevice
    {
        private uint _sdlId;
        private IntPtr _rawInputHandle;
        private int _numKeys;
        private bool _disposed;
        private bool _isRawInputDevice;

        public uint SdlInstanceId => _sdlId;
        public string Name { get; private set; } = "Keyboard";
        public int NumAxes => 0;
        public int NumButtons => _numKeys;
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
                    var devices = RawInputListener.EnumerateKeyboards();
                    for (int i = 0; i < devices.Length; i++)
                    {
                        if (devices[i].Handle == _rawInputHandle)
                            return true;
                    }
                    return false;
                }

                var ids = SDL_GetKeyboards();
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] == _sdlId)
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Opens the keyboard from a Raw Input device enumeration result.
        /// </summary>
        public bool Open(RawInputListener.DeviceInfo deviceInfo)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SdlKeyboardWrapper));

            _isRawInputDevice = true;
            _rawInputHandle = deviceInfo.Handle;
            Name = deviceInfo.Name;
            DevicePath = deviceInfo.DevicePath;

            _numKeys = Math.Min(256, CustomInputState.MaxButtons);

            InstanceGuid = BuildGuid(deviceInfo.DevicePath);
            ProductGuid = BuildGuid("Keyboard");
            VendorId = deviceInfo.VendorId;
            ProductId = deviceInfo.ProductId;

            // Use a hash of the device path as a pseudo SDL instance ID for tracking.
            _sdlId = (uint)deviceInfo.DevicePath.GetHashCode();

            return true;
        }

        public CustomInputState GetCurrentState()
        {
            var state = new CustomInputState();
            RawInputListener.GetKeyboardState(_rawInputHandle, state.Buttons, state.Buttons.Length);
            return state;
        }

        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[_numKeys];
            for (int i = 0; i < _numKeys; i++)
            {
                string name = (i < VirtualKeyName.Length) ? VirtualKeyName[i] : $"Key {i}";
                items[i] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.Key,
                    Name = name,
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = i * 4,
                    Aspect = ObjectAspect.Position
                };
            }
            return items;
        }

        public int GetInputDeviceType() => InputDeviceType.Keyboard;
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
