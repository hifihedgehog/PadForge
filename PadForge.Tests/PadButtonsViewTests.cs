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
        /// button, both axes of a stick and then its click, the four
        /// directions of the D-pad.
        /// A direction's own name picks its row. In the list it holds every
        /// row whatever is picked.</summary>
        [Fact]
        public void UnderThePicture_TheTableHoldsThePickedControlsRows()
        {
            var vm = new PadViewModel(0);
            Assert.True(vm.Mappings.Count > 10);
            vm.ApplyMappingPickerFilter();
            Assert.True(vm.IsButtonsPictureView);
            // The picture opens with its first control picked (#574).
            Assert.Equal(new[] { "ButtonA" }, Shown(vm));

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

            // A stick is its two axes and then its click, as the Buttons
            // board has it.
            vm.PickedTarget = leftStick[1];
            Assert.Equal(new[] { "LeftThumbButton" }.Concat(leftStick), Shown(vm));
            vm.PickedTarget = dpad[2];
            Assert.Equal(dpad, Shown(vm));
            vm.PickedTarget = rightStick[0];
            Assert.Equal(new[] { "RightThumbButton" }.Concat(rightStick), Shown(vm));
            vm.PickedTarget = "RightThumbButton";
            Assert.Equal(new[] { "RightThumbButton" }.Concat(rightStick), Shown(vm));

            // A stick's direction has a name of its own, which the picture
            // sends for a click on that side of the stick.
            var bipolar = vm.Mappings.First(m => m.Category == MappingCategory.LeftStick && m.HasNegDirection);
            vm.PickedTarget = bipolar.NegSettingName;
            Assert.Equal(new[] { "LeftThumbButton" }.Concat(leftStick), Shown(vm));
            Assert.Same(bipolar, vm.RowOfTarget(bipolar.NegSettingName));

            // A pick that names no row goes back to the first control.
            vm.PickedTarget = "NoSuchTarget";
            Assert.Equal(new[] { "ButtonA" }, Shown(vm));
            vm.PickedTarget = null;
            Assert.Equal(new[] { "ButtonA" }, Shown(vm));

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

        /// <summary>The picture always has a control picked, as the Buttons
        /// board opens with A picked (#574): the slot's first row when its
        /// rows are first shown, after a change of its type, and in place of
        /// a pick that names no row. The list leaves a pick alone, and the
        /// picture picks again when it comes back. The pick is the view
        /// model's alone and never saved.</summary>
        [Fact]
        public void ThePictureAlwaysHasAControlPicked()
        {
            var vm = new PadViewModel(0);
            Assert.True(vm.IsButtonsPictureView);
            string first = vm.Mappings[0].TargetSettingName;
            Assert.Equal("ButtonA", first);
            Assert.Equal(first, vm.PickedTarget);
            Assert.True(vm.ShowPictureBar);
            Assert.Equal(new[] { first }, Shown(vm));

            // A pick that names no row, or none, goes back to the first row,
            // and is told once the rows follow it.
            var seen = new List<(string Pick, int Rows)>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PadViewModel.PickedTarget)) seen.Add((vm.PickedTarget, Shown(vm).Count));
            };
            vm.PickedTarget = "NoSuchTarget";
            Assert.Equal(first, vm.PickedTarget);
            Assert.All(seen, s => Assert.Equal((first, 1), s));
            vm.PickedTarget = "DPadUp";
            vm.PickedTarget = null;
            Assert.Equal(first, vm.PickedTarget);

            // The list leaves a pick alone, and none stays none there.
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            vm.PickedTarget = null;
            Assert.Null(vm.PickedTarget);
            vm.SelectedConfigTab = PadViewModel.PictureTabIndex;
            Assert.Equal(first, vm.PickedTarget);

            // Another type's rows: the pick goes to its first row.
            vm.OutputType = PadForge.Engine.VirtualControllerType.KeyboardMouse;
            Assert.NotNull(vm.PickedRow);
            Assert.Equal(vm.Mappings.First(m => PadViewModel.RowMatchesSurface(m, "All", vm.KbmConfig)).TargetSettingName, vm.PickedTarget);
            vm.OutputType = PadForge.Engine.VirtualControllerType.Xbox;
            Assert.Equal(first, vm.PickedTarget);

            // Nothing on the setting holds a pick.
            Assert.DoesNotContain(typeof(PadSetting).GetProperties(), p => p.Name.Contains("Pick", StringComparison.Ordinal));
        }

        /// <summary>The bar has no Value on a Keyboard + Mouse or a MIDI
        /// slot, as their boards draw it (#574): a key, a mouse button or a
        /// note is on or off, and the picture lights it. Every other slot's
        /// bar has it, and a change of type says so.</summary>
        [Fact]
        public void TheBarHasAValueWhereItsBoardDrawsOne()
        {
            var vm = new PadViewModel(0);
            Assert.True(vm.ShowPickedValue);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            foreach (var (type, shown) in new[]
                     {
                         (PadForge.Engine.VirtualControllerType.KeyboardMouse, false), (PadForge.Engine.VirtualControllerType.Midi, false),
                         (PadForge.Engine.VirtualControllerType.Vr, true), (PadForge.Engine.VirtualControllerType.Extended, true),
                         (PadForge.Engine.VirtualControllerType.PlayStation, true),
                     })
            {
                raised.Clear();
                vm.OutputType = type;
                Assert.Equal(shown, vm.ShowPickedValue);
                Assert.Contains(nameof(PadViewModel.ShowPickedValue), raised);
            }
        }

        /// <summary>The picture opens with the controller's button labels
        /// shown, as the Buttons board does (#574). The labels stay the
        /// session's.</summary>
        [Fact]
        public void TheButtonLabelsAreOnAtFirst()
        {
            Assert.True(new PadViewModel(0).AnnotationOverlayEnabled);
            Assert.Contains("private bool _annotationOverlayEnabled = true;",
                ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "PadViewModel.cs"));
        }

        /// <summary>The page's own handlers find the rows of the view it is
        /// about to draw: the rows change before the tab's change and the
        /// pick's change are raised.</summary>
        [Fact]
        public void TheRowsChangeBeforeTheChangeIsRaised()
        {
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            // The first button is picked at the start, so the pick is the
            // second.
            string button = RowsOf(vm, MappingCategory.Buttons)[1];
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

        /// <summary>A slot's first section and the hint over its picture
        /// are in the words of what the picture holds, as each slot's board
        /// has them. A VR slot has a note under its card on the touch
        /// outputs Map All skips.</summary>
        [Fact]
        public void TheFirstSectionAndTheHintSayWhatThePictureHolds()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var vm = new PadViewModel(0);
            Assert.Equal(s.Pad_Section_Buttons, vm.FirstSectionName);
            Assert.Equal(s.Pad_PictureHint, vm.PictureHintText);
            Assert.False(vm.ShowVrFoot);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.OutputType = PadForge.Engine.VirtualControllerType.KeyboardMouse;
            Assert.Equal(s.Pad_Section_KeysMouse, vm.FirstSectionName);
            Assert.Equal(s.Pad_PictureHint_Kbm, vm.PictureHintText);
            Assert.Equal(s.Pad_PictureHint_Kbm, vm.PictureLineText);
            Assert.Contains(nameof(PadViewModel.FirstSectionName), raised);
            Assert.Contains(nameof(PadViewModel.PictureHintText), raised);

            vm.OutputType = PadForge.Engine.VirtualControllerType.Midi;
            Assert.Equal(s.Pad_Section_NotesControls, vm.FirstSectionName);
            Assert.Equal(s.Pad_PictureHint_Midi, vm.PictureHintText);

            raised.Clear();
            vm.OutputType = PadForge.Engine.VirtualControllerType.Vr;
            Assert.Equal(s.Pad_Section_Hands, vm.FirstSectionName);
            Assert.Equal(s.Pad_PictureHint, vm.PictureHintText);
            Assert.True(vm.ShowVrFoot);
            Assert.Contains(nameof(PadViewModel.ShowVrFoot), raised);

            Assert.Equal("Keys & Mouse", s.Pad_Section_KeysMouse);
            Assert.Equal("Notes & Controls", s.Pad_Section_NotesControls);
            Assert.Equal("Hands", s.Pad_Section_Hands);

            var page = Page();
            Assert.Equal("{Binding FirstSectionName}", (string)Named(page, "SectionButtons").Descendants().Single(e => e.Name.LocalName == "TextBlock").Attribute("Text"));
            var note = Named(page, "VrFootNote");
            Assert.Equal("{Binding Pad_Vr_MapAllSkipsTouch" + S, (string)note.Attribute("Text"));
            Assert.Equal("{Binding ShowVrFoot, Converter={StaticResource BoolToVisibilityConverter}}", (string)note.Attribute("Visibility"));
        }

        /// <summary>A recording that cannot start says why on the line
        /// under the picture, where the click was: no device of the slot is
        /// connected. The word holds until the next pick, a recording that
        /// starts, or a few seconds. The recorder says it for the slot it
        /// was asked to record on.</summary>
        [Fact]
        public void ARecordingThatCannotStartSaysWhyUnderThePicture()
        {
            var strings = PadForge.Resources.Strings.Strings.Instance;
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            vm.PickedTarget = "ButtonA";
            Assert.False(vm.ShowPictureLine);
            Assert.Null(vm.RecordRefusal);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.ShowRecordRefusal(strings.Status_NoDeviceToRecord);
            Assert.True(vm.ShowPictureLine);
            Assert.False(vm.IsListening);
            Assert.Equal(strings.Status_NoDeviceToRecord, vm.PictureLineText);
            Assert.Contains(nameof(PadViewModel.ShowPictureLine), raised);
            Assert.Contains(nameof(PadViewModel.PictureLineText), raised);
            Assert.Equal(TimeSpan.FromSeconds(6), PadViewModel.RecordRefusalShown);

            // A click on the same control asks again: the word stays.
            vm.PickedTarget = "ButtonA";
            Assert.Equal(strings.Status_NoDeviceToRecord, vm.PictureLineText);
            // Another pick clears it.
            vm.PickedTarget = "ButtonB";
            Assert.False(vm.ShowPictureLine);
            Assert.Null(vm.RecordRefusal);
            Assert.Equal(strings.Pad_PictureHint, vm.PictureLineText);

            // A recording that starts clears it, and the line says what it
            // listens for.
            vm.ShowRecordRefusal(strings.Status_NoDeviceToRecord);
            vm.CurrentRecordingTarget = "ButtonB";
            Assert.Null(vm.RecordRefusal);
            Assert.True(vm.IsListening);
            Assert.Equal(string.Format(strings.Pad_Listening_Format, "B"), vm.PictureLineText);
            vm.CurrentRecordingTarget = null;
            Assert.False(vm.ShowPictureLine);
            vm.ShowRecordRefusal(null);
            Assert.False(vm.ShowPictureLine);

            // The recorder raises it for the slot it was asked to record on,
            // which has no device connected.
            var main = new MainViewModel();
            var recorder = new PadForge.Services.RecorderService(main);
            var said = new List<(int, string)>();
            recorder.RecordingRefused += (pad, reason) => said.Add((pad, reason));
            var pad2 = main.Pads[2];
            recorder.StartRecording(pad2.Mappings.First(m => m.TargetSettingName == "ButtonA"), 2, Guid.Empty);
            Assert.False(recorder.IsRecording);
            Assert.Equal(new[] { (2, strings.Status_NoDeviceToRecord) }, said);

            // And the window shows it on that slot's line.
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Matches(@"_recorderService\.RecordingRefused \+= \(padIndex, reason\) =>\s*\{\s*if \(padIndex >= 0 && padIndex < _viewModel\.Pads\.Count\)\s*_viewModel\.Pads\[padIndex\]\.ShowRecordRefusal\(reason\);", window);
        }

        /// <summary>The line a recording shows: a row of its own between the
        /// picture and the bar, as the Buttons board draws it, so the page
        /// grows by it and the picture keeps its size when a recording
        /// starts. Its ember top line lies over the hairline under the
        /// picture, and the hairline under it is the bar's. It shows in the
        /// picture's view while a recording waits, with its text and its
        /// Stop bound to the view model. The hint that says how to map a
        /// control is the top row's, over the picture. Stop ends Map All
        /// when it runs and otherwise cancels the recording the way a
        /// second click on the control does.</summary>
        [Fact]
        public void TheListeningLineIsARowUnderThePicture_AndStopCancelsTheRecording()
        {
            var page = Page();
            var row = Named(page, "PictureLineRow");
            Assert.Same(Named(page, "ButtonsPage"), row.Parent);
            Assert.Equal("1", (string)row.Attribute("Grid.Row"));
            Assert.Equal("1,-1,1,0", (string)row.Attribute("Margin"));
            Assert.Equal("{DynamicResource CardStrokeColorDefaultBrush}", (string)row.Attribute("BorderBrush"));
            Assert.Equal("0,0,0,1", (string)row.Attribute("BorderThickness"));
            Assert.Equal("{Binding ShowPictureLine, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)row.Attribute("Visibility"));
            // Drawn after the picture, so its top line lies over the
            // picture's hairline.
            var parts = Named(page, "ButtonsPage").Elements().ToList();
            Assert.True(parts.IndexOf(row) > parts.FindIndex(e => e.Name.LocalName == "ContentTabControl"));
            var line = Assert.Single(row.Elements());
            Assert.Equal("PictureLine", Name(line));
            Assert.Equal("Border", line.Name.LocalName);
            Assert.DoesNotContain(Named(page, "PictureGround"), line.Ancestors());
            Assert.Null(line.Attribute("VerticalAlignment"));
            Assert.Null(line.Attribute("Visibility"));
            // The board's line: #24160D under a #4A2C1A top line, 10 over and
            // under its words and 20 at its sides.
            Assert.Equal("{DynamicResource EmberCardPickBrush}", (string)line.Attribute("Background"));
            Assert.Equal("{DynamicResource EmberEdgeBrush}", (string)line.Attribute("BorderBrush"));
            Assert.Equal("0,1,0,0", (string)line.Attribute("BorderThickness"));
            Assert.Equal("20,10,20,10", (string)line.Attribute("Padding"));
            Assert.Equal("44", (string)line.Attribute("MinHeight"));
            // The list has no such line: a row there shows its own recording
            // state.
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            vm.ShowRecordRefusal(PadForge.Resources.Strings.Strings.Instance.Status_NoDeviceToRecord);
            Assert.True(vm.ShowPictureLine);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.False(vm.ShowPictureLine);
            Assert.Contains(nameof(PadViewModel.ShowPictureLine), raised);
            var hint = Named(page, "PictureHint");
            Assert.Equal("{Binding PictureHintText}", (string)hint.Attribute("Text"));
            Assert.Equal("{Binding IsButtonsPictureView, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)hint.Attribute("Visibility"));
            Assert.Same(Named(page, "ButtonsTopLeft"), hint.Parent);
            var text = Named(page, "PictureLineText");
            Assert.Equal("{Binding PictureLineText}", (string)text.Attribute("Text"));
            Assert.Equal("Pad_PictureLine", Id(text));
            // Its words are read out as they change.
            Assert.Equal("True", (string)text.Attributes().Single(a => a.Name.LocalName == "LiveText.Polite"));
            Assert.Contains(line, text.Ancestors());
            var stop = Named(page, "ListeningStopButton");
            Assert.Equal("Pad_ListeningStop", Id(stop));
            Assert.Equal("{Binding Common_Stop" + S, (string)stop.Attribute("Content"));
            Assert.Equal("ListeningStop_Click", (string)stop.Attribute("Click"));
            // Stop shows while a recording waits, and the ember dot with it.
            // The line that says why a recording could not start has an
            // info glyph in the dot's place and no Stop.
            string listening = "{Binding IsListening, Converter={StaticResource BoolToVisibilityConverter}}";
            Assert.Equal(listening, (string)stop.Attribute("Visibility"));
            Assert.Contains(line, stop.Ancestors());
            Assert.Equal(listening, (string)Assert.Single(line.Descendants(), e => e.Name.LocalName == "Ellipse").Attribute("Visibility"));
            var info = Assert.Single(line.Descendants(), e => e.Name.LocalName == "GlyphText");
            Assert.Equal("\uE946", (string)info.Attribute("Text"));
            Assert.Equal("{Binding IsListening, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}",
                (string)info.Attribute("Visibility"));
            // The page keeps no room for the line: what is always in view
            // with the picture is the room under the last card and the
            // slot's own foot, and the page grows by the line.
            Assert.Contains("double around = ButtonsPage.Margin.Bottom + ButtonsFootHeight();", CodeBehind());

            string code = CodeBehind();
            Assert.Matches(@"private void ListeningStop_Click\(object sender, RoutedEventArgs e\)\s*\{\s*if \(DataContext is not PadViewModel vm\) return;\s*if \(vm\.IsMapAllActive\)\s*\{\s*vm\.StopMapAll\(\);\s*return;\s*\}\s*string target = vm\.CurrentRecordingTarget;\s*if \(!string\.IsNullOrEmpty\(target\)\)\s*ControllerElementRecordRequested\?\.Invoke\(this, target\);", code);
            // A click on the picture picks the control before it asks for a
            // recording, so the bar shows a control no device can record.
            Assert.Matches(@"targetName = MappingTargetOf\(vm, targetName\);\s*if \(targetName == null\) return;[^}]*vm\.PickedTarget = targetName;\s*\}\s*ControllerElementRecordRequested\?\.Invoke\(this, targetName\);", code);
            // The window still answers the page's event by cancelling the
            // recording of the control it is told is already recording.
            Assert.Matches(@"if \(padVm\.CurrentRecordingTarget == targetName\)\s*\{\s*_recorderService\.CancelRecording\(\);",
                ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs"));
        }

        // ── the top row ────────────────────────────────────────────────

        /// <summary>The top row is the head of the Buttons card, as the
        /// board draws it. Its left part: the Picture and List switch and
        /// the layer chips, each a segmented switch in its own track, then
        /// Add Layer in the list and the hint over the picture. Its right
        /// part: the list's tools, then Map All at the row's end. What acts
        /// on the whole controller (Copy, Paste, Copy From, Clear All) is
        /// in the header's More menu, which every section shows, and the
        /// row has no More button of its own.</summary>
        [Fact]
        public void TheTopRowHasTheSwitch_TheLayers_AndTheListsTools()
        {
            var page = Page();
            var top = Named(page, "ButtonsTopRow");
            var head = top.Parent!;
            Assert.Equal("ButtonsCardHead", Name(head));
            Assert.Equal("0", (string)head.Attribute("Grid.Row"));
            // The head carries the card's fill and edge, with the card's
            // upper corners, 32 from each side of the page.
            Assert.Equal("{DynamicResource CardBackgroundFillColorDefaultBrush}", (string)head.Attribute("Background"));
            Assert.Equal("{DynamicResource CardStrokeColorDefaultBrush}", (string)head.Attribute("BorderBrush"));
            Assert.Equal("10,10,0,0", (string)head.Attribute("CornerRadius"));
            Assert.Equal("32,0,32,0", (string)head.Attribute("Margin"));
            const string listOnly = "{Binding IsButtonsListView, Converter={StaticResource BoolToVisibilityConverter}}";

            var left = Named(page, "ButtonsTopLeft").Elements().ToList();
            Assert.Equal(4, left.Count);
            // The switch.
            Assert.Equal("{StaticResource SegmentTrack}", (string)left[0].Attribute("Style"));
            Assert.Equal(new[] { "TabController", "TabMappings" }, Assert.Single(left[0].Elements()).Elements().Select(Name));
            Assert.Equal("{Binding Pad_View_Picture" + S, (string)Assert.Single(Named(page, "TabController").Elements()).Attribute("Text"));
            Assert.Equal("{Binding Pad_View_List" + S, (string)Assert.Single(Named(page, "TabMappings").Elements()).Attribute("Text"));
            Assert.All(new[] { "TabController", "TabMappings" },
                n => Assert.Equal("{StaticResource SegmentRadio}", (string)Named(page, n).Attribute("Style")));
            // The layers, shown when a layer exists.
            Assert.Equal("ShiftLayerTrack", Name(left[1]));
            Assert.Equal("{StaticResource SegmentTrack}", (string)left[1].Attribute("Style"));
            Assert.Equal("{Binding HasShiftLayers, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)left[1].Attribute("Visibility"));
            var strip = Assert.Single(left[1].Elements());
            Assert.Equal("ShiftLayerTabStrip", Name(strip));
            Assert.Equal("{Binding LayerTabs}", (string)strip.Attribute("ItemsSource"));
            Assert.Contains("Style=\"{StaticResource SegmentRadio}\"", strip.ToString());
            // Add Layer, with the list.
            Assert.Equal("AddShiftLayerButton", Name(left[2]));
            Assert.Equal(listOnly, (string)left[2].Attribute("Visibility"));
            // Its plus is an icon, as the List board draws it, so its word
            // carries none.
            var plus = Assert.Single(left[2].Descendants(), e => e.Name.LocalName == "DecorativeStrokeIcon");
            Assert.Equal("{StaticResource PlusLineGeometry}", (string)plus.Attribute("Data"));
            Assert.Equal("Button.Icon", plus.Parent.Name.LocalName);
            Assert.DoesNotContain("+", PadForge.Resources.Strings.Strings.Instance.Pad_Shift_AddLayer);
            Assert.Equal("PictureHint", Name(left[3]));

            // The right part: the list's tools, then Map All, then the
            // list's More, as the List board has them.
            var children = Named(page, "ButtonsTopRight").Elements().ToList();
            Assert.Equal(new[] { "ListTools", null, "ListMoreButton" }, children.Select(Name));
            var tools = children[0];
            Assert.Equal(listOnly, (string)tools.Attribute("Visibility"));
            Assert.Equal(new[] { "MappingFilterSearchBox", "MappingDeviceFilterToggle", null, "MappingSurfaceScopeBox" },
                tools.Elements().Select(Name));
            // The filter box: 36 tall with the search mark at its start,
            // 190 for its words, and its shortcut in its tooltip.
            var filter = Named(page, "MappingFilterSearchBox");
            Assert.Equal("234", (string)filter.Attribute("Width"));
            Assert.Equal("36", (string)filter.Attribute("MinHeight"));
            Assert.Equal("Left", (string)filter.Attribute("IconPlacement"));
            Assert.Equal("Search24", (string)Assert.Single(filter.Descendants(), e => e.Name.LocalName == "DecorativeSymbolIcon").Attribute("Symbol"));
            Assert.Equal("{Binding Mapping_FilterSearch_Tooltip" + S, (string)filter.Attribute("ToolTip"));
            Assert.Equal("{Binding Mapping_FilterSearch_Placeholder" + S, (string)filter.Attribute("PlaceholderText"));
            var strings = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal("Filter outputs and inputs", strings.Mapping_FilterSearch_Placeholder);
            Assert.Contains("Ctrl+F", strings.Mapping_FilterSearch_Tooltip);
            // The funnel is the board's square button.
            var funnel = Named(page, "MappingDeviceFilterToggle");
            Assert.Equal("36", (string)funnel.Attribute("Width"));
            Assert.Equal("36", (string)funnel.Attribute("Height"));
            Assert.Equal("0", (string)funnel.Attribute("Padding"));
            // The scope box has the app's picker metrics.
            Assert.Null(Named(page, "MappingSurfaceScopeBox").Attribute("Padding"));
            Assert.Equal("AddShiftLayer_Click", (string)Named(page, "AddShiftLayerButton").Attribute("Click"));
            Assert.Equal("{Binding MappingInputSearch, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
                (string)Named(page, "MappingFilterSearchBox").Attribute("Text"));
            Assert.Equal("{Binding KbmSurfaceScopeVisible, Converter={StaticResource BoolToVisibilityConverter}}",
                (string)Named(page, "MappingSurfaceScopeBox").Attribute("Visibility"));

            // Map All, in both views, 16 after the tools.
            var mapAll = children[1];
            Assert.Equal("{Binding MapAllButtonText}", (string)mapAll.Attribute("Content"));
            Assert.Equal("MapAllToggle_Click", (string)mapAll.Attribute("Click"));
            Assert.Null(mapAll.Attribute("Visibility"));
            Assert.Equal("16,0,0,0", (string)mapAll.Attribute("Margin"));

            // The list's More, as the List board draws it after Map All: the
            // board's button with its three dots, opening the first four of
            // the header's More through the same commands and handlers. The
            // header's More keeps them too (PadSectionsTests pins it).
            var more = children[2];
            Assert.Equal("Button", more.Name.LocalName);
            Assert.Equal(listOnly, (string)more.Attribute("Visibility"));
            Assert.Equal("Pad_ListMore", Id(more));
            Assert.Equal("{Binding Common_More" + S, (string)more.Attribute("ToolTip"));
            Assert.Equal("{Binding Common_More" + S, (string)more.Attribute("AutomationProperties.Name"));
            Assert.Equal("10,0", (string)more.Attribute("Padding"));
            Assert.Equal("MoreMenu_Click", (string)more.Attribute("Click"));
            var dots = Assert.Single(more.Elements(), e => e.Name.LocalName == "Path");
            Assert.Equal("12.9", (string)dots.Attribute("Width"));
            Assert.Equal("2.4", (string)dots.Attribute("Height"));
            Assert.Equal((string)Named(page, "PadMoreButton").Elements().Single(e => e.Name.LocalName == "Path").Attribute("Data"),
                (string)dots.Attribute("Data"));
            var items = more.Descendants().Where(e => e.Name.LocalName is "MenuItem" or "Separator").ToList();
            var header = Named(page, "PadMoreButton").Descendants().Where(e => e.Name.LocalName is "MenuItem" or "Separator").ToList();
            Assert.Equal(5, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                Assert.Equal(header[i].Name.LocalName, items[i].Name.LocalName);
                foreach (string attribute in new[] { "Header", "Command", "Click", "ToolTip", "Foreground" })
                    Assert.Equal((string)header[i].Attribute(attribute), (string)items[i].Attribute(attribute));
            }
            Assert.Equal("{Binding Pad_ClearAll" + S, (string)items[4].Attribute("Header"));
            Assert.Equal(new[] { "PadMoreButton", "ListMoreButton" }, page.Descendants()
                .Where(e => e.Name.LocalName == "Button" && e.Descendants().Any(i => (string)i.Attribute("Click") == "ClearAllMappings_Click"))
                .Select(Name));

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
            Assert.Equal("6", (string)group.Attribute("Grid.Row"));
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
            // SOCD's line has the board's short title, and its tooltip says
            // what it does.
            Assert.Equal(new[] { "Pad_Socd_FoldTitle", "Pad_Socd_FoldTitle", "Pad_KeepAwake_Card_Header" }.Select(k => "{Binding " + k + S),
                cards.Select(c => (string)c.Attribute("Header")));
            Assert.Equal("SOCD", PadForge.Resources.Strings.Strings.Instance.Pad_Socd_FoldTitle);
            Assert.Equal(new[] { "Pad_Socd_Card_Desc", "Pad_Kbm_Socd_Card_Desc" }.Select(k => "{Binding " + k + S),
                cards.Take(2).Select(c => (string)c.Attribute("Description")));
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

        /// <summary>The table is the list. It folds away while it has no
        /// rows, and under the picture, where the picked control's rows
        /// are the bar's. It takes the picked row as its selected row in
        /// both views, so the list opens on the control the picture left.
        /// It has no columns, so nothing measures them.</summary>
        [Fact]
        public void OneTableServesBothViews()
        {
            var page = Page();
            var table = Named(page, "MappingDataGrid");
            Assert.Equal("3", (string)table.Attribute("Grid.Row"));
            Assert.Equal("{Binding Mappings}", (string)table.Attribute("ItemsSource"));
            var tableStyle = table.Elements().First(e => e.Name.LocalName == "DataGrid.Style");
            var trigger = Assert.Single(tableStyle.Descendants(), e => e.Name.LocalName == "Trigger");
            Assert.Equal("HasItems", (string)trigger.Attribute("Property"));
            Assert.Equal("False", (string)trigger.Attribute("Value"));
            Assert.Equal("Collapsed", (string)Assert.Single(trigger.Elements()).Attribute("Value"));
            var underPicture = Assert.Single(tableStyle.Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding IsButtonsPictureView}", (string)underPicture.Attribute("Binding"));
            Assert.Equal("True", (string)underPicture.Attribute("Value"));
            Assert.Equal("Collapsed", (string)Assert.Single(underPicture.Elements()).Attribute("Value"));

            // The picture's view is a page that scrolls, so an open row or
            // an open card never squeezes the picture. The page's code
            // gives the picture's row a height of its own there, and gives
            // the table's row the room in the list, where the page does not
            // scroll and the table scrolls its own rows.
            var scroll = Named(page, "ButtonsScroll");
            Assert.Equal("ScrollViewer", scroll.Name.LocalName);
            Assert.Equal("ButtonsRoot_SizeChanged", (string)scroll.Attribute("SizeChanged"));
            // The view's content is 32 from each side of the page, as the
            // card's head is, with the page's scroll bar in that margin.
            Assert.Equal("32,0", (string)scroll.Attribute("Padding"));
            // The card's body is drawn behind the picture, the line a
            // recording shows, the bar, the table and the slot's own foot:
            // its sides, its foot and its lower corners.
            var body = Named(page, "ButtonsCardBody");
            Assert.Same(Named(page, "ButtonsPage"), body.Parent);
            Assert.Equal("0", (string)body.Attribute("Grid.Row"));
            Assert.Equal("5", (string)body.Attribute("Grid.RowSpan"));
            Assert.Equal("1,0,1,1", (string)body.Attribute("BorderThickness"));
            Assert.Equal("0,0,10,10", (string)body.Attribute("CornerRadius"));
            // The picture stands on the page's ground inside the card's
            // sides, and is the card's foot while no bar follows it and the
            // slot has no foot of its own.
            var ground = Named(page, "PictureGround");
            Assert.Equal("1,0,1,0", (string)ground.Attribute("Margin"));
            Assert.Equal("{DynamicResource SteelGroundBrush}", (string)ground.Attribute("Background"));
            var foot = Assert.Single(ground.Descendants(), e => e.Name.LocalName == "DataTrigger"
                && (string)e.Attribute("Binding") == "{Binding PictureIsCardFoot}");
            Assert.Equal("True", (string)foot.Attribute("Value"));
            Assert.Equal("0,0,9,9", (string)Assert.Single(foot.Elements()).Attribute("Value"));
            // The section: the top row, the list's chips, and the view's
            // content with the room that is left.
            Assert.Equal(new[] { "Auto", "Auto", "*" },
                Named(page, "MappingsTabRoot").Elements().First(e => e.Name.LocalName == "Grid.RowDefinitions")
                    .Elements().Select(r => (string)r.Attribute("Height")));
            Assert.Equal("2", (string)scroll.Attribute("Grid.Row"));
            // The page inside: the picture, the line a recording shows, the
            // bar, the table, the slot's own foot, the slot's own card, the
            // cards, and an empty row that takes the viewport's leftover
            // pixels.
            var rows = Named(page, "ButtonsPage").Elements().First(e => e.Name.LocalName == "Grid.RowDefinitions")
                .Elements().ToList();
            Assert.Equal(8, rows.Count);
            Assert.All(rows, r => Assert.Equal("Auto", (string)r.Attribute("Height")));
            Assert.Equal("ButtonsPictureRow", Name(rows[0]));
            Assert.Equal("1", (string)Named(page, "PictureLineRow").Attribute("Grid.Row"));
            Assert.Equal("2", (string)Named(page, "PictureBar").Attribute("Grid.Row"));
            Assert.Equal("ButtonsTableRow", Name(rows[3]));
            Assert.Equal("ButtonsFootRow", Name(rows[4]));
            Assert.Equal("ButtonsSlotCardRow", Name(rows[5]));
            Assert.Equal("ButtonsFillRow", Name(rows[7]));
            Assert.Equal("6", (string)Named(page, "OutputGroup").Attribute("Grid.Row"));
            Assert.DoesNotContain(Named(page, "ButtonsPage").Elements(),
                e => (string)e.Attribute("Grid.Row") == "7");
            Assert.Equal("MappingDataGrid_PreviewMouseWheel", (string)table.Attribute("PreviewMouseWheel"));

            string code = CodeBehind();
            // The list does not scroll the page: with every row of a
            // Keyboard + Mouse slot's list on the page, its first open took
            // 707 ms against 457 (measured off screen, 2026-11-12).
            Assert.Matches(@"if \(vm\.IsButtonsListView\)\s*\{\s*ButtonsScroll\.VerticalScrollBarVisibility = ScrollBarVisibility\.Disabled;\s*ButtonsPictureRow\.Height = GridLength\.Auto;\s*ButtonsTableRow\.Height = new GridLength\(1, GridUnitType\.Star\);\s*ButtonsFillRow\.Height = GridLength\.Auto;\s*MappingDataGrid\.VerticalScrollBarVisibility = ScrollBarVisibility\.Visible;\s*return;\s*\}", code);
            Assert.Contains("ButtonsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;", code);
            // In the picture's view the table is as tall as its rows, and
            // the empty last row takes the viewport's leftover. In the list
            // the table is the one row that stretches.
            Assert.Matches(@"ButtonsTableRow\.Height = GridLength\.Auto;[\s\S]{0,700}?ButtonsFillRow\.Height = new GridLength\(1, GridUnitType\.Star\);\s*MappingDataGrid\.VerticalScrollBarVisibility = ScrollBarVisibility\.Disabled;", code);
            // A pick made on the picture leaves the page where it is: the
            // selected row is scrolled into view in the list only.
            Assert.Matches(@"bool underPicture = DataContext is PadViewModel viewVm && viewVm\.IsButtonsPictureView;\s*if \(!underPicture && e\.AddedItems\.Count > 0 && e\.AddedItems\[0\] is MappingItem sel\)", code);
            // The bar under the picture, with its blocks on one line, is in
            // view with the picture, and so are the folded cards (#574). The
            // bar's height is read while it holds a control's one row, and a
            // change of its height sizes the picture again while it shows.
            Assert.Matches(@"double bar = SteadyFoldedHeight\(vm\.ShowPictureBar, OneLineBarHeight\(\), ref _barMeasured\);\s*double folded = SteadyFoldedHeight\(vm\.OutputTabVisible \|\| vm\.ShowMidiCard, FoldedOutputHeight\(\), ref _foldedOutputMeasured\);\s*double picture = PictureHeightFor\(viewport, around, bar, folded, VisualTreeHelper\.GetDpi\(ButtonsScroll\)\.DpiScaleY\);\s*ButtonsPictureRow\.Height = new GridLength\(picture\);", code);
            Assert.Matches(@"if \(PictureBar == null \|\| PictureBar\.Visibility != Visibility\.Visible \|\| PictureBar\.ActualHeight <= 0\) return double\.NaN;\s*if \(PickedBlocks == null \|\| PickedBlocks\.Items\.Count != 1\) return double\.NaN;\s*return PictureBar\.ActualHeight \+ PictureBar\.Margin\.Top \+ PictureBar\.Margin\.Bottom;", code);
            Assert.Equal("PictureBar_SizeChanged", (string)Named(page, "PictureBar").Attribute("SizeChanged"));
            Assert.Matches(@"private void PictureBar_SizeChanged\(object sender, SizeChangedEventArgs e\)\s*\{\s*if \(e\.HeightChanged && DataContext is PadViewModel vm && vm\.ShowPictureBar\) SyncButtonsLayout\(\);", code);
            // Cards that are hidden or not laid out have no height to read.
            // With no output cards to measure, the slot's own card stands
            // alone, and with neither there is nothing to read.
            // The cards go by their own Visibility, as the foot and the bar
            // do, so a page drawn off a screen reads them too.
            Assert.Matches(@"double own = FoldedSlotCardHeight\(\);\s*if \(OutputGroup == null \|\| OutputGroup\.Visibility != Visibility\.Visible \|\| OutputGroup\.ActualHeight <= 0\) return own > 0 \? own : double\.NaN;", code);
            Assert.Matches(@"return Math\.Max\(0, height\) \+ own;", code);
            // The cards' folded height is the group as it stands less what
            // each open card shows, and a change of the group's height sizes
            // the picture again.
            Assert.Equal("OutputGroup_SizeChanged", (string)Named(page, "OutputGroup").Attribute("SizeChanged"));
            Assert.Matches(@"private void OutputGroup_SizeChanged\(object sender, SizeChangedEventArgs e\)\s*\{\s*if \(e\.HeightChanged\) SyncButtonsLayout\(\);", code);
            Assert.Matches(@"double height = OutputGroup\.ActualHeight \+ OutputGroup\.Margin\.Top \+ OutputGroup\.Margin\.Bottom;\s*foreach \(var card in new\[\] \{ SocdFoldCard, KbmSocdFoldCard, KeepAwakeFoldCard \}\)\s*\{\s*if \(card == null \|\| card\.Visibility != Visibility\.Visible \|\| !card\.IsExpanded\) continue;\s*if \(card\.Template\?\.FindName\(""ExpandSite"", card\) is FrameworkElement open\)\s*height -= open\.ActualHeight;", code);
            Assert.Contains("<Border x:Name=\"ExpandSite\"", ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml"));
            // Under the picture the wheel over the table moves the page, and
            // never reaches a closed picker in the row.
            Assert.Matches(@"if \(DataContext is not PadViewModel vm \|\| !vm\.IsButtonsPictureView\) return;\s*e\.Handled = true;\s*ButtonsScroll\.RaiseEvent\(new MouseWheelEventArgs\(e\.MouseDevice, e\.Timestamp, e\.Delta\)", code);
            // A new controller on the page gets its rows before its first
            // frame.
            Assert.Matches(@"_currentPadVm\?\.ApplyMappingPickerFilter\(\);\s*ApplyViewMode\(\);\s*SyncSectionSelection\(\);\s*SyncButtonsLayout\(\);\s*SyncPickedRowSelection\(\);", code);
            Assert.Matches(@"else if \(e\.PropertyName == nameof\(PadViewModel\.ButtonsViewIndex\)\)\s*\{\s*OnButtonsViewChanged\(\);\s*\}\s*else if \(e\.PropertyName == nameof\(PadViewModel\.PickedTarget\)\)\s*\{\s*SyncPickedRowSelection\(\);", code);
            Assert.Matches(@"private void OnButtonsViewChanged\(\)\s*\{\s*SyncButtonsLayout\(\);\s*SyncPickedRowSelection\(\);", code);
            // The table has no columns (#574): a row is its line with its
            // options under it. The pass that sized the columns to their
            // cells is gone with them, and the table never scrolls
            // sideways, so a row is the card's width.
            Assert.DoesNotContain(table.Elements(), e => e.Name.LocalName == "DataGrid.Columns");
            Assert.Equal("False", (string)table.Attribute("AutoGenerateColumns"));
            Assert.Equal("Disabled", (string)table.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
            Assert.Null(table.Attribute("Loaded"));
            Assert.DoesNotContain("FitMappingColumnsOnce", code);
            Assert.DoesNotContain("AutoFitFlexibleColumns", code);
            // A row selected in the table is the picked control.
            Assert.Matches(@"if \(e\.AddedItems\.Count > 0 && e\.AddedItems\[0\] is MappingItem picked\s*&& DataContext is PadViewModel pickVm\)\s*pickVm\.PickedTarget = picked\.TargetSettingName;", code);
            // A chip on the picture picks its control and stays on the
            // picture.
            Assert.Matches(@"private void OnAnnotationChipNavigate\(object sender, string targetSettingName\)\s*\{\s*if \(DataContext is not PadViewModel vm\) return;\s*if \(vm\.RowOfTarget\(targetSettingName\) == null\) return;\s*vm\.PickedTarget = targetSettingName;", code);
            Assert.DoesNotContain("vm.SelectedConfigTab = 2;", code);
        }

        /// <summary>The picture takes what the viewport has left after its
        /// line, the bar under it with its blocks on one line, and the folded
        /// output cards, so the first look, a control picked and every card
        /// folded, fits and has nothing to scroll, as the Buttons board has
        /// it (#574). The numbers are the ones read off the running page on
        /// 2026-11-11: a section 765 high, 48 for the line, and two folded
        /// cards that need 141, with a bar of one line 142 high. A short
        /// window keeps the picture's least height and scrolls.</summary>
        [Fact]
        public void ThePictureLeavesRoomForTheBarAndTheFoldedCards()
        {
            // A control picked: its bar is in view with the cards.
            double picked = PadForge.Views.PadPage.PictureHeightFor(765, 48, 142, 141, 1);
            Assert.Equal(432, picked);
            Assert.True(picked + 48 + 142 + 141 <= 765, "the first look is taller than its viewport");

            // Nothing picked: no bar.
            double twoCards = PadForge.Views.PadPage.PictureHeightFor(765, 48, 0, 141, 1);
            Assert.Equal(574, twoCards);
            Assert.True(twoCards + 48 + 141 <= 765, "the page at rest is taller than its viewport");

            // No cards, and one card.
            Assert.Equal(573, PadForge.Views.PadPage.PictureHeightFor(765, 48, 142, 0, 1));
            Assert.Equal(715, PadForge.Views.PadPage.PictureHeightFor(765, 48, 0, 0, 1));
            Assert.Equal(644, PadForge.Views.PadPage.PictureHeightFor(765, 48, 0, 71, 1));

            // Three cards take more from the picture than two.
            Assert.Equal(503, PadForge.Views.PadPage.PictureHeightFor(765, 48, 0, 212, 1));

            // A short window keeps the least picture, and the page scrolls.
            Assert.Equal(200, PadForge.Views.PadPage.PictureHeightFor(300, 48, 142, 141, 1));

            // A scale that is no scale counts as one.
            Assert.Equal(432, PadForge.Views.PadPage.PictureHeightFor(765, 48, 142, 141, 0));
            Assert.Equal(432, PadForge.Views.PadPage.PictureHeightFor(765, 48, 142, 141, double.NaN));
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
            double bar = Math.Round(142.67 * scale) / scale;
            double cards = Math.Round(141.33 * scale) / scale;

            double picture = PadForge.Views.PadPage.PictureHeightFor(viewport, line, bar, cards, scale);

            double pixels = picture * scale;
            Assert.True(Math.Abs(pixels - Math.Round(pixels)) < 1e-6, $"{pixels} pixels at {scale}");
            double page = Math.Round(pixels) + Math.Round(line * scale) + Math.Round(bar * scale) + Math.Round(cards * scale);
            Assert.True(page <= Math.Round(viewport * scale) - 2, "the page at rest has less than two pixels to spare");
            Assert.True(page >= Math.Round(viewport * scale) - 3, "the picture gave up more than it had to");
        }

        [Fact]
        public void ThePictureIs862PixelsHighAt150Percent()
        {
            double picture = PadForge.Views.PadPage.PictureHeightFor(1148 / 1.5, 72 / 1.5, 0, 212 / 1.5, 1.5);
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

        /// <summary>A Keyboard + Mouse slot's foot, as its board draws it:
        /// one line at the foot of the picture's card with what the slot's
        /// keyboard and its mouse type through, each with its reset. It is
        /// a row of the page's grid, so it stands under the list too, and
        /// the picture is sized with it in view. The old bar between the
        /// page's head and its sections is gone.</summary>
        [Fact]
        public void AKeyboardAndMouseSlotsFootIsTheCardsFoot()
        {
            var page = Page();
            var foot = Named(page, "KbmDeviceBar");
            Assert.Same(Named(page, "ButtonsPage"), foot.Parent);
            Assert.Equal("4", (string)foot.Attribute("Grid.Row"));
            // Inside the card's sides and its foot, 20 at its start and 12
            // at its end.
            Assert.Equal("1,0,1,1", (string)foot.Attribute("Margin"));
            Assert.Equal("20,10,12,10", (string)foot.Attribute("Padding"));
            Assert.Equal("{Binding ShowKbmFoot, Converter={StaticResource BoolToVisibilityConverter}}", (string)foot.Attribute("Visibility"));
            Assert.Equal("ButtonsFoot_SizeChanged", (string)foot.Attribute("SizeChanged"));
            Assert.Equal("ButtonsFoot_IsVisibleChanged", (string)foot.Attribute("IsVisibleChanged"));
            // A hairline over it, but for right under the picture, which has
            // its own.
            var look = Assert.Single(foot.Elements(), e => e.Name.LocalName == "Border.Style").Elements().Single();
            Assert.Equal("0,1,0,0", (string)look.Elements().Single(e => e.Name.LocalName == "Setter").Attribute("Value"));
            var bare = Assert.Single(look.Descendants(), e => e.Name.LocalName == "MultiDataTrigger");
            Assert.Equal(new[] { "{Binding IsButtonsPictureView}=True", "{Binding ShowPictureBar}=False" },
                bare.Descendants().Where(e => e.Name.LocalName == "Condition")
                    .Select(c => (string)c.Attribute("Binding") + "=" + (string)c.Attribute("Value")));
            Assert.Equal("0", (string)bare.Elements().Single(e => e.Name.LocalName == "Setter").Attribute("Value"));

            // The line: Types Through in the quiet color, then each half.
            var line = Assert.Single(foot.Elements(), e => e.Name.LocalName == "FlexLinePanel");
            Assert.Equal("20", (string)line.Attribute("Gap"));
            Assert.Equal("8", (string)line.Attribute("LineGap"));
            var parts = line.Elements().ToList();
            Assert.Equal(new[] { "TextBlock", "StackPanel", "StackPanel" }, parts.Select(e => e.Name.LocalName));
            Assert.Equal("{Binding Menu_TypesThrough" + S, (string)parts[0].Attribute("Text"));
            Assert.Equal("13", (string)parts[0].Attribute("FontSize"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)parts[0].Attribute("Foreground"));
            foreach (var (half, word, name) in new[] { (parts[1], "Keyboard", "DeviceType_Keyboard"), (parts[2], "Mouse", "DeviceType_Mouse") })
            {
                Assert.Equal("{Binding Kbm" + word + "DeviceVisible, Converter={StaticResource BoolToVisibilityConverter}}", (string)half.Attribute("Visibility"));
                var own = half.Elements().ToList();
                Assert.Equal(new[] { "TextBlock", "ComboBox", "SettingResetButton", "ItemsControl" }, own.Select(e => e.Name.LocalName));
                Assert.Equal("{Binding " + name + S, (string)own[0].Attribute("Text"));
                Assert.Equal("0,0,8,0", (string)own[0].Attribute("Margin"));
                Assert.Equal("Kbm" + word + "DeviceCombo", Name(own[1]));
                // 34 tall, which is every picker's height (the app's
                // implicit style, ControlMetricsTests).
                Assert.Null(own[1].Attribute("MinHeight"));
                Assert.Equal("{Binding Kbm" + word + "Device, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)own[1].Attribute("SelectedValue"));
                Assert.Equal("Kbm" + word + "Device", (string)own[2].Attribute("CommandParameter"));
                Assert.Equal("4,0,0,0", (string)own[2].Attribute("Margin"));
                Assert.Equal("{Binding Kbm" + word + "Capabilities}", (string)own[3].Attribute("ItemsSource"));
            }

            // The foot's height is part of what is in view with the picture,
            // read from the foot's own Visibility, and a change of it sizes
            // the picture again.
            string code = CodeBehind();
            Assert.Contains("double around = ButtonsPage.Margin.Bottom + ButtonsFootHeight();", code);
            Assert.Matches(@"private double ButtonsFootHeight\(\)\s*\{\s*if \(KbmDeviceBar == null \|\| KbmDeviceBar\.Visibility != Visibility\.Visible\) return 0;\s*return KbmDeviceBar\.ActualHeight \+ KbmDeviceBar\.Margin\.Top \+ KbmDeviceBar\.Margin\.Bottom;\s*\}", code);
            Assert.Matches(@"private void ButtonsFoot_SizeChanged\(object sender, SizeChangedEventArgs e\)\s*\{\s*if \(e\.HeightChanged\) SyncButtonsLayout\(\);\s*\}", code);
            Assert.Matches(@"private void ButtonsFoot_IsVisibleChanged\(object sender, DependencyPropertyChangedEventArgs e\)\s*=> SyncButtonsLayout\(\);", code);
            // The page's code no longer shows or hides it.
            Assert.DoesNotContain("KbmDeviceBar.Visibility =", code);
            Assert.DoesNotContain("KEYBOARD + MOUSE DEVICE BAR", PageText());

            // The view model: the foot is a Keyboard + Mouse slot's, and the
            // picture is the card's foot while nothing is picked on a slot
            // that has none. The picture opens with a control picked (#574),
            // so its bar is the foot then, and a pick can be none in the
            // list alone.
            var vm = new PadViewModel(0);
            vm.ApplyMappingPickerFilter();
            Assert.False(vm.ShowKbmFoot);
            Assert.True(vm.ShowPictureBar);
            Assert.False(vm.PictureIsCardFoot);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            vm.PickedTarget = null;
            Assert.True(vm.PictureIsCardFoot);
            Assert.Contains(nameof(PadViewModel.PictureIsCardFoot), raised);
            vm.SelectedConfigTab = PadViewModel.PictureTabIndex;
            Assert.False(vm.PictureIsCardFoot);

            raised.Clear();
            vm.OutputType = PadForge.Engine.VirtualControllerType.KeyboardMouse;
            Assert.True(vm.ShowKbmFoot);
            Assert.False(vm.PictureIsCardFoot);
            Assert.Contains(nameof(PadViewModel.ShowKbmFoot), raised);
            Assert.Contains(nameof(PadViewModel.PictureIsCardFoot), raised);
            raised.Clear();
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            vm.PickedTarget = null;
            Assert.False(vm.PictureIsCardFoot);
            vm.OutputType = PadForge.Engine.VirtualControllerType.Xbox;
            Assert.False(vm.ShowKbmFoot);
            Assert.True(vm.PictureIsCardFoot);
            Assert.Contains(nameof(PadViewModel.ShowKbmFoot), raised);
        }

        /// <summary>A MIDI slot's card, as its board draws it: MIDI Output,
        /// a card of its own under the picture's card with the port and the
        /// channel, More over the runs of controls and notes, and Reset All
        /// in its head. It stands where a controller has its output cards,
        /// the picture is sized with it in view, and the old bar between the
        /// page's head and its sections is gone.</summary>
        [Fact]
        public void AMidiSlotsCardStandsUnderThePicture()
        {
            var page = Page();
            var card = Named(page, "MidiConfigBar");
            Assert.Same(Named(page, "ButtonsPage"), card.Parent);
            Assert.Equal("5", (string)card.Attribute("Grid.Row"));
            Assert.Equal("{StaticResource CardBorder}", (string)card.Attribute("Style"));
            Assert.Equal("0,16,0,0", (string)card.Attribute("Margin"));
            Assert.Equal("{Binding ShowMidiCard, Converter={StaticResource BoolToVisibilityConverter}}", (string)card.Attribute("Visibility"));
            Assert.Equal("OutputGroup_SizeChanged", (string)card.Attribute("SizeChanged"));
            var parts = Assert.Single(card.Elements()).Elements().ToList();
            Assert.Equal(new[] { "DockPanel", "SettingRow", "SettingRow", "MoreRow" }, parts.Select(e => e.Name.LocalName));

            // The head: the title, and Reset All at its end.
            var head = parts[0].Elements().ToList();
            Assert.Equal("{StaticResource CardTitle}", (string)head[0].Attribute("Style"));
            Assert.StartsWith("{Binding ", (string)head[0].Attribute("Text"));
            Assert.Equal("{StaticResource ResetAllButton}", (string)head[1].Attribute("Style"));
            Assert.Equal("ResetMidiAll_Click", (string)head[1].Attribute("Click"));
            Assert.Equal("{Binding Pad_ResetAll" + S, (string)head[1].Attribute("Content"));

            // The port, which the legacy MIDI API alone picks, then the channel.
            Assert.Equal("{Binding IsLegacyMidi, Converter={StaticResource BoolToVisibilityConverter}}", (string)parts[1].Attribute("Visibility"));
            Assert.Equal(new[] { "TextBlock", "ComboBox", "SettingResetButton" }, parts[1].Elements().Select(e => e.Name.LocalName));
            Assert.Equal("MidiOutputPortBox", Name(parts[1].Elements().ElementAt(1)));
            // 34 tall, every picker's height.
            Assert.Null(parts[1].Elements().ElementAt(1).Attribute("MinHeight"));
            Assert.Equal("OutputPort", (string)parts[1].Elements().Last().Attribute("Tag"));

            // A row of a number: its label, the box, its reset.
            void IsNumberRow(XElement row, string label, string box, string tag)
            {
                Assert.Equal("44", (string)row.Attribute("MinHeight"));
                var own = row.Elements().ToList();
                Assert.Equal(new[] { "TextBlock", "TextBox", "SettingResetButton" }, own.Select(e => e.Name.LocalName));
                Assert.Equal("{Binding " + label + S, (string)own[0].Attribute("Text"));
                Assert.Equal("{StaticResource DzLabel}", (string)own[0].Attribute("Style"));
                Assert.Equal(box, Name(own[1]));
                Assert.Equal("{StaticResource CardNumberBox}", (string)own[1].Attribute("Style"));
                Assert.Equal("MidiConfig_Changed", (string)own[1].Attribute("LostFocus"));
                Assert.Equal("MidiConfig_KeyDown", (string)own[1].Attribute("KeyDown"));
                Assert.Equal("ResetMidiSetting_Click", (string)own[2].Attribute("Click"));
                Assert.Equal(tag, (string)own[2].Attribute("Tag"));
                Assert.Equal("{Binding " + label + S, (string)own[2].Attribute("SettingLabel"));
            }
            IsNumberRow(parts[2], "Pad_MidiChannel", "MidiChannelBox", "Channel");

            // More: the runs of controls and notes, and the velocity.
            var more = parts[3];
            Assert.Equal("MidiMore", Name(more));
            Assert.Equal("Pad.Buttons.Midi.More", (string)more.Attribute("StateKey"));
            var under = Assert.Single(more.Elements(), e => e.Name.LocalName == "StackPanel").Elements().ToList();
            Assert.Equal(5, under.Count);
            IsNumberRow(under[0], "Pad_MidiCCCount", "MidiCcCountBox", "CcCount");
            IsNumberRow(under[1], "Pad_MidiStartCC", "MidiStartCcBox", "StartCc");
            IsNumberRow(under[2], "Pad_MidiNoteCount", "MidiNoteCountBox", "NoteCount");
            IsNumberRow(under[3], "Pad_MidiStartNote", "MidiStartNoteBox", "StartNote");
            IsNumberRow(under[4], "Pad_MidiVelocity", "MidiVelocityBox", "Velocity");

            // A number's box, as the boards draw one.
            var box = Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Key") == "CardNumberBox");
            string Set(string property) => (string)box.Elements().Single(e => (string)e.Attribute("Property") == property).Attribute("Value");
            Assert.Equal("64", Set("Width"));
            Assert.Equal("34", Set("MinHeight"));
            Assert.Equal("Right", Set("TextAlignment"));
            Assert.Equal("{DynamicResource SteelFieldBrush}", Set("Background"));

            // The old bar is gone, and the page's code no longer shows or hides the card.
            string code = CodeBehind();
            Assert.DoesNotContain("MIDI CONFIGURATION BAR", PageText());
            Assert.DoesNotContain("MidiConfigBar.Visibility = ", code);
            // The picture is sized with the card in view, its More row folded.
            Assert.Contains("double folded = SteadyFoldedHeight(vm.OutputTabVisible || vm.ShowMidiCard, FoldedOutputHeight(), ref _foldedOutputMeasured);", code);
            Assert.Matches(@"if \(MidiConfigBar != null && MidiConfigBar\.Visibility == Visibility\.Visible && MidiConfigBar\.ActualHeight > 0\)\s*\{\s*double card = MidiConfigBar\.ActualHeight \+ MidiConfigBar\.Margin\.Top \+ MidiConfigBar\.Margin\.Bottom;\s*if \(MidiMore != null && MidiMore\.IsExpanded\s*&& MidiMore\.Template\?\.FindName\(""ExpandSite"", MidiMore\) is FrameworkElement more\)\s*card -= more\.ActualHeight;\s*return Math\.Max\(0, card\);\s*\}", code);

            // The view model: a MIDI slot's, under the picture.
            var vm = new PadViewModel(0);
            Assert.False(vm.ShowMidiCard);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.OutputType = PadForge.Engine.VirtualControllerType.Midi;
            Assert.True(vm.ShowMidiCard);
            Assert.False(vm.ShowOutputGroup);
            Assert.Contains(nameof(PadViewModel.ShowMidiCard), raised);
            raised.Clear();
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.False(vm.ShowMidiCard);
            Assert.Contains(nameof(PadViewModel.ShowMidiCard), raised);

            // Each setting's reset, and Reset All over them all.
            Assert.Equal(new[] { "OutputPort", "Channel", "CcCount", "StartCc", "NoteCount", "StartNote", "Velocity" }, PadForge.Views.PadPage.MidiFields);
            var config = new MidiSlotConfig { Channel = 9, CcCount = 3, StartCc = 20, NoteCount = 5, StartNote = 40, Velocity = 80 };
            Assert.False(PadForge.Views.PadPage.ResetMidiField(config, "Nothing"));
            Assert.Equal(9, config.Channel);
            Assert.True(PadForge.Views.PadPage.ResetMidiField(config, "StartNote"));
            Assert.Equal(60, config.StartNote);
            Assert.Equal(9, config.Channel);
            foreach (string field in PadForge.Views.PadPage.MidiFields)
                Assert.True(PadForge.Views.PadPage.ResetMidiField(config, field), field);
            Assert.Equal((1, 6, 1, 11, 60, 127), (config.Channel, config.CcCount, config.StartCc, config.NoteCount, config.StartNote, (int)config.Velocity));
            Assert.Equal(string.Empty, config.OutputPort ?? string.Empty);
            string resets = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.SettingResets.cs");
            // Reset All leaves the port while its row does not show.
            Assert.Matches(@"private void ResetMidiAll_Click\(object sender, RoutedEventArgs e\)\s*\{\s*bool port = DataContext is PadViewModel \{ IsLegacyMidi: true \};\s*ResetMidiFields\(port \? MidiFields : Array\.FindAll\(MidiFields, f => f != nameof\(MidiSlotConfig\.OutputPort\)\)\);\s*\}", resets);
            Assert.Matches(@"foreach \(string field in fields\) any \|= ResetMidiField\(config, field\);\s*if \(!any\) return;\s*if \(before != \(config\.CcCount, config\.StartCc, config\.NoteCount, config\.StartNote\)\) vm\.RebuildMappings\(\);\s*SyncMidiConfigBar\(\);", resets);
        }

        /// <summary>An Extended slot's card. No board draws it, so it is a
        /// card as the MIDI board's, under the picture's card and over the
        /// output cards: Reset All in its head, the two things done to the
        /// slot as a whole, then Customize as a switch with what it sets
        /// under it while it is on. The preset is the header's, as on every
        /// slot, and the old bar with a preset picker of its own is gone,
        /// and with it the page's second row.</summary>
        [Fact]
        public void AnExtendedSlotsCardStandsUnderThePicture()
        {
            var page = Page();
            var card = Named(page, "ExtendedConfigBar");
            Assert.Same(Named(page, "ButtonsPage"), card.Parent);
            // The slot's own card has a row of its own, which a MIDI slot's
            // card shares: a slot is one kind or the other.
            Assert.Equal("5", (string)card.Attribute("Grid.Row"));
            Assert.Equal("5", (string)Named(page, "MidiConfigBar").Attribute("Grid.Row"));
            Assert.Equal("{StaticResource CardBorder}", (string)card.Attribute("Style"));
            Assert.Equal("0,16,0,0", (string)card.Attribute("Margin"));
            Assert.Equal("{Binding ShowExtendedCard, Converter={StaticResource BoolToVisibilityConverter}}", (string)card.Attribute("Visibility"));
            Assert.Equal("OutputGroup_SizeChanged", (string)card.Attribute("SizeChanged"));
            var parts = Assert.Single(card.Elements()).Elements().ToList();
            Assert.Equal(new[] { "DockPanel", "WrapPanel", "SettingRow", "StackPanel" }, parts.Select(e => e.Name.LocalName));

            // The head: the title, and Reset All, which Customize makes usable.
            var head = parts[0].Elements().ToList();
            Assert.Equal("{StaticResource CardTitle}", (string)head[0].Attribute("Style"));
            Assert.Equal("ExtendedResetDefaultsBtn", Name(head[1]));
            Assert.Equal("{StaticResource ResetAllButton}", (string)head[1].Attribute("Style"));
            Assert.Equal("ExtendedResetDefaults_Click", (string)head[1].Attribute("Click"));
            Assert.Equal("{Binding IsChecked, ElementName=ExtendedCustomizeChk}", (string)head[1].Attribute("IsEnabled"));

            // What is done to the slot as a whole.
            Assert.Equal(new[] { "ExtendedImportBtn", "ExtendedCloneBtn" }, parts[1].Elements().Select(Name));
            Assert.Equal(new[] { "ExtendedImportBtn_Click", "ExtendedCloneBtn_Click" }, parts[1].Elements().Select(e => (string)e.Attribute("Click")));
            // 36 tall, every plain button's height.
            Assert.All(parts[1].Elements(), e => Assert.Null(e.Attribute("MinHeight")));
            Assert.All(parts[1].Elements(), e => Assert.Null(e.Attribute("Style")));

            // Customize: a switch under its own label.
            var customize = parts[2].Elements().ToList();
            Assert.Equal(new[] { "TextBlock", "CheckBox", "SettingResetButton" }, customize.Select(e => e.Name.LocalName));
            Assert.Equal("{Binding Pad_ExtendedCustomize" + S, (string)customize[0].Attribute("Text"));
            Assert.Equal("ExtendedCustomizeChk", Name(customize[1]));
            Assert.Equal("{StaticResource SwitchCheckBox}", (string)customize[1].Attribute("Style"));
            Assert.Equal("{Binding Pad_ExtendedCustomize" + S, (string)customize[1].Attribute("Content"));
            Assert.Equal("ExtendedCustomize_Toggled", (string)customize[1].Attribute("Checked"));
            Assert.Equal("ExtendedCustomize_Toggled", (string)customize[1].Attribute("Unchecked"));
            Assert.Equal("Customize", (string)customize[2].Attribute("Tag"));

            // What it sets shows while it is on: the identity, then the layout.
            var custom = parts[3];
            Assert.Equal("ExtendedCustomRows", Name(custom));
            Assert.Equal("{Binding IsChecked, ElementName=ExtendedCustomizeChk, Converter={StaticResource BoolToVisibilityConverter}}", (string)custom.Attribute("Visibility"));
            var rows = custom.Elements().ToList();
            Assert.All(rows, r =>
            {
                Assert.Equal("SettingRow", r.Name.LocalName);
                Assert.Equal("44", (string)r.Attribute("MinHeight"));
                var own = r.Elements().ToList();
                Assert.Equal(3, own.Count);
                Assert.Equal("{StaticResource DzLabel}", (string)own[0].Attribute("Style"));
                Assert.Equal("ResetExtendedSetting_Click", (string)own[2].Attribute("Click"));
                // The reset is named as its row is.
                Assert.Equal((string)own[0].Attribute("Text"), (string)own[2].Attribute("SettingLabel"));
            });
            Assert.Equal(new[]
                {
                    "ExtendedProductStringBox", "ExtendedOemOverrideChk", "ExtendedVidBox", "ExtendedPidBox",
                    "RawStickCountBox", "ExtendedTriggerCountBox", "RawPovCountBox", "RawButtonCountBox", "ExtendedForceFeedbackChk",
                },
                rows.Select(r => Name(r.Elements().ElementAt(1))));
            Assert.Equal(new[]
                {
                    "ProductString", "OemNameOverride", "VendorId", "ProductId",
                    "ThumbstickCount", "TriggerCount", "PovCount", "ButtonCount", "ForceFeedbackEnabled",
                },
                rows.Select(r => (string)r.Elements().Last().Attribute("Tag")));
            // A check box of the card is a switch, and a count is the card's number box.
            Assert.All(rows.Select(r => r.Elements().ElementAt(1)).Where(e => e.Name.LocalName == "CheckBox"),
                e => Assert.Equal("{StaticResource SwitchCheckBox}", (string)e.Attribute("Style")));
            Assert.All(rows.Skip(2).Take(6).Select(r => r.Elements().ElementAt(1)),
                e => Assert.Equal("{StaticResource CardNumberBox}", (string)e.Attribute("Style")));
            Assert.All(rows.Skip(4).Take(4).Select(r => r.Elements().ElementAt(1)), e =>
            {
                Assert.Equal("ExtendedCustomValue_Changed", (string)e.Attribute("LostFocus"));
                Assert.Equal("ExtendedCustomValue_KeyDown", (string)e.Attribute("KeyDown"));
            });
            Assert.Equal("{Binding ExtendedConfig.ForceFeedbackEnabled, Mode=TwoWay}", (string)rows[8].Elements().ElementAt(1).Attribute("IsChecked"));

            // The preset is the header's on an Extended slot too, and the
            // bar's own picker is gone.
            string text = PageText();
            string code = CodeBehind();
            Assert.DoesNotContain("ExtendedProfileCombo", text);
            Assert.DoesNotContain("ExtendedProfileCombo", code);
            Assert.DoesNotContain("EXTENDED CONFIGURATION BAR", text);
            Assert.Contains("HMaestroProfileBar.Visibility = (vm.HasHMaestroProfileBar || isKbm)", code);
            Assert.DoesNotContain("ExtendedConfigBar.Visibility = ", code);
            Assert.DoesNotContain("ExtendedProfileCombo", ExtrasPageTests.RepoText("tools", "capture_all.ps1"));
            // The page has two rows: its head and the selected section.
            var root = page.Root!.Elements().Single(e => e.Name.LocalName == "Grid");
            Assert.Equal(new[] { "Auto", "*" }, root.Elements().First(e => e.Name.LocalName == "Grid.RowDefinitions")
                .Elements().Select(r => (string)r.Attribute("Height")));
            Assert.Equal("1", (string)root.Elements().Single(e => e.Name.LocalName == "ContentTabControl").Attribute("Grid.Row"));
            Assert.DoesNotContain(root.Elements(), e => (string)e.Attribute("Grid.Row") == "2");

            // The picture is sized with the card in view, without what
            // Customize shows.
            Assert.Matches(@"if \(ExtendedConfigBar != null && ExtendedConfigBar\.Visibility == Visibility\.Visible && ExtendedConfigBar\.ActualHeight > 0\)\s*\{\s*double card = ExtendedConfigBar\.ActualHeight \+ ExtendedConfigBar\.Margin\.Top \+ ExtendedConfigBar\.Margin\.Bottom;\s*if \(ExtendedCustomRows != null && ExtendedCustomRows\.Visibility == Visibility\.Visible\)\s*card -= ExtendedCustomRows\.ActualHeight;\s*return Math\.Max\(0, card\);\s*\}\s*return 0;", code);

            // The view model: an Extended slot's, under the picture, with the
            // output cards under it.
            var vm = new PadViewModel(0);
            Assert.False(vm.ShowExtendedCard);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.OutputType = PadForge.Engine.VirtualControllerType.Extended;
            Assert.True(vm.ShowExtendedCard);
            Assert.True(vm.ShowOutputGroup);
            Assert.False(vm.ShowMidiCard);
            Assert.True(vm.HasHMaestroProfileBar);
            Assert.Contains(nameof(PadViewModel.ShowExtendedCard), raised);
            raised.Clear();
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            Assert.False(vm.ShowExtendedCard);
            Assert.Contains(nameof(PadViewModel.ShowExtendedCard), raised);
        }
    }
}
