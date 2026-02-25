using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SDL3;
using static SDL3.SDL;

namespace PadForge.Engine
{
    /// <summary>
    /// Wraps an SDL keyboard device to provide unified input access via
    /// <see cref="ISdlInputDevice"/>. Each key scancode maps to a button index.
    /// </summary>
    public class SdlKeyboardWrapper : ISdlInputDevice
    {
        private uint _sdlId;
        private int _numKeys;
        private bool _disposed;

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
        public ushort VendorId => 0;
        public ushort ProductId => 0;
        public string DevicePath { get; private set; } = string.Empty;
        public Guid InstanceGuid { get; private set; }
        public Guid ProductGuid { get; private set; }

        public bool IsAttached
        {
            get
            {
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
        /// Opens the keyboard with the given SDL instance ID.
        /// </summary>
        public bool Open(uint keyboardId)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SdlKeyboardWrapper));

            _sdlId = keyboardId;
            Name = SDL_GetKeyboardNameForID(keyboardId);
            if (string.IsNullOrEmpty(Name))
                Name = "Keyboard";

            DevicePath = $"Keyboard#{keyboardId}";

            // Get the number of scancodes. Clamp to MaxButtons.
            IntPtr statePtr = SDL_GetKeyboardState(out int numKeys);
            _numKeys = Math.Min(numKeys, CustomInputState.MaxButtons);

            // Build deterministic GUIDs.
            InstanceGuid = BuildGuid($"Keyboard#{keyboardId}");
            ProductGuid = BuildGuid("Keyboard");

            return true;
        }

        public CustomInputState GetCurrentState()
        {
            IntPtr statePtr = SDL_GetKeyboardState(out int numKeys);
            if (statePtr == IntPtr.Zero)
                return null;

            var state = new CustomInputState();
            int count = Math.Min(numKeys, state.Buttons.Length);

            // SDL_GetKeyboardState returns an array of SDL_bool (1 byte each in SDL3).
            for (int i = 0; i < count; i++)
            {
                state.Buttons[i] = Marshal.ReadByte(statePtr, i) != 0;
            }

            return state;
        }

        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[_numKeys];
            for (int i = 0; i < _numKeys; i++)
            {
                string name = (i < ScancodeName.Length) ? ScancodeName[i] : $"Key {i}";
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
