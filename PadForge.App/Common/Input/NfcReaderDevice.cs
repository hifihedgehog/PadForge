using System;
using System.Collections.Generic;
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
    /// Buttons: raw button 0 is "Any NFC Tag" (any ISO 14443 tag pulses it).
    /// Each tag registered in <see cref="NfcTagRegistry"/> is its own button:
    /// the n-th registered tag is raw button n (1-based), so a specific tag
    /// fires only the macros bound to that tag. On a tap the reader pulses the
    /// any-tag button AND, when the UID is registered, that tag's button, for
    /// <see cref="PulseMs"/> -- long enough for a 60 Hz macro poll to catch the
    /// rising edge. The macro evaluator is untouched: it reads
    /// <c>CustomInputState.Buttons[n]</c> through <c>CheckRawButtonTrigger</c>
    /// exactly as for any other device assigned to the slot.
    ///
    /// The tag set is read live from <see cref="NfcTagRegistry"/>, so registering
    /// or removing a tag changes the exposed buttons immediately; the picker is
    /// refreshed off <see cref="NfcTagRegistry.RegistryChanged"/> by InputService.
    /// </summary>
    internal sealed class NfcReaderDevice : ISdlInputDevice
    {
        private const ushort NfcVendorId = 0x4E46;  // "NF"
        private const ushort NfcProductId = 0x4350; // "CP"

        /// <summary>Tag-button hold time. One frame of a 60 Hz (16.7 ms)
        /// macro poll must land inside it; 175 ms gives a clean rising-then-
        /// falling edge and one OnPress per tap at any realistic poll rate.</summary>
        private const int PulseMs = 175;

        // Raw button 0 = "Any NFC Tag". Registered tags are buttons 1..N.
        private const int AnyTagButton = 0;

        private readonly object _stateLock = new();
        private CustomInputState _state;
        private volatile bool _attached;

        // Per-button pulse expiry (TickCount64); index 0 = any-tag, 1..N = tags.
        // Sized to the engine's button capacity so a large tag set never overflows.
        private readonly long[] _pulseUntil = new long[CustomInputState.MaxButtons];

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

        // Button count is the any-tag button plus one per registered tag, read
        // live so a newly registered tag is bindable without re-enumerating.
        private static int ButtonCount => 1 + NfcTagRegistry.Count;

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => ButtonCount;
        public int RawButtonCount => ButtonCount;
        public int NumHats => 0;
        public int[] SupportedButtonIndices
        {
            get
            {
                int n = ButtonCount;
                var a = new int[n];
                for (int i = 0; i < n; i++) a[i] = i;
                return a;
            }
        }
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

        // The mapping picker lists "Any NFC Tag" plus every registered tag by its
        // chosen name, each a bindable button whose InputIndex is the raw button.
        public DeviceObjectItem[] GetDeviceObjects()
        {
            var tags = NfcTagRegistry.Tags;
            var items = new DeviceObjectItem[1 + tags.Count];
            items[0] = new DeviceObjectItem
            {
                Name = "Any NFC Tag",
                ObjectType = DeviceObjectTypeFlags.PushButton,
                ObjectTypeGuid = ObjectGuid.Button,
                InputIndex = AnyTagButton,
            };
            for (int i = 0; i < tags.Count; i++)
                items[i + 1] = new DeviceObjectItem
                {
                    Name = tags[i].Name,
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    ObjectTypeGuid = ObjectGuid.Button,
                    InputIndex = i + 1,
                };
            return items;
        }

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
            long until = Environment.TickCount64 + PulseMs;
            int tagButton = NfcTagRegistry.ButtonForUid(uid); // -1 when unregistered
            lock (_stateLock)
            {
                _pulseUntil[AnyTagButton] = until;            // any tap fires "Any NFC Tag"
                if (tagButton > 0 && tagButton < _pulseUntil.Length)
                    _pulseUntil[tagButton] = until;           // ...and the specific tag, if registered
            }
        }

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            lock (_stateLock)
            {
                long now = Environment.TickCount64;
                var s = _state.Clone();
                for (int b = 0; b < _pulseUntil.Length; b++)
                {
                    long until = _pulseUntil[b];
                    if (until == 0) continue;
                    if (now < until) s.Buttons[b] = true;
                    else _pulseUntil[b] = 0; // expired
                }
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
