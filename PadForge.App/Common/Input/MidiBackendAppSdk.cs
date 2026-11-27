using System;
using System.Collections.Generic;
using Microsoft.Windows.Devices.Midi2;
using Microsoft.Windows.Devices.Midi2.Endpoints.Virtual;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The Windows MIDI Services App SDK runtime, through the projection
    /// compiled against Microsoft.Windows.Devices.Midi2 1.0.16-rc.3.7. The
    /// runtime is Microsoft's own install where a PC still has one, or
    /// PadForge's build of 1.0.17-rc.4.25 from
    /// hifihedgehog/PadForge-MIDI-Runtime, which the Settings card installs.
    /// It is the Windows MIDI Services path on Windows 11 24H2, which the
    /// in-box API does not cover, and on 25H2 until Windows registers the
    /// in-box API, with the legacy WinMM API as the fallback where no
    /// runtime is installed.
    ///
    /// <para>The calls mirror <see cref="MidiBackendInBox"/> one for
    /// one.</para>
    /// </summary>
    internal sealed class MidiBackendAppSdk : IMidiBackend
    {
        private Microsoft.Windows.Devices.Midi2.Initialization.MidiDesktopAppSdkInitializer _initializer;

        public MidiApiKind Kind => MidiApiKind.AppSdk;

        /// <summary>Test seam: set by the test assembly for the life of its
        /// process. Every call that loads the runtime or reaches the MIDI
        /// service then answers as a service that refuses: no start, no
        /// virtual device, no session, no endpoints. A session or a virtual
        /// device made from the test host is one every other program on this
        /// PC sees. False in production, where nothing sets it.</summary>
        internal static bool OffLimitsForTest;

        public bool Start()
        {
            if (OffLimitsForTest) return false;
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
        /// Drops the initializer. Its Dispose calls ShutdownSdkRuntime, which
        /// in Microsoft's projection only drops the native initializer's
        /// reference: the native Shutdown call is commented out there (rc-3
        /// and rc-4 MidiDesktopAppSdkInitializer.cs), so the runtime's DLLs
        /// stay mapped for the life of the process.
        /// <paramref name="skipDispose"/> drops the initializer without
        /// calling Dispose. No production caller passes it.
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
            if (OffLimitsForTest) throw new InvalidOperationException("The test host creates no virtual MIDI device.");
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

            // A fixed text, as in the in-box backend: no slot number, which
            // was the pad index plus one and not the number the UI shows.
            var userSuppliedInfo = new MidiEndpointUserSuppliedInfo();
            userSuppliedInfo.Name = deviceName;
            userSuppliedInfo.Description = "PadForge virtual MIDI controller";

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

            /// <summary>Sends the word the message builder would pack
            /// (<see cref="MidiUmp.Midi1Word"/>) with no WinRT object per
            /// message, as the in-box backend does. The API took it unless the
            /// result carries Failed.</summary>
            public bool Send(Midi1Status status, int channel, int data1, int data2)
            {
                var result = _connection.SendSingleMessageWords(0, MidiUmp.Midi1Word(status, channel, data1, data2));
                return (result & MidiSendMessageResults.Failed) != MidiSendMessageResults.Failed;
            }

            public bool Lost => false;

            public void DisconnectConnection() => _session.DisconnectEndpointConnection(_connection.ConnectionId);

            public void CloseSession()
            {
                _session.Dispose();
                GC.KeepAlive(_virtualDevice);
            }
        }

        public IMidiInputSession CreateInputSession(string name)
        {
            if (OffLimitsForTest) return null;
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
            if (OffLimitsForTest) return result;
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
