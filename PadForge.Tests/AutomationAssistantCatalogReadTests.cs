using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Search;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The catalog's read and find on the real pages (#600): another
    /// device's panel read whole while it shows, a device named by its
    /// product as list_devices names it, and a name the user gave a device
    /// that no find's match spells out.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AutomationAssistantCatalogReadTests
    {
        /// <summary>A device the user named, with its row on the Devices
        /// page as the window makes it (the row's name is the user's).</summary>
        private static (UserDevice Device, DeviceRowViewModel Row) Named(AutomationAssistantCatalogTests.Pages p)
        {
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), ProductName = "Wireless Gamepad", DisplayName = "Zelda Wand", IsOnline = true };
            ud.LoadCapabilities(2, 4, 0, InputDeviceType.Gamepad);
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            var row = new DeviceRowViewModel { DeviceName = "Zelda Wand", ProductName = "Wireless Gamepad", InstanceGuid = ud.InstanceGuid, IsHidHideAvailable = true };
            p.Vm.Devices.Devices.Add(row);
            AutomationReadScrub.ForgetForTest();
            return (ud, row);
        }

        /// <summary>Read of another device's panel reads every child while
        /// that device shows: a switch's value is that device's, as its
        /// words say, never the device the page shows after.</summary>
        [Fact]
        public void AnotherDevicesRead_ReadsEachChildWhileThatDeviceShows()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var shown = p.Vm.Devices.SelectedDevice;
                shown.HidHideEnabled = false;
                var other = new DeviceRowViewModel { DeviceName = "Other Pad", InstanceGuid = Guid.NewGuid(), IsHidHideAvailable = true, HidHideEnabled = true };
                p.Vm.Devices.Devices.Add(other);
                var hide = p.Service.Index.First(e => e.Page == SearchPage.Devices && e.Kind == SearchKind.Setting
                                                      && e.Value?.Path != null && e.Value.Path.EndsWith(nameof(DeviceRowViewModel.HidHideEnabled), StringComparison.Ordinal));
                var read = p.Catalog.Read("device/" + AutomationDevices.IdOf(other.InstanceGuid));
                Assert.Same(shown, p.Vm.Devices.SelectedDevice);
                var child = read.Children.FirstOrDefault(c => c.Id.StartsWith(p.Service.IdOf(hide), StringComparison.Ordinal));
                Assert.True(child != null, "no HidHide child among " + string.Join(", ", read.Children.Select(c => c.Id)));
                Assert.True(child.Value.Value.GetBoolean(), "the child's value is the shown device's");
                foreach (var c in read.Children.Where(c => c.Takes == "switch" && c.Value != null))
                    Assert.Equal(c.Value.Value.GetBoolean() ? PadForge.Resources.Strings.Strings.Instance.Common_On : PadForge.Resources.Strings.Strings.Instance.Common_Off, c.ValueText);
            });
        }

        /// <summary>find and read name a device as list_devices does, by
        /// its product, so one device comes back under one name.</summary>
        [Fact]
        public void ADevice_IsNamedByItsProduct_AsListDevicesNamesIt()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var (ud, _) = Named(p);
                string id = "device/" + AutomationDevices.IdOf(ud.InstanceGuid);
                var found = p.Catalog.Find(null, "device", 100).Places.Single(d => d.Id == id);
                Assert.Equal(AssistantTools.DeviceName(ud), found.Name);
                Assert.Equal(AssistantTools.DeviceName(ud), p.Catalog.Read(id).Place.Name);
            });
        }

        /// <summary>A find matches no words of a name the user gave a
        /// device, whose read comes back scrubbed: a fragment of it finds
        /// neither the device nor the Devices page's entries while the
        /// device shows there. Its product's words still find it.</summary>
        [Fact]
        public void AFind_NeverMatchesANameTheUserGaveADevice()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var (ud, row) = Named(p);
                p.Vm.Devices.SelectedDevice = row;
                Assert.Empty(p.Catalog.Find("Zeld", "device", 100).Places);
                var any = p.Catalog.Find("Zeld", null, 100).Places;
                Assert.True(any.Count == 0, "found " + string.Join(", ", any.Select(a => a.Id)));
                Assert.Contains(p.Catalog.Find("Wireless", "device", 100).Places, d => d.Id == "device/" + AutomationDevices.IdOf(ud.InstanceGuid));
            });
        }
    }
}
