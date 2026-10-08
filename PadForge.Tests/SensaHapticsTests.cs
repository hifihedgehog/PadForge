using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Razer Sensa HD haptics translation (#374, asked in discussion #369).
    /// No Razer hardware exists on the bench, so verification splits along
    /// the seam the architecture provides: the Interhaptics engine (HAR.dll)
    /// is a real native library that runs with no device present, and these
    /// tests drive the ACTUAL shipped binary through the full lifecycle the
    /// Unity reference documents. The Razer provider half degrades cleanly
    /// without Synapse's runtime, and that clean failure IS this machine's
    /// testable contract.
    /// </summary>
    [Collection("SensaNativeEngine")]
    public class SensaHapticsTests
    {
        // Direct bindings to the same shipped DLL the service uses, mirroring
        // Interhaptics_Unity_CoreSDK HAR.Native.cs exactly.
        private static class Har
        {
            private const string Dll = "HAR";
            [DllImport(Dll)] public static extern bool Init();
            [DllImport(Dll)] public static extern void Quit();
            [DllImport(Dll)]
            public static extern int AddParametricEffect(
                [In] double[] _amplitude, int _amplitudeSize,
                [In] double[] _pitch, int _pitchSize,
                double _freqMin, double _freqMax,
                [In] double[] _transient, int _transientSize,
                bool _isLooping);
            [DllImport(Dll)] public static extern void SetEventIntensity(int _hMaterialId, double _intensity);
            [DllImport(Dll)] public static extern void PlayEvent(int _hMaterialId, double _vibrationOffset, double _textureOffset, double _stiffnessOffset);
            [DllImport(Dll)] public static extern void StopAllEvents();
            [DllImport(Dll)] public static extern void ComputeAllEvents(double _curTime);
            [DllImport(Dll)] public static extern void AddTargetToEventMarshal(int _hMaterialId, SensaHapticsService.CommandData[] _target, int _size);
            [DllImport(Dll)] public static extern double GetVibrationLength(int _id);
        }

        private static class Provider
        {
            private const string Dll = "Interhaptics.RazerProvider";
            [DllImport(Dll)] public static extern bool ProviderInit();
            [DllImport(Dll)] public static extern bool ProviderIsPresent();
            [DllImport(Dll)] public static extern bool ProviderClean();
        }

        /// <summary>HAR.dll and the Razer provider are x64 images, so the six
        /// tests that load them or need the worker they feed can run in an
        /// x64 test host and nowhere else. On an ARM64 host the first P/Invoke
        /// is a BadImageFormatException and the service reports Unsupported
        /// by design, neither of which is a defect, so those tests step aside
        /// there. xunit 2 has no run-time skip, which is why this is a return.
        /// The branch an ARM64 process takes is covered on any bench by
        /// <see cref="Service_ReportsUnsupportedAndStartsNoWorkerWhereTheEngineCannotLoad"/>.
        /// </summary>
        private static bool NativeEngineLoadsHere => PadForge.Engine.PlatformSupport.SensaAvailable;

        /// <summary>What an ARM64 process does, driven through the seam
        /// because this bench cannot be one. Start has to say Unsupported
        /// once, on the caller's thread, and leave no worker behind, and
        /// every call after that has to be safe: the owner stops and
        /// disposes the service the same way on every platform.</summary>
        [Fact]
        public void Service_ReportsUnsupportedAndStartsNoWorkerWhereTheEngineCannotLoad()
        {
            var states = new System.Collections.Generic.List<SensaServiceState>();
            int caller = Environment.CurrentManagedThreadId, raisedOn = -1;
            var svc = new SensaHapticsService(retryMs: 50, tickMs: 5);
            svc.PlatformCanLoadEngine = () => false;
            svc.StateChanged += s => { states.Add(s); raisedOn = Environment.CurrentManagedThreadId; };

            svc.Start();

            Assert.Equal(new[] { SensaServiceState.Unsupported }, states);
            Assert.Equal(caller, raisedOn);
            Assert.False(svc.WorkerAlive);
            Assert.Equal(0, svc.ProviderInitAttempts);

            svc.Stop();
            svc.Dispose();
            svc.Dispose();
            Assert.False(svc.WorkerAlive);
        }

        /// <summary>The real engine, end to end, the Unity reference's exact
        /// sequence: Init, parametric effect from time-value amplitude pairs,
        /// body target, live intensity, PlayEvent with the negative-now
        /// clock, compute ticks, stop, quit. This is the shipped HAR.dll
        /// executing, not a mock, and it needs no Razer device.</summary>
        [Fact]
        public void RealEngine_FullLifecycle()
        {
            if (!NativeEngineLoadsHere) return;
            Assert.True(Har.Init());
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int id = Har.AddParametricEffect(
                    new double[] { 0.0, 1.0, 1.0, 1.0 }, 4,
                    null, 0, 65.0, 300.0, null, 0, true);
                Assert.NotEqual(-1, id);

                // The looping envelope spans one second of authored time.
                Assert.True(Har.GetVibrationLength(id) > 0.5);

                Har.AddTargetToEventMarshal(id,
                    new[] { new SensaHapticsService.CommandData(1, 0, 0) }, 1);
                Har.SetEventIntensity(id, 0.0);
                Har.PlayEvent(id, -clock.Elapsed.TotalSeconds, 0.0, 0.0);

                for (int i = 0; i < 5; i++)
                {
                    Har.SetEventIntensity(id, i / 4.0);
                    Har.ComputeAllEvents(clock.Elapsed.TotalSeconds);
                    System.Threading.Thread.Sleep(16);
                }

                Har.StopAllEvents();
            }
            finally
            {
                Har.Quit();
            }
        }

        /// <summary>The provider trio never throws on a machine without
        /// Synapse's Interhaptics runtime: a clean false (or, on a machine
        /// that has Synapse, a clean true) is the contract the service's
        /// retry loop is built on.</summary>
        [Fact]
        public void Provider_DegradesCleanlyWithoutSynapse()
        {
            if (!NativeEngineLoadsHere) return;
            bool up = Provider.ProviderInit();
            if (up)
            {
                // A dev machine with Synapse: presence query and clean must
                // also not throw.
                Provider.ProviderIsPresent();
                Assert.True(Provider.ProviderClean());
            }
            else
            {
                // Init failed (no Synapse runtime). The Unity reference
                // never queries IsPresent for a provider whose Init failed
                // (DeviceInitLoop only registers successful ones), so its
                // value here is UNDEFINED by the reference: measured on
                // this bench it returns true even with init failed, which
                // is why the service gates rendering on init AND presence.
                // The claim this test makes is no-throw.
                Provider.ProviderIsPresent();
            }
        }

        /// <summary>Init after Quit in the same process: an app engine
        /// restart starts a fresh service worker, so a second Init must
        /// succeed. DIAGNOSTIC for the ordering-dependent failure.</summary>
        [Fact]
        public void RealEngine_SurvivesReinit()
        {
            if (!NativeEngineLoadsHere) return;
            Assert.True(Har.Init());
            Har.Quit();
            bool second = Har.Init();
            try { Assert.True(second); }
            finally { if (second) Har.Quit(); }
        }

        /// <summary>The full-lifecycle service against the missing-runtime
        /// path: Start with tiny intervals, confirm the engine comes up and
        /// the state lands on WaitingForRuntime (this bench has no Synapse),
        /// then Stop ends the worker and reports Stopped. The level comes from
        /// the injected source, the Razer Sensa row's in production (#494).</summary>
        [Fact]
        public void Service_StartsTheEngineAndDegradesWithoutRuntime()
        {
            if (!NativeEngineLoadsHere) return;
            var states = new System.Collections.Concurrent.ConcurrentQueue<SensaServiceState>();
            float level = 0.5f;
            using var svc = new SensaHapticsService(100, 5, SensaHapticsService.DefaultPredecessorJoinMs, () => level);
            svc.StateChanged += s => states.Enqueue(s);
            svc.Start();

            long start = Environment.TickCount64;
            while (Environment.TickCount64 - start < 5000 && !svc.EngineStarted)
                System.Threading.Thread.Sleep(10);
            Assert.True(svc.EngineStarted);

            start = Environment.TickCount64;
            while (Environment.TickCount64 - start < 5000 && states.IsEmpty)
                System.Threading.Thread.Sleep(10);

            // The provider bring-up actually runs. The long.MinValue
            // sentinel bug made this count sit at ZERO forever while the
            // worker looked healthy from every other angle (tick-minus-
            // MinValue overflows negative), so one attempt is the
            // discriminating fact. Two is not asserted: a machine where
            // the first init succeeds stops retrying by design.
            long tries = Environment.TickCount64;
            while (Environment.TickCount64 - tries < 5000 && svc.ProviderInitAttempts < 1)
                System.Threading.Thread.Sleep(10);
            Assert.True(svc.ProviderInitAttempts >= 1);

            // Stopped comes from the worker's finally, so it shows the worker
            // ran its teardown. WorkerAlive reads false after any Stop, since
            // Stop drops the thread whether or not the join finished.
            svc.Stop();
            Assert.Contains(SensaServiceState.Stopped, states);
            // On a Synapse-less bench the pre-stop state is WaitingForRuntime;
            // a bench WITH the runtime may report Active instead. Either way
            // the service reported something before Stopped.
            Assert.True(states.Count >= 2);
        }

        /// <summary>F10: a worker still inside ProviderInit outlives its
        /// service's Stop (3 s join, then _thread nulled regardless). Its
        /// finally used to Har.Quit under the NEXT instance's engine. The
        /// next worker now joins its predecessor before it brings the engine
        /// up, so the engine B starts is never quit by A. The hook holds
        /// worker A inside the bring-up window. A's Dispose returns after the
        /// timed-out join, B starts and waits on A, the hook is released, and
        /// B starts its engine only once A's thread is gone, then survives
        /// A's teardown.</summary>
        [Fact]
        public void Service_NextWorkerWaitsForAStragglingPredecessor()
        {
            if (!NativeEngineLoadsHere) return;
            using var hold = new System.Threading.ManualResetEventSlim(false);
            System.Threading.Thread aThread = null;
            SensaHapticsService.BeforeProviderInit = () =>
            {
                aThread ??= System.Threading.Thread.CurrentThread;
                hold.Wait(10000);
            };
            SensaHapticsService a = null, b = null;
            try
            {
                a = new SensaHapticsService(retryMs: 50, tickMs: 5);
                a.Start();
                long t0 = Environment.TickCount64;
                while (Environment.TickCount64 - t0 < 3000 && a.ProviderInitAttempts < 1)
                    System.Threading.Thread.Sleep(5);
                Assert.True(a.EngineStarted);
                Assert.True(a.ProviderInitAttempts >= 1, "worker A never reached the bring-up window");
                Assert.NotNull(aThread);

                // Stop joins 3 s while A sits in the hook, then gives up
                // with A's worker still parked there.
                a.Dispose();
                Assert.True(aThread.IsAlive, "A's Dispose must return with A's worker parked in the hook");

                b = new SensaHapticsService(retryMs: 50, tickMs: 5);
                b.Start();
                System.Threading.Thread.Sleep(100);
                // B is parked on A's join and has not started its engine.
                Assert.True(b.WorkerAlive);
                Assert.False(b.EngineStarted);
                Assert.Equal(0, b.ProviderInitAttempts);

                hold.Set();
                // A leaves the hook, sees its stop, quits its engine, and only
                // then may B start one. B's start is the discriminating fact.
                Assert.True(aThread.Join(10000), "A's worker never exited after the hook released");
                t0 = Environment.TickCount64;
                while (Environment.TickCount64 - t0 < 3000 && b.ProviderInitAttempts < 1)
                    System.Threading.Thread.Sleep(5);
                Assert.True(b.EngineStarted, "B never started its engine after A left");
                System.Threading.Thread.Sleep(100);
                Assert.True(b.WorkerAlive, "B's worker died after A's teardown");
                Assert.True(b.ProviderInitAttempts >= 1, "B never reached its own bring-up");
            }
            finally
            {
                hold.Set();
                SensaHapticsService.BeforeProviderInit = null;
                aThread?.Join(10000);
                b?.Dispose();
                a?.Dispose();
            }
        }

        /// <summary>The predecessor join is bounded. A predecessor wedged
        /// inside ProviderInit used to block every later worker on an
        /// unbounded Join, so each enable added one parked thread and the
        /// feature never came back. The successor now waits its deadline and
        /// quits without starting its engine, because starting over a live
        /// predecessor is the very handoff fault the join exists to prevent,
        /// and it reports
        /// Stopped through its normal teardown. The hook here stays closed
        /// until the cleanup, so worker A cannot have left it.</summary>
        [Fact]
        public void Service_GivesUpOnAWedgedPredecessorInsteadOfBlockingForever()
        {
            if (!NativeEngineLoadsHere) return;
            using var hold = new System.Threading.ManualResetEventSlim(false);
            System.Threading.Thread aThread = null;
            SensaHapticsService.BeforeProviderInit = () =>
            {
                aThread ??= System.Threading.Thread.CurrentThread;
                hold.Wait(30000);
            };
            SensaHapticsService a = null, b = null;
            try
            {
                a = new SensaHapticsService(retryMs: 50, tickMs: 5);
                a.Start();
                long t0 = Environment.TickCount64;
                while (Environment.TickCount64 - t0 < 5000 && a.ProviderInitAttempts < 1)
                    System.Threading.Thread.Sleep(5);
                Assert.True(a.ProviderInitAttempts >= 1, "worker A never reached the bring-up window");

                // Stop joins 3 s and gives up with A still parked in the hook.
                a.Dispose();
                Assert.NotNull(aThread);
                Assert.True(aThread.IsAlive, "A's Dispose must return with A's worker parked in the hook");

                var states = new System.Collections.Concurrent.ConcurrentQueue<SensaServiceState>();
                b = new SensaHapticsService(retryMs: 50, tickMs: 5, predecessorJoinMs: 250);
                b.StateChanged += s => states.Enqueue(s);
                b.Start();

                t0 = Environment.TickCount64;
                while (Environment.TickCount64 - t0 < 5000 && b.WorkerAlive)
                    System.Threading.Thread.Sleep(10);
                Assert.False(b.WorkerAlive, "B's worker never gave up on the wedged predecessor");
                // Positive control: A is still inside the hook, so B left on
                // its deadline and not because A finished.
                Assert.True(aThread.IsAlive, "A was not wedged, so the give-up path never ran");
                Assert.Equal(0, b.ProviderInitAttempts);
                Assert.False(b.EngineStarted);
                Assert.Contains(SensaServiceState.Stopped, states);
            }
            finally
            {
                hold.Set();
                SensaHapticsService.BeforeProviderInit = null;
                // Let A finish its engine teardown before the next test
                // touches the shared native engine.
                aThread?.Join(10000);
                b?.Dispose();
                a?.Dispose();
            }
        }

        /// <summary>Source contracts for the Razer Sensa row (#494). The
        /// worker streams the row's level, which Step 2 sets per virtual
        /// controller through the row's Force Feedback settings. The global
        /// Step 5 lane, the Dashboard card and the switch are gone, and the
        /// switch's two legs are read once by the migration and never
        /// written. The peripheral host runs the worker while the row is
        /// assigned.</summary>
        [Fact]
        public void TheWorkerStreamsTheRowsLevel_AndTheGlobalSwitchIsGone()
        {
            string service = RepoText("PadForge.App", "Services", "SensaHapticsService.cs");
            Assert.Contains("PeripheralOutputs.AmplitudeOf(", service);
            Assert.Contains("PeripheralOutputRow.IdentityFor(", service);
            Assert.Contains("float amp = Math.Clamp(_amplitude(), 0f, 1f);", service);
            Assert.DoesNotContain("PublishAmplitude", service);
            Assert.DoesNotContain("PublisherArmed", service);

            string step5 = RepoText("PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs");
            Assert.DoesNotContain("UpdateSensaLane", step5);
            string im = RepoText("PadForge.App", "Common", "Input", "InputManager.cs");
            Assert.DoesNotContain("UpdateSensaLane", im);

            string ss = RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("public bool EnableSensaHaptics { get; set; }\r\n        public bool ShouldSerializeEnableSensaHaptics() => false;", ss);
            Assert.Contains("public bool? EnableSensaHaptics { get; set; }\r\n        public bool ShouldSerializeEnableSensaHaptics() => false;", ss);
            Assert.Contains("PeripheralSwitchMigration.Switches(data.AppSettings)", ss);
            Assert.DoesNotContain("_mainVm.Dashboard.EnableSensaHaptics", ss);
            string migration = RepoText("PadForge.App", "Services", "PeripheralSwitchMigration.cs");
            Assert.Contains("new LegacySwitch(PeripheralRowKind.RazerSensa, app?.EnableSensaHaptics ?? false,", migration);

            string page = RepoText("PadForge.App", "Views", "DashboardPage.xaml");
            Assert.DoesNotContain("EnableSensaHaptics", page);
            Assert.DoesNotContain("SensaStatus", page);

            string host = RepoText("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs");
            Assert.Contains("bool assigned = linked && SettingsManager.SlotOrders.GetIdentityPlayerNumber(sensaGuid) > 0;", host);
        }

        /// <summary>The Korean Sensa strings spell haptic the way every
        /// other Korean string does. Four of them carried a wrong first
        /// syllable (U+D581 where U+D585 belongs).</summary>
        [Fact]
        public void TheKoreanStringsSpellHaptic()
        {
            string text = RepoText("PadForge.App", "Resources", "Strings", "Strings.ko.resx");
            Assert.DoesNotContain("\uD581\uD2F1", text);
            Assert.Contains("Sensa HD \uD585\uD2F1", text);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
