using System;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the Step 5 "infinitely flashing Initializing" wedge reported
    /// against the Nintendo arc (create a second slot while a Nintendo
    /// slot exists; the new slot's mini card flashes Initializing forever).
    ///
    /// Mechanism under test (InputManager.Step5.VirtualDevices.cs):
    ///  - Pass 1 re-arms _slotInitializing every polling cycle for any
    ///    slot that is active (created + enabled + an online mapped
    ///    device) while _virtualControllers[pad] == null (:806-:817).
    ///  - Pass 2 refuses to create while _createFailed[pad] is latched
    ///    (:1008) or while any _pendingConnectTask / _pendingDisposeTask
    ///    is unfinished (:949-:976), and only a completed create clears
    ///    the flag (:1173 / :1228-:1231).
    ///  - The wedge is FIXED: Pass 1 now clears _slotInitializing
    ///    instead of re-arming it while _createFailed is latched, and a
    ///    VC-less deleted/disabled slot clears both latches (the old
    ///    clear sites were all vc != null guarded, so the flag blinked
    ///    forever and slot deletion leaked both flags to the next slot
    ///    created at the same pad index). The two former wedge tests
    ///    below originally asserted the broken behavior (red against
    ///    the fix) and now guard the fixed contract.
    ///
    /// These tests drive the private UpdateVirtualDevices state machine
    /// headless. The HM driver is never touched: every test either keeps
    /// the slot inactive or pre-latches _createFailed, which short-
    /// circuits Pass 2 before EnsureHMaestroContext (:1008 runs before
    /// :1019).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SlotInitializingWedgeTests : IDisposable
    {
        private const int Pad = 1; // the "new slot" pad index in the repro
        private static readonly Guid DevGuid = new("12341234-5678-9abc-def0-111122223333");

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;

        public SlotInitializingWedgeTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
        }

        // ── Reflection seams into the private Step 5 state ──

        private static void RunStep5(InputManager im)
        {
            var mi = typeof(InputManager).GetMethod(
                "UpdateVirtualDevices", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(mi);
            mi.Invoke(im, null);
        }

        private static bool[] CreateFailed(InputManager im) =>
            (bool[])typeof(InputManager)
                .GetField("_createFailed", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(im);

        private static object[] VirtualControllers(InputManager im) =>
            (object[])typeof(InputManager)
                .GetField("_virtualControllers", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(im);

        /// <summary>Seed the statics with one created + enabled slot at
        /// <see cref="Pad"/> and one mapped device (online per the flag).</summary>
        private static void ArrangeSlot(bool deviceOnline,
            VirtualControllerType type = VirtualControllerType.Xbox)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            for (int i = 0; i < SettingsManager.SlotEnabled.Length; i++)
                SettingsManager.SlotEnabled[i] = true;

            SettingsManager.SlotCreated[Pad] = true;

            var ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Wedge Pad",
                IsOnline = deviceOnline,
                InputState = new CustomInputState(),
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(
                    new UserSetting { InstanceGuid = DevGuid, MapTo = Pad });
        }

        private static InputManager MakeManager(VirtualControllerType type)
        {
            var im = new InputManager();
            im.SlotControllerTypes[Pad] = type;
            return im;
        }

        // ── Question 1 pin: a freshly created device-less slot never
        //    arms the flag (IsSlotActive gates every BeginInitializing) ──

        [Fact]
        public void DevicelessCreatedSlot_NeverShowsInitializing()
        {
            ArrangeSlot(deviceOnline: true);
            // Remove the mapping: created + enabled but nothing assigned.
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Clear();

            var im = MakeManager(VirtualControllerType.Xbox);
            for (int i = 0; i < 5; i++) RunStep5(im);

            Assert.False(im.IsVirtualControllerInitializing(Pad));
        }

        // ── Control: mapped but OFFLINE device never arms the flag ──

        [Fact]
        public void OfflineMappedDevice_NeverShowsInitializing()
        {
            ArrangeSlot(deviceOnline: false);
            var im = MakeManager(VirtualControllerType.Xbox);
            for (int i = 0; i < 5; i++) RunStep5(im);

            Assert.False(im.IsVirtualControllerInitializing(Pad));
        }

        // ── REGRESSION GUARD (was the Bug A wedge): one failed create
        //    + active slot must NOT flash Initializing. Pass 1 used to
        //    re-arm the flag every cycle while Pass 2's _createFailed
        //    gate blocked creation for the rest of the session; it now
        //    clears the flag while the latch is set, so the card shows
        //    rest instead of an eternal flashing Initializing. The
        //    createFailed latch itself stays set (creation retry still
        //    requires a user-driven change). ──

        [Fact]
        public void FailedCreateLatch_ActiveSlot_ShowsRestNotInitializing()
        {
            ArrangeSlot(deviceOnline: true);
            var im = MakeManager(VirtualControllerType.Xbox);
            // Simulate the connect task's failure latch
            // (Step5 :1151 / :1163 / :1169): _createFailed[Pad] = true,
            // _slotInitializing already cleared by the task's finally.
            CreateFailed(im)[Pad] = true;

            for (int cycle = 0; cycle < 10; cycle++)
            {
                RunStep5(im);
                Assert.False(im.IsVirtualControllerInitializing(Pad),
                    $"cycle {cycle}: the latched slot must show rest");
                Assert.Null(VirtualControllers(im)[Pad]); // creation stays off
            }

            // The createFailed latch persists until a user-driven change.
            Assert.True(CreateFailed(im)[Pad]);
        }

        // ── REGRESSION GUARD (was the E4 leak): deleting a VC-less
        //    slot now clears BOTH latches (the old clear sites required
        //    vc != null), so the next slot created at the same pad index
        //    is born clean: fresh Initializing arming and a live create
        //    path instead of a wedge inherited from a dead slot. ──

        [Fact]
        public void DeleteWhileLatched_ClearsBothFlags_NextSlotBornClean()
        {
            ArrangeSlot(deviceOnline: true);
            var im = MakeManager(VirtualControllerType.Xbox);
            CreateFailed(im)[Pad] = true;

            // Latched active slot: shows rest (the Bug A fix).
            RunStep5(im);
            Assert.False(im.IsVirtualControllerInitializing(Pad));

            // User deletes the slot. DeviceService.DeleteSlot resets
            // SlotCreated and removes the slot's UserSettings; Pass 1's
            // VC-less cleanup now clears both latches.
            SettingsManager.SlotCreated[Pad] = false;
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Clear();
            RunStep5(im);

            Assert.False(im.IsVirtualControllerInitializing(Pad),
                "deleted slot must drop the Initializing flag");
            Assert.False(CreateFailed(im)[Pad],
                "deleted slot must drop the createFailed latch");

            // A NEW slot at the same (first free) pad index with an
            // online device arms Initializing normally: the create path
            // is live again (delete-then-recreate is the natural retry).
            SettingsManager.SlotCreated[Pad] = true;
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(
                    new UserSetting { InstanceGuid = DevGuid, MapTo = Pad });
            im.SlotControllerTypes[Pad] = VirtualControllerType.PlayStation;

            RunStep5(im);
            Assert.True(im.IsVirtualControllerInitializing(Pad),
                "recreated slot arms Initializing fresh, latch-free");
            Assert.False(CreateFailed(im)[Pad]);
        }

        // ── Nintendo joined the HM device-required create gate in the
        //    arc (:993-:999). Pin that a device-less Nintendo slot also
        //    never arms the flag. The same IsSlotActive gate applies. ──

        [Fact]
        public void DevicelessNintendoSlot_NeverShowsInitializing()
        {
            ArrangeSlot(deviceOnline: true);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Clear();

            var im = MakeManager(VirtualControllerType.Nintendo);
            for (int i = 0; i < 5; i++) RunStep5(im);

            Assert.False(im.IsVirtualControllerInitializing(Pad));
        }
    }
}
