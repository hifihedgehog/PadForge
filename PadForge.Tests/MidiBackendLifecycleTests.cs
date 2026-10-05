using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The MIDI availability probe, the chosen backend and the
    /// shared input session are statics. Every class that swaps the backend
    /// rides this collection, alone, so no other test's device sweep reads a
    /// fake API.</summary>
    [CollectionDefinition("MidiBackendStatics", DisableParallelization = true)]
    public class MidiBackendStaticsCollection { }

    /// <summary>
    /// The MIDI lifecycle on both APIs. The virtual controller and the input
    /// runtime run one copy of their code over either the in-box
    /// Windows.Devices.Midi2 or the older App SDK runtime, through the seam
    /// the two backends share, so a fake backend drives it here. The janitor
    /// is held off: a test process knows none of a running PadForge's live
    /// endpoints, so a sweep from it would remove them as corpses.
    /// </summary>
    [Collection("MidiBackendStatics")]
    public class MidiBackendLifecycleTests : IDisposable
    {
        public MidiBackendLifecycleTests()
        {
            MidiEndpointJanitor.SweepDisabledForTest = true;
            // An earlier class's device sweep may have opened a real session.
            MidiInputRuntime.Shutdown();
        }

        public void Dispose()
        {
            MidiInputRuntime.Shutdown();
            MidiVirtualController.UseBackendFactoryForTest(null);
            MidiEndpointJanitor.SweepDisabledForTest = false;
        }

        private static FakeBackend Use(FakeBackend backend)
        {
            MidiVirtualController.UseBackendFactoryForTest(() => backend);
            return backend;
        }

        // ── The probe ──

        [Theory]
        [InlineData((int)MidiApiKind.InBox)]
        [InlineData((int)MidiApiKind.AppSdk)]
        public void TheProbe_StartsTheChosenApiOnce_AndReportsIt(int kind)
        {
            var backend = Use(new FakeBackend((MidiApiKind)kind));

            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal((MidiApiKind)kind, MidiVirtualController.ActiveApi);
            Assert.False(MidiVirtualController.ProbeFailed);
            Assert.Same(backend, MidiVirtualController.Backend);

            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(1, backend.Starts);
        }

        /// <summary>The in-box EnsureServiceAvailable returns false in Legacy
        /// API mode. With no legacy fallback named (production has one, see
        /// MidiBackendLegacyTests), the probe then reads as failed.</summary>
        [Fact]
        public void AnApiWhoseServiceDoesNotStart_ReadsAsAFailedProbe()
        {
            Use(new FakeBackend(MidiApiKind.InBox) { StartResult = false });

            Assert.False(MidiVirtualController.IsAvailable());
            Assert.True(MidiVirtualController.ProbeFailed);
            Assert.Equal(MidiApiKind.None, MidiVirtualController.ActiveApi);
            Assert.Null(MidiVirtualController.Backend);
        }

        [Fact]
        public void NoApi_ReadsAsAFailedProbe()
        {
            MidiVirtualController.UseBackendFactoryForTest(() => null);

            Assert.False(MidiVirtualController.IsAvailable());
            Assert.True(MidiVirtualController.ProbeFailed);
            Assert.Equal(MidiApiKind.None, MidiVirtualController.ActiveApi);
        }

        /// <summary>A start that throws partway may hold the older
        /// runtime's initializer. The probe releases it.</summary>
        [Fact]
        public void AStartThatThrows_IsReleased()
        {
            var backend = Use(new FakeBackend(MidiApiKind.AppSdk) { ThrowOnStart = true });

            Assert.False(MidiVirtualController.IsAvailable());
            Assert.Equal(1, backend.Stops);
            Assert.Null(MidiVirtualController.Backend);
        }

        [Fact]
        public void AReset_StopsTheBackend_AndTheNextAskProbesAgain()
        {
            var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
            Assert.True(MidiVirtualController.IsAvailable());

            MidiVirtualController.ResetAvailability();
            Assert.Equal(1, backend.Stops);
            Assert.Equal(MidiApiKind.None, MidiVirtualController.ActiveApi);
            Assert.False(MidiVirtualController.ProbeFailed);

            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(2, backend.Starts);
        }

        /// <summary>The older runtime's uninstall: the latch keeps every
        /// later ask from loading it back in until the reset.</summary>
        [Fact]
        public void SuppressForUninstall_ReleasesTheApi_AndHoldsItReleased()
        {
            var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
            Assert.True(MidiVirtualController.IsAvailable());

            MidiVirtualController.SuppressForUninstall();
            Assert.Equal(1, backend.Stops);
            Assert.False(backend.LastStopSkippedDispose);
            Assert.False(MidiVirtualController.IsAvailable());
            Assert.Equal(1, backend.Starts);

            MidiVirtualController.ResetAvailability();
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(2, backend.Starts);
        }

        [Fact]
        public void ShutdownSkippingDispose_PassesThatToTheBackend()
        {
            var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
            Assert.True(MidiVirtualController.IsAvailable());

            MidiVirtualController.Shutdown(skipDispose: true);
            Assert.True(backend.LastStopSkippedDispose);
            Assert.Null(MidiVirtualController.Backend);
        }

        // ── The virtual controller ──

        [Theory]
        [InlineData((int)MidiApiKind.InBox)]
        [InlineData((int)MidiApiKind.AppSdk)]
        public void AVirtualController_CreatesSendsAndTearsDown_OnEitherApi(int kind)
        {
            var backend = Use(new FakeBackend((MidiApiKind)kind));
            Assert.True(MidiVirtualController.IsAvailable());

            var vc = new MidiVirtualController(padIndex: 2, channel: 9, instanceNum: 3)
            {
                CcNumbers = new[] { 7, 10 },
                NoteNumbers = new[] { 36, 38 },
                Velocity = 100,
            };
            vc.Connect();

            Assert.True(vc.IsConnected);
            var ep = Assert.Single(backend.Endpoints);
            Assert.Equal("PadForge MIDI 3", ep.DeviceName);
            Assert.Equal(2, ep.PadIndex);
            Assert.Equal(vc.UniqueEndpointId, ep.UniqueId);
            Assert.StartsWith("PADFORGE_MIDI_3_", ep.UniqueId, StringComparison.Ordinal);
            Assert.True(MidiVirtualController.IsReadyEndpointInstance($@"SWD\MIDISRV\MIDIU_APPPUB_{ep.UniqueId}"));

            // Only changes go out. CC 10 stays at center (64), the value the
            // controller starts from.
            var state = new MidiRawState { CcValues = new byte[] { 100, 64 }, Notes = new[] { true, false } };
            vc.SubmitMidiRawState(state);
            vc.SubmitMidiRawState(state);
            Assert.Equal(new[]
            {
                (Midi1Status.ControlChange, 9, 7, 100),
                (Midi1Status.NoteOn, 9, 36, 100),
            }, ep.Sent);

            // Teardown releases the held note, then disconnects and closes.
            vc.Dispose();
            Assert.False(vc.IsConnected);
            Assert.Equal((Midi1Status.NoteOff, 9, 36, 0), ep.Sent[^1]);
            Assert.Equal(3, ep.Sent.Count);
            Assert.True(ep.Disconnected);
            Assert.True(ep.Closed);
            Assert.False(MidiVirtualController.IsLiveEndpointInstance($@"SWD\MIDISRV\MIDIU_APPDEV_{ep.UniqueId}"));
        }

        /// <summary>A send the service fails drops that message, never the
        /// polling thread.</summary>
        [Fact]
        public void AFailedSend_IsDropped()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            Assert.True(MidiVirtualController.IsAvailable());
            var vc = new MidiVirtualController(0, 0, 1) { CcNumbers = new[] { 1 }, NoteNumbers = new[] { 60 } };
            vc.Connect();
            var ep = Assert.Single(backend.Endpoints);
            ep.ThrowOnSend = true;

            vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 5 }, Notes = new[] { true } });
            Assert.True(vc.IsConnected);
            vc.Dispose();
            Assert.True(ep.Closed);
        }

        [Fact]
        public void AFailedCreate_ThrowsAndReleasesItsClaim()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            string seenUid = null;
            backend.OnCreate = (name, uid, pad) =>
            {
                seenUid = uid;
                Assert.True(MidiVirtualController.IsLiveEndpointInstance($@"SWD\MIDISRV\MIDIU_APPDEV_{uid}"));
                throw new InvalidOperationException("Failed to open MIDI endpoint connection.");
            };
            Assert.True(MidiVirtualController.IsAvailable());

            var vc = new MidiVirtualController(0, 0, 1);
            var ex = Assert.Throws<InvalidOperationException>(() => vc.Connect());
            Assert.Equal("Failed to open MIDI endpoint connection.", ex.Message);
            Assert.False(vc.IsConnected);
            Assert.NotNull(seenUid);
            Assert.False(MidiVirtualController.IsLiveEndpointInstance($@"SWD\MIDISRV\MIDIU_APPDEV_{seenUid}"));
        }

        [Fact]
        public void ACreateWithNoApi_FailsCleanly()
        {
            MidiVirtualController.UseBackendFactoryForTest(() => null);
            Assert.False(MidiVirtualController.IsAvailable());

            var vc = new MidiVirtualController(0, 0, 1);
            Assert.Throws<InvalidOperationException>(() => vc.Connect());
            Assert.False(vc.IsConnected);
            Assert.False(MidiVirtualController.IsLiveEndpointInstance(
                $@"SWD\MIDISRV\MIDIU_APPDEV_{vc.UniqueEndpointId}"));
        }

        // ── MIDI input ──

        private const string KeysId = @"\\?\SWD#MIDISRV#MIDIU_KS_TESTKEYS#{e7cce071-3c03-423f-88d3-f1045d02552b}";

        [Theory]
        [InlineData((int)MidiApiKind.InBox)]
        [InlineData((int)MidiApiKind.AppSdk)]
        public void MidiInput_OpensOnTheChosenApi_AndDecodesWhatArrives(int kind)
        {
            var backend = Use(new FakeBackend((MidiApiKind)kind));
            backend.NormalEndpoints.Add((KeysId, "Test Keys"));

            var (id, name) = Assert.Single(MidiInputRuntime.EnumerateEndpoints());
            Assert.Equal(KeysId, id);
            Assert.Equal("Test Keys", name);

            var dev = new MidiInputDevice(id, name);
            Assert.True(dev.Open());
            Assert.True(dev.IsAttached);
            Assert.Equal("PadForge MIDI Input", backend.InputSession.Name);
            var conn = Assert.Single(backend.InputSession.Connections);
            Assert.Equal(KeysId, conn.EndpointId);
            Assert.True(conn.Opened);

            // MIDI 1.0 Note On, channel 3, note 60, velocity 90.
            conn.Deliver(0x20923C5Au, 0);
            Assert.True(dev.GetCurrentState().Midi.Notes[60]);

            dev.Dispose();
            Assert.False(dev.IsAttached);
            Assert.True(conn.Detached);
            // The disconnect is fire-and-forget on a worker.
            Assert.True(SpinWait.SpinUntil(() => conn.Disconnected, 5_000));
        }

        [Fact]
        public void AnInputConnectionThatDoesNotOpen_IsUndone()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox) { InputOpens = false });
            backend.NormalEndpoints.Add((KeysId, "Test Keys"));

            var dev = new MidiInputDevice(KeysId, "Test Keys");
            Assert.False(dev.Open());
            Assert.False(dev.IsAttached);
            var conn = Assert.Single(backend.InputSession.Connections);
            Assert.True(conn.Detached);
            Assert.True(SpinWait.SpinUntil(() => conn.Disconnected, 5_000));
        }

        [Fact]
        public void NoApi_EnumeratesNothing_AndOpensNothing()
        {
            MidiVirtualController.UseBackendFactoryForTest(() => null);
            Assert.Empty(MidiInputRuntime.EnumerateEndpoints());
            Assert.Null(MidiInputRuntime.Session);
            Assert.False(new MidiInputDevice(KeysId, "Test Keys").Open());
        }

        // ── Decoding, which both backends feed as the first two UMP words ──

        private static MidiInputDevice Decoder() => new("decode-test-endpoint", "Decode MIDI");

        [Fact]
        public void Midi1_NoteOnAndOff_IncludingVelocityZero()
        {
            var dev = Decoder();
            dev.OnUmp(0x20903C40u, 0);                   // Note On 60, velocity 64
            Assert.True(dev.GetCurrentState().Midi.Notes[60]);
            dev.OnUmp(0x20903C00u, 0);                   // Note On 60, velocity 0 = Note Off
            Assert.False(dev.GetCurrentState().Midi.Notes[60]);
            dev.OnUmp(0x20903E40u, 0);                   // Note On 62
            dev.OnUmp(0x20803E40u, 0);                   // Note Off 62
            Assert.False(dev.GetCurrentState().Midi.Notes[62]);
        }

        [Fact]
        public void Midi1_ControlChangeAndPitchBend()
        {
            var dev = Decoder();
            dev.OnUmp(0x20B50764u, 0);                   // CC 7 = 100, channel 6
            Assert.Equal((byte)100, dev.GetCurrentState().Midi.Cc[7]);
            dev.OnUmp(0x20E00040u, 0);                   // bend LSB 0, MSB 64: center
            Assert.Equal(8192 * 65535 / 16383, dev.GetCurrentState().Midi.PitchBend);
            dev.OnUmp(0x20E07F7Fu, 0);                   // full up
            Assert.Equal(65535, dev.GetCurrentState().Midi.PitchBend);
        }

        [Fact]
        public void Midi2_UsesTheSecondWord()
        {
            var dev = Decoder();
            dev.OnUmp(0x40903C00u, 0x00000000u);         // MIDI 2.0 Note On, velocity 0 is still on
            Assert.True(dev.GetCurrentState().Midi.Notes[60]);
            dev.OnUmp(0x40803C00u, 0x80000000u);         // MIDI 2.0 Note Off
            Assert.False(dev.GetCurrentState().Midi.Notes[60]);
            dev.OnUmp(0x40B00700u, 0xFFFFFFFFu);         // 32-bit CC 7, full scale
            Assert.Equal((byte)127, dev.GetCurrentState().Midi.Cc[7]);
            dev.OnUmp(0x40E00000u, 0xC0000000u);         // 32-bit pitch bend, three quarters up
            Assert.Equal(0xC000, dev.GetCurrentState().Midi.PitchBend);
        }

        [Fact]
        public void OtherMessageTypes_ChangeNothing()
        {
            var dev = Decoder();
            dev.OnUmp(0x10F80000u, 0);                   // system real time: clock
            dev.OnUmp(0xF0000000u, 0);                   // stream: endpoint discovery
            dev.OnUmp(0x30163C00u, 0);                   // SysEx 7
            var s = dev.GetCurrentState().Midi;
            Assert.DoesNotContain(true, s.Notes);
            Assert.Equal(32768, s.PitchBend);
        }

        // ── The fake ──

        private sealed class FakeBackend : IMidiBackend
        {
            public FakeBackend(MidiApiKind kind) => Kind = kind;

            public MidiApiKind Kind { get; }
            public bool StartResult { get; init; } = true;
            public bool ThrowOnStart { get; init; }
            public bool InputOpens { get; init; } = true;
            public int Starts, Stops;
            public bool LastStopSkippedDispose;
            public Func<string, string, int, IMidiVirtualEndpoint> OnCreate;
            public readonly List<FakeEndpoint> Endpoints = new();
            public readonly List<(string Id, string Name)> NormalEndpoints = new();
            public FakeInputSession InputSession;

            public bool Start()
            {
                Starts++;
                if (ThrowOnStart) throw new TypeInitializationException("Midi2", null);
                return StartResult;
            }

            public void Stop(bool skipDispose)
            {
                Stops++;
                LastStopSkippedDispose = skipDispose;
            }

            public IMidiVirtualEndpoint CreateVirtualEndpoint(string deviceName, string uniqueId, int padIndex)
            {
                if (OnCreate != null) return OnCreate(deviceName, uniqueId, padIndex);
                var ep = new FakeEndpoint(deviceName, uniqueId, padIndex);
                lock (Endpoints) Endpoints.Add(ep);
                return ep;
            }

            public IMidiInputSession CreateInputSession(string name)
                => InputSession = new FakeInputSession(name, InputOpens);

            public List<(string Id, string Name)> EnumerateNormalEndpoints() => new(NormalEndpoints);
        }

        private sealed class FakeEndpoint : IMidiVirtualEndpoint
        {
            public FakeEndpoint(string deviceName, string uniqueId, int padIndex)
            {
                DeviceName = deviceName;
                UniqueId = uniqueId;
                PadIndex = padIndex;
            }

            public readonly string DeviceName;
            public readonly string UniqueId;
            public readonly int PadIndex;
            public readonly List<(Midi1Status, int, int, int)> Sent = new();
            public volatile bool ThrowOnSend;
            public volatile bool Disconnected, Closed;

            public void Send(Midi1Status status, int channel, int data1, int data2)
            {
                if (ThrowOnSend) throw new System.Runtime.InteropServices.COMException("send failed");
                lock (Sent) Sent.Add((status, channel, data1, data2));
            }

            public void DisconnectConnection() => Disconnected = true;
            public void CloseSession() => Closed = true;
        }

        private sealed class FakeInputSession : IMidiInputSession
        {
            private readonly bool _opens;

            public FakeInputSession(string name, bool opens)
            {
                Name = name;
                _opens = opens;
            }

            public readonly string Name;
            public readonly List<FakeInputConnection> Connections = new();

            public IMidiInputConnection CreateConnection(string endpointId, Action<uint, uint> onWords)
            {
                var c = new FakeInputConnection(endpointId, onWords, _opens);
                lock (Connections) Connections.Add(c);
                return c;
            }

            public void Dispose() { }
        }

        private sealed class FakeInputConnection : IMidiInputConnection
        {
            private readonly Action<uint, uint> _onWords;
            private readonly bool _opens;

            public FakeInputConnection(string endpointId, Action<uint, uint> onWords, bool opens)
            {
                EndpointId = endpointId;
                _onWords = onWords;
                _opens = opens;
            }

            public readonly string EndpointId;
            public volatile bool Opened, Detached, Disconnected;

            public bool Open() => Opened = _opens;
            public void Detach() => Detached = true;
            public void Disconnect() => Disconnected = true;

            public void Deliver(uint w0, uint w1)
            {
                if (!Detached) _onWords(w0, w1);
            }
        }
    }
}
