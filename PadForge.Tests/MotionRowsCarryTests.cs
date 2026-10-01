using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Motion Pitch, Yaw and Roll rows' settings (#475) through every
    /// lane that copies or stores a row. Reflection over the row's persisted
    /// attributes, so a setting added later fails here on the lane that drops
    /// it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MotionRowsCarryTests : IDisposable
    {
        private const int SourceSlot = 0;
        private const int TargetSlot = 1;
        private static readonly Guid SourcePad = new("c4750001-1111-2222-3333-444444444444");
        private static readonly Guid TargetPad = new("c4750002-1111-2222-3333-444444444444");
        private static readonly Guid Product = new("c4750003-1111-2222-3333-444444444444");

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly bool _stale = InputService.VmMappingsStale;
        private readonly bool _suppressPush = InputService.SuppressMappingEditPush;
        private readonly Action _afterRefresh = SettingsService.AfterMappingSetsRefreshed;

        public MotionRowsCarryTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated);
            Array.Clear(SettingsManager.SlotEnabled);
            SettingsManager.SlotCreated[SourceSlot] = SettingsManager.SlotCreated[TargetSlot] = true;
            SettingsManager.SlotEnabled[SourceSlot] = SettingsManager.SlotEnabled[TargetSlot] = true;
            InputService.VmMappingsStale = false;
            InputService.SuppressMappingEditPush = false;
            SettingsService.AfterMappingSetsRefreshed = null;
            AddDevice(SourcePad, SourceSlot);
            AddDevice(TargetPad, TargetSlot);
        }

        public void Dispose()
        {
            InputManager.RequestMotionRowsReset(-1);
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
            InputService.VmMappingsStale = _stale;
            InputService.SuppressMappingEditPush = _suppressPush;
            SettingsService.AfterMappingSetsRefreshed = _afterRefresh;
        }

        private static void AddDevice(Guid id, int slot)
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, ProductGuid = Product, IsOnline = true,
                ProductName = id.ToString(), InstanceName = id.ToString(),
                CapType = InputDeviceType.Gamepad, InputState = state,
            });
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = Product, MapTo = slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
        }

        // ── The row ──

        /// <summary>Every scalar a row persists as an XML attribute.</summary>
        private static List<PropertyInfo> PersistedScalars() => typeof(MappingRow)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite
                        && (p.PropertyType.IsPrimitive || p.PropertyType == typeof(string))
                        && Attribute.IsDefined(p, typeof(XmlAttributeAttribute)))
            .ToList();

        /// <summary>A Motion Pitch row on <paramref name="layer"/> with every
        /// setting moved off its default, each to a value no other setting
        /// holds, so a lane that copies one setting into another fails too. A
        /// setting added later gets a value from its type.</summary>
        private static MappingRow Authored(string layer)
        {
            var row = new MappingRow
            {
                Target = MappingSetMigrator.MotionPitchTarget,
                LayerMask = layer,
                CombineMode = "Sum",
                CombineExpression = "a + b",
                NoInherit = true,
                TrimDeadzone = 7,
                TrimRate = 44,
                TrimResetOnRelease = false,
                MotionResponse = MappingRow.MotionResponseAngle,
                MotionSpeed = 777,
                MotionMinSpeed = 12,
                MotionAngle = 33,
                MotionDeadzone = 9,
                Sources = new List<MappingSource>
                {
                    new() { DeviceGuid = SourcePad.ToString(), Descriptor = "Axis 1" },
                },
            };
            var fresh = new MappingRow();
            int n = 0;
            foreach (var p in PersistedScalars())
            {
                n++;
                if (!Equals(p.GetValue(row), p.GetValue(fresh))) continue;
                if (p.Name is nameof(MappingRow.SuppressBipolarPair) or nameof(MappingRow.LayerMask)) continue;
                if (p.PropertyType == typeof(bool)) p.SetValue(row, !(bool)p.GetValue(fresh));
                else if (p.PropertyType == typeof(int)) p.SetValue(row, 5000 + n);
                else if (p.PropertyType == typeof(string)) p.SetValue(row, "Carried" + n);
                else throw new InvalidOperationException($"MappingRow.{p.Name} needs a test value for {p.PropertyType}");
            }
            return row;
        }

        [Fact]
        public void TheAuthoredRowMovesEverySettingOffItsDefault()
        {
            // Positive control: a setting left at its default would pass every
            // carry check below on a lane that drops it.
            var row = Authored("Shift");
            var fresh = new MappingRow();
            var scalars = PersistedScalars();
            foreach (var name in new[]
                     {
                         nameof(MappingRow.MotionResponse), nameof(MappingRow.MotionSpeed),
                         nameof(MappingRow.MotionMinSpeed), nameof(MappingRow.MotionAngle),
                         nameof(MappingRow.MotionDeadzone),
                     })
                Assert.Contains(scalars, p => p.Name == name);
            foreach (var p in scalars)
            {
                // The layer is the row's identity, which every lane keys on.
                if (p.Name is nameof(MappingRow.SuppressBipolarPair) or nameof(MappingRow.LayerMask)) continue;
                Assert.False(Equals(p.GetValue(row), p.GetValue(fresh)), $"{p.Name} is still at its default");
            }
            var ints = scalars.Where(p => p.PropertyType == typeof(int)).Select(p => (int)p.GetValue(row)).ToList();
            Assert.Equal(ints.Count, ints.Distinct().Count());
        }

        /// <summary>SuppressBipolarPair is recomputed by every lane from the
        /// sources it copies (CustomExpressionCopyPositionTests).</summary>
        private static void AssertCarried(MappingRow source, MappingRow copy, string lane)
        {
            Assert.NotNull(copy);
            Assert.NotSame(source, copy);
            foreach (var p in PersistedScalars())
            {
                if (p.Name == nameof(MappingRow.SuppressBipolarPair)) continue;
                Assert.True(Equals(p.GetValue(source), p.GetValue(copy)),
                    $"{lane} drops {p.Name}: {p.GetValue(source)} became {p.GetValue(copy)}");
            }
        }

        private static MappingRow Find(MappingSet set, string layer)
            => set?.Rows.SingleOrDefault(r => r.Target == MappingSetMigrator.MotionPitchTarget && r.LayerMask == layer);

        private static MappingSet Seed(MappingRow row)
        {
            var set = new MappingSet();
            set.Rows.Add(row);
            SettingsManager.SlotMappingSets[SourceSlot] = set;
            return set;
        }

        private static PadSetting ThroughClipboard(PadSetting payload)
            => PadSetting.FromJson(payload.ToJson(VirtualControllerType.PlayStation, false), out _, out _);

        // ── The lanes ──

        [Fact]
        public void CopySettingsCarriesEverySettingButTheRowsIdentity()
        {
            // Every row-to-row lane goes through this one method.
            var row = Authored("Shift");
            var copy = new MappingRow { Target = "ButtonA", LayerMask = "Base" };
            row.CopySettingsTo(copy);
            foreach (var p in PersistedScalars())
            {
                if (p.Name is nameof(MappingRow.Target) or nameof(MappingRow.LayerMask)
                    or nameof(MappingRow.SuppressBipolarPair)) continue;
                Assert.True(Equals(p.GetValue(row), p.GetValue(copy)), $"CopySettingsTo drops {p.Name}");
            }
            Assert.Equal("ButtonA", copy.Target);
            Assert.Equal("Base", copy.LayerMask);
            Assert.Empty(copy.Sources);
        }

        [Fact]
        public void AProfileSnapshotCarriesTheSettings()
        {
            var row = Authored("Base");
            var copy = InputService.CloneMappingSetDeep(Seed(row));
            AssertCarried(row, Find(copy, "Base"), "CloneMappingSetDeep");
        }

        [Fact]
        public void CopyAndPasteCarryTheSettings()
        {
            var row = Authored("Base");
            Seed(row);
            var parsed = ThroughClipboard(new PadSetting { SlotMultiSourceRows = InputService.ExtractAllRowsForSlot(SourceSlot) });
            InputService.ApplySlotMappingSetFromRows(TargetSlot, parsed.SlotMultiSourceRows);
            var pasted = Find(SettingsManager.SlotMappingSets[TargetSlot], "Base");
            AssertCarried(row, pasted, "Copy and Paste");
            Assert.Equal(TargetPad.ToString(), Assert.Single(pasted.Sources).DeviceGuid);
        }

        [Fact]
        public void CopyFromCarriesTheSettings()
        {
            var row = Authored("Base");
            Seed(row);
            InputService.ReplaceSlotMappingSet(TargetSlot, SourceSlot);
            AssertCarried(row, Find(SettingsManager.SlotMappingSets[TargetSlot], "Base"), "Copy From");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ADeviceCopyAndPasteCarriesTheSettings(bool rowAlreadyThere)
        {
            var row = Authored("Base");
            Seed(row);
            if (rowAlreadyThere)
            {
                var existing = new MappingSet();
                existing.Rows.Add(new MappingRow { Target = MappingSetMigrator.MotionPitchTarget, LayerMask = "Base" });
                SettingsManager.SlotMappingSets[TargetSlot] = existing;
            }
            var parsed = ThroughClipboard(new PadSetting
            {
                DeviceScopedMultiSourceRows = InputService.ExtractDeviceScopedRowsForSlot(SourceSlot, SourcePad),
            });
            InputService.ApplyMultiSourceRowsToCurrentDevice(TargetSlot, TargetPad, parsed.DeviceScopedMultiSourceRows);
            var pasted = Find(SettingsManager.SlotMappingSets[TargetSlot], "Base");
            AssertCarried(row, pasted, rowAlreadyThere ? "a device paste onto a row" : "a device paste");
            Assert.Equal(TargetPad.ToString(), Assert.Single(pasted.Sources).DeviceGuid);
        }

        [Fact]
        public void AShiftLayerCopyCarriesTheSettings()
        {
            var row = Authored("Shift");
            AssertCarried(row, PadForge.Views.PadPage.CloneLayerRow(row, "Shift"), "the layer copy");
        }

        [Fact]
        public void TheSettingsFileCarriesTheSettings()
        {
            var row = Authored("Shift");
            Seed(row);
            var serializer = new XmlSerializer(typeof(MappingSet[]));
            using var writer = new StringWriter();
            serializer.Serialize(writer, SettingsManager.SlotMappingSets);
            using var reader = new StringReader(writer.ToString());
            var restored = (MappingSet[])serializer.Deserialize(reader);
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsService.LoadOrMigrateSlotMappingSetsForTest(restored);
            AssertCarried(row, Find(SettingsManager.SlotMappingSets[SourceSlot], "Shift"), "the settings file");
        }

        [Fact]
        public void AnOlderSettingsFileReadsTheDefaults()
        {
            // A row saved before #475 has none of the five attributes.
            const string xml = "<MappingRow Target=\"MotionPitch\" LayerMask=\"Base\" />";
            var row = (MappingRow)new XmlSerializer(typeof(MappingRow)).Deserialize(new StringReader(xml));
            Assert.Equal("", row.MotionResponse);
            Assert.Equal(MappingRow.DefaultMotionSpeed, row.MotionSpeed);
            Assert.Equal(0, row.MotionMinSpeed);
            Assert.Equal(MappingRow.DefaultMotionAngle, row.MotionAngle);
            Assert.Equal(MappingRow.DefaultMotionDeadzone, row.MotionDeadzone);
        }

        [Fact]
        public void TheGridLoadsAndSavesTheSettings()
        {
            var vm = new MainViewModel();
            var service = new SettingsService(vm);
            var pad = vm.Pads[SourceSlot];
            pad.OutputType = VirtualControllerType.PlayStation;
            var row = new MappingRow
            {
                Target = MappingSetMigrator.MotionRollTarget, LayerMask = "Base",
                MotionResponse = MappingRow.MotionResponseAngle,
                MotionSpeed = 900, MotionMinSpeed = 30, MotionAngle = 45, MotionDeadzone = 15,
                Sources = new List<MappingSource> { new() { DeviceGuid = SourcePad.ToString(), Descriptor = "Axis 0" } },
            };
            Seed(row);

            InputService.RefreshMappingsToViewModel(pad);
            Assert.True(pad.MappingsViewLoaded);
            var item = pad.Mappings.Single(m => m.TargetSettingName == MappingSetMigrator.MotionRollTarget);
            Assert.Equal(MappingRow.MotionResponseAngle, item.MotionResponse);
            Assert.Equal(900, item.MotionSpeed);
            Assert.Equal(30, item.MotionMinSpeed);
            Assert.Equal(45, item.MotionAngle);
            Assert.Equal(15, item.MotionDeadzone);

            item.MotionResponse = "";
            item.MotionSpeed = 120;
            item.MotionMinSpeed = 5;
            item.MotionAngle = 60;
            item.MotionDeadzone = 25;
            service.PushUiExtraSourcesIntoSlotMappingSets();

            var saved = SettingsManager.SlotMappingSets[SourceSlot].Rows
                .Single(r => r.Target == MappingSetMigrator.MotionRollTarget && r.LayerMask == "Base");
            Assert.Equal("", saved.MotionResponse);
            Assert.Equal(120, saved.MotionSpeed);
            Assert.Equal(5, saved.MotionMinSpeed);
            Assert.Equal(60, saved.MotionAngle);
            Assert.Equal(25, saved.MotionDeadzone);

            // A slot without the row loads the defaults, never the last
            // row's values.
            item.MotionResponse = MappingRow.MotionResponseAngle;
            SettingsManager.SlotMappingSets[SourceSlot].Rows.RemoveAll(r => r.Target == MappingSetMigrator.MotionRollTarget);
            InputService.RefreshMappingsToViewModel(pad);
            item = pad.Mappings.Single(m => m.TargetSettingName == MappingSetMigrator.MotionRollTarget);
            Assert.Equal("", item.MotionResponse);
            Assert.Equal(MappingRow.DefaultMotionSpeed, item.MotionSpeed);
            Assert.Equal(0, item.MotionMinSpeed);
            Assert.Equal(MappingRow.DefaultMotionAngle, item.MotionAngle);
            Assert.Equal(MappingRow.DefaultMotionDeadzone, item.MotionDeadzone);
        }

        [Fact]
        public void TheDefaultsAreTheirPrecedents()
        {
            Assert.Equal(360, MappingRow.DefaultMotionSpeed);      // eden: one turn a second
            Assert.Equal(85, MappingRow.DefaultMotionAngle);       // Dolphin's Tilt angle
            Assert.Equal(20, MappingRow.DefaultMotionDeadzone);    // eden and cemu-no-gyro
            Assert.Equal(MotionRowsModel.MaxRateDps, (float)MappingRow.MaxMotionSpeed);
            var row = new MappingRow();
            Assert.Equal("", row.MotionResponse);
            Assert.Equal(0, row.MotionMinSpeed);
        }

        /// <summary>The Pad page saves an edited row setting and pushes it to
        /// the engine from two name lists in MainWindow's MappingItem handler.
        /// A setting missing from the first is never saved, and one missing
        /// from the second reaches the engine only at the debounced save,
        /// after a reload in between has reverted it (#155).</summary>
        [Fact]
        public void EditingARowSettingSavesItAndPushesItToTheEngine()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string text = File.ReadAllText(Path.Combine(dir.FullName, "PadForge.App", "MainWindow.xaml.cs"));
            int start = text.IndexOf("// Mapping descriptor changes (inversion, half-axis, source) trigger autosave.", StringComparison.Ordinal);
            Assert.True(start >= 0, "the handler moved");
            int dirty = text.IndexOf("_settingsService.MarkDirty();", start, StringComparison.Ordinal);
            int push = text.IndexOf("_settingsService.PushUiExtraSourcesIntoSlotMappingSets();", dirty, StringComparison.Ordinal);
            Assert.True(dirty > start && push > dirty, "the handler moved");
            string dirtyList = text.Substring(start, dirty - start);
            string pushList = text.Substring(dirty, push - dirty);

            var itemProps = typeof(MappingItem).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).ToHashSet();
            var settings = PersistedScalars().Select(p => p.Name).Where(itemProps.Contains).ToList();
            Assert.Contains(nameof(MappingRow.MotionSpeed), settings);
            foreach (var name in settings)
            {
                Assert.Contains($"nameof(MappingItem.{name})", dirtyList);
                Assert.Contains($"nameof(MappingItem.{name})", pushList);
            }
        }
    }
}
