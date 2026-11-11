using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Data;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Buttons section's two views (#574). One mapping table serves both:
    /// under the picture of the controller it holds the rows of the control
    /// last picked there, and in the list it holds every row. These hold the
    /// table's rows to the view and the pick, the line under the picture to
    /// what is being recorded, the two output cards to their fold, and the
    /// motor tests to their rows of the Force Feedback card.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PadButtonsViewTests : IDisposable
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

        private const string S = ", Source={x:Static strings:Strings.Instance}}";

        /// <summary>The rows the table shows now, by target.</summary>
        private static List<string> Shown(PadViewModel vm)
            => CollectionViewSource.GetDefaultView(vm.Mappings).Cast<MappingItem>()
                .Select(m => m.TargetSettingName).ToList();

        private static List<string> RowsOf(PadViewModel vm, MappingCategory category)
            => vm.Mappings.Where(m => m.Category == category).Select(m => m.TargetSettingName).ToList();

        // ── the table's rows ───────────────────────────────────────────

        /// <summary>Under the picture the table holds the picked control's
        /// rows and no others: nothing before a pick, the one row of a
        /// button, both axes of a stick, the four directions of the D-pad.
        /// A direction's own name picks its row. In the list it holds every
        /// row whatever is picked.</summary>
        [Fact]
        public void UnderThePicture_TheTableHoldsThePickedControlsRows()
        {
            var vm = new PadViewModel(0);
            Assert.True(vm.Mappings.Count > 10);
            vm.ApplyMappingPickerFilter();
            Assert.True(vm.IsButtonsPictureView);
            Assert.Empty(Shown(vm));

            var buttons = RowsOf(vm, MappingCategory.Buttons);
            var leftStick = RowsOf(vm, MappingCategory.LeftStick);
            var rightStick = RowsOf(vm, MappingCategory.RightStick);
            var dpad = RowsOf(vm, MappingCategory.DPad);
            var triggers = RowsOf(vm, MappingCategory.Triggers);
            Assert.Equal(2, leftStick.Count);
            Assert.Equal(2, rightStick.Count);
            Assert.Equal(4, dpad.Count);
            Assert.Equal(2, triggers.Count);

            vm.PickedTarget = buttons[0];
            Assert.Equal(new[] { buttons[0] }, Shown(vm));
            vm.PickedTarget = triggers[1];
            Assert.Equal(new[] { triggers[1] }, Shown(vm));

            vm.PickedTarget = leftStick[1];
            Assert.Equal(leftStick, Shown(vm));
            vm.PickedTarget = dpad[2];
            Assert.Equal(dpad, Shown(vm));
            vm.PickedTarget = rightStick[0];
            Assert.Equal(rightStick, Shown(vm));

            // A stick's direction has a name of its own, which the picture
            // sends for a click on that side of the stick.
            var bipolar = vm.Mappings.First(m => m.Category == MappingCategory.LeftStick && m.HasNegDirection);
            vm.PickedTarget = bipolar.NegSettingName;
            Assert.Equal(leftStick, Shown(vm));
            Assert.Same(bipolar, vm.RowOfTarget(bipolar.NegSettingName));

            vm.PickedTarget = "NoSuchTarget";
            Assert.Empty(Shown(vm));
            vm.PickedTarget = null;
            Assert.Empty(Shown(vm));

            // The list holds every row, picked or not.
            vm.PickedTarget = buttons[0];
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.Equal(vm.Mappings.Select(m => m.TargetSettingName), Shown(vm));
            vm.PickedTarget = dpad[0];
            Assert.Equal(vm.Mappings.Count, Shown(vm).Count);

            // Back on the picture the table holds the control that was open
            // in the list. Output is the picture too.
            vm.SelectedConfigTab = PadViewModel.PictureTabIndex;
            Assert.Equal(dpad, Shown(vm));
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            vm.SelectedConfigTab = PadViewModel.OutputTabIndex;
            Assert.Equal(dpad, Shown(vm));
        }

        /// <summary>The page's own handlers find the rows of the view it is
        /// about to draw: the rows change before the tab's change and the
        /// pick's change are raised.</summary>
        [Fact]
        public void TheRowsChangeBeforeTheChangeIsRaised()
        {
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            string button = RowsOf(vm, MappingCategory.Buttons)[0];
            var seen = new List<(string Property, int Rows)>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PadViewModel.PickedTarget) or nameof(PadViewModel.SelectedConfigTab)
                    or nameof(PadViewModel.ButtonsViewIndex))
                    seen.Add((e.PropertyName, Shown(vm).Count));
            };

            vm.PickedTarget = button;
            Assert.Equal((nameof(PadViewModel.PickedTarget), 1), Assert.Single(seen));

            seen.Clear();
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.Equal(new[] { nameof(PadViewModel.SelectedConfigTab), nameof(PadViewModel.ButtonsViewIndex) },
                seen.Select(s => s.Property));
            Assert.All(seen, s => Assert.Equal(vm.Mappings.Count, s.Rows));

            // The same pick again raises nothing, and a pick inside the
            // family already shown leaves the rows alone.
            seen.Clear();
            vm.PickedTarget = vm.PickedTarget;
            Assert.Empty(seen);
        }

        /// <summary>A search typed in the list narrows the list and what its
        /// pickers offer. Under the picture its box is not on the page, so
        /// it narrows neither the picked control's rows nor the pickers
        /// there, and it is in force again when the list comes back.</summary>
        [Fact]
        public void TheListsSearchDoesNotReachThePicture()
        {
            var vm = new PadViewModel(0);
            string button = RowsOf(vm, MappingCategory.Buttons)[0];
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            vm.MappingInputSearch = "no row is named this";
            Assert.Empty(Shown(vm));
            Assert.NotNull(vm.SlotAvailableInputsView.Filter);

            vm.PickedTarget = button;
            vm.SelectedConfigTab = PadViewModel.PictureTabIndex;
            Assert.Equal(new[] { button }, Shown(vm));
            Assert.Null(vm.SlotAvailableInputsView.Filter);

            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.Empty(Shown(vm));
            Assert.NotNull(vm.SlotAvailableInputsView.Filter);
            vm.MappingInputSearch = "";
            Assert.Equal(vm.Mappings.Count, Shown(vm).Count);
        }

        // ── the line under the picture ─────────────────────────────────

        /// <summary>The line says how to map a control, then which control a
        /// recording waits on, then Map All's prompt while that runs. The
        /// control being recorded becomes the picked one, so the table under
        /// the picture follows a recording started anywhere.</summary>
        [Fact]
        public void TheLineUnderThePictureFollowsTheRecording()
        {
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            var strings = PadForge.Resources.Strings.Strings.Instance;
            var row = vm.Mappings.First(m => m.Category == MappingCategory.Triggers);
            Assert.False(vm.IsListening);
            Assert.Equal(strings.Pad_PictureHint, vm.PictureLineText);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.CurrentRecordingTarget = row.TargetSettingName;
            Assert.True(vm.IsListening);
            Assert.Equal(string.Format(strings.Pad_Listening_Format, row.TargetLabel), vm.PictureLineText);
            Assert.Contains(row.TargetLabel, vm.PictureLineText);
            Assert.Contains(nameof(PadViewModel.IsListening), raised);
            Assert.Contains(nameof(PadViewModel.PictureLineText), raised);
            Assert.Equal(row.TargetSettingName, vm.PickedTarget);
            Assert.Equal(new[] { row.TargetSettingName }, Shown(vm));

            // The recording ends. The control stays picked.
            raised.Clear();
            vm.CurrentRecordingTarget = null;
            Assert.False(vm.IsListening);
            Assert.Equal(strings.Pad_PictureHint, vm.PictureLineText);
            Assert.Contains(nameof(PadViewModel.PictureLineText), raised);
            Assert.Equal(row.TargetSettingName, vm.PickedTarget);

            // Map All's prompt is the line while it runs, with its count.
            vm.MapAllPromptText = "Press: A  (1/24)";
            Assert.True(vm.IsListening);
            Assert.Equal("Press: A  (1/24)", vm.PictureLineText);
            vm.MapAllPromptText = null;
            Assert.False(vm.IsListening);

            Assert.Equal("Click a control, then press the input that should drive it.", strings.Pad_PictureHint);
            Assert.Equal("Listening for {0}. Press the input on your device.", strings.Pad_Listening_Format);
        }

        /// <summary>The line in the page: one height whether it gives the
        /// hint or a recording, its text and its Stop bound to the view
        /// model, and shown with the picture only. Stop ends Map All when it
        /// runs and otherwise cancels the recording the way a second click
        /// on the control does.</summary>
        [Fact]
        public void TheLineHasOneHeight_AndStopCancelsTheRecording()
        {
            var page = Page();
            var line = Named(page, "PictureLine");
            Assert.Equal("Border", line.Name.LocalName);
            Assert.Equal("36", (string)line.Attribute("MinHeight"));
            Assert.Equal("1", (string)line.Attribute("Grid.Row"));
            Assert.Equal("{Binding IsButtonsPictureView, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)line.Attribute("Visibility"));
            var text = Named(page, "PictureLineText");
            Assert.Equal("{Binding PictureLineText}", (string)text.Attribute("Text"));
            Assert.Equal("Pad_PictureLine", Id(text));
            Assert.Contains(line, text.Ancestors());
            var stop = Named(page, "ListeningStopButton");
            Assert.Equal("Pad_ListeningStop", Id(stop));
            Assert.Equal("{Binding Common_Stop" + S, (string)stop.Attribute("Content"));
            Assert.Equal("ListeningStop_Click", (string)stop.Attribute("Click"));
            Assert.Equal("{Binding IsListening, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)stop.Attribute("Visibility"));
            Assert.Contains(line, stop.Ancestors());

            string code = CodeBehind();
            Assert.Matches(@"private void ListeningStop_Click\(object sender, RoutedEventArgs e\)\s*\{\s*if \(DataContext is not PadViewModel vm\) return;\s*if \(vm\.IsMapAllActive\)\s*\{\s*vm\.StopMapAll\(\);\s*return;\s*\}\s*string target = vm\.CurrentRecordingTarget;\s*if \(!string\.IsNullOrEmpty\(target\)\)\s*ControllerElementRecordRequested\?\.Invoke\(this, target\);", code);
            // A click on the picture picks the control before it asks for a
            // recording, so the bar shows a control no device can record.
            Assert.Matches(@"if \(DataContext is PadViewModel pickVm\)\s*pickVm\.PickedTarget = targetName;\s*ControllerElementRecordRequested\?\.Invoke\(this, targetName\);", code);
            // The window still answers the page's event by cancelling the
            // recording of the control it is told is already recording.
            Assert.Matches(@"if \(padVm\.CurrentRecordingTarget == targetName\)\s*\{\s*_recorderService\.CancelRecording\(\);",
                ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs"));
        }

        // ── the top row ────────────────────────────────────────────────

        /// <summary>The top row: Picture and List, the layer chips, the
        /// list's tools and Map All, in that order. The tools show with the
        /// list. What acts on the whole controller (Copy, Paste, Copy From,
        /// Clear All) is in the header's More menu, which every section
        /// shows, and the row has no More button of its own.</summary>
        [Fact]
        public void TheTopRowHasTheSwitch_TheLayers_AndTheListsTools()
        {
            var page = Page();
            var top = Named(page, "ButtonsTopRow");
            Assert.Equal("0", (string)top.Attribute("Grid.Row"));
            var children = top.Elements().ToList();
            const string listOnly = "{Binding IsButtonsListView, Converter={StaticResource BoolToVisibilityConverter}}";

            Assert.Equal(new[] { "TabController", "TabMappings", "ShiftLayerTabStrip", "ListTools", null },
                children.Select(Name));
            Assert.Equal("{Binding Pad_View_Picture" + S, (string)Assert.Single(children[0].Elements()).Attribute("Text"));
            Assert.Equal("{Binding Pad_View_List" + S, (string)Assert.Single(children[1].Elements()).Attribute("Text"));
            Assert.Equal("{Binding HasShiftLayers, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)children[2].Attribute("Visibility"));
            Assert.Equal("{Binding LayerTabs}", (string)children[2].Attribute("ItemsSource"));

            // The list's tools, each as it was.
            var tools = children[3];
            Assert.Equal(listOnly, (string)tools.Attribute("Visibility"));
            Assert.Equal(new[] { "AddShiftLayerButton", "MappingFilterSearchBox", "MappingDeviceFilterToggle", null, "MappingSurfaceScopeBox" },
                tools.Elements().Select(Name));
            Assert.Equal("AddShiftLayer_Click", (string)Named(page, "AddShiftLayerButton").Attribute("Click"));
            Assert.Equal("{Binding MappingInputSearch, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
                (string)Named(page, "MappingFilterSearchBox").Attribute("Text"));
            Assert.Equal("{Binding KbmSurfaceScopeVisible, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)Named(page, "MappingSurfaceScopeBox").Attribute("Visibility"));

            // Map All, in both views.
            var mapAll = children[4];
            Assert.Equal("{Binding MapAllButtonText}", (string)mapAll.Attribute("Content"));
            Assert.Equal("MapAllToggle_Click", (string)mapAll.Attribute("Click"));
            Assert.Null(mapAll.Attribute("Visibility"));

            // The row opens no More menu: the one that holds Clear All is
            // the header's (PadSectionsTests pins it).
            Assert.DoesNotContain(top.Descendants(), e => (string)e.Attribute("Click") == "MoreMenu_Click");
            Assert.Equal("PadMoreButton", Name(Assert.Single(page.Descendants(),
                e => e.Descendants().Any(i => (string)i.Attribute("Click") == "ClearAllMappings_Click")
                     && e.Name.LocalName == "Button")));

            // The list's second row: the pipeline chips and the count of
            // one-line rows.
            Assert.Equal(listOnly, (string)Named(page, "ListChipsRow").Attribute("Visibility"));
            Assert.Contains("Binding=\"{Binding DirectMappingCount}\" Value=\"0\"", PageText());

            // What the row replaced is gone: the hint over the list, the Map
            // All pill and its prompt under the picture.
            string text = PageText();
            Assert.DoesNotContain("Pad_MappingHint", text);
            Assert.DoesNotContain("MapAllPillButton", text);
            Assert.DoesNotContain("{Binding MapAllPromptText}", text);
            Assert.Equal(1, text.Split(new[] { "Click=\"MapAllToggle_Click\"" }, StringSplitOptions.None).Length - 1);
        }

        // ── the output cards ───────────────────────────────────────────

        /// <summary>The Output tab's cards fold to one line each under the
        /// picture, the lines of one card: the part's own title, a hint,
        /// and the value it holds. Each keeps its gate, its Reset All and
        /// every row it had.</summary>
        [Fact]
        public void TheOutputCardsFoldUnderThePicture()
        {
            var page = Page();
            var group = Named(page, "OutputGroup");
            Assert.Equal("3", (string)group.Attribute("Grid.Row"));
            Assert.Equal("{StaticResource BareGroupBox}", (string)group.Attribute("Style"));
            // The parts stack at their full height in one list. The page
            // around them scrolls, so they have no scroll viewer of their
            // own.
            var column = Assert.Single(group.Elements());
            Assert.Equal("StackPanel", column.Name.LocalName);
            var list = Assert.Single(column.Elements());
            Assert.Equal("FoldList", list.Name.LocalName);
            var cards = list.Elements().ToList();
            Assert.All(cards, c => Assert.Equal("FoldCard", c.Name.LocalName));
            Assert.Equal(new[] { "SocdFoldCard", "KbmSocdFoldCard", "KeepAwakeFoldCard" }, cards.Select(Name));
            Assert.Equal(new[] { "Pad.Output.Socd", "Pad.Output.KbmSocd", "Pad.Output.KeepAwake" },
                cards.Select(c => (string)c.Attribute("StateKey")));
            Assert.Equal(new[] { "Pad_Socd_Card_Header", "Pad_Socd_Card_Header", "Pad_KeepAwake_Card_Header" }.Select(k => "{Binding " + k + S),
                cards.Select(c => (string)c.Attribute("Header")));
            Assert.Equal(new[] { "Pad_Socd_FoldHint", "Pad_Kbm_Socd_FoldHint", "Pad_KeepAwake_FoldHint" }.Select(k => "{Binding " + k + S),
                cards.Select(c => (string)c.Attribute("Hint")));
            Assert.Equal(new[] { "{Binding SocdSummary}", "{Binding KbmConfig.SocdSummary}", "{Binding KeepAwakeSummary}" },
                cards.Select(c => (string)c.Attribute("Summary")));
            Assert.Equal(new[] { "SocdCardVisible", "KbmSocdEditorVisible", "KeepAwakeCardVisible" }
                    .Select(k => "{Binding " + k + ", Converter={StaticResource BoolToVisibilityConverter}}"),
                cards.Select(c => (string)c.Attribute("Visibility")));

            // Each part's Reset All stands at the end of its line while it
            // is open, its description is the line's tooltip, and its body
            // is its rows alone.
            string[] resets = { "{Binding ResetSocdCardCommand}", "{Binding KbmConfig.ResetSocdCommand}", "{Binding ResetKeepAwakeCardCommand}" };
            string[] descriptions = { "Pad_Socd_Card_Desc", "Pad_Kbm_Socd_Card_Desc", "Pad_KeepAwake_Card_Desc" };
            for (int i = 0; i < cards.Count; i++)
            {
                var action = Assert.Single(Assert.Single(cards[i].Elements(), e => e.Name.LocalName == "FoldCard.HeaderAction").Elements());
                Assert.Equal(resets[i], (string)action.Attribute("Command"));
                Assert.Equal("{StaticResource ResetAllButton}", (string)action.Attribute("Style"));
                Assert.Equal("{Binding " + descriptions[i] + S, (string)cards[i].Attribute("Description"));
                var body = Assert.Single(cards[i].Elements(), e => e.Name.LocalName == "StackPanel").Elements().ToList();
                Assert.True(body.Count > 1);
                Assert.DoesNotContain(body, e => ((string)e.Attribute("Style") ?? "").Contains("ResetAllButton")
                    || ((string)e.Attribute("Text") ?? "").Contains(descriptions[i]));
            }
            string text = PageText();
            foreach (string kept in new[]
            {
                "SelectedValue=\"{Binding SocdMode, Mode=TwoWay}\"", "Command=\"{Binding AddSocdPairCommand}\"",
                "SelectedValue=\"{Binding KbmConfig.SocdMode, Mode=TwoWay}\"", "Command=\"{Binding KbmConfig.AddSocdPairCommand}\"",
                "IsChecked=\"{Binding KeepAwakeEnabled, Mode=TwoWay}\"", "IsChecked=\"{Binding KeepAwakeMotion, Mode=TwoWay}\"",
                "SelectedValue=\"{Binding KeepAwakeAxis, Mode=TwoWay}\"", "Command=\"{Binding ResetKeepAwakeDeflectionCommand}\"",
                "Command=\"{Binding ResetSocdModeCommand}\"", "Command=\"{Binding KbmConfig.ResetSocdModeCommand}\"",
            })
                Assert.Equal(1, text.Split(new[] { kept }, StringSplitOptions.None).Length - 1);
            // The tab's own heading and subtitle went with the tab.
            Assert.DoesNotContain("Pad_Output_Subtitle", text);
        }

        /// <summary>What a folded card says: the SOCD mode's name, On or Off
        /// for Keep Controller Awake, and the same for the Keyboard + Mouse
        /// card from its own config. Each is raised with the setting it
        /// reads.</summary>
        [Fact]
        public void AFoldedCardSaysItsValue()
        {
            var strings = PadForge.Resources.Strings.Strings.Instance;
            var kbm = new KbmSlotConfig();
            Assert.Equal(strings.Pad_Kbm_Socd_Mode_Off_Name, kbm.SocdSummary);
            var raised = new List<string>();
            kbm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            kbm.SocdMode = "LastWins";
            Assert.Equal(strings.Pad_Kbm_Socd_Mode_LastWins_Name, kbm.SocdSummary);
            Assert.Contains(nameof(KbmSlotConfig.SocdSummary), raised);
            kbm.SocdMode = "Neutral";
            Assert.Equal(strings.Pad_Kbm_Socd_Mode_Neutral_Name, kbm.SocdSummary);

            // The gamepad card and Keep Controller Awake read the slot's
            // mapping set, which a bare view model does not have: both say
            // the off value, and their setters raise the folded value.
            var vm = new PadViewModel(0);
            Assert.Equal(strings.Pad_Kbm_Socd_Mode_Off_Name, vm.SocdSummary);
            Assert.Equal(strings.Common_Off, vm.KeepAwakeSummary);
            string source = ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "PadViewModel.cs");
            Assert.Matches(@"set\.SocdMode = value;\s*OnPropertyChanged\(nameof\(SocdMode\)\);\s*OnPropertyChanged\(nameof\(SocdSummary\)\);", source);
            Assert.Matches(@"set\.KeepAwakeEnabled = value;\s*OnPropertyChanged\(nameof\(KeepAwakeEnabled\)\);\s*OnPropertyChanged\(nameof\(KeepAwakeSummary\)\);", source);
            Assert.Matches(@"OnPropertyChanged\(nameof\(AvailableSlotSocdModes\)\);\s*OnPropertyChanged\(nameof\(SocdSummary\)\);", source);
            Assert.Matches(@"OnPropertyChanged\(nameof\(KeepAwakeEnabled\)\);\s*OnPropertyChanged\(nameof\(KeepAwakeSummary\)\);\s*OnPropertyChanged\(nameof\(KeepAwakeMotion\)\);", source);
            // A language change re-words the line and the three values.
            Assert.Matches(@"OnPropertyChanged\(nameof\(PictureLineText\)\);\s*OnPropertyChanged\(nameof\(SocdSummary\)\);\s*OnPropertyChanged\(nameof\(KeepAwakeSummary\)\);\s*_kbmConfig\?\.RaiseLocalizedChanged\(\);", source);
        }

        // ── the table in the page ──────────────────────────────────────

        /// <summary>The table is one element in both views. It folds away
        /// while it has no rows, takes the picked row as its selected row,
        /// and its columns are measured once, in the list, where every row
        /// is there to measure.</summary>
        [Fact]
        public void OneTableServesBothViews()
        {
            var page = Page();
            var table = Named(page, "MappingDataGrid");
            Assert.Equal("2", (string)table.Attribute("Grid.Row"));
            Assert.Equal("{Binding Mappings}", (string)table.Attribute("ItemsSource"));
            var trigger = Assert.Single(table.Elements().First(e => e.Name.LocalName == "DataGrid.Style").Descendants(),
                e => e.Name.LocalName == "Trigger");
            Assert.Equal("HasItems", (string)trigger.Attribute("Property"));
            Assert.Equal("False", (string)trigger.Attribute("Value"));
            Assert.Equal("Collapsed", (string)Assert.Single(trigger.Elements()).Attribute("Value"));

            // The picture's view is a page that scrolls, so an open row or
            // an open card never squeezes the picture. The page's code
            // gives the picture's row a height of its own there, and gives
            // the table's row the room in the list, where the page does not
            // scroll and the table scrolls its own rows.
            var scroll = Named(page, "ButtonsScroll");
            Assert.Equal("ScrollViewer", scroll.Name.LocalName);
            Assert.Equal("ButtonsRoot_SizeChanged", (string)scroll.Attribute("SizeChanged"));
            // The section: the top row, the list's chips, and the view's
            // content with the room that is left.
            Assert.Equal(new[] { "Auto", "Auto", "*" },
                Named(page, "MappingsTabRoot").Elements().First(e => e.Name.LocalName == "Grid.RowDefinitions")
                    .Elements().Select(r => (string)r.Attribute("Height")));
            Assert.Equal("2", (string)scroll.Attribute("Grid.Row"));
            // The page inside: the picture, the line, the table, the cards,
            // and an empty row that takes the viewport's leftover pixels.
            var rows = Named(page, "ButtonsPage").Elements().First(e => e.Name.LocalName == "Grid.RowDefinitions")
                .Elements().ToList();
            Assert.Equal(5, rows.Count);
            Assert.All(rows, r => Assert.Equal("Auto", (string)r.Attribute("Height")));
            Assert.Equal("ButtonsPictureRow", Name(rows[0]));
            Assert.Equal("ButtonsTableRow", Name(rows[2]));
            Assert.Equal("ButtonsFillRow", Name(rows[4]));
            Assert.DoesNotContain(Named(page, "ButtonsPage").Elements(),
                e => (string)e.Attribute("Grid.Row") == "4");
            Assert.Equal("MappingDataGrid_PreviewMouseWheel", (string)table.Attribute("PreviewMouseWheel"));

            string code = CodeBehind();
            Assert.Matches(@"if \(vm\.IsButtonsListView\)\s*\{\s*ButtonsScroll\.VerticalScrollBarVisibility = ScrollBarVisibility\.Disabled;\s*ButtonsScroll\.Padding = new Thickness\(0\);\s*ButtonsPictureRow\.Height = GridLength\.Auto;\s*ButtonsTableRow\.Height = new GridLength\(1, GridUnitType\.Star\);\s*ButtonsFillRow\.Height = GridLength\.Auto;\s*MappingDataGrid\.VerticalScrollBarVisibility = ScrollBarVisibility\.Visible;\s*return;\s*\}", code);
            Assert.Contains("ButtonsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;", code);
            // In the picture's view the table is as tall as its rows, and
            // the empty last row takes the viewport's leftover. In the list
            // the table is the one row that stretches.
            Assert.Matches(@"ButtonsTableRow\.Height = GridLength\.Auto;[\s\S]{0,700}?ButtonsFillRow\.Height = new GridLength\(1, GridUnitType\.Star\);\s*MappingDataGrid\.VerticalScrollBarVisibility = ScrollBarVisibility\.Disabled;", code);
            // A pick made on the picture leaves the page where it is: the
            // selected row is scrolled into view in the list only.
            Assert.Matches(@"bool underPicture = DataContext is PadViewModel viewVm && viewVm\.IsButtonsPictureView;\s*if \(!underPicture && e\.AddedItems\.Count > 0 && e\.AddedItems\[0\] is MappingItem sel\)", code);
            Assert.Matches(@"double folded = SteadyFoldedHeight\(vm\.OutputTabVisible, FoldedOutputHeight\(\), ref _foldedOutputMeasured\);\s*double picture = PictureHeightFor\(viewport, around, folded, VisualTreeHelper\.GetDpi\(ButtonsScroll\)\.DpiScaleY\);\s*ButtonsPictureRow\.Height = new GridLength\(picture\);", code);
            // Cards that are hidden or not laid out have no height to read.
            Assert.Matches(@"if \(OutputGroup == null \|\| !OutputGroup\.IsVisible \|\| OutputGroup\.ActualHeight <= 0\) return double\.NaN;", code);
            // The cards' folded height is the group as it stands less what
            // each open card shows, and a change of the group's height sizes
            // the picture again.
            Assert.Equal("OutputGroup_SizeChanged", (string)Named(page, "OutputGroup").Attribute("SizeChanged"));
            Assert.Matches(@"private void OutputGroup_SizeChanged\(object sender, SizeChangedEventArgs e\)\s*\{\s*if \(e\.HeightChanged\) SyncButtonsLayout\(\);", code);
            Assert.Matches(@"double height = OutputGroup\.ActualHeight \+ OutputGroup\.Margin\.Top \+ OutputGroup\.Margin\.Bottom;\s*foreach \(var card in new\[\] \{ SocdFoldCard, KbmSocdFoldCard, KeepAwakeFoldCard \}\)\s*\{\s*if \(card == null \|\| !card\.IsVisible \|\| !card\.IsExpanded\) continue;\s*if \(card\.Template\?\.FindName\(""ExpandSite"", card\) is FrameworkElement open\)\s*height -= open\.ActualHeight;", code);
            Assert.Contains("<Border x:Name=\"ExpandSite\"", ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml"));
            // Under the picture the wheel over the table moves the page, and
            // never reaches a closed picker in the row.
            Assert.Matches(@"if \(DataContext is not PadViewModel vm \|\| !vm\.IsButtonsPictureView\) return;\s*e\.Handled = true;\s*ButtonsScroll\.RaiseEvent\(new MouseWheelEventArgs\(e\.MouseDevice, e\.Timestamp, e\.Delta\)", code);
            // A new controller on the page gets its rows before its first
            // frame.
            Assert.Matches(@"_currentPadVm\?\.ApplyMappingPickerFilter\(\);\s*ApplyViewMode\(\);\s*SyncSectionSelection\(\);\s*SyncButtonsLayout\(\);\s*SyncPickedRowSelection\(\);", code);
            Assert.Matches(@"else if \(e\.PropertyName == nameof\(PadViewModel\.ButtonsViewIndex\)\)\s*\{\s*OnButtonsViewChanged\(\);\s*\}\s*else if \(e\.PropertyName == nameof\(PadViewModel\.PickedTarget\)\)\s*\{\s*SyncPickedRowSelection\(\);", code);
            Assert.Matches(@"private void OnButtonsViewChanged\(\)\s*\{\s*SyncButtonsLayout\(\);\s*FitMappingColumnsOnce\(\);\s*SyncPickedRowSelection\(\);", code);
            // The fit runs in the list only, and only while a column is
            // still flexible.
            Assert.Matches(@"private void MappingDataGrid_Loaded\(object sender, RoutedEventArgs e\) => FitMappingColumnsOnce\(\);", code);
            Assert.Matches(@"if \(grid == null \|\| !grid\.IsLoaded \|\| grid\.Items\.Count == 0\) return;\s*if \(DataContext is not PadViewModel vm \|\| !vm\.IsButtonsListView\) return;", code);
            Assert.Matches(@"if \(!anyFlexible\) return;", code);
            // A row selected in the table is the picked control.
            Assert.Matches(@"if \(e\.AddedItems\.Count > 0 && e\.AddedItems\[0\] is MappingItem picked\s*&& DataContext is PadViewModel pickVm\)\s*pickVm\.PickedTarget = picked\.TargetSettingName;", code);
            // A chip on the picture picks its control and stays on the
            // picture.
            Assert.Matches(@"private void OnAnnotationChipNavigate\(object sender, string targetSettingName\)\s*\{\s*if \(DataContext is not PadViewModel vm\) return;\s*if \(vm\.RowOfTarget\(targetSettingName\) == null\) return;\s*vm\.PickedTarget = targetSettingName;", code);
            Assert.DoesNotContain("vm.SelectedConfigTab = 2;", code);
        }

        /// <summary>The picture takes what the viewport has left after its
        /// line and the folded output cards, so a page with nothing picked
        /// fits and has nothing to scroll. The numbers are the ones read
        /// off the running page on 2026-11-11: a section 765 high, 48 for
        /// the line, and two folded cards that need 141. The 124 the
        /// picture left under itself before cut the second card off at the
        /// viewport's foot. With one card or none the least room stays, and
        /// a short window keeps the picture's least height and scrolls.</summary>
        [Fact]
        public void ThePictureLeavesRoomForTheFoldedCards()
        {
            double twoCards = PadForge.Views.PadPage.PictureHeightFor(765, 48, 141, 1);
            Assert.Equal(574, twoCards);
            Assert.True(twoCards + 48 + 141 <= 765, "the page at rest is taller than its viewport");

            // No cards, and one card that needs less than the least room.
            Assert.Equal(591, PadForge.Views.PadPage.PictureHeightFor(765, 48, 0, 1));
            Assert.Equal(591, PadForge.Views.PadPage.PictureHeightFor(765, 48, 71, 1));

            // Three cards take more from the picture than two.
            Assert.Equal(503, PadForge.Views.PadPage.PictureHeightFor(765, 48, 212, 1));

            // A short window keeps the least picture, and the page scrolls.
            Assert.Equal(200, PadForge.Views.PadPage.PictureHeightFor(300, 48, 141, 1));

            // A scale that is no scale counts as one.
            Assert.Equal(574, PadForge.Views.PadPage.PictureHeightFor(765, 48, 141, 0));
            Assert.Equal(574, PadForge.Views.PadPage.PictureHeightFor(765, 48, 141, double.NaN));
        }

        /// <summary>The picture's height is a whole number of the screen's
        /// pixels. At 150% the section is 1,148 pixels high, the line 72 and
        /// two folded cards 212, which leaves 864: the picture takes 862.
        /// The 575 units it took before are 862.5 pixels, which the page
        /// rounded up or down with the rows around it, and the picture
        /// moved a pixel each time a card opened.</summary>
        [Theory]
        [InlineData(1.0)]
        [InlineData(1.25)]
        [InlineData(1.5)]
        [InlineData(1.75)]
        [InlineData(2.0)]
        [InlineData(2.25)]
        public void ThePictureIsAWholeNumberOfPixelsHigh(double scale)
        {
            // What the page reports at this scale: sizes that are whole
            // pixels, given in units.
            double viewport = Math.Floor(765.33 * scale) / scale;
            double line = Math.Round(48 * scale) / scale;
            double cards = Math.Round(141.33 * scale) / scale;

            double picture = PadForge.Views.PadPage.PictureHeightFor(viewport, line, cards, scale);

            double pixels = picture * scale;
            Assert.True(Math.Abs(pixels - Math.Round(pixels)) < 1e-6, $"{pixels} pixels at {scale}");
            double page = Math.Round(pixels) + Math.Round(line * scale) + Math.Round(cards * scale);
            Assert.True(page <= Math.Round(viewport * scale) - 2, "the page at rest has less than two pixels to spare");
            Assert.True(page >= Math.Round(viewport * scale) - 3, "the picture gave up more than it had to");
        }

        [Fact]
        public void ThePictureIs862PixelsHighAt150Percent()
        {
            double picture = PadForge.Views.PadPage.PictureHeightFor(1148 / 1.5, 72 / 1.5, 212 / 1.5, 1.5);
            Assert.Equal(862, picture * 1.5, 6);
        }

        /// <summary>The folded height the picture is sized from, through the
        /// moments the cards cannot be measured. The picture is sized when
        /// the view changes, before the cards are back on the page. Sizing
        /// from zero there left the picture 17 too tall after every visit
        /// to the list, with the second card cut off, and nothing corrected
        /// it: cards that come back at the size they had raise no size
        /// change.</summary>
        [Fact]
        public void TheFoldedHeightStandsWhileTheCardsCannotBeMeasured()
        {
            double last = double.NaN;
            // Before the first layout there is nothing to go on.
            Assert.Equal(0, PadForge.Views.PadPage.SteadyFoldedHeight(true, double.NaN, ref last));
            Assert.True(double.IsNaN(last));
            // The first layout measures two folded cards.
            Assert.Equal(141.3, PadForge.Views.PadPage.SteadyFoldedHeight(true, 141.3, ref last));
            // Coming back from the list, the cards are not on the page yet.
            Assert.Equal(141.3, PadForge.Views.PadPage.SteadyFoldedHeight(true, double.NaN, ref last));
            // An open card reads a pixel off, and the picture stays.
            Assert.Equal(141.3, PadForge.Views.PadPage.SteadyFoldedHeight(true, 142.0, ref last));
            Assert.Equal(141.3, last);
            // A controller type with no cards needs no room, and leaves the
            // measurement for the type that has them.
            Assert.Equal(0, PadForge.Views.PadPage.SteadyFoldedHeight(false, double.NaN, ref last));
            Assert.Equal(0, PadForge.Views.PadPage.SteadyFoldedHeight(false, 141.3, ref last));
            Assert.Equal(141.3, last);
            Assert.Equal(141.3, PadForge.Views.PadPage.SteadyFoldedHeight(true, double.NaN, ref last));
            // A card that went is measured when the cards' size changes.
            Assert.Equal(70.7, PadForge.Views.PadPage.SteadyFoldedHeight(true, 70.7, ref last));
            Assert.Equal(70.7, last);
        }

        // ── the motor tests ────────────────────────────────────────────

        /// <summary>Each motor's test is a button on its own row of the
        /// Force Feedback card, after the row's reset. They were capsules
        /// under the picture, and the capsules and their hover code are
        /// gone.</summary>
        [Fact]
        public void TheMotorTestsAreOnTheirRows()
        {
            var page = Page();
            foreach (var (name, id, tip, click, reset) in new[]
            {
                ("TestLeftMotorButton", "Pad_TestLeftMotor", "Pad_TestLeftMotor", "LeftMotor_Click", "{Binding ResetLeftMotorCommand}"),
                ("TestRightMotorButton", "Pad_TestRightMotor", "Pad_TestRightMotor", "RightMotor_Click", "{Binding ResetRightMotorCommand}"),
            })
            {
                var button = Named(page, name);
                Assert.Equal(id, Id(button));
                Assert.Equal(click, (string)button.Attribute("Click"));
                Assert.Equal("{Binding Pad_RumbleAudio_Test" + S, (string)button.Attribute("Content"));
                Assert.Equal("{Binding " + tip + S, (string)button.Attribute("ToolTip"));
                Assert.Equal("{Binding " + tip + S, (string)button.Attribute("AutomationProperties.Name"));
                var row = button.Parent!;
                Assert.Contains(Named(page, "TabForceFeedback"), row.Ancestors());
                var before = button.ElementsBeforeSelf().Last();
                Assert.Equal(reset, (string)before.Attribute("Command"));
            }
            string text = PageText();
            Assert.DoesNotContain("MotorBarsGrid", text);
            Assert.DoesNotContain("MotorCapsuleTemplate", text);
            string code = CodeBehind();
            Assert.DoesNotContain("Motor_MouseEnter", code);
            Assert.DoesNotContain("MotorHoverGlow", code);
            Assert.Contains("padVm.FireTestLeftMotor();", code);
            Assert.Contains("padVm.FireTestRightMotor();", code);
        }

        // ── the strings ────────────────────────────────────────────────

        /// <summary>The seven strings the two views added are in the string
        /// table with their English, and the two the old layout showed are
        /// out of it.</summary>
        [Fact]
        public void TheNewStringsAreInTheTable()
        {
            var table = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"));
            string English(string key) => (string)Assert.Single(table.Root!.Elements("data"),
                d => (string)d.Attribute("name") == key).Element("value");
            Assert.Equal("Picture", English("Pad_View_Picture"));
            Assert.Equal("List", English("Pad_View_List"));
            Assert.Equal("Click a control, then press the input that should drive it.", English("Pad_PictureHint"));
            Assert.Equal("Listening for {0}. Press the input on your device.", English("Pad_Listening_Format"));
            Assert.Equal("Which press games see when both buttons of a pair are held", English("Pad_Socd_FoldHint"));
            Assert.Equal("Which press games see when both keys of a pair are held", English("Pad_Kbm_Socd_FoldHint"));
            Assert.Equal("Holds a small stick deflection so games keep showing controller prompts", English("Pad_KeepAwake_FoldHint"));
            foreach (string gone in new[] { "Pad_MappingHint", "Pad_Output_Subtitle" })
                Assert.DoesNotContain(table.Root!.Elements("data"), d => (string)d.Attribute("name") == gone);
        }
    }
}
