using System;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// The Gyro Tilt card's range and deadzone (#292) ride the lanes InputService
/// runs when the assigned-device dropdown changes, as Motion Lean's settings
/// do. Without them the card kept the previous device's values, and the next
/// save wrote them onto the device now selected.
/// </summary>
[Collection("SettingsManagerStatics")]
public class GyroTiltDeviceSwitchTests
{
    [Fact]
    public void TheTiltCardLoadsTheSelectedDevicesValues()
    {
        var vm = new MainViewModel().Pads[0];
        var tilted = new PadSetting();
        tilted.SetRawMapping("GyroTiltRange", "40");
        tilted.SetRawMapping("GyroTiltInner", "5");
        InputService.LoadPadSettingIntoViewModel(vm, tilted);
        Assert.Equal(40, vm.GyroTiltRangeDeg);
        Assert.Equal(5, vm.GyroTiltInnerDz);
        // A device that never set them shows the defaults, not the last
        // device's values.
        InputService.LoadPadSettingIntoViewModel(vm, new PadSetting());
        Assert.Equal(25, vm.GyroTiltRangeDeg);
        Assert.Equal(0, vm.GyroTiltInnerDz);
    }

    [Fact]
    public void TheTiltCardSavesToTheSelectedDevice()
    {
        var devices = SettingsManager.UserDevices;
        var settings = SettingsManager.UserSettings;
        try
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), IsOnline = true, HasGyro = true, HasAccel = true };
            SettingsManager.UserDevices.Items.Add(ud);
            var assignment = new UserSetting { InstanceGuid = ud.InstanceGuid, MapTo = 0 };
            assignment.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(assignment);

            var main = new MainViewModel();
            var service = new InputService(main);
            var vm = main.Pads[0];
            vm.GyroTiltRangeDeg = 60;
            vm.GyroTiltInnerDz = 12;
            typeof(InputService).GetMethod("SaveViewModelToPadSetting", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(service, new object[] { vm, ud.InstanceGuid, false });
            Assert.Equal("60", assignment.GetPadSetting().GetRawMapping("GyroTiltRange"));
            Assert.Equal("12", assignment.GetPadSetting().GetRawMapping("GyroTiltInner"));
        }
        finally
        {
            SettingsManager.UserDevices = devices;
            SettingsManager.UserSettings = settings;
        }
    }
}
