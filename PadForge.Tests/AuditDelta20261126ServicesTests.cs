using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Privacy;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The services' defects of the audit of fb6718901..eb3294709: the
    /// cursor providers a late dispose took, the engine the problem recorder
    /// read after a stop, and the default a return to it applied before any
    /// start.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditDelta20261126ServicesTests : IDisposable
    {
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
        private readonly string _active = SettingsManager.ActiveProfileId;
        private readonly ProfileData _pending = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly List<int>[] _orders =
        {
            new(SettingsManager.XboxSlotOrder), new(SettingsManager.PlayStationSlotOrder), new(SettingsManager.NintendoSlotOrder),
            new(SettingsManager.ExtendedSlotOrder), new(SettingsManager.KeyboardMouseSlotOrder), new(SettingsManager.MidiSlotOrder),
            new(SettingsManager.VrSlotOrder),
        };

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.Profiles = _profiles;
            SettingsManager.ActiveProfileId = _active;
            SettingsManager.PendingDefaultSnapshot = _pending;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
            SettingsManager.SlotMappingSets = _sets;
            SettingsManager.XboxSlotOrder = _orders[0];
            SettingsManager.PlayStationSlotOrder = _orders[1];
            SettingsManager.NintendoSlotOrder = _orders[2];
            SettingsManager.ExtendedSlotOrder = _orders[3];
            SettingsManager.KeyboardMouseSlotOrder = _orders[4];
            SettingsManager.MidiSlotOrder = _orders[5];
            SettingsManager.VrSlotOrder = _orders[6];
        }

        // ── The cursor providers ──

        /// <summary>A service disposed after a newer one was made takes only
        /// its own providers: Mouse Position keeps reading the newer
        /// one.</summary>
        [Fact]
        public void AnOlderCursorService_DisposedLate_LeavesTheNewerOnesProviders()
        {
            var cursor = SourceCoercion.MouseCursorProvider;
            var point = SourceCoercion.MouseCursorPointProvider;
            CursorControlService older = null, newer = null;
            try
            {
                older = new CursorControlService();
                newer = new CursorControlService();
                var newerCursor = SourceCoercion.MouseCursorProvider;
                var newerPoint = SourceCoercion.MouseCursorPointProvider;
                Assert.NotNull(newerCursor);
                Assert.NotNull(newerPoint);

                older.Dispose();
                Assert.Same(newerCursor, SourceCoercion.MouseCursorProvider);
                Assert.Same(newerPoint, SourceCoercion.MouseCursorPointProvider);
                Assert.Same(newer, CursorControlService.Active);

                newer.Dispose();
                Assert.Null(SourceCoercion.MouseCursorProvider);
                Assert.Null(SourceCoercion.MouseCursorPointProvider);
                Assert.Null(CursorControlService.Active);
            }
            finally
            {
                older?.Dispose();
                newer?.Dispose();
                SourceCoercion.MouseCursorProvider = cursor;
                SourceCoercion.MouseCursorPointProvider = point;
            }
        }

        // ── The recorder after a stop ──

        /// <summary>A stop takes the engine from the problem recorder: a
        /// recording made while it is stopped states no slot's type from the
        /// disposed engine, and its system part says the engine is
        /// stopped.</summary>
        [Fact]
        public void AStop_TakesTheEngineFromTheRecorder_AndTheSystemPartSaysStopped()
        {
            var savedEngine = RecorderHost.Engine;
            try
            {
                Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
                SettingsManager.SlotCreated[0] = true;
                RunSta(() =>
                {
                    var vm = new MainViewModel();
                    using var service = new InputService(vm);
                    var engine = new InputManager();
                    engine.SlotControllerTypes[0] = PadForge.Engine.VirtualControllerType.Xbox;
                    Set(service, "_inputManager", engine);
                    // As Start does.
                    RecorderHost.Engine = engine;
                    var before = new List<string>();
                    new RecorderHost().ControllerLines(new PrivacyScrubber(new PrivacyEnvironment()), before);
                    Assert.Contains(before, l => l.Contains("Xbox", StringComparison.Ordinal));

                    service.Stop();
                    vm.Pads[0].OutputType = PadForge.Engine.VirtualControllerType.PlayStation;
                    Assert.Null(RecorderHost.Engine);
                    var after = new List<string>();
                    new RecorderHost().ControllerLines(new PrivacyScrubber(new PrivacyEnvironment()), after);
                    Assert.DoesNotContain(after, l => l.Contains("Xbox", StringComparison.Ordinal));
                });

                // The line the system part writes for no engine. Read through
                // the line's own helper: the system part's seams belong to
                // the recording tests' collection.
                Assert.Equal("Engine: stopped", RecorderHost.EngineLine(null));
                Assert.Contains("into.Add(EngineLine(Engine));", ExtrasPageTests.RepoText("PadForge.App", "Services", "ProblemRecorderHost.cs"));
            }
            finally
            {
                RecorderHost.Engine = savedEngine;
            }
        }

        // ── The default before any start ──

        /// <summary>With a named profile in force and the engine never
        /// started, the default the file stored is what deactivate applies
        /// and keeps, and what it would turn on that listens on the network
        /// is named first.</summary>
        [Fact]
        public void ADeactivateBeforeAnyStart_AppliesTheStoredDefault_AndKeepsIt()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int> { 0, 1 };
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            // The named profile's two controllers are live, as LoadProfiles
            // leaves them.
            SettingsManager.SlotCreated[0] = SettingsManager.SlotCreated[1] = true;
            SettingsManager.SlotEnabled[0] = SettingsManager.SlotEnabled[1] = true;
            var created = new bool[InputManager.MaxPads];
            created[0] = true;
            var stored = new ProfileData
            {
                SlotCreated = created,
                SlotEnabled = (bool[])created.Clone(),
                EnableWebController = true,
            };
            var named = new ProfileData
            {
                Id = "p1",
                Name = "Racing",
                SlotCreated = (bool[])SettingsManager.SlotCreated.Clone(),
                SlotEnabled = (bool[])SettingsManager.SlotEnabled.Clone(),
            };
            SettingsManager.Profiles = new List<ProfileData> { named };
            SettingsManager.ActiveProfileId = "p1";
            SettingsManager.PendingDefaultSnapshot = stored;
            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            var input = new InputService(vm) { SettingsService = settings };

            Assert.False(input.ExternalNetworkStarts(null).Nothing);
            Assert.Equal("ok default", input.ExternalDeactivate());
            Assert.Same(stored, SettingsManager.PendingDefaultSnapshot);
            Assert.True(SettingsManager.SlotCreated[0]);
            Assert.False(SettingsManager.SlotCreated[1]);
        }

        // ── The S.T.R.I.K.E. 7 download's path ──

        /// <summary>The download's path is looked up once: each look judges
        /// the protected folder's access lists, and the screens' pass asks for
        /// the path four times a second.</summary>
        [Fact]
        public void TheDownloadedStrike7Library_IsLookedUpOnce()
        {
            const string where = @"C:\PadForge\Work\Strike7\Strike7API.dll";
            int looks = 0;
            var service = new KeyboardScreenService(new SettingsViewModel(), null)
            {
                DownloadedLibraryLookup = () => { looks++; return where; },
            };
            for (int pass = 0; pass < 20; pass++)
            {
                Assert.Equal(where, service.Strike7LibraryPath(out string sha256));
                Assert.Equal(KeyboardScreenService.Strike7LibrarySha256, sha256);
            }
            Assert.Equal(1, looks);
        }

        /// <summary>A lookup that failed is made again on the next pass, so
        /// a folder made later is found.</summary>
        [Fact]
        public void AFailedLookOfTheStrike7Download_IsMadeAgain()
        {
            int looks = 0;
            var service = new KeyboardScreenService(new SettingsViewModel(), null)
            {
                DownloadedLibraryLookup = () => ++looks == 1 ? throw new System.IO.IOException("not yet") : @"C:\W\Strike7API.dll",
            };
            Assert.Throws<System.IO.IOException>(() => service.Strike7LibraryPath(out _));
            Assert.Equal(@"C:\W\Strike7API.dll", service.Strike7LibraryPath(out _));
            Assert.Equal(@"C:\W\Strike7API.dll", service.Strike7LibraryPath(out _));
            Assert.Equal(2, looks);
        }

        // ── A program's device with rumble ──

        /// <summary>A program's device that declares rumble is a rumble
        /// device: its Devices row wears the Rumble chip, and the Pad page's
        /// Force Feedback tab, where the slot's gains reach it, shows for it.
        /// One without rumble is neither.</summary>
        [Fact]
        public void AProgramsDeviceWithRumble_WearsTheChip_AndOpensTheForceFeedbackTab()
        {
            UserDevice Program(bool rumble)
            {
                var device = new PadForge.Engine.ProgramDevice(Guid.NewGuid(), Guid.NewGuid(), "program://fw2rumble", "Script pad",
                    new PadForge.Engine.ProgramDeviceShape { Rumble = rumble });
                return new UserDevice
                {
                    InstanceGuid = device.InstanceGuid,
                    ProductName = "Script pad",
                    DevicePath = "program://fw2rumble",
                    CapType = PadForge.Engine.InputDeviceType.Program,
                    Device = device,
                };
            }
            var withRumble = Program(true);
            var without = Program(false);
            Assert.True(InputService.RelaysRumble(withRumble));
            Assert.False(InputService.RelaysRumble(without));

            var vm = new MainViewModel();
            var service = new InputService(vm);
            var populate = typeof(InputService).GetMethod("PopulateDeviceRow", Fields) ?? throw new MissingMethodException("PopulateDeviceRow");
            var row = new DeviceRowViewModel();
            populate.Invoke(service, new object[] { row, withRumble, new[] { withRumble } });
            Assert.True(row.HasRumble);
            var plain = new DeviceRowViewModel();
            populate.Invoke(service, new object[] { plain, without, new[] { without } });
            Assert.False(plain.HasRumble);

            // The Pad page's tab asks the same.
            string page = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            int gate = page.IndexOf("hasForceFeedback =\r\n", StringComparison.Ordinal);
            if (gate < 0) gate = page.IndexOf("hasForceFeedback =\n", StringComparison.Ordinal);
            Assert.True(gate >= 0, "the Pad page's Force Feedback gate");
            int end = page.IndexOf(';', gate);
            Assert.Contains("PadForge.Services.InputService.RelaysRumble(ud)", page[gate..end]);
        }

        // ── A program's undo ──

        private static (MainViewModel Vm, SettingsService Settings, InputService Input) Service()
        {
            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            var input = new InputService(vm) { SettingsService = settings };
            return (vm, settings, input);
        }

        private static AssistantUndo UndoOf(MainViewModel vm, SettingsService settings, InputService input)
            => new AssistantUndo(() => AssistantUndoStore.Take(input, settings),
                state => AssistantUndoStore.Restore(state, input, settings, vm, null),
                networkStarts: state => AssistantUndoStore.NetworkStarts(state, vm));

        /// <summary>An undo under a named profile puts back that profile's
        /// controllers, not the default's the app settings copy holds: the
        /// slots made, each one's type and the order lists.</summary>
        [Fact]
        public void AnUndoUnderANamedProfile_KeepsThatProfilesControllers()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            var defaultCreated = new bool[InputManager.MaxPads];
            defaultCreated[0] = true;
            var defaultTypes = new int[InputManager.MaxPads];
            SettingsManager.PendingDefaultSnapshot = new ProfileData
            {
                SlotCreated = defaultCreated,
                SlotEnabled = (bool[])defaultCreated.Clone(),
                SlotControllerTypes = defaultTypes,
                XboxSlotOrder = new[] { 0 },
                PlayStationSlotOrder = Array.Empty<int>(),
            };
            SettingsManager.Profiles = new List<ProfileData> { new ProfileData { Id = "p1", Name = "Racing" } };
            SettingsManager.ActiveProfileId = "p1";
            var (vm, settings, input) = Service();
            // Racing's setup is live: a PlayStation controller on slot 1 and
            // an Xbox one on slot 2.
            SettingsManager.SlotCreated[0] = SettingsManager.SlotCreated[1] = true;
            SettingsManager.SlotEnabled[0] = SettingsManager.SlotEnabled[1] = true;
            vm.Pads[0].OutputType = PadForge.Engine.VirtualControllerType.PlayStation;
            vm.Pads[1].OutputType = PadForge.Engine.VirtualControllerType.Xbox;
            SettingsManager.SlotOrders.RebuildFromCurrentTopology(i => vm.Pads[i].OutputType, null, null, null, null, null, null, null);
            Assert.Contains(0, SettingsManager.PlayStationSlotOrder);
            int rate = vm.Settings.PollingRateHz;

            var undo = UndoOf(vm, settings, input);
            undo.BeginChange(7, "set_setting", "Polling Rate");
            vm.Settings.PollingRateHz = rate == 500 ? 250 : 500;
            undo.UndoNewestOf(7);

            Assert.Equal(rate, vm.Settings.PollingRateHz);
            Assert.True(SettingsManager.SlotCreated[1]);
            Assert.Equal(PadForge.Engine.VirtualControllerType.PlayStation, vm.Pads[0].OutputType);
            Assert.Equal(PadForge.Engine.VirtualControllerType.Xbox, vm.Pads[1].OutputType);
            Assert.Contains(0, SettingsManager.PlayStationSlotOrder);
            Assert.DoesNotContain(0, SettingsManager.XboxSlotOrder);
        }

        /// <summary>A program's undo never reaches what is the user's alone:
        /// the Plugins switch and the plugins the user removed since stay as
        /// the user left them. And it turns on nothing that listens on the
        /// network and is off now: the program's own undo is refused and
        /// changes nothing, while the user's own Undo on the Activity card
        /// puts it back.</summary>
        [Fact]
        public void AProgramsUndo_KeepsTheUsersOwn_AndTurnsOnNoListener()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            var (vm, settings, input) = Service();
            vm.Dashboard.EnablePlugins = true;
            vm.Dashboard.LoadPluginSettings(new[] { new PluginData { Path = @"C:\Plugins\Pad.exe" } }, false, null, false, false);
            int rate = vm.Settings.PollingRateHz;

            var undo = UndoOf(vm, settings, input);
            undo.BeginChange(7, "set_setting", "Polling Rate");
            vm.Settings.PollingRateHz = rate == 500 ? 250 : 500;
            // The user, on the Plugins page.
            vm.Dashboard.EnablePlugins = false;
            vm.Dashboard.SetPluginRecords(Array.Empty<PluginData>());
            string back = undo.UndoNewestOf(7);
            Assert.Equal(rate, vm.Settings.PollingRateHz);
            Assert.False(vm.Dashboard.EnablePlugins);
            Assert.Empty(vm.Dashboard.PluginRecords);
            // The answer names every part the undo kept as it is now, and
            // says no more came back than did.
            Assert.Contains("What only the user changes stayed as it is now: Automation, the Plugins page (its switch, the plugins, Test Controller and Replay),"
                + " the Web Controller's access code, Remote Link's identity and trusted PCs, and what a recording holds.", back);

            vm.Dashboard.EnableWebController = true;
            int run = undo.BeginChange(8, "set_setting", "Polling Rate");
            vm.Settings.PollingRateHz = rate == 500 ? 250 : 500;
            vm.Dashboard.EnableWebController = false;
            Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => undo.UndoNewestOf(8)).Code);
            Assert.False(vm.Dashboard.EnableWebController);
            Assert.NotEqual(rate, vm.Settings.PollingRateHz);

            var entry = new AutomationActivityEntry(DateTimeOffset.Now, AutomationProgram.Unknown,
                new AutomationAsk(AutomationAskKind.Tool, "set_setting"), "ok", true) { Spell = run };
            undo.Undo(entry);
            Assert.True(vm.Dashboard.EnableWebController);
            Assert.Equal(rate, vm.Settings.PollingRateHz);
        }

        // ── Helpers ──

        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void Set(InputService service, string field, object value)
            => (typeof(InputService).GetField(field, Fields) ?? throw new MissingFieldException(field)).SetValue(service, value);

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                try { body(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The dispatcher test did not finish.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
