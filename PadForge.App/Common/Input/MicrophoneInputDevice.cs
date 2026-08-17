using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// A standalone Windows capture endpoint exposed to the input pipeline
    /// as a standard <see cref="ISdlInputDevice"/> (issue #317). The
    /// microphone is real hardware, so it earns a device row exactly the way
    /// MIDI ports and PC/SC readers do, and its buttons are the registered
    /// voice phrases: button 0 is "Any Phrase", each phrase sits at its
    /// stable registry index. Saying a phrase into THIS microphone pulses
    /// THIS device's buttons.
    ///
    /// Microphone-bearing controllers do not appear here: a DualSense
    /// carries its phrases on its own device through the engine augment
    /// hook, and the sweep skips endpoints whose container belongs to such
    /// a pad so one microphone never shows up twice.
    /// </summary>
    internal sealed class MicrophoneInputDevice : ISdlInputDevice
    {
        private const ushort MicVendorId = 0x4D49;  // "MI"
        private const ushort MicProductId = 0x4350; // "CP"

        /// <summary>Same pulse contract as the NFC lane: one 60 Hz macro
        /// poll frame must land inside it for a clean single OnPress.</summary>
        private const int PulseMs = 175;

        private const int AnyPhraseButton = 0;

        // Open devices by endpoint ID, so the recognition service can pulse
        // the device that owns the session and the sweep can enumerate.
        private static readonly ConcurrentDictionary<string, MicrophoneInputDevice> _open =
            new(StringComparer.Ordinal);

        private readonly object _stateLock = new();
        private CustomInputState _state;
        private volatile bool _attached;
        private readonly long[] _pulseUntil = new long[CustomInputState.MaxButtons];

        public string EndpointId { get; }

        public MicrophoneInputDevice(string endpointId, string friendlyName)
        {
            EndpointId = endpointId;
            Name = friendlyName;
            DevicePath = "mic://" + endpointId;
            InstanceGuid = Md5Guid("pfmic:" + endpointId);
            ProductGuid = Md5Guid("pfmic-product:" + endpointId);
            SdlInstanceId = SyntheticInstanceId.From("mic:" + endpointId);
            _state = new CustomInputState();
        }

        // Span the RANGE of stable registry buttons, not the count, so
        // removing a middle phrase leaves a gap instead of renumbering.
        private static int ButtonSpan => 1 + VoicePhraseRegistry.MaxButtonInUse;

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => ButtonSpan;
        public int RawButtonCount => ButtonSpan;
        public int NumHats => 0;
        public int[] SupportedButtonIndices
        {
            get
            {
                var list = new List<int> { AnyPhraseButton };
                foreach (var p in VoicePhraseRegistry.Phrases) list.Add(p.Button);
                return list.ToArray();
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
        public ushort VendorId => MicVendorId;
        public ushort ProductId => MicProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.Microphone;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        public DeviceObjectItem[] GetDeviceObjects()
        {
            var phrases = VoicePhraseRegistry.Phrases;
            var items = new DeviceObjectItem[1 + phrases.Count];
            items[0] = new DeviceObjectItem
            {
                Name = Resources.Strings.Strings.Instance.Voice_AnyPhrase,
                ObjectType = DeviceObjectTypeFlags.PushButton,
                ObjectTypeGuid = ObjectGuid.Button,
                InputIndex = AnyPhraseButton,
            };
            for (int i = 0; i < phrases.Count; i++)
                items[i + 1] = new DeviceObjectItem
                {
                    Name = phrases[i].Name,
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    ObjectTypeGuid = ObjectGuid.Button,
                    InputIndex = phrases[i].Button, // stable per-phrase button
                };
            return items;
        }

        // ─── Lifecycle ───

        public bool Open()
        {
            _attached = true;
            _open[EndpointId] = this;
            return true;
        }

        public void Dispose()
        {
            _attached = false;
            _open.TryRemove(EndpointId, out _);
        }

        /// <summary>Recognition landed on this endpoint's session: pulse the
        /// Any Phrase button and, when registered, the phrase's own.</summary>
        public static void StampPulse(string endpointId, int registryButton)
        {
            if (!_open.TryGetValue(endpointId, out var dev)) return;
            long until = Environment.TickCount64 + PulseMs;
            lock (dev._stateLock)
            {
                dev._pulseUntil[AnyPhraseButton] = until;
                if (registryButton > 0 && registryButton < dev._pulseUntil.Length)
                    dev._pulseUntil[registryButton] = until;
            }
        }

        /// <summary>Endpoint IDs of every open microphone device, for the
        /// recognition service's session reconciliation.</summary>
        public static List<(string EndpointId, Guid InstanceGuid)> OpenEndpoints()
        {
            var list = new List<(string, Guid)>();
            foreach (var kv in _open) list.Add((kv.Key, kv.Value.InstanceGuid));
            return list;
        }

        private PadForge.Engine.PooledInputStatePair _statePool;

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            lock (_stateLock)
            {
                long now = Environment.TickCount64;
                var s = _statePool.Next();
                _state.CopyInto(s);
                for (int b = 0; b < _pulseUntil.Length; b++)
                {
                    long until = _pulseUntil[b];
                    bool pressed = until != 0 && now < until;
                    // Rewrite EVERY poll, clear on expiry: the NFC lane's
                    // latch lesson, verbatim.
                    if (!pressed) _pulseUntil[b] = 0;
                    s.Buttons[b] = pressed;
                }
                return s;
            }
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
