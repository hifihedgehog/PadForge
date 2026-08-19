using System;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine.Common;
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
        private const int MouseAxes = 3;
        private const int AxisCenter = 32767;
        private const float MotionScale = 2048f;
        private const float ScrollScale = 128f;

        public uint SdlInstanceId => _sdlId;
        public string Name { get; private set; } = "Mouse";
        public int NumAxes => MouseAxes;
        public int NumButtons => MouseButtons;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => _denseButtonIndices ??= BuildDense(MouseButtons);
        private int[] _denseButtonIndices;
        private static int[] BuildDense(int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = i; return a; }
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
        public ushort VendorId { get; private set; }
        public ushort ProductId { get; private set; }
        public string DevicePath { get; private set; } = string.Empty;
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;
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
                        // Match by path (handles can change after PTP registration).
                        if (!string.IsNullOrEmpty(DevicePath) &&
                            devices[i].DevicePath == DevicePath)
                        {
                            // Update stale handle if it changed.
                            if (devices[i].Handle != _rawInputHandle)
                                _rawInputHandle = devices[i].Handle;
                            return true;
                        }
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
        /// Updates the Raw Input handle when the same physical device is
        /// re-enumerated with a new handle (e.g. after PTP registration).
        /// </summary>
        public void UpdateHandle(IntPtr newHandle) => _rawInputHandle = newHandle;

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

            _sdlId = (uint)(deviceInfo.DevicePath.GetHashCode() & 0x7FFFFFFF);

            return true;
        }

        /// <summary>Pre-allocated buffer for mouse button reads.</summary>
        private readonly bool[] _mouseButtonBuffer = new bool[5];

        // Pooled per-tick state (poll thread is the sole caller).
        private PooledInputStatePair _statePool;

        // Sliding-window velocity for the relative axes (#331). The old code
        // published the PER-POLL delta scaled by MotionScale, which made the
        // axis a center/spike comb whenever the mouse's report rate was below
        // the polling rate (7 of 8 polls read zero with a 125 Hz mouse at
        // 1 kHz), and made the scale depend on the polling interval. The
        // window preserves the old scale exactly for a 1 kHz mouse at the
        // default 1 ms interval (counts/s x MotionScale/1000 == counts/poll x
        // MotionScale) and preserves the deflection time-integral at every
        // report rate, so net camera speed through a mapping is unchanged.
        private readonly RelativeVelocityWindow _velocity = new RelativeVelocityWindow();

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            var state = _statePool.Next();

            // _rawInputHandle is kept up-to-date by Step 1. When PTP claims the
            // trackpad's mouse collection, Step 1 redirects all mouse wrappers to
            // IntPtr.Zero (Windows' synthetic mouse output at hDevice=0).
            RawInputListener.ConsumeMouseDelta(_rawInputHandle, out int dx, out int dy);
            int scroll = RawInputListener.ConsumeMouseScroll(_rawInputHandle);

            _velocity.Add(System.Diagnostics.Stopwatch.GetTimestamp(), dx, dy, scroll);
            _velocity.CountsPerSecond(out float vx, out float vy, out _);
            state.Axis[0] = Math.Clamp(AxisCenter + (int)(vx * (MotionScale / 1000f)), 0, 65535);
            state.Axis[1] = Math.Clamp(AxisCenter + (int)(vy * (MotionScale / 1000f)), 0, 65535);
            // Unclamped counts for the mouse-gesture recognizer (#200), from
            // this same single consume. Deliberately the raw per-poll values,
            // not the windowed velocity: gesture recognition wants the exact
            // count stream.
            state.MouseRawDX = dx;
            state.MouseRawDY = dy;

            // Scroll is an impulse lane, not a rate lane: a notch is a
            // discrete ±120 event, so the boxcar AVERAGE that is right for
            // motion collapses its peak by the window factor (one notch read
            // 614 counts instead of 15,360, dead under any mapping threshold
            // above 2%). The window SUM keeps the old per-poll peak exactly,
            // holds it for the 25 ms window instead of one poll, and merges
            // notches inside one window so faster scrolling reads stronger.
            _velocity.WindowSums(out _, out _, out float scrollSum);
            state.Axis[2] = Math.Clamp(AxisCenter + (int)(scrollSum * ScrollScale), 0, 65535);

            RawInputListener.GetMouseButtons(_rawInputHandle, _mouseButtonBuffer);
            // Merge buttons captured by the low-level mouse hook (same reason
            // as keyboard — WH_MOUSE_LL suppression blocks WM_INPUT).
            InputHookManager.MergeHookedMouseState(_mouseButtonBuffer, 5);
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
