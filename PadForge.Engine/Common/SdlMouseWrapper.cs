using System;
using System.Security.Cryptography;
using System.Text;
using SDL3;
using static SDL3.SDL;

namespace PadForge.Engine
{
    /// <summary>
    /// Wraps an SDL mouse device to provide unified input access via
    /// <see cref="ISdlInputDevice"/>. Buttons map to Buttons[0..4],
    /// relative motion maps to Axis[0] (X) and Axis[1] (Y).
    /// </summary>
    public class SdlMouseWrapper : ISdlInputDevice
    {
        private uint _sdlId;
        private bool _disposed;

        /// <summary>Number of mouse buttons (left, middle, right, X1, X2).</summary>
        private const int MouseButtons = 5;

        /// <summary>Number of mouse axes (X delta, Y delta).</summary>
        private const int MouseAxes = 2;

        /// <summary>Center value for unsigned axis range (0-65535).</summary>
        private const int AxisCenter = 32767;

        /// <summary>Scale factor for relative mouse motion to axis range.</summary>
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
        public ushort VendorId => 0;
        public ushort ProductId => 0;
        public string DevicePath { get; private set; } = string.Empty;
        public Guid InstanceGuid { get; private set; }
        public Guid ProductGuid { get; private set; }

        public bool IsAttached
        {
            get
            {
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
        /// Opens the mouse with the given SDL instance ID.
        /// </summary>
        public bool Open(uint mouseId)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SdlMouseWrapper));

            _sdlId = mouseId;
            Name = SDL_GetMouseNameForID(mouseId);
            if (string.IsNullOrEmpty(Name))
                Name = "Mouse";

            DevicePath = $"Mouse#{mouseId}";

            InstanceGuid = BuildGuid($"Mouse#{mouseId}");
            ProductGuid = BuildGuid("Mouse");

            return true;
        }

        public CustomInputState GetCurrentState()
        {
            var state = new CustomInputState();

            // Relative mouse motion → axes centered at 32767.
            // GetRelativeMouseState returns delta since last call.
            uint buttons = SDL_GetRelativeMouseState(out float dx, out float dy);

            // Scale and clamp to unsigned axis range.
            state.Axis[0] = Math.Clamp(AxisCenter + (int)(dx * MotionScale), 0, 65535);
            state.Axis[1] = Math.Clamp(AxisCenter + (int)(dy * MotionScale), 0, 65535);

            // Mouse buttons from bitmask.
            state.Buttons[0] = (buttons & SDL_BUTTON_LMASK) != 0;
            state.Buttons[1] = (buttons & SDL_BUTTON_MMASK) != 0;
            state.Buttons[2] = (buttons & SDL_BUTTON_RMASK) != 0;
            state.Buttons[3] = (buttons & SDL_BUTTON_X1MASK) != 0;
            state.Buttons[4] = (buttons & SDL_BUTTON_X2MASK) != 0;

            return state;
        }

        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[MouseAxes + MouseButtons];
            int index = 0;

            // Axes
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

            // Buttons
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
