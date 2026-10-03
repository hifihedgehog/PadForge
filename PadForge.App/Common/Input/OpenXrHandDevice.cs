using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.OpenXr;

namespace PadForge.Common.Input
{
    /// <summary>
    /// One VR motion controller as a mappable device row (issue #403).
    ///
    /// <para>The headset shipped first because it needs no action set. A
    /// controller needs the whole apparatus, and what comes out the other
    /// side is this: six pose axes in the same convention the Head Tracker
    /// row uses, plus the stick, the trigger, the grip and four buttons.</para>
    ///
    /// <para>Left and right are separate rows with separate identities, so a
    /// mapping survives one controller going to sleep and so losing one does
    /// not neutralize the other.</para>
    ///
    /// <para>Silence returns the axes to rest, the failsafe the Head Tracker
    /// row has. A controller that goes to sleep or drops out of tracking
    /// mid-game must not leave a stick held. One that is set down and still
    /// tracked keeps reporting, and its axes follow it.</para>
    /// </summary>
    internal sealed class OpenXrHandDevice : ISdlInputDevice
    {
        private const ushort HandVendorId = 0x1209;   // pid.codes open-source VID
        private const ushort LeftProductId = 0x2874;
        private const ushort RightProductId = 0x2875;

        /// <summary>Samples older than this return everything to rest.</summary>
        public const int SilenceMs = 1000;

        // Axis layout. The six pose axes come first and in the Head Tracker
        // row's order, so a user who has mapped a head axis reads the same
        // names here.
        internal const int AxisYaw = 0, AxisPitch = 1, AxisRoll = 2;
        internal const int AxisX = 3, AxisY = 4, AxisZ = 5;
        internal const int AxisStickX = 6, AxisStickY = 7;
        internal const int AxisTrigger = 8, AxisSqueeze = 9;
        internal const int AxisCount = 10;

        internal const int ButtonStickClick = 0, ButtonPrimary = 1;
        internal const int ButtonSecondary = 2, ButtonMenu = 3;
        internal const int ButtonCount = 4;

        private static readonly int[] s_axisIndices = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        private static readonly int[] s_buttonIndices = { 0, 1, 2, 3 };

        private readonly OpenXrHand _hand;
        private readonly object _stateLock = new();
        private readonly CustomInputState _state = new();
        private readonly Func<long> _now;
        private PooledInputStatePair _statePool;

        private OpenXrHandState _latest;
        private long _lastSampleTicks;      // 0 = never
        private volatile bool _attached;
        private volatile bool _disposed;
        private volatile int _statusVersion;

        public OpenXrHandDevice(OpenXrHand hand, Func<long> now = null)
        {
            _hand = hand;
            _now = now ?? (() => Environment.TickCount64);
            bool left = hand == OpenXrHand.Left;
            Name = left ? "VR Controller (Left)" : "VR Controller (Right)";
            DevicePath = left ? "openxr://hand/left" : "openxr://hand/right";
            InstanceGuid = Md5Guid("pfopenxrhand:" + (left ? "left" : "right"));
            // Per hand, not shared. The offline-row adoption path matches on
            // ProductGuid, so one seed for both hands let the left row adopt
            // the right row's saved mappings. Every other multi-instance
            // synthetic row discriminates its product seed the same way.
            ProductGuid = Md5Guid("pfopenxrhand-product:" + (left ? "left" : "right"));
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
            ProductId = left ? LeftProductId : RightProductId;
            Center();
        }

        private static Guid Md5Guid(string seed)
            => new(MD5.HashData(Encoding.UTF8.GetBytes(seed)));

        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => AxisCount;
        public int NumButtons => ButtonCount;
        public int RawButtonCount => ButtonCount;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => s_buttonIndices;
        public int[] SupportedAxisIndices => s_axisIndices;
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
        public bool IsAttached => _attached && !_disposed;
        public ushort VendorId => HandVendorId;
        public ushort ProductId { get; }
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.VrController;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>Bumps when the hand goes live or falls silent.</summary>
        public int StatusVersion => _statusVersion;

        /// <summary>True while samples are arriving.</summary>
        public bool IsLive
        {
            get
            {
                lock (_stateLock)
                    return _lastSampleTicks != 0 && _now() - _lastSampleTicks <= SilenceMs;
            }
        }

        // "X" alone said nothing in a picker beside a gamepad's "X Axis",
        // and none of these were localizable. They now read like the head
        // tracker's six, which is the row this one sits next to.
        private static readonly string[] s_axisNames =
        {
            "Controller Yaw", "Controller Pitch", "Controller Roll",
            "Controller X", "Controller Y", "Controller Z",
            "Thumbstick X", "Thumbstick Y", "Trigger", "Grip",
        };

