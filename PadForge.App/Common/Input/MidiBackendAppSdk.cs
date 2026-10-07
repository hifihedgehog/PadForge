using System;
using System.Collections.Generic;
using Microsoft.Windows.Devices.Midi2;
using Microsoft.Windows.Devices.Midi2.Endpoints.Virtual;
using Microsoft.Windows.Devices.Midi2.Messages;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The older Windows MIDI Services App SDK runtime
    /// (Microsoft.Windows.Devices.Midi2 1.0.16-rc.3.7), for PCs that still
    /// have Microsoft's "Windows MIDI Services Runtime and Tools" install.
    /// It is the only path on Windows 11 24H2, which the in-box API does not
    /// cover. Microsoft deleted its installers on 2026-10-01, so it serves
    /// existing installs only. Remove it once the owner ends 24H2 MIDI
    /// support.
    ///
    /// <para>This is the code MidiVirtualController and MidiInputDevice ran
    /// before the in-box port, moved here unchanged.</para>
    /// </summary>
    internal sealed class MidiBackendAppSdk : IMidiBackend
    {
        private Microsoft.Windows.Devices.Midi2.Initialization.MidiDesktopAppSdkInitializer _initializer;

        public MidiApiKind Kind => MidiApiKind.AppSdk;

        public bool Start()
        {
            _initializer = Microsoft.Windows.Devices.Midi2.Initialization.MidiDesktopAppSdkInitializer.Create();
            if (!_initializer.InitializeSdkRuntime())
            {
                _initializer.Dispose();
                _initializer = null;
                return false;
            }
            if (!_initializer.EnsureServiceAvailable())
            {
                _initializer.Dispose();
                _initializer = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Dispose is what calls ShutdownSdkRuntime and lets go of the SDK's
        /// native dlls. <paramref name="skipDispose"/> abandons the
        /// initializer instead, for teardown while the service may ALREADY be
        /// mid-removal (app exit racing an external uninstall): Dispose()
        /// calls into the runtime and crashes if the service is going away
        /// under it.
        /// </summary>
        public void Stop(bool skipDispose)
        {
            if (_initializer != null)
            {
                if (!skipDispose)
                    try { _initializer.Dispose(); } catch { }
                _initializer = null;
            }
        }

        public IMidiVirtualEndpoint CreateVirtualEndpoint(string deviceName, string uniqueId, int padIndex)
        {
            // Define the virtual device.
            var declaredEndpointInfo = new MidiDeclaredEndpointInfo();
            declaredEndpointInfo.Name = deviceName;
            declaredEndpointInfo.ProductInstanceId = uniqueId;
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
            userSuppliedInfo.Description = $"PadForge virtual MIDI controller (slot {padIndex + 1})";

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

            MidiSession session = null;
            MidiEndpointConnection connection = null;
            try
            {
                session = MidiSession.Create(deviceName);
                if (session == null)
                    throw new MidiSessionUnavailableException();

                var virtualDevice = MidiVirtualDeviceManager.CreateVirtualDevice(config);
                if (virtualDevice == null)
                    throw new InvalidOperationException("Failed to create virtual MIDI device.");

                virtualDevice.SuppressHandledMessages = true;

                connection = session.CreateEndpointConnection(virtualDevice.DeviceEndpointDeviceId);
                if (connection == null)
                    throw new InvalidOperationException("Failed to create MIDI endpoint connection.");

                connection.AddMessageProcessingPlugin(virtualDevice);

                if (!connection.Open())
                    throw new InvalidOperationException("Failed to open MIDI endpoint connection.");

                return new Endpoint(session, virtualDevice, connection);
            }
            catch
            {
                // Creation failed partway: tear down what was built. The
                // caller unregisters the endpoint and schedules the janitor
                // for whatever the service stranded.
                try
                {
                    if (connection != null && session != null)
                        session.DisconnectEndpointConnection(connection.ConnectionId);
                }
                catch { /* best effort */ }
                try { session?.Dispose(); } catch { /* best effort */ }
                throw;
            }
        }

        private sealed class Endpoint : IMidiVirtualEndpoint
        {
            private readonly MidiSession _session;
            // Held for the endpoint's life, as the connection's message
            // processing plugin.
            private readonly MidiVirtualDevice _virtualDevice;
            private readonly MidiEndpointConnection _connection;

            public Endpoint(MidiSession session, MidiVirtualDevice virtualDevice, MidiEndpointConnection connection)
            {
                _session = session;
                _virtualDevice = virtualDevice;
                _connection = connection;
            }

            public void Send(Midi1Status status, int channel, int data1, int data2)
            {
                var msg = MidiMessageBuilder.BuildMidi1ChannelVoiceMessage(
                    0,
                    new MidiGroup(0),
                    (Midi1ChannelVoiceMessageStatus)(byte)status,
                    new MidiChannel((byte)channel),
                    (byte)data1,
                    (byte)data2);
                _connection.SendSingleMessagePacket(msg);
            }

            public void DisconnectConnection() => _session.DisconnectEndpointConnection(_connection.ConnectionId);

            public void CloseSession()
            {
                _session.Dispose();
                GC.KeepAlive(_virtualDevice);
            }
        }

        public IMidiInputSession CreateInputSession(string name)
        {
            var session = MidiSession.Create(name);
            return session == null ? null : new InputSession(session);
        }

        private sealed class InputSession : IMidiInputSession
        {
            private readonly MidiSession _session;

            public InputSession(MidiSession session) => _session = session;

            public IMidiInputConnection CreateConnection(string endpointId, Action<uint, uint> onWords)
            {
                var connection = _session.CreateEndpointConnection(endpointId);
                return connection == null ? null : new InputConnection(_session, connection, onWords);
            }

            public void Dispose() => _session.Dispose();
        }

        private sealed class InputConnection : IMidiInputConnection
        {
            private readonly MidiSession _session;
            private readonly MidiEndpointConnection _connection;
            private readonly Action<uint, uint> _onWords;

            public InputConnection(MidiSession session, MidiEndpointConnection connection, Action<uint, uint> onWords)
            {
                _session = session;
                _connection = connection;
                _onWords = onWords;
                _connection.MessageReceived += OnMessageReceived;
            }

            public bool Open() => _connection.Open();

            public void Detach() => _connection.MessageReceived -= OnMessageReceived;

            public void Disconnect() => _session.DisconnectEndpointConnection(_connection.ConnectionId);

            private void OnMessageReceived(IMidiMessageReceivedEventSource sender, MidiMessageReceivedEventArgs args)
            {
                try
                {
                    uint w0 = args.PeekFirstWord();
                    if (w0 >> 28 == 0x4)
                    {
                        // MIDI 2.0 channel voice: the second word carries
                        // the value.
                        if (args.GetMessagePacket() is not MidiMessage64 m64) return;
                        _onWords(m64.Word0, m64.Word1);
                        return;
                    }
                    _onWords(w0, 0);
                }
                catch
                {
                    // A malformed packet must never take down the WinRT
                    // callback thread. Drop it.
                }
            }
        }

        public List<(string Id, string Name)> EnumerateNormalEndpoints()
        {
            var result = new List<(string Id, string Name)>();
            // A throw partway keeps what was read before it, as the
            // enumeration in MidiInputRuntime always has.
            try
            {
                var endpoints = MidiEndpointDeviceInformation.FindAll();
                if (endpoints == null) return result;
                foreach (var ep in endpoints)
                {
                    if (ep == null) continue;
                    var purpose = ep.EndpointPurpose;
                    if (purpose != MidiEndpointDevicePurpose.NormalMessageEndpoint)
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
    }
}
