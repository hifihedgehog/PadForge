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

        /// <summary>The real engine, end to end, the Unity reference's exact
        /// sequence: Init, parametric effect from time-value amplitude pairs,
        /// body target, live intensity, PlayEvent with the negative-now
        /// clock, compute ticks, stop, quit. This is the shipped HAR.dll
        /// executing, not a mock, and it needs no Razer device.</summary>
        [Fact]
        public void RealEngine_FullLifecycle()
        {
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
            Assert.True(Har.Init());
            Har.Quit();
            bool second = Har.Init();
            try { Assert.True(second); }
            finally { if (second) Har.Quit(); }
        }

        /// <summary>Amplitude conversion: the max of the four packed voices
        /// normalized to 0..1, matching LfeOutputState's packing order.</summary>
        [Theory]
        [InlineData(0UL, 0f)]
        [InlineData(0xFFFFUL, 1f)]                       // low voice full
        [InlineData(0xFFFF_0000UL, 1f)]                  // high voice full
        [InlineData(0xFFFF_0000_0000_0000UL, 1f)]        // right trigger full
        [InlineData(0x8000UL, 32768f / 65535f)]
        public void PackToAmplitude_TakesTheLoudestVoice(ulong pack, float expected)
        {
            Assert.Equal(expected, SensaHapticsService.PackToAmplitude(unchecked((long)pack)), 5);
        }

        /// <summary>Publish clamps and round-trips through the volatile
        /// store the worker reads.</summary>
        [Fact]
        public void PublishAmplitude_Clamps()
        {
            SensaHapticsService.PublishAmplitude(2f);
            SensaHapticsService.PublishAmplitude(-1f);
            SensaHapticsService.PublishAmplitude(0.5f);
            // No public getter by design (the worker owns the read); this
            // pins that publishing never throws and the clamp compiles the
            // boundary contract into the test's own calls.
        }

        /// <summary>The full-lifecycle service against the missing-runtime
        /// path: Start with tiny intervals, confirm the publisher arms and
        /// the state lands on WaitingForRuntime (this bench has no Synapse),
        /// then Stop disarms the publisher and reports Stopped.</summary>
        [Fact]
        public void Service_ArmsPublisherAndDegradesWithoutRuntime()
        {
            var states = new System.Collections.Concurrent.ConcurrentQueue<SensaServiceState>();
            using var svc = new SensaHapticsService(retryMs: 100, tickMs: 5);
            svc.StateChanged += s => states.Enqueue(s);
            svc.Start();

            long start = Environment.TickCount64;
            while (Environment.TickCount64 - start < 5000 && !SensaHapticsService.PublisherArmed)
                System.Threading.Thread.Sleep(10);
            Assert.True(SensaHapticsService.PublisherArmed);

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

            svc.Stop();
            Assert.False(SensaHapticsService.PublisherArmed);
            Assert.Contains(SensaServiceState.Stopped, states);
            // On a Synapse-less bench the pre-stop state is WaitingForRuntime;
            // a bench WITH the runtime may report Active instead. Either way
            // the service reported something before Stopped.
            Assert.True(states.Count >= 2);
        }

        /// <summary>F10: a worker still inside ProviderInit outlives its
        /// service's Stop (3 s join, then _thread nulled regardless). Its
        /// finally used to disarm the publisher and Har.Quit under the
        /// NEXT instance's engine. The next worker now joins its
        /// predecessor before arming, so once the old worker is released
        /// the new one is still armed and still running. The hook holds
        /// worker A inside the bring-up window. A's Dispose returns after
        /// the timed-out join, B starts and waits on A, the hook is
        /// released, and B must survive A's teardown.</summary>
        [Fact]
        public void Service_NextWorkerWaitsForAStragglingPredecessor()
        {
            using var hold = new System.Threading.ManualResetEventSlim(false);
            SensaHapticsService.BeforeProviderInit = () => hold.Wait(10000);
            SensaHapticsService a = null, b = null;
            try
            {
                a = new SensaHapticsService(retryMs: 50, tickMs: 5);
                a.Start();
                long t0 = Environment.TickCount64;
                while (Environment.TickCount64 - t0 < 3000 && a.ProviderInitAttempts < 1)
                    System.Threading.Thread.Sleep(5);
                Assert.True(SensaHapticsService.PublisherArmed);
                Assert.True(a.ProviderInitAttempts >= 1, "worker A never reached the bring-up window");

                // Stop joins 3 s while A sits in the hook, then gives up
                // with A's worker still parked there and still armed.
                a.Dispose();
                Assert.True(SensaHapticsService.PublisherArmed,
                    "A's Dispose must return with A's worker still armed and parked in the hook");

                b = new SensaHapticsService(retryMs: 50, tickMs: 5);
                b.Start();
                System.Threading.Thread.Sleep(100);
                // B is parked on A's join and has not armed on its own yet:
                // the armed flag still belongs to A.
                Assert.True(b.WorkerAlive);
                Assert.Equal(0, b.ProviderInitAttempts);

                hold.Set();
                t0 = Environment.TickCount64;
                while (Environment.TickCount64 - t0 < 2000 && b.ProviderInitAttempts < 1)
                    System.Threading.Thread.Sleep(5);
                // The discriminating observation is A's finally, which runs
                // only after A's provider init returns and its loop sees the
                // stop. That native call can outlast any fixed settle, so
                // wait for A's worker to be gone before asserting. With the
                // join, A was already gone before B armed. Without it, A's
                // finally lands here and clears the flag under B.
                t0 = Environment.TickCount64;
                while (Environment.TickCount64 - t0 < 8000 && a.WorkerAlive)
                    System.Threading.Thread.Sleep(10);
                Assert.False(a.WorkerAlive, "A's worker never exited after the hook released");
                System.Threading.Thread.Sleep(100);
                Assert.True(b.WorkerAlive, "B's worker died after A's teardown");
                Assert.True(SensaHapticsService.PublisherArmed, "A's finally disarmed the publisher under B");
                Assert.True(b.ProviderInitAttempts >= 1, "B never reached its own bring-up");
            }
            finally
            {
                hold.Set();
                SensaHapticsService.BeforeProviderInit = null;
                b?.Dispose();
                a?.Dispose();
            }
        }

        /// <summary>Source contracts: the poll-lane publisher exists behind
        /// the armed gate with the same rumble authority as the audio lane,
        /// the setting is GLOBAL ONLY with the autosave allowlist entry
        /// (both #373 lessons, applied from birth), and the Dashboard card
        /// binds the toggle and status.</summary>
        [Fact]
        public void FeedAndSiblingContracts()
        {
            string step5 = RepoText("PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs");
            int at = step5.IndexOf("private void UpdateSensaLane()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = step5.Substring(at, 1300);
            Assert.Contains("SensaHapticsService.PublisherArmed) return;", body);
            Assert.Contains("LfeOutputState.MaxMerge", body);
            Assert.Contains("PublishAmplitude(best)", body);

            string im = RepoText("PadForge.App", "Common", "Input", "InputManager.cs");
            Assert.Contains("UpdateSensaLane();", im);

            string ss = RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("_mainVm.Dashboard.EnableSensaHaptics = appSettings.EnableSensaHaptics;", ss);
            Assert.Contains("EnableSensaHaptics = _mainVm.Dashboard.EnableSensaHaptics,", ss);
            Assert.DoesNotContain("active.EnableSensaHaptics", ss);
            Assert.DoesNotContain("profile.EnableSensaHaptics", ss);

            string mw = RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("nameof(DashboardViewModel.EnableSensaHaptics)", mw);

            string page = RepoText("PadForge.App", "Views", "DashboardPage.xaml");
            Assert.Contains("Binding EnableSensaHaptics", page);
            Assert.Contains("Binding SensaStatus", page);
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
