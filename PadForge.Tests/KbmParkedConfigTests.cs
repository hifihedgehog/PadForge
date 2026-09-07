using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

public partial class DeviceUnassignConfigLifecycleTests
{
    [Theory]
    [InlineData("MouseOnly")]
    [InlineData("KeyboardOnly")]
    public void ParkedKbmConfigurationSurvivesBuilderXmlLoadAndTypeSwitch(string surfaces)
    {
        var (vm, _, _) = Arrange();
        var settings = new SettingsService(vm);
        var pad = vm.Pads[0];
        pad.OutputType = VirtualControllerType.KeyboardMouse;
        pad.KbmConfig.Surfaces = surfaces;
        pad.KbmConfig.SocdMode = "LastWins";
        pad.KbmConfig.SocdPairs = "65:68";
        pad.OutputType = VirtualControllerType.Xbox;
        Assert.Equal(surfaces, pad.KbmConfig.Surfaces);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var data = (KbmSlotConfigData[])typeof(SettingsService).GetMethod("BuildKbmConfigs", flags)!.Invoke(settings, null)!;
        Assert.Equal(surfaces, data.Single(x => x.SlotIndex == 0).Surfaces);

        var serializer = new XmlSerializer(typeof(KbmSlotConfigData[]));
        using var xml = new StringWriter();
        serializer.Serialize(xml, data);
        using var reader = new StringReader(xml.ToString());
        var loaded = (KbmSlotConfigData[])serializer.Deserialize(reader)!;
        var restoredVm = new MainViewModel();
        var restored = new SettingsService(restoredVm);
        var anchor = restoredVm.Pads[0].KbmConfig;
        Assert.Equal(VirtualControllerType.Xbox, restoredVm.Pads[0].OutputType);
        Assert.Equal("Both", anchor.Surfaces);
        typeof(SettingsService).GetMethod("ApplyKbmConfigs", flags)!.Invoke(restored, new object[] { loaded });
        Assert.Same(anchor, restoredVm.Pads[0].KbmConfig);
        Assert.Equal(surfaces, anchor.Surfaces);
        Assert.Equal("LastWins", anchor.SocdMode);
        Assert.Equal("65:68", anchor.SocdPairs);
        restoredVm.Pads[0].OutputType = VirtualControllerType.KeyboardMouse;
        Assert.Equal(surfaces, restoredVm.Pads[0].KbmSurfaces);
    }

    [Fact]
    public void KbmLoadSkipsDeletedAndOutOfRangeSlots()
    {
        var (vm, _, _) = Arrange();
        var settings = new SettingsService(vm);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var load = typeof(SettingsService).GetMethod("ApplyKbmConfigs", flags)!;
        var valid = vm.Pads[0].KbmConfig;
        var deleted = vm.Pads[1].KbmConfig;
        Assert.True(SettingsManager.SlotCreated[0]);
        Assert.False(SettingsManager.SlotCreated[1]);
        load.Invoke(settings, new object[] { new[]
        {
            new KbmSlotConfigData { SlotIndex = 0, Surfaces = "MouseOnly" },
            new KbmSlotConfigData { SlotIndex = 1, Surfaces = "KeyboardOnly" },
            new KbmSlotConfigData { SlotIndex = -1, Surfaces = "KeyboardOnly" },
            new KbmSlotConfigData { SlotIndex = InputManager.MaxPads, Surfaces = "KeyboardOnly" }
        }});
        Assert.Equal("MouseOnly", valid.Surfaces);
        Assert.Equal("Both", deleted.Surfaces);
        load.Invoke(settings, new object[] { null });
        Assert.Equal("MouseOnly", valid.Surfaces);
        Assert.Equal("Both", deleted.Surfaces);
    }
}
