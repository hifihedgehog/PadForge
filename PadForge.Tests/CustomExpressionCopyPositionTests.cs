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
    public class CustomExpressionCopyPositionTests : IDisposable
    {
        private const int SourceSlot = 0;
        private const int TargetSlot = 1;
        private static readonly Guid MissingA = new("b7050001-1111-2222-3333-444444444444");
        private static readonly Guid MissingB = new("b7050002-1111-2222-3333-444444444444");
        private static readonly Guid SourcePad = new("b7050003-1111-2222-3333-444444444444");
        private static readonly Guid TargetPad = new("b7050004-1111-2222-3333-444444444444");
        private static readonly Guid OtherPad = new("b7050005-1111-2222-3333-444444444444");
        private static readonly Guid SharedProduct = new("b7050006-1111-2222-3333-444444444444");

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly bool _stale = InputService.VmMappingsStale;
        private readonly bool _suppressPush = InputService.SuppressMappingEditPush;
        private readonly Action _afterRefresh = SettingsService.AfterMappingSetsRefreshed;

        private MainViewModel _vm;
        private SettingsService _service;
        private readonly CustomInputState _targetState = RestState();
        private readonly CustomInputState _otherState = RestState();

        public CustomExpressionCopyPositionTests()
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
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            _vm = new MainViewModel();
            _service = new SettingsService(_vm);
            AddDevice(MissingA, MissingA, SourceSlot, RestState());
            AddDevice(MissingB, MissingB, SourceSlot, RestState());
            AddDevice(SourcePad, SharedProduct, SourceSlot, RestState());
            AddDevice(TargetPad, SharedProduct, TargetSlot, _targetState);
            AddDevice(OtherPad, OtherPad, TargetSlot, _otherState);
        }

        public void Dispose()
        {
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
            Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
            InputService.VmMappingsStale = _stale;
            InputService.SuppressMappingEditPush = _suppressPush;
            SettingsService.AfterMappingSetsRefreshed = _afterRefresh;
        }

        private static CustomInputState RestState()
        {
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = state.Axis[5] = 0;
            Array.Fill(state.Povs, -1);
            return state;
        }

        private static void AddDevice(Guid id, Guid product, int slot, CustomInputState state)
        {
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, ProductGuid = product, IsOnline = true,
                ProductName = id.ToString(), InstanceName = id.ToString(),
                CapType = InputDeviceType.Gamepad, InputState = state,
            });
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = product, MapTo = slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
        }

        private static MappingSource Source(Guid id, string descriptor, bool invert = false)
            => new() { DeviceGuid = id.ToString(), Descriptor = descriptor, Invert = invert };

        private static MappingRow Row(string target, string formula, params MappingSource[] sources)
            => new() { Target = target, LayerMask = "Base", CombineMode = "Custom",
                CombineExpression = formula, Sources = sources.ToList() };

        private MappingSet Copy(MappingRow row, bool clipboard)
        {
            SettingsManager.SlotMappingSets[SourceSlot] = new MappingSet { Rows = new List<MappingRow> { row } };
            if (clipboard)
            {
                var payload = new PadSetting { SlotMultiSourceRows = InputService.ExtractAllRowsForSlot(SourceSlot) };
                var parsed = PadSetting.FromJson(payload.ToJson(VirtualControllerType.Xbox, false), out _, out _);
                InputService.ApplySlotMappingSetFromRows(TargetSlot, parsed.SlotMultiSourceRows);
            }
            else InputService.ReplaceSlotMappingSet(TargetSlot, SourceSlot);
            return SettingsManager.SlotMappingSets[TargetSlot];
        }

        private MappingItem Hydrate(string target, MappingCategory category)
        {
            _vm ??= new MainViewModel();
            _service ??= new SettingsService(_vm);
            var pad = _vm.Pads[TargetSlot];
            pad.Mappings.Clear();
            var item = new MappingItem(target, target, category);
            pad.Mappings.Add(item);
            InputService.RefreshMappingsToViewModel(pad);
            Assert.True(pad.MappingsViewLoaded);
            return item;
        }

        private MappingItem SaveReload(string target, MappingCategory category)
        {
            _service.PushUiExtraSourcesIntoSlotMappingSets();
            var serializer = new XmlSerializer(typeof(MappingSet));
            using var stream = new MemoryStream();
            serializer.Serialize(stream, SettingsManager.SlotMappingSets[TargetSlot]);
            stream.Position = 0;
            var loaded = (MappingSet)serializer.Deserialize(stream);
            var persisted = new MappingSet[InputManager.MaxPads];
            persisted[TargetSlot] = loaded;
            SettingsService.LoadOrMigrateSlotMappingSetsForTest(persisted);
            SettingsManager.SlotMappingSets[TargetSlot] = InputService.CloneMappingSetDeep(
                SettingsManager.SlotMappingSets[TargetSlot]);
            return Hydrate(target, category);
        }

        private short Trigger(string target = "LeftTrigger")
        {
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(_targetState,
                SettingsManager.SlotMappingSets[TargetSlot], TargetPad.ToString(), TargetSlot, target, out short value));
            return value;
        }

        private short Bipolar(string target = "LeftThumbAxisX")
        {
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(_targetState,
                SettingsManager.SlotMappingSets[TargetSlot], TargetPad.ToString(), TargetSlot, target, out short value));
            return value;
        }

        private Gamepad GamepadOutput()
        {
            var begin = typeof(InputManager).GetMethod("BeginFrameMultiSourceTracking", BindingFlags.NonPublic | BindingFlags.Static);
            var apply = typeof(InputManager).GetMethod("ApplyMappingSetToGamepad", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(begin);
            Assert.NotNull(apply);
            begin.Invoke(null, null);
            object[] args = { _targetState, SettingsManager.SlotMappingSets[TargetSlot], TargetPad.ToString(), 50, TargetSlot, new Gamepad() };
            apply.Invoke(null, args);
            return (Gamepad)args[5];
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MissingSourcesKeepPositionsThroughCopyEditSaveAndReload(bool clipboard)
        {
            var unavailable = Source(MissingA, "Axis 2", invert: true);
            unavailable.Kind = "Incremental";
            unavailable.ParamUp = "Button 7";
            unavailable.ParamMin = 1;
            unavailable.GateDescriptor = "Button 8";
            var row = Row("LeftTrigger", "a + b + s[1 + 1] * 0.25 + d * 0.5",
                unavailable, Source(MissingB, "Button 1", invert: true),
                Source(SourcePad, "Button 0"), new MappingSource { Descriptor = "Button 1" });
            _targetState.Buttons[0] = true;
            _targetState.Buttons[1] = false;
            _otherState.Buttons[1] = true;

            var copied = Copy(row, clipboard);
            Assert.Equal(4, copied.Rows[0].Sources.Count);
            foreach (var blank in copied.Rows[0].Sources.Take(2))
            {
                Assert.Equal("Direct", blank.Kind);
                Assert.Equal("", blank.Descriptor);
                Assert.Equal("", blank.DeviceGuid);
                Assert.False(blank.Invert);
                Assert.Equal("", blank.ParamUp);
                Assert.Equal("", blank.GateDescriptor);
                Assert.Equal(0, blank.ParamMin);
            }
            Assert.Equal((short)16383, Trigger()); // 0.25 from the mapped pad + 0.5 from Any Device.
            Assert.Equal((ushort)49151, GamepadOutput().LeftTrigger);
            _otherState.Buttons[1] = false;
            Assert.Equal((short)-16385, Trigger()); // The Any Device positive control has been released.

            var item = Hydrate(row.Target, MappingCategory.Triggers);
            Assert.True(item.PrimarySourceExists);
            Assert.Equal(4, item.PositionalSourceCount);
            Assert.Equal(4, item.VariableCount);
            Assert.Equal("", item.VariableALabel);
            Assert.Equal("", item.VariableBLabel);
            Assert.Contains("Button 0", item.VariableCLabel);
            Assert.Contains("Button 1", item.VariableDLabel);

            var formula = "s[2 + (a > 0 ? 1 : 0)] * 0.5";
            item.CombineExpression = formula;
            item = SaveReload(row.Target, MappingCategory.Triggers);
            Assert.Equal(formula, item.CombineExpression);
            Assert.Equal(4, item.PositionalSourceCount);
            Assert.Equal((short)-1, Trigger());

            _targetState.Buttons[2] = true;
            item.SelectedInput = new InputChoice
            {
                Descriptor = "Button 2", DeviceGuid = TargetPad.ToString(),
                DisplayName = "Button 2", DeviceLabel = "Destination",
            };
            item = SaveReload(row.Target, MappingCategory.Triggers);
            Assert.Equal(short.MinValue, Trigger()); // a selects s[3], whose button is up.
            item.ClearCommand.Execute(null);
            item = SaveReload(row.Target, MappingCategory.Triggers);
            Assert.Equal(4, item.PositionalSourceCount);
            Assert.Equal((short)-1, Trigger());
            Assert.Equal("Axis 2", row.Sources[0].Descriptor);
            Assert.Equal("Incremental", row.Sources[0].Kind);
            Assert.Equal("a + b + s[1 + 1] * 0.25 + d * 0.5", row.CombineExpression);
        }

        [Fact]
        public void NullAndDuplicatePositionsSurviveSanitizationAndReload()
        {
            var row = Row("ButtonA", "s[0] + s[1] + s[2] + s[3] > 1.5",
                null, Source(MissingA, "Button 0"), Source(SourcePad, "Button 0"), Source(SourcePad, "Button 0"));
            _targetState.Buttons[0] = true;
            Copy(row, clipboard: true);
            Hydrate(row.Target, MappingCategory.Buttons);
            var item = SaveReload(row.Target, MappingCategory.Buttons);
            Assert.Equal(4, item.PositionalSourceCount);
            item.ExtraSources[0] = null;
            item = SaveReload(row.Target, MappingCategory.Buttons);
            Assert.Equal(4, item.PositionalSourceCount);
            Assert.Equal(4, SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources.Count);
            Assert.True(InputManager.TryEvaluateMappingSetButton(_targetState,
                SettingsManager.SlotMappingSets[TargetSlot], TargetPad.ToString(), TargetSlot, row.Target, 50, out bool down));
            Assert.True(down);
            Assert.True(GamepadOutput().IsButtonPressed(Gamepad.A));
            _targetState.Buttons[0] = false;
            Assert.True(InputManager.TryEvaluateMappingSetButton(_targetState,
                SettingsManager.SlotMappingSets[TargetSlot], TargetPad.ToString(), TargetSlot, row.Target, 50, out down));
            Assert.False(down);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FoldedPairAndUnavailableModifierKeepTheirExpressionPositions(bool clipboard)
        {
            var modifier = new MappingSource { Kind = "InvertOnHold", DeviceGuid = MissingB.ToString(), ParamModifier = "Button 9" };
            var row = Row("LeftThumbAxisX", "a * 0.25 + s[1] * 0.5",
                Source(MissingA, "Button 4"), Source(MissingA, "Button 5", true), modifier,
                Source(SourcePad, "Button 0"));
            _targetState.Buttons[0] = true;
            _targetState.Buttons[9] = true;
            Copy(row, clipboard);
            var item = Hydrate(row.Target, MappingCategory.LeftStick);
            item = SaveReload(row.Target, MappingCategory.LeftStick);
            Assert.Equal(2, item.PositionalSourceCount);
            Assert.Equal(2, item.VariableCount);
            Assert.Equal(4, SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources.Count);
            Assert.Contains("Button 0", item.VariableBLabel);
            Assert.Equal((short)16383, Bipolar());
            Assert.Equal((short)16383, GamepadOutput().ThumbLX);
            Assert.Equal("", SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources[2].ParamModifier);
            item.CombineExpression = "c";
            Assert.True(item.IsCombineExpressionWarning);

            // Removing the mismatch restores the real pair and modifier.
            AddDevice(Guid.NewGuid(), MissingA, TargetSlot, RestState());
            var matching = SettingsManager.UserDevices.Items.Last();
            matching.InputState.Buttons[4] = true;
            Copy(row, clipboard);
            Assert.Equal((short)24575, Bipolar());
            matching.InputState.Buttons[4] = false;
            matching.InputState.Buttons[5] = true;
            Assert.Equal((short)8191, Bipolar());
        }

        [Theory]
        [InlineData("LeftThumbAxisX", false)]
        [InlineData("RawAxis0", false)]
        [InlineData("RawAxis2", true)]
        public void MissingPairKeepsDispatchCountAndReadsNeutralOnBothAxisLanes(string target, bool trigger)
        {
            var row = Row(target, "0.25 + a + b",
                Source(MissingA, "Axis 2"), Source(MissingA, "Axis 2", true));
            Copy(row, clipboard: true);
            Hydrate(target, trigger ? MappingCategory.Triggers : MappingCategory.LeftStick);
            var item = SaveReload(target, trigger ? MappingCategory.Triggers : MappingCategory.LeftStick);
            Assert.Equal(trigger ? 2 : 1, item.PositionalSourceCount);
            Assert.Equal(2, SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources.Count);
            Assert.Equal(trigger ? (short)-16385 : (short)8191, trigger ? Trigger(target) : Bipolar(target));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RetargetingOntoOneDeviceDoesNotCreateANewFoldedPair(bool clipboard)
        {
            var anotherSource = Guid.NewGuid();
            AddDevice(anotherSource, SharedProduct, SourceSlot, RestState());
            var row = Row("LeftThumbAxisX", "s[1] * 0.5",
                Source(SourcePad, "Button 0"), Source(anotherSource, "Button 1", true));
            _targetState.Buttons[1] = true;
            Copy(row, clipboard);
            var item = Hydrate(row.Target, MappingCategory.LeftStick);
            item = SaveReload(row.Target, MappingCategory.LeftStick);
            Assert.Equal(2, item.PositionalSourceCount);
            Assert.True(item.SuppressBipolarPair);
            Assert.Equal((short)-16383, Bipolar());
            Assert.Contains("Button 1", item.VariableBLabel);
            Assert.Equal(2, SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources.Count);
            Assert.DoesNotContain(SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources, s => s.Kind == "InvertOnHold");

            // Copy the already-retargeted row again through the actual clipboard DTO.
            var payload = new PadSetting { SlotMultiSourceRows = InputService.ExtractAllRowsForSlot(TargetSlot) };
            var parsed = PadSetting.FromJson(payload.ToJson(VirtualControllerType.Xbox, false), out _, out _);
            Assert.True(parsed.SlotMultiSourceRows[0].SuppressBipolarPair);
            InputService.ApplySlotMappingSetFromRows(TargetSlot, parsed.SlotMultiSourceRows);
            item = Hydrate(row.Target, MappingCategory.LeftStick);
            Assert.True(item.SuppressBipolarPair);
            item.ExtraSources[0].Descriptor = "Button 2";
            _targetState.Buttons[2] = true;
            item = SaveReload(row.Target, MappingCategory.LeftStick);
            Assert.True(item.SuppressBipolarPair);
            Assert.Equal((short)-16383, Bipolar());
            Assert.Contains("Button 2", item.VariableBLabel);

            // A deliberate change to a built-in combine restores its pair rule.
            _targetState.Buttons[0] = true;
            item.CombineMode = "MaxAbs";
            item = SaveReload(row.Target, MappingCategory.LeftStick);
            Assert.False(item.SuppressBipolarPair);
            Assert.Equal(1, item.PositionalSourceCount);
            Assert.Equal((short)0, Bipolar());
        }

        [Theory]
        [InlineData(false, false, 2, -8191)]
        [InlineData(true, false, 2, -24575)]
        [InlineData(false, true, 1, 0)]
        [InlineData(true, true, 2, -24575)]
        public void MatchedStatefulPrimaryUsesItsOwnPairIdentityInTheEditor(
            bool primaryInvert, bool sameDevice, int expectedCount, short expectedHeld)
        {
            var primary = Source(SourcePad, "", primaryInvert);
            primary.Kind = "Incremental";
            primary.ParamMin = primary.ParamMax = 1;
            var secondary = new MappingSource
            {
                DeviceGuid = sameDevice ? SourcePad.ToString() : "",
                Descriptor = "Button 1", Invert = true,
            };
            var row = Row("LeftThumbAxisX", "a * 0.25 + b * 0.5", primary, secondary);
            InputManager.GetSlotSourceKindRuntime(TargetSlot).Clear();
            Copy(row, clipboard: true);
            _targetState.Buttons[1] = true;
            Assert.Equal(expectedHeld, Bipolar());
            Assert.Equal(expectedHeld, GamepadOutput().ThumbLX);
            var item = Hydrate(row.Target, MappingCategory.LeftStick);
            for (int pass = 0; pass < 2; pass++)
            {
                Assert.Equal("", item.PrimarySourceDeviceGuid);
                Assert.Equal(TargetPad.ToString(), item.PrimaryKindSource.DeviceGuid);
                Assert.Equal(primaryInvert, item.PrimaryKindSource.Invert);
                Assert.Equal(expectedCount, item.PositionalSourceCount);
                Assert.Equal(expectedCount, item.VariableCount);
                Assert.Equal(expectedCount == 1, item.IsCombineExpressionWarning);
                if (expectedCount == 2) Assert.Contains("Button 1", item.VariableBLabel);
                else Assert.Equal("", item.VariableBLabel);
                item = SaveReload(row.Target, MappingCategory.LeftStick);
                Assert.Equal(expectedHeld, Bipolar());
            }
            _targetState.Buttons[1] = false;
            Assert.Equal(primaryInvert ? (short)-8191 : (short)8191, Bipolar());

            if (sameDevice && !primaryInvert)
            {
                var notifications = new List<string>();
                item.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
                item.PrimaryKindSource.Invert = true;
                Assert.Equal(2, item.VariableCount);
                Assert.Contains(nameof(MappingItem.VariableCount), notifications);
                Assert.Contains("Button 1", item.VariableBLabel);
                item.PrimaryKindSource.Invert = false;
                notifications.Clear();
                item.PrimaryKindSource.DeviceGuid = OtherPad.ToString();
                Assert.Equal(2, item.VariableCount);
                Assert.Contains(nameof(MappingItem.VariableCount), notifications);
            }
            Assert.Equal(SourcePad.ToString(), primary.DeviceGuid);
            Assert.Equal(primaryInvert, primary.Invert);
        }

        [Fact]
        public void MatchedPrimaryModifierStillConsumesNoExpressionArgument()
        {
            var modifier = new MappingSource
            {
                Kind = "InvertOnHold", DeviceGuid = SourcePad.ToString(), ParamModifier = "Button 9",
            };
            var row = Row("LeftThumbAxisX", "a * 0.5", modifier,
                new MappingSource { Descriptor = "Button 1", Invert = true });
            Copy(row, clipboard: true);
            var item = Hydrate(row.Target, MappingCategory.LeftStick);
            item = SaveReload(row.Target, MappingCategory.LeftStick);
            Assert.Equal("InvertOnHold", item.PrimaryKindSource.Kind);
            Assert.Equal(1, item.VariableCount);
            Assert.Contains("Button 1", item.VariableALabel);
            Assert.Equal("", item.VariableBLabel);
            _targetState.Buttons[1] = true;
            // One contributing source keeps the established scalar shortcut.
            Assert.Equal(short.MinValue, Bipolar());
            Assert.Equal((short)-32767, GamepadOutput().ThumbLX);
            _targetState.Buttons[9] = true;
            Assert.Equal(short.MaxValue, Bipolar());
            _targetState.Buttons[1] = false;
            Assert.Equal((short)0, Bipolar());
        }

        [Theory]
        [InlineData(true, -8191)]
        [InlineData(false, 24575)]
        public void RowModifiersCannotTakeEitherLegOfACustomBipolarPair(
            bool modifierFirst, short expectedBoth)
        {
            var modifier = Source(SourcePad, "Button 8", invert: !modifierFirst);
            modifier.Kind = "InvertOnHold";
            modifier.ParamModifier = "Button 9";
            var numeric = Source(SourcePad, "Button 1", invert: modifierFirst);
            var row = Row("LeftThumbAxisX", "a * 0.5 + b * 0.25",
                modifierFirst ? modifier : numeric,
                modifierFirst ? numeric : modifier,
                Source(SourcePad, "Button 2"));
            var copied = Copy(row, clipboard: true).Rows[0];
            _targetState.Buttons[1] = _targetState.Buttons[2] = true;
            // A modifier's descriptor must not become a folded numeric leg.
            _targetState.Buttons[8] = true;
            Assert.Equal(expectedBoth, Bipolar());
            Assert.Equal(expectedBoth, GamepadOutput().ThumbLX);
            Assert.False(InputManager.IsBipolarNegPair(copied.Sources[0], copied.Sources[1]));
            var item = Hydrate(row.Target, MappingCategory.LeftStick);
            item = SaveReload(row.Target, MappingCategory.LeftStick);
            Assert.Equal(3, SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources.Count);
            Assert.Equal(2, item.PositionalSourceCount);
            Assert.Equal(2, item.VariableCount);
            Assert.Contains("Button 1", item.VariableALabel);
            Assert.Contains("Button 2", item.VariableBLabel);
            Assert.Equal(expectedBoth, Bipolar());
            _targetState.Buttons[9] = true;
            Assert.Equal((short)-expectedBoth, Bipolar());
            _targetState.Buttons[9] = false;
            _targetState.Buttons[1] = false;
            Assert.Equal((short)8191, Bipolar());
            _targetState.Buttons[2] = false;
            _targetState.Buttons[9] = true;
            Assert.Equal((short)0, Bipolar());
        }

        [Theory]
        [InlineData("Direct")]
        [InlineData("Incremental")]
        [InlineData("Ramped")]
        public void NumericPrimaryKindsKeepTheirExistingBipolarPair(string kind)
        {
            var primary = Source(SourcePad, "Button 1");
            primary.Kind = kind;
            primary.ParamUp = "Button 1";
            primary.ParamMin = primary.ParamMax = 1;
            primary.ParamAttackTime = primary.ParamReleaseTime = 0;
            var row = Row("LeftThumbAxisX", "a * 0.5 + b * 0.25", primary,
                Source(SourcePad, "Button 2", true), Source(SourcePad, "Button 3"));
            InputManager.GetSlotSourceKindRuntime(TargetSlot).Clear();
            var copied = Copy(row, clipboard: true).Rows[0];
            Assert.True(InputManager.IsBipolarNegPair(copied.Sources[0], copied.Sources[1]));
            _targetState.Buttons[1] = _targetState.Buttons[3] = true;
            Assert.Equal((short)24575, Bipolar());
            var item = Hydrate(row.Target, MappingCategory.LeftStick);
            item = SaveReload(row.Target, MappingCategory.LeftStick);
            Assert.Equal(2, item.VariableCount);
            Assert.Contains("Button 3", item.VariableBLabel);
            Assert.Equal((short)24575, Bipolar());
            _targetState.Buttons[2] = true;
            InputManager.GetSlotSourceKindRuntime(TargetSlot).FrameSeq++;
            Assert.Equal((short)8191, Bipolar());
        }

        [Fact]
        public void BlankBeforeInvertedAnyDeviceSourceDoesNotFormAPair()
        {
            var row = Row("LeftThumbAxisX", "b * 0.5",
                Source(MissingA, "Button 0"), new MappingSource { Descriptor = "Button 1", Invert = true });
            _otherState.Buttons[1] = true;
            Copy(row, clipboard: false);
            Hydrate(row.Target, MappingCategory.LeftStick);
            var item = SaveReload(row.Target, MappingCategory.LeftStick);
            Assert.Equal(2, item.PositionalSourceCount);
            Assert.True(item.SuppressBipolarPair);
            Assert.Equal(2, SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources.Count);
            Assert.DoesNotContain(SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources, s => s.Kind == "InvertOnHold");
            Assert.Equal((short)-16383, Bipolar());
            _otherState.Buttons[1] = false;
            Assert.Equal((short)0, Bipolar());
        }

        [Fact]
        public void BlankTouchpadPositionsAreInactiveAndAllMissingSourcesHoldPosition()
        {
            var row = Row("TouchpadX1", "aD ? 1 : b * 0.25",
                Source(MissingA, "Button 0"), new MappingSource { Descriptor = "Button 0" });
            _targetState.Buttons[0] = true;
            Copy(row, clipboard: true);
            Hydrate(row.Target, MappingCategory.Touchpad);
            var item = SaveReload(row.Target, MappingCategory.Touchpad);
            Assert.Equal(2, item.PositionalSourceCount);
            Assert.True(InputManager.TryEvaluateMappingSetTouchpadAxis(_targetState,
                SettingsManager.SlotMappingSets[TargetSlot], TargetPad.ToString(), TargetSlot, row.Target, 0, out short value));
            Assert.Equal((short)8191, value);

            Copy(Row(row.Target, "1", Source(MissingA, "Button 0"), Source(MissingB, "Button 1")), clipboard: false);
            Hydrate(row.Target, MappingCategory.Touchpad);
            SaveReload(row.Target, MappingCategory.Touchpad);
            Assert.False(InputManager.TryEvaluateMappingSetTouchpadAxis(_targetState,
                SettingsManager.SlotMappingSets[TargetSlot], TargetPad.ToString(), TargetSlot, row.Target, 0, out _));
        }

        [Fact]
        public void BlankDirectReadsZeroButBoundInversionAndStatefulSourcesStillWork()
        {
            var blank = new MappingSource { Invert = true };
            Assert.Equal(0f, SourceEvaluator.EvaluateForTriggerTarget(_targetState, blank, TargetSlot, "LeftTrigger", 0, null, 0));
            Assert.Equal(0f, SourceEvaluator.EvaluateForBipolarAxisTarget(_targetState, blank, TargetSlot, "LeftThumbAxisX", 0, null, 0));
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(_targetState, blank, 50, TargetSlot, "ButtonA", 0, null, 0));

            blank.Descriptor = "Axis 2";
            Assert.Equal(1f, SourceEvaluator.EvaluateForTriggerTarget(_targetState, blank, TargetSlot, "LeftTrigger", 0, null, 0));
            var incremental = new MappingSource { Kind = "Incremental", ParamMin = 1, ParamMax = 1 };
            Assert.Equal(1f, SourceEvaluator.EvaluateForTriggerTarget(_targetState, incremental,
                TargetSlot, "LeftTrigger", 0, new SourceKindRuntime(), 0.1));
        }

        [Fact]
        public void SteeringDoesNotConvertBlankPositionsOrModifiersIntoInputs()
        {
            var row = Row("LeftThumbAxisX", "b",
                Source(MissingA, "Axis 0"), new MappingSource { Descriptor = "Axis 0" },
                new MappingSource { Kind = "InvertOnHold", ParamModifier = "Button 9" });
            Copy(row, clipboard: true);
            Hydrate(row.Target, MappingCategory.LeftStick);
            _vm.Pads[TargetSlot].Mappings.Add(new MappingItem("Y", "LeftThumbAxisY", MappingCategory.LeftStick)
            {
                SourceDescriptor = "Axis 1",
            });
            _vm.Pads[TargetSlot].StickConfigs[0].SteeringModeIndex = 2;
            _service.PushUiExtraSourcesIntoSlotMappingSets();
            var saved = SettingsManager.SlotMappingSets[TargetSlot].Rows[0];
            Assert.Equal("Direct", saved.Sources[0].Kind);
            Assert.Equal("", saved.Sources[0].Descriptor);
            Assert.NotEqual("Direct", saved.Sources[1].Kind);
            Assert.Equal("InvertOnHold", saved.Sources[2].Kind);
            Assert.Equal("Button 9", saved.Sources[2].ParamModifier);
            _targetState.Axis[0] = 65535;
            var runtime = new SourceKindRuntime();
            Assert.Equal(0f, SourceEvaluator.EvaluateForBipolarAxisTarget(_targetState, saved.Sources[0],
                TargetSlot, row.Target, 0, runtime, 0.1));
            Assert.True(SourceEvaluator.EvaluateForBipolarAxisTarget(_targetState, saved.Sources[1],
                TargetSlot, row.Target, 1, runtime, 0.1) > 0.5f);
        }

        [Fact]
        public void StoredNullPrimaryHydratesAndSavesAsANeutralPosition()
        {
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet
            {
                Rows = new List<MappingRow> { Row("ButtonA", "c", null, null, Source(TargetPad, "Button 0")) },
            };
            var item = Hydrate("ButtonA", MappingCategory.Buttons);
            Assert.True(item.PrimarySourceExists);
            Assert.Equal(3, item.PositionalSourceCount);
            item = SaveReload("ButtonA", MappingCategory.Buttons);
            Assert.Equal(3, item.PositionalSourceCount);
            _targetState.Buttons[0] = true;
            Assert.True(GamepadOutput().IsButtonPressed(Gamepad.A));
        }

        [Fact]
        public void DeviceScopedCopyRecomposesSelectedInputsAfterRetainedSources()
        {
            // Donor [A, B], copy only B, destination retains X: merge is [X, B].
            var row = Row("LeftTrigger", "a + b",
                Source(MissingA, "Button 7"), Source(SourcePad, "Button 0"));
            SettingsManager.SlotMappingSets[SourceSlot] = new MappingSet { Rows = new List<MappingRow> { row } };
            var payload = new PadSetting { DeviceScopedMultiSourceRows = InputService.ExtractDeviceScopedRowsForSlot(SourceSlot, SourcePad) };
            var parsed = PadSetting.FromJson(payload.ToJson(VirtualControllerType.Xbox, false), out _, out _);
            Assert.Single(Assert.Single(parsed.DeviceScopedMultiSourceRows).Sources);
            Assert.Empty(InputService.ExtractDeviceScopedRowsForSlot(SourceSlot, Guid.NewGuid()));
            var retained = Source(OtherPad, "Button 9");
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet
            {
                Rows = new List<MappingRow> { Row(row.Target, "a", retained, Source(TargetPad, "Button 5")) },
            };
            for (int repeat = 0; repeat < 2; repeat++)
            {
                InputService.ApplyMultiSourceRowsToCurrentDevice(TargetSlot, TargetPad, parsed.DeviceScopedMultiSourceRows);
                Hydrate(row.Target, MappingCategory.Triggers);
                var item = SaveReload(row.Target, MappingCategory.Triggers);
                Assert.Equal(2, item.PositionalSourceCount);
                var saved = SettingsManager.SlotMappingSets[TargetSlot].Rows[0];
                Assert.Equal("a + b", saved.CombineExpression);
                Assert.Equal(2, saved.Sources.Count);
                Assert.Equal(OtherPad.ToString(), saved.Sources[0].DeviceGuid);
                Assert.Equal("Button 9", saved.Sources[0].Descriptor);
                Assert.Equal(TargetPad.ToString(), saved.Sources[1].DeviceGuid);
                Assert.Equal("Button 0", saved.Sources[1].Descriptor);
                _targetState.Buttons[0] = false;
                _otherState.Buttons[9] = true;
                Assert.Equal(short.MaxValue, Trigger());
                _targetState.Buttons[0] = true;
                _otherState.Buttons[9] = false;
                Assert.Equal(short.MaxValue, Trigger());
                _targetState.Buttons[0] = false;
                Assert.Equal(short.MinValue, Trigger());
            }
            var edited = Hydrate(row.Target, MappingCategory.Triggers);
            edited.CombineExpression = "a";
            SaveReload(row.Target, MappingCategory.Triggers);
            _otherState.Buttons[9] = true;
            _targetState.Buttons[0] = false;
            Assert.Equal(short.MaxValue, Trigger());
            Assert.Equal("Button 7", row.Sources[0].Descriptor);
            Assert.Equal("a + b", row.CombineExpression);
        }

        [Fact]
        public void DeviceScopedMergeKeepsAuthoredBlankAndModifierPositions()
        {
            var donor = Row("LeftTrigger", "c", Source(SourcePad, "Button 0"));
            var blank = new MappingSource { DeviceGuid = OtherPad.ToString() };
            var modifier = new MappingSource { Kind = "InvertOnHold", DeviceGuid = OtherPad.ToString() };
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet
            {
                Rows = new List<MappingRow> { Row(donor.Target, "a", Source(OtherPad, "Button 9"),
                    blank, Source(TargetPad, "Button 5"), modifier) },
            };
            for (int repeat = 0; repeat < 2; repeat++)
            {
                InputService.ApplyMultiSourceRowsToCurrentDevice(TargetSlot, TargetPad, new[] { donor });
                var sources = SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources;
                Assert.Equal(4, sources.Count);
                Assert.Equal("", sources[1].Descriptor);
                Assert.Equal("InvertOnHold", sources[2].Kind);
                Assert.Equal(OtherPad.ToString(), sources[2].DeviceGuid);
                Hydrate(donor.Target, MappingCategory.Triggers);
                var item = SaveReload(donor.Target, MappingCategory.Triggers);
                Assert.Equal(3, item.PositionalSourceCount);
                _targetState.Buttons[0] = true;
                Assert.Equal(short.MaxValue, Trigger());
                _targetState.Buttons[0] = false;
                _otherState.Buttons[9] = true;
                Assert.Equal(short.MinValue, Trigger());
            }
        }

        [Fact]
        public void PartialExtractionDoesNotTransferSuppressionToANewPrefix()
        {
            var row = Row("LeftThumbAxisX", "a", Source(MissingA, "Button 9"),
                Source(SourcePad, "Button 0"), Source(SourcePad, "Button 1", true));
            row.SuppressBipolarPair = true;
            SettingsManager.SlotMappingSets[SourceSlot] = new MappingSet { Rows = new List<MappingRow> { row } };
            var slice = InputService.ExtractDeviceScopedRowsForSlot(SourceSlot, SourcePad);
            Assert.Equal(2, Assert.Single(slice).Sources.Count);
            Assert.False(slice[0].SuppressBipolarPair);
            InputService.ApplyMultiSourceRowsToCurrentDevice(TargetSlot, TargetPad, slice);
            Hydrate(row.Target, MappingCategory.LeftStick);
            SaveReload(row.Target, MappingCategory.LeftStick);
            _targetState.Buttons[0] = _targetState.Buttons[1] = true;
            Assert.Equal((short)0, Bipolar());
            _targetState.Buttons[1] = false;
            Assert.Equal(short.MaxValue, Bipolar());
        }

        [Theory]
        [InlineData(false, "Custom", false)]
        [InlineData(true, "Custom", true)]
        [InlineData(true, "MaxAbs", false)]
        public void PartialMergeUsesOnlyTheSurvivingPrefixMetadata(bool previousFlag, string previousMode, bool expectedFlag)
        {
            var donor = Row("LeftThumbAxisX", "b", Source(SourcePad, "Button 0"), Source(SourcePad, "Button 1", true));
            donor.SuppressBipolarPair = true;
            var destination = Row(donor.Target, "a", Source(OtherPad, "Button 2"), Source(OtherPad, "Button 3", true));
            destination.SuppressBipolarPair = previousFlag;
            destination.CombineMode = previousMode;
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet { Rows = new List<MappingRow> { destination } };
            InputService.ApplyMultiSourceRowsToCurrentDevice(TargetSlot, TargetPad, new[] { donor });
            Assert.Equal(expectedFlag, destination.SuppressBipolarPair);
            Assert.Equal(4, destination.Sources.Count);
            Hydrate(donor.Target, MappingCategory.LeftStick);
            SaveReload(donor.Target, MappingCategory.LeftStick);
            _otherState.Buttons[2] = _otherState.Buttons[3] = true;
            _targetState.Buttons[0] = true;
            Assert.Equal(expectedFlag ? short.MinValue : short.MaxValue, Bipolar());
        }

        [Fact]
        public void RemovingTheOldPrefixClearsItsSuppressionDuringPartialMerge()
        {
            var donor = Row("LeftThumbAxisX", "a", Source(SourcePad, "Button 0"));
            var destination = Row(donor.Target, "a", Source(TargetPad, "Button 7"),
                Source(OtherPad, "Button 2"), Source(OtherPad, "Button 3", true));
            destination.SuppressBipolarPair = true;
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet { Rows = new List<MappingRow> { destination } };
            InputService.ApplyMultiSourceRowsToCurrentDevice(TargetSlot, TargetPad, new[] { donor });
            Assert.False(destination.SuppressBipolarPair);
            _otherState.Buttons[2] = _otherState.Buttons[3] = true;
            Assert.Equal((short)0, Bipolar());
            _otherState.Buttons[3] = false;
            Assert.Equal(short.MaxValue, Bipolar());
        }

        [Fact]
        public void NonCustomCopyStillDropsMissingSourcesAndDeduplicates()
        {
            var row = Row("ButtonA", "", Source(MissingA, "Button 1"),
                Source(SourcePad, "Button 0"), Source(SourcePad, "Button 0"));
            row.CombineMode = "OR";
            var copied = Copy(row, clipboard: true);
            Assert.Equal(2, copied.Rows[0].Sources.Count);
            SettingsService.SanitizeMappingSet(copied, TargetSlot);
            Assert.Single(copied.Rows[0].Sources);
            _targetState.Buttons[0] = true;
            Assert.True(InputManager.TryEvaluateMappingSetButton(_targetState, copied,
                TargetPad.ToString(), TargetSlot, row.Target, 50, out bool value));
            Assert.True(value);
        }

        [Fact]
        public void DeviceScopedClipboardRetainsAnAlreadySuppressedPair()
        {
            var row = Row("LeftThumbAxisX", "b * 0.5", Source(SourcePad, "Button 0"), Source(SourcePad, "Button 1", true));
            row.SuppressBipolarPair = true;
            SettingsManager.SlotMappingSets[SourceSlot] = new MappingSet { Rows = new List<MappingRow> { row } };
            var payload = new PadSetting { DeviceScopedMultiSourceRows = InputService.ExtractDeviceScopedRowsForSlot(SourceSlot, SourcePad) };
            var parsed = PadSetting.FromJson(payload.ToJson(VirtualControllerType.Xbox, false), out _, out _);
            Assert.True(parsed.DeviceScopedMultiSourceRows[0].SuppressBipolarPair);
            InputService.ApplyMultiSourceRowsToCurrentDevice(TargetSlot, TargetPad, parsed.DeviceScopedMultiSourceRows);
            Hydrate(row.Target, MappingCategory.LeftStick);
            var item = SaveReload(row.Target, MappingCategory.LeftStick);
            Assert.True(item.SuppressBipolarPair);
            Assert.Equal(2, item.PositionalSourceCount);
            Assert.Equal(2, SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources.Count);
            _targetState.Buttons[1] = true;
            Assert.Equal((short)-16383, Bipolar());
            Assert.True(row.SuppressBipolarPair);
        }

        [Fact]
        public void PairSuppressionDefaultsOffAndIsIgnoredOutsideCustom()
        {
            var serializer = new XmlSerializer(typeof(MappingRow));
            using var reader = new StringReader("<MappingRow Target=\"LeftThumbAxisX\" CombineMode=\"Custom\" />");
            var legacy = (MappingRow)serializer.Deserialize(reader);
            Assert.False(legacy.SuppressBipolarPair);

            var row = Row("LeftThumbAxisX", "b", Source(TargetPad, "Button 0"), Source(TargetPad, "Button 1", true));
            row.SuppressBipolarPair = true;
            row.CombineMode = "MaxAbs";
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet { Rows = new List<MappingRow> { row } };
            Assert.True(InputService.CloneMappingSetDeep(SettingsManager.SlotMappingSets[TargetSlot]).Rows[0].SuppressBipolarPair);
            _targetState.Buttons[0] = _targetState.Buttons[1] = true;
            Assert.Equal((short)0, Bipolar());
            var item = Hydrate(row.Target, MappingCategory.LeftStick);
            Assert.Equal(1, item.PositionalSourceCount);

            row.CombineMode = "Custom";
            Assert.Equal(short.MinValue, Bipolar());
            Assert.Equal((short)-32767, GamepadOutput().ThumbLX);
        }

        [Fact]
        public void EditorWriterCarriesSuppressionIntoANewRow()
        {
            var row = Row("LeftThumbAxisX", "b * 0.5", Source(TargetPad, "Button 0"), Source(TargetPad, "Button 1", true));
            row.SuppressBipolarPair = true;
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet { Rows = new List<MappingRow> { row } };
            var item = Hydrate(row.Target, MappingCategory.LeftStick);
            Assert.True(item.SuppressBipolarPair);
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet();
            _targetState.Buttons[1] = true;
            item = SaveReload(row.Target, MappingCategory.LeftStick);
            Assert.True(item.SuppressBipolarPair);
            Assert.Equal((short)-16383, Bipolar());
        }

        [Fact]
        public void LayerCopyAndPasteCarrySuppressionWithoutAddingSources()
        {
            var row = Row("LeftThumbAxisX", "b", Source(TargetPad, "Button 0"), Source(TargetPad, "Button 1", true));
            row.SuppressBipolarPair = true;
            var clipboard = PadForge.Views.PadPage.CloneLayerRow(row, "Shift1");
            var pasted = PadForge.Views.PadPage.CloneLayerRow(clipboard, "Base");
            Assert.True(pasted.SuppressBipolarPair);
            Assert.Equal(2, pasted.Sources.Count);
            Assert.NotSame(row.Sources[0], clipboard.Sources[0]);
            Assert.NotSame(clipboard.Sources[0], pasted.Sources[0]);
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet { Rows = new List<MappingRow> { pasted } };
            _targetState.Buttons[1] = true;
            Assert.Equal(short.MinValue, Bipolar());
        }

        [Fact]
        public void NonCustomDevicePasteKeepsAnExistingNullContribution()
        {
            var incoming = Row("LeftTrigger", "", Source(SourcePad, "Button 0"));
            incoming.CombineMode = "Average";
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet
            {
                Rows = new List<MappingRow> { Row("LeftTrigger", "", null, Source(TargetPad, "Button 9")) },
            };
            _targetState.Buttons[0] = true;
            InputService.ApplyMultiSourceRowsToCurrentDevice(TargetSlot, TargetPad, new[] { incoming });
            Assert.Null(SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources[0]);
            Assert.Equal((short)-1, Trigger());
        }

        [Fact]
        public void AllMissingCustomValuesStayZeroWhileNonCustomRowsStayEmpty()
        {
            var row = Row("LeftTrigger", "a + s[1]",
                Source(MissingA, "Axis 2", true), Source(MissingB, "Button 1"));
            _targetState.Buttons[1] = true;
            Copy(row, clipboard: true);
            Hydrate(row.Target, MappingCategory.Triggers);
            var item = SaveReload(row.Target, MappingCategory.Triggers);
            Assert.Equal(2, item.PositionalSourceCount);
            Assert.Equal(short.MinValue, Trigger());

            row.CombineMode = "MaxAbs";
            Copy(row, clipboard: true);
            Assert.Empty(SettingsManager.SlotMappingSets[TargetSlot].Rows[0].Sources);
            Assert.False(InputManager.TryEvaluateMappingSetRawTrigger(_targetState,
                SettingsManager.SlotMappingSets[TargetSlot], TargetPad.ToString(), TargetSlot, row.Target, out _));
        }

        [Fact]
        public void LoadingAnEmptyRowClearsTheTransientPrimaryPosition()
        {
            Copy(Row("ButtonA", "b", Source(MissingA, "Button 1"), Source(SourcePad, "Button 0")), clipboard: false);
            var item = Hydrate("ButtonA", MappingCategory.Buttons);
            Assert.True(item.PrimarySourceExists);
            SettingsManager.SlotMappingSets[TargetSlot] = new MappingSet();
            InputService.RefreshMappingsToViewModel(_vm.Pads[TargetSlot]);
            Assert.False(item.PrimarySourceExists);
            Assert.Equal(0, item.PositionalSourceCount);
            Assert.Equal(0, item.VariableCount);
            Assert.Equal("", item.VariableALabel);
        }
    }
}
