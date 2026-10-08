using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Logitech LED SDK worker (#494, the LIGHTSYNC mirror of #382 made
    /// per device type) against a scripted engine: a session only while a
    /// type is claimed, every zone of each claimed type in its own color in
    /// percent, the per-key call for keyboards, the whole-device call for the
    /// LIGHTSYNC row, the saved lighting back for a type that leaves, and
    /// restore plus shutdown when nothing is claimed.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class LedSdkBackendTests : IDisposable
    {
        private static readonly Guid Mouse = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid Keyboard = new("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid Headset = new("00000000-0000-0000-0000-0000000000c3");

        public LedSdkBackendTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        private sealed class FakeNative : ILogiLedNative
        {
            private readonly object _lock = new();
            private readonly List<string> _calls = new();
            public volatile bool Present = true;
            public volatile bool SetOk = true;
            public volatile bool InitOk = true;
            public volatile bool LoadOk = true;
            /// <summary>False for an engine without the zone export.</summary>
            public volatile bool ZoneOk = true;
            public bool ZoneCallsAvailable => ZoneOk;
            /// <summary>False for an engine without the target export.</summary>
            public volatile bool TargetOk = true;
            public bool TargetCallsAvailable => TargetOk;
            /// <summary>The device type whose zone calls answer false, or -1.</summary>
            public volatile int FailingType = -1;
            /// <summary>Holds the first Init until set, a native call that
            /// hangs.</summary>
            public ManualResetEventSlim InitGate;

            private void Log(string s) { lock (_lock) _calls.Add(s); }
            public string[] Calls { get { lock (_lock) return _calls.ToArray(); } }
            public int Count(string call) => Calls.Count(c => c == call);
            public int LastIndexOf(string call) => Array.LastIndexOf(Calls, call);

            public bool SoftwarePresent() { Log("present"); return Present; }
            public bool TryLoad(out string detail) { Log("load"); detail = "fake"; return LoadOk; }
            public bool Init()
            {
                Log("init");
                var gate = Interlocked.Exchange(ref InitGate, null);
                gate?.Wait(5000);
                return InitOk;
            }
            public bool SetTarget(int mask) { Log("target:" + mask); return true; }
            public bool SaveCurrent() { Log("save"); return true; }
            public bool Restore() { Log("restore"); return true; }
            public bool SetLighting(int r, int g, int b) { Log($"set:{r},{g},{b}"); return SetOk; }
            public bool SetLightingForTargetZone(int type, int zone, int r, int g, int b)
            {
                Log($"zone:{type}:{zone}:{r},{g},{b}");
                return SetOk && ZoneOk && type != FailingType;
            }
            public void RestoreAndShutdown() => Log("restore-shutdown");
            public void Unload() => Log("unload");
        }

        private static LedSdkBackend Backend(FakeNative native)
            => new(native, retryMs: 200, pollMs: 10, settleMs: 1, presenceSettleMs: 1,
                livenessMs: 60000, stopWaitMs: 2000, orphanWaitMs: 500);

        private static OutputPath Type(string key) => new(OutputFamily.LedSdkType, key);

        private static void Link(params DeviceLinks[] rows) => PeripheralOutputs.PublishLinks(new LinkTable(rows));

        private static readonly Guid Lightsync = PeripheralOutputRow.IdentityFor(PeripheralRowKind.LogitechLightsync);

        private static DeviceLinks LightsyncRow()
            => new() { Device = Lightsync, CatchAll = true, Lighting = PeripheralLinker.LedSdkTypes.Select(t => Type(t)).ToArray() };

        private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
            => SpinWait.SpinUntil(condition, timeoutMs);

        [Fact]
        public void TheZonesAndTypesFollowTheReferences()
        {
            Assert.Equal((0, 5), LedSdkBackend.TypeOf("keyboard"));
            Assert.Equal((3, 3), LedSdkBackend.TypeOf("mouse"));
            Assert.Equal((4, 1), LedSdkBackend.TypeOf("mousemat"));
            Assert.Equal((8, 4), LedSdkBackend.TypeOf("headset"));
            Assert.Equal((14, 4), LedSdkBackend.TypeOf("speaker"));
            Assert.Equal(-1, LedSdkBackend.TypeOf("monitor").Code);
            Assert.Equal(-1, LedSdkBackend.TypeOf(PeripheralLinker.LedSdkWholeDevices).Code);
            Assert.Equal(new[] { "device", "keyboard", "mouse", "mousemat", "headset", "speaker" }, PeripheralLinker.LedSdkTypes);
            // LOGI_DEVICETYPE_MONOCHROME | LOGI_DEVICETYPE_RGB, the manual's
            // example mask that per-key boards ignore (p.23).
            Assert.Equal(3, LedSdkBackend.LogiDeviceTypeWholeDevices);
            Assert.Equal(0, LedSdkBackend.ToPercent(0));
            Assert.Equal(50, LedSdkBackend.ToPercent(0x80));
            Assert.Equal(100, LedSdkBackend.ToPercent(255));
        }

        [Fact]
        public void NothingClaimed_TheEngineIsNeverLoaded()
        {
            var native = new FakeNative();
            // The worker reports Idle on its first pass, so the state moving
            // off Waiting proves the loop ran before the silence is read.
            PeripheralOutputs.SetBackendState(OutputFamily.LedSdkType, BackendState.Waiting);
            using var backend = Backend(native);
            backend.Start();
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.LedSdkType) == BackendState.Idle),
                "the worker ran");
            Thread.Sleep(100);
            Assert.Empty(native.Calls);
        }

        /// <summary>A claimed mouse type opens a session (load, init, every
        /// type targeted, the user's lighting saved) and paints its three
        /// zones in percent. A keyboard adds the per-key call on its own
        /// target and its five zones. The last release puts the saved
        /// lighting back and shuts the engine down.</summary>
        [Fact]
        public void AClaimOpensASession_PaintsItsTypesZones_AndTheLastReleaseRestores()
        {
            var native = new FakeNative();
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { Type("keyboard") } });
            using var backend = Backend(native);
            backend.Start();

            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 0xFF, 0x80, 0x00);
            Assert.True(WaitFor(() => native.Count("zone:3:2:100,50,0") == 1), "the third mouse zone was painted");
            var calls = native.Calls;
            Assert.Equal(new[] { "present", "load", "init", "target:7", "save" }, calls.Take(5));
            Assert.Contains("zone:3:0:100,50,0", calls);
            Assert.Contains("zone:3:1:100,50,0", calls);
            Assert.DoesNotContain(calls, c => c.StartsWith("set:"));
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.LedSdkType) == BackendState.Connected));
            Assert.Equal(PeripheralLinker.LedSdkTypes, PeripheralOutputs.LedSdkPaintable);

            PeripheralOutputs.SetLighting(Keyboard, slot: 1, player: 2, 0x00, 0x00, 0xFF);
            Assert.True(WaitFor(() => native.Count("zone:0:4:0,0,100") == 1), "the keyboard's zones were painted");
            int perKey = native.LastIndexOf("set:0,0,100");
            Assert.True(perKey > 0, "the per-key call went out");
            Assert.Equal("target:4", native.Calls[perKey - 1]);
            Assert.Equal("target:7", native.Calls[perKey + 1]);

            PeripheralOutputs.ReleaseLighting(Mouse, 0);
            PeripheralOutputs.ReleaseLighting(Keyboard, 1);
            Assert.True(WaitFor(() => native.Count("unload") == 1), "the engine was released");
            Assert.Equal(1, native.Count("restore-shutdown"));
            int shutdown = native.LastIndexOf("restore-shutdown");
            Assert.True(shutdown >= 0 && shutdown < native.LastIndexOf("unload"));
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.LedSdkType) == BackendState.Idle));
            Assert.Null(PeripheralOutputs.LedSdkPaintable);
        }

        /// <summary>The LIGHTSYNC row lights the devices with no zones first,
        /// on the RGB and monochrome targets, and every type goes out again
        /// over it, in Aurora's order. A new whole-device color repaints the
        /// types whose colors did not change, among them a mouse another
        /// controller rules.</summary>
        [Fact]
        public void TheLightsyncRow_LightsWholeDevicesFirst_AndEveryTypeAgainOverIt()
        {
            var native = new FakeNative();
            Link(LightsyncRow(), new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } });
            PeripheralOutputs.SetLighting(Lightsync, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            PeripheralOutputs.SetLighting(Mouse, slot: 1, player: 2, 0x00, 0x00, 0xFF);
            using var backend = Backend(native);
            backend.Start();

            Assert.True(WaitFor(() => native.Count("zone:14:3:100,0,0") == 1), "the last speaker zone was painted");
            var calls = native.Calls;
            int save = Array.IndexOf(calls, "save");
            Assert.Equal(new[] { "target:3", "set:100,0,0", "target:7" }, calls.Skip(save + 1).Take(3));
            Assert.True(Array.IndexOf(calls, "zone:0:0:100,0,0") > save + 3, "the keyboard went out over it");
            Assert.True(Array.IndexOf(calls, "zone:3:0:0,0,100") > save + 3, "the mouse went out over it");
            Assert.DoesNotContain("zone:3:0:100,0,0", calls);

            PeripheralOutputs.SetLighting(Lightsync, slot: 0, player: 1, 0x00, 0xFF, 0x00);
            Assert.True(WaitFor(() => native.Count("zone:3:2:0,0,100") == 2), "the unchanged mouse went out again");
            calls = native.Calls;
            int whole = Array.LastIndexOf(calls, "target:3");
            Assert.Equal("set:0,100,0", calls[whole + 1]);
            Assert.True(Array.LastIndexOf(calls, "zone:3:0:0,0,100") > whole, "after the new whole-device color");
        }

        /// <summary>Without LogiLedSetTargetDevice a LogiLedSetLighting
        /// reaches every device, so an engine that lacks it sends neither the
        /// whole-device call nor the per-key one, the keyboard keeps its zone
        /// calls, and the Lighting tab learns the paths it can paint.</summary>
        [Fact]
        public void AnEngineWithoutTheTargetCall_SendsNoWholeDeviceOrPerKeyCall()
        {
            var native = new FakeNative { TargetOk = false };
            Link(LightsyncRow(), new DeviceLinks { Device = Keyboard, Lighting = new[] { Type("keyboard") } });
            PeripheralOutputs.SetLighting(Lightsync, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            PeripheralOutputs.SetLighting(Keyboard, slot: 1, player: 2, 0x00, 0x00, 0xFF);
            using var backend = Backend(native);
            backend.Start();

            Assert.True(WaitFor(() => native.Count("zone:0:4:0,0,100") == 1 && native.Count("zone:14:3:100,0,0") == 1),
                "the keyboard's and the speaker's zones were painted");
            Assert.DoesNotContain(native.Calls, c => c.StartsWith("set:"));
            Assert.Equal(new[] { "keyboard", "mouse", "mousemat", "headset", "speaker" }, PeripheralOutputs.LedSdkPaintable);
        }

        /// <summary>A type that stops being claimed while another still is
        /// gets the saved lighting back, and the remaining type is painted
        /// again over it.</summary>
        [Fact]
        public void ATypeThatLeaves_GetsTheSavedLightingBack_AndTheRestIsRepainted()
        {
            var native = new FakeNative();
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } },
                 new DeviceLinks { Device = Headset, Lighting = new[] { Type("headset") } });
            using var backend = Backend(native);
            backend.Start();

            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            PeripheralOutputs.SetLighting(Headset, slot: 0, player: 1, 0x00, 0xFF, 0x00);
            Assert.True(WaitFor(() => native.Count("zone:8:3:0,100,0") >= 1 && native.Count("zone:3:2:100,0,0") >= 1));

            PeripheralOutputs.ReleaseLighting(Headset, 0);
            Assert.True(WaitFor(() => native.Count("restore") == 1), "the headset got the saved lighting back");
            int restore = native.LastIndexOf("restore");
            Assert.True(WaitFor(() => native.LastIndexOf("zone:3:2:100,0,0") > restore), "the mouse was painted again");
            Assert.Equal(0, native.Count("unload"));
        }

        [Fact]
        public void WithoutGHub_TheWorkerWaitsWithoutLoadingTheEngine()
        {
            var native = new FakeNative { Present = false };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);

            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.LedSdkType) == BackendState.Waiting));
            Assert.Equal(0, native.Count("load"));

            native.Present = true;
            Assert.True(WaitFor(() => native.Count("zone:3:0:0,1,1") >= 1), "the session opened once G HUB ran");
        }

        /// <summary>A dead G HUB shows only as failing calls: three liveness
        /// rounds in which no call took end the session, and the next one
        /// opens after the retry. The color never changes, so every mouse
        /// zone call before the first unload belongs to a round.</summary>
        [Fact]
        public void ThreeFailingRounds_EndTheSession_AndTheNextOneOpens()
        {
            var native = new FakeNative { SetOk = false };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } });
            using var backend = new LedSdkBackend(native, retryMs: 200, pollMs: 10, settleMs: 1, presenceSettleMs: 1,
                livenessMs: 20, stopWaitMs: 2000, orphanWaitMs: 500);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);

            Assert.True(WaitFor(() => native.Count("unload") >= 1), "the failing session ended");
            var calls = native.Calls;
            int unload = Array.IndexOf(calls, "unload");
            Assert.Equal(3, calls.Take(unload).Count(c => c == "zone:3:0:0,1,1"));
            Assert.Equal(1, calls.Take(unload).Count(c => c == "restore-shutdown"));
            Assert.True(WaitFor(() => native.Count("load") >= 2), "a new session opened after the retry");
        }

        /// <summary>The manual names only a dead session as a reason for a
        /// false answer, so a type that fails while another type takes keeps
        /// the session, and its new colors go out without a retry on every
        /// poll in between.</summary>
        [Fact]
        public void ATypeThatFailsAlone_KeepsTheSession_AndIsNotRetriedEveryPoll()
        {
            var native = new FakeNative { FailingType = 8 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } },
                 new DeviceLinks { Device = Headset, Lighting = new[] { Type("headset") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            PeripheralOutputs.SetLighting(Headset, slot: 0, player: 1, 0x00, 0x00, 0x00);
            Assert.True(WaitFor(() => native.Count("zone:3:2:100,0,0") == 1 && native.Count("zone:8:3:0,0,0") == 1));

            // A failing headset whose color changes faster than three polls.
            for (int i = 1; i <= 20; i++)
            {
                PeripheralOutputs.SetLighting(Headset, slot: 0, player: 1, (byte)i, 0x00, 0x00);
                Thread.Sleep(15);
            }
            Assert.True(WaitFor(() => native.Calls.Any(c => c == "zone:8:3:8,0,0")), "the last headset color went out");
            Assert.Equal(0, native.Count("unload"));
            Assert.Equal(1, native.Count("init"));

            // Unchanged, the failing type waits for the next liveness round.
            int before = native.Calls.Count(c => c.StartsWith("zone:8:"));
            Thread.Sleep(150);
            Assert.Equal(before, native.Calls.Count(c => c.StartsWith("zone:8:")));
        }

        /// <summary>Over liveness rounds too, a type that fails while another
        /// takes never ends the session.</summary>
        [Fact]
        public void ATypeThatFailsAlone_KeepsTheSessionOverManyRounds()
        {
            var native = new FakeNative { FailingType = 8 };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } },
                 new DeviceLinks { Device = Headset, Lighting = new[] { Type("headset") } });
            using var backend = new LedSdkBackend(native, retryMs: 200, pollMs: 10, settleMs: 1, presenceSettleMs: 1,
                livenessMs: 20, stopWaitMs: 2000, orphanWaitMs: 500);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            PeripheralOutputs.SetLighting(Headset, slot: 0, player: 1, 0x00, 0xFF, 0x00);

            Assert.True(WaitFor(() => native.Count("zone:3:0:100,0,0") >= 6), "six rounds ran");
            Assert.Equal(0, native.Count("unload"));
            Assert.Equal(1, native.Count("init"));
        }

        /// <summary>An engine without the zone call lights only per-key
        /// keyboards, so a claimed mouse alone never opens a session, and a
        /// keyboard claimed later does, painting the per-key call.</summary>
        [Fact]
        public void AnEngineWithoutZones_OpensASessionOnlyForAPerKeyKeyboard()
        {
            var native = new FakeNative { ZoneOk = false };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } },
                 new DeviceLinks { Device = Keyboard, Lighting = new[] { Type("keyboard") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 0xFF, 0x00, 0x00);

            Assert.True(WaitFor(() => native.Count("unload") >= 1), "the engine went back unopened");
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.LedSdkType) == BackendState.Waiting));
            Thread.Sleep(100);
            Assert.Equal(0, native.Count("init"));
            Assert.Equal(new[] { PeripheralLinker.LedSdkWholeDevices, "keyboard" }, PeripheralOutputs.LedSdkPaintable);

            PeripheralOutputs.SetLighting(Keyboard, slot: 1, player: 2, 0x00, 0x00, 0xFF);
            Assert.True(WaitFor(() => native.Count("set:0,0,100") >= 1), "the per-key keyboard was painted");
            Assert.Equal(1, native.Count("init"));
            Assert.DoesNotContain(native.Calls, c => c.StartsWith("zone:3:"));
        }

        /// <summary>An engine that will not load while Logitech's software
        /// runs, an x64 engine in the ARM64 build among the causes, can paint
        /// nothing, and the Lighting tab learns that instead of a wait for
        /// software already running. Once the software leaves, the tab waits
        /// for it again.</summary>
        [Fact]
        public void AnEngineThatWillNotLoad_PublishesNothingPaintable_UntilTheSoftwareLeaves()
        {
            var native = new FakeNative { LoadOk = false };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);

            Assert.True(WaitFor(() => PeripheralOutputs.LedSdkPaintable is { Length: 0 }), "nothing paintable");
            Assert.Equal(BackendState.Waiting, PeripheralOutputs.StateOf(OutputFamily.LedSdkType));
            Assert.Equal(0, native.Count("init"));

            native.Present = false;
            Assert.True(WaitFor(() => PeripheralOutputs.LedSdkPaintable == null), "the tab waits for the software again");
        }

        /// <summary>A worker whose Stop timed out and whose backend was then
        /// disposed ends at once on its next wait, instead of spinning on the
        /// disposed wake for the whole retry.</summary>
        [Fact]
        public void AnOrphanOfADisposedBackend_EndsInsteadOfSpinning()
        {
            var gate = new ManualResetEventSlim(false);
            var native = new FakeNative { InitGate = gate, InitOk = false };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } });
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);

            var backend = new LedSdkBackend(native, retryMs: 5000, pollMs: 10, settleMs: 1, presenceSettleMs: 1,
                livenessMs: 60000, stopWaitMs: 100, orphanWaitMs: 500);
            backend.Start();
            Assert.True(WaitFor(() => native.Count("init") == 1), "the worker is inside Init");
            backend.Dispose();

            gate.Set();
            Assert.True(WaitFor(() => native.Count("unload") == 1), "the refused engine was released");
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.LedSdkType) == BackendState.Idle, 1500),
                "the orphan ended instead of waiting out the retry");
        }

        /// <summary>An engine that refuses to initialize is released at once,
        /// with nothing saved, and the worker waits and tries again.</summary>
        [Fact]
        public void ARefusedInit_ReleasesTheEngine_AndRetries()
        {
            var native = new FakeNative { InitOk = false };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } });
            using var backend = Backend(native);
            backend.Start();
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);

            Assert.True(WaitFor(() => native.Count("unload") == 1), "the refused engine was released");
            Assert.Equal(new[] { "present", "load", "init", "unload" }, native.Calls.Take(4));
            Assert.Equal(0, native.Count("save"));
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.LedSdkType) == BackendState.Waiting));

            native.InitOk = true;
            Assert.True(WaitFor(() => native.Count("save") == 1), "the retry opened a session");
            Assert.True(native.Count("init") >= 2);
        }

        /// <summary>A Stop that times out inside a native call leaves its
        /// worker as an orphan. The next worker waits for it to leave the SDK
        /// before it loads the engine, so the two never overlap, and the
        /// orphan, finished before any newer worker loaded the engine, ends
        /// its own session: the user's lighting back and the engine shut
        /// down.</summary>
        [Fact]
        public void AnOrphanedWorker_IsWaitedFor_BeforeTheNextSessionLoads()
        {
            var gate = new ManualResetEventSlim(false);
            var native = new FakeNative { InitGate = gate };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } });
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);

            using var first = new LedSdkBackend(native, retryMs: 200, pollMs: 10, settleMs: 1, presenceSettleMs: 1,
                livenessMs: 60000, stopWaitMs: 100, orphanWaitMs: 3000);
            first.Start();
            Assert.True(WaitFor(() => native.Count("init") == 1), "the first worker is inside Init");
            first.Stop();

            using var second = new LedSdkBackend(native, retryMs: 200, pollMs: 10, settleMs: 1, presenceSettleMs: 1,
                livenessMs: 60000, stopWaitMs: 2000, orphanWaitMs: 3000);
            second.Start();
            Thread.Sleep(200);
            Assert.Equal(1, native.Count("load"));

            gate.Set();
            Assert.True(WaitFor(() => native.Count("save") == 1), "the second session opened");
            var calls = native.Calls;
            int orphanLeft = Array.IndexOf(calls, "unload");
            int secondLoad = Array.LastIndexOf(calls, "load");
            Assert.True(orphanLeft >= 0 && orphanLeft < secondLoad, "the orphan left the SDK before the second load");
            Assert.Equal(1, calls.Take(secondLoad).Count(c => c == "restore-shutdown"));
        }

        /// <summary>An orphan that never comes back holds the next worker only
        /// for the orphan wait. When it does leave, superseded, it neither
        /// restores the lighting the new session painted nor reports its own
        /// end over the new session's state.</summary>
        [Fact]
        public void AnOrphanThatNeverReturns_HoldsTheNextWorkerOnlyForTheWait()
        {
            var gate = new ManualResetEventSlim(false);
            var native = new FakeNative { InitGate = gate };
            Link(new DeviceLinks { Device = Mouse, Lighting = new[] { Type("mouse") } });
            PeripheralOutputs.SetLighting(Mouse, slot: 0, player: 1, 1, 2, 3);

            using var first = new LedSdkBackend(native, retryMs: 200, pollMs: 10, settleMs: 1, presenceSettleMs: 1,
                livenessMs: 60000, stopWaitMs: 100, orphanWaitMs: 3000);
            first.Start();
            Assert.True(WaitFor(() => native.Count("init") == 1), "the first worker is inside Init");
            first.Stop();

            using var second = new LedSdkBackend(native, retryMs: 200, pollMs: 10, settleMs: 1, presenceSettleMs: 1,
                livenessMs: 60000, stopWaitMs: 2000, orphanWaitMs: 300);
            second.Start();
            Assert.True(WaitFor(() => native.Count("save") == 1, 3000), "the second session opened at the wait");
            Assert.True(WaitFor(() => PeripheralOutputs.StateOf(OutputFamily.LedSdkType) == BackendState.Connected));

            gate.Set();
            Assert.True(WaitFor(() => native.Count("unload") == 1), "the orphan left");
            Thread.Sleep(50);
            Assert.Equal(0, native.Count("restore-shutdown"));
            Assert.Equal(BackendState.Connected, PeripheralOutputs.StateOf(OutputFamily.LedSdkType));
        }
    }
}
