using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The controller page's six sections (#574). The page had up to fourteen
    /// tabs in two rows, and shows one row of six sections now. These hold
    /// the map from a tab to its section, hold the page's markup to that map,
    /// hold every id a UI tool finds a tab by, and pin the header that took
    /// the two tiers' place.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PadSectionsTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static string PageText() => ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml");

        private static string CodeBehind() => ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");

        private static XDocument Page() => XDocument.Parse(PageText());

        private static string Name(XElement e) => (string)e.Attribute(X + "Name");

        private static string Id(XElement e) => (string)e.Attribute("AutomationProperties.AutomationId");

        private static XElement Named(XDocument page, string name)
            => Assert.Single(page.Descendants(), e => Name(e) == name);

        /// <summary>The six tab items of the page's content, in order.</summary>
        private static List<XElement> Sections(XDocument page)
        {
            var content = Assert.Single(page.Descendants(), e => e.Name.LocalName == "ContentTabControl"
                && (string)e.Attribute("SelectedIndex") == "{Binding SelectedSection, Mode=OneWay}");
            return content.Elements().Where(e => e.Name.LocalName == "TabItem").ToList();
        }

        // What each tab is called in the page: the name of its group or of
        // its part button, and the id a UI tool finds it by.
        private static readonly Dictionary<int, (string Name, string Id)> Tabs = new()
        {
            [0] = ("TabController", "TabController"),
            [1] = ("TabMacros", "MacrosTab"),
            [2] = ("TabMappings", "MappingsTab"),
            [3] = ("TabSticks", "TabSticks"),
            [4] = ("TabTriggers", "TabTriggers"),
            [5] = ("TabForceFeedback", "TabForceFeedback"),
            [6] = ("TabAdaptiveTriggers", "TabAdaptiveTriggers"),
            [7] = ("TabLighting", "TabLighting"),
            [8] = ("TabGyro", "TabGyro"),
            [9] = ("TabImpulseTriggers", "TabImpulseTriggers"),
            [10] = ("TabTouchpad", "TabTouchpad"),
            [11] = ("TabWheel", "TabWheel"),
            [12] = ("TabAudio", "TabAudio"),
            [13] = ("TabPointer", "TabPointer"),
            [14] = ("TabMouse", "TabMouse"),
            [15] = ("TabMenus", "MenusTab"),
            [16] = ("TabBassShakers", "BassShakersTab"),
            [17] = ("OutputGroup", "OutputTab"),
        };

        // ── the map ────────────────────────────────────────────────────

        /// <summary>The six sections and what each holds, in the order the
        /// page shows them. Every one of the eighteen tabs is in exactly one
        /// section, so a tab number still names one place on the page.</summary>
        [Fact]
        public void EveryTabIsInOneSection_InThePagesOrder()
        {
            Assert.Equal(6, PadViewModel.SectionCount);
            Assert.Equal(new[] { 0, 2, 17 }, PadViewModel.TabsOfSection(PadViewModel.ButtonsSection));
            Assert.Equal(new[] { 3, 4, 14 }, PadViewModel.TabsOfSection(PadViewModel.SticksSection));
            Assert.Equal(new[] { 5, 9, 6, 11, 16 }, PadViewModel.TabsOfSection(PadViewModel.RumbleSection));
            Assert.Equal(new[] { 8, 10, 13 }, PadViewModel.TabsOfSection(PadViewModel.MotionSection));
            Assert.Equal(new[] { 7, 12 }, PadViewModel.TabsOfSection(PadViewModel.LightsSection));
            Assert.Equal(new[] { 1, 15 }, PadViewModel.TabsOfSection(PadViewModel.MacrosSection));
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, new[]
            {
                PadViewModel.ButtonsSection, PadViewModel.SticksSection, PadViewModel.RumbleSection,
                PadViewModel.MotionSection, PadViewModel.LightsSection, PadViewModel.MacrosSection,
            });

            var seen = new List<int>();
            for (int section = 0; section < PadViewModel.SectionCount; section++)
            {
                foreach (int tab in PadViewModel.TabsOfSection(section))
                {
                    Assert.Equal(section, PadViewModel.SectionOfTab(tab));
                    seen.Add(tab);
                }
            }
            Assert.Equal(Enumerable.Range(0, 18), seen.OrderBy(t => t));
            Assert.Equal(12, PadViewModel.AudioTabIndex);
            Assert.Equal(16, PadViewModel.BassShakersTabIndex);
            Assert.Equal(17, PadViewModel.OutputTabIndex);

            // A number that names no tab or no section.
            Assert.Equal(-1, PadViewModel.SectionOfTab(18));
            Assert.Equal(-1, PadViewModel.SectionOfTab(-1));
            Assert.Empty(PadViewModel.TabsOfSection(6));
            Assert.Empty(PadViewModel.TabsOfSection(-1));

            // Buttons and Macros and Menus show one part at a time. The
            // four between them stack their tabs.
            Assert.False(PadViewModel.IsStackedSection(PadViewModel.ButtonsSection));
            Assert.True(PadViewModel.IsStackedSection(PadViewModel.SticksSection));
            Assert.True(PadViewModel.IsStackedSection(PadViewModel.RumbleSection));
            Assert.True(PadViewModel.IsStackedSection(PadViewModel.MotionSection));
            Assert.True(PadViewModel.IsStackedSection(PadViewModel.LightsSection));
            Assert.False(PadViewModel.IsStackedSection(PadViewModel.MacrosSection));
        }

        /// <summary>The selected section follows the selected tab, and its
        /// change is raised once per change of section: the page's content
        /// is bound to it, and a group selected inside the section that
        /// already shows must not rebuild the page.</summary>
        [Fact]
        public void TheSelectedSectionFollowsTheTab_AndIsRaisedOnlyWhenItChanges()
        {
            var vm = new PadViewModel(0);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            Assert.Equal(0, vm.SelectedConfigTab);
            Assert.Equal(PadViewModel.ButtonsSection, vm.SelectedSection);

            vm.SelectedConfigTab = 3;
            Assert.Equal(PadViewModel.SticksSection, vm.SelectedSection);
            Assert.Equal(1, raised.Count(n => n == nameof(PadViewModel.SelectedSection)));
            // The tab's own change comes first, so a listener to it reads
            // the new section already.
            Assert.True(raised.IndexOf(nameof(PadViewModel.SelectedConfigTab)) < raised.IndexOf(nameof(PadViewModel.SelectedSection)));

            raised.Clear();
            vm.SelectedConfigTab = 4;
            Assert.Equal(PadViewModel.SticksSection, vm.SelectedSection);
            Assert.Contains(nameof(PadViewModel.SelectedConfigTab), raised);
            Assert.DoesNotContain(nameof(PadViewModel.SelectedSection), raised);

            raised.Clear();
            vm.SelectedConfigTab = 4;
            Assert.Empty(raised);

            foreach (var (tab, section) in new[] { (5, 2), (9, 2), (6, 2), (11, 2), (16, 2), (8, 3), (10, 3), (13, 3), (7, 4), (12, 4), (1, 5), (15, 5), (0, 0), (2, 0), (17, 0), (14, 1) })
            {
                vm.SelectedConfigTab = tab;
                Assert.Equal(section, vm.SelectedSection);
            }
        }

        /// <summary>Buttons remembers the view it last showed and Macros and
        /// Menus the part, while another section is selected too, so a click
        /// on Buttons comes back to the list the user left. Buttons has two
        /// views: tab 2 is the list, and tabs 0 and 17 are the picture, with
        /// the output cards under it. The view's change is raised with the
        /// tab's, and only when the view changes.</summary>
        [Fact]
        public void ButtonsAndMacros_RememberTheirPart()
        {
            var vm = new PadViewModel(0);
            Assert.Equal(0, vm.ButtonsViewIndex);
            Assert.True(vm.IsButtonsPictureView);
            Assert.False(vm.IsButtonsListView);
            Assert.Equal(0, vm.MacrosPartIndex);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.SelectedConfigTab = 2;
            Assert.Equal(1, vm.ButtonsViewIndex);
            Assert.True(vm.IsButtonsListView);
            Assert.False(vm.IsButtonsPictureView);
            Assert.Contains(nameof(PadViewModel.ButtonsViewIndex), raised);
            Assert.Contains(nameof(PadViewModel.IsButtonsListView), raised);
            Assert.Contains(nameof(PadViewModel.IsButtonsPictureView), raised);
            Assert.Contains(nameof(PadViewModel.ShowOutputGroup), raised);
            Assert.DoesNotContain(nameof(PadViewModel.SelectedSection), raised);
            // The tab's own change comes first, so a listener to it reads
            // the new view already.
            Assert.True(raised.IndexOf(nameof(PadViewModel.SelectedConfigTab)) < raised.IndexOf(nameof(PadViewModel.ButtonsViewIndex)));

            vm.SelectedConfigTab = 5;
            Assert.Equal(1, vm.ButtonsViewIndex);
            Assert.Equal(2, vm.LastTabOfSection(PadViewModel.ButtonsSection));

            vm.SelectedConfigTab = 15;
            Assert.Equal(1, vm.MacrosPartIndex);
            Assert.Equal(1, vm.ButtonsViewIndex);
            // Output is the picture, with its cards under it.
            vm.SelectedConfigTab = 17;
            Assert.Equal(0, vm.ButtonsViewIndex);
            Assert.Equal(1, vm.MacrosPartIndex);
            Assert.Equal(15, vm.LastTabOfSection(PadViewModel.MacrosSection));
            vm.SelectedConfigTab = 1;
            Assert.Equal(0, vm.MacrosPartIndex);
            // From the output cards to the picture changes no view.
            vm.SelectedConfigTab = 17;
            raised.Clear();
            vm.SelectedConfigTab = 0;
            Assert.Equal(0, vm.ButtonsViewIndex);
            Assert.DoesNotContain(nameof(PadViewModel.ButtonsViewIndex), raised);

            // A stacked section remembers the group it was last sent to.
            Assert.Equal(5, vm.LastTabOfSection(PadViewModel.RumbleSection));
            Assert.Equal(3, vm.LastTabOfSection(PadViewModel.SticksSection));
            Assert.Equal(0, vm.LastTabOfSection(6));
        }

        /// <summary>What is on the page. A stacked section shows all of its
        /// tabs at once, so the Bass Shakers meters and the Audio group's
        /// device list count as shown while their section is selected,
        /// whichever of its groups the selection names. In Buttons the
        /// picture and the output cards show together and the list shows
        /// alone. Macros and Menus shows only the selected part.</summary>
        [Fact]
        public void IsTabShown_IsTheWholeOfAStackedSection_AndOnePartOfTheOthers()
        {
            var vm = new PadViewModel(0);
            Assert.True(vm.IsTabShown(0));
            Assert.False(vm.IsTabShown(2));
            Assert.True(vm.IsTabShown(17));
            Assert.False(vm.IsTabShown(3));

            vm.SelectedConfigTab = 2;
            Assert.True(vm.IsTabShown(2));
            Assert.False(vm.IsTabShown(0));
            Assert.False(vm.IsTabShown(17));
            vm.SelectedConfigTab = 17;
            Assert.True(vm.IsTabShown(0));
            Assert.True(vm.IsTabShown(17));
            Assert.False(vm.IsTabShown(2));

            vm.SelectedConfigTab = 5;
            foreach (int tab in new[] { 5, 9, 6, 11, 16 })
                Assert.True(vm.IsTabShown(tab));
            Assert.False(vm.IsTabShown(0));
            Assert.False(vm.IsTabShown(12));

            vm.SelectedConfigTab = 7;
            Assert.True(vm.IsTabShown(PadViewModel.AudioTabIndex));
            Assert.False(vm.IsTabShown(PadViewModel.BassShakersTabIndex));

            vm.SelectedConfigTab = 15;
            Assert.True(vm.IsTabShown(15));
            Assert.False(vm.IsTabShown(1));
            Assert.False(vm.IsTabShown(18));
        }

        /// <summary>A jump to the Audio group by its number still refreshes
        /// it, as selecting the Audio tab did. A jump to Lighting, in the
        /// same section, does not: the page asks for the refresh there, and
        /// only when the selected device has the group.</summary>
        [Fact]
        public void AJumpToAudio_RefreshesIt_AndAJumpToItsNeighborLeavesThatToThePage()
        {
            var vm = new PadViewModel(0);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SelectedConfigTab = 7;
            Assert.Equal(PadViewModel.LightsSection, vm.SelectedSection);
            Assert.DoesNotContain(nameof(PadViewModel.SoundMacros), raised);
            vm.SelectedConfigTab = 5;
            Assert.Equal(PadViewModel.RumbleSection, vm.SelectedSection);
            Assert.DoesNotContain(nameof(PadViewModel.RumbleAudioEnabled), raised);

            // The jump by number, read from the source: running it would
            // list this machine's sound devices.
            string setter = ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "PadViewModel.cs");
            Assert.Contains("if (value == AudioTabIndex) RefreshAudioGroup();", setter);
            Assert.Contains("else if (value == BassShakersTabIndex) RefreshBassShakersGroup();", setter);

            string code = CodeBehind();
            Assert.Contains("sectionVm.RefreshAudioGroup();", code);
            Assert.Contains("sectionVm.RefreshBassShakersGroup();", code);
            Assert.Contains("&& IsTabAvailable(PadViewModel.AudioTabIndex))", code);
            Assert.Contains("&& IsTabAvailable(PadViewModel.BassShakersTabIndex))", code);
            // The two places that refresh the mirror list on a device change
            // ask whether the group is on the page, not whether it is the
            // selected tab.
            Assert.Equal(2, setter.Split(new[] { "if (IsTabShown(AudioTabIndex)) RefreshMirrorSources();" }, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("SelectedConfigTab == AudioTabIndex", setter);
            Assert.Contains("&& _currentPadVm.IsTabShown(PadViewModel.BassShakersTabIndex);", code);
        }

        /// <summary>The summary the header reads its power button, state
        /// word and stage chips from is the selected slot's, and follows a
        /// rebuild of the summaries.</summary>
        [Fact]
        public void TheSelectedSlotSummary_FollowsTheSelectedPad_AndARebuild()
        {
            var vm = new DashboardViewModel();
            Assert.Null(vm.SelectedSlotSummary);
            vm.RefreshActiveSlots(new[] { 0, 2 }, canAddMore: true);
            Assert.Null(vm.SelectedSlotSummary);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.SetSelectedPad(2);
            Assert.Same(vm.SlotSummaries[1], vm.SelectedSlotSummary);
            Assert.Contains(nameof(DashboardViewModel.SelectedSlotSummary), raised);
            Assert.True(vm.SelectedSlotSummary.IsSelected);

            // The slots change: the summaries are made again, and the
            // header must not keep one that left the list.
            vm.RefreshActiveSlots(new[] { 0, 1, 2 }, canAddMore: true);
            Assert.Same(vm.SlotSummaries[2], vm.SelectedSlotSummary);
            Assert.Equal(2, vm.SelectedSlotSummary.PadIndex);

            vm.RefreshActiveSlots(new[] { 0, 1 }, canAddMore: true);
            Assert.Null(vm.SelectedSlotSummary);

            vm.SetSelectedPad(-1);
            Assert.Null(vm.SelectedSlotSummary);
        }

        // ── the page ───────────────────────────────────────────────────

        /// <summary>The page's content is six tab items, one per section,
        /// and each holds exactly the tabs the map gives it, in the map's
        /// order. A stacked section is one scrolling column of named groups.
        /// Buttons and Macros and Menus are a part switch over a tab control
        /// of their own, so the preview, the mapping table, the macro editor
        /// and the menu editor each load only while they show, as they did
        /// as tabs.</summary>
        [Fact]
        public void ThePageHoldsEachSectionsTabs_InTheMapsOrder()
        {
            var page = Page();
            var sections = Sections(page);
            Assert.Equal(PadViewModel.SectionCount, sections.Count);

            var scrollNames = new Dictionary<int, string>
            {
                [PadViewModel.SticksSection] = "SticksSectionScroll",
                [PadViewModel.RumbleSection] = "RumbleSectionScroll",
                [PadViewModel.MotionSection] = "MotionSectionScroll",
                [PadViewModel.LightsSection] = "LightsSectionScroll",
            };
            var partIndex = new Dictionary<int, string>
            {
                [PadViewModel.ButtonsSection] = "{Binding ButtonsViewIndex, Mode=OneWay}",
                [PadViewModel.MacrosSection] = "{Binding MacrosPartIndex, Mode=OneWay}",
            };
            string[] sectionKeys =
            {
                "Pad_Section_Buttons", "Pad_Section_Sticks", "Pad_Section_Rumble",
                "Pad_Section_Motion", "Pad_Section_Lights", "Pad_Section_Macros",
            };

            for (int section = 0; section < PadViewModel.SectionCount; section++)
            {
                var item = sections[section];
                var content = Assert.Single(item.Elements());
                var expected = PadViewModel.TabsOfSection(section).Select(t => Tabs[t].Name).ToList();

                if (PadViewModel.IsStackedSection(section))
                {
                    Assert.Equal("ScrollViewer", content.Name.LocalName);
                    Assert.Equal(scrollNames[section], Name(content));
                    Assert.Equal("{Binding " + sectionKeys[section] + ", Source={x:Static strings:Strings.Instance}}",
                        (string)content.Attribute("AutomationProperties.Name"));
                    var column = Assert.Single(content.Elements());
                    Assert.Equal("StackPanel", column.Name.LocalName);
                    Assert.All(column.Elements(), g => Assert.Equal("StackPanel", g.Name.LocalName));
                    Assert.Equal(expected, column.Elements().Select(Name));
                }
                else
                {
                    // Buttons has two views, and its third tab, Output, is
                    // the cards under the picture. Macros and Menus has one
                    // part per tab.
                    bool buttonsSection = section == PadViewModel.ButtonsSection;
                    Assert.Equal("Grid", content.Name.LocalName);
                    // The picture's view of Buttons scrolls as a page, so
                    // its tab control sits inside that page. The part switch
                    // of Macros and Menus sits right over its tab control.
                    var inner = buttonsSection
                        ? Assert.Single(content.Descendants(), e => e.Name.LocalName == "ContentTabControl")
                        : Assert.Single(content.Elements(), e => e.Name.LocalName == "ContentTabControl");
                    Assert.Equal(partIndex[section], (string)inner.Attribute("SelectedIndex"));
                    var switched = buttonsSection ? expected.Take(2).ToList() : expected;
                    Assert.Equal(switched.Count, inner.Elements().Count(e => e.Name.LocalName == "TabItem"));
                    // The switch's buttons, in order, each with its tab's
                    // number. A layer chip is a radio button too, inside an
                    // item template, and is not one of them.
                    var buttons = content.Descendants().Where(e => e.Name.LocalName == "RadioButton"
                        && e.Attribute("Tag") != null
                        && e.Ancestors().All(a => a != inner)
                        && e.Ancestors().All(a => a.Name.LocalName != "DataTemplate")).ToList();
                    Assert.Equal(switched, buttons.Select(Name));
                    Assert.Equal(PadViewModel.TabsOfSection(section).Take(switched.Count).Select(t => t.ToString()),
                        buttons.Select(b => (string)b.Attribute("Tag")));
                    Assert.All(buttons, b => Assert.Equal("TabBtn_Click", (string)b.Attribute("Click")));
                    Assert.All(buttons, b => Assert.Equal("TabBtn_Checked", (string)b.Attribute("Checked")));
                    // One radio group per strip, so neither unchecks the
                    // other's choice.
                    string group = Assert.Single(buttons.Select(b => (string)b.Attribute("GroupName")).Distinct());
                    Assert.Equal(buttonsSection ? "PadButtonsPart" : "PadMacrosPart", group);
                }
            }

            // Buttons is one layout. The picture is the first item of its
            // tab control and the second item is empty, so the picture is
            // off the page in the list. The table and the output cards sit
            // beside that control, never inside it: one table serves both
            // views.
            var buttonsRoot = Named(page, "MappingsTabRoot");
            Assert.Same(Assert.Single(sections[PadViewModel.ButtonsSection].Elements()), buttonsRoot);
            // The top row stays put over a page that scrolls while the
            // picture shows.
            var scroll = Named(page, "ButtonsScroll");
            Assert.Equal("ScrollViewer", scroll.Name.LocalName);
            Assert.Same(buttonsRoot, scroll.Parent);
            Assert.Same(buttonsRoot, Named(page, "ButtonsTopRow").Parent);
            Assert.Equal("{Binding Pad_Section_Buttons, Source={x:Static strings:Strings.Instance}}",
                (string)scroll.Attribute("AutomationProperties.Name"));
            var buttonsPage = Assert.Single(scroll.Elements());
            Assert.Equal("ButtonsPage", Name(buttonsPage));
            var picture = Assert.Single(buttonsPage.Elements(), e => e.Name.LocalName == "ContentTabControl");
            var views = picture.Elements().Where(e => e.Name.LocalName == "TabItem").ToList();
            Assert.Contains(views[0], Named(page, "ControllerModel3D").Ancestors());
            Assert.Contains(views[0], Named(page, "ControllerModel2D").Ancestors());
            Assert.Contains(views[0], Named(page, "TouchSensors").Ancestors());
            Assert.False(views[1].HasElements);
            var table = Named(page, "MappingDataGrid");
            Assert.Same(buttonsPage, table.Parent);
            Assert.Same(buttonsPage, Named(page, "OutputGroup").Parent);
            Assert.DoesNotContain(picture, table.Ancestors());
            // The menu editor is the second part of Macros and Menus.
            var macrosParts = sections[PadViewModel.MacrosSection].Descendants()
                .Single(e => e.Name.LocalName == "ContentTabControl").Elements()
                .Where(e => e.Name.LocalName == "TabItem").ToList();
            Assert.Equal(2, macrosParts.Count);
        }

        /// <summary>Every id a UI tool or a test found a tab by is still in
        /// the page, once. A part of Buttons or of Macros and Menus keeps
        /// its button. A tab that became a group gives its id to the
        /// group's heading, where a tool that looked for the tab finds the
        /// group.</summary>
        [Fact]
        public void EveryTabKeepsItsId()
        {
            var page = Page();
            foreach (var (tab, (name, id)) in Tabs.Select(p => (p.Key, p.Value)))
            {
                var carrier = Assert.Single(page.Descendants(), e => Id(e) == id);
                var named = Named(page, name);
                if (PadViewModel.IsStackedSection(PadViewModel.SectionOfTab(tab)))
                {
                    Assert.Equal("StackPanel", named.Name.LocalName);
                    Assert.Equal("TextBlock", carrier.Name.LocalName);
                    Assert.Contains(named, carrier.Ancestors());
                    // The heading is the group's first words: nothing with
                    // text of its own comes before it.
                    var before = named.Descendants().TakeWhile(e => e != carrier)
                        .Where(e => e.Name.LocalName == "TextBlock" && e.Attribute("Text") != null);
                    Assert.Empty(before);
                }
                else if (tab == PadViewModel.OutputTabIndex)
                {
                    // Output is the group of cards under the picture. A
                    // group box has a peer, so it can carry the id.
                    Assert.Same(named, carrier);
                    Assert.Equal("GroupBox", carrier.Name.LocalName);
                    Assert.Equal("{Binding Pad_Tab_Output, Source={x:Static strings:Strings.Instance}}",
                        (string)carrier.Attribute("AutomationProperties.Name"));
                }
                else
                {
                    Assert.Same(named, carrier);
                    Assert.Equal("RadioButton", carrier.Name.LocalName);
                    Assert.Equal(tab.ToString(), (string)carrier.Attribute("Tag"));
                }
            }

            // The gates the two slot-type tabs had. Output's group shows
            // under the picture on a slot type that has output cards.
            Assert.Equal("{Binding RumbleAudioTabVisible, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)Named(page, "TabBassShakers").Attribute("Visibility"));
            Assert.Equal("{Binding ShowOutputGroup, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)Named(page, "OutputGroup").Attribute("Visibility"));
            Assert.Contains("public bool ShowOutputGroup => IsButtonsPictureView && OutputTabVisible;",
                ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "PadViewModel.Sections.cs"));
            // Sticks, Triggers and Force Feedback start shown and the rest
            // start hidden until the selected device has them, as the tabs
            // did.
            foreach (int tab in new[] { 3, 4, 5 })
                Assert.Null(Named(page, Tabs[tab].Name).Attribute("Visibility"));
            foreach (int tab in new[] { 6, 7, 8, 9, 10, 11, 12, 13, 14 })
                Assert.Equal("Collapsed", (string)Named(page, Tabs[tab].Name).Attribute("Visibility"));
        }

        /// <summary>The page's code names each group by the tab it was, with
        /// the same pairs as the markup, and gives each stacked section its
        /// column. A pair missing here is a group whose rule the eviction
        /// and the section's button cannot read.</summary>
        [Fact]
        public void TheCodeBehindNamesEveryGroup()
        {
            string code = CodeBehind();
            for (int section = 0; section < PadViewModel.SectionCount; section++)
            {
                if (!PadViewModel.IsStackedSection(section)) continue;
                foreach (int tab in PadViewModel.TabsOfSection(section))
                {
                    string key = tab == PadViewModel.AudioTabIndex ? "PadViewModel.AudioTabIndex"
                        : tab == PadViewModel.BassShakersTabIndex ? "PadViewModel.BassShakersTabIndex"
                        : tab.ToString();
                    Assert.Contains("            " + key + " => " + Tabs[tab].Name + ",", code);
                }
            }
            foreach (string pair in new[]
            {
                "PadViewModel.SticksSection => SticksSectionScroll,",
                "PadViewModel.RumbleSection => RumbleSectionScroll,",
                "PadViewModel.MotionSection => MotionSectionScroll,",
                "PadViewModel.LightsSection => LightsSectionScroll,",
                "PadViewModel.ButtonsSection => SectionButtons,",
                "PadViewModel.SticksSection => SectionSticks,",
                "PadViewModel.RumbleSection => SectionRumble,",
                "PadViewModel.MotionSection => SectionMotion,",
                "PadViewModel.LightsSection => SectionLights,",
                "PadViewModel.MacrosSection => SectionMacros,",
            })
                Assert.Contains(pair, code);

            // The two slot-type gates are read from the view model.
            Assert.Contains("if (tab == PadViewModel.OutputTabIndex) return vm.OutputTabVisible;", code);
            Assert.Contains("if (tab == PadViewModel.BassShakersTabIndex) return vm.RumbleAudioTabVisible;", code);
            // A section opens where it was left, and Ctrl+Tab steps through
            // the sections the header shows.
            Assert.Contains("int tab = vm.LastTabOfSection(section);", code);
            Assert.Contains("try { CycleSection((Keyboard.Modifiers & ModifierKeys.Shift) != 0); }", code);
            Assert.DoesNotContain("FindVisualChildren", code);
            Assert.DoesNotContain("PadTabDevice", code);
        }

        // ── the header ─────────────────────────────────────────────────

        /// <summary>The six section buttons: their ids, their numbers, their
        /// names and their one click handler, in the map's order, in one
        /// wrapping row of the header.</summary>
        [Fact]
        public void TheHeaderHasTheSixSections()
        {
            var page = Page();
            var strip = Named(page, "SectionStrip");
            Assert.Equal("WrapPanel", strip.Name.LocalName);
            var buttons = strip.Elements().ToList();
            Assert.All(buttons, b => Assert.Equal("RadioButton", b.Name.LocalName));
            Assert.Equal(new[] { "SectionButtons", "SectionSticks", "SectionRumble", "SectionMotion", "SectionLights", "SectionMacros" },
                buttons.Select(Name));
            Assert.Equal(new[] { "Section_Buttons", "Section_Sticks", "Section_Rumble", "Section_Motion", "Section_Lights", "Section_Macros" },
                buttons.Select(Id));
            Assert.Equal(new[] { "0", "1", "2", "3", "4", "5" }, buttons.Select(b => (string)b.Attribute("Tag")));
            Assert.All(buttons, b => Assert.Equal("SectionBtn_Click", (string)b.Attribute("Click")));
            // A check that did not come from a click goes to the section
            // too: UI Automation's Select, a screen reader's way to activate
            // a radio button, checks it and raises no Click.
            Assert.All(buttons, b => Assert.Equal("SectionBtn_Checked", (string)b.Attribute("Checked")));
            string code = CodeBehind();
            Assert.Matches(@"private void SectionBtn_Checked\(object sender, RoutedEventArgs e\)\s*\{\s*if \(_syncingStrip\) return;\s*if \(sender is RadioButton rb && TryGetTagIndex\(rb, out int section\)\)\s*GoToSection\(section\);", code);
            Assert.Matches(@"private void TabBtn_Checked\(object sender, RoutedEventArgs e\)\s*\{\s*if \(_syncingStrip\) return;", code);
            // The page's own sync of the checks is not such a check.
            Assert.Matches(@"_syncingStrip = true;\s*try\s*\{\s*int section = vm\.SelectedSection;", code);
            Assert.All(buttons, b => Assert.Equal("{StaticResource SectionStripButton}", (string)b.Attribute("Style")));
            Assert.Equal(new[] { "Buttons", "Sticks", "Rumble", "Motion", "Lights", "Macros" }
                    .Select(k => "{Binding Pad_Section_" + k + ", Source={x:Static strings:Strings.Instance}}"),
                buttons.Select(b => (string)Assert.Single(b.Elements()).Attribute("Text")));

            // The strip is in the header, above the content.
            var content = Sections(page)[0].Parent;
            Assert.DoesNotContain(content, strip.Ancestors());
            Assert.Contains(strip.Ancestors(), a => a.Name.LocalName == "StackPanel" && (string)a.Attribute("Grid.Row") == "0");

            // The two tiers and their styles are gone, with the SLOT label
            // and the DEVICE label.
            string text = PageText();
            Assert.DoesNotContain("TabStripButtonCold", text);
            Assert.DoesNotContain("PadTabDevice", text);
            Assert.DoesNotContain("Dashboard_Slot", text);
            Assert.DoesNotContain("Pad_ScopeDevice", text);
            Assert.DoesNotContain("{Binding SelectedConfigTab", text);
        }

        /// <summary>The header's first two lines: the type chip and the
        /// title, the Preset chip as it was, the power button with the
        /// slot's state word, Driven by with the device picker as it was,
        /// Add Device, and the stage chips that came from the slot's Home
        /// card.</summary>
        [Fact]
        public void TheHeaderNamesTheControllerAndItsDevice()
        {
            var page = Page();
            string text = PageText();

            var title = Named(page, "PadTitle");
            Assert.Equal("Level1", (string)title.Attribute("AutomationProperties.HeadingLevel"));
            Assert.Equal(new[] { "{Binding OutputTypeDisplayName, Mode=OneWay}", "{Binding TypeInstanceLabel, Mode=OneWay, StringFormat=' #{0}'}" },
                title.Elements().Select(r => (string)r.Attribute("Text")));
            Assert.Contains("Data=\"{Binding TypeGeometry, Mode=OneWay}\"", text);
            Assert.Contains("Text=\"{Binding TypeGlyph, Mode=OneWay}\"", text);

            // The Preset chip and its two pickers, bindings and resets.
            var preset = Named(page, "HMaestroProfileBar");
            Assert.Contains(preset, Named(page, "HMaestroProfileCombo").Ancestors());
            Assert.Contains(preset, Named(page, "KbmSurfacesCombo").Ancestors());
            Assert.Equal("{Binding ProfileId, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
                (string)Named(page, "HMaestroProfileCombo").Attribute("SelectedValue"));
            Assert.Single(preset.Descendants(), e => Id(e) == "Reset_PadViewModel_ProfileId_490");
            Assert.Single(preset.Descendants(), e => Id(e) == "Reset_PadViewModel_KbmSurfaces_531");

            // The power button: the slot's summary is its data.
            var power = Named(page, "SlotPowerButton");
            Assert.Equal("StateToggleButton", power.Name.LocalName);
            Assert.Equal("Pad_SlotPower", Id(power));
            Assert.Equal("{Binding SelectedSlotSummary}", (string)power.Attribute("DataContext"));
            Assert.Equal("{Binding IsEnabled}", (string)power.Attribute("IsOn"));
            Assert.Equal("SlotPower_Click", (string)power.Attribute("Click"));
            Assert.Contains(Named(page, "SlotPowerHost"), power.Ancestors());
            // The two hosts the window binds start with no data. Left to
            // inherit, each would take the page's view model, which has no
            // SelectedSlotSummary, and fail every binding until the window
            // hands over the dashboard.
            Assert.Equal("{x:Null}", (string)Named(page, "SlotPowerHost").Attribute("DataContext"));
            Assert.Equal("{x:Null}", (string)Named(page, "StageChips").Attribute("DataContext"));
            Assert.DoesNotContain("SelectedSlotSummary", ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "PadViewModel.cs"));
            var word = Named(page, "SlotStateWord");
            Assert.Contains(power, word.Ancestors());
            Assert.Equal("{Binding StatusText}", (string)word.Attribute("Text"));
            Assert.Equal("True", word.Attributes().Single(a => a.Name.LocalName == "LiveText.Polite").Value);

            // Driven by, the picker, Add Device.
            var drivenBy = Named(page, "DrivenByLabel");
            Assert.Equal("{Binding Pad_DrivenBy, Source={x:Static strings:Strings.Instance}}", (string)drivenBy.Attribute("Text"));
            var picker = Assert.Single(page.Descendants(), e => e.Name.LocalName == "ComboBox"
                && (string)e.Attribute("SelectedItem") == "{Binding SelectedMappedDevice, Mode=TwoWay}");
            Assert.Equal("{Binding MappedDevices}", (string)picker.Attribute("ItemsSource"));
            Assert.Same(drivenBy.Parent, picker.Parent);
            var add = Named(page, "AddDeviceButton");
            Assert.Equal("Pad_AddDevice", Id(add));
            Assert.Equal("AddDevice_Click", (string)add.Attribute("Click"));
            Assert.Equal("{Binding Pad_AddDevice, Source={x:Static strings:Strings.Instance}}", (string)add.Attribute("Content"));
            Assert.Same(drivenBy.Parent, add.Parent);

            // The stage chips, and Home's card without them.
            var chips = Named(page, "StageChips");
            Assert.Equal("{Binding SelectedSlotSummary.StageLedger}", (string)chips.Attribute("ItemsSource"));
            Assert.DoesNotContain("StageLedger", ExtrasPageTests.RepoText("PadForge.App", "Views", "HomePage.xaml"));

            // The offer band is in the header still, between the device
            // line and the sections.
            int offer = text.IndexOf("AutomationProperties.AutomationId=\"AssignOfferBanner\"", StringComparison.Ordinal);
            Assert.True(text.IndexOf("x:Name=\"AddDeviceButton\"", StringComparison.Ordinal) < offer);
            Assert.True(offer < text.IndexOf("x:Name=\"SectionStrip\"", StringComparison.Ordinal));

            // The window hands the page the slots' summaries and takes its
            // two requests.
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("PadPageView.BindSlotState(_viewModel.Dashboard);", window);
            Assert.Contains("PadPageView.SlotEnabledToggled += (s, args) =>", window);
            Assert.Contains("PadPageView.AddDeviceRequested += (s, e) => SelectNavItemByTag(\"Devices\");", window);
            string code = CodeBehind();
            Assert.Contains("SlotPowerHost.DataContext = dashboard;", code);
            Assert.Contains("StageChips.DataContext = dashboard;", code);
            Assert.Contains("(vm.PadIndex, !PadForge.Common.Input.SettingsManager.SlotEnabled[vm.PadIndex]));", code);
        }

        /// <summary>The screenshot script reaches every tab through ids the
        /// page has: the section's button, then a part's button or a group's
        /// heading inside that section's column. A row of its table that
        /// names a section the tab is not in, or an id the page dropped,
        /// would send a shot to the wrong place under the right name.</summary>
        [Fact]
        public void TheCaptureScriptsTabTable_NamesIdsThePageHas()
        {
            string script = ExtrasPageTests.RepoText("tools", "capture_all.ps1");
            var page = Page();
            var rows = System.Text.RegularExpressions.Regex.Matches(script,
                @"^    ""(?<name>[A-Za-z ]+)""\s+= @\{ Section = ""(?<section>\w+)""; (?:Part = ""(?<part>\w+)""(?:; Cards = ""(?<cards>\w+)"")?|Column = ""(?<column>\w+)""; Group = ""(?<group>\w+)"") \}\r?$",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            Assert.Equal(18, rows.Count);

            var tabOfId = Tabs.ToDictionary(p => p.Value.Id, p => p.Key);
            var seen = new HashSet<int>();
            foreach (System.Text.RegularExpressions.Match row in rows)
            {
                // A row with cards goes to its part and then opens the cards
                // of the group it names, and that group is the tab it
                // reaches: Output, under the picture.
                string id = row.Groups["cards"].Success ? row.Groups["cards"].Value
                    : row.Groups["part"].Success ? row.Groups["part"].Value : row.Groups["group"].Value;
                if (row.Groups["cards"].Success)
                {
                    Assert.Equal("Output", row.Groups["name"].Value);
                    Assert.Equal("TabController", row.Groups["part"].Value);
                    // The script opens every fold card and More row under
                    // the group: each is an Expander, which reports as a
                    // group, and a combo box that expands too is left alone.
                    var cards = Named(page, "OutputGroup").Descendants().Where(e => e.Name.LocalName == "FoldCard").ToList();
                    Assert.Equal(new[] { "SocdFoldCard", "KbmSocdFoldCard", "KeepAwakeFoldCard" }, cards.Select(Name));
                    Assert.Contains("if ($card.Current.ControlType -ne [System.Windows.Automation.ControlType]::Group) { continue }", script);
                }
                Assert.True(tabOfId.TryGetValue(id, out int tab), row.Groups["name"].Value + " names an id the page does not have: " + id);
                Assert.True(seen.Add(tab), "two rows reach tab " + tab);
                int section = PadViewModel.SectionOfTab(tab);

                var sectionButton = Assert.Single(page.Descendants(), e => Id(e) == row.Groups["section"].Value);
                Assert.Equal(section.ToString(), (string)sectionButton.Attribute("Tag"));
                Assert.Equal(PadViewModel.IsStackedSection(section), row.Groups["group"].Success);
                if (row.Groups["group"].Success)
                {
                    var column = Named(page, row.Groups["column"].Value);
                    Assert.Equal("ScrollViewer", column.Name.LocalName);
                    Assert.Contains(column, Assert.Single(page.Descendants(), e => Id(e) == id).Ancestors());
                }
            }
            Assert.Equal(18, seen.Count);

            // It opens a slot from its Home card by the card's title, and
            // picks a slot's device in the Driven by picker, each by an id.
            // The card's title was the word "Slot" until Home, and the
            // script kept looking for the word.
            Assert.Contains("Find-UIA -Parent $Card -Aid \"Dash_SlotTitle\"", script);
            Assert.DoesNotContain("-eq 'Slot'", script);
            Assert.Contains("AutomationProperties.AutomationId=\"Dash_SlotTitle\"",
                ExtrasPageTests.RepoText("PadForge.App", "Views", "HomePage.xaml"));
            Assert.Contains("Find-UIA -Parent $padPage -Aid \"Pad_DeviceCombo\"", script);
            Assert.Single(page.Descendants(), e => Id(e) == "Pad_DeviceCombo"
                && (string)e.Attribute("SelectedItem") == "{Binding SelectedMappedDevice, Mode=TwoWay}");
            // Nothing in it reads the page's radio buttons as a tab strip
            // any more: the first of them is the Buttons section now.
            Assert.DoesNotContain("$tabs[0]", script);
            Assert.DoesNotContain("Controller Tab\"", script);
        }

        /// <summary>The eight strings the header and the sections added are
        /// in the string table with their English.</summary>
        [Fact]
        public void TheNewStringsAreInTheTable()
        {
            var table = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"));
            string English(string key) => (string)Assert.Single(table.Root!.Elements("data"),
                d => (string)d.Attribute("name") == key).Element("value");
            Assert.Equal("Buttons", English("Pad_Section_Buttons"));
            Assert.Equal("Sticks & Triggers", English("Pad_Section_Sticks"));
            Assert.Equal("Rumble & Haptics", English("Pad_Section_Rumble"));
            Assert.Equal("Motion & Touch", English("Pad_Section_Motion"));
            Assert.Equal("Lights & Sound", English("Pad_Section_Lights"));
            Assert.Equal("Macros & Menus", English("Pad_Section_Macros"));
            Assert.Equal("Driven by", English("Pad_DrivenBy"));
            Assert.Equal("Add Device", English("Pad_AddDevice"));
        }
    }
}
