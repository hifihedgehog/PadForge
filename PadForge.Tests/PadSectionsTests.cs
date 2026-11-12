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
        /// order. A stacked section is one scrolling column of named groups.
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
                    if (section == PadViewModel.MotionSection || section == PadViewModel.LightsSection)
                    {
                        // Motion & Touch and Lights & Sound stand their
                        // groups side by side while each has 420 to itself,
                        // as their boards do, and the groups of a row share
                        // the whole width.
                        Assert.Equal("TileGridPanel", column.Name.LocalName);
                        Assert.Equal("420", (string)column.Attribute("MinItemWidth"));
                        Assert.Equal("16", (string)column.Attribute("Gap"));
                        Assert.Equal("24", (string)column.Attribute("RowGap"));
                        Assert.Equal("True", (string)column.Attribute("FillRows"));
                    }
                    else
                    {
                        Assert.Equal("StackPanel", column.Name.LocalName);
                    }
                    // A group is a panel under its heading, or a group box
                    // that carries the group's name where its board draws no
                    // heading over the cards. Rumble & Haptics is all group
                    // boxes, and so are the sticks, the triggers and the
                    // groups of Motion & Touch and of Lights & Sound.
                    Assert.All(column.Elements(), g => Assert.Contains(g.Name.LocalName, new[] { "StackPanel", "GroupBox" }));
                    if (section == PadViewModel.RumbleSection || section == PadViewModel.MotionSection || section == PadViewModel.LightsSection)
                        Assert.All(column.Elements(), g => Assert.Equal("GroupBox", g.Name.LocalName));
                    if (section == PadViewModel.SticksSection)
                        Assert.Equal(new[] { "GroupBox", "GroupBox", "StackPanel" }, column.Elements().Select(g => g.Name.LocalName));
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
                        // holds the tab control.
                        Assert.Equal("ScrollViewer", content.Name.LocalName);
                        Assert.Equal("MacrosSectionScroll", Name(content));
                        Assert.Equal("{Binding " + sectionKeys[section] + ", Source={x:Static strings:Strings.Instance}}",
                            (string)content.Attribute("AutomationProperties.Name"));
                        Assert.Equal("32,0", (string)content.Attribute("Padding"));
                        Assert.Equal("Disabled", (string)content.Attribute("HorizontalScrollBarVisibility"));
                        var cards = Assert.Single(content.Elements());
                        Assert.Equal("FlexLinePanel", cards.Name.LocalName);
                        Assert.Equal("16", (string)cards.Attribute("Gap"));
                        Assert.Equal("16", (string)cards.Attribute("LineGap"));
                        Assert.Equal(new[] { "MacrosListCard", "MacrosEditorCard" }, cards.Elements().Select(Name));
                        var lists = Named(page, "MacrosListCard");
                        var editor = Named(page, "MacrosEditorCard");
                        string Flex(XElement e, string property)
                            => (string)e.Attributes().First(a => a.Name.LocalName == "FlexLinePanel." + property);
                        Assert.Equal("250", Flex(lists, "Basis"));
                        Assert.Equal("250", (string)lists.Attribute("MinWidth"));
                        Assert.Equal("320", (string)lists.Attribute("MaxWidth"));
                        Assert.Equal("480", Flex(editor, "Basis"));
                        Assert.Equal("999", Flex(editor, "Grow"));
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
        /// group. A group whose board draws no heading over its cards (every
        /// group of Rumble and Haptics, the sticks, the triggers) is a group
        /// box that carries the id and the tab's name itself, and its cards
        /// are all it holds. A group of Motion and Touch or of Lights and
        /// Sound is a group box with the small label its board draws over
        /// its cards: the label carries the id, and the group's paragraph
        /// is the label's tooltip.</summary>
        [Fact]
        public void EveryTabKeepsItsId()
        {
            var page = Page();
            foreach (var (tab, (name, id)) in Tabs.Select(p => (p.Key, p.Value)))
            {
                var carrier = Assert.Single(page.Descendants(), e => Id(e) == id);
                var named = Named(page, name);
                if (named.Name.LocalName == "GroupBox"
                    && PadViewModel.SectionOfTab(tab) is PadViewModel.MotionSection or PadViewModel.LightsSection)
                {
                    Assert.Equal("{StaticResource BareGroupBox}", (string)named.Attribute("Style"));
                    var panel = Assert.Single(named.Elements());
                    Assert.Equal("StackPanel", panel.Name.LocalName);
                    // The label is the group's first element and the group
                    // box takes its name from the same string.
                    Assert.Same(panel.Elements().First(), carrier);
                    Assert.Equal("TextBlock", carrier.Name.LocalName);
                    Assert.Equal("{StaticResource SectionTitle}", (string)carrier.Attribute("Style"));
                    Assert.Equal((string)carrier.Attribute("Text"), (string)named.Attribute("AutomationProperties.Name"));
                    // The tooltip is the paragraph's binding, or, for
                    // Lighting, a tooltip that holds the paragraphs code
                    // shows by what the selected device is.
                    string tip = (string)carrier.Attribute("ToolTip")
                        ?? string.Concat(carrier.Elements().Where(e => e.Name.LocalName == "TextBlock.ToolTip").Select(e => e.ToString()));
                    Assert.Matches(@"\{Binding Pad_\w+_Subtitle, Source=\{x:Static strings:Strings\.Instance\}\}", tip);
                    // No large heading and no paragraph over the cards: the
                    // one text a group may hold outside a card, after its
                    // label, is a status line that a binding fills.
                    Assert.DoesNotContain(panel.Elements(), e => ((string)e.Attribute("Style") ?? "").Contains("TitleTextBlockStyle"));
                    Assert.All(panel.Elements().Skip(1).Where(e => e.Name.LocalName == "TextBlock"), line =>
                        Assert.True(((string)line.Attribute("Text") ?? "").StartsWith("{Binding GameMod", StringComparison.Ordinal),
                            name + " holds a text outside its cards: " + (string)line.Attribute("Text")));
                }
                else if (named.Name.LocalName == "GroupBox" && tab != PadViewModel.OutputTabIndex)
                {
                    Assert.True(PadViewModel.IsStackedSection(PadViewModel.SectionOfTab(tab)));
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
                    Assert.DoesNotContain(panel.Elements(), e => ((string)e.Attribute("Style") ?? "").Contains("TitleTextBlockStyle"));
                }
                else if (PadViewModel.IsStackedSection(PadViewModel.SectionOfTab(tab)))
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
            Assert.Equal("{Binding SelectedSlotSummary}", (string)power.Attribute("DataContext"));
            Assert.Equal("{Binding IsEnabled}", (string)power.Attribute("IsOn"));
            Assert.Equal("SlotPower_Click", (string)power.Attribute("Click"));
            Assert.Contains(Named(page, "SlotPowerHost"), power.Ancestors());
            // The host the window binds starts with no data. Left to
            // inherit, it would take the page's view model, which has no
            // SelectedSlotSummary, and fail every binding until the window
            // hands over the dashboard.
            Assert.Equal("{x:Null}", (string)Named(page, "SlotPowerHost").Attribute("DataContext"));
            Assert.DoesNotContain("SelectedSlotSummary", ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "PadViewModel.cs"));
            var word = Named(page, "SlotStateWord");
            Assert.Contains(power, word.Ancestors());
            Assert.Equal("{Binding StatusText}", (string)word.Attribute("Text"));
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
            // A pill shows what the device's line on the Home card shows:
            // its kind's glyph, its name, its Bluetooth mark and its battery.
            Assert.Equal(new[] { "{Binding TypeGlyph}", "{Binding Name}", "{Binding TransportGlyph}", "{Binding BatteryText}" },
                Assert.Single(pills.Descendants(), e => e.Name.LocalName == "DataTemplate")
                    .Descendants().Select(e => (string)e.Attribute("Text")).Where(t => t != null));
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
            Assert.Equal("{Binding Common_More" + s, (string)more.Attribute("AutomationProperties.Name"));
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
        /// small graph for an axis: each axis has its chart under More.</summary>
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
            var box = Style("DzValueEdit");
            Assert.Equal("46", Setter(box, "Width"));
            Assert.Equal("{DynamicResource SteelFieldBrush}", Setter(box, "Background"));
            Assert.Equal("Right", Setter(box, "TextAlignment"));
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

            // The stick card: a title row, the two columns, then More.
            var more = Assert.Single(page.Descendants(), e => Id(e) == "Pad_More_Stick");
            var card = more.Parent!;
            Assert.Equal("StackPanel", card.Name.LocalName);
            Assert.Equal("{StaticResource CardBorder}", (string)card.Parent!.Attribute("Style"));
            Assert.Equal(new[] { "DockPanel", "Grid", "MoreRow" }, card.Elements().Select(e => e.Name.LocalName));
            var columns = card.Elements().ElementAt(1);
            var left = Assert.Single(columns.Elements(), e => (string)e.Attribute("Grid.Column") == "0");
            var view = Assert.Single(columns.Elements(), e => (string)e.Attribute("Grid.Column") == "1");
            Assert.Contains(left.Descendants(), e => Id(e) == "SensitivityBothChart");
            Assert.DoesNotContain(view.Descendants(), e => e.Name.LocalName == "CurveEditor");
            Assert.DoesNotContain("ChartSize=\"96\"", PageText());

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

            // A trigger's card: its three settings, the chart with its hint
            // under it, and no More row.
            var triggers = Named(page, "TabTriggers");
            Assert.DoesNotContain(triggers.Descendants(), e => e.Name.LocalName == "MoreRow");
            Assert.DoesNotContain(triggers.Descendants(), e => e.Name.LocalName == "Separator");
            var preset = Assert.Single(triggers.Descendants(), e => Id(e) == "TriggerPresetCombo");
            Assert.Equal("{Binding Macro_Sensitivity, Source={x:Static strings:Strings.Instance}}",
                (string)preset.Parent!.Elements().First().Attribute("Text"));
            var chart = Assert.Single(triggers.Descendants(), e => e.Name.LocalName == "CurveEditor");
            Assert.Equal("{Binding Pad_SensitivityCurveHintShort, Source={x:Static strings:Strings.Instance}}",
                (string)chart.ElementsAfterSelf().First().Attribute("Text"));
        }

        /// <summary>Motion and Touch as its board draws it. The section has
        /// row styles of its own that shadow the page's: a label column 170
        /// wide, a slider with no width to stop at, and cards 12 apart.
        /// Every row grows its slider and holds its value in a column 66
        /// wide, so the sliders of a card end at one line and the numbers
        /// end against the resets. A row that belongs to the row above has
        /// its label set in, and no row is set in itself. A card's title
        /// has no icon. A picker in a row gives way in a narrow column.</summary>
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
            var card = Style("CardBorder");
            Assert.Equal("{StaticResource CardBorder}", (string)card.Attribute("BasedOn"));
            Assert.Equal("0,0,0,12", Setter(card, "Margin"));
            var lists = scroll.Descendants().Where(e => e.Name.LocalName == "FoldList").ToList();
            Assert.Equal(2, lists.Count);
            Assert.All(lists, l => Assert.Equal("12", (string)l.Attribute("Gap")));

            // Every row of the section.
            var rows = scroll.Descendants().Where(e => e.Name.LocalName == "SettingRow").ToList();
            Assert.True(rows.Count > 80, "the section has " + rows.Count + " rows");
            Assert.All(rows, r =>
            {
                Assert.Equal("True", (string)r.Attribute("Grow"));
                Assert.Equal("66", (string)r.Attribute("ValueWidth"));
                Assert.Null(r.Attribute("Margin"));
            });
            // A row opens on its label, in the section's column. The two
            // rows of live readings do too.
            Assert.All(rows, r =>
            {
                var first = r.Elements().First(e => !e.Name.LocalName.Contains('.'));
                Assert.Equal("TextBlock", first.Name.LocalName);
                Assert.Contains((string)first.Attribute("Style"), new[] { "{StaticResource DzLabel}", "{StaticResource DzSubLabel}" });
            });
            // No slider keeps a width of its own, and a value typed in
            // place wears the page's value style.
            Assert.All(rows.SelectMany(r => r.Elements()).Where(e => e.Name.LocalName == "Slider"),
                s => Assert.Null(s.Attribute("Width")));
            Assert.All(rows.Where(r => r.Elements().Any(e => e.Name.LocalName == "Slider"))
                .SelectMany(r => r.Elements()).Where(e => e.Name.LocalName == "TextBox"),
                b => Assert.Equal("{StaticResource DzValueEdit}", (string)b.Attribute("Style")));
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
            });

            // A card's title row holds its title and its Reset All, and no
            // icon. Nothing in the section is the old large heading.
            var titles = scroll.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource CardTitle}").ToList();
            Assert.Equal(5, titles.Count);
            Assert.All(titles, title => Assert.DoesNotContain(title.Parent!.Elements(), e => e.Name.LocalName == "GlyphText"));
            Assert.DoesNotContain(scroll.Descendants(), e => ((string)e.Attribute("Style") ?? "").Contains("TitleTextBlockStyle"));

            // Calibration: its two buttons and when it was last done on one
            // line that wraps, and the live readings under More, three to a
            // row that wraps too.
            var calibration = Assert.Single(Named(page, "GyroCalibrationCard").Elements());
            Assert.Equal(new[] { "DockPanel", "WrapPanel", "MoreRow" }, calibration.Elements().Select(e => e.Name.LocalName));
            var line = calibration.Elements().ElementAt(1);
            Assert.Equal(new[] { "{Binding GyroCalibrateCommand}", "{Binding GyroResetCalibrationCommand}" },
                line.Elements().Where(e => e.Name.LocalName == "Button").Select(b => (string)b.Attribute("Command")));
            Assert.Equal("{StaticResource ResetAllButton}", (string)line.Elements().ElementAt(1).Attribute("Style"));
            var readings = Named(page, "GyroCalibrationMore").Descendants().Where(e => e.Name.LocalName == "WrapPanel").ToList();
            Assert.Equal(2, readings.Count);
            Assert.All(readings, w =>
            {
                Assert.Contains(w.Attributes(), a => a.Name.LocalName == "SettingRow.Fills" && a.Value == "True");
                Assert.Equal(3, w.Elements().Count());
                Assert.All(w.Elements(), r => Assert.Equal("{StaticResource LiveReading}", (string)r.Attribute("Style")));
            });
            Assert.Equal("120", Setter(Style("LiveReading"), "Width"));

            // Sensitivity's More: a row for each axis to invert, each with
            // its own switch and reset.
            var more = Named(page, "GyroSensitivityMore");
            foreach (string axis in new[] { "Pitch", "Yaw", "Roll" })
            {
                var toggle = Assert.Single(more.Descendants(), e => e.Name.LocalName == "CheckBox"
                    && (string)e.Attribute("IsChecked") == "{Binding GyroInvert" + axis + ", Mode=TwoWay}");
                Assert.Equal("{StaticResource SwitchCheckBox}", (string)toggle.Attribute("Style"));
                Assert.Single(toggle.Parent!.Elements(), e => e.Name.LocalName == "CheckBox");
                Assert.Equal("{Binding ResetGyroInvert" + axis + "Command}",
                    (string)Assert.Single(toggle.Parent.Elements(), e => e.Name.LocalName == "Button").Attribute("Command"));
            }

            // Grip is the first line of the gyro's list. Its line ends with
            // the grip that is set, by the name the picker shows for it.
            var grip = Named(page, "GyroGripCard");
            Assert.Same(lists[0].Elements().First(), grip);
            var summary = Assert.Single(Assert.Single(grip.Elements(), e => e.Name.LocalName == "FoldCard.Summary").Elements());
            Assert.Equal("{StaticResource OptionTextConverter}", (string)summary.Attribute("Converter"));
            Assert.Equal(new[] { "MotionGrip", "MotionGripOptions", "Pad_Gyro_Grip_Header" },
                summary.Elements().Select(b => (string)b.Attribute("Path")));
            Assert.Contains(grip.Descendants(), e => (string)e.Attribute("SelectedValue") == "{Binding MotionGrip, Mode=TwoWay}");

            // Mouse Output's paragraph is its title's tooltip.
            var mouse = titles.Single(e => ((string)e.Attribute("Text")).Contains("Pad_Touchpad_Mouse_Header"));
            Assert.Contains("Pad_Touchpad_Mouse_Subtitle", (string)mouse.Attribute("ToolTip"));
            Assert.DoesNotContain(scroll.Descendants(), e => e.Name.LocalName == "TextBlock"
                && ((string)e.Attribute("Text") ?? "").Contains("Pad_Touchpad_Mouse_Subtitle"));
        }

        /// <summary>Lights and Sound as its board draws it. The section has
        /// the row styles Motion and Touch has: a label column 170 wide, a
        /// slider with no width to stop at, cards 12 apart, and every
        /// setting row grows its slider and holds its value in a column 66
        /// wide. A color's R, G and B rows keep the tight styles that fit
        /// them under a color picker, and a cell of the equalizer's table
        /// is no setting row. A card's title has no icon, and a card's
        /// paragraph is its title's tooltip. Lighting Control is a line of
        /// the list under the cards, after Indicator LEDs.</summary>
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
            Assert.Equal("0,0,0,12", Setter(Style("CardBorder"), "Margin"));
            var lists = scroll.Descendants().Where(e => e.Name.LocalName == "FoldList").ToList();
            Assert.Equal(2, lists.Count);
            Assert.All(lists, l => Assert.Equal("12", (string)l.Attribute("Gap")));

            // The rows. A color's three and the table's cell stand apart.
            var all = scroll.Descendants().Where(e => e.Name.LocalName == "SettingRow").ToList();
            bool Color(XElement r) => r.Elements().Any(e => (string)e.Attribute("Style") == "{StaticResource DzLabelTight}");
            bool Cell(XElement r) => r.Attribute("Grid.Column") != null;
            var rows = all.Where(r => !Color(r) && !Cell(r)).ToList();
            Assert.Equal(21, all.Count(Color));
            Assert.Equal(1, all.Count(Cell));
            Assert.True(rows.Count > 45, "the section has " + rows.Count + " setting rows");
            Assert.All(rows, r =>
            {
                Assert.Equal("True", (string)r.Attribute("Grow"));
                Assert.Equal("66", (string)r.Attribute("ValueWidth"));
                Assert.Null(r.Attribute("Margin"));
                var first = r.Elements().First(e => !e.Name.LocalName.Contains('.'));
                Assert.Equal("TextBlock", first.Name.LocalName);
                Assert.Contains((string)first.Attribute("Style"), new[] { "{StaticResource DzLabel}", "{StaticResource DzSubLabel}" });
            });
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
            // No slider of a setting row keeps a width of its own, and a
            // picker or a number box gives way.
            Assert.All(rows.SelectMany(r => r.Elements()).Where(e => e.Name.LocalName == "Slider"),
                s => Assert.Null(s.Attribute("Width")));
            var pickers = rows.Where(r => r.Elements().All(e => e.Name.LocalName != "Slider"))
                .SelectMany(r => r.Elements()).Where(e => e.Name.LocalName is "ComboBox" or "NumberBox").ToList();
            Assert.True(pickers.Count >= 17, pickers.Count + " pickers");
            Assert.All(pickers, c =>
            {
                Assert.Null(c.Attribute("Width"));
                Assert.Null(c.Attribute("MinWidth"));
                Assert.NotNull(c.Attribute("MaxWidth"));
                Assert.Contains(c.Attributes(), a => a.Name.LocalName == "SettingRow.Fills" && a.Value == "True");
            });

            // A card's title row: the title and its Reset All, no icon. A
            // card's paragraph is its title's tooltip.
            var titles = scroll.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource CardTitle}").ToList();
            Assert.Equal(5, titles.Count);
            Assert.All(titles, title => Assert.DoesNotContain(title.Parent!.Elements(), e => e.Name.LocalName == "GlyphText"));
            Assert.DoesNotContain(scroll.Descendants(), e => ((string)e.Attribute("Style") ?? "").Contains("TitleTextBlockStyle"));
            foreach (var (title, paragraph) in new[]
            {
                ("Pad_Lighting_LedStrips", "Pad_Lighting_LedStrips_Description"),
                ("Pad_Audio_Output_Header", "Pad_Audio_Output_Desc"),
                ("Pad_Audio_Macros_Header", "Pad_Audio_Macros_Desc"),
            })
            {
                var block = titles.Single(e => ((string)e.Attribute("Text")).Contains(title + ","));
                Assert.Contains(paragraph + ",", (string)block.Attribute("ToolTip"));
                Assert.DoesNotContain(scroll.Descendants(), e => e.Name.LocalName == "TextBlock"
                    && ((string)e.Attribute("Text") ?? "").Contains(paragraph + ","));
            }

            // Lighting: its label's tooltip holds the group's three
            // paragraphs, which code shows by the device, and the route
            // line is the one text of the peripheral's panel.
            var label = Named(page, "LightingGroupLabel");
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

            // The list under the cards: Indicator LEDs, Lighting Control,
            // the Guide button's LED.
            Assert.Equal(new[] { "IndicatorLedsCard", "PeripheralLightingCard", "GuideLedCard" },
                lists[0].Elements().Select(e => (string)e.Attribute(X + "Name")));
            Assert.Same(Named(page, "TabLighting"), lists[0].Ancestors().First(a => a.Name.LocalName == "GroupBox"));

            // A mode's color blocks wrap: two for Battery and three for the
            // audio bands stood in a Grid of fixed columns, which needs 460
            // and 694 where a card in a narrow column has 388 inside.
            var lightbar = Named(page, "LightbarModeCard");
            var wraps = lightbar.Descendants().Where(e => e.Name.LocalName == "WrapPanel"
                && e.Elements().Any(b => b.Descendants().Any(d => d.Name.LocalName == "ColorPickerControl"))).ToList();
            Assert.Equal(new[] { 2, 3 }, wraps.Select(w => w.Elements().Count()));
            Assert.All(wraps.SelectMany(w => w.Elements()), block =>
            {
                Assert.Equal("Border", block.Name.LocalName);
                Assert.Equal("0,0,8,8", (string)block.Attribute("Margin"));
                Assert.Null(block.Attribute("Grid.Column"));
            });
            Assert.DoesNotContain(lightbar.Descendants(), e => e.Name.LocalName == "Grid"
                && e.Elements().Count(b => b.Name.LocalName == "Border" && b.Descendants().Any(d => d.Name.LocalName == "ColorPickerControl")) > 1);

            // The mirror switch's long label has the room its board gives it.
            var mirror = scroll.Descendants().Single(e => e.Name.LocalName == "TextBlock"
                && ((string)e.Attribute("Text") ?? "").Contains("Pad_Audio_Mirror,"));
            Assert.Equal("290", (string)mirror.Attribute("Width"));
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
            // picks a slot's device among the Driven by pills, each by an
            // id. The card's title was the word "Slot" until Home, and the
            // script kept looking for the word.
            Assert.Contains("Find-UIA -Parent $Card -Aid \"Dash_SlotTitle\"", script);
            Assert.DoesNotContain("-eq 'Slot'", script);
            Assert.Contains("AutomationProperties.AutomationId=\"Dash_SlotTitle\"",
                ExtrasPageTests.RepoText("PadForge.App", "Views", "HomePage.xaml"));
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
