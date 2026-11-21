using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Search;
using PadForge.Services;
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
        /// over them, opened as the window opens it. <paramref name="bindPad"/>
        /// makes Search's bind step, as the window passes BindPadPage.
        /// <paramref name="inWindow"/> shows the page in a window off the
        /// screen, so a landing's ring has a layer to draw on.</summary>
        private void WithControllers(int bound, Action<MainViewModel, PadPage, SearchService, List<string>> body,
            Func<MainViewModel, PadPage, Action<PadViewModel>> bindPad = null, bool inWindow = false)
        {
            var lines = new List<string>();
            try
            {
                PageRenderDumpPages.WithApp((app, dispatcher) =>
                {
                    bool[] made = (bool[])SettingsManager.SlotCreated.Clone();
                    Window window = null;
                    try
                    {
                        var main = new MainViewModel();
                        for (int i = 0; i < SettingsManager.SlotCreated.Length; i++) SettingsManager.SlotCreated[i] = i < Kinds.Length;
                        for (int i = 0; i < Kinds.Length; i++) main.Pads[i].OutputType = Kinds[i];
                        var page = new PadPage { DataContext = bound >= 0 ? main.Pads[bound] : null };
                        page.BindSteamVrState(new SettingsViewModel { IsSteamVrInstalled = true });
                        if (inWindow)
                        {
                            window = new Window
                            {
                                Content = page, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                            };
                            window.Show();
                        }
                        else
                        {
                            page.Measure(new Size(1456, 985));
                            page.Arrange(new Rect(0, 0, 1456, 985));
                        }
                        page.UpdateLayout();
                        PageRenderDump.Settle(dispatcher);
                        var service = new SearchService(main, p => p == SearchPage.Pad ? page : null, bindPad?.Invoke(main, page));
                        service.SetIndex(SearchIndexBuilder.Build(SearchSources.Load()));
                        var clock = Stopwatch.StartNew();
                        service.RefreshAvailability();
                        lines.Add("availability read in " + clock.ElapsedMilliseconds + " ms");
                        body(main, page, service, lines);
                    }
                    finally
                    {
                        window?.Close();
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

        /// <summary>Gives a controller's right trigger row a second source,
        /// which the Combine option of a row needs, and returns the row.</summary>
        private static MappingItem AddSecondSource(PadViewModel pad)
        {
            var device = Guid.NewGuid();
            string id = device.ToString("D");
            pad.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = device, Name = "Pad", TypeKey = "Gamepad", IsOnline = true });
            var row = pad.Mappings.First(m => m.TargetSettingName == "RightTrigger");
            row.PrimarySourceDeviceGuid = id;
            row.PrimarySourceDeviceLabel = "Pad";
            row.LoadDescriptor("Axis 5");
            row.ExtraSources.Add(new MappingSourceItem { Kind = "Direct", DeviceGuid = id, DeviceLabel = "Pad", Descriptor = "Axis 2" });
            return row;
        }

        private static bool IsCombine(SearchResult r) => r.Entry is { RowOptions: true, Label.Key: "Pad_Combine_Label" };

        /// <summary>Runs the dispatcher for a while, so posted work and
        /// layout are done.</summary>
        private static void Pump(System.Windows.Threading.Dispatcher dispatcher, int milliseconds)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds),
                System.Windows.Threading.DispatcherPriority.Background, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        /// <summary>The Buttons section opens on the picture with nothing
        /// picked, and its table then holds no row. A row option of a row the
        /// controller has is still found, and opening it opens the list on
        /// that row, with the row's options open and its ring drawn. Words in
        /// the list's own box that hide the row are emptied.</summary>
        [Fact]
        public void ARowOption_IsFoundUnderThePicture_AndItsLandingOpensTheListOnTheRow()
        {
            WithControllers(VrPad, (main, page, service, lines) =>
            {
                var xbox = main.Pads[XboxPad];
                var row = AddSecondSource(xbox);
                Assert.True(xbox.IsButtonsPictureView);
                service.RefreshAvailability();
                var result = Search(service, PadForge.Resources.Strings.Strings.Instance.Pad_Combine_Label, lines).Results
                    .FirstOrDefault(r => IsCombine(r) && ReferenceEquals(r.Pad, xbox));
                Assert.NotNull(result);

                var navigator = new SearchNavigator(service, p => p == SearchPage.Pad ? page : null, main, tag =>
                {
                    if (tag.StartsWith("Pad", StringComparison.Ordinal)) page.DataContext = main.Pads[int.Parse(tag.Substring(3)) - 1];
                }, _ => { }, () => { });
                // The list's own box holds words that hide the row there.
                xbox.MappingInputSearch = "zzqqxx";
                navigator.Open(result);
                Pump(page.Dispatcher, 500);

                Assert.Same(xbox, page.DataContext);
                Assert.True(xbox.IsButtonsListView);
                Assert.Equal(string.Empty, xbox.MappingInputSearch);
                var grid = (System.Windows.Controls.DataGrid)page.FindName("MappingDataGrid");
                Assert.Same(row, grid.SelectedItem);
                var container = grid.ItemContainerGenerator.ContainerFromItem(row) as System.Windows.Controls.DataGridRow;
                Assert.NotNull(container);
                Assert.Equal(Visibility.Visible, container.DetailsVisibility);
                bool ringed = SearchLocator.VisualDescendants(container).OfType<UIElement>().Any(e =>
                    System.Windows.Documents.AdornerLayer.GetAdornerLayer(e)?.GetAdorners(e)?.OfType<SearchLandingAdorner>().Any() == true);
                Assert.True(ringed, "the row's option has no ring");
            }, inWindow: true);
        }

        /// <summary>Another controller's view model rows can be stale against
        /// its stored rows, which a visit to its page mirrors in. Search binds
        /// another controller the way a visit does, so a row option the stored
        /// rows have is offered: here a second source on the Xbox controller's
        /// right trigger row, which the Combine option needs.</summary>
        [Fact]
        public void AnotherControllersStoredRows_AreMirroredBeforeTheyAreRead()
        {
            var mirrored = new List<PadViewModel>();
            WithControllers(VrPad, (main, page, service, lines) =>
            {
                var search = Search(service, PadForge.Resources.Strings.Strings.Instance.Pad_Combine_Label, lines);
                Assert.Contains(search.Results, r => IsCombine(r) && ReferenceEquals(r.Pad, main.Pads[XboxPad]));
                // The bound controller was not mirrored: Search did not
                // refresh it, and binds it back by its data alone.
                Assert.DoesNotContain(main.Pads[VrPad], mirrored);
                Assert.Same(main.Pads[VrPad], page.DataContext);
            }, (main, page) => pad =>
            {
                mirrored.Add(pad);
                if (ReferenceEquals(pad, main.Pads[XboxPad]) && !pad.Mappings.Any(m => m.ExtraSources.Count > 0))
                    AddSecondSource(pad);
                page.DataContext = pad;
            });
        }

        /// <summary>The read's time with the window's own bind step: the
        /// stored rows mirrored in under MirrorOnly, the selected device's
        /// inputs refreshed, then the page's data. Cold is the first read
        /// on a new page, warm the reads after it.</summary>
        [Fact]
        public void TheReadsTime_WithTheVisitsBindStep()
        {
            SettingsService settings = null;
            InputService input = null;
            WithControllers(VrPad, (main, page, service, lines) =>
            {
                for (int k = 0; k < 4; k++)
                {
                    var clock = Stopwatch.StartNew();
                    service.RefreshAvailability();
                    lines.Add("warm read " + k + ": " + clock.ElapsedMilliseconds + " ms");
                }
                Assert.Contains(Search(service, "deadzone", lines).Results, r => ReferenceEquals(r.Pad, main.Pads[XboxPad]));
                input.Dispose();
            }, (main, page) =>
            {
                settings = new SettingsService(main);
                input = new InputService(main) { SettingsService = settings };
                return pad =>
                {
                    settings.MirrorOnly(() => InputService.RefreshMappingsToViewModel(pad));
                    var selected = pad.SelectedMappedDevice;
                    if (selected != null && selected.InstanceGuid != Guid.Empty)
                        input.RefreshAvailableInputsForSlot(pad);
                    page.DataContext = pad;
                };
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
