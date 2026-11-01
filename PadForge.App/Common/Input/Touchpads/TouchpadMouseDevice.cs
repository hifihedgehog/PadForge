using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input.Touchpads
{
    /// <summary>
    /// The mouse row of a precision touchpad PadForge reads alone. While
    /// HidHide hides the touchpad's collection from Windows, Windows moves no
    /// pointer with it, so the row Windows' pointer used to feed reads the
    /// motion and buttons PadForge works out from the touchpad's own frames
    /// (<see cref="PrecisionTouchpadReader.ConsumeCapturedPointer"/>) and
    /// presents them as any mouse row does: two motion axes, the wheel and
    /// five buttons, with the scales of <see cref="SdlMouseWrapper"/>. The
    /// wheel stays at rest. Two-finger scrolling and taps are the touchpad
    /// row's gestures.
    ///
    /// <para>The row keeps the ID, path and name it had as a Raw Input mouse,
    /// so its slots and mappings carry over, and the device pass, which skips
    /// a path whose row is online, leaves it alone.</para>
    /// </summary>
    internal sealed class TouchpadMouseDevice : ISdlInputDevice
    {
        // A mouse row's shape and scales, the mouse wrapper's own
        // (PadForge.Engine Common/SdlMouseWrapper.cs:20-24).
        private const int MouseButtons = 5;
        private const int MouseAxes = 3;
        private const int AxisCenter = 32767;
        private const float MotionScale = 2048f;
        private const float ScrollScale = 128f;

        private readonly Func<(bool Reading, int Dx, int Dy, int Wheel, int Buttons)> _consume;
        private readonly Func<long> _timestamp;
        private readonly RelativeVelocityWindow _velocity = new();
        private PooledInputStatePair _statePool;
        private volatile bool _retired;
        private volatile bool _reading = true;

        /// <param name="touchpadPath">The touchpad collection's path, which
        /// the reader files the capture's state under.</param>
        /// <param name="consume">Test seam: what the touchpad left since the
        /// last poll. The reader by default.</param>
        public TouchpadMouseDevice(string touchpadPath, string mousePath, Guid rowId, string name,
            ushort vendorId, ushort productId, PrecisionTouchpadReader reader,
            Func<(bool Reading, int Dx, int Dy, int Wheel, int Buttons)> consume = null, Func<long> timestamp = null)
        {
            TouchpadPath = touchpadPath ?? string.Empty;
            DevicePath = mousePath ?? string.Empty;
            InstanceGuid = rowId;
            Name = string.IsNullOrWhiteSpace(name) ? "Touchpad" : name;
            VendorId = vendorId;
            ProductId = productId;
            ProductGuid = Md5Guid("Mouse");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
            _timestamp = timestamp ?? Stopwatch.GetTimestamp;
            _consume = consume ?? (() =>
            {
                if (reader == null || !reader.ConsumeCapturedPointer(TouchpadPath, out int dx, out int dy, out int wheel, out int buttons))
                    return (false, 0, 0, 0, 0);
                return (true, dx, dy, wheel, buttons);
            });
        }

        /// <summary>The touchpad collection this row reads.</summary>
        public string TouchpadPath { get; }

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => MouseAxes;
        public int NumButtons => MouseButtons;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => s_buttonIndices;
        private static readonly int[] s_buttonIndices = { 0, 1, 2, 3, 4 };
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public ushort VendorId { get; }
        public ushort ProductId { get; }
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        /// <summary>False once the row was retired or the capture stopped
        /// reading the touchpad.</summary>
        public bool IsAttached => !_retired && _reading;

        public int GetInputDeviceType() => InputDeviceType.Mouse;

        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>A mouse's objects, the ones the mouse wrapper lists
        /// (SdlMouseWrapper.cs:185-229), so a mapping names this row's inputs
        /// as it named them before. Written out as the WinUSB mouse row
        /// writes them (WinUsbMouseDevice), since the wrapper's list is
        /// internal to the engine.</summary>
        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[MouseAxes + MouseButtons];
            int index = 0;
            items[index++] = new DeviceObjectItem
            {
                InputIndex = 0,
                ObjectTypeGuid = ObjectGuid.XAxis,
                Name = "Mouse Speed X",
                ObjectType = DeviceObjectTypeFlags.RelativeAxis,
                Offset = 0
            };
            items[index++] = new DeviceObjectItem
            {
                InputIndex = 1,
                ObjectTypeGuid = ObjectGuid.YAxis,
                Name = "Mouse Speed Y",
                ObjectType = DeviceObjectTypeFlags.RelativeAxis,
                Offset = 4
            };
            items[index++] = new DeviceObjectItem
            {
                InputIndex = 2,
                ObjectTypeGuid = ObjectGuid.ZAxis,
                Name = "Mouse Scroll",
                ObjectType = DeviceObjectTypeFlags.RelativeAxis,
                Offset = 8
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
                    Offset = (MouseAxes + i) * 4
                };
            }
            return items;
        }

        /// <summary>The state for one poll, built as the mouse wrapper builds
        /// a Raw Input mouse's (SdlMouseWrapper.cs:135-177): the counts
        /// gathered since the last poll go through the same motion window,
        /// and the raw counts ride beside the axes for the gesture reader.
        /// Null once the capture stopped reading, which takes the row
        /// offline.</summary>
        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_retired) return null;
            var (reading, dx, dy, wheel, buttons) = _consume();
            if (!reading)
            {
                _reading = false;
                return null;
            }

            var state = _statePool.Next();
            // The wheel in Windows' wheel units, 120 to a notch, as a Raw
            // Input mouse's reaches the mouse wrapper.
            _velocity.Add(_timestamp(), dx, dy, wheel);
            _velocity.CountsPerSecond(out float vx, out float vy, out _);
            state.Axis[0] = Math.Clamp(AxisCenter + (int)(vx * (MotionScale / 1000f)), 0, 65535);
            state.Axis[1] = Math.Clamp(AxisCenter + (int)(vy * (MotionScale / 1000f)), 0, 65535);
            state.MouseRawDX = dx;
            state.MouseRawDY = dy;
            _velocity.WindowSums(out _, out _, out float scrollSum);
            state.Axis[2] = Math.Clamp(AxisCenter + (int)(scrollSum * ScrollScale), 0, 65535);

            // A mouse row's order is left, middle, right, then the side
            // buttons. The reader hands left as bit 0, right as bit 1 and
            // middle as bit 2.
            state.Buttons[0] = (buttons & 1) != 0;
            state.Buttons[1] = (buttons & 4) != 0;
            state.Buttons[2] = (buttons & 2) != 0;
            state.Buttons[3] = false;
            state.Buttons[4] = false;
            return state;
        }

        /// <summary>Retires the row. The capture keeps its own life.</summary>
        public void Dispose() => _retired = true;

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
