using System;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine.Common;

namespace PadForge.Engine
{
    /// <summary>
    /// Wraps a Consumer Control HID collection (issue #168) for unified input
    /// via <see cref="ISdlInputDevice"/>, structurally mirroring
    /// <see cref="SdlKeyboardWrapper"/>. State is read from Raw Input
    /// (per-device) via <see cref="RawInputListener.GetConsumerState"/>.
    /// Buttons only: media remotes, headset strips, and keyboard media rows.
    /// There is no low-level hook for consumer usages, so unlike the keyboard
    /// wrapper there is no InputHookManager merge (and no consume support).
    /// </summary>
    public class ConsumerControlWrapper : ISdlInputDevice
    {
        private uint _sdlId;
        private IntPtr _rawInputHandle;
        private bool _disposed;

        public uint SdlInstanceId => _sdlId;
        public string Name { get; private set; } = "Consumer Control";
        public int NumAxes => 0;
        public int NumButtons => ConsumerUsageTable.TotalSlots;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => Array.Empty<int>();
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
                var devices = RawInputListener.EnumerateConsumerControls();
                for (int i = 0; i < devices.Length; i++)
                {
                    // Match by path: handles can change after other HID
                    // registrations, the same hazard the mouse wrapper guards.
                    if (!string.IsNullOrEmpty(DevicePath)
                        && devices[i].DevicePath == DevicePath)
                    {
                        if (devices[i].Handle != _rawInputHandle)
                            _rawInputHandle = devices[i].Handle;
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Opens the consumer collection from a Raw Input enumeration result,
        /// including the "All Consumer Controls (Merged)" aggregate.
        /// </summary>
        public bool Open(RawInputListener.DeviceInfo deviceInfo)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ConsumerControlWrapper));

            _rawInputHandle = deviceInfo.Handle;
            Name = deviceInfo.Name;
            DevicePath = deviceInfo.DevicePath;

            InstanceGuid = BuildGuid(deviceInfo.DevicePath);
            ProductGuid = BuildGuid("ConsumerControl");
            VendorId = deviceInfo.VendorId;
            ProductId = deviceInfo.ProductId;

            _sdlId = (uint)(deviceInfo.DevicePath.GetHashCode() & 0x7FFFFFFF);

            return true;
        }

        // Pooled per-tick state (poll thread is the sole caller).
        private PooledInputStatePair _statePool;

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            var state = _statePool.Next();
            RawInputListener.GetConsumerState(_rawInputHandle, state.Buttons, state.Buttons.Length);
            return state;
        }

        public DeviceObjectItem[] GetDeviceObjects()
        {
            int n = ConsumerUsageTable.TotalSlots;
            var items = new DeviceObjectItem[n];
            for (int i = 0; i < n; i++)
            {
                string name;
                if (i < ConsumerUsageTable.Fixed.Length)
                {
                    name = ConsumerUsageTable.Fixed[i].Name;
                }
                else
                {
                    ushort usage = RawInputListener.GetDynamicSlotUsage(i);
                    name = usage != 0
                        ? ConsumerUsageTable.DynamicName(usage)
                        : $"Consumer Slot {i}";
                }
                items[i] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.Key,
                    Name = name,
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = i * 4
                };
            }
            return items;
        }

        public int GetInputDeviceType() => InputDeviceType.ConsumerControl;
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
