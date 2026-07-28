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

        // ── Load-time compaction of a legacy gappy profile (S1) ──
        //
        // Profiles saved before compaction-on-delete can carry non-contiguous
        // slot indices. LoadProfiles heals the STORED ones in place, but the
        // ACTIVE profile's topology is the ROOT topology: LoadProfiles
        // Array.Copy's active.SlotCreated straight into
        // SettingsManager.SlotCreated, while UserSettings.MapTo, the live
        // SlotMappingSets, the pad ViewModels, the macros and the per-slot
        // volumes all hydrate from the same file at the OLD indices.
        //
        // InputService.Start already heals that, via CompactSlotsForGaps, which
        // shifts the whole live state through one map and rebuilds the
        // ViewModels through ApplyProfile. It arms itself off
        // SettingsManager.SlotCreated. Compacting the active profile at load
        // therefore DISARMED it: root came up contiguous, the healer saw no
        // gap, and every other mirror stayed at the old index with the slot
        // created and empty.

        /// <summary>A legacy profile with its only created slot at index 2.</summary>
        private static ProfileData GappyProfile(string id)
        {
            var created = new bool[InputManager.MaxPads];
            var enabled = new bool[InputManager.MaxPads];
            created[2] = true;
            enabled[2] = true;
            var sets = new MappingSet[InputManager.MaxPads];
            sets[2] = new MappingSet();
            sets[2].Rows.Add(new MappingRow { Target = "ButtonA" });
            return new ProfileData
            {
                Id = id,
                Name = "Legacy " + id,
                SlotCreated = created,
                SlotEnabled = enabled,
                SlotMappingSets = sets,
                SlotControllerTypes = Enumerable.Repeat((int)VirtualControllerType.Xbox, InputManager.MaxPads).ToArray(),
            };
        }

        [Fact]
        public void LoadProfiles_ActiveGappyProfile_StaysGappySoTheLiveHealerStaysArmed()
        {
            var (_, _, ss) = Arrange();
            var active = GappyProfile("p1");

            ss.LoadProfiles(new[] { active }, new AppSettingsData { ActiveProfileId = "p1" });

            // The active profile keeps its gap: root SlotCreated is copied from
            // it, and every other root mirror is still at the old index, so a
            // contiguous root here is precisely the inconsistency that orphans
            // the pad. CompactSlotsForGaps arms off this array and moves the
            // whole live state together once the engine starts.
            Assert.True(active.SlotCreated[2]);
            Assert.False(active.SlotCreated[0]);
            Assert.True(SettingsManager.SlotCreated[2]);
            Assert.False(SettingsManager.SlotCreated[0]);

            // The slot's mappings are still reachable at the index its devices
            // and ViewModels are keyed to.
            Assert.NotNull(active.SlotMappingSets[2]);
            Assert.Single(active.SlotMappingSets[2].Rows);
        }

        [Fact]
        public void LoadProfiles_InactiveGappyProfile_IsStillCompactedInPlace()
        {
            var (_, _, ss) = Arrange();
            var stored = GappyProfile("p2");

            // No active profile: the stored snapshot is not the root state, so
            // healing the file in place stays correct and must not regress.
            ss.LoadProfiles(new[] { stored }, new AppSettingsData { ActiveProfileId = null });

            Assert.True(stored.SlotCreated[0]);
            Assert.False(stored.SlotCreated[2]);
            Assert.NotNull(stored.SlotMappingSets[0]);
            Assert.Single(stored.SlotMappingSets[0].Rows);
            Assert.Null(stored.SlotMappingSets[2]);
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

        // ── Audit 2026-07-14 (Codex finder): same-product reconnect remap ──

        [Fact]
        public void ApplyProfile_SameProductFallback_RepointsMappingSourcesAtTheNewInstance()
        {
            // A Bluetooth pad returns with a new InstanceGuid. The assignment
            // falls back to ProductGuid and rebinds the slot, but the mapping
            // sets were cloned from the profile naming the OLD instance, and
            // every runtime consumer matches the guid exactly. Without the
            // remap the profile applies "successfully" and the reconnected pad
            // drives nothing.
            var (_, svc, _) = Arrange();

            var oldGuid = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
            var newGuid = new Guid("bbbbbbbb-0000-0000-0000-000000000002");
            var product = new Guid("cccccccc-0000-0000-0000-000000000003");

            // Only the NEW instance exists now, same product. The device and
            // its UserSetting travel together, as enumeration creates them.
            var ud = new UserDevice
            {
                InstanceGuid = newGuid,
                ProductGuid = product,
                ProductName = "Reconnected Pad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = true,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);

            var us = new UserSetting { InstanceGuid = newGuid, ProductGuid = product, MapTo = -1 };
            us.SetPadSetting(new PadSetting());
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);

            var ps = new PadSetting();
            var incoming = IncomingProfile();
            incoming.PadSettings = new[] { ps };
            incoming.Entries = new[]
            {
                new ProfileEntry
                {
                    InstanceGuid = oldGuid,
                    ProductGuid = product,
                    MapTo = 0,
                    PadSettingChecksum = ps.PadSettingChecksum,
                },
            };

            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                Sources = { new MappingSource
                {
                    Descriptor = "Button 0",
                    DeviceGuid = oldGuid.ToString().ToLowerInvariant(),
                } },
            });
            ms.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "Shift1",
                DeviceGuid = oldGuid.ToString().ToLowerInvariant(),
            });
            incoming.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            incoming.SlotMappingSets[0] = ms;

            svc.ApplyProfile(incoming);

            Assert.Equal(0, us.MapTo);   // the fallback bound the new instance

            // Assert the CONTRACT, not a row count: the legacy automap merge
            // also rebuilds this device's rows (canonicalizing descriptors and
            // dropping sources for devices no longer on the slot), so the
            // durable statement is that the OLD instance survives nowhere and
            // the activator, which the merge never rewrites, names the new one.
            var live = SettingsManager.SlotMappingSets[0];
            string oldStr = oldGuid.ToString().ToLowerInvariant();
            string newStr = newGuid.ToString().ToLowerInvariant();

            Assert.DoesNotContain(live.Rows.SelectMany(r => r.Sources),
                s => string.Equals(s.DeviceGuid, oldStr, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(newStr, Assert.Single(live.ShiftActivators).DeviceGuid);
        }

        [Fact]
        public void ApplyProfile_ExactInstanceMatch_LeavesGuidsAlone()
        {
            // Same-window negative control: when the exact instance is present
            // there is no remap, and an empty ("any device") guid must never be
            // rewritten into a concrete one.
            var (_, svc, _) = Arrange();

            var guid = new Guid("dddddddd-0000-0000-0000-000000000004");
            var ud = new UserDevice
            {
                InstanceGuid = guid,
                ProductGuid = guid,
                ProductName = "Same Pad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = true,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);

            var us = new UserSetting { InstanceGuid = guid, ProductGuid = guid, MapTo = -1 };
            us.SetPadSetting(new PadSetting());
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);

            var ps = new PadSetting();
            var incoming = IncomingProfile();
            incoming.PadSettings = new[] { ps };
            incoming.Entries = new[]
            {
                new ProfileEntry
                {
                    InstanceGuid = guid,
                    ProductGuid = guid,
                    MapTo = 0,
                    PadSettingChecksum = ps.PadSettingChecksum,
                },
            };

            var ms = new MappingSet();
            ms.ShiftActivators.Add(new ShiftActivator { LayerMask = "Shift1", DeviceGuid = "" });
            incoming.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            incoming.SlotMappingSets[0] = ms;

            svc.ApplyProfile(incoming);

            // With the exact instance present no remap is recorded, so the
            // "any device" sentinel must survive untouched. A remap that
            // rewrote empty guids would silently pin every abstract binding to
            // one controller.
            Assert.Equal("", Assert.Single(SettingsManager.SlotMappingSets[0].ShiftActivators).DeviceGuid);
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
    
        private static readonly Guid SteamGuid = new("32462c1a-3538-3ea7-4ed1-692056f86c4b");
        private static readonly Guid XboxPadGuid = new("77777777-7777-7777-7777-777777777777");

        private static void AddPad(Guid guid, string name)
        {
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(new UserDevice
                {
                    InstanceGuid = guid,
                    ProductGuid = guid,
                    InstanceName = name,
                    ProductName = name,
                    IsOnline = true,
                    CapType = InputDeviceType.Gamepad,
                });
        }

        private static void AssignTo(Guid guid, int slot)
        {
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                var us = SettingsManager.UserSettings.Items
                    .FirstOrDefault(u => u.InstanceGuid == guid);
                if (us == null)
                {
                    us = new UserSetting { InstanceGuid = guid };
                    us.SetPadSetting(new PadSetting());
                    SettingsManager.UserSettings.Items.Add(us);
                }
                us.MapTo = slot;
            }
        }

        private static void MapButtonA(int slot, Guid deviceGuid, string descriptor)
        {
            var ms = SettingsManager.SlotMappingSets[slot] ?? (SettingsManager.SlotMappingSets[slot] = new MappingSet());
            ms.Rows.RemoveAll(r => r.Target == "ButtonA");
            var row = new MappingRow { Target = "ButtonA", LayerMask = "Base" };
            row.Sources.Add(new MappingSource
            { Kind = "Direct", Descriptor = descriptor, DeviceGuid = deviceGuid.ToString() });
            ms.Rows.Add(row);
        }

        /// <summary>The owner's reproduction, with no Workshop involvement:
        /// a default profile with one pad mapped, a NEW profile with the same
        /// slot type and a DIFFERENT pad, then back to default. The default
        /// profile's row must come back pointing at its own device.
        ///
        /// <para>Reported symptom: the row subtitle shows the other profile's
        /// controller while the picker still lists this profile's. The
        /// subtitle renders PrimarySourceDeviceGuid, so the two having
        /// diverged means the GUID carried across the switch.</para></summary>
        [Fact]
        public void SwitchingBetweenSameTypeProfiles_KeepsEachProfilesOwnDevice()
        {
            var (vm, svc, ss) = Arrange();
            AddPad(SteamGuid, "Steam Controller");
            AddPad(XboxPadGuid, "Xbox pad");

            // Default profile: Steam Controller assigned and mapped.
            AssignTo(SteamGuid, 0);
            MapButtonA(0, SteamGuid, "Button 0");
            svc.UpdatePadDeviceInfo();
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);
            svc.RefreshAvailableInputsForSlot(vm.Pads[0]);
            Assert.Equal(SteamGuid.ToString(),
                vm.Pads[0].Mappings.First(m => m.TargetSettingName == "ButtonA").PrimarySourceDeviceGuid,
                ignoreCase: true);

            // A NEW profile, same slot type, different pad.
            var other = IncomingProfile();
            var otherSets = new MappingSet[InputManager.MaxPads];
            otherSets[0] = new MappingSet();
            otherSets[0].Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                LayerMask = "Base",
                Sources = { new MappingSource
                    { Kind = "Direct", Descriptor = "Button 0", DeviceGuid = XboxPadGuid.ToString() } },
            });
            other.SlotMappingSets = otherSets;
            SettingsManager.Profiles.Add(other);

            svc.LoadProfile(other.Id);
            AssignTo(XboxPadGuid, 0);
            svc.UpdatePadDeviceInfo();
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);
            svc.RefreshAvailableInputsForSlot(vm.Pads[0]);

            // Back to default.
            svc.RevertToDefaultProfile();
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);
            svc.RefreshAvailableInputsForSlot(vm.Pads[0]);

            var row = vm.Pads[0].Mappings.First(m => m.TargetSettingName == "ButtonA");
            Assert.Equal(SteamGuid.ToString(), row.PrimarySourceDeviceGuid, ignoreCase: true);
        }
    
        /// <summary>Reverting to the default profile must never leave the
        /// OUTGOING profile's mappings live under the default's identity.
        ///
        /// <para>ApplyDefaultProfile is "if (_defaultProfileSnapshot != null)
        /// ApplyProfile(...)". With no snapshot it does nothing at all, so
        /// RevertToDefaultProfile saves the outgoing profile, clears
        /// ActiveProfileId, and leaves that profile's SlotMappingSets in
        /// place. The user is now "on default" looking at the other
        /// profile's rows, and the next save persists them as the default
        /// state.</para>
        ///
        /// <para>Receipt from the owner's PadForge.xml: live slot 0 carried
        /// 21 sources on device a5ca845e, which appears in no UserSetting at
        /// all, beside a single row on the one device that IS assigned. The
        /// single row was the one he had re-picked by hand.</para></summary>
        [Fact]
        public void RevertToDefault_WithNoSnapshot_DoesNotStrandTheOtherProfilesRows()
        {
            var (vm, svc, ss) = Arrange();
            AddPad(SteamGuid, "Steam Controller");
            AddPad(XboxPadGuid, "Xbox pad");
            AssignTo(SteamGuid, 0);
            MapButtonA(0, SteamGuid, "Button 0");

            var other = IncomingProfile();
            var otherSets = new MappingSet[InputManager.MaxPads];
            otherSets[0] = new MappingSet();
            otherSets[0].Rows.Add(new MappingRow
            {
                Target = "ButtonA",
                LayerMask = "Base",
                Sources = { new MappingSource
                    { Kind = "Direct", Descriptor = "Button 0", DeviceGuid = XboxPadGuid.ToString() } },
            });
            other.SlotMappingSets = otherSets;
            SettingsManager.Profiles.Add(other);

            svc.LoadProfile(other.Id);
            Assert.Equal(XboxPadGuid.ToString(),
                SettingsManager.SlotMappingSets[0].Rows.First(r => r.Target == "ButtonA")
                    .Sources.First().DeviceGuid, ignoreCase: true);

            // The state Start() would leave after a restart that never
            // persisted a default snapshot.
            svc.ClearDefaultProfileSnapshotForTest();

            svc.RevertToDefaultProfile();

            var live = SettingsManager.SlotMappingSets[0];
            var guid = live?.Rows?.FirstOrDefault(r => r.Target == "ButtonA")
                ?.Sources?.FirstOrDefault()?.DeviceGuid;
            Assert.False(string.Equals(guid, XboxPadGuid.ToString(), StringComparison.OrdinalIgnoreCase),
                "reverting to default left the other profile's rows live, so the "
                + "default profile now owns that profile's device bindings.");
        }

        /// <summary>The owner's 2026-07-28 reproduction, driven through the
        /// SAME entry points the UI uses: CreateEmptyProfile, LoadProfile,
        /// assign a pad, RevertToDefaultProfile. Present since at least 3.6.1.
        ///
        /// <para>The PROFDIAG trace showed the empty profile coming out of
        /// ApplyProfile still holding the DEFAULT profile's 22 rows on the
        /// default's device, with every assignment already cleared. An
        /// authored-empty profile owns zero mappings, so slot 0 must be empty
        /// the instant it is applied. Whatever it inherits, it later persists
        /// as its own and hands back to the default on revert.</para></summary>
        [Fact]
        public void LoadingAnEmptyProfile_DoesNotInheritTheDefaultProfilesRows()
        {
            var (vm, svc, ss) = Arrange();
            AddPad(SteamGuid, "Steam Controller");
            AssignTo(SteamGuid, 0);
            MapButtonA(0, SteamGuid, "Button 0");
            svc.UpdatePadDeviceInfo();
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);

            // Positive control: the default really does own a row right now.
            Assert.Single(SettingsManager.SlotMappingSets[0].Rows);

            var created = svc.CreateEmptyProfile("Empty", "");
            Assert.NotNull(created.SlotMappingSets);
            Assert.Null(created.SlotMappingSets[0]);

            svc.LoadProfile(created.Id);

            var live = SettingsManager.SlotMappingSets[0];
            int rows = live?.Rows?.Count ?? 0;
            Assert.True(rows == 0,
                $"an authored-empty profile came up owning {rows} inherited row(s); "
                + "first source = "
                + (live?.Rows?.FirstOrDefault()?.Sources?.FirstOrDefault()?.DeviceGuid ?? "<none>"));
        }

        /// <summary>The second half of the same reproduction. After the empty
        /// profile is given its own pad, going back to default must restore
        /// the default's own rows on the default's own device.</summary>
        [Fact]
        public void RevertingFromAnEmptyProfile_RestoresTheDefaultsOwnRows()
        {
            var (vm, svc, ss) = Arrange();
            AddPad(SteamGuid, "Steam Controller");
            AddPad(XboxPadGuid, "Xbox pad");
            AssignTo(SteamGuid, 0);
            MapButtonA(0, SteamGuid, "Button 0");
            svc.UpdatePadDeviceInfo();
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);

            var created = svc.CreateEmptyProfile("Empty", "");
            svc.LoadProfile(created.Id);

            // The user builds the new profile up: its own slot, its own pad.
            // ORDER MATTERS, and getting it wrong makes this test vacuous:
            // UpdatePadDeviceInfo pushes the grid into the domain, so running
            // it AFTER authoring the row overwrites the row with the grid's
            // still-empty one and the ViewModel never carries the new pad.
            // The clobber under test needs the grid to actually hold the new
            // profile's device, which is what the user's does.
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotEnabled[0] = true;
            AssignTo(XboxPadGuid, 0);
            svc.UpdatePadDeviceInfo();
            MapButtonA(0, XboxPadGuid, "Button 0");
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);
            Assert.Equal(XboxPadGuid.ToString(),
                vm.Pads[0].Mappings.First(m => m.TargetSettingName == "ButtonA").PrimarySourceDeviceGuid,
                ignoreCase: true);

            svc.RevertToDefaultProfile();

            var guid = SettingsManager.SlotMappingSets[0]?.Rows
                ?.FirstOrDefault(r => r.Target == "ButtonA")
                ?.Sources?.FirstOrDefault()?.DeviceGuid;
            Assert.True(string.Equals(guid, SteamGuid.ToString(), StringComparison.OrdinalIgnoreCase),
                $"back on the default profile, ButtonA reads device {guid ?? "<none>"}; "
                + $"the default's own device is {SteamGuid}.");
        }
    }
}
