using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The poll thread reads a slot's rows without a lock, so the mapping
    /// push never changes a source a row has published. The steering stamp
    /// wrote the steered kind, then the stick's axes, onto sources the row
    /// already held, so a pass could read a steered axis as a plain Direct
    /// read or as a winding stick with no Y axis. Each row now takes a
    /// finished list in one reference assignment.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SteeringStampPublishTests : IDisposable
    {
        private const int Slot = 1;
        private static readonly Guid Pad = new("c3010001-1111-2222-3333-444444444444");

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
        private readonly bool _stale = InputService.VmMappingsStale;
        private readonly bool _suppressPush = InputService.SuppressMappingEditPush;
        private readonly Action _afterRefresh = SettingsService.AfterMappingSetsRefreshed;

        private readonly MainViewModel _vm;
        private readonly SettingsService _service;

        public SteeringStampPublishTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated);
            Array.Clear(SettingsManager.SlotEnabled);
            SettingsManager.SlotCreated[Slot] = true;
            SettingsManager.SlotEnabled[Slot] = true;
            InputService.VmMappingsStale = false;
            InputService.SuppressMappingEditPush = false;
            SettingsService.AfterMappingSetsRefreshed = null;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            _vm = new MainViewModel();
            _service = new SettingsService(_vm);

            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            Array.Fill(state.Povs, -1);
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = Pad, ProductGuid = Pad, IsOnline = true,
                ProductName = Pad.ToString(), InstanceName = Pad.ToString(),
                CapType = InputDeviceType.Gamepad, InputState = state,
            });
            var setting = new UserSetting { InstanceGuid = Pad, ProductGuid = Pad, MapTo = Slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
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

        private static MappingRow Row(string target, string descriptor, string layer = "Base")
            => new() { Target = target, LayerMask = layer,
                Sources = new List<MappingSource> { new() { Descriptor = descriptor } } };

        /// <summary>The slot's two left stick rows on Base, the editor
        /// hydrated from them, and the left stick steered as a winding
        /// stick.</summary>
        private PadViewModel SteeredLeftStick()
        {
            SettingsManager.SlotMappingSets[Slot] = new MappingSet
            {
                Rows = new List<MappingRow> { Row("LeftThumbAxisX", "Axis 0"), Row("LeftThumbAxisY", "Axis 1") },
            };
            var pad = _vm.Pads[Slot];
            pad.Mappings.Clear();
            pad.Mappings.Add(new MappingItem("X", "LeftThumbAxisX", MappingCategory.LeftStick));
            pad.Mappings.Add(new MappingItem("Y", "LeftThumbAxisY", MappingCategory.LeftStick));
            InputService.RefreshMappingsToViewModel(pad);
            Assert.True(pad.MappingsViewLoaded);
            Assert.Equal("Axis 0", pad.Mappings[0].SourceDescriptor);
            pad.StickConfigs[0].SteeringModeIndex = 1;
            return pad;
        }

        private static MappingRow BaseRow(string target)
            => SettingsManager.SlotMappingSets[Slot].Rows.Single(r => r.LayerMask == "Base" && r.Target == target);

        [Fact]
        public void AuthoringBase_PublishesTheSteeredSourceStamped()
        {
            SteeredLeftStick();
            _service.PushUiExtraSourcesIntoSlotMappingSets();
            var src = BaseRow("LeftThumbAxisX").Sources[0];
            Assert.Equal("WindingStick", src.Kind);
            Assert.Equal("Axis 0", src.Descriptor);
            Assert.Equal("Axis 1", src.ParamYDescriptor);
        }

        /// <summary>Authoring a shift layer re-stamps Base's steered row.
        /// The sources Base held before the push keep what they held, and
        /// the row holds a new list with the stamp on it.</summary>
        [Fact]
        public void AuthoringAShiftLayer_NeverChangesASourceBaseHasPublished()
        {
            var pad = SteeredLeftStick();
            var baseRow = BaseRow("LeftThumbAxisX");
            var published = baseRow.Sources;
            var held = published[0];
            Assert.Equal("Direct", held.Kind);

            pad.ActiveLayerMask = "Layer_1";
            _service.PushUiExtraSourcesIntoSlotMappingSets();

            Assert.Equal("Direct", held.Kind);
            Assert.Equal("Axis 0", held.Descriptor);
            Assert.Equal("", held.ParamYDescriptor ?? "");
            Assert.Same(held, published[0]);
            Assert.NotSame(published, baseRow.Sources);
            var now = baseRow.Sources[0];
            Assert.Equal("WindingStick", now.Kind);
            Assert.Equal("Axis 0", now.Descriptor);
            Assert.Equal("Axis 1", now.ParamYDescriptor);
        }
    }
}