        private static readonly string[] s_buttonNames =
        {
            "Thumbstick Click", "Primary Button", "Secondary Button", "Menu Button",
        };

        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[AxisCount + ButtonCount];
            for (int i = 0; i < AxisCount; i++)
                items[i] = new DeviceObjectItem
                {
                    Name = s_axisNames[i],
                    ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                    InputIndex = i,
                    Offset = i * 4,
                };
            for (int i = 0; i < ButtonCount; i++)
                items[AxisCount + i] = new DeviceObjectItem
                {
                    Name = s_buttonNames[i],
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    // The Devices page lists a row's named buttons by this
                    // type, as it does the G-Keys'. Without it the row showed
                    // an empty chip panel.
                    ObjectTypeGuid = ObjectGuid.Button,
                    InputIndex = i,
                    Offset = (AxisCount + i) * 4,
                };
            return items;
        }

        public bool Open()
        {
            if (_disposed) return false;
            _attached = true;
            return true;
        }

        /// <summary>A sample from the OpenXR thread.</summary>
        public void Publish(in OpenXrHandState state)
        {
            lock (_stateLock)
            {
                bool wasLive = _lastSampleTicks != 0;
                _latest = state;
                // An idle hand is not a sample. Treating it as one would hold
                // the silence timer open on a controller that is asleep.
                if (state.IsIdle) return;
                _lastSampleTicks = _now();
                if (!wasLive) _statusVersion++;
            }
        }

        private void Center()
        {
            for (int i = 0; i < AxisCount; i++) _state.Axis[i] = HeadPose.AxisCenter;
            // A trigger and a grip rest at zero, not at center, which is the
            // unsigned convention CustomInputState documents.
            _state.Axis[AxisTrigger] = 0;
            _state.Axis[AxisSqueeze] = 0;
            for (int i = 0; i < ButtonCount; i++) _state.Buttons[i] = false;
        }

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            lock (_stateLock)
            {
                long now = _now();
                bool live = _lastSampleTicks != 0 && now - _lastSampleTicks <= SilenceMs;
                if (!live)
                {
                    Center();
                    if (_lastSampleTicks != 0)
                    {
                        _lastSampleTicks = 0;
                        _statusVersion++;
                    }
                }
                else
                {
                    Fill(_latest);
                }

                var dst = _statePool.Next();
                _state.CopyInto(dst);
                return dst;
            }
        }

        private static double Range(int axis) => HeadTrackingRuntime.GetAxisRange(axis);

        private void Fill(in OpenXrHandState s)
        {
            if (s.PoseValid)
            {
                // The same per-axis ranges the head uses, so a mapping
                // reads the same way whichever source drives it.
                _state.Axis[AxisYaw] = HeadPose.ToAxis(s.YawDeg, Range(HeadPose.AxisYaw));
                _state.Axis[AxisPitch] = HeadPose.ToAxis(-s.PitchDeg, Range(HeadPose.AxisPitch));
                _state.Axis[AxisRoll] = HeadPose.ToAxis(s.RollDeg, Range(HeadPose.AxisRoll));
                _state.Axis[AxisX] = HeadPose.ToAxis(s.TX, Range(HeadPose.AxisX));
                _state.Axis[AxisY] = HeadPose.ToAxis(-s.TY, Range(HeadPose.AxisY));
                _state.Axis[AxisZ] = HeadPose.ToAxis(s.TZ, Range(HeadPose.AxisZ));
            }
            else
            {
                for (int i = AxisYaw; i <= AxisZ; i++) _state.Axis[i] = HeadPose.AxisCenter;
            }

            if (s.ControlsActive)
            {
                _state.Axis[AxisStickX] = HeadPose.ToAxis(s.ThumbstickX, 1.0);
                _state.Axis[AxisStickY] = HeadPose.ToAxis(-s.ThumbstickY, 1.0);
                _state.Axis[AxisTrigger] = (int)Math.Round(Math.Clamp(s.Trigger, 0f, 1f) * 65535);
                _state.Axis[AxisSqueeze] = (int)Math.Round(Math.Clamp(s.Squeeze, 0f, 1f) * 65535);
                _state.Buttons[ButtonStickClick] = s.ThumbstickClick;
                _state.Buttons[ButtonPrimary] = s.PrimaryButton;
                _state.Buttons[ButtonSecondary] = s.SecondaryButton;
                _state.Buttons[ButtonMenu] = s.MenuButton;
            }
            else
            {
                // The action set is not ours this sample. Release rather than
                // repeat, or a game keeps whatever was last held.
                _state.Axis[AxisStickX] = HeadPose.AxisCenter;
                _state.Axis[AxisStickY] = HeadPose.AxisCenter;
                _state.Axis[AxisTrigger] = 0;
                _state.Axis[AxisSqueeze] = 0;
                for (int i = 0; i < ButtonCount; i++) _state.Buttons[i] = false;
            }
        }

        /// <summary>A sample as the OpenXR thread would deliver it.</summary>
        internal void InjectForTest(in OpenXrHandState state) => Publish(state);

        internal void AttachForTest() => _attached = true;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
        }
    }
}
