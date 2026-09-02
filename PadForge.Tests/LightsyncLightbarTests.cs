using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Logitech LIGHTSYNC lightbar mirror (#382, asked in discussion
    /// #379), the Chroma (#373) sibling. The bench replaces the native
    /// layer with a scripted fake, the fake-server pattern applied to a
    /// DLL boundary, and pins the reference-verified behavior: the init
    /// order the suites use (init, settle, target, save), percent
    /// conversion, change-only sends plus the liveness re-send, retry
    /// when no Logitech software serves the SDK, the failure-streak
    /// re-initialization, and restore-before-shutdown teardown.
    /// </summary>
    [Collection("LightsyncPublishedColor")]
    public class LightsyncLightbarTests : IDisposable
    {
        private sealed class FakeNative : LightsyncLightbarService.ILogiLedNative
        {
            private readonly object _lock = new();
            private readonly List<string> _calls = new();
            private readonly string _tag;
            private readonly List<string> _shared;
            public readonly List<(int R, int G, int B)> Lit = new();

            public volatile bool Present = true;
            public volatile bool LoadOk = true;
            public volatile bool InitOk = true;
            public volatile bool SetOk = true;

            /// <summary>When set, Init blocks on it after logging: the
            /// unbounded native call of a G HUB cold start.</summary>
            public ManualResetEventSlim InitGate;

            /// <summary>Optional cross-instance log ("tag:call") so a test
            /// with two services can order their engine calls.</summary>
            public FakeNative(string tag = null, List<string> shared = null)
            {
                _tag = tag;
                _shared = shared;
            }

            private void Log(string s)
            {
                lock (_lock) _calls.Add(s);
                if (_shared != null) lock (_shared) _shared.Add(_tag + ":" + s);
            }
            public string[] Calls { get { lock (_lock) return _calls.ToArray(); } }
            public int Count(string name) => Calls.Count(c => c == name);

            public bool SoftwarePresent() { Log("present"); return Present; }
            public bool TryLoad(out string detail) { Log("load"); detail = "fake"; return LoadOk; }
            public bool Init()
            {
                Log("init");
                InitGate?.Wait();
                return InitOk;
            }
            public bool SetTargetAll() { Log("target"); return true; }
            public bool SaveCurrent() { Log("save"); return true; }
            public bool SetLighting(int r, int g, int b)
            {
                Log("set");
                if (SetOk) lock (_lock) Lit.Add((r, g, b));
                return SetOk;
            }
            public void RestoreAndShutdown() => Log("restore-shutdown");
            public void Unload() => Log("unload");
        }

        public LightsyncLightbarTests() => LightsyncLightbarService.ResetPublishedForTest();
        public void Dispose() => LightsyncLightbarService.ResetPublishedForTest();

        private static bool WaitFor(Func<bool> cond, int timeoutMs = 5000)
        {
            long end = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < end)
            {
                if (cond()) return true;
                Thread.Sleep(20);
            }
            return cond();
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(255, 100)]
        [InlineData(128, 50)]
        [InlineData(64, 25)]
        [InlineData(2, 1)]     // 0.78: rounding, never truncation (Aurora's bug)
        [InlineData(130, 51)]  // 50.98: same discrimination from above
        [InlineData(-5, 0)]
        [InlineData(300, 100)]
        public void ToPercent_RoundsAndClamps(int channel, int expected)
        {
            Assert.Equal(expected, LightsyncLightbarService.ToPercent(channel));
        }

        /// <summary>The full session: the init order the references use,
        /// the percent-converted send, change-only sending with the
        /// liveness re-send, and restore-before-unload teardown.</summary>
        [Fact]
        public void FullSession_OrderSendsLivenessAndTeardown()
        {
            var fake = new FakeNative();
            var states = new List<LightsyncServiceState>();
            using var svc = new LightsyncLightbarService(fake,
                retryMs: 100, pollMs: 10, settleMs: 10, presenceSettleMs: 10, livenessMs: 250);
            svc.StateChanged += s => { lock (states) states.Add(s); };

            LightsyncLightbarService.Publish(255, 0, 0);
            svc.Start();

            Assert.True(WaitFor(() => fake.Lit.Count >= 1), "first send never arrived");
            Assert.Equal((100, 0, 0), fake.Lit[0]);

            // Init ordering: init happened before target and save, and a
            // target re-assert precedes the send (sticky global state).
            var calls = fake.Calls;
            int init = Array.IndexOf(calls, "init");
            int target = Array.IndexOf(calls, "target");
            int save = Array.IndexOf(calls, "save");
            int set = Array.IndexOf(calls, "set");
            Assert.True(init >= 0 && target > init && save > target && set > save,
                $"order was {string.Join(",", calls)}");
            lock (states) Assert.Contains(LightsyncServiceState.Connected, states);

            // Change-only: a new color sends once, promptly.
            int before = fake.Lit.Count;
            LightsyncLightbarService.Publish(0, 128, 255);
            Assert.True(WaitFor(() => fake.Lit.Count > before));
            Assert.Equal((0, 50, 100), fake.Lit[^1]);

            // Liveness: the held color is re-sent on the liveness cadence.
            int held = fake.Lit.Count;
            Assert.True(WaitFor(() => fake.Lit.Count > held, 2000),
                "liveness re-send never fired");
            Assert.Equal((0, 50, 100), fake.Lit[^1]);

            svc.Stop();
            Assert.True(fake.Count("restore-shutdown") >= 1, "teardown must restore then shutdown");
            Assert.True(fake.Count("unload") >= 1, "teardown must unload the engine");
            lock (states) Assert.Equal(LightsyncServiceState.Stopped, states[^1]);
        }

        /// <summary>No Logitech software: the service reports waiting,
        /// retries on the cadence, and never loads the engine.</summary>
        [Fact]
        public void NoSoftware_WaitsRetriesAndNeverLoads()
        {
            var fake = new FakeNative { Present = false };
            var states = new List<LightsyncServiceState>();
            using var svc = new LightsyncLightbarService(fake,
                retryMs: 80, pollMs: 10, settleMs: 5, presenceSettleMs: 5, livenessMs: 500);
            svc.StateChanged += s => { lock (states) states.Add(s); };
            svc.Start();

            Assert.True(WaitFor(() =>
            {
                lock (states) return states.Count(s => s == LightsyncServiceState.WaitingForGHub) >= 2;
            }), "waiting reports never accumulated");
            Assert.Equal(0, fake.Count("load"));
            lock (states) Assert.DoesNotContain(LightsyncServiceState.Connected, states);
        }

        /// <summary>A refused init is retried, releasing the engine each
        /// cycle rather than leaking it.</summary>
        [Fact]
        public void RefusedInit_RetriesAndUnloads()
        {
            var fake = new FakeNative { InitOk = false };
            using var svc = new LightsyncLightbarService(fake,
                retryMs: 80, pollMs: 10, settleMs: 5, presenceSettleMs: 5, livenessMs: 500);
            svc.Start();

            Assert.True(WaitFor(() => fake.Count("init") >= 2), "init was not retried");
            Assert.True(fake.Count("unload") >= 1, "a failed init must release the engine");
        }

        /// <summary>The only signal a dead G HUB gives is failing sends:
        /// a short failure streak tears the session down and re-inits,
        /// and recovery reconnects.</summary>
        [Fact]
        public void SendFailureStreak_ReinitializesAndRecovers()
        {
            var fake = new FakeNative { SetOk = false };
            var states = new List<LightsyncServiceState>();
            using var svc = new LightsyncLightbarService(fake,
                retryMs: 60, pollMs: 10, settleMs: 5, presenceSettleMs: 5, livenessMs: 100);
            svc.StateChanged += s => { lock (states) states.Add(s); };
            LightsyncLightbarService.Publish(10, 20, 30);
            svc.Start();

            Assert.True(WaitFor(() => fake.Count("init") >= 2),
                "the failure streak never forced a re-init");
            Assert.True(fake.Count("restore-shutdown") >= 1);

            fake.SetOk = true;
            Assert.True(WaitFor(() => fake.Lit.Count >= 1), "recovery never sent");
            lock (states) Assert.Contains(LightsyncServiceState.Connected, states);
        }

        /// <summary>A Stop that times out with the worker stuck inside the
        /// SDK (Init during a G HUB cold start) orphans the worker, and the
        /// owner disposes and recreates the service on re-enable. When the
        /// orphan returns it must neither call LogiLedShutdown, which is
        /// process-global and would tear down the new instance's session,
        /// nor report into the old instance's Dashboard closure, and the
        /// new worker must not touch the engine until the orphan is out
        /// of it.</summary>
        [Fact]
        public void OrphanedWorker_NeverShutsDownOrReportsOverTheNewSession()
        {
            var shared = new List<string>();
            using var gate = new ManualResetEventSlim(false);
            var oldFake = new FakeNative("old", shared) { InitGate = gate };
            var oldStates = new List<LightsyncServiceState>();
            var oldSvc = new LightsyncLightbarService(oldFake,
                retryMs: 100, pollMs: 10, settleMs: 5, presenceSettleMs: 5, livenessMs: 500,
                stopWaitMs: 200);
            oldSvc.StateChanged += s => { lock (oldStates) oldStates.Add(s); };
            oldSvc.Start();
            Assert.True(WaitFor(() => oldFake.Count("init") >= 1), "the old worker never reached Init");

            // The owner's re-enable: dispose (Stop waits 200 ms and gives
            // up), then a fresh instance.
            long t0 = Environment.TickCount64;
            oldSvc.Dispose();
            Assert.True(Environment.TickCount64 - t0 < 2000, "Stop must return after its bounded wait");
            int oldStatesAfterStop;
            lock (oldStates) oldStatesAfterStop = oldStates.Count;

            var newFake = new FakeNative("new", shared);
            var newStates = new List<LightsyncServiceState>();
            using var newSvc = new LightsyncLightbarService(newFake,
                retryMs: 100, pollMs: 10, settleMs: 5, presenceSettleMs: 5, livenessMs: 500,
                stopWaitMs: 200);
            newSvc.StateChanged += s => { lock (newStates) newStates.Add(s); };
            LightsyncLightbarService.Publish(255, 0, 0);
            newSvc.Start();

            // The new worker holds off the engine while the orphan is inside it.
            Thread.Sleep(150);
            Assert.Equal(0, newFake.Count("load"));

            gate.Set();
            Assert.True(WaitFor(() => newFake.Lit.Count >= 1), "the new worker never connected");
            lock (newStates) Assert.Contains(LightsyncServiceState.Connected, newStates);

            // The orphan unloaded without a restore-shutdown, and before the
            // new worker loaded the engine.
            Assert.True(WaitFor(() => oldFake.Count("unload") >= 1), "the orphan never unloaded");
            Assert.Equal(0, oldFake.Count("restore-shutdown"));
            string[] log;
            lock (shared) log = shared.ToArray();
            int oldUnload = Array.IndexOf(log, "old:unload");
            int newLoad = Array.IndexOf(log, "new:load");
            Assert.True(oldUnload >= 0 && newLoad > oldUnload, $"order was {string.Join(",", log)}");

            // The old instance raised nothing after its Stop returned.
            lock (oldStates)
            {
                Assert.Equal(oldStatesAfterStop, oldStates.Count);
                Assert.DoesNotContain(LightsyncServiceState.Stopped, oldStates);
            }

            // The live session tears down normally.
            newSvc.Stop();
            Assert.True(newFake.Count("restore-shutdown") >= 1, "the live session must restore then shutdown");
            lock (newStates) Assert.Equal(LightsyncServiceState.Stopped, newStates[^1]);
        }

        /// <summary>The feed and sibling source contracts: the publish
        /// call beside Chroma's inside the lightbar validity gate, the
        /// GLOBAL-ONLY persistence (both #373 post-ship lessons applied
        /// from birth), the autosave allowlist entry, the Dashboard
        /// bindings, and the native layer's loader constants (the shim's
        /// registry key, Aurora's engine validation string, the altered
        /// search path, and byte-return cdecl delegates).</summary>
        [Fact]
        public void FeedAndSiblingContracts()
        {
            string hm = RepoText("PadForge.App", "Common", "Input", "HMaestroVirtualController.cs");
            int anchor = hm.IndexOf("Lightsync lightbar mirror (#382)", StringComparison.Ordinal);
            Assert.True(anchor > 0, "the publish-site anchor comment is gone");
            Assert.Contains("LightsyncLightbarService.Publish(lbRgb[0], lbRgb[1], lbRgb[2])", hm);

            string ss = RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("_mainVm.Dashboard.EnableLightsyncLightbar = appSettings.EnableLightsyncLightbar;", ss);
            Assert.Contains("EnableLightsyncLightbar = _mainVm.Dashboard.EnableLightsyncLightbar,", ss);
            // GLOBAL ONLY: a new bool on ProfileData deserializes to false
            // in every pre-existing profile and the auto-switch would
            // stomp the global (#373's shipped defect, pinned absent).
            Assert.DoesNotContain("active.EnableLightsyncLightbar", ss);
            Assert.DoesNotContain("profile.EnableLightsyncLightbar", ss);

            string mw = RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("nameof(DashboardViewModel.EnableLightsyncLightbar)", mw);

            string dp = RepoText("PadForge.App", "Views", "DashboardPage.xaml");
            Assert.Contains("Binding EnableLightsyncLightbar", dp);
            Assert.Contains("Binding LightsyncStatus", dp);

            string nat = RepoText("PadForge.App", "Services", "LogiLedEngineNative.cs");
            Assert.Contains(@"SOFTWARE\Classes\CLSID\{a6519e67-7632-4375-afdf-caa889744403}\ServerBinary", nat);
            Assert.Contains("Logitech Gaming LED SDK", nat);
            Assert.Contains("LoadWithAlteredSearchPath = 0x00000008", nat);
            Assert.Contains("private delegate byte BoolFn();", nat);
            Assert.Contains("CallingConvention.Cdecl", nat);
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
