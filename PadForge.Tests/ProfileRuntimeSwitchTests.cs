using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Touchpad;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The runtime profile switch (ForegroundMonitorService alt-tab, manual
    /// switch) runs SaveActiveProfileState + ApplyProfile and never reaches
    /// Save's UpdateActiveProfileSnapshot. Anything those two lanes forget is
    /// therefore lost or stale on every switch, while an app restart looks
    /// fine because the load lane carries it. An audit (2026-07-14) found
    /// per-device lighting configs, the menu-overlay toggle, and macros all
    /// missing from one or both lanes.
    ///
    /// <para>These drive the REAL services (MainViewModel + InputService with
    /// its SettingsService wired, exactly as MainWindow builds them), so they
    /// exercise the same code the alt-tab path does. Every test here was
    /// mutation-proved: reverting its fix turns it red.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ProfileRuntimeSwitchTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly List<ProfileData> _savedProfiles;
        private readonly string _savedActiveProfileId;
        private readonly ProfileData _savedPendingDefault;
        private readonly bool[] _savedCreated;
        private readonly bool[] _savedEnabled;
        private readonly MappingSet[] _savedMappingSets;
        private readonly List<int> _savedXboxOrder;
        private readonly List<int> _savedPsOrder;
        private readonly List<int> _savedExtOrder;
        private readonly List<int> _savedKbmOrder;
        private readonly List<int> _savedMidiOrder;
        private readonly Action _savedAfterRefresh;

        public ProfileRuntimeSwitchTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedProfiles = SettingsManager.Profiles;
            _savedActiveProfileId = SettingsManager.ActiveProfileId;
            _savedPendingDefault = SettingsManager.PendingDefaultSnapshot;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
            _savedEnabled = (bool[])SettingsManager.SlotEnabled.Clone();
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedXboxOrder = SettingsManager.XboxSlotOrder;
            _savedPsOrder = SettingsManager.PlayStationSlotOrder;
            _savedExtOrder = SettingsManager.ExtendedSlotOrder;
            _savedKbmOrder = SettingsManager.KeyboardMouseSlotOrder;
            _savedMidiOrder = SettingsManager.MidiSlotOrder;
            _savedAfterRefresh = SettingsService.AfterMappingSetsRefreshed;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.Profiles = _savedProfiles;
            SettingsManager.ActiveProfileId = _savedActiveProfileId;
            SettingsManager.PendingDefaultSnapshot = _savedPendingDefault;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
            Array.Copy(_savedEnabled, SettingsManager.SlotEnabled, _savedEnabled.Length);
            SettingsManager.SlotMappingSets = _savedMappingSets;
            SettingsManager.XboxSlotOrder = _savedXboxOrder;
            SettingsManager.PlayStationSlotOrder = _savedPsOrder;
            SettingsManager.ExtendedSlotOrder = _savedExtOrder;
            SettingsManager.KeyboardMouseSlotOrder = _savedKbmOrder;
            SettingsManager.MidiSlotOrder = _savedMidiOrder;
            SettingsService.AfterMappingSetsRefreshed = _savedAfterRefresh;
        }

        /// <summary>One created Xbox slot at index 0, no devices, and the real
        /// service pair wired the way MainWindow wires it.</summary>
        private static (MainViewModel vm, InputService svc, SettingsService ss) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            Array.Clear(SettingsManager.SlotEnabled, 0, SettingsManager.SlotEnabled.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.XboxSlotOrder = new List<int> { 0 };
            SettingsManager.PlayStationSlotOrder = new List<int>();
            SettingsManager.ExtendedSlotOrder = new List<int>();
            SettingsManager.KeyboardMouseSlotOrder = new List<int>();
            SettingsManager.MidiSlotOrder = new List<int>();

            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            var svc = new InputService(vm) { SettingsService = ss };
            return (vm, svc, ss);
        }

        /// <summary>Registers a named profile and makes it the active one, so
        /// SaveActiveProfileState takes its named-profile branch.</summary>
        private static ProfileData ArrangeActiveProfile()
        {
            var p = new ProfileData { Id = "p1", Name = "Game A" };
            SettingsManager.Profiles.Add(p);
            SettingsManager.ActiveProfileId = "p1";
            return p;
        }

        /// <summary>A profile whose topology matches Arrange()'s, so
        /// ApplyProfile's SlotCreated gate lets the per-slot configs land.</summary>
        private static ProfileData IncomingProfile()
        {
            var created = new bool[InputManager.MaxPads];
            var enabled = new bool[InputManager.MaxPads];
            created[0] = true;
            enabled[0] = true;
            return new ProfileData
            {
                Id = "p2",
                Name = "Game B",
                SlotCreated = created,
                SlotEnabled = enabled,
                SlotControllerTypes = Enumerable.Repeat((int)VirtualControllerType.Xbox, InputManager.MaxPads).ToArray(),
            };
        }

        // ── NoInherit through the Paste / Copy From lane ──
        // ApplyMultiSourceRowsToCurrentDevice is private, so these drive it
        // through ApplyPadSettingToCurrentDevice, the public entry both
        // clipboard Paste and Copy From use. It has two branches (append a new
        // row / update a matching one) and both had to carry the flag.

        private static readonly Guid PasteGuid = new("66666666-6666-6666-6666-666666666666");

        /// <summary>Arrange() plus one online gamepad selected on slot 0, so
        /// ApplyPadSettingToCurrentDevice has a target device.</summary>
        private static (MainViewModel vm, InputService svc) ArrangeWithSelectedDevice()
        {
            var (vm, svc, _) = Arrange();

            var ud = new UserDevice
            {
                InstanceGuid = PasteGuid,
                ProductName = "Test Gamepad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = true,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);

            var us = new UserSetting { InstanceGuid = PasteGuid, MapTo = 0 };
            us.SetPadSetting(new PadSetting());
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);

            svc.UpdatePadDeviceInfo();
            return (vm, svc);
        }

        private static PadSetting PasteSourceWithBlockRow() => new()
        {
            DeviceScopedMultiSourceRows = new List<MappingRow>
            {
                new()
                {
                    Target = "ButtonA",
                    LayerMask = "Shift1",   // NoInherit is inert on Base
                    NoInherit = true,
                    Sources = { new MappingSource { Descriptor = "Button 0" } },
                },
            },
        };

        [Fact]
        public void Paste_AppendingANewRow_CarriesNoInherit()
        {
            var (_, svc) = ArrangeWithSelectedDevice();

            svc.ApplyPadSettingToCurrentDevice(0, PasteSourceWithBlockRow());

            var row = Assert.Single(SettingsManager.SlotMappingSets[0].Rows,
                r => r.Target == "ButtonA" && r.LayerMask == "Shift1");
            Assert.True(row.NoInherit, "Paste dropped NoInherit when appending the row.");
        }

        [Fact]
        public void Paste_UpdatingAnExistingRow_CarriesNoInherit()
        {
            var (_, svc) = ArrangeWithSelectedDevice();

            // Seed the same (Target, LayerMask) so the paste takes its
            // update-in-place branch rather than the append branch.
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow { Target = "ButtonA", LayerMask = "Shift1", NoInherit = false });
            SettingsManager.SlotMappingSets[0] = ms;

            svc.ApplyPadSettingToCurrentDevice(0, PasteSourceWithBlockRow());

            var row = Assert.Single(SettingsManager.SlotMappingSets[0].Rows,
                r => r.Target == "ButtonA" && r.LayerMask == "Shift1");
            Assert.True(row.NoInherit, "Paste dropped NoInherit when updating the existing row.");
        }

        // ── M4: per-(slot, device) configs must ride the runtime switch ──

        [Fact]
        public void SaveActiveProfileState_CapturesDeviceSlotConfigs()
        {
            var (vm, svc, _) = Arrange();
            var profile = ArrangeActiveProfile();
            vm.Pads[0].DeviceConfig.LightbarRed = 42;

            svc.SaveActiveProfileState();

            Assert.NotNull(profile.DeviceSlotConfigs);
            Assert.Contains(profile.DeviceSlotConfigs,
                c => c.SlotIndex == 0 && c.DeviceGuid == Guid.Empty && c.LightbarRed == 42);
        }

        [Fact]
        public void ApplyProfile_RestoresDeviceSlotConfigs()
        {
            var (vm, svc, _) = Arrange();
            vm.Pads[0].DeviceConfig.LightbarRed = 42;

            var incoming = IncomingProfile();
            incoming.DeviceSlotConfigs = new[]
            {
                new DeviceSlotConfigData
                {
                    SlotIndex = 0,
                    DeviceGuid = Guid.Empty,
                    LightingRev = 1,
                    LightbarRed = 77,
                },
            };

            svc.ApplyProfile(incoming);

            Assert.Equal(77, vm.Pads[0].DeviceConfig.LightbarRed);
        }

        [Fact]
        public void ApplyProfile_MutatesDeviceConfigInPlace_NeverReplacesTheAnchor()
        {
            // The load-bearing half of the fix. UserEffectsDispatcher holds a
            // direct PropertyChanged subscription to this instance, so
            // reassigning DeviceConfig here would leave the dispatcher wired
            // to an orphan and silently kill lighting / adaptive triggers on
            // every profile switch. Reset-then-overlay is correct; the reset
            // must mutate, not replace.
            var (vm, svc, _) = Arrange();
            var anchorBefore = vm.Pads[0].DeviceConfig;

            var incoming = IncomingProfile();
            incoming.DeviceSlotConfigs = new[]
            {
                new DeviceSlotConfigData
                {
                    SlotIndex = 0,
                    DeviceGuid = Guid.Empty,
                    LightingRev = 1,
                    LightbarRed = 77,
                },
            };

            svc.ApplyProfile(incoming);

            Assert.Same(anchorBefore, vm.Pads[0].DeviceConfig);
            Assert.Equal(77, anchorBefore.LightbarRed);
        }

        [Fact]
        public void ApplyProfile_NullDeviceSlotConfigs_LeavesLiveConfigsAlone()
        {
            // Legacy sentinel: a profile saved before these rode profiles must
            // not wipe the user's lighting.
            var (vm, svc, _) = Arrange();
            vm.Pads[0].DeviceConfig.LightbarRed = 42;

            var incoming = IncomingProfile();
            incoming.DeviceSlotConfigs = null;

            svc.ApplyProfile(incoming);

            Assert.Equal(42, vm.Pads[0].DeviceConfig.LightbarRed);
        }

        // ── M5: the menu-overlay toggle must ride the runtime switch ──

        [Fact]
        public void SaveActiveProfileState_CapturesEnableMenuOverlay()
        {
            var (vm, svc, _) = Arrange();
            var profile = ArrangeActiveProfile();
            vm.Dashboard.EnableMenuOverlay = false;

            svc.SaveActiveProfileState();

            Assert.False(profile.EnableMenuOverlay);
        }

        [Fact]
        public void ApplyProfile_RestoresEnableMenuOverlay()
        {
            var (vm, svc, _) = Arrange();
            vm.Dashboard.EnableMenuOverlay = true;

            var incoming = IncomingProfile();
            incoming.EnableMenuOverlay = false;

            svc.ApplyProfile(incoming);

            Assert.False(vm.Dashboard.EnableMenuOverlay);
        }

        // ── Macros must ride the switch-away save ──

        [Fact]
        public void SaveActiveProfileState_CapturesMacros()
        {
            // Without this leg a macro edited while the profile was active was
            // lost the moment the foreground monitor switched away: that path
            // never reaches Save's UpdateActiveProfileSnapshot.
            var (vm, svc, _) = Arrange();
            var profile = ArrangeActiveProfile();
            vm.Pads[0].Macros.Add(new MacroItem { PadIndex = 0, Name = "Rapid Fire" });

            svc.SaveActiveProfileState();

            Assert.NotNull(profile.Macros);
            Assert.Contains(profile.Macros, m => m.Name == "Rapid Fire" && m.PadIndex == 0);
        }

        // ── Audit 2026-07-14 (Codex finder): cold-start gesture catalog ──

        [Fact]
        public void ColdLoad_WithNamedProfileActive_InstallsThatProfilesGestures_NotTheDefaults()
        {
            // ApplyProfile (and with it ApplyProfileTouchpadGestures) never runs
            // on the cold path, so the active-profile branch of LoadProfiles is
            // the ONLY lane that can install a named profile's gestures at
            // startup. Without it the DEFAULT's catalog stayed live under the
            // named profile and the first autosave wrote it back over the
            // profile's stored gestures: silent, permanent user-data loss.
            var (_, _, ss) = Arrange();

            var named = new ProfileData
            {
                Id = "p9",
                Name = "Named",
                TouchpadGestures = new[] { new TouchpadCustomGesture { Name = "ProfileGesture" } },
            };
            SettingsManager.Profiles.Add(named);

            var app = new AppSettingsData
            {
                ActiveProfileId = "p9",
                TouchpadGestures = new[] { new TouchpadCustomGesture { Name = "DefaultGesture" } },
            };

            // Capture what the load path installs, the same way InputService's
            // applier does. Wiring it BEFORE the load also covers the
            // applier-already-present leg of ApplyOrStageTouchpadGestures.
            TouchpadCustomGesture[] installed = null;
            ss.TouchpadGesturesApplier = g => installed = g;

            ss.LoadProfiles(new[] { named }, app);

            Assert.NotNull(installed);
            Assert.Equal("ProfileGesture", Assert.Single(installed).Name);
        }

        [Fact]
        public void ColdLoad_WithNoProfileActive_KeepsTheDefaultGestures()
        {
            // Same-window negative control: with no named profile active the
            // default catalog must survive untouched.
            var (_, _, ss) = Arrange();

            TouchpadCustomGesture[] installed = null;
            ss.TouchpadGesturesApplier = g => installed = g;

            var app = new AppSettingsData
            {
                ActiveProfileId = null,
                TouchpadGestures = new[] { new TouchpadCustomGesture { Name = "DefaultGesture" } },
            };
            ss.LoadProfiles(Array.Empty<ProfileData>(), app);

            Assert.Null(installed);   // the active-profile branch never ran
        }

        // ── Audit 2026-07-14 (Codex finder): autosave mirror + empty-profile
        //    sentinels + copy-builder type gates ──

        [Fact]
        public void UpdateActiveProfileSnapshot_CapturesSlotMappingSets()
        {
            // The autosave mirror. Without this leg the stored profile keeps the
            // mappings it had at activation, and Export (which reads the STORED
            // object, not a fresh snapshot) ships them stale.
            var (_, _, ss) = Arrange();
            var profile = ArrangeActiveProfile();

            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow { Target = "ButtonA", LayerMask = "Base" });
            SettingsManager.SlotMappingSets[0] = ms;

            ss.UpdateActiveProfileSnapshot();

            Assert.NotNull(profile.SlotMappingSets);
            var stored = profile.SlotMappingSets[0];
            Assert.NotNull(stored);
            Assert.Equal("ButtonA", Assert.Single(stored.Rows).Target);
            // Deep-cloned, not aliased: a later live edit must not mutate the
            // stored snapshot.
            Assert.NotSame(ms, stored);
        }

        [Fact]
        public void CreateEmptyProfile_StampsAuthoredEmpty_NotTheLegacyNullSentinel()
        {
            // null Macros / SlotMappingSets mean "legacy, leave live state
            // alone", so an unset empty profile INHERITED the outgoing
            // profile's mappings and macros and then persisted them as its own.
            var (_, svc, _) = Arrange();

            var p = svc.CreateEmptyProfile("Empty", null);

            Assert.NotNull(p.Macros);
            Assert.Empty(p.Macros);
            Assert.NotNull(p.SlotMappingSets);
            Assert.Equal(InputManager.MaxPads, p.SlotMappingSets.Length);
            Assert.All(p.SlotMappingSets, s => Assert.Null(s));
        }

        [Fact]
        public void CopyBuilders_RejectSlotsOfTheWrongOutputType()
        {
            // Every pad owns a dormant ExtendedConfig/MidiConfig regardless of
            // its output type, so an ungated copy exported those defaults from
            // an Xbox slot and clobbered a real Extended/MIDI slot on paste.
            var (vm, _, ss) = Arrange();
            vm.Pads[0].OutputType = VirtualControllerType.Xbox;

            Assert.Null(ss.BuildExtendedConfigSnapshotForSlot(0));
            Assert.Null(ss.BuildMidiConfigSnapshotForSlot(0));
        }

        [Fact]
        public void CopyBuilders_StillEmitForMatchingOutputType()
        {
            // Same-window positive control: the gates above must not break the
            // legitimate Extended->Extended / MIDI->MIDI copy.
            var (vm, _, ss) = Arrange();

            vm.Pads[0].OutputType = VirtualControllerType.Extended;
            Assert.NotNull(ss.BuildExtendedConfigSnapshotForSlot(0));

            vm.Pads[0].OutputType = VirtualControllerType.Midi;
            Assert.NotNull(ss.BuildMidiConfigSnapshotForSlot(0));
        }

        // ── M10 sibling: BuildMacroData's null-vs-empty contract ──

        [Fact]
        public void BuildMacroData_WithNoMacros_ReturnsEmpty_NotTheLegacyNullSentinel()
        {
            // null Macros means "profile predates macros riding profiles,
            // leave the live set alone". A profile the user authored with zero
            // macros must say "zero macros", or switching to it keeps the
            // OUTGOING profile's macros live and firing.
            var (_, _, ss) = Arrange();

            var macros = ss.BuildMacroData();

            Assert.NotNull(macros);
            Assert.Empty(macros);
        }

        [Fact]
        public void BuildMacroData_WithMacros_StillReturnsThem()
        {
            // Same-window positive control for the test above.
            var (vm, _, ss) = Arrange();
            vm.Pads[0].Macros.Add(new MacroItem { PadIndex = 0, Name = "Rapid Fire" });

            var macros = ss.BuildMacroData();

            Assert.Contains(macros, m => m.Name == "Rapid Fire" && m.PadIndex == 0);
        }
    }
}
