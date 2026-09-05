using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #395: a virtual pad destroyed from the UI thread (the
    /// bubble-down cascades, the inactivity teardown, a same-group
    /// reorder) used to mutate the slot pointer and the dispose task
    /// array beside the polling thread's Step 5. A poll cycle that ran
    /// between those stores saw an empty slot with no dispose pending and
    /// created the replacement while the old pad was still on the bus.
    /// Two pads with one VID/PID attached at once shift DirectInput's
    /// instance GUID ordinal (measured: the survivor moves from GUID(1) to
    /// GUID(0) when the old pad leaves), and a game that saved the
    /// temporary GUID is left pointing at a pad that no longer reports
    /// it. The fix is one lifecycle lock: Step 5 holds it for a whole
    /// cycle, and every other-thread lifecycle entry takes it, so a
    /// cycle sees either none or all of a caller's changes, and the
    /// inactivity teardown re-checks its latch under the same lock the
    /// polling thread clears it under.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class VcLifecycleLockTests : IDisposable
    {
        private const int Pad = 2;
        private static readonly Guid DevGuid = new("5a5a5a5a-1111-2222-3333-444455556666");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;
        private readonly List<int> _savedXbox;
        private readonly List<FakeVc> _fakes = new List<FakeVc>();

        public VcLifecycleLockTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            _savedXbox = SettingsManager.XboxSlotOrder;
        }

        public void Dispose()
        {
            // Release and drain every worker first, so none of them runs a
            // cycle against the next test's settings.
            foreach (var f in _fakes) f.Gate.Set();
            foreach (var t in _workers) SpinWait.SpinUntil(() => t.IsCompleted, TimeSpan.FromSeconds(10));
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.XboxSlotOrder = _savedXbox;
        }

        private readonly List<Task> _workers = new List<Task>();
        private Task Track(Task t) { _workers.Add(t); return t; }

        private FakeVc NewFake() { var f = new FakeVc(); _fakes.Add(f); return f; }

        /// <summary>Disconnect blocks on <see cref="Gate"/> until a test
        /// releases it, so a dispose task stays pending for exactly as long
        /// as the test wants to look at it. Every fake is released again in
        /// the test class's Dispose so no worker is left behind.</summary>
        private sealed class FakeVc : IVirtualController
        {
            public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);
            public int Disconnects;
            public int Disposes;
            public VirtualControllerType Type => VirtualControllerType.Xbox;
            public bool IsConnected => Disposes == 0;
            public int FeedbackPadIndex { get; set; }
            public void Connect() { }
            public void Disconnect() { Gate.Wait(); Interlocked.Increment(ref Disconnects); }
            public void SubmitGamepadState(Gamepad gp) { }
            public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates) { }
            public void Dispose() { Interlocked.Increment(ref Disposes); }
        }

        private static void RunStep5(InputManager im)
        {
            var mi = typeof(InputManager).GetMethod("UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            mi.Invoke(im, null);
        }

        private static T Field<T>(InputManager im, string name)
            => (T)typeof(InputManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(im);

        /// <summary>A created, enabled Xbox slot whose one device is offline,
        /// so the slot is inactive: Pass 2 never reaches the HM driver, and
        /// the offline grace runs for many cycles before it would act.</summary>
        private static InputManager Arrange(FakeVc vc)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++) SettingsManager.SlotEnabled[i] = true;
            SettingsManager.SlotCreated[Pad] = true;
            SettingsManager.XboxSlotOrder = new List<int> { Pad };
            var ud = new UserDevice { InstanceGuid = DevGuid, ProductName = "Cascade Pad", IsOnline = false, InputState = new CustomInputState() };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = DevGuid, MapTo = Pad });

            var im = new InputManager();
            im.SlotControllerTypes[Pad] = VirtualControllerType.Xbox;
            Field<IVirtualController[]>(im, "_virtualControllers")[Pad] = vc;
            return im;
        }

        [Fact]
        public async Task ADestroyFromAnotherThread_LeavesTheSlotEmptyWithItsDisposeOnRecord_Together()
        {
            var vc = NewFake();
            var im = Arrange(vc);

            await Task.Run(() => im.DestroyVirtualControllerAsync(Pad));

            // Both stores are visible once the call returns, and the dispose
            // is still pending because the fake is holding its gate.
            Assert.Null(Field<IVirtualController[]>(im, "_virtualControllers")[Pad]);
            var dispose = Field<Task[]>(im, "_pendingDisposeTask")[Pad];
            Assert.NotNull(dispose);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, vc.Disposes);

            vc.Gate.Set();
            await dispose.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, vc.Disconnects);
            Assert.Equal(1, vc.Disposes);
        }

        [Fact]
        public void ADestroyFromAnotherThread_WaitsWhileStep5HoldsTheLifecycleLock()
        {
            var vc = NewFake();
            var im = Arrange(vc);
            object gate = Field<object>(im, "_vcLifecycleLock");
            var started = new ManualResetEventSlim(false);

            // Held on this thread, the way a Step 5 cycle holds it. No await
            // inside: Monitor ownership is per thread. The worker signals
            // that it is running before it asks for the lock, so a worker
            // that never started cannot pass as one that waited.
            Monitor.Enter(gate);
            Task destroy;
            try
            {
                destroy = Track(Task.Run(() => { started.Set(); im.DestroyVirtualControllerAsync(Pad); }));
                Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
                Thread.Sleep(200);
                Assert.False(destroy.IsCompleted, "the destroy must not run beside a Step 5 cycle");
                Assert.Same(vc, Field<IVirtualController[]>(im, "_virtualControllers")[Pad]);
            }
            finally { Monitor.Exit(gate); }

            Assert.True(SpinWait.SpinUntil(() => destroy.IsCompleted, TimeSpan.FromSeconds(10)));
            Assert.True(destroy.IsCompletedSuccessfully);
            Assert.Null(Field<IVirtualController[]>(im, "_virtualControllers")[Pad]);
            vc.Gate.Set();
        }

        [Fact]
        public void Step5_WaitsWhileAnotherThreadHoldsTheLifecycleLock()
        {
            var im = Arrange(NewFake());
            object gate = Field<object>(im, "_vcLifecycleLock");
            var started = new ManualResetEventSlim(false);

            Monitor.Enter(gate);
            Task cycle;
            try
            {
                cycle = Track(Task.Run(() => { started.Set(); RunStep5(im); }));
                Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
                Thread.Sleep(200);
                Assert.False(cycle.IsCompleted, "a Step 5 cycle must not run beside a lifecycle caller");
            }
            finally { Monitor.Exit(gate); }
            Assert.True(SpinWait.SpinUntil(() => cycle.IsCompleted, TimeSpan.FromSeconds(10)));
            Assert.True(cycle.IsCompletedSuccessfully, cycle.Exception?.ToString() ?? "");
        }

        [Fact]
        public void AStaleInactivityTeardown_IsRefused_AndALiveOneDestroys()
        {
            var vc = NewFake();
            vc.Gate.Set();
            var im = Arrange(vc);

            // The polling thread cleared the latch (a device came back): refused.
            Assert.False(im.TryInactivityTeardown(Pad, VirtualControllerType.Xbox));
            Assert.Same(vc, Field<IVirtualController[]>(im, "_virtualControllers")[Pad]);

            // The latch is set: the teardown runs and the dispose is on record.
            Field<bool[]>(im, "_hmInactivityFired")[Pad] = true;
            Assert.True(im.TryInactivityTeardown(Pad, VirtualControllerType.Xbox));
            Assert.Null(Field<IVirtualController[]>(im, "_virtualControllers")[Pad]);
            Assert.NotNull(Field<Task[]>(im, "_pendingDisposeTask")[Pad]);

            // Out of range and empty slots are refused without effect.
            Assert.False(im.TryInactivityTeardown(-1, VirtualControllerType.Xbox));
            Assert.False(im.TryInactivityTeardown(InputManager.MaxPads, VirtualControllerType.Xbox));
        }

        [Fact]
        public void ARetiringSlot_HoldsCreationBack_UntilPass1HasRetiredIt()
        {
            // The UI writes created, enabled, type and profile outside the
            // lock, so a slot can turn retiring after Pass 1 visited it and
            // before Pass 2 in the same cycle. Pass 2 must see it.
            var vc = NewFake();
            vc.Gate.Set();
            var im = Arrange(vc);
            Assert.False(im.AnySlotRetiring());

            SettingsManager.SlotEnabled[Pad] = false;
            Assert.True(im.AnySlotRetiring());
            SettingsManager.SlotEnabled[Pad] = true;

            SettingsManager.SlotCreated[Pad] = false;
            Assert.True(im.AnySlotRetiring());
            SettingsManager.SlotCreated[Pad] = true;

            im.SlotControllerTypes[Pad] = VirtualControllerType.PlayStation;
            Assert.True(im.AnySlotRetiring());
            im.SlotControllerTypes[Pad] = VirtualControllerType.Xbox;
            Assert.False(im.AnySlotRetiring());

            // A profile switch that moves the slot's device assignment away
            // retires it too (Pass 1's immediate unassignment branch).
            UserSetting mapping;
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                mapping = SettingsManager.UserSettings.Items[0];
                SettingsManager.UserSettings.Items.Clear();
            }
            Assert.True(im.AnySlotRetiring());
            lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(mapping);
            Assert.False(im.AnySlotRetiring());

            // And the next cycle retires it and puts the dispose on record.
            SettingsManager.SlotEnabled[Pad] = false;
            RunStep5(im);
            Assert.Null(Field<IVirtualController[]>(im, "_virtualControllers")[Pad]);
            Assert.False(im.AnySlotRetiring());
        }

        [Fact]
        public async Task ASecondDestroyOnTheSameSlot_KeepsTheFirstDisposeOnRecord()
        {
            // A reorder can refill a slot whose earlier occupant is still
            // disposing. The second destroy must not drop the first from the
            // record the gate reads, or a create can run while the first pad
            // is still on the bus.
            var first = NewFake();
            var im = Arrange(first);
            im.DestroyVirtualControllerAsync(Pad);
            var afterFirst = Field<Task[]>(im, "_pendingDisposeTask")[Pad];
            Assert.NotNull(afterFirst);
            Assert.False(afterFirst.IsCompleted);

            var second = NewFake();
            Field<IVirtualController[]>(im, "_virtualControllers")[Pad] = second;
            im.DestroyVirtualControllerAsync(Pad);
            var chained = Field<Task[]>(im, "_pendingDisposeTask")[Pad];
            Assert.NotNull(chained);

            // Releasing only the second leaves the record pending on the first.
            second.Gate.Set();
            await Task.Delay(200);
            Assert.False(chained.IsCompleted, "the first dispose is still running, the record must say so");
            Assert.Equal(1, second.Disposes);

            first.Gate.Set();
            await chained.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, first.Disposes);
        }

        [Fact]
        public void TheSource_HoldsOneLockAcrossStep5AndEveryOtherThreadEntry_AndPublishesBeforeEmptying()
        {
            string step5 = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs"));

            int update = step5.IndexOf("private void UpdateVirtualDevices()", StringComparison.Ordinal);
            Assert.True(update > 0);
            Assert.Contains("lock (_vcLifecycleLock)", step5.Substring(update, 200));

            int entry = step5.IndexOf("public void DestroyVirtualControllerAsync(int padIndex)", StringComparison.Ordinal);
            Assert.True(entry > 0);
            Assert.Contains("lock (_vcLifecycleLock)", step5.Substring(entry, 260));

            int reroute = step5.IndexOf("public void RerouteVirtualControllersForReorder(", StringComparison.Ordinal);
            Assert.True(reroute > 0);
            Assert.Contains("lock (_vcLifecycleLock)", step5.Substring(reroute, 800));

            int teardown = step5.IndexOf("public bool TryInactivityTeardown(int padIndex, VirtualControllerType slotType)", StringComparison.Ordinal);
            Assert.True(teardown > 0);
            string teardownBody = step5.Substring(teardown, 2200);
            int lockAt = teardownBody.IndexOf("lock (_vcLifecycleLock)", StringComparison.Ordinal);
            int latchAt = teardownBody.IndexOf("if (!InactivityFireStillValid(padIndex)) return false;", StringComparison.Ordinal);
            int destroyAt = teardownBody.IndexOf("DestroyVirtualController(padIndex, asyncDispose: true);", StringComparison.Ordinal);
            int cascadeAt = teardownBody.IndexOf("GetOrderSnapshotFor(slotType)", StringComparison.Ordinal);
            Assert.True(lockAt > 0 && latchAt > lockAt && destroyAt > latchAt && cascadeAt > destroyAt,
                "lock, then the latch re-check, then the destroy, then the cascade, all under the lock");

            int destroy = step5.IndexOf("private void DestroyVirtualController(int padIndex, bool asyncDispose)", StringComparison.Ordinal);
            Assert.True(destroy > 0);
            int publish = step5.IndexOf("var disposeTask = System.Threading.Tasks.Task.Run(", destroy, StringComparison.Ordinal);
            int chain = step5.IndexOf("? System.Threading.Tasks.Task.WhenAll(earlier, disposeTask)", destroy, StringComparison.Ordinal);
            int empty = step5.IndexOf("_virtualControllers[padIndex] = null;", destroy, StringComparison.Ordinal);
            int nextMethod = step5.IndexOf("private void ", publish, StringComparison.Ordinal);
            Assert.True(publish > destroy && chain > publish && empty > chain && empty < nextMethod, "the task is on record, chained onto any earlier one, before the slot reads as empty");

            // Pass 2 waits on retiring slots, and the create worker publishes
            // its controller and its applied state under the lock.
            Assert.Contains("bool anyRetiring = anyNeedsCreate && AnySlotRetiring();", step5);
            Assert.Contains("if (anyNeedsCreate && !anyDisposePending && !anyConnectPending && !anyRetiring)", step5);
            Assert.Contains("if (!IsSlotActive(i) && !HasAnyDeviceMapped(i)) return true;", step5);
            int kick = step5.IndexOf("async create KICK", StringComparison.Ordinal);
            int publishLock = step5.IndexOf("lock (_vcLifecycleLock)", kick, StringComparison.Ordinal);
            int exchange = step5.IndexOf("Interlocked.CompareExchange(", kick, StringComparison.Ordinal);
            int applied = step5.IndexOf("if (prior == null) PublishExtendedApplied(capturedIndex);", kick, StringComparison.Ordinal);
            int closedRead = step5.IndexOf("closed = _lifecycleClosed;", kick, StringComparison.Ordinal);
            Assert.True(kick > 0 && publishLock > kick && closedRead > publishLock && exchange > closedRead && applied > exchange && applied - publishLock < 900,
                "the worker takes the lock, reads the shutdown flag, then publishes the pointer and the applied state inside it");
            // The build configuration is captured at kick time and carried
            // through construction. The factories never reread the live
            // Extended arrays.
            Assert.Contains("var capturedBuild = CaptureExtendedBuild(padIndex);", step5);
            Assert.Contains("CreateVirtualController(capturedIndex, capturedType, capturedProfile, capturedBuild)", step5);
            int factory = step5.IndexOf("private IVirtualController CreateVirtualController(int padIndex, VirtualControllerType controllerType,", StringComparison.Ordinal);
            int hmFactory = step5.IndexOf("private IVirtualController CreateHMaestroController(VirtualControllerType type, string profileId, int padIndex, in ExtendedBuild build)", StringComparison.Ordinal);
            int midiFactory = step5.IndexOf("private IVirtualController CreateMidiController(", StringComparison.Ordinal);
            Assert.True(factory > 0 && hmFactory > factory && midiFactory > hmFactory);
            string factories = step5.Substring(factory, midiFactory - factory);
            foreach (var live in new[] { "SlotExtendedCustomize[padIndex]", "SlotOemOverrideEnabled[padIndex]", "SlotOemOverrideLabel[padIndex]", "SlotCustomLayouts[padIndex]", "SlotExtendedFfbEnabled[padIndex]", "SlotExtendedVendorId[padIndex]", "SlotExtendedProductId[padIndex]", "SlotProfileIds[padIndex]" })
                Assert.DoesNotContain(live, factories);
            // Teardown closes the lifecycle under the lock, and the live OEM
            // pass leaves an in-flight worker's claim alone.
            int destroyAll = step5.IndexOf("private void DestroyAllVirtualControllers()", StringComparison.Ordinal);
            string destroyAllBody = step5.Substring(destroyAll, 400);
            Assert.True(destroyAllBody.IndexOf("lock (_vcLifecycleLock)", StringComparison.Ordinal) > 0
                && destroyAllBody.IndexOf("_lifecycleClosed = true;", StringComparison.Ordinal) > destroyAllBody.IndexOf("lock (_vcLifecycleLock)", StringComparison.Ordinal));
            Assert.Contains("if (connecting != null && !connecting.IsCompleted) continue;", step5);
            // Nothing slow runs under the lock on the polling thread: no
            // HIDMaestro initialization inline, VR on the async chain, the
            // factory building the type its caller decided on.
            Assert.DoesNotContain("if (isMsSlot) EnsureHMaestroContext();", step5);
            Assert.DoesNotContain("KeyboardMouse only. Genuinely cheap", step5);
            Assert.DoesNotContain("CreateVirtualController(padIndex, slotType)", step5);
            Assert.DoesNotContain("CreateVirtualController(padIndex);", step5);

            string svc = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", "InputService.cs"));
            int handler = svc.IndexOf("public void OnSlotInactivityTimedOut(int padIndex)", StringComparison.Ordinal);
            Assert.True(handler > 0);
            string handlerBody = svc.Substring(handler, 1400);
            Assert.Contains("_inputManager.TryInactivityTeardown(padIndex, slotType)", handlerBody);
            Assert.DoesNotContain("InactivityFireStillValid", handlerBody);
            Assert.DoesNotContain("RunBubbleDownCascadeFromPosition(padIndex, slotType)", handlerBody);
            Assert.DoesNotContain("_inputManager.DestroyVirtualController(", svc);
        }

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "PadForge.sln"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir;
        }
    }
}
