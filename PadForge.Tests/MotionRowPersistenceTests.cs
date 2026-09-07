using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public class MotionRowPersistenceTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnyDeviceMotion_KeepsAuthoredOverlapAcrossRefreshAndLoad(bool accel)
        {
            using var rig = new Rig();
            var a = rig.AddDevice(10);
            rig.AddDevice(20);
            var row = rig.SetRow(accel, "Sum", new MappingSource
            {
                Descriptor = Rig.Descriptor(accel),
            }, Rig.Source(a, accel));
            Assert.Equal(30f, rig.Value(accel), 4);

            SettingsService.RefreshMappingSetsFromLegacy();
            Assert.Equal(2, row.Sources.Count);
            Assert.Equal(30f, rig.Value(accel), 4);
            rig.RoundTripThroughLoader();
            row = rig.Row(accel);
            Assert.Equal(2, row.Sources.Count);
            Assert.Equal("", row.Sources[0].DeviceGuid);
            Assert.Equal(a.InstanceGuidString, row.Sources[1].DeviceGuid);
            Assert.Equal(30f, rig.Value(accel), 4);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BlankDirect_IsNotAnAnyDeviceMotionSource(bool accel)
        {
            using var rig = new Rig();
            var a = rig.AddDevice(10);
            var b = rig.AddDevice(20);
            var row = rig.SetRow(accel, "Sum", new MappingSource(), Rig.Source(a, accel));
            Assert.Equal(10f, rig.Value(accel), 4);

            SettingsService.AfterMappingSetsRefreshed();

            Assert.Equal(3, row.Sources.Count);
            Assert.Equal("", row.Sources[0].Descriptor);
            Assert.Equal(b.InstanceGuidString, row.Sources[2].DeviceGuid);
            Assert.Equal(30f, rig.Value(accel), 4);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CustomMotion_DoesNotAcquireAutomaticArguments(bool accel)
        {
            using var rig = new Rig();
            var a = rig.AddDevice(10);
            var b = rig.AddDevice(20);
            rig.AddDevice(100);
            var row = rig.SetRow(accel, "Custom", new MappingSource(), Rig.Source(a, accel), Rig.Source(b, accel));
            row.CombineExpression = "s[2] - b";
            Assert.Equal(10f, rig.Value(accel), 4);

            SettingsService.RefreshMappingSetsFromLegacy();
            rig.RoundTripThroughLoader();

            Assert.Equal(3, rig.Row(accel).Sources.Count);
            Assert.Equal("s[2] - b", rig.Row(accel).CombineExpression);
            Assert.Equal(10f, rig.Value(accel), 4);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CustomMotion_RemovalKeepsZeroPositionsAndSkipsModifiers(bool merge)
        {
            using var rig = new Rig();
            var a = rig.AddDevice(10);
            var b = rig.AddDevice(20);
            var c = rig.AddDevice(30);
            var row = rig.SetRow(false, "Custom", Rig.Source(a, false), new MappingSource
            {
                Kind = "InvertOnHold", DeviceGuid = a.InstanceGuidString, ParamModifier = "Button 0",
            }, Rig.Source(b, false), Rig.Source(c, false));
            row.CombineExpression = "a + b * 2 + s[2] * 3";
            Assert.Equal(140f, rig.Value(false), 4);

            SettingsManager.UserSettings.Items.RemoveAll(us => us.InstanceGuid == a.InstanceGuid);
            if (merge) SettingsService.RefreshMappingSetsFromLegacy();
            else SettingsService.StripDeviceFromSlot(a.InstanceGuid, 0);

            row = rig.Row(false);
            Assert.Equal(3, row.Sources.Count);
            Assert.Equal("Direct", row.Sources[0].Kind);
            Assert.Equal("", row.Sources[0].Descriptor);
            Assert.Equal("", row.Sources[0].DeviceGuid);
            Assert.False(row.Sources[0].Invert);
            Assert.Equal(b.InstanceGuidString, row.Sources[1].DeviceGuid);
            Assert.Equal(c.InstanceGuidString, row.Sources[2].DeviceGuid);
            Assert.Equal(130f, rig.Value(false), 4);
            rig.RoundTripThroughLoader();
            Assert.Equal(130f, rig.Value(false), 4);
        }

        [Fact]
        public void CustomMotion_RepeatedZeroSlotsSurviveSanitization()
        {
            using var rig = new Rig();
            var device = rig.AddDevice(20);
            var row = rig.SetRow(false, "Custom", new MappingSource(), new MappingSource(), Rig.Source(device, false));
            row.CombineExpression = "s[1 + 1]";
            var ordinary = new MappingRow { Target = "ButtonA" };
            ordinary.Sources.Add(new MappingSource { Descriptor = "Button 0" });
            ordinary.Sources.Add(new MappingSource { Descriptor = "Button 0" });
            rig.Set.Rows.Add(ordinary);
            Assert.Equal(20f, rig.Value(false), 4);

            SettingsService.SanitizeMappingSet(rig.Set, 0);

            Assert.Single(ordinary.Sources);
            Assert.Equal(3, row.Sources.Count);
            Assert.Equal(20f, rig.Value(false), 4);
            rig.RoundTripThroughLoader();
            Assert.Equal(3, rig.Row(false).Sources.Count);
            Assert.Equal(20f, rig.Value(false), 4);
        }

        [Fact]
        public void ClearingMotionInTheEditor_SurvivesSerializationAndTheProductionLoader()
        {
            using var rig = new Rig();
            rig.AddDevice(10);
            SettingsService.AfterMappingSetsRefreshed();
            Assert.Equal(10f, rig.Value(false), 4);
            Assert.True(rig.Manager.DsuMotionSnapshots[0].HasMotion);
            InputService.RefreshMappingsToViewModel(rig.ViewModel.Pads[0]);
            Assert.True(rig.ViewModel.Pads[0].MappingsViewLoaded);
            foreach (var mapping in rig.ViewModel.Pads[0].Mappings.Where(m => MappingSetMigrator.IsMotionTarget(m.TargetSettingName)))
            {
                Assert.False(string.IsNullOrEmpty(mapping.SourceDescriptor));
                mapping.ClearCommand.Execute(null);
                mapping.ExtraSources.Clear();
            }

            rig.Settings.PushUiExtraSourcesIntoSlotMappingSets();
            Assert.Empty(rig.Row(false).Sources);
            Assert.Empty(rig.Row(true).Sources);
            rig.Update();
            Assert.False(rig.Manager.DsuMotionSnapshots[0].HasMotion);

            rig.RoundTripThroughLoader();
            Assert.Empty(rig.Row(false).Sources);
            Assert.Empty(rig.Row(true).Sources);
            rig.Update();
            Assert.False(rig.Manager.MotionSnapshots[0].HasMotion);
            Assert.False(rig.Manager.DsuMotionSnapshots[0].HasMotion);

            rig.Set.Rows.RemoveAll(r => MappingSetMigrator.IsMotionTarget(r.Target));
            SettingsService.AfterMappingSetsRefreshed();
            Assert.Equal(10f, rig.Value(false), 4);
            Assert.True(rig.Manager.DsuMotionSnapshots[0].HasMotion);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AuthoredEmptyMotionRowsSurviveMergeAndUnassign(bool unassign)
        {
            using var rig = new Rig();
            var a = rig.AddDevice(10);
            rig.AddDevice(20);
            rig.SetRow(false, "", Rig.Source(a, false));
            rig.SetRow(true, "", Rig.Source(a, true));
            Assert.Equal(10f, rig.Value(false), 4);
            rig.Set.Rows.Add(new MappingRow { Target = "ButtonB" });
            rig.Row(false).Sources.Clear();
            rig.Row(true).Sources.Clear();

            if (unassign)
            {
                SettingsManager.UserSettings.Items.RemoveAll(us => us.InstanceGuid == a.InstanceGuid);
                SettingsService.StripDeviceFromSlot(a.InstanceGuid, 0);
            }
            SettingsService.RefreshMappingSetsFromLegacy();

            Assert.Empty(rig.Row(false).Sources);
            Assert.Empty(rig.Row(true).Sources);
            Assert.DoesNotContain(rig.Set.Rows, r => r.Target == "ButtonB");
            rig.Update();
            Assert.False(rig.Manager.DsuMotionSnapshots[0].HasMotion);
            rig.Set.Rows.RemoveAll(r => MappingSetMigrator.IsMotionTarget(r.Target));
            SettingsService.AfterMappingSetsRefreshed();
            Assert.Equal(20f, rig.Value(false), 4);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void UnassignThenAssign_RebuildsVacatedMotionForTheReplacement(bool accel, bool authoritative)
        {
            using var rig = new Rig();
            var a = rig.AddDevice(10);
            var original = rig.SetRow(accel, "", Rig.Source(a, accel));
            rig.Set.Authoritative = authoritative;
            Assert.Equal(10f, rig.Value(accel), 4);

            Assert.True(SettingsManager.UnassignDevice(a.InstanceGuid));
            SettingsService.StripDeviceFromSlot(a.InstanceGuid, 0);
            Assert.DoesNotContain(original, rig.Set.Rows);

            var b = rig.AddDevice(20);
            SettingsManager.UserSettings.Items.Single(us => us.InstanceGuid == b.InstanceGuid).GetPadSetting().ButtonA = "Button 0";
            SettingsService.RefreshMappingSetsFromLegacy();

            var replacement = rig.Row(accel);
            Assert.NotSame(original, replacement);
            Assert.Equal(b.InstanceGuidString, Assert.Single(replacement.Sources).DeviceGuid);
            Assert.Equal(20f, rig.Value(accel), 4);
            Assert.Equal(authoritative, rig.Set.Authoritative);
            Assert.Equal(!authoritative, rig.Set.Rows.Any(r => r.Target == "ButtonA"));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void PersistedRowWithReplacementRoster_IsDroppedBeforeMergeOwnership(bool accel, bool authoritative)
        {
            using var rig = new Rig();
            var a = rig.AddDevice(10);
            rig.SetRow(accel, "", Rig.Source(a, accel));
            rig.Set.Authoritative = authoritative;
            Assert.Equal(10f, rig.Value(accel), 4);
            rig.RoundTripThroughLoader(backfill: false);
            var persisted = rig.Row(accel);
            Assert.Equal(a.InstanceGuidString, Assert.Single(persisted.Sources).DeviceGuid);

            Assert.True(SettingsManager.UnassignDevice(a.InstanceGuid));
            var b = rig.AddDevice(20);
            SettingsManager.UserSettings.Items.Single(us => us.InstanceGuid == b.InstanceGuid).GetPadSetting().ButtonA = "Button 0";
            SettingsService.RefreshMappingSetsFromLegacy();

            Assert.DoesNotContain(persisted, rig.Set.Rows);
            Assert.Equal(b.InstanceGuidString, Assert.Single(rig.Row(accel).Sources).DeviceGuid);
            Assert.Equal(20f, rig.Value(accel), 4);
            Assert.Equal(authoritative, rig.Set.Authoritative);
            Assert.Equal(!authoritative, rig.Set.Rows.Any(r => r.Target == "ButtonA"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ModifierOnlyAuthoredMotion_RemainsEmptyAfterItsDeviceLeaves(bool accel)
        {
            using var rig = new Rig();
            var a = rig.AddDevice(10);
            rig.AddDevice(20);
            rig.SetRow(accel, "", Rig.Source(a, accel));
            Assert.Equal(10f, rig.Value(accel), 4);
            var authored = rig.SetRow(accel, "", new MappingSource
            {
                DeviceGuid = a.InstanceGuidString, Kind = "InvertOnHold", ParamModifier = "Button 0",
            });
            SettingsService.AfterMappingSetsRefreshed();
            Assert.Single(authored.Sources);
            Assert.Equal(0f, rig.Value(accel));

            Assert.True(SettingsManager.UnassignDevice(a.InstanceGuid));
            SettingsService.StripDeviceFromSlot(a.InstanceGuid, 0);
            SettingsService.RefreshMappingSetsFromLegacy();

            Assert.Contains(authored, rig.Set.Rows);
            Assert.Empty(authored.Sources);
            Assert.Equal(0f, rig.Value(accel));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnsupportedDirectDescriptor_DoesNotCreateAnAuthoredDisable(bool accel)
        {
            using var rig = new Rig();
            var a = rig.AddDevice(10);
            var b = rig.AddDevice(20);
            var row = rig.SetRow(accel, "Sum", Rig.Source(a, accel));
            Assert.Equal(10f, rig.Value(accel), 4);
            row.Sources[0].Descriptor = "Axis 0";
            Assert.Equal(0f, rig.Value(accel));

            SettingsService.AfterMappingSetsRefreshed();

            Assert.Equal(2, row.Sources.Count);
            Assert.Equal("Axis 0", row.Sources[0].Descriptor);
            Assert.Equal(b.InstanceGuidString, row.Sources[1].DeviceGuid);
            Assert.Equal(20f, rig.Value(accel), 4);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SourceCleanup_PreservesAnExplicitNoInheritRow(bool accel)
        {
            using var rig = new Rig();
            var a = rig.AddDevice(10);
            var row = rig.SetRow(accel, "", Rig.Source(a, accel));
            row.LayerMask = "Shift";
            row.NoInherit = true;
            Assert.Equal(10f, rig.Value(accel), 4);

            Assert.True(SettingsManager.UnassignDevice(a.InstanceGuid));
            SettingsService.StripDeviceFromSlot(a.InstanceGuid, 0);
            SettingsService.RefreshMappingSetsFromLegacy();

            Assert.Contains(row, rig.Set.Rows);
            Assert.True(row.NoInherit);
            Assert.Empty(row.Sources);
        }

        [Fact]
        public void AllBlankCustomMotion_DoesNotBecomeAnAutomaticSensorOnLoad()
        {
            using var rig = new Rig();
            var device = rig.AddDevice(20);
            rig.SetRow(false, "", Rig.Source(device, false));
            Assert.Equal(20f, rig.Value(false), 4);
            rig.SetRow(false, "Custom", new MappingSource(), new MappingSource()).CombineExpression = "1";
            rig.SetRow(true, "", Array.Empty<MappingSource>());
            rig.RoundTripThroughLoader();
            rig.Update();
            Assert.Equal(2, rig.Row(false).Sources.Count);
            Assert.False(rig.Manager.DsuMotionSnapshots[0].HasMotion);
        }

        private sealed class MotionDevice : WebControllerDevice, ISdlInputDevice
        {
            public MotionDevice() : base(Guid.NewGuid().ToString(), "Motion row test") { }
            public bool HasGyroAux { get; set; }
            public bool HasAccelAux { get; set; }
        }

        private sealed class Rig : IDisposable
        {
            private readonly SettingsCollection savedSettings = SettingsManager.UserSettings;
            private readonly DeviceCollection savedDevices = SettingsManager.UserDevices;
            private readonly MappingSet[] savedSets = SettingsManager.SlotMappingSets;
            private readonly bool[] savedCreated = SettingsManager.SlotCreated;
            private readonly bool[] savedEnabled = SettingsManager.SlotEnabled;
            private readonly Action savedAfterRefresh = SettingsService.AfterMappingSetsRefreshed;
            private readonly bool savedStale = InputService.VmMappingsStale;
            private readonly Func<string, int, SourceCoercion.GyroTuning> savedTuning = SourceCoercion.GyroTuningProvider;
            private readonly Func<string, int, (float, float, float)> savedBias = SourceCoercion.GyroBiasProvider;
            private readonly List<MotionDevice> devices = new();
            private readonly DsuMotionServer server = new();
            private readonly Action update;
            public InputManager Manager { get; } = new();
            public MainViewModel ViewModel { get; }
            public SettingsService Settings { get; }
            public MappingSet Set => SettingsManager.SlotMappingSets[0];

            public Rig()
            {
                try
                {
                    SettingsManager.UserSettings = new SettingsCollection();
                    SettingsManager.UserDevices = new DeviceCollection();
                    SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                    SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                    SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
                    InputService.VmMappingsStale = false;
                    SourceCoercion.GyroTuningProvider = (_, _) => new SourceCoercion.GyroTuning
                    {
                        Grip = "Pointing", ApplyToPassthrough = false,
                    };
                    SourceCoercion.GyroBiasProvider = (_, _) => (0f, 0f, 0f);
                    ViewModel = new MainViewModel();
                    Settings = new SettingsService(ViewModel);
                    SettingsManager.SlotCreated[0] = true;
                    SettingsManager.SlotEnabled[0] = true;
                    ViewModel.Pads[0].OutputType = VirtualControllerType.PlayStation;
                    SettingsManager.SlotMappingSets[0] = new MappingSet();
                    Manager.SlotControllerTypes[0] = VirtualControllerType.PlayStation;
                    Manager.DsuServer = server;
                    update = typeof(InputManager).GetMethod("UpdateMotionSnapshots", BindingFlags.Instance | BindingFlags.NonPublic)
                        .CreateDelegate<Action>(Manager);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public UserDevice AddDevice(float value)
            {
                var wrapper = new MotionDevice { HasGyro = true, HasAccel = true };
                devices.Add(wrapper);
                var state = new CustomInputState();
                state.Gyro[0] = value * MathF.PI / 180f;
                state.Accel[0] = value * 9.80665f;
                var device = new UserDevice
                {
                    InstanceGuid = Guid.NewGuid(), ProductGuid = Guid.NewGuid(),
                    Device = wrapper, InputState = state, IsOnline = true,
                    HasGyro = true, HasAccel = true, CapType = InputDeviceType.Gamepad,
                };
                SettingsManager.UserDevices.Items.Add(device);
                var setting = new UserSetting { InstanceGuid = device.InstanceGuid, ProductGuid = device.ProductGuid, MapTo = 0 };
                setting.SetPadSetting(new PadSetting());
                SettingsManager.UserSettings.Items.Add(setting);
                return device;
            }

            public static string Descriptor(bool accel) => accel ? MappingSetMigrator.MotionAccelSourceDescriptor : MappingSetMigrator.MotionGyroSourceDescriptor;
            public static MappingSource Source(UserDevice device, bool accel) => new()
            {
                DeviceGuid = device.InstanceGuidString, Descriptor = Descriptor(accel),
            };
            public MappingRow Row(bool accel) => Set.Rows.Single(r => r.Target == (accel ? MappingSetMigrator.MotionAccelTarget : MappingSetMigrator.MotionGyroTarget));
            public MappingRow SetRow(bool accel, string mode, params MappingSource[] sources)
            {
                string target = accel ? MappingSetMigrator.MotionAccelTarget : MappingSetMigrator.MotionGyroTarget;
                Set.Rows.RemoveAll(r => r.Target == target);
                var row = new MappingRow { Target = target, CombineMode = mode, Sources = sources.ToList() };
                Set.Rows.Add(row);
                return row;
            }
            public void Update() => update();
            public float Value(bool accel)
            {
                Update();
                return accel ? Manager.MotionSnapshots[0].AccelX : Manager.MotionSnapshots[0].GyroPitch;
            }
            public void RoundTripThroughLoader(bool backfill = true)
            {
                var serializer = new XmlSerializer(typeof(MappingSet[]));
                using var writer = new StringWriter();
                serializer.Serialize(writer, SettingsManager.SlotMappingSets);
                using var reader = new StringReader(writer.ToString());
                var restored = (MappingSet[])serializer.Deserialize(reader);
                typeof(SettingsService).GetMethod("LoadOrMigrateSlotMappingSets", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { restored });
                if (backfill) SettingsService.AfterMappingSetsRefreshed();
            }
            public void Dispose()
            {
                try
                {
                    Manager.DsuServer = null;
                    server.Dispose();
                    Manager.Dispose();
                    foreach (var device in devices) device.Dispose();
                }
                finally
                {
                    SettingsManager.UserSettings = savedSettings;
                    SettingsManager.UserDevices = savedDevices;
                    SettingsManager.SlotMappingSets = savedSets;
                    SettingsManager.SlotCreated = savedCreated;
                    SettingsManager.SlotEnabled = savedEnabled;
                    SettingsService.AfterMappingSetsRefreshed = savedAfterRefresh;
                    InputService.VmMappingsStale = savedStale;
                    SourceCoercion.GyroTuningProvider = savedTuning;
                    SourceCoercion.GyroBiasProvider = savedBias;
                }
            }
        }
    }
}
