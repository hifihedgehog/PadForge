using System;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// A contactless PC/SC reader (e.g. ACR122U) exposed to the input
    /// pipeline as a standard <see cref="ISdlInputDevice"/>, so an NFC tap
    /// can trigger a macro through the existing raw-button machinery
    /// (issue #150, Path A). Modeled on <see cref="MidiInputDevice"/>.
    ///
    /// M1 scope: the reader exposes a single momentary button, "Any NFC
    /// Tag" (raw button 0). Presenting any ISO 14443 tag pulses it for
    /// <see cref="PulseMs"/>, which is long enough for a 60 Hz macro poll
    /// to catch the rising edge. The macro evaluator is untouched: it reads
    /// <c>CustomInputState.Buttons[0]</c> through <c>CheckRawButtonTrigger</c>
    /// exactly as for any other device assigned to the slot.
    ///
    /// Per-UID specific-tag binding (a registry mapping each tag's UID to a
    /// distinct button + a "Register tag" flow) is deferred to M3. The UID
    /// the reader returns is already surfaced via <see cref="NfcReaderService.
    /// TagDetected"/> for that later work; M1 deliberately matches "any tag"
    /// so it needs no persisted registry and never touches the settings
    /// serializer.
    /// </summary>
    internal sealed class NfcReaderDevice : ISdlInputDevice
    {
        private const ushort NfcVendorId = 0x4E46;  // "NF"
        private const ushort NfcProductId = 0x4350; // "CP"

        /// <summary>Tag-button hold time. One frame of a 60 Hz (16.7 ms)
        /// macro poll must land inside it; 175 ms gives a clean rising-then-
        /// falling edge and one OnPress per tap at any realistic poll rate.</summary>
        private const int PulseMs = 175;

        // Raw button 0 = "Any NFC Tag".
        private const int AnyTagButton = 0;

        private readonly object _stateLock = new();
        private CustomInputState _state;
        private volatile bool _attached;

        // Pulse expiry for the any-tag button (TickCount64). 0 = released.
        private long _pulseUntil;

        private readonly string _readerName;
        private Action<string, string> _handler;

        public NfcReaderDevice(string readerName)
        {
            _readerName = readerName;
            Name = readerName;
            DevicePath = $"nfc://{readerName}";
            InstanceGuid = Md5Guid("pfnfc:" + readerName);
            ProductGuid = Md5Guid("pfnfc-product:" + readerName);
            SdlInstanceId = unchecked((uint)readerName.GetHashCode());
            _state = new CustomInputState();
        }

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        // One mappable button. RawButtonCount feeds the offline picker
        // fallback; the live picker uses GetDeviceObjects below.
        public int NumButtons => 1;
        public int RawButtonCount => 1;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => new[] { AnyTagButton };
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
        public bool IsAttached => _attached;
        public ushort VendorId => NfcVendorId;
        public ushort ProductId => NfcProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.Nfc;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        // One DeviceObjectItem so the mapping picker lists "Any NFC Tag" as a
        // bindable button (the deliberate divergence from MidiInputDevice's
        // empty list). The macro stores its InputIndex as the raw button.
        public DeviceObjectItem[] GetDeviceObjects() => new[]
        {
            new DeviceObjectItem
            {
                Name = "Any NFC Tag",
                ObjectType = DeviceObjectTypeFlags.PushButton,
                ObjectTypeGuid = ObjectGuid.Button,
                InputIndex = AnyTagButton,
            }
        };

        // ─── Lifecycle ───

        /// <summary>Subscribes to the shared NFC monitor for this reader.
        /// Returns false when the monitor is not running (no Smart Card
        /// service), so the device is never registered without a producer.</summary>
        public bool Open()
        {
            var svc = NfcReaderService.Active;
            if (svc == null) return false;
            _handler = OnTagDetected;
            svc.TagDetected += _handler;
            _attached = true;
            return true;
        }

        public void Dispose()
        {
            _attached = false;
            var svc = NfcReaderService.Active;
            if (svc != null && _handler != null)
            {
                try { svc.TagDetected -= _handler; } catch { }
            }
            _handler = null;
        }

        private void OnTagDetected(string reader, string uid)
        {
            // The monitor fans out to every device; take only our reader.
            if (!string.Equals(reader, _readerName, StringComparison.OrdinalIgnoreCase))
                return;
            lock (_stateLock)
            {
                _pulseUntil = Environment.TickCount64 + PulseMs;
            }
        }

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            lock (_stateLock)
            {
                long now = Environment.TickCount64;
                bool pressed = _pulseUntil != 0 && now < _pulseUntil;
                if (!pressed) _pulseUntil = 0;

                var s = _state.Clone();
                s.Buttons[AnyTagButton] = pressed;
                _state = s;
                return s.Clone();
            }
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
