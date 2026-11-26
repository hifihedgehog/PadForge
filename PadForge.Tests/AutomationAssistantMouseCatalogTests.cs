using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Catalog;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A mapping row's mouse settings as the catalog gives them (#600 item
    /// 40): each says what it takes, a number with the row's bounds, a
    /// choice with its choices, so find and read give their kind, range
    /// and choices as every other setting's.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class AutomationAssistantMouseCatalogTests : IDisposable
    {
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly Guid _mouse = Guid.NewGuid();
        private readonly Guid _spinner = Guid.NewGuid();

        public AutomationAssistantMouseCatalogTests()
        {
            var devices = new DeviceCollection();
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = _mouse, ProductGuid = _mouse, InstanceName = "Test mouse", ProductName = "Test mouse",
                CapType = InputDeviceType.Mouse, IsOnline = true, IsEnabled = true,
            });
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = _spinner, ProductGuid = _spinner, InstanceName = "Spinner", ProductName = "Spinner",
                CapType = InputDeviceType.Joystick, IsOnline = true, IsEnabled = true,
            });
            SettingsManager.UserDevices = devices;
        }

        public void Dispose() => SettingsManager.UserDevices = _savedDevices;

        [Fact]
        public void ARowsMouseSettings_SayWhatTheyTake_WithTheRowsBoundsAndChoices()
        {
            var pad = new PadViewModel(0);
            var travel = pad.Mappings.First(m => m.TargetSettingName == "LeftThumbAxisX");
            travel.LoadDescriptor("Mouse Travel X");
            travel.PrimarySourceDeviceGuid = _mouse.ToString();
            var spinner = pad.Mappings.First(m => m.TargetSettingName == "RightThumbAxisX");
            spinner.LoadDescriptor("Axis 3");
            spinner.PrimarySourceDeviceGuid = _spinner.ToString();
            var items = new List<LiveItem>();
            MouseSteeringCatalog.AddPad(items, pad, 1);
            string prefix = "row/c1/" + pad.Mappings.IndexOf(travel) + "/mouse/";

            var lockItem = items.Single(i => i.Id == prefix + "lock");
            Assert.Equal("number", lockItem.Takes);
            Assert.Equal(MappingSource.MinTravelCounts, lockItem.Minimum);
            Assert.Equal(MappingSource.MaxTravelCounts, lockItem.Maximum);
            // Past the bounds is refused, where the row's setter took it in.
            Assert.Throws<ArgumentException>(() => lockItem.Write("60000"));
            Assert.Equal(MappingSource.DefaultTravelCounts, travel.ParamTravelCounts);

            var smoothing = items.Single(i => i.Id == prefix + "smoothing");
            Assert.Equal("number", smoothing.Takes);
            Assert.Equal(0, smoothing.Minimum);
            Assert.Equal(MappingSource.MaxTravelSmoothingMs, smoothing.Maximum);

            var centering = items.Single(i => i.Id == prefix + "centering");
            Assert.Equal("choice", centering.Takes);
            Assert.Equal(MouseTravelLine.CenteringChoices().Select(c => c.Display), centering.Choices());

            var relative = items.Single(i => i.Id == "row/c1/" + pad.Mappings.IndexOf(spinner) + "/mouse/relative");
            Assert.Equal("switch", relative.Takes);
        }
    }
}
