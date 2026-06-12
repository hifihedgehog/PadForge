using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.Devices.Midi2;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// A hardware (or virtual) MIDI input endpoint exposed to the input
    /// pipeline as a standard <see cref="ISdlInputDevice"/> — the symmetric
    /// counterpart of <see cref="MidiVirtualController"/> (issue #128).
    ///
    /// The exposed surface mirrors the MIDI output config philosophy:
    /// explicit windows, nothing else. Per device (persisted on
    /// <see cref="UserDevice"/>, editable on the Devices page):
    ///   Channel    — 1-16 listens to one channel, 0 = all (omni)
    ///   Start CC / CC Count     — which CCs become axes
    ///   Start Note / Note Count — which notes become buttons
    ///   Pitch Bend — optional extra axis after the CC axes
    ///
    /// Published <see cref="CustomInputState"/> layout:
    ///   Axis[0 .. CcCount-1]   — windowed CCs, absolute value (0-65535)
    ///   Axis[CcCount]          — pitch bend, only when enabled
    ///   Buttons[0 .. NoteCount-1]          — windowed notes
    ///   Buttons[NoteCount + 2w], [.. + 1]  — "CC n +" / "CC n −" pulse
    ///                                        buttons per windowed CC
    ///
    /// Every windowed CC drives BOTH its absolute axis and its relative
    /// pulse buttons. Absolute faders and knobs map the axis; endless
    /// encoders (which send two's-complement deltas around 0x40) map the
    /// pulse buttons. The unused surface stays unmapped and idle.
    ///
    /// State is written by the WinRT message callback and read by the
    /// polling thread, with the same clone-under-lock discipline as
    /// <see cref="WebControllerDevice"/>.
    /// </summary>
    internal sealed class MidiInputDevice : ISdlInputDevice
    {
        // Encoder pulse shaping. A detent presses its pulse button for
        // PulseOnMs then releases for PulseGapMs before the next queued
        // detent fires — long enough for a game polling at 60 Hz to see
        // every press, short enough for ~40 detents/second throughput.
        private const int PulseOnMs = 15;
        private const int PulseGapMs = 10;
        private const int MaxPendingPulses = 64;

        private const ushort MidiVendorId = 0x4D49;  // "MI"
        private const ushort MidiProductId = 0x4D44; // "MD"

        private readonly object _stateLock = new();
        private CustomInputState _state;
        private int[] _pulsePending = Array.Empty<int>();
        private byte[] _pulsePhase = Array.Empty<byte>();  // 0 idle, 1 on, 2 gap
        private long[] _pulsePhaseUntil = Array.Empty<long>();
        private volatile bool _attached;

        private readonly string _endpointId;
        private MidiEndpointConnection _connection;

        // Window settings — see ApplySettings for clamping rules. All
        // reads in the message callback happen under _stateLock so a
        // reconfiguration never tears mid-message.
        private int _channelFilter;   // -1 omni, 0-15 single channel
        private int _startCc = 1;
        private int _ccCount = 6;
        private int _startNote = 60;
        private int _noteCount = 11;
        private bool _pitchBend;

        public MidiInputDevice(string endpointId, string name)
        {
            _endpointId = endpointId;
            Name = name;
            DevicePath = $"midi://{endpointId}";
            InstanceGuid = Md5Guid("pfmidi-in:" + endpointId);
            ProductGuid = Md5Guid("pfmidi-in-product:" + name);
            SdlInstanceId = unchecked((uint)endpointId.GetHashCode());
            ApplySettings(1, 1, 6, 60, 11, false);
        }

        /// <summary>
        /// Applies the per-device window settings and resets the published
        /// state. Mirrors the MIDI output config vocabulary: counts are
        /// clamped so windows stay inside MIDI's 0-127 ranges and the CC
        /// axes (+ optional pitch bend) fit the engine's axis budget.
        /// </summary>
        /// <param name="channel">1-16 single channel, 0 = all (omni).</param>
        public void ApplySettings(int channel, int startCc, int ccCount,
            int startNote, int noteCount, bool pitchBend)
        {
            lock (_stateLock)
            {
                _channelFilter = channel >= 1 && channel <= 16 ? channel - 1 : -1;
                _pitchBend = pitchBend;

                int axisBudget = CustomInputState.MaxAxis - (pitchBend ? 1 : 0);
                _startCc = Math.Clamp(startCc, 0, 127);
                _ccCount = Math.Clamp(ccCount, 0, Math.Min(axisBudget, 128 - _startCc));

                _startNote = Math.Clamp(startNote, 0, 127);
                _noteCount = Math.Clamp(noteCount, 0, 128 - _startNote);

                // Buttons: notes + 2 pulse buttons per windowed CC.
                if (_noteCount + _ccCount * 2 > CustomInputState.MaxButtons)
                    _noteCount = CustomInputState.MaxButtons - _ccCount * 2;

                _pulsePending = new int[_ccCount * 2];
                _pulsePhase = new byte[_ccCount * 2];
                _pulsePhaseUntil = new long[_ccCount * 2];

                var state = new CustomInputState();
                if (_pitchBend)
                    state.Axis[_ccCount] = 32768; // pitch bend rests centered
                _state = state;
            }
        }

        // ─────────────────────────────────────────────
        //  ISdlInputDevice identity / capabilities
        // ─────────────────────────────────────────────

        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes { get { lock (_stateLock) return _ccCount + (_pitchBend ? 1 : 0); } }
        public int NumButtons { get { lock (_stateLock) return _noteCount + _ccCount * 2; } }
        public int RawButtonCount => NumButtons;
        public int NumHats => 0;
        public int[] SupportedButtonIndices
        {
            get
            {
                var dense = new int[NumButtons];
                for (int i = 0; i < dense.Length; i++) dense[i] = i;
                return dense;
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
        public ushort VendorId => MidiVendorId;
        public ushort ProductId => MidiProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.Midi;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        // ─────────────────────────────────────────────
        //  Connection lifecycle
        // ─────────────────────────────────────────────

        /// <summary>Opens the endpoint connection on the shared input
        /// session. Returns false when MIDI services are unavailable or
        /// the endpoint refuses the connection.</summary>
        public bool Open()
        {
            try
            {
                var session = MidiInputRuntime.Session;
                if (session == null) return false;

                _connection = session.CreateEndpointConnection(_endpointId);
                if (_connection == null) return false;

                _connection.MessageReceived += OnMessageReceived;
                if (!_connection.Open())
                {
                    _connection.MessageReceived -= OnMessageReceived;
                    _connection = null;
                    return false;
                }
                _attached = true;
                return true;
            }
            catch
            {
                _connection = null;
                return false;
            }
        }

        public void Dispose()
        {
            _attached = false;
            var conn = _connection;
            _connection = null;
            if (conn != null)
            {
                try { conn.MessageReceived -= OnMessageReceived; } catch { }
                MidiInputRuntime.Disconnect(conn);
            }
        }

        // ─────────────────────────────────────────────
        //  Message parsing (UMP)
        // ─────────────────────────────────────────────

        private void OnMessageReceived(IMidiMessageReceivedEventSource sender, MidiMessageReceivedEventArgs args)
        {
            try
            {
                uint w0 = args.PeekFirstWord();
                uint mt = w0 >> 28;

                if (mt == 0x2)
                {
                    // MIDI 1.0 channel voice (32-bit UMP).
                    int opcode = (int)((w0 >> 20) & 0xF);
                    int channel = (int)((w0 >> 16) & 0xF);
                    int data1 = (int)((w0 >> 8) & 0x7F);
                    int data2 = (int)(w0 & 0x7F);
                    switch (opcode)
                    {
                        case 0x9: NoteEdge(channel, data1, data2 != 0); break; // velocity 0 = NoteOff
                        case 0x8: NoteEdge(channel, data1, false); break;
                        case 0xB: ControlChange(channel, data1, data2 * 65535 / 127, data2 - 64); break;
                        case 0xE: SetPitchBend(channel, ((data2 << 7) | data1) * 65535 / 16383); break;
                    }
                }
                else if (mt == 0x4)
                {
                    // MIDI 2.0 channel voice (64-bit UMP).
                    var packet = args.GetMessagePacket();
                    if (packet is not MidiMessage64 m64) return;
                    uint word0 = m64.Word0;
                    uint word1 = m64.Word1;
                    int opcode = (int)((word0 >> 20) & 0xF);
                    int channel = (int)((word0 >> 16) & 0xF);
                    int index = (int)((word0 >> 8) & 0x7F);
                    switch (opcode)
                    {
                        // MIDI 2.0 NoteOn velocity 0 is a valid note-on (no
                        // NoteOff aliasing in the 2.0 protocol).
                        case 0x9: NoteEdge(channel, index, true); break;
                        case 0x8: NoteEdge(channel, index, false); break;
                        // Relative-encoder detection stays MIDI 1.0 only —
                        // 2.0-native controllers report absolute 32-bit data.
                        case 0xB: ControlChange(channel, index, (int)(word1 >> 16), 0); break;
                        case 0xE: SetPitchBend(channel, (int)(word1 >> 16)); break;
                    }
                }
            }
            catch
            {
                // A malformed packet must never take down the WinRT
                // callback thread; drop it.
            }
        }

        private void NoteEdge(int channel, int note, bool down)
        {
            lock (_stateLock)
            {
                if (_channelFilter >= 0 && channel != _channelFilter) return;
                int n = note - _startNote;
                if (n < 0 || n >= _noteCount) return;
                var s = _state.Clone();
                s.Buttons[n] = down;
                _state = s;
            }
        }

        private void ControlChange(int channel, int cc, int scaled, int relativeDelta)
        {
            lock (_stateLock)
            {
                if (_channelFilter >= 0 && channel != _channelFilter) return;
                int w = cc - _startCc;
                if (w < 0 || w >= _ccCount) return;
                var s = _state.Clone();
                s.Axis[w] = Math.Clamp(scaled, 0, 65535);
                _state = s;

                // Two's-complement relative interpretation: 0x41 = +1,
                // 0x3F = −1. Queue one pulse per detent, capped so an
                // absolute fader sweep (which also lands here) can't
                // build an unbounded backlog on the unmapped buttons.
                if (relativeDelta > 0)
                    _pulsePending[2 * w] = Math.Min(MaxPendingPulses, _pulsePending[2 * w] + relativeDelta);
                else if (relativeDelta < 0)
                    _pulsePending[2 * w + 1] = Math.Min(MaxPendingPulses, _pulsePending[2 * w + 1] - relativeDelta);
            }
        }

        private void SetPitchBend(int channel, int scaled)
        {
            lock (_stateLock)
            {
                if (_channelFilter >= 0 && channel != _channelFilter) return;
                if (!_pitchBend) return;
                var s = _state.Clone();
                s.Axis[_ccCount] = Math.Clamp(scaled, 0, 65535);
                _state = s;
            }
        }

        // ─────────────────────────────────────────────
        //  Polling
        // ─────────────────────────────────────────────

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            lock (_stateLock)
            {
                var s = _state.Clone();
                long now = Environment.TickCount64;
                for (int i = 0; i < _pulsePending.Length; i++)
                {
                    switch (_pulsePhase[i])
                    {
                        case 0: // idle
                            if (_pulsePending[i] > 0)
                            {
                                _pulsePhase[i] = 1;
                                _pulsePhaseUntil[i] = now + PulseOnMs;
                                s.Buttons[_noteCount + i] = true;
                            }
                            break;
                        case 1: // pressed
                            if (now < _pulsePhaseUntil[i])
                            {
                                s.Buttons[_noteCount + i] = true;
                            }
                            else
                            {
                                _pulsePhase[i] = 2;
                                _pulsePhaseUntil[i] = now + PulseGapMs;
                                _pulsePending[i]--;
                            }
                            break;
                        case 2: // release gap
                            if (now >= _pulsePhaseUntil[i])
                                _pulsePhase[i] = 0;
                            break;
                    }
                }
                return s;
            }
        }

        // ─────────────────────────────────────────────
        //  Mapping surface
        // ─────────────────────────────────────────────

        public DeviceObjectItem[] GetDeviceObjects()
        {
            int ccCount, startCc, noteCount, startNote;
            bool pitchBend;
            lock (_stateLock)
            {
                ccCount = _ccCount; startCc = _startCc;
                noteCount = _noteCount; startNote = _startNote;
                pitchBend = _pitchBend;
            }

            int numAxes = ccCount + (pitchBend ? 1 : 0);
            var items = new List<DeviceObjectItem>(numAxes + noteCount + ccCount * 2);
            var standardAxisGuids = new[]
            {
                ObjectGuid.XAxis, ObjectGuid.YAxis, ObjectGuid.ZAxis,
                ObjectGuid.RxAxis, ObjectGuid.RyAxis, ObjectGuid.RzAxis
            };

            for (int i = 0; i < numAxes; i++)
            {
                string name = i < ccCount
                    ? CcDisplayName(startCc + i)
                    : "Pitch Bend";
                items.Add(new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = i < standardAxisGuids.Length ? standardAxisGuids[i] : ObjectGuid.Slider,
                    Name = name,
                    ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                    Offset = i * 4,
                });
            }

            for (int n = 0; n < noteCount; n++)
            {
                int note = startNote + n;
                items.Add(new DeviceObjectItem
                {
                    InputIndex = n,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = $"Note {note} ({NoteDisplayName(note)})",
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (numAxes + n) * 4,
                });
            }

            for (int w = 0; w < ccCount; w++)
            {
                int cc = startCc + w;
                items.Add(new DeviceObjectItem
                {
                    InputIndex = noteCount + 2 * w,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = $"CC {cc} +",
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (numAxes + noteCount + 2 * w) * 4,
                });
                items.Add(new DeviceObjectItem
                {
                    InputIndex = noteCount + 2 * w + 1,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = $"CC {cc} −",
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (numAxes + noteCount + 2 * w + 1) * 4,
                });
            }

            return items.ToArray();
        }

        private static readonly string[] NoteLetters =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>Octave naming with middle C (note 60) = C4.</summary>
        private static string NoteDisplayName(int note) =>
            $"{NoteLetters[note % 12]}{note / 12 - 1}";

        private static readonly Dictionary<int, string> CcNames = new()
        {
            [1] = "Mod Wheel", [2] = "Breath", [4] = "Foot Pedal",
            [5] = "Portamento Time", [7] = "Channel Volume", [8] = "Balance",
            [10] = "Pan", [11] = "Expression", [12] = "Effect 1", [13] = "Effect 2",
            [16] = "General 1", [17] = "General 2", [18] = "General 3", [19] = "General 4",
            [64] = "Sustain", [65] = "Portamento", [66] = "Sostenuto",
            [67] = "Soft Pedal", [68] = "Legato", [69] = "Hold 2",
            [71] = "Resonance", [72] = "Release Time", [73] = "Attack Time",
            [74] = "Brightness", [91] = "Reverb", [93] = "Chorus",
        };

        private static string CcDisplayName(int cc) =>
            CcNames.TryGetValue(cc, out string n) ? $"CC {cc} ({n})" : $"CC {cc}";

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }

    /// <summary>
    /// Shared Windows MIDI Services session for input endpoints, plus the
    /// endpoint enumeration the device thread consumes. Rides the SDK
    /// runtime that <see cref="MidiVirtualController.IsAvailable"/>
    /// initializes; never initializes anything when MIDI services are
    /// absent.
    /// </summary>
    internal static class MidiInputRuntime
    {
        private static readonly object _lock = new();
        private static MidiSession _session;

        /// <summary>The shared input session, or null when Windows MIDI
        /// Services is unavailable.</summary>
        public static MidiSession Session
        {
            get
            {
                if (_session != null) return _session;
                if (!MidiVirtualController.IsAvailable()) return null;
                lock (_lock)
                {
                    _session ??= MidiSession.Create("PadForge MIDI Input");
                    return _session;
                }
            }
        }

        public static void Disconnect(MidiEndpointConnection connection)
        {
            try
            {
                var session = _session;
                if (session != null && connection != null)
                    session.DisconnectEndpointConnection(connection.ConnectionId);
            }
            catch { }
        }

        /// <summary>Enumerates connectable MIDI endpoints. Includes normal
        /// hardware endpoints AND virtual-device endpoints (PadForge's own
        /// MIDI virtual controllers among them — that is the no-hardware
        /// loopback test path); excludes the diagnostics endpoints and the
        /// in-box GM synth, which never produce input.</summary>
        public static List<(string Id, string Name)> EnumerateEndpoints()
        {
            var result = new List<(string Id, string Name)>();
            if (!MidiVirtualController.IsAvailable()) return result;
            try
            {
                var endpoints = MidiEndpointDeviceInformation.FindAll();
                if (endpoints == null) return result;
                foreach (var ep in endpoints)
                {
                    if (ep == null) continue;
                    var purpose = ep.EndpointPurpose;
                    if (purpose != MidiEndpointDevicePurpose.NormalMessageEndpoint
                        && purpose != MidiEndpointDevicePurpose.VirtualDeviceResponder)
                        continue;
                    string id = ep.EndpointDeviceId;
                    if (string.IsNullOrEmpty(id)) continue;
                    string name = ep.Name;
                    if (string.IsNullOrWhiteSpace(name)) name = "MIDI Endpoint";
                    result.Add((id, name));
                }
            }
            catch { }
            return result;
        }

        /// <summary>Tears down the shared session. Call on app exit, before
        /// <see cref="MidiVirtualController.Shutdown"/>.</summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                try { _session?.Dispose(); } catch { }
                _session = null;
            }
        }
    }
}
