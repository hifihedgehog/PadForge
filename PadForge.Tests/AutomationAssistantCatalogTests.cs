using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using PadForge.Common.Input;
using PadForge.Search;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The settings catalog (#597) on the five real pages: every entry of
    /// Search's index has an ID of its own that does not change with the
    /// language, every setting is found by it, settings are found by the
    /// words on their rows, one setting of each kind on each page reads and
    /// writes through its control and back (AutomationAssistantCatalogKindsTests), a value out of range is refused
    /// with the range, the settings of the Automation and Plugins pages are
    /// the user's alone, and show opens the place without activating the
    /// window.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AutomationAssistantCatalogTests
    {
        [Fact]
        public void EveryEntryOfTheIndex_HasAnIdOfItsOwn_ThatNoWordsShownGoInto()
        {
            var index = SearchIndexBuilder.Build(SearchSources.Load());
            var ids = SearchIds.Assign(index);
            Assert.Equal(index.Count, ids.Values.Distinct(StringComparer.Ordinal).Count());
            foreach (var entry in index)
            {
                string id = ids[entry];
                Assert.True(SearchIds.TryRead(id, out string bare, out _, out _, out _), id);
                Assert.Equal(id, bare);
                // Made from keys and paths: never from a word the interface
                // shows, so the same in every language.
                if (entry.Label.Key != null) Assert.Contains(entry.Label.Key, id);
            }
            Assert.True(SearchIds.TryRead(SearchIds.With("pad/Pad_X", 3, 1, "abcd2345"), out string b, out int? c, out int? i, out string d));
            Assert.Equal(("pad/Pad_X", 3, 1, "abcd2345"), (b, c, i, d));
        }

        internal sealed class Pages
        {
            public MainViewModel Vm;
            public PadPage Pad;
            public SettingsPage Settings;
            public ExtrasPage Extras;
            public DevicesPage Devices;
            public ProfilesPage Profiles;
            public SearchService Service;
            public AssistantCatalog Catalog;

            public FrameworkElement PageOf(SearchPage page) => page switch
            {
                SearchPage.Pad => Pad,
                SearchPage.Settings => Settings,
                SearchPage.Extras => Extras,
                SearchPage.Devices => Devices,
                _ => Profiles,
            };
        }

        internal static void WithPages(Action<Pages> body)
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                bool[] made = (bool[])SettingsManager.SlotCreated.Clone();
                var devices = SettingsManager.UserDevices;
                var settings = SettingsManager.UserSettings;
                try
                {
                    SettingsManager.UserDevices = new DeviceCollection();
                    SettingsManager.UserSettings = new SettingsCollection();
                    for (int s = 0; s < SettingsManager.SlotCreated.Length; s++) SettingsManager.SlotCreated[s] = s == 0;
                    var p = new Pages { Vm = new MainViewModel() };
                    p.Vm.Pads[0].OutputType = PadForge.Engine.VirtualControllerType.Xbox;
                    var row = new DeviceRowViewModel { DeviceName = "Test Pad", InstanceGuid = Guid.NewGuid(), IsHidHideAvailable = true };
                    p.Vm.Devices.Devices.Add(row);
                    p.Vm.Devices.SelectedDevice = row;
                    p.Pad = new PadPage { DataContext = p.Vm.Pads[0] };
                    p.Settings = new SettingsPage { DataContext = p.Vm.Settings };
                    p.Extras = new ExtrasPage { DataContext = p.Vm.Dashboard };
                    p.Devices = new DevicesPage { DataContext = p.Vm.Devices };
                    p.Profiles = new ProfilesPage { DataContext = p.Vm.Settings };
                    foreach (var page in new FrameworkElement[] { p.Pad, p.Settings, p.Extras, p.Devices, p.Profiles })
                    {
                        page.Measure(new Size(1456, 985));
                        page.Arrange(new Rect(0, 0, 1456, 985));
                        page.UpdateLayout();
                    }
                    PageRenderDump.Settle(dispatcher);
                    p.Service = new SearchService(p.Vm, p.PageOf, pad => p.Pad.DataContext = pad);
                    p.Service.SetIndex(SearchIndexBuilder.Build(SearchSources.Load()));
                    var navigator = new SearchNavigator(p.Service, p.PageOf, p.Vm, _ => { }, _ => { }, () => { });
                    p.Catalog = new AssistantCatalog(p.Service, navigator, p.PageOf, p.Vm, pad => p.Pad.DataContext = pad,
                        () => AssistantWindowPlace.Behind);
                    body(p);
                }
                finally
                {
                    SettingsManager.SlotCreated = made;
                    SettingsManager.UserDevices = devices;
                    SettingsManager.UserSettings = settings;
                }
            }, TimeSpan.FromMinutes(4), "the catalog run timed out");
        }

        [Fact]
        public void EverySettingTheIndexAccountsFor_IsFoundByItsId_AndByTheWordsOnItsRow()
        {
            WithPages(p =>
            {
                int found = 0;
                foreach (var entry in p.Service.Index.Where(e => e.Kind == SearchKind.Setting))
                {
                    string id = p.Service.IdOf(entry);
                    var setting = p.Catalog.Get(id);
                    Assert.NotNull(setting);
                    Assert.StartsWith(id, setting.Id, StringComparison.Ordinal);
                    found++;
                }
                Assert.True(found > 300, "only " + found + " settings");
                var all = p.Catalog.All();
                Assert.Equal(all.Count, all.Select(s => s.Id).Distinct().Count());
                // A setting is found by its row's words, and its ID reads it.
                var deadzone = p.Catalog.Find("deadzone", 50);
                Assert.NotEmpty(deadzone);
                Assert.All(deadzone, s => Assert.Equal(s.Id, p.Catalog.Get(s.Id).Id));
                Assert.Contains(p.Catalog.Find("polling rate", 10), s => s.Id.StartsWith("settings/", StringComparison.Ordinal));
                Assert.Throws<AssistantRefusal>(() => p.Catalog.Get("settings/No_Such_Key"));
            });
        }

        [Fact]
        public void ANumberOutOfItsRange_IsRefused_WithTheRange()
        {
            WithPages(p =>
            {
                var number = p.Catalog.All().First(s => s.Kind == AssistantSettingKind.Number && s.Maximum != null && s.Value != null);
                var refusal = Assert.Throws<AssistantRefusal>(() => p.Catalog.Set(number.Id, JsonSerializer.SerializeToElement(number.Maximum.Value + 1000)));
                Assert.Equal("out-of-range", refusal.Code);
                Assert.Contains(number.Maximum.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), refusal.Words);
                Assert.Equal(number.Value.Value.GetRawText(), p.Catalog.Get(number.Id).Value.Value.GetRawText());
                // A choice it does not have names the ones it has.
                var choice = p.Catalog.All().First(s => s.Kind == AssistantSettingKind.Choice && s.Choices is { Count: > 0 });
                var wrong = Assert.Throws<AssistantRefusal>(() => p.Catalog.Set(choice.Id, JsonSerializer.SerializeToElement("No Such Choice")));
                Assert.Equal("out-of-range", wrong.Code);
                Assert.Contains(choice.Choices[0], wrong.Words);
            });
        }

        [Fact]
        public void TheAutomationAndPluginsSettings_AreTheUsersAlone()
        {
            WithPages(p =>
            {
                foreach (var entry in p.Service.Index.Where(e => e.Kind == SearchKind.Setting && e.ExtraKey is "Automation" or "Plugins"))
                {
                    string id = p.Service.IdOf(entry);
                    var on = JsonSerializer.SerializeToElement(true);
                    Assert.Equal("users-own", Assert.Throws<AssistantRefusal>(() => p.Catalog.AskFor(id, on)).Code);
                    Assert.Equal("users-own", Assert.Throws<AssistantRefusal>(() => p.Catalog.Set(id, on)).Code);
                }
                Assert.Contains(p.Service.Index, e => e.Kind == SearchKind.Setting && e.ExtraKey == "Automation");
                Assert.False(p.Vm.Dashboard.EnableAutomation);
                // A setting of an extra that listens on the network asks
                // when it would turn the extra on.
                var web = p.Service.Index.First(e => e.Kind == SearchKind.Setting && e.ExtraKey == "WebController" && e.Value?.Kind == SearchValueKind.Switch);
                var ask = p.Catalog.AskFor(p.Service.IdOf(web), JsonSerializer.SerializeToElement(true));
                Assert.NotNull(ask);
                Assert.Contains(ask.Detail, d => d == PadForge.Resources.Strings.Strings.Instance.Automation_Ask_Network);
            });
        }

        [Fact]
        public void Show_OpensThePlace_AndNeverActivatesTheWindow()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Settings, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                try
                {
                    PageRenderDump.Settle(window.Dispatcher);
                    // The positive control: the window shows, and is not active.
                    Assert.True(window.IsVisible);
                    Assert.False(window.IsActive);
                    var setting = p.Catalog.All().First(s => s.Id.StartsWith("settings/", StringComparison.Ordinal) && s.Available);
                    var shown = p.Catalog.Show(new AssistantShowTarget { Kind = AssistantShowKind.Setting, Id = setting.Id });
                    Assert.True(shown.Shown);
                    Assert.Equal(AssistantWindowPlace.Behind, shown.Window);
                    Assert.Contains("front", shown.Message);
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.False(window.IsActive);
                    var device = p.Catalog.Show(new AssistantShowTarget { Kind = AssistantShowKind.Device, Id = AutomationDevices.IdOf(p.Vm.Devices.Devices[0].InstanceGuid) });
                    Assert.True(device.Shown);
                    Assert.Throws<AssistantRefusal>(() => p.Catalog.Show(new AssistantShowTarget { Kind = AssistantShowKind.Profile, Id = "No Such Profile" }));
                    Assert.False(window.IsActive);
                }
                finally
                {
                    window.Close();
                }
            });
        }
    }
}
