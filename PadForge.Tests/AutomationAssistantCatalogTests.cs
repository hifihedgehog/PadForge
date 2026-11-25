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
            public AssistantPointer Pointer;
            public Window Window;

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
                    p.Pointer = new AssistantPointer(() => p.Window ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w.Content is FrameworkElement));
                    p.Catalog = new AssistantCatalog(p.Service, navigator, p.PageOf, p.Vm, pad => p.Pad.DataContext = pad,
                        () => AssistantWindowPlace.Behind, new AssistantCatalogParts { Pointer = p.Pointer });
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
                    var setting = p.Catalog.Read(id).Place;
                    Assert.NotNull(setting);
                    Assert.StartsWith(id, setting.Id, StringComparison.Ordinal);
                    found++;
                }
                Assert.True(found > 300, "only " + found + " settings");
                var all = p.Catalog.All();
                Assert.Equal(all.Count, all.Select(s => s.Id).Distinct().Count());
                // A setting is found by its row's words, and its ID reads it.
                var deadzone = p.Catalog.Find("deadzone", "setting", 50).Places;
                Assert.NotEmpty(deadzone);
                Assert.All(deadzone, s => Assert.Equal(s.Id, p.Catalog.Read(s.Id).Place.Id));
                Assert.Contains(p.Catalog.Find("polling rate", null, 10).Places, s => s.Id.StartsWith("settings/", StringComparison.Ordinal));
                Assert.Equal("unknown-setting", Assert.Throws<AssistantRefusal>(() => p.Catalog.Read("settings/No_Such_Key")).Code);
            });
        }

        [Fact]
        public void ANumberOutOfItsRange_IsRefused_WithTheRange()
        {
            WithPages(p =>
            {
                var number = p.Catalog.All().First(s => s.Takes == "number" && s.Maximum != null && s.Value != null);
                var refusal = Assert.Throws<AssistantRefusal>(() => p.Catalog.Set(number.Id, JsonSerializer.SerializeToElement(number.Maximum.Value + 1000)));
                Assert.Equal("out-of-range", refusal.Code);
                Assert.Contains(number.Maximum.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), refusal.Words);
                Assert.Equal(number.Value.Value.GetRawText(), p.Catalog.Read(number.Id).Place.Value.Value.GetRawText());
                // A choice it does not have names the ones it has.
                var choice = p.Catalog.All().First(s => s.Takes == "choice" && s.Choices is { Count: > 0 });
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
        public void Show_RingsThePlace_WithTheWords_AndNeverActivatesTheWindow()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Settings, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                p.Window = window;
                try
                {
                    PageRenderDump.Settle(window.Dispatcher);
                    // The positive control: the window shows, and is not active.
                    Assert.True(window.IsVisible);
                    Assert.False(window.IsActive);
                    var setting = p.Catalog.All().First(s => s.Id.StartsWith("settings/", StringComparison.Ordinal) && s.Showing);
                    var shown = p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Steps = new[] { new AssistantShowStep { Place = setting.Id, Words = "Here\u202E it is.", Advance = AssistantAdvance.Next } },
                    });
                    Assert.True(shown.Shown);
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.False(window.IsActive);
                    var callout = p.Pointer.Callout;
                    Assert.NotNull(callout);
                    Assert.Equal("Claude Code", callout.Program);
                    // Format characters out: the words cannot turn around.
                    Assert.Equal("Here it is.", callout.Words);
                    Assert.NotNull(p.Pointer.Target);
                    // One callout at a time: a show of a device replaces it.
                    var device = p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Steps = new[] { new AssistantShowStep { Place = "device/" + AutomationDevices.IdOf(p.Vm.Devices.Devices[0].InstanceGuid), Words = "Your pad.", Advance = AssistantAdvance.Next } },
                    });
                    Assert.True(device.Shown);
                    // A device's row on a page no window shows is looked for a
                    // moment, then the callout stands at the top of the window.
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (p.Pointer.Callout?.Words != "Your pad." && clock.ElapsedMilliseconds < 4000)
                    {
                        System.Threading.Thread.Sleep(50);
                        PageRenderDump.Settle(window.Dispatcher);
                    }
                    Assert.Equal("Your pad.", p.Pointer.Callout?.Words);
                    Assert.Equal("unknown-setting", Assert.Throws<AssistantRefusal>(() => p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "x",
                        Steps = new[] { new AssistantShowStep { Place = "profile/No Such Profile", Words = "x" } },
                    })).Code);
                    // A place that is the user's alone is never pointed at.
                    var own = p.Service.Index.First(e => e.Kind == SearchKind.Setting && e.ExtraKey == "Automation");
                    Assert.Equal("users-own", Assert.Throws<AssistantRefusal>(() => p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "x",
                        Steps = new[] { new AssistantShowStep { Place = p.Service.IdOf(own), Words = "x" } },
                    })).Code);
                    // Show with no place takes the callout away.
                    var gone = p.Catalog.Show(new AssistantShowRequest { Program = "x" });
                    Assert.False(gone.Shown);
                    Assert.Null(p.Pointer.Callout);
                    Assert.False(window.IsActive);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void AWalkToASettingInAnotherGroup_RingsThatGroupsButtonFirst()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Settings, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                p.Window = window;
                try
                {
                    PageRenderDump.Settle(window.Dispatcher);
                    // The Settings page is up, so the walk starts at its group.
                    p.Vm.SelectedNavTag = "Settings";
                    var shownGroup = p.Vm.Settings.SelectedSettingsGroup;
                    SettingsGroup? other = null;
                    string id = null;
                    foreach (var entry in p.Service.Index.Where(e => e.Page == SearchPage.Settings && e.Kind == SearchKind.Setting && e.ItemsPath == null && !e.RowOptions))
                    {
                        p.Service.Locator.Reset();
                        var label = p.Service.Locator.Find(entry, p.Service.Index);
                        if (label == null || p.Settings.GroupOf(label) is not { } group || group == shownGroup) continue;
                        var (cls, _) = PadForge.Common.Reach.Of(label);
                        if (cls == PadForge.Common.ReachClass.UsersOwn) continue;
                        other = group;
                        id = p.Service.IdOf(entry);
                        break;
                    }
                    Assert.NotNull(id);
                    var shown = p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Walk = true,
                        Steps = new[] { new AssistantShowStep { Place = id, Words = "This one.", Advance = AssistantAdvance.Click } },
                    });
                    Assert.True(shown.Shown);
                    Assert.True(shown.Steps >= 2, "the group's step comes before the place's");
                    var button = p.Settings.GroupButton(other.Value);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!ReferenceEquals(p.Pointer.Target, button) && clock.ElapsedMilliseconds < 4000)
                    {
                        System.Threading.Thread.Sleep(50);
                        PageRenderDump.Settle(window.Dispatcher);
                    }
                    Assert.Same(button, p.Pointer.Target);
                    Assert.Contains(p.Settings.GroupName(other.Value), p.Pointer.Callout.Words);
                    Assert.False(window.IsActive);
                }
                finally
                {
                    p.Pointer.Clear();
                    window.Close();
                }
            });
        }

        [Fact]
        public void FindReachesEveryKind_AndAReadOfACard_GivesItsSettingsAndLines_AndChangesNothingOnScreen()
        {
            WithPages(p =>
            {
                foreach (string kind in new[] { "setting", "action", "card", "section", "extra", "page", "device", "controller" })
                {
                    var found = p.Catalog.Find(null, kind, 100);
                    Assert.True(found.Places.Count > 0, "find gives no " + kind);
                    Assert.All(found.Places, f => Assert.Equal(kind, f.Kind));
                }
                var few = p.Catalog.Find(null, "setting", 3);
                Assert.Equal(3, few.Places.Count);
                Assert.True(few.LeftOut > 100);
                Assert.Equal("bad-argument", Assert.Throws<AssistantRefusal>(() => p.Catalog.Find("x", "gadget", 5)).Code);

                // A card of the Settings page with its settings and their
                // values, read as the card shows them.
                var cards = p.Catalog.Find(null, "card", 400).Places.Where(c => c.Id.StartsWith("settings.card/", StringComparison.Ordinal)).ToList();
                Assert.NotEmpty(cards);
                var bound = p.Pad.DataContext;
                var device = p.Vm.Devices.SelectedDevice;
                AssistantRead read = null;
                foreach (var card in cards)
                {
                    read = p.Catalog.Read(card.Id);
                    if (read.Children.Any(c => c.Value != null)) break;
                }
                Assert.Contains(read.Children, c => c.Kind == "setting" && c.Value != null);
                Assert.Equal("https://padforge.org/docs/features/settings/", read.Docs);
                // A read changes nothing on screen.
                Assert.Same(bound, p.Pad.DataContext);
                Assert.Same(device, p.Vm.Devices.SelectedDevice);

                // A page reads its cards, and a controller its sections.
                var page = p.Catalog.Read("page/Settings");
                Assert.Contains(page.Children, c => c.Kind == "card");
                var controller = p.Catalog.Read("controller/1");
                Assert.Contains(controller.Children, c => c.Kind == "section");
                Assert.Equal("https://padforge.org/docs/features/controller-slots/", controller.Docs);
            });
        }

        [Fact]
        public void APressOfTheUsersOwn_IsRefused_AndOneThatAsks_CarriesItsQuestion_WithNothingPressed()
        {
            WithPages(p =>
            {
                var actions = p.Catalog.Find(null, "action", 2000).Places;
                var own = actions.FirstOrDefault(a => a.Reach == "yours-alone" && a.Showing);
                Assert.NotNull(own);
                Assert.Equal("users-own", Assert.Throws<AssistantRefusal>(() => p.Catalog.PlanPress(own.Id)).Code);
                var asks = actions.FirstOrDefault(a => a.Reach == "asks" && a.Showing && a.Enabled);
                Assert.NotNull(asks);
                var plan = p.Catalog.PlanPress(asks.Id);
                Assert.NotNull(plan.Ask);
                Assert.NotNull(plan.Press);
                // A setting is not pressed.
                var setting = p.Catalog.All().First(s => s.Showing && s.Reach == "free");
                Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => p.Catalog.PlanPress(setting.Id)).Code);
            });
        }

        [Fact]
        public void AnOpenDialog_IsReadWithItsTextAndButtons_AndAButtonIsPressedByItsClass()
        {
            WithPages(p =>
            {
                var owner = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true, Content = new System.Windows.Controls.Grid() };
                owner.Show();
                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Remove the profile?" });
                int cancels = 0;
                var cancel = new System.Windows.Controls.Button { Content = "Cancel" };
                cancel.Click += (_, _) => cancels++;
                System.Windows.Automation.AutomationProperties.SetAutomationId(cancel, "Dialog_Cancel");
                var remove = new System.Windows.Controls.Button { Content = "Remove" };
                PadForge.Common.Reach.SetClass(remove, PadForge.Common.ReachClass.UsersOwn);
                panel.Children.Add(cancel);
                panel.Children.Add(remove);
                var dialog = new Window { Title = "Profiles", Content = panel, Owner = owner, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 300, Height = 200, WindowStyle = WindowStyle.None, AllowsTransparency = true };
                PadForge.Common.Reach.SetClass(dialog, PadForge.Common.ReachClass.Free);
                var catalog = new AssistantCatalog(p.Service, new SearchNavigator(p.Service, p.PageOf, p.Vm, _ => { }, _ => { }, () => { }),
                    p.PageOf, p.Vm, pad => p.Pad.DataContext = pad, () => AssistantWindowPlace.InFront, new AssistantCatalogParts { MainWindow = () => owner });
                dialog.Show();
                try
                {
                    PadForge.Services.Catalog.DialogRegistry.Register(dialog);
                    PageRenderDump.Settle(dialog.Dispatcher);
                    var open = Assert.Single(catalog.Dialogs().Open);
                    Assert.Equal("open/Window", open.Id);
                    Assert.Equal("Profiles", open.Title);
                    var read = catalog.Read(open.Id);
                    Assert.Equal("dialog", read.Place.Kind);
                    Assert.Contains("Remove the profile?", read.Lines);
                    Assert.Equal(new[] { "Cancel", "Remove" }, read.Buttons.Select(b => b.Name));
                    Assert.Equal("open/Window#Dialog_Cancel", read.Buttons[0].Id);
                    Assert.Equal("yours-alone", read.Buttons[1].Reach);
                    Assert.Equal("users-own", Assert.Throws<AssistantRefusal>(() => catalog.PlanPress(read.Buttons[1].Id)).Code);
                    var plan = catalog.PlanPress(read.Buttons[0].Id);
                    Assert.Null(plan.Ask);
                    plan.Press();
                    // A button's peer clicks it a moment later, as a screen
                    // reader's press does.
                    PageRenderDump.Settle(dialog.Dispatcher);
                    Assert.Equal(1, cancels);
                    // where names it.
                    Assert.Contains(catalog.Where().Dialogs, d => d.Id == "open/Window");
                }
                finally
                {
                    dialog.Close();
                    owner.Close();
                }
                Assert.Empty(catalog.Dialogs().Open);
            });
        }

        [Fact]
        public void AWindowsDialogPadForgeCannotRead_IsNamedAsWaitingForThePerson()
        {
            Assert.Equal("A Windows file chooser, \"Open\", waits for the person. PadForge cannot read it, and what was asked carries on once the person answers it.",
                AssistantWindowsDialogs.Sentence(true, "Open"));
            Assert.StartsWith("A Windows message box waits for the person.", AssistantWindowsDialogs.Sentence(false, null));
            Assert.Null(AssistantWindowsDialogs.Waiting(null));
        }

        [Fact]
        public void APluginsDeclarations_AreFoundReadSetAndPressed_ThroughTheLiveSeam()
        {
            WithPages(p =>
            {
                string speed = "Slow";
                int pressed = 0;
                var declared = new List<PadForge.Services.Catalog.LiveItem>
                {
                    new("plugin/abcd1234/speed", PadForge.Services.Catalog.LiveKind.PluginDeclaration, "Speed", PadForge.Common.ReachClass.Free)
                    {
                        Where = "Wheel Bridge",
                        Read = () => speed,
                        Write = v => speed = v,
                    },
                    new("plugin/abcd1234/recenter", PadForge.Services.Catalog.LiveKind.PluginDeclaration, "Recenter", PadForge.Common.ReachClass.Free)
                    {
                        Where = "Wheel Bridge",
                        Press = () => pressed++,
                    },
                };
                Func<IReadOnlyList<PadForge.Services.Catalog.LiveItem>> provider = () => declared;
                PadForge.Services.Catalog.LiveCatalog.AddProvider(provider);
                try
                {
                    var found = p.Catalog.Find(null, "plugin_setting", 2000).Places;
                    Assert.Contains(found, f => f.Id == "plugin/abcd1234/speed");
                    Assert.Contains(found, f => f.Id == "plugin/abcd1234/recenter");
                    var read = p.Catalog.Read("plugin/abcd1234/speed");
                    Assert.Equal("Slow", read.Place.ValueText);
                    Assert.Equal("text", read.Place.Takes);
                    p.Catalog.Set("plugin/abcd1234/speed", JsonSerializer.SerializeToElement("Fast"));
                    Assert.Equal("Fast", speed);
                    // A press-only declaration takes no value.
                    Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() =>
                        p.Catalog.Set("plugin/abcd1234/recenter", JsonSerializer.SerializeToElement("x"))).Code);
                    p.Catalog.PlanPress("plugin/abcd1234/recenter").Press();
                    Assert.Equal(1, pressed);
                }
                finally
                {
                    PadForge.Services.Catalog.LiveCatalog.RemoveProvider(provider);
                }
            });
        }

        [Fact]
        public void HomesCards_ReachTheCatalogThroughTheLiveSeam_ReadAndPressedOnTheirOwnButtons()
        {
            WithPages(p =>
            {
                var dash = p.Vm.Dashboard;
                dash.SlotSummaries.Clear();
                dash.SlotSummaries.Add(new SlotSummary(0) { OutputType = PadForge.Engine.VirtualControllerType.Xbox, TypeInstanceLabel = "1", SlotNumber = 1 });
                var home = new HomePage { DataContext = dash };
                home.Measure(new Size(1456, 985));
                home.Arrange(new Rect(0, 0, 1456, 985));
                home.UpdateLayout();
                PageRenderDump.Settle(home.Dispatcher);
                int toggled = 0;
                home.SlotEnabledToggled += (_, _) => toggled++;
                Func<IReadOnlyList<PadForge.Services.Catalog.LiveItem>> provider = () => AssistantHomeCards.Items(home, dash, p.Vm.Pads);
                PadForge.Services.Catalog.LiveCatalog.AddProvider(provider);
                try
                {
                    var card = p.Catalog.Read("home/c1");
                    Assert.Equal("setting", card.Place.Kind);
                    Assert.Equal(AssistantHomeCards.Buttons.Count, card.Children.Count);
                    Assert.Contains(card.Children, c => c.Id == "home/c1/power" && c.Kind == "action");
                    // Found by find, as any place is.
                    Assert.Contains(p.Catalog.Find(null, "action", 2000).Places, a => a.Id == "home/c1/power");
                    var plan = p.Catalog.PlanPress("home/c1/power");
                    plan.Press();
                    PageRenderDump.Settle(home.Dispatcher);
                    Assert.Equal(1, toggled);
                }
                finally
                {
                    PadForge.Services.Catalog.LiveCatalog.RemoveProvider(provider);
                }
            });
        }
    }
}
