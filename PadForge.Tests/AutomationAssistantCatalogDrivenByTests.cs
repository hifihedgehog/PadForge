using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PadForge.Search;
using PadForge.Services.Automation;
using PadForge.Services.Catalog;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The devices a place belongs to (#600): a controller page's setting
    /// is of the device its Driven by picks, which its ID names, read and
    /// set for that device whatever the page picks by then, and where names
    /// the pick. The tray's items and the pages' menu items belong to no
    /// device, whatever suffix a program writes on their IDs.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AutomationAssistantCatalogDrivenByTests
    {
        [Fact]
        public void AControllerPagesSetting_IsOfTheDeviceItsIdNames_UnderDrivenBy()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var pad = p.Vm.Pads[0];
                var a = new PadViewModel.MappedDeviceInfo { Name = "Pad A", InstanceGuid = Guid.NewGuid() };
                var b = new PadViewModel.MappedDeviceInfo { Name = "Pad B", InstanceGuid = Guid.NewGuid() };
                pad.MappedDevices.Add(a);
                pad.MappedDevices.Add(b);
                pad.SelectedMappedDevice = a;
                var picks = new List<Guid>();
                EventHandler<PadViewModel.MappedDeviceInfo> picked = (_, d) => picks.Add(d?.InstanceGuid ?? Guid.Empty);
                pad.SelectedDeviceChanged += picked;
                try
                {
                    string forA = "@c1d" + AutomationDevices.IdOf(a.InstanceGuid);
                    var place = p.Catalog.All().First(s => s.Id.StartsWith("pad/", StringComparison.Ordinal) && s.Id.EndsWith(forA, StringComparison.Ordinal)
                                                           && s.Takes == "number" && s.Value != null);
                    // The user picks B: the program's ID still names A.
                    pad.SelectedMappedDevice = b;
                    picks.Clear();
                    p.Catalog.Set(place.Id, place.Value.Value);
                    Assert.Equal(new[] { a.InstanceGuid, b.InstanceGuid }, picks);
                    Assert.Same(b, pad.SelectedMappedDevice);
                    picks.Clear();
                    Assert.Equal(place.Id, p.Catalog.Read(place.Id).Place.Id);
                    Assert.Equal(new[] { a.InstanceGuid, b.InstanceGuid }, picks);
                    // The setting's ID with no device names the device picked now.
                    Assert.True(SearchIds.TryRead(place.Id, out string bare, out int? controller, out int? item, out _));
                    picks.Clear();
                    var now = p.Catalog.Read(SearchIds.With(bare, controller, item, null)).Place;
                    Assert.Equal(SearchIds.With(bare, controller, item, AutomationDevices.IdOf(b.InstanceGuid)), now.Id);
                    Assert.True(now.Showing);
                    Assert.Empty(picks);
                    // where names the pick of the controller page it shows.
                    p.Vm.SelectedNavTag = "Pad1";
                    Assert.Equal(AutomationDevices.IdOf(b.InstanceGuid), p.Catalog.Where().ControllerDevice);
                    // A device the controller does not have is refused.
                    Assert.Equal("unknown-device", Assert.Throws<AssistantRefusal>(() => p.Catalog.Read(SearchIds.With(bare, controller, item, "zzzzzzzz"))).Code);
                }
                finally
                {
                    pad.SelectedDeviceChanged -= picked;
                }
            });
        }

        /// <summary>A program held to its own devices wrote its device's
        /// suffix on the tray's Exit: the ID is no place of that device,
        /// and the tray item it names is not shown for it, so the press is
        /// refused and the tray's work never runs.</summary>
        [Fact]
        public void ATraysItemUnderADevicesSuffix_IsNoPlaceOfThatDevice_AndIsNotPressed()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var pressed = new List<string>();
                var catalog = new AssistantCatalog(p.Service, new SearchNavigator(p.Service, p.PageOf, p.Vm, _ => { }, _ => { }, () => { }),
                    p.PageOf, p.Vm, pad => p.Pad.DataContext = pad, () => AssistantWindowPlace.InFront,
                    new AssistantCatalogParts { PressTray = key => pressed.Add(key) });
                string device = AutomationDevices.IdOf(p.Vm.Devices.SelectedDevice.InstanceGuid);
                string exit = "window.menuitem/Tray/Tray_Exit@d" + device;
                Assert.Null(catalog.DeviceHolding(exit));
                Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => catalog.PlanPress(exit)).Code);
                // The positive control: the tray's own ID plans its press.
                Assert.NotNull(catalog.PlanPress("window.menuitem/Tray/Tray_Exit").Press);
                // A Settings or Extras page's menu item under the suffix.
                var menu = catalog.Find(null, "menu_item", 400).Places.FirstOrDefault(m => m.Showing && m.Reach != "yours-alone" && !m.Id.Contains('@')
                    && (m.Id.StartsWith("settings.", StringComparison.Ordinal) || m.Id.StartsWith("extras.", StringComparison.Ordinal)));
                if (menu != null)
                {
                    Assert.Null(catalog.DeviceHolding(menu.Id + "@d" + device));
                    Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => catalog.PlanPress(menu.Id + "@d" + device)).Code);
                }
                Assert.Empty(pressed);
            });
        }

        /// <summary>show and a walk check a controller page's setting for the
        /// device its ID names, as the page shows it now: under the device
        /// Driven by picks it shows, under another it does not, and neither
        /// picks a device on the Devices page or under Driven by.</summary>
        [Fact]
        public void AShowOfAControllerPagesSetting_IsCheckedForThePickedDevice_AndPicksNothing()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var pad = p.Vm.Pads[0];
                var a = new PadViewModel.MappedDeviceInfo { Name = "Pad A", InstanceGuid = Guid.NewGuid() };
                var b = new PadViewModel.MappedDeviceInfo { Name = "Pad B", InstanceGuid = Guid.NewGuid() };
                pad.MappedDevices.Add(a);
                pad.MappedDevices.Add(b);
                pad.SelectedMappedDevice = a;
                var shownDevice = p.Vm.Devices.SelectedDevice;
                string forA = "@c1d" + AutomationDevices.IdOf(a.InstanceGuid);
                var place = p.Catalog.All().First(s => s.Id.StartsWith("pad/", StringComparison.Ordinal) && s.Id.EndsWith(forA, StringComparison.Ordinal));
                AssistantShowRequest Request(bool walk) => new()
                {
                    Program = "x", Walk = walk,
                    Steps = new[] { new AssistantShowStep { Place = place.Id, Words = "Here.", Advance = AssistantAdvance.Next } },
                };
                var picks = new List<Guid>();
                EventHandler<PadViewModel.MappedDeviceInfo> picked = (_, d) => picks.Add(d?.InstanceGuid ?? Guid.Empty);
                pad.SelectedDeviceChanged += picked;
                try
                {
                    Assert.Equal(place.Id, p.Catalog.CheckShow(Request(false)).Id);
                    Assert.Equal(place.Id, p.Catalog.CheckShow(Request(true)).Id);
                    pad.SelectedMappedDevice = b;
                    picks.Clear();
                    foreach (bool walk in new[] { false, true })
                    {
                        var refusal = Assert.Throws<AssistantRefusal>(() => p.Catalog.CheckShow(Request(walk)));
                        Assert.Equal("not-available", refusal.Code);
                        Assert.Equal(AssistantCatalog.NotShownWords, refusal.Words);
                    }
                    Assert.Empty(picks);
                    Assert.Same(b, pad.SelectedMappedDevice);
                    Assert.Same(shownDevice, p.Vm.Devices.SelectedDevice);
                }
                finally
                {
                    pad.SelectedDeviceChanged -= picked;
                }
            });
        }
    }
}
