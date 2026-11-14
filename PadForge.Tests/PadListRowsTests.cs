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
    /// The mapping list's rows as the List board draws them (#574). Every
    /// row that is not open is one line: the output's name, what drives
    /// it, a meter of its live value, then Record, Clear and a chevron. A
    /// gamepad's rows stand under a band for each kind of output, and a
    /// band folds its rows.
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

        /// <summary>The page's own resource of a key. The host of the
        /// options under the bar and the Macros and Menus section define
        /// some of the keys again for their rows (<see cref="KeyedIn"/>).</summary>
        private static XElement Keyed(XDocument page, string key)
            => Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Key") == key
                && !e.Ancestors().Any(a => Name(a) is "PickedOptions" or "MacrosSectionScroll"));

        /// <summary>A resource an element defines for what it holds.</summary>
        private static XElement KeyedIn(XElement host, string key)
            => Assert.Single(Assert.Single(host.Elements(), e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)).Elements(),
                e => (string)e.Attribute(X + "Key") == key);

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
            Assert.Equal("{StaticResource FormatTextConverter}", (string)countText.Attribute("Converter"));
            Assert.Equal(new[] { "ItemCount", "Pad_Band_Count_Format" }, countText.Elements().Select(b => (string)b.Attribute("Path")));
            var count = countText.Parent.Parent;
            Assert.Equal("TextBlock", count.Name.LocalName);
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)count.Attribute("Foreground"));
            Assert.Equal("13", (string)count.Attribute("FontSize"));
            Assert.Contains("{0}", PadForge.Resources.Strings.Strings.Instance.Pad_Band_Count_Format);

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
            Assert.Equal("1,-1,1,1", (string)table.Attribute("Margin"));
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
        /// 200 and the 12 after it, the source as a chip with the device
        /// beside it and the fan-in tail after that, the meter 96 wide on
        /// the row's live level, then Record, Clear and the chevron, 12
        /// apart. The chevron points up on the open row.</summary>
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
            XElement line = grid;
            Assert.Equal("38", (string)grid.Attribute("MinHeight"));
            Assert.Equal("16,0,8,0", (string)grid.Attribute("Margin"));
            var columns = grid.Elements().First(e => e.Name.LocalName == "Grid.ColumnDefinitions").Elements().ToList();
            Assert.Equal(new[] { "Auto", "*", "Auto" }, columns.Select(c => (string)c.Attribute("Width")));
            Assert.Equal("212", (string)columns[0].Attribute("MinWidth"));
            Assert.Equal("CompactRowOutput", (string)columns[0].Attribute("SharedSizeGroup"));
            Assert.Equal("True", (string)Named(page, "MappingDataGrid").Attribute("Grid.IsSharedSizeScope"));

            var name = Assert.Single(grid.Elements(), e => (string)e.Attribute("Text") == "{Binding TargetLabel}");
            Assert.Equal("14", (string)name.Attribute("FontSize"));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", (string)name.Attribute("Foreground"));
            Assert.Equal("0,0,12,0", (string)name.Attribute("Margin"));

            // What drives it. The chip, then the tail docked at the end so
            // it keeps its width, then the device's name, which takes what
            // is left and trims.
            var source = Assert.Single(grid.Elements(), e => e.Name.LocalName == "DockPanel");
            Assert.Equal("1", (string)source.Attribute("Grid.Column"));
            Assert.Equal("Left", (string)source.Attribute("HorizontalAlignment"));
            var parts = source.Elements().ToList();
            Assert.Equal(3, parts.Count);
            Assert.Equal("SourceChip", Name(parts[0]));
            Assert.Equal("26", (string)parts[0].Attribute("MinHeight"));
            Assert.Equal("10,0", (string)parts[0].Attribute("Padding"));
            Assert.Equal("6", (string)parts[0].Attribute("CornerRadius"));
            Assert.Equal("{DynamicResource SteelRaisedBrush}", (string)parts[0].Attribute("Background"));
            Assert.Equal("{DynamicResource SteelPillLineBrush}", (string)parts[0].Attribute("BorderBrush"));
            Assert.Equal("{Binding SourceDisplayText, Mode=OneWay}", (string)Named(parts[0], "SourceChipText").Attribute("Text"));
            Assert.Equal("Right", (string)parts[1].Attribute("DockPanel.Dock"));
            Assert.Contains(parts[1].Descendants(), e => ((string)e.Attribute("Text") ?? "").StartsWith("{Binding ExtraSources.Count", StringComparison.Ordinal));
            Assert.Contains(parts[1].Descendants(), e => ((string)e.Attribute("Text") ?? "").StartsWith("{Binding CombineModeDisplayName", StringComparison.Ordinal));
            Assert.Equal("{Binding PrimarySourceDeviceLabel}", (string)parts[2].Attribute("Text"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)parts[2].Attribute("Foreground"));
            Assert.Equal("CharacterEllipsis", (string)parts[2].Attribute("TextTrimming"));

            // A row nothing drives says so in a quiet chip.
            var template = line.Ancestors().First(e => e.Name.LocalName == "ControlTemplate");
            var unmapped = Assert.Single(template.Elements().Single(e => e.Name.LocalName == "ControlTemplate.Triggers").Elements(),
                e => e.Name.LocalName == "DataTrigger" && (string)e.Attribute("Binding") == "{Binding IsMapped}");
            Assert.Equal("False", (string)unmapped.Attribute("Value"));
            Assert.Equal(new[] { "SourceChip", "SourceChipText" }, unmapped.Elements().Select(s => (string)s.Attribute("TargetName")));

            // The meter and the three buttons.
            var end = Assert.Single(grid.Elements(), e => e.Name.LocalName == "StackPanel");
            Assert.Equal("2", (string)end.Attribute("Grid.Column"));
            var meter = Assert.Single(end.Elements(), e => e.Name.LocalName == "ProgressBar");
            Assert.Equal("{StaticResource ValueMeter}", (string)meter.Attribute("Style"));
            Assert.Equal("96", (string)meter.Attribute("Width"));
            Assert.Equal("12,0,0,0", (string)meter.Attribute("Margin"));
            Assert.Equal("{Binding LiveLevel, Mode=OneWay}", (string)meter.Attribute("Value"));
            Assert.Equal("{Binding Pad_HeaderValue" + S, (string)meter.Attribute("AutomationProperties.Name"));

            var buttons = end.Elements().Where(e => e.Name.LocalName == "Button").ToList();
            Assert.Equal(3, buttons.Count);
            // Record and Clear take a Tab stop on the open row, and the
            // chevron never does (RowActionButton, 2026-11-13).
            Assert.Equal(new[] { "{StaticResource RowActionButton}", "{StaticResource RowActionButton}", "{StaticResource RowIconButton}" },
                buttons.Select(b => (string)b.Attribute("Style")));
            Assert.Equal("{Binding ToggleRecordCommand}", (string)buttons[0].Attribute("Command"));
            Assert.Equal("{Binding IsRecordable}", (string)buttons[0].Attribute("IsEnabled"));
            Assert.Equal("{Binding RecordButtonText, TargetNullValue=''}", (string)buttons[0].Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding ClearCommand}", (string)buttons[1].Attribute("Command"));
            Assert.Equal("{Binding Common_Clear" + S, (string)buttons[1].Attribute("AutomationProperties.Name"));
            Assert.Equal("MappingRowToggle_Click", (string)buttons[2].Attribute("Click"));
            Assert.Equal("{Binding Pad_HeaderOptions" + S, (string)buttons[2].Attribute("AutomationProperties.Name"));
            // The chevron turns on the open row, which is the selected one.
            var chevron = Named(buttons[2], "RowChevron");
            Assert.Equal("0.5,0.5", (string)chevron.Attribute("RenderTransformOrigin"));
            var open = Assert.Single(template.Elements().Single(e => e.Name.LocalName == "ControlTemplate.Triggers").Elements(),
                e => e.Name.LocalName == "DataTrigger" && (string)e.Attribute("Binding") == "{Binding IsRowSelected}");
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

            // The meter: a track 4 tall that is full at 1.
            var value = Keyed(page, "ValueMeter");
            Assert.Equal("{StaticResource ActivityBar}", (string)value.Attribute("BasedOn"));
            Assert.Equal("4", SetterOf(value, "Height"));
            Assert.Equal("1", SetterOf(value, "Maximum"));
        }

        /// <summary>An open row's options stand under its line as the List
        /// board draws them: rows 40 tall, a label 200 wide, then the
        /// control, then its reset. They were the table's cells, and every
        /// one of them keeps its binding, its gate and its reset's id.
        /// Nothing in an open row can run past the card's edge: every strip
        /// of controls wraps. The rows take their look from styles their
        /// host can replace, which is how the bar under the picture draws
        /// the same rows its board's way.</summary>
        [Fact]
        public void AnOpenRowsOptionsAreLabelRowsUnderItsLine()
        {
            var page = Page();
            var table = Named(page, "MappingDataGrid");

            var rowLine = Keyed(page, "RowOptionLine");
            Assert.Equal("controls:FlexLinePanel", (string)rowLine.Attribute("TargetType"));
            Assert.Equal("40", SetterOf(rowLine, "MinLineHeight"));
            Assert.Equal("0", SetterOf(rowLine, "Gap"));
            Assert.Equal("0", SetterOf(rowLine, "LineGap"));
            Assert.DoesNotContain(rowLine.Elements(), e => (string)e.Attribute("Property") == "EndsApart");
            var label = Keyed(page, "RowOptionLabel");
            Assert.Equal("200", SetterOf(label, "Width"));
            Assert.Equal("0,0,12,0", SetterOf(label, "Margin"));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", SetterOf(label, "Foreground"));
            Assert.Equal("320", SetterOf(Keyed(page, "RowOptionPicker"), "Width"));
            Assert.Equal("180", SetterOf(Keyed(page, "RowOptionSlider"), "Width"));
            Assert.Equal("180", SetterOf(Keyed(page, "RowOptionDeadzoneSlider"), "Width"));
            Assert.Equal("{StaticResource DzSlider}", (string)Keyed(page, "RowOptionDeadzoneSlider").Attribute("BasedOn"));
            Assert.Equal("180", SetterOf(Keyed(page, "RowOptionModePicker"), "MinWidth"));
            // A row's reset stands 12 after the control.
            Assert.Equal("12,0,0,0", Keyed(page, "RowOptionResetMargin").Value);
            // The options are a grid, which has one column in the list.
            var grid = Keyed(page, "RowOptionsGrid");
            Assert.Equal("controls:TileGridPanel", (string)grid.Attribute("TargetType"));
            Assert.Equal("1", SetterOf(grid, "MaxColumns"));
            Assert.Equal("0", SetterOf(grid, "RowGap"));
            Assert.Equal("Collapsed", Keyed(page, "RowOptionDeadzoneEarly").Value);
            Assert.Equal("Visible", Keyed(page, "RowOptionDeadzoneLate").Value);
            Assert.Equal("Visible", Keyed(page, "RowOptionAddSource").Value);

            // The open row's body: the board's inset on the row's own fill,
            // holding the row's source and value and then its options. Both
            // are templates of the page's, which the bar under the picture
            // shares.
            var body = Assert.Single(Assert.Single(Assert.Single(table.Elements(),
                e => e.Name.LocalName == "DataGrid.RowDetailsTemplate").Elements()).Elements());
            Assert.Equal("Border", body.Name.LocalName);
            Assert.Equal("16,2,8,12", (string)body.Attribute("Padding"));
            Assert.Null(body.Attribute("Background"));
            Assert.Null(body.Attribute("BorderThickness"));
            var parts = Assert.Single(body.Elements()).Elements().ToList();
            Assert.Equal(new[] { "{StaticResource MappingRowSource}", "{StaticResource MappingRowOptions}" },
                parts.Select(p => (string)p.Attribute("ContentTemplate")));
            Assert.All(parts, p =>
            {
                Assert.Equal("ContentControl", p.Name.LocalName);
                Assert.Equal("{Binding}", (string)p.Attribute("Content"));
                Assert.Equal("False", (string)p.Attribute("IsTabStop"));
            });

            var sourcePart = Keyed(page, "MappingRowSource");
            var details = Keyed(page, "MappingRowOptions");
            Assert.Equal("DataTemplate", sourcePart.Name.LocalName);
            Assert.Equal("DataTemplate", details.Name.LocalName);
            // The options reach their row through their own root: the bar
            // has no table row above them to ask.
            var root = Assert.Single(details.Elements());
            Assert.Equal("RowOptionsRoot", Name(root));
            Assert.Equal("TileGridPanel", root.Name.LocalName);
            Assert.Equal("{DynamicResource RowOptionsGrid}", (string)root.Attribute("Style"));
            Assert.DoesNotContain("AncestorType={x:Type DataGridRow}", PageText());

            // The deadzone row is a template of its own, which the options
            // show in one of two places.
            var deadzone = Keyed(page, "MappingRowDeadzone");
            Assert.Equal("DataTemplate", deadzone.Name.LocalName);
            var every = sourcePart.Descendants().Concat(details.Descendants()).Concat(deadzone.Descendants())
                .Where(e => (string)e.Attribute("Style") == "{DynamicResource RowOptionLine}").ToList();
            Assert.All(every, r => Assert.Equal("FlexLinePanel", r.Name.LocalName));
            // A source added to the row has the same rows for itself
            // (AnAddedSourceIsRowsUnderALineOfItsOwn). The row's own are
            // the rest: its source and value, its deadzone, and its options,
            // the tuning of its kind and the motion and trim settings among
            // them (TheTuningMotionAndTrimSettingsAreRows).
            var addedSources = Assert.Single(details.Descendants(), e => (string)e.Attribute("ItemsSource") == "{Binding ExtraSources}");
            var rows = every.Where(r => !r.Ancestors().Contains(addedSources)).ToList();
            Assert.Equal(37, rows.Count);
            Assert.Equal(26, every.Count - rows.Count);
            Assert.DoesNotContain("{StaticResource RowOptionLine}", PageText());
            Assert.DoesNotContain("{StaticResource RowOptionLabel}\"", PageText().Replace("BasedOn=\"{StaticResource RowOptionLabel}\"", ""));
            // Source and Value are the first part, and nothing else is.
            Assert.Equal(2, sourcePart.Descendants().Count(e => (string)e.Attribute("Style") == "{DynamicResource RowOptionLine}"));
            // A row's reset is its last part: marked as the row's end part,
            // at the margin its host gives. Both are its own attributes. A
            // reset button names its style where it is made, which is a
            // local value, and a style set on it in a template is the
            // template's value, which a local one outranks: a reset style
            // of the rows' never applied (seen 2026-11-11).
            var resets = rows.Select(r => r.Elements().Last())
                .Where(e => e.Name.LocalName == "SettingResetButton" || (string)e.Attribute("Style") == "{StaticResource ResetButton}").ToList();
            Assert.Equal(34, resets.Count);
            Assert.Equal(24, resets.Count(e => e.Name.LocalName == "SettingResetButton"));
            resets.AddRange(every.Except(rows).Select(r => r.Elements().Last())
                .Where(e => e.Name.LocalName == "SettingResetButton" || (string)e.Attribute("Style") == "{StaticResource ResetButton}"));
            Assert.Equal(34 + 24, resets.Count);
            Assert.All(resets, e =>
            {
                Assert.Equal("True", (string)e.Attributes().First(a => a.Name.LocalName == "FlexLinePanel.AtEnd"));
                Assert.Equal("{DynamicResource RowOptionResetMargin}", (string)e.Attribute("Margin"));
                if (e.Name.LocalName == "SettingResetButton") Assert.Null(e.Attribute("Style"));
            });
            Assert.DoesNotContain(page.Descendants(), e => e.Name.LocalName == "SettingResetButton" && e.Attribute("Style") != null);
            // Whatever is not one row of a setting takes a whole row of the
            // grid: the note, the formula, the added sources and Add Source.
            var whole = root.Elements().Where(e => (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "TileGridPanel.FullRow") == "True").ToList();
            Assert.Equal(new[] { "TextBlock", "StackPanel", "ItemsControl", "Button" }, whole.Select(e => e.Name.LocalName));
            Assert.All(root.Elements().Except(whole), e => Assert.True(
                e.Name.LocalName == "FlexLinePanel" || Name(e) == "PrimaryModeRows"
                || (string)e.Attribute("ContentTemplate") == "{StaticResource MappingRowDeadzone}", e.Name.LocalName + " is neither a row nor a whole row"));

            XElement RowOf(string labelKey, string gate = null)
            {
                var matches = rows.Where(r => r.Elements().Any(e => (string)e.Attribute("Style") == "{DynamicResource RowOptionLabel}"
                    && (string)e.Attribute("Text") == "{Binding " + labelKey + S)).ToList();
                if (gate != null)
                    matches = matches.Where(r => ((string)r.Attribute("Visibility") ?? "").StartsWith("{Binding " + gate + ",", StringComparison.Ordinal)).ToList();
                return Assert.Single(matches);
            }

            XElement ResetOf(XElement row, string id)
                => Assert.Single(row.Elements(), e => (string)e.Attribute("AutomationProperties.AutomationId") == id);

            // Source: the picker, and the way to add the opposite direction.
            var source = RowOf("Pad_HeaderSource");
            Assert.Equal("{Binding IsPrimaryDescriptor, Converter={StaticResource BoolToVisibilityConverter}}", (string)source.Attribute("Visibility"));
            var picker = Assert.Single(source.Elements(), e => e.Name.LocalName == "ComboBox");
            Assert.Equal("{Binding AvailableInputsView}", (string)picker.Attribute("ItemsSource"));
            Assert.Equal("{Binding SelectedInput, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)picker.Attribute("SelectedItem"));
            Assert.Equal("MappingComboBox_PreviewKeyDown", (string)picker.Attribute("PreviewKeyDown"));
            Assert.Equal("{StaticResource RowOptionPicker}", (string)picker.Attribute("Style"));
            var opposite = Assert.Single(source.Elements(), e => e.Name.LocalName == "Button");
            Assert.Equal("{Binding AddOppositeDirectionCommand}", (string)opposite.Attribute("Command"));
            Assert.Equal("{Binding ShouldShowEmptyDirectionHint, Converter={StaticResource BoolToVisibilityConverter}}", (string)opposite.Attribute("Visibility"));

            // The kind's own inputs, each with its reset.
            foreach (var (key, choice, id, gate) in new[]
            {
                ("ResetLabel_UpInput", "ParamUpInputChoice", "Reset_MappingSourceItem_ParamUpInputChoice_5577", "UsesUpDownKeys"),
                ("ResetLabel_DownInput", "ParamDownInputChoice", "Reset_MappingSourceItem_ParamDownInputChoice_5618", "UsesUpDownKeys"),
                ("ResetLabel_ModifierInput", "ParamModifierInputChoice", "Reset_MappingSourceItem_ParamModifierInputChoice_5659", "IsInvertOnHoldKind"),
            })
            {
                var row = RowOf(key, gate);
                var input = Assert.Single(row.Elements(), e => e.Name.LocalName == "ComboBox");
                Assert.Equal("{Binding " + choice + ", Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)input.Attribute("SelectedItem"));
                Assert.Equal(choice, (string)ResetOf(row, id).Attribute("CommandParameter"));
            }

            // Value: the meter and the number.
            var value = RowOf("Pad_HeaderValue");
            Assert.Equal("{Binding LiveLevel, Mode=OneWay}", (string)Assert.Single(value.Elements(), e => e.Name.LocalName == "ProgressBar").Attribute("Value"));
            Assert.Contains(value.Elements(), e => (string)e.Attribute("Text") == "{Binding CurrentValueText, Mode=OneWay}");

            // Primary Mode, the first of the options, with the kind's own
            // inputs under it: the mode decides which inputs there are.
            var mode = RowOf("Pad_Mapping_PrimaryMode");
            Assert.Equal("{Binding Kind, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
                (string)Assert.Single(mode.Elements(), e => e.Name.LocalName == "ComboBox").Attribute("SelectedValue"));
            Assert.Equal("Kind", (string)ResetOf(mode, "Reset_MappingSourceItem_Kind_4282").Attribute("CommandParameter"));
            var primary = Named(details, "PrimaryModeRows");
            Assert.Same(Assert.Single(details.Elements()).Elements().First(), primary);
            Assert.Equal("{Binding PrimaryKindSource}", (string)primary.Attribute("DataContext"));
            Assert.Same(mode, primary.Elements().First(e => e.Name.LocalName != "StackPanel.Style"));
            Assert.Equal("{DynamicResource RowOptionModePicker}", (string)Assert.Single(mode.Elements(), e => e.Name.LocalName == "ComboBox").Attribute("Style"));
            var inputs = Assert.Single(primary.Elements(), e => ((string)e.Attribute("Visibility") ?? "")
                .StartsWith("{Binding DataContext.IsPrimaryDescriptor, ElementName=RowOptionsRoot,", StringComparison.Ordinal));
            Assert.Null(inputs.Attribute("DataContext"));
            Assert.Equal(3, inputs.Elements().Count(e => (string)e.Attribute("Style") == "{DynamicResource RowOptionLine}"));
            // A kind with inputs or tuning of its own takes a whole row of
            // the grid. Direct and Toggle are one row, a cell like any other.
            var wide = primary.Elements().Single(e => e.Name.LocalName == "StackPanel.Style").Descendants()
                .Where(e => e.Name.LocalName == "DataTrigger").ToList();
            Assert.All(wide, t =>
            {
                Assert.Equal("True", (string)t.Attribute("Value"));
                var set = Assert.Single(t.Elements());
                Assert.Equal("controls:TileGridPanel.FullRow", (string)set.Attribute("Property"));
                Assert.Equal("True", (string)set.Attribute("Value"));
            });
            // Every part of the group that a kind shows widens it: the flags
            // that widen are the gates of the group's parts, the mode row
            // aside, which every kind has.
            var gates = primary.Elements().Concat(inputs.Elements())
                .Select(e => (string)e.Attribute("Visibility"))
                .Where(v => v != null && !v.StartsWith("{Binding DataContext.", StringComparison.Ordinal))
                .Select(v => v.Substring("{Binding ".Length).Split(',')[0]).Distinct().OrderBy(f => f).ToList();
            Assert.Equal(6, gates.Count);
            Assert.Equal(gates, wide.Select(t => ((string)t.Attribute("Binding")).Substring("{Binding ".Length).TrimEnd('}')).OrderBy(f => f));

            // The switches that were the Options column's checkboxes.
            foreach (var (key, flag, id, enabled, gate) in new[]
            {
                ("Pad_Invert", "IsInverted", "Reset_MappingItem_IsInverted_5758", "IsInvertApplicable", (string)null),
                ("Pad_Half", "IsHalfAxis", "Reset_MappingItem_IsHalfAxis_5762", "IsHalfAxisApplicable", null),
                ("Pad_Either", "IsBidirectional", "Reset_MappingItem_IsBidirectional_5769", "IsHalfAxis", null),
                ("Pad_InvertOutput", "InvertOutput", "Reset_MappingItem_InvertOutput_5776", null, "IsInvertOutputApplicable"),
                ("Pad_Shift_NoInherit", "NoInherit", "Reset_MappingItem_NoInherit_5788", null, "DataContext.IsActiveLayerInheriting"),
            })
            {
                var row = rows.Single(r => r.Elements().Any(e => (string)e.Attribute("AutomationProperties.AutomationId") == id));
                Assert.Contains(row.Elements(), e => (string)e.Attribute("Text") == "{Binding " + key + S);
                var box = Assert.Single(row.Elements(), e => e.Name.LocalName == "CheckBox");
                Assert.Equal("{StaticResource SwitchCheckBox}", (string)box.Attribute("Style"));
                Assert.Equal("{Binding " + key + S, (string)box.Attribute("Content"));
                Assert.Equal("{Binding " + flag + ", Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)box.Attribute("IsChecked"));
                Assert.Equal(enabled == null ? null : "{Binding " + enabled + "}", (string)box.Attribute("IsEnabled"));
                Assert.Equal(flag, (string)ResetOf(row, id).Attribute("CommandParameter"));
                if (gate != null)
                    Assert.StartsWith("{Binding " + gate + ",", (string)row.Attribute("Visibility"));
                else
                    Assert.Null(row.Attribute("Visibility"));
            }

            // The dials: one per kind of source, each with its own reset.
            foreach (var (gate, bound, reset) in new[]
            {
                ("IsGyroSource", "GyroSensitivity", "ResetGyroSensitivityCommand"),
                ("IsGyroLeanSource", "Sensitivity", "ResetSensitivityCommand"),
                ("IsParamAccelApplicable", "ParamAccel", "ResetParamAccelCommand"),
                ("IsMouseCursorSource", "MouseCursorSensitivity", "ResetMouseCursorSensitivityCommand"),
                ("IsIrPointerSource", "IrPointerSensitivity", "ResetIrPointerSensitivityCommand"),
                ("IsMouseMotionSource", "IrPointerSensitivity", "ResetIrPointerSensitivityCommand"),
            })
            {
                var row = Assert.Single(rows, r => (string)r.Attribute("Visibility") == "{Binding " + gate + ", Converter={StaticResource BoolToVisibilityConverter}}");
                var slider = Assert.Single(row.Elements(), e => e.Name.LocalName == "Slider");
                Assert.Equal("{Binding " + bound + ", Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)slider.Attribute("Value"));
                Assert.StartsWith("{Binding " + bound + ", Mode=TwoWay, UpdateSourceTrigger=LostFocus,",
                    (string)Assert.Single(row.Elements(), e => e.Name.LocalName == "TextBox").Attribute("Text"));
                Assert.Equal("{Binding " + reset + "}", (string)Assert.Single(row.Elements(), e => e.Name.LocalName == "Button").Attribute("Command"));
            }

            // The deadzone, where one applies.
            var zone = RowOf("Pad_HeaderDeadZone");
            Assert.Same(zone, Assert.Single(deadzone.Elements()));
            Assert.Equal("{Binding IsDeadZoneApplicable, Converter={StaticResource BoolToVisibilityConverter}}", (string)zone.Attribute("Visibility"));
            var zoneSlider = Assert.Single(zone.Elements(), e => e.Name.LocalName == "Slider");
            Assert.Equal("{Binding MappingDeadZone, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)zoneSlider.Attribute("Value"));
            Assert.Equal("{DynamicResource RowOptionDeadzoneSlider}", (string)zoneSlider.Attribute("Style"));
            Assert.Null(zoneSlider.Attribute("Width"));
            Assert.Equal("{Binding ResetDeadZoneCommand}", (string)Assert.Single(zone.Elements(), e => e.Name.LocalName == "Button").Attribute("Command"));
            // Its two places: second, after Primary Mode, where the Buttons
            // board has it under the bar, and after the switches and dials,
            // where the List board has it. A host shows one of them, and
            // neither where no deadzone applies: a place whose row is gone
            // would still take a cell of the grid.
            var places = root.Elements().Where(e => (string)e.Attribute("ContentTemplate") == "{StaticResource MappingRowDeadzone}").ToList();
            Assert.Equal(2, places.Count);
            Assert.Same(places[0], primary.ElementsAfterSelf().First());
            Assert.Same(places[1], root.Elements().TakeWhile(e => !whole.Contains(e)).Last());
            foreach (var (place, key) in new[] { (places[0], "RowOptionDeadzoneEarly"), (places[1], "RowOptionDeadzoneLate") })
            {
                Assert.Equal("ContentControl", place.Name.LocalName);
                Assert.Equal("{Binding}", (string)place.Attribute("Content"));
                Assert.Equal("False", (string)place.Attribute("IsTabStop"));
                var shown = Assert.Single(place.Descendants(), e => e.Name.LocalName == "Style");
                Assert.Equal("{DynamicResource " + key + "}", SetterOf(shown, "Visibility"));
                var gone = Assert.Single(shown.Descendants(), e => e.Name.LocalName == "DataTrigger");
                Assert.Equal("{Binding IsDeadZoneApplicable}", (string)gone.Attribute("Binding"));
                Assert.Equal("False", (string)gone.Attribute("Value"));
                Assert.Equal("Collapsed", (string)Assert.Single(gone.Elements()).Attribute("Value"));
            }

            // Combine, and Add Source as a quiet button, which a host can
            // leave out.
            var combine = RowOf("Pad_Combine_Label");
            var combinePicker = Assert.Single(combine.Elements(), e => e.Name.LocalName == "ComboBox");
            Assert.Equal("{Binding CombineMode, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)combinePicker.Attribute("SelectedValue"));
            // The picker keeps its own look, and takes its least width from
            // its host: 200 in the list.
            Assert.Null(combinePicker.Attribute("Style"));
            Assert.Equal("{DynamicResource RowOptionCombineMinWidth}", (string)combinePicker.Attribute("MinWidth"));
            Assert.Equal("200", Keyed(page, "RowOptionCombineMinWidth").Value);
            var add = Assert.Single(details.Descendants(), e => (string)e.Attribute("Command") == "{Binding AddExtraSourceCommand}");
            Assert.Equal("{StaticResource QuietTextButton}", (string)add.Attribute("Style"));
            Assert.Equal("{DynamicResource RowOptionAddSource}", (string)add.Attribute("Visibility"));
            // Its plus is drawn, as the List board has it, and it is drawn out
            // to the start by its own inset.
            Assert.Equal("-14,4,0,0", (string)add.Attribute("Margin"));
            Assert.Equal("M 7,3 L 7,11 M 3,7 L 11,7", (string)Assert.Single(add.Descendants(), e => e.Name.LocalName == "Path").Attribute("Data"));

            // Nothing runs past the card's edge: the table does not scroll
            // sideways, and no strip of an open row is a panel that cannot
            // wrap.
            Assert.Equal("Disabled", (string)table.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
            Assert.DoesNotContain(sourcePart.Descendants().Concat(details.Descendants()), e => e.Name.LocalName == "StackPanel"
                && (string)e.Attribute("Orientation") == "Horizontal");
        }

        private const string ShownBy = ", Converter={StaticResource BoolToVisibilityConverter}}";
        private const string HiddenBy = ", Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}";

        /// <summary>The tuning of a kind, a motion row's settings and the
        /// trim settings are rows of the options, one for each setting.
        /// Each was a strip of settings one after the other, with check
        /// boxes of the old look. The tuning of the row's own source is a
        /// grid of rows for each kind, under Primary Mode. The motion and
        /// the trim settings are tiles of the options, each shown by what
        /// showed it in its strip. No strip is left in the options.</summary>
        [Fact]
        public void TheTuningMotionAndTrimSettingsAreRows()
        {
            var details = Keyed(Page(), "MappingRowOptions");
            var root = Assert.Single(details.Elements());
            var primary = Named(details, "PrimaryModeRows");
            string Label(XElement row) => (string)row.Elements().First().Attribute("Text");
            string Key(string text) => text.Substring("{Binding ".Length, text.IndexOf(',') - "{Binding ".Length);

            var tuning = primary.Elements().Where(e => e.Name.LocalName == "TileGridPanel").ToList();
            Assert.Equal(new[] { "IsRampedKind", "IsIncrementalKind", "IsRapidTriggerKind" },
                tuning.Select(g => Key((string)g.Attribute("Visibility"))));
            Assert.All(tuning, g =>
            {
                Assert.Equal("{DynamicResource RowOptionsGrid}", (string)g.Attribute("Style"));
                Assert.All(g.Elements(), r =>
                {
                    Assert.Equal("{DynamicResource RowOptionLine}", (string)r.Attribute("Style"));
                    Assert.Null(r.Attribute("Visibility"));
                    Assert.Equal("{DynamicResource RowOptionLabel}", (string)r.Elements().First().Attribute("Style"));
                    Assert.Equal("SettingResetButton", r.Elements().Last().Name.LocalName);
                });
            });
            Assert.Equal(new[] { "Pad_Mapping_Ramp_Attack", "Pad_Mapping_Ramp_Release", "Pad_Mapping_Ramp_Reverse", "Pad_Mapping_Ramp_Autocenter" },
                tuning[0].Elements().Select(r => Key(Label(r))));
            Assert.Equal(new[] { "Pad_Mapping_Rate", "Pad_Mapping_Sticky", "Pad_Mapping_Min", "Pad_Mapping_Max" },
                tuning[1].Elements().Select(r => Key(Label(r))));
            Assert.Equal(new[] { "Pad_Mapping_RapidTrigger_Distance" }, tuning[2].Elements().Select(r => Key(Label(r))));
            Assert.Equal(new[]
                {
                    "Reset_MappingSourceItem_ParamAttackTime_4288", "Reset_MappingSourceItem_ParamReleaseTime_4292",
                    "Reset_MappingSourceItem_ParamReverseMultiplier_4296", "Reset_MappingSourceItem_ParamAutocenter_4299",
                    "Reset_MappingSourceItem_ParamRate_4305", "Reset_MappingSourceItem_ParamSticky_4308",
                    "Reset_MappingSourceItem_ParamMin_4311", "Reset_MappingSourceItem_ParamMax_4314",
                    "Reset_MappingSourceItem_ParamRapidTriggerDistance_4316",
                },
                tuning.SelectMany(g => g.Elements()).Select(r => (string)r.Elements().Last().Attribute("AutomationProperties.AutomationId")));
            // A slider has the rows' look and no width of its own, a number
            // is the page's value text, and a check box is a switch.
            var parts = tuning.SelectMany(g => g.Descendants()).ToList();
            Assert.All(parts.Where(e => e.Name.LocalName == "Slider"), s =>
            {
                Assert.Equal("{DynamicResource RowOptionSlider}", (string)s.Attribute("Style"));
                Assert.Null(s.Attribute("Width"));
                Assert.NotNull(s.Attribute("AutomationProperties.Name"));
            });
            Assert.All(parts.Where(e => e.Name.LocalName == "TextBox"), b => Assert.Equal("{StaticResource DzValueEdit}", (string)b.Attribute("Style")));
            Assert.All(parts.Where(e => e.Name.LocalName == "CheckBox"), b => Assert.Equal("{StaticResource SwitchCheckBox}", (string)b.Attribute("Style")));

            // The motion and the trim settings: tiles of the options.
            XElement Tile(string label) => Assert.Single(root.Elements(), e => e.Name.LocalName == "FlexLinePanel"
                && (string)e.Elements().First().Attribute("Text") == "{Binding " + label + S);
            foreach (var (label, gate, reset) in new[]
            {
                ("Pad_Mapping_MotionMode", "CanUseMotionAngle", "MotionResponse"),
                ("Pad_Mapping_MotionTopSpeed", "ShowMotionSpeedSettings", "MotionSpeed"),
                ("Pad_Mapping_MotionStartSpeed", "ShowMotionSpeedSettings", "MotionMinSpeed"),
                ("Pad_Mapping_MotionLeanAngle", "ShowMotionAngleSettings", "MotionAngle"),
                ("Pad_Mapping_MotionDeadzone", "IsMotionAxisRow", "MotionDeadzone"),
            })
            {
                var row = Tile(label);
                Assert.Equal("{Binding " + gate + ShownBy, (string)row.Attribute("Visibility"));
                Assert.Equal(reset, (string)row.Elements().Last().Attribute("CommandParameter"));
            }
            var mode = Tile("Pad_Mapping_MotionMode");
            var modePicker = Assert.Single(mode.Elements(), e => e.Name.LocalName == "ComboBox");
            Assert.Equal("{DynamicResource RowOptionModePicker}", (string)modePicker.Attribute("Style"));
            Assert.Equal("{Binding MotionResponse, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)modePicker.Attribute("SelectedValue"));
            foreach (var (label, command) in new[]
            {
                ("Pad_Mapping_TrimDeadzone", "ResetTrimDeadzoneCommand"),
                ("Pad_Mapping_TrimRate", "ResetTrimRateCommand"),
                ("Pad_Mapping_TrimReset", "ResetTrimResetOnReleaseCommand"),
            })
            {
                var row = Tile(label);
                Assert.Equal("{Binding ShouldShowTrimSettings" + ShownBy, (string)row.Attribute("Visibility"));
                var reset = row.Elements().Last();
                Assert.Equal("{Binding " + command + "}", (string)reset.Attribute("Command"));
                Assert.Equal("{StaticResource ResetButton}", (string)reset.Attribute("Style"));
            }
            Assert.Equal("{StaticResource SwitchCheckBox}", (string)Assert.Single(Tile("Pad_Mapping_TrimReset").Elements(), e => e.Name.LocalName == "CheckBox").Attribute("Style"));

            // No strip of settings is left: every wrap panel of the options
            // is a cloud of the formula's chips.
            var formula = Named(details, "FormulaEditor");
            Assert.All(details.Descendants().Where(e => e.Name.LocalName == "WrapPanel"), w => Assert.Contains(formula, w.Ancestors()));
            Assert.DoesNotContain("ResetButtonTight", details.ToString());
        }

        /// <summary>A source added to a row is the options' own rows, for
        /// that source, under a line of its own. No board draws one. It was
        /// a blue chip that held a type picker, a source picker, Record,
        /// Clear, four check boxes, a dial, a deadzone, the inputs and
        /// tuning of its kind and a small x, all in one wrapping line. Its
        /// head names it and ends in Remove. Then come its type, its source
        /// with Record and Clear, the inputs of its kind, the tuning of its
        /// kind, how it is read, its dials and its deadzone, each shown by
        /// what showed it in the chip. Every reset keeps its id, and the Up
        /// and Down pickers stand once for the two kinds that have them.</summary>
        [Fact]
        public void AnAddedSourceIsRowsUnderALineOfItsOwn()
        {
            var page = Page();
            var details = Keyed(page, "MappingRowOptions");
            var list = Assert.Single(details.Descendants(), e => (string)e.Attribute("ItemsSource") == "{Binding ExtraSources}");
            Assert.Equal("ItemsControl", list.Name.LocalName);
            Assert.Equal("True", (string)list.Attributes().First(a => a.Name.LocalName == "TileGridPanel.FullRow"));
            var block = Assert.Single(Assert.Single(Assert.Single(list.Elements()).Elements()).Elements());
            Assert.Equal("Border", block.Name.LocalName);
            Assert.Equal("0,1,0,0", (string)block.Attribute("BorderThickness"));
            Assert.Equal("{DynamicResource CardStrokeColorDefaultBrush}", (string)block.Attribute("BorderBrush"));
            Assert.Null(block.Attribute("Background"));
            Assert.Null(block.Attribute("CornerRadius"));
            var parts = Assert.Single(block.Elements()).Elements().ToList();
            Assert.Equal(new[] { "FlexLinePanel", "TileGridPanel" }, parts.Select(e => e.Name.LocalName));

            // The head: the name, the direction badge, and Remove at its end.
            var head = parts[0];
            Assert.Equal("True", (string)head.Attribute("EndsApart"));
            Assert.Equal("40", (string)head.Attribute("MinLineHeight"));
            var headParts = head.Elements().ToList();
            Assert.Equal(3, headParts.Count);
            Assert.Equal("{StaticResource EditorSubHead}", (string)headParts[0].Attribute("Style"));
            Assert.Equal("{Binding Pad_HeaderSource" + S, (string)headParts[0].Attribute("Text"));
            Assert.Equal("{Binding DirectionBadge}", (string)Assert.Single(headParts[1].Elements(), e => e.Name.LocalName == "TextBlock").Attribute("Text"));
            var remove = headParts[2];
            Assert.Equal("{StaticResource QuietTextButton}", (string)remove.Attribute("Style"));
            Assert.Equal("{Binding Common_Remove" + S, (string)remove.Attribute("Content"));
            Assert.Equal("{Binding DataContext.RemoveExtraSourceCommand, ElementName=RowOptionsRoot}", (string)remove.Attribute("Command"));
            Assert.Equal("{Binding}", (string)remove.Attribute("CommandParameter"));
            Assert.Equal("{Binding Pad_Mapping_RemoveSource_Tooltip" + S, (string)remove.Attribute("ToolTip"));
            Assert.Equal("True", (string)remove.Attributes().First(a => a.Name.LocalName == "FlexLinePanel.AtEnd"));

            // The rows: a grid as the options' own, so they flow in columns
            // under the bar and stand one under the other in the list. A
            // row that shows while two things hold stands in a tile that
            // does.
            var grid = parts[1];
            Assert.Equal("{DynamicResource RowOptionsGrid}", (string)grid.Attribute("Style"));
            var tiles = grid.Elements().ToList();
            XElement RowIn(XElement tile) => tile.Name.LocalName == "Border"
                ? Assert.Single(tile.Elements(), e => e.Name.LocalName == "FlexLinePanel") : tile;
            string GateOf(XElement tile) => tile.Name.LocalName == "Border"
                ? string.Join(" and ", tile.Descendants().Where(e => e.Name.LocalName == "Condition")
                    .Select(c => ((string)c.Attribute("Binding")).Substring("{Binding ".Length).TrimEnd('}') + " " + (string)c.Attribute("Value")))
                : (string)tile.Attribute("Visibility");
            string Shown(string flag) => "{Binding " + flag + ShownBy;
            string reads = "{Binding IsKindDescriptorless" + HiddenBy;
            var expected = new (string Label, string Gate, string Reset)[]
            {
                ("ResetLabel_SourceType", null, "Reset_MappingSourceItem_Kind_4734"),
                ("Pad_Invert", Shown("IsKeyInvertShown"), null),
                ("Pad_HeaderSource", reads, null),
                ("ResetLabel_ModifierInput", Shown("IsInvertOnHoldKind"), "Reset_MappingSourceItem_ParamModifierInputChoice_4848"),
                ("ResetLabel_UpInput", Shown("UsesUpDownKeys"), "Reset_MappingSourceItem_ParamUpInputChoice_5090"),
                ("ResetLabel_DownInput", Shown("UsesUpDownKeys"), "Reset_MappingSourceItem_ParamDownInputChoice_5149"),
                ("Pad_Mapping_RapidTrigger_Distance", Shown("IsRapidTriggerKind"), "Reset_MappingSourceItem_ParamRapidTriggerDistance_5377"),
                ("Pad_Mapping_Rate", Shown("IsIncrementalKind"), "Reset_MappingSourceItem_ParamRate_5200"),
                ("Pad_Mapping_Sticky", Shown("IsIncrementalKind"), "Reset_MappingSourceItem_ParamSticky_5207"),
                ("Pad_Mapping_Min", Shown("IsIncrementalKind"), "Reset_MappingSourceItem_ParamMin_5213"),
                ("Pad_Mapping_Max", Shown("IsIncrementalKind"), "Reset_MappingSourceItem_ParamMax_5219"),
                ("Pad_Mapping_Ramp_Attack", Shown("IsRampedKind"), "Reset_MappingSourceItem_ParamAttackTime_5350"),
                ("Pad_Mapping_Ramp_Release", Shown("IsRampedKind"), "Reset_MappingSourceItem_ParamReleaseTime_5359"),
                ("Pad_Mapping_Ramp_Reverse", Shown("IsRampedKind"), "Reset_MappingSourceItem_ParamReverseMultiplier_5368"),
                ("Pad_Mapping_Ramp_Autocenter", Shown("IsRampedKind"), "Reset_MappingSourceItem_ParamAutocenter_5375"),
                ("Pad_Invert", reads, "Reset_MappingSourceItem_Invert_4926"),
                ("Pad_Half", reads, "Reset_MappingSourceItem_HalfAxis_4931"),
                ("Pad_Either", reads, "Reset_MappingSourceItem_Bidirectional_4937"),
                ("Pad_InvertOutput", "IsKindDescriptorless False and IsInvertOutputApplicable True", "Reset_MappingSourceItem_InvertOutput_4943"),
                ("Mapping_GyroSensitivity", Shown("IsGyroSource"), null),
                ("Mapping_GyroSensitivity", Shown("IsGyroLeanSource"), null),
                ("Pad_Mapping_Accel", Shown("IsParamAccelApplicable"), null),
                ("Mapping_MouseCursorSensitivity", Shown("IsMouseCursorSource"), null),
                ("Mapping_IrPointerSensitivity", Shown("IsIrPointerSource"), null),
                ("Mapping_IrPointerSensitivity", Shown("IsMouseMotionSource"), null),
                ("Pad_HeaderDeadZone", "IsKindDescriptorless False and IsDeadZoneApplicable True", null),
            };
            Assert.Equal(expected.Length, tiles.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                var row = RowIn(tiles[i]);
                Assert.Equal("{DynamicResource RowOptionLine}", (string)row.Attribute("Style"));
                var label = row.Elements().First();
                Assert.Equal("{DynamicResource RowOptionLabel}", (string)label.Attribute("Style"));
                Assert.Equal("{Binding " + expected[i].Label + S, (string)label.Attribute("Text"));
                Assert.Equal(expected[i].Gate, GateOf(tiles[i]));
                // A row in a tile has no gate of its own, and the tile is
                // hidden until its conditions hold.
                if (tiles[i].Name.LocalName == "Border")
                {
                    Assert.Null(row.Attribute("Visibility"));
                    Assert.Equal("Collapsed", SetterOf(Assert.Single(tiles[i].Descendants(), e => e.Name.LocalName == "Style"), "Visibility"));
                }
                var last = row.Elements().Last();
                if (expected[i].Reset != null)
                {
                    Assert.Equal("SettingResetButton", last.Name.LocalName);
                    Assert.Equal(expected[i].Reset, (string)last.Attribute("AutomationProperties.AutomationId"));
                    Assert.Equal("{Binding ResetSettingCommand}", (string)last.Attribute("Command"));
                }
            }
            // The source and the three inputs take a whole row: a picker of
            // 320, the device, and the row's buttons.
            Assert.All(new[] { 2, 3, 4, 5 }, i =>
                Assert.Equal("True", (string)tiles[i].Attributes().First(a => a.Name.LocalName == "TileGridPanel.FullRow")));

            // Its type: a mode picker over the source's own kinds.
            var kind = Assert.Single(tiles[0].Elements(), e => e.Name.LocalName == "ComboBox");
            Assert.Equal("{Binding KindChoices}", (string)kind.Attribute("ItemsSource"));
            Assert.Equal("{Binding Kind, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)kind.Attribute("SelectedValue"));
            Assert.Equal("{DynamicResource RowOptionModePicker}", (string)kind.Attribute("Style"));
            Assert.Equal("{Binding Pad_Mapping_Kind_Tooltip" + S, (string)kind.Attribute("ToolTip"));

            // Its source: the picker of every input of the slot's devices,
            // the device, Record with the page's record look, and Clear.
            var source = tiles[2].Elements().ToList();
            Assert.Equal(new[] { "TextBlock", "ComboBox", "TextBlock", "Button", "Button" }, source.Select(e => e.Name.LocalName));
            Assert.Equal("{Binding DataContext.AvailableInputsView, ElementName=RowOptionsRoot}", (string)source[1].Attribute("ItemsSource"));
            Assert.Equal("{Binding SelectedInput, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)source[1].Attribute("SelectedItem"));
            Assert.Equal("{StaticResource RowOptionPicker}", (string)source[1].Attribute("Style"));
            Assert.Equal("{Binding Pad_Mapping_ExtraSourcePickerTooltip" + S, (string)source[1].Attribute("ToolTip"));
            Assert.Equal("MappingComboBox_PreviewKeyDown", (string)source[1].Attribute("PreviewKeyDown"));
            Assert.Equal("{Binding DeviceLabel}", SetterOf(Assert.Single(source[2].Descendants(), e => e.Name.LocalName == "Style"), "Text"));
            Assert.Equal("{StaticResource RecordButton}", (string)source[3].Attribute("Style"));
            Assert.Equal("{Binding RecordButtonIcon}", (string)source[3].Attribute("Content"));
            Assert.Equal("{Binding ToggleRecordCommand}", (string)source[3].Attribute("Command"));
            Assert.Equal("{Binding RecordButtonText, TargetNullValue='', FallbackValue=''}", (string)source[3].Attribute("AutomationProperties.Name"));
            Assert.Equal("{StaticResource RowIconButton}", (string)source[4].Attribute("Style"));
            Assert.Equal("{Binding ClearCommand}", (string)source[4].Attribute("Command"));
            Assert.Equal("{Binding Common_Clear" + S, (string)source[4].Attribute("AutomationProperties.Name"));

            // The inputs of a kind: the picker, the device, Record and the reset.
            foreach (var (i, list2, choice, device, icon, command) in new[]
            {
                (3, "ParamInputsView", "ParamModifierInputChoice", "ParamModifierDeviceLabel", "RecordIconParamModifier", "RecordParamModifierCommand"),
                (4, "KeyInputsView", "ParamUpInputChoice", "ParamUpDeviceLabel", "RecordIconParamUp", "RecordParamUpCommand"),
                (5, "KeyInputsView", "ParamDownInputChoice", "ParamDownDeviceLabel", "RecordIconParamDown", "RecordParamDownCommand"),
            })
            {
                var row = tiles[i].Elements().ToList();
                Assert.Equal(new[] { "TextBlock", "ComboBox", "TextBlock", "Button", "SettingResetButton" }, row.Select(e => e.Name.LocalName));
                Assert.Equal("{Binding DataContext." + list2 + ", ElementName=RowOptionsRoot}", (string)row[1].Attribute("ItemsSource"));
                Assert.Equal("{Binding " + choice + ", Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)row[1].Attribute("SelectedItem"));
                Assert.Equal("{StaticResource RowOptionPicker}", (string)row[1].Attribute("Style"));
                Assert.Equal("{Binding " + device + "}", SetterOf(Assert.Single(row[2].Descendants(), e => e.Name.LocalName == "Style"), "Text"));
                Assert.Equal("{StaticResource RecordButton}", (string)row[3].Attribute("Style"));
                Assert.Equal("{Binding " + icon + "}", (string)row[3].Attribute("Content"));
                Assert.Equal("{Binding " + command + "}", (string)row[3].Attribute("Command"));
                Assert.Equal(choice, (string)row[4].Attribute("CommandParameter"));
            }

            // How it is read: switches, with what enabled each check box.
            foreach (var (i, flag, enabled) in new[]
            {
                (15, "Invert", "IsInvertApplicable"), (16, "HalfAxis", "IsHalfAxisApplicable"),
                (17, "Bidirectional", "HalfAxis"), (18, "InvertOutput", (string)null),
            })
            {
                var box = Assert.Single(RowIn(tiles[i]).Elements(), e => e.Name.LocalName == "CheckBox");
                Assert.Equal("{StaticResource SwitchCheckBox}", (string)box.Attribute("Style"));
                Assert.Equal("{Binding " + flag + ", Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)box.Attribute("IsChecked"));
                Assert.Equal(enabled == null ? null : "{Binding " + enabled + "}", (string)box.Attribute("IsEnabled"));
            }

            // The dials and the deadzone: the rows' sliders, the page's
            // value text, and the reset each had.
            foreach (var (i, bound, reset) in new[]
            {
                (19, "GyroSensitivity", "ResetGyroSensitivityCommand"), (20, "Sensitivity", "ResetSensitivityCommand"),
                (21, "ParamAccel", "ResetParamAccelCommand"), (22, "MouseCursorSensitivity", "ResetMouseCursorSensitivityCommand"),
                (23, "IrPointerSensitivity", "ResetIrPointerSensitivityCommand"), (24, "IrPointerSensitivity", "ResetIrPointerSensitivityCommand"),
                (25, "DeadZone", "ResetDeadZoneCommand"),
            })
            {
                var row = RowIn(tiles[i]);
                var slider = Assert.Single(row.Elements(), e => e.Name.LocalName == "Slider");
                Assert.Equal("{Binding " + bound + ", Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)slider.Attribute("Value"));
                Assert.Equal(i == 25 ? "{DynamicResource RowOptionDeadzoneSlider}" : "{DynamicResource RowOptionSlider}", (string)slider.Attribute("Style"));
                Assert.Null(slider.Attribute("Width"));
                Assert.Equal("{StaticResource DzValueEdit}", (string)Assert.Single(row.Elements(), e => e.Name.LocalName == "TextBox").Attribute("Style"));
                var last = row.Elements().Last();
                Assert.Equal("{Binding " + reset + "}", (string)last.Attribute("Command"));
                Assert.Equal("{StaticResource ResetButton}", (string)last.Attribute("Style"));
            }

            // The chip is gone, with its glyphs and its small hot buttons.
            string text = list.ToString();
            Assert.DoesNotContain("58B6E4", text);
            Assert.DoesNotContain("EmberIconButtonHot", text);
            Assert.DoesNotContain("WrapPanel", text);
            Assert.DoesNotContain("\u21B3", text);
        }

        // ── the bar under the picture ──────────────────────────────────

        /// <summary>The bar's row is the one the pick names. On a gamepad a
        /// stick's axes and the D-pad's directions are one control with the
        /// group's name, and any other control is its own row. The bar
        /// shows under the picture once a control is picked, none being
        /// picked at the start, and its options when they are opened.</summary>
        [Fact]
        public void TheBarIsOnThePickedRow()
        {
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            Assert.Null(vm.PickedRow);
            Assert.Equal(string.Empty, vm.PickedName);
            Assert.Equal(string.Empty, vm.PickedKind);
            Assert.False(vm.ShowPictureBar);
            Assert.False(vm.ShowPickedOptions);
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

            // The options open and close, and hold from one pick to the next.
            Assert.False(vm.ShowPickedOptions);
            raised.Clear();
            vm.IsPickedOptionsOpen = true;
            Assert.True(vm.ShowPickedOptions);
            Assert.Contains(nameof(PadViewModel.ShowPickedOptions), raised);

            var stickY = vm.Mappings.Last(m => m.Category == MappingCategory.LeftStick);
            vm.PickedTarget = stickY.TargetSettingName;
            Assert.Same(stickY, vm.PickedRow);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Menu_Host_LeftStick, vm.PickedName);
            Assert.True(vm.ShowPickedOptions);
            vm.PickedTarget = vm.Mappings.First(m => m.Category == MappingCategory.DPad).TargetSettingName;
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Preview_DPad, vm.PickedName);
            vm.PickedTarget = vm.Mappings.First(m => m.Category == MappingCategory.RightStick).TargetSettingName;
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Menu_Host_RightStick, vm.PickedName);

            // The list has no bar, and the picture has it again.
            raised.Clear();
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.False(vm.ShowPictureBar);
            Assert.False(vm.ShowPickedOptions);
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

        /// <summary>The bar, as the Buttons board draws it: the control's
        /// name, a block for each of its rows with the source's picker,
        /// Record and Clear, then the current row's value with Add Source
        /// and the way to its options, which open under the bar.</summary>
        [Fact]
        public void TheBarUnderThePictureIsAsItsBoardDrawsIt()
        {
            var page = Page();
            var bar = Named(page, "PictureBar");
            Assert.Equal("Border", bar.Name.LocalName);
            Assert.Equal("2", (string)bar.Attribute("Grid.Row"));
            Assert.Equal("20,16", (string)bar.Attribute("Padding"));
            Assert.Equal("{Binding ShowPictureBar, Converter={StaticResource BoolToVisibilityConverter}}", (string)bar.Attribute("Visibility"));

            // Three parts that share a line while the card has room: 170
            // for the name, the blocks from 420 with nearly all that is
            // left over, the value from 220.
            var line = Assert.Single(bar.Elements());
            Assert.Equal("FlexLinePanel", line.Name.LocalName);
            Assert.Equal("28", (string)line.Attribute("Gap"));
            Assert.Equal("14", (string)line.Attribute("LineGap"));
            var parts = line.Elements().ToList();
            Assert.Equal(3, parts.Count);
            Assert.Equal(new[] { "170", "420", "220" },
                parts.Select(p => (string)p.Attributes().First(a => a.Name.LocalName == "FlexLinePanel.Basis")));
            // The name, and under it what kind of control it is, which
            // shows where there is one to say.
            Assert.Equal("StackPanel", parts[0].Name.LocalName);
            var heads = parts[0].Elements().ToList();
            Assert.Equal(new[] { "PickedNameText", "PickedKindText" }, heads.Select(Name));
            Assert.Equal("{Binding PickedName}", (string)heads[0].Attribute("Text"));
            Assert.Equal("20", (string)heads[0].Attribute("FontSize"));
            Assert.Equal("SemiBold", (string)heads[0].Attribute("FontWeight"));
            Assert.Equal("{Binding PickedKind}", (string)heads[1].Attribute("Text"));
            Assert.Equal("13", (string)heads[1].Attribute("FontSize"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)heads[1].Attribute("Foreground"));
            Assert.Equal("{Binding PickedKind, Converter={StaticResource StringToVisibility}}", (string)heads[1].Attribute("Visibility"));

            // The blocks: the view's rows, which under the picture are the
            // picked control's.
            var blocks = parts[1];
            Assert.Equal("PickedBlocks", Name(blocks));
            Assert.Equal("{Binding PickedRowsView}", (string)blocks.Attribute("ItemsSource"));
            Assert.Equal("999", (string)blocks.Attributes().First(a => a.Name.LocalName == "FlexLinePanel.Grow"));
            var tiles = Assert.Single(blocks.Descendants(), e => e.Name.LocalName == "TileGridPanel");
            Assert.Equal("300", (string)tiles.Attribute("MinItemWidth"));
            Assert.Equal("True", (string)tiles.Attribute("FillRows"));
            var block = Assert.Single(blocks.Elements().Single(e => e.Name.LocalName == "ItemsControl.ItemTemplate").Elements()).Elements().First();
            Assert.Equal("PickedBlock_GotFocus", (string)block.Attribute("GotKeyboardFocus"));
            // The caption is the output, then "driven by": the picker
            // under it names the input.
            var caption = Named(block, "Caption");
            Assert.Null(caption.Attribute("Text"));
            var said = Assert.Single(caption.Descendants(), e => e.Name.LocalName == "MultiBinding");
            Assert.Equal("{StaticResource FormatTextConverter}", (string)said.Attribute("Converter"));
            Assert.Equal(new[] { "BarLabel", "Pad_DrivenBy_Format" }, said.Elements().Select(b => (string)b.Attribute("Path")));
            Assert.Contains("{0}", PadForge.Resources.Strings.Strings.Instance.Pad_DrivenBy_Format);
            var picker = Assert.Single(block.Descendants(), e => e.Name.LocalName == "ComboBox");
            Assert.Equal("{Binding AvailableInputsView}", (string)picker.Attribute("ItemsSource"));
            Assert.Equal("{Binding SelectedInput, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)picker.Attribute("SelectedItem"));
            Assert.Equal("{Binding IsPrimaryDescriptor, Converter={StaticResource BoolToVisibilityConverter}}", (string)picker.Attribute("Visibility"));
            Assert.Equal("MappingComboBox_PreviewKeyDown", (string)picker.Attribute("PreviewKeyDown"));
            // The closed picker names the input and its device, and an
            // input in the list is its name alone.
            Assert.Equal("DisplayName", (string)picker.Attributes().First(a => a.Name.LocalName == "TextSearch.TextPath"));
            var device = Assert.Single(picker.Descendants(), e => (string)e.Attribute("Text") == "{Binding DeviceLabel}");
            Assert.Equal("{Binding RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type ComboBoxItem}}, Converter={StaticResource NullToCollapsedConverter}, ConverterParameter=Invert, FallbackValue=Visible}",
                (string)device.Attribute("Visibility"));
            // Record has the page's record look: the ember dot and the
            // word, and a square and Stop while the row listens. Its content
            // is the row's glyph for which of the two it is, and the face
            // draws the rest.
            var record = Assert.Single(block.Descendants(), e => (string)e.Attribute("Command") == "{Binding ToggleRecordCommand}");
            Assert.Equal("{Binding IsRecordable}", (string)record.Attribute("IsEnabled"));
            Assert.Equal("{StaticResource RecordButton}", (string)record.Attribute("Style"));
            Assert.Equal("{Binding RecordButtonIcon}", (string)record.Attribute("Content"));
            Assert.Equal("{Binding RecordButtonText, TargetNullValue=''}", (string)record.Attribute("AutomationProperties.Name"));
            Assert.Empty(record.Elements());
            // The glyphs the face follows are the row's own, and a source's.
            var listening = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            Assert.Equal(MenuEditorItem.RecordGlyph, listening.RecordButtonIcon);
            listening.IsRecording = true;
            Assert.Equal(MenuEditorItem.StopGlyph, listening.RecordButtonIcon);
            var extra = new MappingSourceItem();
            Assert.All(new[] { extra.RecordButtonIcon, extra.RecordIconParamUp, extra.RecordIconParamDown, extra.RecordIconParamModifier },
                glyph => Assert.Equal(MenuEditorItem.RecordGlyph, glyph));
            extra.IsRecording = true;
            Assert.Equal(MenuEditorItem.StopGlyph, extra.RecordButtonIcon);
            Assert.Contains(block.Descendants(), e => (string)e.Attribute("Command") == "{Binding ClearCommand}");
            // The current row's caption is in the text color.
            var current = Assert.Single(blocks.Descendants(), e => e.Name.LocalName == "DataTrigger"
                && (string)e.Attribute("Binding") == "{Binding IsRowSelected}");
            Assert.Equal("Caption", (string)Assert.Single(current.Elements()).Attribute("TargetName"));

            // The current row's value, Add Source and the options' switch.
            var side = parts[2];
            Assert.Equal("{Binding PickedRow}", (string)side.Attribute("DataContext"));
            Assert.Equal("{Binding LiveLevel, Mode=OneWay}", (string)Assert.Single(side.Descendants(), e => e.Name.LocalName == "ProgressBar").Attribute("Value"));
            // The value as the boards read it, a share of the full scale,
            // where a slot's board draws one.
            Assert.Contains(side.Descendants(), e => (string)e.Attribute("Text") == "{Binding CurrentPercentText, Mode=OneWay}");
            var value = side.Elements().First();
            Assert.Equal("DockPanel", value.Name.LocalName);
            Assert.Equal("{Binding DataContext.ShowPickedValue, ElementName=PictureBar, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)value.Attribute("Visibility"));
            // Add Source, as the board draws it: a quiet button with its plus
            // drawn and its word, at the column's start.
            var add = Named(side, "PickedAddSource");
            Assert.Equal("Button", add.Name.LocalName);
            Assert.Equal("{StaticResource QuietTextButton}", (string)add.Attribute("Style"));
            Assert.Equal("10,0", (string)add.Attribute("Padding"));
            Assert.Equal("0,10,0,0", (string)add.Parent.Attribute("Margin"));
            Assert.Equal("{Binding Pad_Mapping_AddSource" + S, (string)add.Attribute("AutomationProperties.Name"));
            Assert.Equal("M 7,3 L 7,11 M 3,7 L 11,7", (string)Assert.Single(add.Descendants(), e => e.Name.LocalName == "Path").Attribute("Data"));
            Assert.Contains(add.Descendants(), e => (string)e.Attribute("Text") == "{Binding Pad_Mapping_AddSource" + S);
            Assert.Equal("Add Source", PadForge.Resources.Strings.Strings.Instance.Pad_Mapping_AddSource);
            Assert.Equal("{Binding AddExtraSourceCommand}", (string)add.Attribute("Command"));
            Assert.Equal("PickedAddSource_Click", (string)add.Attribute("Click"));
            var toggle = Named(side, "PickedOptionsToggle");
            Assert.Equal("ToggleButton", toggle.Name.LocalName);
            Assert.Equal("{Binding DataContext.IsPickedOptionsOpen, ElementName=PictureBar, Mode=TwoWay}", (string)toggle.Attribute("IsChecked"));
            // It says More Options while they are closed and Fewer Options
            // while they are open.
            Assert.Equal("{Binding Pad_MoreOptions" + S, (string)toggle.Attribute("AutomationProperties.Name"));
            var word = Assert.Single(toggle.Descendants(), e => e.Name.LocalName == "Style"
                && (string)e.Attribute("TargetType") == "TextBlock");
            Assert.Equal("{Binding Pad_MoreOptions" + S, SetterOf(word, "Text"));
            var opened = Assert.Single(word.Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding IsChecked, ElementName=PickedOptionsToggle}", (string)opened.Attribute("Binding"));
            Assert.Equal("True", (string)opened.Attribute("Value"));
            Assert.Equal("{Binding Pad_FewerOptions" + S, (string)Assert.Single(opened.Elements()).Attribute("Value"));

            // The options, under the bar: the same rows as the list's open
            // row shows.
            var options = Named(page, "PickedOptions");
            Assert.Equal("3", (string)options.Attribute("Grid.Row"));
            Assert.Equal("{Binding ShowPickedOptions, Converter={StaticResource BoolToVisibilityConverter}}", (string)options.Attribute("Visibility"));
            Assert.Equal("0,1,0,0", (string)options.Attribute("BorderThickness"));
            Assert.Equal("20,4,12,12", (string)options.Attribute("Padding"));
            var host = Assert.Single(options.Elements(), e => e.Name.LocalName == "ContentControl");
            Assert.Equal("{Binding PickedRow}", (string)host.Attribute("Content"));
            // The template is the host's only while it has a row: set
            // outright, it is built for no row too, and its lookups through
            // the row find nothing.
            Assert.Null(host.Attribute("ContentTemplate"));
            var hostStyle = Assert.Single(host.Descendants(), e => e.Name.LocalName == "Style");
            Assert.Equal("{StaticResource MappingRowOptions}", (string)Assert.Single(hostStyle.Elements(),
                e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == "ContentTemplate").Attribute("Value"));
            var noRow = Assert.Single(hostStyle.Descendants(), e => e.Name.LocalName == "Trigger");
            Assert.Equal("Content", (string)noRow.Attribute("Property"));
            Assert.Equal("{x:Null}", (string)noRow.Attribute("Value"));
            var none = Assert.Single(noRow.Elements());
            Assert.Equal("ContentTemplate", (string)none.Attribute("Property"));
            Assert.Equal("{x:Null}", (string)none.Attribute("Value"));

            // The board's layout of those rows, which they take from this
            // host: columns of 380 at least with 40 between and no space
            // between rows, each row 44 tall with its reset at its end, a
            // label of 190 with 10 after it, and a slider that fills its
            // row from 60.
            var grid = KeyedIn(options, "RowOptionsGrid");
            Assert.Equal("controls:TileGridPanel", (string)grid.Attribute("TargetType"));
            Assert.Equal("380", SetterOf(grid, "MinItemWidth"));
            Assert.Equal("40", SetterOf(grid, "Gap"));
            Assert.Equal("0", SetterOf(grid, "RowGap"));
            Assert.Equal("True", SetterOf(grid, "FillRows"));
            Assert.DoesNotContain(grid.Elements(), e => (string)e.Attribute("Property") == "MaxColumns");
            var optionLine = KeyedIn(options, "RowOptionLine");
            Assert.Equal("controls:FlexLinePanel", (string)optionLine.Attribute("TargetType"));
            Assert.Equal("44", SetterOf(optionLine, "MinLineHeight"));
            Assert.Equal("True", SetterOf(optionLine, "EndsApart"));
            Assert.Equal("0", SetterOf(optionLine, "Gap"));
            var optionLabel = KeyedIn(options, "RowOptionLabel");
            Assert.Equal("{StaticResource RowOptionLabel}", (string)optionLabel.Attribute("BasedOn"));
            Assert.Equal("190", SetterOf(optionLabel, "Width"));
            Assert.Equal("0,0,10,0", SetterOf(optionLabel, "Margin"));
            Assert.Equal("10,0,0,0", KeyedIn(options, "RowOptionResetMargin").Value);
            // 190, 10, the picker, 10 and the reset's 32 are a column's 380.
            Assert.Equal("138", SetterOf(KeyedIn(options, "RowOptionModePicker"), "MinWidth"));
            Assert.Equal("138", KeyedIn(options, "RowOptionCombineMinWidth").Value);
            foreach (string key in new[] { "RowOptionSlider", "RowOptionDeadzoneSlider" })
            {
                var slider = KeyedIn(options, key);
                Assert.Equal("60", SetterOf(slider, "controls:FlexLinePanel.Basis"));
                Assert.Equal("1", SetterOf(slider, "controls:FlexLinePanel.Grow"));
                Assert.DoesNotContain(slider.Elements(), e => (string)e.Attribute("Property") == "Width");
            }
            // The deadzone is second, and Add Source is the bar's.
            Assert.Equal("Visible", KeyedIn(options, "RowOptionDeadzoneEarly").Value);
            Assert.Equal("Collapsed", KeyedIn(options, "RowOptionDeadzoneLate").Value);
            Assert.Equal("Collapsed", KeyedIn(options, "RowOptionAddSource").Value);

            string code = CodeBehind();
            Assert.Matches(@"private void PickedBlock_GotFocus\(object sender, KeyboardFocusChangedEventArgs e\)\s*\{\s*if \(\(sender as FrameworkElement\)\?\.DataContext is MappingItem row && DataContext is PadViewModel vm\)\s*vm\.PickedTarget = row\.TargetSettingName;", code);
            Assert.Matches(@"private void PickedAddSource_Click\(object sender, RoutedEventArgs e\)\s*\{\s*if \(DataContext is PadViewModel vm\) vm\.IsPickedOptionsOpen = true;", code);
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
        /// take the button from under the pointer before its click. The
        /// chevron opens a closed row and closes the open one.</summary>
        [Fact]
        public void TheLinesButtonsLeaveTheRowClosed()
        {
            string code = CodeBehind();
            Assert.Matches(@"if \(row\.IsSelected \|\| MappingDataGrid == null\) return;[\s\S]{0,400}?if \(IsInsideButton\(e\.OriginalSource as DependencyObject, row\)\) return;\s*MappingDataGrid\.SelectedItem = mi;", code);
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
