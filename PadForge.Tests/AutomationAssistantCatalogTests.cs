using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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

            /// <summary>The page Search reads for each of its sources, as the
            /// window's own SearchPageOf gives it: null for a page that is not
            /// one of the five, a dialog's, the window's, Home's or
            /// About's.</summary>
            public FrameworkElement PageOf(SearchPage page) => page switch
            {
                SearchPage.Pad => Pad,
                SearchPage.Settings => Settings,
                SearchPage.Extras => Extras,
                SearchPage.Devices => Devices,
                SearchPage.Profiles => Profiles,
                _ => null,
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
                    // option: the walk rings the box, which the program
                    // never empties. Words that still hide the row keep the
                    // step. The user empties the box, and a row that holds
                    // the option rings.
                    grid.SelectedItem = null;
                    p.Vm.Pads[0].MappingInputSearch = "no row says this";
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.Empty(grid.Items);
                    Assert.True(p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Walk = true,
                        Steps = new[] { new AssistantShowStep { Place = id, Words = "This one.", Advance = AssistantAdvance.Next } },
                    }).Shown);
                    var box = (System.Windows.Controls.TextBox)p.Pad.FindName("MappingFilterSearchBox");
                    Until(window, () => ReferenceEquals(p.Pointer.Target, box));
                    Assert.Same(box, p.Pointer.Target);
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_ClearSearch, p.Pointer.Callout?.Words);
                    Assert.Equal("no row says this", p.Vm.Pads[0].MappingInputSearch);
                    TypeInto(box, "no row says");
                    Until(window, () => !ReferenceEquals(p.Pointer.Target, box), 600);
                    Assert.Same(box, p.Pointer.Target);
                    TypeInto(box, string.Empty);
                    Until(window, () => p.Pointer.Target is System.Windows.Controls.DataGridRow);
                    var listed = Assert.IsType<System.Windows.Controls.DataGridRow>(p.Pointer.Target);
                    Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_Open_Format, ((MappingItem)listed.Item).TargetLabel),
                        p.Pointer.Callout?.Words);
                    Assert.Null(grid.SelectedItem);
                }
                finally
                {
                    p.Pointer.Clear();
                    p.Vm.Pads[0].MappingInputSearch = string.Empty;
                    window.Close();
                }
            });
        }

        /// <summary>The user types in a text box, as WPF routes it: the
        /// box's text changes, then the key goes down in it, where the guided
        /// pointer hears it. No input reaches the desktop.</summary>
        private static void TypeInto(System.Windows.Controls.TextBox box, string text)
        {
            box.Text = text;
            var source = PresentationSource.FromVisual(box);
            if (source == null) return;
            box.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Back)
            {
                RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
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

                    // A device the search box hides stands in no list. The
                    // walk rings the search box, which the program never
                    // empties. Words that still hide the row keep the step.
                    // The user empties the box, and the device's row rings.
                    p.Pointer.Clear();
                    string place = "device/" + AutomationDevices.IdOf(offline.InstanceGuid);
                    AssistantShowRequest Walk() => new()
                    {
                        Program = "Claude Code",
                        Walk = true,
                        Steps = new[] { new AssistantShowStep { Place = place, Words = "Here.", Advance = AssistantAdvance.Next } },
                    };
                    bool RingsTheRow() => p.Pointer.Target is System.Windows.Controls.ListBoxItem { DataContext: var shown } && ReferenceEquals(shown, offline);
                    p.Vm.Devices.SearchText = "No Such Device";
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.True(p.Catalog.Show(Walk()).Shown);
                    var box = (System.Windows.Controls.TextBox)p.Devices.FindName("DeviceSearchBox");
                    Until(window, () => ReferenceEquals(p.Pointer.Target, box));
                    Assert.Same(box, p.Pointer.Target);
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Automation_Pointer_ClearSearch, p.Pointer.Callout.Words);
                    Assert.Equal("No Such Device", p.Vm.Devices.SearchText);
                    TypeInto(box, "No Such");
                    Until(window, () => !ReferenceEquals(p.Pointer.Target, box), 600);
                    Assert.Same(box, p.Pointer.Target);
                    TypeInto(box, string.Empty);
                    Until(window, RingsTheRow);
                    Assert.True(RingsTheRow(), "the device's row rings once the box is empty");
                    Assert.Equal("Here.", p.Pointer.Callout.Words);

                    // A segment hides it. The walk rings the Show segment's
                    // All, then the connection segment's All, each until the
                    // user picks it. The program picks neither.
                    p.Pointer.Clear();
                    p.Vm.Devices.Filter = DeviceFilter.Wheel;
                    p.Vm.Devices.Connection = DeviceConnection.Connected;
                    PageRenderDump.Settle(window.Dispatcher);
                    var showAll = Segment(p.Devices, "Devices_Filter_All");
                    var connectionAll = Segment(p.Devices, "Devices_Connection_All");
                    Assert.True(p.Catalog.Show(Walk()).Shown);
                    Until(window, () => ReferenceEquals(p.Pointer.Target, showAll));
                    Assert.Same(showAll, p.Pointer.Target);
                    var s = PadForge.Resources.Strings.Strings.Instance;
                    Assert.Equal(string.Format(s.Automation_Pointer_Pick_Format, s.Devices_Filter_All), p.Pointer.Callout.Words);
                    Assert.Equal(DeviceFilter.Wheel, p.Vm.Devices.Filter);
                    Pick(showAll);
                    Until(window, () => ReferenceEquals(p.Pointer.Target, connectionAll));
                    Assert.Same(connectionAll, p.Pointer.Target);
                    Assert.Equal(DeviceConnection.Connected, p.Vm.Devices.Connection);
                    Pick(connectionAll);
                    Until(window, RingsTheRow);
                    Assert.True(RingsTheRow(), "the device's row rings once both segments show it");

                    // A plain show is Search's landing: it empties the search
                    // and sets both segments to All itself.
                    p.Pointer.Clear();
                    p.Vm.Devices.SearchText = "No Such Device";
                    p.Vm.Devices.Filter = DeviceFilter.Wheel;
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.True(p.Catalog.Show(new AssistantShowRequest { Program = "Claude Code", Steps = new[] { StepAt(place) } }).Shown);
                    Until(window, RingsTheRow);
                    Assert.True(RingsTheRow(), "a plain show rings the device's row");
                    Assert.Equal((string.Empty, DeviceFilter.All, DeviceConnection.All), (p.Vm.Devices.SearchText, p.Vm.Devices.Filter, p.Vm.Devices.Connection));
                }
                finally
                {
                    p.Pointer.Clear();
                    p.Vm.Devices.SearchText = string.Empty;
                    p.Vm.Devices.Filter = DeviceFilter.All;
                    p.Vm.Devices.Connection = DeviceConnection.All;
                    fold.EndSearch();
                    PadForge.Common.CardFoldState.Load(kept);
                    window.Close();
                }
            });
        }

        /// <summary>A key typed in the ringed place is the user doing the
        /// step (#600 item 7): a walk that rings the Devices page's search
        /// box stays up while the user types in it and goes on once the box
        /// no longer hides the row. A screenshot key elsewhere leaves the
        /// callout up, and any other key elsewhere is the user acting, which
        /// takes it away, as before.</summary>
        [Fact]
        public void AKeyInTheRingedBox_IsTheUsersStep_AndAKeyElsewhereEndsTheCallout()
        {
            WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Devices, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                var online = new DeviceRowViewModel { DeviceName = "Online Pad", InstanceGuid = Guid.NewGuid(), IsHidHideAvailable = true, IsOnline = true };
                p.Vm.Devices.Devices.Add(online);
                window.Show();
                p.Window = window;
                try
                {
                    p.Vm.Devices.EnsureListViews();
                    p.Vm.SelectedNavTag = "Devices";
                    p.Vm.Devices.SearchText = "No Such Device";
                    PageRenderDump.Settle(window.Dispatcher);
                    var box = (System.Windows.Controls.TextBox)p.Devices.FindName("DeviceSearchBox");
                    void Key(UIElement at, System.Windows.Input.Key key) => at.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                        System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, key)
                    {
                        RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
                    });
                    Assert.True(p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Walk = true,
                        Steps = new[] { new AssistantShowStep { Place = "device/" + AutomationDevices.IdOf(online.InstanceGuid), Words = "Here.", Advance = AssistantAdvance.Next } },
                    }).Shown);
                    Until(window, () => ReferenceEquals(p.Pointer.Target, box));
                    Assert.Same(box, p.Pointer.Target);
                    var ringed = p.Pointer.Callout;
                    // A screenshot key elsewhere leaves it up.
                    Key(p.Devices, System.Windows.Input.Key.LeftShift);
                    Until(window, () => p.Pointer.Callout == null, 600);
                    Assert.Same(ringed, p.Pointer.Callout);
                    // A key in the box whose words still hide the row: the
                    // step stays, ringed as it was.
                    Key(box, System.Windows.Input.Key.A);
                    Until(window, () => !ReferenceEquals(p.Pointer.Callout, ringed), 600);
                    Assert.Same(ringed, p.Pointer.Callout);
                    Assert.Same(box, p.Pointer.Target);
                    // The user empties it, and the walk goes on to the row.
                    TypeInto(box, string.Empty);
                    Until(window, () => p.Pointer.Target is System.Windows.Controls.ListBoxItem { DataContext: var row } && ReferenceEquals(row, online));
                    Assert.Equal("Here.", p.Pointer.Callout?.Words);
                    // Any other key elsewhere is the user acting: the callout
                    // goes.
                    Key(p.Devices, System.Windows.Input.Key.A);
                    Until(window, () => p.Pointer.Callout == null, 2000);
                    Assert.Null(p.Pointer.Callout);

                    // A key in the place of a step that does not wait to be
                    // done is the user acting too: a show of two places that
                    // goes on at a click ends there, and never jumps to the
                    // second place at a keystroke.
                    var second = new DeviceRowViewModel { DeviceName = "Second Pad", InstanceGuid = Guid.NewGuid(), IsHidHideAvailable = true, IsOnline = true };
                    p.Vm.Devices.Devices.Add(second);
                    p.Vm.Devices.RefreshCounts();
                    PageRenderDump.Settle(window.Dispatcher);
                    Assert.True(p.Catalog.Show(new AssistantShowRequest
                    {
                        Program = "Claude Code",
                        Steps = new[]
                        {
                            new AssistantShowStep { Place = "device/" + AutomationDevices.IdOf(online.InstanceGuid), Words = "First.", Advance = AssistantAdvance.Click },
                            new AssistantShowStep { Place = "device/" + AutomationDevices.IdOf(second.InstanceGuid), Words = "Second.", Advance = AssistantAdvance.Click },
                        },
                    }).Shown);
                    Until(window, () => p.Pointer.Target is System.Windows.Controls.ListBoxItem { DataContext: var first } && ReferenceEquals(first, online));
                    Assert.Equal("First.", p.Pointer.Callout?.Words);
                    Key(p.Pointer.Target, System.Windows.Input.Key.A);
                    Until(window, () => p.Pointer.Callout == null, 2000);
                    Assert.Null(p.Pointer.Callout);
                    Assert.NotSame(second, p.Vm.Devices.SelectedDevice);
                }
                finally
                {
                    p.Pointer.Clear();
                    p.Vm.Devices.SearchText = string.Empty;
                    window.Close();
                }
            });
        }

        /// <summary>A segment of the Devices page by its automation
        /// ID.</summary>
        private static System.Windows.Controls.RadioButton Segment(FrameworkElement page, string automationId)
            => SearchLocator.LogicalDescendants(page).OfType<System.Windows.Controls.RadioButton>()
                .Single(r => System.Windows.Automation.AutomationProperties.GetAutomationId(r) == automationId);

        /// <summary>The user picks a segment, as a screen reader's select or
        /// a click checks it, and the guided pointer hears the click.</summary>
        private static void Pick(System.Windows.Controls.RadioButton segment)
        {
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(segment);
            ((System.Windows.Automation.Provider.ISelectionItemProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)).Select();
            Click(segment);
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

        /// <summary>
        /// A program reaches Find a Mapping over the pipe while the dialog's
        /// own modal loop runs, as an assistant does once a person's click
        /// opened it (AS-67): its <c>set</c> picks a row of the list by the
        /// row's words, which turns Apply on, and its <c>press</c> of Apply
        /// closes the dialog with that row, as a person's two clicks do. The
        /// calls come from the SDK's client on a thread of their own, through
        /// PadForge's own host, tools and catalog, and the work reaches the
        /// interface's thread inside the loop.
        /// </summary>
        [Fact]
        public void FindAMapping_TakesAPickAndApply_OverThePipe_WhileItsLoopRuns()
        {
            WithPages(p =>
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var owner = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = new System.Windows.Controls.Grid() };
                owner.Show();
                var ui = new DispatcherAutomationUi(dispatcher);
                string pipeName = "PadForge.Tests.FindAMapping." + Guid.NewGuid().ToString("N");
                var host = TestConsent.AllowAll(new AutomationHost(new RecordingAutomationTarget(), ui,
                    "PadForge.Tests.FindAMappingControl." + Guid.NewGuid().ToString("N"), automationPipeName: pipeName));
                var catalog = new AssistantCatalog(p.Service, new SearchNavigator(p.Service, p.PageOf, p.Vm, _ => { }, _ => { }, () => { }),
                    p.PageOf, p.Vm, pad => p.Pad.DataContext = pad, () => AssistantWindowPlace.InFront, new AssistantCatalogParts { MainWindow = () => owner });
                var app = new FakeAssistantApp();
                var calls = new AssistantCalls(host, app, catalog, ui, new AssistantUndo(app.TakeUndoState, app.RestoreUndoState), AssistantTools.All);
                var server = new AssistantServer(host, calls, ui);
                server.Attach();
                host.Apply(true, AutomationLevel.ChangeSettings);
                PadForge.Common.Input.SdlMappingRow Row(string guid, string name)
                {
                    Assert.True(PadForge.Common.Input.SdlMappingRow.TryParse(guid + "," + name + ",a:b0,b:b1,platform:Windows,", out var row, out _));
                    return row;
                }
                var rows = new List<PadForge.Services.SdlFindRow>();
                for (int i = 0; i < 40; i++)
                    rows.Add(new(Row("030000000d0f0000" + (0x2000 + i).ToString("x4") + "000000000000", "Fight Stick " + i.ToString("00")), PadForge.Common.Input.SdlMappingSource.Community, false)
                        { Detail = "The community copy built in" });
                var picked = rows[33];
                var dialog = new SdlMappingDialog(SdlMappingDialog.Mode.Find, "Fight Stick", "030000000d0f00009900000000000000", new[] { "Xbox #1" })
                {
                    Owner = owner, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                };
                dialog.SetRows(rows);
                string failure = null;
                Task<(CallToolResult Set, CallToolResult Press)> program = null;
                var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
                guard.Tick += (_, _) =>
                {
                    guard.Stop();
                    failure ??= "the dialog was still open after 40 seconds";
                    dialog.Close();
                };
                dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        PadForge.Services.Catalog.DialogRegistry.Register(dialog);
                        var open = Assert.Single(catalog.Dialogs().Open, d => d.Id.StartsWith("dialog.dialog/SdlMappingDialog/", StringComparison.Ordinal));
                        string apply = Assert.Single(catalog.Read(open.Id).Buttons, b => b.Name == PadForge.Resources.Strings.Strings.Instance.Common_Apply).Id;
                        // The program runs on a thread of its own, as it does
                        // over the pipe, while this thread stays in the loop.
                        program = Task.Run(() => PickAndApplyAsync(pipeName, picked.ReadName, apply));
                    }
                    catch (Exception error)
                    {
                        failure = error.ToString();
                        dialog.Close();
                    }
                }));
                guard.Start();
                bool? applied;
                try
                {
                    applied = dialog.ShowDialog();
                    // The press watches for a dialog it opened for a moment
                    // after it, through this thread.
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (program != null && !program.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(20)) Pump(dispatcher, 25);
                }
                finally
                {
                    guard.Stop();
                    host.Dispose();
                    owner.Close();
                }
                Assert.True(failure == null, failure);
                Assert.NotNull(program);
                Assert.True(program.IsCompletedSuccessfully, program.Exception?.ToString() ?? "the program's calls never ended");
                var (set, press) = program.Result;
                Assert.True(set.IsError != true, AssistantRig.Text(set));
                Assert.True(press.IsError != true, AssistantRig.Text(press));
                // What a person's pick and Apply do: the dialog closes with
                // the row picked, which the Devices page then applies.
                Assert.True(applied);
                Assert.Same(picked.Row, dialog.PickedRow);
                Assert.DoesNotContain(PadForge.Services.Catalog.LiveCatalog.Items(), i => i.Id == SdlMappingDialog.FindListId);
            });
        }

        /// <summary>An assistant over the pipe: hello as an assistant's copy
        /// says it, then the SDK's client, which picks a row of Find a
        /// Mapping's list by its words and presses Apply by its ID.</summary>
        private static async Task<(CallToolResult Set, CallToolResult Press)> PickAndApplyAsync(string pipeName, string words, string apply)
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            byte[] hello = System.Text.Encoding.UTF8.GetBytes("hello kind=mcp name=\"Test Assistant\" host=\"host.exe\"\n");
            await pipe.WriteAsync(hello);
            await pipe.FlushAsync();
            var line = new List<byte>();
            var one = new byte[1];
            while (await pipe.ReadAsync(one) == 1 && one[0] != (byte)'\n') line.Add(one[0]);
            Assert.StartsWith("ok padforge=", System.Text.Encoding.UTF8.GetString(line.ToArray()));
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), new McpClientOptions
            {
                ProtocolVersion = "2025-11-25",
                ClientInfo = new Implementation { Name = "Test Assistant", Version = "1" },
            });
            var set = await client.CallToolAsync("set", AssistantRig.Args(("id", SdlMappingDialog.FindListId), ("value", words)));
            var press = await client.CallToolAsync("press", AssistantRig.Args(("id", apply)));
            return (set, press);
        }

        /// <summary>Runs the dispatcher's queue for a moment: the work of
        /// every priority above the application's idle time, then the
        /// rest of <paramref name="ms"/>.</summary>
        private static void Pump(System.Windows.Threading.Dispatcher dispatcher, int ms)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(ms), System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            timer.Stop();
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

                    // A plain show opens the page the card names as the one
                    // that draws it (LiveItem.Page). Nothing else names it
                    // here: this Home stands in no window the catalog knows.
                    var opened = new List<string>();
                    var shower = new AssistantCatalog(p.Service, new SearchNavigator(p.Service, p.PageOf, p.Vm, tag => opened.Add(tag), _ => { }, () => { }),
                        p.PageOf, p.Vm, pad => p.Pad.DataContext = pad, () => AssistantWindowPlace.InFront, new AssistantCatalogParts { Pointer = p.Pointer });
                    try
                    {
                        Assert.True(shower.Show(new AssistantShowRequest { Program = "Home Test", Steps = new[] { StepAt("home/c1") } }).Shown);
                        Assert.Equal(new[] { AssistantHomeCards.HomeTag }, opened);
                    }
                    finally
                    {
                        p.Pointer.Clear();
                    }
                }
                finally
                {
                    PadForge.Services.Catalog.LiveCatalog.RemoveProvider(provider);
                }
            });
        }

        // ─────────────────────────────────────────────
        //  The walk's census (#600 item 7)
        // ─────────────────────────────────────────────

        /// <summary>
        /// The census's world: every page in one window behind a sidebar of
        /// stand-ins for the window's entries, the page shown changing only
        /// when the user clicks an entry, and a place of every kind and shape
        /// the catalog gives. The navigator's calls are the program's own
        /// clicks and are counted. Pages stand as the window holds them, all
        /// of them in it, each but the one shown collapsed, under a
        /// ContentPresenter that the window's logical tree does not cross.
        /// </summary>
        internal sealed class Census
        {
            public Pages P;
            public HomePage Home;
            public AboutPage About;
            public FrameworkElement Shown;
            public PadForge.Services.ControllerDatabaseController Database;
            public readonly Dictionary<string, System.Windows.Controls.Button> Entries = new(StringComparer.Ordinal);
            public readonly List<string> Moves = new();
            public DeviceRowViewModel Online, Offline, Decoy;
            public SdlMappingDialog Dialog;
            public PluginCatalogRig Plugins;
            /// <summary>The plugin's beat. A plugin silent for five seconds
            /// has its devices taken offline, and the census takes minutes.</summary>
            public Timer Beat;
            public List<PadForge.Controls.FoldPart> Folds = new();
            public const string Words = "Census: this one.";

            public FrameworkElement PageOf(string tag) => tag switch
            {
                "Home" => Home,
                "About" => About,
                "Devices" => P.Devices,
                "Profiles" => P.Profiles,
                "Extras" => P.Extras,
                "Settings" => P.Settings,
                _ => tag.StartsWith("Pad", StringComparison.Ordinal) ? P.Pad : null,
            };

            /// <summary>Every page the window holds.</summary>
            public IEnumerable<FrameworkElement> AllPages => new FrameworkElement[] { P.Pad, P.Settings, P.Extras, P.Devices, P.Profiles, Home, About };

            /// <summary>The page an entry opens, as the window opens it: the
            /// controller page bound to its controller, the one page shown
            /// and every other collapsed.</summary>
            public void Go(string tag)
            {
                if (tag.StartsWith("Pad", StringComparison.Ordinal)) P.Pad.DataContext = P.Vm.Pads[int.Parse(tag.AsSpan(3), System.Globalization.CultureInfo.InvariantCulture) - 1];
                P.Vm.SelectedNavTag = tag;
                Shown = PageOf(tag);
                foreach (var page in AllPages) page.Visibility = ReferenceEquals(page, Shown) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        internal static void WithCensus(Action<Census> body)
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                bool[] made = (bool[])SettingsManager.SlotCreated.Clone();
                var devices = SettingsManager.UserDevices;
                var settings = SettingsManager.UserSettings;
                var kept = PadForge.Common.CardFoldState.Snapshot();
                var drawnOn = PadForge.Services.Catalog.MouseSteeringCatalog.DrawnOn;
                var mineBefore = ControllerDatabaseMine.Current;
                var providers = new List<Func<IReadOnlyList<PadForge.Services.Catalog.LiveItem>>>();
                var c = new Census();
                Window window = null;
                try
                {
                    var mouse = Guid.NewGuid();
                    SettingsManager.UserDevices = new DeviceCollection();
                    SettingsManager.UserDevices.Items.Add(new PadForge.Engine.Data.UserDevice
                    {
                        InstanceGuid = mouse, ProductGuid = mouse, InstanceName = "Census Mouse", ProductName = "Census Mouse",
                        CapType = PadForge.Engine.InputDeviceType.Mouse, IsOnline = true, IsEnabled = true, MouseDpi = 1600,
                    });
                    SettingsManager.UserSettings = new SettingsCollection();
                    for (int s = 0; s < SettingsManager.SlotCreated.Length; s++) SettingsManager.SlotCreated[s] = s == 0;
                    var p = new Pages { Vm = new MainViewModel() };
                    c.P = p;
                    var pad = p.Vm.Pads[0];
                    pad.OutputType = PadForge.Engine.VirtualControllerType.Xbox;
                    // A mouse that steers a stick: the row's own line has the
                    // travel settings, and an added source Mouse Speed's.
                    var steered = pad.Mappings.First(m => m.TargetSettingName == "LeftThumbAxisX");
                    steered.LoadDescriptor("Mouse Travel X");
                    steered.PrimarySourceDeviceGuid = mouse.ToString();
                    steered.ExtraSources.Add(new MappingSourceItem { Kind = "Direct", Descriptor = "Axis 1", DeviceGuid = mouse.ToString() });
                    // A macro, two profiles and a shift layer, which have
                    // places of their own and menus per card and per tab.
                    pad.Macros.Add(new MacroItem { Name = "Census Macro" });
                    pad.RebuildLayerTabs(new[] { new PadForge.Engine.Data.ShiftActivator { LayerMask = "Shift1", LayerName = "Aim" } });
                    p.Vm.Settings.ProfileItems.Add(new ProfileListItem { Id = "p1", Name = "Racing" });
                    p.Vm.Settings.ProfileItems.Add(new ProfileListItem { Id = "p2", Name = "Rally" });
                    // A device connected, one that is not, and one the panel
                    // shows while the walks begin.
                    c.Online = new DeviceRowViewModel { DeviceName = "Census Pad", InstanceGuid = Guid.NewGuid(), IsHidHideAvailable = true, IsOnline = true };
                    c.Offline = new DeviceRowViewModel { DeviceName = "Census Stick", InstanceGuid = Guid.NewGuid(), IsHidHideAvailable = true };
                    c.Decoy = new DeviceRowViewModel { DeviceName = "Census Decoy", InstanceGuid = Guid.NewGuid(), IsHidHideAvailable = true, IsOnline = true };
                    foreach (var row in new[] { c.Online, c.Offline, c.Decoy }) p.Vm.Devices.Devices.Add(row);
                    p.Vm.Devices.SelectedDevice = c.Decoy;
                    // Home's cards.
                    p.Vm.Dashboard.SlotSummaries.Clear();
                    p.Vm.Dashboard.SlotSummaries.Add(new SlotSummary(0) { OutputType = PadForge.Engine.VirtualControllerType.Xbox, TypeInstanceLabel = "1", SlotNumber = 1 });

                    p.Pad = new PadPage { DataContext = pad };
                    p.Settings = new SettingsPage { DataContext = p.Vm.Settings };
                    p.Extras = new ExtrasPage { DataContext = p.Vm.Dashboard };
                    p.Devices = new DevicesPage { DataContext = p.Vm.Devices };
                    p.Profiles = new ProfilesPage { DataContext = p.Vm.Settings };
                    c.Home = new HomePage { DataContext = p.Vm.Dashboard };
                    c.About = new AboutPage { DataContext = p.Vm.Settings };
                    foreach (var page in new FrameworkElement[] { p.Pad, p.Settings, p.Extras, p.Devices, p.Profiles, c.Home, c.About })
                    {
                        page.Measure(new Size(1456, 985));
                        page.Arrange(new Rect(0, 0, 1456, 985));
                        page.UpdateLayout();
                    }
                    PageRenderDump.Settle(dispatcher);

                    // A plugin running, with a setting of its own and one of
                    // the device it added, as a plugin declares them.
                    c.Plugins = new PluginCatalogRig(vm: p.Vm.Dashboard);
                    c.Plugins.AddRunning("census.exe");
                    var session = c.Plugins.Connect();
                    Assert.StartsWith("ok key=dead", session.Final("setting key=dead kind=number name=\"Dead Zone\" min=0 max=20 unit=% default=3"));
                    string declared = session.Final("declare key=pedals name=\"Pedal Box\" {\"buttons\":[\"A\"]}");
                    var added = c.Plugins.Host.Sessions.Devices.Find(declared.Substring(6));
                    Assert.NotNull(added);
                    Assert.StartsWith("ok key=clutch", session.Final("setting key=clutch kind=switch name=\"Clutch Inverted\" device=pedals"));
                    c.Beat = new Timer(_ => session.Send("beat"), null, 1000, 1000);
                    p.Vm.Devices.Devices.Add(new DeviceRowViewModel { DeviceName = "Pedal Box", InstanceGuid = added.Guid });
                    p.Vm.Devices.RefreshCounts();
                    c.Plugins.Controller.Refresh();

                    // Home's cards join the catalog as the window's provider
                    // gives them, and a user's own database row as the
                    // Controller Database's part gives it, with a row of the
                    // user's own in force.
                    string sdl = "030000000d0f00009900000000000000";
                    Func<IReadOnlyList<PadForge.Services.Catalog.LiveItem>> homeCards = () => AssistantHomeCards.Items(c.Home, p.Vm.Dashboard, p.Vm.Pads);
                    PadForge.Services.Catalog.LiveCatalog.AddProvider(homeCards);
                    providers.Add(homeCards);
                    c.Database = new PadForge.Services.ControllerDatabaseController(p.Vm, () => { }, () => { })
                    {
                        OwnRows = Array.Empty<string>(),
                        Zone = TimeZoneInfo.Utc,
                        Log = _ => { },
                        SettleAfterSwitch = _ => { },
                    };
                    c.Database.Start();
                    Assert.True(PadForge.Common.Input.SdlMappingRow.TryParse(sdl + ",Census Stick,a:b0,b:b1,platform:Windows,", out var mine, out _));
                    ControllerDatabaseMine.Change(m => m.With(new ControllerDatabaseMineRow(sdl, mine, DateTime.UtcNow, string.Empty, Guid.Empty, Array.Empty<ControllerDatabaseBefore>())));

                    // The window: the sidebar's stand-ins, and every page in
                    // one cell under a ContentPresenter, as the window's
                    // sidebar draws them in its template's ContentOverlay:
                    // the window's visual tree reaches the pages, and its
                    // logical tree stops above them.
                    var sidebar = new System.Windows.Controls.StackPanel { Width = 180 };
                    foreach (string tag in new[] { "Home", "Devices", "Pad1", "Profiles", "Extras", "Settings", "About" })
                    {
                        var entry = new System.Windows.Controls.Button { Content = tag, Tag = tag, Height = 36 };
                        c.Entries[tag] = entry;
                        sidebar.Children.Add(entry);
                    }
                    var grid = new System.Windows.Controls.Grid();
                    grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
                    grid.Children.Add(sidebar);
                    var held = new System.Windows.Controls.Grid();
                    foreach (var page in c.AllPages)
                    {
                        page.Visibility = Visibility.Collapsed;
                        held.Children.Add(page);
                    }
                    var overlay = new System.Windows.Controls.ContentPresenter { Content = held };
                    System.Windows.Controls.Grid.SetColumn(overlay, 1);
                    grid.Children.Add(overlay);
                    window = new Window
                    {
                        Content = grid, Width = 1640, Height = 1000, Left = -10000, Top = -10000,
                        ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                    };
                    window.Show();
                    p.Window = window;

                    p.Service = new SearchService(p.Vm, p.PageOf, bound => p.Pad.DataContext = bound);
                    p.Service.SetIndex(SearchIndexBuilder.Build(SearchSources.Load()));
                    var navigator = new SearchNavigator(p.Service, p.PageOf, p.Vm,
                        tag => { c.Moves.Add("opened " + tag); c.Go(tag); },
                        slot => { c.Moves.Add("opened controller " + (slot + 1)); c.Go("Pad" + (slot + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)); },
                        () => c.Moves.Add("opened Add a Controller"));
                    p.Pointer = new AssistantPointer(() => window);
                    p.Catalog = new AssistantCatalog(p.Service, navigator, p.PageOf, p.Vm, bound => p.Pad.DataContext = bound, () => AssistantWindowPlace.InFront,
                        new AssistantCatalogParts
                        {
                            CatalogPageOf = page => page switch { SearchPage.Home => c.Home, SearchPage.About => c.About, _ => null },
                            NavItem = tag => tag != null && c.Entries.TryGetValue(tag, out var entry) && entry.IsVisible ? entry : null,
                            NavTag = () => p.Vm.SelectedNavTag,
                            MainWindow = () => window,
                            Pointer = p.Pointer,
                        });
                    c.Go("Home");
                    PageRenderDump.Settle(dispatcher);

                    // Find a Mapping, open: a dialog's places.
                    PadForge.Common.Input.SdlMappingRow Row(string guid, string name)
                    {
                        Assert.True(PadForge.Common.Input.SdlMappingRow.TryParse(guid + "," + name + ",a:b0,b:b1,platform:Windows,", out var row, out _));
                        return row;
                    }
                    c.Dialog = new SdlMappingDialog(SdlMappingDialog.Mode.Find, "Census Stick", sdl, new[] { "Xbox #1" })
                    {
                        Owner = window, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                    };
                    c.Dialog.SetRows(new List<PadForge.Services.SdlFindRow>
                    {
                        new(Row("030000000d0f00009200000000000000", "Arcade Stick"), PadForge.Common.Input.SdlMappingSource.Community, false) { Detail = "Community" },
                        new(Row("030000000d0f00008500000000000000", "Fight Pad"), PadForge.Common.Input.SdlMappingSource.Sdl, false) { Detail = "SDL" },
                    });
                    c.Dialog.Show();
                    PadForge.Services.Catalog.DialogRegistry.Register(c.Dialog);
                    PageRenderDump.Settle(dispatcher);
                    body(c);
                }
                finally
                {
                    c.P?.Pointer?.Clear();
                    foreach (var provider in providers) PadForge.Services.Catalog.LiveCatalog.RemoveProvider(provider);
                    try { c.Dialog?.Close(); } catch (InvalidOperationException) { }
                    if (c.Beat != null)
                    {
                        // The last beat ends before its pipe closes.
                        using var beaten = new ManualResetEvent(false);
                        if (c.Beat.Dispose(beaten)) beaten.WaitOne(TimeSpan.FromSeconds(10));
                    }
                    c.Plugins?.Dispose();
                    c.Database?.Dispose();
                    ControllerDatabaseMine.Current = mineBefore;
                    foreach (var fold in c.Folds) fold.EndSearch();
                    window?.Close();
                    PadForge.Services.Catalog.MouseSteeringCatalog.DrawnOn = drawnOn;
                    PadForge.Common.CardFoldState.Load(kept);
                    SettingsManager.SlotCreated = made;
                    SettingsManager.UserDevices = devices;
                    SettingsManager.UserSettings = settings;
                }
            }, TimeSpan.FromMinutes(15), "the census timed out");
        }

        /// <summary>
        /// Every kind of place the catalog gives, walked from far away (#600
        /// item 7, AS-77): another page, another section, the controller's
        /// list on its picture with every row hidden by its box and its stick
        /// groups folded, the Devices page's search and both segments hiding
        /// every device and another device's panel open, another extra's page
        /// open, and every fold card and More shut. For one place of each
        /// shape find gives (each kind, each page's markup, what a place is
        /// shown for, a row's option, each live object and row), each step
        /// of the walk rings a control that shows, the program makes no move
        /// and changes nothing the user did not, and the user's click on the
        /// ringed control, as WPF routes it, brings the next step, until the
        /// place rings with the program's words. A plain show of the same
        /// place rings it too. A kind nothing walks fails by name, and so
        /// does each case named below that no walk reached. Places the user
        /// alone may touch are refused.
        /// </summary>
        [Fact]
        public void EveryKindOfPlace_IsWalkedClickByClick_AndRingsWhenShown()
        {
            WithCensus(c =>
            {
                var p = c.P;
                GoFar(c, "census");
                var live = new HashSet<string>(PadForge.Services.Catalog.LiveCatalog.Entries(p.Vm).Select(e => e.Id), StringComparer.Ordinal);
                var found = new List<AssistantPlace>();
                foreach (string kind in AssistantCatalog.Kinds) found.AddRange(p.Catalog.Find(null, kind, 100_000).Places);
                var byShape = found.GroupBy(x => ShapeOf(x, live)).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
                var failures = new List<string>();
                var walked = new List<AssistantPlace>();
                var tried = new HashSet<string>(StringComparer.Ordinal);

                void Try(string name, AssistantPlace place)
                {
                    if (!tried.Add(place.Id)) return;
                    var trail = new List<string>();
                    string walk = WalkOf(c, place.Id, trail);
                    if (walk != null) failures.Add(name + " (" + place.Id + "), its walk: " + walk + " [" + string.Join(" | ", trail) + "]");
                    string show = PlainShowOf(c, place.Id);
                    if (show != null) failures.Add(name + " (" + place.Id + "), a plain show: " + show);
                    if (walk == null) walked.Add(place);
                }

                foreach (var shape in byShape)
                {
                    // The tray's menu is drawn in the notification area,
                    // outside PadForge's window, where no ring can go: its
                    // items show the words alone (T1's AS-79 test).
                    if (shape.Key.EndsWith(" tray", StringComparison.Ordinal)) continue;
                    var place = shape.FirstOrDefault(x => x.Showing && x.Reach != "yours-alone");
                    if (place != null)
                    {
                        Try(shape.Key, place);
                        continue;
                    }
                    // The user's alone: refused, never pointed at.
                    var own = shape.First();
                    try
                    {
                        p.Catalog.Show(new AssistantShowRequest { Program = "Census", Walk = true, Steps = new[] { StepAt(own.Id) } });
                        p.Pointer.Clear();
                        failures.Add(shape.Key + " (" + own.Id + "): the user's alone, and pointed at");
                    }
                    catch (AssistantRefusal refused) when (refused.Code != "users-own")
                    {
                        failures.Add(shape.Key + " (" + own.Id + "): no place of it shows to walk, and it was refused with " + refused.Code);
                    }
                    catch (AssistantRefusal)
                    {
                        // Refused as the user's own, as it should be.
                    }
                }

                // A place on another device's panel: one of the panel's, named
                // for the device the walk must pick.
                string decoy = AutomationDevices.IdOf(c.Decoy.InstanceGuid);
                var panel = found.FirstOrDefault(x => x.Kind == "setting" && x.Showing && x.Reach != "yours-alone" && x.Id.EndsWith("@d" + decoy, StringComparison.Ordinal));
                if (panel == null) failures.Add("a setting of a device's panel: none shows");
                else
                {
                    string other = panel.Id.Replace("@d" + decoy, "@d" + AutomationDevices.IdOf(c.Online.InstanceGuid), StringComparison.Ordinal);
                    Try("a setting of another device's panel", new AssistantPlace { Id = other, Kind = panel.Kind, Showing = true, Reach = panel.Reach });
                }

                // The cases item 7 and the brief name, each reached by some
                // walk: the shapes above give one place each, and a case no
                // shape's place stands for is walked by its own first place.
                var named = new (string Name, Func<AssistantPlace, bool> Is)[]
                {
                    ("a page's sidebar entry", x => x.Kind == "page"),
                    ("a controller", x => x.Kind == "controller"),
                    ("a controller page's section", x => x.Kind == "section"),
                    ("a controller page's part", x => x.Kind == "part"),
                    ("a controller page's setting in a card", x => x.Kind == "setting" && x.Id.StartsWith("pad/", StringComparison.Ordinal) && !x.Id.Contains("#row", StringComparison.Ordinal)),
                    ("an option of a mapping row", x => x.Id.Contains("#row", StringComparison.Ordinal)),
                    ("a mouse setting of a row's own source", x => x.Id.StartsWith("row/c", StringComparison.Ordinal) && x.Id.Split('/').Length == 5),
                    ("a mouse setting of a row's added source", x => x.Id.StartsWith("row/c", StringComparison.Ordinal) && x.Id.Split('/').Length == 6),
                    ("a layer tab's own menu item", x => x.Kind == "menu_item" && x.Id.Contains("ShiftLayerTabStrip", StringComparison.Ordinal)
                                                         && SearchIds.TryRead(x.Id, out _, out _, out int? tab, out _) && tab != null),
                    ("a menu item of a page's More", x => x.Kind == "menu_item" && x.Id.StartsWith("pad.menuitem/", StringComparison.Ordinal) && !x.Id.Contains("ShiftLayerTabStrip", StringComparison.Ordinal)),
                    ("a macro", x => x.Kind == "macro"),
                    ("a Settings page setting", x => x.Id.StartsWith("settings/", StringComparison.Ordinal)),
                    ("an extra's tile", x => x.Kind == "extra"),
                    ("a setting on an extra's page", x => x.Id.StartsWith("extras/", StringComparison.Ordinal)),
                    ("a card", x => x.Kind == "card"),
                    ("an action", x => x.Kind == "action" && !x.Id.StartsWith("home/", StringComparison.Ordinal)),
                    ("a device", x => x.Kind == "device"),
                    ("a setting of the device panel", x => x.Id.StartsWith("devices/", StringComparison.Ordinal)),
                    ("a profile", x => x.Kind == "profile"),
                    ("a profile card's own menu item", x => x.Id.StartsWith("profiles.menuitem/", StringComparison.Ordinal) && x.Id.Contains("@i", StringComparison.Ordinal)),
                    ("a plugin", x => x.Kind == "plugin" && x.Id != PadForge.Services.Plugins.PluginsController.TestControllerId),
                    ("the Test Controller", x => x.Id == PadForge.Services.Plugins.PluginsController.TestControllerId),
                    ("a plugin's own setting", x => x.Kind == "plugin_setting" && x.Id.StartsWith("plugin/", StringComparison.Ordinal)),
                    ("a setting a plugin declares for its device", x => x.Kind == "plugin_setting" && x.Id.StartsWith("device/", StringComparison.Ordinal)),
                    ("a Home card", x => x.Id.StartsWith("home/c", StringComparison.Ordinal) && x.Id.Split('/').Length == 2),
                    ("a Home card's button", x => x.Id.StartsWith("home/c", StringComparison.Ordinal) && x.Id.Split('/').Length == 3),
                    ("About's markup", x => x.Id.StartsWith("about", StringComparison.Ordinal)),
                    ("a user's database row", x => x.Id.StartsWith("db/", StringComparison.Ordinal) && !x.Id.EndsWith("/remove", StringComparison.Ordinal)),
                    ("its Remove Mine", x => x.Id.StartsWith("db/", StringComparison.Ordinal) && x.Id.EndsWith("/remove", StringComparison.Ordinal)),
                    ("a dialog", x => x.Kind == "dialog"),
                    ("a dialog's own control", x => x.Id.StartsWith("dialog/SdlMappingDialog/", StringComparison.Ordinal)),
                };
                foreach (var (name, @is) in named)
                {
                    if (walked.Any(@is)) continue;
                    var place = found.FirstOrDefault(x => @is(x) && x.Showing && x.Reach != "yours-alone");
                    if (place == null)
                    {
                        failures.Add(name + ": the census found no place of it to walk");
                        continue;
                    }
                    Try(name, place);
                }
                // Every kind find takes.
                foreach (string kind in AssistantCatalog.Kinds)
                    if (!walked.Any(x => x.Kind == kind)) failures.Add("find's kind " + kind + ": no walk of one reached it");
                Assert.True(failures.Count == 0, failures.Count + " failed of " + tried.Count + " places walked:\n" + string.Join("\n", failures));
            });
        }

        /// <summary>A place's shape: its kind, then for an entry of Search's
        /// or the catalog's markup its page and kind as its ID begins and what
        /// it is shown for (a controller, a device, an item, a row's option),
        /// and for a live object or row its ID with each name and number made
        /// a star.</summary>
        private static string ShapeOf(AssistantPlace place, ISet<string> live)
        {
            string id = place.Id;
            if (live.Contains(id))
            {
                string[] parts = id.Split('/');
                for (int i = 1; i < parts.Length; i++)
                    parts[i] = parts[i] is "mouse" or "remove" ? parts[i]
                        : System.Text.RegularExpressions.Regex.IsMatch(parts[i], "^[cs][0-9]+$") ? parts[i][0] + "*" : "*";
                return place.Kind + " " + string.Join("/", parts);
            }
            SearchIds.TryRead(id, out string bare, out int? controller, out int? item, out string device);
            int slash = bare.IndexOf('/');
            var shape = new System.Text.StringBuilder(place.Kind).Append(' ').Append(slash < 0 ? bare : bare.Substring(0, slash));
            if (bare.Contains("/Tray/", StringComparison.Ordinal)) shape.Append(" tray");
            if (bare.Contains('[')) shape.Append(" item");
            if (bare.Contains("#row", StringComparison.Ordinal)) shape.Append(" row");
            if (controller != null) shape.Append(" @c");
            if (device != null) shape.Append(" @d");
            if (item != null) shape.Append(" @i");
            return shape.ToString();
        }

        /// <summary>Far from every place: Home's page (Devices' for a place on
        /// Home), the controller page on another section with its list on the
        /// picture, its box hiding every row, its stick groups folded and no
        /// row picked, Macros &amp; Menus on its menus, the Devices page's
        /// search and both segments hiding every device and another device's
        /// panel open, another Settings group, another extra's page, and every
        /// fold card and More shut. Nothing here is the user's: the walk
        /// starts after it.</summary>
        private static void GoFar(Census c, string id)
        {
            var p = c.P;
            p.Pointer.Clear();
            c.Go(id.StartsWith("home", StringComparison.Ordinal) || id == "page/Home" ? "Devices" : "Home");
            var pad = p.Vm.Pads[0];
            p.Pad.DataContext = pad;
            pad.PickedTarget = null;
            p.Pad.OpenTab(PadViewModel.PictureTabIndex);
            p.Pad.OpenTab(PadViewModel.MenusTabIndex);
            p.Pad.OpenSection(PadViewModel.SticksSection);
            pad.MappingSurfaceScope = "All";
            pad.MappingInputSearch = "zz census";
            var grid = (System.Windows.Controls.DataGrid)p.Pad.FindName("MappingDataGrid");
            grid.SelectedItem = null;
            foreach (var stick in new[] { MappingCategory.LeftStick, MappingCategory.RightStick }) pad.SetListGroupOpen(stick, false);
            foreach (var band in SearchLocator.VisualDescendants(grid).OfType<System.Windows.Controls.Expander>())
                if (band.DataContext is System.Windows.Data.CollectionViewGroup { Name: MappingCategory category } && band.IsExpanded != pad.IsListGroupOpen(category))
                    band.IsExpanded = pad.IsListGroupOpen(category);
            var d = p.Vm.Devices;
            d.SelectedDevice = c.Decoy;
            d.SearchText = "zz census";
            d.Filter = DeviceFilter.Wheel;
            d.Connection = DeviceConnection.Connected;
            p.Vm.Settings.SelectedSettingsGroup = p.Vm.Settings.SelectedSettingsGroup == SettingsGroup.Data ? SettingsGroup.General : SettingsGroup.Data;
            p.Extras.Show(id.Contains("/Crosshair/", StringComparison.Ordinal) ? "Magnifier" : "Crosshair");
            Pump(p.Window.Dispatcher, 100);
            c.Folds = new[] { p.Pad, p.Settings, p.Extras, p.Devices, p.Profiles, c.Home, (FrameworkElement)c.About }
                .SelectMany(page => SearchLocator.VisualDescendants(page).OfType<PadForge.Controls.FoldPart>())
                .Distinct().ToList();
            foreach (var fold in c.Folds)
                if (fold.IsExpanded && fold is not PadForge.Controls.FoldCard { CanOpen: false }) fold.ShowForSearch(false);
            Pump(p.Window.Dispatcher, 100);
        }

        /// <summary>What the user sees and has chosen: the page, the
        /// controller page's section, parts, list, box, scope and picks, the
        /// Devices page's search, segments and device, the Settings group and
        /// its search, the profile picked, the extra shown, and which folds
        /// are open. A walk changes none of them itself.</summary>
        private static string StateOf(Census c)
        {
            var p = c.P;
            var pad = p.Vm.Pads[0];
            var d = p.Vm.Devices;
            var grid = (System.Windows.Controls.DataGrid)p.Pad.FindName("MappingDataGrid");
            return string.Join("; ", new[]
            {
                "page " + p.Vm.SelectedNavTag,
                "shown " + c.Shown?.GetType().Name,
                "controller " + (p.Pad.DataContext as PadViewModel)?.PadIndex,
                "tab " + pad.SelectedConfigTab,
                "buttons " + pad.ButtonsViewIndex,
                "macros " + pad.MacrosPartIndex,
                "macro " + pad.SelectedMacro?.Name,
                "box " + pad.MappingInputSearch,
                "scope " + pad.MappingSurfaceScope,
                "picked " + pad.PickedTarget,
                "row " + (grid.SelectedItem as MappingItem)?.TargetSettingName,
                "sticks " + pad.IsListGroupOpen(MappingCategory.LeftStick) + "," + pad.IsListGroupOpen(MappingCategory.RightStick),
                "search " + d.SearchText,
                "show " + d.Filter,
                "connection " + d.Connection,
                "device " + d.SelectedDevice?.DeviceName,
                "group " + p.Vm.Settings.SelectedSettingsGroup,
                "settings search " + p.Vm.Settings.SettingsSearchText,
                "profile " + p.Vm.Settings.SelectedProfile?.Id,
                "extra " + p.Extras.CurrentExtra,
                "open " + string.Join(",", c.Folds.Select((f, i) => f.IsExpanded ? i : -1).Where(i => i >= 0)),
            });
        }

        /// <summary>Walks to a place and plays the user: each step must ring
        /// a control that shows, with no move and no change of the program's
        /// since the user's last click, and the user's click on it brings the
        /// next step, until the place itself rings with the program's words.
        /// Null when it does, else what went wrong. The steps' words and what
        /// each rang go in <paramref name="trail"/>.</summary>
        private static string WalkOf(Census c, string id, List<string> trail)
        {
            var p = c.P;
            GoFar(c, id);
            string state = StateOf(c);
            c.Moves.Clear();
            try
            {
                p.Catalog.Show(new AssistantShowRequest
                {
                    Program = "Census",
                    Walk = true,
                    Steps = new[] { new AssistantShowStep { Place = id, Words = Census.Words, Advance = AssistantAdvance.Next } },
                });
            }
            catch (AssistantRefusal refused)
            {
                return "refused, " + refused.Code + ": " + refused.Words;
            }
            object seen = null;
            for (int step = 1; step <= 16; step++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while ((p.Pointer.Callout == null || ReferenceEquals(p.Pointer.Callout, seen)) && clock.ElapsedMilliseconds < 5000) Pump(p.Window.Dispatcher, 25);
                var callout = p.Pointer.Callout;
                if (callout == null || ReferenceEquals(callout, seen)) return "step " + step + " never came";
                seen = callout;
                var target = p.Pointer.Target;
                trail.Add(callout.Words + " -> " + Named(target));
                if (target == null || !target.IsVisible || System.Windows.Documents.AdornerLayer.GetAdornerLayer(target) == null)
                    return "step " + step + " rings nothing";
                if (c.Moves.Count > 0) return "the program made a move by step " + step + ": " + string.Join(", ", c.Moves);
                string now = StateOf(c);
                if (now != state) return "the program changed what the user sees by step " + step + ": " + Changed(state, now);
                if (callout.Words == Census.Words)
                {
                    var element = p.Catalog.ElementOf(id);
                    p.Pointer.Clear();
                    return ReferenceEquals(element, target) ? null : "the last step rang " + Named(target) + ", not the place's own " + Named(element);
                }
                // The user's click on the ringed control does its work, and
                // what it changes is the user's.
                Act(c, target);
                state = StateOf(c);
                Hear(target);
            }
            p.Pointer.Clear();
            return "more than 16 steps";
        }

        /// <summary>A plain show of a place from far away rings it: null when
        /// it does, else what went wrong.</summary>
        private static string PlainShowOf(Census c, string id)
        {
            var p = c.P;
            GoFar(c, id);
            try
            {
                p.Catalog.Show(new AssistantShowRequest
                {
                    Program = "Census",
                    Steps = new[] { new AssistantShowStep { Place = id, Words = Census.Words, Advance = AssistantAdvance.Next } },
                });
            }
            catch (AssistantRefusal refused)
            {
                return "refused, " + refused.Code + ": " + refused.Words;
            }
            bool Rings() => p.Pointer.Callout != null && p.Pointer.Target is { IsVisible: true } t && System.Windows.Documents.AdornerLayer.GetAdornerLayer(t) != null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!Rings() && clock.ElapsedMilliseconds < 5000) Pump(p.Window.Dispatcher, 25);
            string result = Rings() ? null : "it rings nothing";
            p.Pointer.Clear();
            return result;
        }

        /// <summary>The user's click on a ringed control does what that
        /// control does, at once: a sidebar entry opens its page, a box is
        /// emptied, a picker shows every row, a fold opens, a segment, a
        /// section, a part or a group is picked, a group's band opens it, a
        /// tile or a crumb opens its page, and a row is picked. A screen
        /// reader's pattern does it where the control has one.</summary>
        private static void Act(Census c, FrameworkElement target)
        {
            switch (target)
            {
                case System.Windows.Controls.Button entry when c.Entries.ContainsValue(entry):
                    c.Go((string)entry.Tag);
                    break;
                case System.Windows.Controls.TextBox box:
                    box.Text = string.Empty;
                    break;
                case System.Windows.Controls.ComboBox picker:
                    picker.SelectedItem = picker.Items.OfType<System.Windows.Controls.ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == "All");
                    break;
                case PadForge.Controls.FoldPart fold:
                    ((System.Windows.Automation.Provider.IExpandCollapseProvider)System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(fold)
                        .GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse)).Expand();
                    break;
                case System.Windows.Controls.Expander group:
                    group.IsExpanded = true;
                    break;
                case System.Windows.Controls.RadioButton radio:
                    ((System.Windows.Automation.Provider.ISelectionItemProvider)System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(radio)
                        .GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)).Select();
                    break;
                case System.Windows.Controls.Primitives.ToggleButton band:
                    band.IsChecked = true;
                    break;
                case PadForge.Controls.ExtraTile tile:
                    tile.RaiseEvent(new RoutedEventArgs(PadForge.Controls.ExtraTile.OpenRequestedEvent, tile));
                    break;
                case System.Windows.Controls.Primitives.ButtonBase button:
                    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
                    break;
                case System.Windows.Controls.ListBoxItem row:
                    row.IsSelected = true;
                    break;
                case System.Windows.Controls.DataGridRow row:
                    for (DependencyObject at = row; at != null; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
                    {
                        if (at is not System.Windows.Controls.DataGrid grid) continue;
                        grid.SelectedItem = row.Item;
                        break;
                    }
                    break;
            }
        }

        /// <summary>The guided pointer hears the user's act as WPF routes it:
        /// a key in a box, a click anywhere else.</summary>
        private static void Hear(FrameworkElement target)
        {
            if (target is System.Windows.Controls.TextBox && PresentationSource.FromVisual(target) is { } source)
            {
                target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Back)
                {
                    RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
                });
                return;
            }
            Click(target);
        }

        /// <summary>An element as a failure names it: its type and its
        /// automation ID or name.</summary>
        private static string Named(FrameworkElement element)
        {
            if (element == null) return "nothing";
            string id = System.Windows.Automation.AutomationProperties.GetAutomationId(element);
            if (string.IsNullOrEmpty(id)) id = element.Name;
            if (string.IsNullOrEmpty(id)) id = System.Windows.Automation.AutomationProperties.GetName(element);
            return element.GetType().Name + (string.IsNullOrEmpty(id) ? string.Empty : " " + id);
        }

        /// <summary>The parts of a state that differ.</summary>
        private static string Changed(string before, string after)
        {
            var was = before.Split("; ");
            var now = after.Split("; ");
            return string.Join(", ", now.Where((part, i) => i >= was.Length || was[i] != part).Select((part, i) => part));
        }
    }
}
