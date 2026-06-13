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
    /// The device listens to the ENTIRE MIDI namespace: all 128 notes, all
    /// 128 CCs, and pitch bend, channel-merged (a note or CC means the same
    /// thing whatever channel it arrives on). There is nothing to configure
    /// — a MIDI device is already defined. The live state lands in
    /// <see cref="CustomInputState.Midi"/> (a dedicated sub-state, not the
    /// gamepad axis/button arrays), and mapping descriptors of the form
    /// "Midi Note N" / "Midi CC N" / "Midi Pitch Bend" resolve against it.
    ///
    /// State is written by the WinRT message callback and read by the
    /// polling thread, with the same clone-under-lock discipline as
    /// <see cref="WebControllerDevice"/>.
    /// </summary>
    internal sealed class MidiInputDevice : ISdlInputDevice
    {
        private const ushort MidiVendorId = 0x4D49;  // "MI"
        private const ushort MidiProductId = 0x4D44; // "MD"

        private readonly object _stateLock = new();
        private CustomInputState _state;
        private volatile bool _attached;

        private readonly string _endpointId;
        private MidiEndpointConnection _connection;

        public MidiInputDevice(string endpointId, string name)
        {
            _endpointId = endpointId;
            Name = name;
            DevicePath = $"midi://{endpointId}";
            InstanceGuid = Md5Guid("pfmidi-in:" + endpointId);
            ProductGuid = Md5Guid("pfmidi-in-product:" + name);
            SdlInstanceId = unchecked((uint)endpointId.GetHashCode());

            var state = new CustomInputState { Midi = new MidiInputState() };
            _state = state;
        }

        // ─────────────────────────────────────────────
        //  ISdlInputDevice identity / capabilities
        // ─────────────────────────────────────────────

        // The MIDI surface lives entirely in CustomInputState.Midi, not in
        // the gamepad Axis[]/Buttons[] arrays, so the device reports zero of
        // those. Its mappable controls come from MappingDisplayResolver's
        // MIDI block and resolve through the "Midi ..." descriptor family.
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => 0;
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

        // GetDeviceObjects is empty: the mapping picker lists MIDI controls
        // through MappingDisplayResolver's dedicated MIDI block (the touchpad
        // pattern), not through the generic Axis/Button enumeration.
        public DeviceObjectItem[] GetDeviceObjects() => Array.Empty<DeviceObjectItem>();

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
        //  Message parsing (UMP) — full namespace, omni
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
                    int data1 = (int)((w0 >> 8) & 0x7F);
                    int data2 = (int)(w0 & 0x7F);
                    switch (opcode)
                    {
                        case 0x9: SetNote(data1, data2 != 0); break; // velocity 0 = NoteOff
                        case 0x8: SetNote(data1, false); break;
                        case 0xB: SetCc(data1, data2); break;
                        case 0xE: SetPitchBend(((data2 << 7) | data1) * 65535 / 16383); break;
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
                    int index = (int)((word0 >> 8) & 0x7F);
                    switch (opcode)
                    {
                        // MIDI 2.0 NoteOn velocity 0 is a valid note-on.
                        case 0x9: SetNote(index, true); break;
                        case 0x8: SetNote(index, false); break;
                        case 0xB: SetCc(index, (int)(word1 >> 25)); break; // 32-bit CC -> 7-bit
                        case 0xE: SetPitchBend((int)(word1 >> 16)); break; // 32-bit -> 16-bit
                    }
                }
            }
            catch
            {
                // A malformed packet must never take down the WinRT
                // callback thread; drop it.
            }
        }

        private void SetNote(int note, bool down)
        {
            if (note < 0 || note >= MidiInputState.NoteCount) return;
            lock (_stateLock)
            {
                var s = _state.Clone();
                s.Midi.Notes[note] = down;
                _state = s;
            }
        }

        private void SetCc(int cc, int value7)
        {
            if (cc < 0 || cc >= MidiInputState.CcCount) return;
            lock (_stateLock)
            {
                var s = _state.Clone();
                s.Midi.Cc[cc] = (byte)Math.Clamp(value7, 0, 127);
                _state = s;
            }
        }

        private void SetPitchBend(int scaled)
        {
            lock (_stateLock)
            {
                var s = _state.Clone();
                s.Midi.PitchBend = Math.Clamp(scaled, 0, 65535);
                _state = s;
            }
        }

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            lock (_stateLock) return _state.Clone();
        }

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
