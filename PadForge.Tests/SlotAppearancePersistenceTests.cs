using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public class SlotAppearancePersistenceTests : IDisposable
    {
        private static readonly Guid DeviceA = new("bdd28f3f-2bdd-46d7-b7da-a9f61c82ea00");
        private static readonly Guid DeviceB = new("bdd28f3f-2bdd-46d7-b7da-a9f61c82ea01");
        private const string Sonic = "XboxSeries=Sonic,DualSense=Midnight,FutureFamily=FutureId";
        private const string Carbon = "XboxSeries=Carbon";
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
        private readonly string _active = SettingsManager.ActiveProfileId;
        private readonly ProfileData _pendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly List<int>[] _orders = GetOrders();
        private readonly Action _afterRefresh = SettingsService.AfterMappingSetsRefreshed;
        private readonly bool _stale = InputService.VmMappingsStale;
        private readonly bool _reloading = InputService.SuppressMappingEditPush;
        private readonly bool _headTracking = HeadTrackingRuntime.Enabled;

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.Profiles = _profiles;
            SettingsManager.ActiveProfileId = _active;
            SettingsManager.PendingDefaultSnapshot = _pendingDefault;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotEnabled = _enabled;
            SettingsManager.SlotMappingSets = _sets;
            SetOrders(_orders);
            SettingsService.AfterMappingSetsRefreshed = _afterRefresh;
            InputService.VmMappingsStale = _stale;
            InputService.SuppressMappingEditPush = _reloading;
            HeadTrackingRuntime.Enabled = _headTracking;
        }

        private static List<int>[] GetOrders() => new[]
        {
            SettingsManager.XboxSlotOrder, SettingsManager.PlayStationSlotOrder,
            SettingsManager.NintendoSlotOrder, SettingsManager.ExtendedSlotOrder,
            SettingsManager.KeyboardMouseSlotOrder, SettingsManager.MidiSlotOrder,
            SettingsManager.VrSlotOrder,
        };

        private static void SetOrders(List<int>[] orders)
        {
            SettingsManager.XboxSlotOrder = orders[0];
            SettingsManager.PlayStationSlotOrder = orders[1];
            SettingsManager.NintendoSlotOrder = orders[2];
            SettingsManager.ExtendedSlotOrder = orders[3];
            SettingsManager.KeyboardMouseSlotOrder = orders[4];
            SettingsManager.MidiSlotOrder = orders[5];
            SettingsManager.VrSlotOrder = orders[6];
        }

        private static (MainViewModel Vm, InputService Input, SettingsService Settings) Arrange(bool createSlot = true)
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
            if (createSlot) SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SetOrders(Enumerable.Range(0, 7).Select(_ => new List<int>()).ToArray());
            if (createSlot) SettingsManager.XboxSlotOrder.Add(0);
            InputService.VmMappingsStale = false;
            InputService.SuppressMappingEditPush = false;
            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            var input = new InputService(vm) { SettingsService = settings };
            return (vm, input, settings);
        }

        private static PadSetting AddDevice(Guid guid, int slot, string appearance, string gain = "50")
        {
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = guid, ProductGuid = guid, ProductName = "Appearance test device",
                InstanceName = "Appearance test device", IsOnline = true,
                CapType = InputDeviceType.Gamepad, CapAxeCount = 6, CapButtonCount = 11, CapPovCount = 1,
            });
            var ps = new PadSetting { Model3DAppearances = appearance, ForceOverall = gain };
            var row = new UserSetting { InstanceGuid = guid, ProductGuid = guid, MapTo = slot };
            row.SetPadSetting(ps);
            SettingsManager.UserSettings.Items.Add(row);
            return ps;
        }

        private static ProfileData LegacyProfile(params (int Slot, Guid Device, string Value)[] rows)
        {
            var profile = new ProfileData
            {
                SlotCreated = new bool[InputManager.MaxPads],
                Entries = new ProfileEntry[rows.Length],
                PadSettings = new PadSetting[rows.Length],
            };
            for (int i = 0; i < rows.Length; i++)
            {
                var ps = new PadSetting { Model3DAppearances = rows[i].Value };
                ps.UpdateChecksum();
                profile.PadSettings[i] = ps;
                profile.Entries[i] = new ProfileEntry
                {
                    MapTo = rows[i].Slot, InstanceGuid = rows[i].Device, PadSettingChecksum = ps.PadSettingChecksum,
                };
                if (rows[i].Slot >= 0) profile.SlotCreated[rows[i].Slot] = true;
            }
            return profile;
        }

        private static void WithFile(Action<string> action)
        {
            string path = Path.Combine(Path.GetTempPath(), "padforge-appearance-" + Guid.NewGuid().ToString("N"));
            try { action(path); }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            }
        }

        [Fact]
        public void InputSelectionChangesTuningWithoutChangingSlotAppearanceOrLegacyRows()
        {
            var (vm, input, settings) = Arrange();
            var a = AddDevice(DeviceA, 0, Carbon, "61");
            var b = AddDevice(DeviceB, 0, "XboxSeries=Other", "23");
            input.RefreshDeviceList();
            var pad = vm.Pads[0];
            pad.SelectedMappedDevice = pad.MappedDevices.Single(d => d.InstanceGuid == DeviceA);
            pad.Model3DAppearances = Sonic;
            Assert.Equal(61, pad.ForceOverallGain);

            settings.UpdatePadSettingsFromViewModels();
            pad.SelectedMappedDevice = pad.MappedDevices.Single(d => d.InstanceGuid == DeviceB);

            Assert.Equal(23, pad.ForceOverallGain);
            Assert.Equal(Sonic, pad.Model3DAppearances);
            Assert.Equal("Sonic", pad.GetModelAppearance("XboxSeries"));
            Assert.Equal("Midnight", pad.GetModelAppearance("DualSense"));
            Assert.Equal(Carbon, a.Model3DAppearances);
            Assert.Equal("XboxSeries=Other", b.Model3DAppearances);
            pad.SelectedMappedDevice = null;
            Assert.Equal(Sonic, SlotAppearancePersistence.Capture(vm.Pads)[0]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LegacyMigrationPrefersNonemptySelectedRowThenSavedOrder(bool profileLane)
        {
            var (vm, input, _) = Arrange();
            AddDevice(DeviceA, 0, Sonic);
            var b = AddDevice(DeviceB, 0, Carbon);
            input.RefreshDeviceList();
            vm.Pads[0].SelectedMappedDevice = vm.Pads[0].MappedDevices.Single(d => d.InstanceGuid == DeviceB);
            var app = new AppSettingsData();
            var profile = LegacyProfile((0, DeviceA, Sonic), (0, DeviceB, Carbon));
            string[] Resolve() => profileLane
                ? SlotAppearancePersistence.ResolveProfile(profile, vm.Pads)
                : SlotAppearancePersistence.ResolveApp(app, vm.Pads);
            Assert.Equal(Carbon, Resolve()[0]);

            vm.Pads[0].SelectedMappedDevice = vm.Pads[0].MappedDevices.Single(d => d.InstanceGuid == DeviceA);
            Assert.Equal(Carbon, Resolve()[0]);
            vm.Pads[0].SelectedMappedDevice = vm.Pads[0].MappedDevices.Single(d => d.InstanceGuid == DeviceB);
            b.Model3DAppearances = " ";
            app = new AppSettingsData();
            profile = LegacyProfile((0, DeviceA, Sonic), (0, DeviceB, " "));
            Assert.Equal(Sonic, Resolve()[0]);

            vm.Pads[0].SelectedMappedDevice = null;
            app = new AppSettingsData();
            profile = LegacyProfile((0, DeviceB, Carbon), (0, DeviceA, Sonic));
            Assert.Equal(profileLane ? Carbon : Sonic, Resolve()[0]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PresentEmptyOrShortArrayNeverRevivesLegacyAppearance(bool profileLane)
        {
            var (vm, _, _) = Arrange();
            AddDevice(DeviceA, 0, Sonic);
            var profile = LegacyProfile((0, DeviceA, Sonic));
            foreach (var authoritative in new[] { Array.Empty<string>(), new string[] { null }, new[] { "" } })
            {
                profile.SlotModel3DAppearances = authoritative;
                var app = new AppSettingsData { SlotModel3DAppearances = authoritative };
                string[] values = profileLane ? SlotAppearancePersistence.ResolveProfile(profile)
                    : SlotAppearancePersistence.ResolveApp(app, vm.Pads);
                Assert.Equal(InputManager.MaxPads, values.Length);
                Assert.All(values, value => Assert.Equal("", value));
            }
            profile.SlotModel3DAppearances = null;
            Assert.Equal(Sonic, SlotAppearancePersistence.ResolveProfile(profile)[0]);
        }

        [Theory]
        [InlineData("ABCDEF12", Sonic, "61")]
        [InlineData("abcdef12", Carbon, "23")]
        public void ProfileMigrationSelectsTheSameExactChecksumTemplateAsProfileApply(
            string checksum, string appearance, string gain)
        {
            var (vm, input, _) = Arrange();
            AddDevice(DeviceA, 0, "XboxSeries=Outgoing", "99");
            input.RefreshDeviceList();
            var profile = LegacyProfile((0, DeviceA, ""));
            // These stored template keys differ only by case.
            profile.PadSettings = new[]
            {
                new PadSetting { PadSettingChecksum = "ABCDEF12", Model3DAppearances = Sonic, ForceOverall = "61" },
                new PadSetting { PadSettingChecksum = "abcdef12", Model3DAppearances = Carbon, ForceOverall = "23" },
            };
            profile.Entries[0].PadSettingChecksum = checksum;
            input.ApplyProfile(profile);

            var linked = SettingsManager.FindSettingByInstanceGuidAndSlot(DeviceA, 0).GetPadSetting();
            Assert.Equal(appearance, linked.Model3DAppearances);
            Assert.Equal(gain, linked.ForceOverall);
            Assert.Equal(appearance, vm.Pads[0].Model3DAppearances);
            Assert.Equal(appearance, profile.SlotModel3DAppearances[0]);
        }

        [Fact]
        public void ProfileMigrationDoesNotReadATemplateRejectedByExactChecksumMatching()
        {
            var (vm, input, _) = Arrange();
            AddDevice(DeviceA, 0, "XboxSeries=Outgoing");
            input.RefreshDeviceList();
            var profile = LegacyProfile((0, DeviceA, Sonic));
            profile.PadSettings[0].PadSettingChecksum = "ABCDEF12";
            profile.Entries[0].PadSettingChecksum = "abcdef12";
            input.ApplyProfile(profile);
            Assert.Empty(SettingsManager.GetAssignedSlots(DeviceA));
            Assert.Equal("", vm.Pads[0].Model3DAppearances);
            Assert.Equal("", profile.SlotModel3DAppearances[0]);

            var matching = LegacyProfile((0, DeviceA, Sonic));
            matching.PadSettings[0].PadSettingChecksum = "ABCDEF12";
            matching.Entries[0].PadSettingChecksum = "ABCDEF12";
            input.ApplyProfile(matching);
            Assert.Equal(new[] { 0 }, SettingsManager.GetAssignedSlots(DeviceA));
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
        }

        [Fact]
        public void LegacyDefaultMigrationUsesItsSnapshotInsteadOfActiveNamedDeviceRows()
        {
            var (vm, _, _) = Arrange();
            AddDevice(DeviceA, 0, Carbon);
            var app = new AppSettingsData
            {
                ActiveProfileId = "named",
                DefaultProfileSnapshot = LegacyProfile((0, DeviceB, Sonic)),
            };
            Assert.Equal(Sonic, SlotAppearancePersistence.ResolveApp(app, vm.Pads)[0]);
            Assert.Equal(Sonic, app.DefaultProfileSnapshot.SlotModel3DAppearances[0]);
            Assert.Equal(Carbon, SlotAppearancePersistence.ResolveApp(new AppSettingsData(), vm.Pads)[0]);
        }

        [Theory]
        [InlineData("")]
        [InlineData(Sonic)]
        public void AppSaveAndLoadKeepsAppearanceWithoutAnyAssignedDevice(string appearance)
        {
            var (vm, _, settings) = Arrange();
            vm.Pads[0].Model3DAppearances = appearance;
            Assert.Empty(SettingsManager.UserSettings.Items);
            WithFile(path =>
            {
                settings.SaveToFile(path);
                Assert.Contains("SlotModel3DAppearances", File.ReadAllText(path));
                var (reloaded, _, reader) = Arrange();
                reloaded.Pads[0].Model3DAppearances = Carbon;
                reader.LoadFromFile(path);
                Assert.True(SettingsManager.SlotCreated[0]);
                Assert.Empty(SettingsManager.UserSettings.Items);
                Assert.Equal(appearance, reloaded.Pads[0].Model3DAppearances);
            });
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void SettingsFileLoadMigratesOnlyWhenTheSlotArrayIsAbsent(bool legacyFile, bool omitAppSettings)
        {
            var (vm, input, settings) = Arrange();
            AddDevice(DeviceA, 0, Sonic);
            AddDevice(DeviceB, 0, "");
            input.RefreshDeviceList();
            vm.Pads[0].Model3DAppearances = Carbon;
            WithFile(path =>
            {
                settings.SaveToFile(path);
                if (legacyFile)
                {
                    var xml = new XmlDocument();
                    xml.Load(path);
                    var node = xml.GetElementsByTagName("SlotModel3DAppearances").Cast<XmlNode>().Single();
                    if (omitAppSettings) node = node.ParentNode;
                    node.ParentNode.RemoveChild(node);
                    xml.Save(path);
                }
                var (reloaded, input2, reader) = Arrange(createSlot: false);
                Assert.All(SettingsManager.SlotCreated, created => Assert.False(created));
                reader.LoadFromFile(path);
                Assert.True(SettingsManager.SlotCreated[0]);
                Assert.True(SettingsManager.SlotEnabled[0]);
                Assert.All(SettingsManager.SlotCreated.Skip(1), created => Assert.False(created));
                Assert.Equal(legacyFile ? Sonic : Carbon, reloaded.Pads[0].Model3DAppearances);
                input2.RefreshDeviceList();
                reloaded.Pads[0].SelectedMappedDevice = reloaded.Pads[0].MappedDevices
                    .Single(d => d.InstanceGuid == DeviceB);
                Assert.Equal(legacyFile ? Sonic : Carbon, reloaded.Pads[0].Model3DAppearances);

                // The migrated value must survive its first authoritative slot-array save.
                reader.SaveToFile(path);
                using (var stream = File.OpenRead(path))
                {
                    var saved = (SettingsFileData)new XmlSerializer(typeof(SettingsFileData)).Deserialize(stream);
                    Assert.NotNull(saved.AppSettings.SlotModel3DAppearances);
                    Assert.Equal(legacyFile ? Sonic : Carbon, saved.AppSettings.SlotModel3DAppearances[0]);
                }
                var (roundTrip, _, nextReader) = Arrange(createSlot: false);
                nextReader.LoadFromFile(path);
                Assert.True(SettingsManager.SlotCreated[0]);
                Assert.Equal(legacyFile ? Sonic : Carbon, roundTrip.Pads[0].Model3DAppearances);
            });
        }

        [Fact]
        public void LastDeviceUnassignmentKeepsTheSlotAppearanceThroughTheRealGridRefresh()
        {
            var (vm, input, settings) = Arrange();
            AddDevice(DeviceA, 0, Carbon);
            input.RefreshDeviceList();
            var devices = new DeviceService(vm, settings);
            devices.DeviceAssignmentChanged += (_, _) => input.RefreshAfterDeviceAssignmentChange();
            vm.Pads[0].Model3DAppearances = Sonic;
            devices.UnassignDevice(DeviceA);
            Assert.True(SettingsManager.SlotCreated[0]);
            Assert.Empty(SettingsManager.GetAssignedSlots(DeviceA));
            Assert.Empty(vm.Pads[0].MappedDevices);
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
            Assert.Equal(Sonic, input.SnapshotCurrentProfile().SlotModel3DAppearances[0]);
        }

        [Fact]
        public void ProfileAppearanceNotificationsRunInsideTheExistingStaleWindow()
        {
            var (vm, input, settings) = Arrange();
            AddDevice(DeviceA, 0, Carbon, "75");
            input.RefreshDeviceList();
            var pad = vm.Pads[0];
            pad.Model3DAppearances = Carbon;
            InputService.RefreshMappingsToViewModel(pad);
            var incoming = input.SnapshotCurrentProfile();
            incoming.SlotModel3DAppearances[0] = Sonic;
            Assert.Single(incoming.PadSettings).ForceOverall = "23";
            var observed = new List<bool>();
            pad.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(PadViewModel.Model3DAppearances)) return;
                observed.Add(InputService.VmMappingsStale);
                settings.MarkDirty();
                // A reentrant consumer must not publish the outgoing grid or tuning.
                settings.FlushPendingDeviceEdits();
            };

            input.ApplyProfile(incoming);
            Assert.Equal(new[] { true }, observed);
            Assert.Equal(Sonic, pad.Model3DAppearances);
            Assert.Equal("23", SettingsManager.FindSettingByInstanceGuidAndSlot(DeviceA, 0).GetPadSetting().ForceOverall);
            Assert.Equal(23, pad.ForceOverallGain);
            Assert.False(InputService.VmMappingsStale);

            pad.ForceOverallGain = 42;
            pad.Model3DAppearances = Carbon;
            Assert.Equal(new[] { true, false }, observed);
            Assert.Equal("42", SettingsManager.FindSettingByInstanceGuidAndSlot(DeviceA, 0).GetPadSetting().ForceOverall);
        }

        [Fact]
        public void SnapshotlessDefaultAppearanceClearRunsInsideItsStaleWindow()
        {
            var (vm, input, settings) = Arrange();
            AddDevice(DeviceA, 0, Carbon, "17");
            input.RefreshDeviceList();
            var pad = vm.Pads[0];
            pad.Model3DAppearances = Sonic;
            Assert.Null(SettingsManager.PendingDefaultSnapshot);
            var observed = new List<bool>();
            pad.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(PadViewModel.Model3DAppearances)) return;
                observed.Add(InputService.VmMappingsStale);
                settings.MarkDirty();
                settings.FlushPendingDeviceEdits();
            };

            input.ApplyDefaultProfile();
            Assert.Equal(new[] { true }, observed);
            Assert.Equal("", pad.Model3DAppearances);
            Assert.Equal("", SettingsManager.PendingDefaultSnapshot.SlotModel3DAppearances[0]);
            Assert.False(InputService.VmMappingsStale);

            pad.ForceOverallGain = 42;
            pad.Model3DAppearances = Carbon;
            Assert.Equal(new[] { true, false }, observed);
            Assert.Equal("42", SettingsManager.FindSettingByInstanceGuidAndSlot(DeviceA, 0).GetPadSetting().ForceOverall);
        }

        [Fact]
        public void NamedAndDefaultProfileSnapshotsRemainSeparateThroughSaveAndLoad()
        {
            var (vm, input, settings) = Arrange();
            vm.Pads[0].Model3DAppearances = Sonic;
            input.SaveActiveProfileState();
            var defaultSnapshot = SettingsManager.PendingDefaultSnapshot;
            var named = input.SnapshotCurrentProfile();
            named.Id = "appearance-named";
            named.Name = "Appearance named";
            named.SlotModel3DAppearances[0] = Carbon;
            SettingsManager.Profiles.Add(named);
            SettingsManager.ActiveProfileId = named.Id;
            input.ApplyProfile(named);
            Assert.Equal(Carbon, vm.Pads[0].Model3DAppearances);
            Assert.Equal(Sonic, defaultSnapshot.SlotModel3DAppearances[0]);
            vm.Pads[0].Model3DAppearances = "XboxSeries=PulseRed";
            settings.UpdateActiveProfileSnapshot();
            Assert.Equal("XboxSeries=PulseRed", named.SlotModel3DAppearances[0]);
            vm.Pads[0].Model3DAppearances = Carbon;
            input.SaveActiveProfileState();
            Assert.Equal(Carbon, named.SlotModel3DAppearances[0]);
            input.ApplyDefaultProfile();
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
            input.ApplyProfile(named);
            Assert.Equal(Carbon, vm.Pads[0].Model3DAppearances);

            WithFile(path =>
            {
                settings.SaveToFile(path);
                var (reloaded, input2, reader) = Arrange();
                reader.LoadFromFile(path);
                Assert.Equal(Carbon, reloaded.Pads[0].Model3DAppearances);
                var restoredDefault = SettingsManager.PendingDefaultSnapshot;
                Assert.NotNull(restoredDefault);
                Assert.Equal(Sonic, restoredDefault.SlotModel3DAppearances[0]);
                input2.ApplyProfile(restoredDefault);
                Assert.Equal(Sonic, reloaded.Pads[0].Model3DAppearances);
                input2.ApplyProfile(SettingsManager.Profiles.Single(p => p.Id == named.Id));
                Assert.Equal(Carbon, reloaded.Pads[0].Model3DAppearances);
            });

        }

        [Fact]
        public void ApplyingALegacyProfileMigratesBeforeDeviceSelectionCanOverwriteAppearance()
        {
            var (vm, input, _) = Arrange();
            AddDevice(DeviceA, 0, Carbon);
            AddDevice(DeviceB, 0, Carbon);
            input.RefreshDeviceList();
            var profile = LegacyProfile((0, DeviceA, Sonic), (0, DeviceB, ""));
            vm.Pads[0].SelectedMappedDevice = vm.Pads[0].MappedDevices.Single(d => d.InstanceGuid == DeviceB);
            vm.Pads[0].Model3DAppearances = "XboxSeries=Outgoing";
            input.ApplyProfile(profile);
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
            Assert.Equal(Sonic, profile.SlotModel3DAppearances[0]);
            vm.Pads[0].SelectedMappedDevice = vm.Pads[0].MappedDevices.Single(d => d.InstanceGuid == DeviceA);
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
        }

        [Fact]
        public void SavingAnUnappliedLegacyProfileKeepsItsSeedAvailableForLaterMigration()
        {
            var (vm, _, settings) = Arrange();
            vm.Pads[0].Model3DAppearances = Carbon;
            var legacy = LegacyProfile((0, DeviceA, Sonic));
            legacy.Id = "unapplied-legacy";
            legacy.Name = "Unapplied legacy";
            SettingsManager.Profiles.Add(legacy);
            Assert.Null(legacy.SlotModel3DAppearances);
            WithFile(path =>
            {
                settings.SaveToFile(path);
                Assert.Equal(Sonic, legacy.PadSettings[0].Model3DAppearances);
                var (reloaded, input, reader) = Arrange();
                reader.LoadFromFile(path);
                Assert.Equal(Carbon, reloaded.Pads[0].Model3DAppearances);
                var restored = SettingsManager.Profiles.Single(p => p.Id == legacy.Id);
                Assert.Equal(Sonic, restored.SlotModel3DAppearances[0]);
                input.ApplyProfile(restored);
                Assert.Equal(Sonic, reloaded.Pads[0].Model3DAppearances);
            });
        }

        [Fact]
        public void CompactionMovesTheSlotAppearanceAndDoesNotKeepTheRetiredPosition()
        {
            var (vm, input, _) = Arrange();
            SettingsManager.SlotCreated[0] = false;
            SettingsManager.SlotCreated[2] = true;
            vm.Pads[2].Model3DAppearances = Sonic;
            var profile = input.SnapshotCurrentProfile();
            var map = new Dictionary<int, int> { [2] = 0 };
            InputService.CompactProfileDataInPlace(profile, map, InputManager.MaxPads);
            Assert.Equal(Sonic, profile.SlotModel3DAppearances[0]);
            Assert.Equal("", profile.SlotModel3DAppearances[2]);
            input.ApplyProfile(profile);
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
            Assert.Equal("", vm.Pads[2].Model3DAppearances);
            var legacy = LegacyProfile((2, DeviceA, Carbon));
            InputService.CompactProfileDataInPlace(legacy, map, InputManager.MaxPads);
            Assert.Equal(Carbon, legacy.SlotModel3DAppearances[0]);
            Assert.Equal(0, legacy.Entries[0].MapTo);
        }

        [Fact]
        public void SlotCopyAndClipboardTransportUseSlotValuesIncludingAnExplicitClear()
        {
            var (vm, _, settings) = Arrange();
            SettingsManager.SlotCreated[1] = true;
            vm.Pads[0].Model3DAppearances = Sonic;
            vm.Pads[1].Model3DAppearances = Carbon;
            settings.CopySlotConfigsAcrossSlots(0, 1);
            Assert.Equal(Sonic, vm.Pads[1].Model3DAppearances);
            vm.Pads[0].Model3DAppearances = "";
            settings.CopySlotConfigsAcrossSlots(0, 1);
            Assert.Equal("", vm.Pads[1].Model3DAppearances);

            vm.Pads[0].Model3DAppearances = Sonic;
            var legacy = new PadSetting { Model3DAppearances = Carbon, ForceOverall = "61" };
            var copy = SlotAppearancePersistence.CreateClipboardSetting(vm.Pads[0], legacy);
            Assert.NotSame(legacy, copy);
            Assert.Equal(Carbon, legacy.Model3DAppearances);
            var decoded = PadSetting.FromJson(copy.ToJson());
            Assert.Equal("61", decoded.ForceOverall);
            SlotAppearancePersistence.ApplyClipboardSetting(vm.Pads[1], decoded);
            Assert.Equal(Sonic, vm.Pads[1].Model3DAppearances);
            vm.Pads[0].Model3DAppearances = "";
            var noDeviceCopy = SlotAppearancePersistence.CreateClipboardSetting(vm.Pads[0], null);
            SlotAppearancePersistence.ApplyClipboardSetting(vm.Pads[1], PadSetting.FromJson(noDeviceCopy.ToJson()));
            Assert.Equal("", vm.Pads[1].Model3DAppearances);
        }

        [Fact]
        public void AppearanceOnlyCopyDonorsNeedNoDeviceAndExcludeSelfDuplicatesAndDeletedSlots()
        {
            var (vm, _, _) = Arrange();
            for (int i = 0; i < 4; i++)
            {
                SettingsManager.SlotCreated[i] = i != 3;
                vm.Pads[i].Model3DAppearances = Sonic;
            }
            Assert.Empty(SettingsManager.UserSettings.Items);
            Assert.Equal(new[] { 1 }, SlotAppearancePersistence.UnlistedCopySlots(vm.Pads, 0, new[] { 2 }));
            vm.Pads[1].Model3DAppearances = "";
            Assert.Empty(SlotAppearancePersistence.UnlistedCopySlots(vm.Pads, 0, new[] { 2 }));
        }

        [Fact]
        public void CreatedSlotCopyCommandsExecuteWithoutADeviceAndRetainTheSelectedDeviceAllowance()
        {
            var (vm, _, _) = Arrange();
            var pad = vm.Pads[0];
            Assert.Null(pad.SelectedMappedDevice);
            int requested = 0;
            pad.CopySettingsRequested += (_, _) => requested++;
            pad.PasteSettingsRequested += (_, _) => requested++;
            pad.CopyFromRequested += (_, _) => requested++;
            var commands = new[] { pad.CopySettingsCommand, pad.PasteSettingsCommand, pad.CopyFromCommand };
            foreach (var command in commands)
            {
                Assert.True(command.CanExecute(null));
                command.Execute(null);
            }
            Assert.Equal(3, requested);
            SettingsManager.SlotCreated[0] = false;
            pad.RefreshCommands();
            Assert.All(commands, command => Assert.False(command.CanExecute(null)));
            pad.SelectedMappedDevice = new PadViewModel.MappedDeviceInfo { InstanceGuid = DeviceA };
            Assert.All(commands, command => Assert.True(command.CanExecute(null)));
        }

        [Fact]
        public void DeleteAndRecreateClearOnlyTheDeletedSlotsAppearance()
        {
            var (vm, _, settings) = Arrange();
            var devices = new DeviceService(vm, settings);
            int slot = devices.CreateSlot();
            Assert.Equal(1, slot);
            vm.Pads[0].Model3DAppearances = Sonic;
            vm.Pads[slot].Model3DAppearances = Carbon;
            devices.DeleteSlot(slot);
            Assert.Equal("", vm.Pads[slot].Model3DAppearances);
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
            Assert.Equal(slot, devices.CreateSlot());
            Assert.Equal("", vm.Pads[slot].Model3DAppearances);
            vm.Pads[0].ResetAllSettings();
            Assert.Equal("", SlotAppearancePersistence.Capture(vm.Pads)[0]);
        }

        [Fact]
        public void ResetToDefaultsClearsSlotValuesBeforeTheNextSave()
        {
            var (vm, _, settings) = Arrange();
            AddDevice(DeviceA, 0, Carbon);
            vm.Pads[0].Model3DAppearances = Sonic;
            settings.ResetToDefaults();
            Assert.All(vm.Pads, pad => Assert.Equal("", pad.Model3DAppearances));
            WithFile(path =>
            {
                settings.SaveToFile(path);
                Assert.Contains("SlotModel3DAppearances", File.ReadAllText(path));
                var (reloaded, _, reader) = Arrange();
                reloaded.Pads[0].Model3DAppearances = Carbon;
                reader.LoadFromFile(path);
                Assert.All(reloaded.Pads, pad => Assert.Equal("", pad.Model3DAppearances));
            });
        }

        [Fact]
        public void EmptyProfilesAndDefaultRecoveryClearOutgoingAppearanceWhileOutputTypeChangesKeepIt()
        {
            var (vm, input, _) = Arrange();
            vm.Pads[0].Model3DAppearances = Sonic;
            vm.Pads[0].OutputType = VirtualControllerType.PlayStation;
            vm.Pads[0].OutputType = VirtualControllerType.Xbox;
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
            input.ApplyDefaultProfile();
            Assert.Equal("", vm.Pads[0].Model3DAppearances);
            vm.Pads[0].Model3DAppearances = Sonic;
            var empty = input.CreateEmptyProfile("Empty appearance", "");
            Assert.NotNull(empty.SlotModel3DAppearances);
            input.ApplyProfile(empty);
            Assert.Equal("", vm.Pads[0].Model3DAppearances);
        }

        [Theory]
        [InlineData("", Sonic)]
        [InlineData(" ", Sonic)]
        [InlineData(Carbon, Carbon)]
        public void UnmappedCopyFromChangesTuningAndOnlyAppliesANonemptyLegacyAppearance(
            string legacyAppearance, string expectedAppearance)
        {
            var (vm, input, settings) = Arrange();
            var targetSetting = AddDevice(DeviceA, 0, "", "17");
            var sourceSetting = AddDevice(DeviceB, -1, legacyAppearance, "61");
            sourceSetting.ButtonA = "Button 3";
            input.RefreshDeviceList();
            vm.Pads[0].Model3DAppearances = Sonic;
            var entry = new PadForge.Views.CopyFromDialog.DeviceEntry
            {
                InstanceGuid = DeviceB, SourceSlot = -1, PadSetting = sourceSetting,
                OutputType = VirtualControllerType.Xbox,
            };

            MainWindow.ApplyCopyFromSettings(input, settings, vm.Pads[0], entry,
                VirtualControllerType.Xbox, false);
            Assert.Equal("61", targetSetting.ForceOverall);
            Assert.Equal(61, vm.Pads[0].ForceOverallGain);
            Assert.Equal(expectedAppearance, vm.Pads[0].Model3DAppearances);
        }

        [Fact]
        public void ADeviceFreeCopyFromKeepsDeviceTuningAndCopiesActualSlotState()
        {
            var (vm, input, settings) = Arrange();
            SettingsManager.SlotCreated[1] = SettingsManager.SlotEnabled[1] = true;
            var targetSetting = AddDevice(DeviceA, 0, "", "17");
            targetSetting.RotationRange = "1050";
            input.RefreshDeviceList();
            var target = vm.Pads[0];
            var source = vm.Pads[1];
            target.DeviceConfig.HeadphoneVolume = 27;
            source.Model3DAppearances = Sonic;
            SettingsManager.SlotMappingSets[1] = CopySourceMappingSet();
            Assert.Empty(source.MappedDevices);
            Assert.Null(InputService.BuildPerDeviceSettingsSnapshot(1, VirtualControllerType.Xbox, false));
            var payload = SlotAppearancePersistence.CreateClipboardSetting(source, null);
            Assert.Equal("[]", payload.SlotPerDeviceSettingsJson);
            var entry = new PadForge.Views.CopyFromDialog.DeviceEntry
            {
                SourceSlot = 1, InstanceGuid = Guid.Empty, PadSetting = payload,
                OutputType = VirtualControllerType.Xbox,
            };

            // This is the dialog's existing whole-slot mapping step, before its apply callback.
            InputService.ReplaceSlotMappingSet(0, 1);
            MainWindow.ApplyCopyFromSettings(input, settings, target, entry, VirtualControllerType.Xbox, false);
            Assert.Equal("17", targetSetting.ForceOverall);
            Assert.Equal("1050", targetSetting.RotationRange);
            Assert.Equal(17, target.ForceOverallGain);
            Assert.Equal(27, target.DeviceConfig.HeadphoneVolume);
            Assert.Equal(Sonic, target.Model3DAppearances);
            Assert.True(SettingsManager.SlotMappingSets[0].KeepAwakeEnabled);
            Assert.Equal("Button 8", target.Mappings.Single(row => row.TargetSettingName == "ButtonA").SourceDescriptor);

            settings.FlushPendingDeviceEdits();
            Assert.Equal("17", targetSetting.ForceOverall);
            Assert.Equal("1050", targetSetting.RotationRange);
            Assert.Equal(27, target.DeviceConfig.HeadphoneVolume);
            Assert.Equal(Sonic, target.Model3DAppearances);
            Assert.True(SettingsManager.SlotMappingSets[0].KeepAwakeEnabled);
            Assert.Equal("Button 8", SettingsManager.SlotMappingSets[0].Rows
                .Single(row => row.Target == "ButtonA" && row.LayerMask == "Base").Sources[0].Descriptor);

            // An authored fallback config still has data to copy without a selected input.
            source.DeviceConfig.HeadphoneVolume = 64;
            MainWindow.ApplyCopyFromSettings(input, settings, target, entry, VirtualControllerType.Xbox, false);
            Assert.Equal(64, target.DeviceConfig.HeadphoneVolume);
            Assert.Equal("17", targetSetting.ForceOverall);
        }

        [Fact]
        public void ARealCopyFromDonorStillCopiesItsDefaultDeviceConfigAndTuning()
        {
            var (vm, input, settings) = Arrange();
            SettingsManager.SlotCreated[1] = SettingsManager.SlotEnabled[1] = true;
            var targetSetting = AddDevice(DeviceA, 0, "", "17");
            var sourceSetting = AddDevice(DeviceB, 1, "", "61");
            input.RefreshDeviceList();
            vm.Pads[0].DeviceConfig.HeadphoneVolume = 27;
            vm.Pads[1].Model3DAppearances = Sonic;
            Assert.Equal(100, vm.Pads[1].DeviceConfig.HeadphoneVolume);
            var entry = new PadForge.Views.CopyFromDialog.DeviceEntry
            {
                SourceSlot = 1, InstanceGuid = DeviceB, PadSetting = sourceSetting,
                OutputType = VirtualControllerType.Xbox,
            };
            MainWindow.ApplyCopyFromSettings(input, settings, vm.Pads[0], entry, VirtualControllerType.Xbox, false);
            Assert.Equal("61", targetSetting.ForceOverall);
            Assert.Equal(100, vm.Pads[0].DeviceConfig.HeadphoneVolume);
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("[]", false)]
        [InlineData("[ ]", false)]
        [InlineData("null", true)]
        [InlineData("[{}]", true)]
        [InlineData("not-json", true)]
        public void ClipboardDeviceListPresenceControlsTheActualTuningWrite(string deviceList, bool writesTuning)
        {
            var (vm, input, _) = Arrange();
            var targetSetting = AddDevice(DeviceA, 0, "", "17");
            input.RefreshDeviceList();
            SettingsManager.SlotMappingSets[0] = CopySourceMappingSet();
            var source = new PadSetting { ForceOverall = "61", SlotPerDeviceSettingsJson = deviceList };
            var decoded = PadSetting.FromJson(source.ToJson());
            input.ApplyPadSettingToCurrentDeviceTranslated(0, decoded,
                VirtualControllerType.Xbox, false, VirtualControllerType.Xbox, false);
            Assert.Equal(writesTuning ? "61" : "17", targetSetting.ForceOverall);
            Assert.Equal("Button 8", vm.Pads[0].Mappings.Single(row => row.TargetSettingName == "ButtonA").SourceDescriptor);
        }

        [Fact]
        public void DeviceFreeClipboardRoundTripKeepsTuningAndExplicitEmptyAppearanceStillClears()
        {
            var (vm, input, settings) = Arrange();
            SettingsManager.SlotCreated[1] = SettingsManager.SlotEnabled[1] = true;
            var targetSetting = AddDevice(DeviceA, 0, "", "17");
            input.RefreshDeviceList();
            var target = vm.Pads[0];
            var source = vm.Pads[1];
            target.DeviceConfig.HeadphoneVolume = 27;
            target.Model3DAppearances = Carbon;
            source.Model3DAppearances = Sonic;
            SettingsManager.SlotMappingSets[1] = CopySourceMappingSet();
            var copy = SlotAppearancePersistence.CreateClipboardSetting(source, null);
            copy.SlotMultiSourceRows = InputService.ExtractAllRowsForSlot(1);
            copy.SlotSetExtrasJson = InputService.BuildSlotSetExtrasJson(1);
            var configs = SlotAppearancePersistence.FilterClipboardDeviceConfigs(copy,
                settings.BuildDeviceConfigSnapshotForSlot(1));
            Assert.Empty(configs);
            copy.SlotDeviceConfigsJson = System.Text.Json.JsonSerializer.Serialize(configs);
            var decoded = PadSetting.FromJson(copy.ToJson());
            Assert.Equal("[]", decoded.SlotPerDeviceSettingsJson);

            InputService.ApplySlotMappingSetFromRows(0, decoded.SlotMultiSourceRows);
            InputService.ApplySlotSetExtrasJson(0, decoded.SlotSetExtrasJson, sameLayout: true);
            SlotAppearancePersistence.ApplyClipboardSetting(target, decoded);
            input.ApplyPadSettingToCurrentDeviceTranslated(0, decoded,
                VirtualControllerType.Xbox, false, VirtualControllerType.Xbox, false);
            settings.ApplyDeviceSlotConfigsToSlot(0, SlotAppearancePersistence.FilterClipboardDeviceConfigs(decoded,
                System.Text.Json.JsonSerializer.Deserialize<DeviceSlotConfigData[]>(decoded.SlotDeviceConfigsJson)));
            Assert.Equal("17", targetSetting.ForceOverall);
            Assert.Equal(27, target.DeviceConfig.HeadphoneVolume);
            Assert.Equal(Sonic, target.Model3DAppearances);
            Assert.True(SettingsManager.SlotMappingSets[0].KeepAwakeEnabled);
            Assert.Equal("Button 8", target.Mappings.Single(row => row.TargetSettingName == "ButtonA").SourceDescriptor);

            settings.FlushPendingDeviceEdits();
            Assert.Equal("17", targetSetting.ForceOverall);
            Assert.Equal(27, target.DeviceConfig.HeadphoneVolume);
            Assert.Equal(Sonic, target.Model3DAppearances);
            Assert.True(SettingsManager.SlotMappingSets[0].KeepAwakeEnabled);
            Assert.Equal("Button 8", SettingsManager.SlotMappingSets[0].Rows
                .Single(row => row.Target == "ButtonA" && row.LayerMask == "Base").Sources[0].Descriptor);

            source.Model3DAppearances = "";
            var empty = PadSetting.FromJson(SlotAppearancePersistence.CreateClipboardSetting(source, null).ToJson());
            SlotAppearancePersistence.ApplyClipboardSetting(target, empty);
            input.ApplyPadSettingToCurrentDeviceTranslated(0, empty,
                VirtualControllerType.Xbox, false, VirtualControllerType.Xbox, false);
            Assert.Equal("", target.Model3DAppearances);
            Assert.Equal("17", targetSetting.ForceOverall);

            settings.FlushPendingDeviceEdits();
            Assert.Equal("", target.Model3DAppearances);
            Assert.Equal("17", targetSetting.ForceOverall);

            var legacy = new PadSetting { ForceOverall = "61" };
            input.ApplyPadSettingToCurrentDeviceTranslated(0, PadSetting.FromJson(legacy.ToJson()),
                VirtualControllerType.Xbox, false, VirtualControllerType.Xbox, false);
            Assert.Equal("61", targetSetting.ForceOverall);
        }

        [Fact]
        public void ClipboardWithoutAPickerSelectionUsesAnActualSavedDeviceSetting()
        {
            var (vm, _, _) = Arrange();
            AddDevice(DeviceB, 1, Carbon, "99");
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = DeviceB, MapTo = -1 });
            SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = DeviceB, MapTo = 0 });
            var saved = AddDevice(DeviceA, 0, Carbon, "61");
            Assert.Null(vm.Pads[0].SelectedMappedDevice);
            vm.Pads[0].Model3DAppearances = Sonic;
            var copy = SlotAppearancePersistence.CreateClipboardSetting(vm.Pads[0], null);
            Assert.NotSame(saved, copy);
            Assert.True(SlotAppearancePersistence.CarriesDeviceSettings(copy));
            Assert.Equal("61", copy.ForceOverall);
            Assert.Equal(Sonic, copy.Model3DAppearances);
            Assert.Equal(Carbon, saved.Model3DAppearances);
            var entry = Assert.Single(InputService.BuildPerDeviceSettingsSnapshot(0, VirtualControllerType.Xbox, false));
            Assert.Equal(DeviceA.ToString(), entry.InstanceGuid);
            Assert.Equal(copy.ForceOverall, PadSetting.FromJson(entry.PadSettingJson).ForceOverall);
        }

        private static MappingSet CopySourceMappingSet()
        {
            var result = new MappingSet { KeepAwakeEnabled = true, KeepAwakeAxis = "LeftThumbAxisX" };
            result.Rows.Add(new MappingRow
            {
                Target = "ButtonA", LayerMask = "Base",
                Sources = new List<MappingSource> { new MappingSource { Descriptor = "Button 8" } },
            });
            return result;
        }

        [Fact]
        public void AnAppearanceOnlyProfileWithNoDeviceOrMappingPayloadReachesPublication()
        {
            var (vm, input, settings) = Arrange();
            vm.Pads[0].Model3DAppearances = Carbon;
            var profile = new ProfileData { SlotModel3DAppearances = SlotAppearancePersistence.Empty() };
            profile.SlotModel3DAppearances[0] = Sonic;
            Assert.Null(profile.Entries);
            Assert.Null(profile.PadSettings);
            Assert.Null(profile.SlotMappingSets);
            Assert.Empty(SettingsManager.UserSettings.Items);

            input.ApplyProfile(profile);
            Assert.True(SettingsManager.SlotCreated[0]);
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
            Assert.False(InputService.VmMappingsStale);
            settings.FlushPendingDeviceEdits();
            Assert.Equal(Sonic, vm.Pads[0].Model3DAppearances);
            Assert.Empty(SettingsManager.UserSettings.Items);
        }

        [Fact]
        public void NewProfileBuildersAndTransferCarryExplicitSlotAppearanceArrays()
        {
            Arrange();
            foreach (var starter in StarterProfileCatalog.All)
            {
                var profile = starter.Build();
                Assert.NotNull(profile.SlotModel3DAppearances);
                Assert.All(profile.SlotModel3DAppearances, value => Assert.Equal("", value));
            }
            var workshop = WorkshopProfileMaterializer.Materialize(new TranslatedProfile
            {
                Name = "Appearance import", NeedsXboxSlot = true,
            });
            Assert.NotNull(workshop.SlotModel3DAppearances);
            workshop.SlotModel3DAppearances[0] = Sonic;
            WithFile(path =>
            {
                Assert.Empty(ProfileTransfer.Export(workshop, path));
                var imported = ProfileTransfer.Import(path, out var packages);
                Assert.NotNull(imported);
                Assert.Empty(packages);
                Assert.Equal(Sonic, imported.SlotModel3DAppearances[0]);
            });
            workshop.SlotModel3DAppearances = Array.Empty<string>();
            var serializer = new XmlSerializer(typeof(ProfileData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, workshop);
            using var reader = new StringReader(writer.ToString());
            var roundTrip = (ProfileData)serializer.Deserialize(reader);
            Assert.NotNull(roundTrip.SlotModel3DAppearances);
            Assert.Empty(roundTrip.SlotModel3DAppearances);
        }
    }
}
