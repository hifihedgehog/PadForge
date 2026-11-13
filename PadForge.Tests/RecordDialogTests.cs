using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Twitch;
using PadForge.Controls;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The dialogs of the Record board (#574): the profile dialog, the Shift
    /// Layer window, NFC Tags, Voice Macros, Chat Commands and Hidden
    /// Buttons. Each dialog is made and read, and never shown, so nothing
    /// reaches the screen or the focus of whoever uses the machine.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class RecordDialogTests
    {
        private static T Part<T>(Window dialog, string name) where T : class
        {
            var part = dialog.FindName(name) as T;
            Assert.True(part != null, name + " is not a " + typeof(T).Name + " of " + dialog.GetType().Name);
            return part;
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "PadForge.App")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T found) yield return found;
                foreach (var deeper in Descendants<T>(child)) yield return deeper;
            }
        }

        // ── The profile dialog ──

        /// <summary>Add a Running Game offers each program once, by file
        /// name, without PadForge itself and without a process that gave no
        /// path.</summary>
        [Fact]
        public void RunningProgramsLeavePadForgeOut()
        {
            var picked = WindowedPrograms.Pick(new (uint, string)[]
            {
                (10, @"C:\Games\b.exe"), (11, null), (12, @"C:\Games\A.exe"), (12, @"c:\games\a.exe"),
                (13, string.Empty), (99, @"C:\PadForge\PadForge.exe"),
            }, 99);
            Assert.Equal(new[] { @"C:\Games\A.exe", @"C:\Games\b.exe" }, picked);
            Assert.Empty(WindowedPrograms.Pick(Array.Empty<(uint, string)>(), 99));
        }

        /// <summary>The walk of the open windows, on this machine as it is:
        /// whole paths, none twice, and never this process.</summary>
        [Fact]
        public void TheRunningProgramsAreWholePathsEachOnce()
        {
            var programs = WindowedPrograms.List();
            Assert.All(programs, path => Assert.True(Path.IsPathRooted(path), path));
            Assert.Equal(programs.Count, programs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain(programs, path => string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void AGamesChipShowsItsFileName()
        {
            Assert.Equal("RocketLeague.exe", ProfileDialog.GameName(@"C:\Games\rocketleague\RocketLeague.exe"));
            Assert.Equal("game.exe", ProfileDialog.GameName("game.exe"));
            Assert.Equal(string.Empty, ProfileDialog.GameName(null));
        }

        /// <summary>The games are chips ahead of the two buttons that add
        /// one, on one line. A chip shows the file name, carries the whole
        /// path as its tooltip, and its remove is named for the game.</summary>
        [Fact]
        public void AProfilesGamesAreChipsAheadOfTheTwoButtons()
        {
            DialogTestHost.Run(() =>
            {
                var dialog = new ProfileDialog();
                dialog.LoadForEdit("Rocket League", new[] { @"C:\Games\RocketLeague.exe", @"D:\Other\Game_2.exe" }, pollingOverrideMs: 2);
                var line = Part<Panel>(dialog, "GamesLine");
                Assert.Equal(4, line.Children.Count);
                var chips = line.Children.OfType<Border>().ToList();
                Assert.Equal(2, chips.Count);
                Assert.Same(chips[0], line.Children[0]);
                Assert.Same(chips[1], line.Children[1]);
                Assert.Same(dialog.FindName("AddRunningGameButton"), line.Children[2]);
                Assert.Same(dialog.FindName("BrowseButton"), line.Children[3]);

                Assert.Equal(@"C:\Games\RocketLeague.exe", chips[0].ToolTip);
                var parts = ((Panel)chips[0].Child).Children;
                Assert.Equal("RocketLeague.exe", ((TextBlock)parts[0]).Text);
                var remove = (ButtonBase)parts[1];
                string removeName = string.Format(Strings.Instance.ProfileDialog_RemoveGame_Format, "RocketLeague.exe");
                Assert.Equal(removeName, AutomationProperties.GetName(remove));
                Assert.Equal(removeName, remove.ToolTip);

                // The Edit look, with More open on a profile that has a
                // polling rate of its own.
                Assert.Equal(Strings.Instance.ProfileDialog_Edit, dialog.Title);
                Assert.Equal(Strings.Instance.ProfileDialog_EditDescription, dialog.Subtitle);
                Assert.True(Part<Expander>(dialog, "ProfileMore").IsExpanded);
                Assert.Equal(2, dialog.PollingOverrideMs);

                // A chip's remove takes its game off the list.
                remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(new[] { @"D:\Other\Game_2.exe" }, dialog.ExecutablePaths);
                Assert.Equal(3, line.Children.Count);
                Assert.Equal("Game_2.exe", ((TextBlock)((Panel)line.Children.OfType<Border>().Single().Child).Children[0]).Text);

                // A game is added once, whatever the case of its path.
                var add = typeof(ProfileDialog).GetMethod("AddGame", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(add);
                add.Invoke(dialog, new object[] { @"d:\other\game_2.EXE" });
                Assert.Single(dialog.ExecutablePaths);
                add.Invoke(dialog, new object[] { @"C:\Games\New.exe" });
                Assert.Equal(new[] { @"D:\Other\Game_2.exe", @"C:\Games\New.exe" }, dialog.ExecutablePaths);
                Assert.Equal(2, line.Children.OfType<Border>().Count());
                dialog.Close();
            });
        }

        /// <summary>The dialog's three looks: Save As from its constructor,
        /// New from ForNew, Edit from LoadForEdit. More stays closed on a
        /// profile with no polling rate of its own.</summary>
        [Fact]
        public void TheProfileDialogTakesItsLook()
        {
            DialogTestHost.Run(() =>
            {
                var saveAs = new ProfileDialog();
                Assert.Equal(Strings.Instance.ProfileDialog_SaveAs, saveAs.Title);
                Assert.Equal(Strings.Instance.ProfileDialog_SaveAsDescription, saveAs.Subtitle);
                Assert.False(Part<Expander>(saveAs, "ProfileMore").IsExpanded);
                Assert.Empty(Part<Panel>(saveAs, "GamesLine").Children.OfType<Border>());
                saveAs.Close();

                var fresh = new ProfileDialog();
                fresh.ForNew();
                Assert.Equal(Strings.Instance.ProfileDialog_NewTitle, fresh.Title);
                Assert.Equal(Strings.Instance.ProfileDialog_NewDescription, fresh.Subtitle);
                fresh.Close();

                var edit = new ProfileDialog();
                edit.LoadForEdit("Desktop", Array.Empty<string>());
                Assert.Equal(Strings.Instance.ProfileDialog_Edit, edit.Title);
                Assert.False(Part<Expander>(edit, "ProfileMore").IsExpanded);
                Assert.Equal(0, edit.PollingOverrideMs);
                edit.Close();
            });
            // The New caller asks for the New look.
            string main = RepoText("PadForge.App", "MainWindow.xaml.cs");
            int at = main.IndexOf("private void OnNewProfile(", StringComparison.Ordinal);
            Assert.True(at > 0);
            Assert.Contains("dialog.ForNew();", main.Substring(at, 300));
        }

        // ── The Shift Layer window ──

        private static readonly InputChoice[] NoInputs = Array.Empty<InputChoice>();
        private static readonly ShiftActivator[] NoLayers = Array.Empty<ShiftActivator>();

        /// <summary>Delete Layer shows for a layer that exists and is not
        /// Base. Its click closes the dialog with the ask set and nothing to
        /// save, and the caller asks before it deletes.</summary>
        [Fact]
        public void TheShiftLayerAsksForItsDelete()
        {
            DialogTestHost.Run(() =>
            {
                var hold = new ShiftActivator { LayerName = "Aim", LayerMask = "Aim", Mode = "Hold", Kind = "Button" };
                var edit = new ShiftActivatorDialog(NoInputs, hold, NoLayers);
                Assert.Equal(Visibility.Visible, Part<Button>(edit, "DeleteLayerButton").Visibility);
                Assert.False(edit.DeleteRequested);
                Assert.Null(edit.Result);
                edit.Close();

                var fresh = new ShiftActivatorDialog(NoInputs, null, NoLayers);
                Assert.Equal(Visibility.Collapsed, Part<Button>(fresh, "DeleteLayerButton").Visibility);
                fresh.Close();

                var baseLayer = new ShiftActivatorDialog("Base", "", "");
                Assert.Equal(Visibility.Collapsed, Part<Button>(baseLayer, "DeleteLayerButton").Visibility);
                baseLayer.Close();
            });

            // A dialog result can be set only on a shown dialog, so the
            // click's two lines and the caller's order are read where they
            // live.
            string dialog = RepoText("PadForge.App", "Views", "ShiftActivatorDialog.xaml.cs");
            int click = dialog.IndexOf("private void DeleteLayerButton_Click(", StringComparison.Ordinal);
            Assert.True(click > 0);
            string body = dialog.Substring(click, 200);
            Assert.True(body.IndexOf("DeleteRequested = true;", StringComparison.Ordinal) > 0);
            Assert.True(body.IndexOf("DialogResult = true;", StringComparison.Ordinal) > body.IndexOf("DeleteRequested = true;", StringComparison.Ordinal));

            string page = RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            int asked = page.IndexOf("if (dlg.DeleteRequested)", StringComparison.Ordinal);
            Assert.True(asked > 0);
            int deleted = page.IndexOf("ConfirmAndDeleteLayer(existing.LayerMask);", asked, StringComparison.Ordinal);
            int applied = page.IndexOf("ApplyShiftActivatorEdit(_currentPadVm.PadIndex, existing, dlg.Result);", asked, StringComparison.Ordinal);
            Assert.True(deleted > asked, "the delete follows the ask");
            Assert.True(applied > deleted, "nothing is applied before the ask is read");
        }

        /// <summary>The Base layer's window is its name and its color: no
        /// input, no unmapped controls, no More. The color stands in the
        /// body, since More is not there to hold it.</summary>
        [Fact]
        public void TheBaseLayersWindowIsItsNameAndItsColor()
        {
            DialogTestHost.Run(() =>
            {
                var dialog = new ShiftActivatorDialog("Base", "#7CC7EA", "");
                Assert.Equal(Strings.Instance.Pad_Shift_BaseConfigTitle, dialog.Title);
                Assert.Equal(Strings.Instance.Pad_Shift_BaseSubtitle, dialog.Subtitle);
                foreach (string row in new[] { "InputRow", "ChordSecondRow", "AxisThresholdRow", "UnmappedRow", "ShiftMore" })
                    Assert.Equal(Visibility.Collapsed, Part<UIElement>(dialog, row).Visibility);
                var color = Part<FrameworkElement>(dialog, "ColorBlock");
                Assert.Same(Part<Panel>(dialog, "BodyPanel"), color.Parent);
                Assert.Equal("7CC7EA", Part<TextBox>(dialog, "ColorHexBox").Text);
                dialog.Close();
            });
        }

        /// <summary>The input rows are labeled for what the input does in
        /// the mode, the line under the title says what the mode does, and
        /// each mode shows the rows it uses.</summary>
        [Fact]
        public void TheShiftLayersRowsFollowItsMode()
        {
            DialogTestHost.Run(() =>
            {
                var toggle = new ShiftActivator
                {
                    LayerName = "Sprint", LayerMask = "Sprint", Mode = "Toggle", Kind = "Button", InheritUnmapped = true, DelayMs = 250,
                };
                var dialog = new ShiftActivatorDialog(NoInputs, toggle, NoLayers);
                var strings = Strings.Instance;
                var input = Part<TextBlock>(dialog, "InputLabel");
                var second = Part<TextBlock>(dialog, "ChordLabel");
                var mode = Part<ComboBox>(dialog, "ModeCombo");
                var kind = Part<ComboBox>(dialog, "KindCombo");
                Visibility Shown(string name) => Part<UIElement>(dialog, name).Visibility;

                Assert.Equal(strings.Pad_Shift_PressedWith, input.Text);
                Assert.Equal(strings.Pad_Shift_ModeToggle_Subtitle, dialog.Subtitle);
                Assert.Equal(Visibility.Visible, Shown("AutoCancelRow"));
                Assert.Equal(Visibility.Visible, Shown("FireOnReleaseBox"));
                Assert.Equal(Visibility.Collapsed, Shown("ChordSecondRow"));
                Assert.Equal(Visibility.Collapsed, Shown("CycleBlock"));
                // Fall Through to Base is the first choice.
                Assert.Equal(0, Part<ComboBox>(dialog, "InheritUnmappedCombo").SelectedIndex);
                // A layer that uses a setting under More opens with More open.
                Assert.True(Part<Expander>(dialog, "ShiftMore").IsExpanded);

                kind.SelectedValue = "Chord";
                Assert.Equal(Visibility.Visible, Shown("ChordSecondRow"));
                Assert.Equal(strings.Pad_Shift_AndWith, second.Text);
                kind.SelectedValue = "Axis";
                Assert.Equal(Visibility.Visible, Shown("AxisThresholdRow"));
                Assert.Equal(Visibility.Collapsed, Shown("ChordSecondRow"));

                mode.SelectedValue = "Cycle";
                Assert.Equal(strings.Pad_Shift_NextLayerWith, input.Text);
                Assert.Equal(strings.Pad_Shift_PreviousLayerWith, second.Text);
                Assert.Equal(strings.Pad_Shift_ModeCycle_Subtitle, dialog.Subtitle);
                Assert.Equal(Visibility.Visible, Shown("ChordSecondRow"));
                Assert.Equal(Visibility.Visible, Shown("CycleBlock"));
                Assert.Equal(Visibility.Collapsed, Shown("KindRow"));
                Assert.Equal(Visibility.Collapsed, Shown("DelayRow"));
                Assert.Equal(Visibility.Collapsed, Shown("AutoCancelRow"));
                Assert.Equal(Visibility.Collapsed, Shown("AxisThresholdRow"));
                Assert.Equal("Button", kind.SelectedValue);

                mode.SelectedValue = "Passive";
                Assert.Equal(strings.Pad_Shift_ModePassive_Subtitle, dialog.Subtitle);
                Assert.Equal(Visibility.Collapsed, Shown("InputRow"));
                Assert.Equal(Visibility.Collapsed, Shown("ChordSecondRow"));
                Assert.Equal(Visibility.Collapsed, Shown("HostLayerRow"));
                Assert.Equal(Visibility.Collapsed, Shown("PostponeMappingBox"));

                mode.SelectedValue = "Hold";
                Assert.Equal(strings.Pad_Shift_HeldWith, input.Text);
                Assert.Equal(strings.Pad_Shift_ModeHold_Subtitle, dialog.Subtitle);
                Assert.Equal(Visibility.Visible, Shown("InputRow"));
                Assert.Equal(Visibility.Visible, Shown("KindRow"));
                Assert.Equal(Visibility.Visible, Shown("DelayRow"));
                Assert.Equal(Visibility.Collapsed, Shown("FireOnReleaseBox"));
                Assert.Equal(Visibility.Collapsed, Shown("AutoCancelRow"));

                foreach (string value in new[] { "Custom", "Sticky" })
                {
                    mode.SelectedValue = value;
                    Assert.Equal(strings.Pad_Shift_PressedWith, input.Text);
                }

                // The row's reset puts the picker back on Do Nothing.
                var reset = Part<Panel>(dialog, "UnmappedRow").Children.OfType<SettingResetButton>().Single();
                Assert.Equal("Reset_?dialog_InheritUnmappedBox_433", AutomationProperties.GetAutomationId(reset));
                reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, Part<ComboBox>(dialog, "InheritUnmappedCombo").SelectedIndex);
                dialog.Close();

                // A new layer starts on Do Nothing with More closed.
                var fresh = new ShiftActivatorDialog(NoInputs, null, NoLayers);
                Assert.Equal(1, Part<ComboBox>(fresh, "InheritUnmappedCombo").SelectedIndex);
                Assert.False(Part<Expander>(fresh, "ShiftMore").IsExpanded);
                Assert.Equal(strings.Pad_Shift_HeldWith, Part<TextBlock>(fresh, "InputLabel").Text);
                fresh.Close();
            });
        }

        /// <summary>More opens with a layer that uses one of the settings it
        /// holds, and stays closed for a new layer and for a color alone.</summary>
        [Fact]
        public void MoreOpensForALayerThatUsesIt()
        {
            Assert.False(ShiftActivatorDialog.UsesMore(null));
            Assert.False(ShiftActivatorDialog.UsesMore(new ShiftActivator { Mode = "Hold", Kind = "Button", Color = "#FF6B2C", InheritUnmapped = true }));
            Assert.False(ShiftActivatorDialog.UsesMore(new ShiftActivator { Mode = null, Kind = null }));
            Assert.True(ShiftActivatorDialog.UsesMore(new ShiftActivator { Mode = "Toggle" }));
            Assert.True(ShiftActivatorDialog.UsesMore(new ShiftActivator { Kind = "Axis" }));
            Assert.True(ShiftActivatorDialog.UsesMore(new ShiftActivator { HostLayerMask = "Menu" }));
            Assert.True(ShiftActivatorDialog.UsesMore(new ShiftActivator { FireOnRelease = true }));
            Assert.True(ShiftActivatorDialog.UsesMore(new ShiftActivator { PostponeMapping = true }));
            Assert.True(ShiftActivatorDialog.UsesMore(new ShiftActivator { DelayMs = 50 }));
            Assert.True(ShiftActivatorDialog.UsesMore(new ShiftActivator { AutoCancelMs = 500 }));
            Assert.True(ShiftActivatorDialog.UsesMore(new ShiftActivator { CycleLayers = "A|B" }));
        }

        /// <summary>An input picker says Choose an Input until it has a
        /// pick, and names the pick's device in its tooltip.</summary>
        [Fact]
        public void AnInputPickerNamesItsDevice()
        {
            DialogTestHost.Run(() =>
            {
                const string pad = "7d0a2c5e-0000-0000-0000-000000000001";
                var inputs = new[]
                {
                    new InputChoice { Descriptor = "Button 4", DisplayName = "Left Bumper", DeviceGuid = pad, DeviceLabel = "Xbox Series X Controller" },
                };
                var dialog = new ShiftActivatorDialog(inputs, null, NoLayers);
                var picker = Part<ComboBox>(dialog, "InputCombo");
                var placeholder = Part<TextBlock>(dialog, "InputPlaceholder");
                Assert.Null(picker.SelectedItem);
                Assert.Equal(Visibility.Visible, placeholder.Visibility);
                Assert.Equal(Strings.Instance.Common_Unassigned, picker.ToolTip);

                picker.SelectedItem = picker.Items.OfType<InputChoice>().Single();
                Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
                Assert.Equal("Xbox Series X Controller", picker.ToolTip);
                Assert.Equal("Xbox Series X Controller", AutomationProperties.GetHelpText(picker));

                // Clear empties the pick, and the placeholder is back.
                Part<ButtonBase>(dialog, "InputClearButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Null(picker.SelectedItem);
                Assert.Equal(Visibility.Visible, placeholder.Visibility);
                dialog.Close();
            });
        }

        /// <summary>A layer with no color shows none: an empty swatch and an
        /// empty hex field, whatever the plate stands at. A typed color is
        /// a color, the plate's own one included.</summary>
        [Fact]
        public void ALayerWithNoColorShowsNone()
        {
            DialogTestHost.Run(() =>
            {
                var plain = new ShiftActivator { LayerName = "Aim", LayerMask = "Aim" };
                var dialog = new ShiftActivatorDialog(NoInputs, plain, NoLayers);
                var hex = Part<TextBox>(dialog, "ColorHexBox");
                var fill = Part<UIElement>(dialog, "ColorPreviewFill");
                var picker = Part<ColorPickerControl>(dialog, "ColorPicker");
                Assert.Equal(string.Empty, hex.Text);
                Assert.Equal(Visibility.Hidden, fill.Visibility);

                string plate = $"{picker.Red:X2}{picker.Green:X2}{picker.Blue:X2}";
                hex.Text = plate;
                hex.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
                Assert.Equal(plate, hex.Text);
                Assert.Equal(Visibility.Visible, fill.Visibility);

                // The heading's reset takes the color away again.
                Part<ButtonBase>(dialog, "ClearColorButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(string.Empty, hex.Text);
                Assert.Equal(Visibility.Hidden, fill.Visibility);
                dialog.Close();

                var tinted = new ShiftActivatorDialog(NoInputs, new ShiftActivator { LayerName = "Aim", LayerMask = "Aim", Color = "#FF6B2C" }, NoLayers);
                Assert.Equal("FF6B2C", Part<TextBox>(tinted, "ColorHexBox").Text);
                Assert.Equal(Visibility.Visible, Part<UIElement>(tinted, "ColorPreviewFill").Visibility);
                tinted.Close();
            });
        }

        /// <summary>A mode's line on what it does shows in the open list,
        /// and the closed picker shows the name alone, so it is one line
        /// tall. Under the app's own picker template, which is what hands
        /// the closed box its look.</summary>
        [Fact]
        public void AModePickerShowsTheNameAloneWhenClosed()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var toggle = new ShiftActivator { LayerName = "Sprint", LayerMask = "Sprint", Mode = "Toggle", Kind = "Button" };
                var dialog = new ShiftActivatorDialog(NoInputs, toggle, NoLayers);
                try
                {
                    new WindowInteropHelper(dialog).EnsureHandle();
                    dialog.ApplyTemplate();
                    var root = (FrameworkElement)VisualTreeHelper.GetChild(dialog, 0);
                    root.Measure(new Size(560, 1400));
                    root.Arrange(new Rect(0, 0, 560, 1400));
                    root.UpdateLayout();

                    var mode = Part<ComboBox>(dialog, "ModeCombo");
                    var shown = Descendants<TextBlock>(mode).Select(text => text.Text).ToList();
                    Assert.Contains(Strings.Instance.Pad_Shift_ModeToggle, shown);
                    Assert.DoesNotContain(Strings.Instance.Pad_Shift_ModeToggle_Subtitle, shown);

                    // A presenter that is no picker's own takes the list's look.
                    var look = (PickTemplateSelector)dialog.FindResource("PickLook");
                    Assert.NotNull(look.Closed);
                    Assert.NotNull(look.List);
                    Assert.NotSame(look.Closed, look.List);
                    Assert.Same(look.List, look.SelectTemplate(mode.SelectedItem, new ContentPresenter()));
                }
                finally { dialog.Close(); }
            }, TimeSpan.FromMinutes(2), "The mode picker's test timed out.");
        }

        // ── NFC Tags ──

        /// <summary>The live line waits with no UID and a wait-colored
        /// flame. A read tag shows its UID, turns Register on and the flame
        /// ember. The list is left out while no tag is registered.</summary>
        [Fact]
        public void ATagReadShowsItsUidAndTurnsRegisterOn()
        {
            var held = NfcTagRegistry.SaveRegistry();
            try
            {
                NfcTagRegistry.LoadRegistry(Array.Empty<(string, string, int)>());
                DialogTestHost.Run(() =>
                {
                    var dialog = new RegisterNfcTagDialog();
                    try
                    {
                        var status = Part<TextBlock>(dialog, "StatusText");
                        var uid = Part<TextBlock>(dialog, "UidText");
                        var register = Part<Button>(dialog, "RegisterBtn");
                        Assert.Equal(NfcReaderService.Active != null ? Strings.Instance.Nfc_TapPrompt : Strings.Instance.Nfc_WaitingController, status.Text);
                        Assert.Equal(string.Empty, uid.Text);
                        Assert.False(register.IsEnabled);
                        Assert.Equal(Visibility.Collapsed, Part<UIElement>(dialog, "TagsBlock").Visibility);
                        Assert.True(NfcTagRegistry.RegistrationCaptureActive);

                        NfcTagRegistry.RaiseControllerTag("04f31b2a9c4d80");
                        dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        Assert.Equal(NfcTagRegistry.NormalizeUid("04f31b2a9c4d80"), uid.Text);
                        Assert.True(register.IsEnabled);
                        Assert.Equal(Strings.Instance.Nfc_TagCaptured, status.Text);

                        // Register names the tag, lists it and waits again.
                        Part<TextBox>(dialog, "NameBox").Text = "Mario";
                        register.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal(string.Empty, uid.Text);
                        Assert.False(register.IsEnabled);
                        Assert.Equal(string.Format(Strings.Instance.Nfc_RegisteredFormat, "Mario"), status.Text);
                        Assert.Equal(Visibility.Visible, Part<UIElement>(dialog, "TagsBlock").Visibility);
                        Assert.Single(Part<ListBox>(dialog, "TagListBox").Items);
                    }
                    finally { dialog.Close(); }
                    Assert.False(NfcTagRegistry.RegistrationCaptureActive);
                });
            }
            finally { NfcTagRegistry.LoadRegistry(held); }
        }

        // ── Voice Macros ──

        /// <summary>A recognition's line sets the phrase apart in SemiBold,
        /// reads as one sentence, and shows the confidence at its end. A
        /// phrase that fired lights its row. With listening off the line
        /// says so, and More is open on the switch.</summary>
        [Fact]
        public void TheVoiceLineSetsThePhraseApart()
        {
            var held = VoicePhraseRegistry.SaveRegistry();
            bool listening = VoiceMacroService.Enabled;
            int mode = VoiceMacroService.ListeningMode;
            float floor = VoiceMacroService.MinConfidence;
            try
            {
                VoicePhraseRegistry.LoadRegistry(new[] { ("reload", "Reload", 1), ("open map", "Map", 2) });
                VoiceMacroService.Enabled = true;
                VoiceMacroService.ListeningMode = 0;
                VoiceMacroService.MinConfidence = 0.80f;
                DialogTestHost.Run(() =>
                {
                    var dialog = new RegisterVoicePhraseDialog();
                    try
                    {
                        var line = Part<TextBlock>(dialog, "HeardText");
                        var confidence = Part<TextBlock>(dialog, "HeardConfidence");
                        Assert.Equal(Strings.Instance.Voice_ListeningIdle, line.Text);
                        Assert.Equal(string.Empty, confidence.Text);
                        Assert.False(Part<Expander>(dialog, "VoiceMore").IsExpanded);

                        dialog.ShowHeard("Headset Microphone", "reload", 0.93f, fired: true);
                        string whole = string.Format(Strings.Instance.Voice_HeardFired_Format, "reload", "Headset Microphone");
                        // The runs read as the whole sentence, and so does
                        // the line's name, which a screen reader is told.
                        Assert.Equal(whole, string.Concat(line.Inlines.OfType<Run>().Select(run => run.Text)));
                        Assert.Equal(whole, AutomationProperties.GetName(line));
                        var set = line.Inlines.OfType<Run>().Where(run => run.FontWeight == FontWeights.SemiBold).ToList();
                        Assert.Equal("reload", Assert.Single(set).Text);
                        Assert.Equal(0.93f.ToString("F2", CultureInfo.CurrentCulture), confidence.Text);

                        var rows = Part<ListBox>(dialog, "PhraseListBox").Items.Cast<RegisterVoicePhraseDialog.PhraseRow>().ToList();
                        Assert.Equal(new[] { "reload", "open map" }, rows.Select(row => row.Phrase));
                        Assert.True(rows[0].IsActive);
                        Assert.False(rows[1].IsActive);

                        // A recognition that did not fire lights no row.
                        dialog.ShowHeard("Headset Microphone", "open map", 0.41f, fired: false);
                        Assert.Equal(string.Format(Strings.Instance.Voice_HeardIgnored_Format, "open map", "Headset Microphone"),
                            AutomationProperties.GetName(line));
                        Assert.False(rows[1].IsActive);

                        // Turning listening off says so on the line.
                        Part<CheckBox>(dialog, "EnabledBox").IsChecked = false;
                        Assert.False(VoiceMacroService.Enabled);
                        Assert.Equal(Strings.Instance.Voice_ListeningOff, line.Text);
                        Assert.Equal(Strings.Instance.Voice_ListeningOff, AutomationProperties.GetName(line));
                        Assert.Equal(string.Empty, confidence.Text);
                    }
                    finally { dialog.Close(); }

                    var off = new RegisterVoicePhraseDialog();
                    try
                    {
                        Assert.Equal(Strings.Instance.Voice_ListeningOff, Part<TextBlock>(off, "HeardText").Text);
                        Assert.True(Part<Expander>(off, "VoiceMore").IsExpanded);
                    }
                    finally { off.Close(); }
                });
            }
            finally
            {
                VoiceMacroService.Enabled = listening;
                VoiceMacroService.ListeningMode = mode;
                VoiceMacroService.MinConfidence = floor;
                VoicePhraseRegistry.LoadRegistry(held);
            }
        }

        // ── Chat Commands ──

        /// <summary>The line under the Word box takes no room until it has
        /// something to say, and says why a word cannot be one.</summary>
        [Fact]
        public void TheChatWordLineShowsOnlyWhatItSays()
        {
            var held = ChatCommandRegistry.SaveRegistry();
            try
            {
                DialogTestHost.Run(() =>
                {
                    var dialog = new ChatCommandsDialog();
                    try
                    {
                        var message = Part<TextBlock>(dialog, "MessageText");
                        var word = Part<TextBox>(dialog, "WordBox");
                        var add = Part<Button>(dialog, "AddBtn");
                        Assert.Equal(Visibility.Collapsed, message.Visibility);

                        word.Text = "no spaces!";
                        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal(Visibility.Visible, message.Visibility);
                        Assert.Equal(ChatCommandsDialog.ProblemText(ChatCommandRegistry.Check("no spaces!")), message.Text);
                        Assert.NotEqual(string.Empty, message.Text);

                        word.Text = "recorddialogtest";
                        add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Instance.TwitchChat_Added_Format, "!recorddialogtest"), message.Text);
                        Assert.Equal(string.Empty, word.Text);
                        var rows = Part<ListBox>(dialog, "WordListBox").Items.Cast<ChatCommandsDialog.WordRow>().ToList();
                        Assert.Contains(rows, row => row.Display == "!recorddialogtest" && row.ToString() == "!recorddialogtest");
                    }
                    finally { dialog.Close(); }
                });
            }
            finally { ChatCommandRegistry.LoadRegistry(held); }
        }

        // ── Hidden Buttons ──

        /// <summary>With the feature off there is no device to learn from:
        /// the line says so and Start Over is off. The heading names the
        /// machine, and the list gives way to a line while nothing is
        /// learned.</summary>
        [Fact]
        public void HiddenButtonsNamesTheMachineAndSaysWhenItCannotLearn()
        {
            var held = HandheldButtonRegistry.SaveRegistry();
            string machine = HandheldButtonRegistry.MachineKey;
            try
            {
                HandheldButtonRegistry.LoadRegistry(Array.Empty<HandheldButtonRegistry.Entry>(), machine);
                DialogTestHost.Run(() =>
                {
                    var dialog = new LearnHandheldButtonDialog();
                    try
                    {
                        Assert.Equal(string.Format(Strings.Instance.Handheld_LearnedOn_Format, MachineIdentity.Current.DisplayName),
                            Part<TextBlock>(dialog, "ListHead").Text);
                        Assert.Equal(Visibility.Visible, Part<UIElement>(dialog, "EmptyText").Visibility);
                        Assert.Equal(Visibility.Collapsed, Part<UIElement>(dialog, "ListFrame").Visibility);
                        Assert.Equal(Visibility.Collapsed, Part<UIElement>(dialog, "SourceRow").Visibility);
                        Assert.False(Part<Button>(dialog, "RegisterBtn").IsEnabled);
                        if (HandheldButtonsDevice.Active == null)
                        {
                            Assert.Equal(Strings.Instance.Handheld_NoDevice, Part<TextBlock>(dialog, "StatusText").Text);
                            Assert.False(Part<Button>(dialog, "StartBtn").IsEnabled);
                        }
                    }
                    finally { dialog.Close(); }
                });

                HandheldButtonRegistry.LoadRegistry(new[]
                {
                    new HandheldButtonRegistry.Entry { Name = "Armoury Crate", Button = 0, Keys = new[] { 0x7C } },
                }, machine);
                DialogTestHost.Run(() =>
                {
                    var dialog = new LearnHandheldButtonDialog();
                    try
                    {
                        Assert.Equal(Visibility.Collapsed, Part<UIElement>(dialog, "EmptyText").Visibility);
                        Assert.Equal(Visibility.Visible, Part<UIElement>(dialog, "ListFrame").Visibility);
                        object row = Assert.Single(Part<ListBox>(dialog, "ButtonListBox").Items);
                        Assert.Equal("Armoury Crate", row.ToString());
                    }
                    finally { dialog.Close(); }
                });
            }
            finally { HandheldButtonRegistry.LoadRegistry(held, machine); }

            // A learn starts as the dialog opens, once, and each step says
            // which of the three it is.
            string code = RepoText("PadForge.App", "Views", "LearnHandheldButtonDialog.xaml.cs");
            int opened = code.IndexOf("Loaded -= opened;", StringComparison.Ordinal);
            Assert.True(opened > 0);
            Assert.True(code.IndexOf("StartLearn();", opened, StringComparison.Ordinal) - opened < 60);
            foreach (string step in new[] { "ShowStep(1);", "ShowStep(2);", "ShowStep(3);" })
                Assert.Contains(step, code);
        }
    }
}
