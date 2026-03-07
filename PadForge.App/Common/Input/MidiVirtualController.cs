using System;
using PadForge.Engine;

using Microsoft.Windows.Devices.Midi2;
using Microsoft.Windows.Devices.Midi2.Endpoints.Virtual;
using Microsoft.Windows.Devices.Midi2.Messages;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Virtual controller that creates a Windows MIDI Services virtual device
    /// and sends MIDI 1.0 messages (CC for axes, Note On/Off for buttons).
    /// The device appears system-wide as a MIDI endpoint that DAWs and synths can connect to.
    /// Falls back gracefully on systems without Windows MIDI Services.
    /// </summary>
    internal sealed class MidiVirtualController : IVirtualController
    {
        private static bool? _isAvailable;
        private static readonly object _availLock = new();
        private static Microsoft.Windows.Devices.Midi2.Initialization.MidiDesktopAppSdkInitializer _initializer;

        private MidiSession _session;
        private MidiEndpointConnection _connection;
        private MidiVirtualDevice _virtualDevice;
        private bool _connected;
        private bool _disposed;

        private readonly int _padIndex;
        private readonly int _channel; // 0-15

        // Change detection — only send messages when values actually change.
        private byte[] _lastCcValues;
        private bool[] _lastNotes;

        // CC numbers for each CC slot (index → MIDI CC number).
        internal int[] CcNumbers { get; set; } = { 1, 2, 3, 4, 5, 6 };

        // Note numbers for each note slot (index → MIDI note number).
        internal int[] NoteNumbers { get; set; } = { 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70 };

        // Note velocity for button presses.
        internal byte Velocity { get; set; } = 127;

        public VirtualControllerType Type => VirtualControllerType.Midi;
        public bool IsConnected => _connected;

        public MidiVirtualController(int padIndex, int channel)
        {
            _padIndex = padIndex;
            _channel = Math.Clamp(channel, 0, 15);
        }

        public void Connect()
        {
            if (_connected) return;

            var deviceName = $"PadForge MIDI {_padIndex + 1}";

            // Define the virtual device.
            var declaredEndpointInfo = new MidiDeclaredEndpointInfo();
            declaredEndpointInfo.Name = deviceName;
            declaredEndpointInfo.ProductInstanceId = $"PADFORGE_MIDI_{_padIndex}";
            declaredEndpointInfo.SpecificationVersionMajor = 1;
            declaredEndpointInfo.SpecificationVersionMinor = 1;
            declaredEndpointInfo.SupportsMidi10Protocol = true;
            declaredEndpointInfo.SupportsMidi20Protocol = false;
            declaredEndpointInfo.SupportsReceivingJitterReductionTimestamps = false;
            declaredEndpointInfo.SupportsSendingJitterReductionTimestamps = false;
            declaredEndpointInfo.HasStaticFunctionBlocks = true;

            var declaredDeviceIdentity = new MidiDeclaredDeviceIdentity();

            var userSuppliedInfo = new MidiEndpointUserSuppliedInfo();
            userSuppliedInfo.Name = deviceName;
            userSuppliedInfo.Description = $"PadForge virtual MIDI controller (slot {_padIndex + 1})";

            var config = new MidiVirtualDeviceCreationConfig(
                deviceName,
                "Virtual MIDI controller from PadForge",
                "PadForge",
                declaredEndpointInfo,
                declaredDeviceIdentity,
                userSuppliedInfo
            );

            // Single function block for MIDI 1.0 output.
            var block = new MidiFunctionBlock();
            block.Number = 0;
            block.Name = "Controller Output";
            block.IsActive = true;
            block.UIHint = MidiFunctionBlockUIHint.Sender;
            block.FirstGroup = new MidiGroup(0);
            block.GroupCount = 1;
            block.Direction = MidiFunctionBlockDirection.Bidirectional;
            block.RepresentsMidi10Connection = MidiFunctionBlockRepresentsMidi10Connection.YesBandwidthUnrestricted;
            block.MaxSystemExclusive8Streams = 0;
            block.MidiCIMessageVersionFormat = 0;
            config.FunctionBlocks.Add(block);

            _session = MidiSession.Create(deviceName);
            if (_session == null)
                throw new InvalidOperationException("Failed to create MIDI session.");

            _virtualDevice = MidiVirtualDeviceManager.CreateVirtualDevice(config);
            if (_virtualDevice == null)
            {
                _session.Dispose();
                _session = null;
                throw new InvalidOperationException("Failed to create virtual MIDI device.");
            }

            _virtualDevice.SuppressHandledMessages = true;

            _connection = _session.CreateEndpointConnection(_virtualDevice.DeviceEndpointDeviceId);
            if (_connection == null)
            {
                _session.Dispose();
                _session = null;
                throw new InvalidOperationException("Failed to create MIDI endpoint connection.");
            }

            _connection.AddMessageProcessingPlugin(_virtualDevice);

            if (!_connection.Open())
            {
                _session.Dispose();
                _session = null;
                throw new InvalidOperationException("Failed to open MIDI endpoint connection.");
            }

            _connected = true;

            // Initialize change detection arrays sized to match configured CC/note counts.
            _lastCcValues = new byte[CcNumbers.Length];
            for (int i = 0; i < _lastCcValues.Length; i++)
                _lastCcValues[i] = 64; // center for axes
            _lastNotes = new bool[NoteNumbers.Length];
        }

        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;

            // Send Note Off for any held notes.
            if (_connection != null && _lastNotes != null)
            {
                for (int i = 0; i < _lastNotes.Length && i < NoteNumbers.Length; i++)
                {
                    if (_lastNotes[i])
                        SendNoteOff(NoteNumbers[i]);
                }
            }
            _lastNotes = null;

            if (_connection != null && _session != null)
            {
                _session.DisconnectEndpointConnection(_connection.ConnectionId);
                _connection = null;
            }

            _session?.Dispose();
            _session = null;
            _virtualDevice = null;
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            // Legacy path — not used for dynamic MIDI. Kept for IVirtualController interface.
        }

        /// <summary>
        /// Sends MIDI messages from a MidiRawState with arbitrary CC and note counts.
        /// Only sends messages when values change (change detection per CC and per note).
        /// </summary>
        public void SubmitMidiRawState(MidiRawState state)
        {
            if (!_connected || _connection == null) return;

            // CCs
            if (state.CcValues != null && _lastCcValues != null)
            {
                int ccCount = Math.Min(state.CcValues.Length, Math.Min(_lastCcValues.Length, CcNumbers.Length));
                for (int i = 0; i < ccCount; i++)
                {
                    if (state.CcValues[i] != _lastCcValues[i])
                    {
                        SendCC(CcNumbers[i], state.CcValues[i]);
                        _lastCcValues[i] = state.CcValues[i];
                    }
                }
            }

            // Notes
            if (state.Notes != null && _lastNotes != null)
            {
                int noteCount = Math.Min(state.Notes.Length, Math.Min(_lastNotes.Length, NoteNumbers.Length));
                for (int i = 0; i < noteCount; i++)
                {
                    if (state.Notes[i] != _lastNotes[i])
                    {
                        if (state.Notes[i])
                            SendNoteOn(NoteNumbers[i], Velocity);
                        else
                            SendNoteOff(NoteNumbers[i]);
                        _lastNotes[i] = state.Notes[i];
                    }
                }
            }
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            // MIDI has no rumble feedback — no-op.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        // ─────────────────────────────────────────────
        //  MIDI message helpers (MIDI 1.0 via UMP)
        // ─────────────────────────────────────────────

        private void SendCC(int ccNumber, byte value)
        {
            var msg = MidiMessageBuilder.BuildMidi1ChannelVoiceMessage(
                0,
                new MidiGroup(0),
                Midi1ChannelVoiceMessageStatus.ControlChange,
                new MidiChannel((byte)_channel),
                (byte)ccNumber,
                value);
            _connection.SendSingleMessagePacket(msg);
        }

        private void SendNoteOn(int note, byte velocity)
        {
            var msg = MidiMessageBuilder.BuildMidi1ChannelVoiceMessage(
                0,
                new MidiGroup(0),
                Midi1ChannelVoiceMessageStatus.NoteOn,
                new MidiChannel((byte)_channel),
                (byte)note,
                velocity);
            _connection.SendSingleMessagePacket(msg);
        }

        private void SendNoteOff(int note)
        {
            var msg = MidiMessageBuilder.BuildMidi1ChannelVoiceMessage(
                0,
                new MidiGroup(0),
                Midi1ChannelVoiceMessageStatus.NoteOff,
                new MidiChannel((byte)_channel),
                (byte)note,
                0);
            _connection.SendSingleMessagePacket(msg);
        }

        // ─────────────────────────────────────────────
        //  Static availability check
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns true if Windows MIDI Services is available on this system.
        /// Caches the result after first check.
        /// </summary>
        public static bool IsAvailable()
        {
            if (_isAvailable.HasValue) return _isAvailable.Value;

            lock (_availLock)
            {
                if (_isAvailable.HasValue) return _isAvailable.Value;

                try
                {
                    _initializer = Microsoft.Windows.Devices.Midi2.Initialization.MidiDesktopAppSdkInitializer.Create();
                    if (!_initializer.InitializeSdkRuntime())
                    {
                        _initializer.Dispose();
                        _initializer = null;
                        _isAvailable = false;
                        return false;
                    }
                    if (!_initializer.EnsureServiceAvailable())
                    {
                        _initializer.Dispose();
                        _initializer = null;
                        _isAvailable = false;
                        return false;
                    }
                    _isAvailable = true;
                    return true;
                }
                catch
                {
                    _isAvailable = false;
                    return false;
                }
            }
        }

        /// <summary>
        /// Resets the cached availability check so the next call to IsAvailable()
        /// re-evaluates. Call after installing MIDI Services.
        /// </summary>
        public static void ResetAvailability()
        {
            lock (_availLock)
            {
                if (_initializer != null)
                {
                    _initializer.Dispose();
                    _initializer = null;
                }
                _isAvailable = null;
            }
        }

        /// <summary>
        /// Shuts down the MIDI Services SDK initializer. Call on app exit.
        /// </summary>
        public static void Shutdown()
        {
            if (_initializer != null)
            {
                _initializer.Dispose();
                _initializer = null;
            }
        }
    }
}
