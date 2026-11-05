using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
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
        private readonly Xunit.Abstractions.ITestOutputHelper _output;

        public MidiBackendLifecycleTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _output = output;
            MidiEndpointJanitor.SweepDisabledForTest = true;
            // An earlier class's device sweep may have opened a real session.
            MidiInputRuntime.Shutdown();
        }

        public void Dispose()
        {
            // A recovery a test started finishes before the next test's
            // probe, which would otherwise find it running and stand down.
            // So does a restart a test's worker is still in: an ask made
            // inside it answers no. So does a release a test gave up on,
            // which would otherwise latch MIDI released under the next test.
            SpinWait.SpinUntil(() => RecoveryIdle() && !MidiServiceRecovery.RestartInFlight
                && !MidiRuntimeSwitch.ReleaseInFlight, 10_000);
            MidiVirtualController.SyncPointForTest = null;
            MidiInputRuntime.Shutdown();
            MidiRuntimeSwitch.ResetForTest();
            MidiVirtualController.UseBackendFactoryForTest(null);
            MidiApiSelection.OsBuildSeam = null;
            MidiEndpointJanitor.SweepDisabledForTest = false;
            MidiServiceRecovery.RecoverSeam = null;
            MidiServiceRecovery.CooldownMs = 120_000;
            MidiServiceRecovery.ResetCooldownForTest();
            MidiVirtualController.ProbeTimeoutMs = 10_000;
            MidiVirtualController.HungProbeReturnMs = 30_000;
            MidiVirtualController.ConnectTimeoutMs = 15_000;
            MidiVirtualController.RefusalRecheckMs = 10_000;
            MidiVirtualController.ServiceDownResetMs = 30_000;
            MidiInputRuntime.SessionTimeoutMs = 3_000;
            MidiInputDevice.OpenTimeoutMs = 3_000;
            MidiServiceRecovery.RestartSeam = null;
            MidiServiceRecovery.TransportsLoadedSeam = null;
            MidiRuntimeSwitch.ReleaseTimeoutMs = 60_000;
            MidiVirtualController.SendDrainMs = 3_000;
        }

        /// <summary>A bound a test means never to reach, so nothing that
        /// waits on it comes due however slowly the test runs.</summary>
        private const int FarOff = 3_600_000;

        /// <summary>Shortens the probe's two bounds: an ask gives up on a
        /// probe after <paramref name="askMs"/>, and the recovery restarts
        /// the service for one still pending <paramref name="restartAtMs"/>
        /// after it started, the create path's bound. The second defaults to
        /// the first, so the restart follows at once.</summary>
        private static void ProbeBounds(int askMs, int? restartAtMs = null)
        {
            MidiVirtualController.ProbeTimeoutMs = askMs;
            MidiVirtualController.ConnectTimeoutMs = restartAtMs ?? askMs;
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
        /// later ask from loading it back in until the release ends.</summary>
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

            Assert.True(MidiVirtualController.EndUninstallHold());
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

        // ── Recovery from a wedged service ──

        /// <summary>A probe that hangs left the card reading Not Running for
        /// the session, and the card told the user to restart Windows. The
        /// service is restarted, the hung probe finishes against it, and the
        /// probe runs again on a fresh backend.</summary>
        [Fact]
        public void AHungProbe_RestartsTheService_AndProbesAgain()
        {
            var gate = new ManualResetEventSlim(false);
            var looked = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { StartGate = gate });
                int restarts = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                // The restart waits until the test has seen the hung state, so
                // the recovery cannot finish before the first look.
                MidiServiceRecovery.RecoverSeam = () =>
                {
                    looked.Wait(5_000);
                    Interlocked.Increment(ref restarts);
                    gate.Set();
                    return true;
                };
                ProbeBounds(200);
                MidiVirtualController.HungProbeReturnMs = 5_000;
                int generation = MidiVirtualController.Generation;

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(MidiVirtualController.ProbeFailed);
                Assert.False(MidiVirtualController.IsAvailable());
                Assert.Equal(1, Volatile.Read(ref backend.Starts));
                looked.Set();

                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.Starts) >= 2
                    && MidiVirtualController.ActiveApi == MidiApiKind.InBox, 5_000));
                Assert.Equal(1, restarts);
                Assert.False(MidiVirtualController.ProbeFailed);
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.Equal(2, backend.Starts);
                Assert.Equal(1, backend.Stops);
                Assert.True(MidiVirtualController.Generation > generation);
            }
            finally
            {
                // A probe still held at the gate keeps the availability lock,
                // and every later test would wait on it.
                looked.Set();
                gate.Set();
            }
        }

        /// <summary>A probe that hangs inside the cooldown of a restart that
        /// already ran waits the cooldown out and restarts the service
        /// then.</summary>
        [Fact]
        public void AHungProbeInsideARestartsCooldown_RestartsWhenTheCooldownEnds()
        {
            var gate = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { StartGate = gate });
                int restarts = 0;
                MidiServiceRecovery.CooldownMs = 2_000;
                MidiServiceRecovery.ResetCooldownForTest();
                // The restart that opens the cooldown leaves the service
                // wedged. The one after the cooldown clears it.
                MidiServiceRecovery.RecoverSeam = () =>
                {
                    if (Interlocked.Increment(ref restarts) >= 2) gate.Set();
                    return true;
                };
                Assert.Equal(MidiServiceRestart.Running, MidiServiceRecovery.TryRestart("an earlier create"));
                ProbeBounds(200);
                MidiVirtualController.HungProbeReturnMs = 5_000;

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.Starts) >= 2
                    && MidiVirtualController.ActiveApi == MidiApiKind.InBox, 10_000));
                Assert.Equal(2, Volatile.Read(ref restarts));
                Assert.Equal(2, backend.Starts);
                Assert.Equal(1, backend.Stops);
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>A refused session is what a stopped or broken service
        /// answers. It gets the restart a hung create gets, and the create
        /// runs once more.</summary>
        [Fact]
        public void ARefusedSession_RestartsTheService_AndTheCreateRetries()
        {
            var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
            int creates = 0, restarts = 0;
            backend.OnCreate = (name, uid, pad) =>
            {
                if (Interlocked.Increment(ref creates) == 1) throw new MidiSessionUnavailableException();
                var ep = new FakeEndpoint(name, uid, pad);
                lock (backend.Endpoints) backend.Endpoints.Add(ep);
                return ep;
            };
            MidiServiceRecovery.ResetCooldownForTest();
            MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
            Assert.True(MidiVirtualController.IsAvailable());

            var vc = new MidiVirtualController(0, 0, 1);
            vc.Connect();

            Assert.True(vc.IsConnected);
            Assert.Equal(1, restarts);
            Assert.Equal(2, creates);
            Assert.Single(backend.Endpoints);
            Assert.Equal(1, backend.Stops);
            Assert.Equal(MidiVirtualController.Generation, vc.CreatedGeneration);
            vc.Dispose();
        }

        /// <summary>Inside the restart's cooldown a refused session fails as
        /// it did, with no second restart.</summary>
        [Fact]
        public void ARefusedSessionInsideTheCooldown_Fails()
        {
            var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
            backend.OnCreate = (_, _, _) => throw new MidiSessionUnavailableException();
            int restarts = 0;
            MidiServiceRecovery.ResetCooldownForTest();
            MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
            Assert.Equal(MidiServiceRestart.Running, MidiServiceRecovery.TryRestart("test"));
            Assert.True(MidiVirtualController.IsAvailable());

            var vc = new MidiVirtualController(0, 0, 1);
            Assert.Throws<MidiSessionUnavailableException>(() => vc.Connect());
            Assert.False(vc.IsConnected);
            Assert.Equal(1, restarts);
            Assert.Equal(0, backend.Stops);
        }

        /// <summary>A create that hung restarts the service, and the new
        /// instance's first probe waits for its device enumeration, up to the
        /// 10 s Microsoft's client gives it, which is also the bound an ask
        /// gives a probe. The ask after the restart gave up in the moment the
        /// answer came, and the create failed with MIDI running (bench
        /// 2026-10-28: the probe answered 2 ms after the bound). The create
        /// waits for that probe through its own bound, as the recovery does
        /// before it restarts anything, and runs again.</summary>
        [Fact]
        public void AHungCreate_WaitsForTheNewInstancesFirstProbe_AndRunsAgain()
        {
            var hang = new ManualResetEventSlim(false);
            var slowProbe = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
                int creates = 0, restarts = 0;
                backend.OnCreate = (name, uid, pad) =>
                {
                    if (Interlocked.Increment(ref creates) == 1) hang.Wait(30_000);
                    var ep = new FakeEndpoint(name, uid, pad);
                    lock (backend.Endpoints) backend.Endpoints.Add(ep);
                    return ep;
                };
                Assert.True(MidiVirtualController.IsAvailable());
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    backend.StartGate = slowProbe;
                    return true;
                };
                // An ask gives up after 200 ms. The create's bound, and the
                // recovery's watch, is 2 s.
                ProbeBounds(200, restartAtMs: 2_000);
                // The probe answers as soon as the ask has given up on it.
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point == "recovery-watching") slowProbe.Set();
                };

                var vc = new MidiVirtualController(0, 0, 1);
                vc.Connect();

                Assert.True(vc.IsConnected);
                Assert.Equal(1, Volatile.Read(ref restarts));
                Assert.Equal(2, Volatile.Read(ref creates));
                Assert.Equal(MidiApiKind.AppSdk, MidiVirtualController.ActiveApi);
                Assert.Equal(MidiVirtualController.Generation, vc.CreatedGeneration);
                vc.Dispose();
            }
            finally
            {
                slowProbe.Set();
                hang.Set();
            }
        }

        /// <summary>A new service instance answers a connectivity check
        /// after 10 s whether or not it has loaded its transports
        /// (MidiSrvTransport.cpp VerifyConnectivity), and a virtual device
        /// create sent before the virtual transport is loaded fails
        /// (MidiDeviceManager.cpp UpdateTransportConfiguration). The bench
        /// took 10.15 s to load them after a restart, and the create made
        /// 10.0 s in failed and latched the slot (2026-10-28). A create that
        /// fails while the service is still loading its transports waits for
        /// them and runs once more: after the create's own restart, and on a
        /// service that started for some other reason.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ACreateTheServiceTurnsDownWhileItLoadsItsTransports_WaitsForThemAndRunsAgain(bool afterItsOwnRestart)
        {
            var hang = new ManualResetEventSlim(false);
            // Set while the running instance has loaded every transport.
            var loaded = new ManualResetEventSlim(afterItsOwnRestart);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
                int creates = 0, restarts = 0, waits = 0;
                backend.OnCreate = (name, uid, pad) =>
                {
                    int n = Interlocked.Increment(ref creates);
                    if (afterItsOwnRestart && n == 1) hang.Wait(30_000);
                    else if (!loaded.IsSet) throw new InvalidOperationException("Failed to create virtual MIDI device.");
                    var ep = new FakeEndpoint(name, uid, pad);
                    lock (backend.Endpoints) backend.Endpoints.Add(ep);
                    return ep;
                };
                Assert.True(MidiVirtualController.IsAvailable());
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                // The restarted instance starts loading its transports.
                MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); loaded.Reset(); return true; };
                // The service finishes loading them while PadForge waits.
                MidiServiceRecovery.TransportsLoadedSeam = ms =>
                {
                    if (ms > 0)
                    {
                        Interlocked.Increment(ref waits);
                        loaded.Set();
                    }
                    return loaded.IsSet;
                };
                ProbeBounds(200, restartAtMs: 1_000);

                var vc = new MidiVirtualController(0, 0, 1);
                vc.Connect();

                Assert.True(vc.IsConnected);
                Assert.Equal(afterItsOwnRestart ? 1 : 0, Volatile.Read(ref restarts));
                Assert.Equal(afterItsOwnRestart ? 3 : 2, Volatile.Read(ref creates));
                Assert.Equal(1, Volatile.Read(ref waits));
                Assert.Single(backend.Endpoints);
                vc.Dispose();
            }
            finally
            {
                loaded.Set();
                hang.Set();
            }
        }

        /// <summary>Each service instance gets its own wait: a create that
        /// waited for one instance to load its transports, then hung and
        /// restarted the service, waits for the new instance too.</summary>
        [Fact]
        public void ACreateThatWaitedThenHung_WaitsForTheRestartedInstanceToo()
        {
            var hang = new ManualResetEventSlim(false);
            var loaded = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
                int creates = 0, restarts = 0, waits = 0;
                backend.OnCreate = (name, uid, pad) =>
                {
                    int n = Interlocked.Increment(ref creates);
                    if (n == 2) hang.Wait(30_000);
                    else if (!loaded.IsSet) throw new InvalidOperationException("Failed to create virtual MIDI device.");
                    var ep = new FakeEndpoint(name, uid, pad);
                    lock (backend.Endpoints) backend.Endpoints.Add(ep);
                    return ep;
                };
                Assert.True(MidiVirtualController.IsAvailable());
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); loaded.Reset(); return true; };
                MidiServiceRecovery.TransportsLoadedSeam = ms =>
                {
                    if (ms > 0)
                    {
                        Interlocked.Increment(ref waits);
                        loaded.Set();
                    }
                    return loaded.IsSet;
                };
                ProbeBounds(200, restartAtMs: 1_000);

                var vc = new MidiVirtualController(0, 0, 1);
                vc.Connect();

                Assert.True(vc.IsConnected);
                Assert.Equal(1, Volatile.Read(ref restarts));
                Assert.Equal(4, Volatile.Read(ref creates));
                Assert.Equal(2, Volatile.Read(ref waits));
                vc.Dispose();
            }
            finally
            {
                loaded.Set();
                hang.Set();
            }
        }

        /// <summary>A create that fails once the service has loaded its
        /// transports, or on a service with no such signal, fails at once,
        /// as it did: nothing waits, and nothing runs it again.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ACreateThatFailsWhileTheServiceIsNotLoading_FailsAtOnce(bool serviceHasTheSignal)
        {
            var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
            int creates = 0, waits = 0;
            backend.OnCreate = (_, _, _) =>
            {
                Interlocked.Increment(ref creates);
                throw new InvalidOperationException("Failed to create virtual MIDI device.");
            };
            MidiServiceRecovery.TransportsLoadedSeam = ms =>
            {
                if (ms > 0) Interlocked.Increment(ref waits);
                return serviceHasTheSignal ? true : null;
            };
            Assert.True(MidiVirtualController.IsAvailable());

            var vc = new MidiVirtualController(0, 0, 1);
            Assert.Throws<InvalidOperationException>(() => vc.Connect());
            Assert.False(vc.IsConnected);
            Assert.Equal(1, Volatile.Read(ref creates));
            Assert.Equal(0, Volatile.Read(ref waits));
        }

        /// <summary>Every backend teardown starts a new generation, and a
        /// controller keeps the one it was made in, so Step 5 can tell it
        /// stale.</summary>
        [Fact]
        public void EveryReset_StartsANewGeneration_AndAControllerKeepsItsOwn()
        {
            Use(new FakeBackend(MidiApiKind.InBox));
            Assert.True(MidiVirtualController.IsAvailable());
            var vc = new MidiVirtualController(0, 0, 1);
            vc.Connect();
            int made = vc.CreatedGeneration;
            Assert.Equal(MidiVirtualController.Generation, made);

            MidiVirtualController.ResetAvailability();
            Assert.Equal(made + 1, MidiVirtualController.Generation);
            Assert.Equal(made, vc.CreatedGeneration);
            vc.Dispose();
        }

        /// <summary>The shared input session is dropped without waiting on
        /// it, and the next asker gets one from the current backend.</summary>
        [Fact]
        public void AResetInputSession_IsReplacedByTheNextAsk()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            Assert.NotNull(MidiInputRuntime.Session);
            var first = backend.InputSession;
            Assert.NotNull(first);

            MidiInputRuntime.ResetSession();
            Assert.True(SpinWait.SpinUntil(() => first.Disposed, 5_000));
            Assert.NotNull(MidiInputRuntime.Session);
            Assert.NotSame(first, backend.InputSession);
            Assert.False(backend.InputSession.Disposed);
        }

        /// <summary>A backend torn down under open MIDI inputs left them
        /// talking to nothing, and an endpoint that came back under the same
        /// id was never reopened. The input sweep drops them with the old
        /// session and opens them again on a session from the new
        /// backend.</summary>
        [Fact]
        public void TheInputSweep_ReopensItsInputsOnANewBackend()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var backend = Use(new FakeBackend(MidiApiKind.InBox));
                backend.NormalEndpoints.Add((KeysId, "Test Keys"));
                var im = new InputManager();
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(sweep);
                bool Sweep() => (bool)sweep.Invoke(im, null);

                // A sweep starts the enumeration, and a later one opens what
                // it found.
                Assert.True(SpinWait.SpinUntil(Sweep, 5_000));
                var first = backend.InputSession;
                var conn = Assert.Single(first.Connections);
                Assert.True(conn.Opened);

                MidiVirtualController.ResetAvailability();
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.True(SpinWait.SpinUntil(Sweep, 5_000));

                Assert.True(conn.Detached);
                Assert.True(SpinWait.SpinUntil(() => first.Disposed, 5_000));
                Assert.NotSame(first, backend.InputSession);
                var again = Assert.Single(backend.InputSession.Connections);
                Assert.Equal(KeysId, again.EndpointId);
                Assert.True(again.Opened);
                Assert.False(again.Detached);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
        }

        /// <summary>An input that failed to open on the old backend is not
        /// held to the minute-long backoff on the new one. The sweep tries it
        /// again at once.</summary>
        [Fact]
        public void TheInputSweep_RetriesAFailedOpenAtOnceOnANewBackend()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var refusing = Use(new FakeBackend(MidiApiKind.InBox) { InputOpens = false });
                refusing.NormalEndpoints.Add((KeysId, "Test Keys"));
                var im = new InputManager();
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(sweep);
                bool Sweep() => (bool)sweep.Invoke(im, null);

                Assert.True(SpinWait.SpinUntil(() =>
                {
                    Sweep();
                    return refusing.InputSession?.Connections.Count > 0;
                }, 5_000));
                Assert.False(Assert.Single(refusing.InputSession.Connections).Opened);

                var working = Use(new FakeBackend(MidiApiKind.InBox));
                working.NormalEndpoints.Add((KeysId, "Test Keys"));
                Assert.True(SpinWait.SpinUntil(Sweep, 5_000));

                var conn = Assert.Single(working.InputSession.Connections);
                Assert.Equal(KeysId, conn.EndpointId);
                Assert.True(conn.Opened);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
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
            Assert.True(vc.WaitForSends());
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

        // ── Live MIDI bar edits (ApplyLayout) ──

        private static (MidiVirtualController Vc, FakeEndpoint Ep) ConnectedController(FakeBackend backend,
            int channel, int startCc, int ccCount, int startNote, int noteCount, byte velocity)
        {
            Assert.True(MidiVirtualController.IsAvailable());
            var vc = new MidiVirtualController(0, channel, 1);
            vc.ApplyLayout(channel, startCc, ccCount, startNote, noteCount, velocity);
            vc.Connect();
            return (vc, Assert.Single(backend.Endpoints));
        }

        /// <summary>A channel and number edit reaches a running slot on the
        /// same port. The held note ends on the channel and number it started
        /// on, and the next submit presses it on the new ones.</summary>
        [Theory]
        [InlineData((int)MidiApiKind.InBox)]
        [InlineData((int)MidiApiKind.AppSdk)]
        public void AMidiBarEdit_ReachesTheRunningController_OnTheSamePort(int kind)
        {
            var backend = Use(new FakeBackend((MidiApiKind)kind));
            var (vc, ep) = ConnectedController(backend, channel: 0, startCc: 1, ccCount: 2, startNote: 60, noteCount: 2, velocity: 100);
            var state = new MidiRawState { CcValues = new byte[] { 100, 64 }, Notes = new[] { true, false } };
            vc.SubmitMidiRawState(state);

            vc.ApplyLayout(channel: 1, startCc: 20, ccCount: 2, startNote: 72, noteCount: 2, velocity: 90);
            vc.SubmitMidiRawState(state);
            Assert.True(vc.WaitForSends());

            Assert.Equal(new[]
            {
                (Midi1Status.ControlChange, 0, 1, 100),
                (Midi1Status.NoteOn, 0, 60, 100),
                (Midi1Status.NoteOff, 0, 60, 0),
                (Midi1Status.ControlChange, 1, 20, 100),
                (Midi1Status.NoteOn, 1, 72, 90),
            }, ep.Sent);
            Assert.Single(backend.Endpoints);
            Assert.False(ep.Disconnected);

            vc.Dispose();
            Assert.Equal((Midi1Status.NoteOff, 1, 72, 0), ep.Sent[^1]);
        }

        /// <summary>More CCs and notes from the same starts: the held note
        /// stays held and the CC keeps its value, so only the new entries go
        /// out.</summary>
        [Fact]
        public void ALargerCount_SendsOnlyTheNewEntries()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 1, 60, 1, 100);
            vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 100 }, Notes = new[] { true } });

            vc.ApplyLayout(0, 1, 3, 60, 2, 100);
            vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 100, 64, 30 }, Notes = new[] { true, true } });
            Assert.True(vc.WaitForSends());

            Assert.Equal(new[]
            {
                (Midi1Status.ControlChange, 0, 1, 100),
                (Midi1Status.NoteOn, 0, 60, 100),
                (Midi1Status.ControlChange, 0, 3, 30),
                (Midi1Status.NoteOn, 0, 61, 100),
            }, ep.Sent);
            vc.Dispose();
        }

        /// <summary>A velocity edit releases nothing and applies from the
        /// next Note On.</summary>
        [Fact]
        public void AVelocityEdit_AppliesFromTheNextNoteOn()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 0, 60, 1, 100);
            vc.SubmitMidiRawState(new MidiRawState { CcValues = Array.Empty<byte>(), Notes = new[] { true } });

            vc.ApplyLayout(0, 1, 0, 60, 1, 50);
            Assert.True(vc.WaitForSends());
            Assert.Single(ep.Sent);
            vc.SubmitMidiRawState(new MidiRawState { CcValues = Array.Empty<byte>(), Notes = new[] { false } });
            vc.SubmitMidiRawState(new MidiRawState { CcValues = Array.Empty<byte>(), Notes = new[] { true } });
            Assert.True(vc.WaitForSends());

            Assert.Equal(new[]
            {
                (Midi1Status.NoteOn, 0, 60, 100),
                (Midi1Status.NoteOff, 0, 60, 0),
                (Midi1Status.NoteOn, 0, 60, 50),
            }, ep.Sent);
            vc.Dispose();
        }

        /// <summary>Step 5 calls ApplyLayout every frame. With nothing
        /// changed, the change state stays, so nothing goes out twice.</summary>
        [Fact]
        public void UnchangedSettings_SendNothingAgain()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 3, 10, 1, 40, 1, 127);
            var state = new MidiRawState { CcValues = new byte[] { 5 }, Notes = new[] { true } };
            vc.SubmitMidiRawState(state);
            for (int i = 0; i < 3; i++)
            {
                vc.ApplyLayout(3, 10, 1, 40, 1, 127);
                vc.SubmitMidiRawState(state);
            }
            Assert.True(vc.WaitForSends());

            Assert.Equal(new[]
            {
                (Midi1Status.ControlChange, 3, 10, 5),
                (Midi1Status.NoteOn, 3, 40, 127),
            }, ep.Sent);
            vc.Dispose();
        }

        /// <summary>The bar sets a start and then re-clamps its count, so a
        /// read between the two can run past 127. The layout clamps again,
        /// and the channel too.</summary>
        [Fact]
        public void ARunPast127_StopsAt127()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, channel: 20, startCc: 125, ccCount: 6, startNote: 126, noteCount: 5, velocity: 127);
            Assert.Equal(new[] { 125, 126, 127 }, vc.CcNumbers);
            Assert.Equal(new[] { 126, 127 }, vc.NoteNumbers);

            vc.SubmitMidiRawState(new MidiRawState
            {
                CcValues = new byte[] { 1, 2, 3, 4, 5, 6 },
                Notes = new[] { true, true, true, true, true },
            });
            Assert.True(vc.WaitForSends());
            Assert.Equal(5, ep.Sent.Count);
            Assert.All(ep.Sent, m => Assert.Equal(15, m.Item2));
            vc.Dispose();
        }

        /// <summary>Step 5 hands the slot's MIDI bar to the running
        /// controller before each submit, so a channel edit made while the
        /// slot runs reaches it on the same port. This collection runs with
        /// nothing beside it, so the test may borrow the settings statics,
        /// and it puts them back.</summary>
        [Fact]
        public void Step5_HandsAMidiBarEditToTheRunningController()
        {
            const int pad = 3;
            var deviceGuid = new Guid("7b1d7e55-3c2a-4f6e-9d41-0a6b5c4d3e21");
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            var savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            MidiVirtualController vc = null;
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox));
                Assert.True(MidiVirtualController.IsAvailable());
                vc = new MidiVirtualController(pad, 0, 1);
                vc.ApplyLayout(0, 1, 1, 60, 1, 100);
                vc.Connect();
                var ep = Assert.Single(backend.Endpoints);

                // A created, enabled MIDI slot with its one device online, so
                // Step 5 submits for it.
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++) SettingsManager.SlotEnabled[i] = true;
                SettingsManager.SlotCreated[pad] = true;
                var ud = new UserDevice { InstanceGuid = deviceGuid, ProductName = "MIDI Bar Pad", IsOnline = true, InputState = new CustomInputState() };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = deviceGuid, MapTo = pad });

                var im = new InputManager();
                var config = new MidiSlotConfig { Channel = 1, StartCc = 1, CcCount = 1, StartNote = 60, NoteCount = 1, Velocity = 100 };
                im.SlotControllerTypes[pad] = VirtualControllerType.Midi;
                im._midiConfigs[pad] = config;
                ((IVirtualController[])typeof(InputManager)
                    .GetField("_virtualControllers", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(im))[pad] = vc;
                im.CombinedMidiRawStates[pad] = new MidiRawState { CcValues = new byte[] { 64 }, Notes = new[] { true } };
                var step5 = typeof(InputManager).GetMethod("UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(step5);

                step5.Invoke(im, null);
                config.Channel = 5;
                step5.Invoke(im, null);
                Assert.True(vc.WaitForSends());

                Assert.Equal(new[]
                {
                    (Midi1Status.NoteOn, 0, 60, 100),
                    (Midi1Status.NoteOff, 0, 60, 0),
                    (Midi1Status.NoteOn, 4, 60, 100),
                }, ep.Sent);
                Assert.Single(backend.Endpoints);
                Assert.False(ep.Disconnected);
            }
            finally
            {
                vc?.Dispose();
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
                Array.Copy(savedCreated, SettingsManager.SlotCreated, savedCreated.Length);
                Array.Copy(savedEnabled, SettingsManager.SlotEnabled, savedEnabled.Length);
            }
        }

        /// <summary>A slot running when the backend is torn down talks to a
        /// backend or a service that is gone. Step 5 rebuilds it, the way it
        /// rebuilds one built on another API, and keeps it while the
        /// generation holds.</summary>
        [Fact]
        public void Step5_RebuildsASlotMadeBeforeTheBackendWasTornDown()
        {
            const int pad = 3;
            var deviceGuid = new Guid("5f2c8a91-0b7d-4e33-a6c4-91d27e0f4b18");
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            var savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            MidiVirtualController vc = null;
            IVirtualController[] vcs = null;
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox));
                Assert.True(MidiVirtualController.IsAvailable());
                vc = new MidiVirtualController(pad, 0, 1);
                vc.ApplyLayout(0, 1, 1, 60, 1, 100);
                vc.Connect();
                var ep = Assert.Single(backend.Endpoints);

                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++) SettingsManager.SlotEnabled[i] = true;
                SettingsManager.SlotCreated[pad] = true;
                var ud = new UserDevice { InstanceGuid = deviceGuid, ProductName = "MIDI Pad", IsOnline = true, InputState = new CustomInputState() };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = deviceGuid, MapTo = pad });

                var im = new InputManager();
                im.SlotControllerTypes[pad] = VirtualControllerType.Midi;
                im._midiConfigs[pad] = new MidiSlotConfig { Channel = 1, StartCc = 1, CcCount = 1, StartNote = 60, NoteCount = 1, Velocity = 100 };
                vcs = (IVirtualController[])typeof(InputManager)
                    .GetField("_virtualControllers", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(im);
                vcs[pad] = vc;
                im.CombinedMidiRawStates[pad] = new MidiRawState { CcValues = new byte[] { 64 }, Notes = new[] { false } };
                var step5 = typeof(InputManager).GetMethod("UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(step5);

                step5.Invoke(im, null);
                Assert.Same(vc, vcs[pad]);

                MidiVirtualController.ResetAvailability();
                Assert.True(MidiVirtualController.IsAvailable());
                step5.Invoke(im, null);

                Assert.NotSame(vc, vcs[pad]);
                Assert.True(SpinWait.SpinUntil(() => ep.Closed, 10_000));
            }
            finally
            {
                vc?.Dispose();
                if (vcs?[pad] is IVirtualController made && !ReferenceEquals(made, vc))
                    made.Dispose();
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
                Array.Copy(savedCreated, SettingsManager.SlotCreated, savedCreated.Length);
                Array.Copy(savedEnabled, SettingsManager.SlotEnabled, savedEnabled.Length);
            }
        }

        /// <summary>A MIDI create that fails for another reason than its
        /// port (a session the service refuses inside another restart's
        /// cooldown, a create that hangs) keeps its status line only until
        /// the slot starts. The line was raised without being remembered for
        /// the slot, so once a restart from another path started a new
        /// generation and the slot retried and ran, the status bar still
        /// said it had failed. The line names the slot the way the UI does,
        /// in the UI language. It says Windows MIDI Services did not create
        /// the port while one of its APIs is still the one in use, and that
        /// MIDI is not running when the create's own service restart left no
        /// API started.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AMidiCreateFailureThatIsNotAPortFailure_ClearsOnceTheSlotRuns(bool restartLeavesNoApi)
        {
            const int pad = 3;
            var deviceGuid = new Guid("2d9b7c14-6a35-4e0f-8b12-c47e9a1d5f63");
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            var savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            IVirtualController[] vcs = null;
            // The one MIDI slot sits on pad 3 and shows as Virtual
            // Controller 1.
            using var orders = new SlotOrdersScope(midi: new[] { pad });
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox));
                int creates = 0;
                backend.OnCreate = (name, uid, p) =>
                {
                    if (Interlocked.Increment(ref creates) == 1) throw new MidiSessionUnavailableException();
                    var ep = new FakeEndpoint(name, uid, p);
                    lock (backend.Endpoints) backend.Endpoints.Add(ep);
                    return ep;
                };
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                if (restartLeavesNoApi)
                {
                    // The refused create restarts the service, and no API
                    // starts on the instance that comes up.
                    MidiServiceRecovery.RecoverSeam = () => { backend.StartResult = false; return true; };
                }
                else
                {
                    // Inside another restart's cooldown, so the refused
                    // create gets no restart of its own and fails.
                    MidiServiceRecovery.RecoverSeam = () => true;
                    Assert.Equal(MidiServiceRestart.Running, MidiServiceRecovery.TryRestart("an earlier create"));
                }
                Assert.True(MidiVirtualController.IsAvailable());

                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++) SettingsManager.SlotEnabled[i] = true;
                SettingsManager.SlotCreated[pad] = true;
                var ud = new UserDevice { InstanceGuid = deviceGuid, ProductName = "MIDI Pad", IsOnline = true, InputState = new CustomInputState() };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = deviceGuid, MapTo = pad });

                var im = new InputManager();
                im.SlotControllerTypes[pad] = VirtualControllerType.Midi;
                im._midiConfigs[pad] = new MidiSlotConfig { Channel = 1, StartCc = 1, CcCount = 1, StartNote = 60, NoteCount = 1, Velocity = 100 };
                vcs = (IVirtualController[])typeof(InputManager)
                    .GetField("_virtualControllers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im);
                var raised = new List<string>();
                var resolved = new List<string>();
                im.ErrorOccurred += (_, e) => { lock (raised) raised.Add(e.Message); };
                im.ErrorResolved += (_, m) => { lock (resolved) resolved.Add(m); };
                T Field<T>(string name) => (T)typeof(InputManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im);
                var step5 = typeof(InputManager).GetMethod("UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                // One Step 5 pass, then its create or dispose worker to the
                // end.
                bool StepUntil(Func<bool> done)
                {
                    for (int i = 0; i < 20 && !done(); i++)
                    {
                        step5.Invoke(im, null);
                        Assert.True(SpinWait.SpinUntil(() =>
                        {
                            var connect = Field<System.Threading.Tasks.Task[]>("_pendingConnectTask")[pad];
                            var dispose = Field<System.Threading.Tasks.Task[]>("_pendingDisposeTask")[pad];
                            return (connect == null || connect.IsCompleted) && (dispose == null || dispose.IsCompleted);
                        }, 10_000), "a Step 5 worker did not finish");
                    }
                    return done();
                }

                Assert.True(StepUntil(() => Field<bool[]>("_createFailed")[pad]));
                string line;
                lock (raised) line = Assert.Single(raised);
                var strings = PadForge.Resources.Strings.Strings.Instance;
                Assert.Equal(string.Format(
                    restartLeavesNoApi ? strings.Status_MidiNotRunning_Format : strings.Status_MidiCreateFailed_Format,
                    string.Format(strings.Main_VirtualController_Format, 1)), line);
                lock (resolved) Assert.Empty(resolved);

                // A restart from another path starts a new generation, the
                // service answers, and the slot retries and runs.
                backend.StartResult = true;
                MidiVirtualController.ResetAvailability();
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.True(StepUntil(() => vcs[pad] is MidiVirtualController { IsConnected: true }));
                lock (resolved) Assert.Equal(new[] { line }, resolved);
                lock (raised) Assert.Single(raised);
            }
            finally
            {
                vcs?[pad]?.Dispose();
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
                Array.Copy(savedCreated, SettingsManager.SlotCreated, savedCreated.Length);
                Array.Copy(savedEnabled, SettingsManager.SlotEnabled, savedEnabled.Length);
            }
        }

        /// <summary>A MIDI create that failed for want of an API retries once
        /// the probe answers, even when the answer came between the failure
        /// and its latch. A probe on a new service instance answers in about
        /// the time an ask waits for it, so the answer came a few
        /// milliseconds after the create gave up, the latch stamped the API
        /// that had just started and the generation it started in, and
        /// nothing that failed ever changed again: the slot stayed failed
        /// with MIDI running (bench 2026-10-28). One row fails at the
        /// create's own ask. The other fails after the create hung and
        /// restarted the service, with the new instance's probe still pending
        /// at the create's bound.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AMidiCreateThatFoundNoApi_RetriesOnceTheProbeAnswers_EvenBeforeItsLatch(bool createHungFirst)
        {
            const int pad = 3;
            var deviceGuid = new Guid("8a4f0c27-3d91-4b6e-9e25-61c7d0b4a3f8");
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            var savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            IVirtualController[] vcs = null;
            var hang = new ManualResetEventSlim(false);
            var slowProbe = new ManualResetEventSlim(false);
            using var orders = new SlotOrdersScope(midi: new[] { pad });
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
                int creates = 0;
                backend.OnCreate = (name, uid, p) =>
                {
                    if (Interlocked.Increment(ref creates) == 1 && createHungFirst) hang.Wait(30_000);
                    var ep = new FakeEndpoint(name, uid, p);
                    lock (backend.Endpoints) backend.Endpoints.Add(ep);
                    return ep;
                };
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                if (createHungFirst)
                {
                    // The probe made before the slot answers. The create
                    // hangs, and the probe on the restarted instance holds
                    // past the create's bound.
                    Assert.True(MidiVirtualController.IsAvailable());
                    MidiServiceRecovery.RecoverSeam = () => { backend.StartGate = slowProbe; return true; };
                    ProbeBounds(200, restartAtMs: 1_000);
                }
                else
                {
                    // The slot's create makes the first probe, which holds
                    // past the ask's bound. Nothing is restarted.
                    backend.StartGate = slowProbe;
                    MidiServiceRecovery.RecoverSeam = () => true;
                    ProbeBounds(200, restartAtMs: FarOff);
                }

                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++) SettingsManager.SlotEnabled[i] = true;
                SettingsManager.SlotCreated[pad] = true;
                var ud = new UserDevice { InstanceGuid = deviceGuid, ProductName = "MIDI Pad", IsOnline = true, InputState = new CustomInputState() };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = deviceGuid, MapTo = pad });

                var im = new InputManager();
                im.SlotControllerTypes[pad] = VirtualControllerType.Midi;
                im._midiConfigs[pad] = new MidiSlotConfig { Channel = 1, StartCc = 1, CcCount = 1, StartNote = 60, NoteCount = 1, Velocity = 100 };
                vcs = (IVirtualController[])typeof(InputManager)
                    .GetField("_virtualControllers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im);
                var raised = new List<string>();
                var resolved = new List<string>();
                // The failure is decided, then the probe answers, then the
                // create worker latches: the order the bench showed.
                im.ErrorOccurred += (_, e) =>
                {
                    lock (raised) raised.Add(e.Message);
                    slowProbe.Set();
                    SpinWait.SpinUntil(() => MidiVirtualController.ActiveApi == MidiApiKind.AppSdk, 10_000);
                };
                im.ErrorResolved += (_, m) => { lock (resolved) resolved.Add(m); };
                T Field<T>(string name) => (T)typeof(InputManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im);
                var step5 = typeof(InputManager).GetMethod("UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                bool StepUntil(Func<bool> done)
                {
                    for (int i = 0; i < 20 && !done(); i++)
                    {
                        step5.Invoke(im, null);
                        Assert.True(SpinWait.SpinUntil(() =>
                        {
                            var connect = Field<System.Threading.Tasks.Task[]>("_pendingConnectTask")[pad];
                            var dispose = Field<System.Threading.Tasks.Task[]>("_pendingDisposeTask")[pad];
                            return (connect == null || connect.IsCompleted) && (dispose == null || dispose.IsCompleted);
                        }, 10_000), "a Step 5 worker did not finish");
                    }
                    return done();
                }

                Assert.True(StepUntil(() => Field<bool[]>("_createFailed")[pad]));
                string line;
                lock (raised) line = Assert.Single(raised);
                var strings = PadForge.Resources.Strings.Strings.Instance;
                Assert.Equal(string.Format(strings.Status_MidiNotRunning_Format,
                    string.Format(strings.Main_VirtualController_Format, 1)), line);
                Assert.Equal(MidiApiKind.AppSdk, MidiVirtualController.ActiveApi);

                // MIDI is running, so the slot tries again and runs.
                Assert.True(StepUntil(() => vcs[pad] is MidiVirtualController { IsConnected: true }));
                lock (resolved) Assert.Equal(new[] { line }, resolved);
                lock (raised) Assert.Single(raised);
            }
            finally
            {
                slowProbe.Set();
                hang.Set();
                vcs?[pad]?.Dispose();
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
                Array.Copy(savedCreated, SettingsManager.SlotCreated, savedCreated.Length);
                Array.Copy(savedEnabled, SettingsManager.SlotEnabled, savedEnabled.Length);
            }
        }

        /// <summary>One MIDI slot with a device online, over a backend whose
        /// create waits at a gate and then fails as a session the service
        /// refuses inside another restart's cooldown, so the failure gets no
        /// restart and no second attempt. The test decides what happens to
        /// the slot while its create is under way.</summary>
        private sealed class HeldMidiCreate : IDisposable
        {
            public const int Pad = 3;
            public readonly InputManager Im;
            public readonly UserDevice Device;
            public readonly ManualResetEventSlim InCreate = new(false);
            public readonly ManualResetEventSlim LetItFail = new(false);
            public readonly List<string> Raised = new();
            public readonly List<string> Resolved = new();

            private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
            private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
            private readonly bool[] _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            private readonly bool[] _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            private readonly SlotOrdersScope _orders = new(midi: new[] { Pad });
            private readonly MethodInfo _step5 = typeof(InputManager)
                .GetMethod("UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic);

            public HeldMidiCreate()
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox));
                backend.OnCreate = (_, _, _) =>
                {
                    InCreate.Set();
                    LetItFail.Wait(30_000);
                    throw new MidiSessionUnavailableException();
                };
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => true;
                Assert.Equal(MidiServiceRestart.Running, MidiServiceRecovery.TryRestart("an earlier create"));
                Assert.True(MidiVirtualController.IsAvailable());

                var deviceGuid = new Guid("5c8e21f4-0d7a-4b96-a3e1-7f42c9b60d18");
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++) SettingsManager.SlotEnabled[i] = true;
                SettingsManager.SlotCreated[Pad] = true;
                Device = new UserDevice { InstanceGuid = deviceGuid, ProductName = "MIDI Pad", IsOnline = true, InputState = new CustomInputState() };
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(Device);
                lock (SettingsManager.UserSettings.SyncRoot)
                    SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = deviceGuid, MapTo = Pad });

                Im = new InputManager();
                Im.SlotControllerTypes[Pad] = VirtualControllerType.Midi;
                Im._midiConfigs[Pad] = new MidiSlotConfig { Channel = 1, StartCc = 1, CcCount = 1, StartNote = 60, NoteCount = 1, Velocity = 100 };
                Im.ErrorOccurred += (_, e) => { lock (Raised) Raised.Add(e.Message); };
                Im.ErrorResolved += (_, m) => { lock (Resolved) Resolved.Add(m); };
            }

            public T Field<T>(string name)
                => (T)typeof(InputManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Im);

            /// <summary>One Step 5 pass.</summary>
            public void Pass() => _step5.Invoke(Im, null);

            /// <summary>Waits for the slot's create worker to end.</summary>
            public void WaitForTheCreate()
                => Assert.True(SpinWait.SpinUntil(() =>
                {
                    var connect = Field<System.Threading.Tasks.Task[]>("_pendingConnectTask")[Pad];
                    return connect == null || connect.IsCompleted;
                }, 10_000), "the create worker did not finish");

            /// <summary>The status text of the line remembered for the slot,
            /// or null.</summary>
            public string Remembered
            {
                get
                {
                    object line = Field<object[]>("_midiCreateError")[Pad];
                    return (string)line?.GetType().GetField("Status").GetValue(line);
                }
            }
            public bool Latched => Field<bool[]>("_createFailed")[Pad];

            public void Dispose()
            {
                LetItFail.Set();
                WaitForTheCreate();
                Field<IVirtualController[]>("_virtualControllers")[Pad]?.Dispose();
                SettingsManager.UserSettings = _savedSettings;
                SettingsManager.UserDevices = _savedDevices;
                Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
                Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
                _orders.Dispose();
            }
        }

        /// <summary>A MIDI create that fails after its slot lost its device
        /// raises its line and takes no latch, since the slot is no longer
        /// eligible. The clear for a retype sat under the latch, so the line
        /// stayed on the status bar after the slot had become another type.
        /// It clears on the next pass, latch or no latch.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AMidiCreateFailure_ClearsOnceTheSlotIsRetyped_LatchedOrNot(bool deviceLeavesDuringTheCreate)
        {
            using var rig = new HeldMidiCreate();
            rig.Pass();
            Assert.True(rig.InCreate.Wait(10_000));

            // In one row the slot's one controller drops out while the
            // create is under way. The create then fails.
            if (deviceLeavesDuringTheCreate) rig.Device.IsOnline = false;
            rig.LetItFail.Set();
            rig.WaitForTheCreate();
            string line;
            lock (rig.Raised) line = Assert.Single(rig.Raised);
            Assert.Equal(line, rig.Remembered);
            Assert.Equal(!deviceLeavesDuringTheCreate, rig.Latched);
            // The device is away from here on in both rows, so no later pass
            // builds anything.
            rig.Device.IsOnline = false;
            rig.Pass();
            lock (rig.Resolved) Assert.Empty(rig.Resolved);

            // The user switches the slot to another type.
            rig.Im.SlotControllerTypes[HeldMidiCreate.Pad] = VirtualControllerType.KeyboardMouse;
            rig.Pass();
            lock (rig.Resolved) Assert.Equal(new[] { line }, rig.Resolved);
            Assert.Null(rig.Remembered);
            Assert.Null(rig.Field<IVirtualController[]>("_virtualControllers")[HeldMidiCreate.Pad]);
        }

        /// <summary>A slot retyped while its MIDI create is under way gets
        /// the failure's line after the retype, for a port the slot no
        /// longer wants. The create resolves it as it returns, with no pass
        /// in between.</summary>
        [Fact]
        public void AMidiCreateThatFailsAfterItsSlotWasRetyped_ResolvesItsLineAsItReturns()
        {
            using var rig = new HeldMidiCreate();
            rig.Pass();
            Assert.True(rig.InCreate.Wait(10_000));

            // Retyped during the create, with its device still online. No
            // pass runs after this, so nothing of the new type is built.
            rig.Im.SlotControllerTypes[HeldMidiCreate.Pad] = VirtualControllerType.KeyboardMouse;
            rig.LetItFail.Set();
            rig.WaitForTheCreate();

            string line;
            lock (rig.Raised) line = Assert.Single(rig.Raised);
            lock (rig.Resolved) Assert.Equal(new[] { line }, rig.Resolved);
            Assert.Null(rig.Remembered);
            Assert.False(rig.Latched);
        }

        /// <summary>The diagnostics log is read by people who need not share
        /// the user's language, so a failed create's ERROR line, and the
        /// RECOVERED line once it clears, are English and name the controller
        /// type and the slot, as the ERROR line did. The status text the user
        /// sees stays in the UI language. Both lines were the status text, in
        /// the UI language, with the controller type left out.</summary>
        [Fact]
        public void AFailedMidiCreatesLogLines_AreEnglish_AndNameTheTypeAndTheSlot_WhateverTheUiLanguage()
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                PadForge.Resources.Strings.Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("de"));
                var strings = PadForge.Resources.Strings.Strings.Instance;
                string status = string.Format(strings.Status_MidiCreateFailed_Format, string.Format(strings.Main_VirtualController_Format, 1));
                // The German line, so the checks below tell the two apart.
                Assert.DoesNotContain("could not start", status);
                const string english = "Failed to create Midi virtual controller for Virtual Controller 1 (pad 3)";

                using var rig = new HeldMidiCreate();
                var log = LogAfterMark();
                rig.Pass();
                Assert.True(rig.InCreate.Wait(10_000));
                rig.LetItFail.Set();
                rig.WaitForTheCreate();

                lock (rig.Raised) Assert.Equal(new[] { status }, rig.Raised);
                Assert.Single(log(), l => l.Contains(" ERROR " + english + ": PadForge.Common.Input.MidiSessionUnavailableException", StringComparison.Ordinal));
                Assert.DoesNotContain(log(), l => l.Contains(status, StringComparison.Ordinal));

                // Retyped, with its device away so nothing of the new type is
                // built: the line clears.
                rig.Device.IsOnline = false;
                rig.Im.SlotControllerTypes[HeldMidiCreate.Pad] = VirtualControllerType.KeyboardMouse;
                rig.Pass();
                lock (rig.Resolved) Assert.Equal(new[] { status }, rig.Resolved);
                Assert.Single(log(), l => l.EndsWith(" RECOVERED " + english, StringComparison.Ordinal));
            }
            finally { PadForge.Resources.Strings.Strings.ChangeCulture(before); }
        }

        /// <summary>The same for a failed create of any other type, and for
        /// a MIDI slot that finds no MIDI API running. A slot in no group's
        /// order is named by its pad, as the UI names it.</summary>
        [Fact]
        public void OtherFailedCreatesLogLines_AreEnglish_AndNameTheTypeAndTheSlot()
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                PadForge.Resources.Strings.Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("de"));
                var strings = PadForge.Resources.Strings.Strings.Instance;
                using var orders = new SlotOrdersScope();
                var im = new InputManager();
                var raised = new List<string>();
                im.ErrorOccurred += (_, e) => { lock (raised) raised.Add(e.Message); };
                var log = LogAfterMark();

                var raise = typeof(InputManager).GetMethod("RaiseCreateError", BindingFlags.Instance | BindingFlags.NonPublic);
                raise.Invoke(im, new object[] { 5, VirtualControllerType.Xbox, new InvalidOperationException("the driver said no") });
                lock (raised)
                    Assert.Equal(string.Format(strings.Status_SlotCreateFailed_Format, string.Format(strings.Main_VirtualController_Format, 6)), Assert.Single(raised));
                Assert.Single(log(), l => l.Contains(
                    " ERROR Failed to create Xbox virtual controller for Virtual Controller 6 (pad 5): System.InvalidOperationException: the driver said no",
                    StringComparison.Ordinal));

                // No MIDI API runs, so a MIDI slot's create stops before it
                // makes anything.
                MidiVirtualController.UseBackendFactoryForTest(() => null);
                im._midiConfigs[4] = new MidiSlotConfig();
                var create = typeof(InputManager).GetMethod("CreateMidiController", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.Null(create.Invoke(im, new object[] { 4 }));
                lock (raised)
                    Assert.Equal(string.Format(strings.Status_MidiNotRunning_Format, string.Format(strings.Main_VirtualController_Format, 5)), raised[^1]);
                Assert.Single(log(), l => l.EndsWith(" ERROR Failed to create Midi virtual controller for Virtual Controller 5 (pad 4):", StringComparison.Ordinal));
            }
            finally { PadForge.Resources.Strings.Strings.ChangeCulture(before); }
        }

        /// <summary>A send that throws fails that message, never the send
        /// worker or the polling thread, and the controller stays connected.
        /// A refused Note On is dropped. A refused CC or Note Off goes out
        /// again on the next submit
        /// (ARefusedNoteOffOrCc_IsSentAgain_ButARefusedNoteOnIsNot).</summary>
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

        // ── Audit 2026-10-08: the probe, its recovery and the latch ──

        /// <summary>Runs <paramref name="body"/> on a pool thread and says
        /// whether it finished within <paramref name="ms"/>. An event wait,
        /// so the test thread never runs the body itself.</summary>
        private static bool Within(int ms, Action body)
        {
            var done = new ManualResetEventSlim(false);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { body(); }
                finally { done.Set(); }
            });
            return done.Wait(ms);
        }

        private static bool RecoveryIdle()
            => (int)typeof(MidiVirtualController).GetField("s_probeRecoveryRunning", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null) == 0;

        /// <summary>Writes a mark of its own to the diagnostics ring and
        /// returns a reader of the lines written after it. The ring is
        /// process-wide and never cleared, so a line an earlier test left
        /// must not satisfy a later one.</summary>
        private static Func<List<string>> LogAfterMark()
        {
            string mark = "MidiBackendLifecycleTests mark " + Guid.NewGuid().ToString("N");
            SdlDiagLog.WriteLine(mark);
            return () =>
            {
                var lines = SdlDiagLog.Snapshot().Split('\n').Select(l => l.TrimEnd()).ToList();
                int at = lines.FindIndex(l => l.EndsWith(mark, StringComparison.Ordinal));
                Assert.True(at >= 0, "the mark rolled out of the diagnostics ring");
                return lines.Skip(at + 1).ToList();
            };
        }

        /// <summary>The probe's answer is one volatile word, as the class's
        /// other cross-thread state is. A Nullable&lt;bool&gt; was two bytes an
        /// optimized reader could see torn mid-reset.</summary>
        [Fact]
        public void TheProbeState_IsOneVolatileWord()
        {
            var field = typeof(MidiVirtualController).GetField("_probeState", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            Assert.Equal(typeof(int), field.FieldType);
            Assert.Contains(typeof(System.Runtime.CompilerServices.IsVolatile), field.GetRequiredCustomModifiers());
        }

        /// <summary>A hung probe that the first restarts do not free keeps
        /// getting a restart once per cooldown until one does. The recovery
        /// gave up after one restart and left MIDI off for the
        /// session.</summary>
        [Fact]
        public void AHungProbe_KeepsRecovering_UntilARestartFreesIt()
        {
            var gate = new ManualResetEventSlim(false);
            try
            {
                Use(new FakeBackend(MidiApiKind.InBox) { StartGate = gate });
                int restarts = 0;
                MidiServiceRecovery.CooldownMs = 300;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () =>
                {
                    if (Interlocked.Increment(ref restarts) == 3) gate.Set();
                    return true;
                };
                ProbeBounds(200);
                MidiVirtualController.HungProbeReturnMs = 200;

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    MidiVirtualController.IsAvailable();
                    return MidiVirtualController.ActiveApi == MidiApiKind.InBox;
                }, 10_000));
                Assert.Equal(3, Volatile.Read(ref restarts));
            }
            finally { gate.Set(); }
        }

        // ── An uninstall's release while the recovery frees a hung probe ──
        //
        // The user clicks Uninstall while the recovery restarts a hung
        // service. Once the restart frees the probe, the release and the
        // recovery's reset and re-probe all want the availability lock, and
        // a monitor hands it over in no fixed order. Each order is driven
        // here in turn. In every one the runtime ends released for good:
        // the latch is up, every backend that started was stopped, and no
        // later ask starts one.

        private static void AssertReleasedForGood(FakeBackend backend, int starts)
        {
            Assert.True(MidiVirtualController.IsSuppressedForUninstall);
            Assert.Null(MidiVirtualController.Backend);
            Assert.Equal(starts, Volatile.Read(ref backend.Starts));
            Assert.Equal(starts, Volatile.Read(ref backend.Stops));
            // No probe kept an answer under the latch, not even that no API
            // started: the release left the probe not run.
            Assert.False(MidiVirtualController.ProbeFailed);
            Assert.Equal(MidiApiKind.None, MidiVirtualController.ActiveApi);
            for (int ask = 0; ask < 3; ask++) Assert.False(MidiVirtualController.IsAvailable());
            Assert.Equal(starts, Volatile.Read(ref backend.Starts));
            Assert.False(MidiVirtualController.ProbeFailed);
        }

        /// <summary>The release takes the lock first. The recovery's reset
        /// keeps the latch, and nothing probes again. A reset that lifted the
        /// latch let the re-probe load the runtime the uninstaller was
        /// removing.</summary>
        [Fact]
        public void AnUninstallDuringTheProbeRecovery_KeepsTheRuntimeReleased_WhenTheReleaseIsThroughFirst()
        {
            var gate = new ManualResetEventSlim(false);
            Thread release = null;
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk) { StartGate = gate });
                MidiServiceRecovery.ResetCooldownForTest();
                ProbeBounds(200);
                MidiVirtualController.HungProbeReturnMs = 10_000;
                var releaseAtTheLock = new ManualResetEventSlim(false);
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point == "latch-before-lock") releaseAtTheLock.Set();
                };
                MidiServiceRecovery.RecoverSeam = () =>
                {
                    // The release goes for the lock the hung probe holds.
                    // The restart frees the probe, and the release is
                    // through before the recovery goes on to its reset.
                    release = new Thread(MidiVirtualController.SuppressForUninstall) { IsBackground = true };
                    release.Start();
                    releaseAtTheLock.Wait(10_000);
                    gate.Set();
                    release.Join(10_000);
                    return true;
                };

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(SpinWait.SpinUntil(() => release != null && !release.IsAlive && RecoveryIdle(), 10_000));
                AssertReleasedForGood(backend, starts: 1);
            }
            finally { gate.Set(); }
        }

        /// <summary>The recovery's re-probe takes the lock first and starts
        /// the runtime once more. The release behind it stops that backend
        /// and sets the latch, so the uninstaller still starts with nothing
        /// loaded.</summary>
        [Fact]
        public void AnUninstallDuringTheProbeRecovery_KeepsTheRuntimeReleased_WhenTheReProbeIsThroughFirst()
        {
            var first = new ManualResetEventSlim(false);
            var second = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk) { StartGate = first });
                MidiServiceRecovery.ResetCooldownForTest();
                ProbeBounds(200);
                MidiVirtualController.HungProbeReturnMs = 10_000;
                MidiServiceRecovery.RecoverSeam = () =>
                {
                    // The restart frees the hung probe. The probe that
                    // follows is held in its start, so it has the lock when
                    // the release arrives.
                    backend.StartGate = second;
                    MidiVirtualController.ProbeTimeoutMs = FarOff;
                    first.Set();
                    return true;
                };

                Assert.False(MidiVirtualController.IsAvailable());
                // The re-probe is in its start, with the lock.
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.Starts) == 2, 10_000));
                var releaseAtTheLock = new ManualResetEventSlim(false);
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point == "latch-before-lock") releaseAtTheLock.Set();
                };
                var release = new Thread(MidiVirtualController.SuppressForUninstall) { IsBackground = true };
                release.Start();
                Assert.True(releaseAtTheLock.Wait(10_000));
                second.Set();
                Assert.True(release.Join(10_000));
                Assert.True(SpinWait.SpinUntil(RecoveryIdle, 10_000));
                AssertReleasedForGood(backend, starts: 2);
            }
            finally
            {
                first.Set();
                second.Set();
            }
        }

        /// <summary>The recovery's re-probe is started, and the release takes
        /// the lock ahead of it. The probe looks at the latch again once it
        /// has the lock, since the ask's own look came before the latch was
        /// set, and starts nothing.</summary>
        [Fact]
        public void AnUninstallDuringTheProbeRecovery_KeepsTheRuntimeReleased_WhenTheReProbeWaitsBehindTheRelease()
        {
            var gate = new ManualResetEventSlim(false);
            var reProbeAtTheLock = new ManualResetEventSlim(false);
            var released = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk) { StartGate = gate });
                MidiServiceRecovery.ResetCooldownForTest();
                ProbeBounds(200);
                MidiVirtualController.HungProbeReturnMs = 10_000;
                int probes = 0;
                MidiVirtualController.SyncPointForTest = point =>
                {
                    // The second probe is the recovery's. It stops short of
                    // the lock until the release is through.
                    if (point == "probe-before-lock" && Interlocked.Increment(ref probes) == 2)
                    {
                        reProbeAtTheLock.Set();
                        released.Wait(10_000);
                    }
                };
                MidiServiceRecovery.RecoverSeam = () =>
                {
                    MidiVirtualController.ProbeTimeoutMs = FarOff;
                    gate.Set();
                    return true;
                };

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(reProbeAtTheLock.Wait(10_000));
                MidiVirtualController.SuppressForUninstall();
                released.Set();
                Assert.True(SpinWait.SpinUntil(RecoveryIdle, 10_000));
                AssertReleasedForGood(backend, starts: 1);
            }
            finally
            {
                gate.Set();
                released.Set();
            }
        }

        // ── A probe slower than its bound, and a probe across a restart ──

        /// <summary>A start that is slow, not hung, answers by itself:
        /// Microsoft's connectivity check waits up to 10 s for the service's
        /// device enumeration before it answers (MidiSrvTransport.cpp), which
        /// is PadForge's own bound for an ask. The recovery watches the probe
        /// up to the create path's bound before it restarts anything, and the
        /// late answer is kept. The answer was thrown away and the service
        /// that had just given it was restarted.</summary>
        [Fact]
        public void AProbeThatAnswersAfterItsBound_IsKept_AndNothingIsRestarted()
        {
            var gate = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { StartGate = gate });
                int restarts = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
                // An ask gives up after 100 ms. The restart waits for the
                // create path's bound, which is far off.
                ProbeBounds(100, restartAtMs: FarOff);
                var watching = new ManualResetEventSlim(false);
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point == "recovery-watching") watching.Set();
                };
                int generation = MidiVirtualController.Generation;

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(MidiVirtualController.ProbeFailed);
                // The answer comes while the recovery watches the probe.
                Assert.True(watching.Wait(10_000));
                gate.Set();

                Assert.True(SpinWait.SpinUntil(() => MidiVirtualController.ActiveApi == MidiApiKind.InBox, 10_000));
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.False(MidiVirtualController.ProbeFailed);
                Assert.True(SpinWait.SpinUntil(RecoveryIdle, 10_000));
                Assert.Equal(0, Volatile.Read(ref restarts));
                Assert.Equal(1, Volatile.Read(ref backend.Starts));
                Assert.Equal(0, Volatile.Read(ref backend.Stops));
                Assert.Equal(generation, MidiVirtualController.Generation);
            }
            finally { gate.Set(); }
        }

        /// <summary>The recovery only watches a probe until it has been
        /// pending for the create path's bound, counted from the probe's
        /// start, and restarts the service from there.</summary>
        [Fact]
        public void TheRecovery_OnlyWatchesAProbe_UpToTheCreatePathsBound()
        {
            MidiVirtualController.ConnectTimeoutMs = 15_000;
            Assert.Equal(15_000, MidiVirtualController.ProbeWatchMs(probeStartedMs: 1_000, nowMs: 1_000));
            // An ask gave up after 10 s: 5 s of watching are left.
            Assert.Equal(5_000, MidiVirtualController.ProbeWatchMs(probeStartedMs: 1_000, nowMs: 11_000));
            Assert.Equal(0, MidiVirtualController.ProbeWatchMs(probeStartedMs: 1_000, nowMs: 16_000));
            Assert.Equal(0, MidiVirtualController.ProbeWatchMs(probeStartedMs: 1_000, nowMs: 600_000));
        }

        /// <summary>A create restarted the service a moment ago, and the
        /// probe that follows is slow on the new instance. The recovery's
        /// restart is refused inside the cooldown, and the probe is watched
        /// through the wait, so its answer is taken when it comes. The
        /// recovery slept the cooldown out without looking, dropped the
        /// answer, and then restarted a service that had answered two minutes
        /// before.</summary>
        [Fact]
        public void AProbeThatAnswersInsideARestartsCooldown_IsKept_AndTheServiceIsNotRestartedAgain()
        {
            var gate = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { StartGate = gate });
                int restarts = 0;
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
                Assert.Equal(MidiServiceRestart.Running, MidiServiceRecovery.TryRestart("an earlier create"));
                ProbeBounds(100);
                // The watch that follows the refused restart runs through
                // the cooldown, and the answer comes during it.
                int asks = 0;
                var watching = new ManualResetEventSlim(false);
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point == "recovery-before-restart") Interlocked.Increment(ref asks);
                    else if (point == "recovery-watching" && Volatile.Read(ref asks) == 1) watching.Set();
                };

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(watching.Wait(10_000));
                gate.Set();

                Assert.True(SpinWait.SpinUntil(() => MidiVirtualController.ActiveApi == MidiApiKind.InBox, 10_000));
                Assert.False(MidiVirtualController.ProbeFailed);
                Assert.True(SpinWait.SpinUntil(RecoveryIdle, 10_000));
                Assert.Equal(1, Volatile.Read(ref restarts));
                Assert.Equal(1, Volatile.Read(ref backend.Starts));
            }
            finally { gate.Set(); }
        }

        /// <summary>A PC whose every start runs past the bound and then
        /// answers still gets MIDI: the first probe is restarted under, the
        /// one after it answers late inside that restart's cooldown, and its
        /// answer is kept. Every probe was given up on and thrown away, so
        /// MIDI never came up and the service was restarted once per cooldown
        /// for as long as PadForge ran.</summary>
        [Fact]
        public void AStartSlowerThanItsBoundEveryTime_StillBringsMidiUp()
        {
            var first = new ManualResetEventSlim(false);
            var second = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { StartGate = first });
                int restarts = 0;
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    backend.StartGate = second;
                    first.Set();
                    return true;
                };
                ProbeBounds(100);
                MidiVirtualController.HungProbeReturnMs = 10_000;
                // The recovery's second restart is refused inside the first
                // one's cooldown, and the watch after it runs through that
                // cooldown.
                int asks = 0;
                var watching = new ManualResetEventSlim(false);
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point == "recovery-before-restart") Interlocked.Increment(ref asks);
                    else if (point == "recovery-watching" && Volatile.Read(ref asks) == 2) watching.Set();
                };

                Assert.False(MidiVirtualController.IsAvailable());
                // The second start is under way, and slow again.
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.Starts) == 2, 10_000));
                Assert.True(SpinWait.SpinUntil(() => MidiVirtualController.ProbeFailed, 10_000));
                Assert.True(watching.Wait(10_000));
                second.Set();

                Assert.True(SpinWait.SpinUntil(() => MidiVirtualController.ActiveApi == MidiApiKind.InBox, 10_000));
                Assert.True(SpinWait.SpinUntil(RecoveryIdle, 10_000));
                Assert.Equal(1, Volatile.Read(ref restarts));
                Assert.Equal(2, Volatile.Read(ref backend.Starts));
            }
            finally
            {
                first.Set();
                second.Set();
            }
        }

        /// <summary>A probe that was running when the service was restarted
        /// answers for neither instance: a Windows MIDI Services start the
        /// dying service fails lands on the legacy API, on a PC whose new
        /// instance would take it. Such a probe keeps nothing, and the next
        /// ask probes the instance that runs.</summary>
        [Fact]
        public void AProbeThatStraddlesAServiceRestart_KeepsNoAnswer()
        {
            var gate = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { StartGate = gate });
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => true;
                ProbeBounds(FarOff);
                bool answer = true;
                var asker = new Thread(() => answer = MidiVirtualController.IsAvailable()) { IsBackground = true };
                asker.Start();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.Starts) == 1, 10_000));

                // A create that hung restarts the service under the probe.
                Assert.Equal(MidiServiceRestart.Running, MidiServiceRecovery.TryRestart("a MIDI create hung"));
                gate.Set();
                Assert.True(asker.Join(10_000));

                Assert.False(answer);
                Assert.Equal(MidiApiKind.None, MidiVirtualController.ActiveApi);
                Assert.Null(MidiVirtualController.Backend);
                Assert.False(MidiVirtualController.ProbeFailed);
                Assert.Equal(1, backend.Stops);

                Assert.True(MidiVirtualController.IsAvailable());
                Assert.Equal(2, backend.Starts);
                Assert.Equal(MidiApiKind.InBox, MidiVirtualController.ActiveApi);
            }
            finally { gate.Set(); }
        }

        /// <summary>A probe can answer in the instant before the recovery
        /// restarts the service. Its answer is kept, since no restart had
        /// begun, and it is for the instance the restart then ends. So it
        /// does not stand: a new generation starts, and the next ask probes
        /// the instance that runs.</summary>
        [Fact]
        public void AnAnswerInTheInstantBeforeTheRestart_DoesNotStand()
        {
            var gate = new ManualResetEventSlim(false);
            var atTheRestart = new ManualResetEventSlim(false);
            var go = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { StartGate = gate });
                int restarts = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
                ProbeBounds(100);
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point != "recovery-before-restart") return;
                    atTheRestart.Set();
                    go.Wait(10_000);
                };
                int generation = MidiVirtualController.Generation;

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(atTheRestart.Wait(10_000));
                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => MidiVirtualController.ActiveApi == MidiApiKind.InBox, 10_000));
                go.Set();

                Assert.True(SpinWait.SpinUntil(RecoveryIdle, 10_000));
                Assert.Equal(1, Volatile.Read(ref restarts));
                Assert.Equal(MidiApiKind.None, MidiVirtualController.ActiveApi);
                Assert.Null(MidiVirtualController.Backend);
                Assert.Equal(1, backend.Stops);
                Assert.True(MidiVirtualController.Generation > generation);

                Assert.True(MidiVirtualController.IsAvailable());
                Assert.Equal(2, backend.Starts);
            }
            finally
            {
                gate.Set();
                go.Set();
            }
        }

        /// <summary>No probe starts inside a service restart PadForge is
        /// performing. One that did met a service that was stopping or
        /// starting, where a Windows MIDI Services API refuses, and the
        /// legacy API took its place: every MIDI slot rebuilt with no port.
        /// An ask answers no until the restart is over, and the next one
        /// probes.</summary>
        [Fact]
        public void NoProbeStartsInsideAServiceRestart()
        {
            var inRestart = new ManualResetEventSlim(false);
            var finish = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox));
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    inRestart.Set();
                    finish.Wait(10_000);
                    return MidiServiceRestart.Running;
                };
                var restart = new Thread(() => MidiVirtualController.RestartService("a MIDI create hung", legacyCall: false))
                    { IsBackground = true };
                restart.Start();
                Assert.True(inRestart.Wait(10_000));
                int probes = 0;
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point == "probe-before-lock") Interlocked.Increment(ref probes);
                };

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.Equal(0, Volatile.Read(ref backend.Starts));
                Assert.False(MidiVirtualController.ProbeFailed);
                // The ask answered by itself: no probe was even sent on its
                // way to the lock.
                Assert.Equal(0, Volatile.Read(ref probes));

                finish.Set();
                Assert.True(restart.Join(10_000));
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.Equal(1, backend.Starts);
                Assert.Equal(1, Volatile.Read(ref probes));
            }
            finally { finish.Set(); }
        }

        /// <summary>A probe an ask started just before a restart began
        /// reaches the lock with the restart in flight, and starts nothing
        /// there either.</summary>
        [Fact]
        public void AProbeOnItsWayToTheLock_StartsNothing_OnceARestartHasBegun()
        {
            var atTheLock = new ManualResetEventSlim(false);
            var inRestart = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var finish = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox));
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    inRestart.Set();
                    finish.Wait(10_000);
                    return MidiServiceRestart.Running;
                };
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point != "probe-before-lock") return;
                    atTheLock.Set();
                    release.Wait(10_000);
                };
                bool answer = true;
                var asker = new Thread(() => answer = MidiVirtualController.IsAvailable()) { IsBackground = true };
                asker.Start();
                Assert.True(atTheLock.Wait(10_000));

                var restart = new Thread(() => MidiVirtualController.RestartService("a MIDI create hung", legacyCall: false))
                    { IsBackground = true };
                restart.Start();
                Assert.True(inRestart.Wait(10_000));
                release.Set();
                Assert.True(asker.Join(10_000));

                Assert.False(answer);
                Assert.Equal(0, Volatile.Read(ref backend.Starts));
                Assert.Equal(MidiApiKind.None, MidiVirtualController.ActiveApi);

                finish.Set();
                Assert.True(restart.Join(10_000));
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.Equal(1, backend.Starts);
            }
            finally
            {
                release.Set();
                finish.Set();
            }
        }

        /// <summary>An ask keeps the recovery of a hung probe going inside a
        /// service restart too. The recovery is best effort, so one that
        /// ended early starts again with the next ask, and it is what frees
        /// the probe. An ask that answered for the restart first never
        /// reached it.</summary>
        [Fact]
        public void AnAskInsideARestart_KeepsAHungProbesRecoveryGoing()
        {
            var gate = new ManualResetEventSlim(false);
            var inRestart = new ManualResetEventSlim(false);
            var finish = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { StartGate = gate });
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    inRestart.Set();
                    finish.Wait(10_000);
                    return MidiServiceRestart.Unchanged;
                };
                ProbeBounds(100);
                int looks = 0;
                MidiVirtualController.SyncPointForTest = point =>
                {
                    // The recovery's first run ends here, before it restarts
                    // anything.
                    if (point == "recovery-before-restart" && Interlocked.Increment(ref looks) == 1)
                        throw new InvalidOperationException("the recovery ended early");
                };

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref looks) == 1 && RecoveryIdle(), 10_000));
                Assert.True(MidiVirtualController.ProbeFailed);

                // A create that hung restarts the service, and an ask lands
                // inside that restart.
                var restart = new Thread(() => MidiVirtualController.RestartService("a MIDI create hung", legacyCall: false))
                    { IsBackground = true };
                restart.Start();
                Assert.True(inRestart.Wait(10_000));
                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref looks) == 2, 10_000),
                    "the ask did not start the recovery again");

                // The probe returns, and the recovery ends with it.
                gate.Set();
                Assert.True(SpinWait.SpinUntil(RecoveryIdle, 10_000));
                Assert.False(MidiVirtualController.ProbeFailed);
                finish.Set();
                Assert.True(restart.Join(10_000));
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.Equal(2, backend.Starts);
            }
            finally
            {
                gate.Set();
                finish.Set();
            }
        }

        // ── The uninstall latch and who holds it ──

        /// <summary>The latch belongs to the releases that hold it. A release
        /// that missed its bound undoes itself when it finishes, and that
        /// undo lifted the latch a second Uninstall click had taken since,
        /// while its uninstaller ran. The install's switch lifted it too.
        /// It stands until every release that holds it has ended, and no
        /// other reset lifts it.</summary>
        [Fact]
        public void TheUninstallLatch_StandsUntilEveryReleaseThatHoldsItHasEnded()
        {
            var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
            Assert.True(MidiVirtualController.IsAvailable());

            MidiVirtualController.SuppressForUninstall();
            MidiVirtualController.SuppressForUninstall();
            // The install's switch and every service recovery reset, and
            // neither lifts the latch.
            MidiVirtualController.ResetAvailability();
            Assert.True(MidiVirtualController.IsSuppressedForUninstall);
            Assert.False(MidiVirtualController.IsAvailable());

            Assert.False(MidiVirtualController.EndUninstallHold());
            Assert.True(MidiVirtualController.IsSuppressedForUninstall);
            Assert.False(MidiVirtualController.IsAvailable());
            Assert.Equal(1, backend.Starts);

            Assert.True(MidiVirtualController.EndUninstallHold());
            Assert.False(MidiVirtualController.IsSuppressedForUninstall);
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(2, backend.Starts);

            // An end with nothing held changes nothing.
            int generation = MidiVirtualController.Generation;
            Assert.False(MidiVirtualController.EndUninstallHold());
            Assert.Equal(generation, MidiVirtualController.Generation);
            Assert.Equal(MidiApiKind.AppSdk, MidiVirtualController.ActiveApi);
        }

        /// <summary>An uninstaller that outlived its wait still runs, and
        /// holds the latch until it exits. A second Uninstall meanwhile is
        /// refused by Windows Installer, which runs one installation at a
        /// time, and that failure ended the release with a reset that lifted
        /// the latch and resumed MIDI input under the first uninstaller. Each
        /// uninstall ends only the hold it took.</summary>
        [Fact]
        public void ASecondUninstall_WhileTheFirstUninstallerStillRuns_LeavesTheLatchToTheFirst()
        {
            var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
            Assert.True(MidiVirtualController.IsAvailable());
            int down = 0, resumed = 0, refreshed = 0;
            Action shutdown = () => Interlocked.Increment(ref down);
            Action resume = () => Interlocked.Increment(ref resumed);
            Action refresh = () => Interlocked.Increment(ref refreshed);

            // The first uninstaller outlives its wait.
            var exited = new System.Threading.Tasks.TaskCompletionSource();
            MidiRuntimeSwitch.RunUninstall(() => (false, exited.Task), shutdown, resume, refresh);
            Assert.True(MidiVirtualController.IsSuppressedForUninstall);
            Assert.True(MidiRuntimeSwitch.UninstallerRunning);
            Assert.Equal(0, Volatile.Read(ref resumed));

            // The second one is refused while the first still runs.
            var refused = Assert.Throws<InstallerFailedException>(() => MidiRuntimeSwitch.RunUninstall(
                () => throw new InstallerFailedException(1618), shutdown, resume, refresh));
            Assert.Equal(1618, refused.ExitCode);
            Assert.True(MidiVirtualController.IsSuppressedForUninstall);
            Assert.True(MidiRuntimeSwitch.UninstallerRunning);
            Assert.Equal(0, Volatile.Read(ref resumed));
            for (int ask = 0; ask < 3; ask++) Assert.False(MidiVirtualController.IsAvailable());
            Assert.Equal(1, Volatile.Read(ref backend.Starts));

            // The first uninstaller exits.
            exited.SetResult();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref refreshed) == 1, 10_000));
            Assert.False(MidiVirtualController.IsSuppressedForUninstall);
            Assert.False(MidiRuntimeSwitch.UninstallerRunning);
            Assert.Equal(1, Volatile.Read(ref resumed));
            Assert.Equal(2, Volatile.Read(ref down));
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(2, backend.Starts);
        }

        /// <summary>The card offers no second Uninstall while a runtime
        /// uninstaller PadForge started has not exited. One that outlived its
        /// five-minute wait left the card usable with the runtime still
        /// listed and Uninstall still on offer.</summary>
        [Fact]
        public void TheCard_OffersNoUninstall_WhileAnUninstallerStillRuns()
        {
            var vm = new SettingsViewModel { HasAnyMidiSlots = () => false };
            vm.IsMidiRuntimeInstalled = true;
            int changes = 0;
            vm.UninstallMidiServicesCommand.CanExecuteChanged += (_, _) => changes++;
            Assert.True(vm.UninstallMidiServicesCommand.CanExecute(null));

            vm.MidiUninstallerRunning = true;
            Assert.False(vm.UninstallMidiServicesCommand.CanExecute(null));
            Assert.Equal(1, changes);

            vm.MidiUninstallerRunning = false;
            Assert.True(vm.UninstallMidiServicesCommand.CanExecute(null));
            Assert.Equal(2, changes);
        }

        /// <summary>The card offers no Install while a runtime uninstaller
        /// PadForge started has not exited either. Once the registry no
        /// longer listed the runtime, Install was on offer, and Windows
        /// Installer refused it with 1618 since one installation runs at a
        /// time.</summary>
        [Fact]
        public void TheCard_OffersNoInstall_WhileAnUninstallerStillRuns()
        {
            var vm = new SettingsViewModel { HasAnyMidiSlots = () => false };
            vm.CanInstallMidiRuntime = true;
            int changes = 0;
            vm.InstallMidiRuntimeCommand.CanExecuteChanged += (_, _) => changes++;
            Assert.True(vm.InstallMidiRuntimeCommand.CanExecute(null));

            var strings = PadForge.Resources.Strings.Strings.Instance;
            var tooltips = new List<string>();
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.MidiInstallTooltip)) tooltips.Add(vm.MidiInstallTooltip); };
            Assert.Equal(strings.Settings_MidiInstallTooltip, vm.MidiInstallTooltip);

            vm.MidiUninstallerRunning = true;
            Assert.False(vm.InstallMidiRuntimeCommand.CanExecute(null));
            Assert.Equal(1, changes);
            Assert.Equal(strings.Settings_MidiInstallAfterUninstaller, vm.MidiInstallTooltip);

            vm.MidiUninstallerRunning = false;
            Assert.True(vm.InstallMidiRuntimeCommand.CanExecute(null));
            Assert.Equal(2, changes);
            Assert.Equal(new[] { strings.Settings_MidiInstallAfterUninstaller, strings.Settings_MidiInstallTooltip }, tooltips);
        }

        // The tick count a release reads once a test has taken its clock. It
        // moves only when the test moves it.
        private long _releaseClockMs = 1_000_000;

        private void UseReleaseClock()
            => MidiRuntimeSwitch.TickSeam = () => Interlocked.Read(ref _releaseClockMs);

        private void AdvanceReleaseClock(long ms) => Interlocked.Add(ref _releaseClockMs, ms);

        /// <summary>Makes the next release give up once its worker is
        /// <paramref name="there"/>. The release reads a clock the test
        /// moves: the call is held at its wait until the worker is there,
        /// and the clock then jumps past the call's bound and past the
        /// create path's bound. So the call finds its bound run out and a
        /// worker that has been at work that long, however the two threads
        /// are scheduled and with no time waited. A call that waits for an
        /// earlier release gives up on it the same way.
        /// <paramref name="also"/> sees every sync point.</summary>
        private void GiveUpOnceTheWorkerIs(Func<bool> there, Action<string> also = null)
        {
            UseReleaseClock();
            MidiRuntimeSwitch.ReleaseTimeoutMs = 60_000;
            MidiVirtualController.SyncPointForTest = point =>
            {
                also?.Invoke(point);
                if (point == "release-waits") SpinWait.SpinUntil(there, 30_000);
                if (point is "release-waits" or "release-waits-for-earlier")
                    AdvanceReleaseClock(Math.Max((long)MidiRuntimeSwitch.ReleaseTimeoutMs, MidiVirtualController.ConnectTimeoutMs));
            };
        }

        private static bool InputsSuppressed(InputManager im)
            => (bool)typeof(InputManager).GetField("_midiInputsSuppressed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im);

        /// <summary>The first Uninstall misses its bound behind a probe that
        /// hangs, and Uninstall is selected again while that release is
        /// still parked. One release is in flight at a time: the second call
        /// waits for the first and only then starts its own worker, so the
        /// first is done with MIDI input and the latch before the second
        /// touches either. The two ran side by side: the first one's undo
        /// lifted the latch the second had set and resumed the inputs it had
        /// shut down, with the second's uninstaller already running. The
        /// first, given up on before it reached MIDI input, closes nothing
        /// and latches nothing.</summary>
        [Fact]
        public void ASecondUninstall_WaitsForARelease_StillParkedBehindAHungProbe()
        {
            var gate = new ManualResetEventSlim(false);
            var free = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk) { StartGate = gate });
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => { free.Wait(10_000); gate.Set(); return true; };
                ProbeBounds(100);
                MidiVirtualController.HungProbeReturnMs = 10_000;
                int workers = 0;
                var firstAtTheLock = new ManualResetEventSlim(false);
                var secondWaits = new ManualResetEventSlim(false);
                var events = new List<string>();
                Action down = () => { lock (events) events.Add("down"); };
                Action up = () => { lock (events) events.Add("up"); };
                Assert.False(MidiVirtualController.IsAvailable());

                Action<string> track = point =>
                {
                    if (point == "release-worker") Interlocked.Increment(ref workers);
                    else if (point == "hold-before-lock") firstAtTheLock.Set();
                    else if (point == "release-waits-for-earlier") secondWaits.Set();
                };

                // The first release gives up with its worker on its way to
                // the lock the hung probe holds, before it has closed MIDI
                // input.
                GiveUpOnceTheWorkerIs(() => firstAtTheLock.IsSet, track);
                Assert.False(MidiRuntimeSwitch.ReleaseForUninstall(down, up, out _));
                lock (events) Assert.Empty(events);

                MidiVirtualController.SyncPointForTest = track;
                MidiRuntimeSwitch.ReleaseTimeoutMs = FarOff;
                bool second = false;
                var hold = MidiUninstallHold.None;
                var click = new Thread(() => second = MidiRuntimeSwitch.ReleaseForUninstall(down, up, out hold)) { IsBackground = true };
                click.Start();
                // The second call waits for the first, and has started no
                // worker of its own.
                Assert.True(secondWaits.Wait(10_000), "the second release did not wait for the first");
                Assert.Equal(1, Volatile.Read(ref workers));
                lock (events) Assert.Empty(events);

                free.Set();
                Assert.True(click.Join(10_000));
                Assert.True(second);
                Assert.Equal(MidiUninstallHold.Released, hold);
                // The first took nothing. The second then shut the inputs
                // down and took the latch for its uninstaller.
                lock (events) Assert.Equal(new[] { "down" }, events);
                Assert.Equal(2, Volatile.Read(ref workers));
                Assert.True(SpinWait.SpinUntil(RecoveryIdle, 10_000));
                Assert.True(MidiVirtualController.IsSuppressedForUninstall);
                Assert.Null(MidiVirtualController.Backend);
                Assert.Equal(Volatile.Read(ref backend.Starts), Volatile.Read(ref backend.Stops));
                int starts = Volatile.Read(ref backend.Starts);
                for (int ask = 0; ask < 3; ask++) Assert.False(MidiVirtualController.IsAvailable());
                Assert.Equal(starts, Volatile.Read(ref backend.Starts));

                // The uninstaller exits.
                MidiRuntimeSwitch.EndUninstall(hold, up);
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);
                lock (events) Assert.Equal(new[] { "down", "up" }, events);
            }
            finally
            {
                free.Set();
                gate.Set();
            }
        }

        /// <summary>A second Uninstall that waited for an earlier release
        /// gives the worker it then starts a bound of its own. The call had
        /// one bound for both waits, so a worker started as that bound ran
        /// out was given up on at once, though it was going through, and a
        /// restart of a service that had just answered was asked for on its
        /// account. Here the first release ends with a millisecond of the
        /// second call's bound left, and that millisecond is gone as the
        /// call reaches its own worker, which is held at its start. The call
        /// waits for the worker, takes its hold, and asks for nothing.</summary>
        [Fact]
        public void ASecondUninstall_GivesItsOwnWorkerABoundOfItsOwn()
        {
            var gate = new ManualResetEventSlim(false);
            var secondWorkerMayRun = new ManualResetEventSlim(false);
            var firstIsDone = new ManualResetEventSlim(false);
            try
            {
                Use(new FakeBackend(MidiApiKind.AppSdk) { StartGate = gate });
                int restarts = 0, asks = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    return MidiServiceRestart.Unchanged;
                };
                // The recovery only watches: the probe answers by itself.
                ProbeBounds(100, restartAtMs: FarOff);
                Assert.False(MidiVirtualController.IsAvailable());

                // The first Uninstall gives up with its worker behind the
                // hung probe.
                var firstAtTheLock = new ManualResetEventSlim(false);
                GiveUpOnceTheWorkerIs(() => firstAtTheLock.IsSet, point =>
                {
                    if (point == "hold-before-lock") firstAtTheLock.Set();
                });
                Assert.False(MidiRuntimeSwitch.ReleaseForUninstall(() => { }, () => { }, out _));

                // Selected again.
                var secondWaits = new ManualResetEventSlim(false);
                var secondAtItsWait = new ManualResetEventSlim(false);
                int workers = 0;
                MidiVirtualController.SyncPointForTest = point =>
                {
                    switch (point)
                    {
                        case "release-asks-for-restart":
                            Interlocked.Increment(ref asks);
                            break;
                        case "release-waits-for-earlier":
                            // Held here until the first release has ended.
                            secondWaits.Set();
                            firstIsDone.Wait(30_000);
                            break;
                        case "release-worker":
                            // The second call's worker, held at its start.
                            if (Interlocked.Increment(ref workers) == 1) secondWorkerMayRun.Wait(30_000);
                            break;
                        case "release-waits":
                            // The bound the call came in with is spent.
                            AdvanceReleaseClock(1);
                            secondAtItsWait.Set();
                            break;
                    }
                };
                MidiRuntimeSwitch.ReleaseTimeoutMs = 60_000;
                bool second = false;
                var hold = MidiUninstallHold.None;
                int down = 0;
                var click = new Thread(() => second = MidiRuntimeSwitch.ReleaseForUninstall(
                    () => Interlocked.Increment(ref down), () => { }, out hold)) { IsBackground = true };
                click.Start();
                Assert.True(secondWaits.Wait(10_000), "the second release did not wait for the first");

                // All but a millisecond of the second call's bound goes by.
                // Then the service answers: the probe returns, its answer is
                // kept, and the first release's worker undoes itself.
                AdvanceReleaseClock(MidiRuntimeSwitch.ReleaseTimeoutMs - 1);
                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => !MidiRuntimeSwitch.ReleaseInFlight, 10_000));
                firstIsDone.Set();

                // The call starts its worker and reaches its own wait with
                // the bound it came in with spent. Only then is the worker
                // let go.
                Assert.True(secondAtItsWait.Wait(10_000));
                secondWorkerMayRun.Set();
                Assert.True(click.Join(10_000));
                Assert.True(second, "the second release gave up on a worker that was going through");
                Assert.Equal(MidiUninstallHold.Released, hold);
                Assert.Equal(1, Volatile.Read(ref down));
                Assert.Equal(0, Volatile.Read(ref asks));
                Assert.True(MidiVirtualController.IsSuppressedForUninstall);

                MidiRuntimeSwitch.EndUninstall(hold, () => { });
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);
                Assert.True(SpinWait.SpinUntil(RecoveryIdle, 10_000));
                Assert.Equal(0, Volatile.Read(ref restarts));
            }
            finally
            {
                gate.Set();
                firstIsDone.Set();
                secondWorkerMayRun.Set();
            }
        }

        /// <summary>A restart is asked for only on behalf of a worker that
        /// has itself been at work for the create path's bound, 15 s. The
        /// call's own wait says nothing about the worker: one that began
        /// late and has been in the teardown for 10 s when the call's bound
        /// runs out is not the wedged-service signature yet, and one that
        /// has been there for 15 s is.</summary>
        [Theory]
        [InlineData(10_000, 0)]
        [InlineData(14_999, 0)]
        [InlineData(15_000, 1)]
        public void AReleaseThatGivesUp_AsksForTheRestart_OnlyForAWorkerAtWorkForTheCreatePathsBound(int workedMs, int expectedAsks)
        {
            var disposeGate = new ManualResetEventSlim(false);
            var workerMayStart = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk) { SessionDisposeGate = disposeGate });
                Assert.NotNull(MidiInputRuntime.Session);
                var session = backend.InputSession;
                int restarts = 0, asks = 0, resumed = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    return MidiServiceRestart.Unchanged;
                };
                MidiVirtualController.ConnectTimeoutMs = 15_000;
                UseReleaseClock();
                MidiRuntimeSwitch.ReleaseTimeoutMs = 60_000;
                MidiVirtualController.SyncPointForTest = point =>
                {
                    switch (point)
                    {
                        case "release-asks-for-restart":
                            Interlocked.Increment(ref asks);
                            break;
                        case "release-worker":
                            workerMayStart.Wait(30_000);
                            break;
                        case "release-waits":
                            // The worker begins late in the call's bound, and
                            // has been in the teardown for workedMs when the
                            // bound runs out.
                            AdvanceReleaseClock(MidiRuntimeSwitch.ReleaseTimeoutMs - workedMs);
                            workerMayStart.Set();
                            SpinWait.SpinUntil(() => session.DisposeEntered, 30_000);
                            AdvanceReleaseClock(workedMs);
                            break;
                    }
                };

                Assert.False(MidiRuntimeSwitch.ReleaseForUninstall(() => MidiInputRuntime.Shutdown(),
                    () => Interlocked.Increment(ref resumed), out _));
                Assert.True(session.DisposeEntered);
                // MIDI input is resumed either way.
                Assert.Equal(1, Volatile.Read(ref resumed));
                Assert.Equal(expectedAsks, Volatile.Read(ref asks));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref restarts) == expectedAsks
                    && !MidiServiceRecovery.RestartInFlight, 10_000));

                disposeGate.Set();
                Assert.True(SpinWait.SpinUntil(() => !MidiRuntimeSwitch.ReleaseInFlight, 10_000));
                Assert.Equal(expectedAsks, Volatile.Read(ref restarts));
            }
            finally
            {
                disposeGate.Set();
                workerMayStart.Set();
            }
        }

        /// <summary>The first Uninstall misses its bound in the input
        /// session's teardown, which hangs on a wedged service, and Uninstall
        /// is selected again as the status line says. The second release
        /// found the session already taken, finished at once and let its
        /// uninstaller start, and when the hung teardown returned, the first
        /// release latched, undid itself and lifted the latch under that
        /// uninstaller. No release starts beside one still at work, so the
        /// second gives up too, and the next one runs alone.</summary>
        [Fact]
        public void ASecondUninstall_NeverStartsBesideARelease_StillInTheInputTeardown()
        {
            var disposeGate = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk) { SessionDisposeGate = disposeGate });
                Assert.NotNull(MidiInputRuntime.Session);
                var session = backend.InputSession;
                int restarts = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                // The restart is tried and leaves the service as it was. The
                // test opens the teardown itself.
                MidiServiceRecovery.RestartSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    return MidiServiceRestart.Unchanged;
                };
                int workers = 0, asks = 0;
                var events = new List<string>();
                Action down = () => { lock (events) events.Add("down"); MidiInputRuntime.Shutdown(); };
                Action up = () => { lock (events) events.Add("up"); };

                GiveUpOnceTheWorkerIs(() => session.DisposeEntered, point =>
                {
                    if (point == "release-worker") Interlocked.Increment(ref workers);
                    else if (point == "release-asks-for-restart") Interlocked.Increment(ref asks);
                });
                Assert.False(MidiRuntimeSwitch.ReleaseForUninstall(down, up, out _));
                lock (events) Assert.Equal(new[] { "down", "up" }, events);
                Assert.Equal(1, Volatile.Read(ref asks));

                // Selected again while the first is still in the teardown.
                // This call asks for the restart too, on behalf of the
                // release it waited for: the first ask may have met a
                // cooldown, or left the service wedged.
                Assert.False(MidiRuntimeSwitch.ReleaseForUninstall(down, up, out _));
                Assert.Equal(1, Volatile.Read(ref workers));
                Assert.Equal(2, Volatile.Read(ref asks));
                lock (events) Assert.Equal(new[] { "down", "up" }, events);
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);

                // The teardown returns, and the first release undoes itself.
                disposeGate.Set();
                Assert.True(SpinWait.SpinUntil(() => !MidiRuntimeSwitch.ReleaseInFlight, 10_000));
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);
                lock (events) Assert.Equal(new[] { "down", "up", "up" }, events);
                Assert.Equal(1, Volatile.Read(ref workers));
                // Both calls asked, and the restart's cooldown kept the two
                // asks to one restart.
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref restarts) == 1
                    && !MidiServiceRecovery.RestartInFlight, 10_000));

                // The next Uninstall runs alone and holds its latch.
                MidiVirtualController.SyncPointForTest = null;
                MidiRuntimeSwitch.ReleaseTimeoutMs = FarOff;
                Assert.True(MidiRuntimeSwitch.ReleaseForUninstall(down, up, out var hold));
                Assert.Equal(MidiUninstallHold.Released, hold);
                lock (events) Assert.Equal(new[] { "down", "up", "up", "down" }, events);
                AssertReleasedForGood(backend, starts: 1);
                MidiRuntimeSwitch.EndUninstall(hold, up);
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);
                Assert.Equal(1, Volatile.Read(ref restarts));
            }
            finally { disposeGate.Set(); }
        }

        /// <summary>The release gives up while the shared input session's
        /// teardown hangs on a wedged service, with the probe settled. MIDI
        /// input was left suppressed behind the hung call and nothing
        /// restarted the service, so input stayed off and every later
        /// Uninstall gave up behind the same call. The call that gives up
        /// resumes MIDI input at once and asks for the service restart, and
        /// the worker undoes itself when the teardown returns.</summary>
        [Fact]
        public void AReleaseThatGivesUpInTheInputTeardown_ResumesMidiInput_AndAsksForTheRestart()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var disposeGate = new ManualResetEventSlim(false);
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk) { SessionDisposeGate = disposeGate });
                backend.NormalEndpoints.Add((KeysId, "Test Keys"));
                int restarts = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    return MidiServiceRestart.Unchanged;
                };
                var im = new InputManager();
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                bool Sweep() => (bool)sweep.Invoke(im, null);
                Assert.True(SpinWait.SpinUntil(Sweep, 10_000));
                var first = backend.InputSession;
                Assert.True(Assert.Single(first.Connections).Opened);

                int asks = 0;
                GiveUpOnceTheWorkerIs(() => first.DisposeEntered, point =>
                {
                    if (point == "release-asks-for-restart") Interlocked.Increment(ref asks);
                });
                Assert.False(MidiRuntimeSwitch.ReleaseForUninstall(im.ShutdownMidiInputs, im.ResumeMidiInputs, out _));
                Assert.Equal(1, Volatile.Read(ref asks));

                // MIDI input is not left off behind the hung teardown: the
                // sweep opens it again on a session of its own.
                Assert.False(InputsSuppressed(im));
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    Sweep();
                    var session = backend.InputSession;
                    return !ReferenceEquals(session, first) && session.Connections.Count == 1 && session.Connections[0].Opened;
                }, 10_000));
                Assert.False(first.Disposed);
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);
                // The restart was asked for.
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref restarts) == 1
                    && !MidiServiceRecovery.RestartInFlight, 10_000));

                // The teardown returns. The release that was given up on
                // undoes itself and leaves nothing latched.
                disposeGate.Set();
                Assert.True(SpinWait.SpinUntil(() => first.Disposed && !MidiRuntimeSwitch.ReleaseInFlight, 10_000));
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);
                Assert.False(InputsSuppressed(im));
                Assert.True(MidiVirtualController.IsAvailable());
            }
            finally
            {
                disposeGate.Set();
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
        }

        /// <summary>The call can give up in the instant its worker reaches
        /// MIDI input: after the worker's own look at whether it was given
        /// up on, before the teardown has suppressed the input sweep. The
        /// call's resume then came first and was lost, the teardown
        /// suppressed input and hung on the wedged service, and nothing was
        /// left to resume it. A teardown whose release was given up on
        /// closes nothing and leaves the sweep running.</summary>
        [Fact]
        public void AReleaseGivenUpOnAsItsWorkerReachesMidiInput_ClosesNothing()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var disposeGate = new ManualResetEventSlim(false);
            var gaveUp = new ManualResetEventSlim(false);
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk) { SessionDisposeGate = disposeGate });
                backend.NormalEndpoints.Add((KeysId, "Test Keys"));
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () => MidiServiceRestart.Unchanged;
                var im = new InputManager();
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.True(SpinWait.SpinUntil(() => (bool)sweep.Invoke(im, null), 10_000));
                var session = backend.InputSession;
                var keys = Assert.Single(session.Connections);
                Assert.True(keys.Opened);

                // The worker has decided to close MIDI input and is about
                // to. The call gives up there, and the worker goes on only
                // once it has.
                bool atInputs = false;
                Action down = () =>
                {
                    Volatile.Write(ref atInputs, true);
                    gaveUp.Wait(30_000);
                    im.ShutdownMidiInputs();
                };
                GiveUpOnceTheWorkerIs(() => Volatile.Read(ref atInputs));
                Assert.False(MidiRuntimeSwitch.ReleaseForUninstall(down, im.ResumeMidiInputs, out _));
                gaveUp.Set();

                Assert.True(SpinWait.SpinUntil(() => session.DisposeEntered || !MidiRuntimeSwitch.ReleaseInFlight, 10_000));
                Assert.False(session.DisposeEntered);
                Assert.False(keys.Detached);
                Assert.False(InputsSuppressed(im));
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);
            }
            finally
            {
                disposeGate.Set();
                gaveUp.Set();
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
        }

        /// <summary>A hung probe has a recovery of its own, which restarts
        /// the service where a restart can reach the hang and waits for a
        /// classic driver where none can. A release that gives up behind
        /// such a probe asks for no restart on its own account.</summary>
        [Fact]
        public void AReleaseThatGivesUpBehindAHungProbe_LeavesTheRestartToTheProbesRecovery()
        {
            var gate = new ManualResetEventSlim(false);
            try
            {
                Use(new FakeBackend(MidiApiKind.AppSdk) { StartGate = gate });
                int restarts = 0, asks = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    return MidiServiceRestart.Unchanged;
                };
                // The recovery only watches: its own restart is far off.
                ProbeBounds(100, restartAtMs: FarOff);
                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(MidiVirtualController.ProbeHung);

                // The call gives up with its worker on its way to the lock
                // the probe holds, and at work for the create path's bound.
                var atTheLock = new ManualResetEventSlim(false);
                GiveUpOnceTheWorkerIs(() => atTheLock.IsSet, point =>
                {
                    if (point == "release-asks-for-restart") Interlocked.Increment(ref asks);
                    else if (point == "hold-before-lock") atTheLock.Set();
                });
                Assert.False(MidiRuntimeSwitch.ReleaseForUninstall(() => { }, () => { }, out _));
                Assert.Equal(0, Volatile.Read(ref asks));
                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => !MidiRuntimeSwitch.ReleaseInFlight && RecoveryIdle(), 10_000));
                Assert.Equal(0, Volatile.Read(ref restarts));
            }
            finally { gate.Set(); }
        }

        /// <summary>A release that gives up in the input session's teardown
        /// asks for the service restart whatever a probe waits on. The
        /// teardown is service RPC, and a probe that hangs in a classic MIDI
        /// driver meanwhile gets no restart from its own recovery. The
        /// release left the restart to that recovery, so nothing freed the
        /// teardown.</summary>
        [Fact]
        public void AReleaseThatGivesUpInTheInputTeardown_AsksForTheRestart_WhileAProbeHangsInAClassicDriver()
        {
            var disposeGate = new ManualResetEventSlim(false);
            var driverGate = new ManualResetEventSlim(false);
            var winMm = new MidiBackendLegacyTests.FakeWinMm { OutputCountGate = driverGate };
            try
            {
                var backend = new FakeBackend(MidiApiKind.AppSdk) { SessionDisposeGate = disposeGate };
                bool runtimeGone = false;
                MidiVirtualController.UseBackendFactoryForTest(
                    () => Volatile.Read(ref runtimeGone) ? null : backend, () => new MidiBackendLegacy(winMm));
                // Hybrid mode: Windows MIDI Services runs through the
                // service, and WinMM stays on the classic drivers.
                MidiApiSelection.ApiModeSeam = () => MidiApiSelection.ApiModeHybrid;
                Assert.NotNull(MidiInputRuntime.Session);
                var session = backend.InputSession;
                int restarts = 0, asks = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    return MidiServiceRestart.Unchanged;
                };
                ProbeBounds(100);
                MidiVirtualController.HungProbeReturnMs = FarOff;

                // The call is held at its wait until its worker is in the
                // teardown and a probe hangs in the classic driver. Then its
                // bound runs out.
                GiveUpOnceTheWorkerIs(() => MidiVirtualController.ProbeHungInDriver, point =>
                {
                    if (point == "release-asks-for-restart") Interlocked.Increment(ref asks);
                    if (point != "release-waits") return;
                    SpinWait.SpinUntil(() => session.DisposeEntered, 30_000);
                    Volatile.Write(ref runtimeGone, true);
                    MidiVirtualController.ResetAvailability();
                    MidiVirtualController.IsAvailable();
                });
                Assert.False(MidiRuntimeSwitch.ReleaseForUninstall(() => MidiInputRuntime.Shutdown(), () => { }, out _));
                Assert.True(session.DisposeEntered);
                Assert.True(MidiVirtualController.ProbeHungInDriver);
                Assert.Equal(1, Volatile.Read(ref asks));
                // The restart is made: the service is what the teardown
                // waits on, whatever the probe waits on.
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref restarts) == 1
                    && !MidiServiceRecovery.RestartInFlight, 10_000));
            }
            finally
            {
                disposeGate.Set();
                driverGate.Set();
            }
        }

        /// <summary>Once the teardown has returned, a release that waits
        /// behind a probe that hangs is the probe's recovery's again: the
        /// call that gives up there asks for no restart of its own, so a
        /// probe that is only slow keeps the time its recovery gives
        /// it.</summary>
        [Fact]
        public void AReleaseThatGivesUpPastTheInputTeardown_BehindAHungProbe_LeavesTheRestartToTheProbesRecovery()
        {
            var gate = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
                int restarts = 0, asks = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    return MidiServiceRestart.Unchanged;
                };
                // The recovery only watches: its own restart is far off.
                ProbeBounds(100, restartAtMs: FarOff);
                Assert.True(MidiVirtualController.IsAvailable());

                // A probe starts and hangs while the teardown runs, so the
                // worker comes out of the teardown behind it: it has left
                // the teardown once it goes for the latch, and the lock the
                // latch needs is the probe's.
                var atTheLatch = new ManualResetEventSlim(false);
                Action down = () =>
                {
                    backend.StartGate = gate;
                    MidiVirtualController.ResetAvailability();
                    MidiVirtualController.IsAvailable();
                };
                GiveUpOnceTheWorkerIs(() => atTheLatch.IsSet, point =>
                {
                    if (point == "release-asks-for-restart") Interlocked.Increment(ref asks);
                    else if (point == "latch-before-lock") atTheLatch.Set();
                });
                Assert.False(MidiRuntimeSwitch.ReleaseForUninstall(down, () => { }, out _));
                Assert.True(MidiVirtualController.ProbeHung);
                Assert.Equal(0, Volatile.Read(ref asks));

                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => !MidiRuntimeSwitch.ReleaseInFlight && RecoveryIdle(), 10_000));
                Assert.Equal(0, Volatile.Read(ref restarts));
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);
            }
            finally { gate.Set(); }
        }

        /// <summary>The end of one uninstall and a release under way run one
        /// at a time. The end lifts the latch and resumes MIDI input, and a
        /// resume that landed between a release closing MIDI input and
        /// latching let the next sweep open sessions on the runtime again as
        /// its uninstaller started.</summary>
        [Fact]
        public void TheEndOfAnUninstall_WaitsForAReleaseUnderWay()
        {
            var inShutdown = new ManualResetEventSlim(false);
            var finishShutdown = new ManualResetEventSlim(false);
            try
            {
                Use(new FakeBackend(MidiApiKind.AppSdk));
                Assert.True(MidiVirtualController.IsAvailable());
                var events = new List<string>();
                Action up = () => { lock (events) events.Add("up"); };
                MidiRuntimeSwitch.ReleaseTimeoutMs = FarOff;
                // The first uninstall holds the latch, its uninstaller still
                // running.
                Assert.True(MidiRuntimeSwitch.ReleaseForUninstall(() => { lock (events) events.Add("down"); }, up, out var first));

                // A second release is closing MIDI input.
                bool second = false;
                var secondHold = MidiUninstallHold.None;
                var release = new Thread(() => second = MidiRuntimeSwitch.ReleaseForUninstall(() =>
                {
                    lock (events) events.Add("down");
                    inShutdown.Set();
                    finishShutdown.Wait(30_000);
                }, up, out secondHold)) { IsBackground = true };
                release.Start();
                Assert.True(inShutdown.Wait(10_000));

                // The first uninstaller exits meanwhile. Its end goes for
                // the lock the release under way holds, and what the end
                // finds once it has that lock is recorded.
                var endAtTheLock = new ManualResetEventSlim(false);
                int endsInTheLock = 0;
                bool releaseWasStillAtWork = false;
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point == "end-before-release-lock") endAtTheLock.Set();
                    else if (point == "end-in-release-lock" && Interlocked.Increment(ref endsInTheLock) == 1)
                        Volatile.Write(ref releaseWasStillAtWork, MidiRuntimeSwitch.ReleaseInFlight);
                };
                var end = new Thread(() => MidiRuntimeSwitch.EndUninstall(first, up)) { IsBackground = true };
                end.Start();
                Assert.True(endAtTheLock.Wait(10_000));
                lock (events) Assert.Equal(new[] { "down", "down" }, events);
                Assert.True(MidiVirtualController.IsSuppressedForUninstall);

                finishShutdown.Set();
                Assert.True(release.Join(10_000));
                Assert.True(end.Join(10_000));
                Assert.True(second);
                Assert.False(Volatile.Read(ref releaseWasStillAtWork), "the end ran beside the release");
                // The second holds the latch by then, so the first one's end
                // lifted nothing and resumed nothing.
                Assert.True(MidiVirtualController.IsSuppressedForUninstall);
                lock (events) Assert.Equal(new[] { "down", "down" }, events);

                MidiRuntimeSwitch.EndUninstall(secondHold, up);
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);
                lock (events) Assert.Equal(new[] { "down", "down", "up" }, events);
            }
            finally { finishShutdown.Set(); }
        }

        /// <summary>A release that does not finish in time keeps the
        /// uninstaller from starting, and fails as a check the status line
        /// words in the UI language, with an English sentence of its own for
        /// the log. The handler threw the UI-language sentence itself, so
        /// the log's line carried it in whatever language the UI was
        /// in.</summary>
        [Fact]
        public void AReleaseThatGivesUp_StartsNoUninstaller_AndFailsAsATypedCheck()
        {
            var gate = new ManualResetEventSlim(false);
            try
            {
                Use(new FakeBackend(MidiApiKind.AppSdk) { StartGate = gate });
                ProbeBounds(100, restartAtMs: FarOff);
                Assert.False(MidiVirtualController.IsAvailable());
                MidiRuntimeSwitch.MarkInstallPending();
                var atTheLock = new ManualResetEventSlim(false);
                GiveUpOnceTheWorkerIs(() => atTheLock.IsSet, point =>
                {
                    if (point == "hold-before-lock") atTheLock.Set();
                });
                int uninstallers = 0;

                var ex = Assert.Throws<DriverCheckException>(() => MidiRuntimeSwitch.RunUninstall(() =>
                {
                    Interlocked.Increment(ref uninstallers);
                    return (true, null);
                }, () => { }, () => { }, null));
                Assert.Equal(DriverCheckFailure.MidiReleaseTimedOut, ex.Failure);
                Assert.Equal("MIDI was not released in time, so the runtime's uninstaller was not started.", ex.Message);
                Assert.Equal(0, Volatile.Read(ref uninstallers));
                Assert.False(MidiRuntimeSwitch.UninstallerRunning);
                Assert.False(MidiVirtualController.IsHeldForUninstall);
                // An uninstall forgets an install that was still watched for.
                Assert.False(MidiRuntimeSwitch.InstallPending);
            }
            finally { gate.Set(); }
        }

        /// <summary>A release that gives up behind a probe hung in a classic
        /// MIDI driver fails as the check whose line names the driver. The
        /// line named the Windows MIDI service, which does not run in Legacy
        /// API mode, and said to select Uninstall again in a moment, where
        /// nothing changes until the driver answers. Once it does, the
        /// worker that was given up on finds MIDI on the legacy API, takes
        /// the start hold an uninstall takes there, and gives it back.</summary>
        [Fact]
        public void AReleaseThatGivesUpBehindAClassicDriver_NamesTheDriver_AndLeavesNoStartHold()
        {
            var gate = new ManualResetEventSlim(false);
            var winMm = new MidiBackendLegacyTests.FakeWinMm { OutputCountGate = gate };
            try
            {
                MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(winMm));
                MidiApiSelection.ApiModeSeam = () => MidiApiSelection.ApiModeLegacy;
                ProbeBounds(100);
                MidiVirtualController.HungProbeReturnMs = FarOff;
                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(MidiVirtualController.ProbeHungInDriver);
                // The call gives up once its worker has gone to decide what
                // to hold, which waits on the lock the probe has.
                var atTheLock = new ManualResetEventSlim(false);
                GiveUpOnceTheWorkerIs(() => atTheLock.IsSet, point =>
                {
                    if (point == "hold-before-lock") atTheLock.Set();
                });
                int uninstallers = 0;

                var ex = Assert.Throws<DriverCheckException>(() => MidiRuntimeSwitch.RunUninstall(() =>
                {
                    Interlocked.Increment(ref uninstallers);
                    return (true, null);
                }, () => { }, () => { }, null));
                Assert.Equal(DriverCheckFailure.MidiReleaseTimedOutOnDriver, ex.Failure);
                Assert.Equal(0, Volatile.Read(ref uninstallers));

                // The driver answers. The worker takes its start hold and
                // undoes it, so nothing is left keeping Windows MIDI Services
                // from starting for the rest of the session.
                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => !MidiRuntimeSwitch.ReleaseInFlight, 10_000));
                Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);
                Assert.False(MidiVirtualController.IsHeldForUninstall);
            }
            finally { gate.Set(); }
        }

        /// <summary>The card's install switches MIDI onto the runtime once
        /// its installer is done, on the same worker. An installer PadForge
        /// could not follow is marked for the card's refresh instead, and an
        /// install that then finishes in time leaves nothing to watch for,
        /// so the refresh does not switch a second time.</summary>
        [Fact]
        public void TheCardsInstall_SwitchesOnceTheInstallerIsDone_AndWatchesOneItCouldNotFollow()
        {
            MidiRuntimeSwitch.ClearPendingInstall();
            int switched = 0;
            var order = new List<string>();
            MidiRuntimeSwitch.RunInstall(() => order.Add("install"), () => { order.Add("switch"); switched++; });
            Assert.Equal(new[] { "install", "switch" }, order);
            Assert.False(MidiRuntimeSwitch.InstallPending);

            var unknown = Assert.Throws<InstallerFailedException>(() =>
                MidiRuntimeSwitch.RunInstall(() => throw InstallerFailedException.Untracked(), () => switched++));
            Assert.True(unknown.OutcomeUnknown);
            Assert.Equal(1, switched);
            Assert.True(MidiRuntimeSwitch.InstallPending);

            MidiRuntimeSwitch.RunInstall(() => { }, () => switched++);
            Assert.Equal(2, switched);
            Assert.False(MidiRuntimeSwitch.InstallPending);
            Assert.False(MidiRuntimeSwitch.TakePendingSwitch(true, MidiApiKind.None, heldForUninstall: false));

            // A failure whose outcome is known is neither watched for nor
            // switched to.
            Assert.Throws<InstallerFailedException>(() =>
                MidiRuntimeSwitch.RunInstall(() => throw new InstallerFailedException(1603), () => switched++));
            Assert.Equal(2, switched);
            Assert.False(MidiRuntimeSwitch.InstallPending);
        }

        /// <summary>An install PadForge stopped following is not switched to
        /// while an uninstall holds anything. The registry still lists the
        /// runtime while its uninstaller runs, and the engine reads no API,
        /// or the legacy one, only because the runtime is held, so the
        /// card's refresh switched, and the switch's reset lifted the
        /// latch.</summary>
        [Fact]
        public void APendingInstall_IsNotSwitchedTo_WhileAnUninstallHoldsTheRuntime()
        {
            MidiRuntimeSwitch.ClearPendingInstall();
            try
            {
                Assert.False(MidiApiSelection.SwitchAfterPendingInstall(true, true, MidiApiKind.None, heldForUninstall: true));
                Assert.False(MidiApiSelection.SwitchAfterPendingInstall(true, true, MidiApiKind.Legacy, heldForUninstall: true));
                Assert.True(MidiApiSelection.SwitchAfterPendingInstall(true, true, MidiApiKind.None, heldForUninstall: false));

                MidiRuntimeSwitch.MarkInstallPending();
                Assert.False(MidiRuntimeSwitch.TakePendingSwitch(true, MidiApiKind.None, heldForUninstall: true));
                // The install is still watched, and switches once the
                // uninstall has let go.
                Assert.True(MidiRuntimeSwitch.InstallPending);
                Assert.True(MidiRuntimeSwitch.TakePendingSwitch(true, MidiApiKind.None, heldForUninstall: false));
            }
            finally { MidiRuntimeSwitch.ClearPendingInstall(); }
        }

        // ── An uninstall under the legacy API ──

        /// <summary>The runtime is installed, its backend refuses to start,
        /// and MIDI runs on the legacy API over a stand-in for
        /// WinMM.</summary>
        private static FakeBackend UseRefusedWindowsMidiServices()
        {
            var wms = new FakeBackend(MidiApiKind.AppSdk) { StartResult = false };
            var winMm = new MidiBackendLegacyTests.FakeWinMm();
            MidiVirtualController.UseBackendFactoryForTest(() => wms, () => new MidiBackendLegacy(winMm));
            return wms;
        }

        /// <summary>A PC that has the runtime and still runs the legacy API
        /// got there because the runtime's backend did not start. Its
        /// uninstall leaves MIDI running and holds only Windows MIDI Services
        /// from starting until the uninstaller exits. Nothing was held
        /// there, so the check that follows a refusal went on starting the
        /// runtime's backend on its timer while the uninstaller removed its
        /// files, and so did any probe after a reset.</summary>
        [Fact]
        public void AnUninstallUnderTheLegacyApi_LeavesMidiRunning_AndStartsNoWindowsMidiServices()
        {
            var running = new ManualResetEventSlim(false);
            var finish = new ManualResetEventSlim(false);
            Thread uninstall = null;
            try
            {
                var wms = UseRefusedWindowsMidiServices();
                MidiVirtualController.RefusalRecheckMs = 0;
                MidiServiceRecovery.CooldownMs = 20;
                int looks = 0;
                MidiVirtualController.SyncPointForTest = point =>
                {
                    if (point == "recheck-look") Interlocked.Increment(ref looks);
                };
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);
                // The check runs: it starts the refused API on the side.
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref wms.Starts) >= 3, 10_000));

                int down = 0, resumed = 0;
                uninstall = new Thread(() => MidiRuntimeSwitch.RunUninstall(() =>
                {
                    running.Set();
                    finish.Wait(30_000);
                    return (true, System.Threading.Tasks.Task.CompletedTask);
                }, () => Interlocked.Increment(ref down), () => Interlocked.Increment(ref resumed), null)) { IsBackground = true };
                uninstall.Start();
                Assert.True(running.Wait(10_000));

                // MIDI keeps running on the legacy API, its input untouched.
                Assert.Equal(0, Volatile.Read(ref down));
                Assert.False(MidiVirtualController.IsSuppressedForUninstall);
                Assert.True(MidiVirtualController.IsHeldForUninstall);
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);

                // The check keeps looking and starts nothing.
                int starts = Volatile.Read(ref wms.Starts);
                int seen = Volatile.Read(ref looks);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref looks) >= seen + 3, 10_000));
                Assert.Equal(starts, Volatile.Read(ref wms.Starts));

                // A reset probes again, and the probe takes the legacy API
                // without trying the runtime.
                MidiVirtualController.ResetAvailability();
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);
                seen = Volatile.Read(ref looks);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref looks) >= seen + 3, 10_000));
                Assert.Equal(starts, Volatile.Read(ref wms.Starts));

                // The uninstaller exits with the runtime still there (it
                // failed, or was canceled), and the service answers by now.
                // The check goes on and moves MIDI onto it.
                wms.StartResult = true;
                finish.Set();
                Assert.True(uninstall.Join(10_000));
                Assert.False(MidiVirtualController.IsHeldForUninstall);
                Assert.Equal(0, Volatile.Read(ref resumed));
                Assert.True(SpinWait.SpinUntil(() => MidiVirtualController.ActiveApi == MidiApiKind.AppSdk, 10_000));
            }
            finally
            {
                finish.Set();
                uninstall?.Join(10_000);
            }
        }

        /// <summary>Under the legacy API an uninstaller that outlives its
        /// wait keeps its start hold until it exits, and its exit ends that
        /// hold: Windows MIDI Services may start again, and the check that
        /// follows a refusal goes on. A start hold that was never ended kept
        /// MIDI on the legacy API for the rest of the session, with nothing
        /// in the log.</summary>
        [Fact]
        public void AnUninstallerPastItsWaitUnderTheLegacyApi_EndsItsStartHoldWhenItExits()
        {
            var wms = UseRefusedWindowsMidiServices();
            MidiVirtualController.RefusalRecheckMs = 0;
            MidiServiceRecovery.CooldownMs = 20;
            int looks = 0;
            MidiVirtualController.SyncPointForTest = point =>
            {
                if (point == "recheck-look") Interlocked.Increment(ref looks);
            };
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);

            int down = 0, resumed = 0, refreshed = 0;
            var exited = new System.Threading.Tasks.TaskCompletionSource();
            MidiRuntimeSwitch.RunUninstall(() => (false, exited.Task),
                () => Interlocked.Increment(ref down), () => Interlocked.Increment(ref resumed),
                () => Interlocked.Increment(ref refreshed));

            // The uninstaller still runs. MIDI keeps running on the legacy
            // API, and the check looks and starts nothing.
            Assert.True(MidiRuntimeSwitch.UninstallerRunning);
            Assert.True(MidiVirtualController.IsHeldForUninstall);
            Assert.False(MidiVirtualController.IsSuppressedForUninstall);
            Assert.Equal(0, Volatile.Read(ref down));
            int starts = Volatile.Read(ref wms.Starts);
            int seen = Volatile.Read(ref looks);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref looks) >= seen + 3, 10_000));
            Assert.Equal(starts, Volatile.Read(ref wms.Starts));

            // It exits, and its start hold ends with it.
            exited.SetResult();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref refreshed) == 1, 10_000));
            Assert.False(MidiRuntimeSwitch.UninstallerRunning);
            Assert.False(MidiVirtualController.IsHeldForUninstall);
            Assert.Equal(0, Volatile.Read(ref resumed));
            // The check starts the refused API again.
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref wms.Starts) > starts, 10_000));
        }

        /// <summary>A hold waits for a Windows MIDI Services start the check
        /// began just before it. The check looked at the latch once, before
        /// it started the backend, so a start under way then ran on into the
        /// uninstall.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AHoldForAnUninstall_WaitsForAStartTheCheckHasBegun(bool release)
        {
            var gate = new ManualResetEventSlim(false);
            var waiting = new ManualResetEventSlim(false);
            try
            {
                var wms = UseRefusedWindowsMidiServices();
                MidiVirtualController.RefusalRecheckMs = 0;
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiVirtualController.SyncPointForTest = point =>
                {
                    // The check's start is held, the way a slow service
                    // holds it.
                    if (point == "recheck-look") wms.StartGate = gate;
                    else if (point == "hold-waits-for-trial") waiting.Set();
                };
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref wms.Starts) == 2, 10_000));

                var hold = MidiUninstallHold.None;
                var taker = new Thread(() =>
                {
                    if (release) MidiVirtualController.SuppressForUninstall();
                    else hold = MidiVirtualController.TryHoldWithoutRelease();
                }) { IsBackground = true };
                taker.Start();
                Assert.True(waiting.Wait(10_000), "the hold did not wait for the start under way");
                Assert.False(MidiVirtualController.IsHeldForUninstall);
                Assert.Equal(1, Volatile.Read(ref wms.Stops));

                gate.Set();
                Assert.True(taker.Join(10_000));
                // The start was over, and let go of, before the hold stood.
                Assert.Equal(2, Volatile.Read(ref wms.Stops));
                Assert.Equal(2, Volatile.Read(ref wms.Starts));
                Assert.True(MidiVirtualController.IsHeldForUninstall);
                Assert.Equal(release, MidiVirtualController.IsSuppressedForUninstall);
                if (!release) Assert.Equal(MidiUninstallHold.StartHeld, hold);
            }
            finally { gate.Set(); }
        }

        /// <summary>What an uninstall holds follows the API in use: nothing
        /// under the in-box API, only Windows MIDI Services' start under the
        /// legacy API, and a full release otherwise, which takes nothing
        /// until MIDI input is closed.</summary>
        [Fact]
        public void WhatAnUninstallHolds_FollowsTheApiInUse()
        {
            Use(new FakeBackend(MidiApiKind.InBox));
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiUninstallHold.None, MidiVirtualController.TryHoldWithoutRelease());
            Assert.False(MidiVirtualController.IsHeldForUninstall);

            Use(new FakeBackend(MidiApiKind.AppSdk));
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiUninstallHold.Released, MidiVirtualController.TryHoldWithoutRelease());
            Assert.False(MidiVirtualController.IsHeldForUninstall);

            // Nothing started yet, or nothing could: a release.
            Use(new FakeBackend(MidiApiKind.AppSdk) { StartResult = false });
            Assert.Equal(MidiUninstallHold.Released, MidiVirtualController.TryHoldWithoutRelease());
            Assert.False(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiUninstallHold.Released, MidiVirtualController.TryHoldWithoutRelease());
            Assert.False(MidiVirtualController.IsHeldForUninstall);

            MidiVirtualController.UseBackendFactoryForTest(() => null,
                () => new MidiBackendLegacy(new MidiBackendLegacyTests.FakeWinMm()));
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiUninstallHold.StartHeld, MidiVirtualController.TryHoldWithoutRelease());
            Assert.True(MidiVirtualController.IsHeldForUninstall);
            Assert.False(MidiVirtualController.IsSuppressedForUninstall);
            // Each hold ends on its own account, and an end with none left
            // changes nothing.
            Assert.Equal(MidiUninstallHold.StartHeld, MidiVirtualController.TryHoldWithoutRelease());
            Assert.False(MidiVirtualController.EndUninstallHold(MidiUninstallHold.StartHeld));
            Assert.True(MidiVirtualController.IsHeldForUninstall);
            Assert.False(MidiVirtualController.EndUninstallHold(MidiUninstallHold.StartHeld));
            Assert.False(MidiVirtualController.IsHeldForUninstall);
            Assert.False(MidiVirtualController.EndUninstallHold(MidiUninstallHold.StartHeld));
            Assert.False(MidiVirtualController.IsHeldForUninstall);
            Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);
        }

        // ── A service restarted under PadForge ──

        /// <summary>A service the user restarts reads as not running while
        /// it stops and starts. A reset then ran the probe inside that
        /// restart, where the Windows MIDI Services API refuses, and MIDI
        /// landed on the legacy API: every slot rebuilt with no port, and
        /// every WinMM input listed as a new device. The watch waits for an
        /// instance that runs, so the probe meets it.</summary>
        [Fact]
        public void AServiceThatIsStoppingOrStarting_StartsNoGeneration_UntilAnInstanceRuns()
        {
            uint pid = 100;
            var wms = new FakeBackend(MidiApiKind.InBox);
            MidiVirtualController.UseBackendFactoryForTest(() => wms,
                () => new MidiBackendLegacy(new MidiBackendLegacyTests.FakeWinMm()));
            MidiServiceRecovery.ProcessIdSeam = () => Volatile.Read(ref pid);
            MidiVirtualController.ResetAvailability();
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiApiKind.InBox, MidiVirtualController.ActiveApi);
            int generation = MidiVirtualController.Generation;

            // Stopping, stopped, starting: the service refuses meanwhile.
            Volatile.Write(ref pid, 0);
            wms.StartResult = false;
            for (int look = 0; look < 5; look++)
            {
                MidiVirtualController.CheckServiceInstance();
                Assert.True(MidiVirtualController.IsAvailable());
                Assert.Equal(MidiApiKind.InBox, MidiVirtualController.ActiveApi);
            }
            Assert.Equal(generation, MidiVirtualController.Generation);

            // The new instance runs.
            Volatile.Write(ref pid, 200);
            wms.StartResult = true;
            MidiVirtualController.CheckServiceInstance();
            Assert.Equal(generation + 1, MidiVirtualController.Generation);
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiApiKind.InBox, MidiVirtualController.ActiveApi);
        }

        /// <summary>A service that stays down still rebuilds MIDI, once it
        /// has read as not running for longer than a restart takes. The
        /// first look only notes when it went down.</summary>
        [Fact]
        public void AServiceThatStaysDown_StartsANewGeneration_OncePastItsBound()
        {
            uint pid = 100;
            Use(new FakeBackend(MidiApiKind.InBox));
            MidiServiceRecovery.ProcessIdSeam = () => Volatile.Read(ref pid);
            MidiVirtualController.ResetAvailability();
            Assert.True(MidiVirtualController.IsAvailable());
            int generation = MidiVirtualController.Generation;
            MidiVirtualController.ServiceDownResetMs = 0;

            Volatile.Write(ref pid, 0);
            MidiVirtualController.CheckServiceInstance();
            Assert.Equal(generation, MidiVirtualController.Generation);
            MidiVirtualController.CheckServiceInstance();
            Assert.Equal(generation + 1, MidiVirtualController.Generation);
        }

        /// <summary>A restart PadForge performs ends in its own reset. The
        /// instance watch stands down through it, so the new instance's
        /// process id does not read as a restart made under PadForge and
        /// start a second generation, which rebuilt every slot
        /// twice.</summary>
        [Fact]
        public void TheInstanceWatch_StandsDown_ThroughARestartPadForgePerforms()
        {
            var inRestart = new ManualResetEventSlim(false);
            var finish = new ManualResetEventSlim(false);
            try
            {
                uint pid = 100;
                Use(new FakeBackend(MidiApiKind.InBox));
                MidiServiceRecovery.ProcessIdSeam = () => Volatile.Read(ref pid);
                MidiVirtualController.ResetAvailability();
                Assert.True(MidiVirtualController.IsAvailable());
                int generation = MidiVirtualController.Generation;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    Volatile.Write(ref pid, 200);
                    inRestart.Set();
                    finish.Wait(10_000);
                    return MidiServiceRestart.Running;
                };
                var restart = new Thread(() => MidiVirtualController.RestartService("a MIDI create hung", legacyCall: false))
                    { IsBackground = true };
                restart.Start();
                Assert.True(inRestart.Wait(10_000));

                MidiVirtualController.CheckServiceInstance();
                Assert.Equal(generation, MidiVirtualController.Generation);

                finish.Set();
                Assert.True(restart.Join(10_000));
                Assert.Equal(generation + 1, MidiVirtualController.Generation);
                MidiVirtualController.CheckServiceInstance();
                Assert.Equal(generation + 1, MidiVirtualController.Generation);
            }
            finally { finish.Set(); }
        }

        /// <summary>The legacy API runs through the service only on the new
        /// MIDI stack in full Windows MIDI Services mode, so only there is
        /// the service instance watched on its account. A missing API mode
        /// value reads as full mode on every Windows, so the build is asked
        /// as well.</summary>
        [Theory]
        [InlineData(22631, MidiApiSelection.ApiModeFull, false)]
        [InlineData(26100, MidiApiSelection.ApiModeHybrid, false)]
        [InlineData(26100, MidiApiSelection.ApiModeFull, true)]
        public void TheInstanceWatch_FollowsTheLegacyApi_OnlyWhereWinMmRunsThroughTheService(int build, int mode, bool watched)
        {
            uint pid = 100;
            MidiVirtualController.UseBackendFactoryForTest(() => null,
                () => new MidiBackendLegacy(new MidiBackendLegacyTests.FakeWinMm()));
            MidiApiSelection.OsBuildSeam = () => build;
            MidiApiSelection.ApiModeSeam = () => mode;
            MidiServiceRecovery.ProcessIdSeam = () => Volatile.Read(ref pid);
            MidiVirtualController.ResetAvailability();
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(MidiApiKind.Legacy, MidiVirtualController.ActiveApi);
            int generation = MidiVirtualController.Generation;

            Volatile.Write(ref pid, 200);
            MidiVirtualController.CheckServiceInstance();
            Assert.Equal(watched ? generation + 1 : generation, MidiVirtualController.Generation);
        }

        // ── A hang no service restart reaches ──

        /// <summary>Windows before 24H2 has no MIDI service, and WinMM talks
        /// to classic drivers there. A missing API mode value reads as full
        /// Windows MIDI Services on every Windows, so a legacy probe that
        /// hung on such a PC was put down to the service: the card said
        /// PadForge restarts it, a restart of nothing was tried once per
        /// cooldown, and MIDI came back only at the next cooldown tick after
        /// the driver answered. The hang is the driver's wherever WinMM does
        /// not run through the service, and MIDI is back as soon as the
        /// driver answers.</summary>
        [Theory]
        [InlineData(19045, MidiApiSelection.ApiModeFull, false)]
        [InlineData(22631, MidiApiSelection.ApiModeFull, false)]
        [InlineData(26100, MidiApiSelection.ApiModeLegacy, false)]
        [InlineData(26100, MidiApiSelection.ApiModeHybrid, false)]
        [InlineData(26100, MidiApiSelection.ApiModeFull, true)]
        public void ALegacyProbeThatHangs_IsTheServices_OnlyWhereWinMmRunsThroughIt(int build, int mode, bool throughService)
        {
            var gate = new ManualResetEventSlim(false);
            var fake = new MidiBackendLegacyTests.FakeWinMm { OutputCountGate = gate };
            try
            {
                MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
                MidiApiSelection.OsBuildSeam = () => build;
                MidiApiSelection.ApiModeSeam = () => mode;
                int restarts = 0;
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                // Nothing answers the restart: there is no instance to end.
                MidiServiceRecovery.RestartSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    return MidiServiceRestart.Unchanged;
                };
                ProbeBounds(100);
                MidiVirtualController.HungProbeReturnMs = 100;

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.Equal(
                    throughService ? MidiNotRunningReason.ServiceDidNotRespond : MidiNotRunningReason.DriverDidNotRespond,
                    MidiVirtualController.NotRunningReason);
                Assert.Equal(throughService, MidiVirtualController.ServiceRestartCanHelp(legacyCall: true));
                if (throughService)
                    Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref restarts) == 1, 10_000));

                // The driver answers.
                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => MidiVirtualController.ActiveApi == MidiApiKind.Legacy, 10_000));
                Assert.Equal(throughService ? 1 : 0, Volatile.Read(ref restarts));
            }
            finally { gate.Set(); }
        }

        /// <summary>A Windows 11 24H2 or later PC can run without the MIDI
        /// service, and its WinMM then talks to classic drivers alone. The
        /// build and the API mode read as the new MIDI stack all the same,
        /// so a legacy probe that hung there was put down to the service: the
        /// card said PadForge restarts it, and a restart of a service that
        /// does not exist was tried once per cooldown. The hang is the
        /// driver's, and MIDI is back as soon as the driver answers.</summary>
        [Fact]
        public void ALegacyProbeThatHangs_OnAPcWithNoMidiService_IsTheDrivers()
        {
            var gate = new ManualResetEventSlim(false);
            var fake = new MidiBackendLegacyTests.FakeWinMm { OutputCountGate = gate };
            try
            {
                MidiVirtualController.UseBackendFactoryForTest(() => null, () => new MidiBackendLegacy(fake));
                MidiApiSelection.OsBuildSeam = () => 26100;
                MidiApiSelection.ApiModeSeam = () => MidiApiSelection.ApiModeFull;
                MidiServiceRecovery.ServiceExistsSeam = () => false;
                int restarts = 0;
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RestartSeam = () =>
                {
                    Interlocked.Increment(ref restarts);
                    return MidiServiceRestart.Unchanged;
                };
                ProbeBounds(100);
                MidiVirtualController.HungProbeReturnMs = 100;

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.Equal(MidiNotRunningReason.DriverDidNotRespond, MidiVirtualController.NotRunningReason);
                Assert.False(MidiVirtualController.ServiceRestartCanHelp(legacyCall: true));
                Assert.False(MidiVirtualController.ServiceRestartCanHelp(legacyCall: false));
                Assert.Equal("no MIDI service restart reaches a classic MIDI driver",
                    MidiVirtualController.DescribeRestartRequest(legacyCall: true, out bool restartFollows));
                Assert.False(restartFollows);
                Assert.Equal(MidiServiceRestart.Refused, MidiVirtualController.RestartService("an off-limits check", legacyCall: false));

                // The driver answers.
                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => MidiVirtualController.ActiveApi == MidiApiKind.Legacy, 10_000));
                Assert.Equal(0, Volatile.Read(ref restarts));
            }
            finally { gate.Set(); }
        }

        /// <summary>A reset (the install's switch) or a release (the
        /// uninstall's) that waits behind a hung probe leaves the recovery's
        /// flags alone, so its restart still comes and frees them both. A
        /// reset cleared the hung flag before it waited, and the recovery's
        /// next attempt stood down.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AResetOrReleaseWhileAProbeHangs_KeepsTheRecoveryRestart(bool release)
        {
            var gate = new ManualResetEventSlim(false);
            try
            {
                Use(new FakeBackend(MidiApiKind.InBox) { StartGate = gate });
                int restarts = 0;
                MidiServiceRecovery.CooldownMs = 1_500;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () =>
                {
                    if (Interlocked.Increment(ref restarts) >= 2) gate.Set();
                    return true;
                };
                Assert.Equal(MidiServiceRestart.Running, MidiServiceRecovery.TryRestart("an earlier create"));
                ProbeBounds(200);
                MidiVirtualController.HungProbeReturnMs = 5_000;

                Assert.False(MidiVirtualController.IsAvailable());
                Assert.True(Within(8_000, release
                    ? MidiVirtualController.SuppressForUninstall
                    : MidiVirtualController.ResetAvailability), "the call waited on a recovery that stood down");
                Assert.Equal(2, Volatile.Read(ref restarts));
            }
            finally { gate.Set(); }
        }

        /// <summary>A dead service holds neither the uninstall's release nor
        /// app exit: both are bounded and give up, and a release that gave up
        /// leaves nothing latched once the service answers, so MIDI is not
        /// left off. On the UI thread they froze PadForge until the hung call
        /// returned.</summary>
        [Fact]
        public void ADeadServiceNeverHoldsTheReleaseOrTheExit()
        {
            var startGate = new ManualResetEventSlim(false);
            var disposeGate = new ManualResetEventSlim(false);
            try
            {
                var backend = Use(new FakeBackend(MidiApiKind.AppSdk) { SessionDisposeGate = disposeGate });
                Assert.NotNull(MidiInputRuntime.Session);
                MidiServiceRecovery.CooldownMs = 300;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => false;
                ProbeBounds(200);
                backend.StartGate = startGate;
                MidiVirtualController.ResetAvailability();
                Assert.False(MidiVirtualController.IsAvailable());

                MidiRuntimeSwitch.ReleaseTimeoutMs = 300;
                bool releasedInTime = true, exitedInTime = true;
                Assert.True(Within(5_000, () => releasedInTime =
                    MidiRuntimeSwitch.ReleaseForUninstall(MidiInputRuntime.Shutdown, () => { }, out _)),
                    "the release waited on the dead service");
                Assert.False(releasedInTime);
                Assert.True(Within(5_000, () => exitedInTime = MidiVirtualController.ShutdownForExit(300)),
                    "app exit waited on the dead service");
                Assert.False(exitedInTime);

                disposeGate.Set();
                startGate.Set();
                Assert.True(SpinWait.SpinUntil(() => !MidiRuntimeSwitch.ReleaseInFlight
                    && !MidiVirtualController.IsSuppressedForUninstall, 10_000));
            }
            finally
            {
                disposeGate.Set();
                startGate.Set();
            }
        }

        /// <summary>An uninstaller that outlives its wait lifts the latch when
        /// it exits, and never before. The latch held MIDI off for the rest of
        /// the session.</summary>
        [Fact]
        public void AnUninstallerThatOutlivesItsWait_LiftsTheLatchWhenItExits()
        {
            var backend = Use(new FakeBackend(MidiApiKind.AppSdk));
            Assert.True(MidiVirtualController.IsAvailable());
            MidiVirtualController.SuppressForUninstall();
            var exited = new System.Threading.Tasks.TaskCompletionSource();
            int resumed = 0, refreshed = 0;
            var lifted = MidiRuntimeSwitch.LiftWhenExited(exited.Task, MidiUninstallHold.Released,
                () => Interlocked.Increment(ref resumed), () => Interlocked.Increment(ref refreshed));

            // The lift hangs on the uninstaller's exit, which has not come.
            Assert.False(lifted.IsCompleted);
            Assert.False(MidiVirtualController.IsAvailable());
            Assert.Equal(0, Volatile.Read(ref resumed));

            exited.SetResult();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref resumed) == 1 && Volatile.Read(ref refreshed) == 1, 5_000));
            Assert.True(MidiVirtualController.IsAvailable());
            Assert.Equal(2, backend.Starts);
        }

        /// <summary>The installer helper keeps the Process of an uninstaller
        /// that outlives its wait and reports its exit, where a using block
        /// disposed it as the method returned.</summary>
        [Fact]
        public void AnUninstallerPastItsWait_IsWatchedToItsExit()
        {
            int saved = DriverInstaller.MidiUninstallWaitMs;
            try
            {
                DriverInstaller.MidiUninstallWaitMs = 50;
                var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping -n 3 127.0.0.1 >nul & exit 7",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                int code = -1;
                Assert.False(DriverInstaller.WaitForUninstaller(proc, c => Volatile.Write(ref code, c), out var exited));
                Assert.False(exited.IsCompleted);
                Assert.True(SpinWait.SpinUntil(() => exited.IsCompleted, 15_000));
                Assert.Equal(7, Volatile.Read(ref code));
            }
            finally { DriverInstaller.MidiUninstallWaitMs = saved; }
        }

        /// <summary>A refusal, a hang or an ended instance from the restart:
        /// whenever the instance a create talked to ended, a new generation
        /// starts, running again or not, so every slot made on it
        /// rebuilds.</summary>
        [Fact]
        public void ARestartThatEndsTheServiceButDoesNotSeeItRun_StartsANewGeneration()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            Assert.True(MidiVirtualController.IsAvailable());
            var first = new MidiVirtualController(0, 0, 1);
            first.Connect();
            backend.OnCreate = (_, _, _) => throw new MidiSessionUnavailableException();
            MidiServiceRecovery.ResetCooldownForTest();
            MidiServiceRecovery.RestartSeam = () => MidiServiceRestart.EndedNotRunning;
            int generation = MidiVirtualController.Generation;

            var second = new MidiVirtualController(1, 0, 2);
            Assert.Throws<MidiSessionUnavailableException>(() => second.Connect());
            Assert.True(MidiVirtualController.Generation > generation);
            Assert.NotEqual(MidiVirtualController.Generation, first.CreatedGeneration);
            first.Dispose();
        }

        /// <summary>A failed start is logged with its API, since otherwise it
        /// reads only as the legacy API on the card. Each line is looked for
        /// after a mark of its own: the diagnostics ring is process-wide and
        /// never cleared, and the legacy tests' refusing backends write the
        /// same "did not start" line.</summary>
        [Fact]
        public void AStartThatFails_IsLoggedWithItsApi()
        {
            // The lines an earlier test leaves in the ring, written here so
            // the test holds whatever ran before it.
            SdlDiagLog.WriteLine("MIDIPROBE MIDI InBox start threw TypeInitializationException 0x80131534");
            SdlDiagLog.WriteLine("MIDIPROBE MIDI AppSdk did not start");

            var threw = LogAfterMark();
            Use(new FakeBackend(MidiApiKind.InBox) { ThrowOnStart = true });
            Assert.False(MidiVirtualController.IsAvailable());
            Assert.Contains(threw(), l => l.Contains("MIDIPROBE MIDI InBox start threw TypeInitializationException"));

            var refused = LogAfterMark();
            Use(new FakeBackend(MidiApiKind.AppSdk) { StartResult = false });
            Assert.False(MidiVirtualController.IsAvailable());
            Assert.Contains(refused(), l => l.Contains("MIDIPROBE MIDI AppSdk did not start"));
        }

        /// <summary>A midisrv restarted under PadForge (Microsoft's MIDI
        /// Settings, its console, the Services app, or a crash restart) is a
        /// new instance, and every slot made on the old one went silent. The
        /// input sweep's worker sees the new process id and starts a new
        /// generation. The same instance starts none.</summary>
        [Fact]
        public void AServiceRestartPadForgeDidNotPerform_StartsANewGeneration()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Use(new FakeBackend(MidiApiKind.InBox));
                uint pid = 100;
                MidiServiceRecovery.ProcessIdSeam = () => Volatile.Read(ref pid);
                MidiVirtualController.ResetAvailability();
                Assert.True(MidiVirtualController.IsAvailable());
                var im = new InputManager();
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                void Sweep() => sweep.Invoke(im, null);

                int generation = MidiVirtualController.Generation;
                for (int i = 0; i < 5; i++) { Sweep(); Thread.Sleep(20); }
                Assert.Equal(generation, MidiVirtualController.Generation);

                Volatile.Write(ref pid, 200);
                Assert.True(SpinWait.SpinUntil(() => { Sweep(); return MidiVirtualController.Generation > generation; }, 5_000));
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
        }

        /// <summary>A session the service refuses gets the restart a refused
        /// create gets, and the input sweep reopens on the new session. MIDI
        /// input sat in its minute-long backoff for good.</summary>
        [Fact]
        public void ARefusedInputSession_RestartsTheService_AndTheSweepReopens()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var backend = Use(new FakeBackend(MidiApiKind.InBox));
                backend.NormalEndpoints.Add((KeysId, "Test Keys"));
                int sessions = 0, restarts = 0;
                backend.OnCreateSession = name => Interlocked.Increment(ref sessions) == 1
                    ? null
                    : backend.InputSession = new FakeInputSession(name, true);
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
                int generation = MidiVirtualController.Generation;
                var im = new InputManager();
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.True(SpinWait.SpinUntil(() =>
                {
                    sweep.Invoke(im, null);
                    return backend.InputSession?.Connections.Count > 0 && backend.InputSession.Connections[0].Opened;
                }, 10_000));
                Assert.Equal(1, Volatile.Read(ref restarts));
                Assert.True(MidiVirtualController.Generation > generation);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
        }

        /// <summary>The MIDI endpoint query that hangs behind the service
        /// gets the service restart, and its gate clears, where it stopped
        /// input discovery for the session with nothing in the log.</summary>
        [Fact]
        public void AnEndpointQueryThatHangs_RestartsTheService()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var gate = new ManualResetEventSlim(false);
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Use(new FakeBackend(MidiApiKind.InBox) { EnumerateGate = gate });
                int restarts = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); gate.Set(); return true; };
                var im = new InputManager { MidiEnumHungMs = 200 };
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                var log = LogAfterMark();

                Assert.True(SpinWait.SpinUntil(() => { sweep.Invoke(im, null); return Volatile.Read(ref restarts) > 0; }, 5_000));
                Assert.True(SpinWait.SpinUntil(() =>
                    !(bool)typeof(InputManager).GetField("_midiEnumRunning", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im), 5_000));
                Assert.Contains(log(), l => l.Contains("MIDIINPUT endpoint query in flight") && l.EndsWith(": restarting the MIDI service"));
            }
            finally
            {
                gate.Set();
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
        }

        /// <summary>The watchdog's line says what follows. It said
        /// "restarting the MIDI service" before anything had been asked: in
        /// Legacy API mode no restart can reach the call, and inside another
        /// restart's cooldown none is made.</summary>
        [Theory]
        [InlineData(MidiApiSelection.ApiModeLegacy, false, "no MIDI service restart reaches a classic MIDI driver")]
        [InlineData(MidiApiSelection.ApiModeFull, true, "so none follows yet")]
        public void TheEndpointQueryWatchdog_SaysWhatFollows_WhenNoRestartDoes(int mode, bool insideCooldown, string expected)
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var gate = new ManualResetEventSlim(false);
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { EnumerateGate = gate });
                MidiApiSelection.ApiModeSeam = () => mode;
                int restarts = 0;
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
                if (insideCooldown)
                    Assert.Equal(MidiServiceRestart.Running, MidiServiceRecovery.TryRestart("an earlier create"));
                Assert.True(MidiVirtualController.IsAvailable());
                var im = new InputManager { MidiEnumHungMs = 0 };
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                var log = LogAfterMark();

                // The first sweep starts the query, which hangs. The watchdog
                // fires on the first sweep that finds it in flight.
                sweep.Invoke(im, null);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.Enumerations) == 1, 10_000));
                sweep.Invoke(im, null);

                var line = Assert.Single(log(), l => l.Contains("MIDIINPUT endpoint query in flight"));
                Assert.EndsWith(expected, line, StringComparison.Ordinal);
                Assert.DoesNotContain("restarting the MIDI service", line);
            }
            finally
            {
                gate.Set();
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
        }

        /// <summary>The watchdogs run on the poll thread, while something is
        /// already hung behind the MIDI service. Whether this PC has that
        /// service is asked on a worker, and a watchdog reads the last
        /// answer.</summary>
        [Fact]
        public void TheEndpointQueryWatchdog_AsksTheServiceManagerOffThePollThread()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var gate = new ManualResetEventSlim(false);
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { EnumerateGate = gate });
                MidiServiceRecovery.CooldownMs = FarOff;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => true;
                Assert.True(MidiVirtualController.IsAvailable());
                int sweepThread = Environment.CurrentManagedThreadId, onSweep = 0, offSweep = 0;
                MidiServiceRecovery.ServiceExistsSeam = () =>
                {
                    if (Environment.CurrentManagedThreadId == sweepThread) Interlocked.Increment(ref onSweep);
                    else Interlocked.Increment(ref offSweep);
                    return true;
                };
                var im = new InputManager { MidiEnumHungMs = 0 };
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                var log = LogAfterMark();

                sweep.Invoke(im, null);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.Enumerations) == 1, 10_000));
                sweep.Invoke(im, null);

                Assert.Contains(log(), l => l.Contains("MIDIINPUT endpoint query in flight"));
                Assert.Equal(0, Volatile.Read(ref onSweep));
                Assert.True(Volatile.Read(ref offSweep) > 0);
            }
            finally
            {
                gate.Set();
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
        }

        /// <summary>The endpoint-query watchdog times the query, not the
        /// worker's wait for the availability probe before it. The two bounds
        /// are the same 10 s, so a probe that hung tripped the watchdog on
        /// the sweep that landed as its wait ran out, and the watchdog asked
        /// for a service restart on the query's account, where the probe's
        /// own recovery was already on it.</summary>
        [Fact]
        public void TheEndpointQueryWatchdog_DoesNotTimeTheProbesWait()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedDevices = SettingsManager.UserDevices;
            var gate = new ManualResetEventSlim(false);
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                var backend = Use(new FakeBackend(MidiApiKind.InBox) { StartGate = gate });
                int restarts = 0;
                MidiServiceRecovery.ResetCooldownForTest();
                MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return false; };
                // The sweep's worker stays in the probe's wait, and the
                // watchdog would fire on any query it found in flight.
                ProbeBounds(FarOff);
                var im = new InputManager { MidiEnumHungMs = 0 };
                var sweep = typeof(InputManager).GetMethod("UpdateMidiInputDevices", BindingFlags.Instance | BindingFlags.NonPublic);
                var log = LogAfterMark();

                sweep.Invoke(im, null);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref backend.Starts) == 1, 10_000));
                for (int i = 0; i < 5; i++) sweep.Invoke(im, null);

                Assert.DoesNotContain(log(), l => l.Contains("MIDIINPUT endpoint query in flight"));
                Assert.Equal(0, Volatile.Read(ref restarts));
            }
            finally
            {
                gate.Set();
                SettingsManager.UserSettings = savedSettings;
                SettingsManager.UserDevices = savedDevices;
            }
        }

        // ── MIDI input calls that outlive their bound ──

        /// <summary>Each input call that outlives its bound is watched on
        /// its own. One watch ran at a time, and a request that found it
        /// taken was dropped: an open's watch, which ends as soon as its
        /// worker gives up, took the place of the session create that was
        /// still hung, so the restart never came.</summary>
        [Fact]
        public void EveryPendingInputCall_IsWatched_WhateverElseIsWatched()
        {
            Use(new FakeBackend(MidiApiKind.InBox));
            int restarts = 0;
            MidiServiceRecovery.ResetCooldownForTest();
            MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
            MidiVirtualController.ConnectTimeoutMs = 300;
            var open = new ManualResetEventSlim(false);
            var session = new ManualResetEventSlim(false);

            // The open's watch comes first and ends with no restart, since
            // its worker returns. The session create stays hung.
            var first = MidiInputRuntime.RestartIfStillPending(open, 0, legacyCall: false, "a MIDI input connection did not open");
            var second = MidiInputRuntime.RestartIfStillPending(session, 0, legacyCall: false, "MIDI input could not get a session");
            open.Set();

            Assert.True(SpinWait.SpinUntil(() => first.IsCompleted && second.IsCompleted, 10_000));
            Assert.Equal(1, Volatile.Read(ref restarts));
            session.Set();
        }

        /// <summary>A call that returns before the create path's bound is
        /// left alone.</summary>
        [Fact]
        public void AnInputCallThatReturnsInTime_GetsNoRestart()
        {
            Use(new FakeBackend(MidiApiKind.InBox));
            int restarts = 0;
            MidiServiceRecovery.ResetCooldownForTest();
            MidiServiceRecovery.RecoverSeam = () => { Interlocked.Increment(ref restarts); return true; };
            MidiVirtualController.ConnectTimeoutMs = 300;
            // The call has returned by the time its watch looks, however
            // the two threads are scheduled.
            var done = new ManualResetEventSlim(true);

            var watch = MidiInputRuntime.RestartIfStillPending(done, 0, legacyCall: false, "a MIDI input connection did not open");
            Assert.True(SpinWait.SpinUntil(() => watch.IsCompleted, 10_000));
            Assert.Equal(0, Volatile.Read(ref restarts));
        }

        /// <summary>A session create that hangs gets its restart every time,
        /// with the open's bound equal to the session's, as shipped. The two
        /// run out together, and the restart came only when the session's
        /// request happened to reach the one watch first.</summary>
        [Fact]
        public void AHungInputSession_GetsItsRestart_EveryTime()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            backend.NormalEndpoints.Add((KeysId, "Test Keys"));
            int restarts = 0;
            ManualResetEventSlim gate = null;
            backend.OnCreateSession = name => { Volatile.Read(ref gate).Wait(); return null; };
            MidiServiceRecovery.RecoverSeam = () =>
            {
                Interlocked.Increment(ref restarts);
                Volatile.Read(ref gate).Set();
                return true;
            };
            MidiInputDevice.OpenTimeoutMs = 100;
            MidiInputRuntime.SessionTimeoutMs = 100;
            MidiVirtualController.ConnectTimeoutMs = 300;
            try
            {
                for (int round = 1; round <= 6; round++)
                {
                    Volatile.Write(ref gate, new ManualResetEventSlim(false));
                    MidiServiceRecovery.ResetCooldownForTest();
                    Assert.True(MidiVirtualController.IsAvailable());

                    Assert.False(new MidiInputDevice(KeysId, "Test Keys").Open());
                    Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref restarts) == round, 10_000),
                        $"no restart followed hung session create {round}");
                    // The restart is over, reset included, before the next
                    // round's ask.
                    Assert.True(SpinWait.SpinUntil(() => !MidiServiceRecovery.RestartInFlight, 10_000));
                }
            }
            finally { Volatile.Read(ref gate)?.Set(); }
        }

        // ── Audit 2026-10-08: sends ──

        /// <summary>A Note Off the API did not take goes out again on the next
        /// submit, so a dropped release does not leave the note sounding. A
        /// refused CC goes out again while the control rests at the refused
        /// value, where change detection alone would send nothing. A refused
        /// Note On is not sent twice, since a duplicate retriggers.</summary>
        [Fact]
        public void ARefusedNoteOffOrCc_IsSentAgain_ButARefusedNoteOnIsNot()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 1, 60, 2, 100);
            int offs = 0, ccs = 0, ons = 0;
            ep.Refuse = m => (m.Item1 == Midi1Status.NoteOff && Interlocked.Increment(ref offs) == 1)
                || (m.Item1 == Midi1Status.ControlChange && Interlocked.Increment(ref ccs) == 1)
                || (m.Item1 == Midi1Status.NoteOn && m.Item3 == 61 && Interlocked.Increment(ref ons) == 1);

            vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 10 }, Notes = new[] { true, true } });
            Assert.True(vc.WaitForSends());
            // The CC stays at the value the API refused.
            var off = new MidiRawState { CcValues = new byte[] { 10 }, Notes = new[] { false, true } };
            vc.SubmitMidiRawState(off);
            Assert.True(vc.WaitForSends());
            vc.SubmitMidiRawState(off);
            Assert.True(vc.WaitForSends());
            vc.SubmitMidiRawState(off);
            Assert.True(vc.WaitForSends());

            Assert.Equal(new[]
            {
                (Midi1Status.NoteOn, 0, 60, 100),
                (Midi1Status.ControlChange, 0, 1, 10),
                (Midi1Status.NoteOff, 0, 60, 0),
            }, ep.Sent);
            vc.Dispose();
        }

        // ── What a port refused and the receiver is still owed ──

        /// <summary>The notes the port was left with sounding: every channel
        /// and key whose last note message the port took was a Note
        /// On.</summary>
        private static List<(int Channel, int Key)> Sounding(FakeEndpoint ep)
        {
            var on = new Dictionary<(int, int), bool>();
            lock (ep.Sent)
                foreach (var m in ep.Sent)
                    if (m.Item1 != Midi1Status.ControlChange) on[(m.Item2, m.Item3)] = m.Item1 == Midi1Status.NoteOn;
            return on.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(k => k).ToList();
        }

        /// <summary>The value each controller was left at, by channel and
        /// number: the last one the port took.</summary>
        private static Dictionary<(int Channel, int Number), int> Controllers(FakeEndpoint ep)
        {
            var last = new Dictionary<(int, int), int>();
            lock (ep.Sent)
                foreach (var m in ep.Sent)
                    if (m.Item1 == Midi1Status.ControlChange) last[(m.Item2, m.Item3)] = m.Item4;
            return last;
        }

        /// <summary>One submit, then the port's answer to everything it
        /// queued, so the next step of a test finds every refusal
        /// recorded.</summary>
        private static void SubmitAndWait(MidiVirtualController vc, MidiRawState state)
        {
            vc.SubmitMidiRawState(state);
            Assert.True(vc.WaitForSends());
        }

        private static MidiRawState Buttons(params bool[] notes) => new() { CcValues = Array.Empty<byte>(), Notes = notes };
        private static MidiRawState Pedal(byte value, params bool[] notes) => new() { CcValues = new[] { value }, Notes = notes };

        /// <summary>A button held while the MIDI bar moves its channel gets
        /// its Note Off on the channel and key it went out on. The port
        /// refuses exactly that message, twice, and it goes out again until
        /// the port takes it, on the old channel. That release carried no
        /// note index, so its refusal marked nothing, and the note sounded on
        /// the channel the slot had left until the receiver was reset.</summary>
        [Fact]
        public void ABarEditsNoteOff_ThePortRefuses_GoesOutAgain_OnTheChannelAndKeyItWasQueuedFor()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 0, 60, 1, 100);
            int releases = 0;
            ep.Refuse = m => m == (Midi1Status.NoteOff, 0, 60, 0) && Interlocked.Increment(ref releases) <= 2;

            SubmitAndWait(vc, Buttons(true));
            // The channel moves with the button held.
            vc.ApplyLayout(1, 1, 0, 60, 1, 100);
            Assert.True(vc.WaitForSends());
            for (int i = 0; i < 3; i++) SubmitAndWait(vc, Buttons(true));

            Assert.Equal(new[]
            {
                (Midi1Status.NoteOn, 0, 60, 100),
                (Midi1Status.NoteOn, 1, 60, 100),
                (Midi1Status.NoteOff, 0, 60, 0),
            }, ep.Sent);
            Assert.Equal(3, Volatile.Read(ref releases));
            Assert.Equal(new[] { (1, 60) }, Sounding(ep));

            SubmitAndWait(vc, Buttons(false));
            Assert.Empty(Sounding(ep));
            vc.Dispose();
        }

        /// <summary>A Note Off the port refused is owed on the channel and
        /// key it went out on, and a MIDI bar edit before the next submit
        /// changes nothing about that. The edit replaced the marks, so the
        /// release was never sent again and the note sounded on.</summary>
        [Fact]
        public void ABarEdit_KeepsANoteOffThatWasStillOwed()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 0, 60, 1, 100);
            int releases = 0;
            ep.Refuse = m => m.Item1 == Midi1Status.NoteOff && Interlocked.Increment(ref releases) == 1;

            SubmitAndWait(vc, Buttons(true));
            SubmitAndWait(vc, Buttons(false));
            Assert.Equal(new[] { (0, 60) }, Sounding(ep));
            // The bar moves the channel and the note before the next submit.
            vc.ApplyLayout(1, 1, 0, 72, 1, 100);
            SubmitAndWait(vc, Buttons(false));
            SubmitAndWait(vc, Buttons(false));

            Assert.Equal(new[]
            {
                (Midi1Status.NoteOn, 0, 60, 100),
                (Midi1Status.NoteOff, 0, 60, 0),
            }, ep.Sent);
            vc.Dispose();
        }

        /// <summary>The same for a Note Off that still waited in the queue as
        /// the bar moved and was refused after it. A refusal from before an
        /// edit marked nothing.</summary>
        [Fact]
        public void ANoteOffQueuedBeforeABarEdit_AndRefusedAfterIt_GoesOutAgain()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 0, 60, 1, 100);
            var port = new SemaphoreSlim(0);
            ep.Port = port;
            int releases = 0;
            ep.Refuse = m => m.Item1 == Midi1Status.NoteOff && Interlocked.Increment(ref releases) == 1;
            try
            {
                // The port holds the Note On, and the release waits behind
                // it as the bar moves.
                vc.SubmitMidiRawState(Buttons(true));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ep.Arrived) == 1, 10_000));
                vc.SubmitMidiRawState(Buttons(false));
                vc.ApplyLayout(1, 1, 0, 72, 1, 100);
                Assert.Equal(1, vc.QueuedSends);

                port.Release(100);
                Assert.True(vc.WaitForSends());
                Assert.Equal(new[] { (0, 60) }, Sounding(ep));
                SubmitAndWait(vc, Buttons(false));
                SubmitAndWait(vc, Buttons(false));

                Assert.Equal(new[]
                {
                    (Midi1Status.NoteOn, 0, 60, 100),
                    (Midi1Status.NoteOff, 0, 60, 0),
                }, ep.Sent);
            }
            finally
            {
                port.Release(100_000);
                vc.Dispose();
            }
        }

        /// <summary>A press since takes the place of a Note Off that is
        /// still owed: the note is meant to sound again, and its own release
        /// ends it. No Note Off goes out between the two presses.</summary>
        [Fact]
        public void APressSince_TakesThePlaceOfANoteOffThatIsOwed()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 0, 60, 1, 100);
            int releases = 0;
            ep.Refuse = m => m.Item1 == Midi1Status.NoteOff && Interlocked.Increment(ref releases) == 1;

            SubmitAndWait(vc, Buttons(true));
            SubmitAndWait(vc, Buttons(false));
            SubmitAndWait(vc, Buttons(true));
            SubmitAndWait(vc, Buttons(true));
            Assert.Equal(new[]
            {
                (Midi1Status.NoteOn, 0, 60, 100),
                (Midi1Status.NoteOn, 0, 60, 100),
            }, ep.Sent);

            SubmitAndWait(vc, Buttons(false));
            Assert.Equal((Midi1Status.NoteOff, 0, 60, 0), ep.Sent[^1]);
            Assert.Empty(Sounding(ep));
            vc.Dispose();
        }

        /// <summary>A sustain pedal held when its channel moves gets its off
        /// on the pair it left. The port refuses it twice, and it goes out
        /// again until taken. That off carried no index either, and a damper
        /// left down holds every note on its channel.</summary>
        [Fact]
        public void AMovedSustainPedalsOff_ThePortRefuses_GoesOutAgain_OnThePairItLeft()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 64, 1, 60, 0, 100);
            int offs = 0;
            ep.Refuse = m => m == (Midi1Status.ControlChange, 0, 64, 0) && Interlocked.Increment(ref offs) <= 2;

            SubmitAndWait(vc, Pedal(127));
            vc.ApplyLayout(1, 64, 1, 60, 0, 100);
            Assert.True(vc.WaitForSends());
            for (int i = 0; i < 3; i++) SubmitAndWait(vc, Pedal(127));

            Assert.Equal(new[]
            {
                (Midi1Status.ControlChange, 0, 64, 127),
                (Midi1Status.ControlChange, 1, 64, 127),
                (Midi1Status.ControlChange, 0, 64, 0),
            }, ep.Sent);
            vc.Dispose();
        }

        /// <summary>A pedal release the port refused is owed on its pair. A
        /// bar edit that moves the slot off that pair before the next submit
        /// still gets the pedal its off there. The edit dropped the mark,
        /// and since the slot had the pedal down as released, nothing else
        /// sent the off.</summary>
        [Fact]
        public void ABarEdit_KeepsAPedalReleaseThatWasStillOwed()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 64, 1, 60, 0, 100);
            int offs = 0;
            ep.Refuse = m => m == (Midi1Status.ControlChange, 0, 64, 0) && Interlocked.Increment(ref offs) == 1;

            SubmitAndWait(vc, Pedal(127));
            SubmitAndWait(vc, Pedal(0));
            Assert.Equal(127, Controllers(ep)[(0, 64)]);
            vc.ApplyLayout(1, 64, 1, 60, 0, 100);
            SubmitAndWait(vc, Pedal(0));
            SubmitAndWait(vc, Pedal(0));

            Assert.Equal(0, Controllers(ep)[(0, 64)]);
            vc.Dispose();
        }

        /// <summary>A controller value the port refused goes out again after
        /// a bar edit that keeps the controller where it was. The edit
        /// dropped the mark for every controller, kept or not.</summary>
        [Fact]
        public void ABarEditThatKeepsAController_KeepsTheValueItIsOwed()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 64, 1, 60, 1, 100);
            int offs = 0;
            ep.Refuse = m => m == (Midi1Status.ControlChange, 0, 64, 0) && Interlocked.Increment(ref offs) == 1;

            SubmitAndWait(vc, Pedal(127, false));
            SubmitAndWait(vc, Pedal(0, false));
            // Only the note moves.
            vc.ApplyLayout(0, 64, 1, 72, 1, 100);
            SubmitAndWait(vc, Pedal(0, false));
            SubmitAndWait(vc, Pedal(0, false));

            Assert.Equal(new[]
            {
                (Midi1Status.ControlChange, 0, 64, 127),
                (Midi1Status.ControlChange, 0, 64, 0),
            }, ep.Sent);
            vc.Dispose();
        }

        /// <summary>A teardown's own releases go out again too. No submit
        /// follows a teardown, so the send worker tries a refused release
        /// again itself, and the port closes once it is through: the held
        /// note's Note Off and the pedal's off, each refused here before the
        /// port takes it. Both were dropped at the first refusal.</summary>
        [Fact]
        public void ATeardownsReleases_ThePortRefuses_GoOutAgainBeforeThePortCloses()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 64, 1, 60, 1, 100);
            // No drain bound in the way, however slowly the test runs.
            MidiVirtualController.SendDrainMs = FarOff;
            SubmitAndWait(vc, Pedal(127, true));
            int releases = 0, offs = 0;
            ep.Refuse = m => (m.Item1 == Midi1Status.NoteOff && Interlocked.Increment(ref releases) <= 3)
                || (m == (Midi1Status.ControlChange, 0, 64, 0) && Interlocked.Increment(ref offs) <= 2);

            vc.Dispose();

            Assert.Equal(new[]
            {
                (Midi1Status.ControlChange, 0, 64, 127),
                (Midi1Status.NoteOn, 0, 60, 100),
                (Midi1Status.ControlChange, 0, 64, 0),
                (Midi1Status.NoteOff, 0, 60, 0),
            }, ep.Sent);
            Assert.True(ep.Closed);
            Assert.Equal(0, Volatile.Read(ref ep.AfterClose));
        }

        /// <summary>A Note Off and a pedal's off that the port refused, and
        /// that no submit sent again before the teardown, go out with the
        /// teardown's own releases, ahead of them. The button and the pedal
        /// were already up, so the teardown had nothing of its own to
        /// release there, and the receiver kept the note and the damper
        /// down.</summary>
        [Fact]
        public void ReleasesStillOwedAtTheTeardown_GoOutBeforeThePortCloses()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 64, 1, 60, 2, 100);
            MidiVirtualController.SendDrainMs = FarOff;
            SubmitAndWait(vc, Pedal(127, true, false));
            int releases = 0, offs = 0;
            ep.Refuse = m => (m == (Midi1Status.NoteOff, 0, 60, 0) && Interlocked.Increment(ref releases) == 1)
                || (m == (Midi1Status.ControlChange, 0, 64, 0) && Interlocked.Increment(ref offs) == 1);
            // The button and the pedal let go, the port refuses both, and
            // the other button is held as the slot goes away.
            SubmitAndWait(vc, Pedal(0, false, true));

            vc.Dispose();

            Assert.Equal(new[]
            {
                (Midi1Status.ControlChange, 0, 64, 127),
                (Midi1Status.NoteOn, 0, 60, 100),
                (Midi1Status.NoteOn, 0, 61, 100),
                (Midi1Status.NoteOff, 0, 60, 0),
                (Midi1Status.ControlChange, 0, 64, 0),
                (Midi1Status.NoteOff, 0, 61, 0),
            }, ep.Sent);
            Assert.True(ep.Closed);
        }

        /// <summary>A port that is gone is owed nothing: no send can reach
        /// it, so nothing is tried again, on a submit or at the teardown.
        /// The refused release was sent again on every submit for as long as
        /// the slot stood.</summary>
        [Fact]
        public void APortThatIsGone_IsOwedNothing()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 0, 60, 1, 100);
            MidiVirtualController.SendDrainMs = FarOff;
            SubmitAndWait(vc, Buttons(true));

            ep.Gone = true;
            SubmitAndWait(vc, Buttons(false));
            int tries = Volatile.Read(ref ep.Arrived);
            for (int i = 0; i < 5; i++) SubmitAndWait(vc, Buttons(false));
            Assert.Equal(tries, Volatile.Read(ref ep.Arrived));

            // The teardown tries its release of a held note once.
            SubmitAndWait(vc, Buttons(true));
            tries = Volatile.Read(ref ep.Arrived);
            vc.Dispose();
            Assert.Equal(tries + 1, Volatile.Read(ref ep.Arrived));
            Assert.True(ep.Closed);
        }

        /// <summary>A port that goes on refusing a teardown's release is
        /// closed once the drain is over, and the send worker stops trying
        /// as the disconnect begins: here the disconnect hangs, as a dead
        /// service holds its RPC, and nothing more reaches the port
        /// meanwhile.</summary>
        [Fact]
        public void ATeardownsRelease_IsNotTriedAgain_OnceTheDrainIsOver()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 0, 60, 1, 100);
            SubmitAndWait(vc, Buttons(true));
            ep.Refuse = m => m.Item1 == Midi1Status.NoteOff;
            ep.DisconnectGate = new ManualResetEventSlim(false);
            MidiVirtualController.SendDrainMs = 50;
            var teardown = new Thread(vc.Dispose) { IsBackground = true };
            try
            {
                teardown.Start();
                Assert.True(SpinWait.SpinUntil(() => ep.Disconnecting, 30_000), "the teardown never got past its drain");
                Assert.True(vc.WaitForSends(30_000), "the worker went on trying past the drain");
                Assert.Equal(new[] { (Midi1Status.NoteOn, 0, 60, 100) }, ep.Sent);
            }
            finally
            {
                ep.DisconnectGate.Set();
                Assert.True(teardown.Join(30_000));
            }
            Assert.True(SpinWait.SpinUntil(() => ep.Closed, 30_000));
        }

        /// <summary>A Note Off is owed only while the slot does not hold
        /// that note down. Here the button is pressed again while its
        /// release still waits at the port, and the port refuses the
        /// release after the new press was queued: what the port refused is
        /// recorded once the press is already on its way, and sending it
        /// again would cut the note the button holds.</summary>
        [Fact]
        public void AnOwedNoteOff_IsNotSent_WhileTheSlotHoldsTheNoteDownAgain()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 0, 60, 1, 100);
            SubmitAndWait(vc, Buttons(true));
            var port = new SemaphoreSlim(0);
            ep.Port = port;
            int releases = 0;
            ep.Refuse = m => m.Item1 == Midi1Status.NoteOff && Interlocked.Increment(ref releases) == 1;
            try
            {
                vc.SubmitMidiRawState(Buttons(false));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ep.Arrived) == 2, 10_000));
                vc.SubmitMidiRawState(Buttons(true));
                port.Release(2);
                Assert.True(vc.WaitForSends());
                SubmitAndWait(vc, Buttons(true));

                Assert.Equal(new[]
                {
                    (Midi1Status.NoteOn, 0, 60, 100),
                    (Midi1Status.NoteOn, 0, 60, 100),
                }, ep.Sent);
                Assert.Equal(new[] { (0, 60) }, Sounding(ep));

                port.Release(100);
                SubmitAndWait(vc, Buttons(false));
                Assert.Empty(Sounding(ep));
            }
            finally
            {
                port.Release(100_000);
                vc.Dispose();
            }
        }

        /// <summary>
        /// The send queue under random controller values, button presses and
        /// MIDI bar edits from one thread, as the polling thread makes them,
        /// against a port that takes none, some or all of what waits after
        /// each submit and refuses a share of what it is sent. Checked on
        /// what the port took: no note left sounding, each controller of the
        /// final layout at its resting value, no switch controller left on
        /// at a channel and number the slot moved away from, the held button
        /// and the pedal released at the teardown, and nothing sent once the
        /// port was closing. With no refusals, presses and releases also
        /// alternate on every channel and key.
        ///
        /// <para>Nothing here depends on speed. The numbers come from a
        /// seeded generator, the port takes a message only when the test
        /// hands it one and the test waits for the port's answer, and
        /// whether the port refuses its nth message follows from n and the
        /// seed.</para>
        /// </summary>
        [Theory]
        [InlineData(20261012, 0)]
        [InlineData(20261012, 40)]
        [InlineData(4817, 150)]
        [InlineData(90210, 40)]
        public void TheSendQueue_UnderBarEditsAndRefusals_LeavesNoNoteSoundingAndNoPedalDown(int seed, int refusedPerThousand)
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            MidiVirtualController.SendDrainMs = FarOff;
            int channel = 0, startCc = 62, ccCount = 5, startNote = 60, noteCount = 3;
            var (vc, ep) = ConnectedController(backend, channel, startCc, ccCount, startNote, noteCount, 100);
            var port = new SemaphoreSlim(0);
            ep.Port = port;
            int reached = 0, refused = 0;
            ep.Refuse = _ =>
            {
                uint x = (uint)Interlocked.Increment(ref reached) + (uint)seed * 0x9E3779B9u;
                x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
                if (x % 1000 >= refusedPerThousand) return false;
                Interlocked.Increment(ref refused);
                return true;
            };

            // The port answers what it is handed, and then the worker stands
            // at the port with its next message or has none left. From there
            // the next step is the same on every run.
            long handed = 0;
            void Hand(int messages)
            {
                int take = Math.Min(messages, vc.PendingSends);
                if (take > 0)
                {
                    handed += take;
                    port.Release(take);
                }
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ep.Answered) == handed
                    && (vc.PendingSends == 0 || Volatile.Read(ref ep.Arrived) == handed + 1), 60_000),
                    "the send worker did not come to rest");
            }

            var rng = new Random(seed);
            var ccs = Enumerable.Repeat((byte)64, ccCount).ToArray();
            var notes = new bool[noteCount];
            MidiRawState State() => new() { CcValues = (byte[])ccs.Clone(), Notes = (bool[])notes.Clone() };
            const int steps = 6_000;
            int edits = 0, deepest = 0;
            for (int step = 0; step < steps; step++)
            {
                if (rng.Next(100) < 2)
                {
                    channel = rng.Next(3);
                    startCc = rng.Next(60, 68);
                    ccCount = rng.Next(1, 7);
                    startNote = rng.Next(60, 63);
                    noteCount = rng.Next(0, 5);
                    vc.ApplyLayout(channel, startCc, ccCount, startNote, noteCount, 100);
                    Array.Resize(ref ccs, ccCount);
                    Array.Resize(ref notes, noteCount);
                    edits++;
                }
                for (int i = 0; i < ccs.Length; i++)
                    if (rng.Next(3) != 0) ccs[i] = (byte)rng.Next(128);
                for (int i = 0; i < notes.Length; i++)
                    if (rng.Next(5) == 0) notes[i] = !notes[i];
                vc.SubmitMidiRawState(State());
                // A port as fast as the submits on one step, a slower one
                // on the next.
                Hand(rng.Next(4) == 0 ? int.MaxValue : rng.Next(4));
                deepest = Math.Max(deepest, vc.QueuedSends);
            }

            // Everything comes to rest: buttons up, each controller at a
            // value of its own, and the port takes all it is sent from here
            // on, less what it refuses.
            Hand(int.MaxValue);
            port.Release(int.MaxValue / 2);
            Array.Clear(notes, 0, notes.Length);
            for (int i = 0; i < ccs.Length; i++) ccs[i] = (byte)(10 + i);
            for (int pass = 0; pass < 60; pass++) SubmitAndWait(vc, State());

            Assert.Empty(Sounding(ep));
            var last = Controllers(ep);
            for (int i = 0; i < ccs.Length; i++)
                Assert.Equal((startCc + i, 10 + i), (startCc + i, last.TryGetValue((channel, startCc + i), out int value) ? value : -1));
            Assert.DoesNotContain(last, kv => kv.Key.Number >= 64 && kv.Key.Number <= 69 && kv.Value >= 64
                && !(kv.Key.Channel == channel && kv.Key.Number >= startCc && kv.Key.Number < startCc + ccs.Length));
            if (refusedPerThousand == 0)
            {
                // A refused Note On is dropped and its Note Off still goes
                // out, so only a run with no refusals is held to the strict
                // order.
                var on = new HashSet<(int, int)>();
                lock (ep.Sent)
                {
                    foreach (var m in ep.Sent)
                    {
                        if (m.Item1 == Midi1Status.ControlChange) continue;
                        if (m.Item1 == Midi1Status.NoteOn) Assert.True(on.Add((m.Item2, m.Item3)), $"Note On twice on channel {m.Item2} key {m.Item3}");
                        else Assert.True(on.Remove((m.Item2, m.Item3)), $"Note Off with no Note On on channel {m.Item2} key {m.Item3}");
                    }
                }
            }

            // The teardown: a held button and a pedal that is down are
            // released before the port closes, whatever the port refuses on
            // the way.
            if (notes.Length > 0) notes[0] = true;
            int pedal = -1;
            for (int i = 0; i < ccs.Length && pedal < 0; i++)
            {
                if (startCc + i < 64 || startCc + i > 69) continue;
                ccs[i] = 127;
                pedal = startCc + i;
            }
            SubmitAndWait(vc, State());
            vc.Dispose();

            Assert.True(ep.Closed);
            Assert.Empty(Sounding(ep));
            if (pedal >= 0) Assert.True(Controllers(ep)[(channel, pedal)] < 64, "the pedal was left down at the teardown");
            Assert.Equal(0, Volatile.Read(ref ep.AfterClose));
            Assert.True(edits > 60, "too few bar edits to mean anything");
            if (refusedPerThousand > 0) Assert.True(refused > 100, "too few refusals to mean anything");
            _output.WriteLine($"seed {seed}, {refusedPerThousand} in a thousand refused: {steps} submits, {edits} bar edits,"
                + $" {reached} messages reached the port, {refused} refused, {ep.Sent.Count} taken, deepest queue {deepest}");
        }

        // ── A port slower than the submit rate ──

        /// <summary>A port slower than the submit rate gets the newest value
        /// of each controller next, and every button press and release in
        /// order. The queue kept every superseded CC value: a sweep left it
        /// 512 messages deep, the synth went on sweeping after the stick had
        /// stopped, and a button tapped on the way found the queue full and
        /// never sounded.</summary>
        [Fact]
        public void ASlowPort_GetsTheNewestValueOfEachController_AndEveryNoteInOrder()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 2, 60, 1, 100);
            var port = new SemaphoreSlim(0);
            ep.Port = port;
            try
            {
                static MidiRawState At(int i) => new()
                {
                    CcValues = new[] { (byte)(i % 128), (byte)((i + 64) % 128) },
                    Notes = new[] { (i >= 300 && i < 400) || (i >= 500 && i < 600) },
                };

                // The port holds the first message while two CCs sweep for
                // a thousand submits and the button is tapped twice.
                vc.SubmitMidiRawState(At(1));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ep.Arrived) == 1, 10_000));
                for (int i = 2; i <= 1000; i++) vc.SubmitMidiRawState(At(i));

                // One entry per controller and the four note messages wait,
                // however long the sweep ran.
                Assert.Equal(6, vc.QueuedSends);

                port.Release(100);
                Assert.True(vc.WaitForSends());
                Assert.Equal(new[]
                {
                    (Midi1Status.ControlChange, 0, 1, 1),
                    (Midi1Status.ControlChange, 0, 2, 40),
                    (Midi1Status.ControlChange, 0, 1, 104),
                    (Midi1Status.NoteOn, 0, 60, 100),
                    (Midi1Status.NoteOff, 0, 60, 0),
                    (Midi1Status.NoteOn, 0, 60, 100),
                    (Midi1Status.NoteOff, 0, 60, 0),
                }, ep.Sent);
            }
            finally
            {
                port.Release(100_000);
                vc.Dispose();
            }
        }

        /// <summary>An entry waits per controller and channel. A value queued
        /// on the channel a slot is leaving goes out there, and the new
        /// channel's value after it.</summary>
        [Fact]
        public void AWaitingControllerValue_BelongsToItsChannel()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 7, 1, 60, 0, 100);
            var port = new SemaphoreSlim(0);
            ep.Port = port;
            try
            {
                static MidiRawState Cc(byte value) => new() { CcValues = new[] { value }, Notes = Array.Empty<bool>() };
                vc.SubmitMidiRawState(Cc(10));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ep.Arrived) == 1, 10_000));
                vc.SubmitMidiRawState(Cc(20));
                vc.SubmitMidiRawState(Cc(30));
                vc.ApplyLayout(1, 7, 1, 60, 0, 100);
                vc.SubmitMidiRawState(Cc(40));
                vc.SubmitMidiRawState(Cc(50));
                Assert.Equal(2, vc.QueuedSends);

                port.Release(100);
                Assert.True(vc.WaitForSends());
                Assert.Equal(new[]
                {
                    (Midi1Status.ControlChange, 0, 7, 10),
                    (Midi1Status.ControlChange, 0, 7, 30),
                    (Midi1Status.ControlChange, 1, 7, 50),
                }, ep.Sent);
            }
            finally
            {
                port.Release(100_000);
                vc.Dispose();
            }
        }

        /// <summary>A switch controller's release takes the place of a value
        /// still waiting for it, so the port's last word for the pedal is
        /// off.</summary>
        [Fact]
        public void ASwitchControllersRelease_TakesThePlaceOfAValueStillWaiting()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 64, 1, 60, 0, 100);
            var port = new SemaphoreSlim(0);
            ep.Port = port;
            try
            {
                static MidiRawState Cc(byte value) => new() { CcValues = new[] { value }, Notes = Array.Empty<bool>() };
                vc.SubmitMidiRawState(Cc(127));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ep.Arrived) == 1, 10_000));
                vc.SubmitMidiRawState(Cc(100));
                // The channel moves, which releases the pedal on the pair it
                // went out on.
                vc.ApplyLayout(1, 64, 1, 60, 0, 100);
                Assert.Equal(1, vc.QueuedSends);

                port.Release(100);
                Assert.True(vc.WaitForSends());
                Assert.Equal(new[]
                {
                    (Midi1Status.ControlChange, 0, 64, 127),
                    (Midi1Status.ControlChange, 0, 64, 0),
                }, ep.Sent);
            }
            finally
            {
                port.Release(100_000);
                vc.Dispose();
            }
        }

        /// <summary>Controller traffic never holds a note back, and a note
        /// never holds a controller back. Only a port that has stopped taking
        /// messages delays a press: with
        /// <see cref="MidiVirtualController.SendQueueLimit"/> note messages
        /// waiting, a further press is not queued, stays uncommitted, and
        /// goes out once the port moves while the button is still
        /// held.</summary>
        [Fact]
        public void APortThatTakesNothing_HoldsAPressBack_OnlyOnceTheNoteLimitWaits()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 1, 60, 1, 100);
            var port = new SemaphoreSlim(0);
            ep.Port = port;
            try
            {
                static MidiRawState State(byte cc, bool note) => new() { CcValues = new[] { cc }, Notes = new[] { note } };
                vc.SubmitMidiRawState(State(10, false));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ep.Arrived) == 1, 10_000));
                int limit = MidiVirtualController.SendQueueLimit;
                Assert.Equal(0, limit % 2);
                for (int i = 0; i < limit; i++) vc.SubmitMidiRawState(State(10, i % 2 == 0));
                Assert.Equal(limit, vc.QueuedSends);

                // The notes are full, so the press waits. The controller's
                // new value still finds its place.
                vc.SubmitMidiRawState(State(20, true));
                Assert.Equal(limit + 1, vc.QueuedSends);

                port.Release(limit + 2);
                Assert.True(vc.WaitForSends());
                vc.SubmitMidiRawState(State(20, true));
                port.Release(10);
                Assert.True(vc.WaitForSends());

                var notes = ep.Sent.Where(m => m.Item1 != Midi1Status.ControlChange).ToList();
                Assert.Equal(limit + 1, notes.Count);
                for (int i = 0; i < notes.Count; i++)
                    Assert.Equal(i % 2 == 0 ? Midi1Status.NoteOn : Midi1Status.NoteOff, notes[i].Item1);
                Assert.Equal(new[]
                {
                    (Midi1Status.ControlChange, 0, 1, 10),
                    (Midi1Status.ControlChange, 0, 1, 20),
                }, ep.Sent.Where(m => m.Item1 == Midi1Status.ControlChange));
            }
            finally
            {
                port.Release(100_000);
                vc.Dispose();
            }
        }

        /// <summary>A velocity of 0 is a Note Off in MIDI 1.0, so every press
        /// would release. The bar and the controller take 1, the quietest
        /// Note On.</summary>
        [Fact]
        public void AVelocityOfZero_SendsTheQuietestNoteOn()
        {
            Assert.Equal(1, new MidiSlotConfig { Velocity = 0 }.Velocity);
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 1, 0, 60, 1, 0);
            vc.SubmitMidiRawState(new MidiRawState { CcValues = Array.Empty<byte>(), Notes = new[] { true } });
            Assert.True(vc.WaitForSends());
            Assert.Equal((Midi1Status.NoteOn, 0, 60, 1), Assert.Single(ep.Sent));
            vc.Dispose();
        }

        /// <summary>A sustain pedal held when its channel moves gets its off
        /// on the old pair, or the damper there holds every note on that
        /// channel. Teardown releases it too.</summary>
        [Theory]
        [InlineData((int)MidiApiKind.InBox)]
        [InlineData((int)MidiApiKind.AppSdk)]
        public void AMovedSustainPedal_IsReleasedOnTheOldPair(int kind)
        {
            var backend = Use(new FakeBackend((MidiApiKind)kind));
            var (vc, ep) = ConnectedController(backend, 0, 64, 1, 60, 0, 100);
            vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 127 }, Notes = Array.Empty<bool>() });
            // The port has heard the pedal go down before the channel moves.
            // A value still waiting would be replaced by the release
            // (ASwitchControllersRelease_TakesThePlaceOfAValueStillWaiting).
            Assert.True(vc.WaitForSends());
            vc.ApplyLayout(1, 64, 1, 60, 0, 100);
            vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 127 }, Notes = Array.Empty<bool>() });
            Assert.True(vc.WaitForSends());
            Assert.Equal(new[]
            {
                (Midi1Status.ControlChange, 0, 64, 127),
                (Midi1Status.ControlChange, 0, 64, 0),
                (Midi1Status.ControlChange, 1, 64, 127),
            }, ep.Sent);

            vc.Dispose();
            Assert.Equal((Midi1Status.ControlChange, 1, 64, 0), ep.Sent[^1]);
            Assert.True(ep.Closed);
        }

        /// <summary>A switch controller PadForge never pressed, and one it
        /// left off, get no release.</summary>
        [Fact]
        public void ASustainPedalNeverPressed_GetsNoRelease()
        {
            var backend = Use(new FakeBackend(MidiApiKind.InBox));
            var (vc, ep) = ConnectedController(backend, 0, 64, 2, 60, 0, 100);
            vc.SubmitMidiRawState(new MidiRawState { CcValues = new byte[] { 64, 10 }, Notes = Array.Empty<bool>() });
            vc.ApplyLayout(1, 64, 2, 60, 0, 100);
            vc.Dispose();
            Assert.Equal(new[] { (Midi1Status.ControlChange, 0, 65, 10) }, ep.Sent);
        }

        // ── The fake ──

        private sealed class FakeBackend : IMidiBackend
        {
            public FakeBackend(MidiApiKind kind) => Kind = kind;

            public MidiApiKind Kind { get; }
            public volatile bool StartResult = true;
            public bool ThrowOnStart { get; init; }
            public bool InputOpens { get; init; } = true;
            public int Starts, Stops;

            /// <summary>Stands in for CreateInputSession when set: return
            /// null for a session the service refuses.</summary>
            public Func<string, IMidiInputSession> OnCreateSession;

            /// <summary>Holds a session's Dispose until set, the way a dead
            /// service holds its RPC.</summary>
            public ManualResetEventSlim SessionDisposeGate;
            public bool LastStopSkippedDispose;
            public Func<string, string, int, IMidiVirtualEndpoint> OnCreate;
            public readonly List<FakeEndpoint> Endpoints = new();
            public readonly List<(string Id, string Name)> NormalEndpoints = new();
            public FakeInputSession InputSession;

            /// <summary>Holds Start until set, the way a wedged service holds
            /// the probe's first call.</summary>
            public ManualResetEventSlim StartGate { get; set; }

            /// <summary>Holds the endpoint query until set.</summary>
            public ManualResetEventSlim EnumerateGate { get; init; }

            public bool Start()
            {
                Interlocked.Increment(ref Starts);
                StartGate?.Wait();
                if (ThrowOnStart) throw new TypeInitializationException("Midi2", null);
                return StartResult;
            }

            public void Stop(bool skipDispose)
            {
                Interlocked.Increment(ref Stops);
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
            {
                if (OnCreateSession != null) return OnCreateSession(name);
                return InputSession = new FakeInputSession(name, InputOpens) { DisposeGate = SessionDisposeGate };
            }

            /// <summary>How many endpoint queries have started.</summary>
            public int Enumerations;

            public List<(string Id, string Name)> EnumerateNormalEndpoints()
            {
                Interlocked.Increment(ref Enumerations);
                EnumerateGate?.Wait();
                return new(NormalEndpoints);
            }
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

            /// <summary>Refuses the messages it returns true for, the way a
            /// full buffer or a busy driver does. A refused message is not
            /// recorded.</summary>
            public Func<(Midi1Status, int, int, int), bool> Refuse;

            /// <summary>A port that takes one message per permit, the way a
            /// slow port takes one message at a time. The test hands out the
            /// permits, so it decides how far the port has got.</summary>
            public SemaphoreSlim Port;

            /// <summary>How many sends have reached the port, the one it has
            /// not taken yet included.</summary>
            public int Arrived;

            /// <summary>How many sends the port has answered, taken or
            /// refused.</summary>
            public int Answered;

            /// <summary>How many sends reached the port once its disconnect
            /// had begun.</summary>
            public int AfterClose;

            /// <summary>A port whose device left: it refuses every send from
            /// then on, and says it is lost.</summary>
            public volatile bool Gone;

            /// <summary>Holds the disconnect until set, the way a dead
            /// service holds its RPC.</summary>
            public ManualResetEventSlim DisconnectGate;

            /// <summary>Set as the disconnect begins, before it waits on the
            /// gate.</summary>
            public volatile bool Disconnecting;

            public bool Send(Midi1Status status, int channel, int data1, int data2)
            {
                if (ThrowOnSend) throw new System.Runtime.InteropServices.COMException("send failed");
                Interlocked.Increment(ref Arrived);
                if (Disconnecting || Closed) Interlocked.Increment(ref AfterClose);
                Port?.Wait();
                var message = (status, channel, data1, data2);
                bool refused = Gone || (Refuse != null && Refuse(message));
                if (!refused) lock (Sent) Sent.Add(message);
                Interlocked.Increment(ref Answered);
                return !refused;
            }

            public bool Lost => Gone;

            public void DisconnectConnection()
            {
                Disconnecting = true;
                DisconnectGate?.Wait();
                Disconnected = true;
            }

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
            public volatile bool Disposed;
            public ManualResetEventSlim DisposeGate;

            /// <summary>Set as the teardown begins, before it waits on the
            /// gate.</summary>
            public volatile bool DisposeEntered;

            public IMidiInputConnection CreateConnection(string endpointId, Action<uint, uint> onWords)
            {
                var c = new FakeInputConnection(endpointId, onWords, _opens);
                lock (Connections) Connections.Add(c);
                return c;
            }

            public void Dispose()
            {
                DisposeEntered = true;
                DisposeGate?.Wait();
                Disposed = true;
            }
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

    /// <summary>Borrows the slot order lists for a test, so the number a
    /// slot shows in the UI is the test's to decide, and puts them back. A
    /// group not named is empty.</summary>
    internal sealed class SlotOrdersScope : IDisposable
    {
        private readonly List<int>[] _saved =
        {
            SettingsManager.XboxSlotOrder, SettingsManager.PlayStationSlotOrder, SettingsManager.NintendoSlotOrder,
            SettingsManager.ExtendedSlotOrder, SettingsManager.KeyboardMouseSlotOrder, SettingsManager.MidiSlotOrder,
            SettingsManager.VrSlotOrder,
        };

        public SlotOrdersScope(int[] midi = null, int[] xbox = null)
        {
            SettingsManager.XboxSlotOrder = new List<int>(xbox ?? Array.Empty<int>());
            SettingsManager.PlayStationSlotOrder = new List<int>();
            SettingsManager.NintendoSlotOrder = new List<int>();
            SettingsManager.ExtendedSlotOrder = new List<int>();
            SettingsManager.KeyboardMouseSlotOrder = new List<int>();
            SettingsManager.MidiSlotOrder = new List<int>(midi ?? Array.Empty<int>());
            SettingsManager.VrSlotOrder = new List<int>();
        }

        public void Dispose()
        {
            SettingsManager.XboxSlotOrder = _saved[0];
            SettingsManager.PlayStationSlotOrder = _saved[1];
            SettingsManager.NintendoSlotOrder = _saved[2];
            SettingsManager.ExtendedSlotOrder = _saved[3];
            SettingsManager.KeyboardMouseSlotOrder = _saved[4];
            SettingsManager.MidiSlotOrder = _saved[5];
            SettingsManager.VrSlotOrder = _saved[6];
        }
    }
}
