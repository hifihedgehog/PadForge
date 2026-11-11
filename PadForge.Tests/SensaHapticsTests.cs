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

        /// <summary>HAR.dll and the Razer provider are x64 images, so the
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

        /// <summary>The crash path's wait (#494): after the engine's quiesce
        /// zeroes the level, the wait returns once the worker has rendered
        /// the zero, which a slow tick renders a while after the quiesce.
        /// What it answers for a service with no worker is held by
        /// <see cref="WaitForSilence_AnswersAtOnceAfterAStopWhoseJoinGaveUp"/>.</summary>
        [Fact]
        public void WaitForSilence_ReturnsOnceTheZeroIsRendered()
        {
            if (!NativeEngineLoadsHere) return;
            float level = 0.8f;
            using var svc = new SensaHapticsService(30000, 200, SensaHapticsService.DefaultPredecessorJoinMs,
                () => System.Threading.Volatile.Read(ref level));
            svc.Start();
            Assert.True(System.Threading.SpinWait.SpinUntil(() => svc.RenderedLevel > 0.5f, 5000), "the level was rendered");
            Assert.False(svc.WaitForSilence(0), "the wait passed a level still rendering");

            System.Threading.Volatile.Write(ref level, 0f);
            Assert.True(svc.WaitForSilence(5000), "the worker never rendered the zero");
            Assert.Equal(0f, svc.RenderedLevel);
        }

        /// <summary>The crash wait never passes a render in flight (#494). A
        /// tick that read its level before the engine's quiesce zeroed it
        /// still renders that level, so the wait holds until that tick has
        /// ended and a later one has rendered the zero. The tick here is
        /// held with the level in hand, read and not yet rendered, which is
        /// where a wait that looked only at the last rendered level took
        /// the zero before it for silence.</summary>
        [Fact]
        public void WaitForSilence_HoldsForATickThatReadItsLevelBeforeTheQuiesce()
        {
            if (!NativeEngineLoadsHere) return;
            int levelBits = 0, reads = 0, held = 0;
            using var read = new System.Threading.ManualResetEventSlim(false);
            using var release = new System.Threading.ManualResetEventSlim(false);
            using var svc = new SensaHapticsService(30000, 5, SensaHapticsService.DefaultPredecessorJoinMs, () =>
            {
                float level = BitConverter.Int32BitsToSingle(System.Threading.Volatile.Read(ref levelBits));
                System.Threading.Interlocked.Increment(ref reads);
                if (level != 0f && System.Threading.Interlocked.Exchange(ref held, 1) == 0)
                {
                    read.Set();
                    release.Wait(10000);
                }
                return level;
            });
            try
            {
                svc.Start();
                // A second read means a whole tick has rendered the zero.
                Assert.True(System.Threading.SpinWait.SpinUntil(
                    () => System.Threading.Volatile.Read(ref reads) >= 2, 5000), "the worker ticked");
                Assert.Equal(0f, svc.RenderedLevel);

                // The level rises, a tick reads it, and the quiesce zeroes it,
                // interlocked as PeripheralOutputs.SilenceHaptics writes it.
                System.Threading.Interlocked.Exchange(ref levelBits, BitConverter.SingleToInt32Bits(0.8f));
                Assert.True(read.Wait(5000), "a tick read the raised level");
                System.Threading.Interlocked.Exchange(ref levelBits, 0);
                Assert.False(svc.WaitForSilence(200), "the wait passed a render in flight");

                int before = System.Threading.Volatile.Read(ref reads);
                release.Set();
                Assert.True(svc.WaitForSilence(5000), "the worker never rendered the zero");
                Assert.Equal(0f, svc.RenderedLevel);
                Assert.True(System.Threading.Volatile.Read(ref reads) > before, "the zero came from no later tick");
            }
            finally { release.Set(); }
        }

        /// <summary>The crash wait answers at once for a service whose Stop
        /// gave up on its join (#494). The worker is then no longer the
        /// service's, and it can still hold a tick with a level in hand.
        /// That is the one state the clause for a service with no worker
        /// decides: the mark stays set until that worker comes back, so a
        /// wait that looked at the mark and the rendered level alone spent
        /// its whole budget on a service already stopped. The tick here is
        /// held inside its level read, and Stop's join gives up after
        /// 50 ms.</summary>
        [Fact]
        public void WaitForSilence_AnswersAtOnceAfterAStopWhoseJoinGaveUp()
        {
            if (!NativeEngineLoadsHere) return;
            int held = 0;
            System.Threading.Thread worker = null;
            using var read = new System.Threading.ManualResetEventSlim(false);
            using var release = new System.Threading.ManualResetEventSlim(false);
            var svc = new SensaHapticsService(retryMs: 30000, tickMs: 5,
                predecessorJoinMs: SensaHapticsService.DefaultPredecessorJoinMs, amplitude: () =>
                {
                    if (System.Threading.Interlocked.Exchange(ref held, 1) == 0)
                    {
                        System.Threading.Volatile.Write(ref worker, System.Threading.Thread.CurrentThread);
                        read.Set();
                        release.Wait(30000);
                    }
                    return 0.8f;
                }, stopJoinMs: 50);
            try
            {
                svc.Start();
                Assert.True(read.Wait(10000), "the worker never read its level");
                // Positive control: while the worker is still the service's,
                // the tick in flight holds the wait.
                Assert.False(svc.WaitForSilence(0), "the wait passed a tick in flight");

                // Stop's join gives up with the tick still held in its read.
                svc.Stop();
                Assert.True(System.Threading.Volatile.Read(ref worker).IsAlive, "Stop must return with the tick still held");
                Assert.False(svc.WorkerAlive);
                Assert.True(svc.WaitForSilence(0), "a stopped service made the crash path wait");
            }
            finally
            {
                release.Set();
                svc.Dispose();
                // Stop joined 50 ms here, so let the worker finish its engine
                // teardown before the next test touches the engine.
                System.Threading.Volatile.Read(ref worker)?.Join(15000);
            }
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
            using var parked = new System.Threading.ManualResetEventSlim(false);
            System.Threading.Thread aThread = null;
            SensaHapticsService.BeforeProviderInit = () =>
            {
                aThread ??= System.Threading.Thread.CurrentThread;
                parked.Set();
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
                // The worker counts the attempt before it runs the hook, so
                // the count alone can be read before the hook has named A.
                Assert.True(parked.Wait(5000), "worker A never entered the hook");
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

        /// <summary>A Start on the same service after a Stop whose join gave
        /// up leaves the old worker stopped. Start and Stop shared one flag,
        /// so the Start cleared the stop under a worker still inside the
        /// bring-up: it went on ticking, and its successor waited out the
        /// predecessor join behind it and quit. Each Start now has its own
        /// stop, so the old worker ends once it leaves the bring-up and the
        /// successor brings the engine up after it. The hook holds the first
        /// worker, and only it, inside the bring-up window.</summary>
        [Fact]
        public void Service_AStartAfterATimedOutStop_LeavesTheOldWorkerStopped()
        {
            if (!NativeEngineLoadsHere) return;
            using var hold = new System.Threading.ManualResetEventSlim(false);
            using var parked = new System.Threading.ManualResetEventSlim(false);
            using var successor = new System.Threading.ManualResetEventSlim(false);
            System.Threading.Thread first = null;
            SensaHapticsService.BeforeProviderInit = () =>
            {
                var me = System.Threading.Thread.CurrentThread;
                var holder = System.Threading.Interlocked.CompareExchange(ref first, me, null);
                if (holder == null)
                {
                    parked.Set();
                    hold.Wait(30000);
                }
                else if (holder != me) successor.Set();
            };
            var svc = new SensaHapticsService(retryMs: 50, tickMs: 5,
                predecessorJoinMs: SensaHapticsService.DefaultPredecessorJoinMs, amplitude: () => 0f, stopJoinMs: 50);
            try
            {
                svc.Start();
                Assert.True(parked.Wait(5000), "the worker never reached the bring-up window");

                // Stop's join gives up with the worker still parked in the hook.
                svc.Stop();
                Assert.True(first.IsAlive, "Stop must return with the worker parked in the hook");
                Assert.False(svc.WorkerAlive);

                svc.Start();
                Assert.True(svc.WorkerAlive);

                hold.Set();
                Assert.True(first.Join(10000), "the old worker kept running after a later Start");
                // The successor was waiting on it, and now runs its own bring-up.
                Assert.True(successor.Wait(10000), "the successor never reached its own bring-up");
                Assert.True(svc.WorkerAlive, "the successor quit");
            }
            finally
            {
                hold.Set();
                SensaHapticsService.BeforeProviderInit = null;
                svc.Dispose();
                // Dispose joined 50 ms here, so let both workers finish their
                // engine teardown before the next test touches the engine.
                first?.Join(10000);
                (typeof(SensaHapticsService)
                    .GetField("s_lastWorker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?.GetValue(null) as System.Threading.Thread)?.Join(15000);
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

            // Neither the Dashboard nor the Extras page, where its services went (#574).
            foreach (string name in new[] { "DashboardPage.xaml", "ExtrasPage.xaml" })
            {
                string page = RepoText("PadForge.App", "Views", name);
                Assert.DoesNotContain("EnableSensaHaptics", page);
                Assert.DoesNotContain("SensaStatus", page);
            }

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
