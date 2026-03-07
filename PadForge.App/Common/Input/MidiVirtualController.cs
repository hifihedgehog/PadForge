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
        private readonly byte[] _lastCcValues = new byte[6]; // LX, LY, LT, RX, RY, RT
        private ushort _lastButtons;

        // Default CC mappings: CC 1-6 for the 6 analog axes.
        internal int[] CcNumbers { get; set; } = { 1, 2, 3, 4, 5, 6 };

        // Default note mappings: Note 60-70 for 11 buttons.
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

            // Initialize change detection to neutral values.
            for (int i = 0; i < _lastCcValues.Length; i++)
                _lastCcValues[i] = 64; // center for axes
            _lastButtons = 0;
        }

        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;

            // Send Note Off for any held buttons.
            if (_connection != null)
            {
                for (int i = 0; i < 11 && i < NoteNumbers.Length; i++)
                {
                    if ((_lastButtons & (1 << i)) != 0)
                        SendNoteOff(NoteNumbers[i]);
                }
            }
            _lastButtons = 0;

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
            if (!_connected || _connection == null) return;

            // Axes → CC messages (convert to 0-127 range)
            byte lx = AxisToMidi(gp.ThumbLX);
            byte ly = AxisToMidi(gp.ThumbLY);
            byte lt = TriggerToMidi(gp.LeftTrigger);
            byte rx = AxisToMidi(gp.ThumbRX);
            byte ry = AxisToMidi(gp.ThumbRY);
            byte rt = TriggerToMidi(gp.RightTrigger);

            byte[] ccValues = { lx, ly, lt, rx, ry, rt };
            for (int i = 0; i < 6 && i < CcNumbers.Length; i++)
            {
                if (ccValues[i] != _lastCcValues[i])
                {
                    SendCC(CcNumbers[i], ccValues[i]);
                    _lastCcValues[i] = ccValues[i];
                }
            }

            // Buttons → Note On/Off
            ushort buttons = gp.Buttons;
            ushort changed = (ushort)(buttons ^ _lastButtons);
            for (int i = 0; i < 11 && i < NoteNumbers.Length; i++)
            {
                ushort mask = (ushort)(1 << i);
                if ((changed & mask) == 0) continue;

                if ((buttons & mask) != 0)
                    SendNoteOn(NoteNumbers[i], Velocity);
                else
                    SendNoteOff(NoteNumbers[i]);
            }
            _lastButtons = buttons;
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
        //  Value conversion
        // ─────────────────────────────────────────────

        private static byte AxisToMidi(short value)
        {
            // -32768..32767 → 0..127
            return (byte)((value + 32768) * 127 / 65535);
        }

        private static byte TriggerToMidi(ushort value)
        {
            // 0..65535 → 0..127
            return (byte)(value * 127 / 65535);
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
