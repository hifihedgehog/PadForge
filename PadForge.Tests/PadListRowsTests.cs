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

        private static XElement Keyed(XDocument page, string key)
            => Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Key") == key);

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
                "Pad_Touchpad", "Pad_Gyro_Passthrough_Header",
            }, bindings.Skip(1).Select(b => (string)b.Attribute("Path")));
            Assert.All(bindings.Skip(1), b => Assert.Equal("{x:Static strings:Strings.Instance}", (string)b.Attribute("Source")));
        }

        /// <summary>A group's fold is kept by category on the view model,
        /// so it holds when the table builds its groups again. Every group
        /// starts open.</summary>
        [Fact]
        public void AGroupsFoldIsKeptByCategory()
        {
            var vm = new PadViewModel(0);
            foreach (MappingCategory category in Enum.GetValues(typeof(MappingCategory)))
                Assert.True(vm.IsListGroupOpen(category));

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
            var count = Assert.Single(style.Descendants(), e => (string)e.Attribute("Text") == "{Binding ItemCount}");
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)count.Attribute("Foreground"));
            Assert.Equal("13", (string)count.Attribute("FontSize"));

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
        /// itself, since the table's lines belong to a row's cells and a
        /// closed row hides them.</summary>
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

        /// <summary>A closed row's line: the name in a column of 200 and
        /// the 12 after it, the source as a chip with the device beside it
        /// and the fan-in tail after that, the meter 96 wide on the row's
        /// live level, then Record, Clear and the chevron, 12 apart.</summary>
        [Fact]
        public void AClosedRowIsOneLine_AsTheListBoardDrawsIt()
        {
            var page = Page();
            var line = Named(page, "CompactLine");
            Assert.Equal("Border", line.Name.LocalName);
            Assert.Equal("Collapsed", (string)line.Attribute("Visibility"));
            // As wide as the table's viewport and still when the table
            // scrolls sideways, so its buttons stay at the card's edge when
            // an open row's cells make the rows wider than the card.
            Assert.Equal("{Binding ViewportWidth, RelativeSource={RelativeSource AncestorType={x:Type ScrollViewer}}}",
                (string)line.Attribute("Width"));
            Assert.Equal("Left", (string)line.Attribute("HorizontalAlignment"));
            Assert.Equal("Vertical", (string)line.Attribute("SelectiveScrollingGrid.SelectiveScrollingOrientation"));

            var grid = Assert.Single(line.Elements());
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
            Assert.All(buttons, b => Assert.Equal("{StaticResource RowIconButton}", (string)b.Attribute("Style")));
            Assert.Equal("{Binding ToggleRecordCommand}", (string)buttons[0].Attribute("Command"));
            Assert.Equal("{Binding IsRecordable}", (string)buttons[0].Attribute("IsEnabled"));
            Assert.Equal("{Binding RecordButtonText, TargetNullValue=''}", (string)buttons[0].Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding ClearCommand}", (string)buttons[1].Attribute("Command"));
            Assert.Equal("{Binding Common_Clear" + S, (string)buttons[1].Attribute("AutomationProperties.Name"));
            Assert.Equal("MappingRowOpen_Click", (string)buttons[2].Attribute("Click"));
            Assert.Equal("{Binding Pad_HeaderOptions" + S, (string)buttons[2].Attribute("AutomationProperties.Name"));

            // The buttons: the quiet reset's size with the glyph in the
            // text color, 12 apart, and no Tab stops, since the row is one.
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

        /// <summary>A click on a closed row opens it, and a click on one of
        /// the line's own buttons leaves it closed: opening the row would
        /// take the button from under the pointer before its click.</summary>
        [Fact]
        public void TheLinesButtonsLeaveTheRowClosed()
        {
            string code = CodeBehind();
            Assert.Matches(@"if \(row\.IsSelected \|\| MappingDataGrid == null\) return;[\s\S]{0,400}?if \(IsInsideButton\(e\.OriginalSource as DependencyObject, row\)\) return;\s*MappingDataGrid\.SelectedItem = mi;", code);
            Assert.Matches(@"private void MappingRowOpen_Click\(object sender, RoutedEventArgs e\)\s*\{\s*if \(MappingDataGrid != null && \(sender as FrameworkElement\)\?\.DataContext is MappingItem mi\)\s*MappingDataGrid\.SelectedItem = mi;", code);
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
