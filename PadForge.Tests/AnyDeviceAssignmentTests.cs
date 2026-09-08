using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Tests;

public partial class DeviceUnassignConfigLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AssigningHeadTrackerPreservesEveryAnyDeviceRow(bool assignmentSetter, bool logicalInputs)
    {
        var oldHook = SettingsService.AfterMappingSetsRefreshed;
        try
        {
            var (vm, input, devices) = Arrange();
            var gamepad = SettingsManager.FindDeviceByInstanceGuid(PadGuid);
            var setting = SettingsManager.FindSettingByInstanceGuidAndSlot(PadGuid, 0);
            setting.SetPadSetting(SettingsManager.CreateDefaultPadSetting(gamepad, VirtualControllerType.Xbox));
            if (logicalInputs)
            {
                var pad = setting.GetPadSetting();
                pad.ButtonA = "Gamepad A";
                pad.ButtonB = "Gamepad B";
                pad.ButtonX = "Gamepad X";
                pad.ButtonY = "Gamepad Y";
            }
            SettingsService.RefreshMappingSetsFromLegacy();
            var rows = SettingsManager.SlotMappingSets[0].Rows;
            Assert.True(rows.Count >= 10);
            foreach (var row in rows)
            {
                var source = Assert.Single(row.Sources);
                source.DeviceGuid = "";
            }
            var before = rows.ToDictionary(row => row.Target, row => row.Sources[0].Descriptor);
            InputService.RefreshMappingsToViewModel(vm.Pads[0]);

            using var tracker = new HeadTrackerDevice(4242, false, 0, () => 1000);
            tracker.AttachForTest();
            var tracked = new UserDevice();
            tracked.LoadFromExternalDevice(tracker);
            tracked.IsOnline = true;
            SettingsManager.UserDevices.Items.Add(tracked);
            input.RefreshDeviceList();

            if (assignmentSetter)
                Assert.True(devices.SetDeviceSlotAssignment(tracked.InstanceGuid, 0, true));
            else
                devices.AssignDeviceToSlot(tracked.InstanceGuid, 0);

            Assert.NotNull(SettingsManager.FindSettingByInstanceGuidAndSlot(tracked.InstanceGuid, 0));
            foreach (var entry in before)
            {
                var row = SettingsManager.SlotMappingSets[0].Rows.Single(row => row.Target == entry.Key);
                var source = Assert.Single(row.Sources);
                Assert.Equal("", source.DeviceGuid);
                Assert.Equal(entry.Value, source.Descriptor);
            }
            SettingsService.RefreshMappingSetsFromLegacy();
            Assert.All(SettingsManager.SlotMappingSets[0].Rows, row => Assert.Single(row.Sources));

            // A separate fresh slot still receives the assigned gamepad's defaults.
            devices.AssignDeviceToSlot(PadGuid, 1);
            Assert.Contains(SettingsManager.SlotMappingSets[1].Rows,
                row => row.Target == "ButtonA" && row.Sources.Any(source => source.DeviceGuid == PadGuid.ToString()));
        }
        finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyDevicePreservationKeepsExplicitExtrasAndDoesNotMistakeModifiersForInputs(bool modifier)
    {
        var oldHook = SettingsService.AfterMappingSetsRefreshed;
        try
        {
            Arrange();
            var setting = SettingsManager.FindSettingByInstanceGuidAndSlot(PadGuid, 0);
            setting.GetPadSetting().ButtonA = "Gamepad A";
            var any = new MappingSource
            {
                Kind = modifier ? "InvertOnHold" : "Direct", DeviceGuid = "", Descriptor = "Gamepad A",
                Invert = true, DeadZone = 37,
            };
            var authored = new MappingSource { DeviceGuid = PadGuid.ToString(), Descriptor = "Gamepad B", Invert = true };
            var row = new MappingRow { Target = "ButtonA", Sources = new() { any } };
            if (!modifier) row.Sources.Add(authored);
            SettingsManager.SlotMappingSets[0] = new MappingSet { Rows = new() { row } };
            SettingsService.RefreshMappingSetsFromLegacy();
            var after = SettingsManager.SlotMappingSets[0].Rows.Single(r => r.Target == "ButtonA");
            Assert.Same(any, after.Sources[0]);
            Assert.Equal(37, any.DeadZone);
            Assert.True(any.Invert);
            Assert.Equal(2, after.Sources.Count);
            if (modifier)
                Assert.Contains(after.Sources, source => source.DeviceGuid == PadGuid.ToString() && source.Descriptor == "Gamepad A");
            else
                Assert.Same(authored, after.Sources[1]);
        }
        finally { SettingsService.AfterMappingSetsRefreshed = oldHook; }
    }
}
