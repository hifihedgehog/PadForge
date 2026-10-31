using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input.WinUsbKeyboards
{
    /// <summary>
    /// The row of a mouse bound to WinUSB from the Devices page, the mouse
    /// switch beside the keyboard's (#549). Once bound, the mouse is no
    /// mouse to Windows: it has no Raw Input row and moves no pointer. This
    /// reads its input reports from the interrupt IN pipe on a thread of
    /// its own, decodes them by its report descriptor
    /// (<see cref="HidMouseReports"/>), and presents them as any mouse row
    /// does: the two motion axes, the wheel and five buttons, with the
    /// scales of <see cref="SdlMouseWrapper"/>.
    ///
    /// <para>The state is built as the bound TouchSense mouse's row builds
    /// its own (<see cref="TouchSenseMouseDevice"/>), which reads one fixed
    /// report where this reads any mouse's. The reader owns the channel's
    /// life, as the keyboard's row's does (<see cref="WinUsbKeyboardDevice"/>).</para>
    ///
    /// <para>The row takes the ID its takeover recorded, the mouse's row
    /// as a Raw Input mouse, so its slots, mappings and macros carry over.
    /// Nothing the low-level mouse hook sees is of this mouse.</para>
    /// </summary>
    internal sealed class WinUsbMouseDevice : IWinUsbInputRow
    {
        // A mouse row's shape and scales, the mouse wrapper's own
        // (PadForge.Engine Common/SdlMouseWrapper.cs:20-24).
        private const int MouseButtons = 5;
        private const int MouseAxes = 3;
        private const int AxisCenter = 32767;
        private const float MotionScale = 2048f;
        private const float ScrollScale = 128f;

        /// <summary>One wheel count as Windows reports a notch to Raw Input.</summary>
        internal const int WheelDelta = TouchSenseMouseDevice.WheelDelta;

        /// <summary>How long a blocking close waits for the reader to
        /// leave, above the read's own timeout.</summary>
        internal const int ReaderJoinMs = TouchSenseMouseDevice.ReaderJoinMs;

        private readonly IKeyboardChannel _channel;
        private readonly Func<long> _timestamp;
        private readonly object _sync = new();
        private readonly RelativeVelocityWindow _velocity = new();
        private PooledInputStatePair _statePool;
        private readonly Thread _reader;
        private volatile bool _stop;
        private volatile bool _ended;

        // What the reader gathered since the last poll, and the buttons
        // down now, bit n - 1 for HID button n. Guarded by _sync.
        private int _dx, _dy, _wheel, _buttons;

        /// <param name="start">False leaves the reader off, for a test that
        /// feeds reports itself (<see cref="Feed"/>).</param>
        /// <param name="timestamp">The clock the motion window runs on,
        /// in Stopwatch ticks.</param>
        public WinUsbMouseDevice(WinUsbKeyboard mouse, IKeyboardChannel channel, bool start = true,
            Func<long> timestamp = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _timestamp = timestamp ?? Stopwatch.GetTimestamp;
            VendorId = mouse.VendorId;
            ProductId = mouse.ProductId;
            DevicePath = mouse.Path ?? string.Empty;
            Name = string.IsNullOrWhiteSpace(mouse.Name)
                ? $"Mouse {mouse.VendorId:X4}:{mouse.ProductId:X4}"
                : mouse.Name;
            InstanceGuid = mouse.RowId != Guid.Empty ? mouse.RowId : IdentityFor(DevicePath);
            ProductGuid = Md5Guid("Mouse");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
            if (start)
            {
                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "WinUsbMouse" };
                _reader.Start();
            }
        }

        /// <summary>The row's ID when its takeover recorded none: hashed
        /// from the interface path in one case, as the keyboard's row is
        /// (<see cref="WinUsbKeyboardDevice.IdentityFor"/>).</summary>
        public static Guid IdentityFor(string interfacePath)
            => Md5Guid("pfwinusbmouse:" + (interfacePath ?? string.Empty).ToUpperInvariant());

        /// <summary>The channel this row reads.</summary>
        public IKeyboardChannel Channel => _channel;

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

        /// <summary>False once the row was retired, its reader left or its
        /// channel was closed, which is how the device pass learns that
        /// the row is to be retired and the mouse opened again.</summary>
        public bool IsAttached => !_stop && !_ended && _channel.IsOpen;

        public int GetInputDeviceType() => InputDeviceType.Mouse;

        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>A mouse's objects, the ones the mouse wrapper lists
        /// (SdlMouseWrapper.cs:185-229), so a mapping names this row's
        /// inputs as it names any mouse's.</summary>
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

        /// <summary>The state for one poll, built as the mouse wrapper
        /// builds a Raw Input mouse's (SdlMouseWrapper.cs:135-177): the
        /// counts gathered since the last poll go through the same motion
        /// window, the wheel is an impulse summed over that window, and the
        /// raw counts ride beside the axes for the gesture reader. Null once
        /// the row is not attached, which takes it offline.</summary>
        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (!IsAttached) return null;
            int dx, dy, wheel, buttons;
            lock (_sync)
            {
                dx = _dx;
                dy = _dy;
                wheel = _wheel;
                buttons = _buttons;
                _dx = _dy = _wheel = 0;
            }

            var state = _statePool.Next();
            _velocity.Add(_timestamp(), dx, dy, wheel * WheelDelta);
            _velocity.CountsPerSecond(out float vx, out float vy, out _);
            state.Axis[0] = Math.Clamp(AxisCenter + (int)(vx * (MotionScale / 1000f)), 0, 65535);
            state.Axis[1] = Math.Clamp(AxisCenter + (int)(vy * (MotionScale / 1000f)), 0, 65535);
            state.MouseRawDX = dx;
            state.MouseRawDY = dy;
            _velocity.WindowSums(out _, out _, out float scrollSum);
            state.Axis[2] = Math.Clamp(AxisCenter + (int)(scrollSum * ScrollScale), 0, 65535);

            // A mouse row's order is left, middle, right, then the side
            // buttons. HID numbers them left, right, middle, side, extra
            // (hid-input.c:783-788), as Windows' mouhid reads them.
            state.Buttons[0] = (buttons & 0x01) != 0;
            state.Buttons[1] = (buttons & 0x04) != 0;
            state.Buttons[2] = (buttons & 0x02) != 0;
            state.Buttons[3] = (buttons & 0x08) != 0;
            state.Buttons[4] = (buttons & 0x10) != 0;
            return state;
        }

        /// <summary>One input report as the pipe delivered it. A report the
        /// layout does not know is passed over.</summary>
        internal void Feed(ReadOnlySpan<byte> report)
        {
            var pointer = _channel.Pointer;
            if (pointer == null) return;
            lock (_sync)
            {
                int dx = 0, dy = 0, wheel = 0, buttons = _buttons;
                if (!pointer.Decode(report, ref dx, ref dy, ref wheel, ref buttons)) return;
                _dx += dx;
                _dy += dy;
                _wheel += wheel;
                _buttons = buttons;
            }
        }

        private void ReadLoop()
        {
            var report = new byte[Math.Max(1, _channel.ReadLength)];
            try
            {
                while (!_stop)
                {
                    if (!_channel.TryRead(report, out int length)) break;
                    if (length > 0) Feed(report.AsSpan(0, length));
                }
            }
            catch
            {
            }
            finally
            {
                _ended = true;
                // On this thread, where waiting for the handle to close
                // holds nobody up.
                try { _channel.Dispose(); } catch { }
            }
        }

        /// <summary>Retires the row. It returns at once, since the poll
        /// thread calls it: the reader sees the flag within one read
        /// timeout and closes the channel as it leaves. A row with no
        /// reader closes its channel here.</summary>
        public void Dispose()
        {
            _stop = true;
            if (_reader != null) return;
            try { _channel.Dispose(); } catch { }
        }

        /// <summary>Retires the row and returns only once the mouse's
        /// handle is closed, for the manager's shutdown.</summary>
        public void Close()
        {
            _stop = true;
            try { _channel.Dispose(); } catch { }
            var reader = _reader;
            if (reader != null && reader != Thread.CurrentThread)
            {
                try { reader.Join(ReaderJoinMs); } catch { }
            }
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
