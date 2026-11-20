using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The mapping list's rows (#574). Every row is one line, open or
    /// closed: the output's name, a cell under each device's column, then
    /// how its sources combine, Clear and a chevron. A gamepad's rows stand
    /// under a band for each kind of output, and a band folds its rows. The
    /// list's columns, the open row's lines and the bar under the picture
    /// are held in <see cref="PadMappingLinesTests"/>.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PadListRowsTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string S = ", Source={x:Static strings:Strings.Instance}}";

        private static string PageText() => ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml");

        private static string CodeBehind() => ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");

        private static XDocument Page() => XDocument.Parse(PageText());

        private static string Name(XElement e) => (string)e.Attribute(X + "Name");

        private static XElement Named(XContainer scope, string name)
            => Assert.Single(scope.Descendants(), e => Name(e) == name);

        /// <summary>The page's own resource of a key. The Macros and Menus
        /// section defines some of the keys again for its rows.</summary>
        private static XElement Keyed(XDocument page, string key)
            => Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Key") == key
                && !e.Ancestors().Any(a => Name(a) == "MacrosSectionScroll"));

        private static string SetterOf(XElement style, string property)
            => (string)Assert.Single(style.Elements(), e => e.Name.LocalName == "Setter"
                && (string)e.Attribute("Property") == property).Attribute("Value");

        // ── the groups ─────────────────────────────────────────────────

        private static ICollectionView Rows(PadViewModel vm) => CollectionViewSource.GetDefaultView(vm.Mappings);

        private static List<MappingCategory> GroupNames(PadViewModel vm)
            => Rows(vm).Groups?.Cast<CollectionViewGroup>().Select(g => (MappingCategory)g.Name).ToList()
               ?? new List<MappingCategory>();

        /// <summary>A gamepad's list stands under a band for each kind of
        /// output, in the rows' own order, with every row in it. The rows
        /// under the picture are one control's and have no bands.</summary>
        [Fact]
        public void AGamepadsListIsGroupedByKind_AndThePicturesRowsAreNot()
        {
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            Assert.True(vm.IsButtonsPictureView);
            Assert.False(vm.ListIsGrouped);
            Assert.Empty(Rows(vm).GroupDescriptions);

            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.True(vm.ListIsGrouped);
            var by = Assert.IsType<PropertyGroupDescription>(Assert.Single(Rows(vm).GroupDescriptions));
            Assert.Equal(nameof(MappingItem.Category), by.PropertyName);
            Assert.Equal(new[]
            {
                MappingCategory.Buttons, MappingCategory.DPad, MappingCategory.Triggers,
                MappingCategory.LeftStick, MappingCategory.RightStick,
            }, GroupNames(vm));
            Assert.Equal(vm.Mappings.Count, Rows(vm).Cast<MappingItem>().Count());
            foreach (CollectionViewGroup group in Rows(vm).Groups)
                Assert.Equal(vm.Mappings.Count(m => m.Category == (MappingCategory)group.Name), group.ItemCount);

            // Applying the rows again leaves the groups as they stand.
            vm.ApplyMappingPickerFilter();
            Assert.Single(Rows(vm).GroupDescriptions);

            // The picture again: one control's rows, no bands.
            vm.PickedTarget = vm.Mappings.First(m => m.Category == MappingCategory.DPad).TargetSettingName;
            vm.SelectedConfigTab = PadViewModel.PictureTabIndex;
            Assert.Empty(Rows(vm).GroupDescriptions);
            Assert.Equal(4, Rows(vm).Cast<MappingItem>().Count());
        }

        /// <summary>The bands are the gamepad grid's. The other types have
        /// row sets of their own, whose boards give them their groups, so
        /// their lists stay flat here: a MIDI slot's notes are Buttons by
        /// category and its control changes Triggers, which would be the
        /// wrong words over them.</summary>
        [Theory]
        [InlineData(VirtualControllerType.Xbox, true)]
        [InlineData(VirtualControllerType.PlayStation, true)]
        [InlineData(VirtualControllerType.Extended, false)]
        [InlineData(VirtualControllerType.Nintendo, false)]
        [InlineData(VirtualControllerType.KeyboardMouse, false)]
        [InlineData(VirtualControllerType.Midi, false)]
        [InlineData(VirtualControllerType.Vr, false)]
        public void OnlyTheGamepadGridIsGrouped(VirtualControllerType type, bool grouped)
        {
            Assert.Equal(grouped, PadViewModel.HasGamepadRows(type));

            var vm = new PadViewModel(0) { OutputType = type };
            vm.RebuildMappings();
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            vm.ApplyMappingPickerFilter();
            Assert.Equal(grouped, vm.ListIsGrouped);
            Assert.Equal(grouped ? 1 : 0, Rows(vm).GroupDescriptions.Count);
        }

        /// <summary>Every kind of output the enum has is one of the seven
        /// names the band's title picks from, in the enum's order: a new
        /// kind with no name would read as nothing.</summary>
        [Fact]
        public void EveryKindOfOutputHasAName()
        {
            Assert.Equal(new[] { "Buttons", "DPad", "Triggers", "LeftStick", "RightStick", "Touchpad", "Motion" },
                Enum.GetNames(typeof(MappingCategory)));
            Assert.Equal(Enumerable.Range(0, 7),
                Enum.GetValues(typeof(MappingCategory)).Cast<MappingCategory>().Select(c => (int)c));

            var title = Named(Page(), "ListGroupTitle");
            var names = Assert.Single(title.Descendants(), e => e.Name.LocalName == "MultiBinding");
            Assert.Equal("{StaticResource IndexTextConverter}", (string)names.Attribute("Converter"));
            var bindings = names.Elements().ToList();
            Assert.Equal("Name", (string)bindings[0].Attribute("Path"));
            Assert.Equal(new[]
            {
                "Preview_Buttons", "Preview_DPad", "Pad_Triggers", "Menu_Host_LeftStick", "Menu_Host_RightStick",
                "Pad_Touchpad", "Pad_Band_Motion",
            }, bindings.Skip(1).Select(b => (string)b.Attribute("Path")));
            Assert.All(bindings.Skip(1), b => Assert.Equal("{x:Static strings:Strings.Instance}", (string)b.Attribute("Source")));
        }

        /// <summary>Rows built again while a control records: the row that
        /// records is still the one that flashes, and no other.</summary>
        [Fact]
        public void ARowBuiltWhileARecordingRunsIsFlaggedFromTheTarget()
        {
            var vm = new PadViewModel(0);
            vm.RebuildMappings();
            vm.CurrentRecordingTarget = "ButtonA";
            Assert.True(vm.Mappings.First(m => m.TargetSettingName == "ButtonA").IsRecordTarget);

            vm.RebuildMappings();
            Assert.Equal(new[] { "ButtonA" }, vm.Mappings.Where(m => m.IsRecordTarget).Select(m => m.TargetSettingName));
            vm.CurrentRecordingTarget = null;
            Assert.DoesNotContain(vm.Mappings, m => m.IsRecordTarget);

            // No recording: rows built again flash nothing.
            vm.RebuildMappings();
            Assert.DoesNotContain(vm.Mappings, m => m.IsRecordTarget);
        }

        /// <summary>A row the list no longer holds has no owner, whether
        /// the rows were built again or the one row was taken out.</summary>
        [Fact]
        public void ARowTheListDropsLetsGoOfItsOwner()
        {
            var vm = new PadViewModel(0);
            vm.RebuildMappings();
            var built = vm.Mappings.First(m => m.TargetSettingName == "ButtonA");
            Assert.Same(vm, built.Owner);

            vm.RebuildMappings();
            Assert.Null(built.Owner);
            Assert.All(vm.Mappings, m => Assert.Same(vm, m.Owner));

            var taken = vm.Mappings[0];
            vm.Mappings.RemoveAt(0);
            Assert.Null(taken.Owner);
        }

        /// <summary>A group's fold is kept by category on the view model,
        /// so it holds when the table builds its groups again. The two
        /// sticks' groups start folded, as the List board opens (#574), and
        /// every other group starts open.</summary>
        [Fact]
        public void AGroupsFoldIsKeptByCategory()
        {
            var vm = new PadViewModel(0);
            foreach (MappingCategory category in Enum.GetValues(typeof(MappingCategory)))
                Assert.Equal(category is not (MappingCategory.LeftStick or MappingCategory.RightStick), vm.IsListGroupOpen(category));
            vm.SetListGroupOpen(MappingCategory.LeftStick, true);
            Assert.True(vm.IsListGroupOpen(MappingCategory.LeftStick));

            vm.SetListGroupOpen(MappingCategory.DPad, false);
            Assert.False(vm.IsListGroupOpen(MappingCategory.DPad));
            Assert.True(vm.IsListGroupOpen(MappingCategory.Buttons));

            // The rows are built again, and the view with them.
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            vm.RebuildMappings();
            Assert.False(vm.IsListGroupOpen(MappingCategory.DPad));

            vm.SetListGroupOpen(MappingCategory.DPad, true);
            vm.SetListGroupOpen(MappingCategory.DPad, true);
            Assert.True(vm.IsListGroupOpen(MappingCategory.DPad));
        }

        /// <summary>A group is a band with its name and how many outputs it
        /// holds, over its rows. It is an Expander, which keeps the rows
        /// virtualized while the table is grouped and tells a screen reader
        /// whether the group is open, and the page's code keeps it in step
        /// with the view model.</summary>
        [Fact]
        public void AGroupIsABandOverItsRows()
        {
            var page = Page();
            var table = Named(page, "MappingDataGrid");
            Assert.Equal("True", (string)table.Attribute("VirtualizingPanel.IsVirtualizingWhenGrouping"));

            var style = Assert.Single(table.Elements(), e => e.Name.LocalName == "DataGrid.GroupStyle");
            var group = Assert.Single(style.Descendants(), e => e.Name.LocalName == "Expander");
            Assert.Equal("True", (string)group.Attribute("IsExpanded"));
            Assert.Equal("ListGroup_Synced", (string)group.Attribute("Loaded"));
            Assert.Equal("ListGroup_ContextChanged", (string)group.Attribute("DataContextChanged"));
            Assert.Equal("ListGroup_Toggled", (string)group.Attribute("Expanded"));
            Assert.Equal("ListGroup_Toggled", (string)group.Attribute("Collapsed"));
            Assert.Equal("{Binding Text, ElementName=ListGroupTitle, TargetNullValue=''}",
                (string)group.Attribute("AutomationProperties.Name"));
            Assert.Single(group.Elements(), e => e.Name.LocalName == "ItemsPresenter");

            // The band: the board's 36 with 16 at each side, a step under
            // the card's fill, a hairline over it. It opens and folds the
            // group, and the rows show while the group is open.
            var band = Named(style, "Band");
            Assert.Equal("ToggleButton", band.Name.LocalName);
            Assert.Equal("Stretch", (string)band.Attribute("HorizontalAlignment"));
            Assert.Equal("{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}",
                (string)band.Attribute("IsChecked"));
            var fill = Assert.Single(band.Descendants(), e => e.Name.LocalName == "Border");
            Assert.Equal("36", (string)fill.Attribute("MinHeight"));
            Assert.Equal("16,0", (string)fill.Attribute("Padding"));
            Assert.Equal("{DynamicResource SteelBandBrush}", (string)fill.Attribute("Background"));
            Assert.Equal("{DynamicResource SteelLineSoftBrush}", (string)fill.Attribute("BorderBrush"));
            Assert.Equal("0,1,0,0", (string)fill.Attribute("BorderThickness"));
            var rows = Named(style, "Rows");
            Assert.Equal("Collapsed", (string)rows.Attribute("Visibility"));
            var open = Assert.Single(style.Descendants(), e => e.Name.LocalName == "Trigger"
                && (string)e.Attribute("Property") == "IsExpanded");
            Assert.Equal("True", (string)open.Attribute("Value"));
            Assert.Equal("Rows", (string)Assert.Single(open.Elements()).Attribute("TargetName"));

            // Its title in the secondary color at 13 SemiBold, then the
            // count in the tertiary color.
            var title = Named(style, "ListGroupTitle");
            Assert.Equal("13", (string)title.Attribute("FontSize"));
            Assert.Equal("SemiBold", (string)title.Attribute("FontWeight"));
            Assert.Equal("{DynamicResource TextFillColorSecondaryBrush}", (string)title.Attribute("Foreground"));
            // The count says what it counts, as the List board has it: "11
            // outputs".
            var counted = Assert.Single(style.Descendants(), e => e.Name.LocalName == "Binding"
                && (string)e.Attribute("Path") == "ItemCount");
            var countText = counted.Parent;
            Assert.Equal("{StaticResource OneOrManyFormatConverter}", (string)countText.Attribute("Converter"));
            Assert.Equal(new[] { "ItemCount", "Pad_Band_Count_One_Format", "Pad_Band_Count_Format" }, countText.Elements().Select(b => (string)b.Attribute("Path")));
            var count = countText.Parent.Parent;
            Assert.Equal("TextBlock", count.Name.LocalName);
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)count.Attribute("Foreground"));
            Assert.Equal("13", (string)count.Attribute("FontSize"));
            Assert.Contains("{0}", PadForge.Resources.Strings.Strings.Instance.Pad_Band_Count_Format);
            Assert.Contains("{0}", PadForge.Resources.Strings.Strings.Instance.Pad_Band_Count_One_Format);

            // The band's fill follows the theme.
            Assert.Contains("<SolidColorBrush x:Key=\"SteelBandBrush\" Color=\"#0F1520\"/>",
                ExtrasPageTests.RepoText("PadForge.App", "App.xaml"));
            Assert.Contains("SetBrush(\"SteelBandBrush\", dark ? Color.FromRgb(0x0F, 0x15, 0x20) : Color.FromRgb(0xF3, 0xF4, 0xF7));",
                ExtrasPageTests.RepoText("PadForge.App", "Common", "EmberTheme.cs"));

            // The page's code: an Expander takes its group's state when it
            // comes on the page or is handed another group, gives the view
            // model the state the user sets, and passes over an Expander
            // inside a row. A picked row's folded group opens.
            string code = CodeBehind();
            Assert.Matches(@"private void SyncListGroup\(Expander group\)\s*\{\s*if \(group\?\.DataContext is not CollectionViewGroup \{ Name: MappingCategory category \}\) return;\s*if \(DataContext is not PadViewModel vm\) return;\s*bool open = vm\.IsListGroupOpen\(category\);\s*if \(group\.IsExpanded != open\) group\.IsExpanded = open;", code);
            Assert.Matches(@"private void ListGroup_Toggled\(object sender, RoutedEventArgs e\)\s*\{\s*if \(sender is not Expander group \|\| !ReferenceEquals\(e\.OriginalSource, group\)\) return;\s*if \(group\.DataContext is not CollectionViewGroup \{ Name: MappingCategory category \}\) return;\s*if \(DataContext is PadViewModel vm\) vm\.SetListGroupOpen\(category, group\.IsExpanded\);", code);
            Assert.Matches(@"if \(vm\.ListIsGrouped && !vm\.IsListGroupOpen\(row\.Category\)\)\s*\{\s*vm\.SetListGroupOpen\(row\.Category, true\);\s*SyncLiveListGroups\(\);\s*\}", code);
        }

        // ── a row's one line ───────────────────────────────────────────

        /// <summary>The table fills the card with no column headings and
        /// no grid lines of its own, and a row draws the hairline over
        /// itself, since the table's lines belong to cells and a row has
        /// none.</summary>
        [Fact]
        public void TheTableFillsTheCard_WithNoHeadingsAndTheRowsOwnHairlines()
        {
            var page = Page();
            var table = Named(page, "MappingDataGrid");
            Assert.Equal("Stretch", (string)table.Attribute("HorizontalAlignment"));
            // The margin is the style's: it leaves room at the table's foot
            // for the sideways scroll bar while the cells scroll.
            Assert.Null(table.Attribute("Margin"));
            var tableStyle = Assert.Single(table.Elements(), e => e.Name.LocalName == "DataGrid.Style").Elements().Single();
            Assert.Equal("1,-1,1,1", SetterOf(tableStyle, "Margin"));
            var scrolls = Assert.Single(tableStyle.Descendants(), e => e.Name.LocalName == "DataTrigger"
                && (string)e.Attribute("Binding") == "{Binding ShowListCellsScroll}");
            Assert.Equal("True", (string)scrolls.Attribute("Value"));
            Assert.Equal("1,-1,1,15", SetterOf(scrolls, "Margin"));
            Assert.Equal("None", (string)table.Attribute("HeadersVisibility"));
            Assert.Equal("None", (string)table.Attribute("GridLinesVisibility"));
            Assert.Equal("39", (string)table.Attribute("MinRowHeight"));

            var row = Named(table, "DGR_Border");
            Assert.Equal("{DynamicResource SteelPaneLineBrush}", (string)row.Attribute("BorderBrush"));
            Assert.Equal("0,1,0,0", (string)row.Attribute("BorderThickness"));

            // An open row lies on the card's lifted fill.
            var rowStyle = Assert.Single(table.Elements(), e => e.Name.LocalName == "DataGrid.RowStyle").Elements().Single();
            var selected = Assert.Single(rowStyle.Elements().Single(e => e.Name.LocalName == "Style.Triggers").Elements(),
                e => e.Name.LocalName == "Trigger" && (string)e.Attribute("Property") == "IsSelected");
            Assert.Equal("{DynamicResource SteelCardHoverBrush}", SetterOf(selected, "Background"));
        }

        /// <summary>A row's line, open or closed: the name in a column of
        /// 200 and the 12 after it, a cell under each device's column of
        /// the head, then how the row's sources combine, Clear and the
        /// chevron, 12 apart. The line has no Record: a click on a cell
        /// records, and each source of the open row has its own. The
        /// chevron points up on the open row.</summary>
        [Fact]
        public void ARowsLineIsAsTheListBoardDrawsIt()
        {
            var page = Page();
            var grid = Named(page, "RowLine");
            Assert.Equal("Grid", grid.Name.LocalName);
            // Every row shows it: nothing hides or sizes it by the row's
            // state.
            Assert.Null(grid.Attribute("Visibility"));
            Assert.Null(grid.Attribute("Width"));
            Assert.Equal("38", (string)grid.Attribute("MinHeight"));
            Assert.Equal("16,0,8,0", (string)grid.Attribute("Margin"));
            var columns = grid.Elements().First(e => e.Name.LocalName == "Grid.ColumnDefinitions").Elements().ToList();
            Assert.Equal(new[] { "Auto", "*", "Auto" }, columns.Select(c => (string)c.Attribute("Width")));
            Assert.Equal("212", (string)columns[0].Attribute("MinWidth"));
            Assert.Equal("CompactRowOutput", (string)columns[0].Attribute("SharedSizeGroup"));
            Assert.Equal("ListRowEnd", (string)columns[2].Attribute("SharedSizeGroup"));
            // The name's column and the row's end are as wide in every row
            // as in the list's head, which stands outside the table: the
            // widths are shared across the page's Buttons section.
            Assert.Equal("True", (string)Named(page, "ButtonsPage").Attribute("Grid.IsSharedSizeScope"));
            Assert.Null(Named(page, "MappingDataGrid").Attribute("Grid.IsSharedSizeScope"));

            var parts = grid.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
            Assert.Equal(new[] { "TextBlock", "ItemsControl", "StackPanel" }, parts.Select(e => e.Name.LocalName));
            var name = parts[0];
            Assert.Equal("{Binding TargetLabel}", (string)name.Attribute("Text"));
            Assert.Equal("14", (string)name.Attribute("FontSize"));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", (string)name.Attribute("Foreground"));
            Assert.Equal("0,0,12,0", (string)name.Attribute("Margin"));

            // The cells, one under each column of the head. None takes the
            // keyboard: the row is the Tab stop, and the open row's sources
            // record from the keyboard.
            var cells = parts[1];
            Assert.Equal("RowCells", Name(cells));
            Assert.Equal("1", (string)cells.Attribute("Grid.Column"));
            Assert.Equal("{Binding Cells}", (string)cells.Attribute("ItemsSource"));
            Assert.Equal("False", (string)cells.Attribute("Focusable"));
            Assert.Equal("False", (string)cells.Attribute("IsTabStop"));

            // The row's end: how the sources combine, Clear and the chevron.
            var end = parts[2];
            Assert.Equal("2", (string)end.Attribute("Grid.Column"));
            var ends = end.Elements().ToList();
            Assert.Equal(new[] { "Border", "Button", "Button" }, ends.Select(e => e.Name.LocalName));
            // A closed row whose sources combine names how. The open row
            // offers the choice, so the name goes there.
            var combine = ends[0];
            Assert.Equal("CombineTag", Name(combine));
            Assert.Equal("Collapsed", (string)combine.Attribute("Visibility"));
            Assert.Equal("{Binding Pad_CombineChip_Tooltip" + S, (string)combine.Attribute("ToolTip"));
            Assert.Equal("{Binding CombineModeDisplayName}", (string)Assert.Single(combine.Elements()).Attribute("Text"));
            var template = grid.Ancestors().First(e => e.Name.LocalName == "ControlTemplate");
            var triggers = template.Elements().Single(e => e.Name.LocalName == "ControlTemplate.Triggers").Elements().ToList();
            var says = Assert.Single(triggers, e => e.Name.LocalName == "MultiDataTrigger"
                && e.Elements().Any(s => (string)s.Attribute("TargetName") == "CombineTag"));
            Assert.Equal(new[] { ("{Binding ShowsCombine}", "True"), ("{Binding IsRowSelected}", "False") },
                says.Descendants().Where(e => e.Name.LocalName == "Condition").Select(c => ((string)c.Attribute("Binding"), (string)c.Attribute("Value"))));
            var shown = Assert.Single(says.Elements(), e => e.Name.LocalName == "Setter");
            Assert.Equal(("Visibility", "Visible"), ((string)shown.Attribute("Property"), (string)shown.Attribute("Value")));

            // Clear takes a Tab stop on the open row, and the chevron never
            // does (RowActionButton, 2026-11-13).
            var buttons = ends.Skip(1).ToList();
            Assert.Equal(new[] { "{StaticResource RowActionButton}", "{StaticResource RowIconButton}" },
                buttons.Select(b => (string)b.Attribute("Style")));
            Assert.Equal("{Binding ClearCommand}", (string)buttons[0].Attribute("Command"));
            Assert.Equal("{Binding Common_Clear" + S, (string)buttons[0].Attribute("AutomationProperties.Name"));
            Assert.Equal("MappingRowToggle_Click", (string)buttons[1].Attribute("Click"));
            Assert.Equal("{Binding Pad_HeaderOptions" + S, (string)buttons[1].Attribute("AutomationProperties.Name"));
            // The line has no Record and no meter: a cell records, and the
            // open row shows the live value.
            Assert.DoesNotContain(grid.Descendants(), e => ((string)e.Attribute("Command") ?? "").Contains("Record", StringComparison.Ordinal));
            Assert.DoesNotContain(grid.Descendants(), e => e.Name.LocalName is "ProgressBar" or "LevelBar");
            Assert.DoesNotContain("ValueMeter", PageText());
            // The chevron turns on the open row, which is the selected one.
            var chevron = Named(buttons[1], "RowChevron");
            Assert.Equal("0.5,0.5", (string)chevron.Attribute("RenderTransformOrigin"));
            var open = Assert.Single(triggers, e => e.Name.LocalName == "DataTrigger" && (string)e.Attribute("Binding") == "{Binding IsRowSelected}");
            Assert.Equal("True", (string)open.Attribute("Value"));
            var turn = Assert.Single(open.Elements());
            Assert.Equal("RowChevron", (string)turn.Attribute("TargetName"));
            Assert.Equal("RenderTransform", (string)turn.Attribute("Property"));
            Assert.Equal("180", (string)Assert.Single(turn.Descendants(), e => e.Name.LocalName == "RotateTransform").Attribute("Angle"));

            // The buttons: the quiet reset's size with the glyph in the
            // text color, 12 apart, and no Tab stops on a closed row, since
            // the row is one.
            var icon = Keyed(page, "RowIconButton");
            Assert.Equal("{StaticResource QuietResetButton}", (string)icon.Attribute("BasedOn"));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", SetterOf(icon, "Foreground"));
            Assert.Equal("12,0,0,0", SetterOf(icon, "Margin"));
            Assert.Equal("False", SetterOf(icon, "IsTabStop"));

            // What the row's end says of the row.
            var row = new MappingItem("B", "ButtonB", MappingCategory.Buttons);
            row.LoadDescriptor("Button 1");
            Assert.False(row.ShowsCombine);
            row.ExtraSources.Add(new MappingSourceItem { Kind = "Direct", Descriptor = "Button 2" });
            Assert.True(row.ShowsCombine);
            Assert.NotEqual(string.Empty, row.CombineModeDisplayName);
        }

        // ── the bar under the picture ──────────────────────────────────

        /// <summary>The bar's row is the one the pick names. On a gamepad a
        /// stick's axes and the D-pad's directions are one control with the
        /// group's name, and any other control is its own row. The bar
        /// shows under the picture once a control is picked, none being
        /// picked at the start, and the picked row's lines stand in it.</summary>
        [Fact]
        public void TheBarIsOnThePickedRow()
        {
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            Assert.Null(vm.PickedRow);
            Assert.Equal(string.Empty, vm.PickedName);
            Assert.Equal(string.Empty, vm.PickedKind);
            Assert.False(vm.ShowPictureBar);
            var a = vm.Mappings.First(m => m.TargetSettingName == "ButtonA");
            vm.PickedTarget = "ButtonA";
            Assert.Same(a, vm.PickedRow);
            Assert.Equal(a.TargetLabel, vm.PickedName);
            Assert.True(vm.ShowPictureBar);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            var b = vm.Mappings.First(m => m.TargetSettingName == "ButtonB");
            vm.PickedTarget = "ButtonB";
            Assert.Same(b, vm.PickedRow);
            Assert.Equal(b.TargetLabel, vm.PickedName);
            Assert.True(vm.ShowPictureBar);
            Assert.Contains(nameof(PadViewModel.PickedRow), raised);
            Assert.Contains(nameof(PadViewModel.PickedName), raised);
            Assert.Contains(nameof(PadViewModel.ShowPictureBar), raised);

            var stickY = vm.Mappings.Last(m => m.Category == MappingCategory.LeftStick);
            vm.PickedTarget = stickY.TargetSettingName;
            Assert.Same(stickY, vm.PickedRow);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Menu_Host_LeftStick, vm.PickedName);
            vm.PickedTarget = vm.Mappings.First(m => m.Category == MappingCategory.DPad).TargetSettingName;
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Preview_DPad, vm.PickedName);
            vm.PickedTarget = vm.Mappings.First(m => m.Category == MappingCategory.RightStick).TargetSettingName;
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Menu_Host_RightStick, vm.PickedName);

            // The list has no bar, and the picture has it again.
            raised.Clear();
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.False(vm.ShowPictureBar);
            Assert.Contains(nameof(PadViewModel.ShowPictureBar), raised);
            vm.SelectedConfigTab = PadViewModel.PictureTabIndex;
            Assert.True(vm.ShowPictureBar);

            // With nothing picked the bar has no row and no name, and does
            // not show.
            var held = vm.PickedTarget;
            vm.PickedTarget = null;
            Assert.Null(vm.PickedRow);
            Assert.Equal(string.Empty, vm.PickedName);
            Assert.False(vm.ShowPictureBar);
            vm.PickedTarget = held;
            Assert.True(vm.ShowPictureBar);

            // Rows built again are new objects: the bar is on the new one.
            var before = vm.PickedRow;
            raised.Clear();
            vm.RebuildMappings();
            Assert.NotSame(before, vm.PickedRow);
            Assert.Equal(before.TargetSettingName, vm.PickedRow.TargetSettingName);
            Assert.Contains(nameof(PadViewModel.PickedRow), raised);

            // A type with rows of its own names a control by its row. A
            // MIDI note is headed by its pitch, as its key on the picture
            // says it, and the row's name is the small line under it, as
            // the MIDI board has it.
            vm.OutputType = VirtualControllerType.Midi;
            vm.RebuildMappings();
            var cc = vm.Mappings.First();
            Assert.StartsWith("MidiCC", cc.TargetSettingName);
            Assert.Null(cc.PictureName);
            vm.PickedTarget = cc.TargetSettingName;
            Assert.Equal(cc.TargetLabel, vm.PickedName);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Pad_Kind_ControlChange, vm.PickedKind);
            int first = vm.MidiConfig.GetNoteNumbers()[0];
            var note = vm.Mappings.First(m => m.TargetSettingName == "MidiNote0");
            Assert.Equal("Note " + first, note.TargetLabel);
            Assert.Equal(MidiSlotConfig.PitchName(first), note.PictureName);
            Assert.Equal(note.PictureName, note.BarLabel);
            vm.PickedTarget = note.TargetSettingName;
            Assert.Equal(note.PictureName, vm.PickedName);
            Assert.Equal(note.TargetLabel, vm.PickedKind);
            // The picture's keys and the bar read one pitch name.
            Assert.Equal("C4", MidiSlotConfig.PitchName(60));
            Assert.Equal("C-1", MidiSlotConfig.PitchName(0));
            Assert.Equal("G9", MidiSlotConfig.PitchName(127));
            Assert.Equal("A#3", MidiSlotConfig.PitchName(58));
            Assert.Null(MidiSlotConfig.PitchName(128));
            Assert.Null(MidiSlotConfig.PitchName(-1));
            Assert.Contains("string name = MidiSlotConfig.PitchName(midiNote);",
                ExtrasPageTests.RepoText("PadForge.App", "Views", "MidiPreviewView.xaml.cs"));

            // A pick of none under the picture is none: no row is picked
            // in its place, and the bar goes.
            vm.PickedTarget = null;
            Assert.Null(vm.PickedRow);
            Assert.False(vm.ShowPictureBar);
        }

        /// <summary>A stick's click is a button in the list and one of
        /// its stick's rows under the picture, after the two axes, as the
        /// Buttons board has it. Picked by its click or by an axis, the
        /// stick is one control. The list keeps the rows' own order.</summary>
        [Fact]
        public void AStickClickIsOneOfItsSticksRowsUnderThePicture()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            var click = vm.Mappings.First(m => m.TargetSettingName == "LeftThumbButton");
            var axes = vm.Mappings.Where(m => m.Category == MappingCategory.LeftStick).ToList();
            Assert.Equal(2, axes.Count);
            Assert.Equal(MappingCategory.Buttons, click.Category);
            Assert.Equal(MappingCategory.LeftStick, PadViewModel.FamilyOf(click));
            Assert.Equal(MappingCategory.RightStick,
                PadViewModel.FamilyOf(vm.Mappings.First(m => m.TargetSettingName == "RightThumbButton")));
            Assert.Equal(MappingCategory.DPad, PadViewModel.FamilyOf(vm.Mappings.First(m => m.TargetSettingName == "DPadUp")));
            Assert.Null(PadViewModel.FamilyOf(vm.Mappings.First(m => m.TargetSettingName == "ButtonA")));
            Assert.Null(PadViewModel.FamilyOf(vm.Mappings.First(m => m.TargetSettingName == "LeftTrigger")));
            Assert.Null(PadViewModel.FamilyOf(null));
            Assert.True(PadViewModel.SameControl(click, axes[0]));
            Assert.False(PadViewModel.SameControl(click, vm.Mappings.First(m => m.TargetSettingName == "RightThumbAxisX")));

            // The bar holds the two axes, then the click. The table under
            // the picture holds the same rows in the list's own order.
            var bar = vm.PickedRowsView;
            var table = Assert.IsType<ListCollectionView>(Rows(vm));
            foreach (string pick in new[] { "LeftThumbButton", "LeftThumbAxisY", "LeftThumbAxisXNeg" })
            {
                vm.PickedTarget = null;
                vm.PickedTarget = pick;
                Assert.Equal(s.Menu_Host_LeftStick, vm.PickedName);
                Assert.Equal(s.Pad_Kind_Stick, vm.PickedKind);
                Assert.Equal(new[] { axes[0], axes[1], click }, bar.Cast<MappingItem>());
                Assert.Equal(new[] { click, axes[0], axes[1] }, table.Cast<MappingItem>());
                Assert.Equal(new[] { "LeftThumbAxisX", "LeftThumbAxisY", "LeftThumbButton" }, vm.PickedTargets().OrderBy(t => t, StringComparer.Ordinal));
            }
            Assert.Null(table.CustomSort);

            // The list has every row in its own order, the click among the
            // buttons, and the bar keeps the picked control's rows.
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.Equal(vm.Mappings.Where(m => m.Category == MappingCategory.Buttons),
                table.Cast<MappingItem>().Where(m => m.Category == MappingCategory.Buttons));
            Assert.Equal(new[] { axes[0], axes[1], click }, bar.Cast<MappingItem>());
            vm.SelectedConfigTab = PadViewModel.PictureTabIndex;
            Assert.Equal(new[] { axes[0], axes[1], click }, bar.Cast<MappingItem>());

            // A VR hand's stick holds its click the same way, and says its
            // hand.
            var vr = new PadViewModel(1) { OutputType = VirtualControllerType.Vr };
            vr.RebuildMappings();
            vr.PickedTarget = "VrRStickClick";
            Assert.Equal(s.Menu_Host_RightStick, vr.PickedName);
            Assert.Equal(string.Format(s.Pad_Kind_RightHand_Format, s.Pad_Kind_Stick), vr.PickedKind);
            Assert.Equal(new[] { "VrRStickX", "VrRStickY", "VrRStickClick" },
                vr.PickedRowsView.Cast<MappingItem>().Select(m => m.TargetSettingName));

            // A lettered preset's raw rows say the control each one is, so
            // its stick's click is found by that.
            string stamp = SettingsManager.GetWireStamp(11);
            try
            {
                var sw = new PadViewModel(11) { OutputType = VirtualControllerType.Nintendo };
                sw.RebuildMappings();
                var l3 = Assert.Single(sw.Mappings, m => m.Role == "LeftThumbButton");
                Assert.StartsWith("RawBtn", l3.TargetSettingName);
                Assert.Equal(MappingCategory.LeftStick, PadViewModel.FamilyOf(l3));
                sw.PickedTarget = l3.TargetSettingName;
                Assert.Equal(s.Menu_Host_LeftStick, sw.PickedName);
                Assert.Equal(s.Pad_Kind_Stick, sw.PickedKind);
                Assert.Equal(new[] { "RawAxis0", "RawAxis1", l3.TargetSettingName },
                    sw.PickedRowsView.Cast<MappingItem>().Select(m => m.TargetSettingName));
                Assert.Equal(s.Pad_Kind_Trigger, sw.KindOf(Assert.Single(sw.Mappings, m => m.Role == "LeftTrigger")));
                Assert.Equal(s.Pad_Kind_SystemButton, sw.KindOf(Assert.Single(sw.Mappings, m => m.Role == "ButtonGuide")));
                Assert.Equal(s.Pad_Kind_FaceButton, sw.KindOf(Assert.Single(sw.Mappings, m => m.Role == "ButtonA")));
                sw.PickedTarget = sw.Mappings.First(m => m.Category == MappingCategory.DPad).TargetSettingName;
                Assert.Equal(s.Preview_DPad, sw.PickedName);
                Assert.Equal(s.Pad_Kind_DPad, sw.PickedKind);
            }
            finally
            {
                SettingsManager.StampNintendoWire(11, stamp);
            }

            // A custom Extended slot's control is named by its picked row.
            var custom = new PadViewModel(2) { OutputType = VirtualControllerType.Extended };
            custom.RebuildMappings();
            var stick = custom.Mappings.First(m => m.Category == MappingCategory.LeftStick);
            Assert.NotNull(stick.Control);
            custom.PickedTarget = stick.TargetSettingName;
            Assert.Equal(stick.TargetLabel, custom.PickedName);
            Assert.Equal(s.Pad_Kind_Stick, custom.PickedKind);
        }

        /// <summary>The bar under the picture has a view of its own over
        /// the rows. In the list, where the bar is hidden, it holds the
        /// picked control's rows and no others, so it builds no block for
        /// each of the table's rows: it built 148 on a Keyboard + Mouse slot
        /// while it shared the table's view.</summary>
        [Fact]
        public void TheBarsRowsAreItsOwn_SoTheHiddenBarBuildsNoBlockPerRow()
        {
            var vm = new PadViewModel(0) { OutputType = VirtualControllerType.KeyboardMouse };
            vm.RebuildMappings();
            vm.ApplyMappingPickerFilter();
            // The picture opens with nothing picked, so the bar holds no row.
            Assert.Empty(vm.PickedRowsView.Cast<MappingItem>());
            vm.PickedTarget = "KbmKey57";
            Assert.Equal(new[] { "KbmKey57" }, vm.PickedRowsView.Cast<MappingItem>().Select(m => m.TargetSettingName));
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.True(Rows(vm).Cast<MappingItem>().Count() > 100);
            Assert.Equal(new[] { "KbmKey57" }, vm.PickedRowsView.Cast<MappingItem>().Select(m => m.TargetSettingName));
            // Rows built again: the bar holds the new row.
            vm.SelectedConfigTab = PadViewModel.PictureTabIndex;
            vm.RebuildMappings();
            Assert.Same(vm.PickedRow, Assert.Single(vm.PickedRowsView.Cast<MappingItem>()));
            // With the pick taken away, in the list or under the picture,
            // the bar holds no row.
            vm.PickedTarget = null;
            Assert.Empty(vm.PickedRowsView.Cast<MappingItem>());
            vm.PickedTarget = "KbmKey57";
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            vm.PickedTarget = null;
            Assert.Empty(vm.PickedRowsView.Cast<MappingItem>());
            Assert.Contains("ItemsSource=\"{Binding PickedRowsView}\"", PageText());
        }

        /// <summary>The small line under the picked control's name says
        /// what kind of control it is, in the boards' words. A controller's
        /// rows go by their group, and its buttons by which button each is.
        /// A keyboard and mouse slot's rows and a VR slot's go by their
        /// target, and a VR control says its hand.</summary>
        [Fact]
        public void TheBarSaysWhatKindOfControlIsPicked()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            string Kind(PadViewModel vm, string target) => vm.KindOf(vm.Mappings.First(m => m.TargetSettingName == target));

            var pad = new PadViewModel(0);
            Assert.Equal(string.Empty, pad.KindOf(null));
            // Nothing is picked as the picture opens, so the bar names no
            // kind until a control is picked.
            Assert.Equal(string.Empty, pad.PickedKind);
            pad.PickedTarget = "ButtonA";
            Assert.Equal(s.Pad_Kind_FaceButton, pad.PickedKind);
            Assert.All(new[] { "ButtonA", "ButtonB", "ButtonX", "ButtonY" }, t => Assert.Equal(s.Pad_Kind_FaceButton, Kind(pad, t)));
            Assert.All(new[] { "LeftShoulder", "RightShoulder" }, t => Assert.Equal(s.Pad_Kind_ShoulderButton, Kind(pad, t)));
            Assert.All(new[] { "ButtonBack", "ButtonStart", "ButtonGuide" }, t => Assert.Equal(s.Pad_Kind_SystemButton, Kind(pad, t)));
            Assert.All(new[] { "LeftThumbButton", "RightThumbButton", "LeftThumbAxisX", "RightThumbAxisY" },
                t => Assert.Equal(s.Pad_Kind_Stick, Kind(pad, t)));
            Assert.All(new[] { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" }, t => Assert.Equal(s.Pad_Kind_DPad, Kind(pad, t)));
            Assert.All(new[] { "LeftTrigger", "RightTrigger" }, t => Assert.Equal(s.Pad_Kind_Trigger, Kind(pad, t)));

            // The picked control's kind follows the pick.
            var raised = new List<string>();
            pad.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            pad.PickedTarget = "LeftShoulder";
            Assert.Equal(s.Pad_Kind_ShoulderButton, pad.PickedKind);
            Assert.Contains(nameof(PadViewModel.PickedKind), raised);

            // Rows a row builder makes for some presets only.
            Assert.Equal(s.Pad_Kind_SystemButton, pad.KindOf(new MappingItem("Share", "ButtonShare", MappingCategory.Buttons)));
            Assert.Equal(s.Pad_Kind_SystemButton, pad.KindOf(new MappingItem("Mute", "ButtonMute", MappingCategory.Buttons)));
            Assert.Equal(s.Pad_Kind_Button, pad.KindOf(new MappingItem("Paddle", "LeftPaddle", MappingCategory.Buttons)));
            Assert.Equal(s.Pad_Kind_Touchpad, pad.KindOf(new MappingItem("Click", "TouchpadClick", MappingCategory.Buttons)));
            Assert.Equal(s.Pad_Kind_Touchpad, pad.KindOf(new MappingItem("X", "TouchpadX1", MappingCategory.Touchpad)));
            Assert.Equal(s.Pad_Kind_Motion, pad.KindOf(new MappingItem("Gyro", "MotionGyro", MappingCategory.Motion)));
            // A pressure row reads like a trigger and is no trigger.
            Assert.Equal(string.Empty, pad.KindOf(new MappingItem("Cross", MappingSetMigrator.PressureTargets[0], MappingCategory.Triggers)));
            // A lettered preset's raw rows, by the control each one is.
            Assert.Equal(s.Pad_Kind_Trigger, pad.KindOf(new MappingItem("ZL", "RawBtn6", MappingCategory.Buttons) { Role = "LeftTrigger" }));
            Assert.Equal(s.Pad_Kind_SystemButton, pad.KindOf(new MappingItem("C", "RawBtn20", MappingCategory.Buttons) { Role = "ButtonC" }));
            Assert.Equal(s.Pad_Kind_SystemButton, pad.KindOf(new MappingItem("QAM", "RawBtn9", MappingCategory.Buttons) { Role = "ButtonQuickAccess" }));
            Assert.Equal(s.Pad_Kind_Touchpad, pad.KindOf(new MappingItem("Pad", "RawBtn16", MappingCategory.Buttons) { Role = "LeftTouchpadClick" }));
            Assert.Equal(s.Pad_Kind_Stick, pad.KindOf(new MappingItem("L3", "RawBtn10", MappingCategory.Buttons) { Role = "LeftThumbButton" }));
            Assert.Equal(s.Pad_Kind_Button, pad.KindOf(new MappingItem("5", "RawBtn4", MappingCategory.Buttons)));

            // Keyboard + Mouse: by the target's name.
            var kbm = new PadViewModel(1) { OutputType = VirtualControllerType.KeyboardMouse };
            kbm.RebuildMappings();
            Assert.Equal(s.Pad_Kind_Key, Kind(kbm, "KbmKey57"));
            Assert.Equal(s.Pad_Kind_MouseButton, Kind(kbm, "KbmMBtn0"));
            Assert.Equal(s.Pad_Kind_MouseMovement, Kind(kbm, "KbmMouseX"));
            Assert.All(new[] { "KbmScroll", "KbmScrollH" }, t => Assert.Equal(s.Pad_Kind_ScrollWheel, Kind(kbm, t)));
            // The mouse's two axes are one control's rows, named by the row.
            kbm.PickedTarget = "KbmMouseY";
            Assert.Equal(kbm.PickedRow.TargetLabel, kbm.PickedName);
            Assert.Equal(s.Pad_Kind_MouseMovement, kbm.PickedKind);

            // VR: the kind and the hand.
            var vr = new PadViewModel(2) { OutputType = VirtualControllerType.Vr };
            vr.RebuildMappings();
            Assert.Equal(string.Format(s.Pad_Kind_RightHand_Format, s.Pad_Kind_AnalogPull), Kind(vr, "VrRTrigger"));
            Assert.Equal(string.Format(s.Pad_Kind_LeftHand_Format, s.Pad_Kind_AnalogPull), Kind(vr, "VrLGrip"));
            Assert.Equal(string.Format(s.Pad_Kind_LeftHand_Format, s.Pad_Kind_Button), Kind(vr, "VrLA"));
            Assert.Equal(string.Format(s.Pad_Kind_RightHand_Format, s.Pad_Kind_Button), Kind(vr, "VrRTriggerClick"));
            Assert.Equal(string.Format(s.Pad_Kind_RightHand_Format, s.Pad_Kind_TouchSensor), Kind(vr, "VrRBTouch"));
            Assert.Equal(string.Format(s.Pad_Kind_LeftHand_Format, s.Pad_Kind_Stick), Kind(vr, "VrLStickX"));
            Assert.Equal(string.Format(s.Pad_Kind_LeftHand_Format, s.Pad_Kind_Stick), Kind(vr, "VrLStickClick"));
            Assert.All(vr.Mappings, m => Assert.NotEqual(string.Empty, vr.KindOf(m)));
            Assert.All(new[] { s.Pad_Kind_LeftHand_Format, s.Pad_Kind_RightHand_Format }, f => Assert.Contains("{0}", f));
        }

        /// <summary>The converter that shows a part of a picker's item in
        /// the closed picker only: with Invert, something there is hides
        /// it and nothing shows it.</summary>
        [Fact]
        public void NothingShowsWhatInvertHides()
        {
            var converter = new PadForge.Converters.NullToCollapsedConverter();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal(System.Windows.Visibility.Visible, converter.Convert(new object(), typeof(System.Windows.Visibility), null, culture));
            Assert.Equal(System.Windows.Visibility.Collapsed, converter.Convert(null, typeof(System.Windows.Visibility), null, culture));
            Assert.Equal(System.Windows.Visibility.Collapsed, converter.Convert(new object(), typeof(System.Windows.Visibility), "Invert", culture));
            Assert.Equal(System.Windows.Visibility.Visible, converter.Convert(null, typeof(System.Windows.Visibility), "Invert", culture));
        }

        /// <summary>A click on a closed row opens it, and a click on one of
        /// the line's own buttons leaves it closed: opening the row would
        /// take the button from under the pointer before its click. A
        /// click on a cell leaves it closed too: the cell records. The
        /// chevron opens a closed row and closes the open one.</summary>
        [Fact]
        public void TheLinesButtonsLeaveTheRowClosed()
        {
            string code = CodeBehind();
            Assert.Matches(@"if \(row\.IsSelected \|\| MappingDataGrid == null\) return;[\s\S]{0,400}?if \(IsInsideButton\(e\.OriginalSource as DependencyObject, row\)\) return;[\s\S]{0,300}?if \(IsInsideListCell\(e\.OriginalSource as DependencyObject, row\)\) return;\s*MappingDataGrid\.SelectedItem = mi;", code);
            Assert.Matches(@"private void MappingRowToggle_Click\(object sender, RoutedEventArgs e\)\s*\{\s*if \(MappingDataGrid == null \|\| \(sender as FrameworkElement\)\?\.DataContext is not MappingItem mi\) return;\s*MappingDataGrid\.SelectedItem = ReferenceEquals\(MappingDataGrid\.SelectedItem, mi\) \? null : mi;", code);
            // Enter or Space on a row opens or closes it by selection alone.
            Assert.Matches(@"MappingDataGrid\.SelectedItem = row\.IsSelected \? null : mi;\s*e\.Handled = true;", code);

            RunSta(() =>
            {
                var inButton = new TextBlock();
                var beside = new TextBlock();
                var button = new Button { Content = new Border { Child = inButton } };
                var line = new StackPanel();
                line.Children.Add(beside);
                line.Children.Add(button);
                var row = new Border { Child = line };
                // The row stands in a button of its own here, which is no
                // button of the line's: the walk stops at the row.
                _ = new Button { Content = row };

                Assert.True(PadPage.IsInsideButton(inButton, row));
                Assert.True(PadPage.IsInsideButton(button, row));
                Assert.False(PadPage.IsInsideButton(beside, row));
                Assert.False(PadPage.IsInsideButton(line, row));
                Assert.False(PadPage.IsInsideButton(row, row));
                Assert.False(PadPage.IsInsideButton(null, row));
            });
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
