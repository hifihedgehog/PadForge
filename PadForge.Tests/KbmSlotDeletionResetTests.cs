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
    [Collection("SettingsManagerStatics")]
    public class KbmSlotDeletionResetTests
    {
        [Fact]
        public void DeletingTheLastSlotResetsKbmInPlaceBeforeRecreation()
        {
            var settings = SettingsManager.UserSettings;
            var devices = SettingsManager.UserDevices;
            var sets = SettingsManager.SlotMappingSets;
            var profiles = SettingsManager.Profiles;
            var created = SettingsManager.SlotCreated;
            var enabled = SettingsManager.SlotEnabled;
            var afterRefresh = SettingsService.AfterMappingSetsRefreshed;
            var orders = Enum.GetValues<VirtualControllerType>()
                .ToDictionary(t => t, t => SettingsManager.SlotOrders.GetOrderFor(t).ToArray());
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.Profiles = new List<ProfileData>();
                SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
                SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
                foreach (var type in orders.Keys) SettingsManager.SlotOrders.GetOrderFor(type).Clear();
                var vm = new MainViewModel();
                var service = new SettingsService(vm);
                var devicesService = new DeviceService(vm, service);
                int surviving = devicesService.CreateSlot(VirtualControllerType.KeyboardMouse);
                int deleted = devicesService.CreateSlot(VirtualControllerType.KeyboardMouse);
                Assert.Equal(0, surviving);
                Assert.Equal(1, deleted);
                vm.Pads[surviving].KbmSurfaces = "KeyboardOnly";
                var pad = vm.Pads[deleted];
                var config = pad.KbmConfig;
                pad.KbmSurfaces = "MouseOnly";
                config.SocdMode = "Neutral";
                config.SocdPairs = "65:68";
                int surfaceNotifications = 0;
                pad.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PadViewModel.KbmSurfaces)) surfaceNotifications++;
                };
                Assert.False(config.KeyboardEnabled);

                devicesService.DeleteSlot(deleted);
                Assert.False(SettingsManager.SlotCreated[deleted]);
                Assert.Equal(new[] { surviving }, Enumerable.Range(0, InputManager.MaxPads)
                    .Where(i => SettingsManager.SlotCreated[i]));
                Assert.Same(config, pad.KbmConfig);
                Assert.Equal("Both", config.Surfaces);
                Assert.Equal("Off", config.SocdMode);
                Assert.Equal(KbmSlotConfig.DefaultSocdPairs, config.SocdPairs);
                Assert.True(surfaceNotifications > 0);
                Assert.Equal("KeyboardOnly", vm.Pads[surviving].KbmSurfaces);

                int recreated = devicesService.CreateSlot(VirtualControllerType.KeyboardMouse);
                Assert.Equal(deleted, recreated);
                Assert.Same(pad, vm.Pads[recreated]);
                Assert.True(config.KeyboardEnabled);
                Assert.True(config.MouseEnabled);
                Assert.Equal("KeyboardOnly", vm.Pads[surviving].KbmSurfaces);
            }
            finally
            {
                SettingsService.AfterMappingSetsRefreshed = afterRefresh;
                SettingsManager.UserSettings = settings;
                SettingsManager.UserDevices = devices;
                SettingsManager.SlotMappingSets = sets;
                SettingsManager.Profiles = profiles;
                SettingsManager.SlotCreated = created;
                SettingsManager.SlotEnabled = enabled;
                foreach (var pair in orders)
                {
                    var order = SettingsManager.SlotOrders.GetOrderFor(pair.Key);
                    order.Clear();
                    order.AddRange(pair.Value);
                }
            }
        }
    }
}
