using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The assistants' auto_map maps a controller again for its type (#600).
    /// The merge that folds the authored defaults into the slot keeps every
    /// row a device already has a source on (its device-present gate,
    /// SettingsService.MergeMappingSetsFromLegacy), so a slot that is not
    /// empty kept its rows. The device's sources leave the slot first, as
    /// an unassign strips them.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class MapSlotAgainTests : IDisposable
    {
        private static readonly Guid Pad = new("55555555-5555-5555-5555-555555555555");

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly Action _hook = SettingsService.AfterMappingSetsRefreshed;

        public void Dispose()
        {
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            SettingsService.AfterMappingSetsRefreshed = _hook;
        }

        [Fact]
        public void ADeviceWithRowsOnTheSlot_GetsTheTypesDefaultRowsInTheirPlace_AndAnAnyDeviceRowStays()
        {
            SettingsService.AfterMappingSetsRefreshed = null;
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            var ud = new UserDevice
            {
                InstanceGuid = Pad, ProductName = "Wireless Controller", CapType = InputDeviceType.Gamepad,
                CapAxeCount = 6, CapButtonCount = 11, CapPovCount = 1, IsOnline = true,
            };
            SettingsManager.UserDevices.Items.Add(ud);
            var us = new UserSetting { InstanceGuid = Pad, MapTo = 0 };
            us.SetPadSetting(SettingsManager.CreateDefaultPadSetting(ud, VirtualControllerType.Xbox));
            SettingsManager.UserSettings.Items.Add(us);
            SettingsService.RefreshMappingSetsFromLegacy();
            string device = Pad.ToString();
            Dictionary<string, string> Defaults() => SettingsManager.SlotMappingSets[0].Rows
                .Where(r => r.Sources.Any(s => s.DeviceGuid == device))
                .ToDictionary(r => r.Target, r => string.Join("|", r.Sources.Where(s => s.DeviceGuid == device).Select(s => s.Descriptor)));
            var defaults = Defaults();
            Assert.True(defaults.Count >= 10);
            Assert.True(defaults.ContainsKey("ButtonA"));

            // The user's own rows: A and B swapped.
            var rows = SettingsManager.SlotMappingSets[0].Rows;
            var a = rows.Single(r => r.Target == "ButtonA").Sources.Single(s => s.DeviceGuid == device);
            var b = rows.Single(r => r.Target == "ButtonB").Sources.Single(s => s.DeviceGuid == device);
            (a.Descriptor, b.Descriptor) = (b.Descriptor, a.Descriptor);
            // And Y read on any device: that row covers the device as it is.
            var y = rows.Single(r => r.Target == "ButtonY");
            y.Sources.RemoveAll(s => s.DeviceGuid == device);
            y.Sources.Add(new MappingSource { DeviceGuid = "", Descriptor = "Button 3" });
            defaults.Remove("ButtonY");

            SettingsService.MapSlotAgain(0, VirtualControllerType.Xbox, null, null);

            Assert.Equal(defaults, Defaults());
            var anyDevice = Assert.Single(SettingsManager.SlotMappingSets[0].Rows.Single(r => r.Target == "ButtonY").Sources);
            Assert.Equal(("", "Button 3"), (anyDevice.DeviceGuid, anyDevice.Descriptor));
        }

        /// <summary>The assistants' auto_map takes this path.</summary>
        [Fact]
        public void AutoMap_MapsTheSlotAgain()
        {
            var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !System.IO.Directory.Exists(System.IO.Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
            Assert.NotNull(root);
            string text = System.IO.File.ReadAllText(System.IO.Path.Combine(root.FullName, "PadForge.App", "MainWindow.Assistant.cs"));
            int start = text.IndexOf("public void AutoMap(int number)", StringComparison.Ordinal);
            Assert.True(start >= 0);
            string body = text.Substring(start, text.IndexOf("InputService.RefreshMappingsToViewModel(pad);", start, StringComparison.Ordinal) - start);
            Assert.Contains("SettingsService.MapSlotAgain(slot, pad.OutputType, pad.ProfileId, pad.ExtendedConfig);", body);
            Assert.DoesNotContain("SettingsManager.ReAutoMapSlot(", body);
        }
    }
}
