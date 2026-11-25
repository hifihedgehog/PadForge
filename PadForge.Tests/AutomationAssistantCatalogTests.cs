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

        /// <summary>The Head Tracking switch asks only when turning it on
        /// starts the UDP listener (#600): the first time, UDP with FreeTrack,
        /// it asks with the network's line. With only OpenXR to bring back it
        /// starts no listener, and nothing asks.</summary>
        [Fact]
        public void TheHeadTrackingSwitch_AsksOnlyWhenItStartsTheUdpListener()
        {
            WithPages(p =>
            {
                bool udp = HeadTrackingRuntime.Enabled, free = HeadTrackingRuntime.FreeTrackEnabled, xr = HeadTrackingRuntime.OpenXrEnabled;
                try
                {
                    var dash = p.Vm.Dashboard;
                    dash.HeadTrackingEnabled = false;
                    dash.HeadTrackingFreeTrack = false;
                    dash.HeadTrackingOpenXr = false;
                    dash.HeadTrackingLastSources = 0;
                    var head = p.Service.Index.First(e => e.Kind == SearchKind.Setting && e.ExtraKey == "HeadTracking"
                                                          && e.Value?.Path == nameof(DashboardViewModel.HeadTrackingAnyEnabled));
                    string id = p.Service.IdOf(head);
                    var on = JsonSerializer.SerializeToElement(true);
                    var ask = p.Catalog.AskFor(id, on);
                    Assert.NotNull(ask);
                    Assert.Contains(PadForge.Resources.Strings.Strings.Instance.Automation_Ask_Network, ask.Detail);
                    dash.HeadTrackingLastSources = DashboardViewModel.HeadTrackingOpenXrBit;
                    Assert.Null(p.Catalog.AskFor(id, on));
                    // Its own UDP switch starts the listener whenever it turns on.
                    var udpSwitch = p.Service.Index.First(e => e.Kind == SearchKind.Setting && e.Value?.Path == nameof(DashboardViewModel.HeadTrackingEnabled));
                    Assert.NotNull(p.Catalog.AskFor(p.Service.IdOf(udpSwitch), on));
                }
                finally
                {
                    HeadTrackingRuntime.Enabled = udp;
                    HeadTrackingRuntime.FreeTrackEnabled = free;
                    HeadTrackingRuntime.OpenXrEnabled = xr;
                }
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

        /// <summary>The first build's words for a place the page does not
        /// show (AS-79), which show and a walk give again.</summary>
        private const string NotShownWords = "The page does not show this setting now, for that controller or device.";

        private static AssistantShowStep StepAt(string place) => new() { Place = place, Words = "Here.", Advance = AssistantAdvance.Next };

        [Fact]
        public void Show_RefusesAPlaceThePageDoesNotShowNow_AndSoDoesAWalk()
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
                    // A setting the page hides for the controller and device
                    // it shows now, that a program may point at otherwise.
                    string hidden = null;
                    var showing = p.Catalog.All().Select(s => s.Id).ToList();
                    foreach (var entry in p.Service.Index.Where(e => e.Kind == SearchKind.Setting))
                    {
                        string bare = p.Service.IdOf(entry);
                        if (showing.Any(id => id == bare || id.StartsWith(bare + "@", StringComparison.Ordinal))) continue;
                        var place = p.Catalog.Read(bare).Place;
                        if (!place.Showing && place.Reach != "yours-alone") { hidden = place.Id; break; }
                    }
                    Assert.NotNull(hidden);
                    foreach (bool walk in new[] { false, true })
                    {
                        var refused = Assert.Throws<AssistantRefusal>(() => p.Catalog.Show(new AssistantShowRequest
                        {
                            Program = "Claude Code",
                            Walk = walk,
                            Steps = new[] { StepAt(hidden) },
                        }));
                        Assert.Equal("not-available", refused.Code);
                        Assert.Equal(NotShownWords, refused.Words);
                        Assert.Null(p.Pointer.Callout);
                    }
                    // A hidden step among shown ones is refused the same way,
                    // and none of them plays.
                    var shown = p.Catalog.All().First(s => s.Id.StartsWith("settings/", StringComparison.Ordinal) && s.Showing && s.Reach == "free");
                    var among = Assert.Throws<AssistantRefusal>(() => p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Steps = new[] { StepAt(shown.Id), StepAt(hidden), StepAt(shown.Id) },
                    }));
                    Assert.Equal(NotShownWords, among.Words);
                    Assert.Null(p.Pointer.Callout);
                    // The positive control, in the same window: a place the
                    // page shows plays.
                    Assert.True(p.Catalog.Show(new AssistantShowRequest { Program = "Claude Code", Steps = new[] { StepAt(shown.Id) } }).Shown);
                    Until(window, () => p.Pointer.Callout != null);
                    Assert.NotNull(p.Pointer.Callout);
                    Assert.False(window.IsActive);

                    // A place on another device's panel is judged for that
                    // device, as read reads it: shown there, it plays, and
                    // that device's panel opens first.
                    var before = p.Vm.Devices.SelectedDevice;
                    var second = new DeviceRowViewModel { DeviceName = "Second Pad", InstanceGuid = Guid.NewGuid(), IsHidHideAvailable = true };
                    p.Vm.Devices.Devices.Add(second);
                    string secondId = AutomationDevices.IdOf(second.InstanceGuid);
                    var theirs = p.Catalog.Read("device/" + secondId).Children
                        .First(c => c.Showing && c.Reach != "yours-alone" && c.Id.EndsWith("@d" + secondId, StringComparison.Ordinal));
                    Assert.Same(before, p.Vm.Devices.SelectedDevice);
                    Assert.True(p.Catalog.Show(new AssistantShowRequest { Program = "Claude Code", Steps = new[] { StepAt(theirs.Id) } }).Shown);
                    Assert.Same(second, p.Vm.Devices.SelectedDevice);
                    // A device no row has is named as such.
                    Assert.Equal("unknown-device", Assert.Throws<AssistantRefusal>(() => p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Steps = new[] { StepAt(theirs.Id.Replace("@d" + secondId, "@dzzzzzzzz", StringComparison.Ordinal)) },
                    })).Code);

                    // Menu items are shown as before: a page's by its page and
                    // section, the tray's always. A profile card's own menu's
                    // item takes the card's ID.
                    p.Vm.Settings.ProfileItems.Add(new ProfileListItem { Id = "p1", Name = "Racing" });
                    var menus = p.Catalog.Find(null, "menu_item", 400).Places;
                    var pageItem = menus.First(m => m.Id.StartsWith("profiles.menuitem/", StringComparison.Ordinal) && m.Reach != "yours-alone");
                    var trayItem = menus.First(m => m.Id.StartsWith("window.menuitem/Tray/", StringComparison.Ordinal) && m.Reach != "yours-alone");
                    Assert.True(p.Catalog.Show(new AssistantShowRequest { Program = "Claude Code", Steps = new[] { StepAt(pageItem.Id) } }).Shown);
                    Assert.True(p.Catalog.Show(new AssistantShowRequest { Program = "Claude Code", Steps = new[] { StepAt(trayItem.Id) } }).Shown);
                }
                finally
                {
                    p.Pointer.Clear();
                    window.Close();
                }
            });
        }

        /// <summary>The folds that hold an element in what they open, outermost
        /// first: what a person opens to reach it.</summary>
        private static List<PadForge.Controls.FoldPart> FoldsOver(FrameworkElement element)
        {
            var folds = new List<PadForge.Controls.FoldPart>();
            DependencyObject below = element;
            for (var at = SearchLocator.Up(element); at != null; below = at, at = SearchLocator.Up(at))
                if (at is PadForge.Controls.FoldPart fold && ReferenceEquals(fold.Content, below))
                    folds.Insert(0, fold);
            return folds;
        }

        private static void Until(Window window, Func<bool> done, int ms = 4000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < ms)
            {
                System.Threading.Thread.Sleep(50);
                PageRenderDump.Settle(window.Dispatcher);
            }
        }

        /// <summary>The user's click on a ringed place, as WPF routes it.
        /// No input reaches the desktop.</summary>
        private static void Click(UIElement element)
            => element.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                Source = element,
            });

        [Fact]
        public void AWalk_RingsEachClosedCardOnTheWay_AndTheUserOpensIt()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                p.Window = window;
                var folds = new List<PadForge.Controls.FoldPart>();
                var kept = PadForge.Common.CardFoldState.Snapshot();
                try
                {
                    PageRenderDump.Settle(window.Dispatcher);
                    p.Vm.SelectedNavTag = "Pad1";
                    // A setting of the controller's page that sits in a fold
                    // card, under no More, which a program may point at.
                    string id = null;
                    FrameworkElement row = null;
                    var shown = p.Catalog.All().Where(s => s.Id.StartsWith("pad/", StringComparison.Ordinal) && s.Showing && s.Reach == "free" && !s.UnderMore).ToList();
                    foreach (var section in shown.GroupBy(s =>
                             {
                                 SearchIds.TryRead(s.Id, out string bare, out _, out _, out _);
                                 return p.Service.Index.First(e => p.Service.IdOf(e) == bare);
                             }).GroupBy(g => g.Key.Section))
                    {
                        foreach (var byEntry in section)
                        {
                            var entry = byEntry.Key;
                            if (entry.Tab >= 0) p.Pad.OpenTab(entry.Tab);
                            else p.Pad.OpenSection(entry.Section);
                            PageRenderDump.Settle(window.Dispatcher);
                            p.Service.Locator.Reset();
                            var label = p.Service.Locator.Find(entry, p.Service.Index);
                            var target = label == null ? null : SearchLocator.TargetOf(label, entry);
                            if (target == null) continue;
                            var over = FoldsOver(target);
                            if (over.Count == 0 || over.Any(f => f is not PadForge.Controls.FoldCard { CanOpen: true } || !f.IsVisible && !ReferenceEquals(f, over[0]))) continue;
                            if (!over[0].IsVisible) continue;
                            id = byEntry.First().Id;
                            row = target;
                            folds = over;
                            break;
                        }
                        if (id != null) break;
                    }
                    Assert.True(id != null, "no setting of the controller's page sits in a fold card");
                    foreach (var fold in folds) fold.ShowForSearch(false);
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.All(folds, f => Assert.False(f.IsExpanded));

                    var shownWalk = p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Walk = true,
                        Steps = new[] { new AssistantShowStep { Place = id, Words = "This one.", Advance = AssistantAdvance.Next } },
                    });
                    Assert.True(shownWalk.Shown);
                    Assert.True(shownWalk.Steps >= folds.Count + 1, "a step for each closed card comes before the place's");
                    for (int i = 0; i < folds.Count; i++)
                    {
                        var fold = folds[i];
                        Until(window, () => ReferenceEquals(p.Pointer.Target, fold));
                        // The card is ringed, closed: the user opens it.
                        Assert.Same(fold, p.Pointer.Target);
                        Assert.False(fold.IsExpanded);
                        Assert.Contains((string)((PadForge.Controls.FoldCard)fold).Header, p.Pointer.Callout.Words);
                        // A click that leaves it closed (its switch, not its
                        // line) keeps the step: the program never opens it.
                        Click(fold);
                        Until(window, () => !ReferenceEquals(p.Pointer.Target, fold), 600);
                        Assert.Same(fold, p.Pointer.Target);
                        Assert.False(fold.IsExpanded);
                        // The user opens it.
                        fold.ShowForSearch(true);
                        Click(fold);
                    }
                    Until(window, () => ReferenceEquals(p.Pointer.Target, row));
                    Assert.Same(row, p.Pointer.Target);
                    Assert.Equal("This one.", p.Pointer.Callout.Words);
                    Assert.False(window.IsActive);
                }
                finally
                {
                    p.Pointer.Clear();
                    foreach (var fold in folds) fold.EndSearch();
                    PadForge.Common.CardFoldState.Load(kept);
                    window.Close();
                }
            });
        }

        /// <summary>A walk to an option of a mapping row rings the row the
        /// option is on, and the user picks it: the walk picks no row and
        /// opens no row's details itself. A click that picks nothing keeps
        /// the step. Once the row is picked, the option rings in its
        /// details. A plain show picks and opens the row itself, as Search's
        /// landing does.</summary>
        [Fact]
        public void AWalkToARowsOption_RingsTheRow_AndTheUserPicksIt()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                p.Window = window;
                try
                {
                    p.Vm.SelectedNavTag = "Pad1";
                    p.Pad.OpenTab(PadViewModel.MappingsTabIndex);
                    PageRenderDump.Settle(window.Dispatcher);
                    var grid = (System.Windows.Controls.DataGrid)p.Pad.FindName("MappingDataGrid");
                    grid.SelectedItem = null;
                    PageRenderDump.Settle(window.Dispatcher);
                    var entry = p.Service.Index.First(e => e.RowOptions && e.Label?.Key == "Pad_Invert");
                    string bare = p.Service.IdOf(entry);
                    string id = p.Catalog.All().First(s => SearchIds.TryRead(s.Id, out string b, out _, out _, out _) && b == bare).Id;
                    bool NoRowOpen() => grid.Items.Cast<object>()
                        .Select(i => grid.ItemContainerGenerator.ContainerFromItem(i) as System.Windows.Controls.DataGridRow)
                        .All(r => r == null || r.DetailsVisibility != Visibility.Visible);

                    var walk = p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Walk = true,
                        Steps = new[] { new AssistantShowStep { Place = id, Words = "This one.", Advance = AssistantAdvance.Next } },
                    });
                    Assert.True(walk.Shown);
                    // The row the option is on rings, not picked and not open.
                    Until(window, () => p.Pointer.Target is System.Windows.Controls.DataGridRow);
                    var row = Assert.IsType<System.Windows.Controls.DataGridRow>(p.Pointer.Target);
                    Assert.Null(grid.SelectedItem);
                    Assert.True(NoRowOpen(), "a row's details opened");
                    Assert.Contains(((MappingItem)row.Item).TargetLabel, p.Pointer.Callout.Words);
                    // A click that picks nothing keeps the step.
                    Click(row);
                    Until(window, () => !ReferenceEquals(p.Pointer.Target, row), 600);
                    Assert.Same(row, p.Pointer.Target);
                    Assert.Null(grid.SelectedItem);
                    // The user picks the row: the option rings in its details.
                    grid.SelectedItem = row.Item;
                    Click(row);
                    Until(window, () => (p.Pointer.Target as FrameworkElement)?.Name == "InvertPart");
                    Assert.Equal("InvertPart", (p.Pointer.Target as FrameworkElement)?.Name);
                    Assert.Same(row.Item, grid.SelectedItem);
                    Assert.Equal("This one.", p.Pointer.Callout.Words);
                    p.Pointer.Clear();

                    // A plain show picks and opens the row itself.
                    grid.SelectedItem = null;
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.True(p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Steps = new[] { new AssistantShowStep { Place = id, Words = "This one.", Advance = AssistantAdvance.Next } },
                    }).Shown);
                    Until(window, () => (p.Pointer.Target as FrameworkElement)?.Name == "InvertPart");
                    Assert.Equal("InvertPart", (p.Pointer.Target as FrameworkElement)?.Name);
                    Assert.NotNull(grid.SelectedItem);
                    p.Pointer.Clear();

                    // A list whose box hides every row that holds the
                    // option: the step names the controller's first such
                    // row, which the user finds.
                    grid.SelectedItem = null;
                    p.Vm.Pads[0].MappingInputSearch = "no row says this";
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.Empty(grid.Items);
                    var first = p.Vm.Pads[0].RowsOfSlot.First(r => !r.IsLineKeyMode);
                    Assert.True(p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Walk = true,
                        Steps = new[] { new AssistantShowStep { Place = id, Words = "This one.", Advance = AssistantAdvance.Next } },
                    }).Shown);
                    Until(window, () => p.Pointer.Callout != null, 3000);
                    Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format, first.TargetLabel), p.Pointer.Callout?.Words);
                }
                finally
                {
                    p.Pointer.Clear();
                    p.Vm.Pads[0].MappingInputSearch = string.Empty;
                    window.Close();
                }
            });
        }

        /// <summary>Show without a walk opens the place, folds and all, as
        /// Search's landing does (AS-75): only a walk leaves the clicking to
        /// the user.</summary>
        [Fact]
        public void APlainShow_OpensTheCardsOnTheWayItself()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                p.Window = window;
                var folds = new List<PadForge.Controls.FoldPart>();
                var kept = PadForge.Common.CardFoldState.Snapshot();
                try
                {
                    PageRenderDump.Settle(window.Dispatcher);
                    p.Vm.SelectedNavTag = "Pad1";
                    string id = null;
                    foreach (var s in p.Catalog.All().Where(s => s.Id.StartsWith("pad/", StringComparison.Ordinal) && s.Showing && s.Reach == "free"))
                    {
                        SearchIds.TryRead(s.Id, out string bare, out _, out _, out _);
                        var entry = p.Service.Index.First(e => p.Service.IdOf(e) == bare);
                        if (entry.Tab >= 0) p.Pad.OpenTab(entry.Tab);
                        else p.Pad.OpenSection(entry.Section);
                        PageRenderDump.Settle(window.Dispatcher);
                        p.Service.Locator.Reset();
                        var label = p.Service.Locator.Find(entry, p.Service.Index);
                        var target = label == null ? null : SearchLocator.TargetOf(label, entry);
                        if (target == null) continue;
                        var over = FoldsOver(target);
                        if (over.Count == 0 || !over[0].IsVisible) continue;
                        id = s.Id;
                        folds = over;
                        break;
                    }
                    Assert.NotNull(id);
                    foreach (var fold in folds) fold.ShowForSearch(false);
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.True(p.Catalog.Show(new AssistantShowRequest { Program = "Claude Code", Steps = new[] { StepAt(id) } }).Shown);
                    Until(window, () => folds.All(f => f.IsExpanded));
                    Assert.All(folds, f => Assert.True(f.IsExpanded));
                }
                finally
                {
                    p.Pointer.Clear();
                    foreach (var fold in folds) fold.EndSearch();
                    PadForge.Common.CardFoldState.Load(kept);
                    window.Close();
                }
            });
        }

        /// <summary>A walk to a device that is not connected rings the Not
        /// Connected card that holds its row and leaves it shut until the
        /// user opens it: the walk's last step goes to the page and opens no
        /// card, where opening the device's panel would.</summary>
        [Fact]
        public void AWalkToADeviceNotConnected_RingsItsCard_AndNeverOpensIt()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Devices, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                // A device connected, so the one that is not stands under the
                // Not Connected card.
                var online = new DeviceRowViewModel { DeviceName = "Online Pad", InstanceGuid = Guid.NewGuid(), IsHidHideAvailable = true, IsOnline = true };
                p.Vm.Devices.Devices.Add(online);
                var offline = p.Vm.Devices.Devices[0];
                Assert.False(offline.IsOnline);
                var kept = PadForge.Common.CardFoldState.Snapshot();
                var fold = (PadForge.Controls.FoldCard)p.Devices.FindName("NotConnectedFold");
                window.Show();
                p.Window = window;
                try
                {
                    p.Vm.Devices.EnsureListViews();
                    p.Vm.Devices.RefreshCounts();
                    p.Vm.SelectedNavTag = "Devices";
                    fold.ShowForSearch(false);
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.True(fold.IsVisible);
                    var shown = p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Walk = true,
                        Steps = new[] { new AssistantShowStep { Place = "device/" + AutomationDevices.IdOf(offline.InstanceGuid), Words = "This one.", Advance = AssistantAdvance.Next } },
                    });
                    Assert.True(shown.Shown);
                    Until(window, () => ReferenceEquals(p.Pointer.Target, fold));
                    Assert.Same(fold, p.Pointer.Target);
                    Assert.False(fold.IsExpanded);
                    // The user opens it, and the device's row is ringed.
                    fold.ShowForSearch(true);
                    Click(fold);
                    Until(window, () => p.Pointer.Target is System.Windows.Controls.ListBoxItem item && ReferenceEquals(item.DataContext, offline));
                    Assert.True(p.Pointer.Target is System.Windows.Controls.ListBoxItem { DataContext: var row } && ReferenceEquals(row, offline));
                    Assert.Equal("This one.", p.Pointer.Callout.Words);

                    // A device the search box hides stands in no list: the
                    // walk's last step opens its panel, which empties the
                    // search, and its row is ringed.
                    p.Pointer.Clear();
                    p.Vm.Devices.SearchText = "No Such Device";
                    p.Vm.Devices.RefreshCounts();
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.True(p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Walk = true,
                        Steps = new[] { new AssistantShowStep { Place = "device/" + AutomationDevices.IdOf(offline.InstanceGuid), Words = "Here.", Advance = AssistantAdvance.Next } },
                    }).Shown);
                    Until(window, () => p.Pointer.Target is System.Windows.Controls.ListBoxItem item && ReferenceEquals(item.DataContext, offline));
                    Assert.Equal(string.Empty, p.Vm.Devices.SearchText);
                    Assert.True(p.Pointer.Target is System.Windows.Controls.ListBoxItem { DataContext: var hidden } && ReferenceEquals(hidden, offline));
                }
                finally
                {
                    p.Pointer.Clear();
                    fold.EndSearch();
                    PadForge.Common.CardFoldState.Load(kept);
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

        /// <summary>A dialog's button that would bring a profile into force,
        /// a profile Delete's confirm or Get Profiles' Save and Apply, names
        /// on the card each extra the switch would turn on that listens on
        /// the network, as the profile switch's card does (#600). It names
        /// nothing when the switch turns nothing on. A press the user allowed
        /// is refused when, by then, it would turn on what the card did not
        /// name. A profile card's Delete reaches the delete for that
        /// card.</summary>
        [Fact]
        public void AButtonThatBringsAProfileIntoForce_NamesWhatItWouldTurnOn()
        {
            WithPages(p =>
            {
                var s = PadForge.Resources.Strings.Strings.Instance;
                // A profile card's own Delete: the delete of that card.
                var racing = new ProfileListItem { Id = "p1", Name = "Racing" };
                var rally = new ProfileListItem { Id = "p2", Name = "Rally" };
                p.Vm.Settings.ProfileItems.Add(racing);
                p.Vm.Settings.ProfileItems.Add(rally);
                var profiles = new Window
                {
                    Content = p.Profiles, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                profiles.Show();
                ProfileListItem deleting = null;
                EventHandler onDelete = (_, _) => deleting = p.Vm.Settings.SelectedProfile;
                p.Vm.Settings.DeleteProfileRequested += onDelete;
                try
                {
                    PageRenderDump.Settle(profiles.Dispatcher);
                    p.Catalog.PlanPress("profiles.menuitem/ProfileListBox/Common_Delete@i1").Press();
                    PageRenderDump.Settle(profiles.Dispatcher);
                    Assert.Same(rally, deleting);
                }
                finally
                {
                    p.Vm.Settings.DeleteProfileRequested -= onDelete;
                    profiles.Close();
                }

                // The confirm that Delete opens, with what its Delete would
                // turn on.
                var owner = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true, Content = new System.Windows.Controls.Grid() };
                owner.Show();
                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Delete Rally?" });
                var cancel = new System.Windows.Controls.Button { Content = "Cancel" };
                var delete = new System.Windows.Controls.Button { Content = "Delete" };
                var starts = new List<string> { AssistantToolWords.TurnsOn(s.Dashboard_WebController) };
                PadForge.Common.Reach.SetStarts(delete, () => starts);
                panel.Children.Add(cancel);
                panel.Children.Add(delete);
                var dialog = new Window { Title = "Profiles", Content = panel, Owner = owner, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 300, Height = 200, WindowStyle = WindowStyle.None, AllowsTransparency = true };
                PadForge.Common.Reach.SetClass(dialog, PadForge.Common.ReachClass.Asks);
                var catalog = new AssistantCatalog(p.Service, new SearchNavigator(p.Service, p.PageOf, p.Vm, _ => { }, _ => { }, () => { }),
                    p.PageOf, p.Vm, pad => p.Pad.DataContext = pad, () => AssistantWindowPlace.InFront, new AssistantCatalogParts { MainWindow = () => owner });
                dialog.Show();
                try
                {
                    PadForge.Services.Catalog.DialogRegistry.Register(dialog);
                    PageRenderDump.Settle(dialog.Dispatcher);
                    string id = catalog.Read("open/Window").Buttons.Single(b => b.Name == "Delete").Id;
                    string always = AssistantToolWords.Reason(PadForge.Common.ReachReason.None);
                    Assert.Equal(new[] { always, AssistantToolWords.TurnsOn(s.Dashboard_WebController), AssistantToolWords.ListensOnNetwork() },
                        catalog.PlanPress(id).Ask.Detail);
                    // A switch that turns nothing on names nothing.
                    starts.Clear();
                    var plain = catalog.PlanPress(id);
                    Assert.Equal(new[] { always }, plain.Ask.Detail);
                    // By the time the user allows it, it would turn on what the
                    // card did not name: refused before it is pressed.
                    starts.Add(AssistantToolWords.TurnsOn(s.Dashboard_MotionServer));
                    Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => plain.Check()).Code);
                    var named = catalog.PlanPress(id);
                    named.Check();
                }
                finally
                {
                    dialog.Close();
                    owner.Close();
                }
            });
        }

        /// <summary>What deleting a profile would turn on that listens on the
        /// network: the default's, for the profile in force, which a delete
        /// puts the default in force for. Nothing for any other
        /// profile.</summary>
        [Fact]
        public void DeletingTheProfileInForce_NamesWhatTheDefaultWouldTurnOn()
        {
            WithPages(p =>
            {
                var profiles = SettingsManager.Profiles;
                string active = SettingsManager.ActiveProfileId;
                var pending = SettingsManager.PendingDefaultSnapshot;
                try
                {
                    SettingsManager.Profiles = new List<PadForge.Services.ProfileData>
                    {
                        new() { Id = "racing-1", Name = "Racing" },
                        new() { Id = "plain-1", Name = "Plain" },
                    };
                    SettingsManager.ActiveProfileId = null;
                    var input = new PadForge.Services.InputService(p.Vm) { SettingsService = new PadForge.Services.SettingsService(p.Vm) };
                    // The default listens on the network.
                    p.Vm.Dashboard.EnableWebController = true;
                    input.RefreshDefaultSnapshot();
                    // Racing is in force, and turned it off.
                    SettingsManager.ActiveProfileId = "racing-1";
                    p.Vm.Dashboard.EnableWebController = false;
                    var s = PadForge.Resources.Strings.Strings.Instance;
                    Assert.Equal(new[] { s.Dashboard_WebController }, input.DeleteNetworkStarts("racing-1").Extras);
                    Assert.True(input.DeleteNetworkStarts("plain-1").Nothing);
                }
                finally
                {
                    SettingsManager.Profiles = profiles;
                    SettingsManager.ActiveProfileId = active;
                    SettingsManager.PendingDefaultSnapshot = pending;
                    p.Vm.Dashboard.EnableWebController = false;
                }
            });
        }

        /// <summary>A program's turning on Switch Profiles by Game (#600) asks
        /// with a line for each listener of each profile with a game, and of
        /// the default, which the watcher returns to, on or off now, and asks
        /// nothing when none holds one. Turning it off never asks. Test not
        /// run yet for the on or off now and the default's line.</summary>
        [Fact]
        public void SwitchingByGame_NamesTheProfilesItWouldPutInForce_AndAsksNothingWhenNoneListens()
        {
            WithPages(p =>
            {
                var profiles = SettingsManager.Profiles;
                string active = SettingsManager.ActiveProfileId;
                try
                {
                    var s = PadForge.Resources.Strings.Strings.Instance;
                    SettingsManager.Profiles = new List<PadForge.Services.ProfileData>
                    {
                        new() { Id = "racing-1", Name = "Racing", ExecutableNames = @"C:\Games\race.exe", EnableWebController = true },
                    };
                    SettingsManager.ActiveProfileId = null;
                    p.Vm.Settings.EnableAutoProfileSwitching = false;
                    p.Vm.Dashboard.EnableWebController = false;
                    var entry = p.Service.Index.Single(e => e.Kind == SearchKind.Setting && e.Value?.Path == nameof(SettingsViewModel.EnableAutoProfileSwitching));
                    string id = p.Service.IdOf(entry);
                    var on = JsonSerializer.SerializeToElement(true);
                    var ask = p.Catalog.AskFor(id, on);
                    Assert.NotNull(ask);
                    Assert.Equal(AssistantToolWords.AskSetting(s.Profiles_AutoSwitch), ask.Title);
                    Assert.Equal(new[]
                    {
                        AssistantToolWords.SetsTo(s.Profiles_AutoSwitch, s.Common_On),
                        AssistantToolWords.ProfileTurnsOn("Racing", s.Dashboard_WebController),
                        s.Automation_Ask_Network,
                    }, ask.Detail);
                    Assert.Null(p.Catalog.AskFor(id, JsonSerializer.SerializeToElement(false)));
                    // On now, and still named, with the default in force,
                    // which holds the live setup.
                    p.Vm.Dashboard.EnableWebController = true;
                    Assert.Equal(new[]
                    {
                        AssistantToolWords.SetsTo(s.Profiles_AutoSwitch, s.Common_On),
                        AssistantToolWords.ProfileTurnsOn("Racing", s.Dashboard_WebController),
                        AssistantToolWords.ProfileTurnsOn(s.Profile_Default, s.Dashboard_WebController),
                        s.Automation_Ask_Network,
                    }, p.Catalog.AskFor(id, on).Detail);
                    // No profile holds a listener: nothing to ask.
                    SettingsManager.Profiles = new List<PadForge.Services.ProfileData>
                    {
                        new() { Id = "plain-1", Name = "Plain", ExecutableNames = @"C:\Games\plain.exe" },
                    };
                    p.Vm.Dashboard.EnableWebController = false;
                    Assert.Null(p.Catalog.AskFor(id, on));
                }
                finally
                {
                    SettingsManager.Profiles = profiles;
                    SettingsManager.ActiveProfileId = active;
                    p.Vm.Dashboard.EnableWebController = false;
                }
            });
        }

        /// <summary>A program's turning on an extra that listens on the network
        /// (#600) names the profile it goes into: the named profile in force,
        /// or the default. One on already asks nothing. Head Tracking's UDP
        /// input names a named profile in force, and with the default in
        /// force asks by its reason alone, since the default holds no opinion
        /// of it. Test not run yet.</summary>
        [Fact]
        public void TurningOnAListener_NamesTheProfileItGoesInto_AndAsksNothingWhenItIsOnAlready()
        {
            WithPages(p =>
            {
                var profiles = SettingsManager.Profiles;
                string active = SettingsManager.ActiveProfileId;
                bool udp = HeadTrackingRuntime.Enabled, free = HeadTrackingRuntime.FreeTrackEnabled, xr = HeadTrackingRuntime.OpenXrEnabled;
                try
                {
                    var s = PadForge.Resources.Strings.Strings.Instance;
                    SettingsManager.Profiles = new List<PadForge.Services.ProfileData> { new() { Id = "racing-1", Name = "Racing" } };
                    SettingsManager.ActiveProfileId = null;
                    var dash = p.Vm.Dashboard;
                    dash.EnableWebController = false;
                    dash.HeadTrackingEnabled = false;
                    var on = JsonSerializer.SerializeToElement(true);
                    string Id(string path) => p.Service.IdOf(p.Service.Index.First(e => e.Kind == SearchKind.Setting && e.Value?.Path == path));

                    string web = Id(nameof(DashboardViewModel.EnableWebController));
                    var ask = p.Catalog.AskFor(web, on);
                    Assert.Equal(3, ask.Detail.Count);
                    Assert.Equal(AssistantToolWords.ProfileTurnsOn(s.Profile_Default, s.Dashboard_WebController), ask.Detail[1]);
                    Assert.Equal(s.Automation_Ask_Network, ask.Detail[2]);
                    SettingsManager.ActiveProfileId = "racing-1";
                    Assert.Equal(AssistantToolWords.ProfileTurnsOn("Racing", s.Dashboard_WebController), p.Catalog.AskFor(web, on).Detail[1]);
                    dash.EnableWebController = true;
                    Assert.Null(p.Catalog.AskFor(web, on));

                    string udpSwitch = Id(nameof(DashboardViewModel.HeadTrackingEnabled));
                    Assert.Equal(AssistantToolWords.ProfileTurnsOn("Racing", s.Automation_Ask_HeadTrackingUdp), p.Catalog.AskFor(udpSwitch, on).Detail[1]);
                    SettingsManager.ActiveProfileId = null;
                    var byReason = p.Catalog.AskFor(udpSwitch, on);
                    Assert.Equal(2, byReason.Detail.Count);
                    Assert.Equal(s.Automation_Ask_Network, byReason.Detail[1]);
                }
                finally
                {
                    SettingsManager.Profiles = profiles;
                    SettingsManager.ActiveProfileId = active;
                    p.Vm.Dashboard.EnableWebController = false;
                    HeadTrackingRuntime.Enabled = udp;
                    HeadTrackingRuntime.FreeTrackEnabled = free;
                    HeadTrackingRuntime.OpenXrEnabled = xr;
                }
            });
        }

        /// <summary>A dialog's button that asks for the network's sake, with a
        /// reader of what its press would turn on (#600), asks with the
        /// reader's lines, each naming the profile and the extra, then the
        /// network's line, and asks nothing when the reader names nothing.
        /// With no reader it asks by its reason. A press the user allowed is
        /// refused when, by then, it would turn on what the card did not
        /// name.</summary>
        [Fact]
        public void ANetworkButtonWithAReader_AsksOnlyWhenItNamesSomething_WithTheProfile()
        {
            WithPages(p =>
            {
                var s = PadForge.Resources.Strings.Strings.Instance;
                var owner = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true, Content = new System.Windows.Controls.Grid() };
                owner.Show();
                var save = new System.Windows.Controls.Button { Content = "Save" };
                PadForge.Common.Reach.SetClass(save, PadForge.Common.ReachClass.Asks);
                PadForge.Common.Reach.SetWhy(save, PadForge.Common.ReachReason.Network);
                var starts = new List<string> { AssistantToolWords.ProfileTurnsOn("Racing", s.Dashboard_WebController) };
                PadForge.Common.Reach.SetStarts(save, () => starts);
                var dialog = new Window { Title = "Edit Profile", Content = save, Owner = owner, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 300, Height = 200, WindowStyle = WindowStyle.None, AllowsTransparency = true };
                PadForge.Common.Reach.SetClass(dialog, PadForge.Common.ReachClass.Free);
                var catalog = new AssistantCatalog(p.Service, new SearchNavigator(p.Service, p.PageOf, p.Vm, _ => { }, _ => { }, () => { }),
                    p.PageOf, p.Vm, pad => p.Pad.DataContext = pad, () => AssistantWindowPlace.InFront, new AssistantCatalogParts { MainWindow = () => owner });
                dialog.Show();
                try
                {
                    PadForge.Services.Catalog.DialogRegistry.Register(dialog);
                    PageRenderDump.Settle(dialog.Dispatcher);
                    string id = catalog.Read("open/Window").Buttons.Single().Id;
                    var plan = catalog.PlanPress(id);
                    Assert.Equal(AssistantToolWords.AskPress("Save"), plan.Ask.Title);
                    Assert.Equal(new[] { AssistantToolWords.ProfileTurnsOn("Racing", s.Dashboard_WebController), AssistantToolWords.ListensOnNetwork() },
                        plan.Ask.Detail);
                    starts.Add(AssistantToolWords.ProfileTurnsOn("Racing", s.Dashboard_MotionServer));
                    Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => plan.Check()).Code);

                    starts.Clear();
                    var nothing = catalog.PlanPress(id);
                    Assert.Null(nothing.Ask);
                    nothing.Check();
                    starts.Add(AssistantToolWords.ProfileTurnsOn("Racing", s.Dashboard_GameMods));
                    Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => nothing.Check()).Code);

                    PadForge.Common.Reach.SetStarts(save, null);
                    Assert.Equal(new[] { AssistantToolWords.ListensOnNetwork() }, catalog.PlanPress(id).Ask.Detail);
                }
                finally
                {
                    dialog.Close();
                    owner.Close();
                }
            });
        }

        /// <summary>A layer tab's Paste and Clear ask a program first (#600),
        /// as a controller's Paste and its Clear All's confirm do. Paste names
        /// the keys the rows a layer's Copy kept would press on a keyboard and
        /// mouse controller, and pastes the rows its card read, whatever was
        /// copied since. With nothing copied it is refused. Clear asks with
        /// the confirm's words, and empties the layer it names.</summary>
        [Fact]
        public void ALayerTabsPasteAndClear_AskAsTheControllersDo()
        {
            WithPages(p =>
            {
                var s = PadForge.Resources.Strings.Strings.Instance;
                var sets = SettingsManager.SlotMappingSets;
                var held = sets[0];
                var clipboard = typeof(PadPage).GetField("_shiftLayerClipboard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Assert.NotNull(clipboard);
                object copiedBefore = clipboard.GetValue(null);
                var pad = p.Vm.Pads[0];
                pad.OutputType = PadForge.Engine.VirtualControllerType.KeyboardMouse;
                var key = pad.Mappings.First(m => !string.IsNullOrEmpty(m.TargetSettingName));
                var row = new PadForge.Engine.Data.MappingRow { Target = key.TargetSettingName, LayerMask = "Shift1" };
                row.Sources.Add(new PadForge.Engine.Data.MappingSource { Descriptor = "Button 1" });
                sets[0] = new PadForge.Engine.Data.MappingSet { Rows = new List<PadForge.Engine.Data.MappingRow> { row } };
                pad.RebuildLayerTabs(new[] { new PadForge.Engine.Data.ShiftActivator { LayerMask = "Shift1", LayerName = "Aim" } });
                var padWindow = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                padWindow.Show();
                p.Window = padWindow;
                try
                {
                    p.Vm.SelectedNavTag = "Pad1";
                    p.Pad.OpenTab(PadViewModel.MappingsTabIndex);
                    PageRenderDump.Settle(padWindow.Dispatcher);
                    const string strip = "pad.menuitem/ShiftLayerTabStrip/";
                    string always = AssistantToolWords.Reason(PadForge.Common.ReachReason.None);

                    clipboard.SetValue(null, null);
                    Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => p.Catalog.PlanPress(strip + "Pad_Shift_Paste@c1i0")).Code);

                    // Copy keeps Aim's row, and stays free.
                    var copy = p.Catalog.PlanPress(strip + "Pad_Shift_Copy@c1i1");
                    Assert.Null(copy.Ask);
                    copy.Press();
                    PageRenderDump.Settle(padWindow.Dispatcher);

                    var paste = p.Catalog.PlanPress(strip + "Pad_Shift_Paste@c1i0");
                    Assert.Equal(AssistantToolWords.AskPress(s.Pad_Shift_Paste), paste.Ask.Title);
                    Assert.Equal(new[] { always, AssistantToolWords.KeyboardMouseLine(), AssistantToolWords.KeyLine(key.TargetLabel, "Button 1") }, paste.Ask.Detail);
                    // On a controller of another type the rows press nothing
                    // in Windows, and the card asks with the confirm's words
                    // alone.
                    pad.OutputType = PadForge.Engine.VirtualControllerType.Xbox;
                    PageRenderDump.Settle(padWindow.Dispatcher);
                    Assert.Equal(new[] { always }, p.Catalog.PlanPress(strip + "Pad_Shift_Paste@c1i0").Ask.Detail);
                    pad.OutputType = PadForge.Engine.VirtualControllerType.KeyboardMouse;
                    PageRenderDump.Settle(padWindow.Dispatcher);
                    // A Copy of Base, which holds no row, while the card shows:
                    // the paste lands the row the card read.
                    p.Catalog.PlanPress(strip + "Pad_Shift_Copy@c1i0").Press();
                    PageRenderDump.Settle(padWindow.Dispatcher);
                    paste.Press();
                    PageRenderDump.Settle(padWindow.Dispatcher);
                    Assert.Single(sets[0].Rows, r => r.LayerMask == "Base" && r.Target == key.TargetSettingName);

                    // Clear asks with the confirm's words, and empties Aim alone.
                    var clear = p.Catalog.PlanPress(strip + "Pad_Shift_Clear@c1i1");
                    Assert.Equal(new[] { always }, clear.Ask.Detail);
                    clear.Press();
                    PageRenderDump.Settle(padWindow.Dispatcher);
                    Assert.DoesNotContain(sets[0].Rows, r => r.LayerMask == "Shift1");
                    Assert.Single(sets[0].Rows, r => r.LayerMask == "Base");
                }
                finally
                {
                    p.Pointer.Clear();
                    padWindow.Close();
                    sets[0] = held;
                    clipboard.SetValue(null, copiedBefore);
                }
            });
        }

        /// <summary>Find a Mapping (#600 item 48, AS-67) as a program reaches
        /// it: the press of Find a Mapping opens it and answers with it, a
        /// read gives its list and search box with the dialog's class, set
        /// picks a row by its words or its place, past the rows the list
        /// draws, refuses words two rows share, and Apply, pressed by its ID,
        /// applies the row set. Driven inside the dialog's own modal loop, as
        /// the Devices page opens it, with a timer that closes it on a
        /// failure.</summary>
        [Fact]
        public void FindAMapping_TakesARowAProgramPicks_AndApplyAppliesIt()
        {
            WithPages(p =>
            {
                var owner = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = new System.Windows.Controls.Grid() };
                owner.Show();
                var catalog = new AssistantCatalog(p.Service, new SearchNavigator(p.Service, p.PageOf, p.Vm, _ => { }, _ => { }, () => { }),
                    p.PageOf, p.Vm, pad => p.Pad.DataContext = pad, () => AssistantWindowPlace.InFront, new AssistantCatalogParts { MainWindow = () => owner });
                PadForge.Common.Input.SdlMappingRow Row(string guid, string name)
                {
                    Assert.True(PadForge.Common.Input.SdlMappingRow.TryParse(guid + "," + name + ",a:b0,b:b1,platform:Windows,", out var row, out _));
                    return row;
                }
                // More rows than the list draws, and two that read the same.
                var rows = new List<PadForge.Services.SdlFindRow>();
                for (int i = 0; i < 60; i++)
                    rows.Add(new(Row("030000000d0f0000" + (0x1000 + i).ToString("x4") + "000000000000", "Arcade Stick " + i.ToString("00")), PadForge.Common.Input.SdlMappingSource.Community, false)
                        { Detail = "The community copy built in" });
                rows.Add(new(Row("030000000d0f00009000000000000000", "Twin Stick"), PadForge.Common.Input.SdlMappingSource.Sdl, false) { Detail = "SDL" });
                rows.Add(new(Row("030000000d0f00009100000000000000", "Twin Stick"), PadForge.Common.Input.SdlMappingSource.Sdl, false) { Detail = "SDL" });
                var dialog = new SdlMappingDialog(SdlMappingDialog.Mode.Find, "Arcade Stick", "030000000d0f00009900000000000000", new[] { "Xbox #1" })
                {
                    Owner = owner, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                };
                dialog.SetRows(rows);
                string failure = null;
                var picked = rows[45];
                var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                guard.Tick += (_, _) =>
                {
                    guard.Stop();
                    failure ??= "the dialog was still open after 30 seconds";
                    dialog.Close();
                };
                dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        PadForge.Services.Catalog.DialogRegistry.Register(dialog);
                        var open = Assert.Single(catalog.Dialogs().Open, d => d.Id.StartsWith("dialog.dialog/SdlMappingDialog/", StringComparison.Ordinal));
                        var read = catalog.Read(open.Id);
                        var list = Assert.Single(read.Children, c => c.Id == SdlMappingDialog.FindListId);
                        Assert.Equal("list", list.Takes);
                        Assert.Equal("free", list.Reach);
                        Assert.Equal(rows.Count, list.Choices.Count);
                        Assert.Equal(rows[0].ReadName, list.ValueText);
                        Assert.Contains(read.Children, c => c.Id == SdlMappingDialog.SearchBoxId && c.Takes == "text");
                        // The list's rows read as their words, never as the
                        // data behind them.
                        Assert.DoesNotContain(read.Lists, l => l.Rows.Any(r => r.Contains("SdlFindRow", StringComparison.Ordinal)));

                        // A row past what the list draws, by its words.
                        catalog.Set(SdlMappingDialog.FindListId, JsonSerializer.SerializeToElement(picked.ReadName));
                        Assert.Same(picked.Row, dialog.PickedRow);
                        Assert.Equal(picked.ReadName, catalog.Read(SdlMappingDialog.FindListId).Place.ValueText);
                        // Words two rows share pick neither.
                        var twins = Assert.Throws<AssistantRefusal>(() => catalog.Set(SdlMappingDialog.FindListId, JsonSerializer.SerializeToElement("Twin Stick, SDL")));
                        Assert.Equal("out-of-range", twins.Code);
                        Assert.Same(picked.Row, dialog.PickedRow);
                        // By its place, and narrowed by the search box first.
                        catalog.Set(SdlMappingDialog.SearchBoxId, JsonSerializer.SerializeToElement("9100"));
                        Assert.Single(catalog.Read(SdlMappingDialog.FindListId).Place.Choices);
                        catalog.Set(SdlMappingDialog.FindListId, JsonSerializer.SerializeToElement(0));
                        Assert.Same(rows[^1].Row, dialog.PickedRow);
                        catalog.Set(SdlMappingDialog.SearchBoxId, JsonSerializer.SerializeToElement(string.Empty));
                        catalog.Set(SdlMappingDialog.FindListId, JsonSerializer.SerializeToElement(45));
                        Assert.Same(picked.Row, dialog.PickedRow);

                        // Apply, by its ID from the read, at the dialog's class.
                        var apply = Assert.Single(catalog.Read(open.Id).Buttons, b => b.Name == PadForge.Resources.Strings.Strings.Instance.Common_Apply);
                        Assert.True(apply.Enabled);
                        var plan = catalog.PlanPress(apply.Id);
                        Assert.Null(plan.Ask);
                        plan.Press();
                    }
                    catch (Exception error)
                    {
                        failure = error.ToString();
                        dialog.Close();
                    }
                }));
                guard.Start();
                bool? applied;
                try { applied = dialog.ShowDialog(); }
                finally
                {
                    guard.Stop();
                    owner.Close();
                }
                Assert.True(failure == null, failure);
                Assert.True(applied);
                Assert.Same(picked.Row, dialog.PickedRow);
                // Gone with the dialog.
                Assert.DoesNotContain(PadForge.Services.Catalog.LiveCatalog.Items(), i => i.Id == SdlMappingDialog.FindListId);
            });
        }

        /// <summary>A person types a letter in Find a Mapping's list and the
        /// list jumps to the first row whose name starts with it, as every
        /// list does: the rows are matched by their names, not by the data
        /// behind them.</summary>
        [Fact]
        public void FindAMappingsList_JumpsByARowsName_AsAPersonTypes()
        {
            WithPages(p =>
            {
                PadForge.Common.Input.SdlMappingRow Row(string guid, string name)
                {
                    Assert.True(PadForge.Common.Input.SdlMappingRow.TryParse(guid + "," + name + ",a:b0,b:b1,platform:Windows,", out var row, out _));
                    return row;
                }
                var dialog = new SdlMappingDialog(SdlMappingDialog.Mode.Find, "Arcade Stick", "030000000d0f00009900000000000000", new[] { "Xbox #1" })
                {
                    Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                };
                dialog.SetRows(new List<PadForge.Services.SdlFindRow>
                {
                    new(Row("030000000d0f00009200000000000000", "Arcade Stick"), PadForge.Common.Input.SdlMappingSource.Community, false) { Detail = "Community" },
                    new(Row("030000000d0f00008500000000000000", "Fight Pad"), PadForge.Common.Input.SdlMappingSource.Sdl, false) { Detail = "SDL" },
                    new(Row("030000000d0f0000a000000000000000", "Twin Stick"), PadForge.Common.Input.SdlMappingSource.Sdl, false) { Detail = "SDL" },
                });
                dialog.Show();
                try
                {
                    PageRenderDump.Settle(dialog.Dispatcher);
                    Assert.Equal(0, dialog.FindList.SelectedIndex);
                    var typed = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, dialog.FindList, "T"))
                    {
                        RoutedEvent = System.Windows.Input.TextCompositionManager.TextInputEvent,
                    };
                    dialog.FindList.RaiseEvent(typed);
                    PageRenderDump.Settle(dialog.Dispatcher);
                    Assert.Equal(2, dialog.FindList.SelectedIndex);
                }
                finally
                {
                    dialog.Close();
                }
            });
        }

        /// <summary>A row's data in a list, whose own text is the record's,
        /// as a list draws no row with it.</summary>
        private sealed record ListRow(string Name, string Detail);

        /// <summary>A dialog's lists read as the rows they draw (#600 item
        /// 8): a row the list has drawn and one it has not, past what it
        /// shows, read the same words, the text its template shows, and a
        /// list that names a member reads that member. Never the data
        /// behind a row.</summary>
        [Fact]
        public void ADialogsLists_ReadAsTheRowsTheyDraw_DrawnOrNot()
        {
            WithPages(p =>
            {
                var owner = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = new System.Windows.Controls.Grid() };
                owner.Show();
                var templated = new System.Windows.Controls.ListBox
                {
                    MaxHeight = 80,
                    ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
                        "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel>"
                        + "<TextBlock Text='{Binding Name}'/><TextBlock Text='{Binding Detail}'/></StackPanel></DataTemplate>"),
                    ItemsSource = Enumerable.Range(0, 60).Select(i => new ListRow("Row " + i, "Detail " + i)).ToList(),
                };
                var named = new System.Windows.Controls.ListBox
                {
                    MaxHeight = 80,
                    DisplayMemberPath = nameof(ListRow.Name),
                    ItemsSource = Enumerable.Range(0, 60).Select(i => new ListRow("Name " + i, "Hidden " + i)).ToList(),
                };
                var panel = new System.Windows.Controls.StackPanel();
                panel.Children.Add(templated);
                panel.Children.Add(named);
                var dialog = new Window { Title = "Rows", Content = panel, Owner = owner, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 300, Height = 400, WindowStyle = WindowStyle.None };
                PadForge.Common.Reach.SetClass(dialog, PadForge.Common.ReachClass.Free);
                var catalog = new AssistantCatalog(p.Service, new SearchNavigator(p.Service, p.PageOf, p.Vm, _ => { }, _ => { }, () => { }),
                    p.PageOf, p.Vm, pad => p.Pad.DataContext = pad, () => AssistantWindowPlace.InFront, new AssistantCatalogParts { MainWindow = () => owner });
                dialog.Show();
                try
                {
                    PadForge.Services.Catalog.DialogRegistry.Register(dialog);
                    PageRenderDump.Settle(dialog.Dispatcher);
                    // The positive control: the list draws only some rows.
                    Assert.Null(templated.ItemContainerGenerator.ContainerFromIndex(59));
                    var read = catalog.Read("open/Window");
                    var rows = read.Lists.Single(l => l.Rows.Count == 60 && l.Rows[0].StartsWith("Row ", StringComparison.Ordinal)).Rows;
                    for (int i = 0; i < 60; i++) Assert.Equal("Row " + i + " Detail " + i, rows[i]);
                    var members = read.Lists.Single(l => l.Rows.Count == 60 && l.Rows[0].StartsWith("Name ", StringComparison.Ordinal)).Rows;
                    for (int i = 0; i < 60; i++) Assert.Equal("Name " + i, members[i]);
                    Assert.DoesNotContain(read.Lists.SelectMany(l => l.Rows), r => r.Contains(nameof(ListRow), StringComparison.Ordinal));
                }
                finally
                {
                    dialog.Close();
                    owner.Close();
                }
            });
        }

        /// <summary>A program's press of a controller's Paste or a macro's
        /// Paste asks first when the clipboard holds something to paste, and
        /// the card lists what it would do: the profile import a controller's
        /// settings amount to, then what their macros or the pasted macro
        /// would do, as save_macro's card does (#600 item 2's indirect
        /// route). The press pastes the text the card read, and never reads
        /// the clipboard again: another program's change meanwhile is not
        /// pasted. A person's Paste reads the clipboard itself.</summary>
        [Fact]
        public void PressingAPaste_AsksWithWhatThePastedMacrosWouldDo()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                var readClipboard = AssistantCatalog.ReadClipboard;
                var pasted = new List<EventArgs>();
                var macrosPasted = new List<EventArgs>();
                EventHandler count = (_, e) => pasted.Add(e);
                EventHandler countMacros = (_, e) => macrosPasted.Add(e);
                int reads = 0;
                p.Vm.Pads[0].PasteSettingsRequested += count;
                p.Vm.Pads[0].PasteMacroRequested += countMacros;
                try
                {
                    PageRenderDump.Settle(window.Dispatcher);
                    var macro = new PadForge.Services.MacroData
                    {
                        Name = "Launch",
                        Actions = new[]
                        {
                            new PadForge.Services.ActionData { Type = PadForge.ViewModels.MacroActionType.RunProgram, ProgramPath = @"C:\Tools\launch.exe" },
                            new PadForge.Services.ActionData { Type = PadForge.ViewModels.MacroActionType.KeyPress, KeyString = "F5" },
                        },
                    };
                    string runs = AssistantToolWords.RunsProgram(@"C:\Tools\launch.exe");
                    string presses = AssistantToolWords.PressesKey("F5");
                    string imports = AssistantToolWords.Reason(PadForge.Common.ReachReason.ImportsProfile);
                    var items = p.Catalog.Find(null, "menu_item", 400).Places;

                    // A controller's settings that carry the macro.
                    string copied = new PadForge.Engine.Data.PadSetting { SlotMacrosJson = PadForge.Services.SettingsService.SerializeMacrosToClipboard(new[] { macro }) }
                        .ToJson(PadForge.Engine.VirtualControllerType.Xbox);
                    AssistantCatalog.ReadClipboard = () =>
                    {
                        reads++;
                        return copied;
                    };
                    var controllerPaste = items.First(m => m.Id.StartsWith("pad.menuitem/PadMoreButton/Common_Paste", StringComparison.Ordinal));
                    var plan = p.Catalog.PlanPress(controllerPaste.Id);
                    Assert.NotNull(plan.Ask);
                    Assert.Equal(new[] { imports, runs, presses }, plan.Ask.Detail);
                    // Settings with no macro are the import alone.
                    string withMacro = copied;
                    copied = new PadForge.Engine.Data.PadSetting().ToJson(PadForge.Engine.VirtualControllerType.Xbox);
                    Assert.Equal(new[] { imports }, p.Catalog.PlanPress(controllerPaste.Id).Ask?.Detail);

                    // The clipboard changed after the card read it: the
                    // press pastes what the card read, and reads no more.
                    reads = 0;
                    plan.Press();
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.Equal(0, reads);
                    Assert.Equal(withMacro, Assert.IsType<PasteTextEventArgs>(Assert.Single(pasted)).Text);
                    // A person's Paste carries no text: it reads the
                    // clipboard when it runs.
                    pasted.Clear();
                    p.Vm.Pads[0].PasteSettingsCommand.Execute(null);
                    Assert.IsNotType<PasteTextEventArgs>(Assert.Single(pasted));

                    // A macro on the clipboard, pasted into the macro list,
                    // asks with the list's reason and what the macro does.
                    p.Pad.OpenSection(PadViewModel.MacrosSection);
                    PageRenderDump.Settle(window.Dispatcher);
                    copied = PadForge.Services.SettingsService.SerializeMacrosToClipboard(new[] { macro });
                    string macroText = copied;
                    var macroPaste = items.First(m => m.Id.StartsWith("pad.menuitem/MacroListMoreButton/Common_Paste", StringComparison.Ordinal));
                    var macroPlan = p.Catalog.PlanPress(macroPaste.Id);
                    Assert.NotNull(macroPlan.Ask);
                    Assert.Contains(runs, macroPlan.Ask.Detail);
                    Assert.Contains(presses, macroPlan.Ask.Detail);
                    Assert.DoesNotContain(imports, macroPlan.Ask.Detail);
                    copied = string.Empty;
                    reads = 0;
                    macroPlan.Press();
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.Equal(0, reads);
                    Assert.Equal(macroText, Assert.IsType<PasteTextEventArgs>(Assert.Single(macrosPasted)).Text);
                }
                finally
                {
                    p.Vm.Pads[0].PasteSettingsRequested -= count;
                    p.Vm.Pads[0].PasteMacroRequested -= countMacros;
                    AssistantCatalog.ReadClipboard = readClipboard;
                    window.Close();
                }
            });
        }

        /// <summary>A controller's Paste onto a keyboard and mouse controller
        /// names on its card the keys the pasted rows would press, after the
        /// keyboard line every other tool gives such a controller (#600 items
        /// 6 and 7). The macros' lines come first, so the card's first lines
        /// keep them whatever the table's length. Planning the press changes
        /// nothing.</summary>
        [Fact]
        public void PastingOntoAKeyboardAndMouseController_NamesTheKeysItWouldChange()
        {
            WithPages(p =>
            {
                var pad = p.Vm.Pads[0];
                var window = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                var readClipboard = AssistantCatalog.ReadClipboard;
                try
                {
                    pad.OutputType = PadForge.Engine.VirtualControllerType.KeyboardMouse;
                    PageRenderDump.Settle(window.Dispatcher);
                    var keys = pad.Mappings.Where(m => m.TargetSettingName.StartsWith("KbmKey", StringComparison.Ordinal)).Take(20).ToList();
                    Assert.Equal(20, keys.Count);
                    var macro = new PadForge.Services.MacroData
                    {
                        Name = "Launch",
                        Actions = new[] { new PadForge.Services.ActionData { Type = PadForge.ViewModels.MacroActionType.RunProgram, ProgramPath = @"C:\Tools\launch.exe" } },
                    };
                    var rows = keys.Select((m, i) => new PadForge.Engine.Data.MappingRow
                    {
                        Target = m.TargetSettingName,
                        LayerMask = "Base",
                        Sources = new List<PadForge.Engine.Data.MappingSource> { new() { Descriptor = "Button " + i } },
                    }).ToList();
                    string copied = new PadForge.Engine.Data.PadSetting
                    {
                        SlotMultiSourceRows = rows,
                        SlotMacrosJson = PadForge.Services.SettingsService.SerializeMacrosToClipboard(new[] { macro }),
                    }.ToJson(PadForge.Engine.VirtualControllerType.KeyboardMouse);
                    AssistantCatalog.ReadClipboard = () => copied;
                    var sets = SettingsManager.SlotMappingSets;
                    var set = sets?[0];
                    var setRows = set?.Rows;
                    string id = p.Catalog.Find(null, "menu_item", 400).Places
                        .First(m => m.Id.StartsWith("pad.menuitem/PadMoreButton/Common_Paste", StringComparison.Ordinal)).Id;

                    var detail = p.Catalog.PlanPress(id).Ask.Detail;
                    string runs = AssistantToolWords.RunsProgram(@"C:\Tools\launch.exe");
                    Assert.Equal(AssistantToolWords.Reason(PadForge.Common.ReachReason.ImportsProfile), detail[0]);
                    Assert.Equal(runs, detail[1]);
                    Assert.Equal(AssistantToolWords.KeyboardMouseLine(), detail[2]);
                    Assert.Equal(keys.Select((m, i) => AssistantToolWords.KeyLine(m.TargetLabel, "Button " + i)), detail.Skip(3));
                    Assert.Contains(runs, detail.Take(AutomationRequestItem.CardDetailLines));
                    // Planning landed nothing.
                    Assert.Same(sets, SettingsManager.SlotMappingSets);
                    Assert.Same(set, sets?[0]);
                    Assert.Same(setRows, set?.Rows);
                    // Settings with no rows still name the keyboard line:
                    // what else they carry can press keys too.
                    string table = copied;
                    copied = new PadForge.Engine.Data.PadSetting().ToJson(PadForge.Engine.VirtualControllerType.KeyboardMouse);
                    Assert.Equal(new[] { AssistantToolWords.Reason(PadForge.Common.ReachReason.ImportsProfile), AssistantToolWords.KeyboardMouseLine() },
                        p.Catalog.PlanPress(id).Ask.Detail);
                    copied = table;

                    // A gamepad controller takes the same settings with no
                    // keyboard line.
                    pad.OutputType = PadForge.Engine.VirtualControllerType.Xbox;
                    PageRenderDump.Settle(window.Dispatcher);
                    var gamepad = p.Catalog.PlanPress(p.Catalog.Find(null, "menu_item", 400).Places
                        .First(m => m.Id.StartsWith("pad.menuitem/PadMoreButton/Common_Paste", StringComparison.Ordinal)).Id).Ask.Detail;
                    Assert.DoesNotContain(AssistantToolWords.KeyboardMouseLine(), gamepad);
                    Assert.Contains(runs, gamepad);
                }
                finally
                {
                    AssistantCatalog.ReadClipboard = readClipboard;
                    pad.OutputType = PadForge.Engine.VirtualControllerType.Xbox;
                    window.Close();
                }
            });
        }

        /// <summary>show rings the button whose menu holds the item an ID
        /// names, and Search lands on the same: the macro list's More for its
        /// Paste and the controller's More for the controller's, though both
        /// items read Paste.</summary>
        [Fact]
        public void ShowAMenuItem_RingsTheButtonWhoseMenuHoldsIt()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                p.Window = window;
                try
                {
                    p.Vm.SelectedNavTag = "Pad1";
                    p.Pad.OpenSection(PadViewModel.MacrosSection);
                    PageRenderDump.Settle(window.Dispatcher);
                    var items = p.Catalog.Find(null, "menu_item", 400).Places;
                    foreach (var (prefix, button) in new[]
                    {
                        ("pad.menuitem/MacroListMoreButton/Common_Paste", "MacroListMoreButton"),
                        ("pad.menuitem/PadMoreButton/Common_Paste", "Pad_More"),
                    })
                    {
                        string id = items.First(m => m.Id.StartsWith(prefix, StringComparison.Ordinal)).Id;
                        Assert.True(p.Catalog.Show(new AssistantShowRequest
                        {
                            Program = "Claude Code",
                            Steps = new[] { new AssistantShowStep { Place = id, Words = "Here.", Advance = AssistantAdvance.Next } },
                        }).Shown);
                        Until(window, () => RingedId(p) == button);
                        Assert.Equal(button, RingedId(p));
                        p.Pointer.Clear();
                        // Search's landing finds the same button.
                        Assert.True(SearchIds.TryRead(id, out string bare, out _, out _, out _));
                        var entry = p.Service.Index.First(e => p.Service.IdOf(e) == bare);
                        Assert.Equal(button, System.Windows.Automation.AutomationProperties.GetAutomationId(SearchNavigator.MenuItemOf(p.Pad, entry).Owner));
                    }
                }
                finally
                {
                    p.Pointer.Clear();
                    window.Close();
                }
            });
        }

        /// <summary>The AutomationId of what the pointer rings, or null.</summary>
        private static string RingedId(Pages p)
            => p.Pointer.Target is { } ringed ? System.Windows.Automation.AutomationProperties.GetAutomationId(ringed) : null;

        /// <summary>The macro list's More and the open macro's More each hold a
        /// Copy of the same command. Each has an ID of its own: show rings
        /// its own button, and a press clicks its own menu's item.</summary>
        [Fact]
        public void TheMacroListsCopy_AndTheMacrosCopy_AreEachTheirOwn()
        {
            WithPages(p =>
            {
                var pad = p.Vm.Pads[0];
                var window = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                p.Window = window;
                var macro = new MacroItem { Name = "Launch" };
                try
                {
                    p.Vm.SelectedNavTag = "Pad1";
                    pad.Macros.Add(macro);
                    pad.SelectedMacro = macro;
                    p.Pad.OpenSection(PadViewModel.MacrosSection);
                    PageRenderDump.Settle(window.Dispatcher);
                    System.Windows.Controls.MenuItem CopyOf(string button)
                    {
                        var owner = SearchLocator.LogicalDescendants(p.Pad).OfType<FrameworkElement>()
                            .Single(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == button);
                        Assert.True(owner.IsVisible, button + " shows");
                        return owner.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>()
                            .Single(m => System.Windows.Data.BindingOperations.GetBinding(m, System.Windows.Controls.MenuItem.CommandProperty)?.Path?.Path == "CopyMacroCommand");
                    }
                    var clicks = new List<string>();
                    CopyOf("MacroListMoreButton").Click += (_, _) => clicks.Add("MacroListMoreButton");
                    CopyOf("MacroMoreButton").Click += (_, _) => clicks.Add("MacroMoreButton");
                    // The two Copy entries, in the page's order: the list's,
                    // then the macro's.
                    var copies = p.Service.Index.Where(e => e.Kind == SearchKind.MenuItem && e.Handler == "CopyMacroCommand").OrderBy(e => e.Order).ToList();
                    Assert.Equal(2, copies.Count);
                    var places = p.Catalog.Find(null, "menu_item", 400).Places;
                    foreach (var (entry, button) in new[] { (copies[0], "MacroListMoreButton"), (copies[1], "MacroMoreButton") })
                    {
                        string bare = p.Service.IdOf(entry);
                        string id = places.Single(m => SearchIds.TryRead(m.Id, out string b, out _, out _, out _) && b == bare).Id;
                        Assert.True(p.Catalog.Show(new AssistantShowRequest
                        {
                            Program = "Claude Code",
                            Steps = new[] { new AssistantShowStep { Place = id, Words = "Here.", Advance = AssistantAdvance.Next } },
                        }).Shown);
                        Until(window, () => RingedId(p) == button);
                        Assert.Equal(button, RingedId(p));
                        p.Pointer.Clear();
                        clicks.Clear();
                        p.Catalog.PlanPress(id).Press();
                        PageRenderDump.Settle(window.Dispatcher);
                        Assert.Equal(new[] { button }, clicks);
                    }
                }
                finally
                {
                    p.Pointer.Clear();
                    pad.Macros.Remove(macro);
                    window.Close();
                }
            });
        }

        /// <summary>An item every profile card or layer tab has on its own
        /// menu has an ID for each card or tab, its position after
        /// <c>@i</c>: find lists each, the place names its card or tab, show
        /// rings that card's button, and a press acts on that card or tab.
        /// The ID with no card or tab names none and is refused.</summary>
        [Fact]
        public void EachCardsOwnMenu_HasAnIdForEachCard()
        {
            WithPages(p =>
            {
                var racing = new ProfileListItem { Id = "p1", Name = "Racing" };
                var rally = new ProfileListItem { Id = "p2", Name = "Rally" };
                p.Vm.Settings.ProfileItems.Add(racing);
                p.Vm.Settings.ProfileItems.Add(rally);
                int exported = 0;
                EventHandler count = (_, _) => exported++;
                p.Vm.Settings.ExportProfileRequested += count;
                // A Profiles page never drawn: find reads the cards from the
                // list's data.
                var drawn = p.Profiles;
                p.Profiles = new ProfilesPage { DataContext = p.Vm.Settings };
                Assert.Null(((System.Windows.Controls.ItemsControl)p.Profiles.FindName("ProfileListBox")).ItemContainerGenerator.ContainerFromItem(rally));
                Assert.Equal(2, p.Catalog.Find(null, "menu_item", 400).Places
                    .Count(m => m.Id.StartsWith("profiles.menuitem/ProfileListBox/Profiles_Export@i", StringComparison.Ordinal)));
                p.Profiles = drawn;
                var window = new Window
                {
                    Content = p.Profiles, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                p.Window = window;
                try
                {
                    p.Vm.SelectedNavTag = "Profiles";
                    PageRenderDump.Settle(window.Dispatcher);
                    const string export = "profiles.menuitem/ProfileListBox/Profiles_Export";
                    var places = p.Catalog.Find(null, "menu_item", 400).Places.Where(m => m.Id.StartsWith(export, StringComparison.Ordinal)).ToList();
                    Assert.Equal(new[] { export + "@i0", export + "@i1" }, places.Select(m => m.Id));
                    Assert.EndsWith("Racing", places[0].Where);
                    Assert.EndsWith("Rally", places[1].Where);
                    // show rings Rally's own More.
                    Assert.True(p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Steps = new[] { new AssistantShowStep { Place = export + "@i1", Words = "Here.", Advance = AssistantAdvance.Next } },
                    }).Shown);
                    Until(window, () => p.Pointer.Target is { DataContext: ProfileListItem shown } && ReferenceEquals(shown, rally));
                    Assert.Equal("Profiles_More", RingedId(p));
                    Assert.Same(rally, p.Pointer.Target.DataContext);
                    p.Pointer.Clear();
                    // A press acts on Rally.
                    p.Catalog.PlanPress(export + "@i1").Press();
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.Equal(1, exported);
                    Assert.Same(rally, p.Vm.Settings.SelectedProfile);
                    // The ID with no card names none.
                    Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => p.Catalog.PlanPress(export)).Code);
                    Assert.Equal(1, exported);
                }
                finally
                {
                    p.Pointer.Clear();
                    p.Vm.Settings.ExportProfileRequested -= count;
                    window.Close();
                }

                // A layer tab's own menu, on the controller page.
                var pad = p.Vm.Pads[0];
                pad.RebuildLayerTabs(new[] { new PadForge.Engine.Data.ShiftActivator { LayerMask = "Shift1", LayerName = "Aim" } });
                var padWindow = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                padWindow.Show();
                p.Window = padWindow;
                try
                {
                    p.Vm.SelectedNavTag = "Pad1";
                    p.Pad.OpenTab(PadViewModel.MappingsTabIndex);
                    PageRenderDump.Settle(padWindow.Dispatcher);
                    var tabs = (System.Windows.Controls.ItemsControl)p.Pad.FindName("ShiftLayerTabStrip");
                    Assert.True(tabs.IsVisible, "the layer tabs show");
                    var copied = new List<object>();
                    foreach (var chip in SearchLocator.VisualDescendants(tabs).OfType<System.Windows.Controls.RadioButton>())
                        foreach (var item in chip.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>()
                                     .Where(m => (m.Header as string) == PadForge.Resources.Strings.Strings.Instance.Pad_Shift_Copy))
                            item.Click += (sender, _) => copied.Add(((System.Windows.Controls.MenuItem)sender).Tag);
                    var copies = p.Catalog.Find(null, "menu_item", 400).Places
                        .Where(m => m.Id.StartsWith("pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Copy", StringComparison.Ordinal)).ToList();
                    Assert.Equal(new[] { "pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Copy@c1i0", "pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Copy@c1i1" },
                        copies.Select(m => m.Id));
                    Assert.EndsWith("Aim", copies[1].Where);
                    p.Catalog.PlanPress(copies[1].Id).Press();
                    PageRenderDump.Settle(padWindow.Dispatcher);
                    Assert.Equal(new object[] { "Shift1" }, copied);
                    // An ID that names a tab the page has not, past the last
                    // or on another controller, is refused as not shown,
                    // never as naming none.
                    foreach (string named in new[] { "pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Copy@c1i9", "pad.menuitem/ShiftLayerTabStrip/Pad_Shift_Copy@c7i1" })
                    {
                        Assert.NotEqual(AssistantCatalog.EachOwnWords, Assert.Throws<AssistantRefusal>(() => p.Catalog.PlanPress(named)).Message);
                        Assert.Equal(AssistantCatalog.NotShownWords, Assert.Throws<AssistantRefusal>(() => p.Catalog.Show(new AssistantShowRequest
                        {
                            Program = "Claude Code",
                            Steps = new[] { new AssistantShowStep { Place = named, Words = "Here.", Advance = AssistantAdvance.Next } },
                        })).Message);
                    }
                }
                finally
                {
                    p.Pointer.Clear();
                    padWindow.Close();
                }
            });
        }

        /// <summary>A program's press of an item of a controller's More menu
        /// acts on the controller the page shows, as the item does once a
        /// person opens the menu there: a menu no one opened yet has no
        /// controller, and one a person opened keeps the controller it was
        /// opened on. An ID of a controller the page does not show is
        /// refused, as a button's press is. Copy stands for the menu's
        /// command items.</summary>
        [Fact]
        public void PressingAMoreMenusItem_ActsOnTheControllerThePageShows()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                var copied = new List<object>();
                EventHandler count = (sender, _) => copied.Add(sender);
                foreach (var pad in p.Vm.Pads) pad.CopySettingsRequested += count;
                try
                {
                    PageRenderDump.Settle(window.Dispatcher);
                    string id = p.Catalog.Find(null, "menu_item", 400).Places
                        .First(m => m.Id.StartsWith("pad.menuitem/PadMoreButton/Common_Copy", StringComparison.Ordinal)).Id;
                    var menu = ((System.Windows.Controls.Button)p.Pad.FindName("PadMoreButton")).ContextMenu;

                    // A menu no one opened.
                    p.Catalog.PlanPress(id).Press();
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.Equal(new object[] { p.Vm.Pads[0] }, copied);

                    // A menu a person opened on another controller, which
                    // cannot copy: its click leaves that controller on it.
                    menu.DataContext = p.Vm.Pads[1];
                    copied.Clear();
                    p.Catalog.PlanPress(id).Press();
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.Equal(new object[] { p.Vm.Pads[0] }, copied);

                    // The ID of a controller the page does not show.
                    Assert.True(SearchIds.TryRead(id, out string bare, out _, out _, out _));
                    string other = SearchIds.With(bare, 7, null, null);
                    Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => p.Catalog.PlanPress(other)).Code);
                    Assert.Equal(new object[] { p.Vm.Pads[0] }, copied);
                }
                finally
                {
                    foreach (var pad in p.Vm.Pads) pad.CopySettingsRequested -= count;
                    window.Close();
                }
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
