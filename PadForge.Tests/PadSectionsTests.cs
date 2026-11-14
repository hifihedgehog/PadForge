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
        /// order. A stacked section scrolls, and holds its named groups in
        /// one column, or two to a row where its board draws them so.
        /// Buttons and Macros and Menus are a part switch over a tab control
        /// of their own, so the preview, the mapping table, the macro editor
        /// and the menu editor each load only while they show, as they did
        /// as tabs. Macros and Menus scrolls as a page, as its board draws
        /// it: a card of the two lists, whose heads are the part switch,
        /// and a card of the editor, which holds the tab control.</summary>
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
                    // A section may hold row styles of its own beside its
                    // one column of groups.
                    var column = Assert.Single(content.Elements(), e => e.Name.LocalName != "ScrollViewer.Resources");
                    if (section == PadViewModel.SticksSection)
                    {
                        Assert.Equal("StackPanel", column.Name.LocalName);
                    }
                    else
                    {
                        // Rumble & Haptics, Motion & Touch and Lights & Sound
                        // are one card per part (the sections pass): two to a
                        // row while each has 600, one under the other in a
                        // narrower window, and a part that stands alone takes
                        // the whole width. A card's own margin is the room
                        // under it, so a row adds none.
                        Assert.Equal("TileGridPanel", column.Name.LocalName);
                        Assert.Equal("600", (string)column.Attribute("MinItemWidth"));
                        Assert.Equal("16", (string)column.Attribute("Gap"));
                        Assert.Equal("0", (string)column.Attribute("RowGap"));
                        Assert.Equal("True", (string)column.Attribute("Balance"));
                        Assert.Null(column.Attribute("FillRows"));
                    }
                    // A group is a group box that carries the group's name and
                    // id and draws nothing of its own: no heading stands over
                    // the cards of any stacked section.
                    Assert.All(column.Elements(), g => Assert.Equal("GroupBox", g.Name.LocalName));
                    Assert.Equal(expected, column.Elements().Select(Name));
                }
                else
                {
                    // Buttons has two views, and its third tab, Output, is
                    // the cards under the picture. Macros and Menus has one
                    // part per tab.
                    bool buttonsSection = section == PadViewModel.ButtonsSection;
                    XElement inner;
                    if (buttonsSection)
                    {
                        // The picture's view of Buttons scrolls as a page,
                        // so its tab control sits inside that page.
                        Assert.Equal("Grid", content.Name.LocalName);
                        inner = Assert.Single(content.Descendants(), e => e.Name.LocalName == "ContentTabControl");
                    }
                    else
                    {
                        // Macros and Menus scrolls as a page of two cards
                        // that stand side by side while the editor has 480
                        // to itself: the lists from 250 to 320 wide, then
                        // the editor with what is left. The editor's card
                        // holds the tab control, and under it in its column
                        // stands the Icon Packages line.
                        Assert.Equal("ScrollViewer", content.Name.LocalName);
                        Assert.Equal("MacrosSectionScroll", Name(content));
                        Assert.Equal("{Binding " + sectionKeys[section] + ", Source={x:Static strings:Strings.Instance}}",
                            (string)content.Attribute("AutomationProperties.Name"));
                        Assert.Equal("36,0", (string)content.Attribute("Padding"));
                        Assert.Equal("Disabled", (string)content.Attribute("HorizontalScrollBarVisibility"));
                        // The section holds the look of its rows beside its
                        // two cards.
                        var cards = Assert.Single(content.Elements(), e => e.Name.LocalName != "ScrollViewer.Resources");
                        Assert.Equal("FlexLinePanel", cards.Name.LocalName);
                        Assert.Equal("16", (string)cards.Attribute("Gap"));
                        Assert.Equal("16", (string)cards.Attribute("LineGap"));
                        Assert.Equal(new[] { "MacrosListCard", "MacrosEditorColumn" }, cards.Elements().Select(Name));
                        var lists = Named(page, "MacrosListCard");
                        var editorColumn = Named(page, "MacrosEditorColumn");
                        var editor = Named(page, "MacrosEditorCard");
                        string Flex(XElement e, string property)
                            => (string)e.Attributes().First(a => a.Name.LocalName == "FlexLinePanel." + property);
                        Assert.Equal("250", Flex(lists, "Basis"));
                        Assert.Equal("250", (string)lists.Attribute("MinWidth"));
                        Assert.Equal("320", (string)lists.Attribute("MaxWidth"));
                        Assert.Equal("480", Flex(editorColumn, "Basis"));
                        Assert.Equal("999", Flex(editorColumn, "Grow"));
                        Assert.Equal("Top", (string)editorColumn.Attribute("VerticalAlignment"));
                        Assert.Equal(new[] { "MacrosEditorCard", "IconPackagesBlock" }, editorColumn.Elements().Select(Name));
                        inner = Assert.Single(editor.Elements());
                        Assert.Equal("ContentTabControl", inner.Name.LocalName);
                        Assert.Same(inner, Assert.Single(content.Descendants(), e => e.Name.LocalName == "ContentTabControl"));
                    }
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
            // The top row is the head of the Buttons card.
            var head = Named(page, "ButtonsCardHead");
            Assert.Same(buttonsRoot, head.Parent);
            Assert.Same(head, Named(page, "ButtonsTopRow").Parent);
            // The first section is named for what the slot's picture holds.
            Assert.Equal("{Binding FirstSectionName, TargetNullValue=''}", (string)scroll.Attribute("AutomationProperties.Name"));
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
        /// its button. A tab that became a group is a group box that carries
        /// the tab's id and name itself, and its cards are all it holds: no
        /// board draws a heading over a group's cards (the sections pass of
        /// #574 took the last labels off Motion and Touch and off Lights and
        /// Sound).</summary>
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
                    Assert.Same(named, carrier);
                    Assert.Equal("GroupBox", carrier.Name.LocalName);
                    Assert.Equal("{StaticResource BareGroupBox}", (string)carrier.Attribute("Style"));
                    Assert.Matches(@"^\{Binding Pad_\w+, Source=\{x:Static strings:Strings\.Instance\}\}$",
                        (string)carrier.Attribute("AutomationProperties.Name"));
                    // No heading and no paragraph over the cards: the one
                    // text a group may hold outside a card is a status line
                    // that code or a binding fills.
                    var panel = Assert.Single(carrier.Elements());
                    Assert.Equal("StackPanel", panel.Name.LocalName);
                    Assert.All(panel.Elements().Where(e => e.Name.LocalName == "TextBlock"), line =>
                        Assert.True(Name(line) != null || ((string)line.Attribute("Text") ?? "").StartsWith("{Binding GameMod", StringComparison.Ordinal),
                            name + " holds a text outside its cards: " + (string)line.Attribute("Text")));
                    Assert.DoesNotContain(panel.Elements(), e => ((string)e.Attribute("Style") ?? "").Contains("TitleTextBlockStyle")
                        || (string)e.Attribute("Style") == "{StaticResource SectionTitle}");
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
        /// and the section's button cannot read. The pairs are one table,
        /// PadPage.TabOfGroupName, which Search places what it finds by too
        /// (#574).</summary>
        [Fact]
        public void TheCodeBehindNamesEveryGroup()
        {
            string code = CodeBehind();
            for (int section = 0; section < PadViewModel.SectionCount; section++)
            {
                if (!PadViewModel.IsStackedSection(section)) continue;
                foreach (int tab in PadViewModel.TabsOfSection(section))
                {
                    Assert.True(PadForge.Views.PadPage.TabOfGroupName.TryGetValue(Tabs[tab].Name, out int named) && named == tab,
                        Tabs[tab].Name + " is not named for tab " + tab);
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
            // The first section is named for what the slot's picture holds
            // (Buttons, Keys & Mouse, Notes & Controls, Hands), the rest by
            // their own strings.
            var names = new[] { "{Binding FirstSectionName}" }.Concat(new[] { "Sticks", "Rumble", "Motion", "Lights", "Macros" }
                    .Select(k => "{Binding Pad_Section_" + k + ", Source={x:Static strings:Strings.Instance}}")).ToList();
            // A tab is its icon and its name in a row (the world-class
            // pass), so the tab says its name itself: a panel has none.
            var rows = buttons.Select(b => Assert.Single(b.Elements())).ToList();
            Assert.All(rows, r => Assert.Equal("StackPanel", r.Name.LocalName));
            Assert.Equal(names, rows.Select(r => (string)Assert.Single(r.Elements(), e => e.Name.LocalName == "TextBlock").Attribute("Text")));
            Assert.Equal(new[] { "{Binding FirstSectionName, TargetNullValue='', FallbackValue=''}" }.Concat(names.Skip(1)),
                buttons.Select(b => (string)b.Attribute("AutomationProperties.Name")));
            // Each has one icon before its name: a line icon, and for
            // Sticks & Triggers the stick's own filled shape.
            Assert.All(rows, r => Assert.Equal(2, r.Elements().Count()));
            Assert.Equal(new[] { "SectionButtonsGeometry", null, "SectionRumbleGeometry", "SectionMotionGeometry", "SectionLightsGeometry", "SectionMacrosGeometry" },
                rows.Select(r => r.Elements().First().Name.LocalName == "DecorativeStrokeIcon"
                    ? ((string)r.Elements().First().Attribute("Data")).Replace("{StaticResource ", string.Empty).TrimEnd('}')
                    : null));
            var stick = rows[1].Elements().First();
            Assert.Equal("Path", stick.Name.LocalName);
            Assert.Equal("{StaticResource ZackslyStickGeometry}", (string)stick.Attribute("Data"));
            Assert.Equal("{StaticResource SectionIconShape}", (string)stick.Attribute("Style"));
            Assert.Equal("LeftToRight", (string)stick.Attribute("FlowDirection"));
            // The icon is ember on the tab whose section shows, and the
            // tab stands on a lit underline.
            string markup = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml");
            Assert.Equal(2, markup.Split(new[] { "<DataTrigger Binding=\"{Binding IsChecked, RelativeSource={RelativeSource AncestorType=RadioButton}}\" Value=\"True\">" }, StringSplitOptions.None).Length - 1);
            Assert.Contains("<controls:Halo x:Name=\"UnderLight\" CornerRadius=\"1\" Radius=\"9\" Strength=\"0.32\"", markup);
            Assert.Contains("<Setter TargetName=\"UnderLight\" Property=\"IsLit\" Value=\"True\"/>", markup);

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

        /// <summary>The header's two lines: the type chip and the title, the
        /// Preset with its two pickers, the power button with the slot's
        /// state word, then Driven by with a pill for each device and Add
        /// Device. The stage icons are on the slot's Home card and not
        /// here.</summary>
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
            // The summary is set on the light around the button, which is
            // the button's parent, so it reaches both.
            var light = power.Parent;
            Assert.Equal("Halo", light.Name.LocalName);
            Assert.Equal("SlotPowerHalo", Name(light));
            Assert.Equal("{Binding SelectedSlotSummary}", (string)light.Attribute("DataContext"));
            Assert.Null(power.Attribute("DataContext"));
            Assert.Equal("{Binding IsEnabled}", (string)power.Attribute("IsOn"));
            Assert.Equal("SlotPower_Click", (string)power.Attribute("Click"));
            Assert.Contains(Named(page, "SlotPowerHost"), power.Ancestors());
            // The pill is the cards' own, a step larger, and its words are
            // the state's, in full in its tooltip over what a click does.
            var pillStyle = Assert.Single(power.Descendants(), e => e.Name.LocalName == "Style" && (string)e.Attribute("TargetType") == "Button");
            Assert.Equal("{StaticResource StatePill}", (string)pillStyle.Attribute("BasedOn"));
            Assert.Equal("{Binding StateWord}", (string)Named(page, "SlotStateWord").Attribute("Text"));
            Assert.Equal("{Binding StateWord, Mode=OneWay, TargetNullValue='', FallbackValue=''}", (string)power.Attribute("AutomationProperties.HelpText"));
            var tip = Assert.Single(power.Elements(), e => e.Name.LocalName == "Button.ToolTip");
            Assert.Equal(new[] { "{Binding StateWord}", "{Binding Home_Card_Power, Source={x:Static strings:Strings.Instance}}" },
                tip.Descendants().Where(e => e.Name.LocalName == "TextBlock").Select(e => (string)e.Attribute("Text")));
            // The type tile and the pill's light read the same summary.
            Assert.Equal(3, ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml")
                .Split(new[] { "{Binding DataContext.Heat, ElementName=SlotPowerButton}" }, StringSplitOptions.None).Length - 1);
            // The host the window binds starts with no data. Left to
            // inherit, it would take the page's view model, which has no
            // SelectedSlotSummary, and fail every binding until the window
            // hands over the dashboard.
            Assert.Equal("{x:Null}", (string)Named(page, "SlotPowerHost").Attribute("DataContext"));
            Assert.DoesNotContain("SelectedSlotSummary", ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "PadViewModel.cs"));
            var word = Named(page, "SlotStateWord");
            Assert.Contains(power, word.Ancestors());
            Assert.Equal("True", word.Attributes().Single(a => a.Name.LocalName == "LiveText.Polite").Value);

            // Driven by, a pill for each device, Add Device. The pills are a
            // list whose selected item is the device the page shows, where a
            // closed picker stood. Each pill is a list item named for its
            // device, and the list is labeled by the words before it.
            var drivenBy = Named(page, "DrivenByLabel");
            Assert.Equal("{Binding Pad_DrivenBy, Source={x:Static strings:Strings.Instance}}", (string)drivenBy.Attribute("Text"));
            var pills = Named(page, "DrivenByList");
            Assert.Equal("ListBox", pills.Name.LocalName);
            Assert.Equal("Pad_DrivenByList", Id(pills));
            Assert.Equal("{Binding MappedDevices}", (string)pills.Attribute("ItemsSource"));
            Assert.Equal("{Binding SelectedMappedDevice, Mode=TwoWay}", (string)pills.Attribute("SelectedItem"));
            Assert.Equal("{Binding ElementName=DrivenByLabel}", (string)pills.Attribute("AutomationProperties.LabeledBy"));
            Assert.Same(drivenBy.Parent, pills.Parent);
            Assert.Equal("{Binding Name, TargetNullValue='', FallbackValue=''}",
                (string)Assert.Single(pills.Descendants(), e => e.Name.LocalName == "Setter"
                    && (string)e.Attribute("Property") == "AutomationProperties.Name").Attribute("Value"));
            Assert.Same(pills, Assert.Single(page.Descendants(),
                e => (string)e.Attribute("SelectedItem") == "{Binding SelectedMappedDevice, Mode=TwoWay}"));
            // A pill as its board draws it (the world-class pass): a light
            // that is ember while the device sends input, its name, and for
            // a connected device a hairline, the mark of a Bluetooth link
            // and its battery, a meter of the level before the percentage
            // (the first board wrote the number alone, and the owner asked
            // for the picture back on 2026-11-13) with the word Low at or
            // under the level Settings names. A device that is not
            // connected says so.
            var pill = Assert.Single(pills.Descendants(), e => e.Name.LocalName == "DataTemplate");
            const string source = ", Source={x:Static strings:Strings.Instance}}";
            Assert.Equal(new[] { "{Binding Name}", "{Binding BatteryText}", "{Binding Pad_Battery_Low" + source, "{Binding Home_Device_NotConnected" + source },
                pill.Descendants().Select(e => (string)e.Attribute("Text")).Where(t => t != null));
            var lamp = Assert.Single(pill.Descendants(), e => e.Name.LocalName == "Halo");
            Assert.Equal("{Binding IsSending}", (string)lamp.Attribute("IsLit"));
            Assert.Equal("Ellipse", Assert.Single(lamp.Elements()).Name.LocalName);
            var meter = Assert.Single(pill.Descendants(), e => e.Name.LocalName == "BatteryMeter");
            Assert.Equal("{Binding BatteryPercent}", (string)meter.Attribute("Percent"));
            Assert.Equal("{Binding BatteryCharging}", (string)meter.Attribute("Charging"));
            Assert.Equal("{Binding BatteryLow}", (string)meter.Attribute("Low"));
            var percent = pill.Descendants().Single(e => (string)e.Attribute("Text") == "{Binding BatteryText}");
            Assert.Contains(meter, percent.ElementsBeforeSelf());
            Assert.Equal("{Binding HasBattery, Converter={StaticResource BoolToVisibilityConverter}}", (string)percent.Attribute("Visibility"));
            Assert.Equal("{Binding BatterySpoken, Mode=OneWay, TargetNullValue='', FallbackValue=''}", (string)percent.Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding BatteryLow, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)pill.Descendants().Single(e => (string)e.Attribute("Text") == "{Binding Pad_Battery_Low" + source).Attribute("Visibility"));
            var link = Assert.Single(pill.Descendants(), e => e.Name.LocalName == "DecorativeStrokeIcon");
            Assert.Equal("{StaticResource BluetoothLineGeometry}", (string)link.Attribute("Data"));
            Assert.Equal("{Binding IsBluetooth, Converter={StaticResource BoolToVisibilityConverter}}", (string)link.Attribute("Visibility"));
            Assert.Equal("{Binding Pad_Transport_Bluetooth" + source, (string)link.Attribute("ToolTip"));
            // The link is a mark, so a screen reader is told it in words,
            // and the hairline shows only before a link or a battery.
            Assert.Contains("<DataTrigger Binding=\"{Binding IsBluetooth}\" Value=\"True\">\r\n                                                <Setter Property=\"AutomationProperties.HelpText\" Value=\"{Binding Pad_Transport_Bluetooth" + source + "\"/>", text);
            Assert.Equal("{Binding HasLinkOrBattery, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)Assert.Single(pill.Descendants(), e => e.Name.LocalName == "Rectangle").Attribute("Visibility"));
            Assert.DoesNotContain(pill.Descendants(), e => e.Name.LocalName == "GlyphText");
            // The selected device is filled and in the text's own color,
            // one that is not connected stands in a dashed outline, and a
            // low battery has a gold edge.
            var container = Assert.Single(pills.Descendants(), e => e.Name.LocalName == "ControlTemplate" && (string)e.Attribute("TargetType") == "ListBoxItem");
            Assert.Single(container.Descendants(), e => Name(e) == "Dash" && (string)e.Attribute("StrokeDashArray") == "4 3");
            Assert.Equal(new[] { "IsSelected", "IsMouseOver" },
                container.Descendants().Where(e => e.Name.LocalName == "Trigger").Select(e => (string)e.Attribute("Property")));
            Assert.Equal(new[] { "{Binding BatteryLow}", "{Binding IsOnline}", "{Binding IsBluetooth}" },
                container.Descendants().Where(e => e.Name.LocalName == "DataTrigger").Select(e => (string)e.Attribute("Binding")));
            // A device with a link or a battery says so, for the hairline.
            var device = new PadViewModel.MappedDeviceInfo();
            Assert.False(device.HasLinkOrBattery);
            var raised = new List<string>();
            device.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            device.TransportGlyph = "\uE702";
            Assert.True(device.HasLinkOrBattery);
            Assert.Contains(nameof(PadViewModel.MappedDeviceInfo.HasLinkOrBattery), raised);
            device.TransportGlyph = string.Empty;
            raised.Clear();
            device.SetBattery(50, false, 15);
            Assert.True(device.HasLinkOrBattery);
            Assert.Contains(nameof(PadViewModel.MappedDeviceInfo.HasLinkOrBattery), raised);
            var add = Named(page, "AddDeviceButton");
            Assert.Equal("Pad_AddDevice", Id(add));
            Assert.Equal("AddDevice_Click", (string)add.Attribute("Click"));
            Assert.Equal("{Binding Pad_AddDevice, Source={x:Static strings:Strings.Instance}}", (string)add.Attribute("Content"));
            Assert.Same(drivenBy.Parent, add.Parent);

            // The stage icons are the Home card's. The page has none, and
            // the one template that draws an icon is still the shared one.
            Assert.DoesNotContain("StageChips", text);
            Assert.DoesNotContain("StageLedger", text);
            string home = ExtrasPageTests.RepoText("PadForge.App", "Views", "HomePage.xaml");
            Assert.Contains("ItemsSource=\"{Binding StageLedger}\"", home);
            Assert.Contains("ItemTemplate=\"{StaticResource StageChipTemplate}\"", home);
            string shared = ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(shared, "<DataTemplate x:Key=\"StageChipTemplate\""));
            Assert.DoesNotContain("x:Key=\"StageChipTemplate\"", text);

            // The offer band is in the header still, between the device
            // line and the sections.
            int offer = text.IndexOf("AutomationProperties.AutomationId=\"AssignOfferBanner\"", StringComparison.Ordinal);
            Assert.True(text.IndexOf("x:Name=\"AddDeviceButton\"", StringComparison.Ordinal) < offer);
            Assert.True(offer < text.IndexOf("x:Name=\"SectionStrip\"", StringComparison.Ordinal));

            // The window hands the page the slots' summaries and takes its
            // requests.
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("PadPageView.BindSlotState(_viewModel.Dashboard);", window);
            Assert.Contains("PadPageView.SlotEnabledToggled += (s, args) =>", window);
            Assert.Contains("PadPageView.AddDeviceRequested += (s, e) => SelectNavItemByTag(\"Devices\");", window);
            string code = CodeBehind();
            Assert.Contains("SlotPowerHost.DataContext = dashboard;", code);
            Assert.DoesNotContain("StageChips", code);
            Assert.Contains("(vm.PadIndex, !PadForge.Common.Input.SettingsManager.SlotEnabled[vm.PadIndex]));", code);
        }

        /// <summary>The Rumble board's words and units (#574): the motor
        /// meters are Left Motor and Right Motor, the names their bars read
        /// to a screen reader (the sections pass), an adaptive trigger's
        /// range and strength read in percent with their unit while their
        /// settings keep 0 to 255, and the Falcon card's paragraph is its
        /// title's tooltip, as Mouse Gestures' is.</summary>
        [Fact]
        public void TheRumbleCardsReadAsTheBoardDoes()
        {
            var page = Page();
            const string s = ", Source={x:Static strings:Strings.Instance}}";
            var activity = Named(page, "TabForceFeedback").Descendants().Where(e => e.Name.LocalName == "TextBlock" && (string)e.Attribute("Grid.Column") == "0"
                && ((string)e.Attribute("Text") ?? "").Contains("MotorLabel", StringComparison.Ordinal)).ToList();
            Assert.Equal(new[] { "{Binding Pad_LeftMotorLabel" + s, "{Binding Pad_RightMotorLabel" + s }, activity.Select(e => (string)e.Attribute("Text")));
            Assert.Equal("Left Motor", PadForge.Resources.Strings.Strings.Instance.Pad_LeftMotorLabel);
            Assert.Equal("Right Motor", PadForge.Resources.Strings.Strings.Instance.Pad_RightMotorLabel);
            Assert.DoesNotContain("Pad_MotorActivity_Left", ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs"));

            var triggers = Named(page, "TabAdaptiveTriggers");
            foreach (string side in new[] { "Left", "Right" })
            {
                var range = Assert.Single(triggers.Descendants(), e => e.Name.LocalName == "RangeSlider"
                    && (string)e.Attribute("LowerValue") == "{Binding DeviceConfig." + side + "StartPosition, Mode=TwoWay, Converter={StaticResource BytePercentConverter}}");
                Assert.Equal("{Binding DeviceConfig." + side + "EndPosition, Mode=TwoWay, Converter={StaticResource BytePercentConverter}}", (string)range.Attribute("UpperValue"));
                Assert.Equal("100", (string)range.Attribute("Maximum"));
                var row = range.Parent.Elements().ToList();
                Assert.Equal("%", (string)row[row.IndexOf(range) + 4].Attribute("Text"));
                var strength = Assert.Single(triggers.Descendants(), e => e.Name.LocalName == "Slider"
                    && (string)e.Attribute("Value") == "{Binding DeviceConfig." + side + "Strength, Mode=TwoWay, Converter={StaticResource BytePercentConverter}}");
                Assert.Equal("100", (string)strength.Attribute("Maximum"));
                Assert.Equal("{Binding DeviceConfig." + side + "Strength, Mode=TwoWay, UpdateSourceTrigger=LostFocus, Converter={StaticResource BytePercentConverter}}",
                    (string)strength.ElementsAfterSelf().First().Attribute("Text"));
                Assert.Equal("%", (string)strength.ElementsAfterSelf().ElementAt(1).Attribute("Text"));
            }
            Assert.Contains("<conv:BytePercentConverter x:Key=\"BytePercentConverter\" />", ExtrasPageTests.RepoText("PadForge.App", "App.xaml"));

            var falcon = Named(page, "FalconCard");
            var title = Assert.Single(falcon.Descendants(), e => (string)e.Attribute("Text") == "{Binding Pad_Falcon_Header" + s);
            Assert.Equal("{Binding Pad_Falcon_Description" + s, (string)title.Attribute("ToolTip"));
            Assert.Single(page.Descendants(), e => ((string)e.Attribute("Text") ?? "").Contains("Pad_Falcon_Description", StringComparison.Ordinal)
                || ((string)e.Attribute("ToolTip") ?? "").Contains("Pad_Falcon_Description", StringComparison.Ordinal));
            var gestures = Named(page, "TabMouse");
            Assert.Equal("GroupBox", gestures.Name.LocalName);
            var gesturesTitle = Assert.Single(gestures.Descendants(), e => (string)e.Attribute("Text") == "{Binding Pad_MouseGestures_Header" + s);
            Assert.Equal("{Binding Pad_MouseGestures_Desc" + s, (string)gesturesTitle.Attribute("ToolTip"));
            var buttons = Assert.Single(gestures.Descendants(), e => (string)e.Attribute("Text") == "{Binding Pad_MouseGestures_Button" + s).Parent;
            Assert.Equal("SettingRow", buttons.Name.LocalName);
            Assert.Equal("{StaticResource DzLabel}", (string)buttons.Elements().First().Attribute("Style"));
            Assert.Contains(buttons.Elements(), e => e.Name.LocalName == "WrapPanel"
                && e.Attributes().Any(a => a.Name.LocalName == "SettingRow.Fills" && a.Value == "True"));
        }

        /// <summary>The pickers of Motion & Touch and of Lights & Sound are
        /// as wide as their choices, as the boards draw a select (#574): a
        /// least width of 120, at the left of the room their row gives
        /// them. Each keeps its cap and still fills, so a narrow row narrows
        /// it before its reset. The gyro's readings of degrees per turn are
        /// rows under Sensitivity's More, and Sound Macros is a line that
        /// folds, the first of the list Sound Packages is in.</summary>
        [Fact]
        public void MotionAndLightsAreAsTheirBoardsDrawThem()
        {
            var page = Page();
            string text = PageText();
            int motion = text.IndexOf("<ScrollViewer x:Name=\"MotionSectionScroll\"", StringComparison.Ordinal);
            int macros = text.IndexOf("SECTION 5: Macros & Menus", StringComparison.Ordinal);
            var pickers = System.Text.RegularExpressions.Regex.Matches(text.Substring(motion, macros - motion),
                    "<(?:ComboBox|ui:NumberBox)\\b(?:[^<>\"]|\"[^\"]*\")*>")
                .Select(m => m.Value).Where(t => t.Contains("controls:SettingRow.Fills=\"True\"", StringComparison.Ordinal)).ToList();
            Assert.Equal(30, pickers.Count);
            Assert.All(pickers, t => Assert.Contains("MinWidth=\"120\" HorizontalAlignment=\"Left\" MaxWidth=", t));

            var more = Named(page, "GyroSensitivityMore");
            var turns = more.Descendants().Where(e => e.Name.LocalName == "Binding" && ((string)e.Attribute("Path") ?? "").EndsWith("_DegPerTurn", StringComparison.Ordinal)).ToList();
            Assert.Equal(new[] { "GyroSensitivityH_DegPerTurn", "GyroSensitivityV_DegPerTurn" }, turns.Select(b => (string)b.Attribute("Path")));
            Assert.DoesNotContain("°/turn", text);
            Assert.Equal("\u2248 {0}\u00b0 per turn", PadForge.Resources.Strings.Strings.Instance.Pad_Gyro_DegPerTurn_Format);
            var vm = new PadViewModel(0) { GyroSensitivityH = 3 };
            Assert.Equal(120, vm.GyroSensitivityH_DegPerTurn);
            vm.GyroSensitivityH = 0.7;
            Assert.Equal(514, vm.GyroSensitivityH_DegPerTurn);

            var macrosLine = Named(page, "SoundMacrosFoldCard");
            Assert.Equal("Pad.Sound.Macros", (string)macrosLine.Attribute("StateKey"));
            Assert.Equal("{Binding SoundMacros.Count}", (string)macrosLine.Attribute("Summary"));
            Assert.Same(Named(page, "SoundPackagesFoldCard").Parent, macrosLine.Parent);
            Assert.Same(macrosLine, Named(page, "SoundPackagesFoldCard").ElementsBeforeSelf().Last());
            Assert.Contains(macrosLine.Descendants(), e => (string)e.Attribute("Command") == "{Binding AddSoundMacroCommand}");
            Assert.Contains(macrosLine.Descendants(), e => (string)e.Attribute("ItemsSource") == "{Binding SoundMacros}");
            Assert.DoesNotContain("Sound Macros hub card", text);
        }

        /// <summary>A VR slot's header says SteamVR's state after the
        /// slot's name, in the words the Settings row has for it. The state
        /// is the settings view model's, which the window hands the page as
        /// it hands the power button the slots' summaries. No other kind of
        /// slot shows it.</summary>
        [Fact]
        public void AVrSlotsHeaderSaysSteamVrsState()
        {
            var page = Page();
            var state = Named(page, "SteamVrState");
            // After the title, on the line another slot's Preset is on.
            Assert.Same(Named(page, "HMaestroProfileBar").Parent, state.Parent);
            Assert.Same(Named(page, "HMaestroProfileBar"), state.ElementsBeforeSelf().Last());
            Assert.Equal("{Binding ShowSteamVrState, Converter={StaticResource BoolToVisibilityConverter}}", (string)state.Attribute("Visibility"));
            Assert.Equal((string)Named(page, "HMaestroProfileBar").Attribute("Margin"), (string)state.Attribute("Margin"));

            // The host the window binds starts with no data, as the power
            // button's does.
            var host = Assert.Single(state.Elements());
            Assert.Equal("SteamVrStateHost", Name(host));
            Assert.Equal("{x:Null}", (string)host.Attribute("DataContext"));
            // One line in the VR board's words, "SteamVR installed".
            var words = Assert.Single(host.Elements());
            Assert.Equal("{Binding SteamVrSentence}", (string)words.Attribute("Text"));
            Assert.Equal("13", (string)words.Attribute("FontSize"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)words.Attribute("Foreground"));
            // A screen reader hears the state change, as on the Settings row.
            Assert.Equal("Pad_SteamVrState", Id(words));
            Assert.Equal("True", words.Attributes().Single(a => a.Name.LocalName == "LiveText.Polite").Value);
            var strings = PadForge.Resources.Strings.Strings.Instance;
            var settings = new SettingsViewModel();
            Assert.Equal(strings.Pad_SteamVr_NotInstalled, settings.SteamVrSentence);
            Assert.Equal(strings.Common_NotInstalled, settings.SteamVrStatusText);
            var told = new List<string>();
            settings.PropertyChanged += (o, e) => told.Add(e.PropertyName);
            settings.IsSteamVrInstalled = true;
            Assert.Contains(nameof(SettingsViewModel.SteamVrSentence), told);
            Assert.StartsWith("SteamVR ", settings.SteamVrSentence);
            Assert.Equal("SteamVR installed", strings.Pad_SteamVr_Installed);
            Assert.Equal("SteamVR not installed", strings.Pad_SteamVr_NotInstalled);
            Assert.Equal("SteamVR running", strings.Pad_SteamVr_Running);
            Assert.Equal("SteamVR running, driver connected", strings.Pad_SteamVr_DriverConnected);
            Assert.Equal("SteamVR running, controllers live", strings.Pad_SteamVr_ControllersLive);
            told.Clear();
            settings.RefreshSteamVrStatus();
            Assert.Contains(nameof(SettingsViewModel.SteamVrSentence), told);

            // Only a VR slot shows it, and a change of type says so.
            var vm = new PadViewModel(0);
            Assert.False(vm.ShowSteamVrState);
            var raised = new List<string>();
            vm.PropertyChanged += (o, e) => raised.Add(e.PropertyName);
            vm.OutputType = PadForge.Engine.VirtualControllerType.Vr;
            Assert.True(vm.ShowSteamVrState);
            Assert.Contains(nameof(PadViewModel.ShowSteamVrState), raised);
            vm.OutputType = PadForge.Engine.VirtualControllerType.Xbox;
            Assert.False(vm.ShowSteamVrState);

            // The window hands the page the settings, whose state it keeps
            // current.
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("PadPageView.BindSteamVrState(_viewModel.Settings);", window);
            Assert.Contains("_viewModel.Settings.RefreshSteamVrStatus();", window);
            Assert.Contains("SteamVrStateHost.DataContext = settings;", CodeBehind());
        }

        /// <summary>The header's More button, at the right of its first
        /// line, holds what acts on the whole controller: Copy, Paste, Copy
        /// From, then Clear All and Delete under a line. The first four
        /// keep the commands, the handler and the tooltips their buttons
        /// had in the list's top row. Delete goes the way the X on the
        /// controller's Home card goes, so the window leaves the page of a
        /// controller it deletes.</summary>
        [Fact]
        public void TheHeadersMoreMenu_HoldsWhatActsOnTheWholeController()
        {
            const string s = ", Source={x:Static strings:Strings.Instance}}";
            var page = Page();
            var more = Named(page, "PadMoreButton");
            Assert.Equal("Pad_More", Id(more));
            // It reads the word More and then the controller it acts on,
            // since a list on the page has a More of its own
            // (ScreenReaderPassTests.AListUnderAHeading_ReadsTheHeading).
            Assert.Null(more.Attribute("AutomationProperties.Name"));
            var spoken = more.Elements().Single(e => e.Name.LocalName == "AutomationProperties.Name").Elements().Single();
            Assert.Equal("{}{0}, {1} #{2}", (string)spoken.Attribute("StringFormat"));
            Assert.Equal("{Binding Common_More" + s, (string)more.Attribute("ToolTip"));
            Assert.Equal("MoreMenu_Click", (string)more.Attribute("Click"));
            // It shows in every section, so it has no visibility of its own.
            Assert.Null(more.Attribute("Visibility"));
            Assert.Same(Named(page, "SlotPowerHost").Parent, more.Parent);

            var menu = Assert.Single(more.Descendants(), e => e.Name.LocalName == "ContextMenu");
            Assert.Equal(new[] { "MenuItem", "MenuItem", "MenuItem", "Separator", "MenuItem", "MenuItem" },
                menu.Elements().Select(e => e.Name.LocalName));
            var items = menu.Elements().Where(e => e.Name.LocalName == "MenuItem").ToList();
            Assert.Equal(new[] { "Common_Copy", "Common_Paste", "Pad_CopyFrom", "Pad_ClearAll", "Main_DeleteVC" }.Select(k => "{Binding " + k + s),
                items.Select(i => (string)i.Attribute("Header")));
            Assert.Equal(new[] { "{Binding CopySettingsCommand}", "{Binding PasteSettingsCommand}", "{Binding CopyFromCommand}", null, null },
                items.Select(i => (string)i.Attribute("Command")));
            Assert.Equal(new[] { null, null, null, "ClearAllMappings_Click", "DeleteController_Click" },
                items.Select(i => (string)i.Attribute("Click")));
            Assert.Equal(new[] { "Pad_CopyTooltip", "Pad_PasteTooltip", "Pad_CopyFromTooltip", "Pad_ClearMappings" }.Select(k => "{Binding " + k + s),
                items.Take(4).Select(i => (string)i.Attribute("ToolTip")));
            Assert.Equal("Pad_DeleteController", Id(items[4]));
            // The two that destroy read in the danger ink.
            Assert.Equal(new[] { null, null, null, "{DynamicResource DangerInkBrush}", "{DynamicResource DangerInkBrush}" },
                items.Select(i => (string)i.Attribute("Foreground")));

            // The menu is outside the page's tree, so the handler hands it
            // the page's view model before it opens.
            string code = CodeBehind();
            Assert.Matches(@"button\.ContextMenu\.DataContext = DataContext;\s*button\.ContextMenu\.PlacementTarget = button;", code);

            // Delete raises the page's request with the controller's index,
            // and the window answers it, the Home card's X and the sidebar
            // entry's X with the one deferred delete, which goes Home first
            // when the controller's page is the one showing.
            Assert.Matches(@"private void DeleteController_Click\(object sender, RoutedEventArgs e\)\s*\{\s*if \(DataContext is PadViewModel vm\)\s*DeleteRequested\?\.Invoke\(this, vm\.PadIndex\);", code);
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("PadPageView.DeleteRequested += (s, slotIndex) => DeleteSlotDeferred(slotIndex);", window);
            Assert.Contains("HomePageView.DeleteSlotRequested += (s, slotIndex) => DeleteSlotDeferred(slotIndex);", window);
            Assert.Matches(@"private void OnSidebarDeleteSlot\(object sender, RoutedEventArgs e\)\s*\{[^}]*DeleteSlotDeferred\(slotIndex\);", window);
            Assert.Matches(@"private void DeleteSlotDeferred\(int slotIndex\)\s*\{\s*Dispatcher\.BeginInvoke\(new Action\(\(\) =>\s*\{[^}]*if \(_viewModel\.SelectedPadIndex == slotIndex\)\s*SelectNavItemByTag\(""Home""\);", window);
        }

        /// <summary>Sticks and Triggers as its board draws it. The section
        /// has row styles of its own that shadow the page's: a label column
        /// 150 wide, a slider with no width to stop at, and a value typed in
        /// a field box, the percent 46 wide and the raw value 58. Every row
        /// of the section grows its slider. A stick's card is two columns,
        /// its settings and the live stick view, and its More row follows
        /// them and so runs the card's width. Open, More holds two columns
        /// of groups and then a curve for each axis. The stick view has no
        /// small graph for an axis: each axis has its chart under More. A
        /// stick's and a trigger's head carry the tag This device, and the
        /// features both sticks share are the lines of the Both Sticks
        /// card.</summary>
        [Fact]
        public void SticksAndTriggers_HasRowsOfItsOwn_AndAStickCardsMoreRunsTheCard()
        {
            var page = Page();
            var scroll = Named(page, "SticksSectionScroll");
            var own = Assert.Single(scroll.Elements(), e => e.Name.LocalName == "ScrollViewer.Resources");
            XElement Style(string key) => Assert.Single(own.Elements(), e => (string)e.Attribute(X + "Key") == key);
            string Setter(XElement style, string property) => (string)style.Elements()
                .SingleOrDefault(e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == property)?.Attribute("Value");

            Assert.Equal("150", Setter(Style("DzLabel"), "Width"));
            var slider = Style("DzSlider");
            Assert.Equal("{StaticResource EmberSlider}", (string)slider.Attribute("BasedOn"));
            Assert.Null(Setter(slider, "Width"));
            Assert.Null(Setter(slider, "MaxWidth"));
            Assert.Equal("{StaticResource DzSlider}", (string)Style("OffsetSlider").Attribute("BasedOn"));
            // A value is typed in the page's field box, which the four stacked
            // sections share: 46 wide at the least, so a longer value widens
            // it and is never cut off. A raw value has a box 58 wide.
            var box = Style("DzValueEdit");
            Assert.Equal("{StaticResource FieldValueEdit}", (string)box.Attribute("BasedOn"));
            Assert.Empty(box.Elements());
            var field = Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Key") == "FieldValueEdit");
            Assert.Equal("46", Setter(field, "MinWidth"));
            Assert.Null(Setter(field, "Width"));
            Assert.Equal("{DynamicResource SteelFieldBrush}", Setter(field, "Background"));
            Assert.Equal("Right", Setter(field, "TextAlignment"));
            Assert.Equal("{StaticResource FieldUnit}", (string)Style("DzPercent").Attribute("BasedOn"));
            var rawBox = Style("DzDigitEdit");
            Assert.Equal("{StaticResource DzValueEdit}", (string)rawBox.Attribute("BasedOn"));
            Assert.Equal("58", Setter(rawBox, "Width"));
            // A text box lays its Padding around its text itself. A content
            // host that took the same Padding as a margin cut a third digit
            // off a boxed value, here and in the page's own value style.
            Assert.DoesNotContain("x:Name=\"PART_ContentHost\" Margin=\"{TemplateBinding Padding}\"", PageText());

            // Every row of the section grows its slider.
            var rows = scroll.Descendants().Where(e => e.Name.LocalName == "SettingRow").ToList();
            Assert.True(rows.Count > 40, "the section has " + rows.Count + " rows");
            Assert.All(rows, r => Assert.Equal("True", (string)r.Attribute("Grow")));
            // A range's two-thumb slider is no Slider, so it is marked to
            // fill its row.
            var ranges = scroll.Descendants().Where(e => e.Name.LocalName == "RangeSlider").ToList();
            Assert.Contains(ranges, r => r.Attributes().Any(a => a.Name.LocalName == "SettingRow.Fills" && a.Value == "True") && r.Attribute("Width") == null);

            // The stick card, as the Sticks board of the world-class pass
            // draws it: a title row, its three parts (the settings, the
            // curve, the live view) on one line while the card is wide
            // (ThreePartPanelTests), then More.
            var more = Assert.Single(page.Descendants(), e => Id(e) == "Pad_More_Stick");
            var card = more.Parent!;
            Assert.Equal("StackPanel", card.Name.LocalName);
            Assert.Equal("{StaticResource CardBorder}", (string)card.Parent!.Attribute("Style"));
            Assert.Equal(new[] { "DockPanel", "ThreePartPanel", "MoreRow" }, card.Elements().Select(e => e.Name.LocalName));
            var line = card.Elements().ElementAt(1);
            Assert.Equal("22", (string)line.Attribute("Gap"));
            Assert.Equal(new[] { "StackPanel", "StackPanel", "Border" }, line.Elements().Select(e => e.Name.LocalName));
            var left = line.Elements().ElementAt(0);
            var curve = line.Elements().ElementAt(1);
            var view = line.Elements().ElementAt(2);
            string Flex(XElement e, string name) => (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "FlexLinePanel." + name);
            // The settings: a column that grows by 60 and no more, so a
            // reset stands beside its control.
            Assert.Equal("{DynamicResource FirstRowSettingsBasis}", Flex(left, "Basis"));
            Assert.Equal("{DynamicResource FirstRowSettingsMost}", (string)left.Attribute("MaxWidth"));
            Assert.Equal(new[] { "SettingRow", "SettingRow", "SettingRow", "SettingRow" }, left.Elements().Select(e => e.Name.LocalName));
            Assert.All(left.Elements(), r => Assert.Equal("{StaticResource DzFirstLabel}", (string)r.Elements().First().Attribute("Style")));
            Assert.Contains(left.Descendants(), e => Id(e) == "StickDeadZoneBothSlider");
            Assert.Contains(left.Descendants(), e => Id(e) == "SensitivityBothCombo");
            Assert.Contains(left.Descendants(), e => (string)e.Attribute("Click") == "CalibrateCenter_Click");
            // The curve takes the room that is left, and the live view
            // keeps its own width at the line's end.
            Assert.Equal("330", Flex(curve, "Basis"));
            Assert.Equal("720", (string)curve.Attribute("MaxWidth"));
            Assert.Contains(curve.Descendants(), e => Id(e) == "SensitivityBothChart");
            Assert.Null(Flex(view, "Basis"));
            Assert.DoesNotContain(left.Descendants().Concat(view.Descendants()), e => e.Name.LocalName == "CurveEditor");
            Assert.DoesNotContain("ChartSize=\"96\"", PageText());
            // The label column is the language's: the three numbers are
            // English's, and the page sets them when it is shown.
            XElement Number(string key) => Assert.Single(own.Elements(), e => (string)e.Attribute(X + "Key") == key);
            Assert.Equal("96", Number("FirstRowLabelWidth").Value);
            Assert.Equal("300", Number("FirstRowSettingsBasis").Value);
            Assert.Equal("360", Number("FirstRowSettingsMost").Value);
            Assert.Equal("{DynamicResource FirstRowLabelWidth}", Setter(Style("DzFirstLabel"), "Width"));
            Assert.Equal("{StaticResource DzLabel}", (string)Style("DzFirstLabel").Attribute("BasedOn"));
            // The head: the mark at 28, the name in the display face, the tag
            // This device, Reset All, and the numbers in a text of their own.
            // A trigger's head is drawn the same way.
            var head = card.Elements().First();
            Assert.Equal(new[] { "LabeledShapeIcon", "TextBlock", "Border", "Button", "TextBlock" }, head.Elements().Select(e => e.Name.LocalName));
            Assert.Equal("28", (string)head.Elements().First().Attribute("Width"));
            Assert.Equal("{StaticResource HeroCardTitle}", (string)head.Elements().ElementAt(1).Attribute("Style"));
            Assert.Equal("{Binding Title}", (string)head.Elements().ElementAt(1).Attribute("Text"));
            void IsDeviceTag(XElement tag)
            {
                Assert.Equal("{StaticResource ChipSteel}", (string)tag.Attribute("Style"));
                Assert.Equal("10,0,0,0", (string)tag.Attribute("Margin"));
                Assert.Equal("{Binding Pad_Scope_Device, Source={x:Static strings:Strings.Instance}}",
                    (string)Assert.Single(tag.Elements()).Attribute("Text"));
            }
            IsDeviceTag(head.Elements().ElementAt(2));

            // Both Sticks (the sections pass): the features both sticks
            // share are lines in the tray of a card named for both, after the
            // sticks' cards and before the triggers', never a list standing
            // alone between cards.
            var both = Named(page, "BothSticksCard");
            Assert.Equal("{StaticResource CardBorder}", (string)both.Attribute("Style"));
            Assert.Same(both, Assert.Single(Named(page, "TabSticks").Elements()).Elements().Last());
            var bothBody = Assert.Single(both.Elements());
            Assert.Equal(new[] { "DockPanel", "FoldList" }, bothBody.Elements().Select(e => e.Name.LocalName));
            var bothHead = bothBody.Elements().First();
            Assert.Equal(new[] { "StackPanel", "TextBlock", "Border", "Button" }, bothHead.Elements().Select(e => e.Name.LocalName));
            // Its mark is the two sticks', L and R side by side.
            var marks = bothHead.Elements().First().Elements().ToList();
            Assert.Equal(new[] { "L", "R" }, marks.Select(m => (string)m.Attribute("Label")));
            Assert.All(marks, m => Assert.Equal("Stick", (string)m.Attribute("Shape")));
            Assert.Equal("{Binding Pad_BothSticks_Header, Source={x:Static strings:Strings.Instance}}", (string)bothHead.Elements().ElementAt(1).Attribute("Text"));
            Assert.Equal("{StaticResource HeroCardTitle}", (string)bothHead.Elements().ElementAt(1).Attribute("Style"));
            IsDeviceTag(bothHead.Elements().ElementAt(2));
            var bothReset = bothHead.Elements().Last();
            Assert.Equal("{Binding ResetBothSticksCommand}", (string)bothReset.Attribute("Command"));
            Assert.Contains("Pad_BothSticks_Header", bothReset.ToString());
            Assert.Contains("Pad_ResetSection_Format", bothReset.ToString());
            Assert.Equal(new[] { "LockFeedbackCard", "FlickStickCard" }, bothBody.Elements().Last().Elements().Select(Name));

            // Under More: two columns of groups, then the two axes' curves.
            var body = Assert.Single(more.Elements());
            var grids = body.Elements().Where(e => e.Name.LocalName == "TileGridPanel").ToList();
            Assert.Equal(2, grids.Count);
            Assert.All(grids, g => Assert.Equal("2", (string)g.Attribute("MaxColumns")));
            string[] Keys(XElement e) => e.Descendants().Where(d => ((string)d.Attribute("Style") ?? "").Contains("CardSubsectionTitle"))
                .Select(d => ((string)d.Attribute("Text")).Split(' ', ',')[1]).ToArray();
            var groups = grids[0].Elements().ToList();
            Assert.Equal(2, groups.Count);
            Assert.Equal(new[] { "Pad_Sticks_Section_CenterPoint", "Pad_Sticks_Section_DeadZone" }, Keys(groups[0]));
            Assert.Equal("Pad_Sticks_Section_Range", Keys(groups[1])[0]);
            Assert.Contains("Pad_Steering_Header", groups[1].ToString());
            var axes = grids[1].Elements().ToList();
            Assert.Equal(2, axes.Count);
            Assert.Contains("PresetNameX", axes[0].ToString());
            Assert.Contains("SensitivityCurveX", axes[0].ToString());
            Assert.Contains("PresetNameY", axes[1].ToString());
            Assert.Contains("SensitivityCurveY", axes[1].ToString());
            // No rule between the groups: each has its label.
            Assert.DoesNotContain(body.Descendants().Take(400), e => e.Name.LocalName == "Separator" && e.Parent == body);

            // A trigger's card: its three settings beside the chart with its
            // hint and the live meter under it, then More, where the range
            // is typed and the raw values stand.
            var triggers = Named(page, "TabTriggers");
            var triggerMore = Assert.Single(triggers.Descendants(), e => e.Name.LocalName == "MoreRow");
            Assert.Equal("Pad_More_Trigger", Id(triggerMore));
            var triggerCard = triggerMore.Parent!;
            Assert.Equal(new[] { "DockPanel", "FlexLinePanel", "MoreRow" }, triggerCard.Elements().Select(e => e.Name.LocalName));
            var triggerHead = triggerCard.Elements().First();
            Assert.Equal(new[] { "LabeledShapeIcon", "TextBlock", "Border", "Button", "TextBlock" }, triggerHead.Elements().Select(e => e.Name.LocalName));
            IsDeviceTag(triggerHead.Elements().ElementAt(2));
            var triggerLine = triggerCard.Elements().ElementAt(1);
            Assert.Equal(new[] { "StackPanel", "StackPanel" }, triggerLine.Elements().Select(e => e.Name.LocalName));
            var triggerRows = triggerLine.Elements().First();
            Assert.Equal("{DynamicResource FirstRowSettingsBasis}", Flex(triggerRows, "Basis"));
            Assert.Equal(new[] { "SettingRow", "SettingRow", "SettingRow" }, triggerRows.Elements().Select(e => e.Name.LocalName));
            // The range's row reads its two ends beside the slider and has
            // no box: the boxes are under More.
            var rangeRow = triggerRows.Elements().First();
            Assert.Equal(new[] { "TextBlock", "RangeSlider", "TextBlock", "Button" }, rangeRow.Elements().Select(e => e.Name.LocalName));
            var typed = triggerMore.Descendants().Where(e => e.Name.LocalName == "TextBox").Select(e => ((string)e.Attribute("Text")).Split(',')[0]).ToArray();
            Assert.Equal(new[] { "{Binding DeadZone", "{Binding MaxRange", "{Binding DeadZoneDigit", "{Binding MaxRangeDigit", "{Binding AntiDeadZoneDigit" }, typed);
            Assert.Single(triggerMore.Descendants(), e => e.Name.LocalName == "TextBox" && ((string)e.Attribute("Text")).Contains("AntiDeadZoneDigit"));
            Assert.DoesNotContain(triggerLine.Descendants(), e => e.Name.LocalName == "TextBox" && ((string)e.Attribute("Text")).Contains("Digit"));
            Assert.DoesNotContain(triggers.Descendants(), e => e.Name.LocalName == "Separator");
            var preset = Assert.Single(triggers.Descendants(), e => Id(e) == "TriggerPresetCombo");
            Assert.Equal("{Binding Macro_Sensitivity, Source={x:Static strings:Strings.Instance}}",
                (string)preset.Parent!.Elements().First().Attribute("Text"));
            var chart = Assert.Single(triggers.Descendants(), e => e.Name.LocalName == "CurveEditor");
            Assert.Equal("{Binding Pad_SensitivityCurveHintShort, Source={x:Static strings:Strings.Instance}}",
                (string)chart.ElementsAfterSelf().First().Attribute("Text"));
        }

        /// <summary>A part card, as the boards of the sections pass draw it
        /// (#574): a card whose head is the part's icon, its name in the
        /// display face, the tag of the scope its settings have, and Reset All
        /// at the head's end. The name's tooltip holds the part's paragraph
        /// where it has one. The icon is a glyph of the icon font, or a
        /// drawing named by its element. Gives back what the card stacks,
        /// the head first.</summary>
        private static List<XElement> PartCard(XElement card, string icon, string title, string paragraph, string scope, string reset)
        {
            Assert.Equal("{StaticResource CardBorder}", (string)card.Attribute("Style"));
            var stack = Assert.Single(card.Elements(), e => !e.Name.LocalName.Contains('.')).Elements().ToList();
            var head = stack[0];
            Assert.Equal("DockPanel", head.Name.LocalName);
            var parts = head.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
            Assert.Equal(new[] { icon.Length == 1 ? "GlyphText" : icon, "TextBlock", "Border", "Button" }, parts.Select(e => e.Name.LocalName));
            if (icon.Length == 1)
            {
                Assert.Equal(icon, (string)parts[0].Attribute("Text"));
                Assert.Equal("{StaticResource PartIconGlyph}", (string)parts[0].Attribute("Style"));
            }
            Assert.Equal(title, (string)parts[1].Attribute("Text"));
            Assert.Equal("{StaticResource HeroCardTitle}", (string)parts[1].Attribute("Style"));
            if (paragraph != null)
                Assert.Contains("{Binding " + paragraph + ", Source={x:Static strings:Strings.Instance}}", parts[1].ToString());
            Assert.Equal("{StaticResource ChipSteel}", (string)parts[2].Attribute("Style"));
            Assert.Equal("10,0,0,0", (string)parts[2].Attribute("Margin"));
            Assert.Equal("{Binding " + scope + ", Source={x:Static strings:Strings.Instance}}",
                (string)Assert.Single(parts[2].Elements()).Attribute("Text"));
            Assert.Equal("{StaticResource ResetAllButton}", (string)parts[3].Attribute("Style"));
            Assert.Equal("{Binding " + reset + "}", (string)parts[3].Attribute("Command"));
            return stack;
        }

        /// <summary>A part card's tray: the last thing the card stacks, in
        /// the page's own tray style, its feature lines in order, each with
        /// its icon. A feature that turns on and off has its switch on its
        /// line with the switch's own reset beside it, its icon lights while
        /// it is on, and no value is written beside the switch.</summary>
        private static void PartTray(List<XElement> stack, params (string Name, string Icon, bool Switch)[] lines)
        {
            var tray = stack.Last();
            Assert.Equal("FoldList", tray.Name.LocalName);
            Assert.Null(tray.Attribute("Style"));
            Assert.Null(tray.Attribute("Margin"));
            var folds = tray.Elements().ToList();
            Assert.Equal(lines.Select(l => l.Name), folds.Select(Name));
            foreach (var (fold, line) in folds.Zip(lines))
            {
                Assert.Equal(line.Icon, (string)fold.Attribute("IconGlyph") ?? (string)fold.Attribute("Icon"));
                var slot = fold.Elements().SingleOrDefault(e => e.Name.LocalName == "FoldCard.Switch");
                Assert.True(line.Switch == (slot != null), line.Name + (line.Switch ? " has no switch" : " has a switch"));
                if (slot == null) continue;
                Assert.True(fold.Attribute("IsOn") != null || fold.Elements().Any(e => e.Name.LocalName == "FoldCard.IsOn"), line.Name + " lights nothing");
                // The switch is the line's state: no value is written beside it.
                Assert.Null(fold.Attribute("Summary"));
                Assert.DoesNotContain(fold.Elements(), e => e.Name.LocalName == "FoldCard.Summary");
                var pair = Assert.Single(slot.Elements());
                Assert.Equal("{StaticResource SwitchCheckBox}", (string)Assert.Single(pair.Elements(), e => e.Name.LocalName == "CheckBox").Attribute("Style"));
                Assert.Single(pair.Elements(), e => e.Name.LocalName is "Button" or "SettingResetButton");
            }
        }

        /// <summary>The rows of a section that has row styles of its own: a
        /// row grows its slider and holds its value in a column 66 wide, so
        /// the sliders of a card end at one line, and opens on its label. A
        /// row whose switch stands at its end opens on a label that takes the
        /// row's room. No row sets itself in, no slider keeps a width of its
        /// own, and a value typed in place stands in the page's field box.
        /// The section's cards stand as far apart as the page's do.</summary>
        private static List<XElement> SectionRows(XElement scroll, XElement own, Func<XElement, bool> apart = null)
        {
            Assert.DoesNotContain(own.Elements(), e => (string)e.Attribute(X + "Key") == "CardBorder");
            Assert.Equal("{StaticResource FieldValueEdit}",
                (string)Assert.Single(own.Elements(), e => (string)e.Attribute(X + "Key") == "DzValueEdit").Attribute("BasedOn"));
            Assert.Equal("{StaticResource FieldUnit}",
                (string)Assert.Single(own.Elements(), e => (string)e.Attribute(X + "Key") == "DzPercent").Attribute("BasedOn"));
            var rows = scroll.Descendants().Where(e => e.Name.LocalName == "SettingRow" && (apart == null || !apart(e))).ToList();
            Assert.All(rows, r =>
            {
                Assert.Equal("True", (string)r.Attribute("Grow"));
                Assert.Equal("66", (string)r.Attribute("ValueWidth"));
                Assert.Null(r.Attribute("Margin"));
                var first = r.Elements().First(e => !e.Name.LocalName.Contains('.'));
                Assert.Equal("TextBlock", first.Name.LocalName);
                bool ends = r.Attributes().Any(a => a.Name.LocalName == "SettingRow.Ends" && a.Value == "True");
                Assert.Contains((string)first.Attribute("Style"), ends
                    ? new[] { "{StaticResource EndsLabel}" }
                    : new[] { "{StaticResource DzLabel}", "{StaticResource DzSubLabel}" });
            });
            Assert.All(rows.SelectMany(r => r.Elements()).Where(e => e.Name.LocalName == "Slider"),
                s => Assert.Null(s.Attribute("Width")));
            Assert.All(rows.Where(r => r.Elements().Any(e => e.Name.LocalName == "Slider"))
                .SelectMany(r => r.Elements()).Where(e => e.Name.LocalName == "TextBox"),
                b => Assert.Equal("{StaticResource DzValueEdit}", (string)b.Attribute("Style")));
            // Nothing in the section is the old large heading, a group's
            // label or a card's small title.
            Assert.DoesNotContain(scroll.Descendants(), e => ((string)e.Attribute("Style") ?? "").Contains("TitleTextBlockStyle")
                || (string)e.Attribute("Style") is "{StaticResource CardTitle}" or "{StaticResource SectionTitle}");
            return rows;
        }

        /// <summary>Motion and Touch as its board draws it (the sections pass
        /// of #574): one card for each part, Gyro, Touchpad and IR Pointer,
        /// two to a row. A card stacks its head, its body (the settings most
        /// people change beside what the part reports right now), its More
        /// row and a tray of the part's features as lines. Motion
        /// Passthrough, Raw or Tuned, is the Gyro card's first row. The
        /// section's label column is 170 wide, and a row that belongs to the
        /// row above has its label set in. A picker in a row gives way in a
        /// narrow column.</summary>
        [Fact]
        public void MotionAndTouch_HasRowsOfItsOwn_AndCardsAsItsBoardDrawsThem()
        {
            var page = Page();
            var scroll = Named(page, "MotionSectionScroll");
            var own = Assert.Single(scroll.Elements(), e => e.Name.LocalName == "ScrollViewer.Resources");
            XElement Style(string key) => Assert.Single(own.Elements(), e => (string)e.Attribute(X + "Key") == key);
            string Setter(XElement style, string property) => (string)style.Elements()
                .SingleOrDefault(e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == property)?.Attribute("Value");

            Assert.Equal("170", Setter(Style("DzLabel"), "Width"));
            var subLabel = Style("DzSubLabel");
            Assert.Equal("{StaticResource DzLabel}", (string)subLabel.Attribute("BasedOn"));
            // 16 in, and its control still starts where every row's does.
            Assert.Equal("154", Setter(subLabel, "Width"));
            Assert.Equal("16,0,12,0", Setter(subLabel, "Margin"));
            var slider = Style("DzSlider");
            Assert.Equal("{StaticResource EmberSlider}", (string)slider.Attribute("BasedOn"));
            Assert.Null(Setter(slider, "Width"));
            Assert.Null(Setter(slider, "MaxWidth"));

            var rows = SectionRows(scroll, own);
            Assert.True(rows.Count > 80, "the section has " + rows.Count + " rows");
            // A picker gives way: it stops at a MaxWidth and is marked to
            // fill, or asks for no width at all.
            var pickers = rows.Where(r => r.Elements().All(e => e.Name.LocalName != "Slider"))
                .SelectMany(r => r.Elements()).Where(e => e.Name.LocalName == "ComboBox").ToList();
            Assert.True(pickers.Count >= 13, pickers.Count + " pickers");
            Assert.All(pickers, c =>
            {
                Assert.Null(c.Attribute("Width"));
                if (c.Attribute("MaxWidth") != null)
                    Assert.Contains(c.Attributes(), a => a.Name.LocalName == "SettingRow.Fills" && a.Value == "True");
                // The cap is the room the row gives a picker, so a cap under
                // its least width would cut its chevron off.
                if (c.Attribute("MaxWidth") != null && c.Attribute("MinWidth") != null)
                    Assert.True(double.Parse((string)c.Attribute("MaxWidth"), System.Globalization.CultureInfo.InvariantCulture)
                        >= double.Parse((string)c.Attribute("MinWidth"), System.Globalization.CultureInfo.InvariantCulture),
                        (string)c.Attribute("MaxWidth") + " under " + (string)c.Attribute("MinWidth"));
            });

            // One card for each part.
            Assert.Equal(new[] { "GyroCard", "TouchpadCard", "IrPointerCard" },
                scroll.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource CardBorder}").Select(Name));

            // Gyro: Motion Passthrough, the calibration and the sensitivity
            // beside what the gyro reports, then More and the gyro's lines.
            var gyro = PartCard(Named(page, "GyroCard"), "", "{Binding Pad_Gyro_Header, Source={x:Static strings:Strings.Instance}}",
                "Pad_Gyro_Subtitle", "Pad_Scope_Device", "ResetGyroCardCommand");
            Assert.Equal(new[] { "DockPanel", "PartBodyPanel", "MoreRow", "FoldList" }, gyro.Select(e => e.Name.LocalName));
            // Its Reset All names the card it resets.
            Assert.Contains("Pad_ResetSection_Format", gyro[0].ToString());
            var gyroBody = gyro[1].Elements().ToList();
            Assert.Equal(2, gyroBody.Count);
            Assert.Equal(new[] { "GyroPassthroughCard", "GyroCalibrationCard", "GyroSensitivityCard" }, gyroBody[0].Elements().Select(Name));
            Assert.Equal("{StaticResource LiveWell}", (string)gyroBody[1].Attribute("Style"));

            // Motion Passthrough first: Raw or Tuned, in a group that reads
            // the row's name, then a line that says what the game reads.
            var passthrough = Named(page, "GyroPassthroughCard");
            var passRow = passthrough.Elements().First();
            Assert.Equal("SettingRow", passRow.Name.LocalName);
            string passName = (string)passRow.Elements().First().Attribute("Text");
            Assert.Equal("{Binding Pad_Gyro_Passthrough_Header, Source={x:Static strings:Strings.Instance}}", passName);
            var choice = passRow.Elements().ElementAt(1);
            Assert.Equal("GroupBox", choice.Name.LocalName);
            Assert.Equal("{StaticResource BareGroupBox}", (string)choice.Attribute("Style"));
            Assert.Equal(passName, (string)choice.Attribute("AutomationProperties.Name"));
            Assert.Contains(choice.Attributes(), a => a.Name.LocalName == "SettingRow.Fills" && a.Value == "True");
            Assert.Contains("Pad_Gyro_ApplyToPassthrough_Tooltip", Assert.Single(choice.Elements()).ToString());
            var segments = choice.Descendants().Where(e => e.Name.LocalName == "RadioButton").ToList();
            Assert.Equal(new[] { "Pad_GyroPassthroughRaw", "Pad_GyroPassthroughTuned" }, segments.Select(Id));
            Assert.Equal(new[] { "{Binding Pad_Gyro_Passthrough_Raw, Source={x:Static strings:Strings.Instance}}", "{Binding Pad_Gyro_Passthrough_Tuned, Source={x:Static strings:Strings.Instance}}" },
                segments.Select(s => (string)s.Attribute("Content")));
            Assert.Equal(new[] { "{Binding GyroPassthroughRaw, Mode=TwoWay}", "{Binding GyroApplyTuningToPassthrough, Mode=TwoWay}" },
                segments.Select(s => (string)s.Attribute("IsChecked")));
            Assert.All(segments, s => Assert.Equal("PadGyroPassthrough", (string)s.Attribute("GroupName")));
            Assert.Equal("{Binding ResetGyroApplyToPassthroughCommand}", (string)passRow.Elements().Last().Attribute("Command"));
            var said = passthrough.Elements().ElementAt(1);
            Assert.Contains("{Binding Pad_Gyro_Passthrough_RawLine, Source={x:Static strings:Strings.Instance}}", said.ToString());
            var whenTuned = Assert.Single(said.Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding GyroApplyTuningToPassthrough}", (string)whenTuned.Attribute("Binding"));
            Assert.Equal("True", (string)whenTuned.Attribute("Value"));
            Assert.Contains("{Binding Pad_Gyro_Passthrough_TunedLine, Source={x:Static strings:Strings.Instance}}", whenTuned.ToString());

            // The calibration: its two buttons on a line that wraps, and when
            // it was last done under them.
            var calibration = Named(page, "GyroCalibrationCard");
            var calLine = Assert.Single(calibration.Elements().First().Elements(), e => e.Name.LocalName == "WrapPanel");
            Assert.Equal(new[] { "{Binding GyroCalibrateCommand}", "{Binding GyroResetCalibrationCommand}" },
                calLine.Elements().Where(e => e.Name.LocalName == "Button").Select(b => (string)b.Attribute("Command")));
            Assert.Equal("{StaticResource ResetAllButton}", (string)calLine.Elements().ElementAt(1).Attribute("Style"));
            Assert.Contains("GyroCalibrationLabel", (string)calibration.Elements().Last().Attribute("Text"));

            // What the gyro reports right now: its tilt as a horizon, drawn
            // from the accelerometer, and its turn rates as bars from center,
            // each named in the language beside a number in a column at
            // least 78 wide, which holds -2000.0 degrees a second, so a
            // changing digit moves nothing.
            var well = gyroBody[1];
            var horizon = Assert.Single(well.Descendants(), e => e.Name.LocalName == "GyroHorizon");
            Assert.Equal(new[] { "{Binding AccelLiveX}", "{Binding AccelLiveY}", "{Binding AccelLiveZ}" },
                new[] { "AccelX", "AccelY", "AccelZ" }.Select(a => (string)horizon.Attribute(a)));
            var rates = Named(page, "GyroRatesLive");
            Assert.Contains(rates, well.Descendants());
            Assert.Equal(new[] { "Auto", "*", "Auto" }, rates.Elements().Single(e => e.Name.LocalName == "Grid.ColumnDefinitions")
                .Elements().Select(c => (string)c.Attribute("Width")));
            foreach (var (axis, row) in new[] { ("Pitch", "0"), ("Yaw", "1"), ("Roll", "2") })
            {
                var cells = rates.Elements().Where(e => !e.Name.LocalName.Contains('.') && (string)e.Attribute("Grid.Row") == row).ToList();
                Assert.Equal(new[] { "TextBlock", "CenterBar", "TextBlock" }, cells.Select(e => e.Name.LocalName));
                Assert.Equal("{Binding Dashboard_HeadTrackingAxis" + axis + ", Source={x:Static strings:Strings.Instance}}", (string)cells[0].Attribute("Text"));
                Assert.Equal("{Binding GyroLiveRate" + axis + "}", (string)cells[1].Attribute("Value"));
                Assert.Equal("{StaticResource LiveNumber}", (string)cells[2].Attribute("Style"));
                Assert.Equal("78", (string)cells[2].Attribute("MinWidth"));
                Assert.Equal("{Binding GyroLiveRate" + axis + ", Mode=OneWay, StringFormat={}{0:+0.0;-0.0;0.0}°/s}", (string)cells[2].Attribute("Text"));
            }
            Assert.Contains(well.Descendants(), e => e.Name.LocalName == "Run"
                && ((string)e.Attribute("Text") ?? "").StartsWith("{Binding Settings_GyroLiveAccel_Label,", StringComparison.Ordinal));

            // Sensitivity's More: the units, then a row for each axis to
            // invert, each with its own switch and reset. It stands over the
            // tray, so it is not the card's foot.
            var more = Named(page, "GyroSensitivityMore");
            Assert.Same(gyro[2], more);
            Assert.Equal("False", (string)more.Attribute("IsFoot"));
            foreach (string axis in new[] { "Pitch", "Yaw", "Roll" })
            {
                var toggle = Assert.Single(more.Descendants(), e => e.Name.LocalName == "CheckBox"
                    && (string)e.Attribute("IsChecked") == "{Binding GyroInvert" + axis + ", Mode=TwoWay}");
                Assert.Equal("{StaticResource SwitchCheckBox}", (string)toggle.Attribute("Style"));
                Assert.Single(toggle.Parent!.Elements(), e => e.Name.LocalName == "CheckBox");
                Assert.Equal("{Binding ResetGyroInvert" + axis + "Command}",
                    (string)Assert.Single(toggle.Parent.Elements(), e => e.Name.LocalName == "Button").Attribute("Command"));
            }

            PartTray(gyro,
                ("GyroGripCard", "", false), ("GyroEngageCard", "", false), ("GyroResponseCard", "", false),
                ("GyroSpaceCard", "", false), ("MotionSteeringFoldCard", "{StaticResource SteeringWheelGeometry}", false),
                ("GyroTiltFoldCard", "", false), ("GyroSimulationCard", "", true), ("CompassYawCard", "", true));
            Assert.Equal("True", (string)Named(page, "MotionSteeringFoldCard").Attribute("IconStroked"));
            // Grip's line ends with the grip that is set, by the name the
            // picker shows for it.
            var grip = Named(page, "GyroGripCard");
            var summary = Assert.Single(Assert.Single(grip.Elements(), e => e.Name.LocalName == "FoldCard.Summary").Elements());
            Assert.Equal("{StaticResource OptionTextConverter}", (string)summary.Attribute("Converter"));
            Assert.Equal(new[] { "MotionGrip", "MotionGripOptions", "Pad_Gyro_Grip_Header" },
                summary.Elements().Select(b => (string)b.Attribute("Path")));
            Assert.Contains(grip.Descendants(), e => (string)e.Attribute("SelectedValue") == "{Binding MotionGrip, Mode=TwoWay}");

            // Touchpad: Mouse Output's settings beside the pad's surface,
            // where each finger shows as it touches, then More and the
            // touchpad's lines.
            var touch = PartCard(Named(page, "TouchpadCard"), "", "{Binding Pad_Touchpad_Header, Source={x:Static strings:Strings.Instance}}",
                "Pad_Touchpad_Subtitle", "Pad_Scope_Device", "ResetTouchpadMouseCardCommand");
            Assert.Equal(new[] { "DockPanel", "PartBodyPanel", "MoreRow", "FoldList" }, touch.Select(e => e.Name.LocalName));
            Assert.Same(Named(page, "TouchpadMouseMore"), touch[2]);
            Assert.Equal("False", (string)touch[2].Attribute("IsFoot"));
            var touchWell = touch[1].Elements().Last();
            Assert.Equal("{StaticResource LiveWell}", (string)touchWell.Attribute("Style"));
            var surface = Assert.Single(touchWell.Descendants(), e => e.Name.LocalName == "TouchSurface");
            foreach (string finger in new[] { "X0", "Y0", "Down0", "X1", "Y1", "Down1" })
                Assert.Equal("{Binding DeviceTouch" + finger + "}", (string)surface.Attribute(finger));
            // Mouse Output's paragraph is its label's tooltip.
            var mouse = Assert.Single(touch[1].Descendants(), e => e.Name.LocalName == "TextBlock"
                && (string)e.Attribute("Text") == "{Binding Pad_Touchpad_Mouse_Header, Source={x:Static strings:Strings.Instance}}");
            Assert.Equal("{StaticResource StatLabel}", (string)mouse.Attribute("Style"));
            Assert.Contains("Pad_Touchpad_Mouse_Subtitle", (string)mouse.Attribute("ToolTip"));
            Assert.DoesNotContain(scroll.Descendants(), e => e.Name.LocalName == "TextBlock"
                && ((string)e.Attribute("Text") ?? "").Contains("Pad_Touchpad_Mouse_Subtitle"));
            PartTray(touch,
                ("TouchpadStickFoldCard", "{StaticResource TouchpadStickIconGeometry}", true), ("TouchpadPointerFoldCard", "", false),
                ("SyntheticPressureFoldCard", "", true), ("SwipeHapticsFoldCard", "", true),
                ("GestureDetectionFoldCard", "", true), ("InBoxGesturesFoldCard", "", false), ("CustomGesturesFoldCard", "", false));

            // IR Pointer: its mode, then More with the sensor bar's place,
            // the offset and the smoothing. It has no tray, so More is the
            // card's foot.
            var pointer = PartCard(Named(page, "IrPointerCard"), "", "{Binding Pad_Pointer_Header, Source={x:Static strings:Strings.Instance}}",
                "Pad_Pointer_Subtitle", "Pad_Scope_Device", "ResetIrPointerCardCommand");
            Assert.Contains("Pad_ResetSection_Format", pointer[0].ToString());
            var pointerMore = Named(page, "IrPointerMore");
            Assert.Same(pointer.Last(), pointerMore);
            Assert.Null(pointerMore.Attribute("IsFoot"));
            Assert.Equal("Pad_More_IrPointer", Id(pointerMore));
            Assert.Equal("Pad.Motion.Pointer.More", (string)pointerMore.Attribute("StateKey"));
            Assert.Equal("{Binding Pad_More_IrPointer, Source={x:Static strings:Strings.Instance}}", (string)pointerMore.Attribute("Hint"));
            foreach (string key in new[] { "Pad_Pointer_SensorBar", "Pad_Pointer_Compensation", "Pad_Pointer_Smoothing" })
                Assert.Contains(pointerMore.Descendants(), e => (string)e.Attribute("Text") == "{Binding " + key + ", Source={x:Static strings:Strings.Instance}}");
            Assert.Contains(pointer, e => e.Name.LocalName == "SettingRow"
                && e.Descendants().Any(d => (string)d.Attribute("Command") == "{Binding ResetPointerModeCommand}"));
        }

        /// <summary>Lights and Sound as its board draws it (the sections
        /// pass of #574): the Lighting card and the Audio card side by side,
        /// and LED Strips and Key Colors under Lighting for a device that
        /// has them. A card stacks its head, its body, its More row and a
        /// tray of the part's features as lines. The section has the row
        /// styles Motion and Touch has. A color's R, G and B rows keep the
        /// tight styles that fit them under a color picker, and a cell of
        /// the equalizer's table is no setting row.</summary>
        [Fact]
        public void LightsAndSound_HasRowsOfItsOwn_AndCardsAsItsBoardDrawsThem()
        {
            var page = Page();
            var scroll = Named(page, "LightsSectionScroll");
            var own = Assert.Single(scroll.Elements(), e => e.Name.LocalName == "ScrollViewer.Resources");
            XElement Style(string key) => Assert.Single(own.Elements(), e => (string)e.Attribute(X + "Key") == key);
            string Setter(XElement style, string property) => (string)style.Elements()
                .SingleOrDefault(e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == property)?.Attribute("Value");

            Assert.Equal("170", Setter(Style("DzLabel"), "Width"));
            Assert.Equal("154", Setter(Style("DzSubLabel"), "Width"));
            Assert.Null(Setter(Style("DzSlider"), "MaxWidth"));

            // The rows. A color's three and the table's cell stand apart.
            bool Color(XElement r) => r.Elements().Any(e => (string)e.Attribute("Style") == "{StaticResource DzLabelTight}");
            bool Cell(XElement r) => r.Attribute("Grid.Column") != null;
            var all = scroll.Descendants().Where(e => e.Name.LocalName == "SettingRow").ToList();
            var rows = SectionRows(scroll, own, r => Color(r) || Cell(r));
            Assert.Equal(21, all.Count(Color));
            Assert.Equal(1, all.Count(Cell));
            Assert.True(rows.Count > 45, "the section has " + rows.Count + " setting rows");
            Assert.All(all.Where(r => Color(r) || Cell(r)), r =>
            {
                Assert.Null(r.Attribute("Grow"));
                Assert.Null(r.Attribute("ValueWidth"));
            });
            // No panel sets its rows in: a row that belongs to the one
            // above has its label set in.
            Assert.DoesNotContain(scroll.Descendants(), e => e.Name.LocalName == "StackPanel"
                && ((string)e.Attribute("Margin") ?? "").StartsWith("20,", StringComparison.Ordinal)
                && e.Descendants().Any(d => d.Name.LocalName == "SettingRow"));
            // A picker or a number box gives way.
            var pickers = rows.Where(r => r.Elements().All(e => e.Name.LocalName != "Slider"))
                .SelectMany(r => r.Elements()).Where(e => e.Name.LocalName is "ComboBox" or "NumberBox").ToList();
            Assert.True(pickers.Count >= 17, pickers.Count + " pickers");
            Assert.All(pickers, c =>
            {
                Assert.Null(c.Attribute("Width"));
                // As wide as its choices, as the board draws a select (#574):
                // a least width of 120 at the left of its room, which a
                // narrow row still narrows under its cap.
                Assert.Equal("120", (string)c.Attribute("MinWidth"));
                Assert.Equal("Left", (string)c.Attribute("HorizontalAlignment"));
                // The cap is the room the row gives it, so a cap under the
                // least width would cut the picker's chevron off.
                Assert.True(double.Parse((string)c.Attribute("MaxWidth"), System.Globalization.CultureInfo.InvariantCulture) >= 120,
                    (string)c.Attribute("MaxWidth"));
                Assert.Contains(c.Attributes(), a => a.Name.LocalName == "SettingRow.Fills" && a.Value == "True");
            });

            // One card for each part.
            Assert.Equal(new[] { "LightingCard", "LedStripsCard", "KeyColorsCard", "AudioCard" },
                scroll.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource CardBorder}").Select(Name));

            // Lighting: the mode and the rows it reads beside the picture of
            // the lit lightbar, then the lights' lines. Its Reset All resets
            // the lightbar and shows with the lightbar's settings.
            var lighting = PartCard(Named(page, "LightingCard"), "", "{Binding Pad_Lighting_Header, Source={x:Static strings:Strings.Instance}}",
                "Pad_Lighting_Subtitle", "Pad_Scope_Device", "DeviceConfig.ResetLightbarAllCommand");
            Assert.Equal(new[] { "DockPanel", "PartBodyPanel", "FoldList" }, lighting.Select(e => e.Name.LocalName));
            Assert.Same(Named(page, "LightbarModeCard"), lighting[1]);
            Assert.Equal("{Binding Visibility, ElementName=LightbarModeCard}", (string)lighting[0].Elements().Last().Attribute("Visibility"));
            var preview = Named(page, "LightbarPreviewPanel");
            Assert.Same(lighting[1].Elements().Last(), preview);
            Assert.Equal("{StaticResource LiveWell}", (string)preview.Attribute("Style"));
            PartTray(lighting, ("IndicatorLedsCard", "", false), ("PeripheralLightingCard", "", true), ("GuideLedCard", "", false));
            Assert.Same(Named(page, "TabLighting"), lighting.Last().Ancestors().First(a => a.Name.LocalName == "GroupBox"));

            // Lighting's title holds the group's three paragraphs, which code
            // shows by the device, and the route line is the one text of the
            // peripheral's panel.
            var label = Named(page, "LightingGroupLabel");
            Assert.Same(lighting[0].Elements().ElementAt(1), label);
            var tip = Assert.Single(Assert.Single(label.Elements(), e => e.Name.LocalName == "TextBlock.ToolTip").Elements());
            Assert.Equal("ToolTip", tip.Name.LocalName);
            Assert.Equal(new[] { "LightingLightbarSubtitle", "LightingPlayerIdleHint", "PeripheralLightingSubtitle" },
                tip.Descendants().Where(e => e.Name.LocalName == "TextBlock").Select(e => (string)e.Attribute(X + "Name")));
            Assert.Equal("Collapsed", (string)Named(page, "PeripheralLightingSubtitle").Attribute("Visibility"));
            var routePanel = Named(page, "PeripheralLightingPanel");
            Assert.Null(routePanel.Attribute("Margin"));
            Assert.Equal("PeripheralLightingRoute", (string)Assert.Single(routePanel.Elements()).Attribute(X + "Name"));
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Contains("PeripheralLightingSubtitle.Visibility = hasPeripheralLighting ? Visibility.Visible : Visibility.Collapsed;", code);
            Assert.Contains("ToolTipService.SetIsEnabled(LightingGroupLabel, hasLightbar || hasPeripheralLighting);", code);

            // A mode's color blocks wrap: two for Battery and three for the
            // audio bands stood in a Grid of fixed columns, which needs 460
            // and 694 where a card in a narrow column has 388 inside.
            var wraps = lighting[1].Descendants().Where(e => e.Name.LocalName == "WrapPanel"
                && e.Elements().Any(b => b.Descendants().Any(d => d.Name.LocalName == "ColorPickerControl"))).ToList();
            Assert.Equal(new[] { 2, 3 }, wraps.Select(w => w.Elements().Count()));
            Assert.All(wraps.SelectMany(w => w.Elements()), group =>
            {
                // Each block stands in a group that reads its color's name and
                // draws nothing (ScreenReaderPassTests.AColorsBox_...).
                Assert.Equal("GroupBox", group.Name.LocalName);
                Assert.Equal("{StaticResource BareGroupBox}", (string)group.Attribute("Style"));
                Assert.Null(group.Attribute("Margin"));
                var block = Assert.Single(group.Elements());
                Assert.Equal("Border", block.Name.LocalName);
                Assert.Equal("0,0,8,8", (string)block.Attribute("Margin"));
                Assert.Null(block.Attribute("Grid.Column"));
            });
            Assert.DoesNotContain(lighting[1].Descendants(), e => e.Name.LocalName == "Grid"
                && e.Elements().Count(b => b.Name.LocalName == "Border" && b.Descendants().Any(d => d.Name.LocalName == "ColorPickerControl")) > 1);

            // LED Strips and Key Colors, for a device that has them.
            PartCard(Named(page, "LedStripsCard"), "", "{Binding Pad_Lighting_LedStrips, Source={x:Static strings:Strings.Instance}}",
                "Pad_Lighting_LedStrips_Description", "Pad_Scope_Device", "DeviceConfig.ResetLedStripCountsCommand");
            PartCard(Named(page, "KeyColorsCard"), "", "{Binding KeyColors.Title}", null, "Pad_Scope_Device", "DeviceConfig.ResetKeyLightingCommand");

            // Audio: the volume and the mirror switch, More, then the sound
            // chain's lines, Sound Macros and Sound Packages.
            var audio = PartCard(Named(page, "AudioCard"), "", "{Binding Pad_Audio_Header, Source={x:Static strings:Strings.Instance}}",
                "Pad_Audio_Subtitle", "Pad_Scope_Device", "ResetSoundOutputAllCommand");
            Assert.Equal("MoreRow", audio[audio.Count - 2].Name.LocalName);
            Assert.Same(Named(page, "SoundOutputMore"), audio[audio.Count - 2]);
            Assert.Equal("False", (string)audio[audio.Count - 2].Attribute("IsFoot"));
            // Its Reset All names the card: Reset Audio.
            var audioReset = audio[0].Elements().Last();
            Assert.Contains("Pad_Audio_Header", audioReset.ToString());
            Assert.Contains("Pad_ResetSection_Format", audioReset.ToString());
            // The title's tooltip is the group's paragraph, and for a Sony pad
            // also how that pad's sound travels.
            var audioTip = Assert.Single(audio[0].Descendants(), e => e.Name.LocalName == "ToolTip");
            var tipLines = audioTip.Descendants().Where(e => e.Name.LocalName == "TextBlock").ToList();
            Assert.Equal(new[] { "{Binding Pad_Audio_Subtitle, Source={x:Static strings:Strings.Instance}}", "{Binding Pad_Audio_Output_Desc, Source={x:Static strings:Strings.Instance}}" },
                tipLines.Select(e => (string)e.Attribute("Text")));
            Assert.Null(tipLines[0].Attribute("Visibility"));
            Assert.Equal("{Binding SelectedDeviceHasDspChain, Converter={StaticResource BoolToVisibilityConverter}}", (string)tipLines[1].Attribute("Visibility"));
            PartTray(audio, ("CrossfeedFoldCard", "", false), ("ParametricEqFoldCard", "", true), ("LimiterFoldCard", "", true),
                ("SoundMacrosFoldCard", "", false), ("SoundPackagesFoldCard", "", false));
            // No paragraph stands outside a tooltip.
            foreach (string paragraph in new[] { "Pad_Lighting_LedStrips_Description", "Pad_Audio_Output_Desc", "Pad_Audio_Subtitle", "Pad_Audio_Macros_Desc" })
                Assert.DoesNotContain(scroll.Descendants(), e => e.Name.LocalName == "TextBlock"
                    && ((string)e.Attribute("Text") ?? "").Contains(paragraph + ",")
                    && !e.Ancestors().Any(a => a.Name.LocalName == "ToolTip"));
            // Sound Macros' paragraph is its line's tooltip.
            var soundMacros = Named(page, "SoundMacrosFoldCard");
            Assert.Equal("{Binding Pad_Audio_Macros_Desc, Source={x:Static strings:Strings.Instance}}", (string)soundMacros.Attribute("Description"));
            Assert.Equal("{Binding Pad_Audio_Macros_Header, Source={x:Static strings:Strings.Instance}}", (string)soundMacros.Attribute("Header"));

            // The mirror switch's long label takes the row's room, its
            // switch at the row's end: a fixed width narrower than the room
            // stood the label in the middle of it.
            var mirror = scroll.Descendants().Single(e => e.Name.LocalName == "TextBlock"
                && ((string)e.Attribute("Text") ?? "").Contains("Pad_Audio_Mirror,"));
            Assert.Null(mirror.Attribute("Width"));
            Assert.Equal("{StaticResource EndsLabel}", (string)mirror.Attribute("Style"));
            // No label of a row with its switch at the end keeps a width.
            Assert.DoesNotContain(page.Descendants().Where(e => e.Name.LocalName == "SettingRow"
                    && e.Attributes().Any(a => a.Name.LocalName == "SettingRow.Ends" && a.Value == "True")),
                r => r.Elements().First(e => !e.Name.LocalName.Contains('.')).Attribute("Width") != null);
        }

        /// <summary>Rumble and Haptics as its board tags and draws it. A card
        /// whose settings the device picked under Driven by keeps as its own
        /// has the tag This device after its title, and Bass Shakers, whose
        /// settings are the whole controller's, has Whole controller. Each
        /// part is one card with its icon, and a part's features are the
        /// lines of its card's tray. A Bass
        /// Shakers tone is a row with Test and the tone's reset, then its gain
        /// set in under it, and the card's last row is Check the Shakers. The
        /// two values of a range row have "to" between them, as the Sticks
        /// board writes it, and no lone dash is left on the page.</summary>
        [Fact]
        public void RumbleAndHaptics_TagsItsCards_AndDrawsATonesRows()
        {
            var page = Page();
            var scroll = Named(page, "RumbleSectionScroll");
            string KeyOf(string binding) => binding.Substring("{Binding ".Length).Split(',')[0];

            // The tags: in a card's head, after its title, before Reset All.
            var tags = scroll.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource ChipSteel}").ToList();
            Assert.Equal(new[]
                {
                    "Pad_Falcon_Header=Pad_Scope_Device", "Pad_ForceFeedback_Header=Pad_Scope_Device", "Pad_ImpulseTriggers_Header=Pad_Scope_Device",
                    "Pad_AdaptiveTriggers_Header=Pad_Scope_Device", "Pad_Wheel=Pad_Scope_Device", "Pad_RumbleAudio_Header=Pad_Scope_Controller",
                },
                tags.Select(t => KeyOf((string)t.ElementsBeforeSelf().Last().Attribute("Text")) + "=" + KeyOf((string)Assert.Single(t.Elements()).Attribute("Text"))));
            Assert.All(tags, t =>
            {
                Assert.Equal("DockPanel", t.Parent!.Name.LocalName);
                Assert.Equal("{StaticResource HeroCardTitle}", (string)t.ElementsBeforeSelf().Last().Attribute("Style"));
                Assert.Equal("{StaticResource ResetAllButton}", (string)t.ElementsAfterSelf().Single().Attribute("Style"));
                Assert.Equal("10,0,0,0", (string)t.Attribute("Margin"));
                Assert.Equal("Center", (string)Assert.Single(t.Elements()).Attribute("VerticalAlignment"));
            });
            // The tag's look, the boards' chip: a pill 22 tall on the raised
            // fill inside the pill hairline, its words 12 in the secondary color.
            var chip = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml")).Descendants()
                .Single(e => (string)e.Attribute(X + "Key") == "ChipSteel");
            string Setter(string property) => (string)chip.Elements().Single(e => (string)e.Attribute("Property") == property).Attribute("Value");
            Assert.Equal("22", Setter("MinHeight"));
            Assert.Equal("11", Setter("CornerRadius"));
            Assert.Equal("9,0", Setter("Padding"));
            Assert.Equal("{DynamicResource SteelPillLineBrush}", Setter("BorderBrush"));
            Assert.Equal("{DynamicResource SteelRaisedBrush}", Setter("Background"));
            Assert.Equal("{DynamicResource TextFillColorSecondaryBrush}", Setter("TextElement.Foreground"));
            Assert.Equal("12", Setter("TextElement.FontSize"));

            // One card for each part (the sections pass of #574). Force
            // Feedback and Impulse Triggers set their gain beside what their
            // motors do now, and their features are the lines of the card's
            // tray: Audio Rumble and Trigger Routing stand in Force
            // Feedback's tray, never alone between the cards. Adaptive
            // Triggers draws each trigger's effect beside its picker.
            XElement CardOf(string title) => scroll.Descendants().Single(e => e.Name.LocalName == "TextBlock"
                    && (string)e.Attribute("Text") == "{Binding " + title + ", Source={x:Static strings:Strings.Instance}}"
                    && (string)e.Attribute("Style") == "{StaticResource HeroCardTitle}")
                .Ancestors().First(a => (string)a.Attribute("Style") == "{StaticResource CardBorder}");
            string Title(string key) => "{Binding " + key + ", Source={x:Static strings:Strings.Instance}}";
            PartCard(CardOf("Pad_Falcon_Header"), "", Title("Pad_Falcon_Header"), "Pad_Falcon_Description", "Pad_Scope_Device", "ResetFalconAllCommand");
            var force = PartCard(CardOf("Pad_ForceFeedback_Header"), "", Title("Pad_ForceFeedback_Header"), "Pad_ForceFeedback_Subtitle",
                "Pad_Scope_Device", "ResetForceAllCommand");
            Assert.Equal(new[] { "DockPanel", "PartBodyPanel", "MoreRow", "FoldList" }, force.Select(e => e.Name.LocalName));
            Assert.Equal("False", (string)force[2].Attribute("IsFoot"));
            var forceWell = force[1].Elements().Last();
            Assert.Equal("{StaticResource LiveWell}", (string)forceWell.Attribute("Style"));
            Assert.Contains(forceWell.Descendants(), e => (string)e.Attribute("Text") == Title("Pad_MotorActivity"));
            PartTray(force, ("AudioRumbleFoldCard", "", true), ("TriggerRoutingFoldCard", "", false));
            var impulse = PartCard(CardOf("Pad_ImpulseTriggers_Header"), "Viewbox", Title("Pad_ImpulseTriggers_Header"), "Pad_ImpulseTriggers_Subtitle",
                "Pad_Scope_Device", "ResetImpulseAllCommand");
            Assert.Equal(new[] { "DockPanel", "PartBodyPanel", "MoreRow", "FoldList" }, impulse.Select(e => e.Name.LocalName));
            Assert.Equal("False", (string)impulse[2].Attribute("IsFoot"));
            Assert.Equal("{StaticResource LiveWell}", (string)impulse[1].Elements().Last().Attribute("Style"));
            PartTray(impulse, ("AudioTriggerRumbleFoldCard", "", true));
            PartCard(CardOf("Pad_AdaptiveTriggers_Header"), "Viewbox", Title("Pad_AdaptiveTriggers_Header"), "Pad_AdaptiveTriggers_Subtitle",
                "Pad_Scope_Device", "DeviceConfig.ResetAdaptiveTriggersCommand");
            foreach (string side in new[] { "Left", "Right" })
                Assert.Equal("TriggerEffectGraph", Named(page, side + "TriggerModePicker").ElementsAfterSelf().First().Name.LocalName);
            PartCard(CardOf("Pad_Wheel"), "Viewbox", Title("Pad_Wheel"), "Pad_Wheel_Subtitle", "Pad_Scope_Device", "ResetWheelAllCommand");
            PartCard(CardOf("Pad_RumbleAudio_Header"), "", Title("Pad_RumbleAudio_Header"), null, "Pad_Scope_Controller", "ResetRumbleAudioAllCommand");

            // A tone: its row, then its gain set in under it.
            var tones = scroll.Descendants().Single(e => e.Name.LocalName == "ItemsControl" && (string)e.Attribute("ItemsSource") == "{Binding RumbleAudioVoices}");
            var tone = Assert.Single(Assert.Single(tones.Descendants(), e => e.Name.LocalName == "DataTemplate").Elements());
            var rows = tone.Elements().ToList();
            Assert.Equal(new[] { "SettingRow", "SettingRow" }, rows.Select(r => r.Name.LocalName));
            var first = rows[0].Elements().ToList();
            Assert.Equal(new[] { "CheckBox", "Slider", "TextBox", "TextBlock", "Button", "Button" }, first.Select(e => e.Name.LocalName));
            Assert.Equal("{Binding Enabled, Mode=TwoWay}", (string)first[0].Attribute("IsChecked"));
            Assert.Equal("{Binding FrequencyHz, Mode=TwoWay}", (string)first[1].Attribute("Value"));
            Assert.Equal("{StaticResource DzSlider}", (string)first[1].Attribute("Style"));
            Assert.Equal("{Binding TestCommand}", (string)first[4].Attribute("Command"));
            Assert.Equal("{StaticResource QuietPlainButton}", (string)first[4].Attribute("Style"));
            Assert.Equal("{Binding ResetCommand}", (string)first[5].Attribute("Command"));
            var gain = rows[1].Elements().ToList();
            Assert.Equal(new[] { "TextBlock", "Slider", "TextBox", "TextBlock", "ProgressBar" }, gain.Select(e => e.Name.LocalName));
            Assert.Equal("{Binding Pad_RumbleAudio_VoiceGain, Source={x:Static strings:Strings.Instance}}", (string)gain[0].Attribute("Text"));
            Assert.Equal("{StaticResource DzSubLabel}", (string)gain[0].Attribute("Style"));
            Assert.Equal("{Binding GainPercent, Mode=TwoWay}", (string)gain[1].Attribute("Value"));
            Assert.Equal("{StaticResource DzSlider}", (string)gain[1].Attribute("Style"));
            Assert.Equal("{Binding MeterLevel, Mode=OneWay}", (string)gain[4].Attribute("Value"));
            Assert.Equal("{StaticResource ActivityBar}", (string)gain[4].Attribute("Style"));

            // The card's last row: Check the Shakers, a sweep, and Stop.
            var check = tones.ElementsAfterSelf().Single();
            Assert.Equal("SettingRow", check.Name.LocalName);
            var parts = check.Elements().ToList();
            Assert.Equal("{Binding Pad_RumbleAudio_CheckShakers, Source={x:Static strings:Strings.Instance}}", (string)parts[0].Attribute("Text"));
            Assert.Equal("{StaticResource DzLabel}", (string)parts[0].Attribute("Style"));
            Assert.Equal(new[] { "{Binding RumbleAudioSweepCommand}", "{Binding RumbleAudioStopTestCommand}" }, parts.Skip(1).Select(b => (string)b.Attribute("Command")));
            Assert.Equal("{StaticResource QuietPlainButton}", (string)parts[2].Attribute("Style"));
            // The line beside the card's switch is the board's short one.
            Assert.Contains(scroll.Descendants(), e => (string)e.Attribute("Text") == "{Binding Pad_RumbleAudio_Subtitle, Source={x:Static strings:Strings.Instance}}");

            // A range's two values have "to" between them, and no lone dash is left.
            var to = page.Descendants().Where(e => e.Name.LocalName == "TextBlock"
                && (string)e.Attribute("Text") == "{Binding Common_To, Source={x:Static strings:Strings.Instance}}").ToList();
            Assert.Equal(4, to.Count);
            Assert.All(to, t =>
            {
                Assert.Equal("TextBox", t.ElementsBeforeSelf().Last().Name.LocalName);
                Assert.Equal("TextBox", t.ElementsAfterSelf().First().Name.LocalName);
                Assert.Equal("13", (string)t.Attribute("FontSize"));
            });
            Assert.DoesNotContain(page.Descendants(), e => e.Name.LocalName == "TextBlock" && (string)e.Attribute("Text") is "\u2013" or "-");
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

            // It picks a slot's device among the Driven by pills by an id
            // (TheCaptureScriptOpensAControllerPageThroughItsSidebarEntry
            // holds how it opens the slot's page).
            Assert.DoesNotContain("-eq 'Slot'", script);
            Assert.Contains("Find-UIA -Parent $padPage -Aid \"Pad_DrivenByList\"", script);
            Assert.Single(page.Descendants(), e => Id(e) == "Pad_DrivenByList"
                && (string)e.Attribute("SelectedItem") == "{Binding SelectedMappedDevice, Mode=TwoWay}");
            // The pills are a list that is always open, so the script
            // selects a list item and opens nothing first, as it had to
            // with the closed picker that stood there.
            int pick = script.IndexOf("function Select-MappedDeviceOnce", StringComparison.Ordinal);
            Assert.True(pick > 0);
            string picking = script.Substring(pick, script.IndexOf("\n}", pick, StringComparison.Ordinal) - pick);
            Assert.Contains("[System.Windows.Automation.SelectionItemPattern]::Pattern).Select()", picking);
            Assert.DoesNotContain("ExpandCollapsePattern", picking);
            // Nothing in it reads the page's radio buttons as a tab strip
            // any more: the first of them is the Buttons section now.
            Assert.DoesNotContain("$tabs[0]", script);
            Assert.DoesNotContain("Controller Tab\"", script);
        }

        /// <summary>Where the screenshot script frames each scrolled shot
        /// of the controller page: a row of its anchor table names a
        /// scrolled view and an element in it, by an id or by the English
        /// word the page shows there. A row whose id the page dropped, or
        /// whose element moved to another view, would lose its shot.</summary>
        [Fact]
        public void TheCaptureScriptsAnchorTable_NamesWhatThePageHas()
        {
            string script = ExtrasPageTests.RepoText("tools", "capture_all.ps1").Replace("\r\n", "\n");
            int at = script.IndexOf("$script:PadAnchors = @{\n", StringComparison.Ordinal);
            Assert.True(at > 0, "no anchor table");
            string table = script.Substring(at, script.IndexOf("\n}\n", at, StringComparison.Ordinal) - at);
            var rows = System.Text.RegularExpressions.Regex.Matches(table,
                @"^    ""(?<shot>[a-z0-9-]+)""\s+= @\{ Column = ""(?<column>\w+)""; (?:Aid = ""(?<aid>\w+)""|Name = ""(?<name>[^""]+)""; Type = ""(?<type>\w+)""(?:; Within = ""(?<within>\w+)"")?)(?:; At = (?<at>[0-9.]+))? \}$",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            // Positive control: every row of the table was read.
            Assert.True(rows.Count >= 20, "only " + rows.Count + " rows read");
            Assert.Equal(table.Split('\n').Count(l => l.StartsWith("    \"", StringComparison.Ordinal)), rows.Count);

            var page = Page();
            var english = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"))
                .Root.Elements("data").ToLookup(d => (string)d.Element("value"), d => (string)d.Attribute("name"));
            XElement Find(string id) => Assert.Single(page.Descendants(), e => Id(e) == id || Name(e) == id);
            foreach (System.Text.RegularExpressions.Match row in rows)
            {
                string shot = row.Groups["shot"].Value;
                XElement column = Find(row.Groups["column"].Value);
                Assert.Equal("ScrollViewer", column.Name.LocalName);
                if (row.Groups["aid"].Success)
                {
                    XElement el = Find(row.Groups["aid"].Value);
                    Assert.True(el.Ancestors().Contains(column), shot + ": " + row.Groups["aid"].Value + " is not in " + row.Groups["column"].Value);
                    continue;
                }
                Assert.Equal("Text", row.Groups["type"].Value);
                XElement scope = column;
                if (row.Groups["within"].Success)
                {
                    scope = Find(row.Groups["within"].Value);
                    Assert.True(scope.Ancestors().Contains(column), shot + ": " + row.Groups["within"].Value + " is not in " + row.Groups["column"].Value);
                }
                var keys = english[row.Groups["name"].Value].ToList();
                Assert.True(keys.Count > 0, shot + ": no string reads '" + row.Groups["name"].Value + "'");
                Assert.True(scope.Descendants().Any(e => e.Name.LocalName == "TextBlock"
                        && keys.Any(k => ((string)e.Attribute("Text") ?? "").StartsWith("{Binding " + k + ",", StringComparison.Ordinal))),
                    shot + ": no text in its view shows '" + row.Groups["name"].Value + "'");
            }
        }

        /// <summary>The screenshot script opens a controller's page through
        /// its sidebar entry, selected by pattern: the entry holds a power
        /// switch and a type chip, and a click on its middle can land on
        /// either. It finds the entry by the slot's type, read from the
        /// settings file, and opens no page from a Home card.</summary>
        [Fact]
        public void TheCaptureScriptOpensAControllerPageThroughItsSidebarEntry()
        {
            string script = ExtrasPageTests.RepoText("tools", "capture_all.ps1").Replace("\r\n", "\n");
            string Body(string name)
            {
                int start = script.IndexOf("\nfunction " + name + " {\n", StringComparison.Ordinal);
                Assert.True(start >= 0, "no function " + name);
                return script.Substring(start, script.IndexOf("\n}\n", start, StringComparison.Ordinal) - start);
            }
            string open = Body("Open-SlotPage");
            Assert.Contains("$pad = Get-SlotPadIndex $Type", open);
            Assert.Contains("$tag = \"Pad\" + ($pad + 1)", open);
            Assert.Contains("if (Nav $tag) {", open);
            Assert.Contains("(Find-SidebarEntry $tag).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)", open);
            Assert.DoesNotContain("Click-", open);
            string index = Body("Get-SlotPadIndex");
            Assert.Contains("Read-SettingsXml $PadForgeXml", index);
            Assert.Contains("SelectNodes(\"SlotCreated/*\")", index);
            Assert.Contains("SelectNodes(\"SlotControllerTypes/*\")", index);
            Assert.DoesNotContain("Save-SettingsXml", index);
            Assert.Contains("Select-El $el -Label $Name", Body("Nav"));
            foreach (string gone in new[] { "Open-SlotCard", "SlotsItemsControl", "Dash_SlotTitle" })
                Assert.DoesNotContain(gone, script);
            // What it rests on: a controller entry's tag, and the id made of it.
            Assert.Contains("public string Tag => $\"Pad{PadIndex + 1}\";", ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "MainViewModel.cs"));
            Assert.Contains("SetNavAutomationId(menuItem, navItem.Tag);", ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs"));
            // Positive control: the passes open their pages this way.
            Assert.True(System.Text.RegularExpressions.Regex.Matches(script, "Open-SlotPage -Type ").Count >= 25);
        }

        /// <summary>The page's two rows of tabs are gone, and so is every
        /// point the screenshot script had measured on them: fractions of
        /// the window that fall in the header now, where a click picks
        /// another device, leaves for the Devices page or opens the Preset
        /// list. Gone with them are the helpers that only those points
        /// used.</summary>
        [Fact]
        public void NoControllerPageRecipeClicksWhereTheOldPageHadAControl()
        {
            string script = ExtrasPageTests.RepoText("tools", "capture_all.ps1");
            foreach (string gone in new[]
            {
                "0.1174", "0.0755", "0.2879", "0.02538", "0.0441", "0.6897", "0.4278", "0.3981", "0.3495", "0.3775",
                "0.7635", "0.4363", "0.232 *", "0.226 *", "0.175 *", "0.669 *", "0.7246 *", "0.5072 *", "0.925 *",
                "Left + 431", "Right - 53", "ModelViewPart", "Find-LowestTextBandY", "Get-ScreenBrightCount", "Get-ScreenBytes",
                "Select-Preset", "Select-ListRowByName", "MenuItemsHost", "$tabsSp[0]", "$tabsV[0]",
                "Open-MappingRow", "Open-MenuEditor", "Capture-MenuStyleShots", "Expand-DisconnectTarget",
            })
                Assert.DoesNotContain(gone, script);
        }

        /// <summary>No wheel turns on the controller page or on an extra's
        /// page. A wheel there changes what the pointer rests on: the 3D
        /// view zooms and keeps the zoom, a closed picker with the focus
        /// takes its next choice, an EQ handle changes its Q. The script's
        /// wheel helpers refuse while such a page shows, those pages scroll
        /// through a view's own scroll pattern, and the recipes that run on
        /// them call no wheel helper.</summary>
        [Fact]
        public void TheWheelStaysOffTheControllerAndExtrasPages()
        {
            string script = ExtrasPageTests.RepoText("tools", "capture_all.ps1").Replace("\r\n", "\n");
            string Body(string name)
            {
                int start = script.IndexOf("\nfunction " + name + " {\n", StringComparison.Ordinal);
                Assert.True(start >= 0, "no function " + name);
                return script.Substring(start, script.IndexOf("\n}\n", start, StringComparison.Ordinal) - start);
            }
            string[] wheel = { "ScrollContent", "Scroll-ToAnchor", "Scroll-ToAnchors", "ScrollPane", "Scroll-PaneToAnchor", "Scroll-AnchorToTop" };
            foreach (string helper in wheel)
            {
                string body = Body(helper);
                int guard = body.IndexOf("if (-not (Test-WheelSafe)) { return", StringComparison.Ordinal);
                Assert.True(guard > 0, helper + " does not ask whether a wheel is safe");
                foreach (string turn in new[] { "[Win32]::ScrollAt(", "[Win32]::MoveTo(", "ScrollContent -Clicks", "ScrollPane -" })
                {
                    int first = body.IndexOf(turn, StringComparison.Ordinal);
                    Assert.True(first < 0 || first > guard, helper + " turns the wheel before it asks");
                }
            }
            string safe = Body("Test-WheelSafe");
            Assert.Contains("$script:StateFailures +=", safe);
            Assert.Contains("$script:WheelUnsafe = \"the controller page\"", Body("Open-SlotPage"));
            Assert.Contains("$script:WheelUnsafe = \"an extra's page\"", Body("Open-Extra"));
            Assert.Contains("$script:WheelUnsafe = $null", Body("Nav"));
            Assert.Contains("$script:WheelUnsafe = $null", Body("Wait-PadForgeReady"));

            // Every wheel event is sent from a helper, or from a recipe of
            // the Devices or the Settings page.
            string[] lines = script.Split('\n');
            string inside = null;
            int sent = 0;
            foreach (string line in lines)
            {
                var head = System.Text.RegularExpressions.Regex.Match(line, @"^function ([\w-]+) \{");
                if (head.Success) inside = head.Groups[1].Value;
                if (!line.Contains("[Win32]::ScrollAt(", StringComparison.Ordinal) || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                sent++;
                Assert.Contains(inside, new[] { "ScrollContent", "ScrollPane", "Scroll-AnchorToTop", "Capture-UpdatesCard", "Select-DeviceByName36" });
            }
            // Positive control: the scan saw the wheel being sent.
            Assert.True(sent >= 5, "only " + sent + " wheel sends were found");

            // The recipes of the controller page and of the Extras pages.
            foreach (string recipe in new[]
            {
                "Tab", "Wait-PadTab", "Open-PickedRow", "Select-MappingRow", "Show-PadAnchor", "Scroll-PadGroupToTop",
                "Capture-SourcePicker", "Capture-IconPicker", "Capture-MotionRow", "Capture-MenuShots", "Capture-MacroShots",
                "Capture-RowOptionShots", "Capture-PeripheralTabs", "Capture-FalconPadShots", "Capture-ScreenColorLighting",
                "Capture-Ds3Preset", "Capture-Colorways", "Capture-AnnotationOverlay",
                "Open-Extra", "Show-ExtraAnchors", "Capture-ExtrasPages", "Capture-ExtrasCatalog", "Capture-WebPlainSection",
                "Capture-GameModsCard", "Capture-GameTelemetryCard", "Capture-TelemetryFormatEditor", "Capture-ScreenOverlayCard",
            })
            {
                string body = Body(recipe);
                foreach (string helper in wheel)
                    Assert.False(System.Text.RegularExpressions.Regex.IsMatch(body, @"(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(helper) + @"(?![\w-])"),
                        recipe + " calls " + helper);
                Assert.DoesNotContain("[Win32]::ScrollAt(", body);
            }
        }

        /// <summary>The Macros list and the Menus list carry ids, so the
        /// screenshot script picks a macro or a menu in any language and
        /// does not take the part switch of the same name for the list. A
        /// macro's steps and a menu's cells are lists it finds by name.</summary>
        [Fact]
        public void TheMacroAndMenuLists_CarryTheIdsTheCaptureScriptPicksThemBy()
        {
            var page = Page();
            foreach (var (id, key) in new[] { ("MacroList", "Pad_Macros"), ("MenuList", "Pad_Menus") })
            {
                XElement list = Assert.Single(page.Descendants(), e => Id(e) == id);
                Assert.Equal("ListBox", list.Name.LocalName);
                Assert.Equal("{Binding " + key + ", Source={x:Static strings:Strings.Instance}}", (string)list.Attribute("AutomationProperties.Name"));
                Assert.Equal("{StaticResource PartList}", (string)list.Attribute("Style"));
            }
            foreach (string name in new[] { "ActionsList", "CellsList" })
                Assert.Equal("ListBox", Named(page, name).Name.LocalName);
            string script = ExtrasPageTests.RepoText("tools", "capture_all.ps1");
            foreach (string id in new[] { "MacroList", "MenuList", "ActionsList", "CellsList" })
                Assert.Contains("Select-PadListItem -ListAid \"" + id + "\"", script);
            // A control picked through its list row stays picked, so each
            // recipe that picks one ends on a fresh process.
            Assert.Contains("$script:RowPicked = $true", script);
            foreach (string recipe in new[] { "function Capture-RowOptionShots {", "function Capture-MotionRow {", "function Capture-SourcePickerSet {" })
            {
                int start = script.IndexOf(recipe, StringComparison.Ordinal);
                Assert.True(start > 0, recipe);
                string body = script.Substring(start, script.IndexOf("\n}", start, StringComparison.Ordinal) - start);
                Assert.Contains("Drop-RowPick | Out-Null", body);
            }
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

        /// <summary>The page's string round (#574): each new string is in the
        /// table with its English, the strings left with no use are gone from
        /// the table and from the class that reads it, and no English string
        /// sends the user to a tab the page no longer has, by the word or by
        /// the old Mappings tab's name. The Tab key's own name, and the strings
        /// that name that key, stay.</summary>
        [Fact]
        public void TheRoundsStringsAreInTheTable_AndNoneNamesATab()
        {
            var table = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"));
            var data = table.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value") ?? "");
            foreach (var (key, english) in new[]
            {
                ("Macro_When_Header", "When"), ("Macro_Do_Header", "Do"), ("Menu_Opens_Header", "Opens"), ("Common_Rename", "Rename"),
                ("Menu_NewMenu", "New Menu"), ("Menu_AddCell", "Add Cell"), ("Menu_RemoveLastCell", "Remove Last Cell"),
                ("Menu_EditCell", "Edit Cell"), ("Menu_Cell_Name", "Name"), ("Menu_Cell_Action", "Action"), ("Menu_Cell_Icon", "Icon"),
                ("Pad_Scope_Device", "This device"), ("Pad_Scope_Controller", "Whole controller"), ("Pad_RumbleAudio_VoiceGain", "Gain"),
                ("Pad_RumbleAudio_CheckShakers", "Check the Shakers"), ("Common_To", "to"), ("Pad_Gyro_Space_Header", "Reference Frame"),
                ("Pad_GyroSpace_FoldHint", "Local, Player or World"), ("Pad_Grip_FoldHint", "How the controller is held"),
                ("Pad_Lighting_GamesWin_Hint", "Games take over whenever they write lighting."), ("Pad_AudioEq_FoldHint", "Preamp, bands, import"),
                ("Pad_More_ForceFeedback", "Per-motor gain, trigger rumble, Steering Angle Rumble, Constant Force"),
                ("Pad_More_ImpulseTriggers", "Per-trigger gain, motor swap, adaptive trigger vibrations, Constant Trigger Force"),
                ("Pad_More_GyroSensitivity", "Units, invert axes, Real-World Calibration"),
                ("Pad_More_SoundOutput", "Mirror Source, Headphone Volume, buffer, Output Path"),
                ("Pad_RumbleAudio_Subtitle", "Plays game rumble as low tones through bass shakers or a subwoofer."),
                ("Macro_EmptyHint", "Pick a macro in the list, or press + beside Macros to make one."),
                ("Pad_More_Macro", "Layer, Source, Threshold, Direction, Consume Trigger Buttons, Repeat"),
            })
                Assert.Equal(english, data[key]);
            Assert.Contains("Press + beside Menus", data["Menu_EmptyHint"]);
            string designer = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            foreach (string gone in new[] { "Menu_Position", "Menu_CellBindings", "Menu_Cells_Tip", "Menu_Reset_Cells", "Macro_EditAction", "Macro_Trigger", "Macro_ActionSequence" })
            {
                Assert.False(data.ContainsKey(gone), gone + " is still in the table");
                Assert.DoesNotContain("public string " + gone + " =>", designer);
            }

            var keyNames = new HashSet<string> { "Key_Tab", "Osk_Key_Tab", "MacroAction_TextBlock_Tooltip", "Pad_Lighting_KeyColors_PickHint", "Pad_Lighting_KeyColors_PickByPressing_Tooltip" };
            Assert.Empty(data.Where(d => !keyNames.Contains(d.Key) && System.Text.RegularExpressions.Regex.IsMatch(d.Value, @"(?i)\btabs?\b")).Select(d => d.Key));
            Assert.Empty(data.Where(d => d.Value.Contains("in Mappings", StringComparison.Ordinal) || d.Value.Contains("Mappings rows", StringComparison.Ordinal)).Select(d => d.Key));
            // The key's own name is kept.
            Assert.Equal("Tab", data["Key_Tab"]);
        }

        /// <summary>The sections pass's strings (#574): each new string is in
        /// the table with its English, the strings it changed say what the
        /// page has now, and the fifteen it left with no use are gone from
        /// the table and from the class that reads it.</summary>
        [Fact]
        public void TheSectionsPassStrings_AreInTheTable()
        {
            var table = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"));
            var data = table.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value") ?? "");
            foreach (var (key, english) in new[]
            {
                ("Pad_Gyro_Passthrough_Raw", "Raw"), ("Pad_Gyro_Passthrough_Tuned", "Tuned"),
                ("Pad_Gyro_Passthrough_RawLine", "The game reads the calibrated motion and applies its own sensitivity and smoothing."),
                ("Pad_Gyro_Passthrough_TunedLine", "The game reads the motion with the sensitivity, smoothing and curve set here."),
                ("Pad_BothSticks_Header", "Both Sticks"),
                ("Pad_More_IrPointer", "Sensor Bar Position, Vertical Offset, Smoothing"),
            })
                Assert.Equal(english, data[key]);
            // The IR pointer's More names the three settings it holds by
            // their own names.
            Assert.Equal(string.Join(", ", data["Pad_Pointer_SensorBar"], data["Pad_Pointer_Compensation"], data["Pad_Pointer_Smoothing"]),
                data["Pad_More_IrPointer"]);
            // The device is picked under Driven by, and Motion Passthrough is
            // a choice of Raw or Tuned, not a switch to turn off.
            Assert.Contains("under Driven by", data["Pad_Audio_Subtitle"]);
            Assert.DoesNotContain("dropdown", data["Pad_Audio_Subtitle"]);
            Assert.StartsWith(data["Pad_Gyro_Passthrough_Tuned"] + " runs the Gyro card's tuning", data["Pad_Gyro_ApplyToPassthrough_Tooltip"]);
            Assert.Contains(data["Pad_Gyro_Passthrough_Raw"] + " sends only the calibrated reading", data["Pad_Gyro_ApplyToPassthrough_Tooltip"]);
            Assert.DoesNotContain("Turn it off", data["Pad_Gyro_ApplyToPassthrough_Tooltip"]);
            // No string sends the user to a card the page no longer has.
            Assert.Empty(data.Where(d => d.Value.Contains("Lightbar card", StringComparison.Ordinal)
                || d.Value.Contains("Sound Output", StringComparison.Ordinal)).Select(d => d.Key));
            string designer = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            foreach (string gone in new[]
            {
                "Pad_Audio_Output_Header", "Pad_Gyro_ApplyToPassthrough", "Pad_Gyro_ResetCalibration_All_Tooltip", "Pad_Gyro_ResetPassthrough_All_Tooltip",
                "Pad_Gyro_ResetSensitivity_All_Tooltip", "Pad_Gyro_Sensitivity_Header", "Pad_Lighting_BaseColor", "Pad_More_GyroCalibration",
                "Pad_Pointer_TuningTitle", "Pad_ResetPointerModeCard", "Pad_ResetPointerTuning", "Settings_GyroLiveRate_Label", "Pad_Audio_Reset_All",
                "Pad_MotorActivity_Left", "Pad_MotorActivity_Right",
            })
            {
                Assert.False(data.ContainsKey(gone), gone + " is still in the table");
                Assert.DoesNotContain("public string " + gone + " =>", designer);
            }
        }
    }
}
