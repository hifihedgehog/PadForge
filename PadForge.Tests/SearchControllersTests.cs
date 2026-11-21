using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Search;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// Search answers for every controller made (#574). The controller page
    /// shows one controller at a time, and a setting another controller has
    /// is still found: its second line names that controller, and opening it
    /// opens that controller's page on it. A setting the bound controller has
    /// stays that controller's. A setting no controller has stays out.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class SearchControllersTests
    {
        private readonly ITestOutputHelper _output;

        public SearchControllersTests(ITestOutputHelper output) => _output = output;

        private static readonly VirtualControllerType[] Kinds =
        {
            VirtualControllerType.Xbox, VirtualControllerType.PlayStation, VirtualControllerType.KeyboardMouse,
            VirtualControllerType.Midi, VirtualControllerType.Vr,
        };

        private const int XboxPad = 0, PlayStationPad = 1, MidiPad = 3, VrPad = 4;

        /// <summary>Five controllers of five kinds, each made, the controller
        /// page bound to <paramref name="bound"/> (or to none), and Search
        /// over them, opened as the window opens it.</summary>
        private void WithControllers(int bound, Action<MainViewModel, PadPage, SearchService, List<string>> body)
        {
            var lines = new List<string>();
            try
            {
                PageRenderDumpPages.WithApp((app, dispatcher) =>
                {
                    bool[] made = (bool[])SettingsManager.SlotCreated.Clone();
                    try
                    {
                        var main = new MainViewModel();
                        for (int i = 0; i < SettingsManager.SlotCreated.Length; i++) SettingsManager.SlotCreated[i] = i < Kinds.Length;
                        for (int i = 0; i < Kinds.Length; i++) main.Pads[i].OutputType = Kinds[i];
                        var page = new PadPage { DataContext = bound >= 0 ? main.Pads[bound] : null };
                        page.BindSteamVrState(new SettingsViewModel { IsSteamVrInstalled = true });
                        page.Measure(new Size(1456, 985));
                        page.Arrange(new Rect(0, 0, 1456, 985));
                        page.UpdateLayout();
                        PageRenderDump.Settle(dispatcher);
                        var service = new SearchService(main, p => p == SearchPage.Pad ? page : null);
                        service.SetIndex(SearchIndexBuilder.Build(SearchSources.Load()));
                        var clock = Stopwatch.StartNew();
                        service.RefreshAvailability();
                        lines.Add("availability read in " + clock.ElapsedMilliseconds + " ms");
                        body(main, page, service, lines);
                    }
                    finally
                    {
                        Array.Copy(made, SettingsManager.SlotCreated, made.Length);
                    }
                }, TimeSpan.FromMinutes(4), "the controllers' search run timed out");
            }
            finally
            {
                foreach (string line in lines) _output.WriteLine(line);
            }
        }

        private static string Line(SearchResult r) => r.GroupLabel + " | " + r.Title + r.QualifierText + " | " + r.Where + " | " + r.Value;

        private static SearchViewModel Search(SearchService service, string query, List<string> lines)
        {
            var search = new SearchViewModel(service.Query) { Query = query };
            lines.Add("\"" + query + "\":");
            lines.AddRange(search.Results.Select(r => "  " + Line(r)));
            lines.Add("  no results text: " + search.NoResultsText);
            return search;
        }

        /// <summary>The results for the deadzone of a stick of
        /// <paramref name="pad"/>: its stick cards' items.</summary>
        private static List<SearchResult> StickDeadzones(SearchViewModel search, PadViewModel pad)
            => search.Results.Where(r => r.Entry is { ItemsPath: "StickConfigs" } && r.Entry.Label.Key == "Pad_Sticks_Section_DeadZone"
                                         && pad.StickConfigs.Contains(r.Payload)).ToList();

        /// <summary>The report: the page last opened is a VR controller's,
        /// which has no stick, and the query is "deadzone". The Xbox
        /// controller's stick deadzones are found, each says it is the Xbox
        /// controller's and reads that controller's value, and the page is
        /// bound to the VR controller again.</summary>
        [Fact]
        public void ASettingOfAnotherController_IsFound_AndNamesItsController()
        {
            WithControllers(VrPad, (main, page, service, lines) =>
            {
                main.Pads[XboxPad].StickConfigs[0].DeadZoneBoth = 8;
                main.Pads[PlayStationPad].StickConfigs[0].DeadZoneBoth = 3;
                service.RefreshAvailability();
                var search = Search(service, "deadzone", lines);

                Assert.Equal(string.Empty, search.NoResultsText);
                var stick = StickDeadzones(search, main.Pads[XboxPad]);
                Assert.Equal(2, stick.Count);
                string xbox = SearchService.SlotName(main.Pads[XboxPad]);
                Assert.All(stick, r => Assert.StartsWith(xbox + PadForge.Resources.Strings.Strings.Instance.Search_WhereSeparator, r.Where));
                Assert.All(stick, r => Assert.Same(main.Pads[XboxPad], r.Pad));
                Assert.StartsWith("8", stick.First(r => ReferenceEquals(r.Payload, main.Pads[XboxPad].StickConfigs[0])).Value);
                Assert.Same(main.Pads[VrPad], page.DataContext);
            });
        }

        /// <summary>A setting the bound controller has is offered once, as
        /// that controller's, though another controller has it too.</summary>
        [Fact]
        public void ASettingOfTheBoundController_StaysItsOwn_AndIsOfferedOnce()
        {
            WithControllers(PlayStationPad, (main, page, service, lines) =>
            {
                var search = Search(service, "deadzone", lines);
                Assert.Empty(StickDeadzones(search, main.Pads[XboxPad]));
                var stick = StickDeadzones(search, main.Pads[PlayStationPad]);
                Assert.Equal(2, stick.Count);
                Assert.All(stick, r => Assert.Same(main.Pads[PlayStationPad], r.Pad));
                Assert.All(stick, r => Assert.StartsWith(SearchService.SlotName(main.Pads[PlayStationPad]), r.Where));
                var settings = search.Results.Where(r => r.Group == SearchGroup.Settings).ToList();
                Assert.Equal(settings.Count, settings.Select(r => (r.Entry, r.Title, r.Qualifier)).Distinct().Count());
                Assert.Same(main.Pads[PlayStationPad], page.DataContext);
            });
        }

        /// <summary>With no controller page opened yet, the same holds:
        /// the first controller that has the setting answers, and the page
        /// is left bound to none.</summary>
        [Fact]
        public void WithNoControllerPageOpened_ASettingIsStillFound()
        {
            WithControllers(-1, (main, page, service, lines) =>
            {
                var search = Search(service, "deadzone", lines);
                Assert.Equal(string.Empty, search.NoResultsText);
                var stick = StickDeadzones(search, main.Pads[XboxPad]);
                Assert.Equal(2, stick.Count);
                Assert.All(stick, r => Assert.Same(main.Pads[XboxPad], r.Pad));
                Assert.Null(page.DataContext);
            });
        }

        /// <summary>Opening another controller's result opens that
        /// controller's page, on the setting's part.</summary>
        [Fact]
        public void OpeningAnotherControllersResult_OpensThatControllersPage()
        {
            WithControllers(MidiPad, (main, page, service, lines) =>
            {
                var result = StickDeadzones(Search(service, "deadzone", lines), main.Pads[XboxPad]).First();
                Assert.Same(main.Pads[XboxPad], result.Pad);

                var opened = new List<string>();
                var navigator = new SearchNavigator(service, p => p == SearchPage.Pad ? page : null, main, tag =>
                {
                    opened.Add(tag);
                    if (tag.StartsWith("Pad", StringComparison.Ordinal)) page.DataContext = main.Pads[int.Parse(tag.Substring(3)) - 1];
                }, _ => { }, () => { });
                navigator.Open(result);
                Assert.Equal(new[] { "Pad" + (XboxPad + 1) }, opened);
                Assert.Same(main.Pads[XboxPad], page.DataContext);
                if (result.Entry.Tab >= 0) Assert.Equal(result.Entry.Tab, main.Pads[XboxPad].SelectedConfigTab);
            });
        }

        /// <summary>Reading the other controllers leaves the page as it was:
        /// bound to its controller, on the part it showed, its picture
        /// showing and bound to that controller all along, and the picture
        /// of no other controller shown.</summary>
        [Fact]
        public void ReadingTheOtherControllers_LeavesThePageOnItsController()
        {
            WithControllers(XboxPad, (main, page, service, lines) =>
            {
                var model = (FrameworkElement)page.FindName("ControllerModel3D");
                var field = model.GetType().GetField("_vm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                main.Pads[XboxPad].SelectedConfigTab = PadViewModel.MacrosTabIndex;
                page.UpdateLayout();
                service.RefreshAvailability();
                Assert.Same(main.Pads[XboxPad], page.DataContext);
                Assert.Equal(PadViewModel.MacrosTabIndex, main.Pads[XboxPad].SelectedConfigTab);
                Assert.Equal(PadViewModel.MacrosSection, main.Pads[XboxPad].SelectedSection);
                Assert.Same(main.Pads[XboxPad], field.GetValue(model));
                Assert.Equal(Visibility.Visible, model.Visibility);
                foreach (string other in new[] { "KBMPreview", "MidiPreview", "VRPreview" })
                    Assert.Equal(Visibility.Collapsed, ((UIElement)page.FindName(other)).Visibility);
                Assert.False(page.ReadingForSearch);
                // And the other controllers were read: the MIDI one's
                // settings are offered.
                Assert.Contains(service.Query("channel"), r => ReferenceEquals(r.Pad, main.Pads[MidiPad]));
            });
        }

        /// <summary>A query that matches nothing still says so, and a part
        /// no controller has (Motion and Touch, with no device that has a
        /// gyro, a touchpad or a pointer) is offered on none.</summary>
        [Fact]
        public void WhatNoControllerHas_StaysOut()
        {
            WithControllers(VrPad, (main, page, service, lines) =>
            {
                var none = Search(service, "zzqqxx", lines);
                Assert.NotEqual(string.Empty, none.NoResultsText);

                foreach (string query in new[] { "gyro", "touchpad", "deadzone", "lean" })
                {
                    var search = Search(service, query, lines);
                    Assert.DoesNotContain(search.Results, r => r.Entry is { Page: SearchPage.Pad, Section: PadViewModel.MotionSection });
                }
            });
        }
    }
}
